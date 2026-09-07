using System;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal sealed class ExposureResult
    {
        internal float CaliberBaseline;
        internal float EnergyCorrection;
        internal float ModMultiplier;
        internal float SuppressorMultiplier;
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
        private const float ReferenceEnergyJoules = 1800f;

        internal static ExposureResult Calculate(ShotDescriptor shot, TuningSnapshot tuning)
        {
            float caliberBaseline = GetCaliberBaseline(shot.AmmoCaliber, tuning);
            float energyCorrection = GetEnergyCorrection(shot, tuning);

            float modCorrection = Mathf.Clamp(
                shot.SummedModLoudness / 100f * tuning.ModLoudnessInfluence,
                -0.6f,
                0.6f);
            float modMultiplier = Mathf.Max(0.1f, 1f + modCorrection);
            float suppressorMultiplier = shot.IsSuppressed ? tuning.SuppressedMultiplier : 1f;
            float roomMultiplier = shot.IsIndoor ? tuning.IndoorDoseMultiplier : 1f;

            float headphonesProtectionDb = 0f;
            float directHeadphonesMultiplier = 1f;
            float roomHeadphonesMultiplier = 1f;
            if (shot.HasActiveHeadphones)
            {
                headphonesProtectionDb = GetHeadphonesProtectionDb(shot, tuning);
                directHeadphonesMultiplier = DbToPressureMultiplier(headphonesProtectionDb);
                roomHeadphonesMultiplier = DbToPressureMultiplier(Mathf.Max(
                    tuning.MinimumHeadphonesProtectionDb,
                    headphonesProtectionDb - tuning.IndoorHeadphonesPenaltyDb));
            }

            float severity = Mathf.Max(0f, caliberBaseline + energyCorrection);
            severity *= modMultiplier;
            severity *= suppressorMultiplier;
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
                CaliberBaseline = caliberBaseline,
                EnergyCorrection = energyCorrection,
                ModMultiplier = modMultiplier,
                SuppressorMultiplier = suppressorMultiplier,
                RoomMultiplier = roomMultiplier,
                HeadphonesMultiplier = headphonesMultiplier,
                HeadphonesProtectionDb = headphonesProtectionDb,
                FinalSeverity = severity,
                LeftDose = severity * (leftEarExposed ? exposedWeight : shieldedWeight),
                RightDose = severity * (leftEarExposed ? shieldedWeight : exposedWeight),
                EarAsymmetry = asymmetry
            };
        }

        internal static float GetHeadphonesProtectionDb(ShotDescriptor shot, TuningSnapshot tuning)
        {
            if (shot == null || !shot.HasActiveHeadphones)
            {
                return 0f;
            }

            float templateProtectionDb = shot.HeadphonesCompressorThresholdDb < -0.01f
                ? -shot.HeadphonesCompressorThresholdDb
                : tuning.DefaultHeadphonesProtectionDb;

            float caliberPenaltyDb = GetHeadphonesCaliberPenaltyDb(shot.AmmoCaliber, tuning);
            return Mathf.Clamp(
                templateProtectionDb + tuning.HeadphonesFitOffsetDb - caliberPenaltyDb,
                tuning.MinimumHeadphonesProtectionDb,
                tuning.MaximumHeadphonesProtectionDb);
        }

        internal static float DbToPressureMultiplier(float attenuationDb)
        {
            return (float)Math.Pow(10.0, -Mathf.Max(0f, attenuationDb) / 20.0);
        }

        internal static float GetCaliberBaseline(string caliber, TuningSnapshot tuning)
        {
            switch ((caliber ?? string.Empty).ToLowerInvariant())
            {
                case "20x1mm":
                    return tuning.RimfireSeverity;

                case "9x18pm":
                case "9x19para":
                case "9x21":
                case "9x33r":
                case "762x25tt":
                case "1143x23acp":
                case "46x30":
                case "57x28":
                case "127x33":
                    return tuning.PistolSeverity;

                case "545x39":
                case "556x45nato":
                case "762x35":
                case "762x39":
                case "9x39":
                case "366tkm":
                case "127x55":
                    return tuning.IntermediateSeverity;

                case "762x51":
                case "762x54r":
                case "68x51":
                case "86x70":
                    return tuning.FullPowerSeverity;

                case "12g":
                case "20g":
                case "23x75":
                    return tuning.ShotgunSeverity;

                case "127x99":
                case "127x108":
                case "26x75":
                case "30x29":
                case "40mmru":
                case "40x46":
                case "725":
                    return tuning.HeavySeverity;

                default:
                    return tuning.UnknownCaliberSeverity;
            }
        }

        private static float GetEnergyCorrection(ShotDescriptor shot, TuningSnapshot tuning)
        {
            if (shot.BulletMassGram <= 0f || shot.MuzzleVelocity <= 0f || tuning.EnergyInfluence <= 0f)
            {
                return 0f;
            }

            double massKg = shot.BulletMassGram / 1000.0;
            double energyJoules = 0.5 * massKg * shot.MuzzleVelocity * shot.MuzzleVelocity;
            double logarithmicRatio = Math.Log10(Math.Max(1.0, energyJoules) / ReferenceEnergyJoules);
            float correction = (float)logarithmicRatio * tuning.EnergyInfluence;
            return Mathf.Clamp(
                correction,
                -tuning.MaximumEnergyCorrection,
                tuning.MaximumEnergyCorrection);
        }

        private static float GetHeadphonesCaliberPenaltyDb(string caliber, TuningSnapshot tuning)
        {
            switch ((caliber ?? string.Empty).ToLowerInvariant())
            {
                case "762x51":
                case "762x54r":
                case "68x51":
                case "86x70":
                    return tuning.FullPowerHeadphonesPenaltyDb;

                case "12g":
                case "20g":
                case "23x75":
                    return tuning.ShotgunHeadphonesPenaltyDb;

                case "127x99":
                case "127x108":
                case "26x75":
                case "30x29":
                case "40mmru":
                case "40x46":
                case "725":
                    return tuning.HeavyHeadphonesPenaltyDb;

                default:
                    return 0f;
            }
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
