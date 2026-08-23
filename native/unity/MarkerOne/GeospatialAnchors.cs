using System;
using System.Collections.Generic;
using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

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
        private readonly Dictionary<string, ARGeospatialAnchor> _made =
            new Dictionary<string, ARGeospatialAnchor>();
        private bool _complained;

        public int Count => _made.Count;

        public bool Ready => _anchors != null && ARSession.state == ARSessionState.SessionTracking;

        private void Awake()
        {
            _anchors = FindFirstObjectByType<ARAnchorManager>();
            if (_anchors == null)
            {
                Debug.LogWarning("MarkerOne: no ARAnchorManager in the scene — placements " +
                                 "will be positioned from the session frame instead of " +
                                 "anchored, which costs a metre or two between sessions.");
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

                if (anchor == null) { return null; }

                _made[id] = anchor;
                return anchor.transform;
            }
            catch (Exception e)
            {
                if (!_complained)
                {
                    _complained = true;
                    Debug.LogWarning("MarkerOne: could not create a geospatial anchor — " +
                                     e.Message + ". Falling back to the session frame.");
                }
                return null;
            }
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
