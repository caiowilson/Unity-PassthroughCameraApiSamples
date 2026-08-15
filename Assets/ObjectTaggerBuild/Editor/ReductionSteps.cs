// Object Tagger slice 2 Task 3 — scripted reduction steps.
//
// These edits are done through Unity rather than by hand-editing scene YAML on
// purpose. Removing a prefab instance touches three coordinated places: the
// PrefabInstance block, its stripped Transform, and the parent Transform's
// m_Children list. Editing those by hand is precisely how a scene ends up with a
// dangling reference that still loads and silently renders nothing -- the same
// failure class as the four zero-labels traps this slice is built around.
//
// Scripting it also makes the reduction re-runnable and reviewable: anyone can
// re-run these from the pinned commit and get the same scene.
//
// Editor-only, so none of this enters a player build.

using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ReductionSteps
{
    private const string RetainedScene =
        "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";

    /// Task 3 Step 1 — remove the ReturnToStartScene instance from the retained scene.
    ///
    /// ReturnToStartScene.cs calls SceneManager.LoadScene(0). Once StartScene is gone
    /// and the retained scene is index 0, that reloads the app and erases the spatial
    /// anchor -- one of the four ways this project renders zero labels with no error.
    ///
    /// This must run BEFORE the StartScene directory is deleted, so the instance is
    /// unwired while its source prefab still exists. Deleting the asset first would
    /// leave a missing-prefab reference in the scene instead.
    public static void RemoveReturnToStartScene()
    {
        var scene = EditorSceneManager.OpenScene(RetainedScene, OpenSceneMode.Single);

        var target = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == "ReturnToStartScene");

        if (target == null)
        {
            Debug.Log("[Reduction] ReturnToStartScene instance NOT FOUND — nothing removed. Already reduced, or the name changed.");
            EditorApplication.Exit(0);
            return;
        }

        var parentName = target.transform.parent != null ? target.transform.parent.name : "<scene root>";
        Debug.Log($"[Reduction] Found ReturnToStartScene under '{parentName}'. Removing.");

        Object.DestroyImmediate(target);
        EditorSceneManager.MarkSceneDirty(scene);
        _ = EditorSceneManager.SaveScene(scene);

        var stillThere = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Any(t => t.gameObject.name == "ReturnToStartScene");

        Debug.Log($"[Reduction] Saved. ReturnToStartScene still present after save: {stillThere}");
        EditorApplication.Exit(stillThere ? 1 : 0);
    }

    /// Task 3 Step 4 — reduce the build scene list to the single retained scene.
    ///
    /// Slice 1's trap #1 said never reorder build scenes, because StartMenu.cs
    /// enumerates from index 1 and StartScene at index 0 carried the model warm-up.
    /// That trap dissolves here, but only because Task 2 re-homed the warm-up and
    /// this task deletes StartMenu. Running this before Task 2 would reintroduce the
    /// first-inference stall and corrupt the re-recorded latency figures.
    public static void SetSingleSceneBuildList()
    {
        var before = EditorBuildSettings.scenes.Select(s => s.path).ToArray();
        Debug.Log($"[Reduction] Build scene list before: {before.Length} entries");
        foreach (var p in before)
        {
            Debug.Log($"[Reduction]   {p}");
        }

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(RetainedScene, true) };

        var after = EditorBuildSettings.scenes.Select(s => s.path).ToArray();
        Debug.Log($"[Reduction] Build scene list after: {after.Length} entries");
        foreach (var p in after)
        {
            Debug.Log($"[Reduction]   {p}");
        }

        EditorApplication.Exit(after.Length == 1 && after[0] == RetainedScene ? 0 : 1);
    }
}
