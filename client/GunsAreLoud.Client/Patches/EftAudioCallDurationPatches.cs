using System.Collections.Generic;
using System.Reflection;
using Audio.ReverbSubsystem;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;

namespace GunsAreLoud.Client.Patches
{
    /// <summary>
    /// Measurement only: these patches change nothing. They bracket the whole
    /// call of the three EFT audio entry points this mod also hooks, so the
    /// summary can separate our own hook cost from the time the game spends
    /// inside its routing and playback API.
    /// </summary>
    [HarmonyPatch(typeof(SuperAudioSample), nameof(SuperAudioSample.PlayOn))]
    internal static class EftPlayOnDurationPatch
    {
        private static void Prefix(out long __state) => __state = PerformanceTrace.Begin();

        private static void Postfix(long __state) =>
            PerformanceTrace.End(PerformanceArea.EftPlayOn, __state);
    }

    [HarmonyPatch]
    internal static class EftSetMixerGroupDurationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SimpleSource), nameof(SimpleSource.SetMixerGroup));
            yield return AccessTools.Method(typeof(SuperSource), nameof(SuperSource.SetMixerGroup));
            yield return AccessTools.Method(typeof(SuperSourceDistant), nameof(SuperSourceDistant.SetMixerGroup));
            yield return AccessTools.Method(typeof(ReverbSuperSource), nameof(ReverbSuperSource.SetMixerGroup));
        }

        private static void Prefix(out long __state) => __state = PerformanceTrace.Begin();

        private static void Postfix(long __state) =>
            PerformanceTrace.End(PerformanceArea.EftSetMixerGroup, __state);
    }

    [HarmonyPatch(typeof(BetterSource), nameof(BetterSource.PlayScheduled))]
    internal static class EftPlayScheduledDurationPatch
    {
        private static void Prefix(out long __state) => __state = PerformanceTrace.Begin();

        private static void Postfix(long __state) =>
            PerformanceTrace.End(PerformanceArea.EftPlayScheduled, __state);
    }
}
