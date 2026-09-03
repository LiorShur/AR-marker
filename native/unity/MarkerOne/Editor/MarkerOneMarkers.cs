using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity.Editor
{
    /// <summary>
    /// Makes the printed markers a venue is pinned by, and the library that
    /// lets the camera recognise them.
    ///
    /// Generated rather than supplied because a good tracking image is a
    /// specific thing and not an obvious one. Detectors work on corners: they
    /// want dense, high-contrast, non-repeating detail, and they do badly on
    /// exactly what a person reaches for first — a logo, a photograph of a sky,
    /// anything with large flat areas or any symmetry at all. A symmetric image
    /// is the worst case, because it does not fail, it resolves confidently at
    /// the wrong rotation and puts the whole venue ninety degrees out.
    ///
    /// So these are asymmetric binary noise: thousands of corners, no repeats,
    /// no symmetry, and a solid corner block that makes which-way-up
    /// unambiguous both to the tracker and to whoever is holding the paper.
    /// </summary>
    public static class MarkerOneMarkers
    {
        private const string Folder = "Assets/MarkerOne/Markers";
        private const int Pixels = 1024;
        private const int Cells = 20;

        /// <summary>Printed on A4 with a margin, which is what somebody will
        /// actually do. Declared to AR Foundation so the tracker knows the
        /// scale, and wrong here means everything in the venue is the wrong
        /// size and the wrong distance away.</summary>
        private const float PrintedMetres = 0.18f;

        [MenuItem("MarkerOne/Make venue markers")]
        public static void Make()
        {
            int count = 8;

            Directory.CreateDirectory(Folder);

            var made = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string name = "marker-" + (i + 1).ToString("00");
                string path = Folder + "/" + name + ".png";

                File.WriteAllBytes(path, Draw(i).EncodeToPNG());
                made.Add(path);
            }

            AssetDatabase.Refresh();

            foreach (string path in made) { Readable(path); }

            Library(made);

            Debug.Log($"MarkerOne: {count} markers in {Folder}. Print them at " +
                      $"{PrintedMetres * 100:0} cm across — the size is declared in the " +
                      "library and a marker printed at another size puts everything in " +
                      "the venue at the wrong distance.");

            EditorUtility.RevealInFinder(Folder);
        }

        /// <summary>
        /// Deterministic noise, so a marker regenerated is the same marker.
        ///
        /// A quiet zone round the edge because trackers want one, and a solid
        /// block in one corner so the image cannot be read upside down.
        /// </summary>
        private static Texture2D Draw(int seed)
        {
            var random = new System.Random(9000 + seed);
            var texture = new Texture2D(Pixels, Pixels, TextureFormat.RGB24, false);

            int margin = Pixels / 16;
            int inner = Pixels - margin * 2;
            int cell = inner / Cells;

            var pixels = new Color32[Pixels * Pixels];
            for (int i = 0; i < pixels.Length; i++) { pixels[i] = new Color32(255, 255, 255, 255); }

            for (int y = 0; y < Cells; y++)
            {
                for (int x = 0; x < Cells; x++)
                {
                    // The corner block: three by three, always solid, always
                    // the same corner.
                    bool corner = x < 3 && y < 3;
                    if (!corner && random.Next(2) == 0) { continue; }

                    for (int py = 0; py < cell; py++)
                    {
                        for (int px = 0; px < cell; px++)
                        {
                            int at = (margin + y * cell + py) * Pixels + margin + x * cell + px;
                            pixels[at] = new Color32(0, 0, 0, 255);
                        }
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>The importer has to be told, or the library cannot read the
        /// pixels it needs to build its descriptors from.</summary>
        private static void Readable(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) { return; }

            importer.textureType = TextureImporterType.Default;
            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.SaveAndReimport();
        }

        private static void Library(List<string> paths)
        {
            const string at = Folder + "/MarkerOne Markers.asset";

            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(at);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
                AssetDatabase.CreateAsset(library, at);
            }

            // Emptied and rebuilt rather than appended to, so running this
            // twice does not produce sixteen entries for eight images — which
            // the tracker would then have to disambiguate between and could
            // not.
            var editable = new SerializedObject(library);
            SerializedProperty images = editable.FindProperty("m_Images");
            images.ClearArray();
            editable.ApplyModifiedProperties();

            for (int i = 0; i < paths.Count; i++)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(paths[i]);
                if (texture == null) { continue; }

                XRReferenceImageLibraryExtensions.Add(library);
                XRReferenceImageLibraryExtensions.SetTexture(library, i, texture, true);
                XRReferenceImageLibraryExtensions.SetName(library, i,
                    Path.GetFileNameWithoutExtension(paths[i]));
                XRReferenceImageLibraryExtensions.SetSpecifySize(library, i, true);
                XRReferenceImageLibraryExtensions.SetSize(library, i,
                    new Vector2(PrintedMetres, PrintedMetres));
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
        }
    }
}
