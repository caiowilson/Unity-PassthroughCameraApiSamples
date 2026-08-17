// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger manual-tagging Task 1 — press-duration classification as
// pure logic.
//
// DetectionManager tracks Time.time at press-start and calls this every
// frame the button/pinch stays held to decide whether the hold has crossed
// the clear-all threshold yet. Pulled out as a pure function for the same
// reason LabelPresentation.IsExpired is pure: the time-boundary comparison
// is exactly the kind of logic this project asserts with edit-mode tests
// rather than trusting by inspection.

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class InputHoldClassification
    {
        /// True once a press that started at pressStartTime has been held
        /// for at least holdThresholdSeconds, evaluated at currentTime.
        /// Inclusive boundary (>=): matches this project's other threshold
        /// checks reading as "at least N" rather than "strictly more than N"
        /// where the two read equally naturally (contrast with
        /// LabelPresentation.IsExpired's deliberate strict `>`, chosen there
        /// so "just at the grace period" reads as not-yet-expired).
        public static bool HasReachedHoldThreshold(float pressStartTime, float currentTime, float holdThresholdSeconds)
        {
            return currentTime - pressStartTime >= holdThresholdSeconds;
        }
    }
}
