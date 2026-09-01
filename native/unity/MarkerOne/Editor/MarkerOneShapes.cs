using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MarkerOne.EditorTools
{
    /// <summary>
    /// The things you can leave somewhere.
    ///
    /// Built from primitives rather than modelled, and generated rather than
    /// imported, for the same reason everything else here is: an asset in a
    /// repository is a thing to lose, and an asset a person has to make by hand
    /// is a step that will be done differently twice.
    ///
    /// They are deliberately plain. A marker's job is to be seen from across a
    /// garden and recognised for what it is, and at three metres through a phone
    /// camera a silhouette does that and a detailed model does not.
    /// </summary>
    public static class MarkerOneShapes
    {
        public const string Folder = "Assets/MarkerOne/Prefabs";

        /// <summary>Scene id, colour, and how to build it. The ids are what the
        /// store holds, so they outlive any of this and are worth choosing
        /// deliberately.</summary>
        public static readonly (string Id, Color Colour)[] Catalogue =
        {
            ("beacon",       new Color(0.25f, 0.62f, 1f)),
            ("rotary-phone", new Color(0.95f, 0.45f, 0.2f)),
            ("pin",          new Color(0.92f, 0.24f, 0.32f)),
            ("signpost",     new Color(0.55f, 0.40f, 0.26f)),
            ("plaque",       new Color(0.78f, 0.72f, 0.55f)),
            ("arrow",        new Color(1f, 0.78f, 0.15f)),
            ("cairn",        new Color(0.62f, 0.62f, 0.60f)),
        };

        public static GameObject Make(string id, Color colour)
        {
            string path = $"{Folder}/{id}.prefab";

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) { return existing; }

            var root = new GameObject(id);
            Material material = MaterialFor(id, colour);

            switch (id)
            {
                case "pin": Pin(root, material); break;
                case "signpost": Signpost(root, material); break;
                case "plaque": Plaque(root, material); break;
                case "arrow": Arrow(root, material); break;
                case "cairn": Cairn(root, material); break;
                default: Block(root, material); break;
            }

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        // ── the shapes ───────────────────────────────────────────

        /// <summary>A cube, for the two ids that already existed. Changing what
        /// they look like would move things people have already placed.</summary>
        private static void Block(GameObject root, Material material)
        {
            Part(root, material, PrimitiveType.Cube,
                 new Vector3(0, 0.15f, 0), Vector3.one * 0.3f);
        }

        /// <summary>A map pin: the shape everybody already reads as "here",
        /// which saves explaining it.</summary>
        private static void Pin(GameObject root, Material material)
        {
            Part(root, material, PrimitiveType.Sphere,
                 new Vector3(0, 0.42f, 0), Vector3.one * 0.22f);

            // A cone would be better and Unity has no cone primitive, so a
            // squashed, inverted capsule stands in. At arm's length the
            // difference is not visible; the silhouette is what carries it.
            Part(root, material, PrimitiveType.Capsule,
                 new Vector3(0, 0.17f, 0), new Vector3(0.09f, 0.2f, 0.09f));
        }

        /// <summary>A post with a board on it — the only shape here that says
        /// "read me" rather than "look at me".</summary>
        private static void Signpost(GameObject root, Material material)
        {
            Part(root, material, PrimitiveType.Cylinder,
                 new Vector3(0, 0.35f, 0), new Vector3(0.035f, 0.35f, 0.035f));

            Part(root, material, PrimitiveType.Cube,
                 new Vector3(0.14f, 0.60f, 0), new Vector3(0.34f, 0.16f, 0.03f));
        }

        /// <summary>A panel leaning back on a base, at the angle a plaque is
        /// set into the ground: readable from standing rather than from
        /// above.</summary>
        private static void Plaque(GameObject root, Material material)
        {
            GameObject panel = Part(root, material, PrimitiveType.Cube,
                                    new Vector3(0, 0.26f, 0),
                                    new Vector3(0.42f, 0.30f, 0.025f));
            panel.transform.localRotation = Quaternion.Euler(-18, 0, 0);

            Part(root, material, PrimitiveType.Cube,
                 new Vector3(0, 0.05f, 0.02f), new Vector3(0.46f, 0.09f, 0.16f));
        }

        /// <summary>Pointing down at the spot rather than along the ground.
        /// An arrow lying flat has to be read; one pointing at its own place
        /// does not.</summary>
        private static void Arrow(GameObject root, Material material)
        {
            Part(root, material, PrimitiveType.Cylinder,
                 new Vector3(0, 0.55f, 0), new Vector3(0.05f, 0.18f, 0.05f));

            GameObject head = Part(root, material, PrimitiveType.Capsule,
                                   new Vector3(0, 0.26f, 0),
                                   new Vector3(0.17f, 0.16f, 0.17f));
            head.transform.localRotation = Quaternion.Euler(180, 0, 0);
        }

        /// <summary>Stacked stones. The one shape that reads as deliberate
        /// human marking without reading as signage, which suits a trail.</summary>
        private static void Cairn(GameObject root, Material material)
        {
            Part(root, material, PrimitiveType.Sphere,
                 new Vector3(0, 0.07f, 0), new Vector3(0.26f, 0.13f, 0.24f));

            GameObject middle = Part(root, material, PrimitiveType.Sphere,
                                     new Vector3(0.01f, 0.18f, -0.01f),
                                     new Vector3(0.19f, 0.11f, 0.18f));
            middle.transform.localRotation = Quaternion.Euler(0, 34, 6);

            Part(root, material, PrimitiveType.Sphere,
                 new Vector3(-0.01f, 0.27f, 0.01f), new Vector3(0.12f, 0.09f, 0.12f));
        }

        // ── plumbing ─────────────────────────────────────────────

        private static GameObject Part(GameObject root, Material material,
                                       PrimitiveType shape, Vector3 at, Vector3 size)
        {
            GameObject part = GameObject.CreatePrimitive(shape);

            // Colliders on placements would intercept the placement raycast, so
            // aiming past one to put something behind it would stop working.
            Collider collider = part.GetComponent<Collider>();
            if (collider != null) { Object.DestroyImmediate(collider); }

            part.GetComponent<Renderer>().sharedMaterial = material;
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = at;
            part.transform.localScale = size;
            return part;
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

        public static List<(string Id, GameObject Prefab)> All()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();

            var made = new List<(string, GameObject)>();
            foreach ((string id, Color colour) in Catalogue)
            {
                made.Add((id, Make(id, colour)));
            }
            return made;
        }
    }
}
