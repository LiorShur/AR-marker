#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace MarkerOne.EditorTools
{
    /// <summary>
    /// The Android manifest entries that cannot be set from the inspector.
    ///
    /// Location is the one that matters. ARCore's Geospatial API needs it, and
    /// on iOS the app simply asks; on Android an unrequested permission is a
    /// permission that does not exist, and Unity only adds the entry when it
    /// notices the API being used — which it does not always do through an
    /// assembly definition. The failure is the one this project already spent a
    /// day on: Earth enabled, never tracking, nothing saying why.
    ///
    /// The other is the OAuth redirect. Google returns the sign-in code by
    /// opening a URL, and Android only delivers it to an activity that has
    /// claimed the scheme. Without the filter the browser opens, consent is
    /// given, and nothing comes back — silently, exactly as on iOS.
    /// </summary>
    public sealed class MarkerOneAndroidPostprocess : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;

        private const string Android = "http://schemas.android.com/apk/res/android";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifest))
            {
                Debug.LogWarning("MarkerOne: no AndroidManifest at " + manifest);
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifest);

            XmlElement root = doc.DocumentElement;
            if (root == null) { return; }

            Permission(doc, root, "android.permission.ACCESS_FINE_LOCATION");
            Permission(doc, root, "android.permission.ACCESS_COARSE_LOCATION");
            Permission(doc, root, "android.permission.INTERNET");

            Redirect(doc, root);

            doc.Save(manifest);
            Debug.Log("MarkerOne: Android manifest updated — location, internet, " +
                      "and the sign-in redirect.");
        }

        private static void Permission(XmlDocument doc, XmlElement root, string name)
        {
            foreach (XmlElement existing in root.SelectNodes("uses-permission"))
            {
                if (existing.GetAttribute("name", Android) == name) { return; }
            }

            XmlElement node = doc.CreateElement("uses-permission");
            node.SetAttribute("name", Android, name);
            root.AppendChild(node);
        }

        /// <summary>
        /// Claim the OAuth scheme on whichever activity actually launches.
        ///
        /// Found by looking for the LAUNCHER category rather than by name,
        /// because which activity that is has changed between Unity versions
        /// and guessing at UnityPlayerActivity would break silently on the ones
        /// where it is something else.
        /// </summary>
        private static void Redirect(XmlDocument doc, XmlElement root)
        {
            string scheme = MarkerOneBuildPostprocess.RedirectScheme();
            if (string.IsNullOrEmpty(scheme))
            {
                Debug.Log("MarkerOne: no Google OAuth client id in any scene — " +
                          "no redirect claimed. Sign-in will not be able to return.");
                return;
            }

            XmlElement launcher = null;

            foreach (XmlElement activity in root.SelectNodes("application/activity"))
            {
                foreach (XmlElement category in activity.SelectNodes("intent-filter/category"))
                {
                    if (category.GetAttribute("name", Android) !=
                        "android.intent.category.LAUNCHER")
                    {
                        continue;
                    }

                    launcher = activity;
                    break;
                }
                if (launcher != null) { break; }
            }

            if (launcher == null)
            {
                Debug.LogWarning("MarkerOne: no launcher activity found — sign-in " +
                                 "redirect not claimed.");
                return;
            }

            // Already there from a previous build of the same project.
            foreach (XmlElement filter in launcher.SelectNodes("intent-filter/data"))
            {
                if (filter.GetAttribute("scheme", Android) == scheme) { return; }
            }

            XmlElement intent = doc.CreateElement("intent-filter");

            XmlElement action = doc.CreateElement("action");
            action.SetAttribute("name", Android, "android.intent.action.VIEW");
            intent.AppendChild(action);

            foreach (string name in new[] { "android.intent.category.DEFAULT",
                                            "android.intent.category.BROWSABLE" })
            {
                XmlElement category = doc.CreateElement("category");
                category.SetAttribute("name", Android, name);
                intent.AppendChild(category);
            }

            XmlElement data = doc.CreateElement("data");
            data.SetAttribute("scheme", Android, scheme);
            intent.AppendChild(data);

            launcher.AppendChild(intent);
        }
    }
}
#endif
