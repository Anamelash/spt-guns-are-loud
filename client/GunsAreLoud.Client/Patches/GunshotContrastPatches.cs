using System.Collections.Generic;
using System.Reflection;
using Audio.ReverbSubsystem;
using GunsAreLoud.Client.Audio;
using HarmonyLib;

namespace GunsAreLoud.Client.Patches
{
    [HarmonyPatch]
    internal static class GunshotContrastRoutingPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SimpleSource), nameof(SimpleSource.SetMixerGroup));
            yield return AccessTools.Method(typeof(SuperSource), nameof(SuperSource.SetMixerGroup));
            yield return AccessTools.Method(typeof(SuperSourceDistant), nameof(SuperSourceDistant.SetMixerGroup));
            yield return AccessTools.Method(typeof(ReverbSuperSource), nameof(ReverbSuperSource.SetMixerGroup));
        }

        private static void Postfix(BetterSource __instance) =>
            GunshotContrastController.Instance?.RefreshSourceTree(__instance);
    }

    [HarmonyPatch(typeof(BetterSource), nameof(BetterSource.PlayScheduled))]
    internal static class GunshotContrastPlaybackPatch
    {
        private static void Prefix(BetterSource __instance) =>
            GunshotContrastController.Instance?.RefreshSourceTree(__instance);
    }
}
