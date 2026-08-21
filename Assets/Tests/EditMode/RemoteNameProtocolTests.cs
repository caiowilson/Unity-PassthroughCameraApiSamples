using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteNameProtocolTests
    {
        [TestCase(400, "invalid_request", RemoteNamingFailureKind.InvalidPayload)]
        [TestCase(408, "invalid_request", RemoteNamingFailureKind.InvalidPayload)]
        [TestCase(400, "invalid_image", RemoteNamingFailureKind.InvalidPayload)]
        [TestCase(413, "payload_too_large", RemoteNamingFailureKind.InvalidPayload)]
        [TestCase(401, "authentication_failed", RemoteNamingFailureKind.Authentication)]
        [TestCase(409, "busy", RemoteNamingFailureKind.Busy)]
        [TestCase(503, "model_unavailable", RemoteNamingFailureKind.ModelUnavailable)]
        [TestCase(504, "inference_timeout", RemoteNamingFailureKind.Timeout)]
        public void ErrorEnvelopeMapsExactStatusAndCodePair(
            long statusCode,
            string code,
            RemoteNamingFailureKind expected)
        {
            var json = ErrorEnvelope(code, "request-123", "sensitive server detail");

            var parsed = RemoteNameProtocol.TryParseError(
                statusCode,
                json,
                "request-123",
                out var failure);

            Assert.IsTrue(parsed);
            Assert.AreEqual(expected, failure);
        }

        [Test]
        public void ErrorEnvelopeMayOmitRequestId()
        {
            const string json =
                "{\"protocol_version\":\"1\",\"error\":{" +
                "\"code\":\"authentication_failed\",\"message\":\"ignored\"}}";

            Assert.IsTrue(RemoteNameProtocol.TryParseError(
                401,
                json,
                "request-123",
                out var failure));
            Assert.AreEqual(RemoteNamingFailureKind.Authentication, failure);
        }

        [TestCase(408, "invalid_image")]
        [TestCase(413, "invalid_request")]
        [TestCase(401, "busy")]
        [TestCase(409, "model_unavailable")]
        [TestCase(503, "inference_timeout")]
        [TestCase(504, "authentication_failed")]
        [TestCase(500, "invalid_request")]
        [TestCase(400, "unknown_error")]
        public void ErrorEnvelopeRejectsUnknownOrInconsistentStatusCodePair(
            long statusCode,
            string code)
        {
            Assert.IsFalse(RemoteNameProtocol.TryParseError(
                statusCode,
                ErrorEnvelope(code, "request-123", "ignored"),
                "request-123",
                out var failure));
            Assert.AreEqual(RemoteNamingFailureKind.InvalidResponse, failure);
        }

        [TestCase(400, "not json")]
        [TestCase(400, "{\"protocol_version\":\"2\",\"error\":{\"code\":\"invalid_request\",\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"error\":null}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"error\":{\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"error\":{\"code\":null,\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"error\":{\"code\":\"invalid_request\",\"message\":null}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"request_id\":null,\"error\":{\"code\":\"invalid_request\",\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"request_id\":\"other-request\",\"error\":{\"code\":\"invalid_request\",\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"protocol_version\":\"1\",\"error\":{\"code\":\"invalid_request\",\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"request_id\":\"request-123\",\"request_id\":\"request-123\",\"error\":{\"code\":\"invalid_request\",\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"error\":{\"code\":\"invalid_request\",\"code\":\"invalid_request\",\"message\":\"ignored\"}}")]
        [TestCase(400, "{\"protocol_version\":\"1\",\"error\":{\"code\":\"invalid_request\",\"message\":\"ignored\",\"message\":\"ignored\"}}")]
        public void ErrorEnvelopeRejectsMalformedDuplicateOrMismatchedPayload(
            long statusCode,
            string json)
        {
            Assert.IsFalse(RemoteNameProtocol.TryParseError(
                statusCode,
                json,
                "request-123",
                out var failure));
            Assert.AreEqual(RemoteNamingFailureKind.InvalidResponse, failure);
        }

        [TestCase(200, true)]
        [TestCase(201, false)]
        [TestCase(204, false)]
        public void NamingResponseRequiresExactlyHttp200(long statusCode, bool expected)
        {
            Assert.AreEqual(expected, RemoteNameProtocol.IsSuccessfulHttpStatus(statusCode));
        }

        [TestCase("mug")]
        [TestCase("coffee mug")]
        [TestCase("t-shirt")]
        [TestCase("children's book")]
        [TestCase("small red coffee mug")]
        public void FoundResponseAcceptsValidatedLowercaseNounPhrase(string name)
        {
            var json =
                $"{{\"protocol_version\":\"1\",\"request_id\":\"request-123\",\"found\":true," +
                $"\"name\":\"{name}\",\"timing\":{{\"server_ms\":12.5,\"inference_ms\":10.0}}}}";

            var parsed = RemoteNameProtocol.TryParse(json, out var response);

            Assert.IsTrue(parsed);
            Assert.AreEqual("1", response.ProtocolVersion);
            Assert.AreEqual("request-123", response.RequestId);
            Assert.IsTrue(response.Found);
            Assert.AreEqual(name, response.Name);
        }

        [Test]
        public void NotFoundResponseRequiresNoName()
        {
            const string json =
                "{\"protocol_version\":\"1\",\"request_id\":\"request-not-found\",\"found\":false," +
                "\"timing\":{\"server_ms\":12.5,\"inference_ms\":10.0}}";

            var parsed = RemoteNameProtocol.TryParse(json, out var response);

            Assert.IsTrue(parsed);
            Assert.AreEqual("request-not-found", response.RequestId);
            Assert.IsFalse(response.Found);
            Assert.IsNull(response.Name);
        }

        [TestCase("not json")]
        [TestCase("{\"protocol_version\":\"2\",\"request_id\":\"request-1\",\"found\":true,\"name\":\"mug\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"bad id\",\"found\":true,\"name\":\"mug\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"name\":\"mug\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"metadata\":{\"found\":false}}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"note\":\"property \\\"found\\\": false\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":\"false\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":null}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":false,\"name\":\"mug\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":true,\"name\":\"six word names are invalid here\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":true,\"name\":\"Coffee Mug\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":true,\"name\":\"mug!\"}")]
        [TestCase("{\"protocol_version\":\"1\",\"request_id\":\"request-1\",\"found\":true,\"name\":\"café\"}")]
        public void InvalidResponseIsRejected(string json)
        {
            Assert.IsFalse(RemoteNameProtocol.TryParse(json, out _));
        }

        private static string ErrorEnvelope(string code, string requestId, string message)
        {
            return
                $"{{\"protocol_version\":\"1\",\"request_id\":\"{requestId}\"," +
                $"\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}";
        }
    }
}
