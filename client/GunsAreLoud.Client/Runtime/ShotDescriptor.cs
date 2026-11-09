using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal enum WeaponCategory
    {
        Pistol,
        Compact,
        LongGun,
        Heavy,
        Unknown
    }

    internal sealed class ShotDescriptor
    {
        internal double DspTime;
        internal string AmmoCaliber;
        internal float BulletMassGram;
        internal float MuzzleVelocity;
        internal int ProjectileCount;
        /// <summary>
        /// Grams the round puts out of the barrel: the bullet of a single
        /// projectile, or the median single projectile of the caliber for a
        /// shot shell (pellet mass × count when the caliber has none).
        /// </summary>
        internal float PayloadMassGram;
        /// <summary>Metres from the muzzle to the listener when the shot was fired.</summary>
        internal float MuzzleDistance;
        internal string WeaponClass;
        internal WeaponCategory WeaponCategory;
        internal string WeaponTemplateId;
        internal bool IsSuppressed;
        internal int SummedModLoudness;
        internal bool IsIndoor;
        internal bool IsLeftStance;
        internal Vector3 MuzzlePosition;
        internal Vector3 MuzzleForward;
        internal bool HasActiveHeadphones;
        internal string HeadphonesName;
        internal string MixerHeadphonesName;
        internal string HeadphonesSource;
        internal float HeadphonesCompressorThresholdDb;
        /// <summary>
        /// Seconds between rounds while the weapon fires continuously (full
        /// auto or burst), or 0 for a single-shot mode or weapon.
        /// </summary>
        internal float ContinuousFireIntervalSeconds;
    }
}
