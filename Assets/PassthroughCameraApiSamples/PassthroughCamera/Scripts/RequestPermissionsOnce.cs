// Copyright (c) Meta Platforms, Inc. and affiliates.

using UnityEngine;

namespace PassthroughCameraSamples
{
    internal static class RequestPermissionsOnce
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AfterSceneLoad()
        {
            // Object Tagger slice 2: request directly instead of subscribing to
            // SceneManager.sceneLoaded.
            //
            // Upstream subscribed here and requested only when a scene whose name was
            // NOT "StartScene" finished loading. The bug is one of ORDERING, not of the
            // hardcoded name: AfterSceneLoad runs after the boot scene has already
            // loaded, so in a single-scene app sceneLoaded never fires again and the
            // request is never made. Changing the string literal to the new scene name
            // would leave the defect intact, presenting as a permission prompt that
            // simply never appears -- and, because the camera then never starts,
            // surfacing downstream as the anchor-gate symptom at
            // SentisInferenceRunManager.cs:158 rather than as a permission problem.
            //
            // RuntimeInitializeOnLoadMethod runs once per process, so the "once"
            // guarantee in the type name is preserved without a flag.
            Debug.Log("[ObjectTagger] Requesting Scene and PassthroughCameraAccess permissions.");
            OVRPermissionsRequester.Request(new[]
            {
                OVRPermissionsRequester.Permission.Scene,
                OVRPermissionsRequester.Permission.PassthroughCameraAccess
            });
        }
    }
}
