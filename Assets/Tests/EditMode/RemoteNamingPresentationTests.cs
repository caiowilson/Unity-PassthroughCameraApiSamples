using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteNamingPresentationTests
    {
        [TestCase("Identifying...")]
        [TestCase("coffee mug")]
        public void ActiveRemotePresentationIsFirstAndFitsTwoLines(string remotePresentation)
        {
            var text = RemoteNamingPresentation.Compose(
                CompanionReadiness.Ready(),
                remotePresentation);

            Assert.AreEqual($"{remotePresentation}\nMac: ready", text);
            Assert.LessOrEqual(text.Split('\n').Length, 2);
            StringAssert.DoesNotContain("Unity Inference Engine", text);
            StringAssert.DoesNotContain("Yolo", text);
            StringAssert.DoesNotContain("Detecting objects", text);
        }

        [Test]
        public void ReadyIdlePresentationShowsInputCueBeforeReadiness()
        {
            Assert.AreEqual(
                "Press A or pinch to identify\nMac: ready",
                RemoteNamingPresentation.Compose(CompanionReadiness.Ready(), null));
        }

        [Test]
        public void UnavailableIdlePresentationShowsReadinessWithoutInvalidInputCue()
        {
            Assert.AreEqual(
                "Mac: unavailable - check the companion",
                RemoteNamingPresentation.Compose(CompanionReadiness.Unavailable(), null));
        }
    }
}
