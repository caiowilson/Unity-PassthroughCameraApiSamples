// Copyright (c) Meta Platforms, Inc. and affiliates.

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

        public bool IsInputActive { get; set; } = false;

        public UnityEvent<bool> OnPause;

        public bool IsCompanionReady => m_companionReadiness.IsReady;

        private bool m_initialMenu;
        private CompanionReadiness m_companionReadiness = CompanionReadiness.Loading();
        private string m_remoteRecognitionPresentation;

        // pause menu
        public bool IsPaused { get; private set; } = true;

        #region Unity Functions
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
        #endregion

        #region Ui state: No permissions Menu
        private void OnNoPermissionMenu()
        {
            m_initialMenu = false;
            IsPaused = true;
            m_initialPanel.SetActive(false);
            m_noPermissionPanel.SetActive(true);
        }
        #endregion

        #region Ui state: Initial Menu

        private void OnInitialMenu()
        {
            m_initialMenu = true;
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

        private void UpdateLabelInformation()
        {
            // The serialized information RectTransform has room for two lines.
            // Remote status/result must be first so it can never be clipped.
            m_labelInformation.text = RemoteNamingPresentation.Compose(
                m_companionReadiness,
                m_remoteRecognitionPresentation);
        }

        public void OnObjectsDetected(int objects)
        {
            UpdateLabelInformation();
        }
        #endregion
    }
}
