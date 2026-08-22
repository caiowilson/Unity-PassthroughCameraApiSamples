using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteNamingSessionTests
    {
        [TestCase(true, false, false, true)]
        [TestCase(false, false, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, false, true, false)]
        public void GestureRoutingRequiresLivePlaybackAndDismissedWelcomeTransition(
            bool cameraIsPlaying,
            bool appPaused,
            bool wasPausedLastFrame,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                RemoteNamingInputPolicy.CanStart(
                    cameraIsPlaying,
                    appPaused,
                    wasPausedLastFrame));
        }

        [TestCase(true,  false, false, true,  true,  true)]
        [TestCase(false, false, false, true,  true,  false)]
        [TestCase(true,  true,  false, true,  true,  false)]
        [TestCase(true,  false, true,  true,  true,  false)]
        [TestCase(true,  false, false, false, true,  false)]
        [TestCase(true,  false, false, true,  false, false)]
        public void GestureRoutingRequiresTrackedAnchorAndCurrentResolvedAim(
            bool cameraIsPlaying,
            bool appPaused,
            bool wasPausedLastFrame,
            bool anchorTracked,
            bool hasResolvedAim,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                RemoteNamingInputPolicy.CanStartResolvedAim(
                    cameraIsPlaying,
                    appPaused,
                    wasPausedLastFrame,
                    anchorTracked,
                    hasResolvedAim));
        }

        [TestCase(true,  true,  false, false, true,  true)]
        [TestCase(false, true,  false, false, true,  false)]
        [TestCase(true,  false, false, false, true,  false)]
        [TestCase(true,  true,  true,  false, true,  false)]
        [TestCase(true,  true,  false, true,  true,  false)]
        [TestCase(true,  true,  false, false, false, false)]
        public void AimReticleRequiresUsableMacInputState(
            bool appStarted,
            bool cameraIsPlaying,
            bool appPaused,
            bool wasPausedLastFrame,
            bool companionReady,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                RemoteNamingInputPolicy.ShouldShowAimReticle(
                    appStarted,
                    cameraIsPlaying,
                    appPaused,
                    wasPausedLastFrame,
                    companionReady));
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void BeginRequiresReadyCompanionAndDismissedWelcome(bool companionReady, bool appPaused)
        {
            var session = new RemoteNamingSession();

            Assert.IsFalse(session.TryBegin(companionReady, appPaused, "request-1"));
            Assert.IsFalse(session.IsRequestActive);
            Assert.IsNull(session.PresentationText);
        }

        [Test]
        public void BeginShowsIdentifyingAndRejectsSecondActiveRequest()
        {
            var session = new RemoteNamingSession();

            Assert.IsTrue(session.TryBegin(true, false, "request-1"));
            Assert.IsFalse(session.TryBegin(true, false, "request-2"));
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-1", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);
        }

        [Test]
        public void MatchingFoundResponseShowsNameForThreeRealtimeSecondsThenExpires()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            var accepted = session.TryAccept(
                new RemoteNameResponse("1", "request-1", true, "coffee mug"),
                10f);

            Assert.IsTrue(accepted);
            Assert.IsFalse(session.IsRequestActive);
            Assert.AreEqual("coffee mug", session.PresentationText);

            session.Tick(12.999f);
            Assert.AreEqual("coffee mug", session.PresentationText);

            session.Tick(13f);
            Assert.IsNull(session.PresentationText);
        }

        [Test]
        public void MismatchedResponseIsIgnoredWithoutReleasingActiveRequest()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            var accepted = session.TryAccept(
                new RemoteNameResponse("1", "request-2", true, "mug"),
                10f);

            Assert.IsFalse(accepted);
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-1", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);
        }

        [Test]
        public void MatchingNotFoundResponseShowsRetryableFailure()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            var accepted = session.TryAccept(
                new RemoteNameResponse("1", "request-1", false, null),
                10f);

            Assert.IsFalse(accepted);
            Assert.IsFalse(session.IsRequestActive);
            Assert.AreEqual("No object found — try again", session.PresentationText);
        }

        [TestCase(RemoteNamingFailureKind.Timeout, "Timed out — try again")]
        [TestCase(RemoteNamingFailureKind.Busy, "Mac busy — try again")]
        [TestCase(RemoteNamingFailureKind.NotFound, "No object found — try again")]
        [TestCase(RemoteNamingFailureKind.Authentication, "Authentication failed — update config")]
        [TestCase(RemoteNamingFailureKind.InvalidPayload, "Couldn’t identify — try again")]
        [TestCase(RemoteNamingFailureKind.InvalidResponse, "Couldn’t identify — try again")]
        [TestCase(RemoteNamingFailureKind.ModelUnavailable, "Mac unavailable — try again")]
        [TestCase(RemoteNamingFailureKind.Connectivity, "Mac unavailable — try again")]
        public void MatchingFailureShowsSafeTextForThreeRealtimeSeconds(
            RemoteNamingFailureKind kind,
            string expectedText)
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            Assert.IsTrue(session.TryFail("request-1", kind, 10f));
            Assert.IsFalse(session.IsRequestActive);
            Assert.AreEqual(expectedText, session.PresentationText);

            session.Tick(12.999f);
            Assert.AreEqual(expectedText, session.PresentationText);

            session.Tick(13f);
            Assert.IsNull(session.PresentationText);
        }

        [Test]
        public void CancelOnlyReleasesMatchingRequestAndIsSilent()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            Assert.IsFalse(session.TryCancel("request-2"));
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-1", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);

            Assert.IsTrue(session.TryCancel("request-1"));
            Assert.IsFalse(session.IsRequestActive);
            Assert.IsNull(session.ActiveRequestId);
            Assert.IsNull(session.PresentationText);
        }

        [TestCase(RemoteNamingFailureKind.None)]
        [TestCase(RemoteNamingFailureKind.Canceled)]
        public void NonVisibleFailureKindCannotEndActiveRequest(RemoteNamingFailureKind kind)
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            Assert.IsFalse(session.TryFail("request-1", kind, 10f));
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-1", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);
        }

        [Test]
        public void WrongLateAndDuplicateFailuresCannotChangeSession()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            Assert.IsFalse(session.TryFail(
                "wrong-request",
                RemoteNamingFailureKind.Timeout,
                10f));
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-1", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);

            Assert.IsTrue(session.TryFail(
                "request-1",
                RemoteNamingFailureKind.Busy,
                10f));
            Assert.IsFalse(session.TryFail(
                "request-1",
                RemoteNamingFailureKind.Authentication,
                11f));
            Assert.IsFalse(session.TryCancel("request-1"));
            Assert.IsFalse(session.TryAccept(
                new RemoteNameResponse("1", "request-1", true, "late mug"),
                11f));
            Assert.IsFalse(session.IsRequestActive);
            Assert.IsNull(session.ActiveRequestId);
            Assert.AreEqual("Mac busy — try again", session.PresentationText);
        }

        [Test]
        public void CanceledRequestRejectsItsLateSuccessAndAllowsNextRequest()
        {
            var session = new RemoteNamingSession();
            Assert.IsTrue(session.TryBegin(true, false, "request-1"));
            Assert.IsTrue(session.TryCancel("request-1"));

            Assert.IsFalse(session.TryAccept(
                new RemoteNameResponse("1", "request-1", true, "late mug"),
                10f));
            Assert.IsFalse(session.IsRequestActive);
            Assert.IsNull(session.PresentationText);

            Assert.IsTrue(session.TryBegin(true, false, "request-2"));
            Assert.AreEqual("request-2", session.ActiveRequestId);
        }

        [TestCase(RemoteNamingFailureKind.Timeout)]
        [TestCase(RemoteNamingFailureKind.Busy)]
        [TestCase(RemoteNamingFailureKind.NotFound)]
        [TestCase(RemoteNamingFailureKind.Authentication)]
        [TestCase(RemoteNamingFailureKind.InvalidPayload)]
        [TestCase(RemoteNamingFailureKind.InvalidResponse)]
        [TestCase(RemoteNamingFailureKind.ModelUnavailable)]
        [TestCase(RemoteNamingFailureKind.Connectivity)]
        public void DeliberateBeginImmediatelyAfterFailureReplacesPresentation(
            RemoteNamingFailureKind kind)
        {
            var session = new RemoteNamingSession();
            Assert.IsTrue(session.TryBegin(true, false, "request-1"));
            Assert.IsTrue(session.TryFail("request-1", kind, 10f));

            Assert.IsTrue(session.TryBegin(true, false, "request-2"));
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-2", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);
        }

        [Test]
        public void DeliberateBeginImmediatelyAfterCancelShowsIdentifying()
        {
            var session = new RemoteNamingSession();
            Assert.IsTrue(session.TryBegin(true, false, "request-1"));
            Assert.IsTrue(session.TryCancel("request-1"));

            Assert.IsTrue(session.TryBegin(true, false, "request-2"));
            Assert.IsTrue(session.IsRequestActive);
            Assert.AreEqual("request-2", session.ActiveRequestId);
            Assert.AreEqual("Identifying...", session.PresentationText);
        }

        [UnityTest]
        public IEnumerator MismatchedNotFoundResponseIsInvalidAndAllowsLaterRequest()
        {
            var fixtureRoot = new GameObject("RemoteNamingResponseFixture");
            fixtureRoot.SetActive(false);
            SingleResponseServer server = null;

            try
            {
                var controllerObject = new GameObject("RemoteNamingController");
                controllerObject.transform.SetParent(fixtureRoot.transform, false);
                var controller = controllerObject.AddComponent<RemoteNamingController>();

                var managerObject = new GameObject("SentisInferenceUiManager");
                managerObject.transform.SetParent(fixtureRoot.transform, false);
                var manager = managerObject.AddComponent<SentisInferenceUiManager>();

                var contentObject = new GameObject("Content", typeof(RectTransform));
                contentObject.transform.SetParent(fixtureRoot.transform, false);
                var templateObject = new GameObject("LabelTemplate", typeof(RectTransform));
                templateObject.transform.SetParent(contentObject.transform, false);
                var textObject = new GameObject(
                    "Label",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Text));
                textObject.transform.SetParent(templateObject.transform, false);
                templateObject.SetActive(false);

                var managerProperties = new SerializedObject(manager);
                managerProperties.FindProperty("m_detectionBoxPrefab").objectReferenceValue =
                    templateObject.GetComponent<RectTransform>();
                managerProperties.ApplyModifiedPropertiesWithoutUndo();

                var controllerProperties = new SerializedObject(controller);
                controllerProperties.FindProperty("m_uiInference").objectReferenceValue = manager;
                controllerProperties.ApplyModifiedPropertiesWithoutUndo();
                fixtureRoot.SetActive(true);

                var operationId = Guid.NewGuid();
                var requestId = operationId.ToString("N");
                var session = GetPrivateField<RemoteNamingSession>(controller, "m_session");
                Assert.IsTrue(session.TryBegin(true, false, requestId));
                SetPrivateField(controller, "m_activeRequestId", requestId);
                SetPrivateField(controller, "m_activeOperationId", operationId);
                Assert.IsTrue(manager.CreatePendingRemoteLabel(operationId, Vector3.one));

                server = new SingleResponseServer(
                    "{\"protocol_version\":\"1\",\"request_id\":\"different-request\",\"found\":false}");
                Assert.IsTrue(RemoteRecognitionConfig.TryParse(
                    "{\"protocol_version\":\"1\",\"mac_base_url\":\"" + server.BaseUrl +
                    "\",\"bearer_token\":\"test-token\",\"request_timeout_seconds\":8}",
                    out var config,
                    out var configError),
                    configError.ToString());

                var sendNameRequest = typeof(RemoteNamingController).GetMethod(
                    "SendNameRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(sendNameRequest);
                var requestRoutine = (IEnumerator)sendNameRequest.Invoke(
                    controller,
                    new object[] { config, requestId, operationId, new byte[] { 1, 2, 3 } });

                while (requestRoutine.MoveNext())
                {
                    yield return requestRoutine.Current;
                }

                while (!server.Completion.IsCompleted)
                {
                    yield return null;
                }
                server.Completion.GetAwaiter().GetResult();

                Assert.IsFalse(session.IsRequestActive);
                Assert.AreEqual("Couldn’t identify — try again", session.PresentationText);
                Assert.IsFalse(
                    contentObject.transform.Cast<Transform>().Any(child => child.gameObject.activeSelf));
                Assert.IsTrue(session.TryBegin(true, false, "later-request"));
            }
            finally
            {
                server?.Dispose();
                UnityEngine.Object.DestroyImmediate(fixtureRoot);
            }
        }

        [Test]
        public void ShippedSceneWiresRemoteNamingAndDisablesContinuousSentis()
        {
            const string scenePath =
                "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            try
            {
                var sceneBehaviours = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .ToArray();
                var detectionManager = sceneBehaviours.OfType<DetectionManager>().Single();
                var remoteNaming = detectionManager.GetComponent<RemoteNamingController>();
                var uiInference = sceneBehaviours.OfType<SentisInferenceUiManager>().Single();
                var runManager = sceneBehaviours.OfType<SentisInferenceRunManager>().Single();
                var ovrManager = sceneBehaviours.Single(
                    behaviour => behaviour.GetType().Name == "OVRManager");
                var reticle = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .Single(transform => transform.name == "RemoteAimReticle");
                var reticleText = reticle.GetComponentInChildren<UnityEngine.UI.Text>(true);

                Assert.IsNotNull(remoteNaming);

                Assert.AreEqual("CenterEyeAnchor", reticle.parent.name);
                Assert.AreEqual(0f, reticle.localPosition.x, 0.0001f);
                Assert.AreEqual(0f, reticle.localPosition.y, 0.0001f);
                Assert.AreEqual(
                    RemoteAimPlacement.ReferenceDistance,
                    reticle.localPosition.z,
                    0.0001f);
                Assert.AreEqual(
                    Vector3.one * RemoteAimPlacement.AuthoredScaleAtReferenceDistance,
                    reticle.localScale);
                Assert.IsNotNull(reticleText);
                Assert.AreEqual("•", reticleText.text);
                Assert.IsFalse(reticleText.raycastTarget);

                var ovrManagerObject = new SerializedObject(ovrManager);
                var simultaneousEnabled = ovrManagerObject.FindProperty(
                    "SimultaneousHandsAndControllersEnabled");
                var launchSimultaneous = ovrManagerObject.FindProperty(
                    "launchSimultaneousHandsControllersOnStartup");
                Assert.IsNotNull(simultaneousEnabled);
                Assert.IsNotNull(launchSimultaneous);
                Assert.IsTrue(simultaneousEnabled.boolValue);
                Assert.IsTrue(launchSimultaneous.boolValue);

                var detectionManagerObject = new SerializedObject(detectionManager);
                Assert.AreSame(
                    remoteNaming,
                    detectionManagerObject.FindProperty("m_remoteNaming").objectReferenceValue);

                var aimProperty = detectionManagerObject.FindProperty("m_aimReticle");
                Assert.AreSame(reticle.gameObject, aimProperty.objectReferenceValue);

                var remoteNamingObject = new SerializedObject(remoteNaming);
                Assert.IsNotNull(
                    remoteNamingObject.FindProperty("m_readinessController").objectReferenceValue);
                Assert.IsNotNull(
                    remoteNamingObject.FindProperty("m_menuManager").objectReferenceValue);
                Assert.AreSame(
                    uiInference,
                    remoteNamingObject.FindProperty("m_uiInference").objectReferenceValue);
                Assert.IsFalse(runManager.enabled);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void ShippedSceneWiresTheCropSquareUnderTheSameAnchorAsTheDot()
        {
            const string scenePath =
                "Assets/PassthroughCameraApiSamples/MultiObjectDetection/MultiObjectDetection.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            try
            {
                var square = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .Single(transform => transform.name == "RemoteCropSquare");
                var detectionManager = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                    .OfType<DetectionManager>()
                    .Single();

                // Same parent as RemoteAimReticle. The square is placed in world
                // space every frame, so the parent only matters for the authored
                // fallback -- but a DIFFERENT parent would mean the two overlays
                // disagree about their fallback, which is a wiring mistake worth
                // catching here.
                Assert.AreEqual("CenterEyeAnchor", square.parent.name);
                Assert.IsFalse(square.gameObject.activeSelf);

                // Eight bracket arms: four corners, two arms each.
                var arms = square.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                Assert.AreEqual(8, arms.Length);

                // The prefab's authored edge must equal the constant the
                // controller divides by, or every square is scaled wrong by
                // exactly that ratio.
                var rect = square.GetComponent<RectTransform>();
                Assert.AreEqual(
                    RemoteCropSquarePlacement.CanvasEdgeUnits, rect.sizeDelta.x, 0.0001f);
                Assert.AreEqual(
                    RemoteCropSquarePlacement.CanvasEdgeUnits, rect.sizeDelta.y, 0.0001f);

                var serialized = new SerializedObject(detectionManager);
                Assert.AreSame(
                    square.gameObject,
                    serialized.FindProperty("m_cropSquare").objectReferenceValue);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}");
            return (T)field.GetValue(instance);
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            var field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing private field {fieldName}");
            field.SetValue(instance, value);
        }

        private sealed class SingleResponseServer : IDisposable
        {
            private readonly TcpListener m_listener;

            public SingleResponseServer(string responseBody)
            {
                m_listener = new TcpListener(IPAddress.Loopback, 0);
                m_listener.Start();
                var port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
                BaseUrl = $"http://127.0.0.1:{port}";
                Completion = Task.Run(async () =>
                {
                    using (var client = await m_listener.AcceptTcpClientAsync())
                    using (var stream = client.GetStream())
                    {
                        var requestBuffer = new byte[8192];
                        await stream.ReadAsync(requestBuffer, 0, requestBuffer.Length);

                        var body = Encoding.UTF8.GetBytes(responseBody);
                        var headers = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/json\r\n" +
                            $"Content-Length: {body.Length}\r\n" +
                            "Connection: close\r\n\r\n");
                        await stream.WriteAsync(headers, 0, headers.Length);
                        await stream.WriteAsync(body, 0, body.Length);
                        await stream.FlushAsync();
                    }
                });
            }

            public string BaseUrl { get; }
            public Task Completion { get; }

            public void Dispose() => m_listener.Stop();
        }
    }
}
