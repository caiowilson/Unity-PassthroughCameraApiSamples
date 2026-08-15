// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 4 Task 1 — separating two failures that shared one signal.
//
// Upstream's raycast returned Vector3?, where null meant BOTH of these:
//
//   (a) the ray was cast fine and hit nothing        — a PER-DETECTION depth miss
//   (b) EnvironmentRaycastManager.IsSupported == false — the SUBSYSTEM is unavailable
//
// Spec line 72 requires distinguishing them, and there is a measurement
// consequence that is easy to miss: slice 2 recorded a 0.9% depth-miss rate
// (17 misses / 1817 attempts) and that figure SILENTLY ASSUMES every miss was
// case (a). Had the subsystem been unavailable the rate would have been 100%,
// and it would have read as "depth quality is bad" rather than "depth is off".
//
// The two need different responses, which is the real reason to split them:
//   (a) is expected and per-detection — drop that one detection, keep running.
//   (b) is a subsystem fault affecting every detection — worth surfacing to the
//       user, because no amount of looking around will fix it.
//
// Deliberately free of MonoBehaviour and Inference Engine types so the policy
// that consumes it stays reachable from edit-mode tests, per slice 3's seam.

using Meta.XR;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum DepthResolveStatus
    {
        /// A world position was resolved.
        Hit = 0,

        /// The ray was cast and hit nothing. Expected, per-detection, not a fault.
        /// This is the only status that belongs in a "depth-miss rate".
        Miss = 1,

        /// EnvironmentRaycastManager reports the subsystem is not supported. Affects
        /// every detection equally; counting these as misses corrupts the rate.
        SubsystemUnavailable = 2,
    }

    public readonly struct DepthResolveResult
    {
        public readonly DepthResolveStatus Status;
        public readonly Vector3 Point;

        private DepthResolveResult(DepthResolveStatus status, Vector3 point)
        {
            Status = status;
            Point = point;
        }

        public bool IsHit => Status == DepthResolveStatus.Hit;

        /// True only for a genuine per-detection miss. Use this — not `!IsHit` — when
        /// accumulating a depth-miss rate, or subsystem faults inflate the numerator
        /// and the resulting figure describes nothing.
        public bool IsPerDetectionMiss => Status == DepthResolveStatus.Miss;

        public static DepthResolveResult Hit(Vector3 point) =>
            new DepthResolveResult(DepthResolveStatus.Hit, point);

        public static DepthResolveResult Miss() =>
            new DepthResolveResult(DepthResolveStatus.Miss, Vector3.zero);

        public static DepthResolveResult SubsystemUnavailable() =>
            new DepthResolveResult(DepthResolveStatus.SubsystemUnavailable, Vector3.zero);

        /// Map an SDK raycast status onto our three-way outcome.
        ///
        /// PURE ON PURPOSE. This mapping is where D-slice4-1 lived: the first version
        /// inferred the outcome from EnvironmentRaycastManager.IsSupported instead of
        /// reading the status, and a Scene-permission denial would have been counted as
        /// depth-quality misses. The fix was correct but sat inside a MonoBehaviour and
        /// needed a live EnvironmentRaycastManager to exercise, so nothing guarded it.
        ///
        /// Extracting it here — the same seam move slice 3 made for decoding — means the
        /// branching is covered by edit-mode tests. Code that has already carried one
        /// defect is the last place to leave untested.
        public static DepthResolveResult FromRaycastStatus(EnvironmentRaycastHitStatus status, Vector3 point)
        {
            switch (status)
            {
                case EnvironmentRaycastHitStatus.Hit:
                    return Hit(point);

                // Subsystem-level: affects every detection equally and no amount of
                // looking around fixes it. MUST stay out of the depth-miss rate.
                //
                // NotReady is the one that mattered: Scene permission gates handle
                // creation, and the SDK's Raycast() checks !IsReady BEFORE IsSupported,
                // so a permission denial arrives here and nowhere else.
                case EnvironmentRaycastHitStatus.NotReady:
                case EnvironmentRaycastHitStatus.NotSupported:
                    return SubsystemUnavailable();

                // Genuine per-detection outcomes: the system worked, this ray did not
                // yield a usable point.
                //
                // HitPointOccluded populates a point, but the object's true surface lies
                // BEYOND that first occluded point, so placing a label there would put it
                // short. Deliberately a miss rather than a hit.
                case EnvironmentRaycastHitStatus.NoHit:
                case EnvironmentRaycastHitStatus.RayOccluded:
                case EnvironmentRaycastHitStatus.HitPointOccluded:
                case EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum:
                    return Miss();

                // A status added by a future SDK version lands here. Treated as a miss,
                // not a hit: an unknown status must never produce a world position.
                default:
                    return Miss();
            }
        }
    }
}
