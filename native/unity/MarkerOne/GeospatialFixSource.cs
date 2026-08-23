using System;
using System.Collections;
using Google.XR.ARCoreExtensions;
using MarkerOne.Core;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MarkerOne.Unity
{
    /// <summary>
    /// Turns what AREarthManager reports into the Fix that WorldSession wants.
    ///
    /// The interesting part is the bearing. A walked baseline derives it from
    /// two positions and a journey; the Geospatial API simply knows it, to
    /// about a degree, because EunRotation carries the device's orientation in
    /// the world. Subtracting the camera's yaw within the session gives the
    /// session's own yaw offset from a single observation — so the "walk twenty
    /// metres to find north" step the web version needs does not exist here.
    ///
    /// Nothing in MarkerOne.Core is referenced from Unity's side of this and
    /// nothing Unity is referenced from Core's. That is what makes the maths
    /// testable on a machine with no AR device.
    /// </summary>
    [RequireComponent(typeof(AREarthManager))]
    public sealed class GeospatialFixSource : MonoBehaviour
    {
        [Tooltip("Seconds between fixes once the session is tracking.")]
        public float Interval = 2f;

        [Tooltip("Fixes worse than this are ignored — a fix admitting to fifty "
               + "metres tells you almost nothing and drags the average with it.")]
        public double WorstUsableAccuracyM = 25;

        [Tooltip("Where the session is put together. Assign in the inspector.")]
        public MarkerOneRig Rig;

        public Camera SessionCamera;

        [Tooltip("Seconds to wait for Earth to become ready before giving up and "
               + "saying so. It is normally a second or two outdoors.")]
        public float StartupTimeout = 30f;

        [Tooltip("Seconds to wait for the location permission prompt to be "
               + "answered. Generous, because it waits on a person.")]
        public float LocationTimeout = 60f;

        /// <summary>Set when startup fails, so an interface can show why rather
        /// than showing nothing.</summary>
        public string Failed { get; private set; }

        /// <summary>Whether Google has Street View coverage good enough to
        /// localize visually at the first place a fix was taken. Without it,
        /// Geospatial falls back to GPS and compass — the same inputs the web
        /// version had, and the same accuracy.</summary>
        public string Vps { get; private set; } = "not checked";

        /// <summary>Where startup has got to. Distinct from Failed: this is the
        /// happy path narrating itself, so a screen showing nothing can say
        /// which of the several waits it is in.</summary>
        public string Status { get; private set; } = "starting";

        public event System.Action<string> Problem;

        private AREarthManager _earth;
        private bool _askedAboutVps;

        private void Awake()
        {
            _earth = GetComponent<AREarthManager>();
            if (SessionCamera == null) { SessionCamera = Camera.main; }
        }

        private IEnumerator Start()
        {
            // ARKit first. Nothing about Earth means anything until the
            // underlying session is tracking.
            Status = "waiting for AR session";
            yield return new WaitUntil(() => ARSession.state == ARSessionState.SessionTracking);
            Debug.Log("MarkerOne: AR session tracking");

            // Then location permission, which is the step with no obvious
            // owner. ARCore needs it and does not ask for it; ARKit does not
            // need it and so never prompts. Without something here the session
            // fails to configure with ErrorLocationPermissionNotGranted and
            // Earth simply never becomes ready — a silence that looks exactly
            // like poor reception.
            yield return AwaitLocation();
            if (!string.IsNullOrEmpty(Failed)) { yield break; }

            // ARCore Extensions configures its session once, early, and that
            // attempt has already failed by the time a person taps Allow.
            // Cycling the component makes it configure again now that the
            // permission it wanted exists.
            yield return Reconfigure();

            Status = "waiting for Earth";

            // Then Earth, separately and defensively. AREarthManager.EarthState
            // dereferences the ARCore Extensions session without checking it
            // exists, so on a build where the extensions did not start — iOS
            // Support unticked, no authentication configured, no ARCoreExtensions
            // component in the scene — reading it throws rather than reporting a
            // state. Catching it turns a dead coroutine into a message saying
            // which of those it was.
            float waited = 0;
            EarthState state = EarthState.ErrorEarthNotReady;

            while (waited < StartupTimeout)
            {
                if (!TryReadEarthState(out state)) { yield break; }
                if (state == EarthState.Enabled) { break; }

                if (state != EarthState.ErrorEarthNotReady)
                {
                    // A real error state, not "still starting up". Saying it
                    // once is more use than saying it sixty times a second.
                    Report("Geospatial unavailable: " + state + ". " + Explain(state));
                    yield break;
                }

                waited += 0.25f;
                yield return new WaitForSeconds(0.25f);
            }

            if (state != EarthState.Enabled)
            {
                Report("Geospatial did not become ready within " + StartupTimeout +
                       "s — last state " + state);
                yield break;
            }

            Debug.Log("MarkerOne: Earth enabled, waiting for a fix");
            Status = "enabled, waiting for a fix";

            var wait = new WaitForSeconds(Interval);
            while (enabled)
            {
                if (_earth.EarthTrackingState == TrackingState.Tracking)
                {
                    TryFix();
                }
                else
                {
                    Status = "enabled, not tracking (" + _earth.EarthTrackingState + ")";
                }
                yield return wait;
            }
        }

        /// <summary>Started in its own method because C# forbids yielding
        /// inside a try/catch, and this call can throw when a project is set to
        /// the new input system only.</summary>
        private bool BeginLocation()
        {
            try
            {
                if (Input.location.status == LocationServiceStatus.Running) { return true; }

                // On iOS this call is what raises the permission dialog.
                Input.location.Start(1f, 0.1f);
                return true;
            }
            catch (System.Exception e)
            {
                Report("Could not start location services: " + e.Message);
                return false;
            }
        }

        private IEnumerator AwaitLocation()
        {
            if (!BeginLocation()) { yield break; }

            float waited = 0;
            while (Input.location.status == LocationServiceStatus.Initializing &&
                   waited < LocationTimeout)
            {
                Status = "waiting for location permission";
                waited += 0.25f;
                yield return new WaitForSeconds(0.25f);
            }

            if (Input.location.status == LocationServiceStatus.Running)
            {
                Debug.Log("MarkerOne: location permission granted");
                yield break;
            }

            Report("Location permission not granted — Geospatial cannot start " +
                   "without it. Settings → Privacy & Security → Location Services, " +
                   "then this app, then While Using the App.");
        }

        private IEnumerator CheckVps(double latitude, double longitude)
        {
            VpsAvailabilityPromise promise =
                AREarthManager.CheckVpsAvailabilityAsync(latitude, longitude);
            yield return promise;

            Vps = promise.Result.ToString();
            Debug.Log("MarkerOne: VPS " + Vps);
        }

        private IEnumerator Reconfigure()
        {
            var extensions = FindFirstObjectByType<ARCoreExtensions>();
            if (extensions == null) { yield break; }

            extensions.enabled = false;
            yield return null;
            extensions.enabled = true;
            yield return null;
        }

        private bool TryReadEarthState(out EarthState state)
        {
            state = EarthState.ErrorEarthNotReady;

            if (_earth == null)
            {
                Report("No AREarthManager on this object.");
                return false;
            }

            try
            {
                state = _earth.EarthState;
                return true;
            }
            catch (System.NullReferenceException)
            {
                Report("ARCore Extensions has not started a session. Check that " +
                       "an ARCore Extensions component is in the scene with its " +
                       "Session, Camera Manager and Config assigned, that iOS " +
                       "Support is enabled in XR Plug-in Management, and that an " +
                       "authentication strategy is set.");
                return false;
            }
        }

        /// <summary>These failures are indistinguishable from inside the app —
        /// no content appears — and each has a different fix.
        ///
        /// Switched on the name rather than the enum member because the member
        /// set has changed across ARCore Extensions releases, and a missing
        /// member is a compile error rather than a silent fallthrough. The two
        /// states this code genuinely depends on, Enabled and ErrorEarthNotReady,
        /// are referenced directly; everything else is advisory text.</summary>
        private static string Explain(EarthState state)
        {
            switch (state.ToString())
            {
                case "ErrorNotAuthorized":
                    return "The ARCore API is not enabled on the Cloud project, or " +
                           "the API key is wrong, restricted to a different bundle " +
                           "id, or was created less than a few minutes ago.";
                case "ErrorGeospatialModeDisabled":
                    return "Geospatial is off in the ARCore Extensions config asset, " +
                           "or iOS Support is unticked in XR Plug-in Management.";
                case "ErrorResourceExhausted":
                case "ErrorResourcesExhausted":
                    return "The project is over its ARCore API quota.";
                case "ErrorInternal":
                    return "ARCore reported an internal failure. Retrying the session " +
                           "is the only remedy.";
                default:
                    return "";
            }
        }

        private void Report(string message)
        {
            Debug.LogWarning("MarkerOne: " + message);
            Failed = message;
            Status = message;
            Problem?.Invoke(message);
        }

        private void TryFix()
        {
            GeospatialPose pose = _earth.CameraGeospatialPose;

            // Documented as valid only while EarthTrackingState is Tracking,
            // and worth re-checking rather than assuming.
            if (pose.HorizontalAccuracy <= 0 || pose.HorizontalAccuracy > WorstUsableAccuracyM)
            {
                Status = string.Format("fix too poor to use: ±{0:0.#}m, want ≤{1:0.#}m",
                                       pose.HorizontalAccuracy, WorstUsableAccuracyM);
                return;
            }

            Status = string.Format("tracking ±{0:0.#}m ±{1:0.#}°",
                                   pose.HorizontalAccuracy, pose.OrientationYawAccuracy);

            var fix = new Fix
            {
                Position = new GeoPoint(pose.Latitude, pose.Longitude, pose.Altitude),
                PositionAccuracyM = pose.HorizontalAccuracy,
                HeadingAccuracyDeg = pose.OrientationYawAccuracy,
                Provider = "geospatial",
                HeadingFrom = "direct"
            };

            double? sessionYaw = SessionYaw(pose);
            if (sessionYaw.HasValue)
            {
                fix.SessionYawDeg = sessionYaw.Value;
                fix.SessionYawAccuracyDeg = pose.OrientationYawAccuracy;
            }

            Vector3 local = SessionCamera != null ? SessionCamera.transform.position : Vector3.zero;
            Rig?.Feed(fix, new Vec3(local.x, local.y, local.z));

            // Once, at the first place we had a position good enough to ask
            // about. Coverage is a property of the neighbourhood, not of the
            // second.
            if (!_askedAboutVps)
            {
                _askedAboutVps = true;
                StartCoroutine(CheckVps(pose.Latitude, pose.Longitude));
            }
        }

        /// <summary>
        /// The world heading of the session's forward axis.
        ///
        /// EunRotation is in East-Up-North: +X east, +Y up, +Z north. Unity's
        /// render frame is East +X, Up +Y, North -Z — the same handedness with
        /// north on the other axis — so a heading taken from EUN cannot be used
        /// against session coordinates without accounting for that.
        ///
        /// Rotating forward by EunRotation gives where the camera points in the
        /// world; atan2(east, north) turns that into a compass heading. The
        /// camera's own yaw within the session is the same direction expressed
        /// in session terms. The difference is the offset between the two
        /// frames, which is the one number the whole localization turns on.
        /// </summary>
        private double? SessionYaw(GeospatialPose pose)
        {
            if (SessionCamera == null) { return null; }

            Vector3 worldForward = pose.EunRotation * Vector3.forward;
            double deviceHeading = Mathf.Atan2(worldForward.x, worldForward.z) * Mathf.Rad2Deg;

            // Unity yaw runs clockwise seen from above, which is already the
            // convention a compass heading uses.
            double cameraYaw = SessionCamera.transform.eulerAngles.y;

            return ((deviceHeading - cameraYaw) % 360 + 360) % 360;
        }
    }
}
