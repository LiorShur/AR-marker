using System.Collections.Generic;
using System.IO;
using MarkerOne.Unity;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Google.XR.ARCoreExtensions;

namespace MarkerOne.EditorTools
{
    /// <summary>
    /// The scene wiring, done from code.
    ///
    /// Every step this replaces is one that can be performed correctly and
    /// still come out wrong: a prefab field can point at a scene object rather
    /// than an asset, and then quietly empty itself when that object is
    /// deleted; a manager can be added to the wrong GameObject; a drag into
    /// the Project window can fail to create anything, with no error.
    ///
    /// None of those are mistakes exactly. They are the interface being
    /// ambiguous about which of two similar things you meant. Code is not
    /// ambiguous about it.
    /// </summary>
    public static class MarkerOneSetup
    {
        private const string Folder = "Assets/MarkerOne/Prefabs";

        /// <summary>Matches the scene ids in content.json, so the native app
        /// and the web app render the same placements.</summary>
        private static readonly (string Id, Color Colour)[] Scenes =
        {
            ("rotary-phone", new Color(0.95f, 0.45f, 0.2f)),
            ("beacon",       new Color(0.25f, 0.7f, 1f)),
        };

        [MenuItem("MarkerOne/Set up scene")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();

            var made = new List<(string Id, GameObject Prefab)>();
            foreach ((string id, Color colour) in Scenes)
            {
                made.Add((id, PrefabFor(id, colour)));
            }

            int wired = WireRig(made);
            int added = EnsureManagers() + EnsureAnchors() + EnsureExtensionsOrigin();
            RetuneRig();

            AssetDatabase.SaveAssets();

            Debug.Log($"MarkerOne: {made.Count} prefabs in {Folder}, " +
                      $"{wired} wired to the rig, {added} AR managers added.");
        }

        /// <summary>Built here rather than dragged, so the reference is to an
        /// asset on disk and cannot be invalidated by anything done in the
        /// hierarchy afterwards.</summary>
        private static GameObject PrefabFor(string id, Color colour)
        {
            string path = $"{Folder}/{id}.prefab";

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) { return existing; }

            var root = new GameObject(id);

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localScale = Vector3.one * 0.3f;

            // Nothing raycasts against placements yet, and a collider on every
            // one of them would start intercepting the placement raycast.
            Object.DestroyImmediate(cube.GetComponent<Collider>());

            cube.GetComponent<Renderer>().sharedMaterial = MaterialFor(id, colour);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static Material MaterialFor(string id, Color colour)
        {
            string path = $"{Folder}/{id}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) { return existing; }

            // CreatePrimitive's default material is the built-in one, which a
            // URP project draws as magenta.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool urp = shader != null;
            if (!urp) { shader = Shader.Find("Standard"); }

            var material = new Material(shader);
            material.SetColor(urp ? "_BaseColor" : "_Color", colour);

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static int WireRig(List<(string Id, GameObject Prefab)> made)
        {
            var rig = Object.FindFirstObjectByType<MarkerOneRig>();
            if (rig == null)
            {
                Debug.LogWarning("MarkerOne: no MarkerOneRig in the open scene — " +
                                 "prefabs were created but nothing was wired.");
                return 0;
            }

            rig.Scenes.Clear();
            foreach ((string id, GameObject prefab) in made)
            {
                rig.Scenes.Add(new MarkerOneRig.ScenePrefab { Scene = id, Prefab = prefab });
            }

            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
            return made.Count;
        }

        /// <summary>Without this component the rig positions placements from
        /// its own frame, which is a metre or two worse between sessions and
        /// gives no sign that anything is missing.</summary>
        private static int EnsureAnchors()
        {
            var rig = Object.FindFirstObjectByType<MarkerOneRig>();
            if (rig == null || rig.GetComponent<GeospatialAnchors>() != null) { return 0; }

            rig.gameObject.AddComponent<GeospatialAnchors>();
            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
            return 1;
        }

        /// <summary>
        /// Put the rig's tuning back to what the code says.
        ///
        /// A public field on a MonoBehaviour is serialized into the scene the
        /// first time the component is added, and the value in the scene wins
        /// forever after. Changing a default in code therefore does nothing to
        /// a component that already exists — silently, with the inspector
        /// showing the old number and the source showing the new one.
        ///
        /// This cost two builds: the anchor agreement limit was raised from ten
        /// metres to fifty, the device went on refusing anchors at ten, and the
        /// log went on saying "past the 10m limit" while the source said 50.
        /// </summary>
        private static void RetuneRig()
        {
            var rig = Object.FindFirstObjectByType<MarkerOneRig>();
            if (rig == null) { return; }

            var fresh = new GameObject("defaults").AddComponent<MarkerOneRig>();
            try
            {
                rig.AnchorAgreementM = fresh.AnchorAgreementM;
                rig.DisagreementsAllowed = fresh.DisagreementsAllowed;
                rig.MinFixes = fresh.MinFixes;
                rig.ImprovementRatio = fresh.ImprovementRatio;
                rig.RetryAfterS = fresh.RetryAfterS;
            }
            finally
            {
                Object.DestroyImmediate(fresh.gameObject);
            }

            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
            Debug.Log("MarkerOne: rig tuning reset to code defaults — anchor agreement " +
                      rig.AnchorAgreementM + "m");
        }

        /// <summary>ARCoreExtensions.Origin is read by exactly one thing —
        /// the line in AddAnchor that parents a new geospatial anchor to the
        /// trackables parent. Leave it empty and everything else works, right
        /// up to the first anchor, which throws from inside the package.</summary>
        private static int EnsureExtensionsOrigin()
        {
            var extensions = Object.FindFirstObjectByType<ARCoreExtensions>();
            if (extensions == null || extensions.Origin != null) { return 0; }

            var origin = Object.FindFirstObjectByType<XROrigin>();
            if (origin == null) { return 0; }

            extensions.Origin = origin;
            EditorUtility.SetDirty(extensions);
            EditorSceneManager.MarkSceneDirty(extensions.gameObject.scene);
            return 1;
        }

        /// <summary>PlacementInput aims with the raycast manager and FloorProbe
        /// learns from the plane manager. Both belong on the XR Origin, and
        /// putting them anywhere else fails silently.</summary>
        private static int EnsureManagers()
        {
            var origin = Object.FindFirstObjectByType<XROrigin>();
            if (origin == null)
            {
                Debug.LogWarning("MarkerOne: no XR Origin in the open scene — " +
                                 "AR managers not added.");
                return 0;
            }

            int added = 0;
            if (origin.GetComponent<ARRaycastManager>() == null)
            {
                origin.gameObject.AddComponent<ARRaycastManager>();
                added++;
            }
            if (origin.GetComponent<ARPlaneManager>() == null)
            {
                origin.gameObject.AddComponent<ARPlaneManager>();
                added++;
            }
            if (origin.GetComponent<ARAnchorManager>() == null)
            {
                origin.gameObject.AddComponent<ARAnchorManager>();
                added++;
            }

            if (added > 0)
            {
                EditorUtility.SetDirty(origin);
                EditorSceneManager.MarkSceneDirty(origin.gameObject.scene);
            }
            return added;
        }
    }
}
