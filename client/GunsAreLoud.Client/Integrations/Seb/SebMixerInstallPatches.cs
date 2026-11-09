using System;
using System.Reflection;
using GunsAreLoud.Client.Audio;
using HarmonyLib;

namespace GunsAreLoud.Client.Integrations.Seb
{
    /// <summary>
    /// The second half of standing aside for Sound Engine Backport's live
    /// mixer: after SEB's own <c>CreateSoundManagers</c> prefix, take the stock
    /// variant back if SEB left the stock mixer in place after all.
    /// </summary>
    [HarmonyPatch(typeof(BetterAudio), nameof(BetterAudio.CreateSoundManagers))]
    [HarmonyAfter(SebIds.ClientGuid, SebIds.ReleaseGuid)]
    internal static class HeadphoneMixerReclaimPatch
    {
        private static bool Prepare() => SebModule.Attached;

        private static void Prefix(BetterAudio __instance)
        {
            try { HeadphoneMixerAsset.ReclaimIfYieldedInVain(__instance); }
            catch (Exception error)
            { Plugin.Log?.LogWarning("Headphone replacement mixer reclaim failed: " + error.Message); }
        }
    }

    /// <summary>
    /// SEB's <c>LiveMixer.Install</c>: this mod's variant goes into its slot
    /// before it reads it. Resolved by name; a SEB whose install is not where
    /// it was leaves the patch out.
    /// </summary>
    [HarmonyPatch]
    internal static class SebLiveMixerInstallPatch
    {
        private static MethodBase TargetMethod() => SebReflection.LiveMixerInstall;

        private static bool Prepare() => SebModule.Attached && SebModule.InstallHookAvailable;

        private static void Prefix(BetterAudio __0)
        {
            try { HeadphoneMixerAsset.InstallIntoHost(__0); }
            catch (Exception error)
            { Plugin.Log?.LogWarning("Headphone live variant install failed: " + error.Message); }
        }
    }

    /// <summary>
    /// After SEB's own prefix, which rewrites NonspatialBypass to World/Bypass:
    /// on this mod's variant that path is World/GAL Passive/Bypass. Without
    /// this, BetterAudio's lookup finds nothing and the raid's audio does not
    /// initialise.
    /// </summary>
    [HarmonyPatch(typeof(BetterAudio), nameof(BetterAudio.FindMixerGroup))]
    [HarmonyAfter(SebIds.ClientGuid, SebIds.ReleaseGuid)]
    internal static class SebLiveMixerLookupPatch
    {
        private static bool Prepare() => SebModule.Attached;

        private static void Prefix(BetterAudio __instance, ref string groupName)
        {
            if (!HeadphoneMixerAsset.HostVariantActive || __instance == null ||
                !HeadphoneMixerAsset.Owns(__instance.Master))
                return;
            groupName = HeadphoneMixerAsset.MapLookup(groupName, HeadphoneMixerAsset.ActiveVariant);
        }
    }
}
