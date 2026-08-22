using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public readonly struct RemoteImageCropPlan
    {
        public RectInt SourceRect { get; }
        public Vector2Int FrameSize { get; }
        public Vector2Int OutputSize { get; }
        public int JpegQuality { get; }

        public RemoteImageCropPlan(
            RectInt sourceRect, Vector2Int frameSize, Vector2Int outputSize, int jpegQuality)
        {
            SourceRect = sourceRect;
            FrameSize = frameSize;
            OutputSize = outputSize;
            JpegQuality = jpegQuality;
        }

        /// The crop in Unity viewport space: origin bottom-left, (0,0) to (1,1)
        /// across the camera image.
        ///
        /// THIS IS THE SHARED NUMBER. It is simultaneously the uvOffset/uvScale
        /// pair Graphics.Blit needs to encode the JPEG, and the space
        /// PassthroughCameraAccess.ViewportPointToRay consumes to place the
        /// aiming square. Neither the encoder nor the overlay owns it, which is
        /// what makes it impossible for the square to show a region the camera
        /// does not actually send.
        public Rect NormalizedRect => new Rect(
            (float)SourceRect.x / FrameSize.x,
            (float)SourceRect.y / FrameSize.y,
            (float)SourceRect.width / FrameSize.x,
            (float)SourceRect.height / FrameSize.y);
    }

    public static class RemoteImageCrop
    {
        // 0.45, tightened from 0.6 on 2026-08-22. Ticket 06 measured that 7 of
        // 11 failures named an object inside the crop but not the centred one,
        // so tightening attacks the dominant failure mode. It stopped at 0.45
        // rather than 0.4 because a second, opposite failure exists: large
        // objects misread from a fragment (crops 032, 062, 064). See D8 in the
        // design spec.
        private const float SourceEdgeFraction = 0.45f;
        private static readonly Vector2Int RequiredOutputSize = new Vector2Int(896, 896);
        private const int RequiredJpegQuality = 80;

        public static bool TryCreatePlan(int frameWidth, int frameHeight, out RemoteImageCropPlan plan)
        {
            plan = default;
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            // Round 0.5 upward. If the remaining margin is odd, the extra pixel is
            // kept on the right/top so the integer crop stays as centered as possible.
            var shorterEdge = Mathf.Min(frameWidth, frameHeight);
            var sourceEdge = Mathf.FloorToInt(shorterEdge * SourceEdgeFraction + 0.5f);
            var sourceRect = new RectInt(
                (frameWidth - sourceEdge) / 2,
                (frameHeight - sourceEdge) / 2,
                sourceEdge,
                sourceEdge);

            plan = new RemoteImageCropPlan(
                sourceRect,
                new Vector2Int(frameWidth, frameHeight),
                RequiredOutputSize,
                RequiredJpegQuality);
            return true;
        }
    }
}
