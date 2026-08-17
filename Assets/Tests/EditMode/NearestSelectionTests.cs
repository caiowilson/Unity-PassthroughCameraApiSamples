// Object Tagger manual-tagging Task 1 — edit-mode tests for IndexOfMinimum.

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class NearestSelectionTests
    {
        [Test]
        public void EmptyListReturnsNegativeOne()
        {
            Assert.AreEqual(-1, NearestSelection.IndexOfMinimum(new float[0]));
        }

        [Test]
        public void SingleElementReturnsZero()
        {
            Assert.AreEqual(0, NearestSelection.IndexOfMinimum(new[] { 5f }));
        }

        [Test]
        public void PicksTheSmallestValue()
        {
            var distances = new[] { 3f, 0.5f, 2f };
            Assert.AreEqual(1, NearestSelection.IndexOfMinimum(distances));
        }

        [Test]
        public void TieResolvesToFirstOccurrence()
        {
            var distances = new[] { 1f, 1f, 2f };
            Assert.AreEqual(0, NearestSelection.IndexOfMinimum(distances));
        }

        [Test]
        public void SmallestAtTheEndIsStillFound()
        {
            var distances = new[] { 9f, 8f, 0.1f };
            Assert.AreEqual(2, NearestSelection.IndexOfMinimum(distances));
        }
    }
}
