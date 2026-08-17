// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 5 Step 1 — visual overlap between DIFFERENT
// labels, resolved by offsetting the farther one vertically.
//
// This is a purely RENDER-TIME concern, distinct from
// LabelAssociation.FindAssociationIndex (which decides which DETECTION
// updates which RECORD, per-class, via
// SentisInferenceUiManager.FindAssociatedViewIndex). Two different labels — could
// be different classes, or same class but far enough apart in the world that
// association correctly treats them as separate objects — can still end up
// visually crowded or overlapping as seen from the camera. That is what this
// file resolves. Locked decision (task brief, not open for re-litigation):
// both labels stay visible; the FARTHER one (from the camera) is offset
// vertically so the nearer object keeps its natural, unoffset position. This
// is never an eviction/hiding mechanism.
//
// A top-level PUBLIC static type, not nested/internal, same reason as
// LabelAssociation/LabelPresentation: this project rejects InternalsVisibleTo
// as a seam (AssemblySeamTests.cs), so a nested/internal type would be
// unreachable from the edit-mode test assembly.

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class LabelOverlap
    {
        /// Angular separation, in degrees, between two world positions as seen
        /// from the camera — the direction from the camera to A vs. the
        /// direction from the camera to B. This deliberately does NOT look at
        /// the raw 3D distance between A and B: two objects far apart in 3D but
        /// both far from the camera and roughly along the same sightline still
        /// visually collide, while two objects close together in 3D but very
        /// near the camera might not. Angular separation captures the former
        /// and not the latter, which is what "would these cards visually
        /// overlap" actually asks.
        ///
        /// Vector3.Angle already returns 0 for a zero-length input direction
        /// (i.e. a position exactly at the camera), so no extra guard is added
        /// here — matches this project's existing pattern of not defending
        /// pure functions against inputs the call site cannot structurally
        /// produce (see LabelPresentation.ComputeCardScale's referenceDistance
        /// note). A depth-resolved label position sitting exactly at the
        /// camera's own position is not a real case this app can produce.
        public static float AngularSeparationDegrees(Vector3 cameraPosition, Vector3 positionA, Vector3 positionB)
        {
            var directionA = (positionA - cameraPosition).normalized;
            var directionB = (positionB - cameraPosition).normalized;
            return Vector3.Angle(directionA, directionB);
        }

        /// Whether two labels, at the given angular separation, are close
        /// enough in the camera's view to be considered visually
        /// overlapping/crowded. Strict `<` (not `<=`): exactly AT the
        /// threshold reads as "just clear" rather than "still overlapping",
        /// so a pair sitting precisely at the boundary is not pushed apart.
        public static bool IsOverlapping(float angularSeparationDegrees, float thresholdDegrees)
        {
            return angularSeparationDegrees < thresholdDegrees;
        }

        /// Computes a per-index vertical (world-space Vector3.up) offset for
        /// each currently-visible label, so that any pair whose angular
        /// separation is below thresholdDegrees ends up with the farther one
        /// pushed clear.
        ///
        /// PURE FUNCTION, no RectTransform/GameObject/MonoBehaviour: takes a
        /// snapshot of base (unsmoothed-by-offset) world positions in, returns
        /// offsets out — same shape as LabelAssociation.FindAssociationIndex.
        /// This matters for more than testability: it is also what keeps Stop
        /// Condition 4 (state must not move back into RectTransforms) intact.
        /// Reading a RectTransform's CURRENT position and adding to it would
        /// make the offset accumulate across frames for any view that stays
        /// visible-but-undetected for multiple DrawUIBoxes calls — the normal
        /// case for any committed label not currently being re-detected, since
        /// labels never auto-expire and simply stay at their last associated
        /// position — climbing the card out of view with no bound on how far.
        /// Recomputing fresh from basePositions (LabelRecord.SmoothedPosition)
        /// every call and having the caller ASSIGN (not add) the result back
        /// to the RectTransform makes that class of bug structurally
        /// impossible.
        ///
        /// Pairwise, in index order (i &lt; j): each pair whose angular
        /// separation is below threshold pushes the farther-from-camera index
        /// up by offsetAtReferenceDistance, scaled by that index's own
        /// LabelPresentation.ComputeCardScale (the same minimum-apparent-size
        /// factor Task 4 uses to size the card itself) — a card's world-space
        /// size grows linearly with distance beyond referenceDistance to hold
        /// its APPARENT size constant, so a fixed world-space offset would
        /// under-clear a farther card; scaling the offset the same way holds
        /// the offset's angular effect constant instead. A later pair sees any
        /// offset already applied to its members by an earlier pair (the
        /// offsets array accumulates within one call, across pairs, on
        /// purpose), so a 3-label cluster gets a stable, non-oscillating
        /// resolution — deterministic given the same inputs, so calling this
        /// twice with identical arguments produces identical output (no
        /// per-call side effects, no hidden state).
        ///
        /// Scope, matching the task brief: this is a pairwise resolution, not a
        /// fixed-point iteration that re-checks every pair after every push.
        /// It handles the plan's stated case (two labels) exactly, and behaves
        /// sanely (no crash, no runaway growth, no unbounded stacking) with
        /// 3+ labels. Once a pair IS flagged, a single vertical push reliably
        /// clears it (the push's own angular effect is roughly constant
        /// regardless of distance, so it only adds to any existing separation).
        /// The real known limitation runs the other way — see
        /// SentisInferenceUiManager.OverlapAngleThresholdDegrees's comment: a
        /// threshold tuned to the card's HEIGHT under-triggers for
        /// horizontally-crowded pairs, since the rendered text is far WIDER
        /// than it is tall.
        public static float[] ComputeVerticalOffsets(
            Vector3 cameraPosition,
            IReadOnlyList<Vector3> basePositions,
            float thresholdDegrees,
            float offsetAtReferenceDistance,
            float baseCardScale,
            float referenceDistanceMeters)
        {
            var offsets = new float[basePositions.Count];

            for (var i = 0; i < basePositions.Count; i++)
            {
                for (var j = i + 1; j < basePositions.Count; j++)
                {
                    var positionA = basePositions[i] + Vector3.up * offsets[i];
                    var positionB = basePositions[j] + Vector3.up * offsets[j];

                    var angularSeparation = AngularSeparationDegrees(cameraPosition, positionA, positionB);
                    if (!IsOverlapping(angularSeparation, thresholdDegrees))
                    {
                        continue;
                    }

                    // Farther-by-distance-to-camera gets pushed. Ties (equal
                    // distance) fall to j, an arbitrary but stable tie-break —
                    // a genuine floating-point tie between two independently
                    // depth-resolved positions is vanishingly unlikely.
                    // Distance is measured from each label's BASE position
                    // (not its already-offset position): the vertical push
                    // barely changes distance-to-camera for a card roughly at
                    // head height in front of the camera, and using the base
                    // position keeps "which one is farther" stable across
                    // pairs within the same call instead of drifting as
                    // offsets accumulate.
                    var distanceA = Vector3.Distance(cameraPosition, basePositions[i]);
                    var distanceB = Vector3.Distance(cameraPosition, basePositions[j]);
                    var fartherIndex = distanceA > distanceB ? i : j;

                    var cardScale = LabelPresentation.ComputeCardScale(
                        Vector3.Distance(cameraPosition, basePositions[fartherIndex]),
                        baseCardScale,
                        referenceDistanceMeters);

                    offsets[fartherIndex] += offsetAtReferenceDistance * cardScale;
                }
            }

            return offsets;
        }
    }
}
