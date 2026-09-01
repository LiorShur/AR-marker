using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Signing in, four ways.
    ///
    /// Every one of them ends at the same place — a Firebase uid — and what
    /// differs is only how much a person has to hand over to get there. The
    /// device identity is offered first and without apology: it needs nothing,
    /// it already works, and for somebody who wants to leave a marker and walk
    /// away it is the right answer. An account only earns its place when
    /// something has to survive a reinstall or move to another phone.
    ///
    /// Registering is a separate button from signing in. One combined button is
    /// friendlier right up to the moment somebody mistypes an address they have
    /// used before, at which point it quietly makes them a second empty account
    /// and everything they placed belongs to the first.
    /// </summary>
    public sealed class SignInScreen : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("MarkerOne Sign In");
            go.AddComponent<SignInScreen>();
            DontDestroyOnLoad(go);
        }
#endif

        public static bool Open;

        private MarkerOneRig _rig;
        private GoogleSignIn _google;
        private AppleSignIn _apple;
        private float _rescan;

        private string _email = "";
        private string _password = "";
        private string _said = "";
        private bool _busy;

        private GUIStyle _text;
        private GUIStyle _field;
        private GUIStyle _button;
        private Texture2D _panel;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan > 0) { return; }
            _rescan = 1f;

            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }
            if (_google == null) { _google = FindFirstObjectByType<GoogleSignIn>(); }

            if (_apple == null && AppleSignIn.Available)
            {
                _apple = FindFirstObjectByType<AppleSignIn>();

                // Made here rather than by the scene setup, because the native
                // callback finds it by object name and getting that name right
                // is not something to leave to whoever wires the scene.
                if (_apple == null)
                {
                    var go = new GameObject(AppleSignIn.ObjectName);
                    _apple = go.AddComponent<AppleSignIn>();
                    DontDestroyOnLoad(go);
                }

                _apple.Finished += OnFinished;
            }
        }

        private void OnDisable()
        {
            if (_apple != null) { _apple.Finished -= OnFinished; }
        }

        private void OnFinished(string account, string why)
        {
            _busy = false;

            if (account != null)
            {
                _said = "signed in as " + account;
                _password = "";
                Open = false;
                return;
            }

            _said = why == "cancelled" ? "" : why;
        }

        private void OnGUI()
        {
            if (!Open || _rig == null) { return; }

            EnsureStyles();

            Rect safe = Screen.safeArea;
            float line = _text.fontSize * 1.9f;
            float pad = _text.fontSize;

            float width = Mathf.Min(safe.width - pad * 2, _text.fontSize * 26);
            float height = line * (AppleSignIn.Available ? 12 : 11) + pad * 2;

            var panel = new Rect(safe.x + (safe.width - width) * 0.5f,
                                 Screen.height - (safe.y + safe.height) + line,
                                 width, height);

            GUI.DrawTexture(panel, _panel);

            var row = new Rect(panel.x + pad, panel.y + pad, panel.width - pad * 2, line);

            GUI.Label(row, string.IsNullOrEmpty(_rig.Signed)
                ? "Signed in as this device"
                : "Signed in as " + _rig.Signed, _text);

            row.y += line * 1.4f;
            GUI.Label(row, "Email", _text);
            row.y += line;
            _email = GUI.TextField(row, _email, 128, _field);

            row.y += line * 1.2f;
            GUI.Label(row, "Password", _text);
            row.y += line;
            _password = GUI.PasswordField(row, _password, '•', 128, _field);

            row.y += line * 1.3f;
            float third = (row.width - pad * 2) / 3;

            var cell = new Rect(row.x, row.y, third, line);
            if (Button(cell, "Sign in")) { SignIn(); }

            cell.x += third + pad;
            if (Button(cell, "Register")) { Register(); }

            cell.x += third + pad;
            if (Button(cell, "Forgot")) { Forgot(); }

            row.y += line * 1.3f;
            cell = new Rect(row.x, row.y, third, line);

            if (_google != null && Button(cell, "Google")) { Google(); }

            if (AppleSignIn.Available)
            {
                cell.x += third + pad;
                if (Button(cell, "Apple")) { Apple(); }
            }

            cell.x += third + pad;
            if (GUI.Button(cell, string.IsNullOrEmpty(_rig.Signed) ? "Close" : "Sign out",
                           _button))
            {
                if (!string.IsNullOrEmpty(_rig.Signed)) { _rig.SignOut(); }
                Open = false;
            }

            if (string.IsNullOrEmpty(_said)) { return; }

            row.y += line * 1.3f;
            GUI.Label(row, _said, _text);
        }

        /// <summary>Buttons are dead while a sign-in is in flight. Two
        /// overlapping attempts end with the second overwriting the first, and
        /// the person seeing an identity they did not choose.</summary>
        private bool Button(Rect at, string label)
        {
            if (_busy)
            {
                GUI.Label(at, label, _text);
                return false;
            }

            return GUI.Button(at, label, _button);
        }

        private async void SignIn()
        {
            _busy = true;
            _said = "signing in…";

            try { OnFinished(await _rig.SignInWithPasswordAsync(_email, _password), null); }
            catch (System.Exception e) { OnFinished(null, Plain(e.Message)); }
        }

        private async void Register()
        {
            _busy = true;
            _said = "making an account…";

            try { OnFinished(await _rig.RegisterAsync(_email, _password), null); }
            catch (System.Exception e) { OnFinished(null, Plain(e.Message)); }
        }

        private async void Forgot()
        {
            _busy = true;
            _said = "sending…";

            try
            {
                await _rig.ResetPasswordAsync(_email);
                _busy = false;
                _said = "check your email";
            }
            catch (System.Exception e) { OnFinished(null, Plain(e.Message)); }
        }

        private void Google()
        {
            _busy = true;
            _said = "opening the browser…";

            _google.Finished -= OnFinished;
            _google.Finished += OnFinished;
            _google.Begin();
        }

        private void Apple()
        {
            _busy = true;
            _said = "";
            _apple.Begin();
        }

        /// <summary>
        /// Firebase's identity errors are shouted constants — EMAIL_EXISTS,
        /// INVALID_LOGIN_CREDENTIALS — which are precise and unreadable. The
        /// ones people actually hit are worth saying in words.
        /// </summary>
        private static string Plain(string message)
        {
            if (message == null) { return "could not sign in"; }

            if (message.Contains("EMAIL_EXISTS")) { return "that email already has an account"; }
            if (message.Contains("EMAIL_NOT_FOUND")) { return "no account for that email"; }
            if (message.Contains("INVALID_PASSWORD") ||
                message.Contains("INVALID_LOGIN_CREDENTIALS")) { return "wrong email or password"; }
            if (message.Contains("WEAK_PASSWORD")) { return "password needs six characters"; }
            if (message.Contains("INVALID_EMAIL")) { return "that is not an email address"; }
            if (message.Contains("TOO_MANY_ATTEMPTS")) { return "too many tries — wait a while"; }
            if (message.Contains("OPERATION_NOT_ALLOWED"))
            {
                return "that sign-in method is off in the Firebase console";
            }

            return message;
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.019f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, new Color(0, 0, 0, 0.9f));
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
