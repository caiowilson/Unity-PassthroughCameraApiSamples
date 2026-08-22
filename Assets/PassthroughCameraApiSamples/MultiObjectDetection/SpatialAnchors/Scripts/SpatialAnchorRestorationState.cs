// Spatial-anchor Restoration Task 1 — the restoration lifecycle states a
// device-local saved space can be in.
//
// Task 3's coordinator will expose this as its `State` property; Task 5
// maps a subset of these values to on-screen status text.
//
// A top-level PUBLIC enum, not nested/internal, on purpose: this project
// rejects InternalsVisibleTo as a seam (see AssemblySeamTests.cs), so a
// nested type would be unreachable from edit-mode tests.

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum SpatialAnchorRestorationState
    {
        /// No snapshot exists yet. Tagging creates a fresh space.
        NoSavedSpace,

        /// A snapshot exists; the coordinator is loading, binding, or
        /// localizing the saved anchor.
        Restoring,

        /// The saved (or freshly created) anchor is localized and bound.
        /// Tagging is enabled.
        Ready,

        /// Restoration failed or tracking was lost. The snapshot is kept and
        /// retried, but tagging is disabled.
        Unavailable,

        /// A confirmed "Forget saved room and labels" reset is in progress.
        Resetting
    }
}
