using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// The first screen, and afterwards a chip in the corner.
    ///
    /// Signing in is required. Everything placed carries the uid of whoever
    /// placed it, and that is what later decides who may edit or remove it, so
    /// an anonymous placement belongs to nobody and can afterwards be corrected
    /// by nobody. Asking once at launch is cheaper than that.
    ///
    /// Four ways in, all ending at the same Firebase uid. Registering is a
    /// separate button from signing in: one combined button is friendlier right
    /// up to the moment somebody mistypes an address they have used before, at
    /// which point it quietly makes them a second empty account and everything
    /// they placed belongs to the first.
    ///
    /// Deliberately IMGUI and self-installing, like the rest of the interface —
    /// no Canvas, no EventSystem, no font asset, nothing to wire.
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

        /// <summary>
        /// Whether the app is waiting for somebody to sign in.
        ///
        /// Read by everything else that draws, so the world is never offered
        /// underneath a screen that has to be answered first.
        /// </summary>
        public static bool Blocking { get; private set; }

        /// <summary>What the account chip is covering, so nothing else draws
        /// underneath it. Empty while the sign-in screen is up.</summary>
        public static Rect Occupied;

        private MarkerOneRig _rig;
        private GoogleSignIn _google;
        private AppleSignIn _apple;
        private float _rescan;

        private string _email = "";
        private string _password = "";
        private string _said = "";
        private bool _busy;
        private float _busySince;
        private bool _sent;

        private GUIStyle _text;
        private GUIStyle _dim;
        private GUIStyle _title;
        private GUIStyle _field;
        private GUIStyle _button;
        private Texture2D _card;
        private Texture2D _chip;
        private Texture2D _scrim;

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan > 0) { return; }
            _rescan = 1f;

            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }

            Blocking = _rig != null && string.IsNullOrEmpty(_rig.Signed);
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
                _said = "";
                _password = "";
                return;
            }

            _said = why == "cancelled" ? "" : why;
        }

        private void OnGUI()
        {
            if (_rig == null) { return; }

            // Lower depth draws on top. Without this it lands wherever Unity
            // happens to order the scripts, which put it behind the readout —
            // a screen underneath the thing it is meant to block is not a
            // screen, it is a decoration.
            GUI.depth = -1000;

            EnsureStyles();

            if (!string.IsNullOrEmpty(_rig.Signed))
            {
                Chip();
                return;
            }

            Occupied = new Rect();

            // A sign-in that never comes back leaves every button dead and
            // killing the app as the only way out: the browser flow can be
            // abandoned, and a native sheet can be dismissed without calling
            // back at all. Waiting is legitimate; waiting for ever is not.
            if (_busy && Time.unscaledTime > _busySince + 30f)
            {
                _busy = false;
                if (string.IsNullOrEmpty(_said)) { _said = "that did not come back — try again"; }
            }

            Launch();
        }

        /// <summary>
        /// The launch screen: everything behind it dimmed rather than hidden,
        /// so it reads as the app waiting rather than the app not having
        /// started.
        /// </summary>
        private void Launch()
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _scrim);

            Rect safe = Screen.safeArea;
            float size = _text.fontSize;
            float line = size * 1.9f;
            float pad = size * 1.4f;
            bool providers = _google != null || AppleSignIn.Available;

            // Counted off the same increments the rows below step through, and
            // including the space for a message whether or not there is one:
            // a card that grows when something goes wrong moves every button
            // out from under the finger already on its way to one.
            float height = pad * 2 + line * (providers ? 14.45f : 13.05f);

            float width = Mathf.Min(safe.width - pad * 2, size * 26);
            float top = Screen.height - (safe.y + safe.height);

            var card = new Rect(safe.x + (safe.width - width) * 0.5f,
                                top + Mathf.Max(line, (safe.height - height) * 0.4f),
                                width, height);

            GUI.DrawTexture(card, _card);

            var row = new Rect(card.x + pad, card.y + pad, card.width - pad * 2, line * 1.6f);
            GUI.Label(row, "MarkerOne", _title);

            row.y += line * 1.7f;
            row.height = line * 2.2f;
            GUI.Label(row, "Sign in to place things. What you place is yours to move "
                         + "and remove, from any phone you sign in on.", _dim);


            row.y += line * 2.3f;
            row.height = line;

            GUI.Label(row, "Email", _dim);
            row.y += line * 0.95f;
            _email = GUI.TextField(row, _email, 128, _field);

            row.y += line * 1.35f;
            GUI.Label(row, "Password", _dim);
            row.y += line * 0.95f;
            _password = GUI.PasswordField(row, _password, '•', 128, _field);

            row.y += line * 1.5f;
            float third = (row.width - pad * 0.5f * 2) / 3;

            var cell = new Rect(row.x, row.y, third, line);
            if (Button(cell, "Sign in")) { SignIn(); }

            cell.x += third + pad * 0.5f;
            if (Button(cell, "Register")) { Register(); }

            cell.x += third + pad * 0.5f;
            if (Button(cell, "Forgot")) { Forgot(); }

            if (providers)
            {
                row.y += line * 1.4f;

                float half = (row.width - pad * 0.5f) / 2;
                bool both = _google != null && AppleSignIn.Available;
                cell = new Rect(row.x, row.y, both ? half : row.width, line);

                if (_google != null && Button(cell, "Continue with Google")) { Google(); }

                if (AppleSignIn.Available)
                {
                    if (both) { cell.x += half + pad * 0.5f; }
                    if (Button(cell, "Continue with Apple")) { Apple(); }
                }
            }

            if (string.IsNullOrEmpty(_said)) { return; }

            // Three lines rather than one. Sign-in failures are the messages
            // most worth reading and the longest, and a message cut off at the
            // edge of a card is a message that was not shown.
            row.y += line * 1.5f;
            GUI.Label(new Rect(row.x, row.y, row.width, line * 2.8f), _said, _text);
        }

        /// <summary>
        /// Afterwards: who you are and how to stop being them, small, tucked
        /// under whatever the readout is currently occupying so the two never
        /// land on top of each other.
        /// </summary>
        private void Chip()
        {
            Rect safe = Screen.safeArea;
            float size = _text.fontSize;
            float line = size * 1.6f;
            float pad = size * 0.6f;

            // One name, whichever of the three ways in somebody took. Apple
            // returns an email once and never again, Google returns one every
            // time, and a password account is one — so the rig settles it in a
            // single place and this shows whatever it settled on.
            string name = _rig.Named;
            if (string.IsNullOrEmpty(name)) { name = "Signed in"; }

            // Only while it is worth doing something about: an account whose
            // address is unproven is one an admin rule matching on the email
            // will never fire for, and the whole remedy is one tap that nothing
            // else in the app offers.
            bool prove = !string.IsNullOrEmpty(_rig.Email) && !_rig.EmailVerified;

            float label = _text.CalcSize(new GUIContent(name)).x;
            float button = size * 5f;
            float width = Mathf.Min(safe.width - 16,
                                    pad * 3 + label + button + (prove ? button + pad : 0));

            // The bottom left corner, which is the one nothing else wants: the
            // readout owns the top, the control bar owns the width of the
            // bottom, and a chip that moves around as those come and go is a
            // chip nobody learns the position of.
            float floor = PlacementInput.Occupied.height > 0
                ? PlacementInput.Occupied.yMin - 8f
                : Screen.height - safe.y - 8f;

            var chip = new Rect(safe.x + 8f, floor - (line + pad), width, line + pad);
            Occupied = chip;

            GUI.DrawTexture(chip, _chip);

            float taken = button + pad * 3 + (prove ? button + pad : 0);
            GUI.Label(new Rect(chip.x + pad, chip.y + pad * 0.5f,
                               width - taken, line), name, _text);

            var stop = new Rect(chip.xMax - pad - button, chip.y + pad * 0.5f, button, line);

            if (prove)
            {
                var verify = new Rect(stop.x - pad - button, stop.y, button, line);
                if (GUI.Button(verify, _sent ? "Sent" : "Verify", _button)) { Prove(); }
            }

            if (!GUI.Button(stop, "Sign out", _button)) { return; }

            // Which puts the launch screen back up on the next frame, because
            // there is nothing to use the app as any more.
            _rig.SignOut();
            _said = "";
            _password = "";
            _busy = false;
            _sent = false;
        }

        /// <summary>
        /// Ask Firebase for the confirmation link.
        ///
        /// The token does not become verified until the link is followed and
        /// the app signs in again, so nothing visible changes here — which is
        /// why the button says so rather than appearing to have done nothing.
        /// </summary>
        private async void Prove()
        {
            _sent = true;

            try { await _rig.VerifyEmailAsync(); }
            catch (System.Exception e)
            {
                _sent = false;
                Debug.LogWarning("MarkerOne: could not send the verification — " + e.Message);
            }
        }

        /// <summary>Buttons are dead while a sign-in is in flight. Two
        /// overlapping attempts end with the second overwriting the first, and
        /// the person seeing an identity they did not choose.</summary>
        private bool Button(Rect at, string label)
        {
            if (_busy)
            {
                GUI.Label(at, label, _dim);
                return false;
            }

            return GUI.Button(at, label, _button);
        }

        private async void SignIn()
        {
            Working();
            _said = "signing in…";

            try { OnFinished(await _rig.SignInWithPasswordAsync(_email, _password), null); }
            catch (System.Exception e) { OnFinished(null, Plain(e.Message)); }
        }

        private async void Register()
        {
            Working();
            _said = "making an account…";

            try { OnFinished(await _rig.RegisterAsync(_email, _password), null); }
            catch (System.Exception e) { OnFinished(null, Plain(e.Message)); }
        }

        private async void Forgot()
        {
            Working();
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
            Working();
            _said = "opening the browser…";

            _google.Finished -= OnFinished;
            _google.Finished += OnFinished;
            _google.Begin();
        }

        private void Apple()
        {
            Working();
            _said = "";
            _apple.Begin();
        }

        private void Working()
        {
            _busy = true;
            _busySince = Time.unscaledTime;
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
            if (message.Contains("MISSING_PASSWORD")) { return "no password"; }
            if (message.Contains("INVALID_PASSWORD") ||
                message.Contains("INVALID_LOGIN_CREDENTIALS")) { return "wrong email or password"; }
            if (message.Contains("WEAK_PASSWORD")) { return "password needs six characters"; }
            if (message.Contains("INVALID_EMAIL")) { return "that is not an email address"; }
            if (message.Contains("TOO_MANY_ATTEMPTS")) { return "too many tries — wait a while"; }
            if (message.Contains("OPERATION_NOT_ALLOWED"))
            {
                return "that sign-in method is off in the Firebase console";
            }

            // The one that means something specific and says nothing. Apple's
            // token names this app's bundle id as its audience, and Firebase
            // only accepts an audience belonging to an app it knows about.
            if (message.Contains("INVALID_IDP_RESPONSE"))
            {
                return "Firebase refused the Apple token. It usually means no iOS " +
                       "app with this bundle id is registered in the Firebase project.";
            }

            return message;
        }

        private void EnsureStyles()
        {
            // The same sizing as the readout, so the two look like parts of one
            // app rather than two.
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.018f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_card == null)
            {
                _card = Solid(new Color(1, 1, 1, 0.10f));

                // Against the camera rather than against the ground, so it
                // needs its own darkness — the readout's, so the two match.
                _chip = Solid(new Color(0, 0, 0, 0.72f));

                // Opaque, not dimmed. The camera does carry on underneath and
                // is warming up the whole time, but a live view with the
                // readout's numbers legible through the card is two screens at
                // once and neither reads as the one being asked about.
                _scrim = Solid(new Color(0.06f, 0.07f, 0.09f, 1f));
            }

            _text = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
            _text.normal.textColor = Color.white;

            _dim = new GUIStyle(_text);
            _dim.normal.textColor = new Color(1, 1, 1, 0.65f);

            _title = new GUIStyle(_text) { fontSize = Mathf.RoundToInt(size * 2.1f) };

            _field = new GUIStyle(GUI.skin.textField) { fontSize = size };
            _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void OnDestroy()
        {
            if (_card != null) { Destroy(_card); }
            if (_chip != null) { Destroy(_chip); }
            if (_scrim != null) { Destroy(_scrim); }
        }
    }
}
