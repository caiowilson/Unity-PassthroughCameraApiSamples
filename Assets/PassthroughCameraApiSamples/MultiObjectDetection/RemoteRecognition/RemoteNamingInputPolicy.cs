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
    }
}
