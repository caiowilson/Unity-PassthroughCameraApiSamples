// Spatial-anchor Restoration Task 4 — DetectionManager's half of the feature:
// the once-per-session restore guard, the Ready-only visibility gate, and the
// LabelsChanged -> Persist composition.
//
// These run the REAL handlers, not a re-implementation of them. Three existing
// patterns make that possible without a device:
//   * DetectionManager is built on an INACTIVE GameObject, so Awake/Start (and
//     their OVRManager and MetaSpatialAnchorOperations calls) never run --
//     exactly what RemoteNamingFailureIntegrationTests already does.
//   * SpatialAnchorRestorationCoordinator takes an ISpatialAnchorOperations and
//     a plain file path, so a fake plus a temp file drives the whole machine.
//   * BindRestoration is the seam Start's dependency construction was split
//     from; invoking it by reflection wires the real events to the real
//     handlers.
//
// The case that matters most is the LAST one in the restore-guard test: Task
// 3's fast recovery re-enters Ready on the very next frame after a tracking
// blip, and a restore that ran again there would silently double every label
// in the room.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialLabelRestorationWiringTests
    {
        private static readonly Pose AnchorPose =
            new Pose(new Vector3(1.5f, -0.25f, 3f), Quaternion.Euler(0f, 90f, 0f));

        private string m_directory;
        private string m_snapshotPath;
        private FakeAnchorOperations m_operations;

        private GameObject m_fixtureRoot;
        private Transform m_contentParent;
        private SentisInferenceUiManager m_uiInference;
        private DetectionManager m_detectionManager;

        [SetUp]
        public void SetUp()
        {
            m_directory = Path.Combine(Path.GetTempPath(), $"object-tagger-wiring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(m_directory);
            m_snapshotPath = Path.Combine(m_directory, SpatialLabelSnapshotStore.DefaultFileName);
            m_operations = new FakeAnchorOperations();

            m_fixtureRoot = new GameObject("SpatialLabelRestorationWiringFixture");
            m_fixtureRoot.SetActive(false);

            var uiObject = new GameObject("SentisInferenceUiManager");
            uiObject.transform.SetParent(m_fixtureRoot.transform, false);
            m_uiInference = uiObject.AddComponent<SentisInferenceUiManager>();

            var contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(m_fixtureRoot.transform, false);
            m_contentParent = contentObject.transform;
            m_contentParent.SetPositionAndRotation(AnchorPose.position, AnchorPose.rotation);

            var templateObject = new GameObject("LabelTemplate", typeof(RectTransform));
            templateObject.transform.SetParent(m_contentParent, false);
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textObject.transform.SetParent(templateObject.transform, false);
            templateObject.SetActive(false);

            var uiProperties = new SerializedObject(m_uiInference);
            uiProperties.FindProperty("m_detectionBoxPrefab").objectReferenceValue =
                templateObject.GetComponent<RectTransform>();
            uiProperties.ApplyModifiedPropertiesWithoutUndo();

            // Inactive on purpose: Awake subscribes to OVRManager and Start
            // builds a MetaSpatialAnchorOperations, neither of which exists off
            // device. BindRestoration is what the test drives instead.
            var detectionObject = new GameObject("DetectionManager");
            detectionObject.transform.SetParent(m_fixtureRoot.transform, false);
            detectionObject.SetActive(false);
            m_detectionManager = detectionObject.AddComponent<DetectionManager>();
            SetPrivateField(m_detectionManager, "m_uiInference", m_uiInference);

            m_fixtureRoot.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(m_fixtureRoot);

            try
            {
                if (Directory.Exists(m_directory))
                {
                    Directory.Delete(m_directory, true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only; a leftover temp dir affects nothing.
            }
        }

        [Test]
        public void ATrackingBlipAndRecoveryDoesNotRestoreTheSavedLabelsTwice()
        {
            var uuid = SeedSavedSnapshot("coffee mug", "desk lamp");
            var coordinator = BindCoordinator();

            coordinator.Initialize();
            Assert.AreEqual(SpatialAnchorRestorationState.Restoring, coordinator.State);
            Assert.AreEqual(0, ActiveCards().Count, "nothing is shown before the anchor is Ready");

            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State);
            Assert.AreEqual(2, m_uiInference.ExportCommittedLabels().Length, "the saved labels are imported once");
            Assert.AreEqual(2, ActiveCards().Count);

            // Tracking blips. Task 3 drops to Unavailable on the next tick.
            m_operations.IsAnchorTracked = false;
            coordinator.Tick(1f);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.AreEqual(0, ActiveCards().Count, "views hide outside Ready");
            Assert.AreEqual(
                2,
                m_uiInference.ExportCommittedLabels().Length,
                "hiding must not discard the labels themselves");

            // Tracking returns. Task 3's fast path re-enters Ready with no SDK
            // round-trip -- and the restore must NOT run again.
            m_operations.IsAnchorTracked = true;
            coordinator.Tick(2f);
            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State);
            Assert.AreEqual(
                2,
                m_uiInference.ExportCommittedLabels().Length,
                "re-entering Ready after a blip must not import the snapshot a second time");
            Assert.AreEqual(2, ActiveCards().Count, "the same views come back, not a duplicate set");

            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_snapshotPath, out var onDisk));
            Assert.AreEqual(2, onDisk.labels.Length, "the blip must not have doubled the saved snapshot either");
            Assert.AreEqual(uuid, onDisk.anchorUuid);
        }

        [Test]
        public void CommittingALabelWhileReadyPersistsItAgainstTheBoundAnchor()
        {
            var uuid = Guid.NewGuid().ToString();
            var coordinator = BindCoordinator();

            // No saved snapshot: the coordinator creates a first-run anchor.
            coordinator.Initialize();
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            Assert.AreEqual(SpatialAnchorRestorationState.Ready, coordinator.State);

            var worldPosition = new Vector3(0.5f, 1f, 2f);
            var operationId = Guid.NewGuid();
            Assert.IsTrue(m_uiInference.CreatePendingRemoteLabel(operationId, worldPosition));
            Assert.IsTrue(m_uiInference.CommitRemoteLabel(operationId, "coffee mug"));

            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_snapshotPath, out var onDisk));
            Assert.AreEqual(uuid, onDisk.anchorUuid, "Persist stamps the bound anchor's own UUID");
            var entry = onDisk.labels.Single();
            Assert.AreEqual("coffee mug", entry.className);
            AssertApproximately(
                worldPosition, SpatialLabelSnapshot.ToWorldPosition(entry.localPosition, AnchorPose));
        }

        [Test]
        public void ChangesMadeOutsideReadyDoNotTouchTheSavedSnapshot()
        {
            var uuid = SeedSavedSnapshot("coffee mug", "desk lamp");
            var coordinator = BindCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            Assert.AreEqual(2, m_uiInference.ExportCommittedLabels().Length);

            m_operations.IsAnchorTracked = false;
            coordinator.Tick(1f);
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            // ClearAnnotations raises LabelsChanged exactly as a user gesture
            // would; outside Ready it must not reach disk.
            m_uiInference.ClearAnnotations();

            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_snapshotPath, out var onDisk));
            Assert.AreEqual(2, onDisk.labels.Length, "the saved snapshot survives changes made outside Ready");
        }

        [Test]
        public void AConfirmedResetHidesTheLabelsBeforeTheEraseAndDeletesTheSnapshotAfterIt()
        {
            var uuid = SeedSavedSnapshot("coffee mug");
            var coordinator = BindCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithSuccess(uuid);
            coordinator.Tick(0f);
            Assert.AreEqual(1, ActiveCards().Count);

            coordinator.RequestReset();
            Assert.AreEqual(SpatialAnchorRestorationState.Resetting, coordinator.State);
            Assert.AreEqual(0, ActiveCards().Count, "the labels go the moment the reset is confirmed");
            Assert.AreEqual(
                0,
                m_uiInference.ExportCommittedLabels().Length,
                "the reset clears the set, not just its visibility");
            Assert.IsTrue(
                File.Exists(m_snapshotPath),
                "clearing the views must not write an empty snapshot before the erase succeeds");

            m_operations.CompleteWithSuccess();
            coordinator.Tick(1f);
            Assert.IsFalse(File.Exists(m_snapshotPath), "the snapshot goes only after the erase reports success");
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private SpatialAnchorRestorationCoordinator BindCoordinator()
        {
            var coordinator = new SpatialAnchorRestorationCoordinator(m_operations, m_snapshotPath);
            InvokePrivate(m_detectionManager, "BindRestoration", coordinator);
            return coordinator;
        }

        private string SeedSavedSnapshot(params string[] classNames)
        {
            var uuid = Guid.NewGuid().ToString();
            var snapshot = new SpatialLabelSnapshot
            {
                anchorUuid = uuid,
                labels = classNames.Select((className, index) => new SpatialLabelEntry
                {
                    id = Guid.NewGuid().ToString(),
                    classId = index,
                    className = className,
                    score = 0.5f,
                    localPosition = new Vector3(0.1f * (index + 1), 0.2f, 0.3f),
                }).ToArray(),
            };

            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_snapshotPath, snapshot));
            return uuid;
        }

        private List<Transform> ActiveCards() =>
            m_contentParent.Cast<Transform>().Where(child => child.gameObject.activeSelf).ToList();

        private static void AssertApproximately(Vector3 expected, Vector3 actual)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-3f), $"x of {actual} vs {expected}");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-3f), $"y of {actual} vs {expected}");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(1e-3f), $"z of {actual} vs {expected}");
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{fieldName} not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(
                methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"{methodName} not found on {target.GetType().Name}");
            method.Invoke(target, arguments);
        }

        // Mirrors SpatialAnchorRestorationStateTests' fake: every Begin* leaves
        // the operation Running and the test decides its outcome. Duplicated
        // rather than shared because that one is a private nested type and this
        // project rejects InternalsVisibleTo (see AssemblySeamTests.cs).
        private sealed class FakeAnchorOperations : ISpatialAnchorOperations
        {
            public SpatialAnchorOperationStatus Status { get; private set; } =
                SpatialAnchorOperationStatus.Idle;

            public bool IsAnchorTracked { get; set; }

            public string BoundAnchorUuid { get; set; }

            public bool TryGetBoundAnchorUuid(out string anchorUuid)
            {
                anchorUuid = BoundAnchorUuid;
                return !string.IsNullOrEmpty(BoundAnchorUuid);
            }

            public void BeginCreate() => Status = SpatialAnchorOperationStatus.Running;

            public void BeginRestore(string anchorUuid) => Status = SpatialAnchorOperationStatus.Running;

            public void BeginErase(string anchorUuid) => Status = SpatialAnchorOperationStatus.Running;

            public void ReleaseRuntimeAnchor()
            {
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
        }
    }
}
