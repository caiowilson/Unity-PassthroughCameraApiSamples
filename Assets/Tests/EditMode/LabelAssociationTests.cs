// Object Tagger slice 5 Task 2, simplified by manual-tagging Task 3 —
// edit-mode tests for class+world-distance label matching.
//
// The grace-period/re-placement tests from slice 5 (SameClassButFarSpawns...,
// SameClassFarAndOutsideGracePeriod..., SameFrameSameClassFarCandidate...,
// AmbiguousMultipleStaleSameClassCandidates...) are deleted along with the
// rule they tested: FindAssociationIndex has no removal/re-placement
// behavior left to pin.

using System.Collections.Generic;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class LabelAssociationTests
    {
        private const float Threshold = 0.1f;

        private static List<LabelAssociation.Existing> One(int classId, Vector3 pos) =>
            new() { new LabelAssociation.Existing(classId, pos) };

        [Test]
        public void SameClassAndCloseAssociates()
        {
            // 3cm away, well under the 0.1m threshold.
            var existing = One(classId: 5, pos: new Vector3(1f, 0f, 2f));
            var newPos = new Vector3(1.03f, 0f, 2f);

            var index = LabelAssociation.FindAssociationIndex(existing, 5, newPos, Threshold);

            Assert.AreEqual(0, index, "close same-class observation must associate to the existing label");
        }

        [Test]
        public void DifferentClassOverlappingDoesNotAssociate()
        {
            // Same position, different class. LabelOverlap owns the visual
            // offset for this case; FindAssociationIndex must never match
            // across classes.
            var existing = One(classId: 5, pos: new Vector3(1f, 0f, 2f));
            var newPos = new Vector3(1f, 0f, 2f);

            var index = LabelAssociation.FindAssociationIndex(existing, 6, newPos, Threshold);

            Assert.AreEqual(-1, index, "different class must never associate");
        }

        [Test]
        public void JustUnderThresholdAssociates()
        {
            var existing = One(classId: 5, pos: Vector3.zero);
            var newPos = new Vector3(Threshold - 0.01f, 0f, 0f);

            var index = LabelAssociation.FindAssociationIndex(existing, 5, newPos, Threshold);

            Assert.AreEqual(0, index, "distance just under the threshold must associate");
        }

        [Test]
        public void ExactlyAtThresholdDoesNotAssociate()
        {
            // Strict less-than: a distance exactly equal to the threshold is
            // treated as "not close enough".
            var existing = One(classId: 5, pos: Vector3.zero);
            var newPos = new Vector3(Threshold, 0f, 0f);

            var index = LabelAssociation.FindAssociationIndex(existing, 5, newPos, Threshold);

            Assert.AreEqual(-1, index, "distance exactly at the threshold must not associate");
        }

        [Test]
        public void JustOverThresholdDoesNotAssociate()
        {
            var existing = One(classId: 5, pos: Vector3.zero);
            var newPos = new Vector3(Threshold + 0.01f, 0f, 0f);

            var index = LabelAssociation.FindAssociationIndex(existing, 5, newPos, Threshold);

            Assert.AreEqual(-1, index, "distance just over the threshold must not associate");
        }

        [Test]
        public void PicksTheNearestSameClassCandidateWhenMultipleAreInRange()
        {
            var existing = new List<LabelAssociation.Existing>
            {
                new(classId: 5, worldPosition: new Vector3(0.08f, 0f, 0f)),
                new(classId: 5, worldPosition: new Vector3(0.03f, 0f, 0f)),
            };
            var newPos = Vector3.zero;

            var index = LabelAssociation.FindAssociationIndex(existing, 5, newPos, Threshold);

            Assert.AreEqual(1, index, "must associate to the nearer of two in-range same-class candidates");
        }

        [Test]
        public void EmptyExistingReturnsNegativeOne()
        {
            var existing = new List<LabelAssociation.Existing>();

            var index = LabelAssociation.FindAssociationIndex(existing, 5, Vector3.zero, Threshold);

            Assert.AreEqual(-1, index);
        }
    }
}
