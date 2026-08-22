namespace PassthroughCameraSamples.MultiObjectDetection
{
    public struct SpatialAnchorRecoveryPresentation
    {
        public SpatialAnchorRecoveryPresentation(string statusText, bool showsRecoveryPanel)
        {
            StatusText = statusText;
            ShowsRecoveryPanel = showsRecoveryPanel;
        }

        public string StatusText { get; }

        public bool ShowsRecoveryPanel { get; }
    }

    public static class SpatialAnchorRecoveryPresentationPolicy
    {
        public const string RestoringStatus = "Restoring saved space...";
        public const string UnavailableStatus = "Saved space unavailable";

        public static SpatialAnchorRecoveryPresentation Evaluate(SpatialAnchorRestorationState state)
        {
            switch (state)
            {
                case SpatialAnchorRestorationState.Restoring:
                    return new SpatialAnchorRecoveryPresentation(RestoringStatus, false);

                case SpatialAnchorRestorationState.Unavailable:
                    return new SpatialAnchorRecoveryPresentation(UnavailableStatus, true);

                default:
                    return default;
            }
        }
    }
}
