using System.Collections;
using Unity.WebRTC;
using UnityEngine;

namespace ZedStream
{
    /// <summary>
    /// Brings the ZED video path up: reads the camera calibration, connects the
    /// WHEP client, and hands the decoded texture to the panel.
    ///
    /// It is also the switch that decides whether this app is a video app at all.
    /// Without the HTV_ZED_VIDEO scripting define the whole rig turns itself off in
    /// Awake, before anything else runs, and the app behaves exactly as it did
    /// before this feature existed — passthrough, hand visualization, and the pose
    /// publisher, untouched. That define is set per build by
    /// BuildAndroid's -videoMode flag, so one source tree produces both.
    ///
    /// Video and pose publishing are independent by construction. This assembly
    /// does not reference the publisher's assembly and the publisher does not
    /// reference this one, so a WebRTC failure cannot take the IOBT / controller
    /// stream down with it — the two share nothing but the frame clock.
    /// </summary>
    public sealed class ZedPanelController : MonoBehaviour
    {
        [Header("Jetson")]
        [Tooltip("Host running zedx_webrtc.sh --xr. The headset must be on the same LAN.")]
        public string serverHost = "192.168.20.131";

        [Tooltip("HTTPS port of the Jetson's WHEP / calibration server.")]
        public int serverPort = 8443;

        [Tooltip("Re-attempt the WHEP handshake this often while disconnected. The " +
                 "headset is routinely started before the robot, so a failed first " +
                 "attempt has to be survivable without relaunching the app.")]
        public float reconnectSeconds = 5f;

        [Header("Scene")]
        [Tooltip("The HMD-driven camera. Defaults to Camera.main.")]
        public Camera hmdCamera;

        public ZedWhepClient whepClient;
        public ZedStereoPanel panel;
        public ZedRestFrame restFrame;

        [Header("Surround")]
        [Tooltip("Camera clear colour behind everything. Only visible where the rest " +
                 "frame is not — it is opaque on purpose, because passthrough behind " +
                 "a video panel is a distraction, not a feature.")]
        public Color clearColor = new Color(0.02f, 0.02f, 0.021f, 1f);

        readonly ZedCalibration m_Calibration = new ZedCalibration();
        Coroutine m_WebRtcUpdate;
        bool m_Recentered;

        public string WhepUrl => $"https://{serverHost}:{serverPort}/whep";
        public string CalibrationUrl => $"https://{serverHost}:{serverPort}/zedx.json";

        void Awake()
        {
#if !HTV_ZED_VIDEO
            // Not a video build. Disable before Start, so no WebRTC type is ever
            // touched and the passthrough setup below never runs.
            gameObject.SetActive(false);
#endif
        }

        void OnEnable()
        {
            if (hmdCamera == null)
                hmdCamera = Camera.main;

            TakeOverFromPassthrough();

            // Required by com.unity.webrtc: it refreshes every video track's texture
            // at end of frame from this coroutine. No frames arrive without it.
            m_WebRtcUpdate = StartCoroutine(WebRTC.Update());

            StartCoroutine(Boot());
        }

        void OnDisable()
        {
            if (m_WebRtcUpdate != null)
            {
                StopCoroutine(m_WebRtcUpdate);
                m_WebRtcUpdate = null;
            }
        }

        IEnumerator Boot()
        {
            yield return ZedCalibrationLoader.Fetch(CalibrationUrl, m_Calibration);

            if (panel != null)
            {
                panel.SetCalibration(m_Calibration);
                // Wire the camera before any frame arrives: the panel's follow
                // logic needs something to orbit around from its very first update,
                // and the fill-view solve reads the camera's stereo projection.
                panel.SetHead(hmdCamera);
            }

            if (whepClient != null)
            {
                whepClient.VideoTextureReceived += HandleVideoTexture;
                yield return ConnectLoop();
            }
            else
            {
                Debug.LogError("[ZedStream] no ZedWhepClient wired — nothing will be received.");
            }
        }

        IEnumerator ConnectLoop()
        {
            var wait = new WaitForSeconds(Mathf.Max(1f, reconnectSeconds));
            while (enabled)
            {
                if (!whepClient.IsConnected)
                {
                    whepClient.Connect(WhepUrl);

                    // Give the handshake a chance to finish before judging it.
                    float deadline = Time.realtimeSinceStartup +
                                     ZedWhepClient.IceGatherTimeoutSeconds +
                                     ZedWhepClient.HttpTimeoutSeconds;
                    while (!whepClient.IsConnected && Time.realtimeSinceStartup < deadline)
                        yield return null;

                    if (!whepClient.IsConnected)
                        Debug.LogWarning($"[ZedStream] not connected — retrying in " +
                                         $"{reconnectSeconds:0.#}s. Is `zedx_webrtc.sh --xr` " +
                                         $"running on {serverHost}?");
                }
                yield return wait;
            }
        }

        void HandleVideoTexture(Texture texture)
        {
            if (panel == null)
                return;

            panel.SetVideoTexture(texture);

            // Recenter on the first frame rather than at startup: head tracking is
            // usually not settled yet when the app launches, and the panel becoming
            // visible is the moment its placement starts to matter.
            if (!m_Recentered)
            {
                m_Recentered = true;
                Recenter();
            }
        }

        /// <summary>
        /// Snaps the panel straight ahead of where the operator is looking now. The
        /// panel eases after the gaze on its own (see ZedStereoPanel), so this is
        /// for the initial placement and for jumping it back without waiting out
        /// the easing.
        /// </summary>
        public void Recenter()
        {
            if (panel != null && hmdCamera != null)
                panel.RecenterOn(hmdCamera.transform);
        }

        /// <summary>
        /// Switches the camera from the passthrough configuration to an opaque one.
        ///
        /// The video panel and passthrough cannot share the screen: the surround is
        /// a dark grey rest frame, which a transparent clear would replace with the
        /// real room. Passthrough is also not free, so the components that drive it
        /// are switched off rather than merely covered up.
        ///
        /// Both are found by name instead of by type. This assembly deliberately
        /// references neither the publisher's assembly nor AR Foundation, and that
        /// isolation is worth more than the compile-time check — a rename here
        /// costs a dark screen in the video build, while a hard reference would put
        /// the pose publisher downstream of WebRTC.
        /// </summary>
        void TakeOverFromPassthrough()
        {
            if (hmdCamera == null)
            {
                Debug.LogError("[ZedStream] no camera found — cannot set up the view.");
                return;
            }

            hmdCamera.clearFlags = CameraClearFlags.SolidColor;
            hmdCamera.backgroundColor = clearColor;

            DisableByName(hmdCamera.gameObject, "PassthroughSetup");
            DisableByName(hmdCamera.gameObject, "ARCameraManager");
        }

        static void DisableByName(GameObject go, string componentName)
        {
            if (go.GetComponent(componentName) is Behaviour behaviour)
            {
                behaviour.enabled = false;
                Debug.Log($"[ZedStream] disabled {componentName} for the video view.");
            }
        }
    }
}
