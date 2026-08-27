using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Light the placements the way the place is lit.
    ///
    /// A cube rendered at a fixed brightness looks pasted on, and the reason is
    /// not resolution or shading — it is that the eye reads a mismatch in
    /// exposure and colour long before it reads geometry. An object lit at
    /// midday brightness against a photograph of dusk is wrong in a way nobody
    /// has to think about.
    ///
    /// ARKit reports what it can see: an average brightness and a colour
    /// temperature, and on some devices a dominant light direction. Feeding
    /// those into the scene's light and ambient costs almost nothing and is the
    /// largest single improvement available to how this looks.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public sealed class SceneLighting : MonoBehaviour
    {
        [Tooltip("How quickly the light follows the camera's estimate. Instant "
               + "tracking flickers as the estimate wobbles frame to frame.")]
        public float FollowPerSecond = 3f;

        [Tooltip("Brightness when the camera has not said anything yet.")]
        public float Default = 1f;

        private Light _light;
        private ARCameraManager _camera;
        private float _rescan;

        private float _wantIntensity;
        private Color _wantColour = Color.white;

        private void Awake()
        {
            _light = GetComponent<Light>();
            _wantIntensity = Default;
        }

        private void OnDisable()
        {
            if (_camera != null) { _camera.frameReceived -= OnFrame; }
        }

        private void Update()
        {
            if (_camera == null)
            {
                _rescan -= Time.unscaledDeltaTime;
                if (_rescan <= 0)
                {
                    _rescan = 1f;
                    _camera = FindFirstObjectByType<ARCameraManager>();
                    if (_camera != null)
                    {
                        // Asked for here rather than in the inspector, because a
                        // scene that has to be configured for this to work is a
                        // scene where it will silently not work.
                        _camera.requestedLightEstimation =
                            LightEstimation.AmbientIntensity | LightEstimation.AmbientColor;
                        _camera.frameReceived += OnFrame;
                    }
                }
                return;
            }

            // Eased rather than applied. The estimate moves as the camera
            // adjusts its own exposure, and following it exactly makes the
            // whole scene pulse.
            float t = 1 - Mathf.Exp(-FollowPerSecond * Time.deltaTime);

            _light.intensity = Mathf.Lerp(_light.intensity, _wantIntensity, t);
            _light.color = Color.Lerp(_light.color, _wantColour, t);
            RenderSettings.ambientLight = _light.color * _wantIntensity * 0.6f;
        }

        private void OnFrame(ARCameraFrameEventArgs args)
        {
            ARLightEstimationData light = args.lightEstimation;

            if (light.averageBrightness.HasValue)
            {
                // Brightness arrives as 0..1 around a mid-grey. Doubling puts a
                // normally-lit scene near one, which is where the light started.
                _wantIntensity = Mathf.Clamp(light.averageBrightness.Value * 2f, 0.15f, 2f);
            }

            if (light.averageColorTemperature.HasValue)
            {
                _wantColour = Mathf.CorrelatedColorTemperatureToRGB(
                    light.averageColorTemperature.Value);
            }
            else if (light.colorCorrection.HasValue)
            {
                _wantColour = light.colorCorrection.Value;
            }

            // Where the device can say which way the light comes from, use it.
            // Shadows falling the wrong way are worse than no shadows, and this
            // is the only thing that can tell us.
            if (light.mainLightDirection.HasValue &&
                light.mainLightDirection.Value.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.LookRotation(light.mainLightDirection.Value);
            }
        }
    }
}
