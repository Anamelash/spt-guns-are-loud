using System;
namespace GunsAreLoud.Client.Audio
{
    internal static class HeadphoneMixerLiveState
    {
        internal static bool Equivalent(float expected, float actual) =>
            !float.IsNaN(expected) && !float.IsInfinity(expected) &&
            !float.IsNaN(actual) && !float.IsInfinity(actual) &&
            Math.Abs(expected - actual) <= 0.0001f + Math.Abs(expected) * 0.00001f;
    }
}
