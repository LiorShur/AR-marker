using System.Collections.Generic;
using System.Text;
using MarkerOne.Core;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity
{
    /// <summary>
    /// What the Xcode console would tell you, on the phone instead.
    ///
    /// Geospatial only does its real work outdoors, and tethering a laptop to
    /// a phone to read four lines of state makes every test a two-person job
    /// in a car park. This puts the same four lines on the screen.
    ///
    /// Deliberately IMGUI. A Canvas needs a scene object, an EventSystem and a
    /// font asset, and the entire point is that this needs nothing: it builds
    /// itself after the scene loads, so there is nothing to wire and nothing
    /// to forget to wire. Immediate-mode GUI is the wrong tool for a real
    /// interface and exactly the right one for a diagnostic that must work
    /// before any interface exists.
    ///
    /// Add MARKERONE_NO_HUD to the scripting define symbols to leave it out.
    /// </summary>
    public sealed class MarkerOneHud : MonoBehaviour
    {
        /// <summary>Toggled by the on-screen button; static so a real UI can
        /// hide the diagnostic without holding a reference to it.</summary>
        public static bool Visible = true;

        /// <summary>What this is covering, so nothing else draws underneath it.
        /// Empty while hidden.</summary>
        public static Rect Occupied;

#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            Visible = true;
            var go = new GameObject("MarkerOne HUD");
            go.AddComponent<MarkerOneHud>();
            DontDestroyOnLoad(go);
        }
#endif

        private const int LogLines = 6;

        private readonly Queue<string> _log = new Queue<string>();
        private readonly StringBuilder _text = new StringBuilder();

        private MarkerOneRig _rig;
        private GeospatialFixSource _source;
        private AROcclusionManager _occlusion;
        private WorldSession _watched;
        private int _items;
        private float _rescan;

        private GUIStyle _style;
        private GUIStyle _button;
        private Texture2D _panel;

        private void OnEnable()
        {
            Application.logMessageReceived += OnLog;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
        }

        private void OnDestroy()
        {
            if (_watched != null) { _watched.PlacementsChanged -= OnPlacements; }
            if (_panel != null) { Destroy(_panel); }
        }

        /// <summary>Warnings and errors regardless of source — a stack trace
        /// from somewhere else is still the reason nothing is happening.</summary>
        private void OnLog(string message, string stack, LogType type)
        {
            bool worth = type == LogType.Error
                      || type == LogType.Exception
                      || type == LogType.Warning
                      || message.StartsWith("MarkerOne");
            if (!worth) { return; }

            if (message.Length > 140) { message = message.Substring(0, 140) + "…"; }
            _log.Enqueue(message);
            while (_log.Count > LogLines) { _log.Dequeue(); }
        }

        private void Update()
        {
            // The rig and the fix source may not exist yet, and after a scene
            // change they may be different objects. Re-look once a second
            // rather than every frame; neither find is cheap.
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan <= 0 && (_rig == null || _source == null))
            {
                _rescan = 1f;
                if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }
                if (_source == null) { _source = FindFirstObjectByType<GeospatialFixSource>(); }
            }

            WorldSession session = _rig != null ? _rig.Session : null;
            if (!ReferenceEquals(session, _watched))
            {
                if (_watched != null) { _watched.PlacementsChanged -= OnPlacements; }
                _watched = session;
                if (_watched != null) { _watched.PlacementsChanged += OnPlacements; }
                _items = 0;
            }
        }

        private void OnPlacements(IReadOnlyList<PlacedItem> items)
        {
            _items = items.Count;
        }

        private void OnGUI()
        {
            // Nothing behind the sign-in screen, which is opaque: drawing
            // underneath it costs a layout pass to produce something nobody can
            // see, and gets the buttons pressed by taps meant for it.
            if (SignInScreen.Blocking)
            {
                Occupied = new Rect();
                return;
            }

            GUI.depth = 0;

            EnsureStyles();

            // Screen.safeArea is measured from the bottom left; IMGUI draws
            // from the top left. Without this the first line hides under the
            // notch on every phone that has one.
            Rect safe = Screen.safeArea;
            float left = safe.x + 8;
            float top = Screen.height - (safe.y + safe.height) + 8;
            float lineHeight = _style.fontSize * 1.35f;

            if (!Visible)
            {
                var collapsed = new Rect(left, top, lineHeight * 4, lineHeight * 1.6f);
                Occupied = collapsed;

                if (GUI.Button(collapsed, "▸ state", _button)) { Visible = true; }
                return;
            }

            string body;
            try
            {
                body = Body();
            }
            catch (System.Exception e)
            {
                // A diagnostic that cannot survive the thing it is diagnosing
                // is worse than no diagnostic, because it looks like nothing
                // is wrong.
                body = "HUD error: " + e.Message;
            }

            float width = Mathf.Min(safe.width - 16, _style.fontSize * 30);

            int lines = 1;
            for (int i = 0; i < body.Length; i++) { if (body[i] == '\n') { lines++; } }

            float height = Mathf.Max(_style.CalcHeight(new GUIContent(body), width - 16),
                                     lines * lineHeight) + 16;

            var box = new Rect(left, top, width, height);

            // Plus the row of buttons that sits under it.
            Occupied = new Rect(box.x, box.y, box.width, box.height + lineHeight * 1.5f + 6);

            GUI.DrawTexture(box, _panel);
            GUI.Label(new Rect(box.x + 8, box.y + 8, box.width - 16, box.height - 16), body, _style);

            if (GUI.Button(new Rect(box.xMax - lineHeight * 2.2f, box.y, lineHeight * 2.2f,
                                    lineHeight * 1.4f), "×", _button))
            {
                Visible = false;
            }

            // Dropping a pin is not placing: it writes a coordinate somebody
            // read off a map, and needs no localization at all. So it lives
            // here rather than in the placement bar, which is about the thing
            // in front of you.
            var pin = new Rect(box.x, box.yMax + 6, lineHeight * 4, lineHeight * 1.5f);
            if (GUI.Button(pin, MapPin.Open ? "Hide pin" : "Drop pin", _button))
            {
                MapPin.Open = !MapPin.Open;
            }

            // Occlusion is a judgement rather than a setting: it makes a
            // placement behind a wall correctly invisible, and it also eats the
            // edges of one standing in front of a surface. Which of those
            // matters more depends on where you are, so it belongs on a button
            // rather than in a file.
            var occlusion = new Rect(pin.xMax + lineHeight * 0.4f, box.yMax + 6,
                                     lineHeight * 4.4f, lineHeight * 1.5f);
            Occlusion(occlusion);

            // Indoors is a different mode rather than a different app: the same
            // content, the same bar, a frame pinned by paper instead of by the
            // Earth.
            var venue = new Rect(occlusion.xMax + lineHeight * 0.4f, box.yMax + 6,
                                 lineHeight * 4, lineHeight * 1.5f);
            if (GUI.Button(venue, "Venue", _button)) { VenuePanel.Open = !VenuePanel.Open; }
        }

        private string Body()
        {
            _text.Length = 0;

            _text.Append("Build  ").Append(Build.Stamp).Append('\n');
            _text.Append("AR     ").Append(ARSession.state).Append('\n');

            // What the phone thinks it has. Everything here needs the network —
            // VPS resolution, the coverage check, every read and write — and
            // "nothing is happening" looks identical whether the cause is the
            // sky, the server or a switch in Settings.
            _text.Append("Net    ").Append(Reachability()).Append('\n');

            if (_source == null)
            {
                _text.Append("Earth  no GeospatialFixSource in scene\n");
            }
            else
            {
                _text.Append("Earth  ")
                     .Append(string.IsNullOrEmpty(_source.Failed) ? _source.Status : _source.Failed)
                     .Append('\n');
                _text.Append("VPS    ").Append(_source.Vps).Append('\n');
            }

            if (_rig == null)
            {
                _text.Append("Rig    no MarkerOneRig in scene\n");
                return Append(_text).ToString();
            }

            _text.Append("Rig    ").Append(_rig.State).Append('\n');

            WorldSession session = _rig.Session;
            if (session == null)
            {
                _text.Append("       no session — check Project Id and Api Key\n");
                return Append(_text).ToString();
            }

            _text.Append("Fixes  ").Append(session.Fixes).Append('\n');

            if (session.Frame != null && session.Frame.Fix != null)
            {
                _text.Append("Fix    ").Append(session.Frame.Fix).Append('\n');
            }

            if (!string.IsNullOrEmpty(_rig.Uid))
            {
                // The claim, and whether it is proven. An admin rule matches
                // on the verified email, so "listed but unverified" is the
                // difference between a button that works and one that is
                // refused every time — and it is invisible anywhere else.
                string account = " (device)";
                if (!string.IsNullOrEmpty(_rig.Signed))
                {
                    account = string.IsNullOrEmpty(_rig.Email)
                        ? " (account, no email in the token)"
                        : " (" + _rig.Email + (_rig.EmailVerified ? " ✓)" : " UNVERIFIED)");
                }

                _text.Append("Uid    ").Append(_rig.Uid)
                     .Append(account)
                     .Append('\n');
            }

            // Anything beyond this is clipped rather than missing, and the
            // two are indistinguishable from the outside.
            Camera eye = _rig.SessionCamera;
            if (eye != null && _rig.NearestM > eye.farClipPlane)
            {
                _text.Append("Clip   nearest is past the ")
                     .Append(eye.farClipPlane.ToString("0"))
                     .Append("m far plane\n");
            }

            int waiting = _rig.Waiting;
            _text.Append("Items  ").Append(_items)
                 .Append(" found, ").Append(_rig.Rendered - waiting).Append(" shown");
            if (waiting > 0)
            {
                _text.Append(", ").Append(waiting).Append(" waiting for arcore");
            }
            if (_rig.Approximate > 0)
            {
                _text.Append(", ").Append(_rig.Approximate).Append(" from gps");
            }
            if (_rig.NearestM >= 0)
            {
                _text.Append(", nearest ").Append(_rig.NearestM.ToString("0.#")).Append('m');
            }
            _text.Append('\n');

            if (_source != null && _source.SessionTiltDeg > 5f)
            {
                _text.Append("Tilt   ").Append(_source.SessionTiltDeg.ToString("0.#"))
                     .Append("° — heading from baseline, walk ~20m\n");
            }

            if (_source != null && _source.FrameErrorM >= 0)
            {
                _text.Append("Frame  off by ")
                     .Append(_source.FrameErrorM.ToString("0.#"))
                     .Append("m vs arcore\n");
            }

            if (_source != null && _rig.Anchors != null)
            {
                double good = _rig.Anchors.AccuracyM;
                if (good < 0 && !_rig.Anchors.GaveUp)
                {
                    _text.Append("Anchor waiting for a better fix than ±")
                         .Append(_rig.Anchors.AnchorWhenBetterThanM.ToString("0.#"))
                         .Append("m\n");
                }

                _text.Append("Anchor ").Append(_rig.Anchors.Count).Append('/').Append(_rig.Rendered)
                     .Append(_rig.Anchors.GaveUp ? " (gave up — see log)"
                             : _rig.Anchors.Ready ? " (arcore)" : " (earth not tracking)")
                     .Append('\n');
            }

            if (_rig.HasNearest)
            {
                Vector3 d = _rig.NearestOffset;
                // Session axes, not camera-relative: x and z depend on which
                // way the session was facing when it started, but y is up
                // whatever happens, and y is the one that answers this.
                _text.AppendFormat("Near   x {0:0.0}  y {1:0.0} (up)  z {2:0.0}\n",
                                   d.x, d.y, d.z);
            }

            // Placements hang off this. If it is not at the origin, everything
            // is offset by however far it has wandered.
            if (_rig.PlacementRoot != null &&
                _rig.PlacementRoot.position.sqrMagnitude > 0.01f)
            {
                Vector3 r = _rig.PlacementRoot.position;
                _text.AppendFormat("Root   {0:0.0} {1:0.0} {2:0.0}  (should be 0 0 0)\n",
                                   r.x, r.y, r.z);
            }

            if (!string.IsNullOrEmpty(session.LastError))
            {
                _text.Append("Error  ").Append(session.LastError).Append('\n');
            }

            return Append(_text).ToString();
        }

        private void Occlusion(Rect at)
        {
            if (_occlusion == null) { _occlusion = FindFirstObjectByType<AROcclusionManager>(); }
            if (_occlusion == null) { return; }

            bool on = _occlusion.requestedOcclusionPreferenceMode !=
                      OcclusionPreferenceMode.NoOcclusion;

            if (!GUI.Button(at, on ? "Depth on" : "Depth off", _button)) { return; }

            _occlusion.requestedOcclusionPreferenceMode = on
                ? OcclusionPreferenceMode.NoOcclusion
                : OcclusionPreferenceMode.PreferEnvironmentOcclusion;
        }

        private static string Reachability()
        {
            switch (Application.internetReachability)
            {
                case NetworkReachability.ReachableViaLocalAreaNetwork:
                    return "wifi";
                case NetworkReachability.ReachableViaCarrierDataNetwork:
                    return "cellular";
                default:
                    // On iOS this is also what a per-app cellular switch turned
                    // off looks like, which is not obviously a network problem
                    // from inside the app.
                    return "none — check Settings → Cellular → MarkerOne";
            }
        }

        private StringBuilder Append(StringBuilder sb)
        {
            if (_log.Count == 0) { return sb; }

            sb.Append('\n');
            foreach (string line in _log)
            {
                sb.Append(line).Append('\n');
            }
            return sb;
        }

        private void EnsureStyles()
        {
            // Sized off the screen rather than a fixed point size: the same
            // number is unreadable on a phone and enormous in the editor.
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.018f));
            if (_style != null && _style.fontSize == size) { return; }

            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0, 0, 0, 0.72f));
                _panel.Apply();
                _panel.hideFlags = HideFlags.HideAndDontSave;
            }

            // Deliberately the built-in skin font rather than an OS font.
            // Font.CreateDynamicFontFromOSFont returns null where the platform
            // has no dynamic font support, and on iOS can return a font that
            // renders no glyphs at all — which is the worse failure, because
            // the panel then draws at full size containing nothing. A
            // monospaced column was not worth that.
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                richText = false,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            _style.normal.textColor = Color.white;

            _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        }
    }
}
