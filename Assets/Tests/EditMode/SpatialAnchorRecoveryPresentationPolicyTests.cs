using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialAnchorRecoveryPresentationPolicyTests
    {
        [Test]
        public void RestoringShowsPassiveStatusWithoutRecoveryPanel()
        {
            var presentation = SpatialAnchorRecoveryPresentationPolicy.Evaluate(
                SpatialAnchorRestorationState.Restoring);

            Assert.AreEqual(
                SpatialAnchorRecoveryPresentationPolicy.RestoringStatus,
                presentation.StatusText);
            Assert.IsFalse(presentation.ShowsRecoveryPanel);
        }

        [Test]
        public void UnavailableShowsStatusAndRecoveryPanel()
        {
            var presentation = SpatialAnchorRecoveryPresentationPolicy.Evaluate(
                SpatialAnchorRestorationState.Unavailable);

            Assert.AreEqual(
                SpatialAnchorRecoveryPresentationPolicy.UnavailableStatus,
                presentation.StatusText);
            Assert.IsTrue(presentation.ShowsRecoveryPanel);
        }

        [TestCase(SpatialAnchorRestorationState.NoSavedSpace)]
        [TestCase(SpatialAnchorRestorationState.Ready)]
        [TestCase(SpatialAnchorRestorationState.Resetting)]
        public void OtherStatesHideTheRecoveryPresentation(SpatialAnchorRestorationState state)
        {
            var presentation = SpatialAnchorRecoveryPresentationPolicy.Evaluate(state);

            Assert.IsNull(presentation.StatusText);
            Assert.IsFalse(presentation.ShowsRecoveryPanel);
        }
    }
}
