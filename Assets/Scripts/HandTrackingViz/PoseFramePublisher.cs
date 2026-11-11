using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;

namespace HandTrackingViz
{
    /// <summary>
    /// The single publisher for the whole device state — HMD pose, both hands'
    /// palm + 21 MediaPipe landmarks, both controllers' pose/inputs, and the
    /// 18-joint upper-body skeleton from Meta's Inside-Out Body Tracking. It
    /// matches the wire format in SCHEMA.md exactly:
    ///
    ///   Transport : TCP server socket (this app is the server), NDJSON —
    ///               one JSON object per line, '\n' terminated, UTF-8.
    ///   Port      : 5555 (Inspector-configurable).
    ///   Sockets   : TCP_NODELAY = true, SendTimeout = 2000 ms.
    ///   Model     : publish-and-forget broadcast — the newest frame is written
    ///               to every connected client; slow/broken clients are dropped
    ///               (their frames are lost). The server only writes, never reads.
    ///
    /// Rate (SCHEMA.md):
    ///   - One JSON line is built PER RENDER FRAME on the main thread (Update)
    ///     and handed to the send thread (latest-value). At the Quest frame
    ///     rate this is ~72 Hz. Even an all-untracked frame is emitted.
    ///   - maxPublishHz caps that: 0 = every frame; > 0 enforces a 1/maxPublishHz
    ///     minimum interval (Time.unscaledTime based).
    ///   - A background send thread wakes on an AutoResetEvent and writes only the
    ///     MOST RECENT payload, so bursts coalesce (latest-state, no lag build-up).
    ///
    /// Contract:
    ///   - All positions are meters. Poses are sampled in Unity world space
    ///     (left-handed, Y-up) but are converted AT SERIALIZATION TIME into the
    ///     wire frame "robot_world" — RIGHT-HANDED, +X forward, +Y left, +Z up
    ///     (FLU axis convention, ROS REP-103 style). +X forward is the XR
    ///     reference space's initial facing direction (where the user looked at
    ///     app start / recenter).
    ///       position   : (x, y, z)_unity     -> (z, -x, y)
    ///       quaternion : (x, y, z, w)_unity  -> (-z, x, -y, w)
    ///     Rotation is a quaternion in (x, y, z, w) order.
    ///   - hands is always 2 elements, order [left, right], tagged with "side".
    ///     Untracked hand -> {"side": ..., "tracked": false} (no palm/landmarks).
    ///   - controllers is likewise always 2 elements, [left, right]. Hands and
    ///     controllers are INDEPENDENT — both, either, or neither may be tracked
    ///     in a given frame; the receiver picks whichever source it wants.
    ///   - body carries the upper-body skeleton as 18 joints (torso + arms), a
    ///     third independent source on the same footing as hands and controllers.
    ///     Its joints stop at the wrist twist because the hands are already
    ///     published in full detail under "hands".
    ///   - Untracked HMD -> {"tracked": false}; a landmark, palm or controller
    ///     pose with "valid": false omits its position/rotation.
    ///   - Floats are formatted "0.######" (&lt;=6 decimals, InvariantCulture).
    ///
    /// No external libraries — pure .NET sockets — so it builds cleanly for Quest
    /// (Android / IL2CPP). Requires the INTERNET permission (Player Settings ->
    /// Internet Access = Require; enabled in this project).
    /// </summary>
    public sealed class PoseFramePublisher : MonoBehaviour
    {
        public const string CoordinateSystem = "robot_world";

        [Header("Tracking")]
        [Tooltip("XR Origin transform — the same one wired into HandVisualizer. Hand joint poses are " +
                 "converted to Unity world space through it. When null, poses are treated as already in world space.")]
        public Transform trackingOrigin;

        [Tooltip("The HMD-driven camera. Its world transform is published as the HMD SE(3) pose. " +
                 "Defaults to Camera.main when left empty.")]
        public Camera hmdCamera;

        [Header("Network (TCP server)")]
        [Tooltip("TCP port the publisher listens on. Receivers connect to tcp://<quest-ip>:<port>.")]
        public int port = 5555;

        [Tooltip("0 = publish every render frame. > 0 caps the publish rate to this many Hz.")]
        public int maxPublishHz = 0;

        [Tooltip("Log client connect/disconnect events to the Unity console.")]
        public bool logConnections = true;

