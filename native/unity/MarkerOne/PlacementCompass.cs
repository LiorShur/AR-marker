using System.Collections.Generic;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Pointing at what you cannot see.
    ///
    /// An object anchored to a coordinate is invisible until you happen to face
    /// it, and a phone's field of view is about sixty degrees — so five sixths
    /// of the world is behind you at any moment. Finding something meant
    /// turning slowly on the spot and hoping, which works badly in a garden and
    /// not at all across a hillside.
    ///
    /// Off-screen placements get an arrow pinned to the edge of the screen,
    /// pointing the way, with the distance beside it. On-screen ones get the
    /// distance alone, since the arrow would only be pointing at something
    /// already in front of you.
    ///
    /// The arrow takes the colour of the thing it points at, which is what
    /// makes it usable with more than one: "the orange one is behind me" is a
    /// thought you can have, where "one of the four is behind me" is not.
    /// </summary>
    public sealed class PlacementCompass : MonoBehaviour
    {
#if !MARKERONE_NO_HUD
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("MarkerOne Compass");
            go.AddComponent<PlacementCompass>();
            DontDestroyOnLoad(go);
        }
#endif

        [Tooltip("How many to point at. Past a handful the edge of the screen "
               + "becomes a fence and none of them is legible.")]
        public int MostAtOnce = 6;

        [Tooltip("Ignore anything further than this. Something four hundred "
               + "metres away is true and useless.")]
        public float WithinM = 150f;

        private MarkerOneRig _rig;
        private Camera _camera;
        private float _rescan;

        private GUIStyle _text;
        private Texture2D _arrow;
        private Texture2D _dot;

        private readonly List<(GameObject Go, float Distance)> _near =
            new List<(GameObject, float)>();

        private void Update()
        {
            _rescan -= Time.unscaledDeltaTime;
            if (_rescan <= 0 && (_rig == null || _camera == null))
            {
                _rescan = 1f;
                if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }
                if (_camera == null) { _camera = Camera.main; }
            }
        }

        private void OnGUI()
        {
            if (_rig == null || _camera == null || !MarkerOneHud.Visible) { return; }

            EnsureStyles();
            Gather();

            foreach ((GameObject go, float distance) in _near)
            {
                Draw(go, distance);
            }
        }

        private void Gather()
        {
            _near.Clear();

            Vector3 eye = _camera.transform.position;

            foreach (KeyValuePair<string, GameObject> entry in _rig.Objects)
            {
                if (entry.Value == null || !entry.Value.activeSelf) { continue; }

                float distance = Vector3.Distance(eye, entry.Value.transform.position);
                if (distance > WithinM) { continue; }

                _near.Add((entry.Value, distance));
            }

            _near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (_near.Count > MostAtOnce) { _near.RemoveRange(MostAtOnce, _near.Count - MostAtOnce); }
        }

        private void Draw(GameObject go, float distance)
        {
            Rect safe = Screen.safeArea;
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            Vector3 projected = _camera.WorldToScreenPoint(go.transform.position);

            // Screen space counts up from the bottom and IMGUI counts down from
            // the top.
            var at = new Vector2(projected.x, Screen.height - projected.y);

            // Behind the camera the projection comes back mirrored, and a point
            // that is in fact over your shoulder appears in front of you. Turn
            // it back through the centre so the arrow points the way you would
            // actually have to turn.
            bool behind = projected.z <= 0;
            if (behind) { at = centre + (centre - at); }

            float margin = _text.fontSize * 2.4f;
            var inside = new Rect(safe.x + margin,
                                  Screen.height - (safe.y + safe.height) + margin,
                                  safe.width - margin * 2,
                                  safe.height - margin * 2);

            Color colour = ColourOf(go);
            string label = distance < 10
                ? distance.ToString("0.0") + "m"
                : distance.ToString("0") + "m";

            if (!behind && inside.Contains(at))
            {
                // In front of you and on screen. An arrow would be pointing at
                // something you can already see, so only the distance is worth
                // saying.
                GUI.color = colour;
                GUI.DrawTexture(new Rect(at.x - 3, at.y - 3, 6, 6), _dot);
                GUI.color = Color.white;

                Label(new Vector2(at.x + 10, at.y - _text.fontSize * 0.6f), label, colour);
                return;
            }

            // Off screen: put it against the edge, pointing outwards.
            Vector2 direction = (at - centre).normalized;
            if (direction.sqrMagnitude < 0.001f) { direction = Vector2.up; }

            Vector2 edge = Clamp(centre, direction, inside);
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg + 90;

            float size = _text.fontSize * 1.6f;
            var box = new Rect(edge.x - size * 0.5f, edge.y - size * 0.5f, size, size);

            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, edge);
            GUI.color = colour;
            GUI.DrawTexture(box, _arrow);
            GUI.color = Color.white;
            GUI.matrix = saved;

            // The label sits inboard of the arrow so it stays on screen however
            // near the corner the arrow ends up.
            Label(edge - direction * size - new Vector2(_text.fontSize, 0), label, colour);
        }

        private void Label(Vector2 at, string what, Color colour)
        {
            var box = new Rect(at.x, at.y, _text.fontSize * 5, _text.fontSize * 1.4f);

            _text.normal.textColor = Color.black;
            GUI.Label(new Rect(box.x + 1, box.y + 1, box.width, box.height), what, _text);

            _text.normal.textColor = colour;
            GUI.Label(box, what, _text);
        }

        /// <summary>The colour of what it points at, so several are telling
        /// apart. Falls back to white rather than guessing.</summary>
        private static Color ColourOf(GameObject go)
        {
            var renderer = go.GetComponentInChildren<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null) { return Color.white; }

            Material material = renderer.sharedMaterial;
            if (material.HasProperty("_BaseColor")) { return material.GetColor("_BaseColor"); }
            if (material.HasProperty("_Color")) { return material.GetColor("_Color"); }
            return Color.white;
        }

        /// <summary>Where a ray from the centre leaves the rectangle.</summary>
        private static Vector2 Clamp(Vector2 centre, Vector2 direction, Rect within)
        {
            float horizontal = direction.x > 0
                ? (within.xMax - centre.x) / direction.x
                : direction.x < 0 ? (within.xMin - centre.x) / direction.x : float.MaxValue;

            float vertical = direction.y > 0
                ? (within.yMax - centre.y) / direction.y
                : direction.y < 0 ? (within.yMin - centre.y) / direction.y : float.MaxValue;

            return centre + direction * Mathf.Min(horizontal, vertical);
        }

        private void EnsureStyles()
        {
            int size = Mathf.Max(11, Mathf.RoundToInt(Screen.height * 0.02f));
            if (_text != null && _text.fontSize == size) { return; }

            if (_arrow == null)
            {
                _arrow = Triangle(48);
                _dot = new Texture2D(1, 1);
                _dot.SetPixel(0, 0, Color.white);
                _dot.Apply();
                _dot.hideFlags = HideFlags.HideAndDontSave;
            }

            _text = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false
            };
        }

        /// <summary>A triangle pointing up, drawn rather than imported so there
        /// is no asset to lose.</summary>
        private static Texture2D Triangle(int side)
        {
            var texture = new Texture2D(side, side, TextureFormat.RGBA32, false);
            var pixels = new Color[side * side];

            for (int y = 0; y < side; y++)
            {
                // Row 0 is the bottom of a texture and the base of the arrow;
                // the top row is a single pixel at the tip.
                float half = (side - 1 - y) * 0.5f;
                float middle = (side - 1) * 0.5f;

                for (int x = 0; x < side; x++)
                {
                    float inside = half - Mathf.Abs(x - middle);
                    // One pixel of feathering, so the edges are not stairs.
                    pixels[y * side + x] = new Color(1, 1, 1, Mathf.Clamp01(inside));
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void OnDestroy()
        {
            if (_arrow != null) { Destroy(_arrow); }
            if (_dot != null) { Destroy(_dot); }
        }
    }
}
