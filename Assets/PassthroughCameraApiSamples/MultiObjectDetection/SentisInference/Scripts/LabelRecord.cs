// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 1, simplified by manual-tagging Task 3 —
// plain-data record for a tracked label.
//
// This is the SOURCE OF TRUTH for a committed label's state. It holds no
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
        /// Assigned once when the label is first committed.
        public Guid SessionId;
        public int ClassId;
        public string ClassName;
        public Vector3 WorldPosition;

        /// Exponentially smoothed toward WorldPosition on each accepted
        /// re-detection (see LabelPresentation.Smooth). Starts equal to
        /// WorldPosition at commit time.
        public Vector3 SmoothedPosition;

        public float LastAssociatedScore;
    }
}
