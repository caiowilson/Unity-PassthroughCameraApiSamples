using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class CompanionReadinessController : MonoBehaviour
    {
        private const float PollIntervalSeconds = 5f;

        private readonly CompanionReadinessStateMachine m_stateMachine = new CompanionReadinessStateMachine();
        private DetectionUiMenuManager m_menuManager;

        private string ConfigPath =>
            Path.Combine(Application.persistentDataPath, RemoteRecognitionConfig.FileName);

        private void Awake()
        {
            m_menuManager = GetComponent<DetectionUiMenuManager>();
        }

        private IEnumerator Start()
        {
            if (!TryLoadConfiguration(out var config))
            {
                Publish(CompanionReadiness.InvalidConfiguration());
                yield break;
            }

            Publish(m_stateMachine.BeginProbe());

            while (enabled)
            {
                var startedAt = Time.realtimeSinceStartup;

                using (var request = UnityWebRequest.Get($"{config.MacBaseUrl}/v1/health"))
                {
                    request.SetRequestHeader("Authorization", $"Bearer {config.BearerToken}");
                    request.timeout = RemoteRecognitionConfig.RequiredRequestTimeoutSeconds;

                    yield return request.SendWebRequest();

                    var transportFailed = request.result == UnityWebRequest.Result.ConnectionError ||
                                          request.result == UnityWebRequest.Result.DataProcessingError;
                    Publish(CompanionHealthProtocol.Evaluate(request.responseCode, transportFailed, request.downloadHandler.text));
                }

                var remainingDelay = PollIntervalSeconds - (Time.realtimeSinceStartup - startedAt);
                if (remainingDelay > 0f)
                {
                    yield return new WaitForSecondsRealtime(remainingDelay);
                }
            }
        }

        private bool TryLoadConfiguration(out RemoteRecognitionConfig config)
        {
            config = null;
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    return false;
                }

                return RemoteRecognitionConfig.TryParse(File.ReadAllText(ConfigPath), out config, out _);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void Publish(CompanionReadiness observation)
        {
            m_menuManager?.SetCompanionReadiness(m_stateMachine.Apply(observation));
        }
    }
}
