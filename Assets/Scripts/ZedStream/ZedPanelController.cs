using System.Collections;
using HandTrackingViz;
using Unity.WebRTC;
using UnityEngine;

namespace ZedStream
{
    /// <summary>
    /// The 3D-view receiver behind the control panel's "3D view" switch.
    ///
    /// It follows <see cref="RemoteView"/> and nothing else: while the switch is on
    /// it connects to <see cref="RemoteView.Endpoint"/> (the address typed on the
    /// panel's keypad), keeps retrying until the source answers, and draws the
    /// stream on the stereo panel. Switching off, or typing a new address, tears
    /// the session down; the new address is connected to straight away.
    ///
    /// Passthrough is NOT handled here. The control panel switches it off before
    /// raising <see cref="RemoteView.Changed"/> and back on after, so the two can
    /// never disagree about who owns the camera's clear settings.
    ///
    /// Every source this app receives (ZED, C1, ...) speaks the same contract: WHEP
    /// at https://host:port/whep with a self-signed certificate, plus an optional
    /// calibration JSON next to it. A source without the JSON still streams; the
    /// panel just uses the built-in calibration.
    ///
    /// Progress is reported through <see cref="RemoteView.ReportStatus"/>, which the
    /// panel shows under the address, so a failure reads as "why" on the headset
    /// rather than as a dark screen.
    ///
    /// Dependency direction: this assembly references HandTrackingViz (for
    /// RemoteView); HandTrackingViz does not reference this one. The pose
    /// publisher therefore never depends on WebRTC, and a WebRTC failure cannot
    /// take the TCP stream down with it.
    /// </summary>
    public sealed class ZedPanelController : MonoBehaviour
    {
        [Tooltip("Re-attempt the WHEP handshake this often while disconnected. The " +
                 "headset is routinely started before the source, so a failed first " +
                 "attempt has to be survivable without toggling anything.")]
        public float reconnectSeconds = 5f;

        [Header("Scene")]
        [Tooltip("The HMD-driven camera. Defaults to Camera.main.")]
        public Camera hmdCamera;

        public ZedWhepClient whepClient;
        public ZedStereoPanel panel;
        public ZedRestFrame restFrame;

        [Tooltip("Dome for fisheye sources (the stereo viewer server). Streams on its " +
                 "/stereo_* WHEP paths go here; everything else goes to the ZED panel.")]
        public FisheyeDome dome;

        Coroutine m_Session;
        Coroutine m_WebRtcUpdate;
        string m_SessionEndpoint;   // "host:port" of the running session, or null
        bool m_Recentered;
        bool m_Started;

        void OnEnable()
        {
            if (hmdCamera == null)
                hmdCamera = Camera.main;

            if (whepClient != null)
                whepClient.VideoTextureReceived += HandleVideoTexture;
            else
                Debug.LogError("[ZedStream] no ZedWhepClient wired — nothing will be received.");

            RemoteView.Changed += Apply;
            // The first Apply waits for Start: the panel and rest frame bind their
            // renderers in Awake, which is not guaranteed to have run yet here.
            if (m_Started)
                Apply();
        }

        void Start()
        {
            m_Started = true;
            ShowVideoSurroundings(false);
            Apply();
        }

        void OnDisable()
        {
            RemoteView.Changed -= Apply;
            if (whepClient != null)
                whepClient.VideoTextureReceived -= HandleVideoTexture;
            StopSession();
        }

        /// <summary>
        /// Brings the session in line with what the panel asks for. Runs inside
        /// RemoteView.Changed, i.e. inside the panel's toggle handler, so nothing
        /// may escape from here — a receiver fault must never break the switch.
        /// </summary>
        void Apply()
        {
            try
            {
                ApplyUnguarded();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                RemoteView.ReportStatus("receiver error");
            }
        }

        void ApplyUnguarded()
        {
            if (!RemoteView.Active || whepClient == null)
            {
                StopSession();
                return;
            }

            RemoteViewEndpoint endpoint = RemoteView.Endpoint;
            if (m_Session != null && m_SessionEndpoint == endpoint.ToString())
                return;

            StopSession();
            StartSession(endpoint);
        }

        void StartSession(RemoteViewEndpoint endpoint)
        {
            m_SessionEndpoint = endpoint.ToString();
            m_Recentered = false;
            ShowVideoSurroundings(true);

            // Required by com.unity.webrtc: it refreshes every video track's texture
            // at end of frame from this coroutine. No frames arrive without it.
            m_WebRtcUpdate = StartCoroutine(WebRTC.Update());
            m_Session = StartCoroutine(Session(endpoint));
            Debug.Log($"[ZedStream] 3D view on — source {endpoint}");
        }

        void StopSession()
        {
            if (m_Session != null)
            {
                StopCoroutine(m_Session);
                m_Session = null;
                Debug.Log($"[ZedStream] 3D view session to {m_SessionEndpoint} stopped");
            }
            if (m_WebRtcUpdate != null)
            {
                StopCoroutine(m_WebRtcUpdate);
                m_WebRtcUpdate = null;
            }
            m_SessionEndpoint = null;

            if (whepClient != null)
                whepClient.Disconnect();
            ShowVideoSurroundings(false);
            RemoteView.ReportStatus(null);
        }

