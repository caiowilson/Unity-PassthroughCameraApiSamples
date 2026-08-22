// Spatial-anchor Restoration Task 2 — tests for atomic device-local
// snapshot storage. Task 3's coordinator will call TryLoad on startup and
// TrySave/TryDelete on every commit/untag/clear/reset; this fixture proves
// the store's on-disk contract in isolation from that lifecycle.
//
// Deliberately uses real files under a per-test temp directory rather than
// Application.persistentDataPath (not reliably writable/isolated in
// EditMode) or in-memory stand-ins for "corrupt" payloads — the point of
// TryLoad's validation is to survive bytes that actually came from disk.

using System;
using System.IO;
using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace ObjectTagger.Tests.EditMode
{
    public class SpatialLabelSnapshotStoreTests
    {
        private string m_directory;
        private string m_path;

        [SetUp]
        public void SetUp()
        {
            m_directory = Path.Combine(Path.GetTempPath(), $"object-tagger-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(m_directory);
            m_path = Path.Combine(m_directory, "spatial-label-snapshot.json");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(m_directory))
                {
                    Directory.Delete(m_directory, true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup only; leftover temp dirs don't affect other tests.
            }
        }

        private static SpatialLabelSnapshot ValidSnapshot()
        {
            return new SpatialLabelSnapshot
            {
                anchorUuid = Guid.NewGuid().ToString(),
                labels = new[]
                {
                    new SpatialLabelEntry
                    {
                        id = Guid.NewGuid().ToString(),
                        classId = 3,
                        className = "chair",
                        score = 0.87f,
                        localPosition = new Vector3(1f, 2f, 3f)
                    }
                }
            };
        }

        [Test]
        public void TrySaveThenTryLoadRoundTripsAValidSnapshot()
        {
            var snapshot = ValidSnapshot();

            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_path, snapshot));
            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));

            Assert.IsNotNull(loaded);
            Assert.AreEqual(snapshot.anchorUuid, loaded.anchorUuid);
            Assert.AreEqual(1, loaded.labels.Length);
            Assert.AreEqual(snapshot.labels[0].id, loaded.labels[0].id);
            Assert.AreEqual(snapshot.labels[0].className, loaded.labels[0].className);
            Assert.AreEqual(snapshot.labels[0].localPosition, loaded.labels[0].localPosition);
        }

        [Test]
        public void TrySaveLeavesNoTemporaryFileBehindOnSuccess()
        {
            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_path, ValidSnapshot()));

            var leftovers = Directory.GetFiles(m_directory, "*.tmp", SearchOption.AllDirectories);
            Assert.IsEmpty(leftovers);
        }

        [Test]
        public void TryLoadReturnsFalseWhenTheFileIsMissing()
        {
            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));
            Assert.IsNull(loaded);
        }

        [Test]
        public void TryLoadReturnsFalseForACorruptFile()
        {
            File.WriteAllText(m_path, "{ this is not valid json");

            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));
            Assert.IsNull(loaded);
        }

        [Test]
        public void TryLoadReturnsFalseForATruncatedFile()
        {
            var json = JsonUtility.ToJson(ValidSnapshot());
            File.WriteAllText(m_path, json.Substring(0, json.Length / 2));

            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));
            Assert.IsNull(loaded);
        }

        [Test]
        public void TryLoadReturnsFalseForAnUnsupportedVersion()
        {
            var snapshot = ValidSnapshot();
            snapshot.version = SpatialLabelSnapshot.CurrentVersion + 1;
            File.WriteAllText(m_path, JsonUtility.ToJson(snapshot));

            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));
            Assert.IsNull(loaded);
        }

        [Test]
        public void TryLoadReturnsFalseForAnInvalidAnchorUuid()
        {
            var snapshot = ValidSnapshot();
            snapshot.anchorUuid = "not-a-uuid";
            File.WriteAllText(m_path, JsonUtility.ToJson(snapshot));

            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));
            Assert.IsNull(loaded);
        }

        [Test]
        public void TryLoadReturnsFalseForAnInvalidLabel()
        {
            var snapshot = ValidSnapshot();
            snapshot.labels[0].id = "not-a-uuid";
            File.WriteAllText(m_path, JsonUtility.ToJson(snapshot));

            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));
            Assert.IsNull(loaded);
        }

        [Test]
        public void TrySaveRejectsAnInvalidSnapshotAndWritesNoFile()
        {
            var snapshot = ValidSnapshot();
            snapshot.anchorUuid = "not-a-uuid";

            Assert.IsFalse(SpatialLabelSnapshotStore.TrySave(m_path, snapshot));
            Assert.IsFalse(File.Exists(m_path));
        }

        [Test]
        public void TrySaveReplacesAnExistingFileWithNewContent()
        {
            var first = ValidSnapshot();
            var second = ValidSnapshot();

            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_path, first));
            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_path, second));
            Assert.IsTrue(SpatialLabelSnapshotStore.TryLoad(m_path, out var loaded));

            Assert.AreEqual(second.anchorUuid, loaded.anchorUuid);
            Assert.AreNotEqual(first.anchorUuid, loaded.anchorUuid);
        }

        [Test]
        public void TrySaveCleansUpItsTemporaryFileWhenTheFinalReplaceFails()
        {
            // Make the destination an existing directory so writing the temp
            // file next to it succeeds but the final replace/move step fails
            // for real, at the filesystem level — not a mocked failure.
            Directory.CreateDirectory(m_path);

            var succeeded = SpatialLabelSnapshotStore.TrySave(m_path, ValidSnapshot());

            Assert.IsFalse(succeeded);
            Assert.IsTrue(Directory.Exists(m_path), "destination directory must be left untouched");
            var leftovers = Directory.GetFiles(m_directory, "*.tmp", SearchOption.AllDirectories);
            Assert.IsEmpty(leftovers, "a failed save must not leave a temp file behind");
        }

        [Test]
        public void TryDeleteRemovesAnExistingFile()
        {
            Assert.IsTrue(SpatialLabelSnapshotStore.TrySave(m_path, ValidSnapshot()));

            Assert.IsTrue(SpatialLabelSnapshotStore.TryDelete(m_path));
            Assert.IsFalse(File.Exists(m_path));
        }

        [Test]
        public void TryDeleteIsIdempotentWhenTheFileIsAlreadyGone()
        {
            Assert.IsTrue(SpatialLabelSnapshotStore.TryDelete(m_path));
        }

        [TestCase(null)]
        [TestCase("")]
        public void TryLoadReturnsFalseForANullOrEmptyPath(string path)
        {
            Assert.IsFalse(SpatialLabelSnapshotStore.TryLoad(path, out var loaded));
            Assert.IsNull(loaded);
        }

        [TestCase(null)]
        [TestCase("")]
        public void TrySaveReturnsFalseForANullOrEmptyPath(string path)
        {
            Assert.IsFalse(SpatialLabelSnapshotStore.TrySave(path, ValidSnapshot()));
        }

        [TestCase(null)]
        [TestCase("")]
        public void TryDeleteReturnsFalseForANullOrEmptyPath(string path)
        {
            Assert.IsFalse(SpatialLabelSnapshotStore.TryDelete(path));
        }
    }
}
