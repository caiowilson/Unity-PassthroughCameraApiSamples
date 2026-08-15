// Object Tagger slice 4 Task 2 — tests for the projection arithmetic.
//
// The Y-flip tests are the point of this file. A missing `1.0f -` mirrors every
// label about the horizontal axis, and on device that reads as "placement is
// broken" rather than "a subtraction is missing" — expensive to diagnose, trivial
// to pin.

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class DetectionProjectionTests
    {
        private static readonly Vector2 Input = new Vector2(640f, 640f);

        [Test]
        public void BoxToRectConvertsCornersToPositionAndSize()
        {
            // Model emits (topLeftX, topLeftY, bottomRightX, bottomRightY), NOT x/y/w/h.
            var rect = DetectionProjection.BoxToRect(new Vector4(10f, 20f, 110f, 220f));

            Assert.AreEqual(10f, rect.x, 1e-4f);
            Assert.AreEqual(20f, rect.y, 1e-4f);
            Assert.AreEqual(100f, rect.width, 1e-4f, "width is x2 - x1");
            Assert.AreEqual(200f, rect.height, 1e-4f, "height is y2 - y1");
        }

        [Test]
        public void NormalizedCenterOfAFullFrameBoxIsTheMiddle()
        {
            var rect = DetectionProjection.BoxToRect(new Vector4(0f, 0f, 640f, 640f));
            var c = DetectionProjection.NormalizedCenter(rect, Input);

            Assert.AreEqual(0.5f, c.x, 1e-5f);
            Assert.AreEqual(0.5f, c.y, 1e-5f);
        }

        [Test]
        public void ViewportPointFlipsY()
        {
            // THE LOAD-BEARING ONE. Model space counts down from the top; Unity's
            // viewport counts up from the bottom.
            var topOfImage = new Vector2(0.5f, 0.0f);   // top in model space
            var vp = DetectionProjection.ToViewportPoint(topOfImage);

            Assert.AreEqual(0.5f, vp.x, 1e-5f, "x must NOT be flipped");
            Assert.AreEqual(1.0f, vp.y, 1e-5f, "top of the image is viewport y=1");
        }

        [Test]
        public void ViewportPointLeavesTheCentreFixed()
        {
            // A centred detection is the one case where a missing flip looks correct.
            // Recorded so nobody validates the flip using the centre alone.
            var vp = DetectionProjection.ToViewportPoint(new Vector2(0.5f, 0.5f));

            Assert.AreEqual(0.5f, vp.x, 1e-5f);
            Assert.AreEqual(0.5f, vp.y, 1e-5f);
        }

        [Test]
        public void NormalizedRectAlsoFlipsY()
        {
            // The second flip site. A box at the TOP of model space must land at the
            // TOP in normalised viewport space, i.e. high y.
            var rect = DetectionProjection.BoxToRect(new Vector4(0f, 0f, 64f, 64f));
            var nr = DetectionProjection.NormalizedRect(rect, Input);

            Assert.AreEqual(0f, nr.x, 1e-5f);
            Assert.AreEqual(0.9f, nr.y, 1e-5f, "1 - (yMax/inputY) = 1 - 0.1");
            Assert.AreEqual(0.1f, nr.width, 1e-5f);
            Assert.AreEqual(0.1f, nr.height, 1e-5f);
        }

        [Test]
        public void CameraPixelOffsetIsZeroAtTheCentre()
        {
            var off = DetectionProjection.ToCameraPixelOffset(new Vector2(0.5f, 0.5f), new Vector2(1280f, 960f));

            Assert.AreEqual(0f, off.x, 1e-4f);
            Assert.AreEqual(0f, off.y, 1e-4f);
        }

        [Test]
        public void SampleOffsetsPutTheCentreFirst()
        {
            // A caller wanting upstream's exact single-centre-ray behaviour takes the
            // first sample. If the ordering changed, that caller would silently start
            // probing an offset point instead.
            var offsets = DetectionProjection.SampleOffsets(0.05f);

            Assert.AreEqual(Vector2.zero, offsets[0], "centre must be sampled first");
            Assert.AreEqual(5, offsets.Length);
        }

        [Test]
        public void RepresentativePointIgnoresAFarOutlier()
        {
            // THE ACTUAL SLICE 4 PROBLEM. Four rays hit the object at ~2m; one slips
            // past it and hits a wall at 8m. A MEAN would drag the label roughly a
            // metre backwards. The median ignores the outlier entirely.
            var cam = Vector3.zero;
            var points = new[]
            {
                new Vector3(0f, 0f, 2.00f),
                new Vector3(0f, 0f, 2.02f),
                new Vector3(0f, 0f, 1.98f),
                new Vector3(0f, 0f, 2.01f),
                new Vector3(0f, 0f, 8.00f),   // through the gap, onto the wall
            };

            Assert.IsTrue(DetectionProjection.TryPickRepresentativePoint(points, points.Length, cam, out var p));
            Assert.Less(p.z, 2.1f, "the far wall hit must not win");
            Assert.Greater(p.z, 1.9f, "result should sit among the object hits");
        }

        [Test]
        public void RepresentativePointOfASingleSampleIsThatSample()
        {
            // Preserves upstream behaviour exactly when only the centre ray resolves.
            var only = new[] { new Vector3(1f, 2f, 3f) };

            Assert.IsTrue(DetectionProjection.TryPickRepresentativePoint(only, 1, Vector3.zero, out var p));
            Assert.AreEqual(only[0], p);
        }

        [Test]
        public void RepresentativePointFailsWhenNothingResolved()
        {
            // Must NOT invent a position. The caller keeps the existing policy of
            // dropping the detection.
            Assert.IsFalse(DetectionProjection.TryPickRepresentativePoint(
                new Vector3[4], 0, Vector3.zero, out _));
            Assert.IsFalse(DetectionProjection.TryPickRepresentativePoint(
                null, 3, Vector3.zero, out _));
        }
    }
}
