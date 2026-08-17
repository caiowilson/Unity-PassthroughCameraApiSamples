// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger manual-tagging Task 1 — index-of-minimum as pure logic.
//
// Shared by two different "nearest to X" needs in SentisInferenceUiManager:
// live-candidate selection (nearest detection to the frame center, in
// normalized image space) and untag targeting (nearest committed label to
// camera-forward, in degrees via LabelOverlap.AngularSeparationDegrees).
// Both reduce to the same operation once the caller has already computed a
// per-candidate distance, so this is one generic function rather than two
// near-duplicates.
//
// A top-level PUBLIC static type, not nested/internal, matching every other
// pure-logic file in this project (LabelAssociation, LabelPresentation,
// LabelOverlap) — this project rejects InternalsVisibleTo as a seam (see
// AssemblySeamTests.cs), so a nested/internal type is unreachable from the
// edit-mode test assembly.

using System.Collections.Generic;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class NearestSelection
    {
        /// Returns the index of the smallest value in `distances`, or -1 if
        /// the list is empty. Ties resolve to the first (lowest-index)
        /// occurrence — deterministic given identical inputs.
        public static int IndexOfMinimum(IReadOnlyList<float> distances)
        {
            var bestIndex = -1;
            var bestDistance = float.MaxValue;

            for (var i = 0; i < distances.Count; i++)
            {
                if (distances[i] < bestDistance)
                {
                    bestDistance = distances[i];
                    bestIndex = i;
                }
            }

            return bestIndex;
        }
    }
}
