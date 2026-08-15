// Object Tagger slice 2 Task 4 — assembly seam canary.
//
// This is deliberately NOT a behavioural test. Slice 2's job is to create the
// seam; slice 3 is the first slice with pure logic worth asserting.
//
// What it proves is exactly one thing, and it is the thing spec line 94 cares
// about: an edit-mode test assembly exists, compiles, and can see the runtime
// assembly. Before slice 2 there were no asmdefs at all, everything lived in
// Assembly-CSharp, and a test assembly could not reference it — so no test could
// compile regardless of what it asserted.
//
// WHAT IS STILL NOT TESTABLE, and why that is deferred rather than overlooked:
//   NonMaxSuppression is `private static`  (SentisInferenceRunManager.cs:168)
//   CalculateIoU     is `internal static`  (SentisInferenceRunManager.cs:222)
// `private` is unreachable from here, and `internal` is unreachable from a
// separate assembly without [assembly: InternalsVisibleTo]. So zero upstream
// behaviours are assertable yet. Slice 3 owns that: it extracts the pure
// decoding logic onto plain arrays so the tests its gate demands need no
// Inference Engine tensors, no Worker, and no live backend.
//
// Adding InternalsVisibleTo here instead would grant access while leaving the
// logic tangled with Tensor<T> parameters, which is the seam choice slice 3
// explicitly rejected.

using NUnit.Framework;
using PassthroughCameraSamples.MultiObjectDetection;

namespace ObjectTagger.Tests.EditMode
{
    public class AssemblySeamTests
    {
        [Test]
        public void TestAssemblyCanReferenceRuntimeAssembly()
        {
            // Resolving a public runtime type proves the asmdef reference chain
            // ObjectTagger.Tests.EditMode -> ObjectTagger.Runtime is wired.
            Assert.IsNotNull(typeof(SentisInferenceRunManager),
                "Test assembly cannot see ObjectTagger.Runtime; the asmdef reference is broken.");
        }

        [Test]
        public void RuntimeTypesRetainTheUpstreamNamespace()
        {
            // Task 1 decided to RETAIN namespace PassthroughCameraSamples.MultiObjectDetection
            // rather than rename, because a rename touches all eight persistent UnityEvent
            // calls and every serialized MonoBehaviour reference, and fails silently.
            // This pins that decision so an accidental rename fails a test instead of
            // surfacing as missing script references at runtime.
            Assert.AreEqual(
                "PassthroughCameraSamples.MultiObjectDetection",
                typeof(SentisInferenceRunManager).Namespace,
                "Runtime namespace changed. Slice 2 Task 1 decided to retain it; a rename needs its own task and its own gate re-run.");
        }
    }
}
