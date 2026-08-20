using System;
using System.Linq;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace ObjectTagger.Tests.EditMode
{
    public class SentisInferenceUiManagerRemoteLabelTests
    {
        private GameObject m_fixtureRoot;
        private Transform m_contentParent;
        private SentisInferenceUiManager m_manager;

        [SetUp]
        public void SetUp()
        {
            m_fixtureRoot = new GameObject("RemoteLabelFixture");
            m_fixtureRoot.SetActive(false);

            var managerObject = new GameObject("Manager");
            managerObject.transform.SetParent(m_fixtureRoot.transform, false);
            m_manager = managerObject.AddComponent<SentisInferenceUiManager>();

            var contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(m_fixtureRoot.transform, false);
            m_contentParent = contentObject.transform;

            var templateObject = new GameObject("LabelTemplate", typeof(RectTransform));
            templateObject.transform.SetParent(m_contentParent, false);
            var textObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textObject.transform.SetParent(templateObject.transform, false);
            templateObject.SetActive(false);

            var managerObjectProperties = new SerializedObject(m_manager);
            managerObjectProperties.FindProperty("m_detectionBoxPrefab").objectReferenceValue =
                templateObject.GetComponent<RectTransform>();
            managerObjectProperties.ApplyModifiedPropertiesWithoutUndo();

            m_fixtureRoot.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(m_fixtureRoot);
        }

        [Test]
        public void CreateCommitAndRemoveReuseTheSameSpatialCard()
        {
            var operationId = Guid.NewGuid();
            var point = new Vector3(1.25f, -0.5f, 2.75f);

            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(operationId, point));
            var card = m_contentParent.Cast<Transform>().Single(child => child.gameObject.activeSelf);
            Assert.AreEqual("Identifying…", card.GetComponentInChildren<Text>(true).text);
            Assert.AreEqual(point, card.position);

            Assert.IsTrue(m_manager.CommitRemoteLabel(operationId, "coffee mug"));
            Assert.AreSame(
                card,
                m_contentParent.Cast<Transform>().Single(child => child.gameObject.activeSelf));
            Assert.AreEqual("coffee mug", card.GetComponentInChildren<Text>(true).text);
            Assert.AreEqual(point, card.position);

            Assert.IsFalse(m_manager.CommitRemoteLabel(Guid.NewGuid(), "wrong"));
            Assert.IsTrue(m_manager.RemoveRemoteLabel(operationId));
            Assert.IsFalse(card.gameObject.activeSelf);
            Assert.IsFalse(m_manager.RemoveRemoteLabel(operationId));
        }

        [Test]
        public void EmptyAndDuplicateOperationIdsCannotCreateAdditionalCards()
        {
            var operationId = Guid.NewGuid();

            Assert.IsFalse(m_manager.CreatePendingRemoteLabel(Guid.Empty, Vector3.one));
            Assert.IsTrue(m_manager.CreatePendingRemoteLabel(operationId, Vector3.one));
            Assert.IsFalse(m_manager.CreatePendingRemoteLabel(operationId, Vector3.zero));
            Assert.AreEqual(
                1,
                m_contentParent.Cast<Transform>().Count(child => child.gameObject.activeSelf));
        }
    }
}
