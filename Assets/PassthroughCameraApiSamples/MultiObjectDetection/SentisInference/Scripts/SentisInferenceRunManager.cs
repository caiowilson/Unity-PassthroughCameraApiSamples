// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private DetectionManager m_detectionManager;

        [Header("Sentis Model config")]
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private TextAsset m_labelsAsset;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.6f;
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.23f;

        // Object Tagger slice 3 Task 4 — the acceptance cap (spec line 76).
        //
        // Upstream had NO cap: the NMS loop appended without bound. Spec line 76 assigns
        // this lever to slice 3 explicitly so that slice 6 can TUNE it rather than build
        // it under performance pressure.
        //
        // Applied after the descending sort, so the cap keeps the most confident
        // detections rather than an arbitrary subset. Default 20 is comfortably above
        // what device runs have shown (slice 1 saw 5 simultaneous, slice 2 saw 7) while
        // still bounding the worst case.
        [SerializeField, Range(1, 100)] private int m_maxAcceptedDetections = 20;

        [Header("Class curation")]
        [SerializeField] private bool m_curateClasses = true;

        [Header("UI display references")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        private Worker m_engine;
        private Vector2Int m_inputSize;
        // Object Tagger slice 3 Task 2: the tuple now carries the detection score.
        //
        // Every earlier note said "confidence dies at :37", which reads like a model or
        // export problem and sends people to the model converter. It is not. The
        // converter already emits scores as a first-class output
        // (graph.Compile(corners, classIDs, scores)), RunInference already reads that
        // tensor every frame, and NonMaxSuppression already holds the value -- it
        // filters on it and sorts by it -- before dropping it when building this tuple.
        //
        // Carrying confidence is therefore a type widening plus one argument. No model
        // change, no re-export, no extra readback.
        private readonly List<(int classId, Vector4 boundingBox, float score)> m_detections = new List<(int classId, Vector4 boundingBox, float score)>();

        private void Awake()
        {
            // Object Tagger slice 2: model warm-up re-homed here from StartMenu.Awake().
            //
            // Upstream ran PreloadModel in StartScene so the first-inference
            // main-thread stall was absorbed before the detection scene opened. A
            // single-scene app has no earlier scene, so the warm-up runs here instead
            // -- still before Start()'s inference loop, which keeps the stall off the
            // detection path rather than eliminating it.
            //
            // PreloadModel hardcodes BackendType.CPU regardless of m_backend. That is
            // preserved verbatim; changing the backend is a slice 6 performance lever.
            // Object Tagger slice 3 Task 5 — model load failure state (spec line 71).
            //
            // Upstream ran all of this bare. A throw here left the MonoBehaviour dead
            // with NO user-visible signal: the app launches, passthrough works, and
            // nothing is ever labelled. That is the same silent-failure class slice 2
            // spent a whole section on, and it is indistinguishable on device from an
            // untracked anchor or a permission failure.
            try
            {
                Debug.Log("[ObjectTagger] PreloadModel warm-up starting.");
                PreloadModel(m_sentisModel);
                Debug.Log("[ObjectTagger] PreloadModel warm-up complete.");

                var model = ModelLoader.Load(m_sentisModel);
                var inputShape = model.inputs[0].shape;
                m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
                Debug.Log($"[ObjectTagger] model input size = {m_inputSize.x}x{m_inputSize.y}, backend = {m_backend}");
                m_engine = new Worker(model, m_backend);
            }
            catch (System.Exception e)
            {
                m_modelLoadFailed = true;
                Debug.LogError($"[ObjectTagger] MODEL LOAD FAILED: {e.GetType().Name}: {e.Message}");
                if (m_uiMenuManager != null)
                {
                    m_uiMenuManager.SetModelLoadFailed(true);
                }
            }

            m_allowedClassIds = null;
        }

        /// True when Awake could not construct the inference engine. Every path that
        /// would dereference m_engine checks this first.
        private bool m_modelLoadFailed;

        /// Curated class IDs, resolved from the labels asset on first use. Null until
        /// resolved; empty means curation is disabled.
        private HashSet<int> m_allowedClassIds;

        private HashSet<int> GetAllowedClassIds()
        {
            if (!m_curateClasses)
            {
                return null;
            }
            if (m_allowedClassIds == null)
            {
                // Resolve by NAME against the shipped labels, so a drifted labels file or
                // an Ultralytics spelling is reported loudly rather than silently matching
                // nothing. See SupportedClasses for the trap this guards.
                var labels = m_labelsAsset != null
                    ? m_labelsAsset.text.Split('\n')
                    : System.Array.Empty<string>();
                m_allowedClassIds = SupportedClasses.Resolve(labels);
            }
            return m_allowedClassIds;
        }

        private IEnumerator Start()
        {
            m_uiInference.SetLabels(m_labelsAsset);

            // Slice 3 Task 5: do not spin an inference loop against a null engine.
            if (m_modelLoadFailed)
            {
                Debug.LogError("[ObjectTagger] inference loop NOT started — model failed to load. No detections will be produced.");
                yield break;
            }

            while (true)
            {
                while (m_uiMenuManager.IsPaused)
                {
                    yield return null;
                }
                yield return RunInference();
            }
        }

        private void OnDestroy()
        {
            // Slice 3 Task 5: upstream dereferenced m_engine unconditionally. If Awake
            // threw, this threw AGAIN on teardown and masked the original error — the
            // second exception is the one you see, and it points at the wrong place.
            if (m_engine == null)
            {
                return;
            }

            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();
        }

        internal static void PreloadModel(ModelAsset modelAsset)
        {
            // Load model
            var model = ModelLoader.Load(modelAsset);
            var inputShape = model.inputs[0].shape;

            // Create engine to run model
            using var worker = new Worker(model, BackendType.CPU);

            // Run inference with an empty image to load the model in the memory. The first inference blocks the main thread for a long time, so we're doing it on the app launch
            Texture tempTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var textureTransform = new TextureTransform().SetDimensions(tempTexture.width, tempTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, inputShape.Get(2), inputShape.Get(3)));
            TextureConverter.ToTensor(tempTexture, input, textureTransform);
            worker.Schedule(input);

            // Complete the inference immediately and destroy the temporary texture
            worker.PeekOutput(0).CompleteAllPendingOperations();
            worker.PeekOutput(1).CompleteAllPendingOperations();
            worker.PeekOutput(2).CompleteAllPendingOperations();
            Destroy(tempTexture);
        }

        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
            {
                yield break;
            }

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, which means 'm_cameraAccess.GetCameraPose()' is not reliable, skipping.");
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();

            // Update Capture data
            Texture targetTexture = m_cameraAccess.GetTexture();

            // Convert the texture to a Tensor and schedule the inference
            var textureTransform = new TextureTransform().SetDimensions(targetTexture.width, targetTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, m_inputSize.x, m_inputSize.y));
            TextureConverter.ToTensor(targetTexture, input, textureTransform);

            // Schedule all model layers
            m_engine.Schedule(input);

            // Get the results. ReadbackAndCloneAsync waits for all layers to complete before returning the result
            var boxesAwaiter = (m_engine.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!boxesAwaiter.IsCompleted)
            {
                yield return null;
            }
            using var boxes = boxesAwaiter.GetResult();
            if (boxes.shape[0] == 0)
            {
                yield break;
            }

            var classIDsAwaiter = (m_engine.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
            while (!classIDsAwaiter.IsCompleted)
            {
                yield return null;
            }
            using var classIDs = classIDsAwaiter.GetResult();
            if (classIDs.shape[0] == 0)
            {
                Debug.LogError("classIDs.shape[0] == 0");
                yield break;
            }

            var scoresAwaiter = (m_engine.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!scoresAwaiter.IsCompleted)
            {
                yield return null;
            }
            using var scores = scoresAwaiter.GetResult();
            if (scores.shape[0] == 0)
            {
                Debug.LogError("scores.shape[0] == 0");
                yield break;
            }

            NonMaxSuppression(m_detections, boxes, classIDs, scores, m_iouThreshold, m_scoreThreshold, m_maxAcceptedDetections, GetAllowedClassIds());

            // Checking if spatial anchor is tracked ensures bounding boxes are placed at correct world space positIons.
            //
            // Object Tagger slice 2 Task 5: this guard is the project's most dangerous
            // silent failure. Inference and NMS have ALREADY run by this line, so a
            // COMPLETED result is discarded here and nothing renders, with no error.
            // Surfacing the anchor verdict turns that absence into something visible.
            //
            // Note the original condition is three-way: a camera that never started --
            // i.e. a permission failure -- presents identically to an untracked anchor.
            // The two are separated here so the UI reports only the anchor.
            var anchorTracked = m_detectionManager.m_spatialAnchor != null
                                && m_detectionManager.m_spatialAnchor.IsTracked;
            m_uiMenuManager.SetAnchorTracked(anchorTracked);

            if (!m_cameraAccess.IsPlaying || !anchorTracked)
            {
                yield break;
            }

            // Update UI.
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);
        }

        // Object Tagger slice 3 Task 3: this is now a THIN ADAPTER.
        //
        // It does the one thing that genuinely needs Inference Engine types — unwrap the
        // tensors — and hands plain spans to DetectionDecoder. All the decoding logic
        // lives there, reachable from edit-mode tests with nothing but arrays.
        //
        // maxAccepted is int.MaxValue here ON PURPOSE. Task 3 is a refactor and must not
        // change behaviour; upstream had no cap. Task 4 sets a real value.
        private static void NonMaxSuppression(List<(int classId, Vector4 boundingBox, float score)> outDetections, Tensor<float> boxes, Tensor<int> classIDs, Tensor<float> scores, float iouThreshold, float scoreThreshold, int maxAccepted, HashSet<int> allowedClassIds)
        {
            DetectionDecoder.SelectDetections(
                boxes.AsReadOnlyNativeArray().AsReadOnlySpan(),
                classIDs.AsReadOnlyNativeArray().AsReadOnlySpan(),
                scores.AsReadOnlyNativeArray().AsReadOnlySpan(),
                iouThreshold,
                scoreThreshold,
                maxAccepted,
                allowedClassIds,
                outDetections);
        }

        /// Forwarder kept so the two callers in SentisInferenceUiManager keep compiling
        /// untouched. Those are the cross-frame ASSOCIATION test that spec line 55
        /// replaces in slice 5, distinct from the intra-frame suppression use — slice 3
        /// must not alter them.
        internal static float CalculateIoU(Vector4 boxA, Vector4 boxB) =>
            DetectionDecoder.CalculateIoU(boxA, boxB);

    }
}
