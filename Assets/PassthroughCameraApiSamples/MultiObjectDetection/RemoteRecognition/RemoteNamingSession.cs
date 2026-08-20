namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class RemoteNamingSession
    {
        public const string IdentifyingPresentation = "Identifying...";
        public const float SuccessPresentationSeconds = 3f;

        private enum SessionState
        {
            Idle,
            Identifying,
            ShowingSuccess,
        }

        private SessionState m_state;
        private float m_successExpiresAt;

        public bool IsRequestActive => m_state == SessionState.Identifying;
        public string ActiveRequestId { get; private set; }
        public string PresentationText { get; private set; }

        public bool TryBegin(bool companionReady, bool appPaused, string requestId)
        {
            if (!companionReady ||
                appPaused ||
                IsRequestActive ||
                !RemoteNameProtocol.IsValidRequestId(requestId))
            {
                return false;
            }

            m_state = SessionState.Identifying;
            ActiveRequestId = requestId;
            PresentationText = IdentifyingPresentation;
            return true;
        }

        public bool TryAccept(RemoteNameResponse response, float realtimeSinceStartup)
        {
            if (!IsMatchingActiveResponse(response))
            {
                return false;
            }

            if (response.Found && !RemoteNameProtocol.IsValidName(response.Name))
            {
                return false;
            }

            ActiveRequestId = null;
            if (!response.Found)
            {
                ResetToIdle();
                return false;
            }

            m_state = SessionState.ShowingSuccess;
            PresentationText = response.Name;
            m_successExpiresAt = realtimeSinceStartup + SuccessPresentationSeconds;
            return true;
        }

        public bool TryFail(string requestId)
        {
            if (!IsRequestActive || ActiveRequestId != requestId)
            {
                return false;
            }

            ResetToIdle();
            return true;
        }

        public void Tick(float realtimeSinceStartup)
        {
            if (m_state == SessionState.ShowingSuccess && realtimeSinceStartup >= m_successExpiresAt)
            {
                ResetToIdle();
            }
        }

        private bool IsMatchingActiveResponse(RemoteNameResponse response)
        {
            return IsRequestActive &&
                   response != null &&
                   response.ProtocolVersion == RemoteRecognitionConfig.SupportedProtocolVersion &&
                   response.RequestId == ActiveRequestId;
        }

        private void ResetToIdle()
        {
            m_state = SessionState.Idle;
            ActiveRequestId = null;
            PresentationText = null;
            m_successExpiresAt = 0f;
        }
    }
}
