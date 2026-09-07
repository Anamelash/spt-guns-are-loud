using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal readonly struct IndoorHeadphonesDamping
    {
        internal readonly float BodyAttenuationDb, TailDbPerSecond, EarlyAttenuationDb, ReverbAttenuationDb;
        internal float BodyGain => ExposureModel.DbToPressureMultiplier(BodyAttenuationDb);
        internal float ReachMultiplier => ExposureModel.DbToPressureMultiplier(ReverbAttenuationDb * 0.5f);

        internal IndoorHeadphonesDamping(float strength)
        {
            // Digital effect calibration, NOT an extra NRR/SPL attenuation claim.
            BodyAttenuationDb = 4f * strength;
            TailDbPerSecond = 48f * strength;
            EarlyAttenuationDb = 4f * strength;
            ReverbAttenuationDb = 8f * strength;
        }
    }

    internal static class IndoorHeadphonesModel
    {
        internal static IndoorHeadphonesDamping Calculate(ShotDescriptor shot, TuningSnapshot tuning)
            => default;

        internal static float RoomSend(float baseline, float target, float mix, float attenuationDb) =>
            Mathf.Clamp(Mathf.Lerp(baseline, Mathf.Clamp(target, -60f, 20f), Mathf.Clamp01(mix)), -80f, 20f);
    }
}
