namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class RemoteNamingSession
    {
        public const string IdentifyingPresentation = "Identifying...";
        public const float SuccessPresentationSeconds = 3f;
        public const float FailurePresentationSeconds = 3f;

        private enum SessionState
        {
            Idle,
            Identifying,
            ShowingSuccess,
            ShowingFailure,
        }

        private SessionState m_state;
        private float m_presentationExpiresAt;

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

            if (!response.Found)
            {
                TryFail(
                    response.RequestId,
                    RemoteNamingFailureKind.NotFound,
                    realtimeSinceStartup);
                return false;
            }

            ActiveRequestId = null;
            m_state = SessionState.ShowingSuccess;
            PresentationText = response.Name;
            m_presentationExpiresAt = realtimeSinceStartup + SuccessPresentationSeconds;
            return true;
        }

        // Ticket 08: the on-headset YOLO fallback has no RemoteNameResponse to
        // match against — it never talks to the Mac — so this mirrors
        // TryAccept's success tail against the requestId alone, and composes
        // the fallback marker into the transient status text itself so the
        // panel reads as lower-capability the same way the committed label
        // does (see RemoteSpatialLabelLifecycle.PresentationFor).
        public bool TryAcceptFallback(string requestId, string className, float realtimeSinceStartup)
        {
            if (!IsRequestActive ||
                ActiveRequestId != requestId ||
                !RemoteNameProtocol.IsValidName(className))
            {
                return false;
            }

            ActiveRequestId = null;
            m_state = SessionState.ShowingSuccess;
            PresentationText = className + RemoteSpatialLabelLifecycle.FallbackMarker;
            m_presentationExpiresAt = realtimeSinceStartup + SuccessPresentationSeconds;
            return true;
        }

        public bool TryCancel(string requestId)
        {
            if (!IsRequestActive || ActiveRequestId != requestId)
            {
                return false;
            }

            ResetToIdle();
            return true;
        }

        public bool TryFail(string requestId)
        {
            return TryCancel(requestId);
        }

        public bool TryFail(
            string requestId,
            RemoteNamingFailureKind kind,
            float realtimeSinceStartup)
        {
            if (!IsRequestActive ||
                ActiveRequestId != requestId ||
                !RemoteNamingFailurePresentation.TryGetText(kind, out var presentation))
            {
                return false;
            }

            m_state = SessionState.ShowingFailure;
            ActiveRequestId = null;
            PresentationText = presentation;
            m_presentationExpiresAt = realtimeSinceStartup + FailurePresentationSeconds;
            return true;
        }

        public void Tick(float realtimeSinceStartup)
        {
            if ((m_state == SessionState.ShowingSuccess ||
                 m_state == SessionState.ShowingFailure) &&
                realtimeSinceStartup >= m_presentationExpiresAt)
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
            m_presentationExpiresAt = 0f;
        }
    }
}
