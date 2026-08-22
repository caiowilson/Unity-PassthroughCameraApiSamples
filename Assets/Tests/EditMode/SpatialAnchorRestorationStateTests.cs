// Spatial-anchor Restoration Task 3 — the restoration state machine.
//
// Everything the coordinator decides is asserted here; everything it cannot
// decide without a headset is behind ISpatialAnchorOperations and faked. That
// split is the point: EditMode has no device and no Link, so an
// OVRSpatialAnchor cannot be created, the anchor store cannot be reached, and
// nothing can localize. Driving the fake's outcomes is the only way to assert
// "load/bind/localization failure becomes Unavailable" at all.
//
// Snapshot persistence is NOT faked. SpatialLabelSnapshotStore is already
// path-agnostic (Task 2), so these tests point the coordinator at a real file
// under a per-test temp directory and assert what actually landed on disk —
// in particular that a confirmed reset deletes it and that a failed
// restoration does not.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;
using UnityEngine.TestTools;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialAnchorRestorationStateTests
    {
        private string m_directory;
        private string m_path;
        private FakeAnchorOperations m_operations;

        [SetUp]
        public void SetUp()
        {
            m_directory = Path.Combine(Path.GetTempPath(), $"object-tagger-anchor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(m_directory);
            m_path = Path.Combine(m_directory, SpatialLabelSnapshotStore.DefaultFileName);
            m_operations = new FakeAnchorOperations();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(m_directory))
                {
                    Directory.Delete(m_directory, true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only; leftover temp dirs don't affect other tests.
            }
        }

        // --- no saved snapshot: create a first-run anchor -------------------

        [Test]
        public void InitializeWithoutASavedSnapshotStartsCreatingAFirstRunAnchor()
        {
            var coordinator = NewCoordinator();

            coordinator.Initialize();

            Assert.AreEqual(SpatialAnchorRestorationState.NoSavedSpace, coordinator.State);
            Assert.IsFalse(coordinator.CanTag, "Tagging must wait until the fresh anchor is saved.");
            Assert.IsNull(coordinator.Snapshot);
            Assert.AreEqual(1, m_operations.CreateRequests);
            Assert.IsEmpty(m_operations.RestoreRequests);
        }

        [Test]
        public void InitializeWithoutASavedSnapshotPersistsTheCreatedUuidWithNoLabels()
        {
            var uuid = Guid.NewGuid().ToString();
            var coordinator = NewCoordinator();
            coordinator.Initialize();

            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);

            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State);
            Assert.IsTrue(coordinator.CanTag);
            Assert.IsNotNull(coordinator.Snapshot);
            Assert.AreEqual(uuid, coordinator.Snapshot.anchorUuid);
            Assert.AreEqual(0, coordinator.Snapshot.labels.Length, "A first-run space starts with no labels.");

            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out var onDisk),
                "The created UUID must be on disk before tagging is enabled.");
            Assert.AreEqual(uuid, onDisk.anchorUuid);
            Assert.AreEqual(0, onDisk.labels.Length);
        }

        [Test]
        public void InitializeWithACorruptSnapshotFileFallsBackToCreatingAFreshSpace()
        {
            File.WriteAllText(m_path, "{ this is not a snapshot");

            var coordinator = NewCoordinator();
            coordinator.Initialize();

            Assert.AreEqual(SpatialAnchorRestorationState.NoSavedSpace, coordinator.State);
            Assert.AreEqual(1, m_operations.CreateRequests);
            Assert.IsEmpty(m_operations.RestoreRequests);
        }

        // --- saved snapshot: restore, never create -------------------------

        [Test]
        public void InitializeWithASavedSnapshotRestoresThatUuidAndNeverCreates()
        {
            var uuid = WriteSnapshot();

            var coordinator = NewCoordinator();
            coordinator.Initialize();

            Assert.AreEqual(SpatialAnchorRestorationState.Restoring, coordinator.State);
            Assert.IsFalse(coordinator.CanTag);
            Assert.AreEqual(new[] { uuid }, m_operations.RestoreRequests.ToArray());
            Assert.AreEqual(0, m_operations.CreateRequests, "Saved data must never be replaced by a new anchor.");
        }

        [Test]
        public void ASuccessfulRestoreBecomesReadyAndKeepsTheSnapshot()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();

            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);

            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State);
            Assert.IsTrue(coordinator.CanTag);
            Assert.AreEqual(uuid, coordinator.Snapshot.anchorUuid);
            Assert.IsTrue(File.Exists(m_path));
        }

        [Test]
        public void AFailedRestoreBecomesUnavailableAndPreservesTheSnapshot()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();

            m_operations.CompleteWithFailure();
            coordinator.Tick(10f);

            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.IsFalse(coordinator.CanTag);
            Assert.AreEqual(uuid, coordinator.Snapshot.anchorUuid, "A failed restore must not drop the snapshot.");
            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out _), "The snapshot file must survive failure.");
        }

        [Test]
        public void UnavailableRetriesTheSameUuidAfterFiveSecondsAndNeverCreates()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithFailure();
            coordinator.Tick(10f);

            coordinator.Tick(10f + SpatialAnchorRestorationCoordinator.RetryIntervalSeconds - 0.1f);
            Assert.AreEqual(1, m_operations.RestoreRequests.Count, "Retried before the five-second interval elapsed.");
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            coordinator.Tick(10f + SpatialAnchorRestorationCoordinator.RetryIntervalSeconds);
            Assert.AreEqual(new[] { uuid, uuid }, m_operations.RestoreRequests.ToArray());
            Assert.AreEqual(SpatialAnchorRestorationState.Restoring, coordinator.State);
            Assert.AreEqual(0, m_operations.CreateRequests);
        }

        [Test]
        public void LosingAnchorTrackingWhileReadyBecomesUnavailableAndPreservesTheSnapshot()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);

            m_operations.IsAnchorTracked = false;
            coordinator.Tick(1f);

            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.IsFalse(coordinator.CanTag);
            Assert.AreEqual(uuid, coordinator.Snapshot.anchorUuid);
            Assert.IsEmpty(m_operations.EraseRequests, "Tracking loss must never erase the saved anchor.");

            // Recovery, not just loss: a one-frame blip must not cost five
            // seconds of disabled tagging. The pre-Task-3 per-frame IsTracked
            // gate recovered on the next frame and so must this.
            m_operations.IsAnchorTracked = true;
            coordinator.Tick(1f + 1f / 72f);

            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State,
                "A single untracked frame must not impose the five-second retry floor.");
            Assert.IsTrue(coordinator.CanTag);
            Assert.AreEqual(1, m_operations.RestoreRequests.Count,
                "Recovering an already-bound, already-tracked anchor needs no anchor-store round-trip.");
        }

        [Test]
        public void UnavailableDoesNotRecoverWhenTheBoundAnchorIsNotTheSavedOne()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);

            m_operations.IsAnchorTracked = false;
            coordinator.Tick(1f);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            // Tracking is back, but the component is bound to a different
            // anchor. The fast path must not call that Ready: labels would land
            // against an anchor the snapshot does not describe.
            m_operations.BoundAnchorUuid = Guid.NewGuid().ToString();
            m_operations.IsAnchorTracked = true;
            coordinator.Tick(2f);

            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.IsFalse(coordinator.CanTag);
        }

        [Test]
        public void UnavailableAfterARestoreFailureStillWaitsTheFullRetryInterval()
        {
            WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithFailure();
            coordinator.Tick(10f);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            // Nothing is bound here, so the fast recovery path must not fire and
            // the five-second cadence still governs.
            coordinator.Tick(10.1f);

            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.AreEqual(1, m_operations.RestoreRequests.Count);
        }

        [Test]
        public void PersistSucceedsWhileUnavailableWhenTheAnchorIsStillBound()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);
            m_operations.IsAnchorTracked = false;
            coordinator.Tick(1f);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            // An untag or clear-all that lands during a tracking blip must not
            // be silently dropped -- the labels really did change.
            var saved = coordinator.Persist(new SpatialLabelSnapshot
            {
                labels = new[] { NewLabel("chair") }
            });

            Assert.IsTrue(saved);
            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out var onDisk));
            Assert.AreEqual(uuid, onDisk.anchorUuid);
            Assert.AreEqual(1, onDisk.labels.Length);
        }

        [Test]
        public void ACreatedAnchorThatCannotBePersistedDoesNotBecomeReady()
        {
            // Makes TrySave fail for real: the snapshot's parent directory path
            // is an existing FILE, so Directory.CreateDirectory throws.
            var blocker = Path.Combine(m_directory, "blocker");
            File.WriteAllText(blocker, "not a directory");
            m_path = Path.Combine(blocker, SpatialLabelSnapshotStore.DefaultFileName);

            var coordinator = NewCoordinator();
            coordinator.Initialize();
            Assert.AreEqual(1, m_operations.CreateRequests);

            m_operations.CompleteWithSuccess(Guid.NewGuid().ToString());
            coordinator.Tick(0f);

            Assert.AreEqual(SpatialAnchorRestorationState.NoSavedSpace, coordinator.State,
                "Tagging must not be enabled against a UUID that was never persisted.");
            Assert.IsFalse(coordinator.CanTag);
            Assert.IsNull(coordinator.Snapshot);

            coordinator.Tick(SpatialAnchorRestorationCoordinator.RetryIntervalSeconds);
            Assert.AreEqual(2, m_operations.CreateRequests);
        }

        [Test]
        public void CanTagIsTrueOnlyInTheReadyState()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();

            Assert.IsFalse(coordinator.CanTag, "NoSavedSpace (uninitialized) must not allow tagging.");

            coordinator.Initialize();
            Assert.AreEqual(SpatialAnchorRestorationState.Restoring, coordinator.State);
            Assert.IsFalse(coordinator.CanTag, "Restoring must not allow tagging.");

            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State);
            Assert.IsTrue(coordinator.CanTag, "Ready must allow tagging.");

            m_operations.IsAnchorTracked = false;
            coordinator.Tick(1f);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.IsFalse(coordinator.CanTag, "Unavailable must not allow tagging.");

            coordinator.RequestReset();
            Assert.AreEqual(SpatialAnchorRestorationState.Resetting, coordinator.State);
            Assert.IsFalse(coordinator.CanTag, "Resetting must not allow tagging.");
        }

        // --- Persist --------------------------------------------------------

        [Test]
        public void PersistStampsTheBoundAnchorUuidOverWhateverTheCallerSupplied()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);

            var saved = coordinator.Persist(new SpatialLabelSnapshot
            {
                anchorUuid = Guid.NewGuid().ToString(),
                labels = new[] { NewLabel("chair") }
            });

            Assert.IsTrue(saved);
            Assert.AreEqual(uuid, coordinator.Snapshot.anchorUuid, "Persist must stamp the live anchor's UUID.");
            Assert.AreEqual(1, coordinator.Snapshot.labels.Length);
            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out var onDisk));
            Assert.AreEqual(uuid, onDisk.anchorUuid);
            Assert.AreEqual("chair", onDisk.labels[0].className);
        }

        [Test]
        public void PersistIsRejectedWhenNoAnchorIsBound()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithFailure();
            coordinator.Tick(0f);

            var saved = coordinator.Persist(new SpatialLabelSnapshot
            {
                anchorUuid = uuid,
                labels = new[] { NewLabel("chair") }
            });

            Assert.IsFalse(saved, "Labels cannot be anchored to an anchor that is not bound.");
            Assert.AreEqual(0, coordinator.Snapshot.labels.Length, "The saved snapshot must be untouched.");
        }

        [Test]
        public void PersistIsRejectedDuringAConfirmedReset()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);
            coordinator.RequestReset();

            var saved = coordinator.Persist(new SpatialLabelSnapshot
            {
                labels = new[] { NewLabel("chair") }
            });

            Assert.IsFalse(saved, "A confirmed reset must not be undone by a late persist.");
        }

        // --- confirmed reset ------------------------------------------------

        [Test]
        public void RequestResetErasesTheAnchorBeforeDeletingTheSnapshot()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);

            coordinator.RequestReset();

            Assert.AreEqual(SpatialAnchorRestorationState.Resetting, coordinator.State);
            Assert.AreEqual(new[] { uuid }, m_operations.EraseRequests.ToArray());
            Assert.IsNotNull(coordinator.Snapshot, "The snapshot survives until the erase completes.");
            Assert.IsTrue(File.Exists(m_path), "The snapshot file survives until the erase completes.");
        }

        [Test]
        public void AConfirmedResetDeletesTheSnapshotOnlyAfterTheEraseCompletes()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);
            coordinator.RequestReset();

            m_operations.CompleteWithSuccess();
            coordinator.Tick(1f);

            Assert.AreEqual(SpatialAnchorRestorationState.NoSavedSpace, coordinator.State);
            Assert.IsNull(coordinator.Snapshot);
            Assert.IsFalse(File.Exists(m_path));
            Assert.GreaterOrEqual(m_operations.ReleaseRequests, 1, "The runtime anchor must be released after a reset.");
        }

        [Test]
        public void AFailedEraseKeepsTheSnapshotAndRetries()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);
            coordinator.RequestReset();

            m_operations.CompleteWithFailure();
            coordinator.Tick(1f);

            Assert.AreEqual(SpatialAnchorRestorationState.Resetting, coordinator.State);
            Assert.IsNotNull(coordinator.Snapshot, "The snapshot must survive an erase that did not happen.");
            Assert.IsTrue(File.Exists(m_path));

            coordinator.Tick(1f + SpatialAnchorRestorationCoordinator.RetryIntervalSeconds);
            Assert.AreEqual(new[] { uuid, uuid }, m_operations.EraseRequests.ToArray());
        }

        [Test]
        public void AFreshSpaceIsCreatedAfterAConfirmedReset()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);
            coordinator.RequestReset();
            m_operations.CompleteWithSuccess();
            coordinator.Tick(1f);

            coordinator.Tick(1f);

            Assert.AreEqual(1, m_operations.CreateRequests, "A reset must permit fresh-space creation.");
            Assert.AreEqual(SpatialAnchorRestorationState.NoSavedSpace, coordinator.State);
        }

        [Test]
        public void RequestResetWhileARestoreIsStillRunningIsNotClobberedByItsOutcome()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();

            coordinator.RequestReset();
            Assert.AreEqual(SpatialAnchorRestorationState.Resetting, coordinator.State);

            // The abandoned restore reports success afterwards; the reset wins.
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(1f);

            Assert.AreEqual(SpatialAnchorRestorationState.NoSavedSpace, coordinator.State);
            Assert.IsNull(coordinator.Snapshot);
            Assert.IsFalse(File.Exists(m_path));
        }

        // --- teardown -------------------------------------------------------

        [Test]
        public void ShutdownReleasesTheRuntimeAnchorWithoutErasingAnything()
        {
            var uuid = WriteSnapshot();
            var coordinator = ReadyCoordinator(uuid);

            coordinator.Shutdown();

            Assert.IsEmpty(m_operations.EraseRequests, "Teardown must never erase the saved anchor.");
            Assert.AreEqual(1, m_operations.ReleaseRequests);
            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out var onDisk),
                "Teardown must leave the snapshot on disk for the next launch.");
            Assert.AreEqual(uuid, onDisk.anchorUuid);

            coordinator.Tick(100f);
            Assert.AreEqual(0, m_operations.CreateRequests, "A shut-down coordinator must not start new work.");
        }

        // --- events and watchdog -------------------------------------------

        [Test]
        public void StateChangedFiresOnlyOnActualTransitions()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            var observed = new List<SpatialAnchorRestorationState>();
            coordinator.StateChanged += observed.Add;

            coordinator.Initialize();
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            coordinator.Tick(1f);
            coordinator.Tick(2f);
            m_operations.IsAnchorTracked = false;
            coordinator.Tick(3f);

            Assert.AreEqual(
                new[]
                {
                    SpatialAnchorRestorationState.Restoring,
                    SpatialAnchorRestorationState.Ready,
                    SpatialAnchorRestorationState.Unavailable
                },
                observed.ToArray());
        }

        [Test]
        public void AnOperationThatNeverReportsBackTimesOutAndIsRetried()
        {
            var uuid = WriteSnapshot();
            var coordinator = NewCoordinator();
            coordinator.Initialize();

            coordinator.Tick(0f);
            coordinator.Tick(SpatialAnchorRestorationCoordinator.OperationTimeoutSeconds - 0.1f);
            Assert.AreEqual(SpatialAnchorRestorationState.Restoring, coordinator.State);

            LogAssert.Expect(LogType.Error, new Regex("timed out"));
            coordinator.Tick(SpatialAnchorRestorationCoordinator.OperationTimeoutSeconds);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            coordinator.Tick(SpatialAnchorRestorationCoordinator.OperationTimeoutSeconds
                + SpatialAnchorRestorationCoordinator.RetryIntervalSeconds);
            Assert.AreEqual(new[] { uuid, uuid }, m_operations.RestoreRequests.ToArray());
        }

        // --- helpers --------------------------------------------------------

        private SpatialAnchorRestorationCoordinator NewCoordinator()
        {
            return new SpatialAnchorRestorationCoordinator(m_operations, m_path);
        }

        private SpatialAnchorRestorationCoordinator ReadyCoordinator(string uuid)
        {
            var coordinator = NewCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State, "Test setup failed to reach Ready.");
            return coordinator;
        }

        private string WriteSnapshot()
        {
            var uuid = Guid.NewGuid().ToString();
            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_path, new SpatialLabelSnapshot
            {
                anchorUuid = uuid,
                labels = Array.Empty<SpatialLabelEntry>()
            }));
            return uuid;
        }

        private static SpatialLabelEntry NewLabel(string className)
        {
            return new SpatialLabelEntry
            {
                id = Guid.NewGuid().ToString(),
                classId = 1,
                className = className,
                score = 0.9f,
                localPosition = new Vector3(0.1f, 0.2f, 0.3f)
            };
        }

        // Stands in for MetaSpatialAnchorOperations. Every Begin* records the
        // request and leaves the operation Running; the test decides the
        // outcome, which is the only way an EditMode test can exercise a
        // localization or bind failure.
        private sealed class FakeAnchorOperations : ISpatialAnchorOperations
        {
            public SpatialAnchorOperationStatus Status { get; private set; } = SpatialAnchorOperationStatus.Idle;

            public bool IsAnchorTracked { get; set; }

            public string BoundAnchorUuid { get; set; }

            public int CreateRequests { get; private set; }

            public List<string> RestoreRequests { get; } = new List<string>();

            public List<string> EraseRequests { get; } = new List<string>();

            public int ReleaseRequests { get; private set; }

            public bool TryGetBoundAnchorUuid(out string anchorUuid)
            {
                anchorUuid = BoundAnchorUuid;
                return !string.IsNullOrEmpty(BoundAnchorUuid);
            }

            public void BeginCreate()
            {
                CreateRequests++;
                Status = SpatialAnchorOperationStatus.Running;
            }

            public void BeginRestore(string anchorUuid)
            {
                RestoreRequests.Add(anchorUuid);
                Status = SpatialAnchorOperationStatus.Running;
            }

            public void BeginErase(string anchorUuid)
            {
                EraseRequests.Add(anchorUuid);
                Status = SpatialAnchorOperationStatus.Running;
            }

            public void ReleaseRuntimeAnchor()
            {
                ReleaseRequests++;
                BoundAnchorUuid = null;
                IsAnchorTracked = false;
            }

            public void CompleteWithSuccess(string boundAnchorUuid = null)
            {
                if (boundAnchorUuid != null)
                {
                    BoundAnchorUuid = boundAnchorUuid;
                    IsAnchorTracked = true;
                }

                Status = SpatialAnchorOperationStatus.Succeeded;
            }

            public void CompleteWithFailure()
            {
                Status = SpatialAnchorOperationStatus.Failed;
            }
        }
    }
}
