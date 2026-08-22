using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteCropSquarePlacementTests
    {
        // A pinhole stand-in for PassthroughCameraAccess.ViewportPointToRay,
        // which returns a WORLD-space ray: viewport (0,0) bottom-left, (1,1)
        // top-right, direction rotated by the camera pose. tanHalfFov is
        // separable so anisotropic intrinsics can be exercised.
        private static RemoteCropSquarePlacement.ViewportRay Pinhole(
            Pose pose, float tanHalfFovX, float tanHalfFovY)
        {
            return viewportPoint => new Ray(
                pose.position,
                pose.rotation * new Vector3(
                    (viewportPoint.x - 0.5f) * 2f * tanHalfFovX,
                    (viewportPoint.y - 0.5f) * 2f * tanHalfFovY,
                    1f));
        }

        private static readonly Pose Identity = new Pose(Vector3.zero, Quaternion.identity);

        // SYNTHETIC. A square in normalized space, which the real crop is NOT --
        // see RealCrop below. Used only where the test is about the arithmetic
        // and a round number makes the expectation readable.
        private static readonly Rect SyntheticSquare = new Rect(0.275f, 0.275f, 0.45f, 0.45f);

        // The rect Task 1 actually produces, taken from RemoteImageCrop rather
        // than hardcoded so this fixture cannot drift from the crop it describes.
        // At 1280x960 it is (0.33125, 0.275, 0.3375, 0.45): 432x432 PIXELS, which
        // over a non-square frame is NOT a square in normalized space.
        private static Rect RealCrop(int frameWidth, int frameHeight)
        {
            RemoteImageCrop.TryCreatePlan(frameWidth, frameHeight, out var plan);
            return plan.NormalizedRect;
        }

        [Test]
        public void FullFrameExtentIsTheWholeFrustumPerMetre()
        {
            var measured = RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                new Rect(0f, 0f, 1f, 1f), Identity, Pinhole(Identity, 0.5f, 0.5f), out var extent);

            Assert.IsTrue(measured);
            // tanHalfFov 0.5 spans 1.0 either side of centre per metre.
            Assert.AreEqual(1f, extent.x, 1e-5f);
            Assert.AreEqual(1f, extent.y, 1e-5f);
        }

        [Test]
        public void FortyFivePercentRectIsFortyFivePercentOfTheFullExtent()
        {
            RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                SyntheticSquare, Identity, Pinhole(Identity, 0.5f, 0.5f), out var extent);

            Assert.AreEqual(0.45f, extent.x, 1e-5f);
        }

        [Test]
        public void ExtentIsIndependentOfTheCameraPose()
        {
            // THIS IS WHY MEASURING ONCE IS LEGITIMATE. ViewportPointToRay
            // depends only on Intrinsics -- "the static intrinsic parameters of
            // the sensor" -- and on CalcSensorCropRegion, which compares two
            // resolutions that do not change while the camera runs. If this test
            // ever fails, the per-session caching in DetectionManager is invalid
            // and the extent must move back to a per-frame measurement.
            var moved = new Pose(new Vector3(3f, -1f, 7f), Quaternion.Euler(12f, 200f, -34f));

            RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                SyntheticSquare, Identity, Pinhole(Identity, 0.5f, 0.5f), out var atOrigin);
            RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                SyntheticSquare, moved, Pinhole(moved, 0.5f, 0.5f), out var atMoved);

            Assert.AreEqual(atOrigin.x, atMoved.x, 1e-5f);
            Assert.AreEqual(atOrigin.y, atMoved.y, 1e-5f);
        }

        [Test]
        public void AnisotropicIntrinsicsProduceARectangleNotAForcedSquare()
        {
            // A pixel-square crop is only an angular square when fx == fy.
            // Forcing a square here would misstate the captured region.
            RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                SyntheticSquare, Identity, Pinhole(Identity, 0.5f, 0.25f), out var extent);

            Assert.AreEqual(2f, extent.x / extent.y, 1e-4f);
        }

        [Test]
        public void RealCropAtRealisticIntrinsicsComesBackAnAngularSquare()
        {
            // THE CASE THE OTHER TESTS MISS. The crop is square in PIXELS but
            // not in normalized space (0.3375 x 0.45 at 1280x960). It reads back
            // as an angular square only because the rect's anisotropy cancels
            // the frame's. Every other test here feeds a synthetic normalized
            // square, so none of them exercises that cancellation.
            //
            // tanHalfFov proportional to the pixel half-extents models the real
            // pinhole: ViewportPointToRay divides pixel offsets by fx and fy,
            // which are equal on this sensor. Drop either anisotropy, or swap an
            // axis, and the two stop cancelling and this fails.
            var pinhole = Pinhole(Identity, 640f / 800f, 480f / 800f);

            var measured = RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                RealCrop(1280, 960), Identity, pinhole, out var extent);

            Assert.IsTrue(measured);
            Assert.AreEqual(1f, extent.x / extent.y, 1e-4f);
        }

        [Test]
        public void MissingRayResolverMeasuresNothing()
        {
            Assert.IsFalse(RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                SyntheticSquare, Identity, null, out _));
        }

        [Test]
        public void FrameScaleIsTheExtentTimesTheAxialDistance()
        {
            var resolved = RemoteCropSquarePlacement.TryFrame(
                Identity, new Vector3(0f, 0f, 2f), new Vector2(0.45f, 0.45f), out var frame);

            Assert.IsTrue(resolved);
            // 0.45 per metre at 2 m axial = 0.9 world units.
            Assert.AreEqual(0.9f / RemoteCropSquarePlacement.CanvasEdgeUnits, frame.LocalScale.x, 1e-5f);
            Assert.AreEqual(new Vector3(0f, 0f, 2f), frame.Position);
        }

        [Test]
        public void FrameScaleIsLinearInDistance()
        {
            RemoteCropSquarePlacement.TryFrame(
                Identity, new Vector3(0f, 0f, 2f), new Vector2(0.45f, 0.45f), out var near);
            RemoteCropSquarePlacement.TryFrame(
                Identity, new Vector3(0f, 0f, 4f), new Vector2(0.45f, 0.45f), out var far);

            Assert.AreEqual(2f * near.LocalScale.x, far.LocalScale.x, 1e-5f);
        }

        [Test]
        public void FrameEdgeIsTheAxisAlignedSpanNotTheDiagonal()
        {
            // REGRESSION. The design spec first described the extent as "the
            // distance between the two intersections", which is the diagonal and
            // oversizes the square by sqrt(2). A square 1.414x too large would
            // still look plausible on device and would have been certified by the
            // inside/outside correspondence gate.
            RemoteCropSquarePlacement.TryFrame(
                Identity, new Vector3(0f, 0f, 2f), new Vector2(0.45f, 0.45f), out var frame);

            var edge = frame.LocalScale.x * RemoteCropSquarePlacement.CanvasEdgeUnits;
            Assert.AreEqual(0.9f, edge, 1e-5f);
            // Constraint form, not Assert.AreNotEqual: classic NUnit gives
            // AreNotEqual no tolerance overload, so a third float argument binds
            // to the message parameter and will not compile. Is.Not.EqualTo(x)
            // .Within(t) is the same assertion with the tolerance intact.
            Assert.That(edge, Is.Not.EqualTo(0.9f * Mathf.Sqrt(2f)).Within(1e-3f));
        }

        [Test]
        public void FrameUsesAxialDistanceFromTheCameraNotEuclideanFromTheEye()
        {
            // The dot scales on Vector3.Distance(viewerPosition, targetPoint),
            // measured from CenterEyeAnchor. The camera sits a few centimetres
            // away, so reusing the dot's distance would misscale the square by
            // roughly the lens offset over the range -- invisible on a dot,
            // meaningful on a square that claims to mark the captured region.
            var camera = new Pose(new Vector3(0.05f, 0f, 0f), Quaternion.identity);
            var target = new Vector3(0.05f, 0f, 2f);

            RemoteCropSquarePlacement.TryFrame(
                camera, target, new Vector2(0.45f, 0.45f), out var frame);

            var edge = frame.LocalScale.x * RemoteCropSquarePlacement.CanvasEdgeUnits;
            Assert.AreEqual(0.9f, edge, 1e-5f);
        }

        [Test]
        public void FrameRotationFollowsTheCameraNotTheEye()
        {
            var rotation = Quaternion.Euler(0f, 35f, 0f);
            var pose = new Pose(new Vector3(1f, 2f, 3f), rotation);

            RemoteCropSquarePlacement.TryFrame(
                pose,
                pose.position + rotation * new Vector3(0f, 0f, 2f),
                new Vector2(0.45f, 0.45f),
                out var frame);

            Assert.AreEqual(0f, Quaternion.Angle(rotation, frame.Rotation), 1e-3f);
        }

        [Test]
        public void PointBehindTheCameraFramesNothing()
        {
            Assert.IsFalse(RemoteCropSquarePlacement.TryFrame(
                Identity, new Vector3(0f, 0f, -2f), new Vector2(0.45f, 0.45f), out _));
        }
    }
}
