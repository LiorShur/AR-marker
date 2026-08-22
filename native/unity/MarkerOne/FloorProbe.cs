using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Where the floor is, according to surfaces actually detected.
    ///
    /// A session's own floor estimate is recomputed from scratch each time and
    /// lands somewhere different — which on the web version was a metre or two
    /// of vertical drift on everything already placed, every visit. A detected
    /// horizontal plane is a real surface and means the same thing twice.
    ///
    /// Lowest wins: a plane can be a table, and a table is not the floor.
    /// </summary>
    public sealed class FloorProbe : MonoBehaviour
    {
        [Tooltip("Planes higher than this above the lowest seen are furniture, "
               + "not floor, and are ignored.")]
        public float Tolerance = 0.35f;

        private ARPlaneManager _planes;
        private float? _floor;

        public double Floor => _floor ?? 0;
        public bool HasFloor => _floor.HasValue;

        private void Awake() => _planes = FindFirstObjectByType<ARPlaneManager>();

        private void OnEnable()
        {
            // AR Foundation 6 replaced the per-trackable events with one
            // trackablesChanged UnityEvent. planesChanged still exists and is
            // deprecated, which compiles today and will not for long.
            if (_planes != null) { _planes.trackablesChanged.AddListener(OnPlanesChanged); }
        }

        private void OnDisable()
        {
            if (_planes != null) { _planes.trackablesChanged.RemoveListener(OnPlanesChanged); }
        }

        private void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            Consider(args.added);
            Consider(args.updated);
        }

        // IEnumerable rather than the concrete collection: AR Foundation has
        // changed what these are twice, and nothing here needs indexing.
        private void Consider(IEnumerable<ARPlane> planes)
        {
            if (planes == null) { return; }

            foreach (ARPlane plane in planes)
            {
                if (plane.alignment != PlaneAlignment.HorizontalUp) { continue; }

                float y = plane.center.y;
                if (!_floor.HasValue || y < _floor.Value) { _floor = y; }
            }
        }

        /// <summary>Every hit test lands on a real surface too, and taps happen
        /// where the user is looking rather than where a plane happened to be
        /// detected.</summary>
        public void Observe(float y)
        {
            if (!_floor.HasValue || y < _floor.Value) { _floor = y; }
        }

        public void Forget() => _floor = null;
    }
}
