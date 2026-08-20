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

        private readonly RemoteNamingSession m_session = new RemoteNamingSession();
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

            var requestId = Guid.NewGuid().ToString("N");
            if (!m_session.TryBegin(
                    m_readinessController.IsReady,
                    m_menuManager.IsPaused,
                    requestId))
            {
                return false;
            }

            PublishPresentation();

            if (!TryCaptureJpeg(frame, out var jpeg) || jpeg.Length > MaximumJpegBytes)
            {
                m_session.TryFail(requestId);
                PublishPresentation();
                return false;
            }

            StartCoroutine(SendNameRequest(
                m_readinessController.CurrentConfig,
                requestId,
                jpeg));
            return true;
        }

        private IEnumerator SendNameRequest(
            RemoteRecognitionConfig config,
            string requestId,
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
                    m_session.TryAccept(response, Time.realtimeSinceStartup))
                {
                    PublishPresentation();
                    yield break;
                }
            }

            // A rejected, malformed, or failed response cannot leave the matching
            // operation busy forever. The request ID still owns this transition, so a
            // late operation cannot clear a newer one.
            m_session.TryFail(requestId);
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
