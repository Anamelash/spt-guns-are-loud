using System;
using EFT;
using EFT.EnvironmentEffect;
using System.Runtime.CompilerServices;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    // Mark the borrowed voice, not its shared mixer group or the reusable SoundBank.
    internal static class GrenadeAudioRoute
    {
        private sealed class Marker { }
        private static readonly ConditionalWeakTable<AudioSource, Marker> Explosions = new ConditionalWeakTable<AudioSource, Marker>();
        [ThreadStatic] internal static bool Emitting;
        internal static bool IsExplosion(AudioSource source) => source != null && Explosions.TryGetValue(source, out _);
        internal static void MarkIfEmitting(BetterSource source)
        {
            if (!Emitting || source == null) return;
            foreach (var audio in source.GetComponentsInChildren<AudioSource>(true))
                Explosions.GetValue(audio, _ => new Marker());
        }
        internal static void Release(BetterSource source)
        {
            if (source == null) return;
            foreach (var audio in source.GetComponentsInChildren<AudioSource>(true)) Explosions.Remove(audio);
        }
    }

    [HarmonyPatch(typeof(BetterAudio), nameof(BetterAudio.PlayAtPointDistant))]
    internal static class GrenadePlaybackPatch
    {
        private static void Prefix(SoundBank bank, out bool __state)
        {
            __state = GrenadeAudioRoute.Emitting;
            GrenadeAudioRoute.Emitting = bank != null && bank.SourceType == BetterAudio.AudioSourceGroupType.Grenades;
        }
        private static void Postfix(SoundBank bank, float distance, Vector3 position, EnvironmentType env, BetterSource __result)
        {
            if (!GrenadeAudioRoute.Emitting || __result == null) return;
            // Banks without indoor recordings still need the physical source environment.
            var environment = EnvironmentManager.Instance;
            bool indoor = (environment != null ? environment.GetEnvironmentByPos(position) : env) == EnvironmentType.Indoor;
            Plugin.Runtime?.HandleExplosion(distance, indoor, position);
            GrenadeAudioRoute.MarkIfEmitting(__result);
            GunshotContrastController.Instance?.RefreshSourceTree(__result);
            if (Plugin.ModConfig?.DiagnosticShotLog.Value != true) return;
            var status = HeadphoneRouteRuntime.Instance?.Status ?? default;
            foreach (var audio in __result.GetComponentsInChildren<AudioSource>(true))
                Plugin.Log?.LogInfo($"grenade audio bank={bank.name} distance={distance:0.0}m mixer={audio.outputAudioMixerGroup?.name} " +
                    $"contrastExempt={GrenadeAudioRoute.IsExplosion(audio)} headphoneMode={status.Effective} fallback={status.Fallback} profile={status.ProfileId}");
        }
        private static Exception Finalizer(Exception __exception, bool __state)
        {
            GrenadeAudioRoute.Emitting = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(BetterSource), nameof(BetterSource.Release))]
    internal static class GrenadeReleasePatch
    {
        private static void Prefix(BetterSource __instance) => GrenadeAudioRoute.Release(__instance);
    }
}

