using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class RemoteNamingFailureIntegrationTests
    {
        [TestCase(UnityWebRequest.Result.ConnectionError, 0, 7.999f, RemoteNamingFailureKind.Connectivity)]
        [TestCase(UnityWebRequest.Result.ConnectionError, 0, 8f, RemoteNamingFailureKind.Timeout)]
        [TestCase(UnityWebRequest.Result.ConnectionError, 0, 9f, RemoteNamingFailureKind.Timeout)]
        [TestCase(UnityWebRequest.Result.DataProcessingError, 200, 1f, RemoteNamingFailureKind.InvalidResponse)]
        public void TransportClassificationUsesOnlyResultStatusAndElapsedDeadline(
            UnityWebRequest.Result result,
            long statusCode,
            float elapsedSeconds,
            RemoteNamingFailureKind expected)
        {
            Assert.AreEqual(
                expected,
                RemoteNamingTransportClassifier.Classify(
                    result,
                    statusCode,
                    elapsedSeconds,
                    RemoteRecognitionConfig.RequiredRequestTimeoutSeconds));
        }

        [UnityTest]
        public IEnumerator LoopbackResponsesRemoveTheExactPendingCardAndShowSafeFailure(
            [ValueSource(nameof(ResponseCases))] ResponseCase responseCase)
        {
            using (var fixture = new ControllerFixture())
            {
                var operationId = fixture.BeginPendingOperation(Vector3.one);
                var requestId = operationId.ToString("N");
                using (var server = new LoopbackServer(
                           responseCase.StatusCode,
                           responseCase.Body.Replace("$REQUEST_ID$", requestId)))
                {
                    var routine = fixture.CreateRequestRoutine(
                        server.BaseUrl,
                        requestId,
                        operationId);

                    Assert.IsTrue(routine.MoveNext());
                    var request = fixture.LiveRequest;
                    yield return routine.Current;
                    while (routine.MoveNext())
                    {
                        yield return routine.Current;
                    }
                    yield return WaitFor(server.Completion);
                    AssertRequestDisposed(request);
                }

                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.AreEqual(responseCase.ExpectedPresentation, fixture.Session.PresentationText);
                Assert.AreEqual(0, fixture.ActiveCardCount);
                Assert.IsNull(fixture.LiveRequest);

                if (responseCase.ExpectedReadiness.HasValue)
                {
                    Assert.AreEqual(responseCase.ExpectedReadiness.Value, fixture.Readiness.Current.Kind);
                }
                else
                {
                    Assert.AreEqual(CompanionReadinessKind.Ready, fixture.Readiness.Current.Kind);
                }
            }
        }

        [UnityTest]
        public IEnumerator EarlyConnectionLossIsConnectivityAndLeavesNoLabel()
        {
            using (var fixture = new ControllerFixture())
            {
                var operationId = fixture.BeginPendingOperation(Vector3.one);
                var requestId = operationId.ToString("N");
                var unusedPort = ReserveUnusedPort();
                var routine = fixture.CreateRequestRoutine(
                    $"http://127.0.0.1:{unusedPort}",
                    requestId,
                    operationId);

                Assert.IsTrue(routine.MoveNext());
                var request = fixture.LiveRequest;
                yield return routine.Current;
                while (routine.MoveNext())
                {
                    yield return routine.Current;
                }

                AssertRequestDisposed(request);
                Assert.AreEqual("Mac unavailable — try again", fixture.Session.PresentationText);
                Assert.AreEqual(CompanionReadinessKind.Unavailable, fixture.Readiness.Current.Kind);
                Assert.AreEqual(0, fixture.ActiveCardCount);
                Assert.IsNull(fixture.LiveRequest);
            }
        }

        [UnityTest]
        [Timeout(15000)]
        public IEnumerator DeadlineExceededOnLoopbackIsTimeoutAndLeavesNoLabel()
        {
            using (var fixture = new ControllerFixture())
            using (var server = new LoopbackServer(
                       200,
                       Success("unused", "late object"),
                       TimeSpan.FromSeconds(9)))
            {
                var operationId = fixture.BeginPendingOperation(Vector3.one);
                var requestId = operationId.ToString("N");
                var routine = fixture.CreateRequestRoutine(
                    server.BaseUrl,
                    requestId,
                    operationId);

                Assert.IsTrue(routine.MoveNext());
                var request = fixture.LiveRequest;
                yield return routine.Current;
                while (routine.MoveNext())
                {
                    yield return routine.Current;
                }

                AssertRequestDisposed(request);
                Assert.AreEqual("Timed out — try again", fixture.Session.PresentationText);
                Assert.AreEqual(CompanionReadinessKind.Ready, fixture.Readiness.Current.Kind);
                Assert.AreEqual(0, fixture.ActiveCardCount);
                Assert.IsNull(fixture.LiveRequest);
            }
        }

        [UnityTest]
        public IEnumerator CancelInvalidatesBeforeAbortAndOldCompletionCannotTouchNewerCard()
        {
            using (var fixture = new ControllerFixture())
            {
                var oldOperationId = fixture.BeginPendingOperation(Vector3.one);
                var oldRequestId = oldOperationId.ToString("N");
                using (var server = new LoopbackServer(
                           200,
                           Success(oldRequestId, "old object"),
                           Timeout.InfiniteTimeSpan))
                {
                    var oldRoutine = fixture.CreateRequestRoutine(
                        server.BaseUrl,
                        oldRequestId,
                        oldOperationId);

                    Assert.IsTrue(oldRoutine.MoveNext());
                    yield return WaitFor(server.RequestReceived);
                    var oldRequest = fixture.LiveRequest;

                    Assert.IsTrue(fixture.Controller.TryCancelPending());
                    AssertRequestDisposed(oldRequest);
                    Assert.IsFalse(fixture.Session.IsRequestActive);
                    Assert.IsNull(fixture.Session.PresentationText);
                    Assert.IsNull(fixture.LiveRequest);
                    Assert.AreEqual(0, fixture.ActiveCardCount);

                    var newOperationId = fixture.BeginPendingOperation(Vector3.right);
                    var newRequestId = newOperationId.ToString("N");
                    Assert.AreEqual(1, fixture.ActiveCardCount);

                    Assert.IsFalse(fixture.Terminate(
                        oldRequestId,
                        oldOperationId,
                        RemoteNamingFailureKind.InvalidResponse));
                    Assert.IsFalse(fixture.Terminate(
                        oldRequestId,
                        newOperationId,
                        RemoteNamingFailureKind.InvalidResponse));
                    Assert.IsFalse(fixture.Terminate(
                        newRequestId,
                        oldOperationId,
                        RemoteNamingFailureKind.InvalidResponse));
                    Assert.IsFalse(fixture.Terminate(
                        oldRequestId,
                        oldOperationId,
                        RemoteNamingFailureKind.InvalidResponse));

                    server.ReleaseResponse();
                    var asyncOperation = (UnityWebRequestAsyncOperation)oldRoutine.Current;
                    while (!asyncOperation.isDone)
                    {
                        yield return null;
                    }
                    while (oldRoutine.MoveNext())
                    {
                        yield return oldRoutine.Current;
                    }

                    Assert.IsTrue(fixture.Session.IsRequestActive);
                    Assert.AreEqual(newRequestId, fixture.Session.ActiveRequestId);
                    Assert.AreEqual("Identifying...", fixture.Session.PresentationText);
                    Assert.AreEqual(1, fixture.ActiveCardCount);
                    Assert.IsTrue(fixture.Manager.CommitRemoteLabel(newOperationId, "new object"));
                }
            }
        }

        [UnityTest]
        public IEnumerator EveryTerminalKindAllowsARealControllerRetry(
            [ValueSource(nameof(TerminalFailures))] RemoteNamingFailureKind failure)
        {
            using (var fixture = new ControllerFixture())
            using (var successServer = new LoopbackServer(
                       200,
                       string.Empty,
                       Timeout.InfiniteTimeSpan))
            {
                fixture.SetConfig(successServer.BaseUrl);
                var failedOperationId = fixture.BeginPendingOperation(Vector3.one);
                var failedRequestId = failedOperationId.ToString("N");
                Assert.IsTrue(fixture.Terminate(
                    failedRequestId,
                    failedOperationId,
                    failure));
                Assert.AreEqual(0, fixture.ActiveCardCount);

                switch (failure)
                {
                    case RemoteNamingFailureKind.Authentication:
                        Assert.AreEqual(
                            CompanionReadinessKind.Misconfigured,
                            fixture.Readiness.Current.Kind);
                        break;
                    case RemoteNamingFailureKind.ModelUnavailable:
                    case RemoteNamingFailureKind.Connectivity:
                        Assert.AreEqual(
                            CompanionReadinessKind.Unavailable,
                            fixture.Readiness.Current.Kind);
                        break;
                }
                Assert.That(
                    fixture.PanelText,
                    Does.Contain(fixture.Readiness.Current.Message));
                Assert.IsTrue(fixture.Controller.CanAttemptNaming);
                Assert.IsTrue(RemoteNamingInputPolicy.ShouldShowAimReticle(
                    true,
                    true,
                    false,
                    false,
                    fixture.Controller.CanAttemptNaming));

                var frame = new Texture2D(2, 2, TextureFormat.RGB24, false);
                try
                {
                    Assert.IsTrue(fixture.Controller.TryStartAtResolvedPoint(frame, Vector3.right));
                    Assert.AreEqual(
                        $"Identifying...\n{fixture.Readiness.Current.Message}",
                        fixture.PanelText);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(frame);
                }

                var retryRequestId = fixture.Session.ActiveRequestId;
                var retryRequest = fixture.LiveRequest;
                successServer.ReleaseResponse(Success(retryRequestId, "coffee mug"));
                yield return WaitFor(successServer.Completion);
                while (fixture.Session.IsRequestActive)
                {
                    yield return null;
                }
                AssertRequestDisposed(retryRequest);

                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.AreEqual("coffee mug", fixture.Session.PresentationText);
                Assert.AreEqual(1, fixture.ActiveCardCount);
                Assert.AreEqual("coffee mug", fixture.ActiveCardText);
                Assert.IsNull(fixture.LiveRequest);
            }
        }

        [Test]
        public void CaptureFailureAndOversizePayloadRemovePendingState()
        {
            using (var fixture = new ControllerFixture())
            {
                var captureFailure = fixture.BeginPendingOperation(Vector3.one);
                Assert.IsFalse(fixture.ContinueAfterCapture(captureFailure, null));
                Assert.AreEqual("Couldn’t identify — try again", fixture.Session.PresentationText);
                Assert.AreEqual(0, fixture.ActiveCardCount);

                var oversize = fixture.BeginPendingOperation(Vector3.right);
                Assert.IsFalse(fixture.ContinueAfterCapture(oversize, new byte[(1024 * 1024) + 1]));
                Assert.AreEqual("Couldn’t identify — try again", fixture.Session.PresentationText);
                Assert.AreEqual(0, fixture.ActiveCardCount);
            }
        }

        [Test]
        public void SecondStartWhileIdentifyingCreatesNoCardOrTransport()
        {
            using (var fixture = new ControllerFixture())
            {
                var firstOperationId = fixture.BeginPendingOperation(Vector3.one);
                var frame = new Texture2D(2, 2, TextureFormat.RGB24, false);
                try
                {
                    Assert.IsFalse(fixture.Controller.TryStartAtResolvedPoint(frame, Vector3.right));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(frame);
                }

                Assert.AreEqual(firstOperationId.ToString("N"), fixture.Session.ActiveRequestId);
                Assert.AreEqual(1, fixture.ActiveCardCount);
                Assert.IsNull(fixture.LiveRequest);
            }
        }

        [Test]
        public void InitialAttemptRequiresConfigurationAndSuccessfulHealth()
        {
            using (var fixture = new ControllerFixture(false))
            {
                var frame = new Texture2D(2, 2, TextureFormat.RGB24, false);
                try
                {
                    Assert.AreEqual(CompanionReadinessKind.Loading, fixture.Readiness.Current.Kind);
                    Assert.IsFalse(fixture.Controller.CanAttemptNaming);
                    Assert.IsFalse(RemoteNamingInputPolicy.ShouldShowAimReticle(
                        true,
                        true,
                        false,
                        false,
                        fixture.Controller.CanAttemptNaming));
                    Assert.IsFalse(fixture.Controller.TryStartAtResolvedPoint(frame, Vector3.right));

                    fixture.PublishReady();
                    Assert.IsTrue(fixture.Controller.CanAttemptNaming);

                    fixture.ClearConfig();
                    fixture.PublishInvalidConfiguration();

                    Assert.AreEqual(CompanionReadinessKind.Misconfigured, fixture.Readiness.Current.Kind);
                    Assert.IsFalse(fixture.Controller.CanAttemptNaming);
                    Assert.IsFalse(RemoteNamingInputPolicy.ShouldShowAimReticle(
                        true,
                        true,
                        false,
                        false,
                        fixture.Controller.CanAttemptNaming));
                    Assert.IsFalse(fixture.Controller.TryStartAtResolvedPoint(frame, Vector3.right));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(frame);
                }

                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.AreEqual(0, fixture.ActiveCardCount);
                Assert.IsNull(fixture.LiveRequest);
            }
        }

        [Test]
        public void BPressImmediatelyCancelsPendingAndConsumesQuickRelease()
        {
            using (var fixture = new ControllerFixture())
            {
                fixture.CreateCommittedLabel("kept object", Vector3.forward);
                var oldOperationId = fixture.BeginPendingOperation(Vector3.one);
                var oldRequestId = oldOperationId.ToString("N");

                fixture.InvokeDetectionManagerAction("HandleBPressStarted");

                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.AreEqual(1, fixture.ActiveCardCount);
                Assert.AreEqual("kept object", fixture.ActiveCardText);
                Assert.IsFalse(fixture.Terminate(
                    oldRequestId,
                    oldOperationId,
                    RemoteNamingFailureKind.InvalidResponse));
                Assert.IsFalse(fixture.Manager.CommitRemoteLabel(oldOperationId, "late object"));

                fixture.InvokeDetectionManagerAction("HandleBRelease");

                Assert.AreEqual(1, fixture.ActiveCardCount);
                Assert.AreEqual("kept object", fixture.ActiveCardText);
            }
        }

        [Test]
        public void HeldBAfterPressCancellationClearsAtThresholdWithoutReleaseAction()
        {
            using (var fixture = new ControllerFixture())
            {
                fixture.CreateCommittedLabel("before hold", Vector3.forward);
                fixture.BeginPendingOperation(Vector3.one);

                fixture.InvokeDetectionManagerAction("HandleBPressStarted");
                Assert.AreEqual(1, fixture.ActiveCardCount);

                fixture.InvokeDetectionManagerAction("HandleBHoldThresholdReached");
                Assert.AreEqual(0, fixture.ActiveCardCount);
                Assert.IsFalse(fixture.Session.IsRequestActive);

                fixture.CreateCommittedLabel("after threshold", Vector3.forward);
                fixture.InvokeDetectionManagerAction("HandleBRelease");

                Assert.AreEqual(1, fixture.ActiveCardCount);
                Assert.AreEqual("after threshold", fixture.ActiveCardText);
            }
        }

        [Test]
        public void QuickBWithoutPendingStillRemovesNearestCommittedLabel()
        {
            using (var fixture = new ControllerFixture())
            {
                fixture.CreateCommittedLabel("remove me", Vector3.forward);

                fixture.InvokeDetectionManagerAction("HandleBPressStarted");
                fixture.InvokeDetectionManagerAction("HandleBRelease");

                Assert.AreEqual(0, fixture.ActiveCardCount);
            }
        }

        [TestCase("HandleRemoteAvailability", false, false)]
        [TestCase("HandleRemoteAvailability", true, true)]
        [TestCase("OnApplicationPause", true, false)]
        public void AnchorLossAndPausePathsCancelPendingWork(
            string methodName,
            bool firstArgument,
            bool secondArgument)
        {
            using (var fixture = new ControllerFixture())
            {
                fixture.BeginPendingOperation(Vector3.one);
                if (methodName == "HandleRemoteAvailability")
                {
                    fixture.InvokeDetectionManagerAction(methodName, firstArgument, secondArgument);
                }
                else
                {
                    InvokePrivate(fixture.Controller, methodName, firstArgument);
                }

                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.IsNull(fixture.Session.PresentationText);
                Assert.AreEqual(0, fixture.ActiveCardCount);
            }
        }

        [Test]
        public void DisableAndDestroyEachCancelPendingWork()
        {
            using (var fixture = new ControllerFixture())
            {
                fixture.BeginPendingOperation(Vector3.one);
                InvokePrivate(fixture.Controller, "OnDisable");
                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.AreEqual(0, fixture.ActiveCardCount);

                fixture.BeginPendingOperation(Vector3.right);
                InvokePrivate(fixture.Controller, "OnDestroy");
                UnityEngine.Object.DestroyImmediate(fixture.Controller);
                Assert.AreEqual(0, fixture.ActiveCardCount);
            }
        }

        [UnityTest]
        public IEnumerator DisableAndDestroyDisposeTheirLiveTransport(
            [Values("OnDisable", "OnDestroy")] string lifecycleMethod)
        {
            using (var fixture = new ControllerFixture())
            using (var server = new LoopbackServer(200, string.Empty, Timeout.InfiniteTimeSpan))
            {
                var operationId = fixture.BeginPendingOperation(Vector3.one);
                var requestId = operationId.ToString("N");
                var routine = fixture.CreateRequestRoutine(server.BaseUrl, requestId, operationId);
                Assert.IsTrue(routine.MoveNext());
                yield return WaitFor(server.RequestReceived);
                var request = fixture.LiveRequest;

                InvokePrivate(fixture.Controller, lifecycleMethod);

                AssertRequestDisposed(request);
                Assert.IsFalse(fixture.Session.IsRequestActive);
                Assert.IsNull(fixture.Session.PresentationText);
                Assert.AreEqual(0, fixture.ActiveCardCount);
                server.ReleaseResponse();
            }
        }

        private static readonly ResponseCase[] ResponseCases =
        {
            new ResponseCase(200, "{\"protocol_version\":\"1\",\"request_id\":\"$REQUEST_ID$\",\"found\":false}", "No object found — try again"),
            new ResponseCase(200, "{\"protocol_version\":\"1\",\"request_id\":\"$REQUEST_ID$\",\"found\":true,\"name\":\"Coffee Mug\"}", "Couldn’t identify — try again"),
            new ResponseCase(200, "{\"protocol_version\":\"1\",\"request_id\":\"wrong\",\"found\":false}", "Couldn’t identify — try again"),
            new ResponseCase(200, "{bad-json", "Couldn’t identify — try again"),
            new ResponseCase(401, Error("authentication_failed"), "Authentication failed — update config", CompanionReadinessKind.Misconfigured),
            new ResponseCase(409, Error("busy"), "Mac busy — try again"),
            new ResponseCase(400, Error("invalid_image"), "Couldn’t identify — try again"),
            new ResponseCase(503, Error("model_unavailable"), "Mac unavailable — try again", CompanionReadinessKind.Unavailable),
        };

        private static readonly RemoteNamingFailureKind[] TerminalFailures =
        {
            RemoteNamingFailureKind.Canceled,
            RemoteNamingFailureKind.Timeout,
            RemoteNamingFailureKind.Busy,
            RemoteNamingFailureKind.NotFound,
            RemoteNamingFailureKind.Authentication,
            RemoteNamingFailureKind.InvalidPayload,
            RemoteNamingFailureKind.InvalidResponse,
            RemoteNamingFailureKind.ModelUnavailable,
            RemoteNamingFailureKind.Connectivity,
        };

        private static string Error(string code)
        {
            return $"{{\"protocol_version\":\"1\",\"error\":{{\"code\":\"{code}\",\"message\":\"unsafe detail\"}}}}";
        }

        private static string Success(string requestId, string name)
        {
            return $"{{\"protocol_version\":\"1\",\"request_id\":\"{requestId}\",\"found\":true,\"name\":\"{name}\"}}";
        }

        private static IEnumerator WaitFor(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }
            task.GetAwaiter().GetResult();
        }

        private static int ReserveUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static object InvokePrivate(object instance, string methodName, params object[] arguments)
        {
            var method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Missing private method {methodName}");
            return method.Invoke(instance, arguments);
        }

        private static void AssertRequestDisposed(UnityWebRequest request)
        {
            Assert.IsNotNull(request);
            var pointer = typeof(UnityWebRequest).GetField(
                "m_Ptr",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(pointer, "UnityWebRequest disposal pointer field changed");
            Assert.AreEqual(IntPtr.Zero, (IntPtr)pointer.GetValue(request));
        }

        private sealed class ControllerFixture : IDisposable
        {
            private readonly GameObject m_root;
            private readonly Transform m_content;
            private readonly DetectionManager m_detectionManager;
            private readonly Text m_statusLabel;

            public ControllerFixture(bool publishInitialReady = true)
            {
                m_root = new GameObject("RemoteNamingFailureFixture");
                m_root.SetActive(false);

                var uiObject = new GameObject("Ui");
                uiObject.transform.SetParent(m_root.transform, false);
                var menu = uiObject.AddComponent<DetectionUiMenuManager>();
                var labelObject = new GameObject("Status", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                labelObject.transform.SetParent(uiObject.transform, false);
                m_statusLabel = labelObject.GetComponent<Text>();
                SetPrivateField(menu, "m_labelInformation", m_statusLabel);
                SetAutoProperty(menu, "IsPaused", false);
                menu.enabled = false;

                Readiness = uiObject.AddComponent<CompanionReadinessController>();
                Readiness.enabled = false;

                var managerObject = new GameObject("SentisInferenceUiManager");
                managerObject.transform.SetParent(m_root.transform, false);
                Manager = managerObject.AddComponent<SentisInferenceUiManager>();
                SetPrivateField(
                    Manager,
                    "m_cameraPoseOverride",
                    (Func<Pose?>)(() => new Pose(Vector3.zero, Quaternion.identity)));
                var contentObject = new GameObject("Content", typeof(RectTransform));
                contentObject.transform.SetParent(m_root.transform, false);
                m_content = contentObject.transform;
                var templateObject = new GameObject("LabelTemplate", typeof(RectTransform));
                templateObject.transform.SetParent(m_content, false);
                var cardText = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                cardText.transform.SetParent(templateObject.transform, false);
                templateObject.SetActive(false);
                var managerProperties = new SerializedObject(Manager);
                managerProperties.FindProperty("m_detectionBoxPrefab").objectReferenceValue =
                    templateObject.GetComponent<RectTransform>();
                managerProperties.ApplyModifiedPropertiesWithoutUndo();

                var controllerObject = new GameObject("RemoteNamingController");
                controllerObject.transform.SetParent(m_root.transform, false);
                Controller = controllerObject.AddComponent<RemoteNamingController>();
                SetPrivateField(Controller, "m_readinessController", Readiness);
                SetPrivateField(Controller, "m_menuManager", menu);
                SetPrivateField(Controller, "m_uiInference", Manager);

                var detectionObject = new GameObject("DetectionManager");
                detectionObject.transform.SetParent(m_root.transform, false);
                detectionObject.SetActive(false);
                m_detectionManager = detectionObject.AddComponent<DetectionManager>();
                SetPrivateField(m_detectionManager, "m_remoteNaming", Controller);
                SetPrivateField(m_detectionManager, "m_uiInference", Manager);
                SetPrivateField(m_detectionManager, "m_uiMenuManager", menu);

                m_root.SetActive(true);
                InvokePrivate(Readiness, "Awake");
                SetAutoProperty(menu, "IsPaused", false);
                InvokePrivate(
                    Readiness,
                    "Publish",
                    publishInitialReady ? CompanionReadiness.Ready() : CompanionReadiness.Loading());
                Assert.IsTrue(RemoteRecognitionConfig.TryParse(
                    "{\"protocol_version\":\"1\",\"mac_base_url\":\"http://127.0.0.1:1\"," +
                    "\"bearer_token\":\"test-token\",\"request_timeout_seconds\":8}",
                    out var config,
                    out var configError),
                    configError.ToString());
                SetAutoProperty(Readiness, "CurrentConfig", config);
            }

            public RemoteNamingController Controller { get; }
            public CompanionReadinessController Readiness { get; }
            public SentisInferenceUiManager Manager { get; }
            public RemoteNamingSession Session => GetPrivateField<RemoteNamingSession>(Controller, "m_session");
            public UnityWebRequest LiveRequest => GetPrivateField<UnityWebRequest>(Controller, "m_liveRequest");
            public int ActiveCardCount => m_content.Cast<Transform>().Count(child => child.gameObject.activeSelf);
            public string PanelText => m_statusLabel.text;
            public string ActiveCardText => m_content.Cast<Transform>()
                .Single(child => child.gameObject.activeSelf)
                .GetComponentInChildren<Text>(true).text;

            public Guid BeginPendingOperation(Vector3 point)
            {
                var operationId = Guid.NewGuid();
                var requestId = operationId.ToString("N");
                Assert.IsTrue(Session.TryBegin(true, false, requestId));
                SetPrivateField(Controller, "m_activeRequestId", requestId);
                SetPrivateField(Controller, "m_activeOperationId", (Guid?)operationId);
                Assert.IsTrue(Manager.CreatePendingRemoteLabel(operationId, point));
                return operationId;
            }

            public Guid CreateCommittedLabel(string name, Vector3 point)
            {
                var operationId = Guid.NewGuid();
                Assert.IsTrue(Manager.CreatePendingRemoteLabel(operationId, point));
                Assert.IsTrue(Manager.CommitRemoteLabel(operationId, name));
                return operationId;
            }

            public IEnumerator CreateRequestRoutine(
                string baseUrl,
                string requestId,
                Guid operationId)
            {
                Assert.IsTrue(RemoteRecognitionConfig.TryParse(
                    "{\"protocol_version\":\"1\",\"mac_base_url\":\"" + baseUrl +
                    "\",\"bearer_token\":\"test-token\",\"request_timeout_seconds\":8}",
                    out var config,
                    out var error),
                    error.ToString());
                var method = typeof(RemoteNamingController).GetMethod(
                    "SendNameRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(method);
                return (IEnumerator)method.Invoke(
                    Controller,
                    new object[] { config, requestId, operationId, new byte[] { 1, 2, 3 } });
            }

            public bool ContinueAfterCapture(Guid operationId, byte[] jpeg)
            {
                return (bool)InvokePrivate(
                    Controller,
                    "ContinueAfterCapture",
                    operationId.ToString("N"),
                    operationId,
                    jpeg);
            }

            public bool Terminate(
                string requestId,
                Guid operationId,
                RemoteNamingFailureKind failure)
            {
                return (bool)InvokePrivate(
                    Controller,
                    "TryTerminateOperation",
                    requestId,
                    operationId,
                    failure,
                    failure == RemoteNamingFailureKind.Canceled);
            }

            public void PublishReady()
            {
                InvokePrivate(Readiness, "Publish", CompanionReadiness.Ready());
            }

            public void PublishInvalidConfiguration()
            {
                InvokePrivate(Readiness, "Publish", CompanionReadiness.InvalidConfiguration());
            }

            public void ClearConfig()
            {
                SetAutoProperty(Readiness, "CurrentConfig", null);
            }

            public void SetConfig(string baseUrl)
            {
                Assert.IsTrue(RemoteRecognitionConfig.TryParse(
                    "{\"protocol_version\":\"1\",\"mac_base_url\":\"" + baseUrl +
                    "\",\"bearer_token\":\"test-token\",\"request_timeout_seconds\":8}",
                    out var config,
                    out var error),
                    error.ToString());
                SetAutoProperty(Readiness, "CurrentConfig", config);
            }

            public void InvokeDetectionManagerAction(string methodName, params object[] arguments)
            {
                InvokePrivate(m_detectionManager, methodName, arguments);
            }

            public void Dispose()
            {
                if (m_root != null)
                {
                    UnityEngine.Object.DestroyImmediate(m_root);
                }
            }
        }

        private sealed class LoopbackServer : IDisposable
        {
            private readonly TcpListener m_listener;
            private readonly TaskCompletionSource<bool> m_release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private string m_body;

            public LoopbackServer(long statusCode, string body, TimeSpan? delay = null)
            {
                m_body = body;
                m_listener = new TcpListener(IPAddress.Loopback, 0);
                m_listener.Start();
                var port = ((IPEndPoint)m_listener.LocalEndpoint).Port;
                BaseUrl = $"http://127.0.0.1:{port}";
                var requestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                RequestReceived = requestReceived.Task;
                if (delay != Timeout.InfiniteTimeSpan)
                {
                    m_release.TrySetResult(true);
                }

                Completion = Task.Run(async () =>
                {
                    try
                    {
                        using (var client = await m_listener.AcceptTcpClientAsync())
                        using (var stream = client.GetStream())
                        {
                            await ReadHeaders(stream);
                            requestReceived.TrySetResult(true);
                            await m_release.Task;
                            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                            {
                                await Task.Delay(delay.Value);
                            }

                            var responseBody = Encoding.UTF8.GetBytes(m_body);
                            var reason = statusCode == 200 ? "OK" : "Error";
                            var headers = Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                                "Content-Type: application/json\r\n" +
                                $"Content-Length: {responseBody.Length}\r\n" +
                                "Connection: close\r\n\r\n");
                            await stream.WriteAsync(headers, 0, headers.Length);
                            await stream.WriteAsync(responseBody, 0, responseBody.Length);
                            await stream.FlushAsync();
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is ObjectDisposedException ||
                        exception is SocketException)
                    {
                        // Client cancellation closes the loopback connection.
                    }
                });
            }

            public string BaseUrl { get; }
            public Task RequestReceived { get; private set; }
            public Task Completion { get; }

            public void ReleaseResponse(string body = null)
            {
                if (body != null)
                {
                    m_body = body;
                }
                m_release.TrySetResult(true);
            }

            public void Dispose()
            {
                m_release.TrySetResult(true);
                m_listener.Stop();
            }

            private static async Task ReadHeaders(NetworkStream stream)
            {
                var buffer = new byte[8192];
                var received = 0;
                while (received < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer, received, buffer.Length - received);
                    if (read == 0)
                    {
                        return;
                    }
                    received += read;
                    if (Encoding.ASCII.GetString(buffer, 0, received).Contains("\r\n\r\n"))
                    {
                        return;
                    }
                }
            }
        }

        public sealed class ResponseCase
        {
            public ResponseCase(
                long statusCode,
                string body,
                string expectedPresentation,
                CompanionReadinessKind? expectedReadiness = null)
            {
                StatusCode = statusCode;
                Body = body;
                ExpectedPresentation = expectedPresentation;
                ExpectedReadiness = expectedReadiness;
            }

            public long StatusCode { get; }
            public string Body { get; }
            public string ExpectedPresentation { get; }
            public CompanionReadinessKind? ExpectedReadiness { get; }

            public override string ToString() => $"HTTP {StatusCode}: {ExpectedPresentation}";
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

        private static void SetAutoProperty(object instance, string propertyName, object value)
        {
            SetPrivateField(instance, $"<{propertyName}>k__BackingField", value);
        }
    }
}
