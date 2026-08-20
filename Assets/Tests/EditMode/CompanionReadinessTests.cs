using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class CompanionReadinessTests
    {
        private const string TokenSentinel = "test-only-secret";
        private const string BodySentinel = "server-detail-that-must-not-reach-ui";

        [TestCase("loading", CompanionReadinessKind.Loading, false)]
        [TestCase("ready", CompanionReadinessKind.Ready, true)]
        [TestCase("error", CompanionReadinessKind.Unavailable, false)]
        public void HealthStatusesMapToSafeReadiness(string status, CompanionReadinessKind expectedKind, bool expectedReady)
        {
            var state = CompanionHealthProtocol.Evaluate(
                200,
                false,
                $"{{\"protocol_version\":\"1\",\"status\":\"{status}\",\"model\":\"test-model\"}}");

            Assert.AreEqual(expectedKind, state.Kind);
            Assert.AreEqual(expectedReady, state.IsReady);
            Assert.IsFalse(state.Message.Contains(TokenSentinel));
            Assert.IsFalse(state.Message.Contains(BodySentinel));
        }

        [Test]
        public void MalformedHealthBodyIsUnavailableWithoutLeakingBody()
        {
            var state = CompanionHealthProtocol.Evaluate(200, false, $"{{\"status\":\"ready\",\"detail\":\"{BodySentinel}\"");

            Assert.AreEqual(CompanionReadinessKind.Unavailable, state.Kind);
            Assert.IsFalse(state.IsReady);
            Assert.IsFalse(state.Message.Contains(BodySentinel));
        }

        [Test]
        public void ProtocolMismatchIsActionableMisconfiguredWithoutLeakingBody()
        {
            var state = CompanionHealthProtocol.Evaluate(
                200,
                false,
                $"{{\"protocol_version\":\"2\",\"status\":\"ready\",\"detail\":\"{BodySentinel}\"}}");

            Assert.AreEqual(CompanionReadinessKind.Misconfigured, state.Kind);
            Assert.IsFalse(state.IsReady);
            Assert.IsTrue(state.Message.Contains("protocol"));
            Assert.IsFalse(state.Message.Contains(BodySentinel));
            Assert.IsFalse(state.Message.Contains(TokenSentinel));
        }

        [Test]
        public void UnauthorizedIsActionableMisconfiguredWithoutLeakingBody()
        {
            var state = CompanionHealthProtocol.Evaluate(401, false, $"{{\"detail\":\"{BodySentinel}\"}}");

            Assert.AreEqual(CompanionReadinessKind.Misconfigured, state.Kind);
            Assert.IsFalse(state.IsReady);
            Assert.IsTrue(state.Message.Contains("authentication"));
            Assert.IsFalse(state.Message.Contains(BodySentinel));
            Assert.IsFalse(state.Message.Contains(TokenSentinel));
        }

        [TestCase(0, true)]
        [TestCase(500, false)]
        public void TransportAndServerFailuresAreUnavailable(long responseCode, bool transportFailed)
        {
            var state = CompanionHealthProtocol.Evaluate(responseCode, transportFailed, $"{{\"detail\":\"{BodySentinel}\"}}");

            Assert.AreEqual(CompanionReadinessKind.Unavailable, state.Kind);
            Assert.IsFalse(state.IsReady);
            Assert.IsFalse(state.Message.Contains(BodySentinel));
            Assert.IsFalse(state.Message.Contains(TokenSentinel));
        }

        [Test]
        public void StateMachineRecoversFromUnavailableToReady()
        {
            var stateMachine = new CompanionReadinessStateMachine();

            stateMachine.Apply(CompanionHealthProtocol.Evaluate(0, true, string.Empty));
            var recovered = stateMachine.Apply(CompanionHealthProtocol.Evaluate(200, false, "{\"protocol_version\":\"1\",\"status\":\"ready\"}"));

            Assert.AreEqual(CompanionReadinessKind.Ready, recovered.Kind);
            Assert.IsTrue(recovered.IsReady);
        }

        [Test]
        public void LaterProbeRetainsReadyUntilItsHealthObservationArrives()
        {
            var stateMachine = new CompanionReadinessStateMachine();

            var firstProbe = stateMachine.BeginProbe();
            stateMachine.Apply(CompanionHealthProtocol.Evaluate(200, false, "{\"protocol_version\":\"1\",\"status\":\"ready\"}"));
            var laterProbe = stateMachine.BeginProbe();

            Assert.AreEqual(CompanionReadinessKind.Loading, firstProbe.Kind);
            Assert.AreEqual(CompanionReadinessKind.Ready, laterProbe.Kind);
            Assert.IsTrue(laterProbe.IsReady);
        }

        [TestCase("loading", CompanionReadinessKind.Loading)]
        [TestCase("error", CompanionReadinessKind.Unavailable)]
        public void StateMachineLeavesReadyForLaterHealthObservations(string status, CompanionReadinessKind expectedKind)
        {
            var stateMachine = new CompanionReadinessStateMachine();
            stateMachine.Apply(CompanionHealthProtocol.Evaluate(200, false, "{\"protocol_version\":\"1\",\"status\":\"ready\"}"));

            var state = stateMachine.Apply(CompanionHealthProtocol.Evaluate(
                200,
                false,
                $"{{\"protocol_version\":\"1\",\"status\":\"{status}\"}}"));

            Assert.AreEqual(expectedKind, state.Kind);
            Assert.IsFalse(state.IsReady);
        }

        [Test]
        public void DetectionUiPrefabSerializesTheReadinessController()
        {
            const string prefabPath =
                "Assets/PassthroughCameraApiSamples/MultiObjectDetection/DetectionManager/Prefabs/DetectionUiMenuPrefab.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            Assert.IsNotNull(prefab);
            Assert.IsNotNull(prefab.GetComponent<CompanionReadinessController>());
        }

        [Test]
        public void PlayerAllowsTrustedLanHttpForTheCompanionProtocol()
        {
            Assert.AreEqual(InsecureHttpOption.AlwaysAllowed, PlayerSettings.insecureHttpOption);
        }
    }
}
