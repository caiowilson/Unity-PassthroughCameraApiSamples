// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
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

        // Object Tagger slice 5 Task 1: LabelRecord itself lives in its own file
        // (LabelRecord.cs) as a top-level PUBLIC type, not nested here — that is what
        // lets edit-mode tests reach it without InternalsVisibleTo, matching how
        // DepthResolveResult/DetectionDecoder/DetectionProjection are already set up in
        // this project. See that file for the record's field-by-field rationale.

        // Object Tagger slice 5 Task 1: pairs a LabelRecord (state) with the
        // RectTransform that currently renders it. The RectTransform is a VIEW driven
        // FROM the record every frame in DrawUIBoxes — the record is never derived
        // from it. Task 4 retires this view; until then it is the only consumer of
        // LabelRecord's WorldPosition.
        //
        // Deliberately a private pairing wrapper rather than a public List<LabelRecord>
        // plus a parallel List<RectTransform>: two lists kept in sync by index drift
        // apart the moment one is reordered or removed from mid-list (exactly what
        // GetOrCreateBoxView's removal-during-iteration does below), and LabelRecord
        // itself must not hold a RectTransform. The pairing wrapper is what makes both
        // constraints satisfiable at once. Task 2 moved matching off the
        // RectTransform entirely (class+world-distance over LabelRecord.WorldPosition,
        // see LabelAssociation.cs) — the RectTransform here is now pure output, never
        // read for matching.
        private class BoxView
        {
            public LabelRecord Record;
            public RectTransform RectTransform;
        }

        // Object Tagger slice 5 Task 1: the grace period ceiling.
        //
        // The project's behavioural spec caps how long an unseen label may persist at
        // 3 seconds — a hard acceptance-criterion ceiling, not a preference. Task 2's
        // re-placement rule and Task 3's formal expiry both need this same number;
        // defining it once here (instead of two independent copies) is what keeps them
        // from drifting apart. Private is enough: only sibling code in this file reads
        // it, and later tasks land in this same file.
        private const float GracePeriodSeconds = 3f;

        // Object Tagger slice 5 Task 2 — the association distance threshold.
        //
        // The acceptance procedure places objects 1.5-2.5m from the camera; the
        // plan calls for a threshold "in the tens of centimetres". 0.3m is chosen
        // as the balance point: too large (e.g. 0.5m+) risks merging two
        // neighbouring same-class objects into one label — a pair of chairs at a
        // table is commonly closer together than that. Too small spawns
        // duplicate labels for the SAME object as the depth/position estimate
        // jitters frame to frame — slice 4's validation record
        // (docs/validation/2026-08-15-slice-4-spatial-placement.md) settled on
        // "AIMS CORRECTLY and JITTERS BADLY": mean accuracy ~2cm, individual
        // placements scatter +/-13cm (its "decisive measurement", corrected
        // result). 0.3m clears that with margin. One disputed, permanently-
        // unresolved reading in the same record (the mouse-precision conflict,
        // D-slice4-2) puts one class's scatter at 27.5cm on 6 samples — the
        // record itself says the raw data to settle which figure is right no
        // longer exists. 0.3m does NOT clear that disputed figure. Chosen
        // anyway because it is disputed rather than confirmed, and because a
        // threshold large enough to clear 27.5cm (e.g. 0.5m+) reopens the
        // same-class-merging risk above. Revisit if a future device run
        // reproduces scatter near that magnitude for a tracked class.
        private const float AssociationDistanceMeters = 0.3f;

        private readonly List<BoxView> m_boxViews = new();
        private string[] m_labels;
        private readonly List<BoxView> m_boxViewPool = new();

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
                      $"raysPerDetection={raysPerDetection} boxesDrawn={m_boxViews.Count}");
        }

        private void Update()
        {
            LogCountsIfDue();

            // Remove boxes that haven't been updated recently
            for (int i = m_boxViews.Count - 1; i >= 0; i--)
            {
                var view = m_boxViews[i];
                if (Time.time - view.Record.LastSeenTime > GracePeriodSeconds)
                {
                    ReturnToPool(view);
                    m_boxViews.RemoveAt(i);
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
        // SCOPE BOUNDARY — the score stops at the RENDERED VIEW, not at this method.
        // Slice 5 Task 1 gives LabelRecord a LastAssociatedScore field (spec line 73
        // assigns per-label score refresh and rendering "<class> — <confidence>%" to
        // slice 5), but per Task 1 Step 3 the RectTransform view below must not read or
        // display it — that is Task 4's job. Rendering it early would make this a
        // rendering change instead of the pure state change Task 1 is meant to be.
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

                var view = GetOrCreateBoxView(detection.classId, worldSpaceCenter);

                // Object Tagger slice 5 Task 1 Step 1: update the RECORD first — this is
                // the state. WorldPosition is the world-space placement Task 2's matching
                // will read instead of RectTransform/IoU. SmoothedPosition just tracks
                // WorldPosition for now; Task 3 owns real smoothing.
                //
                // SCOPE BOUNDARY (Step 3): LastAssociatedScore is stored here so it
                // exists for Task 4, but it must NOT be read by the view below. Slice 3
                // stopped the score at this boundary deliberately.
                var record = view.Record;
                record.WorldPosition = worldSpaceCenter;
                record.SmoothedPosition = worldSpaceCenter;
                record.LastAssociatedScore = detection.score;
                record.LastSeenTime = Time.time;

                // Step 2: the view is a PROJECTION of the record, not the other way
                // round. Everything the RectTransform is set to below is read from
                // `record`, never stored back into it.
                var boxRectTransform = view.RectTransform;
                boxRectTransform.GetComponentInChildren<Text>().text = $"Id: {detection.classId} Class: {classname} Center (px): {center:0.0} Center (%): {normalizedCenter:0.0}";
                boxRectTransform.SetPositionAndRotation(record.WorldPosition, Quaternion.LookRotation(normal));
                boxRectTransform.sizeDelta = size;
            }
        }

        // Object Tagger slice 5 Task 2: class+world-distance matching over
        // LabelRecord.WorldPosition, replacing Task 1's IoU/RectTransform box-extent
        // matching. The decision itself is pure logic in LabelAssociation.Decide —
        // this method's job is just to snapshot m_boxViews into that function's
        // input shape and act on the Decision it returns.
        private BoxView GetOrCreateBoxView(int classId, Vector3 worldSpaceCenter)
        {
            var existing = new LabelAssociation.Existing[m_boxViews.Count];
            for (var i = 0; i < m_boxViews.Count; i++)
            {
                var record = m_boxViews[i].Record;
                existing[i] = new LabelAssociation.Existing(record.ClassId, record.WorldPosition, record.LastSeenTime);
            }

            var decision = LabelAssociation.Decide(
                existing, classId, worldSpaceCenter, Time.time, AssociationDistanceMeters, GracePeriodSeconds);

            // Decision.AssociatedIndex/RemoveIndex are mutually exclusive (see
            // LabelAssociation.Decide), so removing here never invalidates an
            // AssociatedIndex we're about to use below.
            if (decision.RemoveIndex >= 0)
            {
                // Object Tagger slice 5 Task 2 Step 3 (alpha-scope.md re-placement
                // rule): this same-class label is now farther than the association
                // threshold but still inside its grace period. Left alone it would
                // linger up to GracePeriodSeconds after the object it tracked has
                // already moved elsewhere and spawned a new label there — briefly
                // showing two labels for one object. Removing it immediately here
                // is what the spec asks for instead of waiting on Update()'s expiry.
                var staleView = m_boxViews[decision.RemoveIndex];
                ReturnToPool(staleView);
                m_boxViews.RemoveAt(decision.RemoveIndex);
            }

            if (decision.AssociatedIndex >= 0)
            {
                return m_boxViews[decision.AssociatedIndex];
            }

            // Object Tagger slice 5 Task 2: different-class overlap in space is
            // deliberately NOT handled here — the locked decision is that both
            // labels stay visible and the farther one gets a visual offset, which
            // is Task 5's job. This method never inspects, evicts, or otherwise
            // touches any other-class BoxView.

            // Create a new box, backed by a fresh LabelRecord with its own session id.
            var newView = GetViewFromPoolOrCreate();
            newView.Record.SessionId = Guid.NewGuid();
            newView.Record.ClassId = classId;
            newView.Record.ClassName = LabelFor(classId);
            newView.Record.ConfirmationCount = 0;
            m_boxViews.Add(newView);
            return newView;
        }

        private BoxView GetViewFromPoolOrCreate()
        {
            if (m_boxViewPool.Count > 0)
            {
                var pooled = m_boxViewPool[m_boxViewPool.Count - 1];
                pooled.RectTransform.gameObject.SetActive(true);
                m_boxViewPool.RemoveAt(m_boxViewPool.Count - 1);
                return pooled;
            }

            var boxRectTransform = Instantiate(m_detectionBoxPrefab, ContentParent);
            boxRectTransform.gameObject.SetActive(true);
            return new BoxView
            {
                RectTransform = boxRectTransform,
                Record = new LabelRecord()
            };
        }

        internal Transform ContentParent => m_detectionBoxPrefab.parent;

        private void ReturnToPool(BoxView view)
        {
            view.RectTransform.gameObject.SetActive(false);
            m_boxViewPool.Add(view);
        }

        internal void ClearAnnotations()
        {
            foreach (var view in m_boxViews)
            {
                ReturnToPool(view);
            }
            m_boxViews.Clear();
        }
    }
}
