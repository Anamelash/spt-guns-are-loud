using System;
using EFT;
using EFT.EnvironmentEffect;
using System.Collections.Generic;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    // Mark the borrowed voice, not its shared mixer group or the reusable SoundBank.
    internal static class GrenadeAudioRoute
    {
        // Instance ids, not weak tables: the exemption is per issuance of a pooled
        // voice and is always released through Release, and a set lookup on the
        // contrast path is cheaper than a conditional-weak-table probe.
        private static readonly HashSet<int> Explosions = new HashSet<int>();
        private static readonly Dictionary<int, List<int>> Routes = new Dictionary<int, List<int>>();
        private static readonly Stack<List<int>> SpareLists = new Stack<List<int>>();
        private static readonly List<AudioSource> Children = new List<AudioSource>();
        [ThreadStatic] internal static bool Emitting;
        internal static bool IsExplosion(AudioSource source) =>
            source != null && Explosions.Count != 0 && Explosions.Contains(source.GetInstanceID());
        internal static void MarkIfEmitting(BetterSource source)
        {
            if (!Emitting || source == null) return;
            Release(source);
            source.GetComponentsInChildren(true, Children);
            List<int> marked = SpareLists.Count > 0 ? SpareLists.Pop() : new List<int>();
            foreach (AudioSource audio in Children)
            {
                if (audio == null) continue;
                int id = audio.GetInstanceID();
                Explosions.Add(id);
                marked.Add(id);
            }
            Routes[source.GetInstanceID()] = marked;
        }
        internal static void Release(BetterSource source)
        {
            if (source == null) return;
            int key = source.GetInstanceID();
            if (!Routes.TryGetValue(key, out List<int> marked)) return;
            Routes.Remove(key);
            foreach (int id in marked) Explosions.Remove(id);
            marked.Clear();
            SpareLists.Push(marked);
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
            using (PerformanceTrace.Measure(PerformanceArea.HookExplosion))
                HandleExplosionAudio(bank, distance, position, env, __result);
        }

        private static void HandleExplosionAudio(
            SoundBank bank, float distance, Vector3 position, EnvironmentType env, BetterSource __result)
        {
            if (!GrenadeAudioRoute.Emitting || __result == null) return;
            // Banks without indoor recordings still need the physical source environment.
            var environment = EnvironmentManager.Instance;
            bool indoor = (environment != null ? environment.GetEnvironmentByPos(position) : env) == EnvironmentType.Indoor;
            Plugin.Runtime?.HandleExplosion(distance, indoor, position);
            GrenadeAudioRoute.MarkIfEmitting(__result);
            GunshotContrastController.Instance?.RefreshSourceTree(__result);
            if (!DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.Grenade, out DiagnosticReservation reservation)) return;
            var status = HeadphoneRouteRuntime.Instance?.Status ?? default;
            foreach (var audio in __result.GetComponentsInChildren<AudioSource>(true))
                DetailedDiagnostics.Commit(
                    reservation,
                    $"grenade audio bank={bank.name} distance={distance:0.0}m mixer={audio.outputAudioMixerGroup?.name} " +
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

