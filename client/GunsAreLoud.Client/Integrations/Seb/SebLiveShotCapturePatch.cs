using System;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Engine;
using GunsAreLoud.Client.Patches;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Integrations.Seb
{
    /// <summary>
    /// Sound Engine Backport's live engine plays a gunshot itself: inside
    /// FireBullet it borrows a pooled source, sets its clips and starts it with
    /// <c>PlayScheduled</c>, without EFT's queue, so neither <c>Enqueue</c> nor
    /// <c>PlayOn</c> ever sees the shot. EFT's own paths never start a source
    /// inside FireBullet — the queue plays its samples on later frames — so a
    /// gunshot source started inside this mod's FireBullet scope is the shot.
    /// Nothing here names a SEB type, and the patch exists only with SEB present.
    /// </summary>
    [HarmonyPatch(typeof(SuperSource), nameof(SuperSource.PlayScheduled), new[] { typeof(double) })]
    internal static class SebLiveShotCapturePatch
    {
        private static bool Prepare() => SebModule.Attached;

        // On SuperSource, not BetterSource: the base call comes first in the
        // override, before the clips' volume has had occlusion applied. The
        // copy reads the donor's volume the way it does after EFT's PlayOn.
        private static void Prefix(SuperSource __instance, double time, out SebLiveShotState __state)
        {
            __state = null;
            // Every source in the game passes here; this check is the whole
            // cost for all of them but the local player's shot.
            if (!DirectGunshotAudioPatch.TryGetShotScope(
                out LocalGunshotAudioTuning tuning,
                out AutomaticShotContext context,
                out DiagnosticShotToken diagnosticShot,
                out WeaponSoundPlayer soundPlayer,
                out float burstInterval))
                return;

            long trace = PerformanceTrace.Begin();
            try
            {
                AudioClip clipA = __instance.source1 != null ? __instance.source1.clip : null;
                AudioClip clipB = __instance.source2 != null ? __instance.source2.clip : null;
                if (!SebLiveShotCapture.ShouldCapture(
                    Plugin.ModConfig?.Enabled.Value == true,
                    LocalGunshotPlaybackScope.Matches(__instance),
                    clipA != null || clipB != null,
                    IsGunshotGroup(__instance.source1 != null ? __instance.source1.outputAudioMixerGroup : null)))
                    return;

                // Before the base call: the volume SEB set, without occlusion.
                float observed = __instance.source1 != null ? __instance.source1.volume : 0f;
                // The volume SEB chose (its bank volume, gunshot preset and the
                // player's slider) is part of what the correction measures; the
                // slider comes back through the target. The copy follows the
                // same decision, so nothing of SEB's volume is undone twice.
                DirectLevelDecision direct = DirectLevel.Decide(tuning, clipA ?? clipB,
                    clipA != null ? observed : (__instance.source2 != null ? __instance.source2.volume : 0f));
                LocalGunshotAudioProcessor.ApplyDirectGain(__instance, direct);
                DirectGunshotAudioPatch.NoteDirectDecision(direct);
                bool roomRoute = LocalGunshotAudioProcessor.Apply(__instance, tuning);
                __state = new SebLiveShotState(tuning.WithLoopBeat(0f), context, diagnosticShot,
                    burstInterval, roomRoute, clipA, clipB, observed);
            }
            finally
            {
                PerformanceTrace.End(PerformanceArea.HookPlayOnLocal, trace);
            }
        }

        private static void Postfix(SuperSource __instance, double time, SebLiveShotState __state)
        {
            if (__state == null) return;
            long trace = PerformanceTrace.Begin();
            try
            {
                DirectGunshotAudioPatch.NoteCapturedSource();
                float duration = SebLiveShotCapture.OriginalDuration(
                    __state.ClipA != null ? __state.ClipA.length : 0f,
                    __instance.source1 != null ? __instance.source1.pitch : 1f,
                    __state.ClipB != null ? __state.ClipB.length : 0f,
                    __instance.source2 != null ? __instance.source2.pitch : 1f);
                // The copy follows the donor's volume and the direct correction
                // of the shot it belongs to, at the share of its ratio the peak
                // budget left it, as every other copy does.
                bool played = PitchedGunshotLayer.Play(
                    __instance, __state.Tuning, time, duration, __state.BurstIntervalSeconds,
                    directGain: LocalGunshotAudioProcessor.CopyGain(__instance));
                if (DetailedDiagnostics.IsActive(__state.DiagnosticShot))
                {
                    string clipName = __state.ClipA != null ? __state.ClipA.name
                        : __state.ClipB != null ? __state.ClipB.name : "null";
                    DetailedDiagnostics.Commit(
                        __state.DiagnosticShot,
                        DiagnosticEventKind.AudioApply,
                        $"audio apply route=seb-live clip={clipName} source={__instance.GetType().Name} " +
                        $"start=+{(time - AudioSettings.dspTime) * 1000.0:0}ms " +
                        $"burst={__state.BurstIntervalSeconds * 1000f:0}ms " +
                        $"originalDuration={duration * 1000f:0}ms copy={played} " +
                        $"impactTarget={__state.Tuning.DirectBoostDb:0.0}dB " +
                        $"pitchDown={__state.Tuning.PitchedLayerSemitones:0.0}st " +
                        $"pitchGain={__state.Tuning.PitchedLayerGainDb:+0.0;-0.0;0.0}dB " +
                        $"indoorRoom={__state.Tuning.ApplyIndoorRoom} roomRoute={__state.RoomRoute} " +
                        $"donorVolume={(__instance.source1 != null ? __instance.source1.volume : 0f):0.000} " +
                        $"donorGroup={(__instance.source1?.outputAudioMixerGroup != null ? __instance.source1.outputAudioMixerGroup.name : "none")} " +
                        $"occlusion={__instance.OcclusionVolumeFactor:0.00} " +
                        $"donorPreVolume={__state.ObservedVolume:0.000} " +
                        $"userGain={Mathf.Pow(10f, SebModule.GunshotsVolumeDb / 20f):0.000} " +
                        (GalSourceBinding.Find(__instance)?.Direct ?? default).Describe(
                            Plugin.ModConfig?.DirectLevelTarget.Value.ToString() ?? "none"));
                    LocalGunshotPlaybackProbe.Schedule(
                        __instance,
                        "seb-live:" + clipName,
                        time,
                        __state.Tuning.AutomaticPitchedRoute,
                        __state.DiagnosticShot);
                }
            }
            catch (Exception exception)
            {
                Plugin.Log?.LogWarning($"optional low-end playback failed: {exception.Message}");
            }
            PerformanceTrace.End(PerformanceArea.HookPlayOnLocal, trace, countCall: false);
        }

        private static bool IsGunshotGroup(AudioMixerGroup group)
        {
            if (group == null) return false;
            BetterAudio audio = AudioRuntimeLookup.Audio;
            if (audio == null) return false;
            return group == audio.GunshotMixerGroup || group == audio.GunshotOccludedMixerGroup;
        }
    }

    internal sealed class SebLiveShotState
    {
        internal readonly LocalGunshotAudioTuning Tuning;
        internal readonly AutomaticShotContext Context;
        internal readonly DiagnosticShotToken DiagnosticShot;
        internal readonly float BurstIntervalSeconds;
        internal readonly bool RoomRoute;
        internal readonly AudioClip ClipA;
        internal readonly AudioClip ClipB;
        internal readonly float ObservedVolume;

        internal SebLiveShotState(LocalGunshotAudioTuning tuning, AutomaticShotContext context,
            DiagnosticShotToken diagnosticShot, float burstIntervalSeconds, bool roomRoute,
            AudioClip clipA, AudioClip clipB, float observedVolume = 0f)
        {
            ObservedVolume = observedVolume;
            Tuning = tuning;
            Context = context;
            DiagnosticShot = diagnosticShot;
            BurstIntervalSeconds = burstIntervalSeconds;
            RoomRoute = roomRoute;
            ClipA = clipA;
            ClipB = clipB;
        }
    }

    /// <summary>The decisions of the capture, apart from Unity, so they can be tested.</summary>
    internal static class SebLiveShotCapture
    {
        /// <param name="enabled">The mod is on.</param>
        /// <param name="queuedSamplePlaying">The source is playing a sample of
        /// EFT's queue that this mod already handles in PlayOn.</param>
        /// <param name="hasClip">Either channel has a clip to copy.</param>
        /// <param name="gunshotGroup">The source plays on EFT's gunshot group
        /// (open or occluded), as the shot's own sources do.</param>
        internal static bool ShouldCapture(bool enabled, bool queuedSamplePlaying, bool hasClip, bool gunshotGroup) =>
            enabled && !queuedSamplePlaying && hasClip && gunshotGroup;

        /// <summary>
        /// The longer of the two recordings at the pitch each channel plays at,
        /// within the bounds every other copy uses.
        /// </summary>
        internal static float OriginalDuration(float lengthA, float pitchA, float lengthB, float pitchB)
        {
            float a = lengthA > 0f ? lengthA / Mathf.Max(0.1f, pitchA) : 0f;
            float b = lengthB > 0f ? lengthB / Mathf.Max(0.1f, pitchB) : 0f;
            return Mathf.Clamp(Mathf.Max(a, b), 0.01f, 10f);
        }
    }
}
