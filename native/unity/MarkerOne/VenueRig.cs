using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkerOne.Core;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Indoors, where there is no Earth to measure from.
    ///
    /// Geospatial needs sky and street imagery and has neither in a hall, so a
    /// venue is pinned by printed markers instead. One of them defines the
    /// origin; every other pose in the venue — including the other markers — is
    /// stored relative to that. Scan any marker and the whole venue snaps into
    /// place, because knowing where one known thing is is enough to know where
    /// all of them are.
    ///
    /// Which is also what makes it scale from a room to a building. A single
    /// marker holds a room, but tracking drifts about one per cent of the
    /// distance walked, so forty metres and two corners later everything is
    /// half a metre out and getting worse. More markers fix that without any
    /// new mechanism: each one re-pins the frame as you reach it, so the error
    /// resets instead of accumulating.
    ///
    /// Nothing here is anchored in the ARCore sense. The marker is the anchor,
    /// the venue root is a transform hung off it, and everything in the venue
    /// is a child of that — so the whole place moves as one when the frame is
    /// re-pinned, and keeps its shape between times.
    /// </summary>
    public sealed class VenueRig : MonoBehaviour
    {
        /// <summary>
        /// Whether markers are allowed to decide where you are.
        ///
        /// Auto is what somebody walking a building wants: point at the marker
        /// in the next hall and be in the next hall, without having heard of it
        /// beforehand. World is the way out, and it has to exist — a venue is
        /// remembered across launches, so without an explicit off somebody who
        /// set one up last week is placing into it today.
        /// </summary>
        public enum Mode { Auto, World }

        [Tooltip("The venue to show. Set by the venue panel, or by whichever "
               + "marker the camera last recognised.")]
        public string Venue = "";

        [Tooltip("Auto lets a scanned marker choose the venue. World stays "
               + "outdoors whatever the camera sees.")]
        public Mode Choosing = Mode.Auto;

        /// <summary>Whether placements are going into a venue right now.</summary>
        public bool Active => Choosing == Mode.Auto && !string.IsNullOrEmpty(Venue);

        [Tooltip("Where venue objects are parented. Made if left empty.")]
        public Transform VenueRoot;

        [Tooltip("Seconds between re-reading the venue from the store.")]
        public float RefreshEverySeconds = 20f;

        /// <summary>Which marker the frame is currently pinned to, and how long
        /// ago it was seen. Shown, because "why is everything half a metre out"
        /// is answered by "you last passed a marker thirty metres ago".</summary>
        public string PinnedTo { get; private set; }

        public float PinnedSecondsAgo => _pinnedAt <= 0 ? -1 : Time.unscaledTime - _pinnedAt;

        /// <summary>How many markers this venue has, and how many of them this
        /// device has ever seen.</summary>
        public int Markers { get; private set; }

        public int Seen => _seen.Count;

        public int Items => _spawned.Count;

        public string Trouble { get; private set; }

        /// <summary>
        /// Why no marker is being seen, or null if that is not the problem.
        ///
        /// Both of these fail silently otherwise: a scene with no tracked-image
        /// manager and a manager with no library behave identically to a camera
        /// pointed at a blank wall, and "nothing happens" is the least
        /// debuggable symptom there is.
        /// </summary>
        public string Blind
        {
            get
            {
                if (_images == null) { return "no AR Tracked Image Manager — run Set up scene"; }

                if (_images.referenceLibrary == null || _images.referenceLibrary.count == 0)
                {
                    return "no marker library — run MarkerOne → Make venue markers, "
                         + "then Set up scene";
                }

                return null;
            }
        }

        private MarkerOneRig _rig;
        private ARTrackedImageManager _images;

        private readonly Dictionary<string, GameObject> _spawned = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Placement> _known = new Dictionary<string, Placement>();

        /// <summary>Marker name to its pose in the venue, for every marker the
        /// venue has. The one thing the whole mechanism turns on.</summary>
        private readonly Dictionary<string, Placement> _markers = new Dictionary<string, Placement>();

        private readonly HashSet<string> _seen = new HashSet<string>();

        private readonly HashSet<string> _refused = new HashSet<string>();
        private bool _asking;

        /// <summary>How long a marker from somewhere else has to stay in view
        /// before this device believes it has moved.</summary>
        private const float SettleSeconds = 1.5f;

        /// <summary>A marker from somewhere else, being considered. Shown, so
        /// that walking through a door reads as something happening rather than
        /// as a pause.</summary>
        public string Moving { get; private set; }

        private float _movingSince;

        private float _pinnedAt = -1;
        private float _nextRefresh;
        private string _loaded;
        private bool _loading;

        private void Update()
        {
            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }

            if (_images == null)
            {
                _images = FindFirstObjectByType<ARTrackedImageManager>();

                // Without this ARKit detects an image once and then stops
                // following it: the trackable is reported, drops out of
                // Tracking within a frame or two, and a marker held steady in
                // front of the camera flickers into view and vanishes. The
                // default is zero, which means "detect, do not track", and
                // detect-once is not what a marker pinning a room needs.
                if (_images != null) { _images.requestedMaxNumberOfMovingImages = 4; }
            }

            // Forced outdoors: nothing drawn, nothing looked up, and a marker
            // in view is just a piece of paper.
            if (Choosing == Mode.World)
            {
                if (_spawned.Count > 0) { Clear(); }
                return;
            }

            Elsewhere();

            if (string.IsNullOrEmpty(Venue))
            {
                if (_spawned.Count > 0) { Clear(); }
                return;
            }

            if (Venue != _loaded && !_loading) { Load(); }

            Pin();
        }

        /// <summary>
        /// Follow a marker into whichever venue it belongs to.
        ///
        /// A marker the current venue does not know is either a marker from
        /// somewhere else or one nobody has recorded yet, and the store settles
        /// which. Looking it up costs one query and only happens for a name
        /// that has not been seen before, so standing in a room does not keep
        /// asking.
        /// </summary>
        private async void Elsewhere()
        {
            if (_rig == null || _asking || _loading) { return; }

            string stranger = null;
            bool ours = false;

            foreach (string marker in InView())
            {
                if (_markers.ContainsKey(marker)) { ours = true; break; }
                if (stranger == null && !_refused.Contains(marker)) { stranger = marker; }
            }

            // A marker of this venue in view means you are still in this venue,
            // whatever else the camera can also see. Without this the two
            // markers either side of a doorway hand the device back and forth
            // between their venues several times a second.
            if (ours) { Moving = null; return; }

            // Nothing can be a stranger until it is known what is not. Switching
            // clears the marker list, so for the moment after one every marker
            // looks unfamiliar — including the one just walked away from, which
            // is what turned a single step through a door into a loop.
            if (!string.IsNullOrEmpty(Venue) && Venue != _loaded) { Moving = null; return; }

            if (stranger == null) { Moving = null; return; }

            // Seen steadily rather than seen once. Image tracking reports the
            // odd frame of something at the edge of view, and a venue that
            // changes on one frame of evidence changes back on the next.
            if (Moving != stranger)
            {
                Moving = stranger;
                _movingSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime < _movingSince + SettleSeconds) { return; }

            _asking = true;
            try
            {
                Placement found = await _rig.FindMarkerAsync(stranger);

                if (found == null || string.IsNullOrEmpty(found.Venue))
                {
                    // Nobody has recorded it. Remembered as such so this does
                    // not ask again every frame the camera can see it — an
                    // organizer about to add it is looking at one constantly.
                    _refused.Add(stranger);
                    Moving = null;
                    return;
                }

                if (found.Venue == Venue) { return; }

                Venue = found.Venue;
                Entered = stranger;
                _loaded = null;
                _refused.Clear();
                Clear();
                Moving = null;
            }
            catch (Exception e)
            {
                _refused.Add(stranger);
                Moving = null;
                Debug.LogWarning("MarkerOne: could not look up " + stranger + " — " + e.Message);
            }
            finally { _asking = false; }
        }

        /// <summary>Which marker walked this device into the current venue, if
        /// one did rather than somebody typing the name.</summary>
        public string Entered { get; private set; }

        /// <summary>
        /// Put the venue frame where the marker in front of the camera says it
        /// is.
        ///
        /// Deliberately polled rather than driven by the tracked-image event,
        /// whose name and signature have changed across AR Foundation versions
        /// and whose delivery is not guaranteed to be every frame. There are a
        /// handful of images; walking them costs nothing and works everywhere.
        /// </summary>
        private void Pin()
        {
            if (_images == null || _markers.Count == 0) { return; }

            ARTrackedImage best = null;
            float nearest = float.MaxValue;
            Camera eye = _rig != null ? _rig.SessionCamera : Camera.main;

            foreach (ARTrackedImage image in _images.trackables)
            {
                if (!Usable(image)) { continue; }

                string name = image.referenceImage.name;
                if (!_markers.ContainsKey(name)) { continue; }

                _seen.Add(name);

                // The nearest one currently being tracked. Two markers in view
                // at once is a doorway, and the near one is the one whose pose
                // the tracker actually knows well.
                float away = eye != null
                    ? Vector3.Distance(eye.transform.position, image.transform.position)
                    : 0;

                if (away >= nearest) { continue; }

                nearest = away;
                best = image;
            }

            if (best == null) { return; }

            Placement pin = _markers[best.referenceImage.name];

            // The venue root is placed so that the marker's stored pose lands
            // exactly on the marker the camera can see. Everything else in the
            // venue follows, because everything else is a child of the root.
            Matrix4x4 inVenue = Matrix4x4.TRS(Where(pin.At), Turn(pin.At), Vector3.one);
            Matrix4x4 inSession = Matrix4x4.TRS(best.transform.position,
                                                best.transform.rotation, Vector3.one);
            Matrix4x4 root = inSession * inVenue.inverse;

            Transform where = Root();
            where.SetPositionAndRotation(root.GetColumn(3), root.rotation);

            PinnedTo = best.referenceImage.name;
            _pinnedAt = Time.unscaledTime;
        }

        private async void Load()
        {
            _loading = true;
            string want = Venue;

            try
            {
                IReadOnlyList<Placement> items = await _rig.InVenueAsync(want);

                // Somebody changed venue while this was in flight. Whatever
                // came back describes a place they are no longer in.
                if (want != Venue) { return; }

                _known.Clear();
                _markers.Clear();

                foreach (Placement p in items)
                {
                    if (p.At == null) { continue; }

                    _known[p.Id] = p;
                    if (!string.IsNullOrEmpty(p.Marker)) { _markers[p.Marker] = p; }
                }

                Markers = _markers.Count;
                Trouble = _markers.Count == 0
                    ? "this venue has no markers yet — scan one to start it"
                    : null;

                _loaded = want;
                Draw();
            }
            catch (Exception e)
            {
                Trouble = e.Message;
                Debug.LogWarning("MarkerOne: could not read the venue — " + e.Message);
            }
            finally
            {
                _loading = false;
                _nextRefresh = Time.unscaledTime + RefreshEverySeconds;
            }
        }

        /// <summary>Re-read, for whatever somebody else has added since.</summary>
        public void Refresh()
        {
            _loaded = null;
            _nextRefresh = 0;
        }

        private void LateUpdate()
        {
            if (string.IsNullOrEmpty(Venue) || _loading) { return; }
            if (Time.unscaledTime < _nextRefresh) { return; }

            _nextRefresh = Time.unscaledTime + RefreshEverySeconds;
            Load();
        }

        private void Draw()
        {
            Transform root = Root();
            var seen = new HashSet<string>();
            var pieces = new List<Placement>();

            foreach (KeyValuePair<string, Placement> entry in _known)
            {
                Placement p = entry.Value;
                seen.Add(p.Id);

                // A marker is a pose rather than a thing. Drawing something on
                // it would put a cube over the printed image the camera is
                // trying to read.
                if (!string.IsNullOrEmpty(p.Marker)) { continue; }

                if (!_spawned.TryGetValue(p.Id, out GameObject go) || go == null)
                {
                    GameObject prefab = _rig != null ? _rig.PrefabFor(p.Scene) : null;
                    if (prefab == null) { continue; }

                    go = Instantiate(prefab, root);
                    go.name = $"{p.Scene}:{p.Id}";
                    if (go.GetComponent<Appear>() == null) { go.AddComponent<Appear>(); }

                    _spawned[p.Id] = go;
                }

                // A piece hangs off what it was built on rather than off the
                // venue, so a structure keeps its shape even if somebody later
                // moves the thing underneath it.
                if (p.IsChild) { pieces.Add(p); continue; }

                if (go.transform.parent != root) { go.transform.SetParent(root, false); }

                go.transform.localPosition = Where(p.At);
                go.transform.localRotation = Turn(p.At);

                if (go.GetComponent<Appear>() == null)
                {
                    go.transform.localScale = Vector3.one * (float)p.Scale;
                }
            }

            Hang(pieces, root);

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
        /// Pieces onto their parents, in rounds — a piece may be reached before
        /// the piece it sits on. Anything whose parent never turns up falls back
        /// to its venue pose, which every venue placement has.
        /// </summary>
        private void Hang(List<Placement> pieces, Transform root)
        {
            var placed = new HashSet<string>();

            for (int round = 0; round < 8 && placed.Count < pieces.Count; round++)
            {
                bool moved = false;

                foreach (Placement p in pieces)
                {
                    if (placed.Contains(p.Id)) { continue; }
                    if (!_spawned.TryGetValue(p.Id, out GameObject go) || go == null)
                    {
                        placed.Add(p.Id);
                        continue;
                    }

                    if (!_spawned.TryGetValue(p.Parent, out GameObject onto) || onto == null)
                    {
                        continue;
                    }

                    if (_known.TryGetValue(p.Parent, out Placement above) &&
                        above.IsChild && !placed.Contains(p.Parent))
                    {
                        continue;
                    }

                    if (go.transform.parent != onto.transform)
                    {
                        go.transform.SetParent(onto.transform, false);
                    }

                    go.transform.localPosition = Where(p.Offset);
                    go.transform.localRotation = Turn(p.Offset);

                    placed.Add(p.Id);
                    moved = true;
                }

                if (!moved) { break; }
            }

            foreach (Placement p in pieces)
            {
                if (placed.Contains(p.Id)) { continue; }
                if (!_spawned.TryGetValue(p.Id, out GameObject go) || go == null) { continue; }

                if (go.transform.parent != root) { go.transform.SetParent(root, false); }

                go.transform.localPosition = Where(p.At);
                go.transform.localRotation = Turn(p.At);
            }
        }

        /// <summary>What is drawn in the venue, by id — so aiming can find it.
        /// A venue's contents live here rather than on the outdoor rig, and
        /// until this existed the crosshair could not see them at all.</summary>
        public IEnumerable<KeyValuePair<string, GameObject>> Objects => _spawned;

        /// <summary>What is known about something in the venue, or null.</summary>
        public Placement Known(string id) =>
            _known.TryGetValue(id, out Placement p) ? p : null;

        private void Clear()
        {
            foreach (GameObject go in _spawned.Values) { if (go != null) { Destroy(go); } }

            _spawned.Clear();
            _known.Clear();
            _markers.Clear();
            _seen.Clear();
            Markers = 0;
            PinnedTo = null;
            _pinnedAt = -1;
            _loaded = null;

            // Not the refused set: a marker nobody has recorded is still not
            // recorded after changing venue, and clearing it here would put
            // the lookup back into asking once a frame.
        }

        /// <summary>Leave the venue but keep watching for markers.</summary>
        public void Leave()
        {
            Venue = "";
            Entered = null;
            _refused.Clear();
            Clear();
        }

        private Transform Root()
        {
            if (VenueRoot != null) { return VenueRoot; }

            var made = new GameObject("Venue");
            VenueRoot = made.transform;
            return VenueRoot;
        }

        private static Vector3 Where(Attachment a) =>
            new Vector3((float)a.X, (float)a.Y, (float)a.Z);

        private static Quaternion Turn(Attachment a) =>
            new Quaternion((float)a.Rotation.X, (float)a.Rotation.Y,
                           (float)a.Rotation.Z, (float)a.Rotation.W);

        // ── putting things in a venue ────────────────────────────

        /// <summary>
        /// Record where a marker is, which is what starts and extends a venue.
        ///
        /// The first one defines the origin and is stored at the identity: a
        /// venue's coordinates are whatever its first marker says they are, and
        /// there is nothing else indoors for them to be relative to. Every one
        /// after it is measured through the frame the earlier markers pinned,
        /// which is why the organizer has to walk from a marker they have
        /// already recorded rather than starting afresh in another room.
        /// </summary>
        public async Task RecordMarkerAsync(string marker)
        {
            if (_rig == null) { throw new InvalidOperationException("no rig"); }
            if (string.IsNullOrEmpty(Venue)) { throw new InvalidOperationException("no venue"); }
            if (string.IsNullOrEmpty(marker)) { throw new ArgumentException("no marker"); }

            ARTrackedImage image = Tracked(marker);
            if (image == null) { throw new InvalidOperationException("that marker is not in view"); }

            Attachment at;

            if (_markers.Count == 0)
            {
                // The first. Everything else in this venue will be measured
                // from where this piece of paper is.
                at = new Attachment { Rotation = Quat.Identity };
                Root().SetPositionAndRotation(image.transform.position, image.transform.rotation);
            }
            else
            {
                if (PinnedTo == null)
                {
                    throw new InvalidOperationException(
                        "walk from a marker this venue already knows, so this one " +
                        "can be measured from it");
                }

                Transform root = Root();
                Vector3 into = root.InverseTransformPoint(image.transform.position);
                Quaternion turn = Quaternion.Inverse(root.rotation) * image.transform.rotation;

                at = new Attachment
                {
                    X = into.x,
                    Y = into.y,
                    Z = into.z,
                    Rotation = new Quat(turn.x, turn.y, turn.z, turn.w)
                };
            }

            await _rig.PlaceInVenueAsync(Venue, "marker", at, marker, marker);
            Refresh();
        }

        /// <summary>Put something in the venue, where the crosshair is.</summary>
        public async Task PlaceAsync(string scene, Vector3 sessionPoint, Quaternion facing,
            string label = "")
        {
            if (_rig == null) { throw new InvalidOperationException("no rig"); }
            if (string.IsNullOrEmpty(Venue)) { throw new InvalidOperationException("no venue"); }

            if (PinnedTo == null)
            {
                throw new InvalidOperationException(
                    "scan one of this venue's markers first — there is nothing to " +
                    "measure from until you have");
            }

            Transform root = Root();
            Vector3 into = root.InverseTransformPoint(sessionPoint);
            Quaternion turn = Quaternion.Inverse(root.rotation) * facing;

            var at = new Attachment
            {
                X = into.x,
                Y = into.y,
                Z = into.z,
                Rotation = new Quat(turn.x, turn.y, turn.z, turn.w)
            };

            await _rig.PlaceInVenueAsync(Venue, scene, at, null, label);
            Refresh();
        }

        /// <summary>
        /// Whether an image's pose can be believed.
        ///
        /// Limited counts. A marker screwed to a wall is not moving, so once
        /// ARKit has found it there is often nothing left to refine and it
        /// reports Limited rather than Tracking — the pose is still the one it
        /// measured, and refusing it means refusing every marker that is doing
        /// its job properly. Only None is useless.
        /// </summary>
        private static bool Usable(ARTrackedImage image) =>
            image != null &&
            image.trackingState != UnityEngine.XR.ARSubsystems.TrackingState.None;

        /// <summary>
        /// Put a piece on something already in the venue.
        ///
        /// The venue pose is written too, so the piece is still somewhere if
        /// its parent is ever removed — the same cache with the same rules as
        /// outdoors, and for the same reason.
        /// </summary>
        public async Task AttachAsync(string parent, string scene, Attachment offset,
            string label = "")
        {
            if (_rig == null) { throw new InvalidOperationException("no rig"); }
            if (string.IsNullOrEmpty(Venue)) { throw new InvalidOperationException("no venue"); }
            if (offset == null) { throw new ArgumentException("nowhere to put it"); }

            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null)
            {
                throw new InvalidOperationException("nothing to attach to");
            }

            Transform root = Root();
            Vector3 world = onto.transform.TransformPoint(Where(offset));
            Quaternion turn = onto.transform.rotation * Turn(offset);

            Vector3 into = root.InverseTransformPoint(world);
            Quaternion inVenue = Quaternion.Inverse(root.rotation) * turn;

            var at = new Attachment
            {
                X = into.x, Y = into.y, Z = into.z,
                Rotation = new Quat(inVenue.x, inVenue.y, inVenue.z, inVenue.w)
            };

            await _rig.PlaceInVenueAsync(Venue, scene, at, null, label, parent, offset);
            Refresh();
        }

        /// <summary>Where a point in front of the camera falls in a venue
        /// object's own frame.</summary>
        public Attachment OffsetFor(string parent, Vector3 sessionPoint, Quaternion facing)
        {
            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null) { return null; }

            Vector3 into = onto.transform.InverseTransformPoint(sessionPoint);
            Quaternion turn = Quaternion.Inverse(onto.transform.rotation) * facing;

            return new Attachment
            {
                X = into.x, Y = into.y, Z = into.z,
                Rotation = new Quat(turn.x, turn.y, turn.z, turn.w)
            };
        }

        /// <summary>Flush against a face of something in the venue.</summary>
        public Attachment SnapTo(string parent, string scene, MarkerOneRig.Face face, float gapM)
        {
            if (_rig == null) { return null; }
            if (!_spawned.TryGetValue(parent, out GameObject onto) || onto == null) { return null; }

            return _rig.SnapBetween(onto, scene, face, gapM);
        }

        /// <summary>Whichever tracked image is this marker, or null.</summary>
        public ARTrackedImage Tracked(string marker)
        {
            if (_images == null) { return null; }

            foreach (ARTrackedImage image in _images.trackables)
            {
                if (Usable(image) && image.referenceImage.name == marker) { return image; }
            }

            return null;
        }

        /// <summary>Every marker the camera can see right now, whether or not
        /// this venue knows it.</summary>
        public IEnumerable<string> InView()
        {
            if (_images == null) { yield break; }

            foreach (ARTrackedImage image in _images.trackables)
            {
                if (Usable(image)) { yield return image.referenceImage.name; }
            }
        }

        /// <summary>Whether this venue already knows the marker.</summary>
        public bool Knows(string marker) => _markers.ContainsKey(marker);
    }
}
