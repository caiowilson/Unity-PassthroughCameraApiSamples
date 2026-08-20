using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteImageCropTests
    {
        [TestCase(1280, 960, 352, 192, 576)]
        [TestCase(960, 1280, 192, 352, 576)]
        [TestCase(641, 479, 177, 96, 287)]
        public void PlanCentersSixtyPercentSquareAndTargetsFixedOutput(
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
    }
}
