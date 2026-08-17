// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger manual-tagging Task 1 — press-duration classification as
// pure logic.
//
// DetectionManager tracks Time.time at press-start and calls this every
// frame the button/pinch stays held to decide whether the hold has crossed
// the clear-all threshold yet. Pulled out as a pure function because the
// time-boundary comparison is exactly the kind of logic this project
// asserts with edit-mode tests rather than trusting by inspection.

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class InputHoldClassification
    {
        /// True once a press that started at pressStartTime has been held
        /// for at least holdThresholdSeconds, evaluated at currentTime.
        /// Inclusive boundary (>=): a press held for exactly the threshold
        /// duration counts as reaching it, so "just at the threshold" reads
        /// as "at least N" rather than requiring strictly more.
        public static bool HasReachedHoldThreshold(float pressStartTime, float currentTime, float holdThresholdSeconds)
        {
            return currentTime - pressStartTime >= holdThresholdSeconds;
        }
    }
}
