// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionUiMenuManager : MonoBehaviour
    {
        [Header("Ui elements ref.")]
        [SerializeField] private GameObject m_loadingPanel;
        [SerializeField] private GameObject m_initialPanel;
        [SerializeField] private GameObject m_noPermissionPanel;
        [SerializeField] private Text m_labelInformation;
        [SerializeField] private GameObject m_recoveryPanel;
        [SerializeField] private GameObject m_recoveryConfirmationPanel;
        [SerializeField] private Button m_recoveryActionButton;
        [SerializeField] private Button m_recoveryConfirmButton;
        [SerializeField] private Button m_recoveryCancelButton;

        public bool IsInputActive { get; set; } = false;

        public UnityEvent<bool> OnPause;
        public event Action ResetSavedSpaceConfirmed;

        public bool IsCompanionReady => m_companionReadiness.IsReady;

        private const float RestoreStatusMinimumSeconds = 1f;
        private bool m_initialMenu;
        private bool m_noPermissionMenu;
        private CompanionReadiness m_companionReadiness = CompanionReadiness.Loading();
        private string m_remoteRecognitionPresentation;
        private SpatialAnchorRestorationState m_restorationState;
        private SpatialAnchorRecoveryPresentation m_recoveryPresentation;
        private float m_restoreStatusVisibleUntil;
        private bool m_recoveryConfirmationOpen;
        private bool m_recoveryResetRequested;

        // pause menu
        public bool IsPaused { get; private set; } = true;
        public bool IsBlockingTaggingInput => m_recoveryConfirmationOpen;

        #region Unity Functions
        private void Awake()
        {
            if (m_recoveryPanel != null)
            {
                m_recoveryPanel.SetActive(false);
            }

            if (m_recoveryConfirmationPanel != null)
            {
                m_recoveryConfirmationPanel.SetActive(false);
            }

            m_recoveryActionButton?.onClick.AddListener(HandleRecoveryActionPressed);
            m_recoveryConfirmButton?.onClick.AddListener(HandleRecoveryConfirmPressed);
            m_recoveryCancelButton?.onClick.AddListener(HandleRecoveryCancelPressed);
        }

        private IEnumerator Start()
        {
            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(false);
            m_loadingPanel.SetActive(false);

            // Wait for permissions
            OnNoPermissionMenu();
            while (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) || !OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
            {
                yield return null;
            }
            OnInitialMenu();
        }

        private void Update()
        {
            if (!IsInputActive)
                return;

            if (m_initialMenu)
            {
                InitialMenuUpdate();
            }
        }

        private void OnDestroy()
        {
            m_recoveryActionButton?.onClick.RemoveListener(HandleRecoveryActionPressed);
            m_recoveryConfirmButton?.onClick.RemoveListener(HandleRecoveryConfirmPressed);
            m_recoveryCancelButton?.onClick.RemoveListener(HandleRecoveryCancelPressed);
        }
        #endregion

        #region Ui state: No permissions Menu
        private void OnNoPermissionMenu()
        {
            m_initialMenu = false;
            m_noPermissionMenu = true;
            IsPaused = true;
            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(true);
        }
        #endregion

        #region Ui state: Initial Menu

        private void OnInitialMenu()
        {
            if (ShouldBypassInitialMenu())
            {
                OnPauseMenu(false);
                return;
            }

            m_initialMenu = true;
            m_noPermissionMenu = false;
            IsPaused = true;
            m_initialPanel.SetActive(true);
            m_noPermissionPanel.SetActive(false);
        }

        private void InitialMenuUpdate()
        {
            if (InputManager.IsButtonADownOrPinchStarted())
            {
                OnPauseMenu(false);
            }
        }

        private void OnPauseMenu(bool visible)
        {
            m_initialMenu = false;
            m_noPermissionMenu = false;
            IsPaused = visible;

            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(false);

            OnPause?.Invoke(visible);
        }
        #endregion

        #region Ui state: detection information
        // Retained for the disabled Sentis runner and future one-shot fallback.
        // The compact slice-3 panel intentionally presents only the active remote
        // naming flow and companion state.
        private bool m_anchorTracked = true;

        /// Compatibility callback for the disabled Sentis runner. State is retained
        /// for the future fallback slice but is not rendered in the remote-only panel.
        public void SetAnchorTracked(bool tracked)
        {
            if (m_anchorTracked == tracked)
            {
                return;
            }
            m_anchorTracked = tracked;
            UpdateLabelInformation();
        }

        // Retained for the disabled Sentis runner and future one-shot fallback.
        private bool m_modelLoadFailed;

        /// Compatibility callback for the disabled Sentis runner. State is retained
        /// for the future fallback slice but is not rendered in the remote-only panel.
        public void SetModelLoadFailed(bool failed)
        {
            if (m_modelLoadFailed == failed)
            {
                return;
            }
            m_modelLoadFailed = failed;
            UpdateLabelInformation();
        }

        public void SetCompanionReadiness(CompanionReadiness state)
        {
            m_companionReadiness = state ?? CompanionReadiness.Unavailable();
            UpdateLabelInformation();
        }

        public void SetRemoteRecognitionPresentation(string presentation)
        {
            if (m_remoteRecognitionPresentation == presentation)
            {
                return;
            }

            m_remoteRecognitionPresentation = presentation;
            UpdateLabelInformation();
        }

        public void SetSpatialAnchorRestorationState(SpatialAnchorRestorationState state)
        {
            m_restorationState = state;
            if (state == SpatialAnchorRestorationState.Restoring)
            {
                m_restoreStatusVisibleUntil = Time.unscaledTime + RestoreStatusMinimumSeconds;
            }

            m_recoveryPresentation = SpatialAnchorRecoveryPresentationPolicy.Evaluate(state);
            if (!m_recoveryPresentation.ShowsRecoveryPanel &&
                (!m_recoveryConfirmationOpen ||
                 (state != SpatialAnchorRestorationState.Restoring &&
                 state != SpatialAnchorRestorationState.Resetting)))
            {
                CloseRecoveryConfirmation();
            }

            EnsureRestorationPresentationIsReachable();
            UpdateRecoveryPanel();
            UpdateLabelInformation();
        }

        private void UpdateLabelInformation()
        {
            if (!string.IsNullOrEmpty(m_recoveryPresentation.StatusText))
            {
                m_labelInformation.text = m_recoveryPresentation.StatusText;
                return;
            }

            if (m_restoreStatusVisibleUntil > Time.unscaledTime)
            {
                m_labelInformation.text = SpatialAnchorRecoveryPresentationPolicy.RestoringStatus;
                return;
            }

            // The serialized information RectTransform has room for two lines.
            // Remote status/result must be first so it can never be clipped.
            m_labelInformation.text = RemoteNamingPresentation.Compose(
                m_companionReadiness,
                m_remoteRecognitionPresentation);
        }

        private bool ShouldBypassInitialMenu()
        {
            return m_restorationState != SpatialAnchorRestorationState.NoSavedSpace;
        }

        private void EnsureRestorationPresentationIsReachable()
        {
            if (m_initialMenu && ShouldBypassInitialMenu())
            {
                OnPauseMenu(false);
            }
        }

        private void UpdateRecoveryPanel()
        {
            var showPanel = m_recoveryPresentation.ShowsRecoveryPanel;
            if (m_recoveryPanel != null)
            {
                m_recoveryPanel.SetActive(showPanel);
            }

            if (m_recoveryActionButton != null)
            {
                m_recoveryActionButton.gameObject.SetActive(showPanel && !m_recoveryConfirmationOpen);
                m_recoveryActionButton.interactable = showPanel && !m_recoveryResetRequested;
            }

            if (m_recoveryConfirmationPanel != null)
            {
                m_recoveryConfirmationPanel.SetActive(showPanel && m_recoveryConfirmationOpen);
            }

            if (m_recoveryConfirmButton != null)
            {
                m_recoveryConfirmButton.interactable = !m_recoveryResetRequested;
            }

            if (m_recoveryCancelButton != null)
            {
                m_recoveryCancelButton.interactable = !m_recoveryResetRequested;
            }
        }

        private void HandleRecoveryActionPressed()
        {
            if (!m_recoveryPresentation.ShowsRecoveryPanel || m_recoveryConfirmationOpen || m_recoveryResetRequested)
            {
                return;
            }

            m_recoveryConfirmationOpen = true;
            IsPaused = true;
            UpdateRecoveryPanel();
        }

        private void HandleRecoveryConfirmPressed()
        {
            if (!m_recoveryConfirmationOpen || m_recoveryResetRequested)
            {
                return;
            }

            m_recoveryResetRequested = true;
            UpdateRecoveryPanel();
            ResetSavedSpaceConfirmed?.Invoke();
        }

        private void HandleRecoveryCancelPressed()
        {
            if (!m_recoveryConfirmationOpen || m_recoveryResetRequested)
            {
                return;
            }

            CloseRecoveryConfirmation();
            UpdateRecoveryPanel();
        }

        private void CloseRecoveryConfirmation()
        {
            m_recoveryConfirmationOpen = false;
            m_recoveryResetRequested = false;

            if (!m_initialMenu && !m_noPermissionMenu)
            {
                IsPaused = false;
            }
        }

        public void OnObjectsDetected(int objects)
        {
            UpdateLabelInformation();
        }
        #endregion
    }
}
