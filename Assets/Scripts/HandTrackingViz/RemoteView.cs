using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace HandTrackingViz
{
    // ====================================================================
    // RemoteViewEndpoint
    // --------------------------------------------------------------------
    // Where a 3D view (ZED, C1, ...) is served from. Every source this app is
    // meant to receive uses the same WHEP-over-HTTPS contract as the ZED
    // build: the HEADSET connects to the SOURCE, so the headset must be told
    // the source's address — that is what the control panel's keypad sets.
    // ====================================================================
    public readonly struct RemoteViewEndpoint
    {
        public readonly string Host;
        public readonly int Port;

        public RemoteViewEndpoint(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public string WhepUrl => $"https://{Host}:{Port}/whep";
        public string CalibrationUrl => $"https://{Host}:{Port}/zedx.json";

        public override string ToString() => $"{Host}:{Port}";

        /// <summary>
        /// Parses "a.b.c.d" or "a.b.c.d:port" as typed on the keypad. Only
        /// IPv4 literals are accepted — the keypad has no letters, and a
        /// hostname would need DNS the robot LAN usually does not have.
        /// </summary>
        public static bool TryParse(string text, int defaultPort,
                                    out RemoteViewEndpoint endpoint, out string error)
        {
            endpoint = default;
            text = (text ?? "").Trim();

            string hostPart = text;
            int port = defaultPort;

            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                hostPart = text.Substring(0, colon);
                string portPart = text.Substring(colon + 1);
                if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    || port < 1 || port > 65535)
                {
                    error = "Port must be 1-65535";
                    return false;
                }
            }

            string[] parts = hostPart.Split('.');
            if (parts.Length != 4)
            {
                error = "IP must be a.b.c.d";
                return false;
            }

            var octets = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 3
                    || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out octets[i])
                    || octets[i] > 255)
                {
                    error = "Each IP number must be 0-255";
                    return false;
                }
            }

            // Re-joined from the parsed numbers, so "192.168.020.5" is stored
            // as "192.168.20.5" — one canonical spelling per address.
            endpoint = new RemoteViewEndpoint(string.Join(".", octets), port);
            error = null;
            return true;
        }
    }

    // ====================================================================
    // RemoteView
    // --------------------------------------------------------------------
    // The hand-off point between the control panel and a 3D-view receiver.
    //
    // The panel owns the user's intent (3D view on/off, which source); a
    // receiver owns the connection. They meet only here, so a receiver can be
    // added later (the ZED WHEP client lives on feature/zed_streaming) without
    // the panel knowing anything about WebRTC, and without the receiver
    // knowing anything about the panel:
    //
    //   RemoteView.Changed += () => {
    //       if (RemoteView.Active) Connect(RemoteView.Endpoint.WhepUrl);
    //       else Disconnect();
    //   };
    //   RemoteView.ReportStatus("connected");
    //
    // This build ships NO receiver, so turning the 3D view on only switches
    // passthrough off and waits; the panel says so ("no receiver").
    //
    // The endpoint persists across launches (PlayerPrefs). Active does not —
    // the app always starts with the 3D view off.
    // ====================================================================
    public static class RemoteView
    {
        // The ZED rig's Jetson on the lab LAN — the same default
        // ZedPanelController ships with on feature/zed_streaming.
        public const string DefaultHost = "192.168.20.131";
        public const int DefaultPort = 8443;

        private const string HostKey = "HandTrackingViz.RemoteView.Host";
        private const string PortKey = "HandTrackingViz.RemoteView.Port";

        private static bool s_Loaded;
        private static RemoteViewEndpoint s_Endpoint;
        private static bool s_UserSet;

        /// <summary>True while the user has the 3D view toggle on.</summary>
        public static bool Active { get; private set; }

        /// <summary>The source to receive from. Never null-hosted.</summary>
        public static RemoteViewEndpoint Endpoint
        {
            get { Load(); return s_Endpoint; }
        }

        /// <summary>False until an address has been entered and saved at least
        /// once — the panel opens the keypad on first use instead of silently
        /// trying a default that is probably wrong for this network.</summary>
        public static bool EndpointWasSetByUser
        {
            get { Load(); return s_UserSet; }
        }

        /// <summary>Last status reported by a receiver, or null if none has.</summary>
        public static string ReceiverStatus { get; private set; }

        /// <summary>Raised on the main thread when Active or Endpoint changes.</summary>
        public static event Action Changed;

        /// <summary>Raised when a receiver reports a new status string.</summary>
        public static event Action StatusChanged;

        public static void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            Changed?.Invoke();
        }

        public static void SetEndpoint(RemoteViewEndpoint endpoint)
        {
            Load();
            bool same = s_UserSet && s_Endpoint.Host == endpoint.Host && s_Endpoint.Port == endpoint.Port;
            s_Endpoint = endpoint;
            s_UserSet = true;
            PlayerPrefs.SetString(HostKey, endpoint.Host);
            PlayerPrefs.SetInt(PortKey, endpoint.Port);
            PlayerPrefs.Save();
            if (!same) Changed?.Invoke();
        }

        /// <summary>For receivers: a short human-readable state ("connecting",
        /// "connected", "no answer from host", ...) shown on the panel.</summary>
        public static void ReportStatus(string status)
        {
            if (ReceiverStatus == status) return;
            ReceiverStatus = status;
            StatusChanged?.Invoke();
        }

        private static void Load()
        {
            if (s_Loaded) return;
            s_Loaded = true;

            string host = PlayerPrefs.GetString(HostKey, "");
            int port = PlayerPrefs.GetInt(PortKey, DefaultPort);
            // Re-validate what was stored rather than trusting it: a value from
            // an older build or a hand-edited prefs file must not reach a URL.
            if (host.Length > 0 &&
                RemoteViewEndpoint.TryParse($"{host}:{port}", DefaultPort, out RemoteViewEndpoint stored, out _))
            {
                s_Endpoint = stored;
                s_UserSet = true;
            }
            else
            {
                s_Endpoint = new RemoteViewEndpoint(DefaultHost, DefaultPort);
                s_UserSet = false;
            }
        }

        // Static state survives "Enter Play Mode" without a domain reload, which
        // would start a second session with the 3D view already on.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Loaded = false;
            s_UserSet = false;
            Active = false;
            ReceiverStatus = null;
            Changed = null;
            StatusChanged = null;
        }
    }

    // ====================================================================
    // PassthroughSwitch
    // --------------------------------------------------------------------
    // Turns Meta passthrough off and back on at runtime.
    //
    // Passthrough runs exactly while the ARCameraManager on the XR camera is
    // enabled (Meta OpenXR's documented on/off switch), so that is the switch.
    // With passthrough gone the transparent clear PassthroughSetup sets would
    // show as plain black, so the camera clears to an opaque near-black
    // instead — the same surround the ZED build uses behind its video panel.
    // Turning passthrough back on restores the camera's previous clear.
    // ====================================================================
    public sealed class PassthroughSwitch
    {
        public static readonly Color OpaqueBackground = new Color(0.02f, 0.02f, 0.021f, 1f);

        private readonly Camera camera;
        private readonly ARCameraManager cameraManager;
        private CameraClearFlags savedClearFlags;
        private Color savedBackground;
        private bool passthroughOff;

        public PassthroughSwitch(Camera camera)
        {
            this.camera = camera;
            cameraManager = camera != null ? camera.GetComponent<ARCameraManager>() : null;
            if (cameraManager == null)
                cameraManager = UnityEngine.Object.FindFirstObjectByType<ARCameraManager>();
            if (cameraManager == null)
                Debug.LogWarning("[PassthroughSwitch] no ARCameraManager in the scene — passthrough " +
                                 "cannot be switched, only the camera background will change.");
        }

        public bool PassthroughOn => !passthroughOff;

        public void SetPassthrough(bool on)
        {
            if (on == !passthroughOff) return;

            if (!on)
            {
                if (camera != null)
                {
                    savedClearFlags = camera.clearFlags;
                    savedBackground = camera.backgroundColor;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = OpaqueBackground;
                }
                if (cameraManager != null) cameraManager.enabled = false;
                passthroughOff = true;
            }
            else
            {
                if (cameraManager != null) cameraManager.enabled = true;
                if (camera != null)
                {
                    camera.clearFlags = savedClearFlags;
                    camera.backgroundColor = savedBackground;
                }
                passthroughOff = false;
            }
        }
    }
}
