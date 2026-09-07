using MarkerOne.Core;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// The survey, one requirement at a time.
    ///
    /// Deliberately a walk through a list rather than a free measuring tool.
    /// The failure mode of a surveyor with a tape is not inaccuracy, it is the
    /// check nobody thought to make, so the list is the product and the
    /// measuring is in service of it.
    ///
    /// It never says compliant. That is a legal conclusion about a whole
    /// building drawn by somebody qualified to draw it; what this can honestly
    /// say is that it measured 780 mm where the figure cited is 800 mm.
    /// </summary>
    public sealed class SurveyPanel : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("MarkerOne Survey");
            go.AddComponent<SurveyPanel>();
            DontDestroyOnLoad(go);
        }
#endif

        public static bool Open;

        public static Rect Occupied;

        private SurveyRig _survey;
        private float _rescan;
        private bool _list;
        private string _said = "";

        private GUIStyle _text;
        private GUIStyle _dim;
        private GUIStyle _button;
        private Texture2D _panel;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan > 0) { return; }
            _rescan = 1f;

            if (_survey == null)
            {
                _survey = FindFirstObjectByType<SurveyRig>();
                if (_survey == null)
                {
                    var go = new GameObject("MarkerOne Surveying");
                    _survey = go.AddComponent<SurveyRig>();
                    DontDestroyOnLoad(go);
                }
            }

            SurveyRig.Running = Open;
        }

        private void OnGUI()
        {
            if (!Open || _survey == null || SignInScreen.Blocking || ModeMenu.Blocking)
            {
                Occupied = new Rect();
                return;
            }

            GUI.depth = -400;
            EnsureStyles();

            Rect safe = Screen.safeArea;
            float line = _text.fontSize * 1.8f;
            float pad = _text.fontSize;

            float width = Mathf.Min(safe.width - pad * 2, _text.fontSize * 30);
            float height = _list ? line * 14 + pad * 2 : line * 11 + pad * 2;

            var panel = new Rect(safe.x + (safe.width - width) * 0.5f,
                                 Mathf.Max(MarkerOneHud.Occupied.yMax + pad,
                                           Screen.height - (safe.y + safe.height) + line),
                                 width, height);
            Occupied = panel;

            GUI.DrawTexture(panel, _panel);

            var row = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, line);

            if (_list) { List(ref row, line, pad); }
            else { One(ref row, line, pad); }

            var close = new Rect(panel.x + pad, panel.yMax - pad - line,
                                 _text.fontSize * 6, line);
            if (GUI.Button(close, "Close", _button)) { Open = false; }

            var swap = new Rect(close.xMax + pad, close.y, _text.fontSize * 7, line);
            if (GUI.Button(swap, _list ? "This one" : "All of them", _button)) { _list = !_list; }

            var reset = new Rect(swap.xMax + pad, close.y, _text.fontSize * 6, line);
            if (GUI.Button(reset, "Start over", _button))
            {
                _survey.Forget();
                _said = "";
            }
        }

        /// <summary>The requirement in front of you, and what the room says.</summary>
        private void One(ref Rect row, float line, float pad)
        {
            AccessCheck check = _survey.Current;
            if (check == null) { GUI.Label(row, "no checks loaded", _text); return; }

            GUI.Label(row, string.Format("{0} of {1} · {2}", _survey.At + 1,
                                         _survey.Checks.Count, check.Name), _text);

            row.y += line;
            GUI.Label(row, Access.Range(check) + "  ·  " + check.Cite, _dim);

            row.y += line * 1.3f;
            row.height = line * 2;
            GUI.Label(row, check.Aim, _text);

            row.y += line * 2.2f;
            row.height = line;

            Verdict found = _survey.Found(check.Id);
            GUI.Label(row, found == null
                ? (_survey.Half ? "one point down — put the other on the far side"
                                : Prompt(check))
                : (found.Meets ? "✓ " : "✗ ") + found.Says,
                _text);

            row.y += line * 1.2f;
            if (found != null && !found.Meets && !string.IsNullOrEmpty(check.Remedy))
            {
                row.height = line * 2;
                GUI.Label(row, check.Remedy, _dim);
                row.height = line;
            }

            row.y += line * 2f;
            float third = (row.width - pad * 2) / 3;
            var cell = new Rect(row.x, row.y, third, line);

            if (GUI.Button(cell, "Back", _button)) { _survey.Back(); }

            cell.x += third + pad;

            // The one check nothing can measure gets the two buttons that
            // answer it. Whether a circle is clear of a door swing and a bin is
            // a judgement, and pretending otherwise would be inventing a number.
            if (check.How == Measured.Fits)
            {
                if (GUI.Button(cell, "It fits", _button)) { _said = _survey.Says(true); }

                cell.x += third + pad;
                if (GUI.Button(cell, "It does not", _button)) { _said = _survey.Says(false); }
                return;
            }

            GUI.Label(cell, _said, _dim);

            cell.x += third + pad;
            if (GUI.Button(cell, "Next", _button)) { _survey.Next(); _said = ""; }
        }

        private static string Prompt(AccessCheck check)
        {
            switch (check.How)
            {
                case Measured.Height: return "aim at it and tap Measure";
                case Measured.Fits: return "walk round the circle on the floor";
                default: return "aim at one side and tap Measure";
            }
        }

        /// <summary>Everything, and what is left to do.</summary>
        private void List(ref Rect row, float line, float pad)
        {
            GUI.Label(row, string.Format("{0} of {1} measured · {2} short of the figure cited",
                                         _survey.Done, _survey.Checks.Count, _survey.Failing),
                      _text);

            row.y += line * 1.3f;

            foreach (AccessCheck check in _survey.Checks)
            {
                Verdict found = _survey.Found(check.Id);
                string mark = found == null ? "· " : found.Meets ? "✓ " : "✗ ";

                var at = new Rect(row.x, row.y, row.width, line);
                if (GUI.Button(at, mark + check.Name +
                                   (found == null ? "" : "  —  " + found.Says), _button))
                {
                    _survey.Go(IndexOf(check));
                    _list = false;
                }

                row.y += line * 1.05f;
            }
        }

        private int IndexOf(AccessCheck check)
        {
            for (int i = 0; i < _survey.Checks.Count; i++)
            {
                if (_survey.Checks[i] == check) { return i; }
            }
            return 0;
        }

        /// <summary>Called by the placement bar, which owns the crosshair.</summary>
        public void Measure(Vector3 where)
        {
            if (_survey != null) { _said = _survey.Tap(where); }
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.017f));
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

            _dim = new GUIStyle(_text);
            _dim.normal.textColor = new Color(1, 1, 1, 0.65f);

            _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        }

        private void OnDestroy()
        {
            if (_panel != null) { Destroy(_panel); }
        }
    }
}
