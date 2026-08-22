using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialAnchorRecoveryWiringTests
    {
        private string m_directory;
        private string m_snapshotPath;
        private FakeAnchorOperations m_operations;
        private GameObject m_root;
        private DetectionUiMenuManager m_menu;
        private DetectionManager m_detectionManager;
        private Text m_statusText;
        private GameObject m_recoveryPanel;
        private GameObject m_recoveryConfirmationPanel;
        private Button m_recoveryActionButton;
        private Button m_recoveryConfirmButton;
        private Button m_recoveryCancelButton;

        [SetUp]
        public void SetUp()
        {
            m_directory = Path.Combine(Path.GetTempPath(), $"object-tagger-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(m_directory);
            m_snapshotPath = Path.Combine(m_directory, SpatialLabelSnapshotStore.DefaultFileName);
            m_operations = new FakeAnchorOperations();

            m_root = new GameObject("SpatialAnchorRecoveryFixture");
            m_root.SetActive(false);

            var menuObject = new GameObject("UiMenu");
            menuObject.transform.SetParent(m_root.transform, false);
            m_menu = menuObject.AddComponent<DetectionUiMenuManager>();
            m_statusText = new GameObject(
                "Status",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text)).GetComponent<Text>();
            m_statusText.transform.SetParent(menuObject.transform, false);

            var loadingPanel = new GameObject("Loading");
            loadingPanel.transform.SetParent(menuObject.transform, false);
            var initialPanel = new GameObject("Initial");
            initialPanel.transform.SetParent(menuObject.transform, false);
            var noPermissionPanel = new GameObject("NoPermission");
            noPermissionPanel.transform.SetParent(menuObject.transform, false);
            m_recoveryPanel = new GameObject("RecoveryPanel");
            m_recoveryPanel.transform.SetParent(menuObject.transform, false);
            m_recoveryActionButton = CreateButton(
                "ActionButton",
                m_recoveryPanel.transform,
                "Forget saved room and labels");
            m_recoveryConfirmationPanel = new GameObject("ConfirmationPanel");
            m_recoveryConfirmationPanel.transform.SetParent(m_recoveryPanel.transform, false);
            m_recoveryConfirmButton = CreateButton(
                "ConfirmButton",
                m_recoveryConfirmationPanel.transform,
                "Forget saved room and labels");
            m_recoveryCancelButton = CreateButton(
                "CancelButton",
                m_recoveryConfirmationPanel.transform,
                "Cancel");

            SetPrivateField(m_menu, "m_loadingPanel", loadingPanel);
            SetPrivateField(m_menu, "m_initialPanel", initialPanel);
            SetPrivateField(m_menu, "m_noPermissionPanel", noPermissionPanel);
            SetPrivateField(m_menu, "m_labelInformation", m_statusText);
            SetPrivateField(m_menu, "m_recoveryPanel", m_recoveryPanel);
            SetPrivateField(m_menu, "m_recoveryConfirmationPanel", m_recoveryConfirmationPanel);
            SetPrivateField(m_menu, "m_recoveryActionButton", m_recoveryActionButton);
            SetPrivateField(m_menu, "m_recoveryConfirmButton", m_recoveryConfirmButton);
            SetPrivateField(m_menu, "m_recoveryCancelButton", m_recoveryCancelButton);
            InvokePrivate(m_menu, "Awake");
            SetPrivateProperty(m_menu, "IsPaused", false);

            var detectionObject = new GameObject("DetectionManager");
            detectionObject.transform.SetParent(m_root.transform, false);
            detectionObject.SetActive(false);
            m_detectionManager = detectionObject.AddComponent<DetectionManager>();
            SetPrivateField(m_detectionManager, "m_uiMenuManager", m_menu);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(m_root);

            try
            {
                if (Directory.Exists(m_directory))
                {
                    Directory.Delete(m_directory, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [Test]
        public void CoordinatorStateChangesDriveRecoveryStatusAndPanel()
        {
            WriteSnapshot();
            var coordinator = BindCoordinator();

            coordinator.Initialize();
            Assert.AreEqual(
                SpatialAnchorRecoveryPresentationPolicy.RestoringStatus,
                m_statusText.text);
            Assert.IsFalse(m_recoveryPanel.activeSelf);

            m_operations.CompleteWithFailure();
            coordinator.Tick(0f);

            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);
            Assert.AreEqual(
                SpatialAnchorRecoveryPresentationPolicy.UnavailableStatus,
                m_statusText.text);
            Assert.IsTrue(m_recoveryPanel.activeSelf);
            Assert.IsFalse(m_menu.IsPaused);
        }

        [Test]
        public void DetectionManagerRoutesConfirmedRecoveryResetToTheCoordinatorOnce()
        {
            WriteSnapshot();
            var coordinator = BindCoordinator();
            coordinator.Initialize();
            m_operations.CompleteWithFailure();
            coordinator.Tick(0f);

            m_recoveryActionButton.onClick.Invoke();
            Assert.AreEqual(SpatialAnchorRestorationState.Unavailable, coordinator.State);

            m_recoveryConfirmButton.onClick.Invoke();
            m_recoveryConfirmButton.onClick.Invoke();

            Assert.AreEqual(SpatialAnchorRestorationState.Resetting, coordinator.State);
            Assert.AreEqual(1, m_operations.EraseRequests.Count);
        }

        [Test]
        public void RecoveryConfirmationDropsAnInFlightBPress()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Unavailable);
            m_recoveryActionButton.onClick.Invoke();
            SetPrivateField(m_detectionManager, "m_bIsHeld", true);
            SetPrivateField(m_detectionManager, "m_bClearAllFired", true);
            SetPrivateField(m_detectionManager, "m_bCanceledPendingOnPress", true);

            InvokePrivate(m_detectionManager, "UpdateBButtonHoldState", true);

            Assert.IsFalse(GetPrivateField<bool>(m_detectionManager, "m_bIsHeld"));
            Assert.IsFalse(GetPrivateField<bool>(m_detectionManager, "m_bClearAllFired"));
            Assert.IsFalse(GetPrivateField<bool>(m_detectionManager, "m_bCanceledPendingOnPress"));
        }

        [Test]
        public void ShippedPrefabWiresRecoveryPanelAndButtons()
        {
            const string prefabPath =
                "Assets/PassthroughCameraApiSamples/MultiObjectDetection/DetectionManager/Prefabs/DetectionUiMenuPrefab.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab);

            var menu = prefab.GetComponent<DetectionUiMenuManager>();
            Assert.IsNotNull(menu);

            var serialized = new SerializedObject(menu);
            var recoveryPanel = (GameObject)serialized.FindProperty("m_recoveryPanel").objectReferenceValue;
            var confirmationPanel = (GameObject)serialized.FindProperty("m_recoveryConfirmationPanel").objectReferenceValue;
            var actionButton = (Button)serialized.FindProperty("m_recoveryActionButton").objectReferenceValue;
            var confirmButton = (Button)serialized.FindProperty("m_recoveryConfirmButton").objectReferenceValue;
            var cancelButton = (Button)serialized.FindProperty("m_recoveryCancelButton").objectReferenceValue;

            Assert.IsNotNull(recoveryPanel);
            Assert.IsNotNull(confirmationPanel);
            Assert.IsNotNull(actionButton);
            Assert.IsNotNull(confirmButton);
            Assert.IsNotNull(cancelButton);
            Assert.AreEqual(
                "Forget saved room and labels",
                actionButton.GetComponentInChildren<Text>(true).text);
        }

        [Test]
        public void ShippedSceneIncludesTheWiredRecoveryUi()
        {
            const string scenePath =
                "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            try
            {
                var menu = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<DetectionUiMenuManager>(true))
                    .Single();
                var serialized = new SerializedObject(menu);
                var actionButton = (Button)serialized.FindProperty("m_recoveryActionButton").objectReferenceValue;
                var confirmButton = (Button)serialized.FindProperty("m_recoveryConfirmButton").objectReferenceValue;
                var cancelButton = (Button)serialized.FindProperty("m_recoveryCancelButton").objectReferenceValue;

                Assert.IsNotNull(serialized.FindProperty("m_recoveryPanel").objectReferenceValue);
                Assert.IsNotNull(serialized.FindProperty("m_recoveryConfirmationPanel").objectReferenceValue);
                Assert.IsNotNull(actionButton);
                Assert.IsNotNull(confirmButton);
                Assert.IsNotNull(cancelButton);
                Assert.AreEqual(
                    "Forget saved room and labels",
                    actionButton.GetComponentInChildren<Text>(true).text);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private SpatialAnchorRestorationCoordinator BindCoordinator()
        {
            var coordinator = new SpatialAnchorRestorationCoordinator(m_operations, m_snapshotPath);
            InvokePrivate(m_detectionManager, "BindRestoration", coordinator);
            return coordinator;
        }

        private void WriteSnapshot()
        {
            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_snapshotPath, new SpatialLabelSnapshot
            {
                anchorUuid = Guid.NewGuid().ToString(),
                labels = Array.Empty<SpatialLabelEntry>()
            }));
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{fieldName} not found on {target.GetType().Name}");
            return (T)field.GetValue(target);
        }

        private static Button CreateButton(string name, Transform parent, string label)
        {
            var buttonObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            var labelObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);
            labelObject.GetComponent<Text>().text = label;
            return buttonObject.GetComponent<Button>();
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{fieldName} not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        private static void SetPrivateProperty(object target, string propertyName, object value)
        {
            var property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(property, $"{propertyName} not found on {target.GetType().Name}");
            property.SetValue(target, value);
        }

        private static object InvokePrivate(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"{methodName} not found on {target.GetType().Name}");
            return method.Invoke(target, arguments);
        }

        private sealed class FakeAnchorOperations : ISpatialAnchorOperations
        {
            public SpatialAnchorOperationStatus Status { get; private set; } =
                SpatialAnchorOperationStatus.Idle;

            public bool IsAnchorTracked { get; set; }

            public string BoundAnchorUuid { get; set; }

            public System.Collections.Generic.List<string> EraseRequests { get; } =
                new System.Collections.Generic.List<string>();

            public bool TryGetBoundAnchorUuid(out string anchorUuid)
            {
                anchorUuid = BoundAnchorUuid;
                return !string.IsNullOrEmpty(BoundAnchorUuid);
            }

            public void BeginCreate() => Status = SpatialAnchorOperationStatus.Running;

            public void BeginRestore(string anchorUuid) => Status = SpatialAnchorOperationStatus.Running;

            public void BeginErase(string anchorUuid)
            {
                EraseRequests.Add(anchorUuid);
                Status = SpatialAnchorOperationStatus.Running;
            }

            public void ReleaseRuntimeAnchor()
            {
                BoundAnchorUuid = null;
                IsAnchorTracked = false;
            }

            public void CompleteWithFailure() => Status = SpatialAnchorOperationStatus.Failed;
        }
    }
}
