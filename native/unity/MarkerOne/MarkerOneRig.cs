using System;
using System.Collections;
using System.Collections.Generic;
using MarkerOne.Core;
using UnityEngine;

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

        [Tooltip("Draw a plain marker where a placement has no prefab, rather "
               + "than drawing nothing. A visible mistake beats an empty world.")]
        public bool PlaceholderForMissing = true;

        [Tooltip("Keep taking fixes until at least this many are in the "
               + "estimate, even after the session says it is ready.")]
        public int MinFixes = 10;

        [Tooltip("Once past MinFixes, take a fix only if it is at least this "
               + "much better than what the frame is currently built on. 0.7 "
               + "means thirty percent better.")]
        public double ImprovementRatio = 0.7;

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
        private readonly HashSet<string> _unknownScenes = new HashSet<string>();
        private IPlacementStore _store;

        /// <summary>How many of the known placements actually became objects.
        /// Distinct from how many were found, and the difference is the entire
        /// content of "it says two and I can see none".</summary>
        public int Rendered => _spawned.Count;

        /// <summary>Metres to the closest known placement, or -1 with none.
        /// Something two hundred metres away is not missing, it is far.</summary>
        public double NearestM { get; private set; } = -1;

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
                if (Worth(fix, localPose))
                {
                    await Session.AddFixAsync(fix, localPose);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: fix rejected — " + e.Message);
            }
        }

        /// <summary>
        /// Whether this fix is worth adding to the estimate.
        ///
        /// Reaching Ready on one fix and then ignoring everything until the
        /// user has walked twenty-five metres wastes the entire reason the
        /// origin is estimated rather than taken: OriginEstimator weights by
        /// one over accuracy squared, so a later sample cannot make the answer
        /// worse, and a good one drags it a long way. With Geospatial that
        /// matters more than it did on the web, because the first fix is
        /// typically GPS-grade and a VPS lock arriving twenty seconds later is
        /// an order of magnitude better — worth roughly a thousand times as
        /// much to the estimate, and previously thrown away.
        /// </summary>
        private bool Worth(Fix fix, Vec3 localPose)
        {
            if (Session.State != SessionState.Ready) { return true; }
            if (Session.NeedsRelocalize(localPose)) { return true; }
            if (Session.Fixes < MinFixes) { return true; }

            LocalizationFrame frame = Session.Frame;
            if (frame == null || frame.Fix == null) { return true; }

            // Past that, only take what actually improves things. Adding
            // equally-good fixes forever costs work and buys almost nothing,
            // since the accuracy floor is deliberately half the best single
            // fix however many there are.
            return fix.PositionAccuracyM > 0
                && fix.PositionAccuracyM < frame.Fix.PositionAccuracyM * ImprovementRatio;
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

            NearestM = -1;
            foreach (PlacedItem near in items)
            {
                if (NearestM < 0 || near.DistanceM < NearestM) { NearestM = near.DistanceM; }
            }

            foreach (PlacedItem item in items)
            {
                seen.Add(item.Id);

                if (!_spawned.TryGetValue(item.Id, out GameObject go) || go == null)
                {
                    GameObject prefab = PrefabFor(item.Scene);
                    if (prefab == null)
                    {
                        // Once per scene id. Every refresh would otherwise say
                        // it again, and the log is the only place it is said.
                        if (_unknownScenes.Add(item.Scene))
                        {
                            Debug.LogWarning("MarkerOne: nothing to render for scene '" +
                                             item.Scene + "' — add it to Scenes on the rig, " +
                                             "or check the prefab reference is not empty.");
                        }
                        if (!PlaceholderForMissing) { continue; }
                    }

                    go = prefab != null
                        ? Instantiate(prefab, PlacementRoot)
                        : Placeholder();
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

        /// <summary>
        /// A cube, built in code, for placements whose prefab is missing.
        ///
        /// The alternative is what happened here: placements written, read
        /// back, counted, positioned correctly and drawing nothing, because a
        /// prefab field had been emptied by deleting the object it pointed at.
        /// Nothing on screen and nothing wrong — the hardest kind of bug to
        /// look at. A cube in the right place answers most of the question
        /// before anyone opens a log.
        ///
        /// Wrapped in an empty parent so the item's own scale multiplies the
        /// marker's size instead of replacing it.
        /// </summary>
        private GameObject Placeholder()
        {
            var root = new GameObject("placeholder");
            root.transform.SetParent(PlacementRoot, false);

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localScale = Vector3.one * 0.3f;

            Collider collider = cube.GetComponent<Collider>();
            if (collider != null) { Destroy(collider); }

            // CreatePrimitive assigns the built-in default material, which a
            // URP project renders as magenta. Which is fine — magenta is what
            // a placeholder should look like — but a shader that exists is
            // better, and URP's Lit is certainly in the build.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                var material = new Material(shader);
                material.SetColor("_BaseColor", new Color(1f, 0.25f, 0.8f));
                cube.GetComponent<Renderer>().material = material;
            }

            return root;
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
