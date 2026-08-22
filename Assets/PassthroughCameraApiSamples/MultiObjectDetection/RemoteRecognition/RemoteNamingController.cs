using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class RemoteNamingController : MonoBehaviour
    {
        private const int MaximumJpegBytes = 1024 * 1024;

        [SerializeField] private CompanionReadinessController m_readinessController;
        [SerializeField] private DetectionUiMenuManager m_menuManager;
        [SerializeField] private SentisInferenceUiManager m_uiInference;

        private readonly RemoteNamingSession m_session = new RemoteNamingSession();
        private string m_activeRequestId;
        private Guid? m_activeOperationId;
        private UnityWebRequest m_liveRequest;
        private string m_lastPublishedPresentation;

        /// <summary>
        /// Whether the user may make a deliberate naming attempt. This stays true
        /// across naming-result readiness downgrades after health was once ready.
        /// </summary>
        public bool CanAttemptNaming =>
            isActiveAndEnabled &&
            m_readinessController != null &&
            m_readinessController.CanAttemptNaming;

        private void Update()
        {
            m_session.Tick(Time.realtimeSinceStartup);
            PublishPresentation();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                TryCancelPending();
            }
        }

        private void OnDisable()
        {
            TryCancelPending();
        }

        private void OnDestroy()
        {
            TryCancelPending();
        }

        public bool TryStart(Texture frame)
        {
            if (!HasStartPrerequisites(frame) ||
                m_uiInference == null ||
                !m_uiInference.TryResolveCenterPoint(out var point))
            {
                return false;
            }

            return StartAtResolvedPoint(frame, point);
        }

        public bool TryStartAtResolvedPoint(Texture frame, Vector3 point)
        {
            if (!HasStartPrerequisites(frame) || m_uiInference == null)
            {
                return false;
            }

            return StartAtResolvedPoint(frame, point);
        }

        private bool StartAtResolvedPoint(Texture frame, Vector3 point)
        {
            if (m_session.IsRequestActive)
            {
                return false;
            }

            var operationId = Guid.NewGuid();
            var requestId = operationId.ToString("N");
            if (!m_session.TryBegin(
                    m_readinessController.CanAttemptNaming,
                    m_menuManager.IsPaused,
                    requestId))
            {
                return false;
            }

            m_activeRequestId = requestId;
            m_activeOperationId = operationId;
            PublishPresentation();

            if (!m_uiInference.CreatePendingRemoteLabel(operationId, point))
            {
                TryTerminateOperation(
                    requestId,
                    operationId,
                    RemoteNamingFailureKind.InvalidResponse,
                    false);
                return false;
            }

            if (!TryCaptureJpeg(frame, out var jpeg))
            {
                return ContinueAfterCapture(requestId, operationId, null);
            }

            return ContinueAfterCapture(requestId, operationId, jpeg);
        }

        private bool ContinueAfterCapture(string requestId, Guid operationId, byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0 || jpeg.Length > MaximumJpegBytes)
            {
                TryTerminateOperation(
                    requestId,
                    operationId,
                    RemoteNamingFailureKind.InvalidPayload,
                    false);
                return false;
            }

            StartCoroutine(SendNameRequest(
                m_readinessController.CurrentConfig,
                requestId,
                operationId,
                jpeg));
            return true;
        }

        private bool HasStartPrerequisites(Texture frame)
        {
            return isActiveAndEnabled &&
                   frame != null &&
                   m_readinessController != null &&
                   m_menuManager != null &&
                   m_readinessController.CanAttemptNaming;
        }

        public bool TryCancelPending()
        {
            if (!m_activeOperationId.HasValue || string.IsNullOrEmpty(m_activeRequestId))
            {
                return false;
            }

            return TryTerminateOperation(
                m_activeRequestId,
                m_activeOperationId.Value,
                RemoteNamingFailureKind.Canceled,
                true);
        }

        private IEnumerator SendNameRequest(
            RemoteRecognitionConfig config,
            string requestId,
            Guid operationId,
            byte[] jpeg)
        {
            var sections = new List<IMultipartFormSection>(2)
            {
                new MultipartFormDataSection("request_id", requestId),
                new MultipartFormFileSection("image", jpeg, "capture.jpg", "image/jpeg"),
            };

            using (var request = UnityWebRequest.Post($"{config.MacBaseUrl}/v1/name", sections))
            {
                request.SetRequestHeader("Authorization", $"Bearer {config.BearerToken}");
                // Unity's integer timer can complete just before its configured boundary.
                // Keep it disabled and enforce the exact deadline below.
                request.timeout = 0;

                if (!TryAttachLiveRequest(requestId, operationId, request))
                {
                    yield break;
                }

                var startedAt = Time.realtimeSinceStartupAsDouble;
                var deadlineAt = startedAt + config.RequestTimeoutSeconds;
                var operation = request.SendWebRequest();
                while (!operation.isDone && Time.realtimeSinceStartupAsDouble < deadlineAt)
                {
                    yield return null;
                }

                var elapsedSeconds = Time.realtimeSinceStartupAsDouble - startedAt;
                if (!operation.isDone)
                {
                    TryTerminateOperation(
                        requestId,
                        operationId,
                        RemoteNamingFailureKind.Timeout,
                        true);
                    yield break;
                }

                if (!TryDetachLiveRequest(requestId, operationId, request))
                {
                    yield break;
                }

                var transportFailure = RemoteNamingTransportClassifier.Classify(
                    request.result,
                    request.responseCode,
                    elapsedSeconds,
                    config.RequestTimeoutSeconds);
                if (transportFailure != RemoteNamingFailureKind.None)
                {
                    TryTerminateOperation(
                        requestId,
                        operationId,
                        transportFailure,
                        false);
                    yield break;
                }

                if (!RemoteNameProtocol.IsSuccessfulHttpStatus(request.responseCode))
                {
                    var parsedError = RemoteNameProtocol.TryParseError(
                        request.responseCode,
                        request.downloadHandler.text,
                        requestId,
                        out var failure);
                    TryTerminateOperation(
                        requestId,
                        operationId,
                        parsedError ? failure : RemoteNamingFailureKind.InvalidResponse,
                        false);
                    yield break;
                }

                if (!RemoteNameProtocol.TryParse(request.downloadHandler.text, out var response) ||
                    response.RequestId != requestId)
                {
                    TryTerminateOperation(
                        requestId,
                        operationId,
                        RemoteNamingFailureKind.InvalidResponse,
                        false);
                    yield break;
                }

                if (!response.Found)
                {
                    TryTerminateOperation(
                        requestId,
                        operationId,
                        RemoteNamingFailureKind.NotFound,
                        false);
                    yield break;
                }

                if (!IsActiveTuple(requestId, operationId))
                {
                    yield break;
                }

                if (!m_uiInference.CommitRemoteLabel(operationId, response.Name) ||
                    !m_session.TryAccept(response, Time.realtimeSinceStartup))
                {
                    TryTerminateOperation(
                        requestId,
                        operationId,
                        RemoteNamingFailureKind.InvalidResponse,
                        false);
                    yield break;
                }

                m_activeRequestId = null;
                m_activeOperationId = null;
                PublishPresentation();
            }
        }

        private bool TryAttachLiveRequest(
            string requestId,
            Guid operationId,
            UnityWebRequest request)
        {
            if (!IsActiveTuple(requestId, operationId) ||
                request == null ||
                m_liveRequest != null)
            {
                return false;
            }

            m_liveRequest = request;
            return true;
        }

        private bool TryDetachLiveRequest(
            string requestId,
            Guid operationId,
            UnityWebRequest request)
        {
            if (!IsActiveTuple(requestId, operationId) ||
                !ReferenceEquals(m_liveRequest, request))
            {
                return false;
            }

            m_liveRequest = null;
            return true;
        }

        private bool IsActiveTuple(string requestId, Guid operationId)
        {
            return m_session.IsRequestActive &&
                   m_session.ActiveRequestId == requestId &&
                   m_activeRequestId == requestId &&
                   m_activeOperationId == operationId;
        }

        private bool TryTerminateOperation(
            string requestId,
            Guid operationId,
            RemoteNamingFailureKind failure,
            bool abortTransport)
        {
            if (!IsActiveTuple(requestId, operationId))
            {
                return false;
            }

            var transitioned = failure == RemoteNamingFailureKind.Canceled
                ? m_session.TryCancel(requestId)
                : m_session.TryFail(requestId, failure, Time.realtimeSinceStartup);
            if (!transitioned)
            {
                return false;
            }

            var request = m_liveRequest;
            m_activeRequestId = null;
            m_activeOperationId = null;
            m_liveRequest = null;
            m_uiInference?.RemoveRemoteLabel(operationId);
            PublishPresentation();
            m_readinessController?.ReportNamingFailure(failure);

            if (abortTransport && request != null)
            {
                request.Abort();
                request.Dispose();
            }

            return true;
        }

        private static bool TryCaptureJpeg(Texture frame, out byte[] jpeg)
        {
            jpeg = null;
            if (!RemoteImageCrop.TryCreatePlan(frame.width, frame.height, out var plan))
            {
                return false;
            }

            RenderTexture target = null;
            Texture2D cpuTexture = null;
            var previousActive = RenderTexture.active;

            try
            {
                target = RenderTexture.GetTemporary(
                    plan.OutputSize.x,
                    plan.OutputSize.y,
                    0,
                    RenderTextureFormat.ARGB32);

                var uvScale = new Vector2(
                    (float)plan.SourceRect.width / frame.width,
                    (float)plan.SourceRect.height / frame.height);
                var uvOffset = new Vector2(
                    (float)plan.SourceRect.x / frame.width,
                    (float)plan.SourceRect.y / frame.height);

                Graphics.Blit(frame, target, uvScale, uvOffset);
                RenderTexture.active = target;

                cpuTexture = new Texture2D(
                    plan.OutputSize.x,
                    plan.OutputSize.y,
                    TextureFormat.RGB24,
                    false);
                cpuTexture.ReadPixels(
                    new Rect(0, 0, plan.OutputSize.x, plan.OutputSize.y),
                    0,
                    0,
                    false);
                cpuTexture.Apply(false, false);
                jpeg = cpuTexture.EncodeToJPG(plan.JpegQuality);
                return jpeg != null && jpeg.Length > 0;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ObjectTagger] remote image capture failed ({exception.GetType().Name})");
                jpeg = null;
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (target != null)
                {
                    RenderTexture.ReleaseTemporary(target);
                }
                if (cpuTexture != null)
                {
                    if (Application.isPlaying)
                    {
                        Destroy(cpuTexture);
                    }
                    else
                    {
                        DestroyImmediate(cpuTexture);
                    }
                }
            }
        }

        private void PublishPresentation()
        {
            if (m_menuManager == null || m_lastPublishedPresentation == m_session.PresentationText)
            {
                return;
            }

            m_lastPublishedPresentation = m_session.PresentationText;
            m_menuManager.SetRemoteRecognitionPresentation(m_lastPublishedPresentation);
        }
    }

    /// <summary>
    /// Arbitrates the request deadline and Unity transport outcome without inspecting free-form error text.
    /// </summary>
    public static class RemoteNamingTransportClassifier
    {
        /// <summary>
        /// Makes the observed deadline authoritative, then classifies earlier transport completion.
        /// </summary>
        public static RemoteNamingFailureKind Classify(
            UnityWebRequest.Result result,
            long statusCode,
            double elapsedSeconds,
            double deadlineSeconds)
        {
            if (elapsedSeconds >= deadlineSeconds)
            {
                return RemoteNamingFailureKind.Timeout;
            }

            if (result == UnityWebRequest.Result.DataProcessingError)
            {
                return RemoteNamingFailureKind.InvalidResponse;
            }

            if (result != UnityWebRequest.Result.ConnectionError)
            {
                return RemoteNamingFailureKind.None;
            }

            if (statusCode != 0)
            {
                return RemoteNamingFailureKind.InvalidResponse;
            }

            return RemoteNamingFailureKind.Connectivity;
        }
    }
}
