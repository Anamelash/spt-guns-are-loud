using System;
using System.Runtime.CompilerServices;
using GunsAreLoud.Client.Audio;
using EFT.InventoryLogic;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using UnityEngine;

namespace GunsAreLoud.Client.Patches
{
    [HarmonyPatch(typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.FireBullet))]
    internal static class FireBulletPatch
    {
        private static readonly ConditionalWeakTable<WeaponSoundPlayer, LocalGunshotAudioTuningBox> LastAutomaticTuning =
            new ConditionalWeakTable<WeaponSoundPlayer, LocalGunshotAudioTuningBox>();

        private static void Prefix(
            WeaponSoundPlayer __instance,
            Ammo ammo,
            Vector3 shotPosition,
            Vector3 shotDirection,
            float pitchMult,
            out ShotProcessingState __state)
        {
            using (PerformanceTrace.Measure(PerformanceArea.HookFireBullet))
                __state = PrepareShot(__instance, ammo, shotPosition, shotDirection, pitchMult);
        }

        private static ShotProcessingState PrepareShot(
            WeaponSoundPlayer __instance,
            Ammo ammo,
            Vector3 shotPosition,
            Vector3 shotDirection,
            float pitchMult)
        {
            ShotProcessingState state = null;
            DirectGunshotAudioPatch.EndLocalShot();
            AutomaticBurstRouting previousRouting = null;
            if (__instance != null && LastAutomaticTuning.TryGetValue(
                __instance, out LocalGunshotAudioTuningBox previous) &&
                previous.Context?.Routing != null && !previous.Context.Routing.BurstEnded &&
                (previous.Context.Routing.BodySource == null ||
                 previous.Context.Routing.BodySource.source1?.isPlaying == true ||
                 previous.Context.Routing.BodySource.source2?.isPlaying == true))
                previousRouting = previous.Context.Routing;
            if (__instance != null) LastAutomaticTuning.Remove(__instance);

            if (Plugin.Runtime == null || Plugin.ModConfig == null || !Plugin.ModConfig.Enabled.Value)
            {
                return null;
            }

            if (!ShotDescriptorFactory.TryCreate(
                __instance,
                ammo,
                shotPosition,
                shotDirection,
                out ShotDescriptor descriptor))
            {
                return null;
            }

            DetailedDiagnostics.TryBeginLocalShot(out DiagnosticShotToken diagnosticShot);
            TuningSnapshot tuning = Plugin.ModConfig.GetTuning();
            ExposureResult exposure = ExposureModel.Calculate(descriptor, tuning);
            float directBoostDb = DirectLoudnessModel.CalculateBoostDb(descriptor, exposure, tuning);
            float normalizedImpact = Mathf.Clamp01(directBoostDb / Mathf.Max(0.01f, tuning.MaximumDirectBoostDb));
            float directBodyGain = Mathf.Clamp(
                tuning.DirectBodyGain * (0.5f + normalizedImpact),
                0f,
                0.5f);
            float pressureFrequencyHz = DirectLoudnessModel.CalculatePressureFrequencyHz(exposure, tuning);
            directBodyGain = DirectLoudnessModel.CalculatePitchedBodyGain(descriptor, tuning);
            var audioTuning = new LocalGunshotAudioTuning(
                directBoostDb,
                directBodyGain,
                pressureFrequencyHz,
                tuning.AutomaticPitchedRoute,
                tuning.PitchedLayerSemitones,
                tuning.PitchedLayerHighpassHz,
                tuning.PitchedLayerLowpassHz,
                tuning.PitchedLayerFadePercent,
                tuning.AutomaticPitchedTailSeconds,
                tuning.PitchedLayerGainDb,
                tuning.PitchedLayerOcclusion,
                tuning.PitchedLayerOccludedLowpassHz,
                __instance.IsAutoWeapon
                    ? __instance.BeatLn / Mathf.Clamp(pitchMult, 0.965f, 1.045f)
                    : 0f,
                descriptor.IsIndoor && tuning.IndoorRoomStrength > 0.001f,
                Mathf.Clamp(tuning.IndoorRoomStrength, 0f, 2f),
                tuning.IndoorEarlyReflectionsSendDb,
                tuning.IndoorReverbSendDb,
                tuning.IndoorReverbReach,
                tuning.AutomaticTailMode,
                tuning.LowEndNormalizationPercent,
                tuning.CaliberContrastPercent,
                headphonesDamping: default,
                normalizeBass: descriptor.WeaponCategory == WeaponCategory.Pistol && !__instance.IsAutoWeapon,
                automaticReportOverlapShots: tuning.AutomaticReportOverlapShots,
                automaticLateToleranceScale: tuning.AutomaticLateToleranceScale);

            state = new ShotProcessingState
            {
                Shot = descriptor,
                Exposure = exposure,
                Tuning = tuning,
                DirectBoostDb = directBoostDb,
                DirectBodyGain = directBodyGain,
                AudioTuning = audioTuning,
                DiagnosticShot = diagnosticShot
            };

            if (__instance.IsAutoWeapon)
            {
                state.AudioContext = new AutomaticShotContext(previousRouting, diagnosticShot);
                LastAutomaticTuning.Add(
                    __instance,
                    new LocalGunshotAudioTuningBox(audioTuning, state.AudioContext));
            }

            DirectGunshotAudioPatch.BeginLocalShot(
                audioTuning, state.AudioContext, diagnosticShot);
            return state;
        }

        internal static bool TryGetLastAutomaticTuning(
            WeaponSoundPlayer soundPlayer,
            out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context)
        {
            tuning = default;
            context = null;
            if (soundPlayer == null ||
                !LastAutomaticTuning.TryGetValue(
                    soundPlayer,
                    out LocalGunshotAudioTuningBox boxed))
            {
                return false;
            }

            tuning = boxed.Value;
            context = boxed.Context;
            return true;
        }

        private static void Postfix(WeaponSoundPlayer __instance, ShotProcessingState __state)
        {
            long trace = PerformanceTrace.Begin();
            int tunedAudioSamples = DirectGunshotAudioPatch.EndLocalShot();
            if (__state != null)
            {
                if (tunedAudioSamples == 0 && TryPlayAutomaticBeat(__instance, __state.AudioTuning, __state.AudioContext))
                {
                    tunedAudioSamples = 1;
                }
                __state.TunedAudioSamples = tunedAudioSamples;
                Plugin.Runtime?.HandleShot(__state);
            }
            // One shot is one FireBullet hook call: the prefix counted it.
            PerformanceTrace.End(PerformanceArea.HookFireBullet, trace, countCall: false);
        }

        private static bool TryPlayAutomaticBeat(
            WeaponSoundPlayer soundPlayer,
            LocalGunshotAudioTuning tuning,
            AutomaticShotContext context)
        {
            if (soundPlayer == null ||
                !soundPlayer.IsAutoWeapon ||
                tuning.PitchedLayerLoopBeatSeconds <= 0.001f ||
                EftAudioFields.Queue(soundPlayer) is not SuperBetterAudioQueue queue ||
                queue.AudioSources == null)
            {
                return false;
            }

            SuperSource authoritativeSource = context?.Routing.BodySource;
            int lastSource = EftAudioFields.LastSource(queue);
            if (authoritativeSource == null && lastSource >= 0 &&
                lastSource < queue.AudioSources.Length)
                authoritativeSource = queue.AudioSources[lastSource] as SuperSource;

            foreach (BetterSource betterSource in queue.AudioSources)
            {
                if (betterSource is not SuperSource source || !source.Loop)
                {
                    continue;
                }

                bool firstPlaying = source.source1 != null &&
                    source.source1.isPlaying &&
                    source.source1.clip != null;
                bool secondPlaying = source.source2 != null &&
                    source.source2.isPlaying &&
                    source.source2.clip != null;
                if (!firstPlaying && !secondPlaying)
                {
                    continue;
                }
                if (authoritativeSource != null && !ReferenceEquals(authoritativeSource, source))
                    continue;

                double start = AudioSettings.dspTime;
                bool rebound = LocalGunshotAudioProcessor.EnsureAutomaticRoute(
                    source, tuning, context, out bool reboundPlayed);
                bool pitchedPlayed = !rebound && LocalGunshotAudioProcessor.TriggerAutomaticPitchedBeat(
                    source, tuning, tuning.PitchedLayerLoopBeatSeconds, context);
                bool played = reboundPlayed || pitchedPlayed;
                DiagnosticShotToken diagnosticShot = context?.DiagnosticShot ?? default;
                if (played && DetailedDiagnostics.IsActive(diagnosticShot))
                {
                    string clipName = firstPlaying
                        ? source.source1.clip.name
                        : source.source2.clip.name;
                    LocalGunshotPlaybackProbe.Schedule(
                        source,
                        (tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.CachedReport
                            ? "auto-cache:"
                            : "auto-dsp:") + clipName,
                        start,
                        tuning.AutomaticPitchedRoute,
                        diagnosticShot);
                }
                if (played)
                {
                    return true;
                }
            }

            return false;
        }

        private static Exception Finalizer(Exception __exception)
        {
            DirectGunshotAudioPatch.EndLocalShot();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.UpdatePitch))]
    internal static class AutomaticPitchTimelinePatch
    {

        private static void Postfix(WeaponSoundPlayer __instance, float pitch)
        {
            using (PerformanceTrace.Measure(PerformanceArea.HookUpdatePitch))
            {
                // EFT calls this for every weapon it plays, ours and everyone
                // else's. Only a weapon this mod is currently tracking can own an
                // automatic timeline, so decide that before any reflection.
                if (__instance == null || !__instance.IsAutoWeapon || __instance.BeatLn <= 0.001f ||
                    !FireBulletPatch.TryGetLastAutomaticTuning(__instance, out _, out _) ||
                    EftAudioFields.Queue(__instance) is not SuperBetterAudioQueue queue || queue.AudioSources == null)
                    return;
                float interval = __instance.BeatLn / Mathf.Clamp(pitch, 0.965f, 1.045f);
                double now = AudioSettings.dspTime;
                foreach (BetterSource betterSource in queue.AudioSources)
                    if (betterSource is SuperSource source && source.Loop)
                        LocalGunshotAudioProcessor.UpdateAutomaticInterval(source, now, interval);
            }
        }
    }

    /// <summary>
    /// EFT enqueues the authored automatic tail when the trigger is released,
    /// outside FireBullet. Re-open only the sample-tagging scope so that this
    /// real weapon tail uses the same pitched path as a pistol one-shot.
    /// </summary>
    [HarmonyPatch(typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.StopFiringLoop))]
    internal static class StopFiringLoopPatch
    {
        private static void Prefix(WeaponSoundPlayer __instance, out bool __state)
        {
            LocalGunshotAudioTuning tuning = default;
            AutomaticShotContext context = null;
            __state = Plugin.Runtime != null &&
                Plugin.ModConfig != null &&
                Plugin.ModConfig.Enabled.Value &&
                FireBulletPatch.TryGetLastAutomaticTuning(
                    __instance,
                    out tuning,
                    out context);
            if (__state)
            {
                DirectGunshotAudioPatch.BeginLocalShot(
                    tuning,
                    context,
                    context?.DiagnosticShot ?? default);
            }
        }

        private static void Postfix(WeaponSoundPlayer __instance, bool __state)
        {
            if (!__state)
            {
                return;
            }

            int taggedSamples = DirectGunshotAudioPatch.EndLocalShot();
            if (FireBulletPatch.TryGetLastAutomaticTuning(
                __instance, out _, out AutomaticShotContext context))
                context.Routing.BurstEnded = true;
            DiagnosticShotToken diagnosticShot = context?.DiagnosticShot ?? default;
            if (DetailedDiagnostics.IsActive(diagnosticShot))
            {
                DetailedDiagnostics.Commit(
                    diagnosticShot,
                    DiagnosticEventKind.AutomaticTimeline,
                    $"automatic authored tail tagged samples={taggedSamples}");
            }
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                DirectGunshotAudioPatch.EndLocalShot();
            }
            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(SuperBetterAudioQueue),
        nameof(SuperBetterAudioQueue.Enqueue),
        new[]
        {
            typeof(AudioClip),
            typeof(AudioClip),
            typeof(float),
            typeof(double),
            typeof(float),
            typeof(float),
            typeof(float)
        })]
    internal static class DirectGunshotAudioPatch
    {
        [ThreadStatic]
        private static LocalGunshotAudioTuning _localShotTuning;

        [ThreadStatic]
        private static bool _insideLocalShot;

        [ThreadStatic]
        private static int _registeredSamples;

        [ThreadStatic]
        private static AutomaticShotContext _context;

        [ThreadStatic]
        private static DiagnosticShotToken _diagnosticShot;

        internal static void BeginLocalShot(
            LocalGunshotAudioTuning tuning,
            AutomaticShotContext context = null,
            DiagnosticShotToken diagnosticShot = default)
        {
            _context = context;
            _diagnosticShot = diagnosticShot;
            _localShotTuning = tuning;
            _registeredSamples = 0;
            _insideLocalShot = true;
        }

        internal static int EndLocalShot()
        {
            int registeredSamples = _registeredSamples;
            _insideLocalShot = false;
            _localShotTuning = default;
            _context = null;
            _diagnosticShot = default;
            _registeredSamples = 0;
            return registeredSamples;
        }

        private static void Postfix(SuperBetterAudioQueue __instance)
        {
            using (PerformanceTrace.Measure(PerformanceArea.HookEnqueue))
                RegisterLastQueuedSample(__instance);
        }

        internal static bool TryGetCurrentTuning(out LocalGunshotAudioTuning tuning)
        {
            tuning = _localShotTuning;
            return _insideLocalShot;
        }

        internal static void RegisterLastQueuedSample(SuperBetterAudioQueue queue)
        {
            if (queue?._samples == null)
            {
                return;
            }

            SuperAudioSample lastSample = null;
            foreach (SuperAudioSample sample in queue._samples)
            {
                lastSample = sample;
            }

            if (lastSample == null)
            {
                return;
            }

            if (!_insideLocalShot)
            {
                // Not our shot. The queue hands out the same sample objects again,
                // so clear whatever an earlier shot may have left on this one.
                LocalGunshotSampleRegistry.Forget(lastSample);
                return;
            }

            LocalGunshotSampleRegistry.Register(
                lastSample, _localShotTuning, _context, _diagnosticShot);
            _registeredSamples++;
        }
    }

    [HarmonyPatch(typeof(SuperAudioSample), nameof(SuperAudioSample.PlayOn))]
    internal static class LocalGunshotSamplePlaybackPatch
    {
        private static void Prefix(
            SuperAudioSample __instance,
            SuperSource source,
            out LocalGunshotPlaybackState __state)
        {
            __state = null;
            long trace = PerformanceTrace.Begin();
            if (!LocalGunshotSampleRegistry.TryTake(__instance, out LocalGunshotAudioTuning tuning,
                out AutomaticShotContext context, out DiagnosticShotToken diagnosticShot) ||
                Plugin.ModConfig?.Enabled.Value != true)
            {
                LocalGunshotAudioProcessor.Bypass(source);
                // Every sound EFT plays through this pool passes here, not only
                // the local player's: that cost is measured separately.
                PerformanceTrace.End(PerformanceArea.HookPlayOnForeign, trace);
                return;
            }

            string clipName = DetailedDiagnostics.IsActive(diagnosticShot)
                ? (__instance.Clip1 != null ? __instance.Clip1.name : "null")
                : null;
            float originalDurationSeconds = PitchedGunshotLayer.CalculateOriginalDuration(
                __instance.Start,
                __instance.End,
                tuning.PitchedLayerLoopBeatSeconds,
                __instance.Clip1,
                __instance.Clip2,
                __instance.Pitch);
            bool automaticBodyLoop =
                __instance.End <= __instance.Start &&
                tuning.PitchedLayerLoopBeatSeconds > 0.001f;
            bool roomRouteAvailable = LocalGunshotAudioProcessor.Apply(
                source, tuning, automaticBodyLoop, __instance.Start);
            if (automaticBodyLoop)
            {
                LocalGunshotAudioProcessor.BindAutomaticRoutes(
                    source, tuning, __instance.Clip1, __instance.Clip2,
                    __instance.Start, context);
                LocalGunshotAudioProcessor.PrepareAutomaticPitchedBeat(
                    source,
                    tuning,
                    __instance.Clip1,
                    __instance.Clip2,
                    __instance.Start,
                    originalDurationSeconds);
            }
            __state = new LocalGunshotPlaybackState(
                source,
                clipName,
                __instance.Start,
                originalDurationSeconds,
                tuning,
                automaticBodyLoop,
                __instance.Clip1,
                __instance.Clip2,
                context,
                diagnosticShot);
            if (DetailedDiagnostics.IsActive(diagnosticShot))
            {
                DetailedDiagnostics.Commit(
                    diagnosticShot,
                    DiagnosticEventKind.AudioApply,
                    $"audio apply clip={clipName} source={source.GetType().Name} " +
                    $"impactTarget={tuning.DirectBoostDb:0.0}dB " +
                    $"autoRoute={tuning.AutomaticPitchedRoute} " +
                    $"pitchDown={tuning.PitchedLayerSemitones:0.0}st " +
                    $"pitchBand={tuning.PitchedLayerHighpassHz:0}-{tuning.PitchedLayerLowpassHz:0}Hz " +
                    $"pitchFade={tuning.PitchedLayerFadePercent:0}% " +
                    $"autoTail={tuning.AutomaticPitchedTailSeconds * 1000f:0}ms " +
                    $"pitchGain={tuning.PitchedLayerGainDb:+0.0;-0.0;0.0}dB " +
                    $"pitchOcclusion={tuning.PitchedLayerOcclusion} " +
                    $"automaticBodyLoop={automaticBodyLoop} " +
                    $"originalDuration={originalDurationSeconds * 1000f:0}ms " +
                    $"indoorRoom={tuning.ApplyIndoorRoom} " +
                    $"roomMix={tuning.IndoorRoomMix:0.00} " +
                    $"roomRoute={roomRouteAvailable} " +
                    $"early={tuning.EarlyReflectionsSendDb:0.0}dB " +
                    $"reverb={tuning.ReverbSendDb:0.0}dB reach={tuning.ReverbReach:0.00} " +
                    // The game's own source, as it is at the moment of the shot.
                    // Everything else here describes what this mod adds; nothing
                    // said what state the shot itself was left in, which is the
                    // one place a mode left behind by an earlier setting could
                    // quietly attenuate or filter every round.
                    DescribeDonor(source));
            }
            LocalGunshotPlaybackScope.Begin(source);
            PerformanceTrace.End(PerformanceArea.HookPlayOnLocal, trace);
        }

        /// <summary>
        /// The state of the game's own weapon source at the moment of the shot,
        /// and how many of this mod's components are sitting on it. A component
        /// left behind by a setting that is no longer selected is inert only as
        /// long as it is disabled; this says whether that holds.
        /// </summary>
        private static string DescribeDonor(SuperSource source)
        {
            AudioSource donor = source?.source1 ?? source?.source2;
            if (donor == null) return "donor=none";
            GalSourceBinding binding = GalSourceBinding.Find(source);
            AutomaticPitchedGunshotFilter pitched = binding?.PitchedFilter(donor);
            AutomaticBeatCapture capture = binding?.Capture(donor);
            return
                $"donorVolume={donor.volume:0.000} donorMute={donor.mute} " +
                $"donorGroup={donor.outputAudioMixerGroup?.name ?? "none"} " +
                $"donorBypassEffects={donor.bypassEffects} " +
                $"donorBypassListener={donor.bypassListenerEffects} " +
                $"donorBypassReverb={donor.bypassReverbZones} " +
                $"onDonor=pitched:{Attachment(pitched)} " +
                $"capture:{Attachment(capture)}";
        }

        private static string Attachment(Behaviour component) =>
            component == null ? "-" : component.enabled ? "on" : "off";

        private static void Postfix(LocalGunshotPlaybackState __state)
        {
            if (__state == null)
            {
                return;
            }

            long trace = PerformanceTrace.Begin();
            LocalGunshotPlaybackScope.End(__state.Source);
            try
            {
                if (__state.AutomaticBodyLoop)
                {
                    if (__state.Tuning.AutomaticPitchedRoute ==
                        AutomaticPitchedRoute.CachedReport)
                    {
                        LocalGunshotAudioProcessor.PlayPreparedAutomaticPitchedBeat(
                            __state.Source,
                            __state.Tuning,
                            __state.ClipA,
                            __state.ClipB,
                            __state.ScheduledStart,
                            __state.OriginalDurationSeconds,
                            __state.Context);
                    }
                    else
                    {
                        LocalGunshotAudioProcessor.TriggerAutomaticPitchedBeat(
                            __state.Source,
                            __state.Tuning,
                            __state.OriginalDurationSeconds);
                    }
                }
                else if (__state.Context?.ShouldPlayReleaseCopy != false)
                {
                    PitchedGunshotLayer.Play(
                        __state.Source,
                        __state.Tuning,
                        __state.ScheduledStart,
                        __state.OriginalDurationSeconds);
                }
                else if (DetailedDiagnostics.IsActive(__state.DiagnosticShot))
                {
                    DetailedDiagnostics.Commit(
                        __state.DiagnosticShot,
                        DiagnosticEventKind.AutomaticTimeline,
                        $"automatic release copy skipped clip={__state.ClipName}: per-shot tail already scheduled");
                }
                LocalGunshotPlaybackProbe.Schedule(
                    __state.Source,
                    __state.ClipName,
                    __state.ScheduledStart,
                    __state.Tuning.AutomaticPitchedRoute,
                    __state.DiagnosticShot);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning($"optional low-end playback failed: {exception.Message}");
            }
            // The prefix already counted this playback as one hook call.
            PerformanceTrace.End(PerformanceArea.HookPlayOnLocal, trace, countCall: false);
        }

        private static Exception Finalizer(Exception __exception, LocalGunshotPlaybackState __state)
        {
            if (__state != null)
            {
                LocalGunshotPlaybackScope.End(__state.Source);
            }
            return __exception;
        }
    }

    internal sealed class LocalGunshotPlaybackState
    {
        internal readonly SuperSource Source;
        internal readonly string ClipName;
        internal readonly double ScheduledStart;
        internal readonly float OriginalDurationSeconds;
        internal readonly LocalGunshotAudioTuning Tuning;
        internal readonly bool AutomaticBodyLoop;
        internal readonly AudioClip ClipA;
        internal readonly AudioClip ClipB;
        internal readonly AutomaticShotContext Context;
        internal readonly DiagnosticShotToken DiagnosticShot;

        internal LocalGunshotPlaybackState(
            SuperSource source,
            string clipName,
            double scheduledStart,
            float originalDurationSeconds,
            LocalGunshotAudioTuning tuning,
            bool automaticBodyLoop,
            AudioClip clipA,
            AudioClip clipB,
            AutomaticShotContext context,
            DiagnosticShotToken diagnosticShot)
        {
            Source = source;
            ClipName = clipName;
            ScheduledStart = scheduledStart;
            OriginalDurationSeconds = originalDurationSeconds;
            Tuning = tuning;
            AutomaticBodyLoop = automaticBodyLoop;
            ClipA = clipA;
            ClipB = clipB;
            Context = context;
            DiagnosticShot = diagnosticShot;
        }
    }

    internal static class LocalGunshotPlaybackScope
    {
        [ThreadStatic]
        private static BetterSource _source;

        internal static void Begin(BetterSource source)
        {
            _source = source;
        }

        internal static void End(BetterSource source)
        {
            if (ReferenceEquals(_source, source))
            {
                _source = null;
            }
        }

        internal static bool Matches(BetterSource source)
        {
            return ReferenceEquals(_source, source);
        }
    }

    [HarmonyPatch(typeof(BetterSource), nameof(BetterSource.RefreshSpatialization))]
    internal static class LocalGunshotForceStereoPatch
    {
        private static void Prefix(BetterSource __instance, ref bool enabledSpat)
        {
            long trace = PerformanceTrace.Begin();
            if (LocalGunshotPlaybackScope.Matches(__instance))
            {
                enabledSpat = false;
            }
            PerformanceTrace.End(PerformanceArea.HookSpatialization, trace);
        }
    }

    [HarmonyPatch(typeof(BetterAudio), nameof(BetterAudio.ReleaseQueue))]
    internal static class LocalGunshotQueueReleasePatch
    {
        private static void Prefix(BetterAudioQueue queue)
        {
            using (PerformanceTrace.Measure(PerformanceArea.HookReleaseQueue))
            {
                if (queue?.AudioSources == null)
                {
                    return;
                }

                foreach (BetterSource source in queue.AudioSources)
                {
                    LocalGunshotAudioProcessor.Bypass(source);
                }

                // A released queue keeps its sample objects for the next sound.
                // Any tag still on them belongs to a shot that never played.
                if (queue is SuperBetterAudioQueue superQueue && superQueue._samples != null)
                    foreach (SuperAudioSample sample in superQueue._samples)
                        LocalGunshotSampleRegistry.Forget(sample);
            }
        }
    }
}
