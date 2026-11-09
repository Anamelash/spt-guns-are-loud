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
        /// <summary>The direct level correction of the sample this shot played (or of the loop it continued).</summary>
        internal GunsAreLoud.Client.Audio.DirectLevelDecision Direct;
        /// <summary>The cartridge class the baseline target uses, and whether it came from muzzle energy.</summary>
        internal CaliberClass DirectClass;
        internal bool DirectClassFromEnergy;
    }

    internal static class DirectLoudnessModel
    {
        internal static float CalculatePitchedBodyGain(ShotDescriptor shot, TuningSnapshot tuning)
        {
            // Caliber is applied once, later, by CalculateLayerGain. It must not
            // remain hidden in normalizedImpact when Contrast is set to zero.
            float boost = tuning.BaseDirectBoostDb + (shot.IsIndoor ? tuning.IndoorDirectBoostDb : 0f);
            float impact = Mathf.Clamp01(boost / Mathf.Max(0.01f, tuning.MaximumDirectBoostDb));
            return Mathf.Clamp(tuning.DirectBodyGain * (0.5f + impact), 0f, 0.5f);
        }

        internal static float CalculateBoostDb(
            ShotDescriptor shot,
            ExposureResult exposure,
            TuningSnapshot tuning)
        {
            float caliberPosition = CartridgePosition(exposure.EnergyIndex);

            float boostDb = tuning.BaseDirectBoostDb + tuning.CaliberBoostSpreadDb * caliberPosition;
            if (shot.IsIndoor)
            {
                boostDb += tuning.IndoorDirectBoostDb;
            }

            // A suppressor is in the shot's level and moves the whole event
            // through the direct correction, the copy with it.
            return Mathf.Clamp(boostDb, 0f, tuning.MaximumDirectBoostDb);
        }

        internal static float CalculatePressureFrequencyHz(
            ExposureResult exposure,
            TuningSnapshot tuning)
        {
            float caliberPosition = CartridgePosition(exposure.EnergyIndex);
            return Mathf.Lerp(122f, 68f, caliberPosition);
        }

        /// <summary>
        /// Where a cartridge sits on the scale of the direct sound, 0 to 1, from
        /// its energy term: 0 at a 140 J rimfire round, 1 at the median .50 BMG
        /// round.
        /// </summary>
        internal static float CartridgePosition(float energyIndex) =>
            Mathf.Clamp01((energyIndex - ShotLevelIndex.PositionFloorEnergyIndex) / ShotLevelIndex.PositionSpanDb);
    }
}
