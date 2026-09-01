using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Sign in with Apple.
    ///
    /// Native rather than through a browser, unlike Google here. Apple's web
    /// flow requires a client secret — a JWT signed with a key from the
    /// developer portal — and nothing shipped inside an app is secret, so
    /// ASAuthorization is the only correct route on iOS.
    ///
    /// It is also not optional. Apple require Sign in with Apple from any app
    /// that offers another third-party sign-in, so offering Google on the App
    /// Store obliges this.
    ///
    /// The GameObject's name matters: the native side calls back by name
    /// through UnitySendMessage, which is the only channel it has.
    /// </summary>
    public sealed class AppleSignIn : MonoBehaviour
    {
        public const string ObjectName = "MarkerOne Apple";

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void MarkerOneAppleSignIn(string hashedNonce);
#endif

        /// <summary>Raised with the account, or with null and a reason.</summary>
        public event Action<string, string> Finished;

        public bool Busy { get; private set; }

        /// <summary>Whether this build can offer it at all.</summary>
        public static bool Available =>
            Application.platform == RuntimePlatform.IPhonePlayer;

        private string _nonce;
        private MarkerOneRig _rig;

        private void Awake()
        {
            // Renaming this breaks the native callback silently, so it is set
            // here rather than trusted to whoever made the object.
            gameObject.name = ObjectName;
            _rig = FindFirstObjectByType<MarkerOneRig>();
        }

        public void Begin()
        {
            if (Busy) { return; }

            if (!Available)
            {
                Fail("Sign in with Apple is only available on iOS");
                return;
            }

            Busy = true;

            // Kept here and sent to Firebase; Apple only ever sees the hash.
            // Firebase compares the two, which is what stops a token captured
            // from one sign-in being replayed into another.
            _nonce = Random(32);

#if UNITY_IOS && !UNITY_EDITOR
            MarkerOneAppleSignIn(Sha256(_nonce));
#else
            Fail("not an iOS build");
#endif
        }

        /// <summary>Called by name from the native side. Do not rename.</summary>
        public void OnAppleToken(string identityToken)
        {
            Exchange(identityToken);
        }

        /// <summary>Called by name from the native side. Do not rename.</summary>
        public void OnAppleFailed(string why)
        {
            Fail(why);
        }

        private async void Exchange(string identityToken)
        {
            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }

            if (_rig == null)
            {
                Fail("no rig to sign in");
                return;
            }

            try
            {
                string account = await _rig.SignInWithAppleAsync(identityToken, _nonce);

                Busy = false;
                Debug.Log("MarkerOne: signed in with Apple as " + account);
                Finished?.Invoke(account, null);
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        private void Fail(string why)
        {
            Busy = false;
            _nonce = null;

            if (why != "cancelled")
            {
                Debug.LogWarning("MarkerOne: Apple sign-in failed — " + why);
            }

            Finished?.Invoke(null, why);
        }

        /// <summary>Base64url of random bytes: what Apple's documentation calls
        /// a nonce, and what Firebase expects to receive unhashed.</summary>
        private static string Random(int bytes)
        {
            var raw = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(raw); }

            return Convert.ToBase64String(raw)
                          .TrimEnd('=')
                          .Replace('+', '-')
                          .Replace('/', '_');
        }

        /// <summary>Hex rather than base64. Apple specify the SHA-256 as a
        /// hexadecimal string, and sending the wrong encoding fails at Firebase
        /// with a mismatch that says nothing about which end was wrong.</summary>
        private static string Sha256(string value)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));

            var hex = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) { hex.Append(b.ToString("x2")); }
            return hex.ToString();
        }
    }
}
