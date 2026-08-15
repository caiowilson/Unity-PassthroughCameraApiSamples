// Object Tagger slice 4 Task 1 — tests for the depth-failure distinction.
//
// The point of these is narrow but load-bearing: `!IsHit` and `IsPerDetectionMiss`
// must NOT be interchangeable. Treating them as equivalent is exactly the bug the
// type was introduced to prevent, and it would silently corrupt the depth-miss
// rate the slice 4 gate has to record.

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
