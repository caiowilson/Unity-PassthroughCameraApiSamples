// Spatial-anchor Restoration Task 3 — the restoration state machine: restore
// the saved shared anchor if there is one, create one if there is not, and
// never destroy saved data except on a confirmed reset.
//
// Before this class, DetectionManager always created a brand-new anchor on
// startup and OnDestroy unconditionally called EraseAnchorAsync(), so every
// normal app close deleted the anchor labels were pinned to. That is the root
// cause of "nothing persists across relaunch". The rule that replaces it is
// enforced structurally: this class is the only caller of
// ISpatialAnchorOperations.BeginErase, and it calls it from RequestReset()
// alone. Teardown goes through Shutdown(), which releases the runtime
// component and nothing else.
//
// Pure by construction: not a MonoBehaviour, no Meta SDK types, no
// UnityEngine.Time. Every SDK call is behind ISpatialAnchorOperations and
// every deadline is measured against the timestamp Tick is given, so
// SpatialAnchorRestorationStateTests can drive the whole machine in EditMode.
// Snapshot persistence is NOT abstracted — SpatialLabelSnapshotStore is
// already path-agnostic, so tests point this at a real temp file.
//
// Task 4 composes label export/restore onto Persist/Snapshot; this class
// deliberately knows nothing about label views.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class SpatialAnchorRestorationCoordinator
    {
        /// How long a failed attempt waits before the next one. Retries never
        /// stop: the escape hatch for a space that cannot be restored is a
        /// confirmed reset, not silently abandoning the saved data.
        public const float RetryIntervalSeconds = 5f;

        /// How long an operation may stay Running before it is treated as
        /// failed. Without this, one Begin* call that never reports back (a
        /// coroutine killed by a deactivated host, an OVRTask that never
        /// completes) would wedge the machine forever with no symptom.
        public const float OperationTimeoutSeconds = 30f;

        private enum PendingOperation
        {
            None,
            Create,
            Restore,
            Erase
        }

        private enum ResetStage
        {
            None,
            EraseAnchor,
            DeleteSnapshot
        }

        private readonly ISpatialAnchorOperations m_operations;
        private readonly string m_snapshotPath;

        private bool m_initialized;
        private PendingOperation m_pending;
        private ResetStage m_resetStage;
        private float m_nextAttemptAt;
        private bool m_hasPendingObservation;
        private float m_pendingObservedAt;

        public SpatialAnchorRestorationCoordinator(
            ISpatialAnchorOperations operations, string snapshotPath)
        {
            m_operations = operations ?? throw new ArgumentNullException(nameof(operations));
            m_snapshotPath = snapshotPath;
            State = SpatialAnchorRestorationState.NoSavedSpace;
        }

        public SpatialAnchorRestorationState State { get; private set; }

        /// The single gate on tagging. Only Ready means "the shared anchor is
        /// bound, localized and tracked", which is the only condition under
        /// which a committed label lands where the user pointed.
        public bool CanTag => State == SpatialAnchorRestorationState.Ready;

        /// The snapshot loaded at startup or last written by Persist. Null
        /// only before a space exists and after a confirmed reset.
        public SpatialLabelSnapshot Snapshot { get; private set; }

        public event Action<SpatialAnchorRestorationState> StateChanged;

        /// Loads the saved snapshot, if any, and starts the matching attempt:
        /// restore its UUID, or create a first-run anchor. Idempotent.
        public void Initialize()
        {
            if (m_initialized)
            {
                return;
            }

            m_initialized = true;

            if (SpatialLabelSnapshotStore.TryLoad(m_snapshotPath, out var loaded))
            {
                Snapshot = loaded;
            }

            StartAttempt();
        }

        /// Drives the machine. Called every frame the owner is enabled, with
        /// a monotonically increasing timestamp in seconds.
        public void Tick(float realtimeSinceStartup)
        {
            if (!m_initialized)
            {
                return;
            }

            if (m_pending != PendingOperation.None)
            {
                PollPendingOperation(realtimeSinceStartup);
                return;
            }

            switch (State)
            {
                case SpatialAnchorRestorationState.Ready:
                    if (!m_operations.IsAnchorTracked)
                    {
                        Debug.LogWarning(
                            "[ObjectTagger] the shared spatial anchor stopped being tracked; " +
                            "tagging is disabled and restoration will be retried");
                        EnterUnavailable(realtimeSinceStartup);
                    }

                    break;

                case SpatialAnchorRestorationState.Resetting:
                    if (realtimeSinceStartup >= m_nextAttemptAt)
                    {
                        ContinueReset(realtimeSinceStartup);
                    }

                    break;

                default:
                    if (realtimeSinceStartup >= m_nextAttemptAt)
                    {
                        StartAttempt();
                    }

                    break;
            }
        }

        /// Writes snapshot's labels to disk against the currently bound
        /// anchor. The caller's anchorUuid and version are ignored: this
        /// stamps the live anchor's UUID so a snapshot can never reference an
        /// anchor the labels were not actually placed against. Returns false
        /// without touching disk when no anchor is bound, when a confirmed
        /// reset is in progress, or when the labels are invalid.
        public bool Persist(SpatialLabelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            if (State == SpatialAnchorRestorationState.Resetting)
            {
                Debug.LogWarning("[ObjectTagger] ignoring a label persist during a confirmed reset");
                return false;
            }

            if (!m_operations.TryGetBoundAnchorUuid(out var anchorUuid))
            {
                Debug.LogWarning("[ObjectTagger] cannot persist labels: no shared spatial anchor is bound");
                return false;
            }

            return SaveSnapshot(anchorUuid, snapshot.labels);
        }

        /// The confirmed "forget saved room and labels" path, and the ONLY
        /// path that erases anything from the headset's anchor store. The
        /// snapshot file is deleted after the erase reports success, never
        /// before, so a failed erase cannot leave labels pointing at an
        /// anchor that still exists.
        public void RequestReset()
        {
            if (!m_initialized)
            {
                Debug.LogWarning("[ObjectTagger] ignoring a reset requested before initialization");
                return;
            }

            if (State == SpatialAnchorRestorationState.Resetting)
            {
                return;
            }

            SetState(SpatialAnchorRestorationState.Resetting);
            m_resetStage = ResetStage.EraseAnchor;
            BeginErase();
        }

        /// Teardown: drops the runtime OVRSpatialAnchor component and stops
        /// driving the machine. Erases nothing and deletes nothing — the
        /// saved anchor and its snapshot are exactly what the next launch
        /// restores.
        public void Shutdown()
        {
            m_initialized = false;
            SetPending(PendingOperation.None);
            m_operations.ReleaseRuntimeAnchor();
        }

        private void PollPendingOperation(float now)
        {
            var status = m_operations.Status;
            if (status == SpatialAnchorOperationStatus.Running)
            {
                if (!m_hasPendingObservation)
                {
                    m_hasPendingObservation = true;
                    m_pendingObservedAt = now;
                    return;
                }

                if (now - m_pendingObservedAt < OperationTimeoutSeconds)
                {
                    return;
                }

                Debug.LogError(
                    $"[ObjectTagger] the {m_pending} spatial-anchor operation timed out after " +
                    $"{OperationTimeoutSeconds}s without reporting back; treating it as failed");
                CompletePending(false, now);
                return;
            }

            CompletePending(status == SpatialAnchorOperationStatus.Succeeded, now);
        }

        private void CompletePending(bool succeeded, float now)
        {
            var completed = m_pending;
            SetPending(PendingOperation.None);

            switch (completed)
            {
                case PendingOperation.Erase:
                    CompleteErase(succeeded, now);
                    break;

                case PendingOperation.Create:
                    CompleteCreate(succeeded, now);
                    break;

                case PendingOperation.Restore:
                    CompleteRestore(succeeded, now);
                    break;
            }
        }

        private void CompleteCreate(bool succeeded, float now)
        {
            if (succeeded &&
                m_operations.TryGetBoundAnchorUuid(out var anchorUuid) &&
                SaveSnapshot(anchorUuid, Array.Empty<SpatialLabelEntry>()))
            {
                SetState(SpatialAnchorRestorationState.Ready);
                return;
            }

            // Stays NoSavedSpace rather than Unavailable: there is no snapshot
            // to be unavailable about, and Unavailable's retry path restores a
            // saved UUID, which does not exist yet.
            Debug.LogWarning(
                "[ObjectTagger] could not create and save a fresh shared spatial anchor; " +
                $"retrying in {RetryIntervalSeconds}s");
            ScheduleRetry(now);
            SetState(SpatialAnchorRestorationState.NoSavedSpace);
        }

        private void CompleteRestore(bool succeeded, float now)
        {
            if (succeeded)
            {
                SetState(SpatialAnchorRestorationState.Ready);
                return;
            }

            Debug.LogWarning(
                "[ObjectTagger] could not restore the saved shared spatial anchor; " +
                $"keeping the snapshot and retrying in {RetryIntervalSeconds}s");
            EnterUnavailable(now);
        }

        private void CompleteErase(bool succeeded, float now)
        {
            if (!succeeded)
            {
                Debug.LogWarning(
                    "[ObjectTagger] could not erase the saved shared spatial anchor; " +
                    $"keeping the snapshot and retrying in {RetryIntervalSeconds}s");
                ScheduleRetry(now);
                return;
            }

            m_resetStage = ResetStage.DeleteSnapshot;
            FinishReset(now);
        }

        private void ContinueReset(float now)
        {
            if (m_resetStage == ResetStage.DeleteSnapshot)
            {
                FinishReset(now);
                return;
            }

            BeginErase();
        }

        private void BeginErase()
        {
            SetPending(PendingOperation.Erase);
            m_operations.BeginErase(Snapshot != null ? Snapshot.anchorUuid : null);
        }

        private void FinishReset(float now)
        {
            if (!SpatialLabelSnapshotStore.TryDelete(m_snapshotPath))
            {
                Debug.LogError(
                    "[ObjectTagger] erased the shared spatial anchor but could not delete its snapshot; " +
                    $"retrying the delete in {RetryIntervalSeconds}s");
                ScheduleRetry(now);
                return;
            }

            Snapshot = null;
            m_resetStage = ResetStage.None;
            m_operations.ReleaseRuntimeAnchor();
            m_nextAttemptAt = now;
            SetState(SpatialAnchorRestorationState.NoSavedSpace);
        }

        private void StartAttempt()
        {
            // The one invariant that keeps a saved room from being silently
            // replaced: while a snapshot exists, the only attempt made is a
            // restore of its UUID.
            if (Snapshot != null && SpatialLabelSnapshot.IsValidUuid(Snapshot.anchorUuid))
            {
                SetState(SpatialAnchorRestorationState.Restoring);
                SetPending(PendingOperation.Restore);
                m_operations.BeginRestore(Snapshot.anchorUuid);
                return;
            }

            SetState(SpatialAnchorRestorationState.NoSavedSpace);
            SetPending(PendingOperation.Create);
            m_operations.BeginCreate();
        }

        private bool SaveSnapshot(string anchorUuid, SpatialLabelEntry[] labels)
        {
            var candidate = new SpatialLabelSnapshot
            {
                version = SpatialLabelSnapshot.CurrentVersion,
                anchorUuid = anchorUuid,
                labels = labels ?? Array.Empty<SpatialLabelEntry>()
            };

            if (!candidate.IsValid())
            {
                Debug.LogWarning("[ObjectTagger] refusing to persist an invalid spatial-label snapshot");
                return false;
            }

            if (!SpatialLabelSnapshotStore.TrySave(m_snapshotPath, candidate))
            {
                return false;
            }

            Snapshot = candidate;
            return true;
        }

        private void EnterUnavailable(float now)
        {
            ScheduleRetry(now);
            SetState(SpatialAnchorRestorationState.Unavailable);
        }

        private void ScheduleRetry(float now)
        {
            m_nextAttemptAt = now + RetryIntervalSeconds;
        }

        private void SetPending(PendingOperation pending)
        {
            m_pending = pending;
            m_hasPendingObservation = false;
            m_pendingObservedAt = 0f;
        }

        private void SetState(SpatialAnchorRestorationState next)
        {
            if (State == next)
            {
                return;
            }

            State = next;
            StateChanged?.Invoke(next);
        }
    }
}
