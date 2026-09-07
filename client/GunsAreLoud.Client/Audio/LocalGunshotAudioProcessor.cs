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

            ConfigureFilter(source.source1, tuning, !automaticBodyLoop, scheduledStart);
            ConfigureFilter(source.source2, tuning, !automaticBodyLoop, scheduledStart);

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

        internal static void Bypass(BetterSource source)
        {
            if (source == null)
            {
                return;
            }

            BypassFilter(source.source1);
            if (source is SuperSource superSource)
            {
                BypassFilter(superSource.source2);
            }
            if (source is ReverbSuperSource reverbSource)
            {
                RestoreRoomResponse(reverbSource);
            }
            source.GetComponent<PitchedGunshotLayer>()?.StopAll();
            source.GetComponent<AutomaticBeatTimeline>()?.StopTimeline();
            source.GetComponent<AutomaticImpactTimeline>()?.StopTimeline();
        }

        internal static void PrepareAutomaticPitchedBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            AudioClip clipA,
            AudioClip clipB,
            double scheduledStart,
            float durationSeconds)
        {
            if (source == null ||
                tuning.LowEndMode != GunshotLowEndMode.PitchedCopy ||
                tuning.AutomaticPitchedRoute != AutomaticPitchedRoute.CachedReport ||
                durationSeconds <= 0.001f)
            {
                CancelCapture(source?.source1);
                CancelCapture(source?.source2);
                return;
            }

            // A cached route must not inherit an active built-in DSP voice from
            // an F12 route change on a pooled EFT source.
            source.source1?.GetComponent<AutomaticPitchedGunshotFilter>()?.Bypass();
            source.source2?.GetComponent<AutomaticPitchedGunshotFilter>()?.Bypass();
            ArmCapture(
                source.source1,
                clipA,
                scheduledStart,
                durationSeconds);
            ArmCapture(
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

            AutomaticBeatTimeline timeline = source.GetComponent<AutomaticBeatTimeline>();
            if (timeline == null)
            {
                timeline = source.gameObject.AddComponent<AutomaticBeatTimeline>();
            }
            return timeline.Begin(
                source,
                tuning,
                clipA,
                clipB,
                scheduledStart,
                durationSeconds,
                context);
        }

        internal static bool BeginAutomaticImpactTimeline(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            double firstBoundary,
            double streamStart = double.NaN)
        {
            if (source == null || tuning.LowEndMode != GunshotLowEndMode.OriginalBand)
            {
                return false;
            }

            AutomaticImpactTimeline timeline = source.GetComponent<AutomaticImpactTimeline>();
            if (timeline == null)
            {
                timeline = source.gameObject.AddComponent<AutomaticImpactTimeline>();
            }
            double anchor = double.IsNaN(streamStart) ? firstBoundary : streamStart;
            source.source1?.GetComponent<LocalGunshotImpactFilter>()?.BeginStream(anchor);
            source.source2?.GetComponent<LocalGunshotImpactFilter>()?.BeginStream(anchor);
            return timeline.Begin(source, tuning, firstBoundary);
        }

        internal static bool TriggerAutomaticImpactBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning)
        {
            return source != null &&
                tuning.LowEndMode == GunshotLowEndMode.OriginalBand &&
                source.GetComponent<AutomaticImpactTimeline>()?.TriggerNext(tuning) == true;
        }

        internal static bool EnsureAutomaticRoute(
            SuperSource source, LocalGunshotAudioTuning tuning, AutomaticShotContext context,
            out bool played)
        {
            played = false;
            if (source == null || context == null) return false;
            bool original = tuning.LowEndMode == GunshotLowEndMode.OriginalBand;
            bool cached = tuning.LowEndMode == GunshotLowEndMode.PitchedCopy &&
                tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.CachedReport;
            int selected = original ? 1 : cached ? 2 : 3;
            AutomaticBurstRouting routing = context.Routing;
            int previous = !routing.RouteInitialized ? selected :
                routing.LastLowEndMode == GunshotLowEndMode.OriginalBand ? 1 :
                routing.LastPitchedRoute == AutomaticPitchedRoute.CachedReport ? 2 : 3;
            AutomaticImpactTimeline impact = source.GetComponent<AutomaticImpactTimeline>();
            AutomaticBeatTimeline beat = source.GetComponent<AutomaticBeatTimeline>();
            bool impactStreamReady =
                source.source1?.GetComponent<LocalGunshotImpactFilter>()?.NeedsStreamReset != true &&
                source.source2?.GetComponent<LocalGunshotImpactFilter>()?.NeedsStreamReset != true;
            bool active = routing.RouteInitialized && ReferenceEquals(routing.BodySource, source) &&
                (original ? impact?.Active == true && impactStreamReady :
                cached ? beat?.Active == true : true);
            bool rebind = AutomaticRouteTransition.ShouldRebind(active, previous, selected);
            routing.BodySource = source;
            routing.LastLowEndMode = tuning.LowEndMode;
            routing.LastPitchedRoute = tuning.AutomaticPitchedRoute;
            routing.RouteInitialized = true;
            if (!rebind) return false;

            if (previous != selected)
            {
                source.GetComponent<PitchedGunshotLayer>()?.StopAll();
                source.source1?.GetComponent<AutomaticPitchedGunshotFilter>()?.Bypass();
                source.source2?.GetComponent<AutomaticPitchedGunshotFilter>()?.Bypass();
            }

            AudioSource donor = source.source1 != null && source.source1.isPlaying
                ? source.source1 : source.source2;
            double now = AudioSettings.dspTime;
            double boundary = donor != null && donor.clip != null
                ? AutomaticBeatTiming.CalculateCurrentBoundary(
                    now, donor.timeSamples, donor.clip.samples, donor.clip.frequency, donor.pitch)
                : now;
            if (original)
            {
                impact?.StopTimeline();
                played = BeginAutomaticImpactTimeline(source, tuning, boundary, now);
                return true;
            }
            if (cached)
            {
                beat?.StopTimeline();
                if (beat == null) beat = source.gameObject.AddComponent<AutomaticBeatTimeline>();
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
                context.Routing.LastLowEndMode = tuning.LowEndMode;
                context.Routing.LastPitchedRoute = tuning.AutomaticPitchedRoute;
                context.Routing.RouteInitialized = true;
            }
            AutomaticBeatTimeline beat = source.GetComponent<AutomaticBeatTimeline>();
            if (beat == null) beat = source.gameObject.AddComponent<AutomaticBeatTimeline>();
            if (tuning.AutomaticPitchedRoute != AutomaticPitchedRoute.CachedReport)
                beat.Bind(source, tuning, clipA, clipB, firstBoundary, tuning.PitchedLayerLoopBeatSeconds);
        }

        internal static void ConfigureAutomaticImpactBeat(SuperSource source, LocalGunshotAudioTuning tuning)
        {
            if (source == null) return;
            // Preserve a preceding fallback's stream until its envelope ends.
            if (tuning.LowEndMode == GunshotLowEndMode.PitchedCopy &&
                tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.CachedReport) return;
            ConfigureFilter(source.source1, tuning, false, 0.0);
            ConfigureFilter(source.source2, tuning, false, 0.0);
        }

        internal static bool PlayAutomaticFallback(SuperSource source, LocalGunshotAudioTuning tuning,
            double boundary, double streamStart)
        {
            if (source == null || tuning.DirectBodyGain <= 0.001f) return false;
            bool first = FallbackChannel(source.source1, tuning, boundary, streamStart);
            bool second = FallbackChannel(source.source2, tuning, boundary, streamStart);
            return first || second;
        }

        private static bool FallbackChannel(AudioSource source, LocalGunshotAudioTuning tuning,
            double boundary, double streamStart)
        {
            if (source == null || source.clip == null) return false;
            // The live tap follows this filter in Unity's component chain. Do
            // not bake the temporary addition into a cached donor. Silent
            // warmup publishes clean body PCM independently of this source.
            CancelCapture(source);
            LocalGunshotImpactFilter filter = source.GetComponent<LocalGunshotImpactFilter>();
            if (filter == null) filter = source.gameObject.AddComponent<LocalGunshotImpactFilter>();
            filter.Configure(tuning.DirectBodyGain, tuning.PressureFrequencyHz,
                AudioSettings.outputSampleRate, tuning.HeadphonesDamping.BodyGain,
                tuning.HeadphonesDamping.TailDbPerSecond, armOnset: false);
            if (filter.NeedsStreamReset) filter.BeginStream(streamStart);
            bool played = filter.Trigger(boundary);
            filter.FinishAfterCurrentEnvelopes(System.Math.Max(boundary, AudioSettings.dspTime));
            return played;
        }

        internal static void RetireAutomaticFallback(SuperSource source)
        {
            double now = AudioSettings.dspTime;
            source?.source1?.GetComponent<LocalGunshotImpactFilter>()?.FinishAfterCurrentEnvelopes(now);
            source?.source2?.GetComponent<LocalGunshotImpactFilter>()?.FinishAfterCurrentEnvelopes(now);
        }

        internal static void UpdateAutomaticInterval(SuperSource source, double changeTime, float beatSeconds)
        {
            source?.GetComponent<AutomaticImpactTimeline>()?.UpdateInterval(changeTime, beatSeconds);
            source?.GetComponent<AutomaticBeatTimeline>()?.UpdateInterval(changeTime, beatSeconds);
        }

        internal static bool TriggerAutomaticPitchedBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            float durationSeconds,
            AutomaticShotContext context = null)
        {
            if (source == null ||
                tuning.LowEndMode != GunshotLowEndMode.PitchedCopy ||
                durationSeconds <= 0.001f)
            {
                return false;
            }

            if (tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.CachedReport)
            {
                return source.GetComponent<AutomaticBeatTimeline>()?.TriggerNext(tuning, context) == true;
            }

            source.GetComponent<PitchedGunshotLayer>()?.StopAll();
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

        private static void ConfigureFilter(AudioSource source, LocalGunshotAudioTuning tuning,
            bool armOnset, double scheduledStart)
        {
            if (source == null)
            {
                return;
            }

            LocalGunshotImpactFilter filter = source.GetComponent<LocalGunshotImpactFilter>();
            if (filter == null)
            {
                filter = source.gameObject.AddComponent<LocalGunshotImpactFilter>();
            }
            filter.Configure(
                tuning.LowEndMode == GunshotLowEndMode.OriginalBand
                    ? tuning.DirectBodyGain
                    : 0f,
                tuning.PressureFrequencyHz,
                AudioSettings.outputSampleRate,
                tuning.HeadphonesDamping.BodyGain,
                tuning.HeadphonesDamping.TailDbPerSecond,
                armOnset);
            if (armOnset)
                filter.BeginStream(scheduledStart);
        }

        private static void BypassFilter(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.GetComponent<LocalGunshotImpactFilter>()?.Bypass();
            source.GetComponent<AutomaticPitchedGunshotFilter>()?.Bypass();
            source.GetComponent<AutomaticBeatCapture>()?.Cancel();
        }

        private static void CancelCapture(AudioSource source)
        {
            source?.GetComponent<AutomaticBeatCapture>()?.Cancel();
        }

        private static void ArmCapture(
            AudioSource source,
            AudioClip clip,
            double scheduledStart,
            float durationSeconds)
        {
            if (source == null || clip == null)
            {
                return;
            }

            AutomaticBeatCapture capture = source.GetComponent<AutomaticBeatCapture>();
            if (capture == null)
            {
                capture = source.gameObject.AddComponent<AutomaticBeatCapture>();
            }
            capture.Arm(
                clip,
                scheduledStart,
                durationSeconds,
                AudioSettings.outputSampleRate);
        }

        private static void GetCachedBeat(
            AudioClip clip,
            float durationSeconds,
            out CachedAutomaticBeat beat)
        {
            AutomaticBeatClipCache.TryGet(
                clip,
                durationSeconds,
                AudioSettings.outputSampleRate,
                out beat);
        }

        private static bool ConfigureAndTriggerAutomaticFilter(
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

            AutomaticPitchedGunshotFilter filter =
                source.GetComponent<AutomaticPitchedGunshotFilter>();
            if (filter == null)
            {
                filter = source.gameObject.AddComponent<AutomaticPitchedGunshotFilter>();
            }

            filter.Configure(
                route,
                pitchRatio,
                highpassHz,
                lowpassHz,
                durationSeconds,
                fadePercent,
                gain,
                AudioSettings.outputSampleRate,
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
