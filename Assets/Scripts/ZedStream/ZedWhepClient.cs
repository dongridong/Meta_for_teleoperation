using System;
using System.Collections;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

namespace ZedStream
{
    /// <summary>
    /// Receives the Jetson's SBS H.264 stream over WebRTC, signalled with WHEP.
    ///
    /// This is a direct port of connect() in the reference page
    /// (zedx/web/index.html) — that page is the ground truth for how this system
    /// behaves, so where the two differ, it is right and this is wrong.
    ///
    /// WHEP is a single HTTP POST: offer SDP up, answer SDP back. There is no
    /// signalling server to run and no WebSocket to keep alive.
    ///
    ///   1. RTCPeerConnection with NO ICE servers (same LAN, no NAT to traverse)
    ///   2. AddTransceiver(video, RecvOnly) — this app only ever receives
    ///   3. CreateOffer -> SetLocalDescription
    ///   4. WAIT for ICE gathering to complete — trickle is deliberately not used.
    ///      On a LAN gathering finishes almost immediately, and one round trip
    ///      means one place for the connection to fail rather than several.
    ///   5. POST the offer, Content-Type: application/sdp
    ///   6. 201 + answer SDP -> SetRemoteDescription. The Location header is the
    ///      session's own address, which is DELETEd on shutdown so the server does
    ///      not keep feeding a peer that is gone.
    ///
    /// The decoded frames arrive as a <see cref="Texture"/> through
    /// <see cref="VideoTextureReceived"/>. Nothing here knows what is done with
    /// them — the stereo split, the HIT shift and the panel geometry all live in
    /// <see cref="ZedStereoPanel"/>.
    /// </summary>
    public sealed class ZedWhepClient : MonoBehaviour
    {
        /// <summary>
        /// How long to wait for ICE candidate gathering before POSTing anyway.
        /// Matches the reference page's 3 s: on a LAN this never fires, but a
        /// runtime that never reports Complete must not hang the connection.
        /// </summary>
        public const float IceGatherTimeoutSeconds = 3f;

        public const int HttpTimeoutSeconds = 10;

        /// <summary>
        /// Raised on the main thread whenever the decoder hands us a texture. It
        /// can fire more than once — the runtime is free to hand over a different
        /// texture object, e.g. after a resolution change — so subscribers must
        /// rebind rather than cache the first one and assume it lives forever.
        /// </summary>
        public event Action<Texture> VideoTextureReceived;

        /// <summary>True once the answer has been applied. Not a guarantee that
        /// frames are flowing — that is what <see cref="HasVideo"/> is for.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>True once at least one decoded texture has arrived.</summary>
        public bool HasVideo { get; private set; }

        RTCPeerConnection m_Pc;
        VideoStreamTrack m_Track;
        string m_SessionUrl;          // resolved WHEP Location, DELETEd on teardown
        Coroutine m_ConnectRoutine;

        /// <summary>
        /// Starts the WHEP handshake against <paramref name="whepUrl"/>. Safe to
        /// call again after a failure; an in-flight attempt is cancelled first.
        /// </summary>
        public void Connect(string whepUrl)
        {
            Disconnect();
            m_ConnectRoutine = StartCoroutine(ConnectRoutine(whepUrl));
        }

        public void Disconnect()
        {
            if (m_ConnectRoutine != null)
            {
                StopCoroutine(m_ConnectRoutine);
                m_ConnectRoutine = null;
            }

            // Tell the server the session is over before dropping the peer
            // connection. Fire-and-forget: this often runs during teardown, when
            // there is no frame left to finish a coroutine in.
            if (!string.IsNullOrEmpty(m_SessionUrl))
            {
                try
                {
                    UnityWebRequest del = UnityWebRequest.Delete(m_SessionUrl);
                    UnityWebRequestAsyncOperation op = ZedHttp.Send(del, ZedHttp.HostOf(m_SessionUrl));
                    op.completed += _ => del.Dispose();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ZedStream] WHEP DELETE failed: {e.Message}");
                }
                m_SessionUrl = null;
            }

            if (m_Track != null)
            {
                m_Track.OnVideoReceived -= HandleVideoReceived;
                m_Track.Dispose();
                m_Track = null;
            }