        [Header("Palm pose")]
        public HandLandmarkProvider.PalmPoseSource palmPoseSource = HandLandmarkProvider.PalmPoseSource.OpenXRPalmJoint;

        [Header("Controllers")]
        [Tooltip("Sample and publish the hand-held controllers alongside the hands. " +
                 "Turning this off publishes both controllers as tracked:false — it does " +
                 "not remove the \"controllers\" field, so the schema stays stable.")]
        public bool publishControllers = true;

        [Header("Body (Inside-Out Body Tracking)")]
        [Tooltip("Sample and publish the 18-joint upper-body skeleton alongside the hands. " +
                 "Turning this off publishes the body as tracked:false — it does not remove " +
                 "the \"body\" field, so the schema stays stable. Requires the body-tracking " +
                 "OpenXR feature to be enabled; the user is asked for the body-tracking " +
                 "permission on the first frame this is needed.")]
        public bool publishBody = true;

        [Header("Status HUD")]
        [Tooltip("Show four head-locked dots in the top-centre of the view: HMD, left, " +
                 "right (hand or controller) and receiver connection. Green = tracked / " +
                 "connected, red = missing.")]
        public bool showStatusHud = true;

        [Header("Control panel")]
        [Tooltip("Show a gear button above the status dots. It opens a panel just below " +
                 "the dots with two switches: IP (shows <headset-ip>:<port> for receivers " +
                 "to connect to) and 3D view (turns passthrough off for a ZED/C1 stereo " +
                 "source, whose IP is entered on a keypad). Both start off. Poke with a " +
                 "fingertip or point a controller and pull the trigger.")]
        public bool showControlPanel = true;

        [Header("Debug")]
        public bool logPublishCount = false;
        public float logIntervalSeconds = 5f;

        static readonly IFormatProvider Inv = CultureInfo.InvariantCulture;
        const string NumFmt = "0.######";   // <= 6 decimals, trailing zeros trimmed

        readonly HandLandmarkProvider m_Provider = new HandLandmarkProvider();
        readonly ControllerSampler m_ControllerSampler = new ControllerSampler();
        HandLandmarkFrame m_Left;
        HandLandmarkFrame m_Right;
        bool m_LeftTracked;
        bool m_RightTracked;
        readonly ControllerData m_LeftController = new ControllerData();
        readonly ControllerData m_RightController = new ControllerData();
        readonly BodyLandmarkProvider m_BodyProvider = new BodyLandmarkProvider();
        readonly BodyLandmarkFrame m_Body = new BodyLandmarkFrame();
        StatusIndicator m_StatusHud;
        bool m_ControllerSamplingFailed;
        bool m_BodySamplingFailed;

        // Reused each frame (built on the main thread only).
        readonly StringBuilder m_Sb = new StringBuilder(4096);
        float m_LastPublishTime = -999f;
        long m_PublishCounter;
        float m_LastLogTime;

        // Latest-value payload hand-off: main thread writes, send thread reads.
        readonly object m_PayloadLock = new object();
        byte[] m_LatestPayload;                // UTF-8 JSON line (incl. '\n')
        readonly AutoResetEvent m_PayloadReady = new AutoResetEvent(false);

        // Connected clients (publish-and-forget broadcast targets).
        readonly object m_ClientsLock = new object();
        readonly List<TcpClient> m_Clients = new List<TcpClient>();
        readonly List<TcpClient> m_SendScratch = new List<TcpClient>();

        TcpListener m_Listener;
        Thread m_AcceptThread;
        Thread m_SendThread;
        volatile bool m_Running;

        /// <summary>
        /// Number of currently connected TCP subscribers. Thread-safe; feeds the
        /// on-device status HUD's RECEIVER dot. Broken clients are dropped by the
        /// send loop on write failure (~SendTimeout = 2 s), so this decays to 0
        /// shortly after the last receiver goes away.
        /// </summary>
        public int ConnectedClientCount
        {
            get { lock (m_ClientsLock) { return m_Clients.Count; } }
        }

        void Awake()
        {
            m_Left = new HandLandmarkFrame(HandSide.Left);
            m_Right = new HandLandmarkFrame(HandSide.Right);
            if (hmdCamera == null)
                hmdCamera = Camera.main;
        }

