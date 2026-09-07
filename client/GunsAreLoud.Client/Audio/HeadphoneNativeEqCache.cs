using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    internal interface IHeadphoneNativeEqProvider
    {
        bool TryGet(HeadsetProfile profile, int sampleRate, out HeadphoneNativeEqFit fit);
    }

    internal sealed class HeadphoneNativeEqCacheProvider : IHeadphoneNativeEqProvider
    {
        internal static readonly HeadphoneNativeEqCacheProvider Instance = new HeadphoneNativeEqCacheProvider();
        private HeadphoneNativeEqCacheProvider() { }
        public bool TryGet(HeadsetProfile profile, int sampleRate, out HeadphoneNativeEqFit fit) =>
            HeadphoneNativeEqCache.TryGet(profile, sampleRate, out fit);
    }

    // Native EQ fitting is optimization work measured in tens to hundreds of
    // milliseconds. It is precomputed off the main/audio threads; activation
    // only performs a bounded dictionary lookup and falls back while pending.
    internal static class HeadphoneNativeEqCache
    {
        private struct Key : IEquatable<Key>
        {
            internal readonly string ProfileId;
            internal readonly int SampleRate;
            internal Key(string profileId, int sampleRate)
            { ProfileId = profileId ?? ""; SampleRate = sampleRate; }
            public bool Equals(Key other) => SampleRate == other.SampleRate &&
                string.Equals(ProfileId, other.ProfileId, StringComparison.Ordinal);
            public override bool Equals(object value) => value is Key other && Equals(other);
            public override int GetHashCode() => unchecked((StringComparer.Ordinal.GetHashCode(ProfileId) * 397) ^ SampleRate);
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<Key, HeadphoneNativeEqFit> Fits =
            new Dictionary<Key, HeadphoneNativeEqFit>();
        private static readonly HashSet<int> PendingRates = new HashSet<int>();

        internal static void PreloadAll(int sampleRate)
        {
            int rate = Math.Max(8000, sampleRate);
            lock (Gate)
            {
                if (PendingRates.Contains(rate) || AllReady(rate)) return;
                PendingRates.Add(rate);
            }
            Task.Run(() => CalculateAll(rate));
        }

        internal static bool TryGet(HeadsetProfile profile, int sampleRate, out HeadphoneNativeEqFit fit)
        {
            fit = null;
            if (profile == null) return false;
            lock (Gate) return Fits.TryGetValue(new Key(profile.ProfileId, Math.Max(8000, sampleRate)), out fit);
        }

        private static void CalculateAll(int sampleRate)
        {
            try
            {
                for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
                {
                    HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(i);
                    var key = new Key(profile.ProfileId, sampleRate);
                    lock (Gate) if (Fits.ContainsKey(key)) continue;
                    HeadphoneNativeEqFit fit;
                    try { fit = HeadphoneNativeEqFit.Calculate(profile.Passive, sampleRate); }
                    catch { continue; }
                    if (!IsPublishable(profile, sampleRate, fit)) continue;
                    lock (Gate) Fits[key] = fit;
                }
            }
            finally
            {
                lock (Gate) PendingRates.Remove(sampleRate);
            }
        }

        private static bool AllReady(int sampleRate)
        {
            for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
                if (!Fits.ContainsKey(new Key(HeadsetProfileRegistry.ProfileAt(i).ProfileId, sampleRate))) return false;
            return true;
        }

        internal static bool IsPublishable(HeadsetProfile profile, int sampleRate, HeadphoneNativeEqFit fit)
        {
            if (profile?.Passive == null || fit == null || fit.BandCount != profile.Passive.BandCount ||
                !Finite(fit.BaseVolumeDb) || fit.BaseVolumeDb < -80f || fit.BaseVolumeDb > 0f ||
                !Finite(fit.MaximumAnchorErrorDb) || fit.MaximumAnchorErrorDb < 0f ||
                fit.MaximumAnchorErrorDb > 1.5f) return false;
            float maximumFrequency = Math.Min(22000f, Math.Max(8000, sampleRate) * 0.49f);
            for (int i = 0; i < fit.BandCount; i++)
                if (!Finite(fit.FrequencyAt(i)) || fit.FrequencyAt(i) < 10f || fit.FrequencyAt(i) > maximumFrequency ||
                    !Finite(fit.LinearGainAt(i)) || fit.LinearGainAt(i) < 0.05f || fit.LinearGainAt(i) > 3f ||
                    !Finite(fit.OctaveRangeAt(i)) || fit.OctaveRangeAt(i) < 0.2f || fit.OctaveRangeAt(i) > 5f ||
                    !Finite(fit.AnchorErrorDbAt(i))) return false;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
