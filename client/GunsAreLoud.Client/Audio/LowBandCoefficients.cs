using System;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// The one-pole coefficient every low-band filter in this mod is built from.
    /// It used to live on the in-source filter of the original-band method; that
    /// method is gone, and the pitched copy's band-pass and tail still need it.
    /// </summary>
    internal static class LowBandCoefficients
    {
        internal static float Lowpass(int sampleRate, float cutoffHz)
        {
            float rate = Mathf.Max(8000, sampleRate);
            float cutoff = Mathf.Clamp(cutoffHz, 20f, rate * 0.45f);
            return 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / rate);
        }
    }
}
