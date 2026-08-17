// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 3, simplified by manual-tagging Task 3 —
// position smoothing and camera-relative presentation as pure logic.
//
// IsVisible and IsExpired (slice 5) are gone: both existed to gate
// automatic labels — visible only after N confirmations, removed after a
// grace period. Manual tagging has neither concept; a committed label is
// visible from the moment SentisInferenceUiManager creates it and stays
// until explicitly untagged. What remains here is the part still needed
// for "keep tracking while visible" — smoothing toward re-detections and
// billboarding/scaling every frame from the current camera pose.
//
// A top-level PUBLIC static type, same reason as LabelAssociation: this
// project rejects InternalsVisibleTo as a seam (AssemblySeamTests.cs).

using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class LabelPresentation
    {
        /// Smooth a position toward a target using exponential (lerp-based) smoothing.
        ///
        /// factor should be in (0, 1) and represent the blend ratio per frame:
        /// smaller values (e.g. 0.1) smooth more heavily, larger values (e.g. 0.3)
        /// respond faster. lerp cannot overshoot with factor in this range.
        public static Vector3 Smooth(Vector3 smoothedPosition, Vector3 targetPosition, float factor)
        {
            return Vector3.Lerp(smoothedPosition, targetPosition, factor);
        }

        // Object Tagger slice 5 Task 4 Step 3 — minimum-apparent-size scale.
        //
        // A fixed world-space size (constant localScale) is legible up close but
        // shrinks ANGULARLY as the camera moves away -- readable at 1m, too small
        // at 4m, because apparent size is world-size divided by distance. The
        // card's visual elements (dot diameter, text cap height) are authored in
        // the prefab as world-meter sizes calibrated to read comfortably at
        // referenceDistance -- the near end of the 1-4m legibility range. Beyond
        // referenceDistance, the scale grows LINEARLY with distance, which holds
        // apparent size (world-size / distance) constant from that point out to
        // 4m and beyond: (baseScale * distance/referenceDistance) / distance ==
        // baseScale / referenceDistance, a constant independent of distance.
        //
        // Below referenceDistance the multiplier is clamped to baseScale rather
        // than shrinking further: getting closer than the reference point only
        // makes a fixed-size card look BIGGER, not smaller, so it is never the
        // legibility risk this mechanism exists to fix.
        //
        // referenceDistance must be > 0. Callers pass a compile-time positive
        // constant (see SentisInferenceUiManager's ReferenceDistanceMeters), so no
        // runtime guard is added here -- the same pattern LabelPresentation
        // already uses for its other pure functions (no defensive checks against
        // inputs the call site structurally cannot produce).
        public static float ComputeCardScale(float distance, float baseScale, float referenceDistance)
        {
            return baseScale * Mathf.Max(1f, distance / referenceDistance);
        }

        // Object Tagger slice 5 final-review fix — CRITICAL finding: labels did not
        // actually billboard. Rotation was previously computed once per inference
        // inside DrawUIBoxes (at most ~1Hz, per the placeholder cadence assumption)
        // and never touched again, so a retained-but-not-redetected label stayed
        // frozen at its last inference-time orientation for up to the full 3-second
        // grace period -- failing alpha-scope.md line 69 ("Labels face the user from
        // every approach angle") the moment the camera moved after that inference.
        //
        // This function is called every frame (SentisInferenceUiManager.Update()'s
        // per-frame visual pass), from the CURRENT camera position, not the stale
        // inference-time one -- that is the entire fix. Pulled out as a pure
        // function, same reasoning as ComputeCardScale: reachable from edit-mode
        // tests with no RectTransform/GameObject/MonoBehaviour involved.
        public static Quaternion FaceCameraRotation(Vector3 labelPosition, Vector3 cameraPosition)
        {
            var facingDirection = (labelPosition - cameraPosition).normalized;
            return Quaternion.LookRotation(facingDirection);
        }
    }
}
