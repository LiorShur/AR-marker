using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// A shadow, so the thing looks like it is standing somewhere.
    ///
    /// An object with nothing beneath it reads as floating however correct its
    /// position is — the eye takes contact with the ground from the shadow, not
    /// from the geometry, and without one a cube resting perfectly on tarmac
    /// still looks pasted onto the photograph.
    ///
    /// A real shadow needs something to fall on, and there is no ground here —
    /// only a camera image. So this is a soft dark ellipse laid flat at the
    /// object's base and sized to its footprint: the oldest trick there is, and
    /// still the one that does most of the work.
    ///
    /// Kept level in world space rather than parented rigidly, because an
    /// anchor's pitch and roll drift by a degree or two and a shadow that tilts
    /// with them announces itself immediately.
    /// </summary>
    public sealed class Grounding : MonoBehaviour
    {
        [Tooltip("How much wider than the object the shadow spreads.")]
        public float Spread = 1.6f;

        private static Texture2D _blob;
        private static Material _material;

        private Transform _shadow;

        private void Start()
        {
            Bounds footprint = Footprint();
            if (footprint.size.sqrMagnitude <= 0) { return; }

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "shadow";

            // The primitive brings a collider, and a collider under every
            // placement would start catching the placement raycast — aiming
            // past an object to put something behind it would stop working.
            Collider collider = quad.GetComponent<Collider>();
            if (collider != null) { Destroy(collider); }

            quad.GetComponent<Renderer>().sharedMaterial = Material();
            quad.transform.SetParent(transform, false);

            float across = Mathf.Max(footprint.size.x, footprint.size.z) * Spread;
            quad.transform.localScale = new Vector3(across, across, 1);

            _shadow = quad.transform;
            Place(footprint);
        }

        private void LateUpdate()
        {
            if (_shadow == null) { return; }
            Place(Footprint());
        }

        /// <summary>Flat, level, and just above the base — a hair off the
        /// ground so it does not fight the camera image for the same depth.</summary>
        private void Place(Bounds footprint)
        {
            _shadow.position = new Vector3(transform.position.x,
                                           footprint.min.y + 0.01f,
                                           transform.position.z);
            _shadow.rotation = Quaternion.Euler(90, 0, 0);
        }

        private Bounds Footprint()
        {
            var renderers = GetComponentsInChildren<Renderer>();
            var bounds = new Bounds(transform.position, Vector3.zero);
            bool any = false;

            foreach (Renderer r in renderers)
            {
                if (r == null || r.transform == _shadow) { continue; }

                if (!any) { bounds = r.bounds; any = true; }
                else { bounds.Encapsulate(r.bounds); }
            }

            return any ? bounds : new Bounds(transform.position, Vector3.zero);
        }

        /// <summary>Shared across every placement — one texture, one material,
        /// however many things are on screen.</summary>
        private static Material Material()
        {
            if (_material != null) { return _material; }

            _material = new Material(Unlit()) { renderQueue = 3000 };
            _material.mainTexture = Blob();
            _material.hideFlags = HideFlags.HideAndDontSave;

            // URP's unlit shader is opaque until told otherwise, and the
            // incantation is not discoverable: the surface type, the blend
            // modes, depth writing and the keyword all have to agree.
            if (_material.HasProperty("_Surface"))
            {
                _material.SetFloat("_Surface", 1);
                _material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _material.SetFloat("_DstBlend",
                                   (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _material.SetFloat("_ZWrite", 0);
                _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                _material.DisableKeyword("_ALPHATEST_ON");
            }

            _material.color = Color.white;
            return _material;
        }

        private static Shader Unlit()
        {
            return Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Sprites/Default");
        }

        /// <summary>A soft dark ellipse, drawn rather than imported so there is
        /// no asset to lose and nothing to remember to include in a build.</summary>
        private static Texture2D Blob()
        {
            if (_blob != null) { return _blob; }

            const int side = 128;
            _blob = new Texture2D(side, side, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[side * side];
            float middle = (side - 1) * 0.5f;

            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    float dx = (x - middle) / middle;
                    float dy = (y - middle) / middle;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);

                    // Squared falloff rather than linear: a real contact shadow
                    // is dense under the object and gone within its own width,
                    // and a linear ramp reads as a grey disc.
                    float a = Mathf.Clamp01(1 - r);
                    pixels[y * side + x] = new Color(0, 0, 0, a * a * 0.38f);
                }
            }

            _blob.SetPixels(pixels);
            _blob.Apply();
            return _blob;
        }
    }
}
