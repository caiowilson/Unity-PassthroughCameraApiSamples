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

        public static string PresentationFor(LabelRecord record)
        {
            if (record == null || record.Source != LabelSource.RemoteRecognition)
            {
                return null;
            }

            return record.RemoteState == RemoteLabelState.Identifying
                ? IdentifyingPresentation
                : record.RemoteState == RemoteLabelState.Committed
                    ? record.RemoteName
                    : null;
        }

        public static bool CanParticipateInLocalAssociation(LabelRecord record) =>
            record != null && record.Source == LabelSource.LocalDetection;
    }
}
