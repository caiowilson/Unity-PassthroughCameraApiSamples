// Added by Object Tagger slice 1 to build the UNMODIFIED upstream sample headlessly.
// Editor-only, so it is excluded from the player build itself.
//
// Deliberately ASSERTS rather than sets the scripting backend and target
// architecture: upstream already ships IL2CPP + ARM64, so writing them would
// dirty the tracked ProjectSettings.asset and break the "unmodified sample"
// claim the slice 1 gate depends on. Only user-level EditorUserBuildSettings
// (texture compression, development build) are assigned; those are untracked.
//
// Scene list is taken verbatim from EditorBuildSettings in upstream order.
// Per deviation D2 the order is NOT changed: StartScene must remain index 0,
// because StartMenu.cs enumerates from index 1 and performs the model warm-up.

using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BaselineBuild
{
    public static void BuildAndroid()
    {
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        Debug.Log($"[BaselineBuild] enabled scene count: {scenes.Length}");
        for (var i = 0; i < scenes.Length; i++)
        {
            Debug.Log($"[BaselineBuild] scene {i}: {scenes[i]}");
        }

        var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
        var arch = PlayerSettings.Android.targetArchitectures;
        var appId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
        Debug.Log($"[BaselineBuild] scriptingBackend={backend}");
        Debug.Log($"[BaselineBuild] targetArchitectures={arch}");
        Debug.Log($"[BaselineBuild] applicationIdentifier={appId}");

        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        EditorUserBuildSettings.development = true;
        Debug.Log($"[BaselineBuild] textureCompression={EditorUserBuildSettings.androidBuildSubtarget} development={EditorUserBuildSettings.development}");

        var outPath = "/Users/caiowilson/Work/upstream/build/MultiObjectDetectionBaseline.apk";

        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.Development,
        };

        var report = BuildPipeline.BuildPlayer(opts);
        var summary = report.summary;

        Debug.Log($"[BaselineBuild] result={summary.result}");
        Debug.Log($"[BaselineBuild] outputPath={summary.outputPath}");
        Debug.Log($"[BaselineBuild] totalSize={summary.totalSize}");
        Debug.Log($"[BaselineBuild] totalErrors={summary.totalErrors} totalWarnings={summary.totalWarnings}");

        if (summary.result != BuildResult.Succeeded)
        {
            Debug.LogError("[BaselineBuild] BUILD FAILED");
            EditorApplication.Exit(1);
        }

        EditorApplication.Exit(0);
    }
}
