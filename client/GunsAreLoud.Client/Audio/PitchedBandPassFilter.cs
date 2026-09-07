using System;
using System.Threading;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    // Shared by offline level measurement and live playback: identical coefficients,
    // Q and stereo state. No Unity API, allocation or locks in Process.
    internal sealed class PitchedBandPassState
    {
        private readonly Biquad _high;
        private readonly Biquad _low;
        private readonly double[] _h1 = new double[2], _h2 = new double[2];
        private readonly double[] _l1 = new double[2], _l2 = new double[2];

        internal PitchedBandPassState(int rate, float highpass, float lowpass)
        {
            rate = Math.Max(8000, rate);
            lowpass = Math.Max(80f, Math.Min(3000f, lowpass));
            highpass = Math.Max(10f, Math.Min(lowpass - 10f, highpass));
            _high = new Biquad(rate, highpass, true);
            _low = new Biquad(rate, lowpass, false);
        }

        internal float Process(float value, int channel)
        {
            double high = _high.Process(value, ref _h1[channel], ref _h2[channel]);
            return (float)_low.Process(high, ref _l1[channel], ref _l2[channel]);
        }

        private readonly struct Biquad
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

        internal void Configure(int rate, float highpass, float lowpass) =>
            Volatile.Write(ref _state, new ProcessingState(rate, highpass, lowpass));

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
            ProcessingState state = Volatile.Read(ref _state);
            if (state == null || data == null || channels < 1 || channels > 2) return;
            for (int frame = 0; frame + channels <= data.Length; frame += channels)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = state.Band.Process(data[frame + channel], channel);
                    data[frame + channel] = value;
                }
            }
        }
    }
}
