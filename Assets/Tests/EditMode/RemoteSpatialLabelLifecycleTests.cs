using System;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteSpatialLabelLifecycleTests
    {
        [Test]
        public void CreateInitializesIdentifyingRecordAtLiteralCapturedPoint()
        {
            var operationId = Guid.NewGuid();
            var point = new Vector3(1.25f, -0.5f, 2.75f);

            Assert.IsTrue(RemoteSpatialLabelLifecycle.TryCreate(operationId, point, out var record));
            Assert.AreEqual(operationId, record.SessionId);
            Assert.AreEqual(LabelSource.RemoteRecognition, record.Source);
            Assert.AreEqual(RemoteLabelState.Identifying, record.RemoteState);
            Assert.AreEqual(point, record.WorldPosition);
            Assert.AreEqual(point, record.SmoothedPosition);
            Assert.AreEqual("Identifying…", RemoteSpatialLabelLifecycle.PresentationFor(record));
        }

        [Test]
        public void MatchingCommitChangesOnlyStateAndName()
        {
            var operationId = Guid.NewGuid();
            var point = new Vector3(1f, 2f, 3f);
            RemoteSpatialLabelLifecycle.TryCreate(operationId, point, out var record);

            Assert.IsTrue(RemoteSpatialLabelLifecycle.TryCommit(record, operationId, "coffee mug"));
            Assert.AreEqual(RemoteLabelState.Committed, record.RemoteState);
            Assert.AreEqual("coffee mug", RemoteSpatialLabelLifecycle.PresentationFor(record));
            Assert.AreEqual(point, record.WorldPosition);
            Assert.AreEqual(point, record.SmoothedPosition);
        }

        [Test]
        public void EmptyMismatchedLateAndInvalidCommitsAreRejected()
        {
            Assert.IsFalse(RemoteSpatialLabelLifecycle.TryCreate(Guid.Empty, Vector3.one, out _));

            var operationId = Guid.NewGuid();
            RemoteSpatialLabelLifecycle.TryCreate(operationId, Vector3.one, out var record);

            Assert.IsFalse(RemoteSpatialLabelLifecycle.TryCommit(record, Guid.NewGuid(), "mug"));
            Assert.IsFalse(RemoteSpatialLabelLifecycle.TryCommit(record, operationId, "MUG"));
            Assert.IsTrue(RemoteSpatialLabelLifecycle.TryCommit(record, operationId, "mug"));
            Assert.IsFalse(RemoteSpatialLabelLifecycle.TryCommit(record, operationId, "late name"));
        }

        [Test]
        public void RemoteRecordCannotParticipateInLocalAssociation()
        {
            RemoteSpatialLabelLifecycle.TryCreate(Guid.NewGuid(), Vector3.one, out var remote);

            Assert.IsFalse(RemoteSpatialLabelLifecycle.CanParticipateInLocalAssociation(remote));
            Assert.IsTrue(RemoteSpatialLabelLifecycle.CanParticipateInLocalAssociation(new LabelRecord()));
        }
    }
}
