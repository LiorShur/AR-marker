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

        [Tooltip("Seconds to wait for Earth to become ready before giving up and "
               + "saying so. It is normally a second or two outdoors.")]
        public float StartupTimeout = 30f;

        /// <summary>Set when startup fails, so an interface can show why rather
        /// than showing nothing.</summary>
        public string Failed { get; private set; }

        public event System.Action<string> Problem;

        private AREarthManager _earth;

        private void Awake()
        {
            _earth = GetComponent<AREarthManager>();
            if (SessionCamera == null) { SessionCamera = Camera.main; }
        }

        private IEnumerator Start()
        {
            // ARKit first. Nothing about Earth means anything until the
            // underlying session is tracking.
            yield return new WaitUntil(() => ARSession.state == ARSessionState.SessionTracking);
            Debug.Log("MarkerOne: AR session tracking");

            // Then Earth, separately and defensively. AREarthManager.EarthState
            // dereferences the ARCore Extensions session without checking it
            // exists, so on a build where the extensions did not start — iOS
            // Support unticked, no authentication configured, no ARCoreExtensions
            // component in the scene — reading it throws rather than reporting a
            // state. Catching it turns a dead coroutine into a message saying
            // which of those it was.
            float waited = 0;
            EarthState state = EarthState.ErrorEarthNotReady;

            while (waited < StartupTimeout)
            {
                if (!TryReadEarthState(out state)) { yield break; }
                if (state == EarthState.Enabled) { break; }

                if (state != EarthState.ErrorEarthNotReady)
                {
                    // A real error state, not "still starting up". Saying it
                    // once is more use than saying it sixty times a second.
                    Report("Geospatial unavailable: " + state + ". " + Explain(state));
                    yield break;
                }

                waited += 0.25f;
                yield return new WaitForSeconds(0.25f);
            }

            if (state != EarthState.Enabled)
            {
                Report("Geospatial did not become ready within " + StartupTimeout +
                       "s — last state " + state);
                yield break;
            }

            Debug.Log("MarkerOne: Earth enabled, waiting for a fix");

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

        private bool TryReadEarthState(out EarthState state)
        {
            state = EarthState.ErrorEarthNotReady;

            if (_earth == null)
            {
                Report("No AREarthManager on this object.");
                return false;
            }

            try
            {
                state = _earth.EarthState;
                return true;
            }
            catch (System.NullReferenceException)
            {
                Report("ARCore Extensions has not started a session. Check that " +
                       "an ARCore Extensions component is in the scene with its " +
                       "Session, Camera Manager and Config assigned, that iOS " +
                       "Support is enabled in XR Plug-in Management, and that an " +
                       "authentication strategy is set.");
                return false;
            }
        }

        /// <summary>The four failures below are indistinguishable from the app —
        /// no content appears — and each has a different fix.</summary>
        private static string Explain(EarthState state)
        {
            switch (state)
            {
                case EarthState.ErrorAPIKeyInvalid:
                    return "The API key is wrong, restricted to a different bundle id, " +
                           "or was created less than a few minutes ago.";
                case EarthState.ErrorGeospatialModeDisabled:
                    return "Geospatial is off in the ARCore Extensions config asset, " +
                           "or iOS Support is unticked.";
                case EarthState.ErrorNotAuthorized:
                    return "The ARCore API is not enabled on the Cloud project.";
                case EarthState.ErrorResourcesExhausted:
                    return "The project is over its ARCore API quota.";
                default:
                    return "";
            }
        }

        private void Report(string message)
        {
            Debug.LogWarning("MarkerOne: " + message);
            Failed = message;
            Problem?.Invoke(message);
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
