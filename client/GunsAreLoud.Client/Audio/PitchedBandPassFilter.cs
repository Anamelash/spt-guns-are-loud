using System;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    // Shared by offline level measurement and live playback: identical coefficients,
    // Q and stereo state. No Unity API, allocation or locks in Process.
    internal sealed class PitchedBandPassState
    {
        private Biquad _high;
        private Biquad _low;
        private readonly double[] _h1 = new double[2], _h2 = new double[2];
        private readonly double[] _l1 = new double[2], _l2 = new double[2];

        internal PitchedBandPassState(int rate, float highpass, float lowpass)
        {
            Reconfigure(rate, highpass, lowpass);
        }

        /// <summary>
        /// Re-arms this state for another copy: the same coefficients for the same
        /// band, computed once, with the delay elements cleared.
        /// </summary>
        internal void Reconfigure(int rate, float highpass, float lowpass)
        {
            rate = Math.Max(8000, rate);
            lowpass = Math.Max(80f, Math.Min(3000f, lowpass));
            highpass = Math.Max(10f, Math.Min(lowpass - 10f, highpass));
            BandCoefficients.Resolve(rate, highpass, lowpass, out _high, out _low);
            Array.Clear(_h1, 0, _h1.Length);
            Array.Clear(_h2, 0, _h2.Length);
            Array.Clear(_l1, 0, _l1.Length);
            Array.Clear(_l2, 0, _l2.Length);
        }

        internal float Process(float value, int channel)
        {
            double high = _high.Process(value, ref _h1[channel], ref _h2[channel]);
            return (float)_low.Process(high, ref _l1[channel], ref _l2[channel]);
        }

        /// <summary>
        /// Coefficients depend only on rate and the two cutoffs, and the band is
        /// the same for every copy of one weapon. Resolving them once keeps the
        /// four transcendentals out of a per-copy configure.
        /// </summary>
        private static class BandCoefficients
        {
            private const int Capacity = 8;
            private static readonly Entry[] Entries = new Entry[Capacity];
            private static int _next;

            internal static void Resolve(int rate, float highpass, float lowpass,
                out Biquad high, out Biquad low)
            {
                for (int index = 0; index < Capacity; index++)
                {
                    Entry entry = Entries[index];
                    if (entry == null) break;
                    if (entry.Rate != rate || entry.Highpass != highpass || entry.Lowpass != lowpass)
                        continue;
                    high = entry.High;
                    low = entry.Low;
                    return;
                }
                high = new Biquad(rate, highpass, true);
                low = new Biquad(rate, lowpass, false);
                Entries[_next] = new Entry
                {
                    Rate = rate, Highpass = highpass, Lowpass = lowpass, High = high, Low = low
                };
                _next = (_next + 1) % Capacity;
            }

            private sealed class Entry
            {
                internal int Rate;
                internal float Highpass, Lowpass;
                internal Biquad High, Low;
            }
        }

        internal readonly struct Biquad
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;
            internal Biquad(int rate, float frequency, bool highpass)
            {
                double w = 2.0 * Math.PI * frequency / rate;
                double c = Math.Cos(w), alpha = Math.Sin(w) / 2.0; // Q = 1
                double a0 = 1.0 + alpha;
                _b0 = (highpass ? 1.0 + c : 1.0 - c) / (2.0 * a0);
                _b1 = (highpass ? -(1.0 + c) : 1.0 - c) / a0;
                _b2 = _b0;
                _a1 = -2.0 * c / a0;
                _a2 = (1.0 - alpha) / a0;
            }

            internal double Process(double x, ref double z1, ref double z2)
            {
                double y = _b0 * x + z1;
                z1 = _b1 * x - _a1 * y + z2;
                z2 = _b2 * x - _a2 * y;
                return y;
            }
        }
    }

    internal sealed class PitchedBandPassFilter : MonoBehaviour
    {
        private ProcessingState _state;
        private ProcessingState _spare;
        private float _antiDenormal = 1e-18f;

        // Two states rotate, as in the tail filter: the one taken here was
        // replaced at the previous schedule and cannot still be in a callback.
        internal void Configure(int rate, float highpass, float lowpass)
        {
            ProcessingState state = _spare;
            if (state != null) state.Band.Reconfigure(rate, highpass, lowpass);
            else state = new ProcessingState(rate, highpass, lowpass);
            _spare = Volatile.Read(ref _state);
            Volatile.Write(ref _state, state);
        }

        /// <summary>
        /// Stops processing until the next configure. A stopped voice must not
        /// keep filtering silence: the callback is delivered for as long as the
        /// source object is enabled, whether or not it is playing.
        /// </summary>
        internal void Bypass() => Volatile.Write(ref _state, null);

        private sealed class ProcessingState
        {
            internal readonly PitchedBandPassState Band;
            internal ProcessingState(int rate, float high, float low)
            {
                Band = new PitchedBandPassState(rate, high, low);
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            long trace = AudioFilterTrace.Begin();
            ProcessingState state = Volatile.Read(ref _state);
            if (state == null || data == null || channels < 1 || channels > 2)
            {
                AudioFilterTrace.Record(
                    AudioFilterKind.PitchedBand, trace,
                    data == null ? 0 : data.Length, channels, idle: true);
                return;
            }
            // A copy's tail decays towards denormal state, where every multiply
            // costs the processor a penalty for the rest of the voice. An
            // alternating offset far below a sample's least significant bit keeps
            // the filter out of that range; the offline measurement path shares the
            // filter state but not this, so a measured clip stays bit-exact.
            for (int frame = 0; frame + channels <= data.Length; frame += channels)
            {
                _antiDenormal = -_antiDenormal;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = state.Band.Process(data[frame + channel] + _antiDenormal, channel);
                    data[frame + channel] = value;
                }
            }
            AudioFilterTrace.Record(AudioFilterKind.PitchedBand, trace, data.Length, channels);
        }
    }
}
