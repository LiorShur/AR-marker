using UnityEngine;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Grow into place rather than blink into existence.
    ///
    /// A placement that appears between one frame and the next is read as a
    /// glitch — the eye has no account of where it came from, so it registers
    /// as the display failing rather than as a thing arriving. A quarter of a
    /// second of growth is enough to turn the same event into something that
    /// happened on purpose.
    ///
    /// It also covers the moment ARCore is still settling a new anchor, which
    /// is exactly when a hard cut would be most jarring.
    /// </summary>
    public sealed class Appear : MonoBehaviour
    {
        public float Seconds = 0.28f;

        private Vector3 _full;
        private float _elapsed;

        private void Start()
        {
            _full = transform.localScale;
            transform.localScale = Vector3.zero;
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;

            float t = Seconds <= 0 ? 1 : Mathf.Clamp01(_elapsed / Seconds);

            // Overshoots slightly and settles. A linear ramp arrives and stops
            // dead, which reads as mechanical; this reads as placed.
            float eased = 1 - Mathf.Pow(1 - t, 3);
            float overshoot = Mathf.Sin(t * Mathf.PI) * 0.08f;

            transform.localScale = _full * (eased + overshoot);

            if (t >= 1)
            {
                transform.localScale = _full;
                Destroy(this);
            }
        }
    }
}
