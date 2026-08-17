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
        // 2.00m. This is a direct, explicit user ruling, given twice, not a value
        // derived from engineering analysis of slice 4's scatter data. The plan's
        // guidance was a threshold "in the tens of centimetres"; an earlier,
        // incomplete SDD ledger already recorded the user overriding that guidance
        // to 2.00m after being shown the conflict, but that ledger entry was never
        // acted on — Task 2 was first implemented at 0.3m by an implementer who
        // reasoned independently from slice 4's numbers without ever seeing the
        // ruling. This reopening corrects that miss and restores the user's
        // explicit value.
        //
        // Known consequence, already flagged in that ledger and not yet
        // contradicted by evidence: the acceptance room places same-class objects
        // (e.g. two chairs) within 2m of each other routinely, so a 2.00m
        // threshold will associate them into ONE label instead of two. This is
        // the opposite failure mode from the 0.3m value it replaces, which was
        // chosen specifically to avoid it. Task 6's gate step — "two simultaneous
        // labels, individually legible" — is the empirical test that will surface
        // this if it turns out to be wrong in practice. Do not shrink this value
        // to pass that gate without going back to the user first: it is
        // authority, not oversight.
        private const float AssociationDistanceMeters = 2.00f;

        // Object Tagger slice 5 Task 3: confirmation count before a label becomes visible.
        //
        // Final-review fix (Important #7): the code does NOT implement strict
        // frame-to-frame consecutiveness. record.ConfirmationCount is incremented on
        // every ACCEPTED ASSOCIATION and is never reset except by full expiry
        // (LabelPresentation.IsExpired, gated on GracePeriodSeconds) -- so what this
        // actually implements is "N accepted detections with no gap longer than the
        // full grace period", not "N consecutive detections" in the literal sense.
        // alpha-scope.md's "Label lifecycle" section states: "A label appears after N
        // consecutive accepted detections associate to the same position, so a single
        // spurious detection does not produce a visible label." This is a DELIBERATE,
        // DOCUMENTED DEVIATION from that literal "consecutive" wording, kept rather
        // than changed: a lossy detector will legitimately miss a frame here and
        // there, and resetting progress toward confirmation on a single missed frame
        // would make confirmation less robust for no real benefit -- a genuinely
        // spurious one-off detection still fails to reach the threshold either way,
        // since it has no matching re-detections to associate against at all. This
        // constant defines that threshold.
        //
        // LATENCY ARITHMETIC (Task 3 Step 2, spec line 76):
        // No on-device inference cadence has ever been measured in this project.
        // The configured cadence lever (m_minSecondsBetweenInferences) defaults to 0
        // ("uncapped, matches upstream"), so there is no baseline rate. This arithmetic
        // uses a conservative WORST-CASE ASSUMPTION in the absence of real data.
        //
        // ASSUMPTION: Sentis on a mobile CPU backend for a YOLO-based model this size
        // plausibly runs well under 2 inferences/sec; assuming pessimistic 1 inference/sec
        // (1s cadence) as a conservative placeholder.
        //
        // CHOSEN VALUE: N = 2 confirmations.
        //
        // Final-review fix (Minor #8): the arithmetic below previously claimed "the
        // spawning detection itself counts as the first confirmation" while its own
        // parenthetical, two sentences later, correctly described the opposite --
        // directly contradicting itself. Corrected throughout: a newly-spawned label
        // starts at ConfirmationCount=0, and the spawning detection is NOT
        // incremented (only subsequent ASSOCIATIONS increment it). Reaching
        // ConfirmationCount >= 2 therefore requires THREE total detections: the
        // spawn (detection 1, count stays 0) + the first association (detection 2,
        // count reaches 1) + the second association (detection 3, count reaches 2,
        // now visible).
        //
        // LATENCY = 2 intervals between those 3 detections × 1s cadence = 2 seconds
        // from spawn to visible, PLUS up to one more cadence interval (~1s) of wait
        // before the very first inference resolves the object at all (an object can
        // enter camera coverage at any point within an inference interval, not
        // necessarily right as one starts) -- so the worst-case latency from an
        // object entering coverage to its label appearing is closer to 3 seconds,
        // not 2. This is still within "a few seconds" (alpha-scope.md), but with
        // less margin than the previous framing implied. This arithmetic MUST be
        // reconciled against the REAL logged cadence figure at Task 6's device gate.
        // Once that number exists, N may need revisiting.
        private const int ConfirmationsBeforeVisible = 2;

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

            // Object Tagger slice 5 Task 3: remove boxes that haven't been updated
            // recently. Task 3 owns expiry logic; Update() applies it here via the
            // pure-logic IsExpired function. Works for both confirmed (ConfirmationCount
            // >= ConfirmationsBeforeVisible) and not-yet-confirmed labels — a spurious
            // detection that never reaches confirmation is still cleaned up once it
            // stops being re-detected, not lingering forever unconfirmed.
            //
            // Deliberately runs BEFORE the per-frame visual pass below, so a view that
            // is about to be removed this same frame is never redundantly refreshed.
            for (int i = m_boxViews.Count - 1; i >= 0; i--)
            {
                var view = m_boxViews[i];
                if (LabelPresentation.IsExpired(view.Record.LastSeenTime, Time.time, GracePeriodSeconds))
                {
                    ReturnToPool(view);
                    m_boxViews.RemoveAt(i);
                }
            }

            // Object Tagger slice 5 final-review fix — CRITICAL finding: labels did
            // not actually billboard. See RefreshLabelViews's own comment for the
            // full explanation; this call is what makes it run every frame instead of
            // only at inference cadence.
            //
            // Nothing to refresh with an empty list — skip the camera-pose query
            // entirely rather than pay for it (and its DllImport call) every frame
            // when there are no labels on screen.
            if (m_boxViews.Count == 0)
            {
                return;
            }

            if (TryGetCurrentCameraPose(out var cameraPose))
            {
                RefreshLabelViews(cameraPose.position);
            }
            // else: the current head pose is not reliable this frame (see
            // TryGetCurrentCameraPose). Every view simply keeps whatever
            // rotation/scale/position it last had — a stale-by-one-frame render is
            // an unnoticeable, graceful degradation; computing fresh values from a
            // garbage near-origin pose would not be.
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

        /// Object Tagger slice 5 final-review fix — the CRITICAL finding's fix.
        ///
        /// Runs every frame (from Update(), guarded by TryGetCurrentCameraPose)
        /// over EVERY view in m_boxViews, not just this frame's detections — a
        /// retained-but-not-redetected label still needs its orientation, scale,
        /// and position refreshed every frame too, from the CURRENT camera pose.
        /// Previously all of this lived inside DrawUIBoxes's per-detection loop,
        /// which only runs at inference cadence (at most ~1Hz, per the placeholder
        /// assumption elsewhere in this file) — so a label's card stayed frozen at
        /// whatever the camera pose was at its LAST inference for up to its entire
        /// grace period, failing alpha-scope.md line 69 ("Labels face the user from
        /// every approach angle") the moment the camera moved afterward. It also
        /// meant the minimum-apparent-size scale went stale the same way, and
        /// ApplyOverlapOffsets could not clear a visual collision caused purely by
        /// camera movement (no new detection required for that to happen).
        ///
        /// Stop Condition 4 (state must not move back into RectTransforms): this
        /// method only READS from LabelRecord and computes fresh every call — it
        /// never writes anything back into a LabelRecord field. Same pattern
        /// ApplyOverlapOffsets already used correctly.
        ///
        /// Visibility (SetActive) is included here too, per the final review's
        /// Minor #10: it doesn't depend on camera pose at all, but making it a
        /// per-frame invariant enforced by construction (rather than something that
        /// happens to be correct today only because it is read from state that
        /// doesn't change between detections) is low risk and keeps every per-view
        /// visual property in the same one place.
        ///
        /// Text content (view.Label.text) deliberately stays in DrawUIBoxes's
        /// per-detection loop, not here — it only needs to change when a new
        /// detection is associated; re-setting it every frame would be wasted work
        /// and was never part of what was broken.
        private void RefreshLabelViews(Vector3 cameraPosition)
        {
            foreach (var view in m_boxViews)
            {
                var record = view.Record;
                var boxRectTransform = view.RectTransform;

                boxRectTransform.gameObject.SetActive(
                    LabelPresentation.IsVisible(record.ConfirmationCount, ConfirmationsBeforeVisible));

                boxRectTransform.rotation = LabelPresentation.FaceCameraRotation(record.SmoothedPosition, cameraPosition);

                var cardDistance = Vector3.Distance(cameraPosition, record.SmoothedPosition);
                var cardScale = LabelPresentation.ComputeCardScale(cardDistance, BaseCardScale, ReferenceDistanceMeters);
                boxRectTransform.localScale = Vector3.one * cardScale;
            }

            // Object Tagger slice 5 Task 5 Step 1's ApplyOverlapOffsets already sets
            // RectTransform.position (assigning SmoothedPosition + any needed offset,
            // never adding to whatever position was already there) for every
            // currently-visible view, including its correctly-handled zero-visible
            // (no-op) and lone-visible (reset to bare SmoothedPosition) cases — see
            // that method's own comment. Now that it runs every frame with the
            // CURRENT camera position instead of once per DrawUIBoxes call with the
            // inference-time one, it is sufficient on its own as the position step;
            // no separate "set position with no offset" call is needed here.
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
                    //
                    // classname is mangled (spaces -> underscores) right here, at this
                    // one log call site, rather than by LabelFor() globally — see
                    // LabelFor's own comment (Minor #9 fix).
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

                // Calculate distance and center point first
                float distance = Vector3.Distance(cameraPose.position, worldPos.Value);
                var worldSpaceCenter = m_cameraAccess.ViewportPointToRay(normRect.center, cameraPose).GetPoint(distance);

                // Object Tagger slice 5 Task 4 Step 2: the corner-ray reconstruction
                // that used to live here (intersecting minRay/maxRay against a plane
                // to derive a Vector2 size for the box's RectTransform) is retired.
                // It existed solely to size a bounding box, and there is no bounding
                // box any more (locked decision: a billboarded text card + anchor dot,
                // no box, no outline). worldSpaceCenter above is unaffected — it comes
                // from normRect.center, not from the corner rays.
                //
                // Object Tagger slice 5 final-review fix: the per-detection facing
                // direction/rotation that used to be computed here (`normal`, then
                // Quaternion.LookRotation(normal) below) is gone. It was computed once
                // at inference time and never touched again, which is the CRITICAL
                // finding this fix wave exists for — see RefreshLabelViews, which now
                // computes rotation fresh every frame from the CURRENT camera position.

                var view = GetOrCreateBoxView(detection.classId, worldSpaceCenter, out var wasAssociation);

                // Object Tagger slice 5 Task 1 Step 1: update the RECORD first — this is
                // the state. WorldPosition is the world-space placement Task 2's matching
                // will read instead of RectTransform/IoU.
                //
                // Object Tagger slice 5 Task 3:
                // - On spawn (wasAssociation==false): SmoothedPosition starts at
                //   WorldPosition so smoothing begins at the first observation, not
                //   from the origin. ConfirmationCount starts at 0 (set in
                //   GetOrCreateBoxView) and will be incremented here if this spawn
                //   happens to also be an association (which it isn't, but the logic
                //   below is uniform).
                // - On association (wasAssociation==true): ConfirmationCount is
                //   incremented ONCE per association, so the N-th associated detection
                //   makes it >= threshold. SmoothedPosition is lerped toward
                //   WorldPosition to smooth out jitter.
                // - Visibility is gated on ConfirmationCount >= ConfirmationsBeforeVisible
                //   by SetActive below.
                //
                // SCOPE BOUNDARY: LastAssociatedScore is stored here so it exists for
                // Task 4, but it must NOT be read by the view below. Slice 3 stopped
                // the score at this boundary deliberately.
                var record = view.Record;
                record.WorldPosition = worldSpaceCenter;

                if (!wasAssociation)
                {
                    // New spawn: initialize SmoothedPosition at the first observation.
                    record.SmoothedPosition = worldSpaceCenter;
                }
                else
                {
                    // Associated re-detection: smooth toward the new position.
                    record.SmoothedPosition = LabelPresentation.Smooth(
                        record.SmoothedPosition, worldSpaceCenter, SmoothingFactor);

                    // Increment confirmation count on each associated detection.
                    record.ConfirmationCount++;
                }

                record.LastAssociatedScore = detection.score;
                record.LastSeenTime = Time.time;

                // Object Tagger slice 5 Task 4 Step 1: "<class> — <confidence>%",
                // rounded, from the RECORD's LastAssociatedScore (last accepted
                // detection's score, unsmoothed — smoothing applies to position
                // only, per the locked decision). This is the first time confidence
                // reaches the screen; slices 3-4 deliberately carried it without
                // rendering it. Score is a 0-1 probability (m_scoreThreshold is
                // [Range(0,1)], defaulted to 0.23/0.3 across this project's
                // configs), so *100 rounded is a percentage, not double-scaling an
                // already-scaled value. Reads record.ClassName directly (final-review
                // fix, Minor #9: it is now the raw unmangled name, not a
                // spaces-to-underscores mangled value that needed reversing for
                // display — see LabelFor's comment) so the view stays a projection of
                // the record, not of loop-local state.
                //
                // Text content only needs to change when a new detection is
                // associated — this is the only per-detection-loop write to the view
                // left after the final-review fix moved rotation/scale/visibility/
                // position into the per-frame RefreshLabelViews pass (see Update()).
                view.Label.text = $"{record.ClassName} — {Mathf.RoundToInt(record.LastAssociatedScore * 100)}%";
            }
        }

        /// Object Tagger slice 5 Task 5 Step 1: applies LabelOverlap's pairwise
        /// vertical-offset resolution to every currently-VISIBLE view's rendered
        /// position.
        ///
        /// Object Tagger slice 5 final-review fix: called from RefreshLabelViews
        /// (Update()'s per-frame visual pass) with the CURRENT camera position now,
        /// rather than once per DrawUIBoxes call with the inference-time one. The
        /// logic below is unchanged -- only WHEN it runs and WHICH camera position
        /// it is given changed. This is also why the "reads state fresh, assigns
        /// rather than adds" discipline described below matters even more now: it
        /// runs far more often (every frame, not once per inference).
        ///
        /// Deliberately reads LabelRecord.SmoothedPosition (the state) as the
        /// base position for every view, and ASSIGNS (not adds) the result to
        /// the RectTransform -- never reads the RectTransform's own current
        /// position and never writes the offset back into the record. Reading
        /// the RectTransform's position would make the offset accumulate frame
        /// over frame for a view that stays visible-but-undetected across
        /// several calls (see LabelOverlap.ComputeVerticalOffsets's
        /// comment for why that is a real bug, not a theoretical one), and
        /// writing the offset into the record would violate Stop Condition 4 by
        /// the same principle that RectTransform state must not move back into
        /// LabelRecord -- this is the mirror case: view-only adjustments must
        /// not leak back into the record either.
        ///
        /// Invisible (unconfirmed) labels are excluded: LabelPresentation.IsVisible
        /// gates whether a view is even rendered, so an invisible label cannot
        /// visually collide with anything.
        ///
        /// KNOWN CONSEQUENCE: offsetting boxRectTransform's position also moves
        /// the anchor dot, which is a CHILD of boxRectTransform at local
        /// anchoredPosition (0,0) (see BoxView's comment: the dot "tracks
        /// SmoothedPosition exactly"). An offset card's dot therefore no longer
        /// sits on the real-world object -- it moves with the card. This is the
        /// direct, intended consequence of the locked decision (offset the
        /// RENDERED position); splitting the dot from the card to keep it
        /// anchored to the true position while the text moves would need a
        /// prefab hierarchy change, which is out of this task's scope. Flagged
        /// here so Task 6's device gate knows to look at it.
        private void ApplyOverlapOffsets(Vector3 cameraPosition)
        {
            var visibleViews = new List<BoxView>(m_boxViews.Count);
            foreach (var view in m_boxViews)
            {
                if (LabelPresentation.IsVisible(view.Record.ConfirmationCount, ConfirmationsBeforeVisible))
                {
                    visibleViews.Add(view);
                }
            }

            if (visibleViews.Count == 0)
            {
                // Nothing to reset. Deliberately NOT short-circuiting on
                // Count == 1 as well: a lone visible view still needs to run
                // through the snapshot-and-assign below so a PREVIOUSLY
                // applied offset gets cleared once its overlap partner is no
                // longer around to justify it (e.g. the nearer of a pair
                // expired since the last frame this pass ran, or wasn't
                // re-detected this frame). With one view, ComputeVerticalOffsets
                // always returns [0], so the assignment below resets that
                // view's RectTransform.position back to its bare
                // SmoothedPosition -- the reset IS the point, not wasted work.
                // Skipping it here would leave a stale 6cm offset baked into
                // the RectTransform with nothing left to clear it, breaking
                // the "always derived fresh from the record" invariant this
                // whole method exists to uphold.
                return;
            }

            var basePositions = new Vector3[visibleViews.Count];
            for (var i = 0; i < visibleViews.Count; i++)
            {
                basePositions[i] = visibleViews[i].Record.SmoothedPosition;
            }

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                cameraPosition, basePositions, OverlapAngleThresholdDegrees, OverlapOffsetMeters,
                BaseCardScale, ReferenceDistanceMeters);

            for (var i = 0; i < visibleViews.Count; i++)
            {
                visibleViews[i].RectTransform.position = basePositions[i] + Vector3.up * offsets[i];
            }
        }

        // Object Tagger slice 5 Task 2: class+world-distance matching over
        // LabelRecord.WorldPosition, replacing Task 1's IoU/RectTransform box-extent
        // matching. The decision itself is pure logic in LabelAssociation.Decide —
        // this method's job is just to snapshot m_boxViews into that function's
        // input shape and act on the Decision it returns.
        //
        // Object Tagger slice 5 Task 3: returns wasAssociation to distinguish spawns
        // from re-detections. Task 3 uses this to increment ConfirmationCount only on
        // associations (not on the spawn itself), keeping all record mutation in
        // DrawUIBoxes alongside the other field updates.
        private BoxView GetOrCreateBoxView(int classId, Vector3 worldSpaceCenter, out bool wasAssociation)
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
                wasAssociation = true;
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
            wasAssociation = false;
            return newView;
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
