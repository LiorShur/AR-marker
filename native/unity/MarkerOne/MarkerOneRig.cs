using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        [Tooltip("Optional. Found automatically. When present, placements are "
               + "anchored by ARCore rather than positioned from our frame.")]
        public GeospatialAnchors Anchors;

        [Tooltip("Refuse an anchor that lands further than this from where the "
               + "frame says the object is. Generous on purpose: the frame is "
               + "the doubtful party, so this guards against an absurd anchor "
               + "rather than adjudicating between two credible answers.")]
        public float AnchorAgreementM = 50f;

        [Header("Placement")]
        [Tooltip("Metres of query radius. Loading a city to render the three "
               + "things you can see is the obvious mistake.")]
        public double RadiusM = 300;

        public double RelocalizeAfterM = 25;

        [Tooltip("Seconds before retrying a read that failed. A dropped request "
               + "should cost a few seconds, not the session.")]
        public float RetryAfterS = 10f;

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

        /// <summary>What actually happened to a placement, once the write has
        /// been away and come back. Saying "placed" the instant the button is
        /// pressed is a guess, and it was wrong every time the network was.</summary>
        public event Action<bool, string> Placed;

        /// <summary>
        /// Whether something can be put down right now.
        ///
        /// Deliberately not "is the state Ready". Ready describes the last
        /// read, and a dropped query turns it to Error — which says nothing
        /// about whether this session knows where it is. Placing needs a frame
        /// and nothing else, so a network blip was blocking placements it had
        /// no bearing on.
        /// </summary>
        public bool CanPlace => Session != null && Session.Frame != null;

        /// <summary>This device's anonymous uid, shown so it can be pasted into
        /// a rule. Stable across launches now that the refresh token is kept.</summary>
        public string Uid => _store?.Uid;

        /// <summary>The account signed in, or null while anonymous.</summary>
        public string Signed => (_store as FirestorePlacementStore)?.Signed;

        /// <summary>Trade a Google identity for a Firebase one. The uid changes,
        /// so what the session is holding is no longer this user's.</summary>
        public async Task<string> SignInWithGoogleAsync(string googleIdToken)
        {
            if (!(_store is FirestorePlacementStore store))
            {
                throw new InvalidOperationException("no Firestore store to sign in");
            }

            await store.SignInWithGoogleAsync(googleIdToken);

            if (Session != null) { await Session.RefreshAsync(); }
            return store.Signed;
        }

        public async void SignOut()
        {
            if (!(_store is FirestorePlacementStore store)) { return; }

            store.SignOut();
            try
            {
                if (Session != null) { await Session.RefreshAsync(); }
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: could not refresh after signing out — " + e.Message);
            }
        }

        private readonly Dictionary<string, GameObject> _spawned = new Dictionary<string, GameObject>();
        private readonly HashSet<string> _unknownScenes = new HashSet<string>();

        /// <summary>What each placement needs to become an anchor, kept so the
        /// attempt can be repeated. Earth is often not tracking yet at the
        /// moment placements first arrive, and one try was never enough.</summary>
        private readonly Dictionary<string, (double Lat, double Lon, double Height, double Heading)>
            _onGlobe = new Dictionary<string, (double, double, double, double)>();

        private float _retry;
        private float _refetch;
        private float _recheck;

        /// <summary>Placements made in this session, against the physical spot
        /// they were aimed at. ARKit holds that spot steady in session
        /// coordinates all session; what moves underneath it is ARCore's idea
        /// of where the session is on the globe.</summary>
        private readonly Dictionary<string, (Vector3 Point, Quaternion Rotation, float Ground,
                                            double Accuracy)>
            _mine = new Dictionary<string, (Vector3, Quaternion, float, double)>();

        [Tooltip("Rewrite a placement's coordinates when re-converting the spot "
               + "it was aimed at moves by more than this. ARCore revises its "
               + "solution as it re-localizes, and a coordinate written before "
               + "a revision is stale by the size of it.")]
        public double RewriteOverM = 1.0;

        [Tooltip("Seconds between checking placements made this session against "
               + "ARCore's current answer.")]
        public float RecheckAfterS = 5f;

        [Tooltip("Drop and rebuild an anchor that has ended up further than "
               + "this from where ARCore now converts its own coordinates.")]
        public float AnchorStaleOverM = 1.5f;

        /// <summary>How many times each placement's anchor has been refused for
        /// disagreeing with the frame. Counted rather than flagged so the
        /// retrying stops: indoors every attempt disagrees, and creating and
        /// destroying an anchor per placement per second forever is not a
        /// fallback, it is a leak with a schedule.</summary>
        private readonly Dictionary<string, int> _disagreed = new Dictionary<string, int>();

        /// <summary>The session-space height each placement belongs at: the
        /// floor this session measured plus the offset it was left at. Held
        /// against the anchor every frame, not set once — see Update.</summary>
        private readonly Dictionary<string, float> _groundY = new Dictionary<string, float>();

        /// <summary>How each placement's pose was obtained. Only "map" matters:
        /// those are seeds anybody may correct.</summary>
        private readonly Dictionary<string, string> _provider = new Dictionary<string, string>();

        [Tooltip("Give up anchoring a placement after this many anchors land "
               + "too far from where the frame puts it.")]
        public int DisagreementsAllowed = 10;
        private IPlacementStore _store;

        /// <summary>How many of the known placements actually became objects.
        /// Distinct from how many were found, and the difference is the entire
        /// content of "it says two and I can see none".</summary>
        public int Rendered => _spawned.Count;

        /// <summary>How many are held back because ARCore cannot place them
        /// yet. Not an error, and worth distinguishing from a world that is
        /// genuinely empty.</summary>
        public int Waiting
        {
            get
            {
                int waiting = 0;
                foreach (KeyValuePair<string, GameObject> entry in _spawned)
                {
                    if (entry.Value != null && !entry.Value.activeSelf) { waiting++; }
                }
                return waiting;
            }
        }

        /// <summary>Metres to the closest known placement, or -1 with none.
        /// Something two hundred metres away is not missing, it is far.</summary>
        public double NearestM { get; private set; } = -1;

        /// <summary>Where the closest rendered object actually is, relative to
        /// the camera, in session axes. "Nine shown and none visible" is nearly
        /// always vertical — the horizontal distance looks reasonable while the
        /// object sits fifteen metres overhead — and no other number on screen
        /// can tell you that.</summary>
        public Vector3 NearestOffset { get; private set; }

        public bool HasNearest { get; private set; }

        private void Awake()
        {
            if (SessionCamera == null) { SessionCamera = Camera.main; }
            if (Anchors == null) { Anchors = FindFirstObjectByType<GeospatialAnchors>(); }
            EnsurePlacementRoot();

            if (string.IsNullOrEmpty(ProjectId) || string.IsNullOrEmpty(ApiKey))
            {
                Debug.LogWarning("MarkerOne: no Firebase project configured — placements are off.");
                return;
            }

            // PlayerPrefs, so the device keeps the same anonymous identity
            // across launches. Without it every launch is a different person
            // and nothing you placed yesterday is yours.
            const string kept = "MarkerOne.RefreshToken";
            _store = new FirestorePlacementStore(ProjectId, ApiKey)
            {
                ReadRefreshToken = () => PlayerPrefs.GetString(kept, null),
                WriteRefreshToken = token =>
                {
                    PlayerPrefs.SetString(kept, token ?? "");
                    PlayerPrefs.Save();
                }
            };
            Session = new WorldSession(_store, () => Floor != null ? Floor.Floor : 0)
            {
                RadiusM = RadiusM,
                RelocalizeAfterM = RelocalizeAfterM
            };

            Session.StateChanged += (state, detail) => StateChanged?.Invoke(state, detail);
            Session.PlacementsChanged += Render;
        }

        /// <summary>
        /// Placements are positioned in session coordinates, and session
        /// coordinates are world coordinates — Feed() passes the camera's world
        /// position on exactly that assumption. So the transform they hang off
        /// has to be the identity.
        ///
        /// Defaulting it to this component's own transform made that an
        /// invisible dependency on where somebody happened to drag the rig in
        /// the hierarchy. Move it thirty metres and every placement moves
        /// thirty metres, with nothing on screen connecting the two: the
        /// placements are at the right offsets from their parent, the parent is
        /// simply somewhere else, and the world looks empty.
        ///
        /// A dedicated root removes the dependency rather than documenting it.
        /// </summary>
        private void EnsurePlacementRoot()
        {
            if (PlacementRoot == null)
            {
                var root = new GameObject("MarkerOne Placements");
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                PlacementRoot = root.transform;
                return;
            }

            // Assigned deliberately: respect it, but say so, because every
            // placement inherits whatever is wrong with it.
            if (PlacementRoot.position.sqrMagnitude > 0.0001f ||
                Quaternion.Angle(PlacementRoot.rotation, Quaternion.identity) > 0.1f)
            {
                Debug.LogWarning("MarkerOne: PlacementRoot is at " + PlacementRoot.position +
                                 " rather than the origin. Placements are positioned in " +
                                 "session coordinates, so every one of them will be off by " +
                                 "that much.");
            }
        }

        /// <summary>
        /// Put an unanchored placement where ARCore says it is.
        ///
        /// Returns false when ARCore cannot say, which is when the frame is
        /// still the best available answer. Horizontally ARCore, vertically the
        /// floor — the same division the anchored path uses, for the same
        /// reason: altitude is the weakest thing Geospatial produces and the
        /// floor is measured.
        /// </summary>
        private bool Reposition(string id, GameObject go)
        {
            if (Anchors == null || go == null) { return false; }
            if (go.transform.parent != PlacementRoot) { return true; }
            if (!_onGlobe.TryGetValue(id, out var at)) { return false; }

            if (!Anchors.TryLocal(at.Lat, at.Lon, at.Height,
                                  out Vector3 where, out float northYawDeg))
            {
                return false;
            }

            if (!go.activeSelf) { go.SetActive(true); }

            float y = _groundY.TryGetValue(id, out float ground) ? ground : where.y;
            go.transform.position = new Vector3(where.x, y, where.z);

            // Heading runs clockwise from north, and northYawDeg is where north
            // is in this session, so the sum is the object's yaw here.
            go.transform.rotation = Quaternion.Euler(0, northYawDeg + (float)at.Heading, 0);
            return true;
        }

        /// <summary>
        /// Re-convert what this session placed, and rewrite it if the answer
        /// has moved.
        ///
        /// ARCore does not hold still. It re-solves against VPS, and when it
        /// re-localizes the whole session shifts on the globe — reportedly by
        /// fifteen metres, consistently eastward, some seconds after placing.
        /// A coordinate written before such a revision is stale by the size of
        /// it, and the accuracy figure alongside it is a confidence rather than
        /// an error bar.
        ///
        /// The physical spot does not move: ARKit holds it steady in session
        /// coordinates. So converting it again later asks the same question of
        /// a device that now knows more, and the difference between the two
        /// answers is exactly what the revision was.
        /// </summary>
        private void Rewrite()
        {
            if (Session == null || Anchors == null || _mine.Count == 0) { return; }

            _recheck -= Time.unscaledDeltaTime;
            if (_recheck > 0) { return; }
            _recheck = RecheckAfterS;

            double now = Anchors.AccuracyM;

            foreach (var mine in new List<KeyValuePair<string, (Vector3 Point,
                     Quaternion Rotation, float Ground, double Accuracy)>>(_mine))
            {
                // Only overwrite with something better.
                //
                // The first version of this rewrote whenever the answer had
                // moved, which is only right if the newer answer is the better
                // one — and ARCore's accuracy goes down as well as up. Reported
                // ±0.4m in a driveway and ±4.4m under trees forty metres away,
                // so walking from one to the other would have replaced a
                // half-metre coordinate with a four-metre one, confidently, and
                // called it a correction.
                if (now <= 0 || (mine.Value.Accuracy > 0 && now >= mine.Value.Accuracy))
                {
                    continue;
                }

                if (!Anchors.TryGlobal(mine.Value.Point, mine.Value.Rotation,
                                       out double lat, out double lon,
                                       out double height, out double headingDeg))
                {
                    continue;
                }

                if (!_onGlobe.TryGetValue(mine.Key, out var was)) { continue; }

                double moved = Geodesy.Haversine(new GeoPoint(was.Lat, was.Lon),
                                                 new GeoPoint(lat, lon));
                if (moved < RewriteOverM) { continue; }

                Debug.Log(string.Format(
                    "MarkerOne: ARCore now knows this place to ±{0:0.#}m against the ±{1:0.#}m " +
                    "it was placed at, and puts {2} {3:0.#}m away — rewriting.",
                    now, mine.Value.Accuracy, mine.Key, moved));

                _mine[mine.Key] = (mine.Value.Point, mine.Value.Rotation, mine.Value.Ground, now);

                Correct(mine.Key, lat, lon, height, headingDeg,
                        mine.Value.Ground - (float)(Floor != null ? Floor.Floor : 0));
            }
        }

        /// <summary>Rebuild an anchor that no longer agrees with what ARCore
        /// makes of the same coordinates now.</summary>
        private void Refresh(string id, GameObject go)
        {
            if (!_onGlobe.TryGetValue(id, out var at)) { return; }

            if (!Anchors.TryLocal(at.Lat, at.Lon, at.Height,
                                  out Vector3 now, out float _))
            {
                return;
            }

            Vector3 held = go.transform.position;
            float apart = Vector2.Distance(new Vector2(held.x, held.z),
                                           new Vector2(now.x, now.z));

            if (apart < AnchorStaleOverM) { return; }

            Debug.Log(string.Format("MarkerOne: anchor for {0} is {1:0.#}m from where ARCore " +
                                    "now puts those coordinates — rebuilding.", id, apart));

            // Back onto the frame path, which Reposition immediately takes over
            // with ARCore's current answer, and a new anchor next pass.
            go.transform.SetParent(PlacementRoot, true);
            Anchors.Release(id);
        }

        private async void Correct(string id, double lat, double lon, double height,
                                   double headingDeg, double groundOffset)
        {
            try
            {
                await Session.RepositionAsync(id, new GeoPoint(lat, lon, height),
                                              headingDeg, groundOffset);

                // The anchor was made from the old coordinates and is now
                // holding the wrong place. Dropped so the next pass makes one
                // from the new ones.
                Anchors.Release(id);
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: could not rewrite " + id + " — " + e.Message);
            }
        }

        private void Update()
        {
            Retry();
            Rewrite();
            Anchor();

            // Placements that ARCore can locate but has not yet anchored are
            // kept current: its solution moves as it re-localizes, and an
            // object left where the last conversion put it drifts by exactly
            // the amount that re-localization corrected.
            foreach (KeyValuePair<string, GameObject> entry in _spawned)
            {
                Reposition(entry.Key, entry.Value);
            }

            HasNearest = false;
            if (SessionCamera == null) { return; }

            Vector3 eye = SessionCamera.transform.position;
            float best = float.MaxValue;

            foreach (KeyValuePair<string, GameObject> entry in _spawned)
            {
                if (entry.Value == null) { continue; }

                // Hold the height against the anchor, every frame.
                //
                // Setting it once at attach was not enough: the object is a
                // child of the anchor, so once ARCore starts refining that
                // anchor's pose the height goes with it. Placements made where
                // the altitude was poor came back sixteen metres in the air —
                // horizontally right, and unreachable.
                //
                // Horizontally the anchor, vertically the floor, continuously.
                if (entry.Value.transform.parent != PlacementRoot &&
                    _groundY.TryGetValue(entry.Key, out float ground))
                {
                    Vector3 at = entry.Value.transform.position;
                    if (Mathf.Abs(at.y - ground) > 0.01f)
                    {
                        entry.Value.transform.position = new Vector3(at.x, ground, at.z);
                    }
                }

                Vector3 offset = entry.Value.transform.position - eye;
                float distance = offset.sqrMagnitude;
                if (distance >= best) { continue; }

                best = distance;
                NearestOffset = offset;
                HasNearest = true;
            }
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
        /// Try a failed read again.
        ///
        /// Reads were only ever attempted from inside AddFixAsync, on the
        /// assumption that a new fix is the thing that makes a new query worth
        /// running. That stopped being true when fixes started being filtered:
        /// once the frame is good the gate rejects nearly everything, so after
        /// one dropped request nothing asked again and the world stayed empty
        /// until the user walked a hundred metres. A network blip should cost a
        /// few seconds.
        /// </summary>
        private void Retry()
        {
            if (Session == null || Session.State != SessionState.Error)
            {
                _refetch = 0;
                return;
            }

            _refetch -= Time.unscaledDeltaTime;
            if (_refetch > 0) { return; }
            _refetch = RetryAfterS;

            Refetch();
        }

        /// <summary>Async void deliberately: this is a timer firing, not
        /// something anybody awaits, and RefreshAsync reports its own
        /// failures before rethrowing.</summary>
        private async void Refetch()
        {
            try { await Session.RefreshAsync(); }
            catch (Exception) { /* already emitted as Error; the timer comes round again */ }
        }

        /// <summary>
        /// Move anything not yet anchored onto an anchor, and keep trying.
        ///
        /// Doing this once, inside the render that follows a fetch, meant the
        /// single attempt landed in the seconds before Earth began tracking —
        /// ARCore returned null, and nothing ever asked again. Placements then
        /// stayed on the frame for the life of the session while the readout
        /// cheerfully said the anchor path was in use.
        /// </summary>
        private void Anchor()
        {
            if (Anchors == null || _spawned.Count == 0) { return; }

            _retry -= Time.unscaledDeltaTime;
            if (_retry > 0) { return; }
            _retry = 1f;

            if (!Anchors.Ready) { return; }

            foreach (KeyValuePair<string, GameObject> entry in _spawned)
            {
                if (entry.Value == null) { continue; }

                // Already attached. Still worth checking: an anchor is made
                // once, from the solution ARCore had at that moment, and it
                // then holds that position steadily while ARCore revises
                // everything around it. On a fresh launch the revision is the
                // convergence from GPS to VPS, and an anchor built before it
                // keeps the whole set ten metres east of where it belongs —
                // rigidly, in formation, which is what a shifted origin looks
                // like and what a heading error does not.
                if (entry.Value.transform.parent != PlacementRoot)
                {
                    Refresh(entry.Key, entry.Value);
                    continue;
                }

                if (!_onGlobe.TryGetValue(entry.Key, out var at)) { continue; }

                _disagreed.TryGetValue(entry.Key, out int refused);
                if (refused >= DisagreementsAllowed) { continue; }

                // Made earlier but still resolving, or not made yet.
                Transform anchor = Anchors.Has(entry.Key)
                    ? Anchors.Tracked(entry.Key)
                    : Anchors.Acquire(entry.Key, at.Lat, at.Lon, at.Height, at.Heading);

                if (anchor == null) { continue; }

                // Where the frame says this is. The object is still on the
                // frame path, so its current position is exactly that.
                Vector3 was = entry.Value.transform.position;
                Vector3 to = anchor.position;

                // Horizontally only. The frame puts the object at the floor the
                // session measured; the anchor puts it at the WGS84 altitude
                // recorded when it was placed. Those two are supposed to
                // disagree — altitude is the weakest axis Geospatial has, which
                // is the whole reason the vertical comes from the floor — so
                // folding it into the comparison rejects good anchors for
                // being right about the thing they are not being asked.
                float apart = Vector2.Distance(new Vector2(was.x, was.z),
                                               new Vector2(to.x, to.z));
                float vertical = Mathf.Abs(was.y - to.y);

                if (apart > AnchorAgreementM)
                {
                    // Not an improvement — a contradiction. Indoors, with no
                    // VPS and weak GPS, ARCore will resolve an anchor tens of
                    // metres out, and attaching to it makes the object vanish
                    // rather than merely sit wrong. Dropped and retried; it
                    // often agrees a few seconds later.
                    _disagreed[entry.Key] = refused + 1;

                    if (refused == 0 || refused + 1 == DisagreementsAllowed)
                    {
                        Debug.LogWarning(string.Format(
                            "MarkerOne: anchor for {0} landed {1:0.#}m horizontally from " +
                            "where the frame puts it ({2:0.#}m vertically), past the " +
                            "{3:0.#}m limit. {4}",
                            entry.Key, apart, vertical, AnchorAgreementM,
                            refused + 1 == DisagreementsAllowed
                                ? "Giving up on anchoring this one."
                                : "Staying on the frame and retrying."));
                    }

                    Anchors.Release(entry.Key);
                    continue;
                }

                _disagreed[entry.Key] = 0;

                // Horizontally the anchor, vertically the floor. ARCore knows
                // where this is on the globe far better than our frame does;
                // the session knows how high the ground is far better than any
                // altitude does. Taking each from whichever measured it beats
                // taking both from either.
                entry.Value.transform.SetParent(anchor, false);
                entry.Value.transform.position = new Vector3(to.x, was.y, to.z);
                entry.Value.transform.rotation = anchor.rotation;
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
            // Either axis. A fix no better placed but far better oriented is
            // worth having — at thirty metres out, seventeen degrees of heading
            // error moves an object nine metres, which is more than the
            // position error contributes.
            bool better = fix.PositionAccuracyM > 0
                       && fix.PositionAccuracyM < frame.Fix.PositionAccuracyM * ImprovementRatio;

            bool aimed = fix.HeadingAccuracyDeg > 0
                      && fix.HeadingAccuracyDeg < frame.Fix.HeadingAccuracyDeg * ImprovementRatio;

            return better || aimed;
        }

        /// <summary>Leave something here. localPoint is in session coordinates —
        /// a hit test result, or the reticle's position.</summary>
        public async void Place(string scene, Vector3 localPoint, string label = "")
        {
            if (!CanPlace)
            {
                Debug.LogWarning("MarkerOne: not located yet");
                Placed?.Invoke(false, "not located yet");
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

            var local = new Vec3(localPoint.x, localPoint.y, localPoint.z);
            var rotation = Quaternion.Euler(0, (float)(yaw * Mathf.Rad2Deg), 0);

            try
            {
                // ARCore first. It converts this point to a latitude and
                // longitude with the solution it is still refining; the frame
                // converts it with one averaged out of a handful of fixes and
                // then frozen. Coordinates are written once and are wrong
                // forever, so this is the moment it matters most.
                if (Anchors != null &&
                    Anchors.TryGlobal(localPoint, rotation,
                                      out double lat, out double lon,
                                      out double height, out double headingDeg))
                {
                    Placement written = await Session.PlaceAtAsync(
                        scene, new GeoPoint(lat, lon, height), headingDeg, local, label);

                    if (written?.Id != null)
                    {
                        _mine[written.Id] = (localPoint, rotation, (float)localPoint.y,
                                             Anchors.AccuracyM);
                    }
                }
                else
                {
                    await Session.PlaceAsync(scene, local, yaw, label);
                }

                Placed?.Invoke(true, scene);
            }
            catch (Exception e)
            {
                Debug.LogError("MarkerOne: could not place — " + e.Message);
                Placed?.Invoke(false, e.Message);
            }
        }

        /// <summary>
        /// Delete everything this session can see.
        ///
        /// Every reading is confounded until this exists. The placements
        /// currently in the store were written by a frame that was wrong in
        /// ways since fixed, so they sit tens of metres from where they were
        /// left — and a test that cannot tell a bad coordinate from a bad
        /// renderer is not a test.
        ///
        /// The store's rules decide what is actually allowed to go; anything
        /// refused is counted rather than hidden.
        /// </summary>
        public async void ClearAll()
        {
            if (Session == null) { return; }

            var ids = new List<string>(_spawned.Keys);
            int gone = 0;
            int refused = 0;

            foreach (string id in ids)
            {
                try
                {
                    await Session.RemoveAsync(id);
                    gone++;
                }
                catch (Exception e)
                {
                    refused++;
                    Debug.LogWarning("MarkerOne: could not remove " + id + " — " + e.Message);
                }
            }

            string said = gone + " removed" + (refused > 0 ? ", " + refused + " refused" : "");
            Debug.Log("MarkerOne: " + said);
            Placed?.Invoke(refused == 0, said);
        }

        /// <summary>Whether this placement came off a map and is waiting for
        /// somebody to stand in front of the real thing.</summary>
        public bool IsSeed(string id) =>
            _provider.TryGetValue(id, out string p) && p == "map";

        /// <summary>The placements currently rendered, by id. Order is not
        /// promised — the caller decides what near means.</summary>
        public IEnumerable<KeyValuePair<string, GameObject>> Objects => _spawned;

        /// <summary>
        /// Move a placement to where it actually belongs.
        ///
        /// A map seed says "something is about here" to within whatever the
        /// satellite imagery was worth, and no amount of localizing improves
        /// that — the error is in the record, not in the device. The only
        /// thing that can fix it is somebody standing in front of the real
        /// thing, which is what this is.
        ///
        /// Claiming goes with it. A seed is owned by whoever ran the script,
        /// in practice an identity that existed for one write, so correcting
        /// one and leaving it in that name would mean nobody could ever correct
        /// it again.
        /// </summary>
        public async void Adjust(string id, Vector3 worldPoint, float yawDeg)
        {
            if (Session == null || Anchors == null) { return; }

            var rotation = Quaternion.Euler(0, yawDeg, 0);

            if (!Anchors.TryGlobal(worldPoint, rotation, out double lat, out double lon,
                                   out double height, out double headingDeg))
            {
                Placed?.Invoke(false, "ARCore cannot say where that is yet");
                return;
            }

            double floor = Floor != null ? Floor.Floor : 0;
            bool seed = IsSeed(id);

            try
            {
                await Session.RepositionAsync(id, new GeoPoint(lat, lon, height),
                                              headingDeg, worldPoint.y - floor, seed);

                // Now it improves like anything else placed here.
                _mine[id] = (worldPoint, rotation, worldPoint.y, Anchors.AccuracyM);
                _provider[id] = "geospatial";
                Anchors.Release(id);

                Placed?.Invoke(true, seed ? "corrected and claimed" : "moved");
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: could not adjust " + id + " — " + e.Message);
                Placed?.Invoke(false, e.Message);
            }
        }

        /// <summary>Drop a pin at a coordinate. Needs no localization — the
        /// device may be nowhere near it.</summary>
        public async void Seed(string scene, double lat, double lon, string label)
        {
            if (Session == null)
            {
                Placed?.Invoke(false, "no session — check Project Id and Api Key");
                return;
            }

            try
            {
                await Session.SeedAsync(scene, new GeoPoint(lat, lon), 0, label);
                // Six places, because that is what a map gives and what the
                // user typed. Six is about a tenth of a metre at these
                // latitudes, so reporting five was quietly showing them
                // something ten metres coarser than what was actually stored.
                Placed?.Invoke(true, string.Format("pinned {0} at {1:F6}, {2:F6}",
                                                   scene, lat, lon));
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: could not pin — " + e.Message);
                Placed?.Invoke(false, e.Message);
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

                _onGlobe[item.Id] = (item.Position.Lat, item.Position.Lon,
                                     item.Position.Height, item.HeadingDeg);
                _groundY[item.Id] = (float)item.Local.Y;
                _provider[item.Id] = item.Provider;

                // ARCore first, every refresh.
                if (Reposition(item.Id, go)) { continue; }

                // ARCore cannot place it yet. On a device with Geospatial that
                // is a wait, not a reason to guess: the frame is known to be
                // ten or twenty metres out and, when the camera basis is
                // ambiguous, ninety degrees out as well — which is exactly the
                // whole formation appearing rotated flat on its side. Drawing
                // from it while waiting does not degrade gracefully, it invents
                // an arrangement and presents it with confidence.
                //
                // Where there is no Geospatial at all the frame is the only
                // answer there is, and then it is used.
                if (Anchors != null)
                {
                    if (go.activeSelf) { go.SetActive(false); }
                    continue;
                }

                // An anchored object is ARCore's to position. Writing the
                // frame's answer over it every refresh would undo exactly the
                // thing the anchor is for.
                if (Anchors == null || !Anchors.Has(item.Id))
                {
                    if (go.transform.parent != PlacementRoot)
                    {
                        go.transform.SetParent(PlacementRoot, false);
                    }

                    // Double to float at the boundary and nowhere earlier: a
                    // float loses centimetres a few hundred metres out, which
                    // is the whole range this works in.
                    go.transform.localPosition = new Vector3(
                        (float)item.Local.X, (float)item.Local.Y, (float)item.Local.Z);
                    go.transform.localRotation =
                        Quaternion.Euler(0, (float)(item.YawRad * Mathf.Rad2Deg), 0);
                }

                go.transform.localScale = Vector3.one * (float)item.Scale;
            }

            var gone = new List<string>();
            foreach (KeyValuePair<string, GameObject> entry in _spawned)
            {
                if (seen.Contains(entry.Key)) { continue; }
                if (entry.Value != null) { Destroy(entry.Value); }
                Anchors?.Release(entry.Key);
                _onGlobe.Remove(entry.Key);
                _disagreed.Remove(entry.Key);
                _groundY.Remove(entry.Key);
                _mine.Remove(entry.Key);
                _provider.Remove(entry.Key);
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
