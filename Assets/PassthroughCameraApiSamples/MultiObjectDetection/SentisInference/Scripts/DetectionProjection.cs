// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 4 Task 2 — the 2D→3D projection arithmetic, extracted.
//
// Spec line 72: "Extract spatial resolution out of SentisInferenceUiManager,
// leaving the existing box view intact."
//
// Everything here is arithmetic on a detection box. The only genuinely platform
// -dependent steps in the placement chain are ViewportPointToRay and the depth
// raycast itself; both stay behind adapters. That split is what makes the
// interesting part testable, following slice 3's seam.
//
// THE Y FLIP IS LOAD-BEARING. Model boxes are top-left origin; Unity's viewport
// is bottom-left origin. A missing `1.0f -` mirrors every label about the
// horizontal axis, which on device reads as "placement is broken" rather than
// "one subtraction is missing". It appears TWICE — the ray point and the
// normalised rect — and both are pinned by tests.

using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class DetectionProjection
    {
        /// Model output is (topLeftX, topLeftY, bottomRightX, bottomRightY) in MODEL
        /// INPUT PIXEL SPACE — not normalised, not camera-image space. Slice 3 Task 1
        /// measured that input as 640x640.
        public static Rect BoxToRect(Vector4 boundingBox) =>
            new Rect(boundingBox.x, boundingBox.y, boundingBox.z - boundingBox.x, boundingBox.w - boundingBox.y);

        /// Box centre, normalised to 0..1 against the model input size.
        public static Vector2 NormalizedCenter(Rect rect, Vector2 inputSize) =>
            new Vector2(rect.center.x / inputSize.x, rect.center.y / inputSize.y);

        /// Normalised centre → Unity viewport point.
        ///
        /// This is the Y flip. Model space counts down from the top; the viewport
        /// counts up from the bottom.
        public static Vector2 ToViewportPoint(Vector2 normalizedCenter) =>
            new Vector2(normalizedCenter.x, 1.0f - normalizedCenter.y);

        /// Normalised centre → pixel offset from the camera image centre. Feeds the
        /// existing debug label; not used for placement.
        public static Vector2 ToCameraPixelOffset(Vector2 normalizedCenter, Vector2 currentResolution) =>
            currentResolution * (normalizedCenter - (Vector2.one * 0.5f));

        /// Detection rect → normalised rect, Y-flipped. Used by the box view, which
        /// slice 4 leaves intact.
        public static Rect NormalizedRect(Rect rect, Vector2 inputSize) =>
            new Rect(
                rect.x / inputSize.x,
                1f - (rect.yMax / inputSize.y),
                rect.width / inputSize.x,
                rect.height / inputSize.y);

        /// Sample offsets, in NORMALISED units, for probing depth around a box centre.
        ///
        /// Slice 4 Task 3 groundwork. A single centre ray is why boxes landed a few
        /// centimetres off during slice 2's gate: for a mug the centre hits the mug,
        /// but for a chair it can pass between the legs and hit the wall behind.
        /// Probing a small pattern and taking a robust statistic rejects that outlier.
        ///
        /// Returned centre-first so a caller that only wants one sample gets exactly
        /// upstream's behaviour by taking the first.
        public static Vector2[] SampleOffsets(float spread) => new[]
        {
            Vector2.zero,
            new Vector2(0f, spread),
            new Vector2(0f, -spread),
            new Vector2(spread, 0f),
            new Vector2(-spread, 0f),
        };

        /// Pick the representative point from a set of resolved depth samples.
        ///
        /// Uses the MEDIAN BY DISTANCE from the camera rather than the mean: one ray
        /// slipping past the object and hitting a far wall is an extreme outlier, and
        /// a mean would drag the label toward it. A median ignores it.
        ///
        /// Returns false when nothing resolved, so the caller keeps the existing
        /// "drop this detection" policy rather than inventing a position.
        public static bool TryPickRepresentativePoint(
            Vector3[] resolvedPoints, int resolvedCount, Vector3 cameraPosition, out Vector3 point)
        {
            point = Vector3.zero;
            if (resolvedPoints == null || resolvedCount <= 0)
            {
                return false;
            }

            if (resolvedCount == 1)
            {
                point = resolvedPoints[0];
                return true;
            }

            // Insertion sort by distance — resolvedCount is a handful, so this is
            // cheaper and allocation-free compared with sorting a List with a lambda.
            var order = new int[resolvedCount];
            for (var i = 0; i < resolvedCount; i++)
            {
                order[i] = i;
            }

            for (var i = 1; i < resolvedCount; i++)
            {
                var key = order[i];
                var keyDist = (resolvedPoints[key] - cameraPosition).sqrMagnitude;
                var j = i - 1;
                while (j >= 0 && (resolvedPoints[order[j]] - cameraPosition).sqrMagnitude > keyDist)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }

            point = resolvedPoints[order[resolvedCount / 2]];
            return true;
        }
    }
}
