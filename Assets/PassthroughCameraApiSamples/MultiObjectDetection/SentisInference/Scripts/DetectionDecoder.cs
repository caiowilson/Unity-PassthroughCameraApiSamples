// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 3 Task 3 — the pure decoding logic, extracted.
//
// WHY THIS FILE EXISTS. Slice 3's gate (spec line 85) requires edit-mode tests for
// decoding, threshold and NMS-with-score. Upstream's NonMaxSuppression was
// `private static` and took Unity Inference Engine `Tensor<T>` parameters, so the
// only way to reach it was InternalsVisibleTo -- which grants access but leaves the
// logic needing tensors, a Worker and a live backend to exercise. A test that needs
// those is not an edit-mode test; it only looks like one.
//
// So the logic moved onto plain spans instead. Tests pass ordinary arrays. The
// MonoBehaviour keeps the tensor readback and calls in here.
//
// THIS IS A REFACTOR, NOT A REDESIGN. Two upstream behaviours are easy to "fix" by
// accident and are preserved deliberately:
//   * suppression is CLASS-AGNOSTIC -- the inner loop never compares class IDs.
//     Upstream's own comment says "Suppress overlapping boxes regardless of class".
//     Making it per-class would change what the device shows, and slice 2's gate
//     figures were recorded against the current behaviour.
//   * the score filter is `>=`, not `>`, and IoU suppression is `>`, not `>=`.

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// Pure, allocation-light detection decoding. No Unity Inference Engine types,
    /// no MonoBehaviour, no scene dependencies — so it is reachable from an
    /// edit-mode test assembly with nothing but arrays.
    public static class DetectionDecoder
    {
        /// Score-threshold filter, descending sort, class-agnostic IoU suppression,
        /// and the acceptance cap.
        ///
        /// <param name="boxesFlat">4 floats per candidate, row-major:
        /// (topLeftX, topLeftY, bottomRightX, bottomRightY).</param>
        /// <param name="maxAccepted">Upper bound on accepted detections. Applied AFTER
        /// the descending sort, so the highest-scoring detections survive. Slice 3
        /// Task 3 passes int.MaxValue to preserve upstream behaviour exactly; Task 4
        /// sets a real value. Spec line 76 assigns this lever to slice 3 so that
        /// slice 6 can tune it rather than build it.</param>
        public static void SelectDetections(
            System.ReadOnlySpan<float> boxesFlat,
            System.ReadOnlySpan<int> classIds,
            System.ReadOnlySpan<float> scores,
            float iouThreshold,
            float scoreThreshold,
            int maxAccepted,
            HashSet<int> allowedClassIds,
            List<(int classId, Vector4 boundingBox, float score)> outDetections)
        {
            outDetections.Clear();

            // Filter by score threshold AND curated class in the same pass. `>=` matches
            // upstream — do not tighten it.
            //
            // ORDERING IS LOAD-BEARING: curation happens HERE, before the sort and before
            // the acceptance cap. Filtering later — at the label-lookup site, say — would
            // let unsupported classes consume cap slots and then be discarded, so the cap
            // would silently under-deliver supported detections. That failure reads as a
            // detection problem, not a filtering one.
            //
            // A null or empty set means "no curation", which preserves upstream behaviour.
            var curating = allowedClassIds != null && allowedClassIds.Count > 0;
            var filteredIndices = new List<int>();
            for (var i = 0; i < scores.Length; i++)
            {
                if (scores[i] < scoreThreshold)
                {
                    continue;
                }
                if (curating && !allowedClassIds.Contains(classIds[i]))
                {
                    continue;
                }
                filteredIndices.Add(i);
            }

            if (filteredIndices.Count == 0)
            {
                return;
            }

            // Sort filtered indices by score, descending. The cap below depends on this
            // ordering: without it, capping would keep arbitrary detections rather than
            // the most confident ones.
            // NOTE: the comparator captures `scores`, which is a ReadOnlySpan and cannot
            // be captured by a lambda (ref struct). Copy the needed scores into a plain
            // array first and sort against that.
            var scoreByIndex = new Dictionary<int, float>(filteredIndices.Count);
            foreach (var idx in filteredIndices)
            {
                scoreByIndex[idx] = scores[idx];
            }
            filteredIndices.Sort((a, b) => scoreByIndex[b].CompareTo(scoreByIndex[a]));

            var suppressed = new bool[filteredIndices.Count];
            for (var i = 0; i < filteredIndices.Count; i++)
            {
                if (suppressed[i])
                {
                    continue;
                }

                if (outDetections.Count >= maxAccepted)
                {
                    break;
                }

                var idx = filteredIndices[i];
                outDetections.Add((classIds[idx], GetBox(boxesFlat, idx), scores[idx]));

                // Suppress overlapping boxes REGARDLESS OF CLASS. Preserved from upstream.
                for (var j = i + 1; j < filteredIndices.Count; j++)
                {
                    if (suppressed[j])
                    {
                        continue;
                    }

                    var jdx = filteredIndices[j];
                    if (CalculateIoU(GetBox(boxesFlat, idx), GetBox(boxesFlat, jdx)) > iouThreshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }
        }

        /// Boxes are stored flat, 4 per candidate.
        public static Vector4 GetBox(System.ReadOnlySpan<float> boxesFlat, int i) =>
            new Vector4(boxesFlat[i * 4], boxesFlat[(i * 4) + 1], boxesFlat[(i * 4) + 2], boxesFlat[(i * 4) + 3]);

        /// Moved here from SentisInferenceRunManager unchanged. It was already pure and
        /// already took two UnityEngine.Vector4 — it never needed tensors.
        ///
        /// SentisInferenceRunManager keeps a forwarding member so the two callers in
        /// SentisInferenceUiManager (the cross-frame ASSOCIATION test, which spec line 55
        /// replaces in slice 5) keep compiling untouched. Slice 3 must not alter those.
        public static float CalculateIoU(Vector4 boxA, Vector4 boxB)
        {
            // Boxes are in format (topLeftX, topLeftY, bottomRightX, bottomRightY)
            var x1 = Mathf.Max(boxA.x, boxB.x);
            var y1 = Mathf.Max(boxA.y, boxB.y);
            var x2 = Mathf.Min(boxA.z, boxB.z);
            var y2 = Mathf.Min(boxA.w, boxB.w);

            var intersectionWidth = Mathf.Max(0, x2 - x1);
            var intersectionHeight = Mathf.Max(0, y2 - y1);
            var intersectionArea = intersectionWidth * intersectionHeight;

            var boxAArea = (boxA.z - boxA.x) * (boxA.w - boxA.y);
            var boxBArea = (boxB.z - boxB.x) * (boxB.w - boxB.y);

            var unionArea = boxAArea + boxBArea - intersectionArea;

            if (unionArea == 0)
            {
                return 0;
            }

            return intersectionArea / unionArea;
        }
    }
}
