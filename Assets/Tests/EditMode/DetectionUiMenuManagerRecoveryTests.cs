using System.Reflection;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class DetectionUiMenuManagerRecoveryTests
    {
        private GameObject m_root;
        private DetectionUiMenuManager m_menu;
        private Text m_statusText;
        private GameObject m_recoveryPanel;
        private GameObject m_recoveryConfirmationPanel;
        private Button m_recoveryActionButton;
        private Button m_recoveryConfirmButton;
        private Button m_recoveryCancelButton;
        private int m_confirmedResets;

        [SetUp]
        public void SetUp()
        {
            m_root = new GameObject("DetectionUiMenuRecoveryFixture");
            m_root.SetActive(false);

            m_menu = m_root.AddComponent<DetectionUiMenuManager>();
            m_statusText = new GameObject(
                "Status",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text)).GetComponent<Text>();
            m_statusText.transform.SetParent(m_root.transform, false);

            var loadingPanel = new GameObject("Loading");
            loadingPanel.transform.SetParent(m_root.transform, false);
            var initialPanel = new GameObject("Initial");
            initialPanel.transform.SetParent(m_root.transform, false);
            var noPermissionPanel = new GameObject("NoPermission");
            noPermissionPanel.transform.SetParent(m_root.transform, false);

            m_recoveryPanel = new GameObject("RecoveryPanel");
            m_recoveryPanel.transform.SetParent(m_root.transform, false);
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
            m_menu.ResetSavedSpaceConfirmed += OnResetSavedSpaceConfirmed;
        }

        [TearDown]
        public void TearDown()
        {
            m_menu.ResetSavedSpaceConfirmed -= OnResetSavedSpaceConfirmed;
            UnityEngine.Object.DestroyImmediate(m_root);
        }

        [Test]
        public void RestoringShowsPassiveStatusWithoutRecoveryPanel()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Restoring);

            Assert.AreEqual(
                SpatialAnchorRecoveryPresentationPolicy.RestoringStatus,
                m_statusText.text);
            Assert.IsFalse(m_recoveryPanel.activeSelf);
        }

        [Test]
        public void UnavailableShowsRecoveryPanelWithoutOwningInputUntilConfirmationOpens()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Unavailable);

            Assert.AreEqual(
                SpatialAnchorRecoveryPresentationPolicy.UnavailableStatus,
                m_statusText.text);
            Assert.IsTrue(m_recoveryPanel.activeSelf);
            Assert.IsTrue(m_recoveryActionButton.gameObject.activeSelf);
            Assert.IsFalse(m_recoveryConfirmationPanel.activeSelf);
            Assert.IsFalse(m_menu.IsPaused);
            Assert.IsFalse(m_menu.IsBlockingTaggingInput);
        }

        [Test]
        public void FirstActivationOpensConfirmationWithoutResetting()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Unavailable);

            m_recoveryActionButton.onClick.Invoke();

            Assert.AreEqual(0, m_confirmedResets);
            Assert.IsFalse(m_recoveryActionButton.gameObject.activeSelf);
            Assert.IsTrue(m_recoveryConfirmationPanel.activeSelf);
            Assert.IsTrue(m_menu.IsPaused);
            Assert.IsTrue(m_menu.IsBlockingTaggingInput);
        }

        [Test]
        public void CancelClosesConfirmationWithoutChangingSavedData()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Unavailable);
            m_recoveryActionButton.onClick.Invoke();

            m_recoveryCancelButton.onClick.Invoke();

            Assert.AreEqual(0, m_confirmedResets);
            Assert.IsTrue(m_recoveryActionButton.gameObject.activeSelf);
            Assert.IsFalse(m_recoveryConfirmationPanel.activeSelf);
            Assert.IsFalse(m_menu.IsPaused);
            Assert.IsFalse(m_menu.IsBlockingTaggingInput);
        }

        [Test]
        public void SecondExplicitConfirmationInvokesResetOnceAndDisablesDuplicateActions()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Unavailable);
            m_recoveryActionButton.onClick.Invoke();

            m_recoveryConfirmButton.onClick.Invoke();
            m_recoveryConfirmButton.onClick.Invoke();
            m_recoveryCancelButton.onClick.Invoke();

            Assert.AreEqual(1, m_confirmedResets);
            Assert.IsFalse(m_recoveryConfirmButton.interactable);
            Assert.IsFalse(m_recoveryCancelButton.interactable);
            Assert.IsTrue(m_menu.IsPaused);
            Assert.IsTrue(m_menu.IsBlockingTaggingInput);
        }

        [Test]
        public void ReadyNoSavedSpaceAndResettingHideTheRecoveryPanel()
        {
            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Unavailable);
            m_recoveryActionButton.onClick.Invoke();

            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Ready);
            Assert.IsFalse(m_recoveryPanel.activeSelf);
            Assert.IsFalse(m_recoveryConfirmationPanel.activeSelf);
            Assert.IsFalse(m_menu.IsPaused);

            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.NoSavedSpace);
            Assert.IsFalse(m_recoveryPanel.activeSelf);

            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.Resetting);
            Assert.IsFalse(m_recoveryPanel.activeSelf);
        }

        [Test]
        public void InitialRecoveryPresentationDoesNotUnpauseTheNoPermissionState()
        {
            InvokePrivate(m_menu, "OnNoPermissionMenu");

            m_menu.SetSpatialAnchorRestorationState(SpatialAnchorRestorationState.NoSavedSpace);

            Assert.IsTrue(m_menu.IsPaused);
        }

        private void OnResetSavedSpaceConfirmed() => m_confirmedResets++;

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
    }
}
