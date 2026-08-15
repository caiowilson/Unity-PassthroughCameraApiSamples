// Copyright (c) Meta Platforms, Inc. and affiliates.
//
// Object Tagger slice 3 Task 4 — the curated class set.
//
// THE TRAP THIS FILE EXISTS TO AVOID. The shipped SentisYoloClasses.txt uses
// DARKNET spellings; the ONNX `names` metadata uses ULTRALYTICS spellings. Slice 3
// Task 1 verified all 80 indices agree semantically, but six names differ:
//
//     shipped (authoritative)   ONNX metadata
//     motorbike                 motorcycle
//     aeroplane                 airplane
//     sofa                      couch
//     pottedplant               potted plant
//     diningtable               dining table
//     tvmonitor                 tv
//
// Class lookups resolve through the SHIPPED file, so a curated set written with
// ONNX spellings matches NOTHING — silently. Four of those six are ordinary-room
// objects, and `tvmonitor` was among the classes actually detected on device in
// both slice 1 and slice 2.
//
// So the set below is written in shipped spellings AND resolved by name at
// runtime. Any entry that fails to resolve is logged as an error rather than
// quietly dropped. A silent miss here would look like a detection problem.

using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class SupportedClasses
    {
        /// Curated alpha class set, in SHIPPED (darknet) spelling.
        ///
        /// alpha-scope.md requires at least six classes commonly present in an
        /// ordinary room. This is 26, deliberately weighted to indoor objects. It is
        /// a subset of the model's 80 — the model is unchanged; this only decides
        /// what the app is willing to label.
        public static readonly string[] CuratedNames =
        {
            // people
            "person",
            // tableware and small objects
            "bottle", "cup", "fork", "knife", "spoon", "bowl", "vase", "scissors",
            // furniture
            "chair", "sofa", "pottedplant", "diningtable",
            // screens and desk equipment
            "tvmonitor", "laptop", "mouse", "remote", "keyboard", "cell phone", "book", "clock",
            // carried items
            "backpack", "handbag", "suitcase", "tie", "teddy bear",
        };

        /// Resolve the curated names against the labels actually loaded from the
        /// shipped asset. Returns the set of class IDs the app will accept.
        ///
        /// Resolving by NAME rather than hardcoding indices means a drifted labels
        /// file is caught here instead of silently changing which objects the app
        /// labels.
        public static HashSet<int> Resolve(IReadOnlyList<string> labels)
        {
            var allowed = new HashSet<int>();
            if (labels == null || labels.Count == 0)
            {
                Debug.LogError("[ObjectTagger] SupportedClasses.Resolve: no labels supplied; curation disabled.");
                return allowed;
            }

            var index = new Dictionary<string, int>(labels.Count);
            for (var i = 0; i < labels.Count; i++)
            {
                var name = labels[i]?.Trim();
                if (!string.IsNullOrEmpty(name) && !index.ContainsKey(name))
                {
                    index[name] = i;
                }
            }

            var unresolved = new List<string>();
            foreach (var wanted in CuratedNames)
            {
                if (index.TryGetValue(wanted, out var id))
                {
                    _ = allowed.Add(id);
                }
                else
                {
                    unresolved.Add(wanted);
                }
            }

            if (unresolved.Count > 0)
            {
                // Loud on purpose. This is the darknet/Ultralytics trap firing, and its
                // natural symptom would otherwise be "that object never gets labelled".
                Debug.LogError(
                    $"[ObjectTagger] {unresolved.Count} curated class name(s) did not resolve against the " +
                    $"shipped labels file: {string.Join(", ", unresolved)}. " +
                    "Check for Ultralytics spellings (tv/couch/potted plant/dining table/motorcycle/airplane) " +
                    "where darknet spellings are required.");
            }

            Debug.Log($"[ObjectTagger] curated class set resolved: {allowed.Count}/{CuratedNames.Length} names, from {labels.Count} labels.");
            return allowed;
        }
    }
}
