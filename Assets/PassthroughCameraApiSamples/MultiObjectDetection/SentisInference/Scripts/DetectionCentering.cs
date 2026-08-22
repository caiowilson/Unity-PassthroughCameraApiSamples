// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Ticket 08: the same nearest-to-frame-center selection DrawUIBoxes uses for
// its live-candidate preview, extracted so the one-shot on-headset YOLO
// fallback (SentisInferenceRunManager.RunOneShotDetection) can pick a single
// detection without pulling in DrawUIBoxes's depth/association/view work.
//
// Deliberately not wired back into DrawUIBoxes itself: that method is dead at
// runtime today (the continuous detection loop stays disabled per D4) and
// does substantially more than return an index, so re-pointing it here would
// touch tested, shipped-but-currently-dead code for no behavioral gain.

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class DetectionCentering
    {
        private static readonly Vector2 FrameCenter = new Vector2(0.5f, 0.5f);

        public static int IndexNearestCenter(
            IReadOnlyList<(int classId, Vector4 boundingBox, float score)> detections,
            Vector2 inputSize)
        {
            if (detections == null || detections.Count == 0)
            {
                return -1;
            }

            var centerDistances = new List<float>(detections.Count);
            for (var i = 0; i < detections.Count; i++)
            {
                var box = detections[i].boundingBox;
                var rect = new Rect(box.x, box.y, box.z - box.x, box.w - box.y);
                var normalizedCenter = rect.center / inputSize;
                centerDistances.Add(Vector2.Distance(normalizedCenter, FrameCenter));
            }

            return NearestSelection.IndexOfMinimum(centerDistances);
        }
    }
}
