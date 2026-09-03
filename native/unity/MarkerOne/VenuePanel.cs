using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Starting a venue, joining one, and adding markers to it.
    ///
    /// Separate from the placement bar because it is a different job done at a
    /// different time: an organizer walks a building once with this open, and
    /// everybody who comes afterwards never opens it at all — they point the
    /// camera at a marker and the room fills up.
    /// </summary>
    public sealed class VenuePanel : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("MarkerOne Venue");
            go.AddComponent<VenuePanel>();
            DontDestroyOnLoad(go);
        }
#endif

        public static bool Open;

        /// <summary>What this is covering. Empty while closed.</summary>
        public static Rect Occupied;

        private const string Remembered = "MarkerOne.Venue";

        private VenueRig _venue;
        private string _name = "";
        private string _said = "";
        private bool _busy;
        private float _rescan;

        private GUIStyle _text;
        private GUIStyle _field;
        private GUIStyle _button;
        private Texture2D _panel;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan > 0) { return; }
            _rescan = 1f;

            if (_venue != null) { return; }

            _venue = FindFirstObjectByType<VenueRig>();
            if (_venue == null) { return; }

            // Kept across launches. Somebody at a three-day conference should
            // not have to type the venue in every morning.
            if (string.IsNullOrEmpty(_venue.Venue))
            {
                _venue.Venue = PlayerPrefs.GetString(Remembered, "");
            }

            _name = _venue.Venue ?? "";
        }

        private void OnGUI()
        {
            if (!Open || _venue == null || SignInScreen.Blocking)
            {
                Occupied = new Rect();
                return;
            }

            GUI.depth = -500;
            EnsureStyles();

            Rect safe = Screen.safeArea;
            float line = _text.fontSize * 1.9f;
            float pad = _text.fontSize;

            float width = Mathf.Min(safe.width - pad * 2, _text.fontSize * 26);
            float height = line * 9 + pad * 2;

            var panel = new Rect(safe.x + (safe.width - width) * 0.5f,
                                 Mathf.Max(MarkerOneHud.Occupied.yMax + pad,
                                           Screen.height - (safe.y + safe.height) + line),
                                 width, height);
            Occupied = panel;

            GUI.DrawTexture(panel, _panel);

            var row = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, line);
            GUI.Label(row, "Venue", _text);

            row.y += line;
            float wide = row.width - pad - _text.fontSize * 5;
            _name = GUI.TextField(new Rect(row.x, row.y, wide, line), _name, 64, _field);

            var enter = new Rect(row.x + wide + pad, row.y, _text.fontSize * 5, line);
            if (GUI.Button(enter, "Enter", _button))
            {
                _venue.Venue = _name.Trim();
                PlayerPrefs.SetString(Remembered, _venue.Venue);
                PlayerPrefs.Save();
                _venue.Refresh();
                _said = string.IsNullOrEmpty(_venue.Venue) ? "left the venue" : "";
            }

            row.y += line * 1.4f;
            GUI.Label(row, State(), _text);

            row.y += line * 2.4f;

            // Adding markers is the organizer's job and the only part of this
            // that has to be done in the right order: each new marker is
            // measured through the frame the ones before it pinned.
            foreach (string marker in _venue.InView())
            {
                bool known = _venue.Knows(marker);
                string what = known ? marker + " · known" : "Add " + marker;

                if (_busy) { GUI.Label(row, what, _text); }
                else if (GUI.Button(row, what, _button) && !known) { Add(marker); }

                row.y += line * 1.1f;
                break;
            }

            var close = new Rect(panel.x + pad, panel.yMax - pad - line,
                                 _text.fontSize * 6, line);
            if (GUI.Button(close, "Close", _button)) { Open = false; }

            if (string.IsNullOrEmpty(_said)) { return; }

            GUI.Label(new Rect(close.xMax + pad, close.y,
                               panel.width - close.width - pad * 3, line), _said, _text);
        }

        private string State()
        {
            if (string.IsNullOrEmpty(_venue.Venue))
            {
                return "Type a name and press Enter. A new name starts a venue; "
                     + "an existing one joins it.";
            }

            if (!string.IsNullOrEmpty(_venue.Trouble)) { return _venue.Trouble; }

            if (_venue.PinnedTo == null)
            {
                return _venue.Markers + " markers · point the camera at one";
            }

            return string.Format("{0} items · pinned to {1} {2:0}s ago · {3}/{4} markers seen",
                                 _venue.Items, _venue.PinnedTo, _venue.PinnedSecondsAgo,
                                 _venue.Seen, _venue.Markers);
        }

        private async void Add(string marker)
        {
            _busy = true;
            _said = "recording…";

            try
            {
                await _venue.RecordMarkerAsync(marker);
                _said = marker + " recorded";
            }
            catch (System.Exception e) { _said = e.Message; }
            finally { _busy = false; }
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.018f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0, 0, 0, 0.82f));
                _panel.Apply();
                _panel.hideFlags = HideFlags.HideAndDontSave;
            }

            _text = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
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
