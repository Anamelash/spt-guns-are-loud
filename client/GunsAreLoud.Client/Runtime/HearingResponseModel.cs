using System;
using GunsAreLoud.Client.Configuration;

namespace GunsAreLoud.Client.Runtime
{
    internal readonly struct HearingResponse
    {
        internal readonly bool HearingLossEnabled;
        internal readonly bool TinnitusEnabled;
        internal readonly float HearingLeft;
        internal readonly float HearingRight;
        internal readonly float AttenuationLeftDb;
        internal readonly float AttenuationRightDb;
        internal readonly float CutoffLeftHz;
        internal readonly float CutoffRightHz;
        internal readonly float TinnitusLeft;
        internal readonly float TinnitusRight;

        internal HearingResponse(
            bool hearingLossEnabled,
            bool tinnitusEnabled,
            float hearingLeft,
            float hearingRight,
            float attenuationLeftDb,
            float attenuationRightDb,
            float cutoffLeftHz,
            float cutoffRightHz,
            float tinnitusLeft,
            float tinnitusRight)
        {
            HearingLossEnabled = hearingLossEnabled;
            TinnitusEnabled = tinnitusEnabled;
            HearingLeft = hearingLeft;
            HearingRight = hearingRight;
            AttenuationLeftDb = attenuationLeftDb;
            AttenuationRightDb = attenuationRightDb;
            CutoffLeftHz = cutoffLeftHz;
            CutoffRightHz = cutoffRightHz;
            TinnitusLeft = tinnitusLeft;
            TinnitusRight = tinnitusRight;
        }

        internal bool ProcessingActive =>
            (HearingLossEnabled && (HearingLeft > 0.0001f || HearingRight > 0.0001f)) ||
            TinnitusLeft > 0.000001f || TinnitusRight > 0.000001f;
    }

    internal static class HearingResponseModel
    {
        internal static HearingResponse Calculate(
            float leftDose,
            float rightDose,
            TuningSnapshot tuning,
            int sampleRate)
        {
            float maximumDose = Math.Max(0.01f, tuning.MaximumDose);
            float normalizedLeft = Clamp01(leftDose / maximumDose);
            float normalizedRight = Clamp01(rightDose / maximumDose);
            bool hearingLoss = tuning.ExposureEnabled && tuning.HearingLossEnabled;
            bool tinnitus = tuning.ExposureEnabled && tuning.TinnitusEnabled;

            float hearingLeft = hearingLoss ? MapHearingLoss(normalizedLeft, tuning) : 0f;
            float hearingRight = hearingLoss ? MapHearingLoss(normalizedRight, tuning) : 0f;
            float neutralCutoff = Math.Max(8000, sampleRate) * 0.49f;

            return new HearingResponse(
                hearingLoss,
                tinnitus,
                hearingLeft,
                hearingRight,
                hearingLoss ? tuning.MaximumAttenuationDb * hearingLeft : 0f,
                hearingLoss ? tuning.MaximumAttenuationDb * hearingRight : 0f,
                hearingLoss ? Lerp(neutralCutoff, tuning.MinimumLowpassHz, hearingLeft) : neutralCutoff,
                hearingLoss ? Lerp(neutralCutoff, tuning.MinimumLowpassHz, hearingRight) : neutralCutoff,
                tinnitus ? MapTinnitus(normalizedLeft, tuning) : 0f,
                tinnitus ? MapTinnitus(normalizedRight, tuning) : 0f);
        }

        // The loudest ringing gunfire can reach with these settings: the level at
        // maximum dose, or zero when gunshot ringing is disabled.
        internal static float TinnitusCeiling(TuningSnapshot tuning) =>
            tuning != null && tuning.ExposureEnabled && tuning.TinnitusEnabled
                ? Math.Max(0f, tuning.TinnitusMaximumLevel)
                : 0f;

        private static float MapHearingLoss(float normalizedDose, TuningSnapshot tuning)
        {
            float duration = Math.Max(0.05f, tuning.HearingLossDurationScale);
            return (float)Math.Pow(normalizedDose, Math.Max(0.01f, tuning.HearingResponseCurve) / duration);
        }

        private static float MapTinnitus(float normalizedDose, TuningSnapshot tuning)
        {
            float threshold = Clamp01(tuning.TinnitusThreshold);
            if (normalizedDose <= threshold) return 0f;

            float amount = Clamp01((normalizedDose - threshold) / Math.Max(0.0001f, 1f - threshold));
            float duration = Math.Max(0.05f, tuning.TinnitusDurationScale);
            return tuning.TinnitusMaximumLevel *
                (float)Math.Pow(amount, 0.7f / duration);
        }

        private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));
        private static float Lerp(float from, float to, float amount) => from + (to - from) * Clamp01(amount);
    }

    internal sealed class HearingExposureState
    {
        internal float LeftDose { get; private set; }
        internal float RightDose { get; private set; }

        internal void Add(float leftDose, float rightDose, TuningSnapshot tuning)
        {
            if (!tuning.ExposureEnabled) return;
            float maximum = Math.Max(0.01f, tuning.MaximumDose);
            LeftDose = Math.Min(maximum, LeftDose + Math.Max(0f, leftDose));
            RightDose = Math.Min(maximum, RightDose + Math.Max(0f, rightDose));
        }

        internal void Advance(float deltaTime, TuningSnapshot tuning)
        {
            if (!tuning.ExposureEnabled)
            {
                Reset();
                return;
            }

            float maximum = Math.Max(0.01f, tuning.MaximumDose);
            LeftDose = Decay(LeftDose, maximum, deltaTime, tuning);
            RightDose = Decay(RightDose, maximum, deltaTime, tuning);
        }

        internal void Reset()
        {
            LeftDose = 0f;
            RightDose = 0f;
        }

        private static float Decay(float dose, float maximum, float deltaTime, TuningSnapshot tuning)
        {
            if (dose <= 0.0001f) return 0f;
            float normalized = Math.Max(0f, Math.Min(1f, dose / maximum));
            float timeConstant = tuning.FastRecoverySeconds +
                (tuning.SlowRecoverySeconds - tuning.FastRecoverySeconds) * normalized;
            return dose * (float)Math.Exp(-Math.Max(0f, deltaTime) / Math.Max(0.05f, timeConstant));
        }
    }
}
