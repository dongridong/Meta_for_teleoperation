using System;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace HandTrackingViz
{
    /// <summary>
    /// OpenXR feature that turns on Meta's Inside-Out Body Tracking (IOBT) and reads
    /// the upper-body skeleton out of the runtime every frame.
    ///
    /// WHY THIS EXISTS AS A CUSTOM FEATURE
    /// -----------------------------------
    /// Body tracking is exposed by the Horizon OS OpenXR runtime through the
    /// XR_FB_body_tracking extension, but nothing in this project's package set
    /// surfaces it: com.unity.xr.hands covers hands only, and com.unity.xr.meta-openxr
    /// covers passthrough/anchors/planes. Rather than pull in the whole Meta XR Core
    /// SDK (OVRPlugin) — which would duplicate the OpenXR backend this project already
    /// uses and put the working passthrough setup at risk — this feature calls the four
    /// extension entry points directly. No new package dependency, no native plugin:
    /// the entry points are resolved from the runtime's own xrGetInstanceProcAddr and
    /// invoked through delegates.
    ///
    /// IOBT IS THE RUNTIME'S ALGORITHM, NOT OURS. The headset's cameras observe the
    /// real torso and arms; the runtime solves the skeleton. This feature's job is to
    /// enable it, ask for HIGH fidelity (the mode that actually uses the cameras
    /// inside-out, as opposed to inferring arms from HMD + controllers), and hand the
    /// joints to <see cref="BodyLandmarkProvider"/>.
    ///
    /// EXTENSIONS USED
    ///   XR_FB_body_tracking             — required. Tracker + 70-joint locate call.
    ///   XR_META_body_tracking_fidelity  — optional. Lets us REQUEST high fidelity;
    ///                                     without it the runtime's default applies.
    ///
    /// FRAME TIME
    /// xrLocateBodyJointsFB needs the XrTime to locate joints at, and Unity's OpenXR
    /// package does not expose the predicted display time to C#. We therefore hook
    /// xrWaitFrame through <see cref="HookGetInstanceProcAddr"/> and cache the
    /// predictedDisplayTime the runtime writes there. That is the same instant the
    /// hand joints are sampled for, so body and hands in one published frame are
    /// mutually consistent rather than a frame apart.
    ///
    /// COORDINATES
    /// Joints come back in OpenXR convention (right-handed, Y-up, -Z forward) relative
    /// to the current app space. They are converted to Unity's left-handed Y-up frame
    /// here, so callers receive TRACKING-space Unity poses — exactly the shape
    /// XR Hands hands out, and what <see cref="BodyLandmarkProvider"/> then pushes
    /// through the XR Origin to reach unity_world.
    ///
    /// This feature must be ENABLED in Project Settings ▸ XR Plug-in Management ▸
    /// OpenXR ▸ Android. The project's Full Setup step enables it automatically.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(
        UiName = "Meta Inside-Out Body Tracking (publisher)",
        BuildTargetGroups = new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone },
        Company = "dongridong",
        Desc = "Reads the upper-body skeleton via XR_FB_body_tracking and requests IOBT high fidelity, " +
               "so PoseFramePublisher can stream it alongside the hands.",
        OpenxrExtensionStrings = ExtensionBodyTracking + " " + ExtensionBodyTrackingFidelity,
        Version = "1",
        Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Feature,
        FeatureId = featureId)]
