using UnityEngine;

namespace ZedStream
{
    /// <summary>
    /// The quad the stereo video is drawn on.
    ///
    /// ANGULAR SIZE IS RECOMPUTED, NEVER STORED. The panel subtends the camera's
    /// field of view times <see cref="magnification"/>, and that depends on the
    /// panel's distance — hardcoding the size silently breaks it the first time the
    /// distance changes.
    ///
    /// At magnification 1 one camera degree is one headset degree, which is what
    /// UNITY_PORT_PROMPT.md section 4 asks for and what keeps hand-eye coordination
    /// honest. <see cref="fillView"/> trades that away for a bigger picture, solving
    /// against the headset's real projection for the scale that fills the display
    /// (~1.9x on a Quest 3; see <see cref="FillTarget"/>). It is worth being clear
    /// about what that does and does not buy — it shows no more of the world, since
    /// the camera's 74°x50° is fixed, so objects simply look larger than life and
    /// the same pixels are spread over twice the angle.
    ///
    /// HOW IT FOLLOWS THE HEAD. UNITY_PORT_PROMPT.md specifies a strictly
    /// world-locked panel, on the grounds that the ZED is bolted to the robot's
    /// body: turning your head does not change what the camera sees, so a panel
    /// that swings with your head tells the eyes something the inner ear disagrees
    /// with. That reasoning still holds. In practice, though, the panel covers only
    /// about 74°x50° of a ~110°x96° display, so a strictly world-locked panel
    /// leaves the view entirely after a modest turn and the operator has no way
    /// back to it.
    ///
    /// The compromise is a LAZY FOLLOW, and the deadzone is the whole point of it:
    ///   - Inside <see cref="followDeadzoneDeg"/> the panel does not rotate at all.
    ///     Small head movements — the constant micro-adjustments of precise work —
    ///     see a genuinely world-locked panel, which is where the vestibular
    ///     conflict would have been worst and most frequent.
    ///   - Past the deadzone it eases toward the new direction and keeps easing
    ///     until it is nearly centred again (<see cref="followSettleDeg"/>, so the
    ///     two thresholds form a hysteresis and the panel cannot chatter on the
    ///     boundary). A deliberate turn brings the image with you; it just arrives
    ///     a moment later, damped, rather than locked to your head.
    ///
    /// Roll is always discarded. Following yaw and pitch keeps the panel square to
    /// the eyes — which is also what preserves the 1:1 angular size, since a panel
    /// viewed obliquely no longer subtends the angles it was sized for — but a
    /// rolling horizon is about the most reliable way there is to make someone ill.
    ///
    /// Set <see cref="followHead"/> to false for the specification's original
    /// behaviour.
    ///
    /// <see cref="SetPanelPose"/> remains the ONE place that decides where the
    /// panel sits. The plan is for the Jetson to eventually send the camera's own
    /// orientation per frame (a data channel that does not exist yet), so that a
    /// head-following camera can be drawn where it actually pointed. When that
    /// arrives it replaces the follow logic below and nothing else changes.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class ZedStereoPanel : MonoBehaviour
    {
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int HitUvId = Shader.PropertyToID("_HitUv");
        static readonly int FeatherXId = Shader.PropertyToID("_FeatherX");
        static readonly int FeatherYId = Shader.PropertyToID("_FeatherY");
        static readonly int FlipVId = Shader.PropertyToID("_FlipV");
        static readonly int SrgbToLinearId = Shader.PropertyToID("_SrgbToLinear");

        [Header("Geometry")]
        [Tooltip("Distance from the viewer to the panel, in metres. The panel's size " +
                 "is derived from this and the camera's field of view every frame — " +
                 "changing it moves the panel and rescales it together, keeping the " +
                 "1:1 angular size intact.")]
        public float panelDistance = 2f;

        [Tooltip("Degrees to drop the panel below the gaze axis. A panel centred exactly " +
                 "on the gaze reads as too high, because a relaxed head does not look at " +
                 "the horizon — resting gaze sits roughly 10-15° under it, and a screen " +
                 "placed on the horizontal has to be looked UP at. Negative raises it.")]
        [Range(-30f, 30f)] public float pitchOffsetDeg = 10f;

        /// <summary>How far <see cref="fillView"/> pushes the panel.</summary>
        public enum FillTarget
        {
            /// <summary>
            /// As large as fits without losing any of the picture: the image reaches
            /// the left and right edges of the display and a letterbox remains above
            /// and below. ~1.9x on a Quest 3.
            /// </summary>
            FitInside = 0,

            /// <summary>
            /// No border anywhere — the panel covers the display in both axes. The
            /// camera is much wider than it is tall relative to the headset, so
            /// closing the vertical gap costs roughly a third of the picture's width
            /// off the sides. ~2.4x on a Quest 3.
            /// </summary>
            CoverDisplay = 1,
        }

        [Header("Magnification")]
        [Tooltip("Blow the panel up until it fills as much of the display as it can " +
                 "without either eye losing an edge. This does NOT show more of the " +
                 "world — the camera still sees 74°x50°, it is just drawn bigger — and " +
                 "it breaks the 1:1 angular size the specification asks for, so a known " +
                 "object no longer looks its real size and angular resolution roughly " +
                 "halves. Off is the specification's behaviour.")]
        public bool fillView = true;

        [Tooltip("Fit Inside fills the display left-to-right and keeps the letterbox " +
                 "above and below, losing none of the camera's view. Cover Display " +
                 "removes the letterbox too, at the cost of pushing about a third of " +
                 "the picture's width off the sides — on a teleoperation camera that " +
                 "is workspace you can no longer see.")]
        public FillTarget fillTarget = FillTarget.FitInside;

        [Tooltip("Manual magnification, used when Fill View is off. 1 = one camera " +
                 "degree to one headset degree, which is the value the rest of the " +
                 "design assumes.")]
        [Range(1f, 4f)] public float magnification = 1f;

        [Header("Head following")]
        [Tooltip("Let the panel drift after the operator's gaze. Off is the " +
                 "specification's strictly world-locked panel: comfortable, but easy " +
                 "to lose behind you.")]
        public bool followHead = true;

        [Tooltip("How far off-centre the gaze can go before the panel starts moving. " +
                 "Inside this angle the panel is genuinely world-locked, which is what " +
                 "keeps small head movements from making the image swim.")]
        [Range(0f, 45f)] public float followDeadzoneDeg = 15f;

        [Tooltip("The panel keeps easing toward the gaze until it is this close to " +
                 "centred. Smaller than the deadzone on purpose — the gap between the " +
                 "two is the hysteresis that stops the panel twitching at the boundary.")]
        [Range(0f, 10f)] public float followSettleDeg = 2f;

        [Tooltip("How briskly the panel catches up once it is moving. Higher is " +
                 "snappier; roughly, the panel closes most of the gap in 1/value " +
                 "seconds. Too high and it may as well be head-locked.")]
        [Range(0.5f, 20f)] public float followSharpness = 4f;

        [Header("Edge feathering")]
        [Tooltip("Fade the panel's edges to transparent so there is no hard rectangular " +
                 "border. What makes the surround annoying is the boundary, not the area.")]
        public bool featherEdges = true;

        [Tooltip("Horizontal fade width, as a fraction of the panel's width.")]
        [Range(0f, 0.5f)] public float featherX = 0.04f;

        [Tooltip("Vertical fade width, as a fraction of the panel's height. Usually wants " +
                 "to differ from the horizontal one — the vertical margin is much taller.")]
        [Range(0f, 0.5f)] public float featherY = 0.04f;

        [Header("Decoder quirks")]
        [Tooltip("Flip the video vertically. Whether this is needed depends on how the " +
                 "platform's decoder hands the texture over; check the picture and set it.")]
        public bool flipVertically;

        [Tooltip("Convert the sampled colour from sRGB to linear in the shader. Needed only " +
                 "if the decoded texture is not flagged sRGB — the symptom is washed-out or " +
                 "too-dark colour.")]
        public bool convertSrgbToLinear;

        ZedCalibration m_Calibration;
        Material m_Material;
        MeshRenderer m_Renderer;
        Transform m_Head;
        Camera m_Camera;

        // Last fill factor solved, and the last one announced. Re-solved every
        // frame rather than latched: on the first frames the XR runtime has not
        // filled in the real stereo projection yet, and a one-shot solve happily
        // freezes a placeholder 90°x90° frustum in place for the whole session.
        float m_FillMagnification = 1f;
        float m_LoggedFillMagnification;

        // The direction the panel sits in, as seen from the head. Roll-free.
        Quaternion m_Orbit = Quaternion.identity;
        bool m_Orbitset;

        // True while the panel is easing back toward the gaze. Latched, so the
        // deadzone decides when to START moving and followSettleDeg decides when to
        // stop — one threshold alone would leave the panel juddering on the edge.
        bool m_Catching;

        void Awake()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            m_Material = m_Renderer.material;   // instance, so edits do not touch the asset

            // Nothing to show until the first decoded frame arrives; an untextured
            // panel is a grey rectangle floating in the dark, which looks like a bug.
            m_Renderer.enabled = false;
        }

