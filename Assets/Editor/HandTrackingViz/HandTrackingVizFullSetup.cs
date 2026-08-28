using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace HandTrackingViz.EditorTools
{
    /// <summary>
    /// End-to-end, idempotent setup that leaves the project in a buildable-and-runnable state for
    /// Meta Quest: scene rig + HandVisualizer, Android player settings, the OpenXR loader registered
    /// for Android/Standalone, and the required OpenXR features (Meta Quest Support, Hand Tracking,
    /// Passthrough camera) enabled.
    ///
    /// Designed to be invoked headlessly:
    ///   Unity -batchmode -quit -projectPath . -buildTarget Android \
    ///         -executeMethod HandTrackingViz.EditorTools.HandTrackingVizFullSetup.RunAll
    /// It can also be run from the menu: <b>Tools ▸ Hand Tracking Viz ▸ 3. Full Setup (incl. XR enable)</b>.
    /// </summary>
    public static class HandTrackingVizFullSetup
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";

        // OpenXR features (matched by concrete type full-name) that must be enabled for the app to work.
        static readonly string[] RequiredFeatureTypes =
        {
            "UnityEngine.XR.OpenXR.Features.MetaQuestSupport.MetaQuestFeature", // Meta Quest Support
            "UnityEngine.XR.Hands.OpenXR.HandTracking",                         // Hand Tracking Subsystem
            "UnityEngine.XR.OpenXR.Features.Meta.ARCameraFeature",              // Meta Quest: Camera (Passthrough)
            "UnityEngine.XR.OpenXR.Features.Meta.ARSessionFeature",             // Meta Quest: Session (required by the AR/passthrough features)
            "UnityEngine.XR.OpenXR.Features.CompositionLayers.OpenXRCompositionLayersFeature", // Composition Layers Support (required because the com.unity.xr.compositionlayers package is present)
            "HandTrackingViz.BodyTrackingFeature",                              // Meta Inside-Out Body Tracking (this project's own feature; publishes the upper-body skeleton)
            // Interaction profiles — keep OpenXR project validation happy / allow controller fallback.
            "UnityEngine.XR.OpenXR.Features.Interactions.HandCommonPosesInteraction",
            "UnityEngine.XR.OpenXR.Features.Interactions.OculusTouchControllerProfile",
        };

        [MenuItem("Tools/Hand Tracking Viz/3. Full Setup (incl. XR enable)")]
        public static void RunAll()
        {
            int failures = 0;

            failures += Step("Scene setup (XR Origin + passthrough camera + HandVisualizer)", SetupSceneStep);
            // The ZED rig goes into the same scene. It is inert unless the build
            // defines HTV_ZED_VIDEO (BuildAndroid's -videoMode zed), so adding it
            // unconditionally costs the hands and controller builds nothing.
            failures += Step("ZED video rig (panel + rest frame + WHEP client)", () =>
                ZedStream.EditorTools.ZedSceneSetup.SetupRig());
            failures += Step("Android player settings (IL2CPP / ARM64 / minSDK32 / Linear)", () =>
                HandTrackingVizPlayerSettings.Configure());
            failures += Step("Register OpenXR loader (Android + Standalone)", () =>
            {
                AssignOpenXRLoader(BuildTargetGroup.Android);
                AssignOpenXRLoader(BuildTargetGroup.Standalone);
            });
            failures += Step("Enable OpenXR features (Android + Standalone)", () =>
            {
                EnableOpenXRFeatures(BuildTargetGroup.Android);
                EnableOpenXRFeatures(BuildTargetGroup.Standalone);
            });

            failures += Step("Save assets", () =>
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            });

            if (failures == 0)
                Debug.Log("[HandTrackingViz] FULL SETUP OK — scene, Android player settings, OpenXR loader and features are configured.");
            else
                Debug.LogError($"[HandTrackingViz] FULL SETUP finished with {failures} failing step(s). See errors above.");
        }

        static int Step(string label, Action body)
        {
            try
            {
                body();
                Debug.Log($"[HandTrackingViz] [OK] {label}");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HandTrackingViz] [FAIL] {label}: {e}");
                return 1;
            }
        }

        static void SetupSceneStep()
        {
            // Ensure the target scene is the active one (batchmode starts on an empty untitled scene).
            Scene active = EditorSceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Reuse the project's tested scene builder (creates rig, passthrough camera, HandVisualizer, saves).
            HandTrackingVizSceneSetup.SetupScene();
        }

        static void AssignOpenXRLoader(BuildTargetGroup group)
        {
            XRGeneralSettingsPerBuildTarget perBuild;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perBuild) || perBuild == null)
                throw new Exception("XRGeneralSettingsPerBuildTarget config object not found.");

            XRGeneralSettings settings = perBuild.SettingsForBuildTarget(group);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = $"{group} Settings";
                perBuild.SetSettingsForBuildTarget(group, settings);
                AssetDatabase.AddObjectToAsset(settings, perBuild);
            }

            XRManagerSettings manager = settings.Manager;
            if (manager == null)
            {
                manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = $"{group} Providers";
                settings.Manager = manager;
                AssetDatabase.AddObjectToAsset(manager, perBuild);
            }

            if (!XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderType, group))
                throw new Exception($"AssignLoader(OpenXRLoader) returned false for {group}.");

            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(manager);
            Debug.Log($"[HandTrackingViz]   • OpenXR loader assigned for {group}.");
        }

        static void EnableOpenXRFeatures(BuildTargetGroup group)
        {
            FeatureHelpers.RefreshFeatures(group);
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null)
                throw new Exception($"OpenXRSettings for {group} not found.");

            var wanted = new HashSet<string>(RequiredFeatureTypes);
            var enabled = new List<string>();

            foreach (OpenXRFeature feature in settings.GetFeatures())
            {
                if (feature == null)
                    continue;
                if (!wanted.Contains(feature.GetType().FullName))
                    continue;

                // Set the serialized field directly so it persists regardless of property accessibility.
                var so = new SerializedObject(feature);
                SerializedProperty prop = so.FindProperty("m_enabled");
                if (prop != null && !prop.boolValue)
                {
                    prop.boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorUtility.SetDirty(feature);
                enabled.Add(feature.GetType().Name);
            }

            Debug.Log($"[HandTrackingViz]   • {group} features enabled: " +
                      (enabled.Count > 0 ? string.Join(", ", enabled) : "(none matched)"));
        }
    }
}
