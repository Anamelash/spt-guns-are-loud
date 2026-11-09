using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// How loud one shot is at the shooter's ear, as a level index in dB:
    /// the muzzle energy of what the round fires, relative to a reference
    /// cartridge, plus the spherical spreading from the muzzle to the ear,
    /// plus what the muzzle devices take off or add.
    /// </summary>
    internal readonly struct ShotLevel
    {
        internal readonly float EnergyJoules;
        internal readonly float PayloadMassGram;
        internal readonly float MuzzleDistance;
        /// <summary>L_E: 10·log10(E / reference energy), dB.</summary>
        internal readonly float EnergyIndex;
        /// <summary>L_d: 20·log10(reference distance / d), dB.</summary>
        internal readonly float DistanceIndex;
        /// <summary>L_m: the muzzle devices' loudness on the scale, dB (negative for a suppressor).</summary>
        internal readonly float DeviceIndex;
        /// <summary>L = L_E + L_d + L_m, dB.</summary>
        internal readonly float LevelIndex;
        /// <summary>True when the round carried no mass or no velocity and L_E is the reference.</summary>
        internal readonly bool Fallback;

        internal ShotLevel(
            float energyJoules,
            float payloadMassGram,
            float muzzleDistance,
            float energyIndex,
            float distanceIndex,
            bool fallback,
            float deviceIndex = 0f)
        {
            EnergyJoules = energyJoules;
            PayloadMassGram = payloadMassGram;
            MuzzleDistance = muzzleDistance;
            EnergyIndex = energyIndex;
            DistanceIndex = distanceIndex;
            DeviceIndex = float.IsNaN(deviceIndex) || float.IsInfinity(deviceIndex) ? 0f : deviceIndex;
            LevelIndex = energyIndex + distanceIndex + DeviceIndex;
            Fallback = fallback;
        }
    }

    internal static class ShotLevelIndex
    {
        // An intermediate rifle cartridge: 5.56x45 and 7.62x39 rounds sit at
        // 1.7-2.2 kJ, so a typical intermediate round has L_E close to 0 dB.
        internal const float ReferenceEnergyJoules = 1800f;

        // Levels are expressed at 1 m from the muzzle; nearer is louder by
        // spherical spreading (6 dB per halving of the distance).
        internal const float ReferenceDistanceMeters = 1f;

        // The muzzle of a held weapon is never closer to the ear than a
        // compact pistol held up close (0.3 m), and a distance beyond 2 m means
        // the listener is not where the shooter is (a free camera, a spectator
        // view); the spreading term is held at those bounds.
        internal const float MinimumDistanceMeters = 0.3f;
        internal const float MaximumDistanceMeters = 2f;

        // Severity is a straight line in L through the long-gun anchors:
        // 5.45x39 at 0.9 m (L = -0.2) -> 0.86, 7.62x51 at 0.9 m (L = +3.6) ->
        // 1.12 and .50 BMG at 1.1 m (L = +9.0) -> 1.55.
        internal const float SeverityAtZeroIndex = 0.866f;
        internal const float SeverityPerDb = 0.0751f;

        // The headset penalty is a line in L_E that reaches the configured
        // penalty at the median .50 BMG round (L_E = +9.83 dB) and crosses zero
        // where the same line through 7.62x51 (L_E = +2.7 dB, a third of the
        // .50 BMG penalty) meets it: L_E = -0.84 dB, intermediate rounds.
        internal const float PenaltyZeroEnergyIndex = -0.84f;
        internal const float PenaltySpanDb = 10.67f;
        internal const float PenaltyHeadroomDb = 2f;

        // The cartridge scale of the direct sound: its floor is a 140 J
        // rimfire round (L_E = -11.09 dB), its ceiling the median .50 BMG round
        // (L_E = +9.83 dB), 20.92 dB apart.
        internal const float PositionFloorEnergyIndex = -11.09f;
        internal const float PositionSpanDb = 20.92f;

        // The cartridge weight of the added low end is a line in L_E whose
        // slope is the configured contrast over the distance from the median
        // 9x19 round (L_E = -6.2 dB) to the median 5.45x39 round (-1.1 dB):
        // -1.1 - (-6.2) = 5.1 dB. It is held at -8 dB below and +3.5 dB above.
        internal const float ContrastPistolEnergyIndex = -6.2f;
        internal const float ContrastIntermediateEnergyIndex = -1.1f;
        internal const float ContrastSpanDb = ContrastIntermediateEnergyIndex - ContrastPistolEnergyIndex;
        internal const float ContrastMinimumDb = -8f;
        internal const float ContrastMaximumDb = 3.5f;

        /// <summary>
        /// The level of a shot; <paramref name="indexPerLoudness"/> is how many
        /// dB of the index one point of the game's muzzle-device loudness is
        /// worth (0 leaves the devices out).
        /// </summary>
        internal static ShotLevel Compute(ShotDescriptor shot, float indexPerLoudness = 0f)
        {
            if (shot == null)
                return Compute(0f, 0f, ReferenceDistanceMeters);
            return Compute(shot.PayloadMassGram, shot.MuzzleVelocity, shot.MuzzleDistance,
                DeviceIndex(shot.SummedModLoudness, indexPerLoudness));
        }

        /// <summary>
        /// L_m: the summed loudness of the weapon's muzzle devices, as the game
        /// rates them (suppressors −12 to −39, brakes and compensators +5 to
        /// +40), in dB of the index.
        /// </summary>
        internal static float DeviceIndex(int summedModLoudness, float indexPerLoudness)
        {
            if (float.IsNaN(indexPerLoudness) || float.IsInfinity(indexPerLoudness)) return 0f;
            return summedModLoudness * indexPerLoudness;
        }

        /// <summary>
        /// E = ½·m·v² of the payload, L_E against the reference cartridge, L_d
        /// over the clamped muzzle distance and L_m of the muzzle devices. A
        /// round without mass or velocity gets the reference cartridge
        /// (L_E = 0) and is flagged.
        /// </summary>
        internal static ShotLevel Compute(float payloadMassGram, float velocity, float muzzleDistance,
            float deviceIndex = 0f)
        {
            float distance = ClampDistance(muzzleDistance);
            float distanceIndex = DistanceIndex(distance);
            if (!(payloadMassGram > 0f) || !(velocity > 0f) ||
                float.IsInfinity(payloadMassGram) || float.IsInfinity(velocity))
            {
                return new ShotLevel(0f, Math.Max(0f, payloadMassGram), distance, 0f, distanceIndex, true, deviceIndex);
            }

            float energy = EnergyJoules(payloadMassGram, velocity);
            return new ShotLevel(energy, payloadMassGram, distance, EnergyIndex(energy), distanceIndex, false, deviceIndex);
        }

        internal static float EnergyJoules(float payloadMassGram, float velocity) =>
            (float)(0.5 * (payloadMassGram / 1000.0) * velocity * velocity);

        internal static float EnergyIndex(float energyJoules) =>
            (float)(10.0 * Math.Log10(energyJoules / ReferenceEnergyJoules));

        internal static float DistanceIndex(float muzzleDistance) =>
            (float)(20.0 * Math.Log10(ReferenceDistanceMeters / ClampDistance(muzzleDistance)));

        internal static float ClampDistance(float muzzleDistance)
        {
            if (float.IsNaN(muzzleDistance)) return ReferenceDistanceMeters;
            return Math.Min(MaximumDistanceMeters, Math.Max(MinimumDistanceMeters, muzzleDistance));
        }

        /// <summary>
        /// The mass a round puts out of the barrel. A single projectile is its
        /// own bullet. A shot shell is ranked as the single projectile of its
        /// caliber (the median slug), since the game's pellet masses and counts
        /// do not describe the charge; a caliber without single projectiles
        /// keeps pellet mass × count.
        /// </summary>
        internal static float ResolvePayloadMassGram(
            float bulletMassGram,
            int projectileCount,
            float caliberSingleProjectileMassGram)
        {
            float bullet = Math.Max(0f, bulletMassGram);
            if (projectileCount <= 1) return bullet;
            return caliberSingleProjectileMassGram > 0f
                ? caliberSingleProjectileMassGram
                : bullet * projectileCount;
        }

        /// <summary>
        /// The median of the positive masses in a sequence (the mean of the two
        /// middle values for an even count), or 0 when there are none. A median
        /// rather than a mean or maximum, so one outlying round of a caliber
        /// does not move the whole caliber.
        /// </summary>
        internal static float MedianMassGram(IEnumerable<float> masses)
        {
            if (masses == null) return 0f;
            var values = new List<float>();
            foreach (float mass in masses)
                if (mass > 0f && !float.IsInfinity(mass)) values.Add(mass);
            if (values.Count == 0) return 0f;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1
                ? values[middle]
                : (values[middle - 1] + values[middle]) * 0.5f;
        }
    }
}
