using System;
using System.Collections;
using Google.XR.ARCoreExtensions;
using MarkerOne.Core;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Turns what AREarthManager reports into the Fix that WorldSession wants.
    ///
    /// The interesting part is the bearing. A walked baseline derives it from
    /// two positions and a journey; the Geospatial API simply knows it, to
    /// about a degree, because EunRotation carries the device's orientation in
    /// the world. Subtracting the camera's yaw within the session gives the
    /// session's own yaw offset from a single observation — so the "walk twenty
    /// metres to find north" step the web version needs does not exist here.
    ///
    /// Nothing in MarkerOne.Core is referenced from Unity's side of this and
    /// nothing Unity is referenced from Core's. That is what makes the maths
    /// testable on a machine with no AR device.
    /// </summary>
    [RequireComponent(typeof(AREarthManager))]
    public sealed class GeospatialFixSource : MonoBehaviour
    {
        [Tooltip("Seconds between fixes once the session is tracking.")]
        public float Interval = 2f;

        [Tooltip("Fixes worse than this are ignored — a fix admitting to fifty "
               + "metres tells you almost nothing and drags the average with it.")]
        public double WorstUsableAccuracyM = 25;

        [Tooltip("Where the session is put together. Assign in the inspector.")]
        public MarkerOneRig Rig;

        public Camera SessionCamera;

        private AREarthManager _earth;

        private void Awake()
        {
            _earth = GetComponent<AREarthManager>();
            if (SessionCamera == null) { SessionCamera = Camera.main; }
        }

        private IEnumerator Start()
        {
            // The session has to be up and Earth has to be tracking before any
            // of this means anything.
            yield return new WaitUntil(() =>
                ARSession.state == ARSessionState.SessionTracking &&
                _earth != null && _earth.EarthState == EarthState.Enabled);

            var wait = new WaitForSeconds(Interval);

            while (enabled)
            {
                if (_earth.EarthTrackingState == TrackingState.Tracking)
                {
                    TryFix();
                }
                yield return wait;
            }
        }

        private void TryFix()
        {
            GeospatialPose pose = _earth.CameraGeospatialPose;

            // Documented as valid only while EarthTrackingState is Tracking,
            // and worth re-checking rather than assuming.
            if (pose.HorizontalAccuracy <= 0 || pose.HorizontalAccuracy > WorstUsableAccuracyM)
            {
                return;
            }

            var fix = new Fix
            {
                Position = new GeoPoint(pose.Latitude, pose.Longitude, pose.Altitude),
                PositionAccuracyM = pose.HorizontalAccuracy,
                HeadingAccuracyDeg = pose.OrientationYawAccuracy,
                Provider = "geospatial",
                HeadingFrom = "direct"
            };

            double? sessionYaw = SessionYaw(pose);
            if (sessionYaw.HasValue)
            {
                fix.SessionYawDeg = sessionYaw.Value;
                fix.SessionYawAccuracyDeg = pose.OrientationYawAccuracy;
            }

            Vector3 local = SessionCamera != null ? SessionCamera.transform.position : Vector3.zero;
            Rig?.Feed(fix, new Vec3(local.x, local.y, local.z));
        }

        /// <summary>
        /// The world heading of the session's forward axis.
        ///
        /// EunRotation is in East-Up-North: +X east, +Y up, +Z north. Unity's
        /// render frame is East +X, Up +Y, North -Z — the same handedness with
        /// north on the other axis — so a heading taken from EUN cannot be used
        /// against session coordinates without accounting for that.
        ///
        /// Rotating forward by EunRotation gives where the camera points in the
        /// world; atan2(east, north) turns that into a compass heading. The
        /// camera's own yaw within the session is the same direction expressed
        /// in session terms. The difference is the offset between the two
        /// frames, which is the one number the whole localization turns on.
        /// </summary>
        private double? SessionYaw(GeospatialPose pose)
        {
            if (SessionCamera == null) { return null; }

            Vector3 worldForward = pose.EunRotation * Vector3.forward;
            double deviceHeading = Mathf.Atan2(worldForward.x, worldForward.z) * Mathf.Rad2Deg;

            // Unity yaw runs clockwise seen from above, which is already the
            // convention a compass heading uses.
            double cameraYaw = SessionCamera.transform.eulerAngles.y;

            return ((deviceHeading - cameraYaw) % 360 + 360) % 360;
        }
    }
}
