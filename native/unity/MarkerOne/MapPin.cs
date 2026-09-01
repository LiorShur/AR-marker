using MarkerOne.Core;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Dropping a pin at a coordinate, from inside the app.
    ///
    /// The same thing scripts/place.mjs does, without a laptop. Which matters
    /// more than convenience: the moment you want to seed a place is usually
    /// the moment you are looking at it on a map, and that is rarely at a desk.
    ///
    /// One field for the coordinates, not two, because Google Maps copies both
    /// at once — right-click a spot and the first menu item is
    /// "-33.924900, 18.424100", which pastes straight in. Splitting that across
    /// two boxes would mean editing a string by hand to use it, which is work
    /// invented for the sake of a tidier form.
    /// </summary>
    public sealed class MapPin : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("MarkerOne Map Pin");
            go.AddComponent<MapPin>();
            DontDestroyOnLoad(go);
        }
#endif

        public static bool Open;

        private MarkerOneRig _rig;
        private float _rescan;

        private string _coordinates = "";
        private string _label = "";
        private int _scene;
        private string _said = "";
        private bool _pinning;

        private GUIStyle _text;
        private GUIStyle _field;
        private GUIStyle _button;
        private Texture2D _panel;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan <= 0 && _rig == null)
            {
                _rescan = 1f;
                _rig = FindFirstObjectByType<MarkerOneRig>();
                if (_rig != null) { _rig.Placed += OnPinned; }
            }
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Placed -= OnPinned; }
        }

        /// <summary>
        /// What became of the write.
        ///
        /// Without this the panel said "pinning…" for ever: the rig raises the
        /// outcome as an event and only the placement bar was listening, so a
        /// pin that succeeded and a pin that failed looked identical and both
        /// looked like nothing happening.
        ///
        /// The fields are cleared here rather than when the button is pressed,
        /// because a failed write should leave what was typed alone — the whole
        /// point of the message is that somebody might want to try again.
        /// </summary>
        private void OnPinned(bool ok, string detail)
        {
            if (!_pinning) { return; }
            _pinning = false;

            _said = detail;
            if (!ok) { return; }

            _coordinates = "";
            _label = "";
        }

        private void OnGUI()
        {
            if (!Open || _rig == null || SignInScreen.Blocking) { return; }

            EnsureStyles();

            Rect safe = Screen.safeArea;
            float line = _text.fontSize * 1.9f;
            float pad = _text.fontSize;

            float width = Mathf.Min(safe.width - pad * 2, _text.fontSize * 26);
            float height = line * 7 + pad * 2;

            var panel = new Rect(safe.x + (safe.width - width) * 0.5f,
                                 Screen.height - (safe.y + safe.height) + line * 2,
                                 width, height);

            GUI.DrawTexture(panel, _panel);

            var row = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, line);

            GUI.Label(row, "Latitude, longitude", _text);

            row.y += line;
            _coordinates = GUI.TextField(row, _coordinates, 128, _field);

            row.y += line * 1.2f;
            GUI.Label(row, "Name", _text);

            row.y += line;
            _label = GUI.TextField(row, _label, 40, _field);

            row.y += line * 1.3f;
            float third = (row.width - pad * 2) / 3;

            var cell = new Rect(row.x, row.y, third, line);
            if (GUI.Button(cell, SceneId() ?? "—", _button)) { _scene++; }

            cell.x += third + pad;
            if (GUI.Button(cell, "Drop pin", _button)) { Drop(); }

            cell.x += third + pad;
            if (GUI.Button(cell, "Close", _button)) { Open = false; }

            if (!string.IsNullOrEmpty(_said))
            {
                row.y += line * 1.2f;
                GUI.Label(row, _said, _text);
            }
        }

        private void Drop()
        {
            if (!Coordinates.TryParse(_coordinates, out double lat, out double lon))
            {
                _said = "expected something like  -33.9249, 18.4241";
                return;
            }

            string scene = SceneId();
            if (string.IsNullOrEmpty(scene))
            {
                _said = "no scenes configured on the rig";
                return;
            }

            _pinning = true;
            _said = "pinning…";
            _rig.Seed(scene, lat, lon, _label);
        }

        private string SceneId()
        {
            if (_rig == null || _rig.Scenes == null || _rig.Scenes.Count == 0) { return null; }
            return _rig.Scenes[_scene % _rig.Scenes.Count].Scene;
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.019f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0, 0, 0, 0.86f));
                _panel.Apply();
                _panel.hideFlags = HideFlags.HideAndDontSave;
            }

            _text = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = false };
            _text.normal.textColor = Color.white;

            _field = new GUIStyle(GUI.skin.textField) { fontSize = size };
            _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        }

        private void OnDestroy()
        {
            if (_panel != null) { Destroy(_panel); }
        }
    }
}
