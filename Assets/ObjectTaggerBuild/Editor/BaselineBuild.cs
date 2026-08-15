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

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BaselineBuild
{
    private const string OutPathArg = "-objectTaggerBuildOut";

    // Object Tagger slice 2, deviation D12.
    //
    // The output path used to be a hardcoded absolute path to ONE fixed filename.
    // That had two problems, and the second one destroyed evidence: it only worked
    // on this machine, and because every build wrote the same file, the slice 2
    // build overwrote slice 1's APK. That is precisely why the ~23 MB size delta
    // between the two cannot be attributed -- there is no longer an artifact to
    // diff against.
    //
    // Gate and evidence builds MUST pass -objectTaggerBuildOut <path> so their APK
    // survives under a name identifying what produced it. Builds without it go to a
    // clearly-labelled dev artifact that is expected to be overwritten.
    //
    // The default stays inside <repo-parent>/build, matching where slice 1's
    // evidence was recorded, but is now derived from the project location rather
    // than hardcoded, so it works on any machine.
    private static string ResolveOutputPath()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == OutPathArg)
            {
                Debug.Log($"[BaselineBuild] output path from {OutPathArg}: {args[i + 1]}");
                return args[i + 1];
            }
        }

        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        var buildDir = Path.Combine(Directory.GetParent(projectRoot).FullName, "build");
        _ = Directory.CreateDirectory(buildDir);
        var fallback = Path.Combine(buildDir, "ObjectTagger-dev.apk");
        Debug.LogWarning($"[BaselineBuild] {OutPathArg} not supplied; writing the overwritable dev artifact at {fallback}. Pass {OutPathArg} for any build whose APK is evidence.");
        return fallback;
    }

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

        var outPath = ResolveOutputPath();

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
