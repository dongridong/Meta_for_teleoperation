using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// Scene-facing component that drives hand-landmark visualization on top of passthrough.
    ///
    /// It wires together the reusable calculation layer (<see cref="HandLandmarkProvider"/>) and the
    /// rendering layer (<see cref="HandVisual"/>). The visualization is rendered in unity_world
    /// (left-handed, Y-up) — the same in-memory poses <see cref="PoseFramePublisher"/> publishes
    /// (the publisher re-expresses them in the right-handed "robot_world" frame at serialization,
    /// see SCHEMA.md).
    ///
    /// Add this to any GameObject in an XR scene and assign <see cref="trackingOrigin"/> to the XR Origin.
    /// </summary>
    public sealed class HandVisualizer : MonoBehaviour
    {
        [Header("Tracking")]
        [Tooltip("XR Origin transform. XR Hands joint poses are relative to this and are converted to unity_world. " +
                 "Leave empty only if your rig places the origin at world identity.")]
        public Transform trackingOrigin;

        [Header("Markers (landmarks)")]
        [Tooltip("Colour of the LEFT hand's joint spheres. Kept identical to the " +
                 "Galaxy_with_hands edition so the two look the same.")]
        public Color leftHandColor = Color.red;
        [Tooltip("Colour of the RIGHT hand's joint spheres. Deliberately different from " +
                 "the left one so the hands stay distinguishable.")]
        public Color rightHandColor = new Color(1f, 0.4f, 0.1f); // orange-red

        [Header("Bones (skeleton lines)")]
        [Tooltip("Colour of the lines connecting the landmarks. Shared by both hands and " +
                 "kept identical to the Galaxy_with_hands edition so the two look the same.")]
        public Color boneColor = Color.green;

        [Header("Sizes (meters)")]
        [Tooltip("Radius of each joint sphere.")]
        public float jointSphereRadius = 0.008f;
        [Tooltip("Width of the bone lines.")]
        public float boneLineWidth = 0.003f;
        [Tooltip("Length of the palm coordinate-frame axes (default 5 cm).")]
        public float axisLength = 0.05f;
        [Tooltip("Width of the palm coordinate-frame axis lines.")]
        public float axisLineWidth = 0.004f;

        [Header("Palm pose")]
        public HandLandmarkProvider.PalmPoseSource palmPoseSource = HandLandmarkProvider.PalmPoseSource.OpenXRPalmJoint;

        readonly HandLandmarkProvider m_Provider = new HandLandmarkProvider();

        HandLandmarkFrame m_LeftFrame;
        HandLandmarkFrame m_RightFrame;
        HandVisual m_LeftVisual;
        HandVisual m_RightVisual;

        // Procedurally created materials (unlit, so passthrough lighting doesn't affect them).
        Material m_LeftMat;
        Material m_RightMat;
        Material m_BoneMat;
        Material m_AxisXMat;
        Material m_AxisYMat;
        Material m_AxisZMat;

        void Awake()
        {
#if HTV_NO_HAND_VISUALIZATION
            // Controller-only build (-handVisualization off, see BuildAndroid.cs).
            //
            // Hand tracking stays enabled project-wide, so the Quest runtime
            // starts reporting tracked hands the moment a controller is put
            // down or loses tracking — and this component, which keys purely on
            // XRHandSubsystem state, would then draw a hand skeleton over
            // passthrough mid-session.
            //
            // Disabling here (before any material or HandVisual is created)
            // means Update() never runs and nothing is instantiated. Only the
            // rendering goes away: PoseFramePublisher keeps sampling and
            // publishing the hands exactly as in the hand build, so the wire
            // schema is identical.
            enabled = false;
            return;
#else
            m_LeftFrame = new HandLandmarkFrame(HandSide.Left);
            m_RightFrame = new HandLandmarkFrame(HandSide.Right);

            m_LeftMat = MakeUnlitMaterial(leftHandColor);
            m_RightMat = MakeUnlitMaterial(rightHandColor);
            m_BoneMat = MakeUnlitMaterial(boneColor);    // shared by both hands
            m_AxisXMat = MakeUnlitMaterial(Color.red);   // X
            m_AxisYMat = MakeUnlitMaterial(Color.green); // Y
            m_AxisZMat = MakeUnlitMaterial(Color.blue);  // Z

            m_LeftVisual = CreateVisual("LeftHandVisual", m_LeftMat);
            m_RightVisual = CreateVisual("RightHandVisual", m_RightMat);
#endif
        }

        HandVisual CreateVisual(string name, Material handMat)
        {
            return new HandVisual(
                transform,
                name,
                jointSphereRadius,
                boneLineWidth,
                axisLength,
                axisLineWidth,
                handMat,
                m_BoneMat,
                m_AxisXMat,
                m_AxisYMat,
                m_AxisZMat);
        }

        void Update()
        {
            m_Provider.palmPoseSource = palmPoseSource;
            UpdateHand(HandSide.Left, m_LeftFrame, m_LeftVisual);
            UpdateHand(HandSide.Right, m_RightFrame, m_RightVisual);
        }

        void UpdateHand(HandSide side, HandLandmarkFrame frame, HandVisual visual)
        {
            bool tracked = m_Provider.TryUpdate(side, trackingOrigin, frame);
            if (!tracked)
            {
                // Tracking loss (or subsystem not running): skip rendering this hand.
                visual.SetVisible(false);
                return;
            }

            visual.SetVisible(true);
            visual.Render(frame);
        }

        // Safe on the HTV_NO_HAND_VISUALIZATION path too: Awake() returns before
        // creating anything, so every material field is still null and
        // DestroyMaterial ignores nulls.
        void OnDestroy()
        {
            DestroyMaterial(m_LeftMat);
            DestroyMaterial(m_RightMat);
            DestroyMaterial(m_BoneMat);
            DestroyMaterial(m_AxisXMat);
            DestroyMaterial(m_AxisYMat);
            DestroyMaterial(m_AxisZMat);
        }

        static void DestroyMaterial(Material m)
        {
            if (m != null)
                Destroy(m);
        }

        /// <summary>Creates a simple unlit, solid-color material that works under URP (with fallbacks).</summary>
        static Material MakeUnlitMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }
    }
}
