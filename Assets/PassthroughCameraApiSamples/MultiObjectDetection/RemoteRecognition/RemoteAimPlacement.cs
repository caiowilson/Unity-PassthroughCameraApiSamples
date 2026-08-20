using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public readonly struct RemoteAimFrame
    {
        internal RemoteAimFrame(
            bool hasResolvedTarget,
            Vector3 resolvedPoint,
            Vector3 reticleWorldPosition,
            float uniformScale)
        {
            HasResolvedTarget = hasResolvedTarget;
            ResolvedPoint = resolvedPoint;
            ReticleWorldPosition = reticleWorldPosition;
            UniformScale = uniformScale;
        }

        public bool HasResolvedTarget { get; }
        public Vector3 ResolvedPoint { get; }
        public Vector3 ReticleWorldPosition { get; }
        public float UniformScale { get; }
    }

    public static class RemoteAimPlacement
    {
        public const float ReferenceDistance = 2f;
        public const float AuthoredScaleAtReferenceDistance = 0.001f;

        public static RemoteAimFrame Resolved(Vector3 viewerPosition, Vector3 targetPoint)
        {
            var distance = Vector3.Distance(viewerPosition, targetPoint);
            var scale = AuthoredScaleAtReferenceDistance * distance / ReferenceDistance;
            return new RemoteAimFrame(true, targetPoint, targetPoint, scale);
        }

        public static RemoteAimFrame Fallback(Vector3 viewerPosition, Quaternion viewerRotation)
        {
            var fallbackPoint = viewerPosition + viewerRotation * Vector3.forward * ReferenceDistance;
            return new RemoteAimFrame(
                false,
                Vector3.zero,
                fallbackPoint,
                AuthoredScaleAtReferenceDistance);
        }
    }
}
