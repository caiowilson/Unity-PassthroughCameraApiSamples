// Object Tagger slice 5 Task 3 — edit-mode tests for label presentation logic
// (confirmation, smoothing, expiry).
//
// These tests satisfy Task 3 Step 4: confirmation threshold boundary, smoothing
// convergence, and expiry boundary must be assertable as pure logic with no
// RectTransform, GameObject, MonoBehaviour, or scene.

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class LabelPresentationTests
    {
        private const float GracePeriod = 3f;
        private const int ConfirmationThreshold = 2;

        // ===== Confirmation Threshold Tests =====

        [Test]
        public void IsVisibleReturnsFalseWhenBelowThreshold()
        {
            // N-1 confirmations → not yet visible.
            var isVisible = LabelPresentation.IsVisible(
                confirmationCount: ConfirmationThreshold - 1,
                confirmationThreshold: ConfirmationThreshold);

            Assert.IsFalse(isVisible, "a label with fewer than N confirmations must not be visible");
        }

        [Test]
        public void IsVisibleReturnsTrueAtThreshold()
        {
            // Exactly N confirmations → now visible.
            var isVisible = LabelPresentation.IsVisible(
                confirmationCount: ConfirmationThreshold,
                confirmationThreshold: ConfirmationThreshold);

            Assert.IsTrue(isVisible, "a label with exactly N confirmations must become visible");
        }

        [Test]
        public void IsVisibleReturnsTrueAboveThreshold()
        {
            // N+1 confirmations → stays visible.
            var isVisible = LabelPresentation.IsVisible(
                confirmationCount: ConfirmationThreshold + 1,
                confirmationThreshold: ConfirmationThreshold);

            Assert.IsTrue(isVisible, "a label with more than N confirmations must remain visible");
        }

        // ===== Smoothing Convergence Tests =====

        [Test]
        public void SmoothingMovesTowardTarget()
        {
            var smoothed = new Vector3(0f, 0f, 0f);
            var target = new Vector3(1f, 0f, 0f);
            float factor = 0.3f;

            var result = LabelPresentation.Smooth(smoothed, target, factor);

            // After one iteration with factor 0.3: 0 + 0.3 * (1 - 0) = 0.3
            Assert.AreEqual(new Vector3(0.3f, 0f, 0f), result, "smoothing must move toward the target");
            Assert.LessOrEqual(Vector3.Distance(result, target), Vector3.Distance(smoothed, target),
                "distance to target must not increase");
        }

        [Test]
        public void SmoothingConverges()
        {
            // Repeated smoothing iterations should converge toward target without overshoot.
            var smoothed = new Vector3(0f, 0f, 0f);
            var target = new Vector3(10f, 0f, 0f);
            float factor = 0.3f;

            for (int i = 0; i < 10; i++)
            {
                smoothed = LabelPresentation.Smooth(smoothed, target, factor);
            }

            // After 10 iterations with factor 0.3, should be quite close but not exactly at target.
            // (geometric series: 1 - (0.7)^10 ≈ 0.972, so ~9.72 out of 10)
            Assert.Greater(smoothed.x, 9f, "after many iterations, should be very close to target");
            Assert.Less(smoothed.x, 10f, "should not overshoot the target");
            Assert.AreEqual(0f, smoothed.y);
            Assert.AreEqual(0f, smoothed.z);
        }

        [Test]
        public void SmoothingMonotonicallyDecreaseDistance()
        {
            // Distance to target must decrease or stay the same with each iteration (no oscillation).
            var smoothed = new Vector3(0f, 0f, 0f);
            var target = new Vector3(5f, 3f, 2f);
            float factor = 0.2f;

            float prevDistance = Vector3.Distance(smoothed, target);
            for (int i = 0; i < 5; i++)
            {
                smoothed = LabelPresentation.Smooth(smoothed, target, factor);
                float currentDistance = Vector3.Distance(smoothed, target);
                Assert.LessOrEqual(currentDistance, prevDistance,
                    $"iteration {i}: distance should not increase (prev={prevDistance}, current={currentDistance})");
                prevDistance = currentDistance;
            }
        }

        [Test]
        public void SmoothingWithZeroFactorDoesNotMove()
        {
            // Edge case: factor = 0 means no change.
            var smoothed = new Vector3(1f, 2f, 3f);
            var target = new Vector3(10f, 20f, 30f);
            float factor = 0f;

            var result = LabelPresentation.Smooth(smoothed, target, factor);

            Assert.AreEqual(smoothed, result, "with factor 0, position should not change");
        }

        [Test]
        public void SmoothingWithFactorOneJumpsToTarget()
        {
            // Edge case: factor = 1 means jump directly to target.
            var smoothed = new Vector3(1f, 2f, 3f);
            var target = new Vector3(10f, 20f, 30f);
            float factor = 1f;

            var result = LabelPresentation.Smooth(smoothed, target, factor);

            Assert.AreEqual(target, result, "with factor 1, position should jump to target");
        }

        // ===== Expiry Tests =====

        [Test]
        public void IsExpiredReturnsFalseWhenRecentlyUpdated()
        {
            // Just updated -> not expired.
            float lastSeenTime = 10f;
            float currentTime = 10.5f;

            var isExpired = LabelPresentation.IsExpired(lastSeenTime, currentTime, GracePeriod);

            Assert.IsFalse(isExpired, "a recently updated label must not be expired");
        }

        [Test]
        public void IsExpiredReturnsFalseJustBeforeGracePeriod()
        {
            // Just before the 3s boundary.
            float lastSeenTime = 0f;
            float currentTime = 2.9f;

            var isExpired = LabelPresentation.IsExpired(lastSeenTime, currentTime, GracePeriod);

            Assert.IsFalse(isExpired, "just before grace period, label must not be expired");
        }

        [Test]
        public void IsExpiredReturnsTrueAtGracePeriod()
        {
            // Exactly at the boundary (elapsed > GracePeriod).
            float lastSeenTime = 0f;
            float currentTime = 3f;

            var isExpired = LabelPresentation.IsExpired(lastSeenTime, currentTime, GracePeriod);

            // With >, 3f - 0f = 3f is NOT > 3f, so not expired.
            Assert.IsFalse(isExpired, "at exactly grace period boundary, not expired yet");
        }

        [Test]
        public void IsExpiredReturnsTrueJustAfterGracePeriod()
        {
            // Just past the 3s boundary (elapsed > GracePeriod).
            float lastSeenTime = 0f;
            float currentTime = 3.1f;

            var isExpired = LabelPresentation.IsExpired(lastSeenTime, currentTime, GracePeriod);

            Assert.IsTrue(isExpired, "just after grace period, label must be expired");
        }

        [Test]
        public void IsExpiredReturnsTrueWhenWayPastGracePeriod()
        {
            // Well past grace period.
            float lastSeenTime = 0f;
            float currentTime = 10f;

            var isExpired = LabelPresentation.IsExpired(lastSeenTime, currentTime, GracePeriod);

            Assert.IsTrue(isExpired, "long after grace period, label must be expired");
        }

        // ===== Minimum-Apparent-Size Scale Tests (slice 5 Task 4 Step 3) =====
        //
        // The property under test is angular/apparent size, not the raw formula:
        // beyond referenceDistance, scale must grow exactly proportionally with
        // distance so that scale/distance (and therefore world-size/distance,
        // i.e. apparent size) stays CONSTANT out to 4m and beyond. At or below
        // referenceDistance, scale must not shrink below baseScale -- getting
        // closer than the reference point is never the legibility risk this
        // mechanism exists to fix.

        private const float ReferenceDistance = 1f;
        private const float BaseScale = 1f;

        [Test]
        public void ComputeCardScaleStaysAtBaseAtReferenceDistance()
        {
            var scale = LabelPresentation.ComputeCardScale(ReferenceDistance, BaseScale, ReferenceDistance);

            Assert.AreEqual(BaseScale, scale, 1e-5f, "at exactly referenceDistance, scale must equal baseScale");
        }

        [Test]
        public void ComputeCardScaleClampsToBaseBelowReferenceDistance()
        {
            // Closer than referenceDistance: must not shrink below baseScale.
            var scale = LabelPresentation.ComputeCardScale(0.3f, BaseScale, ReferenceDistance);

            Assert.AreEqual(BaseScale, scale, 1e-5f, "below referenceDistance, scale must be clamped to baseScale, not shrink further");
        }

        [Test]
        public void ComputeCardScaleGrowsProportionallyBeyondReferenceDistance()
        {
            // At 4m (the far end of the legibility acceptance range) with a 1m
            // reference distance, scale must be exactly 4x baseScale.
            var scale = LabelPresentation.ComputeCardScale(4f, BaseScale, ReferenceDistance);

            Assert.AreEqual(4f * BaseScale, scale, 1e-5f, "beyond referenceDistance, scale must grow linearly with distance");
        }

        [Test]
        public void ComputeCardScaleHoldsApparentSizeConstantBeyondReferenceDistance()
        {
            // The actual requirement: apparent (angular) size is world-size/distance,
            // i.e. proportional to scale/distance. For any two distances beyond
            // referenceDistance, scale/distance must be the SAME constant
            // (baseScale/referenceDistance) -- this is what "legible at 1m AND 4m"
            // cashes out to, not merely "scale increases".
            var scaleAt1_5m = LabelPresentation.ComputeCardScale(1.5f, BaseScale, ReferenceDistance);
            var scaleAt4m = LabelPresentation.ComputeCardScale(4f, BaseScale, ReferenceDistance);

            var apparentSizeAt1_5m = scaleAt1_5m / 1.5f;
            var apparentSizeAt4m = scaleAt4m / 4f;

            Assert.AreEqual(apparentSizeAt1_5m, apparentSizeAt4m, 1e-5f,
                "apparent size (scale/distance) must stay constant beyond referenceDistance");
            Assert.AreEqual(BaseScale / ReferenceDistance, apparentSizeAt4m, 1e-5f,
                "the constant apparent size beyond referenceDistance must equal baseScale/referenceDistance");
        }

        [Test]
        public void ComputeCardScaleWithNonUnitBaseScale()
        {
            // Boundary sanity with a non-1 baseScale, to catch an implementation
            // that hardcodes 1 instead of multiplying by baseScale.
            var scale = LabelPresentation.ComputeCardScale(2f, 0.5f, ReferenceDistance);

            Assert.AreEqual(1f, scale, 1e-5f, "baseScale must scale the whole result, not just the clamp floor");
        }
    }
}
