// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        // FROM the record — the record is never derived from it. Final-review fix:
        // that projection now happens every FRAME (RefreshLabelViews, called from
        // Update()), not once per DrawUIBoxes call at inference cadence — see
        // RefreshLabelViews's comment for why that distinction was the whole point
        // of this fix wave. Task 4 retires this view; until then it is the only
        // consumer of LabelRecord's WorldPosition.
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
        // Object Tagger slice 5 Task 4: Label is resolved ONCE, in
        // GetViewFromPoolOrCreate, and cached here rather than looked up per-frame
        // via GetComponentInChildren<Text>() inside DrawUIBoxes. That per-frame
        // lookup would have been broken: DrawUIBoxes calls SetActive(false) on
        // unconfirmed views (Task 3's visibility gate), the source prefab itself
        // is already inactive by the time any clone is made (see Awake()), and
        // the no-argument GetComponentInChildren<T>() overload defaults
        // includeInactive to false — so a per-frame lookup would return null on
        // every still-unconfirmed label and throw a NullReferenceException on
        // `.text`. Resolving once with includeInactive:true at creation time
        // (see GetViewFromPoolOrCreate) sidesteps that entirely, and is still a
        // VIEW-side cache, not new state: Label is derived once from the
        // prefab's fixed hierarchy, never written back to LabelRecord.
        private class BoxView
        {
            public LabelRecord Record;
            public RectTransform RectTransform;
            public Text Label;
        }

        // Object Tagger slice 5 Task 2 — the association distance threshold.
        //
        // 0.10m (10cm), set by explicit user ruling on 2026-08-16 during
        // Task 6's device gate. See D-slice5-4 in the docs repo's validation
        // record, ~/Work/object-tagger/docs/validation/
        // 2026-08-15-slice-5-labels.md. This SUPERSEDES the prior 2.00m
        // value (D-slice5-2): the on-device Task 6 Step 2 check that value's
        // own comment called for found that two same-class objects (two
        // drinking glasses) merged into one label at close range, and the
        // user clarified directly that the earlier "2.00m" ruling — despite
        // being given and reconfirmed twice — was itself a misunderstanding.
        // There is no separate "detection range" concept; this is the only
        // threshold, and 0.10m is what the user wants it to be. Not a
        // re-derivation from the plan's own advisory guidance ("tens of
        // centimetres") either — asked for directly, as an exact number,
        // after the plan's vague phrasing contributed to the earlier 0.3m
        // vs. 2.00m confusion.
        //
        // Known, accepted risk of this value, flagged before the user chose
        // it and confirmed anyway: slice 4's device-measured frame-to-frame
        // position jitter was +/-13cm (docs/validation/
        // 2026-08-15-slice-4-spatial-placement.md, its decisive corrected
        // reading), which EXCEEDS this 0.10m threshold. That measurement
        // predates this slice's position smoothing (SmoothingFactor,
        // below), so it is not a direct read on post-smoothing jitter, but
        // it is the closest real figure available. If a single static
        // object's own re-detections drift by more than 10cm, they could
        // fail to re-associate with their own label, producing duplicate or
        // flickering labels on one object — the opposite failure mode from
        // the over-merging this value fixes. Re-verify specifically for
        // this at the next on-device check.
        private const float AssociationDistanceMeters = 0.1f;

        // Object Tagger slice 5 Task 3: position smoothing factor.
        //
        // Final-review fix (Important #2): the comment previously said "each frame,
        // SmoothedPosition = Lerp(...)". That was wrong about WHEN smoothing runs:
        // LabelPresentation.Smooth is called once per ACCEPTED ASSOCIATION (i.e. once
        // per DrawUIBoxes call that re-detects this label), which happens at inference
        // cadence -- the same placeholder ~1 inference/sec worst-case assumption
        // ConfirmationsBeforeVisible's comment uses -- not at frame rate (which can be
        // tens of times faster). Corrected: SmoothedPosition = Lerp(SmoothedPosition,
        // WorldPosition, SmoothingFactor) is applied once per accepted association.
        //
        // Factor should be in (0, 1): smaller values smooth more heavily, larger
        // values respond faster. With Lerp, factor in (0,1) cannot overshoot or
        // oscillate.
        //
        // VALUE RAISED FROM 0.2 TO 0.5 as part of this same fix. At the assumed 1Hz
        // worst-case cadence, 0.2 takes 1-(0.8)^n to close n iterations' worth of a
        // position error: ~7 iterations (~7 seconds of continuous re-detection) to
        // close 80% of it -- well past the point a label is even required to be
        // visible or retained, per slice 4's validation record
        // (docs/validation/2026-08-15-slice-4-spatial-placement.md), which measured
        // real frame-to-frame position jitter of +/-13cm (its decisive corrected
        // reading; one disputed, unresolved reading in the same record puts one
        // class as high as 27.5cm, D-slice4-2). At 0.2, that much jitter stays
        // visibly "crawling" toward its settled position for several seconds after a
        // label becomes visible or is re-placed -- longer than alpha-scope.md's "a
        // few seconds" latency budget comfortably allows. 0.5 closes 1-(0.5)^n of the
        // error per n iterations: 87.5% within 3 iterations (~3s at the same
        // placeholder cadence), which fits that budget with real margin, while still
        // meaningfully damping frame-to-frame jitter (each association still halves
        // the remaining error, not a full jump to the latest noisy estimate). Revisit
        // together with the cadence assumption itself at Task 6's device gate, once a
        // real inference cadence and real jitter figures exist to compute against.
        private const float SmoothingFactor = 0.5f;

        // Object Tagger slice 5 Task 4 Step 3 — minimum-apparent-size reference
        // distance and base scale.
        //
        // Legibility at 1m AND 4m is an acceptance criterion (alpha-scope.md). A
        // fixed world size alone fails it: apparent size shrinks with distance, so
        // a card sized to read comfortably up close goes illegible at 4m. The card's
        // prefab is authored in world-meter units (dot diameter, text cap height)
        // calibrated to read comfortably at ReferenceDistanceMeters -- the NEAR end
        // of the range, chosen deliberately: a card sized for the far end (4m)
        // would look oversized at 1m, while one sized for the near end and then
        // scaled UP as the camera backs away (LabelPresentation.ComputeCardScale)
        // holds that same apparent size out to 4m and beyond. BaseCardScale is the
        // multiplier at/below ReferenceDistanceMeters -- 1x, i.e. the prefab's
        // authored size IS the near-distance size, nothing scales it down further.
        //
        // Both are a reasoned starting point, not a confirmed one: no on-device
        // measurement has ever assessed legibility on the actual Quest 3 display at
        // either distance. MUST be confirmed by human judgment at Task 6's device
        // gate, exactly like Task 3's cadence assumption -- revisit both constants
        // and the prefab's authored sizes together if that check fails.
        private const float ReferenceDistanceMeters = 1f;
        private const float BaseCardScale = 1f;

        // Object Tagger slice 5 Task 5 Step 1 — overlap detection threshold.
        //
        // The card's rendered visual footprint (not the layout RectTransform's
        // 0.2m x 0.2m bounding box, which is mostly empty margin) is: text cap
        // height = font size 50 x the text RectTransform's local scale 0.0005 =
        // 0.025m (2.5cm), and the anchor dot is 0.02m (2cm) across, so the
        // card's angular height at ReferenceDistanceMeters (1m) is
        // 2*atan(0.025/1) =~ 2.86deg. 3deg is chosen as "about one card-height" --
        // two label centres closer together than that in the camera's view are
        // close enough for their cards to visually collide. Because
        // LabelPresentation.ComputeCardScale holds the card's APPARENT size
        // constant for every distance beyond ReferenceDistanceMeters (that is
        // the entire point of that function -- see its own comment), this same
        // 3deg figure applies unchanged across the whole 1-4m acceptance range;
        // it needs no distance term, unlike the offset amount below which does.
        //
        // KNOWN LIMITATION -- this threshold is derived from the card's
        // HEIGHT (~2.86deg, above), but the card is far WIDER than it is
        // tall: legacy Text with m_HorizontalOverflow allowing unconstrained
        // width, cap height 0.025m, and a string like "cell phone — 45%" at
        // roughly 17 characters renders to something in the neighbourhood of
        // 0.2m wide -- an angular WIDTH around 11-12deg at 1m, roughly 4x this
        // 3deg threshold. Vector3.Angle (see LabelOverlap.AngularSeparationDegrees)
        // is a CONE test around the camera and does not distinguish horizontal
        // from vertical crowding, so two labels separated horizontally by, say,
        // 5deg -- close enough that their TEXT visibly overlaps -- are NOT
        // flagged by this test at all (5deg > 3deg threshold). This is an
        // UNDER-trigger for horizontal crowding, not an over-trigger: once a
        // pair IS flagged, a single vertical push does reliably clear it (the
        // push's own angular effect, offsetAtReferenceDistance/referenceDistance
        // in radians =~ 3.4deg regardless of distance, combines with any
        // existing horizontal separation via combining as a rough right-angle
        // sum, so the resulting separation only grows). Not corrected here:
        // raising the threshold to card-WIDTH would flag most same-row pairs
        // and cause aggressive, over-eager stacking, and is unvalidated
        // against the task brief's stated two-label scenario. Left as a known
        // gap for Task 6's device gate to look for (horizontally adjacent text
        // that visually overlaps but was never offset).
        private const float OverlapAngleThresholdDegrees = 3f;

        // Object Tagger slice 5 Task 5 Step 1 — overlap offset amount.
        //
        // 0.06m (6cm) at ReferenceDistanceMeters clears the card's ~5cm visible
        // footprint (2.5cm text + 2cm dot + the 0.03m anchored gap between them,
        // see OverlapAngleThresholdDegrees's comment) with a small margin. Like
        // the threshold above, this is scaled by ComputeCardScale per farther
        // label in LabelOverlap.ComputeVerticalOffsets (using the SAME
        // BaseCardScale/ReferenceDistanceMeters constants the card's own render
        // scale uses) so the offset's ANGULAR effect stays constant beyond
        // ReferenceDistanceMeters too -- a fixed 0.06m world-space push would
        // under-clear a card whose own world-space size has grown 4x by 4m.
        private const float OverlapOffsetMeters = 0.06f;

        // Object Tagger manual-tagging Task 3 — ghost/preview dimming.
        //
        // The live candidate's preview reuses the exact same pooled BoxView
        // prefab as a committed label, distinguished only by reduced opacity
        // on every Graphic (Text and the anchor-dot Image) found under its
        // RectTransform, applied once at creation. No new prefab/material is
        // needed.
        private const float GhostAlphaMultiplier = 0.5f;

        private static readonly Vector2 FrameCenter = new Vector2(0.5f, 0.5f);

        // Object Tagger manual-tagging Task 3 — the live candidate: whichever
        // detection's image-space box center is nearest the frame center this
        // tick, resolved to a world position. Null when nothing qualifies
        // (no detections, or the nearest-to-center detection's depth probe
        // missed this tick) — the ghost simply doesn't render that tick, per
        // the design's "can appear and vanish frame-to-frame" requirement.
        private readonly struct LiveCandidateState
        {
            public readonly int ClassId;
            public readonly string ClassName;
            public readonly Vector3 WorldPosition;
            public readonly float Score;

            public LiveCandidateState(int classId, string className, Vector3 worldPosition, float score)
            {
                ClassId = classId;
                ClassName = className;
                WorldPosition = worldPosition;
                Score = score;
            }
        }

        private LiveCandidateState? m_liveCandidate;

        /// A single dedicated, never-pooled BoxView for the ghost preview.
        /// Lazily created on first use. Never passed to ReturnToPool — unlike
        /// every entry in m_boxViews, this instance is permanent for the
        /// component's lifetime, so a dimmed ghost view can never leak back
        /// into m_boxViewPool and be reused (undimmed callers expect) for a
        /// real committed label.
        private BoxView m_ghostView;

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

            // Nothing to refresh -- skip the camera-pose query entirely
            // rather than pay for it (and its DllImport call) every frame
            // when there is no committed label and no live candidate.
            if (m_boxViews.Count == 0 && !m_liveCandidate.HasValue)
            {
                return;
            }

            if (TryGetCurrentCameraPose(out var cameraPose))
            {
                RefreshLabelViews(cameraPose.position);
            }
            // else: the current head pose is not reliable this frame (see
            // TryGetCurrentCameraPose). Every view simply keeps whatever
            // rotation/scale/position it last had.
        }

        // Object Tagger slice 5 final-review fix — mirrors the exact reliability
        // check SentisInferenceRunManager.RunInference already performs before
        // trusting m_cameraAccess.GetCameraPose() (see that method, ~line 244-252).
        //
        // WHY THIS CHECK EXISTS: PassthroughCameraAccess.GetCameraPose() calls
        // OVRPlugin.GetNodePoseStateAtTime internally, but does NOT itself verify
        // that call succeeded — on failure, the underlying OVRPlugin wrapper returns
        // PoseStatef.identity (confirmed by reading OVRPlugin.cs in the installed
        // com.meta.xr.sdk.core package), i.e. a pose at the WORLD ORIGIN with
        // identity rotation, not the last-known-good pose and not an exception.
        // GetCameraPose() then applies the lens offset to that and returns it
        // looking like an ordinary, valid Pose — there is no way to distinguish a
        // real near-origin head pose from a failed query by inspecting the return
        // value alone.
        //
        // Calling GetCameraPose() every frame without this guard would risk
        // occasionally computing this frame's label rotation/scale/position from
        // that garbage origin pose whenever the underlying native query has a
        // transient failure — a visible one-frame snap/flicker toward the origin,
        // exactly the kind of glitch this fix wave exists to eliminate, not
        // reintroduce. SentisInferenceRunManager already established the fix for
        // this failure mode (skip this frame's work entirely); this method applies
        // the identical pattern here rather than inventing a second one.
        //
        // Cost/safety of calling this every frame: the underlying native call is a
        // single head-pose query — Quest apps routinely query head pose once per
        // render frame as a matter of course, so this is not a new category of cost,
        // just an additional call site for one that already happens elsewhere in the
        // same frame's pipeline (camera rendering itself).
        private bool TryGetCurrentCameraPose(out Pose pose)
        {
            pose = default;

            if (!m_cameraAccess.IsPlaying)
            {
                return false;
            }

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);
            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                return false;
            }

            pose = m_cameraAccess.GetCameraPose();
            return true;
        }

        // Object Tagger slice 5 final-review fix, simplified by manual-
        // tagging Task 3 — per-frame billboard/scale pass.
        //
        // Runs every frame (from Update(), guarded by TryGetCurrentCameraPose)
        // over every view in m_boxViews. The SetActive visibility gate from
        // slice 5 is gone: everything in m_boxViews is, by construction, a
        // committed label and is always visible from the moment it exists
        // until TryUntagNearestToCenter or ClearAnnotations removes it.
        private void RefreshLabelViews(Vector3 cameraPosition)
        {
            foreach (var view in m_boxViews)
            {
                var record = view.Record;
                var boxRectTransform = view.RectTransform;

                boxRectTransform.rotation = LabelPresentation.FaceCameraRotation(record.SmoothedPosition, cameraPosition);

                var cardDistance = Vector3.Distance(cameraPosition, record.SmoothedPosition);
                var cardScale = LabelPresentation.ComputeCardScale(cardDistance, BaseCardScale, ReferenceDistanceMeters);
                boxRectTransform.localScale = Vector3.one * cardScale;
            }

            RefreshGhostView(cameraPosition);

            ApplyOverlapOffsets(cameraPosition);
        }

        /// Object Tagger slice 3 Task 4: the single guarded label lookup.
        ///
        /// Upstream indexed m_labels at TWO sites with no bounds check. An out-of-range
        /// classId threw IndexOutOfRange from deep inside the draw loop, which on device
        /// reads as "detection stopped working" rather than "the labels file is wrong".
        // Object Tagger slice 5 final-review fix (Minor #9): returns the RAW class
        // name, unmangled. Previously this replaced spaces with underscores here and
        // the render site (DrawUIBoxes) replaced them back for display — a lossy
        // round-trip for any class name containing a genuine underscore (harmless
        // for the actual COCO class list this project uses, but LabelRecord.ClassName
        // is meant to be this record's source of truth, and a mangled value sitting
        // in a "source of truth" field is worse than it needs to be). The record now
        // stores the real name directly. The one call site that still wants the
        // underscore form for its log line (the depth-miss log in DrawUIBoxes)
        // mangles it locally, right there, instead of relying on this method to do
        // it globally.
        private string LabelFor(int classId)
        {
            if (m_labels == null || classId < 0 || classId >= m_labels.Length)
            {
                Debug.LogError($"[ObjectTagger] class id {classId} is outside the label range (labels={m_labels?.Length ?? 0}).");
                return $"class_{classId}";
            }
            return m_labels[classId];
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

        // Object Tagger slice 3 Task 2, rewritten by manual-tagging Task 3 —
        // per-detection loop that ONLY associates into already-committed
        // labels; it never spawns a new one. Spawning happens exclusively at
        // explicit commit (TryCommitLiveCandidate). This method's other job,
        // unrelated to commit/associate, is picking this tick's live
        // candidate (nearest-to-frame-center detection) for ghost preview.
        public void DrawUIBoxes(List<(int classId, Vector4 boundingBox, float score)> detections, Vector2 inputSize, Pose cameraPose)
        {
            if (detections.Count == 0)
            {
                OnObjectsDetected?.Invoke(0);
                m_liveCandidate = null;
                return;
            }

            OnObjectsDetected?.Invoke(detections.Count);
            m_detectionsSeen += detections.Count;

            // First pass: image-space rects and normalized centers for every
            // detection, plus which one is nearest the frame center. Kept
            // separate from the per-detection work below so NearestSelection
            // sees every candidate before any of them is processed.
            var rects = new List<Rect>(detections.Count);
            var normalizedCenters = new List<Vector2>(detections.Count);
            var centerDistances = new List<float>(detections.Count);
            for (var i = 0; i < detections.Count; i++)
            {
                var box = detections[i].boundingBox;
                var rect = new Rect(box.x, box.y, box.z - box.x, box.w - box.y);
                rects.Add(rect);
                var normalizedCenter = rect.center / inputSize;
                normalizedCenters.Add(normalizedCenter);
                centerDistances.Add(Vector2.Distance(normalizedCenter, FrameCenter));
            }
            var liveCandidateDetectionIndex = NearestSelection.IndexOfMinimum(centerDistances);

            LiveCandidateState? newLiveCandidate = null;

            // Draw the bounding boxes
            for (var i = 0; i < detections.Count; i++)
            {
                var detection = detections[i];
                Rect rect = rects[i];
                Vector2 normalizedCenter = normalizedCenters[i];

                // Get the object class name
                var classname = LabelFor(detection.classId);

                // Get the 3D marker world position using Depth Raycast.
                var sampleStatus = ProbeDepth(normalizedCenter, cameraPose, out var resolvedPoint);

                m_raycastAttempts++;

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
                    Debug.Log($"[ObjectTagger] depth miss for '{classname.Replace(' ', '_')}' at normalizedCenter:{normalizedCenter}, " +
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

                float distance = Vector3.Distance(cameraPose.position, worldPos.Value);
                var worldSpaceCenter = m_cameraAccess.ViewportPointToRay(normRect.center, cameraPose).GetPoint(distance);

                // Associate into an existing committed label only -- never
                // spawn here. A detection that matches nothing is simply not
                // tracked unless the user commits it.
                var associatedIndex = FindAssociatedViewIndex(detection.classId, worldSpaceCenter);
                if (associatedIndex >= 0)
                {
                    var view = m_boxViews[associatedIndex];
                    var record = view.Record;
                    record.WorldPosition = worldSpaceCenter;
                    record.SmoothedPosition = LabelPresentation.Smooth(record.SmoothedPosition, worldSpaceCenter, SmoothingFactor);
                    record.LastAssociatedScore = detection.score;
                    view.Label.text = $"{record.ClassName} — {Mathf.RoundToInt(record.LastAssociatedScore * 100)}%";
                }

                if (i == liveCandidateDetectionIndex)
                {
                    var smoothedGhostPosition = m_liveCandidate.HasValue
                        ? LabelPresentation.Smooth(m_liveCandidate.Value.WorldPosition, worldSpaceCenter, SmoothingFactor)
                        : worldSpaceCenter;
                    newLiveCandidate = new LiveCandidateState(detection.classId, classname, smoothedGhostPosition, detection.score);
                }
            }

            m_liveCandidate = newLiveCandidate;
        }

        // Object Tagger slice 5 Task 5 Step 1, simplified by manual-tagging
        // Task 3 — applies LabelOverlap's pairwise vertical-offset resolution
        // to every committed view's rendered position.
        //
        // No separate visibleViews filter is needed any more: m_boxViews IS
        // the visible set now that the confirmation gate is gone.
        private void ApplyOverlapOffsets(Vector3 cameraPosition)
        {
            if (m_boxViews.Count == 0)
            {
                return;
            }

            var basePositions = new Vector3[m_boxViews.Count];
            for (var i = 0; i < m_boxViews.Count; i++)
            {
                basePositions[i] = m_boxViews[i].Record.SmoothedPosition;
            }

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                cameraPosition, basePositions, OverlapAngleThresholdDegrees, OverlapOffsetMeters,
                BaseCardScale, ReferenceDistanceMeters);

            for (var i = 0; i < m_boxViews.Count; i++)
            {
                m_boxViews[i].RectTransform.position = basePositions[i] + Vector3.up * offsets[i];
            }
        }

        // Object Tagger manual-tagging Task 3 — the association-only lookup.
        //
        // Snapshots m_boxViews into LabelAssociation's input shape and
        // returns the matching index or -1. Used both by the passive
        // per-detection loop (DrawUIBoxes, associate-only, never spawns) and
        // by TryCommitLiveCandidate (associate-or-spawn: spawns itself when
        // this returns -1).
        private int FindAssociatedViewIndex(int classId, Vector3 worldPosition)
        {
            var existing = new LabelAssociation.Existing[m_boxViews.Count];
            for (var i = 0; i < m_boxViews.Count; i++)
            {
                var record = m_boxViews[i].Record;
                existing[i] = new LabelAssociation.Existing(record.ClassId, record.WorldPosition);
            }

            return LabelAssociation.FindAssociationIndex(existing, classId, worldPosition, AssociationDistanceMeters);
        }

        // Object Tagger manual-tagging Task 3 — A-button/pinch commit.
        //
        // Promotes the current live candidate into a persisted, immediately
        // visible label. Associate-or-spawn: if the candidate is already
        // within AssociationDistanceMeters of an existing committed label of
        // the same class, this re-affirms that label (updates its position/
        // score) instead of creating a duplicate. Returns false if there is
        // no live candidate to commit (e.g. nothing detected this tick).
        internal bool TryCommitLiveCandidate()
        {
            if (!m_liveCandidate.HasValue)
            {
                return false;
            }

            var candidate = m_liveCandidate.Value;
            var existingIndex = FindAssociatedViewIndex(candidate.ClassId, candidate.WorldPosition);

            BoxView view;
            if (existingIndex >= 0)
            {
                view = m_boxViews[existingIndex];
                view.Record.SmoothedPosition = LabelPresentation.Smooth(view.Record.SmoothedPosition, candidate.WorldPosition, SmoothingFactor);
            }
            else
            {
                view = GetViewFromPoolOrCreate();
                view.Record.SessionId = Guid.NewGuid();
                view.Record.ClassId = candidate.ClassId;
                view.Record.ClassName = candidate.ClassName;
                view.Record.SmoothedPosition = candidate.WorldPosition;
                m_boxViews.Add(view);
                view.RectTransform.gameObject.SetActive(true);
            }

            view.Record.WorldPosition = candidate.WorldPosition;
            view.Record.LastAssociatedScore = candidate.Score;
            view.Label.text = $"{view.Record.ClassName} — {Mathf.RoundToInt(view.Record.LastAssociatedScore * 100)}%";
            return true;
        }

        // Object Tagger manual-tagging Task 3 — B-button/pinch short-press
        // targeted untag.
        //
        // Removes the single committed label nearest the center of view.
        // "Nearest to center of view" is measured the same way as the live
        // candidate's targeting conceptually, but in camera-angle space
        // (committed labels only have a world position, not an image-space
        // box) via the existing LabelOverlap.AngularSeparationDegrees,
        // comparing each label's direction from the camera against the
        // camera's own forward direction. Returns false if there is nothing
        // to untag or the current camera pose is not reliable this tick.
        internal bool TryUntagNearestToCenter()
        {
            if (m_boxViews.Count == 0 || !TryGetCurrentCameraPose(out var cameraPose))
            {
                return false;
            }

            var cameraForward = cameraPose.rotation * Vector3.forward;
            var angularSeparations = new List<float>(m_boxViews.Count);
            foreach (var view in m_boxViews)
            {
                angularSeparations.Add(LabelOverlap.AngularSeparationDegrees(
                    cameraPose.position, cameraPose.position + cameraForward, view.Record.SmoothedPosition));
            }

            var nearestIndex = NearestSelection.IndexOfMinimum(angularSeparations);
            ReturnToPool(m_boxViews[nearestIndex]);
            m_boxViews.RemoveAt(nearestIndex);
            return true;
        }

        // Object Tagger manual-tagging Task 3 — the ghost/preview view.
        //
        // Reuses GetViewFromPoolOrCreate's exact instantiation path, then
        // dims every Graphic under its RectTransform (Text and the anchor-
        // dot Image) once, at creation, rather than reduce-opacity-per-frame
        // work. The view's LabelRecord is allocated but never used -- the
        // ghost reads from m_liveCandidate, not a LabelRecord -- an accepted
        // minor waste rather than introducing a second, near-duplicate view
        // type just to omit one unused field.
        private BoxView CreateGhostView()
        {
            var view = GetViewFromPoolOrCreate();
            foreach (var graphic in view.RectTransform.GetComponentsInChildren<Graphic>(true))
            {
                var color = graphic.color;
                color.a *= GhostAlphaMultiplier;
                graphic.color = color;
            }
            view.RectTransform.gameObject.SetActive(false);
            return view;
        }

        // Object Tagger manual-tagging Task 3 — per-frame ghost refresh.
        //
        // Mirrors the per-view work in RefreshLabelViews (billboard, scale)
        // for the single ghost view, driven by m_liveCandidate instead of a
        // LabelRecord. Does not participate in ApplyOverlapOffsets -- the
        // ghost may visually overlap a committed label; accepted
        // simplification, flagged for the device gate (Task 6) to observe,
        // not a blocking requirement.
        private void RefreshGhostView(Vector3 cameraPosition)
        {
            if (m_ghostView == null)
            {
                m_ghostView = CreateGhostView();
            }

            if (!m_liveCandidate.HasValue)
            {
                m_ghostView.RectTransform.gameObject.SetActive(false);
                return;
            }

            var candidate = m_liveCandidate.Value;
            m_ghostView.RectTransform.gameObject.SetActive(true);
            m_ghostView.Label.text = $"{candidate.ClassName} — {Mathf.RoundToInt(candidate.Score * 100)}%";
            m_ghostView.RectTransform.rotation = LabelPresentation.FaceCameraRotation(candidate.WorldPosition, cameraPosition);
            var cardDistance = Vector3.Distance(cameraPosition, candidate.WorldPosition);
            var cardScale = LabelPresentation.ComputeCardScale(cardDistance, BaseCardScale, ReferenceDistanceMeters);
            m_ghostView.RectTransform.localScale = Vector3.one * cardScale;
            m_ghostView.RectTransform.position = candidate.WorldPosition;
        }

        private BoxView GetViewFromPoolOrCreate()
        {
            // Object Tagger slice 5 Task 3: do NOT activate here. Visibility is
            // gated on ConfirmationCount — labels start invisible and only become
            // visible once ConfirmationCount >= ConfirmationsBeforeVisible. Final-
            // review fix: that gate is now applied by RefreshLabelViews (Update()'s
            // per-frame visual pass), not DrawUIBoxes — see RefreshLabelViews's
            // comment. Activating here would cause freshly-spawned labels to flash
            // visible for the remainder of the frame before that pass runs. Keeping
            // activation deactivated here keeps the invariant in one place.
            if (m_boxViewPool.Count > 0)
            {
                var pooled = m_boxViewPool[m_boxViewPool.Count - 1];
                // Do not activate; RefreshLabelViews controls visibility.
                m_boxViewPool.RemoveAt(m_boxViewPool.Count - 1);
                return pooled;
            }

            var boxRectTransform = Instantiate(m_detectionBoxPrefab, ContentParent);
            // Object Tagger slice 5 Task 4: resolve the Text component here, with
            // includeInactive:true. The source prefab is already deactivated by
            // Awake() by the time any view is created, so the clone is inactive
            // from the moment it exists -- the no-argument
            // GetComponentInChildren<Text>() overload would return null here, not
            // just later. See the BoxView.Label comment for why this must be
            // cached rather than looked up per-frame.
            var label = boxRectTransform.GetComponentInChildren<Text>(true);
            // Start deactivated; RefreshLabelViews controls visibility.
            boxRectTransform.gameObject.SetActive(false);
            return new BoxView
            {
                RectTransform = boxRectTransform,
                Label = label,
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
