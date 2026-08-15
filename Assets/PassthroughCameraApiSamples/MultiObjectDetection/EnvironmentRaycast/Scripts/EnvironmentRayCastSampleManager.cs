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

        private bool m_loggedUnavailable;

        /// Object Tagger slice 4 Task 1: returns a discriminated result instead of
        /// Vector3?, because null previously meant BOTH "ray hit nothing" and
        /// "subsystem unavailable". See DepthResolveResult for why that mattered to
        /// the depth-miss rate slice 2 recorded.
        public DepthResolveResult ResolveDepth(Ray ray)
        {
            // Slice 4, D-slice4-1 fix. The first version of this branched on
            // EnvironmentRaycastManager.IsSupported and mapped every other false return
            // to Miss(). That was WRONG, and wrong in exactly the way this type exists
            // to prevent. Traced through the SDK:
            //
            //   IsSupported is a HARDWARE flag (_provider.IsSupported) with no
            //   permission input. Readiness is separate: CreateHandle is gated by
            //   Permission.HasUserAuthorizedPermission(ScenePermission), and
            //   Raycast() checks !IsReady FIRST, before IsSupported.
            //
            // So with Scene permission DENIED: IsSupported stays true, the old code
            // skipped its SubsystemUnavailable branch, Raycast returned false via
            // !IsReady, and that became Miss(). Every frame in that state would have
            // driven the depth-miss rate toward 100% — reading as "depth quality is
            // terrible" when the truth is "Scene permission was denied" — while
            // subsystemUnavailable sat at 0.
            //
            // The SDK already publishes the distinction through hit.status; the bug was
            // reading only the bool return and inferring the rest. Branch on the status.
            _ = m_raycastManager.Raycast(ray, out var hitInfo);

            switch (hitInfo.status)
            {
                case EnvironmentRaycastHitStatus.Hit:
                    return DepthResolveResult.Hit(hitInfo.point);

                // Subsystem-level: affects every detection equally, and no amount of
                // looking around fixes it. Must NOT enter the depth-miss rate.
                case EnvironmentRaycastHitStatus.NotReady:
                case EnvironmentRaycastHitStatus.NotSupported:
                    LogUnavailableOnce(hitInfo.status);
                    return DepthResolveResult.SubsystemUnavailable();

                // Genuine per-detection outcomes: the system was working and this
                // particular ray did not yield a usable point.
                //
                // HitPointOccluded populates hit.point with the first occluded point,
                // but the object's true surface is beyond it, so using that point would
                // place the label short. Treated as a miss deliberately.
                case EnvironmentRaycastHitStatus.NoHit:
                case EnvironmentRaycastHitStatus.RayOccluded:
                case EnvironmentRaycastHitStatus.HitPointOccluded:
                case EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum:
                default:
                    return DepthResolveResult.Miss();
            }
        }

        private void LogUnavailableOnce(EnvironmentRaycastHitStatus status)
        {
            // Rate-limited. Upstream logged per raycast; at ~1800 detections per gate
            // run that buries every other line in the log.
            if (m_loggedUnavailable)
            {
                return;
            }
            m_loggedUnavailable = true;

            var cause = status == EnvironmentRaycastHitStatus.NotSupported
                ? "the device/SDK reports environment raycast is NOT SUPPORTED."
                : HasScenePermission()
                    ? "the raycast system is NOT READY. Scene permission IS granted, so this is most likely the normal several-frame warm-up after the component is enabled — or the component is disabled."
                    : "the raycast system is NOT READY and Scene permission is NOT GRANTED. That is the likely cause.";

            Debug.LogError($"[ObjectTagger] depth unavailable ({status}): {cause} These frames are counted separately and are NOT part of the depth-miss rate.");
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
