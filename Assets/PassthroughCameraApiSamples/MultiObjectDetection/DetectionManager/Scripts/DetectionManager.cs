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

        private bool m_isStarted;
        private bool m_wasPausedLastFrame = true;
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
            EraseSpatialAnchor();
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
        }

        private void OnTrackingLost() => m_isHeadsetTracking = false;
        private void OnTrackingAcquired() => m_isHeadsetTracking = true;

        // Object Tagger manual-tagging Task 4 — A commits the live
        // candidate; B is overloaded by press duration: a quick press
        // untags the nearest committed label, a hold past
        // HoldToClearAllThresholdSeconds clears every label.
        private void Update()
        {
            if (!m_isStarted)
            {
                // Manage the Initial Ui Menu
                if (m_cameraAccess.IsPlaying)
                {
                    m_isStarted = true;
                }
            }
            else if (m_uiMenuManager != null &&
                     RemoteNamingInputPolicy.CanStart(
                         m_cameraAccess.IsPlaying,
                         m_uiMenuManager.IsPaused,
                         m_wasPausedLastFrame) &&
                     InputManager.IsButtonADownOrPinchStarted())
            {
                m_remoteNaming?.TryStart(m_cameraAccess.GetTexture());
            }

            m_wasPausedLastFrame = m_uiMenuManager == null || m_uiMenuManager.IsPaused;
            UpdateBButtonHoldState();
        }

        // Object Tagger manual-tagging Task 4 — B-button/pinch hold-duration
        // state machine.
        //
        // Clear-all fires the instant the hold crosses the threshold (not
        // deferred to release), giving immediate feedback for a hold gesture
        // rather than making the user release first to see anything happen.
        // m_bClearAllFired guards against ALSO firing a targeted untag on
        // release once clear-all has already fired for this press.
        private const float HoldToClearAllThresholdSeconds = 1f;
        private bool m_bIsHeld;
        private float m_bPressStartTime;
        private bool m_bClearAllFired;

        private void UpdateBButtonHoldState()
        {
            var isHeldNow = InputManager.IsButtonBHeldOrMiddleFingerPinchHeld();

            if (isHeldNow && !m_bIsHeld)
            {
                // Press started this frame.
                m_bIsHeld = true;
                m_bPressStartTime = Time.time;
                m_bClearAllFired = false;
            }
            else if (isHeldNow && m_bIsHeld)
            {
                if (!m_bClearAllFired && InputHoldClassification.HasReachedHoldThreshold(m_bPressStartTime, Time.time, HoldToClearAllThresholdSeconds))
                {
                    m_uiInference.ClearAnnotations();
                    m_bClearAllFired = true;
                }
            }
            else if (!isHeldNow && m_bIsHeld)
            {
                // Released.
                if (!m_bClearAllFired)
                {
                    m_uiInference.TryUntagNearestToCenter();
                }
                m_bIsHeld = false;
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
