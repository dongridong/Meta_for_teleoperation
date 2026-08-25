using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace HandTrackingViz
{
    /// <summary>
    /// Pure calculation logic for the upper-body skeleton — the body-side counterpart
    /// to <see cref="HandLandmarkProvider"/>, and deliberately shaped like it so the
    /// publisher treats hands and body the same way.
    ///
    /// Responsibilities:
    ///  - Acquire the enabled <see cref="BodyTrackingFeature"/> (Meta IOBT via OpenXR).
    ///  - Ask for the Horizon OS body-tracking permission, which the runtime requires
    ///    before it will create a body tracker at all.
    ///  - Read the 18 upper-body joints and express them in unity_world.
    ///
    /// Coordinate system: outputs are unity_world (left-handed, Y-up, meters). The
    /// feature hands back tracking-space Unity poses; we push them through the XR
    /// Origin exactly as the hand provider does, so both sources end up in the same
    /// frame before <see cref="PoseFramePublisher"/> converts to "robot_world".
    ///
    /// Contains NO rendering code, matching the rest of the calculation layer.
    /// </summary>
    public sealed class BodyLandmarkProvider
    {
        /// <summary>
        /// Horizon OS permission gating body tracking. It is a runtime permission:
        /// declaring it in the manifest is necessary but not sufficient, the user must
        /// also accept the consent dialog.
        /// </summary>
        public const string BodyTrackingPermission = "com.oculus.permission.BODY_TRACKING";

        BodyTrackingFeature m_Feature;
        bool m_LoggedFeatureDisabled;
#if UNITY_ANDROID && !UNITY_EDITOR
        bool m_PermissionRequested;
#endif

        /// <summary>True when an enabled body-tracking feature with a live tracker is available.</summary>
        public bool HasTracker => m_Feature != null && m_Feature.HasTracker;

        /// <summary>
        /// Fills <paramref name="frame"/> with the current unity_world upper-body
        /// joints. Returns false (and marks the frame untracked) when the body is not
        /// tracked this frame — during permission consent, when the user is out of the
        /// tracking volume, or on a headset without body tracking. Callers should
        /// publish the body as tracked:false on a false result.
        /// </summary>
        /// <param name="trackingOrigin">
        /// The XR Origin transform body poses are relative to. When null, poses are
        /// treated as already being in world space.
        /// </param>
        /// <param name="frame">Reusable output frame (allocated once by the caller, refilled each frame).</param>
        public bool TryUpdate(Transform trackingOrigin, BodyLandmarkFrame frame)
        {
            frame.isTracked = false;
            frame.confidence = 0f;
            frame.fidelity = BodyTrackingFidelity.Unspecified;

            if (!EnsureFeature())
                return false;

            if (!EnsurePermission())
                return false;

            // Located in tracking space first, then moved to world in a second pass —
            // the feature has no view of the scene graph, and keeping the transform
            // here mirrors HandLandmarkProvider.ToWorld.
            if (!m_Feature.TryLocateUpperBody(frame.positions, frame.rotations, frame.valid, out float confidence))
                return false;

            if (trackingOrigin != null)
            {
                for (int i = 0; i < BodyJointMapping.JointCount; i++)
                {
                    if (!frame.valid[i])
                        continue;
                    frame.positions[i] = trackingOrigin.TransformPoint(frame.positions[i]);
                    frame.rotations[i] = trackingOrigin.rotation * frame.rotations[i];
                }
            }

            frame.confidence = confidence;
            frame.fidelity = m_Feature.AcceptedFidelity;
            frame.isTracked = true;
            return true;
        }

        /// <summary>
        /// Caches the enabled feature instance. Returns false when body tracking is
        /// switched off in the OpenXR settings — a configuration mistake worth saying
        /// out loud once, since nothing else in the app would hint at it.
        /// </summary>
        bool EnsureFeature()
        {
            if (m_Feature != null)
                return true;

            m_Feature = BodyTrackingFeature.GetEnabledInstance();
            if (m_Feature == null)
            {
                if (!m_LoggedFeatureDisabled)
                {
                    m_LoggedFeatureDisabled = true;
                    Debug.LogWarning("[BodyLandmarkProvider] the body-tracking OpenXR feature is not enabled — " +
                                     "publishing the body as tracked:false. Enable it under Project Settings ▸ " +
                                     "XR Plug-in Management ▸ OpenXR ▸ Android, or run " +
                                     "Tools ▸ Hand Tracking Viz ▸ 3. Full Setup. (Logged once.)");
                }
                return false;
            }

            return true;
        }

        /// <summary>
        /// Requests the body-tracking permission once, on the first frame it is
        /// needed. The dialog is asynchronous, so this returns false until the user
        /// accepts; <see cref="BodyTrackingFeature"/> keeps retrying tracker creation
        /// in the meantime, which is what makes the delay invisible on the wire (the
        /// body simply reports tracked:false until it is ready).
        /// </summary>
        bool EnsurePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(BodyTrackingPermission))
                return true;

            if (!m_PermissionRequested)
            {
                m_PermissionRequested = true;
                Permission.RequestUserPermission(BodyTrackingPermission);
                Debug.Log($"[BodyLandmarkProvider] requested {BodyTrackingPermission}.");
            }
            return false;
#else
            // On the editor / desktop OpenXR there is no Horizon OS permission model;
            // let the tracker-creation call be the arbiter of availability.
            return true;
#endif
        }
    }
}
