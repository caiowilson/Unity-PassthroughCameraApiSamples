// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 3 — confirmation count, position smoothing, and
// expiry as pure logic.
//
// These functions own the three constraints of Task 3: labels only become
// visible after N consecutive associated detections (Step 1), their rendered
// positions are smoothed toward the detected position (Step 2), and unseen
// labels expire within the grace period (Step 3). Each is computed from
// LabelRecord's plain-data fields with no RectTransform, GameObject, or
// MonoBehaviour involved, making them reachable from edit-mode tests and
// testable as pure logic.
//
// A top-level PUBLIC static type, same reason as LabelAssociation: this
// project rejects InternalsVisibleTo as a seam (see AssemblySeamTests.cs), so
// nested/internal functions would be unreachable from the edit-mode test
// assembly.

using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class LabelPresentation
    {
        /// Decide whether a label should be visible based on its confirmation count.
        ///
        /// A label appears only after N consecutive accepted detections associate
        /// to the same position, so a single spurious detection does not produce a
        /// visible label.
        public static bool IsVisible(int confirmationCount, int confirmationThreshold)
        {
            return confirmationCount >= confirmationThreshold;
        }

        /// Smooth a position toward a target using exponential (lerp-based) smoothing.
        ///
        /// factor should be in (0, 1) and represent the blend ratio per frame:
        /// smaller values (e.g. 0.1) smooth more heavily, larger values (e.g. 0.3)
        /// respond faster. lerp cannot overshoot with factor in this range.
        public static Vector3 Smooth(Vector3 smoothedPosition, Vector3 targetPosition, float factor)
        {
            return Vector3.Lerp(smoothedPosition, targetPosition, factor);
        }

        /// Decide whether a label has expired based on the grace period.
        ///
        /// A label that has not been re-detected within GracePeriodSeconds is stale
        /// and should be removed. Must not exceed 3 seconds (alpha-scope.md, an
        /// acceptance criterion).
        public static bool IsExpired(float lastSeenTime, float currentTime, float gracePeriodSeconds)
        {
            return currentTime - lastSeenTime > gracePeriodSeconds;
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
    }
}
