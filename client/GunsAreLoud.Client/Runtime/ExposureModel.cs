using System;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal sealed class ExposureResult
    {
        /// <summary>L = L_E + L_d + L_m, dB.</summary>
        internal float LevelIndex;
        /// <summary>L_E, dB against the reference cartridge.</summary>
        internal float EnergyIndex;
        /// <summary>L_d, dB against the reference distance.</summary>
        internal float DistanceIndex;
        /// <summary>L_m, dB: the muzzle devices on the scale.</summary>
        internal float DeviceIndex;
        internal float EnergyJoules;
        internal float PayloadMassGram;
        internal float MuzzleDistance;
        /// <summary>True when the round had no mass or velocity and L_E is the reference.</summary>
        internal bool IndexFallback;
        /// <summary>Severity from the level index alone, before any multiplier.</summary>
        internal float BaseSeverity;
        internal float HeadphonesCaliberPenaltyDb;
        internal float RoomMultiplier;
        internal float HeadphonesMultiplier;
        internal float HeadphonesProtectionDb;
        internal float FinalSeverity;
        internal float LeftDose;
        internal float RightDose;
        internal float EarAsymmetry;
    }

    internal static class ExposureModel
    {
        internal static ExposureResult Calculate(ShotDescriptor shot, TuningSnapshot tuning)
        {
            // The muzzle devices are in the level, so a suppressor takes the
            // severity down the same line as a lighter round or a longer barrel.
            ShotLevel level = ShotLevelIndex.Compute(shot, tuning.MuzzleDeviceIndexPerLoudness);
            float baseSeverity = BaseSeverity(level.LevelIndex);
            float roomMultiplier = shot.IsIndoor ? tuning.IndoorDoseMultiplier : 1f;

            float headphonesProtectionDb = 0f;
            float caliberPenaltyDb = 0f;
            float directHeadphonesMultiplier = 1f;
            float roomHeadphonesMultiplier = 1f;
            if (shot.HasActiveHeadphones)
            {
                caliberPenaltyDb = HeadsetCaliberPenaltyDb(level.EnergyIndex, tuning.HeadsetCaliberPenaltyDb);
                headphonesProtectionDb = GetHeadphonesProtectionDb(shot, caliberPenaltyDb, tuning);
                directHeadphonesMultiplier = DbToPressureMultiplier(headphonesProtectionDb);
                roomHeadphonesMultiplier = DbToPressureMultiplier(Mathf.Max(
                    tuning.MinimumHeadphonesProtectionDb,
                    headphonesProtectionDb - tuning.IndoorHeadphonesPenaltyDb));
            }

            float severity = baseSeverity;
            float roomContribution = Mathf.Max(0f, roomMultiplier - 1f);
            float protectedRoomMultiplier =
                directHeadphonesMultiplier + roomContribution * roomHeadphonesMultiplier;
            float headphonesMultiplier = protectedRoomMultiplier / Mathf.Max(1f, roomMultiplier);
            severity *= protectedRoomMultiplier;
            severity *= tuning.MasterSeverityScale;

            float asymmetry = tuning.EarModelEnabled
                ? GetAsymmetry(shot.WeaponCategory, tuning)
                : 0f;

            float exposedWeight = 1f + asymmetry;
            float shieldedWeight = 1f - asymmetry;
            bool leftEarExposed = !shot.IsLeftStance;

            return new ExposureResult
            {
                LevelIndex = level.LevelIndex,
                EnergyIndex = level.EnergyIndex,
                DistanceIndex = level.DistanceIndex,
                DeviceIndex = level.DeviceIndex,
                EnergyJoules = level.EnergyJoules,
                PayloadMassGram = level.PayloadMassGram,
                MuzzleDistance = level.MuzzleDistance,
                IndexFallback = level.Fallback,
                BaseSeverity = baseSeverity,
                HeadphonesCaliberPenaltyDb = caliberPenaltyDb,
                RoomMultiplier = roomMultiplier,
                HeadphonesMultiplier = headphonesMultiplier,
                HeadphonesProtectionDb = headphonesProtectionDb,
                FinalSeverity = severity,
                LeftDose = severity * (leftEarExposed ? exposedWeight : shieldedWeight),
                RightDose = severity * (leftEarExposed ? shieldedWeight : exposedWeight),
                EarAsymmetry = asymmetry
            };
        }

        /// <summary>
        /// Severity of a shot from its level index, before mods, suppressor,
        /// room, headset and preset: a line through the long-gun anchors, never
        /// below zero.
        /// </summary>
        internal static float BaseSeverity(float levelIndex) =>
            Mathf.Max(0f, ShotLevelIndex.SeverityAtZeroIndex + ShotLevelIndex.SeverityPerDb * levelIndex);

        /// <summary>
        /// How many dB of headset protection a cartridge takes away: the more
        /// energy, the more low end gets through the ear cups. It depends on
        /// the energy term only (the size of the charge sets the spectrum, the
        /// distance only the level), reaches <paramref name="penaltyAtHeavyDb"/>
        /// at the median .50 BMG round and is zero for intermediate rounds and
        /// below.
        /// </summary>
        internal static float HeadsetCaliberPenaltyDb(float energyIndex, float penaltyAtHeavyDb)
        {
            float penalty = Mathf.Max(0f, penaltyAtHeavyDb);
            return Mathf.Clamp(
                penalty * (energyIndex - ShotLevelIndex.PenaltyZeroEnergyIndex) / ShotLevelIndex.PenaltySpanDb,
                0f,
                penalty + ShotLevelIndex.PenaltyHeadroomDb);
        }

        internal static float GetHeadphonesProtectionDb(ShotDescriptor shot, TuningSnapshot tuning)
        {
            if (shot == null || !shot.HasActiveHeadphones)
            {
                return 0f;
            }

            return GetHeadphonesProtectionDb(
                shot,
                HeadsetCaliberPenaltyDb(ShotLevelIndex.Compute(shot).EnergyIndex, tuning.HeadsetCaliberPenaltyDb),
                tuning);
        }

        private static float GetHeadphonesProtectionDb(
            ShotDescriptor shot,
            float caliberPenaltyDb,
            TuningSnapshot tuning)
        {
            float templateProtectionDb = shot.HeadphonesCompressorThresholdDb < -0.01f
                ? -shot.HeadphonesCompressorThresholdDb
                : tuning.DefaultHeadphonesProtectionDb;

            return Mathf.Clamp(
                templateProtectionDb + tuning.HeadphonesFitOffsetDb - caliberPenaltyDb,
                tuning.MinimumHeadphonesProtectionDb,
                tuning.MaximumHeadphonesProtectionDb);
        }

        internal static float DbToPressureMultiplier(float attenuationDb)
        {
            return (float)Math.Pow(10.0, -Mathf.Max(0f, attenuationDb) / 20.0);
        }

        private static float GetAsymmetry(WeaponCategory category, TuningSnapshot tuning)
        {
            float value;
            switch (category)
            {
                case WeaponCategory.Pistol:
                    value = tuning.PistolAsymmetry;
                    break;
                case WeaponCategory.Compact:
                    value = tuning.CompactGunAsymmetry;
                    break;
                case WeaponCategory.LongGun:
                case WeaponCategory.Heavy:
                    value = tuning.LongGunAsymmetry;
                    break;
                default:
                    value = tuning.CompactGunAsymmetry;
                    break;
            }

            return Mathf.Clamp(value, 0f, tuning.MaximumAsymmetry);
        }
    }
}
