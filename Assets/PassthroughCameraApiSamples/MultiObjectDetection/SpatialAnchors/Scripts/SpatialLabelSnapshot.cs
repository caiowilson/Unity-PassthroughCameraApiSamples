// Spatial-anchor Restoration Task 1 — the pure-data contract every later
// task in this plan builds on. Task 2 persists this DTO to disk as JSON
// (JsonUtility, hence public fields rather than properties); Task 3's
// coordinator exposes an instance as `Snapshot`; Task 4 populates `labels`
// from committed LabelRecord instances and uses ToLocalPosition /
// ToWorldPosition to convert positions to and from the anchor's local space.
//
// Deliberately free of any Meta SDK type (no OVRSpatialAnchor): Task 3 owns
// the Meta anchor lifecycle. anchorUuid is a string, not a System.Guid,
// because JsonUtility cannot serialize Guid; IsValidUuid/IsValid give
// callers a parse-and-check surface instead of reaching for Guid.Parse
// themselves. ToLocalPosition/ToWorldPosition take a plain UnityEngine.Pose
// for the anchor's transform for the same reason.
//
// SpatialLabelEntry lives in this file rather than its own, but is still a
// top-level PUBLIC type, not nested/internal — this project rejects
// InternalsVisibleTo as a seam (see AssemblySeamTests.cs), so a nested type
// would be unreachable from edit-mode tests.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [Serializable]
    public class SpatialLabelSnapshot
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string anchorUuid;
        public SpatialLabelEntry[] labels = Array.Empty<SpatialLabelEntry>();

        /// True when version matches CurrentVersion, anchorUuid parses as a
        /// UUID, and every label is individually valid.
        public bool IsValid()
        {
            if (version != CurrentVersion || !IsValidUuid(anchorUuid) || labels == null)
            {
                return false;
            }

            foreach (var label in labels)
            {
                if (label == null || !label.IsValid())
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsValidUuid(string candidate)
        {
            return !string.IsNullOrEmpty(candidate) && Guid.TryParse(candidate, out _);
        }

        /// Converts a world-space position into the anchor-local space
        /// defined by anchorPose, for storage in SpatialLabelEntry.localPosition.
        public static Vector3 ToLocalPosition(Vector3 worldPosition, Pose anchorPose)
        {
            return Quaternion.Inverse(anchorPose.rotation) * (worldPosition - anchorPose.position);
        }

        /// Inverse of ToLocalPosition: reconstructs a world-space position
        /// from a stored anchor-local position and the anchor's current pose.
        public static Vector3 ToWorldPosition(Vector3 localPosition, Pose anchorPose)
        {
            return anchorPose.position + anchorPose.rotation * localPosition;
        }
    }

    [Serializable]
    public class SpatialLabelEntry
    {
        public string id;
        public int classId;
        public string className;
        public float score;
        public Vector3 localPosition;

        /// True when id parses as a UUID, className is non-empty, and score
        /// and every localPosition component are finite.
        public bool IsValid()
        {
            return SpatialLabelSnapshot.IsValidUuid(id)
                && !string.IsNullOrEmpty(className)
                && IsFinite(score)
                && IsFinite(localPosition.x)
                && IsFinite(localPosition.y)
                && IsFinite(localPosition.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