#endif
    public sealed class BodyTrackingFeature : OpenXRFeature
    {
        public const string featureId = "com.dongridong.openxr.feature.body_tracking";

        /// <summary>Required — the tracker and the joint locate call live here.</summary>
        public const string ExtensionBodyTracking = "XR_FB_body_tracking";

        /// <summary>Optional — lets the app ask for IOBT high fidelity explicitly.</summary>
        public const string ExtensionBodyTrackingFidelity = "XR_META_body_tracking_fidelity";

        // ------------------------------------------------------------------
        // OpenXR constants (values verified against the OpenXR headers bundled
        // with com.unity.xr.openxr — see openxr_reflection_full.h)
        // ------------------------------------------------------------------
        const int XR_SUCCESS = 0;

        const int XR_TYPE_BODY_TRACKER_CREATE_INFO_FB = 1000076001;
        const int XR_TYPE_BODY_JOINTS_LOCATE_INFO_FB = 1000076002;
        const int XR_TYPE_BODY_JOINT_LOCATIONS_FB = 1000076005;

        const int XR_BODY_JOINT_SET_DEFAULT_FB = 0;

        const ulong XR_SPACE_LOCATION_ORIENTATION_VALID_BIT = 0x00000001;
        const ulong XR_SPACE_LOCATION_POSITION_VALID_BIT = 0x00000002;

        // XR_META_body_tracking_fidelity. Not present in the bundled headers (it is
        // newer than them), so these come from the extension spec. Every use is
        // non-fatal: if a value is wrong the request simply fails and we fall back to
        // the runtime's default fidelity.
        const int XR_BODY_TRACKING_FIDELITY_LOW_META = 1;
        const int XR_BODY_TRACKING_FIDELITY_HIGH_META = 2;

        // Byte offset of XrFrameState.predictedDisplayTime:
        //   XrStructureType type (4) + 4 padding + void* next (8) = 16.
        const int FrameStatePredictedDisplayTimeOffset = 16;

        // ------------------------------------------------------------------
        // Native structs. Layouts mirror the C declarations exactly; every field
        // lands at the same offset under the platform's natural alignment.
        // ------------------------------------------------------------------

        /// <summary>XrPosef — orientation first, then position (OpenXR convention).</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct XrPosef
        {
            public float qx, qy, qz, qw;
            public float px, py, pz;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrBodyJointLocationFB
        {
            public ulong locationFlags;   // XrSpaceLocationFlags
            public XrPosef pose;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrBodyTrackerCreateInfoFB
        {
            public int type;
            public IntPtr next;
            public int bodyJointSet;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrBodyJointsLocateInfoFB
        {
            public int type;
            public IntPtr next;
            public ulong baseSpace;       // XrSpace
            public long time;             // XrTime
        }

        [StructLayout(LayoutKind.Sequential)]
        struct XrBodyJointLocationsFB
        {
            public int type;
            public IntPtr next;
            public uint isActive;         // XrBool32
            public float confidence;
            public uint jointCount;       // in: array capacity
            public IntPtr jointLocations; // in: pointer to our buffer
            public uint skeletonChangedCount;
            public long time;             // XrTime
        }

        // ------------------------------------------------------------------
        // Entry points
        // ------------------------------------------------------------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetInstanceProcAddrDelegate(ulong instance, IntPtr name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int WaitFrameDelegate(ulong session, IntPtr frameWaitInfo, IntPtr frameState);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int CreateBodyTrackerDelegate(ulong session, ref XrBodyTrackerCreateInfoFB createInfo, out ulong bodyTracker);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DestroyBodyTrackerDelegate(ulong bodyTracker);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int LocateBodyJointsDelegate(ulong bodyTracker, ref XrBodyJointsLocateInfoFB locateInfo, ref XrBodyJointLocationsFB locations);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int RequestBodyTrackingFidelityDelegate(ulong bodyTracker, int fidelity);

        // ------------------------------------------------------------------
        // Hook state. The interceptor must be a static method (reverse P/Invoke
        // under IL2CPP), and the delegates it is built from must stay referenced
        // for as long as the runtime holds their function pointers — hence static
        // fields rather than locals.
        // ------------------------------------------------------------------
        static GetInstanceProcAddrDelegate s_OriginalGetProcAddr;
        static GetInstanceProcAddrDelegate s_InterceptorKeepAlive;
        static WaitFrameDelegate s_OriginalWaitFrame;
        static WaitFrameDelegate s_WaitFrameHookKeepAlive;
        static IntPtr s_WaitFrameHookPtr;
        static long s_PredictedDisplayTime;

        // Resolved once per instance.
        CreateBodyTrackerDelegate m_CreateBodyTracker;
        DestroyBodyTrackerDelegate m_DestroyBodyTracker;
        LocateBodyJointsDelegate m_LocateBodyJoints;
        RequestBodyTrackingFidelityDelegate m_RequestFidelity;

        ulong m_Instance;
        ulong m_Session;
        ulong m_BodyTracker;
        IntPtr m_JointBuffer;
        int m_JointStride;

        bool m_ExtensionAvailable;
        bool m_FidelityExtensionAvailable;
        float m_NextCreateAttemptTime;
        bool m_LoggedCreateFailure;
        bool m_LoggedLocateFailure;
        bool m_LoggedMissingTime;

        /// <summary>
        /// The fidelity the runtime accepted for the live tracker. Reported on the
        /// wire so a receiver can tell true inside-out tracking from the inferred
        /// fallback. <see cref="BodyTrackingFidelity.Unspecified"/> means
        /// XR_META_body_tracking_fidelity was unavailable and the runtime default is
        /// in effect.
        /// </summary>
        public BodyTrackingFidelity AcceptedFidelity { get; private set; } = BodyTrackingFidelity.Unspecified;

        /// <summary>True when XR_FB_body_tracking is enabled on the running instance.</summary>
        public bool IsSupported => m_ExtensionAvailable;

        /// <summary>True once a body tracker has been created and can be located against.</summary>
        public bool HasTracker => m_BodyTracker != 0;

        /// <summary>
        /// The enabled instance of this feature, or null when the feature is turned
        /// off in the OpenXR settings (or OpenXR is not running at all).
        /// </summary>
        public static BodyTrackingFeature GetEnabledInstance()
        {
            OpenXRSettings settings = OpenXRSettings.Instance;
            if (settings == null)
                return null;
            var feature = settings.GetFeature<BodyTrackingFeature>();
            return feature != null && feature.enabled ? feature : null;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Splices our interceptor into the runtime's function-lookup chain so we can
        /// see the predicted display time that xrWaitFrame produces each frame.
        /// Everything we do not care about is forwarded untouched.
        /// </summary>
        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            if (func == IntPtr.Zero)
                return func;

            s_OriginalGetProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(func);

            if (s_WaitFrameHookPtr == IntPtr.Zero)
            {
                s_WaitFrameHookKeepAlive = WaitFrameHook;
                s_WaitFrameHookPtr = Marshal.GetFunctionPointerForDelegate(s_WaitFrameHookKeepAlive);
            }

            s_InterceptorKeepAlive = InterceptGetInstanceProcAddr;
            return Marshal.GetFunctionPointerForDelegate(s_InterceptorKeepAlive);
        }

        [MonoPInvokeCallback(typeof(GetInstanceProcAddrDelegate))]
        static int InterceptGetInstanceProcAddr(ulong instance, IntPtr name, out IntPtr function)
        {
            function = IntPtr.Zero;

            GetInstanceProcAddrDelegate original = s_OriginalGetProcAddr;
            if (original == null)
                return -1;   // XR_ERROR_VALIDATION_FAILURE; nothing sane to forward to

            int result = original(instance, name, out function);
            if (result != XR_SUCCESS || function == IntPtr.Zero)
                return result;

            // Name comparison happens only during instance setup (a few hundred
            // lookups), never per frame, so the marshalled string is not a hot path.
            if (Marshal.PtrToStringAnsi(name) == "xrWaitFrame")
            {
                s_OriginalWaitFrame = Marshal.GetDelegateForFunctionPointer<WaitFrameDelegate>(function);
                function = s_WaitFrameHookPtr;
            }

            return result;
        }

        [MonoPInvokeCallback(typeof(WaitFrameDelegate))]
        static int WaitFrameHook(ulong session, IntPtr frameWaitInfo, IntPtr frameState)
        {
            WaitFrameDelegate original = s_OriginalWaitFrame;
            if (original == null)
                return -1;

            int result = original(session, frameWaitInfo, frameState);
            if (result == XR_SUCCESS && frameState != IntPtr.Zero)
            {
                // Read the field in place rather than marshalling the whole struct —
                // this runs on Unity's frame-pacing path every frame.
                System.Threading.Interlocked.Exchange(
                    ref s_PredictedDisplayTime,
                    Marshal.ReadInt64(frameState, FrameStatePredictedDisplayTimeOffset));
            }
            return result;
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            m_Instance = xrInstance;
            m_ExtensionAvailable = OpenXRRuntime.IsExtensionEnabled(ExtensionBodyTracking);
            m_FidelityExtensionAvailable = OpenXRRuntime.IsExtensionEnabled(ExtensionBodyTrackingFidelity);

            if (!m_ExtensionAvailable)
            {
                // Disabling ourselves is the honest outcome: on a runtime without body
                // tracking there is nothing to publish, and staying "enabled" would
                // only produce per-frame failures.
                Debug.LogWarning($"[BodyTrackingFeature] {ExtensionBodyTracking} is not available on this runtime — " +
                                 "body tracking will be published as tracked:false. Hands, HMD and controllers are unaffected.");
                return false;
            }

            if (!TryResolve("xrCreateBodyTrackerFB", out m_CreateBodyTracker) ||
                !TryResolve("xrDestroyBodyTrackerFB", out m_DestroyBodyTracker) ||
                !TryResolve("xrLocateBodyJointsFB", out m_LocateBodyJoints))
            {
                Debug.LogError("[BodyTrackingFeature] failed to resolve the XR_FB_body_tracking entry points; " +
                               "body tracking disabled.");
                m_ExtensionAvailable = false;
                return false;
            }

            if (m_FidelityExtensionAvailable && !TryResolve("xrRequestBodyTrackingFidelityMETA", out m_RequestFidelity))
                m_FidelityExtensionAvailable = false;   // optional — keep going without it

            // Guard against a re-create without an intervening destroy (OpenXR restart)
            // turning into a slow leak of the native joint buffer.
            if (m_JointBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(m_JointBuffer);
            m_JointStride = Marshal.SizeOf<XrBodyJointLocationFB>();
            m_JointBuffer = Marshal.AllocHGlobal(m_JointStride * BodyJointMapping.OpenXRJointCount);

            Debug.Log($"[BodyTrackingFeature] {ExtensionBodyTracking} ready" +
                      (m_FidelityExtensionAvailable ? $", {ExtensionBodyTrackingFidelity} available (will request HIGH / IOBT)." : "."));
            return true;
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            m_Session = xrSession;
            // The tracker is NOT created here: on Horizon OS the body-tracking
            // permission is usually still pending at session creation, and creating it
            // too early fails permanently. BodyLandmarkProvider requests the
            // permission, and EnsureTracker() retries until the runtime says yes.
            m_NextCreateAttemptTime = 0f;
            m_LoggedCreateFailure = false;
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            DestroyTracker();
            m_Session = 0;
        }

        protected override void OnInstanceDestroy(ulong xrInstance)
        {
            DestroyTracker();
            if (m_JointBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(m_JointBuffer);
                m_JointBuffer = IntPtr.Zero;
            }
            // Drop the cached frame time too: a new instance must supply its own
            // before we locate anything against it.
            System.Threading.Interlocked.Exchange(ref s_PredictedDisplayTime, 0);

            m_Instance = 0;
            m_ExtensionAvailable = false;
            m_FidelityExtensionAvailable = false;
            m_CreateBodyTracker = null;
            m_DestroyBodyTracker = null;
            m_LocateBodyJoints = null;
            m_RequestFidelity = null;
            AcceptedFidelity = BodyTrackingFidelity.Unspecified;
        }

        bool TryResolve<T>(string name, out T fn) where T : Delegate
        {
            fn = null;
            GetInstanceProcAddrDelegate getProc = s_OriginalGetProcAddr;
            if (getProc == null || m_Instance == 0)
                return false;

            IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
            try
            {
                if (getProc(m_Instance, namePtr, out IntPtr function) != XR_SUCCESS || function == IntPtr.Zero)
                    return false;
                fn = Marshal.GetDelegateForFunctionPointer<T>(function);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }
        }

        // ------------------------------------------------------------------
        // Tracker
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates the body tracker if it does not exist yet, retrying at most once a
        /// second. Creation fails until the user grants the body-tracking permission,
        /// so a failure here is expected during the first seconds of a session and is
        /// logged only once.
        /// </summary>
        bool EnsureTracker()
        {
            if (m_BodyTracker != 0)
                return true;
            if (!m_ExtensionAvailable || m_Session == 0 || m_CreateBodyTracker == null)
                return false;
            if (Time.unscaledTime < m_NextCreateAttemptTime)
                return false;

            m_NextCreateAttemptTime = Time.unscaledTime + 1f;

            var createInfo = new XrBodyTrackerCreateInfoFB
            {
                type = XR_TYPE_BODY_TRACKER_CREATE_INFO_FB,
                next = IntPtr.Zero,
                bodyJointSet = XR_BODY_JOINT_SET_DEFAULT_FB
            };

            int result = m_CreateBodyTracker(m_Session, ref createInfo, out ulong tracker);
            if (result != XR_SUCCESS || tracker == 0)
            {
                if (!m_LoggedCreateFailure)
                {
                    m_LoggedCreateFailure = true;
                    Debug.LogWarning($"[BodyTrackingFeature] xrCreateBodyTrackerFB returned {result}. " +
                                     "Usually this means the body-tracking permission has not been granted yet — " +
                                     "retrying once a second. (Logged once.)");
                }
                return false;
            }

            m_BodyTracker = tracker;
            m_LoggedCreateFailure = false;
            RequestHighFidelity();

            Debug.Log($"[BodyTrackingFeature] body tracker created; fidelity = {AcceptedFidelity}.");
            return true;
        }

        /// <summary>
        /// Asks the runtime for HIGH fidelity — Meta's Inside-Out Body Tracking, where
        /// the headset cameras track the real arms — instead of inferring the upper
        /// body from headset and controller poses. Best effort: on a runtime without
        /// the fidelity extension the default mode stays in force, which on Quest 3 /
        /// 3S with current Horizon OS is already IOBT.
        /// </summary>
        void RequestHighFidelity()
        {
            AcceptedFidelity = BodyTrackingFidelity.Unspecified;
            if (!m_FidelityExtensionAvailable || m_RequestFidelity == null || m_BodyTracker == 0)
                return;

            int result = m_RequestFidelity(m_BodyTracker, XR_BODY_TRACKING_FIDELITY_HIGH_META);
            if (result == XR_SUCCESS)
            {
                AcceptedFidelity = BodyTrackingFidelity.High;
                return;
            }

            // The runtime can decline HIGH (unsupported headset, tracking conditions);
            // ask for LOW so the wire still reports a definite mode.
            if (m_RequestFidelity(m_BodyTracker, XR_BODY_TRACKING_FIDELITY_LOW_META) == XR_SUCCESS)
            {
                AcceptedFidelity = BodyTrackingFidelity.Low;
                Debug.LogWarning("[BodyTrackingFeature] high fidelity (IOBT) declined by the runtime; using low fidelity.");
            }
        }

        void DestroyTracker()
        {
            if (m_BodyTracker != 0 && m_DestroyBodyTracker != null)
            {
                try { m_DestroyBodyTracker(m_BodyTracker); } catch { /* shutting down */ }
            }
            m_BodyTracker = 0;
            AcceptedFidelity = BodyTrackingFidelity.Unspecified;
        }

        // ------------------------------------------------------------------
        // Per-frame read
        // ------------------------------------------------------------------

        /// <summary>
        /// Locates the 18 upper-body joints for the current frame and writes them, in
        /// Unity's left-handed Y-up convention, into the caller's arrays. Poses are in
        /// TRACKING space (relative to the XR Origin), matching what XR Hands reports.
        ///
        /// Returns false when the body is not being tracked this frame, in which case
        /// the arrays are left untouched and the whole body must be published as
        /// tracked:false.
        /// </summary>
        /// <param name="positions">Length <see cref="BodyJointMapping.JointCount"/>, filled on success.</param>
        /// <param name="rotations">Length <see cref="BodyJointMapping.JointCount"/>, filled on success.</param>
        /// <param name="valid">Per-joint validity, filled on success.</param>
        /// <param name="confidence">Runtime confidence in the body pose, 0..1.</param>
        public bool TryLocateUpperBody(Vector3[] positions, Quaternion[] rotations, bool[] valid, out float confidence)
        {
            confidence = 0f;

            if (!EnsureTracker() || m_LocateBodyJoints == null || m_JointBuffer == IntPtr.Zero)
                return false;

            long displayTime = System.Threading.Interlocked.Read(ref s_PredictedDisplayTime);
            if (displayTime <= 0)
            {
                if (!m_LoggedMissingTime)
                {
                    m_LoggedMissingTime = true;
                    Debug.LogError("[BodyTrackingFeature] no predicted display time captured from xrWaitFrame; " +
                                   "body joints cannot be located. (Logged once.)");
                }
                return false;
            }

            ulong appSpace = GetCurrentAppSpace();
            if (appSpace == 0)
                return false;

            var locateInfo = new XrBodyJointsLocateInfoFB
            {
                type = XR_TYPE_BODY_JOINTS_LOCATE_INFO_FB,
                next = IntPtr.Zero,
                baseSpace = appSpace,
                time = displayTime
            };

            var locations = new XrBodyJointLocationsFB
            {
                type = XR_TYPE_BODY_JOINT_LOCATIONS_FB,
                next = IntPtr.Zero,
                jointCount = BodyJointMapping.OpenXRJointCount,   // runtime fills all 70…
                jointLocations = m_JointBuffer
            };

            int result = m_LocateBodyJoints(m_BodyTracker, ref locateInfo, ref locations);
            if (result != XR_SUCCESS)
            {
                if (!m_LoggedLocateFailure)
                {
                    m_LoggedLocateFailure = true;
                    Debug.LogWarning($"[BodyTrackingFeature] xrLocateBodyJointsFB returned {result}; " +
                                     "recreating the tracker. (Logged once.)");
                }
                DestroyTracker();
                return false;
            }

            if (locations.isActive == 0)
                return false;

            confidence = locations.confidence;

            // …but only 0..17 are read back: 18..69 are the hand joints, which the
            // "hands" field already carries from the dedicated hand-tracking subsystem.
            int count = Math.Min(BodyJointMapping.JointCount, (int)locations.jointCount);
            for (int i = 0; i < BodyJointMapping.JointCount; i++)
            {
                if (i >= count)
                {
                    valid[i] = false;
                    continue;
                }

                var joint = Marshal.PtrToStructure<XrBodyJointLocationFB>(m_JointBuffer + i * m_JointStride);
                bool poseValid = (joint.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) != 0 &&
                                 (joint.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) != 0;
                valid[i] = poseValid;
                if (!poseValid)
                    continue;

                // OpenXR (right-handed, -Z forward) -> Unity (left-handed, +Z forward):
                //   position   (x, y, z)    -> (x, y, -z)
                //   quaternion (x, y, z, w) -> (-x, -y, z, w)
                positions[i] = new Vector3(joint.pose.px, joint.pose.py, -joint.pose.pz);
                rotations[i] = new Quaternion(-joint.pose.qx, -joint.pose.qy, joint.pose.qz, joint.pose.qw);
            }

            return true;
        }
    }
}
