using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MarkerOne.EditorTools
{
    /// <summary>
    /// The Android player settings ARCore needs, set rather than described.
    ///
    /// Every one of these is a place the iOS setup went wrong at least once by
    /// being left to a person and a screenshot. They are all one line of code.
    /// </summary>
    public static class MarkerOneAndroid
    {
        [MenuItem("MarkerOne/Configure Android")]
        public static void Run()
        {
            // Google's Android Resolver copies a Gradle template in here and
            // uses File.Copy, which does not create directories. On a project
            // that has never built for Android the folder does not exist, and
            // the resolver reports a DirectoryNotFoundException from inside
            // itself — accurate, and no help at all in saying that the fix is
            // one empty folder.
            string plugins = Path.Combine(Application.dataPath, "Plugins", "Android");
            if (!Directory.Exists(plugins))
            {
                Directory.CreateDirectory(plugins);
                AssetDatabase.Refresh();
                Debug.Log("MarkerOne: created Assets/Plugins/Android for the Android resolver.");
            }

            // ARCore does not support Vulkan on every device, and Unity puts
            // Vulkan first by default. Leaving it there gives a black camera
            // feed with no error at all — the same failure the iOS notes warn
            // about, and the one most likely to be blamed on the app.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                                           new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });

            // ARCore's floor, by name rather than by cast. A cast to an enum
            // value the editor does not define is not an error — it produces an
            // out-of-range enum that a setter may quietly refuse, which is
            // exactly what happened here: Vulkan came out of the list and the
            // SDK version stayed at 23, and the build failed for a setting the
            // console had already claimed to have set.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Read back rather than reported. Every one of these is a setter
            // that can decline, and a message describing what was asked for is
            // worth nothing next to one describing what is now true.
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            int minSdk = (int)PlayerSettings.Android.minSdkVersion;

            Debug.Log(string.Format(
                "MarkerOne: Android is now — graphics {0}, min SDK {1}, {2}, {3}.",
                string.Join(" ", apis), minSdk,
                PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android),
                PlayerSettings.Android.targetArchitectures));

            if (minSdk < 24)
            {
                Debug.LogWarning("MarkerOne: the minimum SDK did not take — it is still " +
                                 minSdk + " and ARCore needs 24. Set it by hand in Player " +
                                 "Settings → Other Settings → Identification.");
            }

            foreach (var api in apis)
            {
                if (api != UnityEngine.Rendering.GraphicsDeviceType.Vulkan) { continue; }

                Debug.LogWarning("MarkerOne: Vulkan is still in the graphics API list. " +
                                 "ARCore does not support it on every device and a Vulkan " +
                                 "build shows a black camera feed with no error at all.");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("MarkerOne: still on " + EditorUserBuildSettings.activeBuildTarget +
                          ". Switch in File → Build Profiles when you are ready — these " +
                          "settings are stored either way.");
            }
        }
    }
}
