using System.Runtime.CompilerServices;
using GunsAreLoud.Client.Configuration;

namespace GunsAreLoud.Client.Runtime
{
    internal readonly struct LocalGunshotAudioTuning
    {
        internal readonly float DirectBoostDb;
        internal readonly float DirectBodyGain;
        internal readonly float PressureFrequencyHz;
        internal readonly GunshotLowEndMode LowEndMode;
        internal readonly AutomaticPitchedRoute AutomaticPitchedRoute;
        internal readonly AutomaticTailMode AutomaticTailMode;
        internal readonly float PitchedLayerSemitones;
        internal readonly float PitchedLayerHighpassHz;
        internal readonly float PitchedLayerLowpassHz;
        internal readonly float PitchedLayerFadePercent;
        internal readonly float AutomaticPitchedTailSeconds;
        internal readonly float PitchedLayerGainDb;
        internal readonly float LowEndNormalizationPercent;
        internal readonly float CaliberContrastPercent;
        internal readonly PitchedLayerOcclusionMode PitchedLayerOcclusion;
        internal readonly float PitchedLayerOccludedLowpassHz;
        internal readonly float PitchedLayerLoopBeatSeconds;
        internal readonly bool ApplyIndoorRoom;
        internal readonly float IndoorRoomMix;
        internal readonly float EarlyReflectionsSendDb;
        internal readonly float ReverbSendDb;
        internal readonly float ReverbReach;
        internal readonly IndoorHeadphonesDamping HeadphonesDamping;

        internal LocalGunshotAudioTuning(
            float directBoostDb,
            float directBodyGain,
            float pressureFrequencyHz,
            GunshotLowEndMode lowEndMode,
            AutomaticPitchedRoute automaticPitchedRoute,
            float pitchedLayerSemitones,
            float pitchedLayerHighpassHz,
            float pitchedLayerLowpassHz,
            float pitchedLayerFadePercent,
            float automaticPitchedTailSeconds,
            float pitchedLayerGainDb,
            PitchedLayerOcclusionMode pitchedLayerOcclusion,
            float pitchedLayerOccludedLowpassHz,
            float pitchedLayerLoopBeatSeconds,
            bool applyIndoorRoom,
            float indoorRoomMix,
            float earlyReflectionsSendDb,
            float reverbSendDb,
            float reverbReach,
            AutomaticTailMode automaticTailMode = AutomaticTailMode.FullReportPerShot,
            float lowEndNormalizationPercent = 0f,
            float caliberContrastPercent = 100f,
            IndoorHeadphonesDamping headphonesDamping = default)
        {
            DirectBoostDb = directBoostDb;
            DirectBodyGain = directBodyGain;
            PressureFrequencyHz = pressureFrequencyHz;
            LowEndMode = lowEndMode;
            AutomaticPitchedRoute = automaticPitchedRoute;
            AutomaticTailMode = automaticTailMode;
            PitchedLayerSemitones = pitchedLayerSemitones;
            PitchedLayerHighpassHz = pitchedLayerHighpassHz;
            PitchedLayerLowpassHz = pitchedLayerLowpassHz;
            PitchedLayerFadePercent = pitchedLayerFadePercent;
            AutomaticPitchedTailSeconds = automaticPitchedTailSeconds;
            PitchedLayerGainDb = pitchedLayerGainDb;
            LowEndNormalizationPercent = lowEndNormalizationPercent;
            CaliberContrastPercent = caliberContrastPercent;
            PitchedLayerOcclusion = pitchedLayerOcclusion;
            PitchedLayerOccludedLowpassHz = pitchedLayerOccludedLowpassHz;
            PitchedLayerLoopBeatSeconds = pitchedLayerLoopBeatSeconds;
            ApplyIndoorRoom = applyIndoorRoom;
            IndoorRoomMix = indoorRoomMix;
            EarlyReflectionsSendDb = earlyReflectionsSendDb;
            ReverbSendDb = reverbSendDb;
            ReverbReach = reverbReach;
            HeadphonesDamping = headphonesDamping;
        }
    }

    internal sealed class LocalGunshotAudioTuningBox
    {
        internal readonly LocalGunshotAudioTuning Value;
        internal readonly AutomaticShotContext Context;

        internal LocalGunshotAudioTuningBox(LocalGunshotAudioTuning value, AutomaticShotContext context = null)
        {
            Value = value;
            Context = context;
        }
    }

    // Shared with the queued release tail, not global to the current weapon:
    // a later burst cannot suppress an earlier burst's fallback tail.
    internal sealed class AutomaticShotContext
    {
        internal bool AuthoredTailScheduled;
        internal readonly AutomaticBurstRouting Routing;
        internal bool ShouldPlayReleaseCopy => !AuthoredTailScheduled;

        internal AutomaticShotContext(AutomaticBurstRouting routing = null)
        {
            Routing = routing ?? new AutomaticBurstRouting();
        }
    }

    internal sealed class AutomaticBurstRouting
    {
        internal bool BurstEnded;
        internal SuperSource BodySource;
        internal GunshotLowEndMode LastLowEndMode;
        internal AutomaticPitchedRoute LastPitchedRoute;
        internal bool RouteInitialized;
    }

    internal static class LocalGunshotSampleRegistry
    {
        private static readonly ConditionalWeakTable<SuperAudioSample, LocalGunshotAudioTuningBox> Samples =
            new ConditionalWeakTable<SuperAudioSample, LocalGunshotAudioTuningBox>();

        internal static void Register(SuperAudioSample sample, LocalGunshotAudioTuning tuning,
            AutomaticShotContext context = null)
        {
            if (sample == null)
            {
                return;
            }

            Samples.Remove(sample);
            Samples.Add(sample, new LocalGunshotAudioTuningBox(tuning, context));
        }

        internal static bool TryTake(SuperAudioSample sample, out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context)
        {
            tuning = default;
            context = null;
            if (sample == null || !Samples.TryGetValue(sample, out LocalGunshotAudioTuningBox boxed))
            {
                return false;
            }

            Samples.Remove(sample);
            tuning = boxed.Value;
            context = boxed.Context;
            return true;
        }
    }
}
