// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class EnvironmentRayCastSampleManager : MonoBehaviour
    {
        private const string SPATIALPERMISSION = "com.oculus.permission.USE_SCENE";
        [SerializeField] private EnvironmentRaycastManager m_raycastManager;

        private void Start()
        {
            if (!EnvironmentRaycastManager.IsSupported)
            {
                Debug.LogError("EnvironmentRaycastManager is not supported: please read the official documentation to get more details. (https://developers.meta.com/horizon/documentation/unity/unity-depthapi-overview/)");
            }
        }

        /// Object Tagger slice 4 Task 1 Step 2.
        ///
        /// This was DEAD CODE upstream — nothing called it. Scene permission is owned by
        /// RequestPermissionsOnce, which slice 2 re-homed. Kept with a stated meaning
        /// rather than deleted, because it is the only way to distinguish "the subsystem
        /// reports unsupported because permission was denied" from "unsupported on this
        /// hardware", and those warrant different messages.
        ///
        /// It is NOT a third notion of availability. Callers should branch on
        /// DepthResolveStatus; this exists only to EXPLAIN a SubsystemUnavailable.
        public bool HasScenePermission()
        {
#if UNITY_ANDROID
            return Permission.HasUserAuthorizedPermission(SPATIALPERMISSION);
#else
            return true;
#endif
        }

        private bool m_loggedUnsupported;

        /// Object Tagger slice 4 Task 1: returns a discriminated result instead of
        /// Vector3?, because null previously meant BOTH "ray hit nothing" and
        /// "subsystem unavailable". See DepthResolveResult for why that mattered to
        /// the depth-miss rate slice 2 recorded.
        public DepthResolveResult ResolveDepth(Ray ray)
        {
            if (!EnvironmentRaycastManager.IsSupported)
            {
                // Rate-limited. Upstream logged this per raycast; at ~1800 detections
                // per gate run that buries every other line in the log.
                if (!m_loggedUnsupported)
                {
                    m_loggedUnsupported = true;
                    var permissionNote = HasScenePermission()
                        ? "Scene permission IS granted, so this is a hardware/support limitation."
                        : "Scene permission is NOT granted — that is the likely cause.";
                    Debug.LogError($"[ObjectTagger] EnvironmentRaycastManager is not supported. {permissionNote}");
                }
                return DepthResolveResult.SubsystemUnavailable();
            }

            return m_raycastManager.Raycast(ray, out var hitInfo)
                ? DepthResolveResult.Hit(hitInfo.point)
                : DepthResolveResult.Miss();
        }

        /// Compatibility shim for callers still expecting the old shape. It collapses
        /// the distinction again, so prefer ResolveDepth.
        public Vector3? Raycast(Ray ray)
        {
            var result = ResolveDepth(ray);
            return result.IsHit ? result.Point : (Vector3?)null;
        }
    }
}
