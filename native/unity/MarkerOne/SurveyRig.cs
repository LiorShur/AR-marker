using System.Collections.Generic;
using MarkerOne.Core;
using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Measuring a room against a written requirement.
    ///
    /// The measuring is the easy half. What makes this worth carrying instead
    /// of a tape measure is the other half: a tape tells you 780 mm and leaves
    /// you to remember what the figure was, whether it is a minimum or a band,
    /// and what to do about it — and it cannot show anybody where the rail
    /// should have gone. This holds the requirement beside the measurement and
    /// draws the difference in the room.
    ///
    /// Deliberately session-only, like the indoor mode it lives in. A survey is
    /// an hour's work in one room, and nothing about it needs to survive the
    /// walk back to the car.
    /// </summary>
    public sealed class SurveyRig : MonoBehaviour
    {
        public static bool Running;

        private readonly Dictionary<string, Verdict> _found = new Dictionary<string, Verdict>();
        private readonly List<GameObject> _drawn = new List<GameObject>();

        private IReadOnlyList<AccessCheck> _checks = Standards.Toilet();
        private MarkerOneRig _rig;
        private int _at;

        /// <summary>The first point of a pair, once one has been put down.</summary>
        private Vector3? _from;

        public IReadOnlyList<AccessCheck> Checks => _checks;

        public AccessCheck Current => _at >= 0 && _at < _checks.Count ? _checks[_at] : null;

        public int At => _at;

        public bool Half => _from.HasValue;

        public Verdict Found(string id) => _found.TryGetValue(id, out Verdict v) ? v : null;

        public int Done => _found.Count;

        public int Failing
        {
            get
            {
                int bad = 0;
                foreach (Verdict v in _found.Values) { if (!v.Meets) { bad++; } }
                return bad;
            }
        }

        private void Update()
        {
            if (_rig == null) { _rig = FindFirstObjectByType<MarkerOneRig>(); }
            if (!Running && _drawn.Count > 0) { Wipe(); }
        }

        public void Go(int to)
        {
            _at = Mathf.Clamp(to, 0, _checks.Count - 1);
            _from = null;
            Show();
        }

        public void Next() => Go(_at + 1);

        public void Back() => Go(_at - 1);

        /// <summary>
        /// Put down a point. Two of them make a distance; for a height, one is
        /// enough and the floor is the other.
        /// </summary>
        public string Tap(Vector3 where)
        {
            AccessCheck check = Current;
            if (check == null) { return "nothing to measure"; }

            switch (check.How)
            {
                case Measured.Height:
                    return Record(check, where.y - Floor());

                case Measured.Fits:
                    // Nothing to measure. The shape is on the floor and the
                    // judgement is the surveyor's, which is the honest division
                    // of labour — whether a circle is clear of a door swing and
                    // a bin is not a number.
                    return "walk round it, then say whether it fits";

                default:
                    if (!_from.HasValue)
                    {
                        _from = where;
                        Show();
                        return "now the other side";
                    }

                    double across = Vector3.Distance(_from.Value, where);
                    _from = null;
                    return Record(check, across);
            }
        }

        /// <summary>For a check nothing can measure: the surveyor's answer.
        /// </summary>
        public string Says(bool fits)
        {
            AccessCheck check = Current;
            if (check == null) { return ""; }

            var verdict = new Verdict
            {
                Check = check,
                MeasuredM = fits ? check.AtLeastM ?? 0 : 0,
                Meets = fits,
                Says = fits ? "fits, by eye" : "does not fit"
            };

            _found[check.Id] = verdict;
            Show();
            return verdict.Says;
        }

        private string Record(AccessCheck check, double measuredM)
        {
            Verdict verdict = Access.Judge(check, measuredM);
            _found[check.Id] = verdict;
            Show();
            return verdict.Says;
        }

        public void Forget()
        {
            _found.Clear();
            _from = null;
            Go(0);
        }

        /// <summary>
        /// Draw what the standard asks for, where it asks for it.
        ///
        /// This is the part a tape measure cannot do and the reason for using
        /// AR at all: a rail 60 mm too low is an abstraction until a translucent
        /// one is hanging where it should be, next to the one that is there.
        /// </summary>
        private void Show()
        {
            Wipe();

            AccessCheck check = Current;
            if (check == null || _rig == null) { return; }

            Transform root = _rig.PlacementRoot;
            Camera eye = _rig.SessionCamera;
            if (eye == null) { return; }

            if (check.How == Measured.Fits && check.AtLeastM.HasValue)
            {
                // On the floor, in front of where the surveyor is standing.
                Vector3 ahead = eye.transform.position + Flat(eye.transform.forward) * 1.5f;
                Ghost(Ring(check.AtLeastM.Value), new Vector3(ahead.x, Floor(), ahead.z),
                      Quaternion.identity, root);
                return;
            }

            if (check.How == Measured.Height)
            {
                // A line at the cited height, across the wall being looked at.
                double at = check.AtLeastM ?? check.AtMostM ?? 0;
                Vector3 ahead = eye.transform.position + Flat(eye.transform.forward) * 1.2f;

                Ghost(Bar(1.2f), new Vector3(ahead.x, Floor() + (float)at, ahead.z),
                      Quaternion.LookRotation(Flat(eye.transform.forward)), root);
                return;
            }

            if (_from.HasValue && check.AtLeastM.HasValue)
            {
                // How wide the gap has to be, laid from the first point, so the
                // second one can be put down against something rather than
                // guessed at.
                Vector3 across = Flat(eye.transform.right) * (float)check.AtLeastM.Value;
                Ghost(Bar((float)check.AtLeastM.Value),
                      _from.Value + across * 0.5f,
                      Quaternion.LookRotation(Flat(eye.transform.right)), root);
            }
        }

        private void Ghost(Mesh mesh, Vector3 at, Quaternion turn, Transform root)
        {
            var go = new GameObject("required");
            go.transform.SetParent(root != null ? root : transform, false);
            go.transform.SetPositionAndRotation(at, turn);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Translucent();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _drawn.Add(go);
        }

        private void Wipe()
        {
            foreach (GameObject go in _drawn) { if (go != null) { Destroy(go); } }
            _drawn.Clear();
        }

        private float Floor() => _rig != null && _rig.Floor != null ? _rig.Floor.Floor : 0;

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0;
            return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
        }

        // ── the two shapes a requirement takes ───────────────────

        private static Mesh _ring;
        private static float _ringM;
        private static Mesh _bar;
        private static float _barM;
        private static Material _material;

        /// <summary>A flat annulus of the required diameter, lying on the floor.
        /// An outline rather than a disc, because a filled circle hides the
        /// thing you are checking it against.</summary>
        private static Mesh Ring(double diameterM)
        {
            if (_ring != null && Mathf.Approximately(_ringM, (float)diameterM)) { return _ring; }

            const int Steps = 64;
            float outer = (float)diameterM * 0.5f;
            float inner = outer - 0.03f;

            var vertices = new Vector3[Steps * 2];
            var triangles = new int[Steps * 6];

            for (int i = 0; i < Steps; i++)
            {
                float turn = i / (float)Steps * Mathf.PI * 2;
                float x = Mathf.Cos(turn), z = Mathf.Sin(turn);

                vertices[i * 2] = new Vector3(x * inner, 0, z * inner);
                vertices[i * 2 + 1] = new Vector3(x * outer, 0, z * outer);

                int a = i * 2, b = i * 2 + 1;
                int c = (i * 2 + 2) % (Steps * 2), d = (i * 2 + 3) % (Steps * 2);

                triangles[i * 6] = a;
                triangles[i * 6 + 1] = c;
                triangles[i * 6 + 2] = b;
                triangles[i * 6 + 3] = b;
                triangles[i * 6 + 4] = c;
                triangles[i * 6 + 5] = d;
            }

            _ring = new Mesh { vertices = vertices, triangles = triangles };
            _ring.RecalculateNormals();
            _ring.hideFlags = HideFlags.HideAndDontSave;
            _ringM = (float)diameterM;
            return _ring;
        }

        /// <summary>A thin horizontal bar of the required length, for a height
        /// or a width.</summary>
        private static Mesh Bar(float lengthM)
        {
            if (_bar != null && Mathf.Approximately(_barM, lengthM)) { return _bar; }

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh source = cube.GetComponent<MeshFilter>().sharedMesh;

            var vertices = new Vector3[source.vertexCount];
            Vector3[] from = source.vertices;

            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3(from[i].x * lengthM, from[i].y * 0.02f,
                                          from[i].z * 0.02f);
            }

            _bar = new Mesh { vertices = vertices, triangles = source.triangles };
            _bar.RecalculateNormals();
            _bar.hideFlags = HideFlags.HideAndDontSave;
            _barM = lengthM;

            DestroyImmediate(cube);
            return _bar;
        }

        private static Material Translucent()
        {
            if (_material != null) { return _material; }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                            Shader.Find("Unlit/Color");

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetColor("_BaseColor", new Color(0.3f, 0.9f, 1f, 0.55f));
            _material.color = new Color(0.3f, 0.9f, 1f, 0.55f);

            // Drawn as something the room is being compared against rather than
            // as something in it: visible through what it is measuring, because
            // a rail hidden behind the wall it should be on says nothing.
            _material.renderQueue = 3100;
            return _material;
        }
    }
}
