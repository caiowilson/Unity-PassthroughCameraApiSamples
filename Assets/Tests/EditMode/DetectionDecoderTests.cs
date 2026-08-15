// Object Tagger slice 3 — edit-mode tests for the extracted decoding logic.
//
// These exist to satisfy spec line 85: "edit-mode tests green for decoding,
// threshold, and NMS-with-score." Note what is NOT here: no Worker, no
// BackendType, no Tensor<T>, no scene, no MonoBehaviour. That is the entire
// point of Task 3's extraction. Before it, reaching this logic required
// InternalsVisibleTo AND a live inference backend, which is not an edit-mode
// test however it is labelled.

using System.Collections.Generic;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class DetectionDecoderTests
    {
        // Boxes are flat, 4 floats each: (topLeftX, topLeftY, bottomRightX, bottomRightY).
        private static readonly List<(int classId, Vector4 boundingBox, float score)> Results = new();

        private static List<(int classId, Vector4 boundingBox, float score)> Run(
            float[] boxes, int[] classIds, float[] scores,
            float iou = 0.6f, float scoreThreshold = 0.23f, int maxAccepted = int.MaxValue,
            HashSet<int> allowed = null)
        {
            var outList = new List<(int classId, Vector4 boundingBox, float score)>();
            DetectionDecoder.SelectDetections(boxes, classIds, scores, iou, scoreThreshold, maxAccepted, allowed, outList);
            return outList;
        }

        /// Two boxes far apart so NMS suppresses nothing; isolates the threshold.
        private static float[] TwoDisjointBoxes() => new float[]
        {
            0f, 0f, 10f, 10f,
            100f, 100f, 110f, 110f,
        };

        [Test]
        public void ScoreThresholdIsInclusive()
        {
            // Upstream filters with `>=`, not `>`. A detection sitting exactly ON the
            // threshold must survive. Tightening this to `>` would silently drop
            // borderline detections and look like a model regression.
            var kept = Run(TwoDisjointBoxes(), new[] { 0, 1 }, new[] { 0.23f, 0.22f }, scoreThreshold: 0.23f);

            Assert.AreEqual(1, kept.Count, "exactly the on-threshold detection should survive");
            Assert.AreEqual(0, kept[0].classId, "the 0.23 detection is the one at the threshold");
        }

        [Test]
        public void ScoreSurvivesIntoTheTuple()
        {
            // The whole point of slice 3 Task 2. Upstream discarded this value at the
            // moment it built the tuple, despite holding it to filter and sort.
            var kept = Run(TwoDisjointBoxes(), new[] { 7, 9 }, new[] { 0.9f, 0.5f });

            Assert.AreEqual(2, kept.Count);
            Assert.AreEqual(0.9f, kept[0].score, 1e-6f);
            Assert.AreEqual(0.5f, kept[1].score, 1e-6f);
        }

        [Test]
        public void ResultsAreOrderedByScoreDescending()
        {
            // The acceptance cap depends on this ordering: cap without sort keeps
            // arbitrary detections rather than the most confident ones.
            var kept = Run(TwoDisjointBoxes(), new[] { 0, 1 }, new[] { 0.4f, 0.95f });

            Assert.AreEqual(2, kept.Count);
            Assert.Greater(kept[0].score, kept[1].score, "highest score must come first");
            Assert.AreEqual(1, kept[0].classId, "the 0.95 detection is classId 1");
        }

        [Test]
        public void SuppressionIsClassAgnostic()
        {
            // REGRESSION GUARD. Upstream suppresses overlapping boxes REGARDLESS of
            // class — its own comment says so. "Fixing" this into per-class NMS is a
            // tempting change that would alter what the device shows, and slice 2's
            // gate figures were recorded against the current behaviour.
            var heavilyOverlapping = new float[]
            {
                0f, 0f, 10f, 10f,
                0f, 0f, 10f, 10f,   // identical box, DIFFERENT class
            };

            var kept = Run(heavilyOverlapping, new[] { 3, 42 }, new[] { 0.9f, 0.8f });

            Assert.AreEqual(1, kept.Count,
                "identical boxes of different classes must still suppress each other");
            Assert.AreEqual(3, kept[0].classId, "the higher-scoring detection survives");
        }

        [Test]
        public void AcceptanceCapKeepsTheHighestScoring()
        {
            // Spec line 76 assigns this lever to slice 3 so slice 6 can tune it.
            var boxes = new float[]
            {
                0f, 0f, 10f, 10f,
                100f, 100f, 110f, 110f,
                200f, 200f, 210f, 210f,
            };

            var kept = Run(boxes, new[] { 0, 1, 2 }, new[] { 0.5f, 0.99f, 0.7f }, maxAccepted: 2);

            Assert.AreEqual(2, kept.Count, "cap must bound the accepted count");
            Assert.AreEqual(1, kept[0].classId, "0.99 first");
            Assert.AreEqual(2, kept[1].classId, "0.70 second — the 0.50 is dropped, not an arbitrary one");
        }

        [Test]
        public void EverythingBelowThresholdYieldsNothing()
        {
            var kept = Run(TwoDisjointBoxes(), new[] { 0, 1 }, new[] { 0.1f, 0.2f }, scoreThreshold: 0.23f);
            Assert.IsEmpty(kept);
        }

        [Test]
        public void OutputListIsClearedBeforeUse()
        {
            // The runtime reuses a single member list across frames. If it were not
            // cleared, detections would accumulate forever.
            var outList = new List<(int classId, Vector4 boundingBox, float score)> { (99, Vector4.zero, 1f) };
            DetectionDecoder.SelectDetections(
                TwoDisjointBoxes(), new[] { 0, 1 }, new[] { 0.9f, 0.8f }, 0.6f, 0.23f, int.MaxValue, null, outList);

            Assert.AreEqual(2, outList.Count, "stale entry from a previous frame must not survive");
            CollectionAssert.DoesNotContain(outList.ConvertAll(d => d.classId), 99);
        }

        [Test]
        public void CuratedClassSurvivesAndUncuratedIsDropped()
        {
            var kept = Run(TwoDisjointBoxes(), new[] { 5, 77 }, new[] { 0.9f, 0.8f },
                allowed: new HashSet<int> { 5 });

            Assert.AreEqual(1, kept.Count, "only the curated class should survive");
            Assert.AreEqual(5, kept[0].classId);
        }

        [Test]
        public void NullOrEmptyCurationSetMeansNoCuration()
        {
            // Preserves upstream behaviour when curation is switched off. An empty set
            // must NOT mean "reject everything", or disabling curation would silently
            // blank the app.
            Assert.AreEqual(2, Run(TwoDisjointBoxes(), new[] { 5, 77 }, new[] { 0.9f, 0.8f }, allowed: null).Count);
            Assert.AreEqual(2, Run(TwoDisjointBoxes(), new[] { 5, 77 }, new[] { 0.9f, 0.8f }, allowed: new HashSet<int>()).Count);
        }

        [Test]
        public void CurationIsAppliedBeforeTheAcceptanceCap()
        {
            // THE ORDERING THE IMPLEMENTATION DEPENDS ON. If uncurated classes were
            // filtered after the cap, they would consume cap slots and the cap would
            // silently under-deliver supported detections — which on device reads as a
            // detection failure, not a filtering one.
            //
            // Three candidates, highest two scores both UNCURATED, cap of 1. Correct
            // behaviour keeps the curated one. Broken ordering yields nothing.
            var boxes = new float[]
            {
                0f, 0f, 10f, 10f,
                100f, 100f, 110f, 110f,
                200f, 200f, 210f, 210f,
            };

            var kept = Run(boxes, new[] { 70, 71, 5 }, new[] { 0.99f, 0.95f, 0.30f },
                maxAccepted: 1, allowed: new HashSet<int> { 5 });

            Assert.AreEqual(1, kept.Count, "the curated detection must still be delivered");
            Assert.AreEqual(5, kept[0].classId,
                "uncurated classes must not consume the acceptance cap");
        }

        [Test]
        public void CalculateIoUReturnsZeroForDisjointBoxes()
        {
            var a = new Vector4(0f, 0f, 10f, 10f);
            var b = new Vector4(100f, 100f, 110f, 110f);
            Assert.AreEqual(0f, DetectionDecoder.CalculateIoU(a, b), 1e-6f);
        }

        [Test]
        public void CalculateIoUReturnsOneForIdenticalBoxes()
        {
            var a = new Vector4(0f, 0f, 10f, 10f);
            Assert.AreEqual(1f, DetectionDecoder.CalculateIoU(a, a), 1e-6f);
        }

        [Test]
        public void CalculateIoUHandlesTheZeroUnionBranch()
        {
            // Degenerate zero-area boxes. Upstream guards `unionArea == 0` and returns 0
            // rather than dividing. Without the guard this is a NaN that propagates
            // silently into suppression decisions.
            var degenerate = new Vector4(5f, 5f, 5f, 5f);
            var result = DetectionDecoder.CalculateIoU(degenerate, degenerate);

            Assert.AreEqual(0f, result, 1e-6f);
            Assert.IsFalse(float.IsNaN(result), "zero-area boxes must not produce NaN");
        }
    }
}