        void Start()
        {
            if (showStatusHud && hmdCamera != null)
                m_StatusHud = StatusIndicator.Attach(hmdCamera.transform);
            if (showControlPanel && hmdCamera != null)
                ControlPanel.Attach(hmdCamera, trackingOrigin, port);
        }

        void OnEnable()
        {
            try
            {
                m_Listener = new TcpListener(IPAddress.Any, port);
                m_Listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"[PoseFramePublisher] failed to bind TCP :{port} — {e.Message}. " +
                               "Is the INTERNET permission granted and the port free?");
                m_Listener = null;
                return;
            }

            m_Running = true;
            m_AcceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PoseFramePublisher.Accept" };
            m_AcceptThread.Start();
            m_SendThread = new Thread(SendLoop) { IsBackground = true, Name = "PoseFramePublisher.Send" };
            m_SendThread.Start();

            Debug.Log($"[PoseFramePublisher] TCP NDJSON server listening on 0.0.0.0:{port}.");
        }

        void OnDisable()
        {
            m_Running = false;
            m_PayloadReady.Set();                     // wake the send thread so it can exit
            try { m_Listener?.Stop(); } catch { /* ignore */ }   // unblocks AcceptTcpClient
            try { m_AcceptThread?.Join(500); } catch { /* ignore */ }
            try { m_SendThread?.Join(500); } catch { /* ignore */ }

            lock (m_ClientsLock)
            {
                foreach (var c in m_Clients) { try { c.Close(); } catch { /* ignore */ } }
                m_Clients.Clear();
            }
            m_AcceptThread = null;
            m_SendThread = null;
            m_Listener = null;
        }

        void Update()
        {
            // Sampling runs every frame regardless of the rate cap so the HUD
            // mirrors the live state; only the network hand-off is capped.
            m_Provider.palmPoseSource = palmPoseSource;
            m_LeftTracked = m_Provider.TryUpdate(HandSide.Left, trackingOrigin, m_Left);
            m_RightTracked = m_Provider.TryUpdate(HandSide.Right, trackingOrigin, m_Right);

            if (hmdCamera == null)
                hmdCamera = Camera.main;
            PoseSE3 hmd = SampleHmdPose();

            // Controllers are sampled alongside the hands, not instead of them:
            // the runtime can report both, and the receiver picks whichever
            // source is tracked. Sampling is unconditional so a frame with no
            // controller still says so explicitly.
            //
            // Guarded because this runs BEFORE the payload hand-off: an exception
            // escaping here would abort Update() every frame, and the publisher
            // would then accept TCP clients (background thread) while never
            // sending a byte — a silent, confusing failure. Controller trouble
            // must cost the controllers, not the whole stream.
            try
            {
                if (publishControllers)
                    m_ControllerSampler.Fill(m_LeftController, m_RightController, trackingOrigin);
                else
                    ControllerSampler.MarkUntracked(m_LeftController, m_RightController);
            }
            catch (Exception e)
            {
                ControllerSampler.MarkUntracked(m_LeftController, m_RightController);
                if (!m_ControllerSamplingFailed)
                {
                    m_ControllerSamplingFailed = true;   // log once, not every frame
                    Debug.LogError($"[PoseFramePublisher] controller sampling failed, publishing them as " +
                                   $"untracked from here on. Hands/HMD are unaffected. {e}");
                }
            }

            // The body is a third independent source, sampled on the same terms as
            // the controllers: unconditionally, so an untracked frame says so, and
            // guarded so that trouble in the body-tracking extension costs the body
            // alone rather than aborting Update() and silently killing the stream.
            try
            {
                if (publishBody)
                    m_BodyProvider.TryUpdate(trackingOrigin, m_Body);
                else
                    m_Body.isTracked = false;
            }
            catch (Exception e)
            {
                m_Body.isTracked = false;
                if (!m_BodySamplingFailed)
                {
                    m_BodySamplingFailed = true;   // log once, not every frame
                    Debug.LogError($"[PoseFramePublisher] body sampling failed, publishing the body as " +
                                   $"untracked from here on. Hands/HMD/controllers are unaffected. {e}");
                }
            }

            // The HUD mirrors exactly what is being published: a dot is green only
            // if the receiver sees that source as tracked (left/right count either
            // the hand or the controller), and RECEIVER is green while at least
            // one TCP subscriber is connected.
            if (m_StatusHud != null)
                m_StatusHud.SetStates(
                    hmd.tracked,
                    m_LeftTracked || m_LeftController.tracked,
                    m_RightTracked || m_RightController.tracked,
                    ConnectedClientCount > 0);

            if (!m_Running)
                return;

            if (maxPublishHz > 0)
            {
                float minInterval = 1f / maxPublishHz;
                if (Time.unscaledTime - m_LastPublishTime < minInterval) return;
            }
            m_LastPublishTime = Time.unscaledTime;

            byte[] bytes = Encoding.UTF8.GetBytes(BuildJson(hmd));
            lock (m_PayloadLock) { m_LatestPayload = bytes; }
            m_PayloadReady.Set();

            if (logPublishCount && Time.time - m_LastLogTime >= logIntervalSeconds)
            {
                m_LastLogTime = Time.time;
                Debug.Log($"[PoseFramePublisher] {Interlocked.Read(ref m_PublishCounter)} frames sent, " +
                          $"{ConnectedClientCount} client(s).");
            }
        }

