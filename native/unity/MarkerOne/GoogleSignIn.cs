using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MarkerOne.Core;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Signing in with Google, without the Firebase SDK.
    ///
    /// The device identity works — the refresh token is kept and the uid
    /// survives a launch — but it is still a device. Reinstall the app, or pick
    /// up a different phone, and everything you placed belongs to somebody
    /// else. An account fixes that and nothing else does.
    ///
    /// Doing it without the Firebase Unity SDK means running the OAuth flow
    /// directly: open Google's consent page in the system browser, take the
    /// code back through a custom URL scheme, exchange it for an identity
    /// token, and hand that to Firebase. PKCE throughout, because a public
    /// client has no secret to prove itself with and the code alone is
    /// interceptable by anything else that claims the same scheme.
    ///
    /// The alternative was the Firebase Unity SDK, which brings its own
    /// Firestore, its own auth, and its own opinions about a REST client this
    /// project has already tested twice over.
    /// </summary>
    public sealed class GoogleSignIn : MonoBehaviour
    {
        [Tooltip("The iOS OAuth client id from Google Cloud → Credentials. "
               + "Looks like 123-abc.apps.googleusercontent.com.")]
        public string ClientId = "";

        /// <summary>Raised with the account, or with null and a reason.</summary>
        public event Action<string, string> Finished;

        public bool Busy { get; private set; }

        private string _verifier;
        private string _state;
        private MarkerOneRig _rig;

        /// <summary>The URL scheme Google redirects back through: the client id
        /// with its parts reversed, which is the convention for installed
        /// applications and what the Info.plist entry has to match.</summary>
        public string Scheme
        {
            get
            {
                if (string.IsNullOrEmpty(ClientId)) { return ""; }

                string[] parts = ClientId.Split('.');
                Array.Reverse(parts);
                return string.Join(".", parts);
            }
        }

        private void Awake()
        {
            _rig = FindFirstObjectByType<MarkerOneRig>();
            Application.deepLinkActivated += OnRedirect;

            // A cold start: iOS may have launched the app with the redirect
            // rather than delivering it to a running one.
            if (!string.IsNullOrEmpty(Application.absoluteURL))
            {
                OnRedirect(Application.absoluteURL);
            }
        }

        private void OnDestroy()
        {
            Application.deepLinkActivated -= OnRedirect;
        }

        public void Begin()
        {
            if (Busy) { return; }

            if (string.IsNullOrEmpty(ClientId))
            {
                Fail("no OAuth client id set on the GoogleSignIn component");
                return;
            }

            Busy = true;
            _verifier = Random(64);
            _state = Random(16);

            string url = "https://accounts.google.com/o/oauth2/v2/auth"
                       + "?client_id=" + Uri.EscapeDataString(ClientId)
                       + "&redirect_uri=" + Uri.EscapeDataString(Scheme + ":/oauth2redirect")
                       + "&response_type=code"
                       + "&scope=" + Uri.EscapeDataString("openid email profile")
                       + "&code_challenge=" + Challenge(_verifier)
                       + "&code_challenge_method=S256"
                       + "&state=" + _state;

            Application.OpenURL(url);
        }

        private void OnRedirect(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(Scheme, StringComparison.Ordinal))
            {
                return;
            }

            Dictionary<string, string> parts = Query(url);

            if (parts.TryGetValue("error", out string error))
            {
                Fail(error);
                return;
            }

            // The state is the only thing standing between this and accepting a
            // code from whoever else can open a URL.
            if (!parts.TryGetValue("state", out string state) || state != _state)
            {
                Fail("the redirect did not match the request");
                return;
            }

            if (!parts.TryGetValue("code", out string code))
            {
                Fail("the redirect carried no code");
                return;
            }

            StartCoroutine(Exchange(code));
        }

        private IEnumerator Exchange(string code)
        {
            Task<string> work = ExchangeAsync(code);
            while (!work.IsCompleted) { yield return null; }

            Busy = false;

            if (work.Exception != null)
            {
                Fail(work.Exception.GetBaseException().Message);
                yield break;
            }

            Debug.Log("MarkerOne: signed in as " + work.Result);
            Finished?.Invoke(work.Result, null);
        }

        private async Task<string> ExchangeAsync(string code)
        {
            // Form-encoded, which is what this endpoint takes, and the reason
            // it does not go through the store's JSON sender.
            using var http = new HttpClient();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "code", code },
                { "client_id", ClientId },
                { "redirect_uri", Scheme + ":/oauth2redirect" },
                { "grant_type", "authorization_code" },
                { "code_verifier", _verifier }
            });

            HttpResponseMessage response =
                await http.PostAsync("https://oauth2.googleapis.com/token", form);
            string text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("Google refused the code: " + text);
            }

            string idToken = Json.Parse(text)["id_token"].AsString;
            if (string.IsNullOrEmpty(idToken))
            {
                throw new Exception("Google returned no id_token");
            }

            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }
            if (_rig == null) { throw new Exception("no rig to sign in"); }

            return await _rig.SignInWithGoogleAsync(idToken);
        }

        private void Fail(string why)
        {
            Busy = false;
            Debug.LogWarning("MarkerOne: sign-in failed — " + why);
            Finished?.Invoke(null, why);
        }

        private static Dictionary<string, string> Query(string url)
        {
            var found = new Dictionary<string, string>();

            int mark = url.IndexOf('?');
            if (mark < 0) { return found; }

            foreach (string pair in url.Substring(mark + 1).Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0) { continue; }

                found[Uri.UnescapeDataString(pair.Substring(0, equals))] =
                    Uri.UnescapeDataString(pair.Substring(equals + 1));
            }

            return found;
        }

        private static string Random(int bytes)
        {
            var raw = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(raw); }
            return Base64Url(raw);
        }

        private static string Challenge(string verifier)
        {
            using var sha = SHA256.Create();
            return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        /// <summary>Base64 as the OAuth specification wants it: no padding, and
        /// the two characters that mean something in a URL replaced.</summary>
        private static string Base64Url(byte[] raw)
        {
            return Convert.ToBase64String(raw)
                          .TrimEnd('=')
                          .Replace('+', '-')
                          .Replace('/', '_');
        }
    }
}
