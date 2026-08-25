using System;
using System.Collections.Generic;
using Google.XR.ARCoreExtensions;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Letting ARCore hold the positions instead of holding them ourselves.
    ///
    /// A session frame is built once from a handful of fixes and then used for
    /// everything. Place something and come back tomorrow and two independent
    /// frame errors have composed — nothing drifts while you watch, it is
    /// simply in the wrong place from the moment it loads. That is the whole
    /// of the metre or three between sessions.
    ///
    /// A geospatial anchor is a latitude and longitude handed to ARCore, which
    /// keeps resolving it against VPS for as long as the session runs. It is
    /// not an estimate that gets used; it is a question that stays open. Our
    /// frame stops being in the rendering path at all, and what is left is
    /// whatever VPS can do — which is the number worth having.
    ///
    /// Falls back silently. Where Geospatial is unavailable there is no anchor
    /// to make, and the frame is still the best answer available.
    /// </summary>
    public sealed class GeospatialAnchors : MonoBehaviour
    {
        private ARAnchorManager _anchors;
        private AREarthManager _earth;
        private readonly Dictionary<string, ARGeospatialAnchor> _made =
            new Dictionary<string, ARGeospatialAnchor>();
        private bool _complained;
        private bool _refused;
        private bool _saidConvert;
        private int _failures;

        [Tooltip("Stop attempting anchors after this many consecutive failures. "
               + "Eight placements retried every second is eight exceptions a "
               + "second, which is its own problem.")]
        public int GiveUpAfter = 12;

        /// <summary>True once the attempts have been abandoned, so the readout
        /// can say so rather than showing a zero that looks like it is still
        /// trying.</summary>
        public bool GaveUp => _failures >= GiveUpAfter;

        public int Count => _made.Count;

        /// <summary>ARCore will not resolve a latitude and longitude while
        /// Earth is not tracking, and says so by returning null rather than by
        /// throwing. Checking only the ARKit session made this look ready
        /// several seconds before it was, and a failed attempt was never
        /// retried.</summary>
        public bool Ready
        {
            get
            {
                if (_anchors == null || _earth == null || GaveUp) { return false; }
                if (ARSession.state != ARSessionState.SessionTracking) { return false; }

                try { return _earth.EarthTrackingState == TrackingState.Tracking; }
                catch (Exception) { return false; }
            }
        }

        public bool Has(string id) => _made.TryGetValue(id, out ARGeospatialAnchor a) && a != null;

        /// <summary>An anchor already made but not yet resolved. Acquire hands
        /// back its transform once it starts tracking.</summary>
        public Transform Tracked(string id)
        {
            if (!_made.TryGetValue(id, out ARGeospatialAnchor anchor) || anchor == null)
            {
                return null;
            }
            return anchor.trackingState == TrackingState.Tracking ? anchor.transform : null;
        }

        private void Awake()
        {
            _anchors = FindFirstObjectByType<ARAnchorManager>();
            _earth = FindFirstObjectByType<AREarthManager>();
            EnsureExtensionsOrigin();
            if (_anchors == null)
            {
                Debug.LogWarning("MarkerOne: no ARAnchorManager in the scene — placements " +
                                 "will be positioned from the session frame instead of " +
                                 "anchored, which costs a metre or two between sessions.");
            }
        }

        /// <summary>
        /// AddAnchor ends with:
        ///
        ///     anchor.transform.SetParent(
        ///         ARCoreExtensions._instance.Origin.TrackablesParent, false);
        ///
        /// so an unassigned Origin on the ARCoreExtensions component is a
        /// NullReferenceException thrown from inside the package, several
        /// frames after everything that could have reported it. Nothing else
        /// in Geospatial reads that field, which is why it can sit empty
        /// through a working localization and only fail here.
        /// </summary>
        private void EnsureExtensionsOrigin()
        {
            var extensions = FindFirstObjectByType<ARCoreExtensions>();
            if (extensions == null || extensions.Origin != null) { return; }

            var origin = FindFirstObjectByType<XROrigin>();
            if (origin == null)
            {
                Debug.LogWarning("MarkerOne: ARCoreExtensions has no Origin and there is no " +
                                 "XR Origin to give it. Geospatial anchors will fail.");
                return;
            }

            extensions.Origin = origin;
            Debug.Log("MarkerOne: filled in the empty ARCoreExtensions Origin. Assign it in " +
                      "the inspector to stop relying on this.");
        }

        /// <summary>
        /// Where a point in this session is on the globe, according to ARCore.
        ///
        /// This is the whole answer to a fortnight of drift. Our frame is an
        /// estimate: a handful of fixes averaged into one rigid transform, held
        /// for the life of the session. ARCore is not doing that — it keeps
        /// re-solving against VPS, and when it re-localizes its answer moves.
        /// A single rigid transform cannot track something that moves, so our
        /// frame is wrong by however much ARCore has revised itself since the
        /// samples were taken. On the device: Earth reporting ±0.9m ±2° while
        /// the frame it produced was 9.5m out.
        ///
        /// Everything written with that frame is permanently wrong, and no
        /// amount of anchoring afterwards recovers it, because the coordinates
        /// themselves are the error. Asking ARCore instead means a placement is
        /// stored as well as ARCore currently knows how — which is the best
        /// anything on this device knows.
        ///
        /// The frame stays for reading back when Earth is not tracking, which
        /// is the case this cannot serve.
        /// </summary>
        public bool TryGlobal(Vector3 worldPoint, Quaternion worldRotation,
                              out double latitude, out double longitude,
                              out double height, out double headingDeg)
        {
            latitude = longitude = height = headingDeg = 0;

            if (_earth == null || !Ready) { return false; }

            try
            {
                GeospatialPose at = _earth.Convert(new Pose(worldPoint, worldRotation));

                latitude = at.Latitude;
                longitude = at.Longitude;
                height = at.Altitude;

                // Convert documents Heading as zero on the returned pose, so it
                // comes from the rotation. EUN: +X east, +Z north, and
                // atan2(east, north) is a compass bearing.
                Vector3 forward = at.EunRotation * Vector3.forward;
                double heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                headingDeg = (heading % 360 + 360) % 360;

                return true;
            }
            catch (Exception e)
            {
                if (!_saidConvert)
                {
                    _saidConvert = true;
                    Debug.LogWarning("MarkerOne: Earth.Convert failed — " + e.Message +
                                     ". Placements will use the session frame instead.");
                }
                return false;
            }
        }

        /// <summary>The anchor for a placement, made once and kept. Null means
        /// the caller should position it itself.</summary>
        public Transform Acquire(string id, double latitude, double longitude,
                                 double altitude, double headingDeg)
        {
            if (_made.TryGetValue(id, out ARGeospatialAnchor existing))
            {
                if (existing != null) { return existing.transform; }
                _made.Remove(id);
            }

            if (!Ready) { return null; }

            try
            {
                // ARCore's anchor rotation is East-Up-North and measured the
                // other way round from a compass heading.
                Quaternion rotation = Quaternion.AngleAxis(180f - (float)headingDeg, Vector3.up);

                ARGeospatialAnchor anchor =
                    _anchors.AddAnchor(latitude, longitude, altitude, rotation);

                if (anchor == null)
                {
                    // The other way this fails, and the quiet one.
                    if (!_refused)
                    {
                        _refused = true;
                        Debug.LogWarning("MarkerOne: ARCore declined to create a geospatial " +
                                         "anchor while Earth reported " +
                                         _earth.EarthTrackingState + ". Still using the frame.");
                    }
                    return null;
                }

                _made[id] = anchor;
                _failures = 0;

                // Made, but not yet worth using. A fresh anchor reports its
                // pose before ARCore has resolved it, and that pose is the
                // session origin — so attaching to it immediately teleports
                // the object to wherever the session happened to start.
                return anchor.trackingState == TrackingState.Tracking ? anchor.transform : null;
            }
            catch (Exception e)
            {
                _failures++;

                if (!_complained)
                {
                    _complained = true;

                    // The whole exception, not just its message. A
                    // NullReferenceException raised inside somebody else's
                    // package is only diagnosable from the frame it was thrown
                    // in, and Message says nothing at all.
                    Debug.LogException(e);

                    // Everything the call depends on, since one of these is
                    // almost certainly the null in question.
                    Debug.LogWarning(string.Format(
                        "MarkerOne: anchor failed. manager={0} enabled={1} subsystem={2} " +
                        "running={3} earth={4} at {5:F7},{6:F7} @{7:F1}m",
                        _anchors != null,
                        _anchors != null && _anchors.enabled,
                        _anchors != null && _anchors.subsystem != null,
                        _anchors != null && _anchors.subsystem != null && _anchors.subsystem.running,
                        Earth(), latitude, longitude, altitude));
                }

                if (GaveUp)
                {
                    Debug.LogWarning("MarkerOne: giving up on geospatial anchors after " +
                                     _failures + " failures. Placements stay on the session " +
                                     "frame, which costs a metre or two between sessions.");
                }
                return null;
            }
        }

        private string Earth()
        {
            try { return _earth == null ? "none" : _earth.EarthTrackingState.ToString(); }
            catch (Exception) { return "unreadable"; }
        }

        public void Release(string id)
        {
            if (!_made.TryGetValue(id, out ARGeospatialAnchor anchor)) { return; }
            _made.Remove(id);
            if (anchor != null) { Destroy(anchor.gameObject); }
        }

        private void OnDestroy()
        {
            _made.Clear();
        }
    }
}