        // HMD pose is read from the XR camera transform, which Unity's XR
        // subsystem updates every frame from the active OpenXR reference space.
        // Camera.transform values are already in Unity world space (left-handed,
        // Y-up, meters).
        //
        // The transform always holds SOME value — it just goes stale when the
        // runtime loses head tracking — so `tracked` is gated on the head device's
        // own isTracked flag when the runtime exposes one. Without this, the
        // wire's hmd.tracked (and the HUD's HMD dot) could never report a loss.
        PoseSE3 SampleHmdPose()
        {
            if (hmdCamera == null) return PoseSE3.NotTracked;

            bool tracked = true;
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool live))
                tracked = live;

            return new PoseSE3
            {
                tracked = tracked,
                position = hmdCamera.transform.position,
                rotation = hmdCamera.transform.rotation
            };
        }

        // ----------------------------------------------------------------
        // Networking threads
        // ----------------------------------------------------------------
        void AcceptLoop()
        {
            while (m_Running)
            {
                TcpClient client;
                try { client = m_Listener.AcceptTcpClient(); }
                catch { break; }   // listener stopped (shutdown) or faulted
                try
                {
                    client.NoDelay = true;
                    client.SendTimeout = 2000;
                }
                catch { /* best effort */ }
                lock (m_ClientsLock) { m_Clients.Add(client); }
                if (logConnections)
                    Debug.Log($"[PoseFramePublisher] client connected: {SafeRemote(client)}");
            }
        }

        void SendLoop()
        {
            while (m_Running)
            {
                m_PayloadReady.WaitOne();
                if (!m_Running) break;

                byte[] payload;
                lock (m_PayloadLock) { payload = m_LatestPayload; }
                if (payload == null) continue;

                m_SendScratch.Clear();
                lock (m_ClientsLock) { m_SendScratch.AddRange(m_Clients); }
                if (m_SendScratch.Count == 0) continue;

                foreach (var c in m_SendScratch)
                {
                    try
                    {
                        NetworkStream stream = c.GetStream();
                        stream.Write(payload, 0, payload.Length);
                    }
                    catch
                    {
                        RemoveClient(c);   // slow/broken client -> drop it
                    }
                }
                Interlocked.Increment(ref m_PublishCounter);
            }
        }

        void RemoveClient(TcpClient c)
        {
            lock (m_ClientsLock) { m_Clients.Remove(c); }
            try { c.Close(); } catch { /* ignore */ }
            if (logConnections)
                Debug.Log("[PoseFramePublisher] client dropped.");
        }

        static string SafeRemote(TcpClient c)
        {
            try { return c.Client.RemoteEndPoint?.ToString() ?? "?"; }
            catch { return "?"; }
        }

        // ----------------------------------------------------------------
        // JSON (built on the main thread, reusing `m_Sb`)
        // ----------------------------------------------------------------
        string BuildJson(PoseSE3 hmd)
        {
            var sb = m_Sb;
            sb.Clear();
            sb.Append('{');
            sb.Append("\"timestamp\":").Append(Time.timeAsDouble.ToString(NumFmt, Inv)).Append(',');
            sb.Append("\"frame\":").Append(Time.frameCount.ToString(Inv)).Append(',');
            sb.Append("\"coordinate_system\":\"").Append(CoordinateSystem).Append("\",");

            // hmd: { "tracked": bool, ["position", "rotation"] }
            sb.Append("\"hmd\":");
            AppendHmd(sb, hmd);
            sb.Append(',');

            // hands: exactly 2, order [left, right], each tagged with "side".
            sb.Append("\"hands\":[");
            AppendHand(sb, "left", m_Left, m_LeftTracked);
            sb.Append(',');
            AppendHand(sb, "right", m_Right, m_RightTracked);
            sb.Append("],");

            // controllers: exactly 2, order [left, right], same shape contract as
            // hands. Additive — a consumer that only reads "hands" is unaffected.
            sb.Append("\"controllers\":[");
            AppendController(sb, "left", m_LeftController);
            sb.Append(',');
            AppendController(sb, "right", m_RightController);
            sb.Append("],");

            // body: the upper-body skeleton, last so that every field a pre-body
            // receiver knows keeps its position in the object. Also additive — a
            // consumer that only reads hands/controllers is unaffected.
            sb.Append("\"body\":");
            AppendBody(sb, m_Body);

            sb.Append('}');
            sb.Append('\n');   // NDJSON line terminator
            return sb.ToString();
        }

        static void AppendHmd(StringBuilder sb, PoseSE3 hmd)
        {
            sb.Append("{\"tracked\":").Append(hmd.tracked ? "true" : "false");
            if (hmd.tracked)
            {
                sb.Append(",\"position\":"); AppendVec3(sb, hmd.position);
                sb.Append(",\"rotation\":"); AppendQuat(sb, hmd.rotation);
            }
            sb.Append('}');
        }

        static void AppendHand(StringBuilder sb, string side, HandLandmarkFrame frame, bool tracked)
        {
            sb.Append("{\"side\":\"").Append(side).Append("\",\"tracked\":")
              .Append(tracked ? "true" : "false");
            if (!tracked) { sb.Append('}'); return; }

            // palm: { "valid": bool, ["position", "rotation"] }
            sb.Append(",\"palm\":{\"valid\":").Append(frame.palmValid ? "true" : "false");
            if (frame.palmValid)
            {
                sb.Append(",\"position\":"); AppendVec3(sb, frame.palmPose.position);
                sb.Append(",\"rotation\":"); AppendQuat(sb, frame.palmPose.rotation);
            }
            sb.Append('}');

            // landmarks: exactly 21, objects { "i", "valid", ["position", "rotation"] }
            sb.Append(",\"landmarks\":[");
            for (int i = 0; i < HandLandmarkMapping.LandmarkCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i.ToString(Inv))
                  .Append(",\"valid\":").Append(frame.valid[i] ? "true" : "false");
                if (frame.valid[i])
                {
                    sb.Append(",\"position\":"); AppendVec3(sb, frame.positions[i]);
                    sb.Append(",\"rotation\":"); AppendQuat(sb, frame.rotations[i]);
                }
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
        }

        // One controller: the handle pose + analog/digital inputs. Mirrors
        // AppendHand's contract — an untracked controller is {"side",
        // "tracked":false} with nothing else, and an invalid pose omits
        // position/rotation.
        //
        // Wire names are teleop-facing (see ControllerData in PoseSnapshot.cs):
        // "controller" is the handle POSE, "grip_trigger" is the INDEX trigger
        // (drives the gripper downstream) and "ctrl_trigger" is the hardware
        // grip/squeeze axis. See the controllers section in SCHEMA.md.
        static void AppendController(StringBuilder sb, string side, ControllerData c)
        {
            sb.Append("{\"side\":\"").Append(side).Append("\",\"tracked\":")
              .Append(c.tracked ? "true" : "false");
            if (!c.tracked) { sb.Append('}'); return; }

            sb.Append(",\"controller\":"); AppendValidPose(sb, c.pose);

            sb.Append(",\"grip_trigger\":").Append(c.gripTrigger.ToString(NumFmt, Inv));
            sb.Append(",\"ctrl_trigger\":").Append(c.ctrlTrigger.ToString(NumFmt, Inv));
            sb.Append(",\"thumbstick\":"); AppendVec2(sb, c.thumbstick);

            sb.Append(",\"buttons\":{\"primary\":").Append(c.primaryButton ? "true" : "false")
              .Append(",\"secondary\":").Append(c.secondaryButton ? "true" : "false")
              .Append(",\"menu\":").Append(c.menuButton ? "true" : "false")
              .Append(",\"thumbstick\":").Append(c.thumbstickClicked ? "true" : "false")
              .Append('}');

            sb.Append('}');
        }

        // The upper body: 18 joints (torso + arms), plus how the runtime is tracking
        // them. Same contract as hands and controllers — an untracked body is
        // {"tracked":false} with nothing else, and a joint that could not be located
        // this frame omits its position/rotation.
        //
        // "fidelity" tells the receiver WHICH tracking mode produced these joints:
        // "high" is Meta's Inside-Out Body Tracking (headset cameras watching the
        // real arms), "low" is the inferred fallback, and "unspecified" means the
        // runtime chose without being asked. A receiver that cares about arm accuracy
        // should treat "low" as a much weaker signal than "high".
        static void AppendBody(StringBuilder sb, BodyLandmarkFrame body)
        {
            sb.Append("{\"tracked\":").Append(body.isTracked ? "true" : "false");
            if (!body.isTracked) { sb.Append('}'); return; }

            sb.Append(",\"fidelity\":\"").Append(FidelityName(body.fidelity)).Append('"');
            sb.Append(",\"confidence\":").Append(body.confidence.ToString(NumFmt, Inv));

            // joints: exactly 18, objects { "i", "valid", ["position", "rotation"] } —
            // deliberately the same element shape as a hand landmark.
            sb.Append(",\"joints\":[");
            for (int i = 0; i < BodyJointMapping.JointCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i.ToString(Inv))
                  .Append(",\"valid\":").Append(body.valid[i] ? "true" : "false");
                if (body.valid[i])
                {
                    sb.Append(",\"position\":"); AppendVec3(sb, body.positions[i]);
                    sb.Append(",\"rotation\":"); AppendQuat(sb, body.rotations[i]);
                }
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
        }

        static string FidelityName(BodyTrackingFidelity fidelity)
        {
            switch (fidelity)
            {
                case BodyTrackingFidelity.High: return "high";
                case BodyTrackingFidelity.Low: return "low";
                default: return "unspecified";
            }
        }

        // A pose carried as { "valid": bool, ["position", "rotation"] } — the
        // same shape the palm and each landmark use.
        static void AppendValidPose(StringBuilder sb, PoseSE3 p)
        {
            sb.Append("{\"valid\":").Append(p.tracked ? "true" : "false");
            if (p.tracked)
            {
                sb.Append(",\"position\":"); AppendVec3(sb, p.position);
                sb.Append(",\"rotation\":"); AppendQuat(sb, p.rotation);
            }
            sb.Append('}');
        }

        // World rotation as a [x, y, z, w] array in the robot_world wire frame.
        // Basis change from Unity (left-handed, Y-up) to FLU (right-handed,
        // +X forward, +Y left, +Z up): (x, y, z, w) -> (-z, x, -y, w).
        static void AppendQuat(StringBuilder sb, Quaternion q)
        {
            sb.Append('[');
            sb.Append((-q.z).ToString(NumFmt, Inv)).Append(',');
            sb.Append(q.x.ToString(NumFmt, Inv)).Append(',');
            sb.Append((-q.y).ToString(NumFmt, Inv)).Append(',');
            sb.Append(q.w.ToString(NumFmt, Inv));
            sb.Append(']');
        }

        // World position as a [x, y, z] array (meters) in the robot_world wire
        // frame. Same basis change as AppendQuat: (x, y, z) -> (z, -x, y).
        static void AppendVec3(StringBuilder sb, Vector3 v)
        {
            sb.Append('[');
            sb.Append(v.z.ToString(NumFmt, Inv)).Append(',');
            sb.Append((-v.x).ToString(NumFmt, Inv)).Append(',');
            sb.Append(v.y.ToString(NumFmt, Inv));
            sb.Append(']');
        }

        // Vector2 as an [x, y] array (unitless axis, each -1..1).
        static void AppendVec2(StringBuilder sb, Vector2 v)
        {
            sb.Append('[');
            sb.Append(v.x.ToString(NumFmt, Inv)).Append(',');
            sb.Append(v.y.ToString(NumFmt, Inv));
            sb.Append(']');
        }
    }
}
