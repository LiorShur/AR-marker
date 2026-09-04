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
        /// <summary>Which side of a thing a piece goes on.</summary>
        public enum Face { Free, Top, Right, Left, Front, Behind }

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
        /// <summary>Trade an Apple identity for a Firebase one. The nonce is
        /// the one Apple was given the hash of.</summary>
        public async Task<string> SignInWithAppleAsync(string appleIdToken, string rawNonce)
        {
            return await SignedInAs(store => store.SignInWithAppleAsync(appleIdToken, rawNonce));
        }

        public async Task<string> RegisterAsync(string email, string password)
        {
            return await SignedInAs(store => store.RegisterAsync(email, password));
        }

        public async Task<string> SignInWithPasswordAsync(string email, string password)
        {
            return await SignedInAs(store => store.SignInWithPasswordAsync(email, password));
        }

        /// <summary>The email the token carries, and whether Firebase counts it
        /// as proven — which is what an admin rule is matching on.</summary>
        public string Email => (_store as FirestorePlacementStore)?.Email;

        public bool EmailVerified => (_store as FirestorePlacementStore)?.EmailVerified ?? false;

        public async Task VerifyEmailAsync()
        {
            if (_store is FirestorePlacementStore store) { await store.VerifyEmailAsync(); }
        }

        public async Task ResetPasswordAsync(string email)
        {
            if (_store is FirestorePlacementStore store) { await store.ResetPasswordAsync(email); }
        }

        /// <summary>
        /// Whatever a sign-in has in common: do it, then re-read the world.
        ///
        /// The uid changes, so what the session is holding was fetched as
        /// somebody else and its idea of what belongs to whom is now wrong.
        /// </summary>
        private async Task<string> SignedInAs(Func<FirestorePlacementStore, Task<string>> how)
        {
            if (!(_store is FirestorePlacementStore store))
            {
                throw new InvalidOperationException("no Firestore store to sign in");
            }

            await how(store);

            if (Session != null) { await Session.RefreshAsync(); }
            return store.Signed;
        }

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

        [Tooltip("How long to wait for ARCore before drawing a placement from "
               + "the session frame instead. Somewhere with no VPS coverage "
               + "this wait never ends, and hiding things for ever is not a "
               + "graceful way to say so.")]
        public float PatienceS = 10f;

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

        /// <summary>The last known record for each placement, kept so an
        /// interface can say what a thing is without another read.</summary>
        private readonly Dictionary<string, PlacedItem> _info = new Dictionary<string, PlacedItem>();

        [Header("Who may clear everything")]
        [Tooltip("Emails or uids allowed to remove other people's placements. "
               + "Empty means nobody, which is the right default: the button "
               + "erases a shared world and the rules will refuse it anyway.")]
        public List<string> Admins = new List<string>();

        /// <summary>When each placement first failed to be positioned by
        /// ARCore, so patience can run out.</summary>
        private readonly Dictionary<string, float> _waitingSince = new Dictionary<string, float>();

        /// <summary>How many are currently drawn from the frame rather than
        /// from ARCore, which is worth admitting on screen.</summary>
        public int Approximate { get; private set; }

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

        /// <summary>
        /// Metres to the closest placement on screen, measured now.
        ///
        /// It used to come from the store's DistanceM, which is the distance at
        /// the moment the query ran. Walk twenty metres and it still says
        /// twenty — so the readout claimed the nearest thing was 19.9m away
        /// while the object beside it sat 2.6m from the camera. Both numbers
        /// were true and only one of them was being asked for.
        /// </summary>
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
            const string who = "MarkerOne.Account";
            _store = new FirestorePlacementStore(ProjectId, ApiKey)
            {
                ReadRefreshToken = () => PlayerPrefs.GetString(kept, null),
                WriteRefreshToken = token =>
                {
                    PlayerPrefs.SetString(kept, token ?? "");
                    PlayerPrefs.Save();
                },

                // Beside it, so a signed-in person comes back from a relaunch
                // still signed in rather than being asked all over again.
                ReadAccount = () => PlayerPrefs.GetString(who, null),
                WriteAccount = account =>
                {
                    PlayerPrefs.SetString(who, account ?? "");
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

            // A piece of something is positioned by the thing it hangs off,
            // and writing a world position over it every frame drags it back to
            // its cached coordinates — which is the one number in a child that
            // is explicitly not the truth. Left alone entirely.
            if (IsAttached(id)) { return true; }

            // Anchored, so ARCore is positioning it and nothing here should.
            // Asked of the anchor rather than inferred from the hierarchy: a
            // placement can be detached from its anchor for several reasons and
            // reading the parent gets that wrong in both directions.
            if (Anchors.Has(id))
            {
                // Shown, which this used to forget. Something placed is hidden
                // until ARCore can position it, and the anchor arriving is
                // exactly the moment it can — but this returned before saying
                // so, and the object stayed invisible until some later refresh
                // happened to take the other branch. Which is why a placement
                // appeared only when the next one was made.
                if (!go.activeSelf) { go.SetActive(true); }
                _waitingSince.Remove(id);
                return true;
            }

            if (!_onGlobe.TryGetValue(id, out var at)) { return false; }

            if (!Anchors.TryLocal(at.Lat, at.Lon, at.Height,
                                  out Vector3 where, out float northYawDeg))
            {
                return false;
            }

            if (!go.activeSelf) { go.SetActive(true); }
            _waitingSince.Remove(id);

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

        /// <summary>
        /// Drop a placement's anchor and put the placement back under the
        /// placement root, in that order.
        ///
        /// One helper rather than the same two lines at each call site, because
        /// getting the order wrong destroys the placement and the failure shows
        /// up minutes later as a correction that did not take.
        /// </summary>
        private void Detach(string id)
        {
            if (_spawned.TryGetValue(id, out GameObject go) && go != null)
            {
                go.transform.SetParent(PlacementRoot, true);
            }

            Anchors?.Release(id);
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
            Detach(id);
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
                Detach(id);
            }
            catch (Exception e)
            {
                Debug.LogWarning("MarkerOne: could not rewrite " + id + " — " + e.Message);
            }
        }

        /// <summary>Whether this placement has waited long enough for ARCore
        /// that the frame is now the better of two poor answers.</summary>
        private bool PatienceRunOut(string id)
        {
            if (!_waitingSince.TryGetValue(id, out float since))
            {
                _waitingSince[id] = Time.unscaledTime;
                return false;
            }

            return Time.unscaledTime - since > PatienceS;
        }

        private void Update()
        {
            // Whoever is signed in gets the credit on anything placed from
            // here. Kept current rather than read once, because signing in
            // happens mid-session and the placements made afterwards should
            // carry the name.
            if (Session != null) { Session.Author = Called(); }

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
            NearestM = -1;
            Approximate = 0;
            if (SessionCamera == null) { return; }

            Vector3 eye = SessionCamera.transform.position;
            float best = float.MaxValue;

            foreach (KeyValuePair<string, GameObject> entry in _spawned)
            {
                if (entry.Value == null) { continue; }

                // Something held back because ARCore cannot place it yet is not
                // near anything — it has no position worth reporting, and
                // saying it was 1.6m away while nothing was drawn there was a
                // small lie of its own.
                if (!entry.Value.activeSelf) { continue; }

                // Drawn from the frame: no anchor, and ARCore could not place
                // it either.
                if (entry.Value.transform.parent == PlacementRoot &&
                    _waitingSince.ContainsKey(entry.Key))
                {
                    Approximate++;
                }

                // Hold the height against the anchor, every frame.
                //
                // Setting it once at attach was not enough: the object is a
                // child of the anchor, so once ARCore starts refining that
                // anchor's pose the height goes with it. Placements made where
                // the altitude was poor came back sixteen metres in the air —
                // horizontally right, and unreachable.
                //
                // Horizontally the anchor, vertically the floor, continuously —
                // but never for a piece of a structure, whose height is the
                // whole point of where it was put.
                if (entry.Value.transform.parent != PlacementRoot &&
                    !IsAttached(entry.Key) &&
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
                NearestM = offset.magnitude;
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

            // Bracketing the one synchronous native call in this path. If the
            // app stops here, the log says which side of it — which beats
            // attaching a debugger to find out.
            Debug.Log("MarkerOne: converting the placement point");

            try
            {
                // ARCore first. It converts this point to a latitude and
                // longitude with the solution it is still refining; the frame
                // converts it with one averaged out of a handful of fixes and
                // then frozen. Coordinates are written once and are wrong
                // forever, so this is the moment it matters most.
                // Declared before the call rather than inline: with the
                // short-circuiting && above, the compiler cannot prove they
                // were assigned when Anchors is null, and it is right not to.
                double lat = 0, lon = 0, height = 0, headingDeg = 0;

                bool converted = Anchors != null &&
                                 Anchors.TryGlobal(localPoint, rotation,
                                                   out lat, out lon, out height, out headingDeg);

                Debug.Log("MarkerOne: converted — " + (converted ? "writing" : "using the frame"));

                if (converted)
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
        /// Place as a piece of something already there.
        ///
        /// The offset is measured in the parent's own frame, from the parent's
        /// transform — so whatever ARCore later decides about where the parent
        /// really is, the piece moves with it and the structure keeps its
        /// shape. Coordinates are still written, because a child still needs a
        /// geohash to be found by and somewhere to stand if its parent is ever
        /// missing, but they are a cache and the offset is the truth.
        /// </summary>
        public async void Attach(string parent, string scene, Vector3 localPoint,
            string label = "")
        {
            if (!CanPlace)
            {
                Placed?.Invoke(false, "not located yet");
                return;
            }

            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null)
            {
                Placed?.Invoke(false, "nothing to attach to");
                return;
            }

            // Depth is capped when drawing, so it is capped when writing too —
            // a structure that cannot be drawn is not worth storing.
            if (Depth(parent) >= 7)
            {
                Placed?.Invoke(false, "that is as deep as a structure goes");
                return;
            }

            double yaw = 0;
            if (SessionCamera != null)
            {
                Vector3 forward = SessionCamera.transform.forward;
                yaw = Mathf.Atan2(-forward.x, -forward.z);
            }

            var rotation = Quaternion.Euler(0, (float)(yaw * Mathf.Rad2Deg), 0);

            // Into the parent's frame, which is the whole point.
            Vector3 into = onto.transform.InverseTransformPoint(
                PlacementRoot != null ? PlacementRoot.TransformPoint(localPoint) : localPoint);
            Quaternion turn = Quaternion.Inverse(onto.transform.rotation) * rotation;

            var offset = new Attachment
            {
                X = into.x,
                Y = into.y,
                Z = into.z,
                Rotation = new Quat(turn.x, turn.y, turn.z, turn.w)
            };

            try
            {
                await Session.AttachAsync(scene, new Vec3(localPoint.x, localPoint.y, localPoint.z),
                                          yaw, parent, offset, label);
                Placed?.Invoke(true, scene);
            }
            catch (Exception e)
            {
                Debug.LogError("MarkerOne: could not attach — " + e.Message);
                Placed?.Invoke(false, e.Message);
            }
        }

        /// <summary>
        /// The bottom of a structure — what actually holds it up.
        ///
        /// Moving a piece of something is nearly always meant as moving the
        /// thing: a piece has no anchor of its own, so moving one alone would
        /// mean rewriting its offset, and the offset is the one number holding
        /// the shape together.
        /// </summary>
        public string RootOf(string id)
        {
            string at = id;

            for (int step = 0; step < 16; step++)
            {
                PlacedItem item = Info(at);
                if (item == null || string.IsNullOrEmpty(item.Parent)) { break; }

                at = item.Parent;
            }

            return at;
        }

        /// <summary>
        /// Where a point in front of the camera falls in a parent's own frame.
        /// </summary>
        public Attachment OffsetFor(string parent, Vector3 localPoint, Quaternion facing)
        {
            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null) { return null; }

            Vector3 into = onto.transform.InverseTransformPoint(
                PlacementRoot != null ? PlacementRoot.TransformPoint(localPoint) : localPoint);
            Quaternion turn = Quaternion.Inverse(onto.transform.rotation) * facing;

            return new Attachment
            {
                X = into.x, Y = into.y, Z = into.z,
                Rotation = new Quat(turn.x, turn.y, turn.z, turn.w)
            };
        }

        /// <summary>
        /// Flush against one face of what it is being built on.
        ///
        /// Aiming is worth about a centimetre at arm's length and rather less
        /// at three metres, which is fine for leaving a marker in a park and
        /// useless for stacking blocks — a tower built by eye leans, and the
        /// lean accumulates. So the offset is computed from the two shapes
        /// rather than measured off a crosshair: the piece sits exactly on the
        /// face, centred on it, and the only imprecision left is deliberate.
        /// </summary>
        public Attachment SnapTo(string parent, string scene, Face face, float gapM)
        {
            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null) { return null; }

            GameObject prefab = PrefabFor(scene);
            if (prefab == null) { return null; }

            Bounds host = LocalBounds(onto);
            Bounds piece = LocalBounds(prefab);

            // Centred on the face by default, and standing on the same floor
            // for the four sides — a block placed beside another belongs level
            // with it, not floating at its midpoint.
            Vector3 at = host.center - piece.center;

            switch (face)
            {
                case Face.Top:
                    at.y = host.max.y - piece.min.y + gapM;
                    break;
                case Face.Right:
                    at.x = host.max.x - piece.min.x + gapM;
                    at.y = host.min.y - piece.min.y;
                    break;
                case Face.Left:
                    at.x = host.min.x - piece.max.x - gapM;
                    at.y = host.min.y - piece.min.y;
                    break;
                case Face.Front:
                    at.z = host.max.z - piece.min.z + gapM;
                    at.y = host.min.y - piece.min.y;
                    break;
                case Face.Behind:
                    at.z = host.min.z - piece.max.z - gapM;
                    at.y = host.min.y - piece.min.y;
                    break;
            }

            return new Attachment { X = at.x, Y = at.y, Z = at.z, Rotation = Quat.Identity };
        }

        /// <summary>
        /// What a thing occupies, in its own frame.
        ///
        /// From meshes rather than from renderer bounds, which are world-space
        /// and axis-aligned: a parent turned forty degrees would give a box
        /// bigger than itself, and a piece snapped to that box would sit in
        /// mid-air beside the face it was meant to touch. Works on a prefab
        /// asset as well as on something in the scene, which is the point —
        /// the piece being placed does not exist yet.
        /// </summary>
        private static Bounds LocalBounds(GameObject go)
        {
            var box = new Bounds();
            bool any = false;

            foreach (MeshFilter filter in go.GetComponentsInChildren<MeshFilter>())
            {
                // The contact shadow is wider than what casts it and lies flat
                // on the floor. Snapping to it would put every piece a shadow's
                // width out and sitting at ground level.
                if (filter.gameObject.name == "shadow") { continue; }

                Mesh mesh = filter.sharedMesh;
                if (mesh == null) { continue; }

                Matrix4x4 into = go.transform.worldToLocalMatrix *
                                 filter.transform.localToWorldMatrix;

                Bounds local = mesh.bounds;
                Vector3 c = local.center, e = local.extents;

                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3(
                        c.x + ((corner & 1) == 0 ? -e.x : e.x),
                        c.y + ((corner & 2) == 0 ? -e.y : e.y),
                        c.z + ((corner & 4) == 0 ? -e.z : e.z));

                    point = into.MultiplyPoint3x4(point);

                    if (!any) { box = new Bounds(point, Vector3.zero); any = true; }
                    else { box.Encapsulate(point); }
                }
            }

            return box;
        }

        /// <summary>Write a piece at an offset somebody else worked out.</summary>
        public async void AttachWith(string parent, string scene, Attachment offset,
            string label = "")
        {
            if (offset == null) { Placed?.Invoke(false, "nothing to attach to"); return; }
            if (!CanPlace) { Placed?.Invoke(false, "not located yet"); return; }

            if (Depth(parent) >= 7)
            {
                Placed?.Invoke(false, "that is as deep as a structure goes");
                return;
            }

            try
            {
                Vector3 where = _spawned.TryGetValue(parent, out GameObject onto) && onto != null
                    ? onto.transform.TransformPoint(new Vector3((float)offset.X, (float)offset.Y,
                                                                (float)offset.Z))
                    : Vector3.zero;

                if (PlacementRoot != null) { where = PlacementRoot.InverseTransformPoint(where); }

                await Session.AttachAsync(scene, new Vec3(where.x, where.y, where.z), 0,
                                          parent, offset, label);
                Placed?.Invoke(true, scene);
            }
            catch (Exception e)
            {
                Debug.LogError("MarkerOne: could not attach — " + e.Message);
                Placed?.Invoke(false, e.Message);
            }
        }

        /// <summary>How many things this hangs off, following the chain up.
        /// Capped so a loop written by hand cannot spin here.</summary>
        private int Depth(string id)
        {
            int deep = 0;
            string at = id;

            while (deep < 16)
            {
                PlacedItem item = Info(at);
                if (item == null || string.IsNullOrEmpty(item.Parent)) { break; }

                at = item.Parent;
                deep++;
            }

            return deep;
        }

        /// <summary>Whether this is a piece of something larger.</summary>
        public bool IsAttached(string id) => !string.IsNullOrEmpty(Info(id)?.Parent);

        /// <summary>Everything in a venue, markers included.</summary>
        public async Task<IReadOnlyList<Placement>> InVenueAsync(string venue)
        {
            if (!(_store is FirestorePlacementStore store))
            {
                throw new InvalidOperationException("no Firestore store");
            }

            return await store.InVenueAsync(venue);
        }

        /// <summary>
        /// Write something into a venue.
        ///
        /// No coordinates, because there are none: a venue exists precisely
        /// where a fix cannot be had. The geopose is written at zero and the
        /// geohash with it, so nothing indoors is ever returned by a nearby
        /// query — a hall full of party decorations has no business appearing
        /// to somebody walking past the building.
        /// </summary>
        public async Task<Placement> PlaceInVenueAsync(string venue, string scene,
            Attachment at, string marker = null, string label = "")
        {
            if (!(_store is FirestorePlacementStore store))
            {
                throw new InvalidOperationException("no Firestore store");
            }

            var placement = new Placement
            {
                Scene = scene,
                Position = new GeoPoint(0, 0, 0),
                Orientation = Quat.Identity,
                Scale = 1,
                Label = label ?? "",
                Author = Called(),
                Venue = venue,
                At = at,
                Marker = marker,
                Fix = new FixQuality { Provider = "venue", PositionM = 0, HeadingDeg = 0 }
            };

            return await store.PlaceAsync(placement);
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

        /// <summary>What is known about a placement, or null.</summary>
        public PlacedItem Info(string id) =>
            _info.TryGetValue(id, out PlacedItem item) ? item : null;

        /// <summary>Whether this device may remove other people's placements.
        /// The rules decide in the end; this only decides what to offer.</summary>
        public bool IsAdmin => Admins != null && (Listed(Uid) || Listed(Signed));

        /// <summary>
        /// By email as well as by uid.
        ///
        /// A uid is sixteen unreadable characters that have to be found on a
        /// device and pasted into the scene, and it changes the moment the
        /// person it belongs to signs in — so a list written before accounts
        /// existed silently stops matching anybody. An email is what somebody
        /// putting a name on this list actually knows.
        /// </summary>
        private bool Listed(string it)
        {
            if (string.IsNullOrEmpty(it)) { return false; }

            foreach (string admin in Admins)
            {
                if (!string.IsNullOrEmpty(admin) &&
                    string.Equals(admin.Trim(), it, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether this device placed the thing, and may therefore
        /// remove it. A seed belongs to whoever ran the script and is claimable
        /// by correction rather than by deletion.</summary>
        public bool IsMine(string id)
        {
            PlacedItem item = Info(id);
            return item != null && !string.IsNullOrEmpty(Uid) && item.Owner == Uid;
        }

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
            if (Session == null) { return; }

            var rotation = Quaternion.Euler(0, yawDeg, 0);

            double lat, lon, height, headingDeg;
            double accuracy = Anchors != null ? Anchors.AccuracyM : -1;

            if (Anchors != null &&
                Anchors.TryGlobal(worldPoint, rotation, out lat, out lon, out height,
                                  out headingDeg))
            {
                // ARCore knows where this is. Half a metre.
            }
            else if (Session.Frame != null)
            {
                // It does not, and out here it is not going to. Correcting a
                // placement is the thing most worth doing where a seed was
                // dropped from a map — and seeds get dropped precisely on the
                // places nobody has walked. Refusing to let anyone fix one
                // because the fixing needs a visual localization the place
                // cannot provide is the same mistake Place had, arrived at from
                // the other side.
                var local = new Vec3(worldPoint.x, worldPoint.y, worldPoint.z);
                GeoPoint from = Session.Frame.ToGlobal(local);

                lat = from.Lat;
                lon = from.Lon;
                height = from.Height;
                headingDeg = Session.Frame.LocalYawToHeading(yawDeg * Mathf.Deg2Rad);

                // Recorded as the frame's own accuracy rather than left unknown,
                // so the rewrite pass can tell later whether ARCore's eventual
                // answer is an improvement on it.
                accuracy = Session.Frame.Fix != null
                    ? Session.Frame.Fix.PositionAccuracyM
                    : 30;
            }
            else
            {
                Placed?.Invoke(false, "not located yet");
                return;
            }

            double floor = Floor != null ? Floor.Floor : 0;
            bool seed = IsSeed(id);

            try
            {
                await Session.RepositionAsync(id, new GeoPoint(lat, lon, height),
                                              headingDeg, worldPoint.y - floor, seed);

                // Now it improves like anything else placed here — including
                // the frame-derived case, which the rewrite pass will replace
                // the moment ARCore can do better.
                _mine[id] = (worldPoint, rotation, worldPoint.y, accuracy);
                _provider[id] = "geospatial";
                Detach(id);

                Placed?.Invoke(true, (seed ? "corrected" : "moved") +
                                     (Anchors != null && Anchors.AccuracyM > 0
                                         ? ""
                                         : " from GPS — roughly here"));
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

        /// <summary>
        /// A short name for whoever is signed in.
        ///
        /// The email's local part rather than the whole address: it is enough
        /// to recognise a person by, it fits in a label, and publishing a full
        /// address on every placement a person leaves in the world is more than
        /// they agreed to when they signed in.
        /// </summary>
        public string Named => Called();

        private string Called()
        {
            string signed = Signed;
            if (string.IsNullOrEmpty(signed)) { return ""; }

            int at = signed.IndexOf('@');
            string name = at > 0 ? signed.Substring(0, at) : signed;
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        public async void Remove(string id)
        {
            if (Session == null) { return; }
            try { await Session.RemoveAsync(id); }
            catch (Exception e) { Debug.LogWarning("MarkerOne: could not remove — " + e.Message); }
        }

        /// <summary>
        /// Hang the pieces on the things they belong to.
        ///
        /// Repeated rather than done once, because a piece may hang off another
        /// piece: whatever could not be placed this round because its parent
        /// had not been placed yet may well be placeable the next. Capped,
        /// because rules cannot check for a cycle — they see one document at a
        /// time — so a chain that loops has to stop somewhere, and stopping
        /// costs its author a structure that never draws rather than costing
        /// everybody a hang.
        /// </summary>
        private void Attach(List<PlacedItem> attached)
        {
            const int Deepest = 8;

            var placed = new HashSet<string>();

            for (int round = 0; round < Deepest && placed.Count < attached.Count; round++)
            {
                bool moved = false;

                foreach (PlacedItem item in attached)
                {
                    if (placed.Contains(item.Id)) { continue; }

                    if (!_spawned.TryGetValue(item.Id, out GameObject go) || go == null)
                    {
                        placed.Add(item.Id);
                        continue;
                    }

                    // Its parent has to be somewhere before this can be
                    // anywhere. Not yet drawn is not the same as absent, which
                    // is why this comes round again.
                    if (!_spawned.TryGetValue(item.Parent, out GameObject onto) ||
                        onto == null || !onto.activeSelf)
                    {
                        continue;
                    }

                    // Only once the parent itself is settled, or the offset
                    // gets measured from a position about to change.
                    if (!string.IsNullOrEmpty(Info(item.Parent)?.Parent) &&
                        !placed.Contains(item.Parent))
                    {
                        continue;
                    }

                    if (go.transform.parent != onto.transform)
                    {
                        go.transform.SetParent(onto.transform, false);
                    }

                    go.transform.localPosition = new Vector3(
                        (float)item.Offset.X, (float)item.Offset.Y, (float)item.Offset.Z);
                    go.transform.localRotation = new Quaternion(
                        (float)item.Offset.Rotation.X, (float)item.Offset.Rotation.Y,
                        (float)item.Offset.Rotation.Z, (float)item.Offset.Rotation.W);

                    if (!go.activeSelf) { go.SetActive(true); }

                    placed.Add(item.Id);
                    moved = true;
                }

                if (!moved) { break; }
            }

            // Whatever is left has no parent to hang on — deleted, out of
            // range, or still waiting for a fix. The stored coordinates are a
            // cache kept for exactly this, and roughly right in the right place
            // beats correct nowhere.
            foreach (PlacedItem item in attached)
            {
                if (placed.Contains(item.Id)) { continue; }
                if (!_spawned.TryGetValue(item.Id, out GameObject go) || go == null) { continue; }

                if (go.transform.parent != PlacementRoot)
                {
                    go.transform.SetParent(PlacementRoot, false);
                }

                go.transform.localPosition = new Vector3(
                    (float)item.Local.X, (float)item.Local.Y, (float)item.Local.Z);
                go.transform.localRotation =
                    Quaternion.Euler(0, (float)(item.YawRad * Mathf.Rad2Deg), 0);

                if (!go.activeSelf) { go.SetActive(true); }
            }
        }

        /// <summary>Diffed rather than rebuilt. Re-creating these every refresh
        /// would drop and reload every model, which is both slow and
        /// visible.</summary>
        private void Render(IReadOnlyList<PlacedItem> items)
        {
            var seen = new HashSet<string>();
            var attached = new List<PlacedItem>();


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

                    // Added here rather than baked into every prefab, so
                    // whatever content arrives later gets them for free.
                    if (go.GetComponent<Grounding>() == null) { go.AddComponent<Grounding>(); }
                    if (go.GetComponent<Appear>() == null) { go.AddComponent<Appear>(); }
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
                _info[item.Id] = item;

                // A piece of something larger is positioned by that larger
                // thing and never by itself. An anchor of its own is exactly
                // what has to be avoided: two anchors are corrected separately
                // and drift apart, which is a stack of bricks coming to pieces
                // while you watch.
                if (!string.IsNullOrEmpty(item.Parent) && item.Offset != null)
                {
                    attached.Add(item);
                    Anchors?.Release(item.Id);
                    continue;
                }

                // ARCore first, every refresh.
                if (Reposition(item.Id, go)) { continue; }

                // ARCore cannot place it. For a few seconds that is a wait
                // worth honouring — the frame is much worse and drawing from it
                // immediately produces a wrong arrangement presented with
                // confidence.
                //
                // But out where there is no VPS coverage the wait never ends,
                // and hiding everything for ever is not a graceful way to say
                // so: it is indistinguishable from an empty world, in exactly
                // the places somebody most wants to leave a marker. So patience
                // runs out, and then the frame — five or fifteen metres, and
                // honestly labelled — beats nothing at all.
                if (Anchors != null && !PatienceRunOut(item.Id))
                {
                    if (go.activeSelf) { go.SetActive(false); }
                    continue;
                }

                if (!go.activeSelf) { go.SetActive(true); }

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

                // Left alone while it is growing in. Writing the final scale
                // every refresh would cancel the animation on the frame after
                // it started, which looks exactly like no animation at all.
                if (go.GetComponent<Appear>() == null)
                {
                    go.transform.localScale = Vector3.one * (float)item.Scale;
                }
            }

            Attach(attached);

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
                _info.Remove(entry.Key);
                _waitingSince.Remove(entry.Key);
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

        /// <summary>What to draw for a scene id, or null. Public because the
        /// venue rig draws the same content in a different frame, and there is
        /// no reason for two lists of prefabs.</summary>
        public GameObject PrefabFor(string scene)
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
