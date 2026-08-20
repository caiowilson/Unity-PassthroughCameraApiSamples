namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class RemoteNamingPresentation
    {
        public const string IdleInputCue = "Press A or pinch to identify";

        public static string Compose(
            CompanionReadiness companionReadiness,
            string remotePresentation)
        {
            var readiness = companionReadiness ?? CompanionReadiness.Unavailable();
            if (!string.IsNullOrEmpty(remotePresentation))
            {
                return $"{remotePresentation}\n{readiness.Message}";
            }

            return readiness.IsReady
                ? $"{IdleInputCue}\n{readiness.Message}"
                : readiness.Message;
        }
    }
}