        /// <summary>
        /// Supplies the HMD camera the panel orbits around, and whose projection the
        /// fill-view solve reads. Without it the panel stays wherever it was last
        /// placed and cannot compute a fill factor.
        /// </summary>
        public void SetHead(Camera camera)
        {
            m_Camera = camera;
            m_Head = camera != null ? camera.transform : null;
        }

        /// <summary>
        /// Supplies the measured calibration. Until this is called the panel keeps
        /// the defaults, which are the right order of magnitude but not this unit's
        /// numbers.
        /// </summary>
        public void SetCalibration(ZedCalibration calibration)
        {
            m_Calibration = calibration;
            if (calibration != null)
                panelDistance = calibration.panel_distance_m;
        }

        /// <summary>
        /// Binds the decoded video texture. Called only when the decoder hands over
        /// a texture — not per render frame. The video is 30 fps and the compositor
        /// runs at 72/90 Hz, and re-binding on every render frame would be pure
        /// waste; the package refreshes the texture's contents in place.
        /// </summary>
        public void SetVideoTexture(Texture texture)
        {
            m_Material.SetTexture(MainTexId, texture);
            m_Renderer.enabled = texture != null;
        }

        /// <summary>
        /// THE single place that decides where the panel is. Everything that wants
        /// to move the panel goes through here.
        /// </summary>
        public void SetPanelPose(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>
        /// Snaps the panel to straight ahead of <paramref name="head"/>, with no
        /// easing — the user's "recenter". Also used for the initial placement, so
        /// the first frame does not arrive somewhere behind the operator.
        /// </summary>
        public void RecenterOn(Transform head)
        {
            if (head == null)
                return;

            m_Head = head;
            if (TryLevelLookRotation(head.forward, out Quaternion level))
                m_Orbit = level;
            m_Orbitset = true;
            m_Catching = false;
            ApplyOrbit();
        }

        void LateUpdate()
        {
            UpdateGeometry();
            UpdateFollow();
        }

        void UpdateGeometry()
        {
            ZedCalibration cal = m_Calibration;
            if (m_Material == null || cal == null)
                return;

            // Size from distance and field of view, every frame. A Unity quad is
            // 1x1 in local units, so the full extents are twice the half extents.
            Vector2 half = cal.PanelHalfExtents(panelDistance) * CurrentMagnification(cal);
            transform.localScale = new Vector3(half.x * 2f, half.y * 2f, 1f);

            m_Material.SetFloat(HitUvId, cal.HitUv());
            m_Material.SetFloat(FeatherXId, featherEdges ? featherX : 0f);
            m_Material.SetFloat(FeatherYId, featherEdges ? featherY : 0f);
            m_Material.SetFloat(FlipVId, flipVertically ? 1f : 0f);
            m_Material.SetFloat(SrgbToLinearId, convertSrgbToLinear ? 1f : 0f);
        }

        /// <summary>
        /// The factor the panel is scaled by. 1 keeps one camera degree on one
        /// headset degree; anything above magnifies, which is a deliberate trade of
        /// correct scale and sharpness for a bigger picture.
        /// </summary>
        float CurrentMagnification(ZedCalibration cal)
        {
            if (!fillView)
                return Mathf.Max(1f, magnification);

            if (TrySolveFillMagnification(cal, out float solved))
                m_FillMagnification = solved;

            return m_FillMagnification;
        }

        /// <summary>
        /// Smallest uniform scale at which the panel COVERS the display — no border
        /// left anywhere in either eye. That is what "fill the view" has to mean:
        /// a scale chosen so both eyes still see the panel's every edge would stop
        /// well short, since each eye's frustum reaches much further outward
        /// (temporally) than inward and a centred rectangle limited by the narrow
        /// nasal side leaves a wide border on the outside.
        ///
        /// So the half-extents are the LARGER side of each axis, maximised across
        /// both eyes. The consequence is honest and worth knowing: the outermost
        /// part of the picture falls in each eye's monocular wing and is seen by one
        /// eye only. Ordinary vision works the same way at its edges, so it reads as
        /// unremarkable — unlike a hard rectangular border, which is exactly what
        /// this is trading away.
        ///
        /// One scale is used for both axes, so the aspect ratio — and with it the
        /// stereo geometry — is untouched. Only the size changes.
        /// </summary>
        bool TrySolveFillMagnification(ZedCalibration cal, out float factor)
        {
            factor = 1f;
            if (m_Camera == null || cal == null)
                return false;

            float halfTanX, halfTanY;
            if (m_Camera.stereoEnabled)
            {
                if (!TryFrustumHalfTangents(m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left),
                                            out float lx, out float ly) ||
                    !TryFrustumHalfTangents(m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right),
                                            out float rx, out float ry))
                    return false;

                halfTanX = Mathf.Max(lx, rx);
                halfTanY = Mathf.Max(ly, ry);
            }
            else if (!TryFrustumHalfTangents(m_Camera.projectionMatrix, out halfTanX, out halfTanY))
            {
                return false;
            }

