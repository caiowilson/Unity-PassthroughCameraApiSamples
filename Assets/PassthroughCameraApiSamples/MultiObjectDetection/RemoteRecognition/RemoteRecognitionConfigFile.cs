using System.Collections.Generic;
using System.IO;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class RemoteRecognitionConfigFile
    {
        public static bool TryLoad(IEnumerable<string> candidatePaths, out RemoteRecognitionConfig config)
        {
            config = null;

            foreach (var path in candidatePaths)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    return RemoteRecognitionConfig.TryParse(File.ReadAllText(path), out config, out _);
                }
                catch (IOException)
                {
                    // A shell-pushed Android external-storage file can exist while
                    // remaining unreadable to the app sandbox. Try the app-owned path.
                }
                catch (System.UnauthorizedAccessException)
                {
                    // See IOException handling above.
                }
            }

            config = null;
            return false;
        }
    }
}
