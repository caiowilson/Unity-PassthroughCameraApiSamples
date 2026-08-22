using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public readonly struct RemoteCropSquareFrame
    {
        internal RemoteCropSquareFrame(Vector3 position, Quaternion rotation, Vector3 localScale)
        {
            Position = position;
            Rotation = rotation;
            LocalScale = localScale;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 LocalScale { get; }
    }

    /// Places the crop region's frustum cross-section in world space.
    ///
    /// Two operations with very different lifetimes. TryMeasureExtentPerMetre
    /// reads the camera intrinsics and runs ONCE per capture resolution.
    /// TryFrame runs every frame and is one multiply -- the same linear-in-
    /// distance law the aiming dot already uses in RemoteAimPlacement, because a
    /// frustum cross-section subtends a fixed angle.
    ///
    /// The crop is defined in CAMERA space; the operator sees through their
    /// EYES, and the lens offset between them is a real physical translation. A
    /// square is therefore exactly truthful only at the depth it is drawn at.
    /// Drawing it at the resolved depth makes it exact for the object about to be
    /// captured, which is the only place exactness matters. Do not "fix" this by
    /// billboarding toward the eye, which looks tidier and is wrong.
    public static class RemoteCropSquarePlacement
    {
        /// Edge of the prefab root's RectTransform in canvas units. World edge
        /// length = CanvasEdgeUnits * LocalScale. MUST equal the root
        /// m_SizeDelta authored by CropSquareAuthoring.
        public const float CanvasEdgeUnits = 100f;

        /// Authored fallback distance for the scene instance, matching
        /// RemoteAimPlacement.ReferenceDistance. Only affects the object before
        /// the first depth resolve; every later frame overwrites the transform.
        /// Lives here so CropSquareAuthoring does not carry a second magic 2f.
        public const float ReferenceDistance = 2f;

        /// Supplied by the caller so the platform call stays out of here and the
        /// arithmetic stays testable. In the app this wraps
        /// PassthroughCameraAccess.ViewportPointToRay, which returns a
        /// WORLD-space ray.
        public delegate Ray ViewportRay(Vector2 viewportPoint);

        /// The crop's angular extent, in world units per metre of axial distance.
        ///
        /// SESSION-CONSTANT, which is what licenses caching it.
        /// ViewportPointToRay depends only on Intrinsics -- documented as "the
        /// static intrinsic parameters of the sensor" -- and on
        /// CalcSensorCropRegion, which compares sensor resolution against current
        /// resolution, neither of which changes while the camera runs. The pose
        /// argument is used only to rotate back out of world space; the result is
        /// pose-independent, pinned by ExtentIsIndependentOfTheCameraPose.
        ///
        /// Recompute if CurrentResolution changes -- the camera restarts on
        /// application pause and may come back at a different resolution.
        public static bool TryMeasureExtentPerMetre(
            Rect normalizedCropRect,
            Pose cameraPose,
            ViewportRay viewportRay,
            out Vector2 extentPerMetre)
        {
            extentPerMetre = default;
            if (viewportRay == null)
            {
                return false;
            }

            if (!TryCameraSpaceAtUnitDepth(
                    viewportRay(normalizedCropRect.min), cameraPose.rotation, out var min) ||
                !TryCameraSpaceAtUnitDepth(
                    viewportRay(normalizedCropRect.max), cameraPose.rotation, out var max))
            {
                return false;
            }

            // The AXIS-ALIGNED span, componentwise. NOT (max - min).magnitude,
            // which is the diagonal and oversizes the square by sqrt(2). Pinned
            // by FrameEdgeIsTheAxisAlignedSpanNotTheDiagonal.
            extentPerMetre = new Vector2(Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y));
            return extentPerMetre.x > 0f && extentPerMetre.y > 0f;
        }

        public static bool TryFrame(
            Pose cameraPose,
            Vector3 resolvedPoint,
            Vector2 extentPerMetre,
            out RemoteCropSquareFrame frame)
        {
            frame = default;

            // Axial distance along the CAMERA's forward from the CAMERA's
            // position. Deliberately NOT the dot's
            // Vector3.Distance(viewerPosition, targetPoint), which is measured
            // from CenterEyeAnchor: the eye-to-camera lens offset is a real few
            // centimetres, and reusing the dot's distance would misscale the
            // square by roughly that over the working range. Invisible on a dot,
            // meaningful on a square that claims to mark the captured region.
            var forward = cameraPose.rotation * Vector3.forward;
            var axialDistance = Vector3.Dot(resolvedPoint - cameraPose.position, forward);
            if (axialDistance <= 0f)
            {
                return false;
            }

            frame = new RemoteCropSquareFrame(
                resolvedPoint,
                cameraPose.rotation,
                new Vector3(
                    extentPerMetre.x * axialDistance / CanvasEdgeUnits,
                    extentPerMetre.y * axialDistance / CanvasEdgeUnits,
                    1f));
            return true;
        }

        /// Unity's Ray constructor normalizes direction, and ViewportPointToRay
        /// returns world space. Rotate back into camera space and divide by depth
        /// to recover the direction at one metre of axial distance. That IS the
        /// plane intersection at 1 m, which is exactly why no per-frame
        /// intersection is needed.
        private static bool TryCameraSpaceAtUnitDepth(
            Ray ray, Quaternion cameraRotation, out Vector3 direction)
        {
            direction = default;
            var local = Quaternion.Inverse(cameraRotation) * ray.direction;
            if (local.z <= 1e-6f)
            {
                return false;
            }

            direction = local / local.z;
            return true;
        }
    }
}