            // The camera's own half-angles, in the same tangent space.
            float camTanX = Mathf.Tan(cal.hfov_deg * Mathf.Deg2Rad * 0.5f);
            float camTanY = Mathf.Tan(cal.vfov_deg * Mathf.Deg2Rad * 0.5f);
            if (camTanX <= 0f || camTanY <= 0f)
                return false;

            float needX = halfTanX / camTanX;
            float needY = halfTanY / camTanY;

            // Covering the display means satisfying BOTH axes, so the larger demand
            // wins and the other axis overflows off-screen. Fitting means satisfying
            // only the tighter one, so the looser axis keeps its border.
            factor = fillTarget == FillTarget.CoverDisplay
                ? Mathf.Max(needX, needY)
                : Mathf.Min(needX, needY);
            factor = Mathf.Max(1f, factor);

            // Announced only when it actually changes, since this runs every frame.
            if (Mathf.Abs(factor - m_LoggedFillMagnification) > 0.02f)
            {
                m_LoggedFillMagnification = factor;
                Debug.Log($"[ZedStream] fill view ({fillTarget}): magnification {factor:0.00}x " +
                          $"(display {Mathf.Atan(halfTanX) * Mathf.Rad2Deg * 2f:0.#}° x " +
                          $"{Mathf.Atan(halfTanY) * Mathf.Rad2Deg * 2f:0.#}°, " +
                          $"camera {cal.hfov_deg:0.#}° x {cal.vfov_deg:0.#}°). " +
                          $"Angular resolution ~{cal.width / cal.hfov_deg / factor:0.#} px/°.");
            }
            return true;
        }

        /// <summary>
        /// Half-extents of a projection's frustum at unit depth — i.e. the tangent of
        /// each half-angle — taking the LARGER side of each axis. An asymmetric
        /// frustum reaches further one way than the other, and a centred rectangle
        /// only covers it once it matches the longer reach.
        /// </summary>
        static bool TryFrustumHalfTangents(Matrix4x4 p, out float halfTanX, out float halfTanY)
        {
            halfTanX = halfTanY = 0f;
            if (Mathf.Approximately(p.m00, 0f) || Mathf.Approximately(p.m11, 0f))
                return false;

            float right = (1f + p.m02) / p.m00;
            float left = (1f - p.m02) / p.m00;
            float up = (1f + p.m12) / p.m11;
            float down = (1f - p.m12) / p.m11;

            halfTanX = Mathf.Max(Mathf.Abs(left), Mathf.Abs(right));
            halfTanY = Mathf.Max(Mathf.Abs(up), Mathf.Abs(down));
            return halfTanX > 0f && halfTanY > 0f;
        }

        void UpdateFollow()
        {
            if (m_Head == null || !m_Orbitset)
                return;

            if (followHead && TryLevelLookRotation(m_Head.forward, out Quaternion target))
            {
                float offAxis = Quaternion.Angle(m_Orbit, target);

                if (!m_Catching && offAxis > followDeadzoneDeg)
                    m_Catching = true;
                else if (m_Catching && offAxis <= followSettleDeg)
                    m_Catching = false;

                if (m_Catching)
                {
                    // Framerate-independent exponential ease: the fraction of the
                    // remaining gap closed per second is constant, so the motion
                    // feels the same at 72 Hz and 90 Hz.
                    float t = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
                    m_Orbit = Quaternion.Slerp(m_Orbit, target, t);
                }
            }

            ApplyOrbit();
        }

        /// <summary>
        /// Places the panel on its orbit around the head. Position tracks the head
        /// immediately while the ORIENTATION is what lags: leaning or shifting in
        /// the chair should not slide the image away, and translation carries none
        /// of the rotational conflict the deadzone exists to avoid.
        ///
        /// <see cref="pitchOffsetDeg"/> is applied here rather than folded into the
        /// orbit, so the follow logic keeps comparing gaze against gaze — the panel
        /// hangs a fixed angle below wherever the orbit points, and the deadzone
        /// measures the same thing whatever the offset is set to. The panel is turned
        /// by the offset as well as moved by it, so it stays square to the line from
        /// the eyes: a panel dropped without being tilted would be viewed obliquely,
        /// which is the one thing that quietly breaks the angular size.
        /// </summary>
        void ApplyOrbit()
        {
            if (m_Head == null)
                return;

            Quaternion placed = m_Orbit * Quaternion.AngleAxis(pitchOffsetDeg, Vector3.right);
            Vector3 forward = placed * Vector3.forward;
            SetPanelPose(m_Head.position + forward * panelDistance, placed);
        }

        /// <summary>
        /// The roll-free orientation looking along <paramref name="forward"/>: yaw
        /// and pitch are kept, roll is discarded. Fails when the gaze is within a
        /// fraction of a degree of straight up or down, where "which way is level"
        /// stops being answerable — the caller keeps its previous value, so the
        /// panel simply holds still through the singularity instead of spinning.
        /// </summary>
        static bool TryLevelLookRotation(Vector3 forward, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (forward.sqrMagnitude < 1e-6f)
                return false;

            forward.Normalize();
            if (Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.9999f)
                return false;

            rotation = Quaternion.LookRotation(forward, Vector3.up);
            return true;
        }

        void OnDestroy()
        {
            if (m_Material != null)
                Destroy(m_Material);
        }
    }
}
