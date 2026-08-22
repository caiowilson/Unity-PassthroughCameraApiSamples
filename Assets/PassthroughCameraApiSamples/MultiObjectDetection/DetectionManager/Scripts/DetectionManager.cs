// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private RemoteNamingController m_remoteNaming;
        [SerializeField] private GameObject m_aimReticle;
        [SerializeField] private GameObject m_cropSquare;

        private bool m_isStarted;
        private bool m_wasPausedLastFrame = true;
        private RemoteAimFrame m_currentAimFrame;
        // The square's frame is held here rather than folded into RemoteAimFrame:
        // this class already resolves the centre point once per frame, so both
        // frames come from that one resolve and the dot's types stay untouched.
        //
        // The extent is cached against the resolution that produced it. It is a
        // session constant (see RemoteCropSquarePlacement) but the camera
        // restarts on application pause and may return at a different
        // resolution, so the key is the resolution, not a bool.
        private Vector2Int m_cropExtentResolution;
        private Vector2 m_cropExtentPerMetre;
        private bool m_hasCropExtent;
        private bool m_loggedCropGeometry;
        internal OVRSpatialAnchor m_spatialAnchor;
        private bool m_isHeadsetTracking;

        private void Awake()
        {
            StartCoroutine(UpdateSpatialAnchor());
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
        }

        private void OnDestroy()
        {
            if (m_aimReticle != null)
            {
                m_aimReticle.SetActive(false);
            }

            EraseSpatialAnchor();
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
        }

        private void OnTrackingLost()
        {
            m_isHeadsetTracking = false;
            m_remoteNaming?.TryCancelPending();
        }
        private void OnTrackingAcquired() => m_isHeadsetTracking = true;

        // Object Tagger manual-tagging Task 4 — A commits the live
        // candidate; B is overloaded by press duration: a quick press
        // untags the nearest committed label, a hold past
        // HoldToClearAllThresholdSeconds clears every label.
        private void Update()
        {
            var wasStartedAtFrameStart = m_isStarted;
            if (!m_isStarted)
            {
                // Manage the Initial Ui Menu
                if (m_cameraAccess.IsPlaying)
                {
                    m_isStarted = true;
                }
            }

            // Resolve once before input so the visible dot and a request started
            // this frame share one physical-camera/depth sample.
            UpdateAimReticle();

            var anchorTracked = m_spatialAnchor != null && m_spatialAnchor.IsTracked;
            HandleRemoteAvailability(
                anchorTracked,
                m_uiMenuManager == null || m_uiMenuManager.IsPaused);

            if (wasStartedAtFrameStart &&
                m_uiMenuManager != null &&
                RemoteNamingInputPolicy.CanStartResolvedAim(
                    m_cameraAccess.IsPlaying,
                    m_uiMenuManager.IsPaused,
                    m_wasPausedLastFrame,
                    anchorTracked,
                    m_currentAimFrame.HasResolvedTarget) &&
                InputManager.IsButtonADownOrPinchStarted())
            {
                m_remoteNaming?.TryStartAtResolvedPoint(
                    m_cameraAccess.GetTexture(),
                    m_currentAimFrame.ResolvedPoint);
            }

            m_wasPausedLastFrame = m_uiMenuManager == null || m_uiMenuManager.IsPaused;
            UpdateBButtonHoldState();
        }

        private void UpdateAimReticle()
        {
            var shouldShow = RemoteNamingInputPolicy.ShouldShowAimReticle(
                m_isStarted,
                m_cameraAccess != null && m_cameraAccess.IsPlaying,
                m_uiMenuManager == null || m_uiMenuManager.IsPaused,
                m_wasPausedLastFrame,
                m_remoteNaming != null && m_remoteNaming.CanAttemptNaming);

            var viewer = m_aimReticle != null ? m_aimReticle.transform.parent : null;
            var viewerPosition = viewer != null ? viewer.position : transform.position;
            var viewerRotation = viewer != null ? viewer.rotation : transform.rotation;

            Vector3 targetPoint = default;
            Pose cameraPose = default;
            var resolved = shouldShow &&
                m_uiInference != null &&
                m_uiInference.TryResolveCenterPoint(out targetPoint, out cameraPose);

            m_currentAimFrame = resolved
                ? RemoteAimPlacement.Resolved(viewerPosition, targetPoint)
                : RemoteAimPlacement.Fallback(viewerPosition, viewerRotation);

            if (m_aimReticle != null)
            {
                m_aimReticle.transform.position = m_currentAimFrame.ReticleWorldPosition;
                m_aimReticle.transform.localScale = Vector3.one * m_currentAimFrame.UniformScale;
                if (m_aimReticle.activeSelf != shouldShow)
                {
                    m_aimReticle.SetActive(shouldShow);
                }
            }

            UpdateCropSquare(resolved, cameraPose, targetPoint);
        }

        // The square is shown only when depth actually resolved. That is not a
        // limitation but an affordance: CanStartResolvedAim already requires
        // HasResolvedTarget, so A/pinch is a no-op without it. "Brackets visible"
        // therefore means "the trigger will fire". The dot's own gating is
        // deliberately left alone; it still shows in the unresolved state, a
        // pre-existing inconsistency recorded as D3 in the design spec.
        private void UpdateCropSquare(bool resolved, Pose cameraPose, Vector3 resolvedPoint)
        {
            if (m_cropSquare == null)
            {
                return;
            }

            var shown = false;
            if (resolved &&
                m_cameraAccess != null &&
                TryGetCropExtent(cameraPose, out var extentPerMetre) &&
                RemoteCropSquarePlacement.TryFrame(
                    cameraPose, resolvedPoint, extentPerMetre, out var square))
            {
                m_cropSquare.transform.SetPositionAndRotation(square.Position, square.Rotation);
                m_cropSquare.transform.localScale = square.LocalScale;
                shown = true;
            }

            if (m_cropSquare.activeSelf != shown)
            {
                m_cropSquare.SetActive(shown);
            }
        }

        // Reads the intrinsics once per capture resolution. Doing this every
        // frame would re-derive a number that cannot change, and would need the
        // ray delegate live on the hot path.
        private bool TryGetCropExtent(Pose cameraPose, out Vector2 extentPerMetre)
        {
            var resolution = m_cameraAccess.CurrentResolution;
            if (m_hasCropExtent && m_cropExtentResolution == resolution)
            {
                extentPerMetre = m_cropExtentPerMetre;
                return true;
            }

            extentPerMetre = default;
            if (!RemoteImageCrop.TryCreatePlan(resolution.x, resolution.y, out var plan))
            {
                return false;
            }

            LogCropGeometryOnce(resolution, plan);
            if (!RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                    plan.NormalizedRect,
                    cameraPose,
                    viewportPoint => m_cameraAccess.ViewportPointToRay(viewportPoint, cameraPose),
                    out var measured))
            {
                return false;
            }

            m_cropExtentPerMetre = measured;
            m_cropExtentResolution = resolution;
            m_hasCropExtent = true;
            extentPerMetre = measured;
            return true;
        }

        // The granted capture resolution has never been observed on this project
        // -- every crop number to date, ticket 06's included, was read off
        // PassthroughCameraAccessPrefab. The scene also carries stale
        // requestedResolution/eye overrides from the superseded
        // WebCamTextureManager API that make it read as 800x600 (D6). One log
        // line settles both with evidence.
        private void LogCropGeometryOnce(Vector2Int resolution, RemoteImageCropPlan plan)
        {
            if (m_loggedCropGeometry)
            {
                return;
            }

            m_loggedCropGeometry = true;
            Debug.Log(
                $"[ObjectTagger] camera resolution {resolution.x}x{resolution.y}, " +
                $"crop {plan.SourceRect}, normalized {plan.NormalizedRect}, " +
                $"output {plan.OutputSize.x}x{plan.OutputSize.y} q{plan.JpegQuality}");
        }

        // Object Tagger manual-tagging Task 4 — B-button/pinch hold-duration
        // state machine.
        //
        // Clear-all fires the instant the hold crosses the threshold (not
        // deferred to release), giving immediate feedback for a hold gesture
        // rather than making the user release first to see anything happen.
        // m_bClearAllFired guards against ALSO firing a targeted untag on
        // release once clear-all has already fired for this press.
        // m_bCanceledPendingOnPress similarly prevents quick release from
        // reinterpreting an already-consumed cancellation as a targeted untag.
        private const float HoldToClearAllThresholdSeconds = 1f;
        private bool m_bIsHeld;
        private float m_bPressStartTime;
        private bool m_bClearAllFired;
        private bool m_bCanceledPendingOnPress;

        private void UpdateBButtonHoldState()
        {
            var isHeldNow = InputManager.IsButtonBHeldOrMiddleFingerPinchHeld();

            if (isHeldNow && !m_bIsHeld)
            {
                HandleBPressStarted();
            }
            else if (isHeldNow && m_bIsHeld)
            {
                if (!m_bClearAllFired && InputHoldClassification.HasReachedHoldThreshold(m_bPressStartTime, Time.time, HoldToClearAllThresholdSeconds))
                {
                    HandleBHoldThresholdReached();
                }
            }
            else if (!isHeldNow && m_bIsHeld)
            {
                HandleBRelease();
            }
        }

        private void HandleBPressStarted()
        {
            m_bIsHeld = true;
            m_bPressStartTime = Time.time;
            m_bClearAllFired = false;
            m_bCanceledPendingOnPress =
                m_remoteNaming != null && m_remoteNaming.TryCancelPending();
        }

        private void HandleBHoldThresholdReached()
        {
            HandleHeldBClear();
            m_bClearAllFired = true;
        }

        private void HandleBRelease()
        {
            if (!m_bClearAllFired && !m_bCanceledPendingOnPress)
            {
                HandleQuickBRelease();
            }

            m_bIsHeld = false;
            m_bCanceledPendingOnPress = false;
        }

        private void HandleQuickBRelease()
        {
            var canceledPending = m_remoteNaming != null && m_remoteNaming.TryCancelPending();
            if (!canceledPending)
            {
                m_uiInference?.TryUntagNearestToCenter();
            }
        }

        private void HandleHeldBClear()
        {
            m_remoteNaming?.TryCancelPending();
            m_uiInference?.ClearAnnotations();
        }

        private void HandleRemoteAvailability(bool anchorTracked, bool appPaused)
        {
            if (!anchorTracked || appPaused)
            {
                m_remoteNaming?.TryCancelPending();
            }
        }

        private IEnumerator UpdateSpatialAnchor()
        {
            while (true)
            {
                yield return null;
                if (m_spatialAnchor == null)
                {
                    yield return CreateSpatialAnchorAndSave();
                    if (m_spatialAnchor == null)
                    {
                        continue;
                    }
                }

                if (!m_spatialAnchor.IsTracked)
                {
                    yield return RestoreSpatialAnchorTracking();
                }
            }

            IEnumerator CreateSpatialAnchorAndSave()
            {
                m_spatialAnchor = m_uiInference.ContentParent.gameObject.AddComponent<OVRSpatialAnchor>();

                // Wait for localization because SaveAnchorAsync() requires the anchor to be localized first.
                while (true)
                {
                    if (m_spatialAnchor == null)
                    {
                        // Spatial Anchor destroys itself when creation fails.
                        yield break;
                    }
                    if (m_spatialAnchor.Localized)
                    {
                        break;
                    }
                    yield return null;
                }

                // Save the anchor.
                var awaiter = m_spatialAnchor.SaveAnchorAsync().GetAwaiter();
                while (!awaiter.IsCompleted)
                {
                    yield return null;
                }
                var saveAnchorResult = awaiter.GetResult();
                if (!saveAnchorResult.Success)
                {
                    LogSpatialAnchor($"SaveAnchorAsync() failed {saveAnchorResult}", LogType.Error);
                    EraseSpatialAnchor();
                    yield break;
                }
                LogSpatialAnchor("created");
            }

            IEnumerator RestoreSpatialAnchorTracking()
            {
                // Try to restore spatial anchor tracking. If restoration fails, erase it.
                LogSpatialAnchor("tracking was lost, restoring...");
                const int numRetries = 20;
                for (int i = 0; i < numRetries; i++)
                {
                    yield return new WaitForSeconds(1f);
                    if (!m_isHeadsetTracking)
                    {
                        LogSpatialAnchor($"{nameof(m_isHeadsetTracking)} is false, retrying ({i})");
                        continue;
                    }

                    var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>(1);
                    var awaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[]
                    {
                        m_spatialAnchor.Uuid
                    }, unboundAnchors).GetAwaiter();
                    while (!awaiter.IsCompleted)
                    {
                        yield return null;
                    }
                    var loadResult = awaiter.GetResult();
                    if (!loadResult.Success)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() failed {loadResult.Status}, retrying ({i})", LogType.Error);
                        continue;
                    }
                    if (unboundAnchors.Count != 0)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() unexpected count:{unboundAnchors.Count}, retrying ({i})", LogType.Error);
                        continue;
                    }
                    yield return null;
                    if (!m_spatialAnchor.IsTracked)
                    {
                        LogSpatialAnchor($"tracking is not restored, retrying ({i})");
                        continue;
                    }

                    LogSpatialAnchor("tracking was restored successfully");
                    yield break;
                }

                LogSpatialAnchor($"tracking restoration failed after {numRetries} retries", LogType.Warning);
                EraseSpatialAnchor();
            }
        }

        private void EraseSpatialAnchor()
        {
            if (m_spatialAnchor != null)
            {
                LogSpatialAnchor("EraseSpatialAnchor");
                m_spatialAnchor.EraseAnchorAsync();
                DestroyImmediate(m_spatialAnchor);
                m_spatialAnchor = null;

                CleanMarkers();
                m_remoteNaming?.TryCancelPending();
                m_uiInference.ClearAnnotations();
            }
        }

        // Object Tagger slice 5 Task 1: the marker-destroy loop, m_spawnedEntities
        // clear, and OnObjectsIdentified invocation are deleted along with the rest of
        // the "spawn 3D marker" feature (SpawnCurrentDetectedObjects and
        // HasExistingMarkerInBoundingBox, both removed). Final-review fix (Important
        // #5): removed on the design spec's non-adoption clause
        // (docs/superpowers/specs/2026-08-14-object-tagger-alpha-design.md, line 57:
        // "the sample's marker interaction which is not adopted"), not because the
        // feature was dead code — the project's validation record
        // (docs/validation/2026-08-15-slice-5-labels.md, D-slice5-1) found it was
        // very likely live in the running app before this deletion. CleanMarkers()
        // itself is kept: EraseSpatialAnchor() (spatial-anchor lifecycle, out of
        // scope for this task) still calls it.
        private void CleanMarkers()
        {
            LogSpatialAnchor("CleanMarkers");
        }

        private static void LogSpatialAnchor(string message, LogType logType = LogType.Log)
        {
            Debug.unityLogger.Log(logType, $"{nameof(OVRSpatialAnchor)}: {message}");
        }
    }
}
