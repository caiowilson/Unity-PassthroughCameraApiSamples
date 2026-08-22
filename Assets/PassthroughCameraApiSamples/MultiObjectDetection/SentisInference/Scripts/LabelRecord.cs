// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 1, simplified by manual-tagging Task 3 —
// plain-data record for a tracked label.
//
// This is the SOURCE OF TRUTH for a registered label's state. It holds no
// RectTransform and no GameObject, so it can be matched, smoothed, and
// rendered without any of that logic touching a Unity view type or a
// MonoBehaviour.
//
// ConfirmationCount and LastSeenTime (slice 5) are gone: both existed to
// support automatic label creation (a confirmation gate before becoming
// visible) and automatic expiry (a grace period keyed off last-seen time).
// Manual tagging has neither — a record only exists here because a human
// pressed A, and it is visible from the moment it exists.
//
// A top-level PUBLIC type, not nested/internal, on purpose: this project
// rejects InternalsVisibleTo as a seam (see AssemblySeamTests.cs), so edit-
// mode tests need a reachable type without it.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class LabelRecord
    {
        /// Identifies this label across frames independent of any view object.
        /// Assigned once when the local label is committed or the remote label is created.
        public Guid SessionId;
        public LabelSource Source;
        public RemoteLabelState RemoteState;
        public string RemoteName;
        public int ClassId;
        public string ClassName;
        public Vector3 WorldPosition;

        /// Exponentially smoothed toward WorldPosition on each accepted
        /// re-detection (see LabelPresentation.Smooth). Starts equal to
        /// WorldPosition at commit time.
        public Vector3 SmoothedPosition;

        /// Spatial-anchor Restoration Task 4 — SmoothedPosition expressed in
        /// the shared anchor's local space, cached at the instant
        /// SmoothedPosition was last written, from the anchor pose live at
        /// that same instant.
        ///
        /// This is the value that gets persisted, and it is cached rather than
        /// derived at save time for one reason: SmoothedPosition only changes
        /// on commit, association or restore, while Meta keeps refining the
        /// anchor's own pose underneath it. Re-deriving a stale label's local
        /// offset from a later anchor pose would shift it by however far the
        /// anchor had moved since -- silently, into the saved snapshot, and
        /// compounding across every session that reloads and re-saves it.
        /// Pairing each world position with the pose it was actually measured
        /// against is what keeps the stored offset stable.
        ///
        /// Every write to SmoothedPosition must refresh this (see
        /// SentisInferenceUiManager.CacheAnchorLocalPosition).
        public Vector3 AnchorLocalPosition;

        public float LastAssociatedScore;

        /// Ticket 08: set when this label was committed by the on-headset YOLO
        /// fallback (a fixed, small vocabulary) rather than the Mac's free-form
        /// naming. Drives the lower-capability presentation marker in
        /// RemoteSpatialLabelLifecycle.PresentationFor.
        public bool IsFallbackResult;
    }
}
