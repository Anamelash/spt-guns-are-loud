using System.Runtime.CompilerServices;
using Audio.ReverbSubsystem;
using Audio.SpatialSystem;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal static class LocalGunshotAudioProcessor
    {
        private static readonly ConditionalWeakTable<ReverbSuperSource, RoomResponseState> RoomStates =
            new ConditionalWeakTable<ReverbSuperSource, RoomResponseState>();

        internal static bool Apply(SuperSource source, LocalGunshotAudioTuning tuning,
            bool automaticBodyLoop = false, double scheduledStart = 0.0)
        {
            if (source == null)
            {
                return false;
            }

            GalSourceBinding binding = GalSourceBinding.Of(source);
            if (source is ReverbSuperSource reverbSource)
            {
                if (tuning.ApplyIndoorRoom)
                {
                    ApplyRoomResponse(reverbSource, tuning);
                }
                else
                {
                    RestoreRoomResponse(reverbSource);
                }

                return true;
            }

            return false;
        }

        // These filters stay attached on purpose: EFT re-issues its pooled weapon
        // sources within milliseconds, and add/destroy per shot is real churn.
        // They are switched off instead, so a source playing someone else's sound
        // receives no callback of ours at all, and the whole undo is one component
        // lookup and a flag test once per issuance.
        internal static void Bypass(BetterSource source)
        {
            if (source == null)
            {
                return;
            }

            GalSourceBinding binding = GalSourceBinding.Find(source);
            if (binding == null || !binding.MarkBypassed())
            {
                return;
            }

            BypassChannel(binding, source.source1);
            if (source is SuperSource superSource)
            {
                BypassChannel(binding, superSource.source2);
            }
            if (source is ReverbSuperSource reverbSource)
            {
                RestoreRoomResponse(reverbSource);
            }
            PitchedGunshotLayer.StopForSource(source);
            binding.BeatTimeline()?.StopTimeline();
        }

        internal static void PrepareAutomaticPitchedBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            AudioClip clipA,
            AudioClip clipB,
            double scheduledStart,
            float durationSeconds)
        {
            if (source == null) return;
            GalSourceBinding binding = GalSourceBinding.Of(source);
            if (tuning.AutomaticPitchedRoute != AutomaticPitchedRoute.CachedReport ||
                durationSeconds <= 0.001f)
            {
                CancelCapture(binding, source.source1);
                CancelCapture(binding, source.source2);
                return;
            }

            // A cached route must not inherit an active built-in DSP voice from
            // an F12 route change on a pooled EFT source.
            binding.PitchedFilter(source.source1)?.Bypass();
            binding.PitchedFilter(source.source2)?.Bypass();
            ArmCapture(
                binding,
                source.source1,
                clipA,
                scheduledStart,
                durationSeconds);
            ArmCapture(
                binding,
                source.source2,
                clipB,
                scheduledStart,
                durationSeconds);
        }

        internal static bool PlayPreparedAutomaticPitchedBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            AudioClip clipA,
            AudioClip clipB,
            double scheduledStart,
            float durationSeconds,
            AutomaticShotContext context = null)
        {
            if (source == null ||
                tuning.AutomaticPitchedRoute != AutomaticPitchedRoute.CachedReport)
            {
                return false;
            }

            AutomaticBeatTimeline timeline = GalSourceBinding.Of(source).EnsureBeatTimeline();
            return timeline.Begin(
                source,
                tuning,
                clipA,
                clipB,
                scheduledStart,
                durationSeconds,
                context);
        }

        internal static bool EnsureAutomaticRoute(
            SuperSource source, LocalGunshotAudioTuning tuning, AutomaticShotContext context,
            out bool played)
        {
            played = false;
            if (source == null || context == null) return false;
            bool cached = tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.CachedReport;
            int selected = cached ? 2 : 3;
            AutomaticBurstRouting routing = context.Routing;
            int previous = !routing.RouteInitialized ? selected :
                routing.LastPitchedRoute == AutomaticPitchedRoute.CachedReport ? 2 : 3;
            GalSourceBinding binding = GalSourceBinding.Of(source);
            AutomaticBeatTimeline beat = binding.BeatTimeline();
            bool active = routing.RouteInitialized && ReferenceEquals(routing.BodySource, source) &&
                (cached ? beat?.Active == true : true);
            bool rebind = AutomaticRouteTransition.ShouldRebind(active, previous, selected);
            routing.BodySource = source;
            routing.LastPitchedRoute = tuning.AutomaticPitchedRoute;
            routing.RouteInitialized = true;
            if (!rebind) return false;

            if (previous != selected)
            {
                PitchedGunshotLayer.StopForSource(source);
                binding.PitchedFilter(source.source1)?.Bypass();
                binding.PitchedFilter(source.source2)?.Bypass();
            }

            AudioSource donor = source.source1 != null && source.source1.isPlaying
                ? source.source1 : source.source2;
            double now = AudioSettings.dspTime;
            double boundary = donor != null && donor.clip != null
                ? AutomaticBeatTiming.CalculateCurrentBoundary(
                    now, donor.timeSamples, donor.clip.samples, donor.clip.frequency, donor.pitch)
                : now;
            if (cached)
            {
                beat?.StopTimeline();
                if (beat == null) beat = binding.EnsureBeatTimeline();
                played = beat.Begin(source, tuning, source.source1?.clip, source.source2?.clip, boundary,
                    tuning.PitchedLayerLoopBeatSeconds, context);
                return true;
            }
            return false;
        }

        internal static void BindAutomaticRoutes(
            SuperSource source, LocalGunshotAudioTuning tuning, AudioClip clipA,
            AudioClip clipB, double firstBoundary, AutomaticShotContext context)
        {
            if (source == null) return;
            if (context != null)
            {
                context.Routing.BodySource = source;
                context.Routing.LastPitchedRoute = tuning.AutomaticPitchedRoute;
                context.Routing.RouteInitialized = true;
            }
            AutomaticBeatTimeline beat = GalSourceBinding.Of(source).EnsureBeatTimeline();
            if (tuning.AutomaticPitchedRoute != AutomaticPitchedRoute.CachedReport)
                beat.Bind(source, tuning, clipA, clipB, firstBoundary, tuning.PitchedLayerLoopBeatSeconds);
        }


        internal static void UpdateAutomaticInterval(SuperSource source, double changeTime, float beatSeconds)
        {
            GalSourceBinding binding = GalSourceBinding.Find(source);
            if (binding == null) return;
            binding.BeatTimeline()?.UpdateInterval(changeTime, beatSeconds);
        }

        internal static bool TriggerAutomaticPitchedBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            float durationSeconds,
            AutomaticShotContext context = null)
        {
            if (source == null ||
                durationSeconds <= 0.001f)
            {
                return false;
            }

            GalSourceBinding binding = GalSourceBinding.Of(source);
            if (tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.CachedReport)
            {
                return binding.BeatTimeline()?.TriggerNext(tuning, context) == true;
            }

            PitchedGunshotLayer.StopForSource(source);
            float donorOcclusion = Mathf.Clamp01(source.OcclusionVolumeFactor);
            float lowpassHz = PitchedGunshotLayer.CalculateOccludedLowpass(
                tuning.PitchedLayerLowpassHz,
                tuning.PitchedLayerOccludedLowpassHz,
                donorOcclusion,
                tuning.PitchedLayerOcclusion);
            float highpassHz = Mathf.Clamp(
                tuning.PitchedLayerHighpassHz,
                10f,
                Mathf.Max(10f, lowpassHz - 10f));
            float openGain = PitchedGunshotLayer.CalculateLayerGain(
                tuning.DirectBodyGain,
                tuning.PressureFrequencyHz,
                tuning.PitchedLayerGainDb,
                tuning.CaliberContrastPercent) * tuning.HeadphonesDamping.BodyGain;
            float gain = PitchedGunshotLayer.CalculateOccludedGain(
                openGain,
                donorOcclusion,
                tuning.PitchedLayerOcclusion);
            float pitchRatio = PitchedGunshotLayer.CalculatePitchRatio(
                tuning.PitchedLayerSemitones);

            bool first = ConfigureAndTriggerAutomaticFilter(
                binding,
                source.source1,
                tuning.AutomaticPitchedRoute,
                pitchRatio,
                highpassHz,
                lowpassHz,
                durationSeconds,
                tuning.PitchedLayerFadePercent,
                gain,
                tuning.HeadphonesDamping.TailDbPerSecond);
            bool second = ConfigureAndTriggerAutomaticFilter(
                binding,
                source.source2,
                tuning.AutomaticPitchedRoute,
                pitchRatio,
                highpassHz,
                lowpassHz,
                durationSeconds,
                tuning.PitchedLayerFadePercent,
                gain,
                tuning.HeadphonesDamping.TailDbPerSecond);
            return first || second;
        }

        private static void BypassChannel(GalSourceBinding binding, AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            AutomaticPitchedGunshotFilter pitched = binding.PitchedFilter(source);
            AutomaticBeatCapture capture = binding.Capture(source);
            pitched?.Bypass();
            capture?.Cancel();
            // Bypassing is not enough: a disabled behaviour receives no
            // OnAudioFilterRead at all, whereas a bypassed one still costs a
            // callback per buffer for every foreign sound on this pooled source.
            SetActive(pitched, false);
            // The capture component stays on until it has published its cache,
            // otherwise the next shot would find the cache cold again.
            if (capture == null || !capture.AwaitingPublish) SetActive(capture, false);
        }

        // Every filter resets its own audio-thread stream by generation when it is
        // configured, so re-enabling here cannot resume a stale envelope.
        private static void SetActive(Behaviour component, bool active)
        {
            if (component != null && component.enabled != active) component.enabled = active;
        }

        private static void CancelCapture(GalSourceBinding binding, AudioSource source)
        {
            if (source == null) return;
            AutomaticBeatCapture capture = binding.Capture(source);
            if (capture == null) return;
            capture.Cancel();
            if (!capture.AwaitingPublish) SetActive(capture, false);
        }

        private static void ArmCapture(
            GalSourceBinding binding,
            AudioSource source,
            AudioClip clip,
            double scheduledStart,
            float durationSeconds)
        {
            if (source == null || clip == null || durationSeconds <= 0.001f)
            {
                return;
            }

            // A warm cache needs no live tap. Ask before attaching one: the
            // component used to be added on the first automatic shot with every
            // weapon and then sat on the pooled source for the rest of the raid.
            int rate = Mathf.Max(8000, AudioRuntimeState.OutputSampleRate);
            float span = Mathf.Clamp(durationSeconds, 0.01f, 0.5f);
            if (AutomaticCopyCache.ContainsRoundBody(clip, span, rate))
            {
                CancelCapture(binding, source);
                return;
            }

            GalSourceCensus.NoteSource(source.GetInstanceID());
            AutomaticBeatCapture capture = binding.EnsureCapture(source);
            SetActive(capture, true);
            capture.Arm(
                clip,
                scheduledStart,
                durationSeconds,
                rate);
        }

        private static void GetCachedBeat(
            AudioClip clip,
            float durationSeconds,
            out CachedAutomaticCopy beat)
        {
            AutomaticCopyCache.TryGetRoundBody(
                clip,
                durationSeconds,
                AudioRuntimeState.OutputSampleRate,
                out beat);
        }

        private static bool ConfigureAndTriggerAutomaticFilter(
            GalSourceBinding binding,
            AudioSource source,
            AutomaticPitchedRoute route,
            float pitchRatio,
            float highpassHz,
            float lowpassHz,
            float durationSeconds,
            float fadePercent,
            float gain,
            float headphoneTailDbPerSecond)
        {
            if (source == null || source.clip == null || source.volume <= 0.0001f)
            {
                return false;
            }

            GalSourceCensus.NoteSource(source.GetInstanceID());
            AutomaticPitchedGunshotFilter filter = binding.EnsurePitchedFilter(source);
            SetActive(filter, true);

            filter.Configure(
                route,
                pitchRatio,
                highpassHz,
                lowpassHz,
                durationSeconds,
                fadePercent,
                gain,
                AudioRuntimeState.OutputSampleRate,
                headphoneTailDbPerSecond);
            filter.Trigger();
            return true;
        }

        private static void ApplyRoomResponse(ReverbSuperSource source, LocalGunshotAudioTuning tuning)
        {
            RoomResponseState state = GetRoomResponseState(source);
            if (!state.Captured)
            {
                state.Capture(source);
            }

            state.Apply(source, tuning);
            state.Overridden = true;
        }

        private static RoomResponseState GetRoomResponseState(ReverbSuperSource source)
        {
            if (RoomStates.TryGetValue(source, out RoomResponseState state))
            {
                return state;
            }

            state = new RoomResponseState();
            RoomStates.Add(source, state);
            return state;
        }

        private static void RestoreRoomResponse(ReverbSuperSource source)
        {
            if (!RoomStates.TryGetValue(source, out RoomResponseState state) || !state.Overridden)
            {
                return;
            }

            state.Restore(source);
        }

        private sealed class RoomResponseState
        {
            internal bool Captured;
            internal bool Overridden;
            private bool _enableReverb;
            private SpatializerValues _a;
            private SpatializerValues _b;

            internal void Capture(ReverbSuperSource source)
            {
                _enableReverb = source.EnableReverb;
                _a = SpatializerValues.Capture(source._spatializerA);
                _b = SpatializerValues.Capture(source._spatializerB);
                Captured = true;
            }

            internal void Apply(ReverbSuperSource source, LocalGunshotAudioTuning tuning)
            {
                source.EnableReverb = true;
                _a.Apply(source._spatializerA, tuning);
                _b.Apply(source._spatializerB, tuning);
            }

            internal void Restore(ReverbSuperSource source)
            {
                source.EnableReverb = _enableReverb;
                _a.Restore(source._spatializerA);
                _b.Restore(source._spatializerB);
                Overridden = false;
            }
        }

        private readonly struct SpatializerValues
        {
            private readonly float _earlyReflectionsSendDb;
            private readonly float _reverbSendDb;
            private readonly float _reverbReach;

            private SpatializerValues(float earlyReflectionsSendDb, float reverbSendDb, float reverbReach)
            {
                _earlyReflectionsSendDb = earlyReflectionsSendDb;
                _reverbSendDb = reverbSendDb;
                _reverbReach = reverbReach;
            }

            internal static SpatializerValues Capture(BaseSpatialAudioSource spatializer)
            {
                return spatializer == null
                    ? default
                    : new SpatializerValues(
                        spatializer.EarlyReflectionsSendDB,
                        spatializer.ReverbSendDB,
                        spatializer.ReverbReach);
            }

            internal void Restore(BaseSpatialAudioSource spatializer)
            {
                if (spatializer == null)
                {
                    return;
                }

                spatializer.EarlyReflectionsSendDB = _earlyReflectionsSendDb;
                spatializer.ReverbSendDB = _reverbSendDb;
                spatializer.ReverbReach = _reverbReach;
                spatializer.UpdateParameters();
            }

            internal void Apply(BaseSpatialAudioSource spatializer, LocalGunshotAudioTuning tuning)
            {
                if (spatializer == null)
                {
                    return;
                }

                float mix = Mathf.Clamp01(tuning.IndoorRoomMix);
                spatializer.EarlyReflectionsSendDB = IndoorHeadphonesModel.RoomSend(
                    _earlyReflectionsSendDb,
                    tuning.EarlyReflectionsSendDb,
                    mix,
                    tuning.HeadphonesDamping.EarlyAttenuationDb);
                spatializer.ReverbSendDB = IndoorHeadphonesModel.RoomSend(
                    _reverbSendDb,
                    tuning.ReverbSendDb,
                    mix,
                    tuning.HeadphonesDamping.ReverbAttenuationDb);
                spatializer.ReverbReach = Mathf.Lerp(
                    _reverbReach,
                    Mathf.Clamp01(tuning.ReverbReach),
                    mix) * tuning.HeadphonesDamping.ReachMultiplier;
                spatializer.UpdateParameters();
            }
        }
    }
}
