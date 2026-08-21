using System;
using System.Collections;
using System.Collections.Generic;
using MarkerOne.Core;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Where the tested core meets the scene.
    ///
    /// Everything that can be got wrong quietly — the geodesy, the frame, the
    /// origin estimate, what gets written — lives in MarkerOne.Core and has
    /// fifteen hundred assertions behind it. This file is the part that cannot
    /// be tested without a device, so it is kept as thin as it can be: read a
    /// pose, hand it over, instantiate what comes back.
    /// </summary>
    public sealed class MarkerOneRig : MonoBehaviour
    {
        [Header("Firebase")]
        public string ProjectId = "";
        public string ApiKey = "";

        [Header("Content")]
        [Tooltip("One prefab per scene id in content.json. A missing id renders "
               + "nothing rather than throwing.")]
        public List<ScenePrefab> Scenes = new List<ScenePrefab>();

        public Transform PlacementRoot;
        public Camera SessionCamera;
        public FloorProbe Floor;

        [Header("Placement")]
        [Tooltip("Metres of query radius. Loading a city to render the three "
               + "things you can see is the obvious mistake.")]
        public double RadiusM = 300;

        public double RelocalizeAfterM = 25;

        [Serializable]
        public struct ScenePrefab
        {
            public string Scene;
            public GameObject Prefab;
        }

        public WorldSession Session { get; private set; }
        public SessionState State => Session?.State ?? SessionState.Idle;

        /// <summary>Raised on the main thread. State, then a human-readable
        /// detail when there is one.</summary>
        public event Action<SessionState, string> StateChanged;

        private readonly Dictionary<string, GameObject> _spawned = new Dictionary<string, GameObject>();
        private IPlacementStore _store;

        private void Awake()
        {
            if (SessionCamera == null) { SessionCamera = Camera.main; }
            if (PlacementRoot == null) { PlacementRoot = transform; }

            if (string.IsNullOrEmpty(ProjectId) || string.IsNullOrEmpty(ApiKey))
            {
                Debug.LogWarning("MarkerOne: no Firebase project configured — placements are off.");
                return;
            }

            _store = new FirestorePlacementStore(ProjectId, ApiKey);
            Session = new WorldSession(_store, () => Floor != null ? Floor.Floor : 0)
            {
                RadiusM = RadiusM,
                RelocalizeAfterM = RelocalizeAfterM
            };

            Session.StateChanged += (state, detail) => StateChanged?.Invoke(state, detail);
            Session.PlacementsChanged += Render;
        }

        /// <summary>Called by whatever is producing fixes. Async void because
        /// it is an event handler at the edge of the system; everything it
        /// calls into reports its own failures.</summary>
        public async void Feed(Fix fix, Vec3 localPose)
        {
            if (Session == null) { return; }

            try
            {
                if (Session.State != SessionState.Ready || Session.NeedsRelocalize(localPose))
                {
                    await Session.AddFixAsync(fix, localPose);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: fix rejected — " + e.Message);
            }
        }

        /// <summary>Leave something here. localPoint is in session coordinates —
        /// a hit test result, or the reticle's position.</summary>
        public async void Place(string scene, Vector3 localPoint, string label = "")
        {
            if (Session == null || Session.State != SessionState.Ready)
            {
                Debug.LogWarning("MarkerOne: not located yet");
                return;
            }

            // Face the way the user is facing, so what they put down reads the
            // right way round to them.
            double yaw = 0;
            if (SessionCamera != null)
            {
                Vector3 forward = SessionCamera.transform.forward;
                yaw = Mathf.Atan2(-forward.x, -forward.z);
            }

            Floor?.Observe(localPoint.y);

            try
            {
                await Session.PlaceAsync(scene,
                    new Vec3(localPoint.x, localPoint.y, localPoint.z), yaw, label);
            }
            catch (Exception e)
            {
                Debug.LogError("MarkerOne: could not place — " + e.Message);
            }
        }

        public async void Remove(string id)
        {
            if (Session == null) { return; }
            try { await Session.RemoveAsync(id); }
            catch (Exception e) { Debug.LogWarning("MarkerOne: could not remove — " + e.Message); }
        }

        /// <summary>Diffed rather than rebuilt. Re-creating these every refresh
        /// would drop and reload every model, which is both slow and
        /// visible.</summary>
        private void Render(IReadOnlyList<PlacedItem> items)
        {
            var seen = new HashSet<string>();

            foreach (PlacedItem item in items)
            {
                seen.Add(item.Id);

                if (!_spawned.TryGetValue(item.Id, out GameObject go) || go == null)
                {
                    GameObject prefab = PrefabFor(item.Scene);
                    if (prefab == null) { continue; }

                    go = Instantiate(prefab, PlacementRoot);
                    go.name = $"{item.Scene}:{item.Id}";
                    _spawned[item.Id] = go;

                    foreach (IPlacedItemView view in go.GetComponentsInChildren<IPlacedItemView>(true))
                    {
                        view.Bind(item);
                    }
                }

                // Double to float at the boundary and nowhere earlier: a float
                // loses centimetres a few hundred metres out, which is the whole
                // range this works in.
                go.transform.localPosition =
                    new Vector3((float)item.Local.X, (float)item.Local.Y, (float)item.Local.Z);
                go.transform.localRotation =
                    Quaternion.Euler(0, (float)(item.YawRad * Mathf.Rad2Deg), 0);
                go.transform.localScale = Vector3.one * (float)item.Scale;
            }

            var gone = new List<string>();
            foreach (KeyValuePair<string, GameObject> entry in _spawned)
            {
                if (seen.Contains(entry.Key)) { continue; }
                if (entry.Value != null) { Destroy(entry.Value); }
                gone.Add(entry.Key);
            }
            foreach (string id in gone) { _spawned.Remove(id); }
        }

        private GameObject PrefabFor(string scene)
        {
            foreach (ScenePrefab entry in Scenes)
            {
                if (entry.Scene == scene) { return entry.Prefab; }
            }
            return null;
        }

        private void OnDestroy()
        {
            Session?.Reset();
            _spawned.Clear();
        }
    }

    /// <summary>Implement on anything inside a placement prefab that wants to
    /// know what it is — a caption showing who left it and when, most
    /// obviously.</summary>
    public interface IPlacedItemView
    {
        void Bind(PlacedItem item);
    }
}
