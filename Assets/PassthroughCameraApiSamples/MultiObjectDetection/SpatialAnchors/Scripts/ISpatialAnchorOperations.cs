// Spatial-anchor Restoration Task 3 — the seam between the restoration state
// machine (SpatialAnchorRestorationCoordinator, pure and fully tested) and
// Meta's anchor SDK (MetaSpatialAnchorOperations, untestable off-device).
//
// EditMode tests cannot create an OVRSpatialAnchor, reach a headset's anchor
// store, or localize anything: there is no device and no Link. So every call
// that needs the SDK lives behind this interface, and the coordinator sees
// only "an operation is running / it succeeded / it failed". Tests supply a
// fake and drive every outcome; the runtime supplies
// MetaSpatialAnchorOperations.
//
// PUBLIC and top-level on purpose: this project rejects InternalsVisibleTo as
// a seam (see AssemblySeamTests.cs), so an internal or nested contract would
// be unreachable from the test assembly.
//
// The contract is deliberately start/poll rather than async: one operation at
// a time, started by a Begin* call that returns immediately, its outcome read
// back from Status. That keeps all coroutine and OVRTask handling inside the
// adapter and leaves the coordinator a synchronous state machine.

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum SpatialAnchorOperationStatus
    {
        /// No operation has been started yet.
        Idle,

        /// The most recently started operation is still in flight.
        Running,

        /// The most recently started operation finished successfully.
        Succeeded,

        /// The most recently started operation finished unsuccessfully.
        Failed
    }

    public interface ISpatialAnchorOperations
    {
        /// Outcome of the operation started by the most recent Begin* call.
        /// Anything other than Running means that operation is over; the
        /// coordinator treats every non-Succeeded terminal value as failure.
        SpatialAnchorOperationStatus Status { get; }

        /// Whether the bound anchor is currently being tracked. False when no
        /// anchor is bound.
        bool IsAnchorTracked { get; }

        /// The UUID of the currently bound anchor, if one is bound.
        bool TryGetBoundAnchorUuid(out string anchorUuid);

        /// Creates a fresh anchor on the shared label root, waits for it to
        /// localize, and saves it to the headset's anchor store.
        void BeginCreate();

        /// Loads exactly anchorUuid from the headset's anchor store, localizes
        /// it, and binds it to the shared label root. Never creates an anchor.
        void BeginRestore(string anchorUuid);

        /// Erases the saved anchor from the headset's anchor store — the bound
        /// component if there is one, otherwise anchorUuid. Succeeds when
        /// there is nothing to erase. This is the ONLY destructive operation
        /// in the contract, and the coordinator calls it only from
        /// RequestReset().
        void BeginErase(string anchorUuid);

        /// Drops the runtime OVRSpatialAnchor component. Must NOT erase
        /// anything: the saved anchor has to survive app teardown, which is
        /// the whole point of Task 3.
        void ReleaseRuntimeAnchor();
    }
}
