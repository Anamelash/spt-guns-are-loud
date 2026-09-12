// Unity 2022.3 editor-only PlayMode validation of the exact runtime resolver.
// Copy this and GunshotContrastMixerRouter.cs to Assets/Editor, then invoke
// GAL.MixerContrastRuntimeValidation.Run without -quit; this harness exits Unity.
using System;
using GunsAreLoud.Client.Audio;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace GAL
{
    [InitializeOnLoad]
    public static class MixerContrastRuntimeValidation
    {
        private const string Pending = "GAL_CONTRAST_RUNTIME_VALIDATION_PENDING";

        static MixerContrastRuntimeValidation()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            try
            {
                AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(
                    "Assets/GunsAreLoud/Audio/MasterMixer.mixer");
                if (!GunshotContrastMixerRouter.TryCreate(mixer, out GunshotContrastMixerRouter router,
                    out string failure))
                    throw new InvalidOperationException(failure);
                if (router.Count != GunshotContrastMixerRouteTable.Routes.Length)
                    throw new InvalidOperationException("runtime route count mismatch");
                router.SetTarget(6f);
                router.Tick(0.04f);
                VerifyLevel(mixer, -3f);
                router.Tick(0.04f);
                VerifyLevel(mixer, -6f);
                router.SetTarget(0f);
                router.Tick(0.08f);
                VerifyLevel(mixer, 0f);
                Debug.Log("GAL_CONTRAST_RUNTIME_VALIDATION_COMPLETE routes=" + router.Count);
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        private static void VerifyLevel(AudioMixer mixer, float expected)
        {
            foreach (ContrastRouteSpec route in GunshotContrastMixerRouteTable.Routes)
                if (!mixer.GetFloat(route.Parameter, out float actual) || Math.Abs(actual - expected) > 0.0001f)
                    throw new InvalidOperationException(
                        $"contrast ramp mismatch {route.Parameter}: expected={expected} actual={actual}");
        }
    }
}
