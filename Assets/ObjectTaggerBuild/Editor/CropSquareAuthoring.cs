// Authors RemoteCropSquarePrefab through Unity prefab APIs rather than
// handwritten YAML, following the slice 5 precedent. Kept rather than deleted:
// it is the readable record of every authored value, and it regenerates the
// prefab if the numbers need to change after a device tuning pass.

using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using PassthroughCameraSamples.MultiObjectDetection;

public static class CropSquareAuthoring
{
    // Must equal RemoteCropSquarePlacement.CanvasEdgeUnits. The controller sets
    // localScale = worldEdge / CanvasEdgeUnits, so a mismatch scales the square
    // wrong by exactly that ratio.
    private const float RootEdgeUnits = 100f;

    // Expressed as a fraction of the edge so they survive the uniform scaling.
    //
    // Tuned on device 2026-08-22. The bisection trial (crop 012) measured the
    // operator reading the boundary ~5% of frame width INBOARD of the true
    // crop edge, on the left edge and -- via the ticket's first trial -- the
    // right edge too. Both edges inboard is a scaling signature, not the
    // translation a lens-offset error would produce. The authored geometry was
    // then proven correct: every arm's OUTER edge lands exactly on the
    // boundary and RootEdgeUnits matches CanvasEdgeUnits, so the square really
    // does enclose the crop. The bias is therefore in the reading, from two
    // mechanisms: a 4-unit stroke put its visual centre 2 units inboard, and
    // arms covering only 25% out from each corner left a 50-unit ink-free gap
    // mid-edge with nothing to align against.
    //
    // Arms lengthened and stroke halved to attack both. Lengthening cannot
    // move the boundary: BL_H becomes size (40,4) at offset (+20,+2), still
    // spanning x in [0,40] with its outer edge at 0.
    // Second pass, same session: at StrokeUnits 2f the halo still read thick,
    // because Outline adds HaloOffsetUnits on BOTH sides -- the visible mark was
    // 2 + 2*1.5 = 5 units, mostly halo. Halo now scaled with the stroke, so the
    // mark is 1.5 + 2*0.75 = 3 units. Note the retune did NOT move the measured
    // boundary offset (46.5 px -> 46.0 px), which is why D19 stopped tuning ink
    // and went after the runtime extent instead.
    // Third pass. The visible mark is stroke + 2*halo, because Outline extends
    // it on BOTH sides. Converted to what the operator actually sees, against
    // the square's 30.2 degree extent:
    //
    //   stroke 4,   halo 1.5   -> 7 units -> 2.10 deg   (shipped, "quite thick")
    //   stroke 2,   halo 1.5   -> 5 units -> 1.50 deg   ("still quite thick")
    //   stroke 1.5, halo 0.75  -> 3 units -> 0.91 deg   ("still pretty thick")
    //   stroke 0.5, halo 0.25  -> 1 unit  -> 0.30 deg   (this pass)
    //
    // 0.91 deg is ~55 arcmin, about a little-finger width at arm's length. The
    // first two passes were tuned in canvas units without checking the angle,
    // which is why 30-40% steps felt like no change. Raise both together if the
    // amber ever washes out; the halo must stay a fraction of the stroke.
    private const float ArmLengthUnits = 40f;    // 40% of the edge; gap 20 units
    private const float StrokeUnits = 0.5f;      // 0.5% of the edge
    private const float HaloOffsetUnits = 0.25f; // half the stroke

    private static readonly Color ViewfinderAmber = new Color(1f, 0.7019608f, 0f, 1f);
    private static readonly Color Halo = new Color(0f, 0f, 0f, 0.75f);

    private const string PrefabPath =
        "Assets/PassthroughCameraApiSamples/MultiObjectDetection/RemoteRecognition/Prefabs/RemoteCropSquarePrefab.prefab";

