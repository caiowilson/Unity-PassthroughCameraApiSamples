namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum RemoteNamingFailureKind
    {
        None,
        Canceled,
        Timeout,
        Busy,
        NotFound,
        Authentication,
        InvalidPayload,
        InvalidResponse,
        ModelUnavailable,
        Connectivity,
    }

    public static class RemoteNamingFailurePresentation
    {
        public static bool TryGetText(RemoteNamingFailureKind kind, out string text)
        {
            switch (kind)
            {
                case RemoteNamingFailureKind.Timeout:
                    text = "Timed out — try again";
                    return true;
                case RemoteNamingFailureKind.Busy:
                    text = "Mac busy — try again";
                    return true;
                case RemoteNamingFailureKind.NotFound:
                    text = "No object found — try again";
                    return true;
                case RemoteNamingFailureKind.Authentication:
                    text = "Authentication failed — update config";
                    return true;
                case RemoteNamingFailureKind.InvalidPayload:
                case RemoteNamingFailureKind.InvalidResponse:
                    text = "Couldn’t identify — try again";
                    return true;
                case RemoteNamingFailureKind.ModelUnavailable:
                case RemoteNamingFailureKind.Connectivity:
                    text = "Mac unavailable — try again";
                    return true;
                default:
                    text = null;
                    return false;
            }
        }
    }

    // Ticket 08 (D2): a remote transport failure or timeout may hand the
    // in-flight capture to one local YOLO attempt. Everything else —
    // configuration/authentication/protocol errors, invalid input, and a
    // valid "not found" — stays an actionable error and never falls back.
    public static class RemoteNamingFallbackEligibility
    {
        public static bool IsEligible(RemoteNamingFailureKind kind)
        {
            return kind == RemoteNamingFailureKind.Timeout ||
                   kind == RemoteNamingFailureKind.Connectivity ||
                   kind == RemoteNamingFailureKind.ModelUnavailable;
        }
    }
}
