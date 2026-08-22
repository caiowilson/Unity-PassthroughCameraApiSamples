using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum LabelSource
    {
        LocalDetection = 0,
        RemoteRecognition = 1,
    }

    public enum RemoteLabelState
    {
        None = 0,
        Identifying = 1,
        Committed = 2,
    }

    public static class RemoteSpatialLabelLifecycle
    {
        public const string IdentifyingPresentation = "Identifying…";

        // Ticket 08: appended to a fallback-committed label's class name so the
        // user can tell a Quest-local YOLO guess apart from the Mac's free-form
        // naming.
        public const string FallbackMarker = " (on-headset)";

        public static bool TryCreate(Guid operationId, Vector3 point, out LabelRecord record)
        {
            record = null;
            if (operationId == Guid.Empty)
            {
                return false;
            }

            record = new LabelRecord
            {
                SessionId = operationId,
                Source = LabelSource.RemoteRecognition,
                RemoteState = RemoteLabelState.Identifying,
                WorldPosition = point,
                SmoothedPosition = point,
            };
            return true;
        }

        public static bool TryCommit(LabelRecord record, Guid operationId, string name)
        {
            if (record == null ||
                record.Source != LabelSource.RemoteRecognition ||
                record.RemoteState != RemoteLabelState.Identifying ||
                record.SessionId != operationId ||
                !RemoteNameProtocol.IsValidName(name))
            {
                return false;
            }

            record.RemoteName = name;
            record.RemoteState = RemoteLabelState.Committed;
            return true;
        }

        // Ticket 08: commits through the same pending-label lifecycle as
        // TryCommit, but from the on-headset YOLO fallback rather than the
        // Mac's response. Marks the record so PresentationFor can flag it as
        // lower-capability.
        public static bool TryCommitFallback(LabelRecord record, Guid operationId, string className)
        {
            if (record == null ||
                record.Source != LabelSource.RemoteRecognition ||
                record.RemoteState != RemoteLabelState.Identifying ||
                record.SessionId != operationId ||
                !RemoteNameProtocol.IsValidName(className))
            {
                return false;
            }

            record.RemoteName = className;
            record.RemoteState = RemoteLabelState.Committed;
            record.IsFallbackResult = true;
            return true;
        }

        public static string PresentationFor(LabelRecord record)
        {
            if (record == null || record.Source != LabelSource.RemoteRecognition)
            {
                return null;
            }

            if (record.RemoteState == RemoteLabelState.Identifying)
            {
                return IdentifyingPresentation;
            }

            if (record.RemoteState != RemoteLabelState.Committed)
            {
                return null;
            }

            return record.IsFallbackResult
                ? record.RemoteName + FallbackMarker
                : record.RemoteName;
        }

        public static bool CanParticipateInLocalAssociation(LabelRecord record) =>
            record != null && record.Source == LabelSource.LocalDetection;
    }
}