        IEnumerator Session(RemoteViewEndpoint endpoint)
        {
            // A fresh calibration per session: values from a previous source must
            // not leak into this one through FromJsonOverwrite's merge.
            var calibration = new ZedCalibration();
            bool calibrated = false;
            if (panel != null)
                panel.SetHead(hmdCamera);
            if (dome != null && hmdCamera != null)
                dome.SetHead(hmdCamera.transform);

            var wait = new WaitForSeconds(Mathf.Max(1f, reconnectSeconds));
            while (true)
            {
                if (!whepClient.IsConnected)
                {
                    RemoteView.ReportStatus("connecting...");

                    // Retried with the connection, because a source that is down
                    // fails both; once it answers, the JSON is fetched for real.
                    if (!calibrated)
                    {
                        yield return ZedCalibrationLoader.Fetch(endpoint.CalibrationUrl, calibration,
                                                                ok => calibrated = ok);
                        if (panel != null)
                            panel.SetCalibration(calibration);
                    }

                    string[] urls = endpoint.WhepUrls;
                    whepClient.Connect(urls);

                    // Give the handshake a chance to finish before judging it.
                    float deadline = Time.realtimeSinceStartup +
                                     ZedWhepClient.IceGatherTimeoutSeconds +
                                     ZedWhepClient.HttpTimeoutSeconds * urls.Length + 1f;
                    while (!whepClient.IsConnected && whepClient.IsConnecting &&
                           Time.realtimeSinceStartup < deadline)
                        yield return null;

                    if (!whepClient.IsConnected)
                    {
                        string why = whepClient.LastError ?? "no answer";
                        RemoteView.ReportStatus($"{why}, retrying");
                        Debug.LogWarning($"[ZedStream] not connected to {endpoint} ({why}) — " +
                                         $"retrying in {reconnectSeconds:0.#}s");
                    }
                    else if (!whepClient.HasVideo)
                    {
                        RemoteView.ReportStatus("connected, waiting for video");
                    }
                }
                yield return wait;
            }
        }

        /// <summary>How a stream is drawn.</summary>
        public enum ViewKind
        {
            /// <summary>Flat 1:1 stereo panel — the ZED (pinhole lenses, /whep).</summary>
            ZedPanel,
            /// <summary>Fisheye dome — the stereo viewer server (/stereo_*/whep).</summary>
            FisheyeDome,
        }

        /// <summary>
        /// Picks the view from the WHEP URL the session connected on. Only the stereo
        /// viewer's own paths go to the dome; /whep and anything unknown stay on the
        /// ZED panel, which is what this app drew before the dome existed.
        /// </summary>
        public static ViewKind ViewKindFor(string connectedUrl)
        {
            if (string.IsNullOrEmpty(connectedUrl) ||
                !System.Uri.TryCreate(connectedUrl, System.UriKind.Absolute, out System.Uri uri))
                return ViewKind.ZedPanel;
            return uri.AbsolutePath.StartsWith("/stereo_", System.StringComparison.Ordinal)
                ? ViewKind.FisheyeDome
                : ViewKind.ZedPanel;
        }

        void HandleVideoTexture(Texture texture)
        {
            if (texture != null)
                RemoteView.ReportStatus($"streaming {texture.width}x{texture.height}");

            ViewKind kind = ViewKindFor(whepClient != null ? whepClient.ConnectedUrl : null);
            if (kind == ViewKind.FisheyeDome && dome != null)
            {
                if (panel != null)
                    panel.SetVideoTexture(null);
                // The dome surrounds the viewer completely; the rest frame behind it
                // would only cost fill rate.
                if (restFrame != null)
                    restFrame.SetVisible(false);
                dome.SetVideoTexture(texture);
                if (!m_Recentered)
                {
                    m_Recentered = true;
                    dome.Recenter();
                }
                Debug.Log($"[ZedStream] view: fisheye dome ({whepClient.ConnectedUrl})");
                return;
            }

            if (dome != null)
                dome.SetVideoTexture(null);
            if (panel == null)
                return;

            panel.SetVideoTexture(texture);
            Debug.Log($"[ZedStream] view: ZED panel ({(whepClient != null ? whepClient.ConnectedUrl : "?")})");

            // Recenter on the first frame rather than at switch-on: the panel
            // becoming visible is the moment its placement starts to matter.
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
        /// The panel shows only once a frame arrives (ZedStereoPanel hides itself
        /// until then); the dark rest frame shows for the whole session, so the
        /// operator sees the video space is up even before the source answers.
        /// </summary>
        void ShowVideoSurroundings(bool on)
        {
            if (restFrame != null)
                restFrame.SetVisible(on);
            if (!on && panel != null)
                panel.SetVideoTexture(null);
            if (!on && dome != null)
                dome.SetVideoTexture(null);
        }
    }
}
