using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal sealed class ShotProcessingState
    {
        internal AutomaticShotContext AudioContext;
        internal ShotDescriptor Shot;
        internal ExposureResult Exposure;
        internal TuningSnapshot Tuning;
        internal float DirectBoostDb;
        internal float DirectBodyGain;
        internal LocalGunshotAudioTuning AudioTuning;
        internal int TunedAudioSamples;
        internal DiagnosticShotToken DiagnosticShot;
    }

    internal static class DirectLoudnessModel
    {
        internal static float CalculatePitchedBodyGain(ShotDescriptor shot, TuningSnapshot tuning)
        {
            // Caliber is applied once, later, by CalculateLayerGain. It must not
            // remain hidden in normalizedImpact when Contrast is set to zero.
            float boost = tuning.BaseDirectBoostDb + (shot.IsIndoor ? tuning.IndoorDirectBoostDb : 0f);
            if (shot.IsSuppressed) boost *= 0.38f;
            float impact = Mathf.Clamp01(boost / Mathf.Max(0.01f, tuning.MaximumDirectBoostDb));
            return Mathf.Clamp(tuning.DirectBodyGain * (0.5f + impact), 0f, 0.5f);
        }

        internal static float CalculateBoostDb(
            ShotDescriptor shot,
            ExposureResult exposure,
            TuningSnapshot tuning)
        {
            float caliberPosition = Mathf.InverseLerp(
                tuning.RimfireSeverity,
                tuning.HeavySeverity,
                exposure.CaliberBaseline);

            float boostDb = tuning.BaseDirectBoostDb + tuning.CaliberBoostSpreadDb * caliberPosition;
            if (shot.IsIndoor)
            {
                boostDb += tuning.IndoorDirectBoostDb;
            }

            if (shot.IsSuppressed)
            {
                boostDb *= 0.38f;
            }

            return Mathf.Clamp(boostDb, 0f, tuning.MaximumDirectBoostDb);
        }

        internal static float CalculatePressureFrequencyHz(
            ExposureResult exposure,
            TuningSnapshot tuning)
        {
            float caliberPosition = Mathf.InverseLerp(
                tuning.RimfireSeverity,
                tuning.HeavySeverity,
                exposure.CaliberBaseline);
            return Mathf.Lerp(122f, 68f, caliberPosition);
        }
    }
}
