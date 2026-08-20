using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public readonly struct RemoteImageCropPlan
    {
        public RectInt SourceRect { get; }
        public Vector2Int OutputSize { get; }
        public int JpegQuality { get; }

        public RemoteImageCropPlan(RectInt sourceRect, Vector2Int outputSize, int jpegQuality)
        {
            SourceRect = sourceRect;
            OutputSize = outputSize;
            JpegQuality = jpegQuality;
        }
    }

    public static class RemoteImageCrop
    {
        private const float SourceEdgeFraction = 0.6f;
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

            plan = new RemoteImageCropPlan(sourceRect, RequiredOutputSize, RequiredJpegQuality);
            return true;
        }
    }
}
