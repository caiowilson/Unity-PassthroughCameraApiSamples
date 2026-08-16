// Object Tagger slice 5 Task 2 — edit-mode tests for class+world-distance
// label association.
//
// These exist to satisfy Task 2 Step 4: the matching decision must be
// assertable as pure logic — given existing label snapshots (class id, world
// position, last-seen time) and a new observation (class id, world position),
// which associate, which spawn, which trigger the re-placement removal. No
// RectTransform, no GameObject, no MonoBehaviour, no scene.

using System.Collections.Generic;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class LabelAssociationTests
    {
        private const float Threshold = 0.3f;
        private const float GracePeriod = 3f;

        private static List<LabelAssociation.Existing> One(int classId, Vector3 pos, float lastSeenTime) =>
            new() { new LabelAssociation.Existing(classId, pos, lastSeenTime) };

        [Test]
        public void SameClassAndCloseAssociates()
        {
            // 10cm away, well under the 0.3m threshold.
            var existing = One(classId: 5, pos: new Vector3(1f, 0f, 2f), lastSeenTime: 0f);
            var newPos = new Vector3(1.1f, 0f, 2f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 0.1f, Threshold, GracePeriod);

            Assert.AreEqual(0, decision.AssociatedIndex, "close same-class observation must reuse the existing label");
            Assert.AreEqual(-1, decision.RemoveIndex, "an association must not also remove anything");
        }

        [Test]
        public void SameClassButFarSpawnsNewAndReapsTheStaleOne()
        {
            // 2m away — far outside the threshold — but still inside the 3s grace
            // period (only 0.5s old). This is the moved-object case Step 3 targets:
            // exactly one same-class candidate, so it is unambiguous.
            var existing = One(classId: 5, pos: new Vector3(0f, 0f, 0f), lastSeenTime: 1.0f);
            var newPos = new Vector3(2f, 0f, 0f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 1.5f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex, "far observation must spawn a new label, not reuse");
            Assert.AreEqual(0, decision.RemoveIndex, "the unique stale same-class label must be reaped immediately");
        }

        [Test]
        public void SameClassFarAndOutsideGracePeriodSpawnsWithoutRemoval()
        {
            // Already past the grace period -> Update()'s own expiry loop would
            // already have reaped this; the re-placement rule must not double-fire.
            var existing = One(classId: 5, pos: new Vector3(0f, 0f, 0f), lastSeenTime: 0f);
            var newPos = new Vector3(2f, 0f, 0f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 5f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex);
            Assert.AreEqual(-1, decision.RemoveIndex, "an already-expired candidate is not this rule's concern");
        }

        [Test]
        public void AmbiguousMultipleStaleSameClassCandidatesReapsNeither()
        {
            // Two same-class labels, both far from the new observation and both
            // still in grace. Narrowest-correct interpretation: we cannot tell
            // which one (if either) moved, so neither is reaped — a lingering
            // label for up to the grace period is preferable to misattributing an
            // unrelated, still-present object's label.
            var existing = new List<LabelAssociation.Existing>
            {
                new(classId: 5, worldPosition: new Vector3(0f, 0f, 0f), lastSeenTime: 1f),
                new(classId: 5, worldPosition: new Vector3(5f, 0f, 0f), lastSeenTime: 1f),
            };
            var newPos = new Vector3(10f, 0f, 0f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 1.2f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex);
            Assert.AreEqual(-1, decision.RemoveIndex, "ambiguous which stale record moved -> reap neither");
        }

        [Test]
        public void DifferentClassOverlappingDoesNotEvict()
        {
            // Same position, different class. Upstream would have evicted this;
            // the locked decision removes eviction entirely (Task 5 owns the
            // visual offset for this case). Both must remain untouched.
            var existing = One(classId: 5, pos: new Vector3(1f, 0f, 2f), lastSeenTime: 0f);
            var newPos = new Vector3(1f, 0f, 2f);

            var decision = LabelAssociation.Decide(existing, 6, newPos, currentTime: 0.1f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex, "different class must never associate");
            Assert.AreEqual(-1, decision.RemoveIndex, "different-class overlap must not evict");
        }

        [Test]
        public void JustUnderThresholdAssociates()
        {
            var existing = One(classId: 5, pos: Vector3.zero, lastSeenTime: 0f);
            var newPos = new Vector3(Threshold - 0.01f, 0f, 0f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 0.1f, Threshold, GracePeriod);

            Assert.AreEqual(0, decision.AssociatedIndex, "distance just under the threshold must associate");
        }

        [Test]
        public void ExactlyAtThresholdDoesNotAssociate()
        {
            // Strict less-than: a distance exactly equal to the threshold is
            // treated as "not close enough", matching a half-open [0, threshold)
            // band with no ambiguity at the boundary itself.
            var existing = One(classId: 5, pos: Vector3.zero, lastSeenTime: 0f);
            var newPos = new Vector3(Threshold, 0f, 0f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 0.1f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex, "distance exactly at the threshold must not associate");
        }

        [Test]
        public void JustOverThresholdDoesNotAssociate()
        {
            var existing = One(classId: 5, pos: Vector3.zero, lastSeenTime: 0f);
            var newPos = new Vector3(Threshold + 0.01f, 0f, 0f);

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 0.1f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex, "distance just over the threshold must not associate");
        }

        [Test]
        public void PicksTheNearestSameClassCandidateWhenMultipleAreInRange()
        {
            var existing = new List<LabelAssociation.Existing>
            {
                new(classId: 5, worldPosition: new Vector3(0.2f, 0f, 0f), lastSeenTime: 0f),
                new(classId: 5, worldPosition: new Vector3(0.05f, 0f, 0f), lastSeenTime: 0f),
            };
            var newPos = Vector3.zero;

            var decision = LabelAssociation.Decide(existing, 5, newPos, currentTime: 0.1f, Threshold, GracePeriod);

            Assert.AreEqual(1, decision.AssociatedIndex, "must reuse the nearer of two in-range same-class candidates");
        }

        [Test]
        public void EmptyExistingSpawnsWithNoRemoval()
        {
            var existing = new List<LabelAssociation.Existing>();

            var decision = LabelAssociation.Decide(existing, 5, Vector3.zero, currentTime: 0f, Threshold, GracePeriod);

            Assert.AreEqual(-1, decision.AssociatedIndex);
            Assert.AreEqual(-1, decision.RemoveIndex);
        }
    }
}
