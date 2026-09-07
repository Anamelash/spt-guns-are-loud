using System;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    // Minimum-phase FIR reconstructed from magnitude-only attenuation data.
    // Design work allocates on the main thread; ProcessSample is allocation-free.
    internal sealed class HeadphonePassiveFilter
    {
        private const int TapCount = 128;
        private readonly float[] _taps;
        private readonly float[,] _history = new float[2, TapCount];
        private readonly int[] _position = new int[2];

        internal HeadphonePassiveFilter(int sampleRate, HeadsetPassiveProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            _taps = Design(Math.Max(8000, sampleRate), profile);
        }

        internal float ProcessSample(float input, int channel)
        {
            if (channel < 0 || channel > 1 || float.IsNaN(input) || float.IsInfinity(input)) input = 0f;
            int p = _position[channel];
            _history[channel, p] = input;
            double output = 0;
            int h = p;
            for (int i = 0; i < _taps.Length; i++)
            {
                output += _taps[i] * _history[channel, h];
                if (--h < 0) h = TapCount - 1;
            }
            _position[channel] = p + 1 == TapCount ? 0 : p + 1;
            return double.IsNaN(output) || double.IsInfinity(output) ? 0f : (float)output;
        }

        internal void Reset()
        {
            Array.Clear(_history, 0, _history.Length);
            _position[0] = _position[1] = 0;
        }

        private static float[] Design(int rate, HeadsetPassiveProfile profile)
        {
            const int n = 256;
            var logMagnitude = new double[n];
            for (int k = 0; k < n; k++)
            {
                double frequency = Math.Min(rate * 0.5, Math.Min(k, n - k) * rate / (double)n);
                double attenuation = Interpolate(profile, Math.Max(1.0, frequency));
                logMagnitude[k] = Math.Log(Math.Pow(10.0, -attenuation / 20.0));
            }
            var cepstrum = new double[n];
            for (int t = 0; t < n; t++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++) sum += logMagnitude[k] * Math.Cos(2 * Math.PI * k * t / n);
                cepstrum[t] = sum / n;
            }
            for (int t = 1; t < n / 2; t++) cepstrum[t] *= 2;
            for (int t = n / 2 + 1; t < n; t++) cepstrum[t] = 0;
            var taps = new float[TapCount];
            for (int t = 0; t < TapCount; t++)
            {
                double sum = 0;
                for (int k = 0; k < n; k++)
                {
                    double real = 0, imaginary = 0;
                    for (int c = 0; c <= n / 2; c++)
                    {
                        double angle = -2 * Math.PI * k * c / n;
                        real += cepstrum[c] * Math.Cos(angle);
                        imaginary += cepstrum[c] * Math.Sin(angle);
                    }
                    double magnitude = Math.Exp(real);
                    double phase = imaginary + 2 * Math.PI * k * t / n;
                    sum += magnitude * Math.Cos(phase);
                }
                taps[t] = (float)(sum / n);
            }
            return taps;
        }

        private static double Interpolate(HeadsetPassiveProfile profile, double frequency)
        {
            if (profile.BandCount == 0) return 0;
            if (frequency <= profile.FrequencyAt(0)) return Math.Max(0, profile.MeanAttenuationAt(0));
            for (int i = 1; i < profile.BandCount; i++)
            {
                if (frequency > profile.FrequencyAt(i)) continue;
                double p = Math.Log(frequency / profile.FrequencyAt(i - 1), 2) /
                    Math.Log(profile.FrequencyAt(i) / profile.FrequencyAt(i - 1), 2);
                return Math.Max(0, profile.MeanAttenuationAt(i - 1) +
                    (profile.MeanAttenuationAt(i) - profile.MeanAttenuationAt(i - 1)) * p);
            }
            return Math.Max(0, profile.MeanAttenuationAt(profile.BandCount - 1));
        }
    }
}
