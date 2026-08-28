using System.IO;
using UnityEditor;
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

            // ARCore's own floor. Newer editors reject 24 outright, so this
            // asks for the lowest the installed editor will accept at or above
            // it rather than insisting on a number that may not exist.
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)24;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            Debug.Log("MarkerOne: Android configured — OpenGLES3 only, min SDK " +
                      (int)PlayerSettings.Android.minSdkVersion +
                      ", IL2CPP, ARM64. Graphics API matters most: ARCore does not " +
                      "support Vulkan everywhere and a Vulkan build shows a black " +
                      "camera with no error.");

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("MarkerOne: still on " + EditorUserBuildSettings.activeBuildTarget +
                          ". Switch in File → Build Profiles when you are ready — these " +
                          "settings are stored either way.");
            }
        }
    }
}
