// Spatial-anchor Restoration Task 1 — tests for the pure-data snapshot
// contract every later task in this plan builds on.
//
// Narrow on purpose: Task 1 only defines the DTO shape, its validation
// surface, and the local/world position helpers. Persistence (Task 2), the
// Meta anchor lifecycle (Task 3), and label population (Task 4) each have
// their own task and their own tests.

using System;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialLabelSnapshotTests
    {
        [Test]
        public void ToLocalThenToWorldRoundTripsATranslatedRotatedAnchorPose()
        {
            var anchorPose = new Pose(new Vector3(5f, -2f, 3f), Quaternion.Euler(0f, 90f, 0f));
            var worldPosition = new Vector3(1f, 2f, 3f);

            var localPosition = SpatialLabelSnapshot.ToLocalPosition(worldPosition, anchorPose);
            var roundTripped = SpatialLabelSnapshot.ToWorldPosition(localPosition, anchorPose);

            Assert.That(roundTripped.x, Is.EqualTo(worldPosition.x).Within(1e-4f));
            Assert.That(roundTripped.y, Is.EqualTo(worldPosition.y).Within(1e-4f));
            Assert.That(roundTripped.z, Is.EqualTo(worldPosition.z).Within(1e-4f));
        }

        [Test]
        public void ToLocalPositionActuallyRotatesRelativeToTheAnchor()
        {
            // Proves the helper isn't just subtracting the anchor's position —
            // a 90-degree yaw on the anchor must rotate the offset into anchor
            // space, moving it off the world X axis. Checked without assuming
            // a rotation-direction sign convention.
            var anchorPose = new Pose(Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
            var worldPosition = new Vector3(1f, 0f, 0f);

            var localPosition = SpatialLabelSnapshot.ToLocalPosition(worldPosition, anchorPose);

            Assert.That(Mathf.Abs(localPosition.x), Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(localPosition.z), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(localPosition.y, Is.EqualTo(0f).Within(1e-4f));
        }

        [TestCase("f47ac10b-58cc-4372-a567-0e02b2c3d479", true)]
        [TestCase("not-a-uuid", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsValidUuidRecognizesValidAndInvalidUuids(string candidate, bool expected)
        {
            Assert.AreEqual(expected, SpatialLabelSnapshot.IsValidUuid(candidate));
        }

        [Test]
        public void SnapshotWithValidAnchorUuidAndNoLabelsIsValid()
        {
            var snapshot = new SpatialLabelSnapshot
            {
                anchorUuid = Guid.NewGuid().ToString(),
                labels = Array.Empty<SpatialLabelEntry>()
            };

            Assert.IsTrue(snapshot.IsValid());
        }

        [Test]
        public void SnapshotWithInvalidAnchorUuidIsInvalid()
        {
            var snapshot = new SpatialLabelSnapshot
            {
                anchorUuid = "not-a-uuid",
                labels = Array.Empty<SpatialLabelEntry>()
            };

            Assert.IsFalse(snapshot.IsValid());
        }

        [Test]
        public void SnapshotWithUnsupportedVersionIsInvalid()
        {
            var snapshot = new SpatialLabelSnapshot
            {
                version = SpatialLabelSnapshot.CurrentVersion + 1,
                anchorUuid = Guid.NewGuid().ToString(),
                labels = Array.Empty<SpatialLabelEntry>()
            };

            Assert.IsFalse(snapshot.IsValid());
        }

        [TestCase(1f, 2f, 3f, 0.5f, true)]
        [TestCase(float.NaN, 2f, 3f, 0.5f, false)]
        [TestCase(1f, float.PositiveInfinity, 3f, 0.5f, false)]
        [TestCase(1f, 2f, float.NegativeInfinity, 0.5f, false)]
        [TestCase(1f, 2f, 3f, float.NaN, false)]
        [TestCase(1f, 2f, 3f, float.PositiveInfinity, false)]
        public void LabelValidityRequiresFiniteCoordinatesAndScore(float x, float y, float z, float score, bool expected)
        {
            var label = new SpatialLabelEntry
            {
                id = Guid.NewGuid().ToString(),
                classId = 1,
                className = "chair",
                score = score,
                localPosition = new Vector3(x, y, z)
            };

            Assert.AreEqual(expected, label.IsValid());
        }

        [Test]
        public void LabelWithInvalidIdIsInvalidEvenWithFiniteData()
        {
            var label = new SpatialLabelEntry
            {
                id = "not-a-uuid",
                classId = 1,
                className = "chair",
                score = 0.9f,
                localPosition = new Vector3(1f, 2f, 3f)
            };

            Assert.IsFalse(label.IsValid());
        }

        [Test]
        public void SnapshotIsInvalidWhenAnyLabelIsInvalid()
        {
            var snapshot = new SpatialLabelSnapshot
            {
                anchorUuid = Guid.NewGuid().ToString(),
                labels = new[]
                {
                    new SpatialLabelEntry
                    {
                        id = Guid.NewGuid().ToString(),
                        classId = 1,
                        className = "chair",
                        score = 0.5f,
                        localPosition = Vector3.zero
                    },
                    new SpatialLabelEntry
                    {
                        id = "not-a-uuid",
                        classId = 2,
                        className = "table",
                        score = 0.5f,
                        localPosition = Vector3.zero
                    }
                }
            };

            Assert.IsFalse(snapshot.IsValid());
        }

        [Test]
        public void JsonUtilityRoundTripsASnapshotWithTwoLabels()
        {
            var firstLabelId = Guid.NewGuid().ToString();
            var secondLabelId = Guid.NewGuid().ToString();
            var snapshot = new SpatialLabelSnapshot
            {
                anchorUuid = Guid.NewGuid().ToString(),
                labels = new[]
                {
                    new SpatialLabelEntry
                    {
                        id = firstLabelId,
                        classId = 3,
                        className = "chair",
                        score = 0.87f,
                        localPosition = new Vector3(1f, 2f, 3f)
                    },
                    new SpatialLabelEntry
                    {
                        id = secondLabelId,
                        classId = 7,
                        className = "table",
                        score = 0.62f,
                        localPosition = new Vector3(-1f, 0.5f, 2.25f)
                    }
                }
            };

            var json = JsonUtility.ToJson(snapshot);
            var roundTripped = JsonUtility.FromJson<SpatialLabelSnapshot>(json);

            Assert.IsTrue(roundTripped.IsValid());
            Assert.AreEqual(SpatialLabelSnapshot.CurrentVersion, roundTripped.version);
            Assert.AreEqual(snapshot.anchorUuid, roundTripped.anchorUuid);
            Assert.AreEqual(2, roundTripped.labels.Length);

            Assert.AreEqual(firstLabelId, roundTripped.labels[0].id);
            Assert.AreEqual(3, roundTripped.labels[0].classId);
            Assert.AreEqual("chair", roundTripped.labels[0].className);
            Assert.AreEqual(0.87f, roundTripped.labels[0].score, 1e-6f);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), roundTripped.labels[0].localPosition);

            Assert.AreEqual(secondLabelId, roundTripped.labels[1].id);
            Assert.AreEqual(7, roundTripped.labels[1].classId);
            Assert.AreEqual("table", roundTripped.labels[1].className);
            Assert.AreEqual(0.62f, roundTripped.labels[1].score, 1e-6f);
            Assert.AreEqual(new Vector3(-1f, 0.5f, 2.25f), roundTripped.labels[1].localPosition);
        }

        [Test]
        public void StateEnumHasExactlyTheFiveExpectedMembers()
        {
            var names = Enum.GetNames(typeof(SpatialAnchorRestorationState));
            CollectionAssert.AreEquivalent(
                new[] { "NoSavedSpace", "Restoring", "Ready", "Unavailable", "Resetting" },
                names);
        }
    }
}
