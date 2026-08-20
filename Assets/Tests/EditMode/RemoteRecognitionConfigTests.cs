using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteRecognitionConfigTests
    {
        private const string TestToken = "test-only-secret";

        private const string ValidJson = @"{
            ""protocol_version"": ""1"",
            ""mac_base_url"": ""http://192.168.1.25:8765"",
            ""bearer_token"": ""test-only-secret"",
            ""request_timeout_seconds"": 8
        }";

        [Test]
        public void ValidConfigurationParsesAndNormalizesOneTrailingSlash()
        {
            var json = ValidJson.Replace("192.168.1.25:8765\"", "192.168.1.25:8765/\"");

            var parsed = RemoteRecognitionConfig.TryParse(json, out var config, out var error);

            Assert.IsTrue(parsed);
            Assert.IsNotNull(config);
            Assert.AreEqual(RemoteRecognitionConfig.SupportedProtocolVersion, config.ProtocolVersion);
            Assert.AreEqual("http://192.168.1.25:8765", config.MacBaseUrl);
            Assert.AreEqual(TestToken, config.BearerToken);
            Assert.AreEqual(RemoteRecognitionConfig.RequiredRequestTimeoutSeconds, config.RequestTimeoutSeconds);
            Assert.AreEqual(RemoteRecognitionConfigError.None, error);
        }

        [TestCase("")]
        [TestCase("not-json")]
        public void EmptyOrMalformedJsonIsRejectedWithoutLeakingInput(string json)
        {
            Assert.IsFalse(RemoteRecognitionConfig.TryParse(json, out var config, out var error));

            Assert.IsNull(config);
            Assert.AreNotEqual(RemoteRecognitionConfigError.None, error);
            Assert.IsFalse(error.ToString().Contains(TestToken));
        }

        [Test]
        public void UnsupportedProtocolIsRejectedWithoutLeakingTheToken()
        {
            var json = ValidJson.Replace("\"protocol_version\": \"1\"", "\"protocol_version\": \"2\"");

            Assert.IsFalse(RemoteRecognitionConfig.TryParse(json, out _, out var error));

            Assert.AreEqual(RemoteRecognitionConfigError.UnsupportedProtocol, error);
            Assert.IsFalse(error.ToString().Contains(TestToken));
        }

        [TestCase("")]
        [TestCase("contains\\nnewline")]
        [TestCase("contains\\rreturn")]
        public void MissingOrLineBreakingTokenIsRejectedWithoutLeakingTheToken(string token)
        {
            var json = ValidJson.Replace(TestToken, token);

            Assert.IsFalse(RemoteRecognitionConfig.TryParse(json, out _, out var error));

            Assert.AreEqual(RemoteRecognitionConfigError.InvalidBearerToken, error);
            Assert.IsFalse(error.ToString().Contains(TestToken));
        }

        [TestCase("https://192.168.1.25:8765")]
        [TestCase("http://user:password@192.168.1.25:8765")]
        [TestCase("http://192.168.1.25:8765/v1")]
        [TestCase("http://192.168.1.25:8765/?query=yes")]
        [TestCase("http://192.168.1.25:8765/#fragment")]
        [TestCase("file:///tmp/companion")]
        public void NonHttpOrStructurallyUnsafeUrlIsRejected(string url)
        {
            var json = ValidJson.Replace("http://192.168.1.25:8765", url);

            Assert.IsFalse(RemoteRecognitionConfig.TryParse(json, out _, out var error));

            Assert.AreEqual(RemoteRecognitionConfigError.InvalidMacBaseUrl, error);
            Assert.IsFalse(error.ToString().Contains(TestToken));
        }

        [TestCase(0)]
        [TestCase(7)]
        [TestCase(9)]
        public void TimeoutOtherThanEightSecondsIsRejected(int timeout)
        {
            var json = ValidJson.Replace("\"request_timeout_seconds\": 8", $"\"request_timeout_seconds\": {timeout}");

            Assert.IsFalse(RemoteRecognitionConfig.TryParse(json, out _, out var error));

            Assert.AreEqual(RemoteRecognitionConfigError.InvalidRequestTimeout, error);
            Assert.IsFalse(error.ToString().Contains(TestToken));
        }
    }
}
