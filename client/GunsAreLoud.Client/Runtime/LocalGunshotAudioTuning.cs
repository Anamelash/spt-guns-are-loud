using System.Runtime.CompilerServices;
using GunsAreLoud.Client.Audio;
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
        /// <summary>Level the direct sound of this shot should play at, in dB on the measure's scale.</summary>
        internal readonly float DirectTargetDb;
        /// <summary>q: how much of the way to the target the direct sound is moved (0-1).</summary>
        internal readonly float DirectStrength;
        /// <summary>How much of the shot's peak budget the recording leaves to the copy, dB.</summary>
        internal readonly float DirectCopyHeadroomDb;
        /// <summary>Shot density: how far over the peak budget the event may go into the program limiter, dB; 0 keeps the limiter a safety.</summary>
        internal readonly float DirectDensityDb;
        /// <summary>L_E of the round, dB against the reference cartridge: the copy's cartridge weight.</summary>
        internal readonly float EnergyIndex;

        /// <summary>The configured level of an intermediate rifle cartridge above 9x19 in the copy, dB.</summary>
        internal float CartridgeContrastDb => CaliberContrastPercent * ModConfig.CartridgeContrastSpanDb / 100f;

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
            float automaticLateToleranceScale = 1f,
            float directTargetDb = 0f,
            float directStrength = 0f,
            float directCopyHeadroomDb = DirectLevelModel.DefaultCopyHeadroomDb,
            float energyIndex = 0f,
            float directDensityDb = 0f)
        {
            EnergyIndex = energyIndex;
            DirectDensityDb = directDensityDb;
            DirectTargetDb = directTargetDb;
            DirectStrength = directStrength;
            DirectCopyHeadroomDb = directCopyHeadroomDb;
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

        /// <summary>The same tuning with another loop beat (0 = a one-shot).</summary>
        internal LocalGunshotAudioTuning WithLoopBeat(float loopBeatSeconds) =>
            new LocalGunshotAudioTuning(
                DirectBoostDb,
                DirectBodyGain,
                PressureFrequencyHz,
                AutomaticPitchedRoute,
                PitchedLayerSemitones,
                PitchedLayerHighpassHz,
                PitchedLayerLowpassHz,
                PitchedLayerFadePercent,
                AutomaticPitchedTailSeconds,
                PitchedLayerGainDb,
                PitchedLayerOcclusion,
                PitchedLayerOccludedLowpassHz,
                loopBeatSeconds,
                ApplyIndoorRoom,
                IndoorRoomMix,
                EarlyReflectionsSendDb,
                ReverbSendDb,
                ReverbReach,
                AutomaticTailMode,
                LowEndNormalizationPercent,
                CaliberContrastPercent,
                HeadphonesDamping,
                NormalizeBass,
                AutomaticReportOverlapShots,
                AutomaticLateToleranceScale,
                DirectTargetDb,
                DirectStrength,
                DirectCopyHeadroomDb,
                EnergyIndex,
                DirectDensityDb);
    }

    internal sealed class LocalGunshotAudioTuningBox
    {
        internal readonly LocalGunshotAudioTuning Value;
        internal readonly AutomaticShotContext Context;
        internal readonly DiagnosticShotToken DiagnosticShot;
        internal readonly float BurstIntervalSeconds;
        internal readonly DirectLevelDecision Direct;

        internal LocalGunshotAudioTuningBox(
            LocalGunshotAudioTuning value,
            AutomaticShotContext context = null,
            DiagnosticShotToken diagnosticShot = default,
            float burstIntervalSeconds = 0f,
            DirectLevelDecision direct = default)
        {
            Direct = direct;
            Value = value;
            Context = context;
            DiagnosticShot = diagnosticShot;
            BurstIntervalSeconds = burstIntervalSeconds;
        }

        /// <summary>
        /// The fire interval a queued sample's copy is released by: a whole-shot
        /// sample of continuous fire (an engine that queues every round as its
        /// own shot) has later rounds that shorten it. A looped body, or a shot
        /// that is not continuous fire, keeps its copy whole.
        /// </summary>
        internal static float BurstIntervalForSample(bool wholeShotSample, float continuousFireIntervalSeconds) =>
            wholeShotSample && continuousFireIntervalSeconds > 0.001f ? continuousFireIntervalSeconds : 0f;
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
        // The direct level correction of this burst's body sample: its release
        // tail plays with exactly this correction.
        internal DirectLevelDecision BodyDirect;
    }

    internal static class LocalGunshotSampleRegistry
    {
        private static readonly ConditionalWeakTable<SuperAudioSample, LocalGunshotAudioTuningBox> Samples =
            new ConditionalWeakTable<SuperAudioSample, LocalGunshotAudioTuningBox>();

        internal static void Register(SuperAudioSample sample, LocalGunshotAudioTuning tuning,
            AutomaticShotContext context = null,
            DiagnosticShotToken diagnosticShot = default,
            float continuousFireIntervalSeconds = 0f,
            DirectLevelDecision direct = default)
        {
            if (sample == null)
            {
                return;
            }

            Samples.Remove(sample);
            Samples.Add(sample, new LocalGunshotAudioTuningBox(tuning, context, diagnosticShot,
                continuousFireIntervalSeconds, direct));
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
            out DiagnosticShotToken diagnosticShot) =>
            TryTake(sample, out tuning, out context, out diagnosticShot, out _);

        internal static bool TryTake(SuperAudioSample sample, out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context,
            out DiagnosticShotToken diagnosticShot,
            out float continuousFireIntervalSeconds) =>
            TryTake(sample, out tuning, out context, out diagnosticShot, out continuousFireIntervalSeconds, out _);

        internal static bool TryTake(SuperAudioSample sample, out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context,
            out DiagnosticShotToken diagnosticShot,
            out float continuousFireIntervalSeconds,
            out DirectLevelDecision direct)
        {
            direct = default;
            tuning = default;
            context = null;
            diagnosticShot = default;
            continuousFireIntervalSeconds = 0f;
            if (sample == null || !Samples.TryGetValue(sample, out LocalGunshotAudioTuningBox boxed))
            {
                return false;
            }

            Samples.Remove(sample);
            tuning = boxed.Value;
            context = boxed.Context;
            diagnosticShot = boxed.DiagnosticShot;
            continuousFireIntervalSeconds = boxed.BurstIntervalSeconds;
            direct = boxed.Direct;
            return true;
        }

        internal static bool TryTake(
            SuperAudioSample sample,
            out LocalGunshotAudioTuning tuning,
            out AutomaticShotContext context) =>
            TryTake(sample, out tuning, out context, out _);
    }
}
