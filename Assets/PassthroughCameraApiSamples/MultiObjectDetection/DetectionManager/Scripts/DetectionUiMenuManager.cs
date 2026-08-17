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

        private bool m_initialMenu;

        // start menu
        private int m_objectsDetected = 0;

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
        // Object Tagger slice 2 Task 5: anchor-tracking state, surfaced here rather
        // than in new UI. Reusing the existing information label is the smallest
        // change that turns this project's most likely silent failure into
        // something a tester can see. Label presentation proper is slice 5.
        private bool m_anchorTracked = true;

        /// Called when the spatial-anchor guard in SentisInferenceRunManager changes
        /// verdict. While the anchor is untracked, inference still runs but every
        /// result is discarded, so the app looks alive and renders nothing. Without
        /// this the only symptom is absence.
        public void SetAnchorTracked(bool tracked)
        {
            if (m_anchorTracked == tracked)
            {
                return;
            }
            m_anchorTracked = tracked;
            UpdateLabelInformation();
        }

        // Object Tagger slice 3 Task 5 — model load failure, surfaced.
        private bool m_modelLoadFailed;

        /// A failed model load leaves the app running with passthrough working and
        /// nothing ever labelled — indistinguishable on device from an untracked anchor
        /// or a denied permission. This makes it say which one it is.
        public void SetModelLoadFailed(bool failed)
        {
            if (m_modelLoadFailed == failed)
            {
                return;
            }
            m_modelLoadFailed = failed;
            UpdateLabelInformation();
        }

        private void UpdateLabelInformation()
        {
            // The version string was hardcoded to "2.1.3" upstream. The pinned package
            // is com.unity.ai.inference 2.2.1, so that display was simply wrong. Corrected
            // rather than carried forward -- slice 2's gate re-answers what the app
            // displays, and recording a known-wrong string as observed evidence would
            // poison that answer. Logged as a deviation.
            var anchorLine = m_anchorTracked
                ? string.Empty
                : "\n<<< SPATIAL ANCHOR NOT TRACKED - detections are being discarded >>>";

            // Model-load failure outranks the anchor line: if the model never loaded,
            // the anchor state is irrelevant because no inference runs at all.
            var modelLine = m_modelLoadFailed
                ? "\n<<< MODEL FAILED TO LOAD - no inference is running >>>"
                : string.Empty;

            // Object Tagger slice 5 Task 5 Step 2: "Objects identified" removed.
            // Task 1 deleted DetectionManager.cs's OnObjectsIdentified UnityEvent
            // along with the marker-spawn feature it existed to report (correctly
            // removed, not adopted per the design spec). Nothing has called
            // OnObjectsIndentified below since, so this line was permanently
            // stuck at "Objects identified: 0" -- a live, always-visible panel
            // showing a fixed value reads as a real observation ("nothing has
            // ever been identified") when it is actually dead instrumentation.
            // Removed rather than fed with new counting, matching how this
            // project only surfaces state that is genuinely meaningful (see
            // m_anchorTracked/m_modelLoadFailed above).
            m_labelInformation.text =
                $"Unity Inference Engine version: 2.2.1\nAI model: Yolo\nDetecting objects: {m_objectsDetected}{modelLine}{anchorLine}";
        }

        public void OnObjectsDetected(int objects)
        {
            m_objectsDetected = objects;
            UpdateLabelInformation();
        }
        #endregion
    }
}
