// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 2, simplified by manual-tagging Task 3 —
// class-plus-distance label matching.
//
// Manual tagging removed the re-placement/reap-stale rule entirely: that
// rule existed to auto-respawn a label at a new position when its old one
// aged out of the association threshold but was still within its grace
// period — both auto-respawn and grace-period expiry are gone under manual
// tagging. What is left is the part that still matters for "keep tracking
// while visible" (spec): given a new observation, does it match an EXISTING
// committed label closely enough to update it, or not. There is no third
// outcome (spawn) here — spawning only happens at explicit commit
// (SentisInferenceUiManager.TryCommitLiveCandidate), which calls this same
// function to decide associate-vs-create.
//
// A top-level PUBLIC static type, not nested/internal — this project
// rejects InternalsVisibleTo as a seam (AssemblySeamTests.cs).

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class LabelAssociation
    {
        /// A read-only snapshot of one currently-committed label — only what
        /// the matching decision needs.
        public readonly struct Existing
        {
            public readonly int ClassId;
            public readonly Vector3 WorldPosition;

            public Existing(int classId, Vector3 worldPosition)
            {
                ClassId = classId;
                WorldPosition = worldPosition;
            }
        }

        /// Finds the nearest existing label of the same class within
        /// distanceThresholdMeters of newWorldPosition, or -1 if none
        /// qualifies. A straight Vector3.Distance check, not IoU.
        ///
        /// Different-class overlap is not this function's concern: when a
        /// new observation overlaps a different-class existing label in
        /// space, both stay visible and the farther one gets a visual
        /// offset (LabelOverlap, unrelated to this function).
        public static int FindAssociationIndex(
            IReadOnlyList<Existing> existing,
            int newClassId,
            Vector3 newWorldPosition,
            float distanceThresholdMeters)
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

            return associatedIndex;
        }
    }
}
