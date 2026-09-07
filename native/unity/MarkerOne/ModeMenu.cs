using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// What are you doing, before you start doing it.
    ///
    /// The three modes are not settings on one thing, they are three different
    /// answers to "how does this app know where something is" — the Earth, this
    /// session, or a printed marker — and each is right where the others are
    /// useless. Asking once at the start costs a tap and saves the failure that
    /// has come up most in testing: standing indoors in the outdoor mode, where
    /// everything works, nothing complains, and the placements come back metres
    /// away tomorrow.
    /// </summary>
    public sealed class ModeMenu : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            AppMode.Chosen = false;
            AppMode.Now = AppMode.Working.Outdoors;

            var go = new GameObject("MarkerOne Mode");
            go.AddComponent<ModeMenu>();
            DontDestroyOnLoad(go);
        }
#endif

        public static bool Open;

        /// <summary>Whether the app is waiting for a mode to be chosen. Read by
        /// everything that draws, the same way the sign-in screen is.</summary>
        public static bool Blocking { get; private set; }

        private VenueRig _venue;
        private SketchRig _sketch;
        private float _rescan;

        private GUIStyle _text;
        private GUIStyle _dim;
        private GUIStyle _title;
        private GUIStyle _button;
        private Texture2D _card;
        private Texture2D _scrim;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan <= 0)
            {
                _rescan = 1f;
                if (_venue == null) { _venue = FindFirstObjectByType<VenueRig>(); }

                if (_sketch == null)
                {
                    _sketch = FindFirstObjectByType<SketchRig>();
                    if (_sketch == null)
                    {
                        var go = new GameObject("MarkerOne Sketch");
                        _sketch = go.AddComponent<SketchRig>();
                        DontDestroyOnLoad(go);
                    }
                }
            }

            // After signing in, not before. Two full-screen questions at once is
            // one question nobody reads.
            Blocking = !SignInScreen.Blocking && !AppMode.Chosen;
            if (Blocking) { Open = true; }
        }

        private void OnGUI()
        {
            if (!Open || SignInScreen.Blocking) { return; }

            GUI.depth = -900;
            EnsureStyles();

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _scrim);

            Rect safe = Screen.safeArea;
            float size = _text.fontSize;
            float line = size * 1.9f;
            float pad = size * 1.4f;

            float width = Mathf.Min(safe.width - pad * 2, size * 28);
            float height = pad * 2 + line * 15.5f;

            var card = new Rect(safe.x + (safe.width - width) * 0.5f,
                                Screen.height - (safe.y + safe.height) +
                                    Mathf.Max(line, (safe.height - height) * 0.35f),
                                width, height);

            GUI.DrawTexture(card, _card);

            var row = new Rect(card.x + pad, card.y + pad, card.width - pad * 2, line * 1.6f);
            GUI.Label(row, "Where are you?", _title);

            row.y += line * 1.9f;
            row.height = line;

            Choice(ref row, line, AppMode.Working.Outdoors, "Outdoors",
                   "Anchored to the Earth. Half a metre in a city, and what you "
                 + "leave stays there for anyone.");

            Choice(ref row, line, AppMode.Working.Indoors, "Indoors, this session",
                   "No setup, nothing kept. Indoors there is no fix worth storing, "
                 + "so this holds things steady while you work and forgets them "
                 + "when you close the app.");

            Choice(ref row, line, AppMode.Working.Venue, "Venue",
                   "Pinned by printed markers. Centimetres, repeatable, shared — "
                 + "the accurate indoor option, and the one that needs paper on "
                 + "the wall first.");

            // Changing mode later is a tap on the readout, so this is a start
            // rather than a commitment.
            GUI.Label(new Rect(card.x + pad, card.yMax - pad - line, card.width - pad * 2, line),
                      "You can change this at any time from the readout.", _dim);
        }

        private void Choice(ref Rect row, float line, AppMode.Working what, string name,
            string why)
        {
            if (GUI.Button(new Rect(row.x, row.y, row.width, line * 1.2f), name, _button))
            {
                Pick(what);
            }

            row.y += line * 1.4f;
            GUI.Label(new Rect(row.x, row.y, row.width, line * 2.2f), why, _dim);
            row.y += line * 2.5f;
        }

        private void Pick(AppMode.Working what)
        {
            AppMode.Now = what;
            AppMode.Chosen = true;
            Open = false;

            // The venue rig is told rather than left to work it out, so that
            // "Outdoors" means outdoors even standing in front of a marker.
            if (_venue != null)
            {
                _venue.Choosing = what == AppMode.Working.Venue
                    ? VenueRig.Mode.Auto
                    : VenueRig.Mode.World;
            }

            if (what != AppMode.Working.Indoors && _sketch != null) { _sketch.Clear(); }

            VenuePanel.Open = what == AppMode.Working.Venue &&
                              (_venue == null || string.IsNullOrEmpty(_venue.Venue));
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.018f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_card == null)
            {
                _card = Solid(new Color(1, 1, 1, 0.10f));
                _scrim = Solid(new Color(0.06f, 0.07f, 0.09f, 1f));
            }

            _text = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
            _text.normal.textColor = Color.white;

            _dim = new GUIStyle(_text);
            _dim.normal.textColor = new Color(1, 1, 1, 0.65f);

            _title = new GUIStyle(_text) { fontSize = Mathf.RoundToInt(size * 2.1f) };
            _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        }

        private static Texture2D Solid(Color colour)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, colour);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void OnDestroy()
        {
            if (_card != null) { Destroy(_card); }
            if (_scrim != null) { Destroy(_scrim); }
        }
    }
}
