// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 5 Task 1 — plain-data record for a tracked label.
//
// This is the SOURCE OF TRUTH for a detected object's label state. It
// deliberately holds no RectTransform and no GameObject — spec line 55 moves
// per-label state out of the view so it can be matched (Task 2), smoothed and
// expired (Task 3), and rendered as a text card (Task 4) without any of that
// logic touching a Unity view type or a MonoBehaviour.
//
// A top-level PUBLIC type, not nested/internal, on purpose: every other piece
// of pure per-frame logic in this project (DepthResolveResult, DetectionDecoder,
// DetectionProjection) lives this way specifically so edit-mode tests can reach
// it without InternalsVisibleTo — see AssemblySeamTests.cs, which pins that this
// project rejects InternalsVisibleTo as a seam choice. Task 3 adds smoothing and
// expiry logic that will want exactly this kind of test.
//
// Score is carried here but MUST NOT be read by SentisInferenceUiManager's
// RectTransform view yet — Task 1 Step 3 keeps this a pure state change, not a
// rendering change. Task 4 renders it.

using System;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class LabelRecord
    {
        /// Identifies this label across frames independent of any view object.
        /// Assigned once when the label is first created; unchanged for as long
        /// as this label keeps matching the same real-world detection.
        public Guid SessionId;
        public int ClassId;
        public string ClassName;
        public Vector3 WorldPosition;

        /// Task 3 fills this in with actual smoothing. Task 1 keeps it equal to
        /// WorldPosition so the field exists and compiles without asserting
        /// smoothing behaviour this task doesn't own.
        public Vector3 SmoothedPosition;

        public float LastAssociatedScore;

        /// Task 3 owns confirmation-before-visible logic. Task 1 only defines
        /// the field so later tasks have somewhere to put it.
        public int ConfirmationCount;

        public float LastSeenTime;
    }
}
