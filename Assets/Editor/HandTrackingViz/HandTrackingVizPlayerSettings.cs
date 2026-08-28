using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HandTrackingViz.EditorTools
{
    /// <summary>
    /// One-click Android/Quest player configuration:
    /// switches the active platform to Android, sets IL2CPP + ARM64, a Quest-compatible min SDK,
    /// and linear color space (recommended for XR + URP).
    ///
    /// Run it from the menu: <b>Tools ▸ Hand Tracking Viz ▸ 2. Configure Android Player Settings</b>.
    ///
    /// The hand-tracking Android permission is added automatically at build time by
    /// <c>HandTrackingHandPermissionPostprocessor</c> (and by the OpenXR hand-tracking feature).
    /// </summary>
    public static class HandTrackingVizPlayerSettings
    {
        [MenuItem("Tools/Hand Tracking Viz/2. Configure Android Player Settings")]
        public static void Configure()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                {
                    Debug.LogError("[HandTrackingViz] Could not switch to the Android build target. " +
                                   "Make sure Android Build Support is installed via the Unity Hub.");
                    return;
                }
            }

            var android = NamedBuildTarget.Android;

            // IL2CPP is required for ARM64.
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Quest currently requires Android 12L (API 32) as the minimum.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            // Recommended for XR + URP.
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Require the INTERNET permission rather than letting Unity infer it.
            // The app is a TCP server (PoseFramePublisher) and, in the ZED build, a
            // WebRTC client; both are dead in the water without it. Inference has no
            // business deciding that — and com.unity.webrtc's build processor warns
            // about exactly this case.
            PlayerSettings.Android.forceInternetPermission = true;

            AssetDatabase.SaveAssets();
            Debug.Log("[HandTrackingViz] Android player settings configured: IL2CPP, ARM64, min SDK 32, " +
                      "Linear color space, INTERNET permission required.");
        }

        [MenuItem("Tools/Hand Tracking Viz/Setup Everything (Scene + Android)")]
        public static void SetupEverything()
        {
            HandTrackingVizSceneSetup.SetupScene();
            Configure();
            Debug.Log("[HandTrackingViz] Full setup done. Remaining manual step: enable the OpenXR features " +
                      "(Meta Quest group, Hand Tracking Subsystem, Passthrough) in " +
                      "Project Settings ▸ XR Plug-in Management ▸ OpenXR (Android).");
        }
    }
}
