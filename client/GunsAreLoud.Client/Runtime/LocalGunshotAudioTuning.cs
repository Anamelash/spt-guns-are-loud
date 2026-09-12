using System.Runtime.CompilerServices;
using GunsAreLoud.Client.Configuration;

namespace GunsAreLoud.Client.Runtime
{
    internal readonly struct LocalGunshotAudioTuning
    {
        internal readonly bool NormalizeBass;
        internal readonly float DirectBoostDb;
        internal readonly float DirectBodyGain;
        internal readonly float PressureFrequencyHz;
        internal readonly AutomaticPitchedRoute AutomaticPitchedRoute;
        internal readonly AutomaticTailMode AutomaticTailMode;
        internal readonly float AutomaticReportOverlapShots;
        internal readonly float AutomaticLateToleranceScale;
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
            IndoorHeadphonesDamping headphonesDamping = default, bool normalizeBass = false,
            float automaticReportOverlapShots = 6f,
            float automaticLateToleranceScale = 1f)
        {
            NormalizeBass = normalizeBass;
            DirectBoostDb = directBoostDb;
            DirectBodyGain = directBodyGain;
            PressureFrequencyHz = pressureFrequencyHz;
            AutomaticPitchedRoute = automaticPitchedRoute;
            AutomaticTailMode = automaticTailMode;
            AutomaticReportOverlapShots = automaticReportOverlapShots;
            AutomaticLateToleranceScale = automaticLateToleranceScale;
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
        internal readonly DiagnosticShotToken DiagnosticShot;

        internal LocalGunshotAudioTuningBox(
            LocalGunshotAudioTuning value,
            AutomaticShotContext context = null,
            DiagnosticShotToken diagnosticShot = default)
        {
            Value = value;
            Context = context;
            DiagnosticShot = diagnosticShot;
        }
    }

    // Shared with the queued release tail, not global to the current weapon:
    // a later burst cannot suppress an earlier burst's fallback tail.
    internal sealed class AutomaticShotContext
    {
        internal bool FullReportScheduled;
        internal readonly AutomaticBurstRouting Routing;
        internal readonly DiagnosticShotToken DiagnosticShot;
        internal bool ShouldPlayReleaseCopy => !FullReportScheduled;

        internal AutomaticShotContext(
            AutomaticBurstRouting routing = null,
            DiagnosticShotToken diagnosticShot = default)
        {
            Routing = routing ?? new AutomaticBurstRouting();
            DiagnosticShot = diagnosticShot;
        }
    }

    internal sealed class AutomaticBurstRouting
    {
        internal bool BurstEnded;
        internal SuperSource BodySource;
        internal AutomaticPitchedRoute LastPitchedRoute;
        internal bool RouteInitialized;
    }

    internal static class LocalGunshotSampleRegistry
    {
        private static readonly ConditionalWeakTable<SuperAudioSample, LocalGunshotAudioTuningBox> Samples =
            new ConditionalWeakTable<SuperAudioSample, LocalGunshotAudioTuningBox>();

        internal static void Register(SuperAudioSample sample, LocalGunshotAudioTuning tuning,
            AutomaticShotContext context = null,
            DiagnosticShotToken diagnosticShot = default)
        {
            if (sample == null)
            {
                return;
            }

            Samples.Remove(sample);
            Samples.Add(sample, new LocalGunshotAudioTuningBox(tuning, context, diagnosticShot));
        }

        /// <summary>
        /// Drops any registration on this sample. EFT re-issues sample objects
        /// from its queue, so a tag left by a shot that never played must not make
        /// the next sound on the same object look like ours.
        /// </summary>
        internal static void Forget(SuperAudioSample sample)
        {
            if (sample != null) Samples.Remove(sample);
        }

        internal static bool TryTake(SuperAudioSample sample, out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context,
            out DiagnosticShotToken diagnosticShot)
        {
            tuning = default;
            context = null;
            diagnosticShot = default;
            if (sample == null || !Samples.TryGetValue(sample, out LocalGunshotAudioTuningBox boxed))
            {
                return false;
            }

            Samples.Remove(sample);
            tuning = boxed.Value;
            context = boxed.Context;
            diagnosticShot = boxed.DiagnosticShot;
            return true;
        }

        internal static bool TryTake(
            SuperAudioSample sample,
            out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context) =>
            TryTake(sample, out tuning, out context, out _);
    }
}
