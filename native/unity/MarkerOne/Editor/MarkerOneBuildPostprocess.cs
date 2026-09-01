using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

namespace MarkerOne.EditorTools
{
    /// <summary>
    /// Register the OAuth redirect scheme in the built Xcode project.
    ///
    /// Google returns the authorization code by opening a URL, and iOS only
    /// hands that URL to an app that has claimed the scheme in its Info.plist.
    /// Without the entry the browser opens, consent is given, and nothing comes
    /// back — a failure with no error anywhere, which is the kind this project
    /// has had enough of.
    ///
    /// Written on every build because Build and Run with Replace regenerates
    /// the Xcode project, and anything added by hand in Xcode is gone the next
    /// time.
    /// </summary>
    public static class MarkerOneBuildPostprocess
    {
        [PostProcessBuild(100)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
#if UNITY_IOS
            if (target != BuildTarget.iOS) { return; }

            string clientId = FindClientId();
            if (string.IsNullOrEmpty(clientId))
            {
                Debug.Log("MarkerOne: no Google OAuth client id in any scene — " +
                          "no URL scheme added. Google sign-in will not be able to " +
                          "return; the other three are unaffected.");
                AppleSignIn(path);
                return;
            }

            string scheme = Reversed(clientId);
            string plistPath = Path.Combine(path, "Info.plist");

            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);

            PlistElementArray types = plist.root.CreateArray("CFBundleURLTypes");
            PlistElementDict entry = types.AddDict();
            entry.SetString("CFBundleURLName", scheme);
            PlistElementArray schemes = entry.CreateArray("CFBundleURLSchemes");
            schemes.AddString(scheme);

            plist.WriteToFile(plistPath);
            Debug.Log("MarkerOne: registered URL scheme " + scheme);

            AppleSignIn(path);
#endif
        }

#if UNITY_IOS
        /// <summary>
        /// What Sign in with Apple needs from the Xcode project.
        ///
        /// A framework and an entitlement, neither of which Unity adds for a
        /// plugin it does not know the purpose of. Both are written on every
        /// build because Replace regenerates the project, and the failure when
        /// they are missing is a link error for the framework and, worse, a
        /// silent refusal at runtime for the entitlement.
        ///
        /// The App ID also needs the capability enabled in Apple's developer
        /// portal. That part cannot be done from here.
        /// </summary>
        private static void AppleSignIn(string path)
        {
            string projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string target = project.GetUnityMainTargetGuid();
            string framework = project.GetUnityFrameworkTargetGuid();

            project.AddFrameworkToProject(target, "AuthenticationServices.framework", true);
            project.AddFrameworkToProject(framework, "AuthenticationServices.framework", true);

            const string file = "MarkerOne.entitlements";
            var entitlements = new PlistDocument();

            string full = Path.Combine(path, file);
            if (File.Exists(full)) { entitlements.ReadFromFile(full); }
            else { entitlements.Create(); }

            PlistElementArray signin = entitlements.root.CreateArray("com.apple.developer.applesignin");
            signin.AddString("Default");
            entitlements.WriteToFile(full);

            project.AddFile(full, file);
            project.SetBuildProperty(target, "CODE_SIGN_ENTITLEMENTS", file);
            project.WriteToFile(projectPath);

            Debug.Log("MarkerOne: added AuthenticationServices and the Sign in with " +
                      "Apple entitlement. The App ID needs the capability enabled in " +
                      "Apple's developer portal too.");
        }
#endif

        /// <summary>
        /// Read the client id out of the scene rather than keeping a second
        /// copy of it.
        ///
        /// The component's field is the one a person edits, so it is the one
        /// worth trusting; a build setting beside it would only be a thing to
        /// forget to update. The scene is YAML on disk and the field name is
        /// unique to that component, so reading it back is a line of regex
        /// rather than a reason to load a scene during a build.
        /// </summary>
        /// <summary>The reversed client id, which is the scheme both platforms
        /// have to claim. Shared so Android and iOS cannot disagree about it.</summary>
        internal static string RedirectScheme()
        {
            string clientId = FindClientId();
            return string.IsNullOrEmpty(clientId) ? null : Reversed(clientId);
        }

        private static string FindClientId()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Scene"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) { continue; }

                Match found = Regex.Match(File.ReadAllText(path),
                                          @"^\s*ClientId:\s*(\S+\.apps\.googleusercontent\.com)\s*$",
                                          RegexOptions.Multiline);
                if (found.Success) { return found.Groups[1].Value; }
            }

            return null;
        }

        private static string Reversed(string clientId)
        {
            string[] parts = clientId.Split('.');
            System.Array.Reverse(parts);
            return string.Join(".", parts);
        }
    }
}
