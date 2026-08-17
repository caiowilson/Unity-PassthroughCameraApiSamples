// Object Tagger slice 5 Task 3, simplified by manual-tagging Task 3 —
// edit-mode tests for label presentation logic (smoothing, billboard/scale).
//
// The Confirmation Threshold and Expiry sections are deleted along with
// IsVisible/IsExpired themselves — see LabelPresentation.cs's header for why.
// What remains satisfies smoothing convergence and the minimum-apparent-size
// scale / billboard rotation as pure logic, with no RectTransform, GameObject,
// MonoBehaviour, or scene.

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class LabelPresentationTests
    {
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

        // ===== FaceCameraRotation Tests (final-review fix — CRITICAL finding) =====
        //
        // These pin the fix for the finding that labels did not actually billboard:
        // rotation must be computed fresh from the CURRENT camera position every
        // call, not cached from wherever the camera was at inference time.

        [Test]
        public void FaceCameraRotationFacesCameraFromInFront()
        {
            // Label sits 5m down +Z from the camera at the origin. The rotation's
            // forward axis must point from camera toward label, i.e. +Z.
            var labelPosition = new Vector3(0f, 0f, 5f);
            var cameraPosition = Vector3.zero;

            var rotation = LabelPresentation.FaceCameraRotation(labelPosition, cameraPosition);
            var forward = rotation * Vector3.forward;

            Assert.Less(Vector3.Distance(forward, Vector3.forward), 1e-4f,
                "rotation's forward axis must point from the camera toward the label");
        }

        [Test]
        public void FaceCameraRotationFollowsAMovedCameraNotAStaleOne()
        {
            // This is the direct regression test for the billboard bug: the SAME
            // label position must produce a DIFFERENT rotation once the camera has
            // moved, proving rotation is recomputed from the current camera position
            // rather than frozen at some earlier (stale) one.
            var labelPosition = new Vector3(0f, 0f, 5f);
            var cameraPositionBefore = Vector3.zero;
            var cameraPositionAfter = new Vector3(5f, 0f, 5f);

            var rotationBefore = LabelPresentation.FaceCameraRotation(labelPosition, cameraPositionBefore);
            var rotationAfter = LabelPresentation.FaceCameraRotation(labelPosition, cameraPositionAfter);

            Assert.Greater(Quaternion.Angle(rotationBefore, rotationAfter), 1f,
                "rotation must change once the camera moves, not stay frozen at its old value");

            // And it must face the NEW camera position specifically, not just any
            // different direction.
            var forwardAfter = rotationAfter * Vector3.forward;
            var expectedDirection = (labelPosition - cameraPositionAfter).normalized;
            Assert.Less(Vector3.Distance(forwardAfter, expectedDirection), 1e-4f,
                "after the camera moves, rotation must face the label from the NEW camera position");
        }

        [Test]
        public void FaceCameraRotationFacesCameraFromTheSide()
        {
            // Camera off to the side of the label — sanity check the formula isn't
            // accidentally axis-locked to a single approach direction.
            var labelPosition = new Vector3(0f, 0f, 0f);
            var cameraPosition = new Vector3(3f, 0f, 0f);

            var rotation = LabelPresentation.FaceCameraRotation(labelPosition, cameraPosition);
            var forward = rotation * Vector3.forward;
            var expectedDirection = new Vector3(-1f, 0f, 0f);

            Assert.Less(Vector3.Distance(forward, expectedDirection), 1e-4f,
                "rotation must face toward the label from wherever the camera actually is");
        }
    }
}
