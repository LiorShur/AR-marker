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
            float w = (bar.width - pad * 4) / 3;
            var row = new Rect(x, bar.y + pad * 0.5f, w, line);

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
            else
            {
                status = string.Format("{0} · {1:0.0}m",
                                       _onSurface ? "surface" : "mid-air", _range);
            }
            GUI.Label(note, status, _text);
        }

        private void Crosshair()
        {
            float size = Mathf.Max(2, Screen.height * 0.0022f);
            float arm = size * 9;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            GUI.color = _onSurface ? new Color(0.4f, 1f, 0.55f, 0.95f)
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
