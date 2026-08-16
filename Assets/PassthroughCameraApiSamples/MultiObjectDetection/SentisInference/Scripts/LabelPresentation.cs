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
    }
}
