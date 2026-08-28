using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode Android build entry point, so the APK can be produced without
// opening the editor UI:
//
//   Unity -quit -batchmode -nographics -projectPath <proj> \
//         -buildTarget Android -executeMethod BuildAndroid.Build
//
// Scenes come from the Build Settings list (EditorBuildSettings), so this never
// disagrees with what a manual File > Build does. Output goes to
// Build/<productName>.apk unless -output <path> is passed.
//
// Optional per-build identity overrides let ONE project (one branch, one
// ProjectSettings.asset) emit several differently-identified APKs without
// editing the working tree — e.g. the hand and controller builds:
//
//   ... -executeMethod BuildAndroid.Build -output Build/Meta_with_hands.apk
//   ... -executeMethod BuildAndroid.Build -output Build/Meta_with_controllers.apk \
//         -productName "Meta with controller" \
//         -applicationIdentifier com.dongridong.metacontroller \
//         -handVisualization off
//   ... -executeMethod BuildAndroid.Build -output Build/Meta_with_zed.apk \
//         -productName "Meta with ZED" \
//         -applicationIdentifier com.dongridong.metazed \
//         -handVisualization off -videoMode zed
//
// productName is the label the headset shows under the app icon, so it is a
// human-readable name with spaces; the default output file name replaces those
// spaces with underscores to keep the APK path shell-friendly.
//
// -handVisualization off compiles the on-device hand skeleton/landmark
// rendering out of the APK, and is what makes the controller build a controller
// build. Hand tracking stays enabled project-wide, so the Quest runtime hands
// the app hand joints the moment a controller stops being tracked — without
// this the skeleton pops up mid-session whenever a controller is put down.
// PUBLISHED DATA IS UNAFFECTED: the "hands" field of the TCP stream is
// identical in both APKs (see SCHEMA.md); only the rendering goes away.
//
// -videoMode zed turns this into the ZED teleoperation build: the app draws the
// robot camera's stereo stream on a world-locked panel instead of passthrough.
//
// The rig lives in the same scene as everything else and disables itself in
// Awake unless its define is set, so the hands and controller APKs are
// unaffected by its presence. PUBLISHED DATA IS AGAIN UNAFFECTED: all three
// APKs emit the identical TCP stream, IOBT body and controllers included, and
// the ZED build publishes it while the video is running.
//
// The committed identity is restored after the build, so an overridden build
// never leaves ProjectSettings.asset dirty. Neither -handVisualization nor
// -videoMode needs such a restore: they are player-only scripting defines that
// never touch project settings in the first place.
public static class BuildAndroid
{
    // Player-only scripting define that strips hand visualization. Gated on in
    // HandVisualizer; see -handVisualization above.
    const string NoHandVisualizationDefine = "HTV_NO_HAND_VISUALIZATION";

    // Player-only scripting define that activates the ZED video rig. Gated on in
    // ZedPanelController; see -videoMode above.
    const string ZedVideoDefine = "HTV_ZED_VIDEO";

    [MenuItem("Build/Android APK")]
    public static void Build()
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
            throw new Exception("[BuildAndroid] no enabled scenes in Build Settings.");

        // Parsed before anything is mutated: this validation can throw, and
        // throwing after PlayerSettings has been overridden but before the
        // restoring try/finally below would leave the working tree dirty.
        // Rejecting anything but on/off is deliberate — a typo silently
        // shipping a controller APK that still draws hands is exactly the
        // failure this flag exists to prevent. Not a PlayerSetting, so unlike
        // the identity overrides there is nothing to capture and restore.
        string handVisualization = (ArgValue("-handVisualization") ?? "on").ToLowerInvariant();
        if (handVisualization != "on" && handVisualization != "off")
            throw new Exception($"[BuildAndroid] -handVisualization must be \"on\" or \"off\", " +
                                $"got \"{handVisualization}\".");
        bool handVisualizationEnabled = handVisualization == "on";

        // Same treatment, same reason: a typo must not quietly produce an APK that
        // shows passthrough where the operator expects the robot's camera.
        string videoMode = (ArgValue("-videoMode") ?? "off").ToLowerInvariant();
        if (videoMode != "zed" && videoMode != "off")
            throw new Exception($"[BuildAndroid] -videoMode must be \"zed\" or " +
                                $"\"off\", got \"{videoMode}\".");
        bool zedVideoEnabled = videoMode == "zed";

        // Per-build identity overrides (optional). Capture the committed values
        // first so they can be restored after the build — the overrides are for
        // this one APK only and must never be serialized back to disk.
        string origProductName = PlayerSettings.productName;
        string origAppId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);

        string productName = ArgValue("-productName");
        if (!string.IsNullOrEmpty(productName))
            PlayerSettings.productName = productName;

        string appId = ArgValue("-applicationIdentifier");
        if (!string.IsNullOrEmpty(appId))
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, appId);

        // Default output name follows productName, so it reflects an override.
        // Spaces are fine in the on-device label but awkward in a file path, so
        // the file name gets underscores.
        string output = ArgValue("-output")
                        ?? Path.Combine("Build",
                               PlayerSettings.productName.Replace(' ', '_') + ".apk");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));

        Debug.Log($"[BuildAndroid] scenes: {string.Join(", ", scenes)}");
        Debug.Log($"[BuildAndroid] output: {output}");
        Debug.Log($"[BuildAndroid] productName: {PlayerSettings.productName} " +
                  $"applicationIdentifier: {PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android)}");
        Debug.Log($"[BuildAndroid] handVisualization: {handVisualization}");
        Debug.Log($"[BuildAndroid] videoMode: {videoMode}");

        var extraDefines = new List<string>();
        if (!handVisualizationEnabled)
            extraDefines.Add(NoHandVisualizationDefine);
        if (zedVideoEnabled)
            extraDefines.Add(ZedVideoDefine);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
            // Player-only: applies to this build's script compilation and is
            // never written back to ProjectSettings.asset. Collected into a list so
            // the flags compose — the ZED build is also a controller build.
            extraScriptingDefines = extraDefines.Count == 0 ? null : extraDefines.ToArray(),
        };

        BuildSummary summary;
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(options);
            summary = report.summary;
        }
        finally
        {
            // Restore the committed identity even if the build throws, so the
            // working tree stays clean between builds.
            PlayerSettings.productName = origProductName;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, origAppId);
        }

        Debug.Log($"[BuildAndroid] result={summary.result} " +
                  $"size={summary.totalSize / (1024 * 1024)}MB " +
                  $"errors={summary.totalErrors} warnings={summary.totalWarnings}");

        if (summary.result != BuildResult.Succeeded)
        {
            // Batch mode ignores a non-zero exit unless we force it, and a silent
            // "failed" build that still exits 0 is worse than a loud crash.
            EditorApplication.Exit(1);
        }
    }

    private static string ArgValue(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, flag);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }
}
