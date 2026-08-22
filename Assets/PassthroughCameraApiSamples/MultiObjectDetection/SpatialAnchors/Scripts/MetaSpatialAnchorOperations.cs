// Spatial-anchor Restoration Task 3 — the one place Meta's anchor SDK is
// touched. Everything here needs a headset (an OVRSpatialAnchor component, the
// device anchor store, localization), so none of it can run in EditMode; the
// decisions live in SpatialAnchorRestorationCoordinator instead, and this
// class only turns them into OVRSpatialAnchor calls and reports Succeeded or
// Failed back through ISpatialAnchorOperations.
//
// EraseAnchorAsync/EraseAnchorsAsync appear exactly once each, in
// EraseRoutine, which only BeginErase starts, which only
// SpatialAnchorRestorationCoordinator.RequestReset() calls. ReleaseRuntimeAnchor
// destroys the component and nothing else — that is what teardown uses, and
// the reason a saved space now survives an app close.
//
// OVRTask is polled through GetAwaiter()/IsCompleted rather than awaited, the
// pattern the rest of this project already uses, so every step stays on a
// coroutine with a single owner and no synchronization-context surprises.
//
// Deadlines cover the waits that poll a STATE (localizing, IsTracked), which
// are the ones observed to stall. The OVRTask awaiter loops are deliberately
// unbounded -- an OVRTask that never completes has no cancellation to offer
// here -- so the backstop for those is the coordinator's
// OperationTimeoutSeconds watchdog, not a deadline in this file.
//
// Log levels follow one rule: a cause the coordinator will retry and that can
// resolve itself (no tracking yet, the space not recognised yet, a transient
// load or localize failure) is a WARNING, because on a headset set down on a
// desk it repeats every five seconds. A cause that indicates a bug or
// permanently broken input (a non-UUID, a missing label root, BindTo rejected,
// a failed save or erase) is an ERROR.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public sealed class MetaSpatialAnchorOperations : ISpatialAnchorOperations
    {
        /// Timeout handed to UnboundAnchor.LocalizeAsync.
        private const double LocalizeTimeoutSeconds = 10d;

        /// How long a freshly created or bound anchor is given to report
        /// IsTracked before the attempt is called a failure.
        private const float TrackingWaitSeconds = 2f;

        /// How long a newly added OVRSpatialAnchor component is given to
        /// localize before creation is called a failure. The SDK destroys the
        /// component when creation fails, but a component that simply never
        /// localizes would otherwise wait forever.
        private const float CreateLocalizeWaitSeconds = 15f;

        private readonly MonoBehaviour m_coroutineHost;
        private readonly Func<GameObject> m_anchorRootProvider;

        private OVRSpatialAnchor m_anchor;
        private Coroutine m_running;
        private bool m_succeeded;

        /// <param name="coroutineHost">MonoBehaviour whose coroutines run the
        /// operations. Its destruction cancels them.</param>
        /// <param name="anchorRootProvider">Resolves the shared label root the
        /// anchor attaches to. Called at operation time, not construction
        /// time, so the root does not have to exist yet.</param>
        public MetaSpatialAnchorOperations(
            MonoBehaviour coroutineHost, Func<GameObject> anchorRootProvider)
        {
            m_coroutineHost = coroutineHost ?? throw new ArgumentNullException(nameof(coroutineHost));
            m_anchorRootProvider = anchorRootProvider ?? throw new ArgumentNullException(nameof(anchorRootProvider));
        }

        public SpatialAnchorOperationStatus Status { get; private set; } = SpatialAnchorOperationStatus.Idle;

        public bool IsAnchorTracked => m_anchor != null && m_anchor.IsTracked;

        public bool TryGetBoundAnchorUuid(out string anchorUuid)
        {
            if (m_anchor != null && m_anchor.Created)
            {
                anchorUuid = m_anchor.Uuid.ToString();
                return true;
            }

            anchorUuid = null;
            return false;
        }

        public void BeginCreate() => Run(CreateRoutine());

        public void BeginRestore(string anchorUuid) => Run(RestoreRoutine(anchorUuid));

        public void BeginErase(string anchorUuid) => Run(EraseRoutine(anchorUuid));

        /// Drops the runtime component. NEVER erases: the saved anchor has to
        /// outlive the process.
        public void ReleaseRuntimeAnchor()
        {
            if (m_anchor == null)
            {
                return;
            }

            UnityEngine.Object.DestroyImmediate(m_anchor);
            m_anchor = null;
        }

        private void Run(IEnumerator routine)
        {
            if (m_running != null)
            {
                m_coroutineHost.StopCoroutine(m_running);
                m_running = null;
            }

            m_succeeded = false;
            Status = SpatialAnchorOperationStatus.Running;
            m_running = m_coroutineHost.StartCoroutine(TrackRoutine(routine));
        }

        // Success has to be claimed explicitly, so every early `yield break`
        // — including ones added later — reports Failed rather than leaving
        // the coordinator waiting on an operation that already gave up.
        private IEnumerator TrackRoutine(IEnumerator routine)
        {
            yield return routine;
            Status = m_succeeded
                ? SpatialAnchorOperationStatus.Succeeded
                : SpatialAnchorOperationStatus.Failed;
            m_running = null;
        }

        private IEnumerator CreateRoutine()
        {
            ReleaseRuntimeAnchor();

            var root = m_anchorRootProvider();
            if (root == null)
            {
                LogAnchor("cannot create an anchor: the shared label root is unavailable", LogType.Error);
                yield break;
            }

            m_anchor = root.AddComponent<OVRSpatialAnchor>();

            // SaveAnchorAsync requires a localized anchor. OVRSpatialAnchor
            // destroys itself when creation fails, so a null component here is
            // the SDK's way of reporting that.
            var deadline = Time.realtimeSinceStartup + CreateLocalizeWaitSeconds;
            while (m_anchor != null && !m_anchor.Localized && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (m_anchor == null)
            {
                LogAnchor("anchor creation failed: the component destroyed itself before localizing", LogType.Error);
                yield break;
            }

            if (!m_anchor.Localized)
            {
                LogAnchor($"the new anchor did not localize within {CreateLocalizeWaitSeconds}s", LogType.Error);
                ReleaseRuntimeAnchor();
                yield break;
            }

            var saveAwaiter = m_anchor.SaveAnchorAsync().GetAwaiter();
            while (!saveAwaiter.IsCompleted)
            {
                yield return null;
            }

            var saveResult = saveAwaiter.GetResult();
            if (!saveResult.Success)
            {
                LogAnchor($"SaveAnchorAsync() failed {saveResult.Status}", LogType.Error);
                ReleaseRuntimeAnchor();
                yield break;
            }

            if (m_anchor == null)
            {
                LogAnchor("the new anchor was destroyed while it was being saved", LogType.Error);
                yield break;
            }

            LogAnchor($"created and saved {m_anchor.Uuid}");

            // Reports success on the save alone, without waiting for
            // IsTracked. The anchor is already in the store at this point, so
            // failing here would leave it there with no snapshot referencing
            // it and the next attempt would create a second one. Succeeding
            // instead lets the coordinator persist the UUID; if the anchor is
            // not tracked yet it falls to Unavailable and retries a RESTORE of
            // that UUID, which orphans nothing.
            yield return null;
            if (m_anchor != null && !m_anchor.IsTracked)
            {
                LogAnchor("the new anchor is saved but not tracked yet", LogType.Warning);
            }

            m_succeeded = true;
        }

        private IEnumerator RestoreRoutine(string anchorUuid)
        {
            if (!Guid.TryParse(anchorUuid, out var uuid))
            {
                LogAnchor($"cannot restore: '{anchorUuid}' is not a UUID", LogType.Error);
                yield break;
            }

            if (m_anchor != null && m_anchor.Created && m_anchor.Uuid == uuid)
            {
                yield return ResumeBoundAnchorRoutine(uuid);
                yield break;
            }

            ReleaseRuntimeAnchor();

            var unbound = new List<OVRSpatialAnchor.UnboundAnchor>(1);
            var loadAwaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound).GetAwaiter();
            while (!loadAwaiter.IsCompleted)
            {
                yield return null;
            }

            var loadResult = loadAwaiter.GetResult();
            if (!loadResult.Success)
            {
                LogAnchor($"LoadUnboundAnchorsAsync({uuid}) failed {loadResult.Status}", LogType.Warning);
                yield break;
            }

            if (unbound.Count == 0)
            {
                LogAnchor($"the saved anchor {uuid} is not in this headset's anchor store yet", LogType.Warning);
                yield break;
            }

            yield return LocalizeAndBindRoutine(unbound[0]);
        }

        // Tracking loss does not unbind the anchor, so the cheap path is to
        // wait for tracking to come back. Re-fetching a bound anchor's UUID is
        // the upstream sample's idiom for prompting that: it returns zero
        // unbound anchors while the binding still holds.
        private IEnumerator ResumeBoundAnchorRoutine(Guid uuid)
        {
            if (m_anchor.IsTracked)
            {
                m_succeeded = true;
                yield break;
            }

            var unbound = new List<OVRSpatialAnchor.UnboundAnchor>(1);
            var loadAwaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, unbound).GetAwaiter();
            while (!loadAwaiter.IsCompleted)
            {
                yield return null;
            }

            var loadResult = loadAwaiter.GetResult();
            if (!loadResult.Success)
            {
                LogAnchor($"LoadUnboundAnchorsAsync({uuid}) failed {loadResult.Status} while resuming", LogType.Warning);
                yield break;
            }

            if (unbound.Count != 0)
            {
                // The binding is gone even though the component survives.
                // Release it so the next attempt takes the full load-and-bind
                // path; binding this result to the live component would throw.
                LogAnchor($"the binding for {uuid} was lost; releasing it so the next attempt rebinds", LogType.Warning);
                ReleaseRuntimeAnchor();
                yield break;
            }

            yield return WaitForTrackingRoutine();
        }

        private IEnumerator LocalizeAndBindRoutine(OVRSpatialAnchor.UnboundAnchor unbound)
        {
            if (!TryBeginLocalize(unbound, out var localizeTask))
            {
                yield break;
            }

            var localizeAwaiter = localizeTask.GetAwaiter();
            while (!localizeAwaiter.IsCompleted)
            {
                yield return null;
            }

            if (!localizeAwaiter.GetResult())
            {
                LogAnchor($"LocalizeAsync() failed for {unbound.Uuid}", LogType.Warning);
                yield break;
            }

            if (!TryBind(unbound, out var bound))
            {
                yield break;
            }

            m_anchor = bound;
            LogAnchor($"restored and bound {unbound.Uuid}");
            yield return WaitForTrackingRoutine();
        }

        private IEnumerator WaitForTrackingRoutine()
        {
            var deadline = Time.realtimeSinceStartup + TrackingWaitSeconds;
            while (m_anchor != null && !m_anchor.IsTracked && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (m_anchor == null)
            {
                LogAnchor("the anchor was destroyed before it started tracking", LogType.Error);
                yield break;
            }

            if (!m_anchor.IsTracked)
            {
                // Deliberately keeps the component bound: the retry then takes
                // the cheap ResumeBoundAnchorRoutine path.
                LogAnchor($"the anchor is bound but was not tracked within {TrackingWaitSeconds}s", LogType.Warning);
                yield break;
            }

            m_succeeded = true;
        }

        private IEnumerator EraseRoutine(string anchorUuid)
        {
            // The ONLY erase in this project. Reached from BeginErase, which
            // only SpatialAnchorRestorationCoordinator.RequestReset() calls.
            if (m_anchor != null && m_anchor.Created)
            {
                var uuid = m_anchor.Uuid;
                var awaiter = m_anchor.EraseAnchorAsync().GetAwaiter();
                while (!awaiter.IsCompleted)
                {
                    yield return null;
                }

                var result = awaiter.GetResult();
                if (!result.Success)
                {
                    // Deliberately KEEPS the component. Releasing it here would
                    // leave the retry with nothing to erase through, and when
                    // the snapshot is also null -- a reset requested during
                    // first-run creation -- the retry would take the "nothing
                    // to erase" branch below and report success over an anchor
                    // that still exists.
                    LogAnchor($"EraseAnchorAsync() failed {result.Status} for {uuid}", LogType.Error);
                    yield break;
                }

                ReleaseRuntimeAnchor();
                LogAnchor($"erased the saved anchor {uuid}");
                m_succeeded = true;
                yield break;
            }

            if (!Guid.TryParse(anchorUuid, out var savedUuid))
            {
                // Nothing bound and nothing saved: the reset has nothing to do.
                m_succeeded = true;
                yield break;
            }

            // Restoration never succeeded, so there is no component to erase
            // through; erase by UUID instead. Passing an empty anchor
            // collection rather than null is required: EraseAnchorsAsync
            // enumerates it unconditionally.
            var byUuidAwaiter = OVRSpatialAnchor
                .EraseAnchorsAsync(Array.Empty<OVRSpatialAnchor>(), new[] { savedUuid })
                .GetAwaiter();
            while (!byUuidAwaiter.IsCompleted)
            {
                yield return null;
            }

            var byUuidResult = byUuidAwaiter.GetResult();
            if (!byUuidResult.Success)
            {
                LogAnchor($"EraseAnchorsAsync() failed {byUuidResult.Status} for {savedUuid}", LogType.Error);
                yield break;
            }

            LogAnchor($"erased the saved anchor {savedUuid}");
            m_succeeded = true;
        }

        // LocalizeAsync throws when the anchor cannot be localized at all, and
        // BindTo throws on every misuse the SDK documents. Both are real
        // device failure modes, so they are turned into a failed operation
        // rather than an unhandled exception that would kill the coroutine and
        // leave the status Running.
        private bool TryBeginLocalize(OVRSpatialAnchor.UnboundAnchor unbound, out OVRTask<bool> task)
        {
            try
            {
                task = unbound.LocalizeAsync(LocalizeTimeoutSeconds);
                return true;
            }
            catch (InvalidOperationException e)
            {
                LogAnchor($"LocalizeAsync() rejected {unbound.Uuid}: {e.Message}", LogType.Error);
                task = default;
                return false;
            }
        }

        private bool TryBind(OVRSpatialAnchor.UnboundAnchor unbound, out OVRSpatialAnchor bound)
        {
            bound = null;

            var root = m_anchorRootProvider();
            if (root == null)
            {
                LogAnchor("cannot bind the saved anchor: the shared label root is unavailable", LogType.Error);
                return false;
            }

            OVRSpatialAnchor component = null;
            try
            {
                // The SDK wants a component instantiated on the same frame as
                // the bind, and one that is not already Created.
                component = root.AddComponent<OVRSpatialAnchor>();
                unbound.BindTo(component);
                bound = component;
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                LogAnchor($"BindTo() rejected {unbound.Uuid}: {e.Message}", LogType.Error);
                if (component != null)
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }

                return false;
            }
        }

        private static void LogAnchor(string message, LogType logType = LogType.Log)
        {
            Debug.unityLogger.Log(logType, $"[ObjectTagger] {nameof(OVRSpatialAnchor)}: {message}");
        }
    }
}
