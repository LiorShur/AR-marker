using System.Collections.Generic;
using System.IO;
using MarkerOne.Unity;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
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
        [MenuItem("MarkerOne/Set up scene")]
        public static void Run()
        {
            List<(string Id, GameObject Prefab)> made = MarkerOneShapes.All();

            int wired = WireRig(made);
            int added = EnsureManagers() + EnsureAnchors() + EnsureExtensionsOrigin();
            RetuneRig();
            ZeroOrigin();
            SeeFarEnough();
            added += EnsureDepth();
            EnsureLighting();

            AssetDatabase.SaveAssets();

            Debug.Log($"MarkerOne: {made.Count} shapes in {MarkerOneShapes.Folder}, " +
                      $"{wired} wired to the rig, {added} AR managers added.");
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

            // Assigned rather than left to Awake's FindFirstObjectByType.
            // Both end up the same at runtime, but an inspector reading "None"
            // beside a field the whole anchoring path depends on is a question
            // nobody should have to answer twice.
            if (rig.Anchors == null)
            {
                rig.Anchors = Object.FindFirstObjectByType<GeospatialAnchors>();
            }
            if (rig.Floor == null) { rig.Floor = Object.FindFirstObjectByType<FloorProbe>(); }
            if (rig.SessionCamera == null) { rig.SessionCamera = Camera.main; }

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
            if (rig.GetComponent<GoogleSignIn>() == null)
            {
                rig.gameObject.AddComponent<GoogleSignIn>();
            }

            // AppleSignIn is deliberately not added here. The native side calls
            // back by GameObject name, so it makes its own object with the name
            // it needs rather than inheriting whatever this one is called.

            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
            return 1;
        }

        /// <summary>
        /// Occlusion: let the world hide what is behind it.
        ///
        /// Without this a placement draws over everything, so a cube left
        /// behind a wall is visible through the wall — which is the single
        /// clearest signal to a viewer that what they are looking at is not
        /// really there. The iPhone 12 Pro has LiDAR and ARKit will hand over a
        /// depth image for the whole scene; AR Foundation composites against it
        /// once an AROcclusionManager exists to ask.
        ///
        /// Requested at the highest quality: the manager falls back on its own
        /// where a device cannot do it, and asking for less on a device that
        /// can is leaving the best part on the table.
        /// </summary>
        private static int EnsureDepth()
        {
            var rig = Object.FindFirstObjectByType<MarkerOneRig>();
            Camera camera = rig != null && rig.SessionCamera != null ? rig.SessionCamera : Camera.main;
            if (camera == null) { return 0; }

            var occlusion = camera.GetComponent<AROcclusionManager>();
            if (occlusion == null) { occlusion = camera.gameObject.AddComponent<AROcclusionManager>(); }

            occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Best;
            occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.PreferEnvironmentOcclusion;

            // Raw LiDAR depth is noisy at edges and jitters frame to frame, so
            // an object standing against a surface gets chewed at its outline
            // and appears to break apart. Temporal smoothing averages the depth
            // over time and costs almost nothing; without it occlusion looks
            // less like depth and more like damage.
            occlusion.environmentDepthTemporalSmoothingRequested = true;

            EditorUtility.SetDirty(occlusion);
            EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
            Debug.Log("MarkerOne: occlusion requested at " +
                      occlusion.requestedEnvironmentDepthMode);
            return 1;
        }

        /// <summary>Drive the scene's light from what the camera can see of the
        /// real one. An object lit at midday brightness against a photograph of
        /// dusk is wrong before anybody looks at its geometry.</summary>
        private static void EnsureLighting()
        {
            Light sun = null;
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type != LightType.Directional) { continue; }
                sun = light;
                break;
            }

            if (sun == null || sun.GetComponent<SceneLighting>() != null) { return; }

            sun.gameObject.AddComponent<SceneLighting>();

            // Shadows from a light whose direction is a guess land in the wrong
            // place, and a wrong shadow reads worse than none. Grounding draws
            // a contact shadow that does not depend on knowing where the sun is.
            sun.shadows = LightShadows.None;

            EditorUtility.SetDirty(sun);
            EditorSceneManager.MarkSceneDirty(sun.gameObject.scene);
            Debug.Log("MarkerOne: scene lighting follows the camera now.");
        }

        /// <summary>
        /// Let the camera see as far as the rig queries.
        ///
        /// Unity's AR template sets the far clipping plane to twenty metres,
        /// which is a sensible default for AR you can reach out and touch and
        /// a poor one for content anchored to the globe. The rig fetches
        /// placements within three hundred metres; everything past twenty of
        /// them was clipped away and never drawn, which looks precisely like a
        /// placement that failed to appear.
        /// </summary>
        private static void SeeFarEnough()
        {
            var rig = Object.FindFirstObjectByType<MarkerOneRig>();
            Camera camera = rig != null && rig.SessionCamera != null
                ? rig.SessionCamera
                : Camera.main;

            if (camera == null) { return; }

            float wanted = Mathf.Max(1000f, (float)((rig != null ? rig.RadiusM : 300) * 1.2));
            if (camera.farClipPlane >= wanted) { return; }

            Debug.Log("MarkerOne: camera far clip was " + camera.farClipPlane +
                      "m, which would have hidden anything further — set to " + wanted + "m.");
            camera.farClipPlane = wanted;

            EditorUtility.SetDirty(camera);
            EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
        }

        /// <summary>
        /// Move the XR Origin to the world origin.
        ///
        /// ARCore's Convert works in session space, which is the XR Origin's
        /// trackables space. Session space and world space are the same thing
        /// only while the XR Origin sits at zero, and nothing enforces that.
        /// This one had been dragged about thirty metres, and every coordinate
        /// written was thirty metres wrong, then read back and drawn thirty
        /// metres wrong again, with a correctly resolved anchor holding it
        /// faithfully in the wrong place.
        ///
        /// The conversion handles a displaced origin now. Zeroing it anyway,
        /// because the amount of this project that reads more simply when the
        /// two spaces coincide is considerable.
        /// </summary>
        private static void ZeroOrigin()
        {
            // The AR Session's transform means nothing — the session is not
            // placed in the scene — but a non-zero one invites the reader to
            // wonder whether it does.
            var session = Object.FindFirstObjectByType<ARSession>();
            if (session != null && session.transform.localPosition.sqrMagnitude > 0.0001f)
            {
                session.transform.localPosition = Vector3.zero;
                EditorUtility.SetDirty(session);
            }

            var origin = Object.FindFirstObjectByType<XROrigin>();
            if (origin == null) { return; }

            Transform t = origin.transform;
            if (t.localPosition.sqrMagnitude < 0.0001f &&
                Quaternion.Angle(t.localRotation, Quaternion.identity) < 0.1f)
            {
                return;
            }

            Debug.Log("MarkerOne: XR Origin was at " + t.localPosition + " — moved to zero.");
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            EditorUtility.SetDirty(origin);
            EditorSceneManager.MarkSceneDirty(origin.gameObject.scene);
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