    [MenuItem("Object Tagger/Author Crop Square Prefab")]
    public static void Author()
    {
        var root = new GameObject("RemoteCropSquarePrefab");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = new Vector2(RootEdgeUnits, RootEdgeUnits);
        rootRect.localScale = Vector3.one * 0.001f;
        root.SetActive(false);

        // Four corners, two arms each. Anchoring each arm to its own corner is
        // what keeps arm geometry fixed while the root's extent changes.
        foreach (var corner in new[] { "BL", "BR", "TL", "TR" })
        {
            var isLeft = corner[1] == 'L';
            var isBottom = corner[0] == 'B';
            var anchor = new Vector2(isLeft ? 0f : 1f, isBottom ? 0f : 1f);
            var xSign = isLeft ? 1f : -1f;
            var ySign = isBottom ? 1f : -1f;

            AddArm(rootRect, corner + "_H", anchor,
                new Vector2(ArmLengthUnits, StrokeUnits),
                new Vector2(xSign * ArmLengthUnits * 0.5f, ySign * StrokeUnits * 0.5f));
            AddArm(rootRect, corner + "_V", anchor,
                new Vector2(StrokeUnits, ArmLengthUnits),
                new Vector2(xSign * StrokeUnits * 0.5f, ySign * ArmLengthUnits * 0.5f));
        }

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PrefabPath));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out var success);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ObjectTagger] authored {PrefabPath} success={success}");
    }

    private static void AddArm(RectTransform parent, string name, Vector2 anchor, Vector2 size, Vector2 offset)
    {
        var arm = new GameObject(name);
        // Image brings RectTransform and CanvasRenderer with it. A sprite-less
        // Image renders a solid quad, so the brackets need no sprite asset.
        var image = arm.AddComponent<Image>();
        image.color = ViewfinderAmber;
        image.raycastTarget = false;

        var outline = arm.AddComponent<Outline>();
        outline.effectColor = Halo;
        outline.effectDistance = new Vector2(HaloOffsetUnits, HaloOffsetUnits);

        var rect = arm.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = offset;
    }

    private const string ScenePath =
        "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";

    private const string InstanceName = "RemoteCropSquare";

    /// Instances the authored prefab under CenterEyeAnchor -- the same parent
    /// RemoteAimReticle uses -- and assigns it to DetectionManager.m_cropSquare.
    ///
    /// Idempotent: re-running finds the existing instance instead of adding a
    /// second one, so this can be replayed from the pinned commit.
    [MenuItem("Object Tagger/Wire Crop Square Into Scene")]
    public static void WireScene()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var anchor = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .FirstOrDefault(t => t.name == "CenterEyeAnchor");
        if (anchor == null)
        {
            Debug.LogError("[ObjectTagger] CenterEyeAnchor NOT FOUND — scene not wired.");
            EditorApplication.Exit(1);
            return;
        }

        var existing = anchor.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name == InstanceName);

        GameObject instance;
        if (existing != null)
        {
            instance = existing.gameObject;
            Debug.Log($"[ObjectTagger] reusing existing {InstanceName}");
        }
        else
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (asset == null)
            {
                Debug.LogError($"[ObjectTagger] {PrefabPath} NOT FOUND — run Author first.");
                EditorApplication.Exit(1);
                return;
            }

            instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
            instance.name = InstanceName;
            instance.transform.SetParent(anchor, false);
        }

        // Overwritten every frame once depth resolves. These only matter if the
        // object is ever visible before the first resolve, which the visibility
        // gate is meant to prevent.
        instance.transform.localPosition = new Vector3(0f, 0f, RemoteCropSquarePlacement.ReferenceDistance);
        instance.transform.localRotation = Quaternion.identity;
        instance.SetActive(false);

        var detectionManager = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
            .OfType<DetectionManager>()
            .Single();

        var serialized = new SerializedObject(detectionManager);
        var property = serialized.FindProperty("m_cropSquare");
        if (property == null)
        {
            Debug.LogError("[ObjectTagger] m_cropSquare not found on DetectionManager — Task 3 incomplete.");
            EditorApplication.Exit(1);
            return;
        }

        property.objectReferenceValue = instance;
        _ = serialized.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        _ = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[ObjectTagger] wired {InstanceName} under CenterEyeAnchor");
    }
}