            if (m_Pc != null)
            {
                m_Pc.OnTrack = null;
                m_Pc.OnIceConnectionChange = null;
                m_Pc.Close();
                m_Pc.Dispose();
                m_Pc = null;
            }

            IsConnected = false;
            HasVideo = false;
        }

        void OnDisable() => Disconnect();

        IEnumerator ConnectRoutine(string whepUrl)
        {
            string host = ZedHttp.HostOf(whepUrl);
            if (string.IsNullOrEmpty(host))
            {
                Debug.LogError($"[ZedStream] WHEP url is not a valid absolute URL: {whepUrl}");
                yield break;
            }

            // No ICE servers on purpose: the headset and the Jetson are on the same
            // LAN, so host candidates are all that is ever needed and a STUN lookup
            // would only add a way to fail.
            var config = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() };
            m_Pc = new RTCPeerConnection(ref config);
            m_Pc.OnTrack = HandleTrack;
            m_Pc.OnIceConnectionChange = state => Debug.Log($"[ZedStream] ICE: {state}");

            m_Pc.AddTransceiver(TrackKind.Video,
                new RTCRtpTransceiverInit { direction = RTCRtpTransceiverDirection.RecvOnly });

            RTCSessionDescriptionAsyncOperation offerOp = m_Pc.CreateOffer();
            yield return offerOp;
            if (offerOp.IsError)
            {
                Fail($"CreateOffer: {offerOp.Error.message}");
                yield break;
            }

            RTCSessionDescription offer = offerOp.Desc;
            RTCSetSessionDescriptionAsyncOperation setLocal = m_Pc.SetLocalDescription(ref offer);
            yield return setLocal;
            if (setLocal.IsError)
            {
                Fail($"SetLocalDescription: {setLocal.Error.message}");
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + IceGatherTimeoutSeconds;
            while (m_Pc.GatheringState != RTCIceGatheringState.Complete &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;

            if (m_Pc.GatheringState != RTCIceGatheringState.Complete)
                Debug.LogWarning("[ZedStream] ICE gathering did not complete in time — " +
                                 "POSTing the offer anyway.");

            Debug.Log($"[ZedStream] WHEP POST {whepUrl}");
            using (var request = new UnityWebRequest(whepUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(m_Pc.LocalDescription.sdp));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/sdp");
                request.timeout = HttpTimeoutSeconds;

                yield return ZedHttp.Send(request, host);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail($"WHEP POST {request.responseCode} {request.error}");
                    yield break;
                }

                // The Location header is usually a path, not an absolute URL.
                m_SessionUrl = ZedHttp.ResolveAgainst(whepUrl, request.GetResponseHeader("Location"));

                var answer = new RTCSessionDescription
                {
                    type = RTCSdpType.Answer,
                    sdp = request.downloadHandler.text,
                };
                RTCSetSessionDescriptionAsyncOperation setRemote = m_Pc.SetRemoteDescription(ref answer);
                yield return setRemote;
                if (setRemote.IsError)
                {
                    Fail($"SetRemoteDescription: {setRemote.Error.message}");
                    yield break;
                }
            }

            IsConnected = true;
            m_ConnectRoutine = null;
            Debug.Log($"[ZedStream] WHEP connected{(m_SessionUrl != null ? $" ({m_SessionUrl})" : "")}");
        }

        void HandleTrack(RTCTrackEvent e)
        {
            if (!(e.Track is VideoStreamTrack video))
                return;

            Debug.Log($"[ZedStream] video track received (id={video.Id})");
            m_Track = video;
            video.OnVideoReceived += HandleVideoReceived;
        }

        void HandleVideoReceived(Texture texture)
        {
            if (texture == null)
                return;

            // The HIT shift samples past the edge of each eye's half, so the far
            // side must clamp rather than wrap — wrapping would fold the other
            // eye's image into this one at the seam.
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            if (!HasVideo)
            {
                HasVideo = true;
                Debug.Log($"[ZedStream] first frame — {texture.width}x{texture.height}");
            }

            VideoTextureReceived?.Invoke(texture);
        }

        void Fail(string message)
        {
            Debug.LogError($"[ZedStream] connection failed — {message}");
            m_ConnectRoutine = null;
            Disconnect();
        }
    }
}
