using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum CompanionReadinessKind
    {
        Loading,
        Ready,
        Unavailable,
        Misconfigured,
    }

    public sealed class CompanionReadiness
    {
        public CompanionReadinessKind Kind { get; }
        public string Message { get; }
        public bool IsReady => Kind == CompanionReadinessKind.Ready;

        private CompanionReadiness(CompanionReadinessKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public static CompanionReadiness Loading()
        {
            return new CompanionReadiness(CompanionReadinessKind.Loading, "Mac: loading");
        }

        public static CompanionReadiness Ready()
        {
            return new CompanionReadiness(CompanionReadinessKind.Ready, "Mac: ready");
        }

        public static CompanionReadiness Unavailable()
        {
            return new CompanionReadiness(CompanionReadinessKind.Unavailable, "Mac: unavailable - check the companion");
        }

        public static CompanionReadiness AuthenticationFailed()
        {
            return new CompanionReadiness(CompanionReadinessKind.Misconfigured, "Mac: authentication failed - update the configuration");
        }

        public static CompanionReadiness ProtocolMismatch()
        {
            return new CompanionReadiness(CompanionReadinessKind.Misconfigured, "Mac: protocol mismatch - update the app or companion");
        }

        public static CompanionReadiness InvalidConfiguration()
        {
            return new CompanionReadiness(CompanionReadinessKind.Misconfigured, "Mac: configuration invalid or missing - provision remote-recognition.json");
        }
    }

    public static class CompanionHealthProtocol
    {
        public static CompanionReadiness Evaluate(long responseCode, bool transportFailed, string responseBody)
        {
            if (transportFailed)
            {
                return CompanionReadiness.Unavailable();
            }

            if (responseCode == 401)
            {
                return CompanionReadiness.AuthenticationFailed();
            }

            if (responseCode < 200 || responseCode >= 300)
            {
                return CompanionReadiness.Unavailable();
            }

            CompanionHealthPayload payload;
            try
            {
                payload = JsonUtility.FromJson<CompanionHealthPayload>(responseBody);
            }
            catch (ArgumentException)
            {
                return CompanionReadiness.Unavailable();
            }

            if (payload == null)
            {
                return CompanionReadiness.Unavailable();
            }

            if (payload.protocol_version != RemoteRecognitionConfig.SupportedProtocolVersion)
            {
                return CompanionReadiness.ProtocolMismatch();
            }

            switch (payload.status)
            {
                case "loading":
                    return CompanionReadiness.Loading();
                case "ready":
                    return CompanionReadiness.Ready();
                case "error":
                default:
                    return CompanionReadiness.Unavailable();
            }
        }

        [Serializable]
        private sealed class CompanionHealthPayload
        {
            public string protocol_version;
            public string status;
            public string model;
        }
    }

    public sealed class CompanionReadinessStateMachine
    {
        private bool m_hasBegunProbe;

        public CompanionReadiness Current { get; private set; } = CompanionReadiness.Loading();

        public CompanionReadiness BeginProbe()
        {
            if (!m_hasBegunProbe)
            {
                m_hasBegunProbe = true;
                Current = CompanionReadiness.Loading();
            }

            return Current;
        }

        public CompanionReadiness Apply(CompanionReadiness observation)
        {
            Current = observation ?? CompanionReadiness.Unavailable();
            return Current;
        }
    }
}
