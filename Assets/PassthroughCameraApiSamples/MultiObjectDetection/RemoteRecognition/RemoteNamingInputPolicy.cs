namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class RemoteNamingInputPolicy
    {
        public static bool CanStart(
            bool cameraIsPlaying,
            bool appPaused,
            bool wasPausedLastFrame)
        {
            return cameraIsPlaying && !appPaused && !wasPausedLastFrame;
        }

        public static bool CanStartResolvedAim(
            bool cameraIsPlaying,
            bool appPaused,
            bool wasPausedLastFrame,
            bool anchorTracked,
            bool hasResolvedAim)
        {
            return anchorTracked &&
                   hasResolvedAim &&
                   CanStart(cameraIsPlaying, appPaused, wasPausedLastFrame);
        }

        public static bool ShouldShowAimReticle(
            bool appStarted,
            bool cameraIsPlaying,
            bool appPaused,
            bool wasPausedLastFrame,
            bool companionReady)
        {
            return appStarted &&
                   companionReady &&
                   CanStart(cameraIsPlaying, appPaused, wasPausedLastFrame);
        }
    }
}
