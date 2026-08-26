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

        /// <summary>What the crosshair is nearest to, and whether we are in the
        /// middle of moving it.</summary>
        private string _selected;
        private bool _adjusting;

        [Tooltip("How far off the crosshair a placement can be and still count "
               + "as the thing being aimed at, in degrees.")]
        public float SelectWithinDeg = 12f;

        private Vector3 _target;
        private bool _onSurface;
        private float _range;

        private GUIStyle _text;
        private GUIStyle _button;
        private Texture2D _panel;
        private Texture2D _mark;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan <= 0 && (_rig == null || _raycaster == null || _camera == null))
            {
                _rescan = 1f;
                if (_rig == null)
                {
                    _rig = FindFirstObjectByType<MarkerOneRig>();
                    if (_rig != null) { _rig.Placed += OnPlaced; }
                }
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
            if (_adjusting || _rig == null || _camera == null) { return; }

            Vector3 eye = _camera.transform.position;
            Vector3 look = _camera.transform.forward;

            string best = null;
            float bestAngle = SelectWithinDeg;

            foreach (KeyValuePair<string, GameObject> entry in _rig.Objects)
            {
                if (entry.Value == null || !entry.Value.activeSelf) { continue; }

                Vector3 to = entry.Value.transform.position - eye;
                if (to.sqrMagnitude < 0.01f) { continue; }

                float angle = Vector3.Angle(look, to);
                if (angle >= bestAngle) { continue; }

                bestAngle = angle;
                best = entry.Key;
            }

            _selected = best;
        }

        /// <summary>Where the crosshair is pointing, in world space — which is
        /// also session space, since the XR Origin sits at the identity. Feed()
        /// passes the camera's world position on the same assumption.</summary>
        private void Aim()
        {
            if (_camera == null) { return; }

            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            if (_raycaster != null &&
                _raycaster.Raycast(centre, _hits, TrackableType.PlaneWithinPolygon))
            {
                _target = _hits[0].pose.position;
                _onSurface = true;
                _range = Vector3.Distance(_camera.transform.position, _target);
                return;
            }

            // No plane. Still allow it — waist-high in mid-air is a legitimate
            // place to leave something, and refusing to place anything until a
            // plane is found makes the app feel broken on grass and gravel,
            // which is most of where this is meant to work.
            _target = _camera.transform.position + _camera.transform.forward * FallbackDistanceM;
            _onSurface = false;
            _range = FallbackDistanceM;
        }

        private void Place()
        {
            if (_rig == null) { Say("no rig in scene"); return; }
            if (!_rig.CanPlace) { Say("not located yet — " + _rig.State); return; }

            string scene = SceneId();
            if (string.IsNullOrEmpty(scene)) { Say("no scenes configured on the rig"); return; }

            // A coordinate is written once and is wrong for ever. Waiting a few
            // seconds for ARCore to be sure costs nothing next to that, and
            // below this threshold no anchor would be created anyway — so a
            // placement made now would be stored badly and drawn from the
            // frame, which is the combination that has wasted the most time.
            double accuracy = _rig.Anchors != null ? _rig.Anchors.AccuracyM : -1;
            if (_rig.Anchors != null && accuracy <= 0)
            {
                Say("waiting for ARCore to be sure where it is");
                return;
            }

            _rig.Place(scene, _target, _label);

            // Not "placed" — the write has not been anywhere yet. OnPlaced says
            // what happened when it comes back.
            Say("placing " + scene + (string.IsNullOrEmpty(_label) ? "" : " · " + _label) + "…");
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
            GUI.DrawTexture(bar, _panel);

            float x = bar.x + pad;
            float w = (bar.width - pad * 5) / 4;
            var row = new Rect(x, bar.y + pad * 0.5f, w, line);

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

            if (GUI.Button(row, SceneId() ?? "—", _button))
            {
                _scene++;
            }

            row.x += w + pad;
            _label = GUI.TextField(row, _label, 40, _text);

            row.x += w + pad;

            // The button becomes Adjust when the crosshair is on something.
            // Aiming at a placement and pressing Place would otherwise put a
            // second one on top of the first, which is never what was meant.
            if (_selected != null)
            {
                if (GUI.Button(row, _rig.IsSeed(_selected) ? "Correct" : "Move", _button))
                {
                    _adjusting = true;
                    Say("aim at where it really belongs");
                }
            }
            else if (GUI.Button(row, "Place", _button))
            {
                Place();
            }

            // Two taps, because it deletes everything and a stray thumb on a
            // phone held at arm's length is not a decision.
            row.x += w + pad;
            bool arming = Time.unscaledTime < _clearArmedUntil;
            if (GUI.Button(row, arming ? "Sure?" : "Clear", _button))
            {
                if (arming)
                {
                    _clearArmedUntil = 0;
                    if (_rig != null) { _rig.ClearAll(); }
                    Say("clearing…");
                }
                else
                {
                    _clearArmedUntil = Time.unscaledTime + 4f;
                }
            }

            // What the crosshair is over, and what just happened.
            var note = new Rect(bar.x + pad, bar.y + line + pad * 0.6f,
                                bar.width - pad * 2, line);
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
            else if (_selected != null)
            {
                status = _rig != null && _rig.IsSeed(_selected)
                    ? "from a map — aim at the real spot and Correct it"
                    : "aiming at a placement";
            }
            else
            {
                // The accuracy is what the placement will be stored at, so it
                // belongs where somebody about to press Place can see it.
                double accuracy = _rig != null && _rig.Anchors != null
                    ? _rig.Anchors.AccuracyM
                    : -1;

                status = string.Format("{0} · {1:0.0}m{2}",
                                       _onSurface ? "surface" : "mid-air", _range,
                                       accuracy > 0
                                           ? string.Format("  ·  places at ±{0:0.#}m", accuracy)
                                           : "  ·  waiting for a fix");
            }
            GUI.Label(note, status, _text);
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

            GUI.color = _adjusting || _selected != null
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
