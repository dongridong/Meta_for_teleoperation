using UnityEngine;
using UnityEngine.XR;

namespace ZedStream
{
    /// <summary>
    /// Shows a stereo FISHEYE stream (the stereo viewer server: /stereo_raw/whep,
    /// /stereo_h264/whep) as a dome around the viewer. The picture is fixed in space:
    /// turning the head looks around inside it, the way that server's own web page
    /// behaves in VR. See FisheyeDome.shader for the lens model.
    ///
    /// Kept apart from <see cref="ZedStereoPanel"/>, which draws the ZED's pinhole
    /// stream as a flat 1:1 panel. <see cref="ZedPanelController"/> sends a stream to
    /// one or the other by the WHEP path it connected on, so changing this file can
    /// never change what the ZED path looks like.
    ///
    /// The dome sits on the head (position only). Its rotation is the recentre yaw:
    /// "straight ahead" in the video is wherever the operator was facing when it was
    /// last recentred — on the first frame, or on a right thumbstick press.
    ///
    /// Live lens tuning, as on the web page: right thumbstick left/right turns the two
    /// eye images against each other (where the eyes converge), up/down changes the
    /// lens field of view. Values persist on the headset.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class FisheyeDome : MonoBehaviour
    {
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int HalfFovId = Shader.PropertyToID("_HalfFov");
        static readonly int RadiusId = Shader.PropertyToID("_Radius");
        static readonly int CentreXId = Shader.PropertyToID("_CentreX");
        static readonly int CentreYId = Shader.PropertyToID("_CentreY");
        static readonly int SepId = Shader.PropertyToID("_Sep");
        static readonly int MonoId = Shader.PropertyToID("_Mono");
        static readonly int SwapId = Shader.PropertyToID("_Swap");
        static readonly int FlipVId = Shader.PropertyToID("_FlipV");

        const string FovKey = "ZedStream.FisheyeDome.FovDeg";
        const string SepKey = "ZedStream.FisheyeDome.SepDeg";

        /// <summary>Below this width:height a frame is one lens, not a side-by-side
        /// pair — the web page's test.</summary>
        const float MonoAspectBelow = 1.6f;

        [Header("Lens (equidistant fisheye) — defaults match the web page")]
        [Tooltip("Lens field of view in degrees. Right thumbstick up/down adjusts it live.")]
        [Range(135f, 225f)] public float fieldOfViewDeg = 190f;

        [Tooltip("Image circle radius as a fraction of ONE EYE's image width.")]
        [Range(0.1f, 1f)] public float circleRadius = 0.5f;

        [Tooltip("Image circle centre within one eye's image, 0..1; y is measured from the TOP.")]
        public Vector2 circleCentre = new Vector2(0.5f, 0.5f);

        [Tooltip("Degrees the two eye images are turned against each other; shifts where " +
                 "the eyes converge. Right thumbstick left/right adjusts it live.")]
        [Range(-10f, 10f)] public float eyeSeparationDeg = 0f;

        [Tooltip("Swap which half of a side-by-side frame goes to which eye.")]
        public bool swapEyes;

        [Tooltip("Flip the picture upside down, for a source that sends it that way.")]
        public bool flipVertically;

        [Header("Geometry")]
        [Tooltip("Dome radius in metres. Only needs to be well beyond arm's reach and " +
                 "inside the camera's far plane; the picture does not depend on it.")]
        public float radius = 20f;

        [Header("Live tuning")]
        public bool thumbstickTuning = true;

        Transform m_Head;
        MeshRenderer m_Renderer;
        Material m_Material;
        bool m_StickWasPressed;
        bool m_TuningDirty;

        void Awake()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            m_Material = m_Renderer.material;   // instance, so edits do not touch the asset
            m_Renderer.enabled = false;         // nothing to show until a frame arrives

            fieldOfViewDeg = PlayerPrefs.GetFloat(FovKey, fieldOfViewDeg);
            eyeSeparationDeg = PlayerPrefs.GetFloat(SepKey, eyeSeparationDeg);
        }

        /// <summary>The head the dome is centred on and recentres to.</summary>
        public void SetHead(Transform head) => m_Head = head;

        /// <summary>
        /// Binds the decoded video, or hides the dome when <paramref name="texture"/> is
        /// null. Called when the decoder hands over a texture, not every frame.
        /// </summary>
        public void SetVideoTexture(Texture texture)
        {
            if (m_Material == null)
                return;
            m_Material.SetTexture(MainTexId, texture);
            m_Renderer.enabled = texture != null;
            bool mono = texture != null && texture.height > 0 &&
                        (float)texture.width / texture.height < MonoAspectBelow;
            m_Material.SetFloat(MonoId, mono ? 1f : 0f);
        }

        /// <summary>Makes the current gaze direction "straight ahead" (yaw only, as
        /// on the web page — the horizon stays level).</summary>
        public void Recenter()
        {
            if (m_Head == null)
                return;
            Vector3 forward = Vector3.ProjectOnPlane(m_Head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f)
                return;   // looking straight up or down: keep the old yaw
            transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        void Update()
        {
            if (thumbstickTuning && m_Renderer.enabled)
                ReadThumbstick();
        }

        void LateUpdate()
        {
            if (m_Head != null)
                transform.position = m_Head.position;
            transform.localScale = Vector3.one * (radius * 2f);   // built-in sphere is 1 unit across

            if (m_Material == null)
                return;
            m_Material.SetFloat(HalfFovId, fieldOfViewDeg * 0.5f * Mathf.Deg2Rad);
            m_Material.SetFloat(RadiusId, circleRadius);
            m_Material.SetFloat(CentreXId, circleCentre.x);
            m_Material.SetFloat(CentreYId, circleCentre.y);
            m_Material.SetFloat(SepId, eyeSeparationDeg * Mathf.Deg2Rad);
            m_Material.SetFloat(SwapId, swapEyes ? 1f : 0f);
            m_Material.SetFloat(FlipVId, flipVertically ? 1f : 0f);
        }

        void ReadThumbstick()
        {
            // Right controller only. With hand tracking this device has no stick,
            // the reads fail, and nothing happens.
            InputDevice right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!right.isValid)
                return;

            if (right.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool pressed))
            {
                if (pressed && !m_StickWasPressed)
                    Recenter();
                m_StickWasPressed = pressed;
            }

            if (!right.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis))
                return;

            float x = Mathf.Abs(axis.x) < 0.2f ? 0f : axis.x;
            float y = Mathf.Abs(axis.y) < 0.2f ? 0f : axis.y;
            if (x != 0f || y != 0f)
            {
                // Rates match the web page: ~4°/s of separation, ~16°/s of FOV at
                // full tilt (it steps per frame at ~72 Hz).
                eyeSeparationDeg = Mathf.Clamp(eyeSeparationDeg + x * 4f * Time.deltaTime, -10f, 10f);
                fieldOfViewDeg = Mathf.Clamp(fieldOfViewDeg + y * 16f * Time.deltaTime, 135f, 225f);
                m_TuningDirty = true;
            }
            else if (m_TuningDirty)
            {
                // Saved when the stick is let go, not every frame of the push.
                m_TuningDirty = false;
                PlayerPrefs.SetFloat(FovKey, fieldOfViewDeg);
                PlayerPrefs.SetFloat(SepKey, eyeSeparationDeg);
                PlayerPrefs.Save();
                Debug.Log($"[ZedStream] dome lens: fov {fieldOfViewDeg:0.0}°, sep {eyeSeparationDeg:0.00}°");
            }
        }

        void OnDestroy()
        {
            if (m_Material != null)
                Destroy(m_Material);
        }
    }
}
