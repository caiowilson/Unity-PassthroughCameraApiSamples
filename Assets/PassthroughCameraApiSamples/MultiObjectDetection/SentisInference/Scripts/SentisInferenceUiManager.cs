// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceUiManager : MonoBehaviour
    {
        [Header("Placement configuration")]
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [SerializeField] private RectTransform m_detectionBoxPrefab;
        [Space(10)]
        public UnityEvent<int> OnObjectsDetected;

        internal readonly List<BoundingBoxData> m_boxDrawn = new();
        private string[] m_labels;
        private readonly List<BoundingBoxData> m_boxPool = new();

        internal class BoundingBoxData
        {
            public string ClassName;
            public int ClassId;
            public RectTransform BoxRectTransform;
            public float lastUpdateTime;
        }

        private void Awake() => m_detectionBoxPrefab.gameObject.SetActive(false);

        // Object Tagger slice 2 Task 5 — detection and depth-raycast counters.
        // Emitted periodically rather than per-frame so the log stays readable over a
        // multi-minute gate run.
        private int m_raycastAttempts;
        private int m_raycastMisses;

        /// Slice 4 Task 1: counted apart from m_raycastMisses on purpose. These are
        /// subsystem faults, not depth quality, and mixing them corrupts the rate.
        private int m_depthSubsystemUnavailable;

        [Header("Depth sampling (slice 4 Task 3)")]
        /// Rays cast per detection. DEFAULT 1 IS BEHAVIOUR-NEUTRAL — it is exactly
        /// upstream's single centre ray. Raising it probes a small pattern around the
        /// centre and takes the median by distance, which is the fix for a centre ray
        /// slipping past a chair onto the wall behind. Do not raise it without an
        /// on-device baseline to prove the offset improved.
        [SerializeField, Range(1, 5)] private int m_depthSamplesPerDetection = 1;

        /// Sample spread in NORMALISED viewport units.
        [SerializeField, Range(0.005f, 0.15f)] private float m_depthSampleSpread = 0.03f;

        private readonly Vector3[] m_depthSampleBuffer = new Vector3[5];

        /// Probe depth around a detection centre and pick a representative point.
        ///
        /// Outcome accounting, which decides what the miss rate means:
        ///   ANY sample resolving  -> Hit. One detection, one attempt, succeeded.
        ///   ALL samples missing   -> Miss.
        ///   ANY subsystem-unavailable -> SubsystemUnavailable, and it short-circuits:
        ///     the subsystem is down for every ray, so probing the rest is pointless
        ///     and would let one detection inflate that counter N times.
        private DepthResolveStatus ProbeDepth(Vector2 normalizedCenter, Pose cameraPose, out Vector3 point)
        {
            point = Vector3.zero;

            var sampleCount = Mathf.Clamp(m_depthSamplesPerDetection, 1, m_depthSampleBuffer.Length);
            var offsets = DetectionProjection.SampleOffsets(m_depthSampleSpread);
            var resolved = 0;

            for (var s = 0; s < sampleCount; s++)
            {
                // SampleOffsets returns centre-first, so sampleCount==1 is exactly the
                // upstream centre ray.
                var probe = normalizedCenter + offsets[s];
                var ray = m_cameraAccess.ViewportPointToRay(
                    DetectionProjection.ToViewportPoint(probe), cameraPose);

                var result = m_environmentRaycast.ResolveDepth(ray);

                if (result.Status == DepthResolveStatus.SubsystemUnavailable)
                {
                    return DepthResolveStatus.SubsystemUnavailable;
                }

                if (result.IsHit)
                {
                    m_depthSampleBuffer[resolved++] = result.Point;
                }
            }

            m_depthRaysCast += sampleCount;

            if (resolved == 0)
            {
                return DepthResolveStatus.Miss;
            }

            return DetectionProjection.TryPickRepresentativePoint(
                m_depthSampleBuffer, resolved, cameraPose.position, out point)
                ? DepthResolveStatus.Hit
                : DepthResolveStatus.Miss;
        }

        /// Recorded so a future miss-rate or performance comparison is not silently
        /// apples-to-oranges once the sample count stops being 1.
        private int m_depthRaysCast;
        private int m_detectionsSeen;
        private float m_nextCountLogTime;

        private void LogCountsIfDue()
        {
            const float logIntervalSeconds = 10f;
            if (Time.time < m_nextCountLogTime)
            {
                return;
            }
            m_nextCountLogTime = Time.time + logIntervalSeconds;

            // Denominator EXCLUDES subsystem-unavailable frames. They are counted as
            // attempts (they were attempted) but including them here would DEFLATE the
            // rate: a run where depth was off entirely would report a low miss rate
            // rather than an undefined one. The rate answers "when depth was working,
            // how often did it fail to resolve?" — nothing else.
            var qualified = m_raycastAttempts - m_depthSubsystemUnavailable;
            var missRate = qualified > 0
                ? (100f * m_raycastMisses / qualified).ToString("F1")
                : "n/a";

            // The rate deliberately EXCLUDES subsystem-unavailable frames — it is a
            // depth-quality figure, and a subsystem fault is not a quality problem.
            // Reported alongside so a non-zero value is impossible to miss.
            // raysPerDetection is reported so a future comparison against slice 2's 0.9%
            // is not silently apples-to-oranges once the sample count stops being 1.
            var raysPerDetection = m_raycastAttempts > 0
                ? ((float)m_depthRaysCast / m_raycastAttempts).ToString("F1")
                : "n/a";

            Debug.Log($"[ObjectTagger] counts: detections={m_detectionsSeen} " +
                      $"raycastAttempts={m_raycastAttempts} raycastMisses={m_raycastMisses} " +
                      $"missRate={missRate}% subsystemUnavailable={m_depthSubsystemUnavailable} " +
                      $"raysPerDetection={raysPerDetection} boxesDrawn={m_boxDrawn.Count}");
        }

        private void Update()
        {
            LogCountsIfDue();

            // Remove boxes that haven't been updated recently
            for (int i = m_boxDrawn.Count - 1; i >= 0; i--)
            {
                var box = m_boxDrawn[i];
                const float timeToPersistBoxes = 3f;
                if (Time.time - box.lastUpdateTime > timeToPersistBoxes)
                {
                    ReturnToPool(box);
                    m_boxDrawn.RemoveAt(i);
                }
            }
        }

        /// Object Tagger slice 3 Task 4: the single guarded label lookup.
        ///
        /// Upstream indexed m_labels at TWO sites with no bounds check. An out-of-range
        /// classId threw IndexOutOfRange from deep inside the draw loop, which on device
        /// reads as "detection stopped working" rather than "the labels file is wrong".
        private string LabelFor(int classId)
        {
            if (m_labels == null || classId < 0 || classId >= m_labels.Length)
            {
                Debug.LogError($"[ObjectTagger] class id {classId} is outside the label range (labels={m_labels?.Length ?? 0}).");
                return $"class_{classId}";
            }
            return m_labels[classId].Replace(" ", "_");
        }

        public void SetLabels(TextAsset labelsAsset)
        {
            // Parse neural net labels
            // Object Tagger slice 3 Task 4: drop the trailing empty entry at parse time.
            //
            // SentisYoloClasses.txt is 80 classes but ENDS WITH A NEWLINE, so the naive
            // Split yields 81 entries with an empty last one. Harmless while ArgMax can
            // only produce 0-79, but it makes m_labels.Length lie, and slice 3 both
            // validates against that length and filters the class set.
            var rawLabels = labelsAsset.text.Split('\n');
            var parsed = new List<string>(rawLabels.Length);
            foreach (var raw in rawLabels)
            {
                var name = raw.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    parsed.Add(name);
                }
            }
            m_labels = parsed.ToArray();
            Debug.Log($"[ObjectTagger] labels parsed: {m_labels.Length} usable of {rawLabels.Length} raw entries.");
        }

        // Object Tagger slice 3 Task 2: signature widened to carry the score.
        //
        // SCOPE BOUNDARY — the score stops here. BoundingBoxData deliberately gains no
        // score field in slice 3. Spec line 73 assigns per-label score refresh and
        // rendering "<class> — <confidence>%" to SLICE 5, which also moves per-label
        // state out of RectTransforms. Carrying it further now would eat that work and
        // couple this slice to state slice 5 is about to relocate.
        public void DrawUIBoxes(List<(int classId, Vector4 boundingBox, float score)> detections, Vector2 inputSize, Pose cameraPose)
        {
            Vector2 currentResolution = m_cameraAccess.CurrentResolution;

            if (detections.Count == 0)
            {
                OnObjectsDetected?.Invoke(0);
                return;
            }

            OnObjectsDetected?.Invoke(detections.Count);
            m_detectionsSeen += detections.Count;

            // Draw the bounding boxes
            for (var i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                float x1 = detection.boundingBox[0];
                float y1 = detection.boundingBox[1];
                float x2 = detection.boundingBox[2];
                float y2 = detection.boundingBox[3];
                Rect rect = new Rect(x1, y1, x2 - x1, y2 - y1);
                // Rect rect = Rect.MinMaxRect(x1, y1, x2, y2); // todo

                Vector2 normalizedCenter = rect.center / inputSize;
                Vector2 center = currentResolution * (normalizedCenter - Vector2.one * 0.5f);

                // Get the object class name
                var classname = LabelFor(detection.classId);

                // Get the 3D marker world position using Depth Raycast.
                //
                // Slice 4 Task 3: probes m_depthSamplesPerDetection rays around the box
                // centre and takes the MEDIAN by distance. DEFAULT IS 1, which is exactly
                // upstream's single centre ray — the mechanism is built but not pulled,
                // because the plan requires measuring the current offset on device before
                // changing placement. Raise the sample count only with a baseline to
                // improve against.
                var sampleStatus = ProbeDepth(normalizedCenter, cameraPose, out var resolvedPoint);

                // Object Tagger slice 2 Task 5: count attempts, not just failures.
                // Upstream logs raycast FAILURES and never a total, so no failure RATE is
                // derivable -- slice 1 recorded 10 absolute failures with no denominator.
                //
                // ONE DETECTION IS ONE ATTEMPT regardless of how many rays it took. If
                // attempts counted rays, moving from 1 to 5 samples would multiply the
                // denominator fivefold and make the rate incomparable with slice 2's
                // recorded 0.9%. The rate answers "how often did a DETECTION fail to
                // resolve", which is the question slice 4's gate asks.
                m_raycastAttempts++;

                // Slice 4 Task 1 Step 3: the two failure causes are counted SEPARATELY.
                //
                // Slice 2 reported 0.9% and that number assumed every miss was a
                // per-detection miss. Folding subsystem-unavailable frames into the same
                // counter would inflate the numerator toward 100% and produce a figure
                // that describes nothing -- "depth is bad" when the truth is "depth is off".
                if (sampleStatus == DepthResolveStatus.SubsystemUnavailable)
                {
                    m_depthSubsystemUnavailable++;
                    continue;
                }

                var depth = sampleStatus == DepthResolveStatus.Hit
                    ? DepthResolveResult.Hit(resolvedPoint)
                    : DepthResolveResult.Miss();

                if (!depth.IsHit)
                {
                    m_raycastMisses++;
                    // The individual rays now live inside ProbeDepth, so log the box
                    // centre that generated them instead. More useful anyway: it says
                    // WHERE on screen the detection failed to resolve, which is what a
                    // depth-miss investigation actually needs.
                    Debug.Log($"[ObjectTagger] depth miss for '{classname}' at normalizedCenter:{normalizedCenter}, " +
                              $"samples:{m_depthSamplesPerDetection}, cameraPose:{cameraPose}");
                    continue;
                }

                var worldPos = (Vector3?)depth.Point;
                var normRect = new Rect(
                    rect.x / inputSize.x,
                    1f - rect.yMax / inputSize.y,
                    rect.width / inputSize.x,
                    rect.height / inputSize.y
                );

                // Calculate distance and center point first
                float distance = Vector3.Distance(cameraPose.position, worldPos.Value);
                var worldSpaceCenter = m_cameraAccess.ViewportPointToRay(normRect.center, cameraPose).GetPoint(distance);
                var normal = (worldSpaceCenter - cameraPose.position).normalized;

                // Intersect corner rays with the plane perpendicular to the camera view
                var plane = new Plane(normal, worldSpaceCenter);
                var minRay = m_cameraAccess.ViewportPointToRay(normRect.min, cameraPose);
                var maxRay = m_cameraAccess.ViewportPointToRay(normRect.max, cameraPose);
                plane.Raycast(minRay, out float intersectionDistanceMin);
                plane.Raycast(maxRay, out float intersectionDistanceMax);
                var min = minRay.GetPoint(intersectionDistanceMin);
                var max = maxRay.GetPoint(intersectionDistanceMax);

                // Transform world-space positions to camera's local space to get 2D size
                var topLeftLocal = Quaternion.Inverse(cameraPose.rotation) * (min - cameraPose.position);
                var bottomRightLocal = Quaternion.Inverse(cameraPose.rotation) * (max - cameraPose.position);
                var size = new Vector2(
                    Mathf.Abs(bottomRightLocal.x - topLeftLocal.x),
                    Mathf.Abs(bottomRightLocal.y - topLeftLocal.y));

                var boxData = GetOrCreateBoundingBoxData(detection.classId, worldSpaceCenter, size);
                var boxRectTransform = boxData.BoxRectTransform;
                boxRectTransform.GetComponentInChildren<Text>().text = $"Id: {detection.classId} Class: {classname} Center (px): {center:0.0} Center (%): {normalizedCenter:0.0}";
                boxRectTransform.SetPositionAndRotation(worldSpaceCenter, Quaternion.LookRotation(normal));
                boxRectTransform.sizeDelta = size;
                boxData.lastUpdateTime = Time.time;
            }
        }

        private BoundingBoxData GetOrCreateBoundingBoxData(int classId, Vector3 worldSpaceCenter, Vector2 worldSpaceSize)
        {
            BoundingBoxData reusedBox = null;
            for (int i = m_boxDrawn.Count - 1; i >= 0; i--)
            {
                var box = m_boxDrawn[i];
                var localPos = box.BoxRectTransform.InverseTransformPoint(worldSpaceCenter);
                var newBox = new Vector4(
                    localPos.x - worldSpaceSize.x * 0.5f,
                    localPos.y - worldSpaceSize.y * 0.5f,
                    localPos.x + worldSpaceSize.x * 0.5f,
                    localPos.y + worldSpaceSize.y * 0.5f
                );

                var sizeDelta = box.BoxRectTransform.sizeDelta;
                var currentBox = new Vector4(
                    -sizeDelta.x * 0.5f,
                    -sizeDelta.y * 0.5f,
                    sizeDelta.x * 0.5f,
                    sizeDelta.y * 0.5f);

                if (box.ClassId == classId)
                {
                    // If the new box overlaps with an existing one of the same class, reuse it
                    if (SentisInferenceRunManager.CalculateIoU(newBox, currentBox) > 0f)
                    {
                        if (reusedBox == null)
                        {
                            reusedBox = box;
                        }
                        else
                        {
                            // Same overlapping class - remove the existing box
                            ReturnToPool(box);
                            m_boxDrawn.RemoveAt(i);
                        }
                    }
                }
                // If the new box's IoU with another class is significant, remove the existing box
                else if (SentisInferenceRunManager.CalculateIoU(newBox, currentBox) > 0.1f)
                {
                    // Different overlapping class - remove the existing box
                    ReturnToPool(box);
                    m_boxDrawn.RemoveAt(i);
                }
            }

            if (reusedBox != null)
            {
                return reusedBox;
            }

            // Create a new box
            var newData = GetBoxFromPoolOrCreate();
            newData.ClassId = classId;
            newData.ClassName = LabelFor(classId);
            m_boxDrawn.Add(newData);
            return newData;
        }

        private BoundingBoxData GetBoxFromPoolOrCreate()
        {
            if (m_boxPool.Count > 0)
            {
                var pooled = m_boxPool[m_boxPool.Count - 1];
                pooled.BoxRectTransform.gameObject.SetActive(true);
                m_boxPool.RemoveAt(m_boxPool.Count - 1);
                return pooled;
            }

            var boxRectTransform = Instantiate(m_detectionBoxPrefab, ContentParent);
            boxRectTransform.gameObject.SetActive(true);
            return new BoundingBoxData
            {
                BoxRectTransform = boxRectTransform
            };
        }

        internal Transform ContentParent => m_detectionBoxPrefab.parent;

        private void ReturnToPool(BoundingBoxData box)
        {
            box.BoxRectTransform.gameObject.SetActive(false);
            m_boxPool.Add(box);
        }

        internal void ClearAnnotations()
        {
            foreach (var box in m_boxDrawn)
            {
                ReturnToPool(box);
            }
            m_boxDrawn.Clear();
        }
    }
}
