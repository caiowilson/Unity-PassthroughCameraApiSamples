using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteNameProtocolTests
    {
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
    }
}
