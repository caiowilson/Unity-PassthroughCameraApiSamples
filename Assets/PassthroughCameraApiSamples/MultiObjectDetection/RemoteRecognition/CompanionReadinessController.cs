using System;
using System.Collections;
using System.Collections.Generic;
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

        public CompanionReadiness Current => m_stateMachine.Current;
        public bool IsReady => Current.IsReady && CurrentConfig != null;

        // Ticket 08 (D3): replaces the prior sticky "observed Ready once" gate.
        // A valid configuration is enough to attempt naming — over the Mac
        // path when Ready, or the on-headset fallback otherwise — provided the
        // current state is not an actionable configuration error. This is what
        // lets A/pinch resolve a target and fall back even when the companion
        // has never once answered.
        internal bool CanAttemptEitherPath =>
            CurrentConfig != null && Current.Kind != CompanionReadinessKind.Misconfigured;
        public RemoteRecognitionConfig CurrentConfig { get; private set; }

        /// <summary>
        /// Applies only naming failures that immediately invalidate companion readiness.
        /// </summary>
        public void ReportNamingFailure(RemoteNamingFailureKind failure)
        {
            switch (failure)
            {
                case RemoteNamingFailureKind.Authentication:
                    Publish(CompanionReadiness.AuthenticationFailed());
                    break;
                case RemoteNamingFailureKind.Connectivity:
                case RemoteNamingFailureKind.ModelUnavailable:
                    Publish(CompanionReadiness.Unavailable());
                    break;
            }
        }

        private void Awake()
        {
            m_menuManager = GetComponent<DetectionUiMenuManager>();
        }

        private IEnumerator Start()
        {
            Debug.Log("[ObjectTagger] companion readiness controller started");

            if (!TryLoadConfiguration(out var config))
            {
                Debug.LogWarning("[ObjectTagger] companion configuration is missing or invalid");
                Publish(CompanionReadiness.InvalidConfiguration());
                yield break;
            }

            CurrentConfig = config;
            Debug.Log("[ObjectTagger] companion configuration loaded");
            Publish(m_stateMachine.BeginProbe());

            while (enabled)
            {
                var startedAt = Time.realtimeSinceStartup;

                using (var request = UnityWebRequest.Get($"{config.MacBaseUrl}/v1/health"))
                {
                    request.SetRequestHeader("Authorization", $"Bearer {config.BearerToken}");
                    request.timeout = RemoteRecognitionConfig.RequiredRequestTimeoutSeconds;

                    Debug.Log("[ObjectTagger] companion health probe started");
                    yield return request.SendWebRequest();

                    var transportFailed = request.result == UnityWebRequest.Result.ConnectionError ||
                                          request.result == UnityWebRequest.Result.DataProcessingError;
                    Debug.Log($"[ObjectTagger] companion health probe completed result={request.result} status={request.responseCode}");
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
            return RemoteRecognitionConfigFile.TryLoad(ConfigurationPaths(), out config);
        }

        private static IEnumerable<string> ConfigurationPaths()
        {
            yield return Path.Combine(Application.persistentDataPath, RemoteRecognitionConfig.FileName);

#if UNITY_ANDROID && !UNITY_EDITOR
            string appOwnedFilesDirectory = null;
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var filesDirectory = activity.Call<AndroidJavaObject>("getFilesDir"))
                {
                    appOwnedFilesDirectory = filesDirectory.Call<string>("getAbsolutePath");
                }
            }
            catch (Exception exception) when (exception is AndroidJavaException || exception is NullReferenceException)
            {
                // The normal Unity path remains available when Android context lookup fails.
            }

            if (!string.IsNullOrEmpty(appOwnedFilesDirectory))
            {
                yield return Path.Combine(appOwnedFilesDirectory, RemoteRecognitionConfig.FileName);
            }
#endif
        }

        private void Publish(CompanionReadiness observation)
        {
            var current = m_stateMachine.Apply(observation);
            m_menuManager?.SetCompanionReadiness(current);
        }
    }
}
