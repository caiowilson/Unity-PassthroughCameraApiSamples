using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        public static bool TryParse(string json, out RemoteNameResponse response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            JObject payload;
            try
            {
                payload = JObject.Parse(
                    json,
                    new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    });
            }
            catch (JsonException)
            {
                return false;
            }

            if (!TryReadString(payload, "protocol_version", out var protocolVersion) ||
                protocolVersion != RemoteRecognitionConfig.SupportedProtocolVersion ||
                !TryReadString(payload, "request_id", out var requestId) ||
                !IsValidRequestId(requestId) ||
                !payload.TryGetValue("found", StringComparison.Ordinal, out var foundToken) ||
                foundToken.Type != JTokenType.Boolean)
            {
                return false;
            }

            var found = foundToken.Value<bool>();
            var nameToken = payload.GetValue("name", StringComparison.Ordinal);
            string name = null;
            if (found)
            {
                if (nameToken == null || nameToken.Type != JTokenType.String)
                {
                    return false;
                }
                name = nameToken.Value<string>();
                if (!IsValidName(name))
                {
                    return false;
                }
            }
            else if (nameToken != null)
            {
                return false;
            }

            response = new RemoteNameResponse(protocolVersion, requestId, found, name);
            return true;
        }

        public static bool IsValidRequestId(string requestId)
        {
            return requestId != null && RequestIdPattern.IsMatch(requestId);
        }

        public static bool IsSuccessfulHttpStatus(long statusCode)
        {
            return statusCode == 200;
        }

        public static bool IsValidName(string name)
        {
            return name != null && NamePattern.IsMatch(name);
        }

        private static bool TryReadString(JObject payload, string propertyName, out string value)
        {
            value = null;
            if (!payload.TryGetValue(propertyName, StringComparison.Ordinal, out var token) ||
                token.Type != JTokenType.String)
            {
                return false;
            }

            value = token.Value<string>();
            return true;
        }
    }
}
