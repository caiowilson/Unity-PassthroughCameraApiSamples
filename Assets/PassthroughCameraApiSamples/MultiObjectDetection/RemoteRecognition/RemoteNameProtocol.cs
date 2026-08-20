using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class RemoteNameResponse
    {
        public string ProtocolVersion { get; }
        public string RequestId { get; }
        public bool Found { get; }
        public string Name { get; }

        public RemoteNameResponse(string protocolVersion, string requestId, bool found, string name)
        {
            ProtocolVersion = protocolVersion;
            RequestId = requestId;
            Found = found;
            Name = name;
        }
    }

    public static class RemoteNameProtocol
    {
        private static readonly Regex RequestIdPattern = new Regex(
            @"\A[A-Za-z0-9._:-]{1,128}\z",
            RegexOptions.CultureInvariant);

        private static readonly Regex NamePattern = new Regex(
            @"\A[a-z]+(?:[-'][a-z]+)*(?: [a-z]+(?:[-'][a-z]+)*){0,4}\z",
            RegexOptions.CultureInvariant);

        private static readonly Regex FoundPropertyPattern = new Regex(
            "\"found\"\\s*:",
            RegexOptions.CultureInvariant);

        public static bool TryParse(string json, out RemoteNameResponse response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(json) || !FoundPropertyPattern.IsMatch(json))
            {
                return false;
            }

            RemoteNamePayload payload;
            try
            {
                payload = JsonUtility.FromJson<RemoteNamePayload>(json);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (payload == null ||
                payload.protocol_version != RemoteRecognitionConfig.SupportedProtocolVersion ||
                !IsValidRequestId(payload.request_id))
            {
                return false;
            }

            if (payload.found)
            {
                if (!IsValidName(payload.name))
                {
                    return false;
                }
            }
            else if (payload.name != null)
            {
                return false;
            }

            response = new RemoteNameResponse(
                payload.protocol_version,
                payload.request_id,
                payload.found,
                payload.name);
            return true;
        }

        public static bool IsValidRequestId(string requestId)
        {
            return requestId != null && RequestIdPattern.IsMatch(requestId);
        }

        public static bool IsValidName(string name)
        {
            return name != null && NamePattern.IsMatch(name);
        }

        [Serializable]
        private sealed class RemoteNamePayload
        {
            public string protocol_version;
            public string request_id;
            public bool found;
            public string name;
        }
    }
}
