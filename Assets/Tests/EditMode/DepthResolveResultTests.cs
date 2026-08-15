// Object Tagger slice 4 Task 1 — tests for the depth-failure distinction.
//
// The point of these is narrow but load-bearing: `!IsHit` and `IsPerDetectionMiss`
// must NOT be interchangeable. Treating them as equivalent is exactly the bug the
// type was introduced to prevent, and it would silently corrupt the depth-miss
// rate the slice 4 gate has to record.

using Meta.XR;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class DepthResolveResultTests
    {
        [Test]
        public void HitCarriesThePointAndReportsHit()
        {
            var p = new Vector3(1f, 2f, 3f);
            var r = DepthResolveResult.Hit(p);

            Assert.AreEqual(DepthResolveStatus.Hit, r.Status);
            Assert.IsTrue(r.IsHit);
            Assert.IsFalse(r.IsPerDetectionMiss, "a hit is not a miss");
            Assert.AreEqual(p, r.Point);
        }

        [Test]
        public void MissIsAPerDetectionMiss()
        {
            var r = DepthResolveResult.Miss();

            Assert.AreEqual(DepthResolveStatus.Miss, r.Status);
            Assert.IsFalse(r.IsHit);
            Assert.IsTrue(r.IsPerDetectionMiss, "this is the only status that belongs in a miss rate");
        }

        [Test]
        public void SubsystemUnavailableIsNotAPerDetectionMiss()
        {
            // THE WHOLE REASON THIS TYPE EXISTS. Both of these are "not a hit", but only
            // one of them is a depth-QUALITY failure. Counting subsystem faults as misses
            // drives the rate toward 100% and turns "depth is off" into "depth is bad".
            var r = DepthResolveResult.SubsystemUnavailable();

            Assert.AreEqual(DepthResolveStatus.SubsystemUnavailable, r.Status);
            Assert.IsFalse(r.IsHit, "it did not resolve a position");
            Assert.IsFalse(r.IsPerDetectionMiss,
                "a subsystem fault must NOT be counted in the depth-miss rate");
        }

        // ---- FromRaycastStatus: the mapping where D-slice4-1 actually lived ----
        //
        // The original defect was inferring the outcome from
        // EnvironmentRaycastManager.IsSupported rather than reading the SDK status, so a
        // Scene-permission denial was counted as depth-quality misses. These tests cover
        // the mapping directly; the adapter around them needs a live manager and cannot
        // be reached from edit mode.

        [Test]
        public void StatusHitMapsToHitAndKeepsThePoint()
        {
            var p = new Vector3(4f, 5f, 6f);
            var r = DepthResolveResult.FromRaycastStatus(EnvironmentRaycastHitStatus.Hit, p);

            Assert.IsTrue(r.IsHit);
            Assert.AreEqual(p, r.Point);
        }

        [Test]
        public void StatusNotReadyIsSubsystemUnavailableNotAMiss()
        {
            // THE REGRESSION GUARD FOR D-slice4-1. Scene permission gates handle
            // creation, and the SDK checks !IsReady BEFORE IsSupported — so a permission
            // denial arrives as NotReady and nowhere else. Mapping it to Miss drives the
            // depth-miss rate toward 100% and reports "depth quality is terrible" when
            // the truth is "permission was denied".
            var r = DepthResolveResult.FromRaycastStatus(EnvironmentRaycastHitStatus.NotReady, Vector3.zero);

            Assert.AreEqual(DepthResolveStatus.SubsystemUnavailable, r.Status);
            Assert.IsFalse(r.IsPerDetectionMiss, "NotReady must NOT be counted in the depth-miss rate");
        }

        [Test]
        public void StatusNotSupportedIsSubsystemUnavailable()
        {
            var r = DepthResolveResult.FromRaycastStatus(EnvironmentRaycastHitStatus.NotSupported, Vector3.zero);

            Assert.AreEqual(DepthResolveStatus.SubsystemUnavailable, r.Status);
            Assert.IsFalse(r.IsPerDetectionMiss);
        }

        [TestCase(EnvironmentRaycastHitStatus.NoHit)]
        [TestCase(EnvironmentRaycastHitStatus.RayOccluded)]
        [TestCase(EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum)]
        public void GenuinePerDetectionOutcomesMapToMiss(EnvironmentRaycastHitStatus status)
        {
            var r = DepthResolveResult.FromRaycastStatus(status, Vector3.zero);

            Assert.IsTrue(r.IsPerDetectionMiss, $"{status} is a per-detection outcome and belongs in the rate");
        }

        [Test]
        public void HitPointOccludedIsAMissEvenThoughItCarriesAPoint()
        {
            // Subtle one, and neither the original implementation nor the bug report
            // named it. The SDK populates hit.point with the FIRST OCCLUDED point, but
            // the object's real surface lies beyond it — so treating this as a hit would
            // place the label short of the object. It is a miss on purpose.
            var nearOccluder = new Vector3(0f, 0f, 0.5f);
            var r = DepthResolveResult.FromRaycastStatus(EnvironmentRaycastHitStatus.HitPointOccluded, nearOccluder);

            Assert.IsFalse(r.IsHit, "an occluded hit point must not be used as a position");
            Assert.IsTrue(r.IsPerDetectionMiss);
        }

        [Test]
        public void AnUnknownFutureStatusMapsToMissNotHit()
        {
            // A status added by a later SDK version must never yield a world position.
            // Defaulting to Hit would place labels at Vector3.zero.
            var r = DepthResolveResult.FromRaycastStatus((EnvironmentRaycastHitStatus)9999, Vector3.zero);

            Assert.IsFalse(r.IsHit, "an unrecognised status must never produce a position");
        }

        [Test]
        public void NotHitDoesNotImplyPerDetectionMiss()
        {
            // Stated as its own test because `!IsHit` is the tempting shorthand, and it
            // is wrong for exactly one status. If someone later collapses these, this
            // fails rather than the gate quietly recording a meaningless rate.
            var unavailable = DepthResolveResult.SubsystemUnavailable();

            Assert.IsFalse(unavailable.IsHit);
            Assert.AreNotEqual(!unavailable.IsHit, unavailable.IsPerDetectionMiss,
                "!IsHit and IsPerDetectionMiss must diverge for SubsystemUnavailable");
        }
    }
}
