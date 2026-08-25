using UnityEngine;

namespace HandTrackingViz
{
    /// <summary>
    /// The 18 upper-body joints published by this app, in the exact index order of
    /// OpenXR's <c>XrBodyJointFB</c> (XR_FB_body_tracking). These are joints 0..17 of
    /// the 70-joint default joint set — the whole set MINUS the two hands.
    ///
    /// That split is not a coincidence we imposed: XR_FB_body_tracking numbers the
    /// torso and arms first (0..17) and the left/right hand joints after (18..69).
    /// The hands are already published in full from the OpenXR hand-tracking
    /// subsystem as MediaPipe 21-point landmarks (see <see cref="HandLandmark"/>),
    /// so publishing 0..17 gives the complete upper-body skeleton with ZERO overlap
    /// against the existing "hands" field.
    ///
    /// Indices are wire-visible — a receiver reads <c>body.joints[i]</c> by these
    /// numbers, so this order must never be rearranged. See SCHEMA.md.
    /// </summary>
    public enum BodyJoint
    {
        Root = 0,
        Hips = 1,
        SpineLower = 2,
        SpineMiddle = 3,
        SpineUpper = 4,
        Chest = 5,
        Neck = 6,
        Head = 7,
        LeftShoulder = 8,
        LeftScapula = 9,
        LeftArmUpper = 10,
        LeftArmLower = 11,
        LeftWristTwist = 12,
        RightShoulder = 13,
        RightScapula = 14,
        RightArmUpper = 15,
        RightArmLower = 16,
        RightWristTwist = 17
    }

    /// <summary>
    /// How the runtime was asked to track the body. Meta's Inside-Out Body Tracking
    /// (IOBT) — the headset cameras observing the real arms and torso — is the HIGH
    /// fidelity mode; LOW is the older three-point inference from HMD + controllers.
    ///
    /// This reports the fidelity this app REQUESTED and the runtime ACCEPTED, via
    /// XR_META_body_tracking_fidelity. <see cref="Unspecified"/> means that extension
    /// was not available, so whatever the runtime picks by default is in effect
    /// (on Quest 3 / 3S with recent Horizon OS that is IOBT).
    /// </summary>
    public enum BodyTrackingFidelity
    {
        Unspecified = 0,
        Low = 1,
        High = 2
    }

    /// <summary>
    /// One frame of upper-body data expressed in <b>unity_world</b> (left-handed,
    /// Y-up, meters) — the same in-memory convention <see cref="HandLandmarkFrame"/>
    /// uses. <see cref="PoseFramePublisher"/> re-expresses it in the right-handed
    /// "robot_world" wire frame at serialization time, so this type never holds
    /// wire coordinates.
    ///
    /// Deliberately free of rendering concerns, matching the hand data types.
    /// </summary>
    public sealed class BodyLandmarkFrame
    {
        /// <summary>True when the runtime reported an active body this frame.</summary>
        public bool isTracked;

        /// <summary>Joint positions in unity_world. Length == <see cref="BodyJointMapping.JointCount"/>.</summary>
        public readonly Vector3[] positions = new Vector3[BodyJointMapping.JointCount];

        /// <summary>Joint rotations in unity_world (quaternion x,y,z,w). Same length as positions.</summary>
        public readonly Quaternion[] rotations = new Quaternion[BodyJointMapping.JointCount];

        /// <summary>Per-joint validity — a joint can be missing even while the body is tracked.</summary>
        public readonly bool[] valid = new bool[BodyJointMapping.JointCount];

        /// <summary>Runtime-reported confidence in the body pose, 0..1.</summary>
        public float confidence;

        /// <summary>The fidelity the runtime accepted for this tracker.</summary>
        public BodyTrackingFidelity fidelity;
    }

    /// <summary>
    /// Static tables describing the published upper-body skeleton. Kept separate so
    /// the provider, the publisher and the docs share one source of truth — the same
    /// role <see cref="HandLandmarkMapping"/> plays for the hands.
    /// </summary>
    public static class BodyJointMapping
    {
        /// <summary>Upper-body joints published: XrBodyJointFB 0..17 (hands excluded).</summary>
        public const int JointCount = 18;

        /// <summary>
        /// Total joints in XR_FB_body_tracking's default joint set (XR_BODY_JOINT_COUNT_FB).
        /// The runtime always fills all 70; we read back only the first
        /// <see cref="JointCount"/>, because 18..69 duplicate the hand data that the
        /// "hands" field already carries at higher fidelity.
        /// </summary>
        public const int OpenXRJointCount = 70;

        /// <summary>
        /// Wire-facing joint names, indexed by <see cref="BodyJoint"/>. Published in
        /// SCHEMA.md rather than on the wire (the wire carries indices only, to keep
        /// each frame small), so this is the table the docs are generated from.
        /// </summary>
        public static readonly string[] JointNames =
        {
            "ROOT",             // 0
            "HIPS",             // 1
            "SPINE_LOWER",      // 2
            "SPINE_MIDDLE",     // 3
            "SPINE_UPPER",      // 4
            "CHEST",            // 5
            "NECK",             // 6
            "HEAD",             // 7
            "LEFT_SHOULDER",    // 8
            "LEFT_SCAPULA",     // 9
            "LEFT_ARM_UPPER",   // 10
            "LEFT_ARM_LOWER",   // 11
            "LEFT_WRIST_TWIST", // 12
            "RIGHT_SHOULDER",   // 13
            "RIGHT_SCAPULA",    // 14
            "RIGHT_ARM_UPPER",  // 15
            "RIGHT_ARM_LOWER",  // 16
            "RIGHT_WRIST_TWIST" // 17
        };

        /// <summary>
        /// Parent chain of the upper-body skeleton, as (parent, child) index pairs —
        /// the counterpart to <see cref="HandLandmarkMapping.BoneConnections"/>.
        /// Provided so a receiver can draw or rig the skeleton without hardcoding the
        /// hierarchy itself.
        ///
        /// The chain is spine-outward: root → hips → spine → chest, then chest → neck
        /// → head and chest → scapula → shoulder → upper arm → lower arm → wrist
        /// twist on each side. The wrist twist joints (12 / 17) are where this
        /// skeleton meets the hands — they sit at the same place as landmark 0
        /// (WRIST) of the matching hand in the "hands" field.
        /// </summary>
        public static readonly (int parent, int child)[] BoneConnections =
        {
            // Spine
            (0, 1), (1, 2), (2, 3), (3, 4), (4, 5),
            // Head
            (5, 6), (6, 7),
            // Left arm
            (5, 9), (9, 8), (8, 10), (10, 11), (11, 12),
            // Right arm
            (5, 14), (14, 13), (13, 15), (15, 16), (16, 17)
        };
    }
}
