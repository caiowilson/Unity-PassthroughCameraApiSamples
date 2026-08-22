// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.IO;
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
        private bool m_loggedCropExtent;
        private bool m_loggedCropGeometry;
        // Spatial-anchor Restoration Task 3: the shared anchor is no longer a
        // field here. Its whole lifecycle -- restore the saved one, or create
        // and save a first-run one, and erase it ONLY on a confirmed reset --
        // belongs to the coordinator, and the live OVRSpatialAnchor component
        // belongs to the operations adapter behind it. Nothing outside that
        // adapter can reach EraseAnchorAsync any more, which is the point:
        // OnDestroy used to erase the saved anchor on every app close.
        private SpatialAnchorRestorationCoordinator m_anchorRestoration;

        // Spatial-anchor Restoration Task 4: the once-per-session restore
        // guard. Ready is re-entered every time a tracking blip recovers (see
        // the coordinator's Unavailable fast path), and each re-entry would
        // otherwise import the saved labels again on top of the ones already
        // on screen -- doubling the room's labels on every blip. Set on the
        // FIRST Ready of the session whatever the outcome, so "restore is
        // attempted exactly once" holds even when there is nothing to restore.
        private bool m_hasAttemptedLabelRestore;

        // Read by SentisInferenceRunManager, which discards a completed
        // detection when the shared anchor cannot place it in world space.
        internal bool IsSpatialAnchorReady =>
            m_anchorRestoration != null && m_anchorRestoration.CanTag;

        private void Awake()
        {
            OVRManager.TrackingLost += OnTrackingLost;
        }

        // Deferred to Start so the shared label root the anchor attaches to is
        // resolvable, and so Initialize() -- which reads the saved snapshot off
        // disk -- runs once the scene is fully awake.
        private void Start()
        {
            var operations = new MetaSpatialAnchorOperations(this, ResolveAnchorRoot);
            BindRestoration(new SpatialAnchorRestorationCoordinator(
                operations,
                Path.Combine(
                    Application.persistentDataPath, SpatialLabelSnapshotStore.DefaultFileName)));
            m_anchorRestoration.Initialize();
        }

        // Split out of Start so this wiring can be exercised without the Meta
        // adapter Start builds: an EditMode test binds a coordinator backed by
        // a fake ISpatialAnchorOperations and then drives the REAL handlers,
        // instead of re-implementing the wiring and testing the copy.
        private void BindRestoration(SpatialAnchorRestorationCoordinator coordinator)
        {
            m_anchorRestoration = coordinator;
            m_anchorRestoration.StateChanged += OnAnchorRestorationStateChanged;

            if (m_uiInference != null)
            {
                m_uiInference.LabelsChanged += OnLabelsChanged;
                // StateChanged only fires on a CHANGE, and the machine starts
                // outside Ready, so the initial hidden state has to be set here
                // rather than waiting for a transition that never comes.
                m_uiInference.SetRestorationAvailable(m_anchorRestoration.CanTag);
            }
        }

        private GameObject ResolveAnchorRoot()
        {
            var contentParent = m_uiInference != null ? m_uiInference.ContentParent : null;
            return contentParent != null ? contentParent.gameObject : null;
        }

        // Spatial-anchor Restoration Task 4: the anchor's world pose, read live
        // off the GameObject the OVRSpatialAnchor component is attached to.
        // Meta keeps that transform tracking the real-world anchor, so this is
        // the anchor pose without this class -- or SentisInferenceUiManager --
        // needing to hold an OVRSpatialAnchor reference.
        private bool TryResolveAnchorPose(out Pose anchorPose)
        {
            var root = ResolveAnchorRoot();
            if (root == null)
            {
                anchorPose = default;
                return false;
            }

            anchorPose = new Pose(root.transform.position, root.transform.rotation);
            return true;
        }

        private void OnDestroy()
        {
            if (m_aimReticle != null)
            {
                m_aimReticle.SetActive(false);
            }

            if (m_uiInference != null)
            {
                m_uiInference.LabelsChanged -= OnLabelsChanged;
            }

            if (m_anchorRestoration != null)
            {
                m_anchorRestoration.StateChanged -= OnAnchorRestorationStateChanged;

                // Runtime disposal only. Teardown deliberately does NOT erase
                // the Meta anchor or delete the snapshot -- that is what makes
                // a tagged room survive an app close.
                m_anchorRestoration.Shutdown();
            }

            OVRManager.TrackingLost -= OnTrackingLost;
        }

        // The confirmed "forget saved room and labels" path. Task 5 wires the
        // settings screen to this; the erase itself lives in the coordinator.
        internal void RequestSpatialSpaceReset() => m_anchorRestoration?.RequestReset();

        private void OnAnchorRestorationStateChanged(SpatialAnchorRestorationState state)
        {
            if (state == SpatialAnchorRestorationState.Resetting)
            {
                // Clearing the label views on the confirmed reset rather than after
                // the erase completes: the erase can fail and be retried, and
                // leaving labels on screen through that would say the reset had not
                // happened. The snapshot on disk is still untouched until the erase
                // succeeds, which is the ordering that actually matters.
                //
                // The LabelsChanged this raises does NOT write an empty snapshot
                // over the saved one: OnLabelsChanged persists only when Ready,
                // and the coordinator refuses a persist while Resetting anyway.
                m_remoteNaming?.TryCancelPending();
                m_uiInference?.ClearAnnotations();
            }

            var ready = state == SpatialAnchorRestorationState.Ready;
            m_uiInference?.SetRestorationAvailable(ready);

            // After SetRestorationAvailable(true), so restored views are created
            // already visible instead of spawning hidden and flipping on.
            if (ready)
            {
                RestoreSavedLabelsOnce();
            }
        }

        // Spatial-anchor Restoration Task 4: import the saved labels, at most
        // once per session. A snapshot with no labels is a freshly created
        // anchor, not a restored room -- there is nothing to import, and the
        // attempt is still consumed so a later Ready cannot re-import the
        // labels this session has since committed and persisted.
        private void RestoreSavedLabelsOnce()
        {
            if (m_hasAttemptedLabelRestore)
            {
                return;
            }

            m_hasAttemptedLabelRestore = true;

            var snapshot = m_anchorRestoration.Snapshot;
            if (snapshot?.labels == null || snapshot.labels.Length == 0 || m_uiInference == null)
            {
                return;
            }

            if (!TryResolveAnchorPose(out var anchorPose))
            {
                Debug.LogWarning(
                    $"[ObjectTagger] the shared spatial anchor is Ready but its root transform could " +
                    $"not be resolved; {snapshot.labels.Length} saved label(s) were not restored. " +
                    "The snapshot is left on disk untouched.");
                return;
            }

            var restored = m_uiInference.RestoreCommittedLabels(snapshot.labels, anchorPose);
            Debug.Log(
                $"[ObjectTagger] restored {restored} of {snapshot.labels.Length} saved spatial labels");
        }

        // Spatial-anchor Restoration Task 4: the committed set changed, so the
        // snapshot on disk is now stale. Persist only when Ready -- outside it
        // the anchor these positions are relative to is not the bound one, and
        // saving against it would move every label on the next launch.
        private void OnLabelsChanged()
        {
            if (m_anchorRestoration == null || !m_anchorRestoration.CanTag || m_uiInference == null)
            {
                return;
            }

            // No anchor pose is passed: every label already carries the local
            // position it was measured at, and handing the export a live pose
            // is exactly how anchor drift would leak into the snapshot. See
            // LabelRecord.AnchorLocalPosition.
            //
            // anchorUuid is left unset on purpose too: Persist ignores whatever
            // the caller puts there and stamps the live bound anchor's UUID, so
            // a snapshot can never name an anchor its labels were not placed
            // against.
            m_anchorRestoration.Persist(new SpatialLabelSnapshot
            {
                labels = m_uiInference.ExportCommittedLabels()
            });
        }

        private void OnTrackingLost() => m_remoteNaming?.TryCancelPending();

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

            // Spatial-anchor Restoration Task 3: tagging is gated on the whole
            // restoration state machine, not on Meta's raw IsTracked flag. Only
            // Ready means the shared anchor is the SAVED one, bound and
            // localized -- tagging before that would pin labels to an anchor
            // the next launch will not recognise.
            m_anchorRestoration?.Tick(Time.realtimeSinceStartup);
            var canTag = IsSpatialAnchorReady;
            HandleRemoteAvailability(
                canTag,
                m_uiMenuManager == null || m_uiMenuManager.IsPaused);

            if (wasStartedAtFrameStart &&
                m_uiMenuManager != null &&
                RemoteNamingInputPolicy.CanStartResolvedAim(
                    m_cameraAccess.IsPlaying,
                    m_uiMenuManager.IsPaused,
                    m_wasPausedLastFrame,
                    canTag,
                    m_currentAimFrame.HasResolvedTarget) &&
                InputManager.IsButtonADownOrPinchStarted())
            {
                m_remoteNaming?.TryStartAtResolvedPoint(
                    m_cameraAccess.GetTexture(),
                    m_currentAimFrame.ResolvedPoint);
            }

            m_wasPausedLastFrame = m_uiMenuManager == null || m_uiMenuManager.IsPaused;
            UpdateBButtonHoldState(canTag);
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

            LogCropExtentOnce(resolution, plan, measured, cameraPose);

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

        // Diagnostic for the ~5%-per-side undersize measured on device
        // 2026-08-22 (D19). Two trials, 012 and 017, put the operator's read of
        // the bracket boundary 46.5 px and 46.0 px inboard of the true crop edge
        // on a 896-wide crop -- unchanged across a rendering change, so the
        // square really is smaller than the region it captures.
        //
        // The check that matters is LINEARITY. For a pinhole, a crop covering a
        // fraction of the frame must subtend that same fraction of the frame's
        // angle. If crop/full does not equal the normalized rect, then
        // RemoteImageCrop and ViewportPointToRay disagree about what the
        // normalized coordinates are relative to -- the delivered frame versus
        // the sensor crop region -- and that is the bug. Every EditMode test
        // feeds a synthetic pinhole at f=800, so none of them can see this.
        private void LogCropExtentOnce(
            Vector2Int resolution, RemoteImageCropPlan plan, Vector2 measured, Pose cameraPose)
        {
            if (m_loggedCropExtent)
            {
                return;
            }

            m_loggedCropExtent = true;

            var norm = plan.NormalizedRect;
            Debug.Log(
                $"[ObjectTagger] DIAG crop extent measured ({measured.x:F6}, {measured.y:F6}) per metre, " +
                $"normalized rect {norm.width:F6}x{norm.height:F6}, source {plan.SourceRect.width}x{plan.SourceRect.height}");

            if (!RemoteCropSquarePlacement.TryMeasureExtentPerMetre(
                    new Rect(0f, 0f, 1f, 1f),
                    cameraPose,
                    viewportPoint => m_cameraAccess.ViewportPointToRay(viewportPoint, cameraPose),
                    out var fullExtent))
            {
                Debug.Log("[ObjectTagger] DIAG full-frame extent UNAVAILABLE");
                return;
            }

            var fx = fullExtent.x > 0f ? resolution.x / fullExtent.x : 0f;
            var fy = fullExtent.y > 0f ? resolution.y / fullExtent.y : 0f;
            var expectedX = fx > 0f ? plan.SourceRect.width / fx : 0f;
            var expectedY = fy > 0f ? plan.SourceRect.height / fy : 0f;

            Debug.Log(
                $"[ObjectTagger] DIAG full-frame extent ({fullExtent.x:F6}, {fullExtent.y:F6}), " +
                $"implied f ({fx:F2}, {fy:F2}) px, " +
                $"expected crop extent ({expectedX:F6}, {expectedY:F6})");
            Debug.Log(
                $"[ObjectTagger] DIAG ratio measured/expected " +
                $"({(expectedX > 0f ? measured.x / expectedX : 0f):F6}, {(expectedY > 0f ? measured.y / expectedY : 0f):F6}), " +
                $"linearity crop/full ({(fullExtent.x > 0f ? measured.x / fullExtent.x : 0f):F6}, " +
                $"{(fullExtent.y > 0f ? measured.y / fullExtent.y : 0f):F6}) " +
                $"vs normalized ({norm.width:F6}, {norm.height:F6})");
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

        // Spatial-anchor Restoration Task 4: the whole gesture is dropped
        // outside Ready, including a press already in flight. Both of its
        // outcomes remove committed labels, the views are hidden there so the
        // user cannot see what they would be removing, and the removal would
        // be written to disk the moment Ready returned. Commit input needs no
        // equivalent guard: remote naming already requires canTag
        // (RemoteNamingInputPolicy.CanStartResolvedAim, above) and
        // SentisInferenceRunManager discards a detection outright unless
        // IsSpatialAnchorReady.
        private void UpdateBButtonHoldState(bool canTag)
        {
            if (!canTag)
            {
                m_bIsHeld = false;
                m_bClearAllFired = false;
                m_bCanceledPendingOnPress = false;
                return;
            }

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

        private void HandleRemoteAvailability(bool canTag, bool appPaused)
        {
            if (!canTag || appPaused)
            {
                m_remoteNaming?.TryCancelPending();
            }
        }
    }
}
