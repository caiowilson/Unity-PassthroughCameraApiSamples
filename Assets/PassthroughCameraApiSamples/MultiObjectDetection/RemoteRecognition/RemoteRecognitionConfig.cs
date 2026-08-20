using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum RemoteRecognitionConfigError
    {
        None,
        InvalidJson,
        UnsupportedProtocol,
        InvalidMacBaseUrl,
        InvalidBearerToken,
        InvalidRequestTimeout,
    }

    public sealed class RemoteRecognitionConfig
    {
        public const string FileName = "remote-recognition.json";
        public const string SupportedProtocolVersion = "1";
        public const int RequiredRequestTimeoutSeconds = 8;

        public string ProtocolVersion { get; }
        public string MacBaseUrl { get; }
        public string BearerToken { get; }
        public int RequestTimeoutSeconds { get; }

        private RemoteRecognitionConfig(string protocolVersion, string macBaseUrl, string bearerToken, int requestTimeoutSeconds)
        {
            ProtocolVersion = protocolVersion;
            MacBaseUrl = macBaseUrl;
            BearerToken = bearerToken;
            RequestTimeoutSeconds = requestTimeoutSeconds;
        }

        public static bool TryParse(string json, out RemoteRecognitionConfig config, out RemoteRecognitionConfigError error)
        {
            config = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = RemoteRecognitionConfigError.InvalidJson;
                return false;
            }

            RemoteRecognitionConfigPayload payload;
            try
            {
                payload = JsonUtility.FromJson<RemoteRecognitionConfigPayload>(json);
            }
            catch (ArgumentException)
            {
                error = RemoteRecognitionConfigError.InvalidJson;
                return false;
            }

            if (payload == null)
            {
                error = RemoteRecognitionConfigError.InvalidJson;
                return false;
            }

            if (payload.protocol_version != SupportedProtocolVersion)
            {
                error = RemoteRecognitionConfigError.UnsupportedProtocol;
                return false;
            }

            if (!TryNormalizeMacBaseUrl(payload.mac_base_url, out var macBaseUrl))
            {
                error = RemoteRecognitionConfigError.InvalidMacBaseUrl;
                return false;
            }

            if (string.IsNullOrEmpty(payload.bearer_token) ||
                payload.bearer_token.IndexOf('\r') >= 0 ||
                payload.bearer_token.IndexOf('\n') >= 0)
            {
                error = RemoteRecognitionConfigError.InvalidBearerToken;
                return false;
            }

            if (payload.request_timeout_seconds != RequiredRequestTimeoutSeconds)
            {
                error = RemoteRecognitionConfigError.InvalidRequestTimeout;
                return false;
            }

            config = new RemoteRecognitionConfig(
                payload.protocol_version,
                macBaseUrl,
                payload.bearer_token,
                payload.request_timeout_seconds);
            error = RemoteRecognitionConfigError.None;
            return true;
        }

        private static bool TryNormalizeMacBaseUrl(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                uri.AbsolutePath != "/")
            {
                return false;
            }

            normalized = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        [Serializable]
        private sealed class RemoteRecognitionConfigPayload
        {
            public string protocol_version;
            public string mac_base_url;
            public string bearer_token;
            public int request_timeout_seconds;
        }
    }
}
