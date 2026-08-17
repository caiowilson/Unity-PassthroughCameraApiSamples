// Object Tagger manual-tagging Task 1 — edit-mode tests for hold-duration
// classification.

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class InputHoldClassificationTests
    {
        private const float ThresholdSeconds = 1f;

        [Test]
        public void JustBelowThresholdHasNotReached()
        {
            Assert.IsFalse(InputHoldClassification.HasReachedHoldThreshold(
                pressStartTime: 0f, currentTime: 0.9f, ThresholdSeconds));
        }

        [Test]
        public void ExactlyAtThresholdHasReached()
        {
            Assert.IsTrue(InputHoldClassification.HasReachedHoldThreshold(
                pressStartTime: 0f, currentTime: 1f, ThresholdSeconds));
        }

        [Test]
        public void JustAboveThresholdHasReached()
        {
            Assert.IsTrue(InputHoldClassification.HasReachedHoldThreshold(
                pressStartTime: 0f, currentTime: 1.1f, ThresholdSeconds));
        }

        [Test]
        public void ZeroElapsedHasNotReached()
        {
            Assert.IsFalse(InputHoldClassification.HasReachedHoldThreshold(
                pressStartTime: 5f, currentTime: 5f, ThresholdSeconds));
        }
    }
}
