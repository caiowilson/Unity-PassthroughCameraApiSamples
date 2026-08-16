// Object Tagger slice 5 Task 1 — tests for the plain-data label record.
//
// Narrow on purpose: Task 1's job is a state-shape change, not new matching,
// smoothing, or expiry logic (Tasks 2-3 own those). What has to be pinned here
// is the shape itself — that LabelRecord is reachable from edit-mode tests at
// all (see AssemblySeamTests.cs: this project rejects InternalsVisibleTo, so a
// nested `internal` type would have been unreachable from this assembly) and
// that it carries no Unity view type.

using System;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class LabelRecordTests
    {
        [Test]
        public void FieldsRoundTripPlainValues()
        {
            // Proves the type is plain data reachable from this assembly, and that
            // every field spec line 55 asks for is actually present.
            var record = new LabelRecord
            {
                SessionId = Guid.NewGuid(),
                ClassId = 3,
                ClassName = "chair",
                WorldPosition = new Vector3(1f, 2f, 3f),
                SmoothedPosition = new Vector3(1.1f, 2.1f, 3.1f),
                LastAssociatedScore = 0.87f,
                ConfirmationCount = 2,
                LastSeenTime = 12.5f
            };

            Assert.AreNotEqual(Guid.Empty, record.SessionId);
            Assert.AreEqual(3, record.ClassId);
            Assert.AreEqual("chair", record.ClassName);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), record.WorldPosition);
            Assert.AreEqual(new Vector3(1.1f, 2.1f, 3.1f), record.SmoothedPosition);
            Assert.AreEqual(0.87f, record.LastAssociatedScore);
            Assert.AreEqual(2, record.ConfirmationCount);
            Assert.AreEqual(12.5f, record.LastSeenTime);
        }

        [Test]
        public void HasNoRectTransformOrGameObjectField()
        {
            // THE WHOLE REASON THIS TYPE EXISTS. If a future edit adds a RectTransform
            // or GameObject back onto LabelRecord, it re-couples state to the view that
            // slice 5 exists to separate, and this test is what catches it.
            var fields = typeof(LabelRecord).GetFields();

            foreach (var field in fields)
            {
                Assert.IsFalse(
                    typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType),
                    $"LabelRecord.{field.Name} is a {field.FieldType} — LabelRecord must hold no Unity view/engine object, only plain data.");
            }
        }
    }
}
