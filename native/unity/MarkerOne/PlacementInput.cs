using System.Collections.Generic;
using MarkerOne.Core;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Putting something down.
    ///
    /// A crosshair at the centre of the screen rather than tap-to-place. Two
    /// reasons: aiming with the phone is steadier than aiming with a thumb at
    /// arm's length, and a fixed reticle can report what it is pointing at
    /// before anything is committed — a tap that silently lands on nothing is
    /// the single most confusing thing an AR app can do.
    ///
    /// Self-installing and IMGUI for the same reason as MarkerOneHud: there is
    /// nothing to add to the scene, so there is nothing to wire wrongly. This
    /// wants replacing with a real interface eventually. It is not that.
    /// </summary>
    public sealed class PlacementInput : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("MarkerOne Placement");
            go.AddComponent<PlacementInput>();
            DontDestroyOnLoad(go);
        }
#endif

        /// <summary>Where to put something when no surface is found. Far enough
        /// to be in front of you, near enough to be somewhere you can see.</summary>
        public float FallbackDistanceM = 2f;

        private MarkerOneRig _rig;
        private ARRaycastManager _raycaster;
        private Camera _camera;
        private float _rescan;

        private readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();

        private int _scene;
        private string _label = "";
        private string _said;
        private float _saidUntil;
        private float _clearArmedUntil;
        private float _deleteArmedUntil;

        /// <summary>What the crosshair is nearest to, and whether we are in the
        /// middle of moving it.</summary>
        private string _selected;

        /// <summary>What the next piece is being put on, while building.</summary>
        private string _building;

        private VenueRig _venue;
        private bool _adjusting;

        [Tooltip("How far off the crosshair a placement can be and still count "
               + "as the thing being aimed at, in degrees.")]
        public float SelectWithinDeg = 12f;

        [Tooltip("Seconds to wait for ARCore before allowing a placement to be "
               + "made from GPS instead. Somewhere remote this wait never ends "
               + "on its own.")]
        public float WaitForArcoreS = 20f;

        private float _firstAimedAt;

        /// <summary>The control bar, so the compass does not label its arrows
        /// on top of the buttons.</summary>
        public static Rect Occupied;

        private Vector3 _target;
        private bool _onSurface;
        private string _surface = "mid-air";
        private float _range;

        private GUIStyle _text;
        private GUIStyle _button;
        private Texture2D _panel;
        private Texture2D _mark;

        private void Update()
        {
            if (_firstAimedAt <= 0) { _firstAimedAt = Time.unscaledTime; }

            _rescan -= Time.unscaledDeltaTime;
            if (_rescan <= 0 && (_rig == null || _raycaster == null || _camera == null))
            {
                _rescan = 1f;
                if (_rig == null)
                {
                    _rig = FindFirstObjectByType<MarkerOneRig>();
                    if (_rig != null) { _rig.Placed += OnPlaced; }
                }
                if (_venue == null) { _venue = FindFirstObjectByType<VenueRig>(); }
                if (_raycaster == null) { _raycaster = FindFirstObjectByType<ARRaycastManager>(); }
                if (_camera == null) { _camera = Camera.main; }
            }

            Aim();
            Select();
        }

        /// <summary>
        /// Whatever the crosshair is nearest to, by angle rather than by
        /// distance.
        ///
        /// Angle is what aiming means: a cube two metres away and one thirty
        /// metres away are both "that one" if they are under the crosshair, and
        /// picking by distance would always take the near one. Nothing is
        /// raycast because placements deliberately carry no colliders — one on
        /// every placement would start intercepting the placement raycast, and
        /// then aiming past an object to put something behind it would stop
        /// working.
        /// </summary>
        private void Select()
        {
            if (_adjusting || _building != null || _rig == null || _camera == null) { return; }

            Vector3 eye = _camera.transform.position;
            Vector3 look = _camera.transform.forward;

            string best = null;
            float bestMiss = SelectWithinDeg;

            foreach (KeyValuePair<string, GameObject> entry in _rig.Objects)
            {
                if (entry.Value == null || !entry.Value.activeSelf) { continue; }

                if (!Middle(entry.Value, out Vector3 middle, out float radius)) { continue; }

                Vector3 to = middle - eye;
                float away = to.magnitude;
                if (away < 0.05f) { continue; }

                // How far outside the thing the crosshair is, rather than how
                // far from a point on it. A pivot sits at the base, so aiming
                // at the middle of a cube two metres away missed its pivot by
                // twenty degrees and selected nothing — the object filled the
                // screen and the bar said "mid-air".
                float half = Mathf.Atan2(radius, away) * Mathf.Rad2Deg;
                float miss = Vector3.Angle(look, to) - half;

                if (miss >= bestMiss) { continue; }

                bestMiss = miss;
                best = entry.Key;
            }

            _selected = best;
        }

        /// <summary>
        /// Where a placement actually is and how big it looks, from its
        /// renderers rather than from its transform.
        ///
        /// False for something with nothing drawn in it, which is not aimable
        /// at by definition.
        /// </summary>
        private static bool Middle(GameObject go, out Vector3 middle, out float radius)
        {
            middle = go.transform.position;
            radius = 0;

            // The contact shadow is a renderer too, and it is a flat ellipse
            // wider than the thing casting it — counting it would pull the
            // middle down to the ground and make the object selectable from
            // well off to either side of it.
            var bounds = new Bounds();
            bool any = false;

            foreach (Renderer part in go.GetComponentsInChildren<Renderer>())
            {
                if (part.gameObject.name == "shadow") { continue; }

                if (!any) { bounds = part.bounds; any = true; }
                else { bounds.Encapsulate(part.bounds); }
            }

            if (!any) { return false; }

            middle = bounds.center;

            // The largest extent rather than the diagonal: a wide flat plaque
            // should be selectable across its width without also being
            // selectable from well above it.
            radius = Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z));
            return true;
        }

        /// <summary>Where the crosshair is pointing, in world space — which is
        /// also session space, since the XR Origin sits at the identity. Feed()
        /// passes the camera's world position on the same assumption.</summary>
        private void Aim()
        {
            if (_camera == null) { return; }

            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            // Planes first, then the depth image, then feature points.
            //
            // Plane detection wants flat, textured, man-made surfaces and finds
            // almost nothing on grass, gravel or a brick step — which is most of
            // where this is used, and why the bar kept reading "mid-air". An
            // object left hanging two metres up then intersects the ground when
            // you walk round it, and with occlusion on that reads as the object
            // breaking apart rather than as the object being in the wrong place.
            //
            // The device has LiDAR and AR Foundation will raycast against the
            // depth image, which hits real ground almost anywhere. Feature
            // points are the last resort: sparse and noisy, but a point on the
            // actual surface beats a guess at two metres.
            const TrackableType anything = TrackableType.PlaneWithinPolygon
                                         | TrackableType.Depth
                                         | TrackableType.FeaturePoint;

            if (_raycaster != null && _raycaster.Raycast(centre, _hits, anything))
            {
                ARRaycastHit best = Best(_hits);

                _target = best.pose.position;
                _onSurface = true;
                _surface = Describe(best.hitType);
                _range = Vector3.Distance(_camera.transform.position, _target);
                return;
            }

            // Nothing at all to stand on. Still allowed: waist-high in mid-air
            // is a legitimate place to leave something, and refusing until a
            // surface is found would make the app useless facing open ground.
            _target = _camera.transform.position + _camera.transform.forward * FallbackDistanceM;
            _onSurface = false;
            _surface = "mid-air";
            _range = FallbackDistanceM;
        }

        /// <summary>
        /// Put it in the venue rather than on the Earth.
        ///
        /// The same crosshair and the same target point; only the frame it is
        /// measured in differs. Indoors there is no fix to write down, so the
        /// venue's own origin is the only thing a pose can be relative to.
        /// </summary>
        private async void InVenue()
        {
            string scene = SceneId();
            if (string.IsNullOrEmpty(scene)) { Say("no scenes configured on the rig"); return; }

            Quaternion facing = Quaternion.identity;
            if (_camera != null)
            {
                Vector3 forward = _camera.transform.forward;
                facing = Quaternion.Euler(0, Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg, 0);
            }

            try
            {
                await _venue.PlaceAsync(scene, _target, facing, _label);
                Say("placed in " + _venue.Venue);
            }
            catch (System.Exception e) { Say(e.Message); }
        }

        /// <summary>
        /// Delete the thing being aimed at, twice-confirmed.
        ///
        /// Offered for what this device owns, and for anything at all to an
        /// admin. A map seed belongs to whoever ran the script and is meant to
        /// be claimed by correcting it rather than removed, so deleting one is
        /// an admin's business.
        /// </summary>
        private void Remove(Rect at, float w, float pad)
        {
            bool mine = _rig.IsMine(_selected);
            if (!mine && !_rig.IsAdmin)
            {
                GUI.Label(new Rect(at.x, at.y, w * 2, at.height), "someone else's", _text);
                return;
            }

            bool arming = Time.unscaledTime < _deleteArmedUntil;
            if (!GUI.Button(at, arming ? "Sure?" : "Delete", _button)) { return; }

            if (!arming)
            {
                _deleteArmedUntil = Time.unscaledTime + 4f;
                return;
            }

            _deleteArmedUntil = 0;
            _rig.Remove(_selected);
            Say("removing…");
        }

        private void Place()
        {
            if (_rig == null) { Say("no rig in scene"); return; }

            if (_venue != null && !string.IsNullOrEmpty(_venue.Venue))
            {
                InVenue();
                return;
            }

            if (!_rig.CanPlace) { Say("not located yet — " + _rig.State); return; }

            string scene = SceneId();
            if (string.IsNullOrEmpty(scene)) { Say("no scenes configured on the rig"); return; }

            // Refusing while ARCore is still settling is right. Refusing where
            // ARCore will never arrive is not.
            //
            // Out past the last of the Street View coverage, Earth does not
            // track and is not going to, and an app that will not let you leave
            // a marker in a place precisely because it is remote has the logic
            // exactly backwards. So the wait is bounded: after it the placement
            // goes through the frame — five or fifteen metres rather than half
            // a metre — and the bar says which it was rather than pretending
            // they are the same thing.
            double accuracy = _rig.Anchors != null ? _rig.Anchors.AccuracyM : -1;
            bool precise = accuracy > 0;

            if (!precise && Time.unscaledTime < _firstAimedAt + WaitForArcoreS)
            {
                Say("waiting for ARCore — it will place from GPS shortly");
                return;
            }

            _rig.Place(scene, _target, _label);

            // Not "placed" — the write has not been anywhere yet. OnPlaced says
            // what happened when it comes back.
            Say("placing " + scene + (precise ? "" : " from GPS — roughly here") + "…");
        }

        private string SceneId()
        {
            if (_rig == null || _rig.Scenes == null || _rig.Scenes.Count == 0) { return null; }
            return _rig.Scenes[_scene % _rig.Scenes.Count].Scene;
        }

        private void OnPlaced(bool ok, string detail)
        {
            Say(ok ? "placed " + detail : "could not place — " + detail);
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Placed -= OnPlaced; }
        }

        private void Say(string message)
        {
            _said = message;
            _saidUntil = Time.unscaledTime + 3f;
        }

        private void OnGUI()
        {
            // Nothing to place until somebody has signed in. The bar drawn
            // underneath a screen that must be answered first is an invitation
            // to do work that will be refused.
            if (SignInScreen.Blocking) { return; }

            EnsureStyles();

            Rect safe = Screen.safeArea;
            float bottom = Screen.height - safe.y;
            float line = _text.fontSize * 1.5f;
            float pad = 10;

            Crosshair();

            // Controls along the bottom, above the home indicator.
            float barHeight = line * 2.6f;
            var bar = new Rect(safe.x + pad, bottom - barHeight - pad,
                               safe.width - pad * 2, barHeight);
            Occupied = bar;
            GUI.DrawTexture(bar, _panel);

            float x = bar.x + pad;
            float w = (bar.width - pad * 5) / 4;
            var row = new Rect(x, bar.y + pad * 0.5f, w, line);

            Buttons(row, w, pad);
            Note(new Rect(bar.x + pad, bar.y + line + pad * 0.6f, bar.width - pad * 2, line));
        }

        /// <summary>
        /// The top row, which is about what you can do. Every path out of here
        /// is a return, which is why the line underneath is drawn by the
        /// caller: it belongs to all of them.
        /// </summary>
        private void Buttons(Rect row, float w, float pad)
        {

            // Adjusting takes over the bar. Being able to place a new thing
            // while halfway through moving an old one is a way to end up with
            // both and mean neither.
            if (_adjusting)
            {
                if (GUI.Button(row, "Cancel", _button)) { _adjusting = false; }

                row.x += w + pad;
                row.width = w * 2 + pad;
                if (GUI.Button(row, "Put it here", _button))
                {
                    _rig.Adjust(_selected, _target, Facing());
                    _adjusting = false;
                }

                return;
            }

            // Mid-build. The bar is about the piece being added, not about
            // anything else that could be done meanwhile.
            if (_building != null)
            {
                if (GUI.Button(row, "Cancel", _button)) { _building = null; }

                row.x += w + pad;
                if (GUI.Button(row, SceneId() ?? "—", _button)) { _scene++; }

                row.x += w + pad;
                row.width = w * 2 + pad;
                if (GUI.Button(row, "Put it here", _button))
                {
                    _rig.Attach(_building, SceneId(), _target, _label);
                    _building = null;
                }

                return;
            }

            // Aiming at something changes what the bar is for. Aiming at a
            // placement and pressing Place would otherwise put a second one on
            // top of the first, which is never what was meant.
            if (_selected != null)
            {
                if (GUI.Button(row, _rig.IsSeed(_selected) ? "Correct" : "Move", _button))
                {
                    _adjusting = true;
                    Say("aim at where it really belongs");
                }

                row.x += w + pad;

                // Building rather than placing. A piece put on something keeps
                // its position relative to that thing for good, which is the
                // only way a structure survives: two things anchored separately
                // are corrected separately and come apart.
                if (GUI.Button(row, "Build on", _button))
                {
                    _building = _selected;
                    Say("aim where the next piece goes");
                }

                row.x += w + pad;
                Remove(row, w, pad);
                return;
            }


            if (GUI.Button(row, SceneId() ?? "—", _button))
            {
                _scene++;
            }

            row.x += w + pad;
            _label = GUI.TextField(row, _label, 40, _text);

            row.x += w + pad;
            if (GUI.Button(row, "Place", _button))
            {
                Place();
            }

            // Only offered to somebody it would work for. The rules refuse it
            // otherwise, and a button whose only outcome is a refusal is worse
            // than no button.
            if (!_rig.IsAdmin) { return; }

            // Two taps, because it erases a shared world and a stray thumb on a
            // phone held at arm's length is not a decision.
            row.x += w + pad;
            bool arming = Time.unscaledTime < _clearArmedUntil;
            if (GUI.Button(row, arming ? "Erase all?" : "Clear", _button))
            {
                if (arming)
                {
                    _clearArmedUntil = 0;
                    _rig.ClearAll();
                    Say("clearing…");
                }
                else
                {
                    _clearArmedUntil = Time.unscaledTime + 4f;
                }
            }
        }

        /// <summary>What the crosshair is over, and what just happened.</summary>
        private void Note(Rect note)
        {
            string status;
            if (Time.unscaledTime < _saidUntil && _said != null)
            {
                status = _said;
            }
            else if (_raycaster == null)
            {
                status = "no AR Raycast Manager — everything lands in mid-air";
            }
            else if (_adjusting)
            {
                status = "aim at where it really belongs, then Put it here";
            }
            else if (_building != null)
            {
                status = "part of " + Describe(_building);
            }
            else if (_selected != null)
            {
                status = Describe(_selected);
            }
            else
            {
                // The accuracy is what the placement will be stored at, so it
                // belongs where somebody about to press Place can see it.
                double accuracy = _rig != null && _rig.Anchors != null
                    ? _rig.Anchors.AccuracyM
                    : -1;

                string quality;
                if (accuracy > 0)
                {
                    quality = string.Format("  ·  places at ±{0:0.#}m", accuracy);
                }
                else if (Time.unscaledTime < _firstAimedAt + WaitForArcoreS)
                {
                    quality = "  ·  waiting for a fix";
                }
                else
                {
                    // No visual fix here, and there is not going to be. Say
                    // what will happen rather than what is missing.
                    quality = "  ·  no visual fix — will place from GPS";
                }

                status = string.Format("{0} · {1:0.0}m{2}", _surface, _range, quality);
            }
            GUI.Label(note, status, _text);
        }

        /// <summary>
        /// The most trustworthy hit, not the nearest.
        ///
        /// Raycast results come back sorted by distance, and the nearest is
        /// often a feature point floating slightly in front of the surface
        /// everything else agrees on. A plane is a considered answer, depth is
        /// a measurement, and a feature point is a guess that happened to be
        /// close — so they are preferred in that order regardless of which
        /// arrived first.
        /// </summary>
        private static ARRaycastHit Best(List<ARRaycastHit> hits)
        {
            ARRaycastHit best = hits[0];
            int rank = Rank(best.hitType);

            foreach (ARRaycastHit hit in hits)
            {
                int r = Rank(hit.hitType);
                if (r <= rank) { continue; }

                rank = r;
                best = hit;
            }

            return best;
        }

        private static int Rank(TrackableType type)
        {
            if ((type & TrackableType.PlaneWithinPolygon) != 0) { return 3; }
            if ((type & TrackableType.Depth) != 0) { return 2; }
            return 1;
        }

        private static string Describe(TrackableType type)
        {
            if ((type & TrackableType.PlaneWithinPolygon) != 0) { return "surface"; }
            if ((type & TrackableType.Depth) != 0) { return "ground"; }
            return "a point";
        }

        /// <summary>
        /// What is known about the thing being aimed at, in one line.
        ///
        /// Name, who left it, when, and whether it came off a map — the last
        /// being the one that changes what somebody should do about it, since a
        /// seed is asking to be corrected and an aimed placement is not.
        /// </summary>
        private string Describe(string id)
        {
            PlacedItem item = _rig.Info(id);
            if (item == null) { return "aiming at a placement"; }

            var said = new System.Text.StringBuilder();

            said.Append(_rig.IsAttached(id) ? "⛓ " : _rig.IsSeed(id) ? "⌖ " : "◆ ");
            said.Append(string.IsNullOrEmpty(item.Label) ? item.Scene : item.Label);

            if (!string.IsNullOrEmpty(item.Author))
            {
                said.Append("  ·  by ").Append(item.Author);
            }

            string when = Day(item.CreatedAt);
            if (when != null) { said.Append("  ·  ").Append(when); }

            if (_rig.IsSeed(id)) { said.Append("  ·  from a map, Correct it here"); }

            return said.ToString();
        }

        /// <summary>The date out of an ISO timestamp, without parsing it. A
        /// wrong date is worse than no date, and the first ten characters are
        /// the date in every string this writes.</summary>
        private static string Day(string createdAt)
        {
            if (string.IsNullOrEmpty(createdAt) || createdAt.Length < 10) { return null; }
            return createdAt.Substring(0, 10);
        }

        /// <summary>Which way the object should face: back towards whoever is
        /// putting it there, as with a new placement.</summary>
        private float Facing()
        {
            if (_camera == null) { return 0; }

            Vector3 forward = _camera.transform.forward;
            return Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg;
        }

        private void Crosshair()
        {
            float size = Mathf.Max(2, Screen.height * 0.0022f);
            float arm = size * 9;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            GUI.color = _adjusting || _building != null || _selected != null
                ? new Color(1f, 0.85f, 0.3f, 0.95f)
                : _onSurface ? new Color(0.4f, 1f, 0.55f, 0.95f)
                             : new Color(1f, 1f, 1f, 0.6f);
            GUI.DrawTexture(new Rect(cx - arm, cy - size * 0.5f, arm * 2, size), _mark);
            GUI.DrawTexture(new Rect(cx - size * 0.5f, cy - arm, size, arm * 2), _mark);
            GUI.color = Color.white;
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.019f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_panel == null)
            {
                _panel = Solid(new Color(0, 0, 0, 0.72f));
                _mark = Solid(Color.white);
            }

            _text = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false
            };
            _text.normal.textColor = Color.white;

            _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        }

        private static Texture2D Solid(Color colour)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, colour);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private void OnDestroy()
        {
            if (_panel != null) { Destroy(_panel); }
            if (_mark != null) { Destroy(_mark); }
        }
    }
}
