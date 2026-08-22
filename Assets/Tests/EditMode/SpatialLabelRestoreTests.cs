// Spatial-anchor Restoration Task 4 — the export/restore surface on
// SentisInferenceUiManager, and the LabelsChanged contract that drives
// persistence from DetectionManager.
//
// Everything here goes through the real component with a real prefab
// hierarchy, because the thing under test IS the view rebuild: a pure-data
// test of the conversion maths would already be covered by
// SpatialLabelSnapshotTests and would prove nothing about pooled views,
// activation, or the committed-vs-pending filter.
//
// TryUntagNearestToCenter is internal and this project rejects
// InternalsVisibleTo (see AssemblySeamTests.cs), so it is reached by
// reflection — the same pattern RemoteNamingFailureIntegrationTests already
// uses for m_cameraPoseOverride, which this fixture also sets so the untag
// path has a deterministic head pose.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialLabelRestoreTests
    {
        // Deliberately neither at the origin nor axis-aligned: an identity
        // anchor pose would let a world/local mix-up pass every assertion.
        private static readonly Pose AnchorPose =
            new Pose(new Vector3(1.5f, -0.25f, 3f), Quaternion.Euler(0f, 90f, 0f));

        private GameObject m_fixtureRoot;
        private Transform m_contentParent;
        private SentisInferenceUiManager m_manager;
        private int m_labelsChangedCount;

        [SetUp]
        public void SetUp()
        {
            m_fixtureRoot = new GameObject("SpatialLabelRestoreFixture");
            m_fixtureRoot.SetActive(false);

            var managerObject = new GameObject("Manager");
            managerObject.transform.SetParent(m_fixtureRoot.transform, false);
            m_manager = managerObject.AddComponent<SentisInferenceUiManager>();
            SetPrivateField(
                m_manager,
                "m_cameraPoseOverride",
                (Func<Pose?>)(() => new Pose(Vector3.zero, Quaternion.identity)));

            var contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(m_fixtureRoot.transform, false);
            m_contentParent = contentObject.transform;
            // The content parent IS the anchor root at runtime -- the
            // OVRSpatialAnchor component lives on it -- and the manager reads
            // its live transform whenever it caches a label's local position.
            m_contentParent.SetPositionAndRotation(AnchorPose.position, AnchorPose.rotation);

            var templateObject = new GameObject("LabelTemplate", typeof(RectTransform));
            templateObject.transform.SetParent(m_contentParent, false);
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textObject.transform.SetParent(templateObject.transform, false);
            templateObject.SetActive(false);

            var managerProperties = new SerializedObject(m_manager);
            managerProperties.FindProperty("m_detectionBoxPrefab").objectReferenceValue =
                templateObject.GetComponent<RectTransform>();
            managerProperties.ApplyModifiedPropertiesWithoutUndo();

            m_fixtureRoot.SetActive(true);

            m_labelsChangedCount = 0;
            m_manager.LabelsChanged += OnLabelsChanged;
        }

        [TearDown]
        public void TearDown()
        {
            m_manager.LabelsChanged -= OnLabelsChanged;
            UnityEngine.Object.DestroyImmediate(m_fixtureRoot);
        }

        private void OnLabelsChanged() => m_labelsChangedCount++;

        // ---------------------------------------------------------------
        // Export
        // ---------------------------------------------------------------

        [Test]
        public void ExportWritesCommittedLabelsInAnchorLocalSpace()
        {
            var first = CommitRemoteLabelAt("coffee mug", new Vector3(0.5f, 1f, 2f));
            var second = CommitRemoteLabelAt("desk lamp", new Vector3(-2f, 0.25f, 4.5f));

            var exported = m_manager.ExportCommittedLabels();

            Assert.AreEqual(2, exported.Length);
            AssertEntry(exported, first, "coffee mug", new Vector3(0.5f, 1f, 2f));
            AssertEntry(exported, second, "desk lamp", new Vector3(-2f, 0.25f, 4.5f));
        }

        [Test]
        public void ExportSkipsPendingRemoteLabels()
        {
            var committed = CommitRemoteLabelAt("coffee mug", new Vector3(0.5f, 1f, 2f));
            var pending = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(pending, new Vector3(0f, 0f, 1f)));

            var exported = m_manager.ExportCommittedLabels();

            Assert.AreEqual(1, exported.Length, "a label still being identified is not committed");
            Assert.AreEqual(committed.ToString(), exported[0].id);
        }

        [Test]
        public void ExportMarksFallbackCommittedLabelsAsOnHeadset()
        {
            var operationId = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(operationId, new Vector3(0f, 0f, 1f)));
            Assert.IsTrue(m_manager.CommitRemoteLabelFallback(operationId, "chair"));

            var exported = m_manager.ExportCommittedLabels();

            Assert.AreEqual(1, exported.Length);
            Assert.AreEqual("chair" + RemoteSpatialLabelLifecycle.FallbackMarker, exported[0].className);
        }

        [Test]
        public void ExportAfterUntagOmitsTheRemovedLabel()
        {
            // The override head pose sits at the origin looking down +Z, so the
            // label on the +Z axis is the one nearest the centre of view.
            var centred = CommitRemoteLabelAt("coffee mug", new Vector3(0f, 0f, 2f));
            var offAxis = CommitRemoteLabelAt("desk lamp", new Vector3(3f, 0f, 2f));

            Assert.IsTrue(InvokeUntagNearestToCenter());

            var exported = m_manager.ExportCommittedLabels();
            Assert.AreEqual(1, exported.Length);
            Assert.AreEqual(offAxis.ToString(), exported[0].id);
            Assert.IsFalse(exported.Any(entry => entry.id == centred.ToString()));
        }

        [Test]
        public void ExportAfterClearAllIsEmpty()
        {
            CommitRemoteLabelAt("coffee mug", new Vector3(0f, 0f, 2f));
            CommitRemoteLabelAt("desk lamp", new Vector3(3f, 0f, 2f));

            m_manager.ClearAnnotations();

            Assert.AreEqual(0, m_manager.ExportCommittedLabels().Length);
        }

        [Test]
        public void EveryExportedEntryIsIndividuallyPersistable()
        {
            // The coordinator rejects a whole snapshot if any single label
            // fails SpatialLabelEntry.IsValid(), so one unusable record must
            // never cost every other label its persistence.
            CommitRemoteLabelAt("coffee mug", new Vector3(0.5f, 1f, 2f));
            var pending = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(pending, new Vector3(0f, 0f, 1f)));

            var exported = m_manager.ExportCommittedLabels();

            Assert.IsNotEmpty(exported);
            Assert.IsTrue(exported.All(entry => entry.IsValid()));
        }

        // ---------------------------------------------------------------
        // Anchor drift
        //
        // A committed label's world position only changes on commit,
        // association or restore. Meta keeps refining the anchor's pose
        // underneath it, and any later LabelsChanged -- a DIFFERENT label
        // re-associating is enough -- re-exports every label. If export
        // re-derived a stale label's local offset from the pose live at that
        // moment, the anchor's movement since would be baked into the saved
        // snapshot and compound on every reload.
        // ---------------------------------------------------------------

        [Test]
        public void ExportKeepsAStaleLabelsLocalPositionWhenTheAnchorMovesAfterwards()
        {
            var worldPosition = new Vector3(0.5f, 1f, 2f);
            CommitRemoteLabelAt("coffee mug", worldPosition);
            var expectedLocal = SpatialLabelSnapshot.ToLocalPosition(worldPosition, AnchorPose);

            MoveAnchorTo(new Pose(new Vector3(4f, -1f, 7f), Quaternion.Euler(10f, 37f, -5f)));

            // The mug is never touched again; an unrelated second label is what
            // triggers the re-export in production.
            CommitRemoteLabelAt("desk lamp", new Vector3(-2f, 0.25f, 4.5f));

            var mug = m_manager.ExportCommittedLabels().Single(entry => entry.className == "coffee mug");
            AssertApproximately(expectedLocal, mug.localPosition);
            AssertApproximately(
                worldPosition,
                SpatialLabelSnapshot.ToWorldPosition(mug.localPosition, AnchorPose));
        }

        [Test]
        public void ExportUsesTheAnchorPoseContemporaneousWithEachLabel()
        {
            var earlyWorldPosition = new Vector3(0.5f, 1f, 2f);
            CommitRemoteLabelAt("coffee mug", earlyWorldPosition);

            var laterAnchorPose = new Pose(new Vector3(4f, -1f, 7f), Quaternion.Euler(0f, 37f, 0f));
            MoveAnchorTo(laterAnchorPose);

            var lateWorldPosition = new Vector3(-2f, 0.25f, 4.5f);
            CommitRemoteLabelAt("desk lamp", lateWorldPosition);

            var exported = m_manager.ExportCommittedLabels();

            // Each label round-trips through the pose that was live when IT was
            // placed -- two different poses in one snapshot.
            AssertApproximately(
                earlyWorldPosition,
                SpatialLabelSnapshot.ToWorldPosition(
                    exported.Single(e => e.className == "coffee mug").localPosition, AnchorPose));
            AssertApproximately(
                lateWorldPosition,
                SpatialLabelSnapshot.ToWorldPosition(
                    exported.Single(e => e.className == "desk lamp").localPosition, laterAnchorPose));
        }

        [Test]
        public void RestoredLabelsKeepTheirStoredLocalPositionAcrossALaterAnchorMove()
        {
            var entry = Entry("coffee mug", new Vector3(0.5f, 1f, 2f), 41, 0.7f);
            var storedLocal = entry.localPosition;

            Assert.AreEqual(1, m_manager.RestoreCommittedLabels(new[] { entry }, AnchorPose));

            MoveAnchorTo(new Pose(new Vector3(-3f, 2f, 1f), Quaternion.Euler(0f, -120f, 0f)));

            var exported = m_manager.ExportCommittedLabels().Single();
            AssertApproximately(storedLocal, exported.localPosition);
        }

        // ---------------------------------------------------------------
        // Restore
        // ---------------------------------------------------------------

        [Test]
        public void RestoreImportsEveryValidLabelAtItsAnchorRelativeWorldPosition()
        {
            var worldPositions = new[]
            {
                new Vector3(0.5f, 1f, 2f),
                new Vector3(-2f, 0.25f, 4.5f),
                new Vector3(0f, -1.75f, 0.5f),
            };
            var entries = worldPositions
                .Select((position, index) => Entry($"label {index}", position, index + 1, 0.4f + 0.1f * index))
                .ToArray();

            Assert.AreEqual(3, m_manager.RestoreCommittedLabels(entries, AnchorPose));
            Assert.AreEqual(3, ActiveCards().Count);

            for (var i = 0; i < worldPositions.Length; i++)
            {
                var card = ActiveCards().Single(
                    child => child.GetComponentInChildren<Text>(true).text == $"label {i}");
                AssertApproximately(worldPositions[i], card.position);
            }
        }

        [Test]
        public void RestoreSkipsInvalidEntries()
        {
            var entries = new[]
            {
                null,
                Entry("bad id", new Vector3(0f, 0f, 1f), 1, 0.5f, id: "not-a-uuid"),
                Entry(string.Empty, new Vector3(0f, 0f, 2f), 1, 0.5f),
                Entry("nan score", new Vector3(0f, 0f, 3f), 1, float.NaN),
                Entry("infinite position", new Vector3(float.PositiveInfinity, 0f, 4f), 1, 0.5f),
                Entry("good one", new Vector3(0.5f, 1f, 2f), 7, 0.9f),
                Entry("good two", new Vector3(-1f, 0f, 3f), 8, 0.8f),
            };

            Assert.AreEqual(2, m_manager.RestoreCommittedLabels(entries, AnchorPose));
            Assert.AreEqual(2, ActiveCards().Count);
        }

        [Test]
        public void RestoreRetainsClassIdClassNameAndConfidence()
        {
            var entries = new[]
            {
                Entry("coffee mug", new Vector3(0.5f, 1f, 2f), 41, 0.73f),
                Entry("desk lamp", new Vector3(-2f, 0.25f, 4.5f), 62, 0.51f),
            };

            Assert.AreEqual(2, m_manager.RestoreCommittedLabels(entries, AnchorPose));

            var exported = m_manager.ExportCommittedLabels();
            Assert.AreEqual(2, exported.Length);

            foreach (var original in entries)
            {
                var roundTripped = exported.Single(entry => entry.className == original.className);
                Assert.AreEqual(original.classId, roundTripped.classId);
                Assert.That(roundTripped.score, Is.EqualTo(original.score).Within(1e-4f));
                AssertApproximately(original.localPosition, roundTripped.localPosition);
            }
        }

        [Test]
        public void RestoredLabelsGetFreshUniqueSessionIds()
        {
            // A restored label is a NEW session's label, not a resumed remote
            // naming operation: reusing the stored id would let a restored card
            // collide with a live operation id in FindRemoteViewIndex.
            var entries = new[]
            {
                Entry("coffee mug", new Vector3(0.5f, 1f, 2f), 41, 0.73f),
                Entry("desk lamp", new Vector3(-2f, 0.25f, 4.5f), 62, 0.51f),
            };
            var originalIds = entries.Select(entry => entry.id).ToArray();

            Assert.AreEqual(2, m_manager.RestoreCommittedLabels(entries, AnchorPose));

            var exportedIds = m_manager.ExportCommittedLabels().Select(entry => entry.id).ToArray();
            Assert.AreEqual(2, exportedIds.Distinct().Count());
            CollectionAssert.IsEmpty(exportedIds.Intersect(originalIds));
        }

        [Test]
        public void RestoredLabelsShowTheStoredNameWithoutAConfidenceSuffix()
        {
            // The stored name is the only thing that survives a relaunch, and
            // for a remote-named label the stored score is meaningless (it is
            // never set). Rendering "coffee mug — 0%" would invent a number.
            Assert.AreEqual(
                1,
                m_manager.RestoreCommittedLabels(
                    new[] { Entry("coffee mug", new Vector3(0.5f, 1f, 2f), 41, 0f) }, AnchorPose));

            Assert.AreEqual(
                "coffee mug",
                ActiveCards().Single().GetComponentInChildren<Text>(true).text);
        }

        [Test]
        public void RestoreToleratesNullAndEmptyInput()
        {
            Assert.AreEqual(0, m_manager.RestoreCommittedLabels(null, AnchorPose));
            Assert.AreEqual(0, m_manager.RestoreCommittedLabels(Array.Empty<SpatialLabelEntry>(), AnchorPose));
            Assert.AreEqual(0, ActiveCards().Count);
        }

        // ---------------------------------------------------------------
        // SetRestorationAvailable
        // ---------------------------------------------------------------

        [Test]
        public void SetRestorationAvailableHidesAndReshowsViewsWithoutChangingTheExport()
        {
            CommitRemoteLabelAt("coffee mug", new Vector3(0.5f, 1f, 2f));
            CommitRemoteLabelAt("desk lamp", new Vector3(-2f, 0.25f, 4.5f));
            var beforeHide = m_manager.ExportCommittedLabels();

            m_manager.SetRestorationAvailable(false);
            Assert.AreEqual(0, ActiveCards().Count, "Restoring/Unavailable must hide every committed view");
            Assert.AreEqual(
                beforeHide.Length,
                m_manager.ExportCommittedLabels().Length,
                "hiding views must not drop the labels themselves");

            m_manager.SetRestorationAvailable(true);
            Assert.AreEqual(2, ActiveCards().Count, "returning to Ready must bring the same views back");
        }

        [Test]
        public void RestoreWhileUnavailableCreatesHiddenViewsThatReappearWhenReady()
        {
            m_manager.SetRestorationAvailable(false);

            Assert.AreEqual(
                1,
                m_manager.RestoreCommittedLabels(
                    new[] { Entry("coffee mug", new Vector3(0.5f, 1f, 2f), 41, 0.7f) }, AnchorPose));
            Assert.AreEqual(0, ActiveCards().Count);

            m_manager.SetRestorationAvailable(true);
            Assert.AreEqual(1, ActiveCards().Count);
        }

        // ---------------------------------------------------------------
        // LabelsChanged
        // ---------------------------------------------------------------

        [Test]
        public void LabelsChangedFiresOnceForEachCommittedSetChange()
        {
            var operationId = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(operationId, new Vector3(0f, 0f, 2f)));
            Assert.AreEqual(0, m_labelsChangedCount, "a pending label is not part of the committed set");

            Assert.IsTrue(m_manager.CommitRemoteLabel(operationId, "coffee mug"));
            Assert.AreEqual(1, m_labelsChangedCount);

            var fallbackId = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(fallbackId, new Vector3(3f, 0f, 2f)));
            Assert.IsTrue(m_manager.CommitRemoteLabelFallback(fallbackId, "chair"));
            Assert.AreEqual(2, m_labelsChangedCount);

            Assert.IsTrue(InvokeUntagNearestToCenter());
            Assert.AreEqual(3, m_labelsChangedCount);

            m_manager.ClearAnnotations();
            Assert.AreEqual(4, m_labelsChangedCount);

            Assert.AreEqual(
                1,
                m_manager.RestoreCommittedLabels(
                    new[] { Entry("coffee mug", new Vector3(0.5f, 1f, 2f), 41, 0.7f) }, AnchorPose));
            Assert.AreEqual(5, m_labelsChangedCount, "restore fires exactly one event, not one per label");
        }

        [Test]
        public void LabelsChangedFiresWhenRemovingACommittedRemoteLabel()
        {
            var operationId = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(operationId, new Vector3(0f, 0f, 2f)));
            Assert.IsTrue(m_manager.CommitRemoteLabel(operationId, "coffee mug"));
            m_labelsChangedCount = 0;

            Assert.IsTrue(m_manager.RemoveRemoteLabel(operationId));
            Assert.AreEqual(1, m_labelsChangedCount);
        }

        [Test]
        public void LabelsChangedIgnoresGhostPreviewAndVisibilityOnlyChanges()
        {
            CommitRemoteLabelAt("coffee mug", new Vector3(0.5f, 1f, 2f));
            m_labelsChangedCount = 0;

            InvokeInvalidateLiveCandidate();
            m_manager.SetRestorationAvailable(false);
            m_manager.SetRestorationAvailable(true);

            Assert.AreEqual(0, m_labelsChangedCount);
        }

        [Test]
        public void LabelsChangedIsSilentForNoOpCallsAndCancelledPendingLabels()
        {
            var pending = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(pending, new Vector3(0f, 0f, 2f)));
            m_labelsChangedCount = 0;

            // Cancelling a naming operation that never committed leaves the
            // committed set untouched, and so must not trigger a snapshot write.
            Assert.IsTrue(m_manager.RemoveRemoteLabel(pending));
            Assert.AreEqual(0, m_labelsChangedCount);

            Assert.IsFalse(m_manager.CommitRemoteLabel(Guid.NewGuid(), "nothing"));
            Assert.IsFalse(m_manager.RemoveRemoteLabel(Guid.NewGuid()));
            m_manager.ClearAnnotations();
            Assert.AreEqual(0, m_manager.RestoreCommittedLabels(Array.Empty<SpatialLabelEntry>(), AnchorPose));
            Assert.IsFalse(InvokeUntagNearestToCenter());

            Assert.AreEqual(0, m_labelsChangedCount);
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private Guid CommitRemoteLabelAt(string name, Vector3 worldPosition)
        {
            var operationId = Guid.NewGuid();
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(operationId, worldPosition));
            Assert.IsTrue(m_manager.CommitRemoteLabel(operationId, name));
            return operationId;
        }

        private static SpatialLabelEntry Entry(
            string className, Vector3 worldPosition, int classId, float score, string id = null)
        {
            return new SpatialLabelEntry
            {
                id = id ?? Guid.NewGuid().ToString(),
                classId = classId,
                className = className,
                score = score,
                localPosition = SpatialLabelSnapshot.ToLocalPosition(worldPosition, AnchorPose),
            };
        }

        /// Stands in for Meta refining the shared anchor's pose: the label root
        /// the OVRSpatialAnchor component sits on is what actually moves.
        private void MoveAnchorTo(Pose pose) =>
            m_contentParent.SetPositionAndRotation(pose.position, pose.rotation);

        private List<Transform> ActiveCards() =>
            m_contentParent.Cast<Transform>().Where(child => child.gameObject.activeSelf).ToList();

        private static void AssertEntry(
            SpatialLabelEntry[] exported, Guid sessionId, string className, Vector3 worldPosition)
        {
            var entry = exported.SingleOrDefault(candidate => candidate.id == sessionId.ToString());
            Assert.IsNotNull(entry, $"no exported entry for {sessionId}");
            Assert.AreEqual(className, entry.className);
            Assert.IsTrue(entry.IsValid());
            AssertApproximately(
                SpatialLabelSnapshot.ToLocalPosition(worldPosition, AnchorPose), entry.localPosition);
            AssertApproximately(
                worldPosition, SpatialLabelSnapshot.ToWorldPosition(entry.localPosition, AnchorPose));
        }

        private static void AssertApproximately(Vector3 expected, Vector3 actual)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-3f), $"x of {actual} vs {expected}");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-3f), $"y of {actual} vs {expected}");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(1e-3f), $"z of {actual} vs {expected}");
        }

        private bool InvokeUntagNearestToCenter() =>
            (bool)InvokeNonPublic(m_manager, "TryUntagNearestToCenter");

        private void InvokeInvalidateLiveCandidate() =>
            InvokeNonPublic(m_manager, "InvalidateLiveCandidate");

        private static object InvokeNonPublic(object target, string methodName)
        {
            var method = target.GetType().GetMethod(
                methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"{methodName} not found on {target.GetType().Name}");
            return method.Invoke(target, Array.Empty<object>());
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{fieldName} not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }
    }
}
