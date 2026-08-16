// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 2 — class-plus-distance label association.
//
// Replaces Task 1's IoU/RectTransform box-extent matching entirely. Box extent
// stopped being a meaningful concept once Task 1 moved label state onto
// LabelRecord.WorldPosition: labels are becoming text cards keyed to a world
// position, not boxes drawn around a detection, and "does this rectangle
// overlap that rectangle in view space" has no equivalent worth keeping.
//
// A top-level PUBLIC static type, not nested/internal, on purpose — same
// reason as DetectionDecoder/DetectionProjection/LabelRecord: this project
// rejects InternalsVisibleTo as a seam (see AssemblySeamTests.cs), so a
// nested/internal type would be unreachable from the edit-mode test assembly.
// Task 4's Step 4 explicitly asks for this decision to be testable as pure
// logic, with no RectTransform/GameObject/MonoBehaviour involved.

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class LabelAssociation
    {
        /// A read-only snapshot of one currently-tracked label — only what the
        /// matching decision needs. Deliberately NOT LabelRecord itself: this
        /// keeps the decision function decoupled from fields it never touches
        /// (SessionId, ConfirmationCount, ...) and lets tests build cases
        /// without constructing a full record.
        public readonly struct Existing
        {
            public readonly int ClassId;
            public readonly Vector3 WorldPosition;
            public readonly float LastSeenTime;

            public Existing(int classId, Vector3 worldPosition, float lastSeenTime)
            {
                ClassId = classId;
                WorldPosition = worldPosition;
                LastSeenTime = lastSeenTime;
            }
        }

        /// The matching decision for one new observation.
        ///
        /// AssociatedIndex and RemoveIndex are mutually exclusive by construction:
        /// the re-placement rule (Step 3) only fires when the new observation does
        /// NOT associate to anything — i.e. it is about to spawn a brand new label.
        public readonly struct Decision
        {
            /// Index into the `existing` list to reuse, or -1 to spawn a new label.
            public readonly int AssociatedIndex;

            /// Index into the `existing` list of a stale same-class label to
            /// remove immediately (the re-placement rule), or -1 if none applies.
            public readonly int RemoveIndex;

            public Decision(int associatedIndex, int removeIndex)
            {
                AssociatedIndex = associatedIndex;
                RemoveIndex = removeIndex;
            }

            public static readonly Decision SpawnOnly = new Decision(-1, -1);
        }

        /// Decide what a new (classId, worldPosition) observation does against the
        /// currently-tracked labels.
        ///
        /// Association rule: same class AND world distance below
        /// `distanceThresholdMeters` -> associate (reuse the nearest such match).
        /// This is a straight Vector3.Distance check over LabelRecord.WorldPosition,
        /// not IoU — box extent is not read here.
        ///
        /// Different-class overlap is explicitly NOT eviction. That is the locked
        /// decision: when a new label overlaps a different-class existing label in
        /// space, both stay visible and the farther one gets a visual offset —
        /// Task 5's job. This function does not inspect, evict, or otherwise touch
        /// any Existing entry whose ClassId differs from newClassId.
        ///
        /// Re-placement rule (alpha-scope.md, via Task 2 Step 3): if nothing
        /// associates, a same-class label that is now farther than the threshold
        /// but still inside its grace period reads as "the same physical object
        /// moved" ONLY when it is the UNIQUE such candidate. With two or more
        /// same-class candidates still in grace, there is no way to tell which one
        /// (if any) the new observation replaces without risking misattribution of
        /// a still-present, unrelated object's label — so none are reaped, and the
        /// caller's existing grace-period expiry is left to clean them up later.
        /// Wrongly reaping an unrelated object's label is the worse bug; a stale
        /// label lingering up to the grace period is already spec-compliant
        /// behaviour.
        public static Decision Decide(
            IReadOnlyList<Existing> existing,
            int newClassId,
            Vector3 newWorldPosition,
            float currentTime,
            float distanceThresholdMeters,
            float gracePeriodSeconds)
        {
            var associatedIndex = -1;
            var bestDistance = float.MaxValue;

            for (var i = 0; i < existing.Count; i++)
            {
                var candidate = existing[i];
                if (candidate.ClassId != newClassId)
                {
                    continue;
                }

                var distance = Vector3.Distance(candidate.WorldPosition, newWorldPosition);
                if (distance < distanceThresholdMeters && distance < bestDistance)
                {
                    bestDistance = distance;
                    associatedIndex = i;
                }
            }

            if (associatedIndex >= 0)
            {
                return new Decision(associatedIndex, -1);
            }

            // Not associated -> this observation is about to spawn a new label.
            // Look for a unique stale same-class candidate to reap immediately.
            var staleIndex = -1;
            for (var i = 0; i < existing.Count; i++)
            {
                var candidate = existing[i];
                if (candidate.ClassId != newClassId)
                {
                    continue;
                }

                // Every same-class candidate reached here is already known to be
                // >= distanceThresholdMeters away — otherwise it would have won the
                // association loop above.
                var age = currentTime - candidate.LastSeenTime;
                if (age > gracePeriodSeconds)
                {
                    continue;
                }

                if (staleIndex >= 0)
                {
                    // A second in-grace same-class candidate: ambiguous which (if
                    // either) is "the same object that moved". Reap neither.
                    return Decision.SpawnOnly;
                }

                staleIndex = i;
            }

            return new Decision(-1, staleIndex);
        }
    }
}
