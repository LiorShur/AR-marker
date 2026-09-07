using System.Collections.Generic;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Indoors, for as long as the app is open.
    ///
    /// Nothing here is written down, and that is the feature rather than a
    /// shortcut. Indoors there is no fix worth storing: geospatial wants sky and
    /// street imagery and has neither, so a coordinate written in a room is
    /// accurate to about nine metres and everything left there comes back
    /// somewhere else. A venue solves that with printed markers; this is for
    /// when there are none and the work is happening now — sketching a layout,
    /// walking a client through an idea, surveying a room against a standard.
    ///
    /// Tracking holds these steady for as long as the session lasts, which is
    /// centimetres over a room and is all anybody needs from something they are
    /// going to look at, adjust and then close.
    /// </summary>
    public sealed class SketchRig : MonoBehaviour
    {
        private readonly Dictionary<string, GameObject> _spawned =
            new Dictionary<string, GameObject>();

        private readonly Dictionary<string, string> _labels = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _scenes = new Dictionary<string, string>();

        private MarkerOneRig _rig;
        private int _next;

        public int Items => _spawned.Count;

        public IEnumerable<KeyValuePair<string, GameObject>> Objects => _spawned;

        private void Update()
        {
            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }

            // Everything goes when the mode does. Leaving it drawn under a
            // venue's contents would put two rooms on top of each other.
            if (AppMode.Now != AppMode.Working.Indoors && _spawned.Count > 0) { Clear(); }
        }

        public string Place(string scene, Vector3 point, Quaternion facing, string label = "")
        {
            if (_rig == null) { return null; }

            GameObject prefab = _rig.PrefabFor(scene);
            if (prefab == null) { return null; }

            Transform root = _rig.PlacementRoot;
            GameObject go = Instantiate(prefab, root != null ? root : transform);

            go.transform.position = point;
            go.transform.rotation = facing;

            if (go.GetComponent<Grounding>() == null) { go.AddComponent<Grounding>(); }
            if (go.GetComponent<Appear>() == null) { go.AddComponent<Appear>(); }

            string id = "sketch-" + (++_next);
            go.name = scene + ":" + id;

            _spawned[id] = go;
            _labels[id] = label ?? "";
            _scenes[id] = scene;
            return id;
        }

        /// <summary>Put a piece on something, which in here is nothing more than
        /// making it a child — there are no anchors to drift apart.</summary>
        public string Attach(string parent, string scene, Vector3 point, Quaternion facing,
            string label = "")
        {
            string id = Place(scene, point, facing, label);
            if (id == null) { return null; }

            if (_spawned.TryGetValue(parent, out GameObject onto) && onto != null)
            {
                _spawned[id].transform.SetParent(onto.transform, true);
            }

            return id;
        }

        /// <summary>Flush against a face, borrowed from the rig — the maths
        /// cares about two shapes and not about which world they are in.</summary>
        public bool Snap(string parent, string scene, MarkerOneRig.Face face, float gapM,
            string label = "")
        {
            if (_rig == null) { return false; }
            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null) { return false; }

            MarkerOne.Core.Attachment offset = _rig.SnapBetween(onto, scene, face, gapM);
            if (offset == null) { return false; }

            var local = new Vector3((float)offset.X, (float)offset.Y, (float)offset.Z);
            var turn = new Quaternion((float)offset.Rotation.X, (float)offset.Rotation.Y,
                                      (float)offset.Rotation.Z, (float)offset.Rotation.W);

            string id = Place(scene, onto.transform.TransformPoint(local),
                              onto.transform.rotation * turn, label);
            if (id == null) { return false; }

            _spawned[id].transform.SetParent(onto.transform, true);
            return true;
        }

        public void Move(string id, Vector3 point, Quaternion facing)
        {
            if (!_spawned.TryGetValue(id, out GameObject go) || go == null) { return; }

            go.transform.position = point;
            go.transform.rotation = facing;
        }

        public void Remove(string id)
        {
            if (!_spawned.TryGetValue(id, out GameObject go)) { return; }

            if (go != null) { Destroy(go); }
            _spawned.Remove(id);
            _labels.Remove(id);
            _scenes.Remove(id);
        }

        public string Describe(string id)
        {
            if (!_scenes.TryGetValue(id, out string scene)) { return "aiming at a placement"; }

            string label = _labels.TryGetValue(id, out string it) ? it : "";
            return "◇ " + (string.IsNullOrEmpty(label) ? scene : label) + "  ·  this session only";
        }

        public void Clear()
        {
            foreach (GameObject go in _spawned.Values) { if (go != null) { Destroy(go); } }

            _spawned.Clear();
            _labels.Clear();
            _scenes.Clear();
        }
    }
}
