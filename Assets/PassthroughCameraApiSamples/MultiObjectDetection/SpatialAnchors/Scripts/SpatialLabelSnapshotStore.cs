// Spatial-anchor Restoration Task 2 — atomic device-local persistence for
// the SpatialLabelSnapshot contract Task 1 defined. Task 3's coordinator
// calls TryLoad once on startup and TrySave/TryDelete on every
// commit/untag/clear/reset; this class knows nothing about that lifecycle,
// the Meta anchor, or Application.persistentDataPath — it only reads and
// writes whatever path it is given.
//
// Never logs file contents, label class names, or positions on a failure
// path: only a generic message plus the device-local path, which is not
// user content.

using System;
using System.IO;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class SpatialLabelSnapshotStore
    {
        public const string DefaultFileName = "spatial-label-snapshot.json";

        private const string TempFileSuffix = ".tmp";

        /// Loads and validates the snapshot at path. Returns false — with
        /// snapshot set to null — when the file is missing, unreadable,
        /// not valid JSON, or deserializes to a snapshot that fails
        /// SpatialLabelSnapshot.IsValid() (including an unsupported
        /// version). Never throws.
        public static bool TryLoad(string path, out SpatialLabelSnapshot snapshot)
        {
            snapshot = null;

            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                var json = File.ReadAllText(path);
                var candidate = JsonUtility.FromJson<SpatialLabelSnapshot>(json);
                if (candidate == null || !candidate.IsValid())
                {
                    return false;
                }

                snapshot = candidate;
                return true;
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Debug.LogWarning($"[ObjectTagger] failed to load spatial-label snapshot: {path} ({e.GetType().Name})");
                snapshot = null;
                return false;
            }
        }

        /// Writes snapshot to path by first writing a temp file next to it,
        /// then replacing/moving it into place, so a crash mid-write never
        /// leaves a torn file at path. Rejects a null or invalid snapshot
        /// without touching disk. Cleans up the temp file on any failure
        /// and never throws.
        public static bool TrySave(string path, SpatialLabelSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(path) || snapshot == null || !snapshot.IsValid())
            {
                return false;
            }

            var tempPath = path + TempFileSuffix;

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tempPath, JsonUtility.ToJson(snapshot));

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }

                return true;
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Debug.LogWarning($"[ObjectTagger] failed to save spatial-label snapshot: {path} ({e.GetType().Name})");
                DeleteTempFileQuietly(tempPath);
                return false;
            }
        }

        /// Deletes the snapshot at path. Returns true if the file does not
        /// exist by the time this returns (deleting an already-missing
        /// file is not a failure), false only when an existing file could
        /// not be removed. Never throws.
        public static bool TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Debug.LogWarning($"[ObjectTagger] failed to delete spatial-label snapshot: {path} ({e.GetType().Name})");
                return false;
            }
        }

        private static void DeleteTempFileQuietly(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                // Best-effort cleanup only; the save already failed and was
                // reported above.
            }
        }

        private static bool IsRecoverable(Exception exception)
        {
            return exception is IOException
                || exception is UnauthorizedAccessException
                || exception is ArgumentException
                || exception is NotSupportedException
                || exception is System.Security.SecurityException;
        }
    }
}
