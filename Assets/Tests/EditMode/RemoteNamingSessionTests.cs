using System.Linq;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        public void MatchingNotFoundResponseIsUnsuccessfulAndReturnsToIdle()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            var accepted = session.TryAccept(
                new RemoteNameResponse("1", "request-1", false, null),
                10f);

            Assert.IsFalse(accepted);
            Assert.IsFalse(session.IsRequestActive);
            Assert.IsNull(session.PresentationText);
        }

        [Test]
        public void FailureOnlyReleasesTheMatchingActiveRequest()
        {
            var session = new RemoteNamingSession();
            session.TryBegin(true, false, "request-1");

            Assert.IsFalse(session.TryFail("request-2"));
            Assert.IsTrue(session.IsRequestActive);
            Assert.IsTrue(session.TryFail("request-1"));
            Assert.IsFalse(session.IsRequestActive);
            Assert.IsNull(session.PresentationText);
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
                var runManager = sceneBehaviours.OfType<SentisInferenceRunManager>().Single();

                Assert.IsNotNull(remoteNaming);

                var detectionManagerObject = new SerializedObject(detectionManager);
                Assert.AreSame(
                    remoteNaming,
                    detectionManagerObject.FindProperty("m_remoteNaming").objectReferenceValue);

                var remoteNamingObject = new SerializedObject(remoteNaming);
                Assert.IsNotNull(
                    remoteNamingObject.FindProperty("m_readinessController").objectReferenceValue);
                Assert.IsNotNull(
                    remoteNamingObject.FindProperty("m_menuManager").objectReferenceValue);
                Assert.IsFalse(runManager.enabled);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
