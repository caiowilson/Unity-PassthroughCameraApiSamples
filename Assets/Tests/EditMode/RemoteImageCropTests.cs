using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteImageCropTests
    {
        // 0.45 of the SHORTER edge, rounded half up, centered with any odd
        // remainder pixel kept on the right/top. Worked examples:
        //   1280x960 -> floor(960*0.45 + 0.5) = 432, x = (1280-432)/2 = 424
        //   641x479  -> floor(479*0.45 + 0.5) = 216, x = (641-216)/2  = 212
        [TestCase(1280, 960, 424, 264, 432)]
        [TestCase(960, 1280, 264, 424, 432)]
        [TestCase(641, 479, 212, 131, 216)]
        public void PlanCentersFortyFivePercentSquareAndTargetsFixedOutput(
            int frameWidth,
            int frameHeight,
            int expectedX,
            int expectedY,
            int expectedEdge)
        {
            var created = RemoteImageCrop.TryCreatePlan(frameWidth, frameHeight, out var plan);

            Assert.IsTrue(created);
            Assert.AreEqual(
                new RectInt(expectedX, expectedY, expectedEdge, expectedEdge),
                plan.SourceRect);
            Assert.AreEqual(new Vector2Int(frameWidth, frameHeight), plan.FrameSize);
            Assert.AreEqual(new Vector2Int(896, 896), plan.OutputSize);
            Assert.AreEqual(80, plan.JpegQuality);
        }

        [TestCase(0, 960)]
        [TestCase(1280, 0)]
        [TestCase(-1, 960)]
        [TestCase(1280, -1)]
        public void PlanRejectsInvalidFrameDimensions(int frameWidth, int frameHeight)
        {
            Assert.IsFalse(RemoteImageCrop.TryCreatePlan(frameWidth, frameHeight, out _));
        }

        [Test]
        public void NormalizedRectMatchesTheSourceRectOverTheFrame()
        {
            RemoteImageCrop.TryCreatePlan(1280, 960, out var plan);

            var normalized = plan.NormalizedRect;

            Assert.AreEqual(424f / 1280f, normalized.x, 1e-6f);
            Assert.AreEqual(264f / 960f, normalized.y, 1e-6f);
            Assert.AreEqual(432f / 1280f, normalized.width, 1e-6f);
            Assert.AreEqual(432f / 960f, normalized.height, 1e-6f);
        }

        [Test]
        public void NormalizedRectIsCenteredSoAFlipWouldBeUndetectable()
        {
            // The crop is symmetric about the frame centre, which is why this
            // slice is immune to the Y-flip bug DetectionProjection warns about
            // in capitals. If a future crop stops being centered, THIS test
            // fails first and the square's flip handling must be revisited.
            RemoteImageCrop.TryCreatePlan(1280, 960, out var plan);

            var normalized = plan.NormalizedRect;

            Assert.AreEqual(0.5f, normalized.center.x, 1e-6f);
            Assert.AreEqual(0.5f, normalized.center.y, 1e-6f);
        }
    }
}
