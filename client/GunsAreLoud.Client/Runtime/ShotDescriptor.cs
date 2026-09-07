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
    }
}
