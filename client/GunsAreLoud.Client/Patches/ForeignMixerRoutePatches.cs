using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.AudioCulling;
using Audio.RadioSystem;
using Audio.ReverbSubsystem;
using Audio.Vehicles;
using GunsAreLoud.Client.Audio;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Patches
{
    /// <summary>
    /// Where EFT hands a serialized stock mixer group to an audio source. The
    /// Unity setter itself is native and cannot be patched, so each place that
    /// assigns one is listed here. Every entry resolves against the game assembly
    /// at load; one that cannot be found is skipped by its patch class and
    /// reported by the compatibility test, never thrown into PatchAll.
    /// </summary>
    internal static class ForeignMixerRouteTargets
    {
        internal readonly struct Target
        {
            internal readonly string Label;
            internal readonly Func<MethodBase> Resolve;
            internal Target(string label, Func<MethodBase> resolve) { Label = label; Resolve = resolve; }
        }

        /// <summary>Pooled sources: every forced group goes through one of these.</summary>
        internal static readonly Target[] SetMixerGroup =
        {
            Declared<SimpleSource>("SetMixerGroup", typeof(AudioMixerGroup)),
            Declared<SuperSource>("SetMixerGroup", typeof(AudioMixerGroup)),
            Declared<SuperSourceDistant>("SetMixerGroup", typeof(AudioMixerGroup)),
            Declared<ReverbSimpleSource>("SetMixerGroup", typeof(AudioMixerGroup)),
            Declared<ReverbSuperSource>("SetMixerGroup", typeof(AudioMixerGroup)),
            Declared<BaseAmbientSoundPlayer>("SetMixerGroup", typeof(AudioMixerGroup))
        };

        /// <summary>Components that write their own serialized group into plain sources.</summary>
        internal static readonly Target[] OwnerSetup =
        {
            Declared<PrecipitationAmbientBlender>("Init"),
            Declared<WindAmbientBlender>("Init"),
            Declared<SyncLoopSoundPlayer>("Awake"),
            Declared<BaseAmbientSoundPlayer>("Awake"),
            Declared<SourceOccluder>("SetupSources")
        };

        internal static readonly Target VehicleSetUp =
            Declared<VehicleMovementSoundContext>("SetUpAudioSource");

        internal static readonly Target BroadcastSetUp =
            Declared<ClientBroadcastPlayer>("SetupSource", typeof(AudioSource));

        internal static IEnumerable<Target> All() =>
            SetMixerGroup.Concat(OwnerSetup).Concat(new[] { VehicleSetUp, BroadcastSetUp });

        internal static IEnumerable<MethodBase> Resolved(IEnumerable<Target> targets) =>
            targets.Select(target => SafeResolve(target.Resolve)).Where(method => method != null);

        internal static MethodBase SafeResolve(Func<MethodBase> resolve)
        {
            try { return resolve(); }
            catch (Exception) { return null; }
        }

        private static Target Declared<T>(string name, params Type[] parameters) =>
            new Target(typeof(T).Name + "." + name, () => parameters.Length == 0
                ? AccessTools.FirstMethod(typeof(T), method => method.Name == name && method.DeclaringType == typeof(T))
                : AccessTools.DeclaredMethod(typeof(T), name, parameters));
    }

    /// <summary>
    /// Substitutes the argument before EFT stores it. Covers every pooled source
    /// call, including trigger sounds and sound banks that pass a serialized
    /// group as <c>forceMixerGroup</c>, and the ambient player's own setter.
    /// </summary>
    [HarmonyPatch]
    internal static class ForeignMixerRouteSetMixerGroupPatch
    {
        private static bool Prepare() =>
            ForeignMixerRouteTargets.Resolved(ForeignMixerRouteTargets.SetMixerGroup).Any();

        private static IEnumerable<MethodBase> TargetMethods() =>
            ForeignMixerRouteTargets.Resolved(ForeignMixerRouteTargets.SetMixerGroup);

        private static void Prefix(ref AudioMixerGroup __0)
        {
            StockMixerGroupMap map = HeadphoneMixerAsset.StockGroupMap;
            if (map != null && map.TryMap(__0, out AudioMixerGroup mapped)) __0 = mapped;
        }
    }

    /// <summary>
    /// After a component has written its serialized group into the sources it
    /// owns. They are the component's own object and its children in every
    /// prefab seen; a source elsewhere is picked up by the scene sweep.
    /// </summary>
    [HarmonyPatch]
    internal static class ForeignMixerRouteOwnerSetupPatch
    {
        private static bool Prepare() =>
            ForeignMixerRouteTargets.Resolved(ForeignMixerRouteTargets.OwnerSetup).Any();

        private static IEnumerable<MethodBase> TargetMethods() =>
            ForeignMixerRouteTargets.Resolved(ForeignMixerRouteTargets.OwnerSetup);

        private static void Postfix(Component __instance) =>
            HeadphoneMixerAsset.StockGroupMap?.RemapChildren(__instance);
    }

    /// <summary>The BTR: six sources set up one at a time, each passed by reference.</summary>
    [HarmonyPatch]
    internal static class ForeignMixerRouteVehiclePatch
    {
        private static bool Prepare() =>
            ForeignMixerRouteTargets.SafeResolve(ForeignMixerRouteTargets.VehicleSetUp.Resolve) != null;

        private static MethodBase TargetMethod() =>
            ForeignMixerRouteTargets.SafeResolve(ForeignMixerRouteTargets.VehicleSetUp.Resolve);

        private static void Postfix(ref AudioSource __0) =>
            HeadphoneMixerAsset.StockGroupMap?.Remap(__0);
    }

    /// <summary>Radio broadcasts: the source is handed in, not owned by a child.</summary>
    [HarmonyPatch]
    internal static class ForeignMixerRouteBroadcastPatch
    {
        private static bool Prepare() =>
            ForeignMixerRouteTargets.SafeResolve(ForeignMixerRouteTargets.BroadcastSetUp.Resolve) != null;

        private static MethodBase TargetMethod() =>
            ForeignMixerRouteTargets.SafeResolve(ForeignMixerRouteTargets.BroadcastSetUp.Resolve);

        private static void Postfix(AudioSource __0) =>
            HeadphoneMixerAsset.StockGroupMap?.Remap(__0);
    }
}
