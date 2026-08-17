// Object Tagger slice 5 Task 5 Step 1 — edit-mode tests for visual-overlap
// detection and the vertical-offset resolution.
//
// These satisfy the task brief's testability requirement: angular-overlap
// detection and offset-amount computation must be assertable as pure logic,
// with no RectTransform/GameObject/MonoBehaviour/scene involved. Covers: two
// cards above threshold (no offset), below threshold (farther offset, nearer
// untouched), a boundary case at/near threshold, a 3-label chain, and a
// determinism check (same input twice -> same output, pinning that offsets
// never accumulate across calls -- see LabelOverlap.ComputeVerticalOffsets's
// comment on why that matters).

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class LabelOverlapTests
    {
        private const float ThresholdDegrees = 3f;
        private const float OffsetAtReferenceDistance = 0.06f;
        private const float BaseCardScale = 1f;
        private const float ReferenceDistanceMeters = 1f;

        private static readonly Vector3 Camera = Vector3.zero;

        // ===== AngularSeparationDegrees =====

        [Test]
        public void AngularSeparationIsZeroForIdenticalDirection()
        {
            var a = new Vector3(0f, 0f, 1f);
            var b = new Vector3(0f, 0f, 2f); // same direction from camera, farther away

            var angle = LabelOverlap.AngularSeparationDegrees(Camera, a, b);

            Assert.AreEqual(0f, angle, 1e-4f, "two positions along the same ray from the camera must have zero angular separation");
        }

        [Test]
        public void AngularSeparationIsNinetyForPerpendicularDirections()
        {
            var a = new Vector3(0f, 0f, 1f);
            var b = new Vector3(1f, 0f, 0f);

            var angle = LabelOverlap.AngularSeparationDegrees(Camera, a, b);

            Assert.AreEqual(90f, angle, 1e-3f, "perpendicular directions from the camera must be 90 degrees apart");
        }

        // ===== IsOverlapping =====

        [Test]
        public void IsOverlappingTrueBelowThreshold()
        {
            Assert.IsTrue(LabelOverlap.IsOverlapping(2f, ThresholdDegrees));
        }

        [Test]
        public void IsOverlappingFalseAtThreshold()
        {
            // Strict '<', mirroring LabelPresentation.IsExpired's own
            // strict-boundary convention: exactly AT the threshold is "clear".
            Assert.IsFalse(LabelOverlap.IsOverlapping(ThresholdDegrees, ThresholdDegrees));
        }

        [Test]
        public void IsOverlappingFalseAboveThreshold()
        {
            Assert.IsFalse(LabelOverlap.IsOverlapping(5f, ThresholdDegrees));
        }

        // ===== ComputeVerticalOffsets =====

        [Test]
        public void TwoLabelsAboveThresholdGetNoOffset()
        {
            // Far apart angularly (90 degrees) -- well clear.
            var positions = new[]
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(1f, 0f, 0f),
            };

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(0f, offsets[0], "clear pair: nearer/either label must not be offset");
            Assert.AreEqual(0f, offsets[1], "clear pair: nearer/either label must not be offset");
        }

        [Test]
        public void TwoLabelsBelowThresholdOffsetsOnlyTheFartherOne()
        {
            // Same direction (0 degrees apart, well under threshold), index 0 at
            // 1m (nearer), index 1 at 2m (farther).
            var positions = new[]
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 2f),
            };

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(0f, offsets[0], "the NEARER label must keep its natural, unoffset position");
            Assert.Greater(offsets[1], 0f, "the FARTHER label must be pushed clear");
        }

        [Test]
        public void FartherOffsetScalesWithCardScaleBeyondReferenceDistance()
        {
            // Farther label at 4m (4x ReferenceDistanceMeters) -> ComputeCardScale
            // there is 4x baseScale, so the offset should be 4x the base offset.
            var positions = new[]
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 4f),
            };

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(0f, offsets[0]);
            Assert.AreEqual(OffsetAtReferenceDistance * 4f, offsets[1], 1e-4f,
                "offset must scale with the farther label's own ComputeCardScale factor");
        }

        [Test]
        public void BoundaryJustBelowThresholdOffsetsFarther()
        {
            // Construct two positions with angular separation just under 3 degrees.
            var near = new Vector3(0f, 0f, 10f);
            var angleRadians = (ThresholdDegrees - 0.1f) * Mathf.Deg2Rad;
            var far = new Vector3(Mathf.Sin(angleRadians) * 10f, 0f, Mathf.Cos(angleRadians) * 10f);
            // far is constructed at the same 10m radius as near but with an equal
            // camera-distance; make far strictly farther so "which one gets
            // offset" is unambiguous.
            far *= 1.01f;

            var positions = new[] { near, far };

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(0f, offsets[0], "nearer label at a just-below-threshold separation must stay unoffset");
            Assert.Greater(offsets[1], 0f, "farther label at a just-below-threshold separation must be offset");
        }

        [Test]
        public void BoundaryJustAboveThresholdGetsNoOffset()
        {
            var near = new Vector3(0f, 0f, 10f);
            var angleRadians = (ThresholdDegrees + 0.1f) * Mathf.Deg2Rad;
            var far = new Vector3(Mathf.Sin(angleRadians) * 10f, 0f, Mathf.Cos(angleRadians) * 10f);

            var positions = new[] { near, far };

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(0f, offsets[0]);
            Assert.AreEqual(0f, offsets[1], "just clear of threshold, neither label should be offset");
        }

        [Test]
        public void ThreeLabelChainResolvesWithoutRunaway()
        {
            // Three labels all along nearly the same sightline at increasing
            // distance -- every pair overlaps. Verifies the pass is stable (no
            // exception, no unbounded growth) with 3+ labels, per the task
            // brief's "behaves sanely" bar rather than a full fixed-point
            // guarantee.
            var positions = new[]
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(0.01f, 0f, 2f),
                new Vector3(0.02f, 0f, 3f),
            };

            var offsets = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(0f, offsets[0], "the nearest of the chain must stay unoffset");
            Assert.Greater(offsets[1], 0f, "the middle label overlaps the nearest and must be offset");
            Assert.Greater(offsets[2], 0f, "the farthest label overlaps at least one nearer label and must be offset");
            foreach (var offset in offsets)
            {
                Assert.Less(offset, 10f, "no offset should blow up unboundedly for a small chain");
            }
        }

        [Test]
        public void SameInputTwiceYieldsSameOutput()
        {
            // Pins that the function is pure: no accumulation, no hidden state.
            // See LabelOverlap.ComputeVerticalOffsets's comment for why this
            // property is load-bearing (it is what makes the caller's
            // assign-not-add pattern in SentisInferenceUiManager safe).
            var positions = new[]
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 2f),
            };

            var first = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);
            var second = LabelOverlap.ComputeVerticalOffsets(
                Camera, positions, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);

            Assert.AreEqual(first[0], second[0], 1e-6f);
            Assert.AreEqual(first[1], second[1], 1e-6f);
        }

        [Test]
        public void ZeroOrOneLabelYieldsNoOffsets()
        {
            var zero = LabelOverlap.ComputeVerticalOffsets(
                Camera, System.Array.Empty<Vector3>(), ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);
            Assert.AreEqual(0, zero.Length);

            var one = LabelOverlap.ComputeVerticalOffsets(
                Camera, new[] { new Vector3(0f, 0f, 1f) }, ThresholdDegrees, OffsetAtReferenceDistance, BaseCardScale, ReferenceDistanceMeters);
            Assert.AreEqual(1, one.Length);
            Assert.AreEqual(0f, one[0]);
        }
    }
}
