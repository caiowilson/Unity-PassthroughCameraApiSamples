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
        private Guid? m_activeOperationId;
        private string m_lastPublishedPresentation;

        public bool IsReady =>
            isActiveAndEnabled &&
            m_readinessController != null &&
            m_readinessController.IsReady &&
            m_readinessController.CurrentConfig != null;

        private void Update()
        {
            m_session.Tick(Time.realtimeSinceStartup);
            PublishPresentation();
        }

        public bool TryStart(Texture frame)
        {
            if (!isActiveAndEnabled ||
                frame == null ||
                m_readinessController == null ||
                m_menuManager == null ||
                !m_readinessController.IsReady ||
                m_readinessController.CurrentConfig == null)
            {
                return false;
            }

            if (m_uiInference == null || !m_uiInference.TryResolveCenterPoint(out var point))
            {
                return false;
            }

            var operationId = Guid.NewGuid();
            var requestId = operationId.ToString("N");
            if (!m_session.TryBegin(
                    m_readinessController.IsReady,
                    m_menuManager.IsPaused,
                    requestId))
            {
                return false;
            }

            m_activeOperationId = operationId;
            PublishPresentation();

            if (!m_uiInference.CreatePendingRemoteLabel(operationId, point))
            {
                FailOperation(requestId, operationId);
                return false;
            }

            if (!TryCaptureJpeg(frame, out var jpeg) || jpeg.Length > MaximumJpegBytes)
            {
                FailOperation(requestId, operationId);
                return false;
            }

            StartCoroutine(SendNameRequest(
                m_readinessController.CurrentConfig,
                requestId,
                operationId,
                jpeg));
            return true;
        }

        public bool TryCancelPending()
        {
            if (!m_session.IsRequestActive)
            {
                return false;
            }

            var requestId = m_session.ActiveRequestId;
            var operationId = m_activeOperationId;
            var canceled = m_session.TryFail(requestId);
            if (operationId.HasValue)
            {
                m_uiInference?.RemoveRemoteLabel(operationId.Value);
                if (m_activeOperationId == operationId)
                {
                    m_activeOperationId = null;
                }
            }
            PublishPresentation();
            return canceled;
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
                request.timeout = config.RequestTimeoutSeconds;

                yield return request.SendWebRequest();

                var succeeded = request.result == UnityWebRequest.Result.Success &&
                                RemoteNameProtocol.IsSuccessfulHttpStatus(request.responseCode);
                if (succeeded &&
                    RemoteNameProtocol.TryParse(request.downloadHandler.text, out var response) &&
                    m_session.IsRequestActive &&
                    m_session.ActiveRequestId == requestId &&
                    response.RequestId == requestId)
                {
                    if (response.Found)
                    {
                        if (!m_uiInference.CommitRemoteLabel(operationId, response.Name) ||
                            !m_session.TryAccept(response, Time.realtimeSinceStartup))
                        {
                            FailOperation(requestId, operationId);
                            yield break;
                        }

                        if (m_activeOperationId == operationId)
                        {
                            m_activeOperationId = null;
                        }
                        PublishPresentation();
                        yield break;
                    }

                    m_session.TryAccept(response, Time.realtimeSinceStartup);
                    if (m_session.IsRequestActive)
                    {
                        FailOperation(requestId, operationId);
                        yield break;
                    }

                    m_uiInference.RemoveRemoteLabel(operationId);
                    if (m_activeOperationId == operationId)
                    {
                        m_activeOperationId = null;
                    }
                    PublishPresentation();
                    yield break;
                }
            }

            FailOperation(requestId, operationId);
        }

        private void FailOperation(string requestId, Guid operationId)
        {
            m_uiInference?.RemoveRemoteLabel(operationId);
            m_session.TryFail(requestId);
            if (m_activeOperationId == operationId)
            {
                m_activeOperationId = null;
            }
            PublishPresentation();
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
                    Destroy(cpuTexture);
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
}
