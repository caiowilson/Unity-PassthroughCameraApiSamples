using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteAimPlacementTests
    {
        [Test]
        public void ResolvedFrameUsesOneWorldPointForReticleAndRequest()
        {
            var viewerPosition = new Vector3(1f, 2f, 3f);
            var targetPoint = viewerPosition + new Vector3(0.2f, -0.1f, 2f);

            var frame = RemoteAimPlacement.Resolved(viewerPosition, targetPoint);

            Assert.IsTrue(frame.HasResolvedTarget);
            Assert.AreEqual(targetPoint, frame.ReticleWorldPosition);
            Assert.AreEqual(targetPoint, frame.ResolvedPoint);
            Assert.AreEqual(
                RemoteAimPlacement.AuthoredScaleAtReferenceDistance *
                Vector3.Distance(viewerPosition, targetPoint) /
                RemoteAimPlacement.ReferenceDistance,
                frame.UniformScale,
                0.0000001f);
        }

        [TestCase(0.5f, 0.00025f)]
        [TestCase(1f, 0.0005f)]
        [TestCase(2f, 0.001f)]
        [TestCase(4f, 0.002f)]
        public void ResolvedScalePreservesAngularSize(float distance, float expectedScale)
        {
            var frame = RemoteAimPlacement.Resolved(
                Vector3.zero,
                Vector3.forward * distance);

            Assert.AreEqual(expectedScale, frame.UniformScale, 0.0000001f);
        }

        [Test]
        public void FallbackStaysCenteredAtComfortableReferenceDistanceAndCannotStart()
        {
            var viewerPosition = new Vector3(1f, 2f, 3f);
            var viewerRotation = Quaternion.Euler(0f, 90f, 0f);

            var frame = RemoteAimPlacement.Fallback(viewerPosition, viewerRotation);

            Assert.IsFalse(frame.HasResolvedTarget);
            Assert.AreEqual(
                viewerPosition + viewerRotation * Vector3.forward *
                RemoteAimPlacement.ReferenceDistance,
                frame.ReticleWorldPosition);
            Assert.AreEqual(
                RemoteAimPlacement.AuthoredScaleAtReferenceDistance,
                frame.UniformScale,
                0.0000001f);
        }
    }
}
