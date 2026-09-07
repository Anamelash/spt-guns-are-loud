using System;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    // Unity 2022.3 Frequency Gain is the biquad A coefficient: a control of 2
    // produced +12.0412 dB at its center in the native renderer, not +6.0206 dB.
    // Controls therefore use 10^(band dB/40). Its "Octave range" control is
    // inverse Q: width=1 and width=2 match Q=1 and Q=.5 in native sweeps.
    internal sealed class HeadphoneNativeEqFit
    {
        internal readonly float BaseVolumeDb, MaximumAnchorErrorDb;
        private readonly float[] _frequencies, _linearGains, _octaveRanges, _anchorErrors;
        internal int BandCount => _frequencies.Length;
        internal float FrequencyAt(int i) => _frequencies[i];
        internal float LinearGainAt(int i) => _linearGains[i];
        internal float OctaveRangeAt(int i) => _octaveRanges[i];
        internal float AnchorErrorDbAt(int i) => _anchorErrors[i];

        private HeadphoneNativeEqFit(float volume, float maximumError,
            float[] frequencies, float[] gains, float[] octaveRanges, float[] errors)
        { BaseVolumeDb = volume; MaximumAnchorErrorDb = maximumError;
          _frequencies = frequencies; _linearGains = gains; _octaveRanges = octaveRanges; _anchorErrors = errors; }

        internal static HeadphoneNativeEqFit Calculate(HeadsetPassiveProfile profile, int sampleRate)
        {
            if (profile == null || profile.BandCount == 0) throw new ArgumentException("Passive profile has no bands");
            int count = profile.BandCount;
            var frequencies = new float[count]; var controlsDb = new double[count];
            var target = new double[count];
            for (int i = 0; i < count; i++) { frequencies[i] = profile.FrequencyAt(i); target[i] = -profile.MeanAttenuationAt(i); }
            double volume = target[0];
            for (int i = 1; i < count; i++) volume = Math.Min(volume, target[i]);
            var octaveRanges = new double[count];
            for (int i = 0; i < count; i++) octaveRanges[i] = 0.7071067811865476;
            double step = 6;
            // First fully solve the stable fixed-width baseline. Flexible widths
            // are accepted only when they reduce the actual maximum anchor error.
            while (step >= 0.01)
            {
                bool changed;
                do
                {
                    changed = Improve(ref volume, controlsDb, target, frequencies, sampleRate, octaveRanges, -step, -80, 0) |
                              Improve(ref volume, controlsDb, target, frequencies, sampleRate, octaveRanges, step, -80, 0);
                    for (int band = 0; band < count; band++)
                    {
                        changed |= ImproveBand(band, -step, volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
                        changed |= ImproveBand(band, step, volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
                    }
                } while (changed);
                step *= 0.5;
            }
            double bestVolume = volume, bestMaximum = MaximumError(volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
            var bestControls = (double[])controlsDb.Clone();
            var bestWidths = (double[])octaveRanges.Clone();
            step = 6;
            while (step >= 0.01)
            {
                bool changed;
                do
                {
                    changed = Improve(ref volume, controlsDb, target, frequencies, sampleRate, octaveRanges, -step, -80, 0) |
                              Improve(ref volume, controlsDb, target, frequencies, sampleRate, octaveRanges, step, -80, 0);
                    for (int band = 0; band < count; band++)
                    {
                        changed |= ImproveBand(band, -step, volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
                        changed |= ImproveBand(band, step, volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
                        changed |= ImproveWidth(band, -step * 0.15, volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
                        changed |= ImproveWidth(band, step * 0.15, volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
                    }
                } while (changed);
                step *= 0.5;
            }
            double flexibleMaximum = MaximumError(volume, controlsDb, target, frequencies, sampleRate, octaveRanges);
            if (flexibleMaximum >= bestMaximum)
            {
                volume = bestVolume; controlsDb = bestControls; octaveRanges = bestWidths;
            }
            var linear = new float[count]; var widths = new float[count]; var errors = new float[count]; float maximum = 0;
            for (int i = 0; i < count; i++)
            {
                linear[i] = (float)Math.Pow(10, controlsDb[i] / 40);
                widths[i] = (float)octaveRanges[i];
                errors[i] = (float)(ResponseDb(frequencies[i], volume, controlsDb, frequencies, sampleRate, octaveRanges) - target[i]);
                maximum = Math.Max(maximum, Math.Abs(errors[i]));
            }
            return new HeadphoneNativeEqFit((float)volume, maximum, frequencies, linear, widths, errors);
        }

        private static bool Improve(ref double volume, double[] gains, double[] target, float[] frequency,
            int rate, double[] widths, double delta, double minimum, double maximum)
        {
            double candidate = Math.Max(minimum, Math.Min(maximum, volume + delta));
            double before = Error(volume, gains, target, frequency, rate, widths), after = Error(candidate, gains, target, frequency, rate, widths);
            if (after + 1e-9 >= before) return false; volume = candidate; return true;
        }
        private static bool ImproveBand(int band, double delta, double volume, double[] gains, double[] target,
            float[] frequency, int rate, double[] widths)
        {
            double original = gains[band], candidate = Math.Max(-52.04119, Math.Min(19.08485, original + delta));
            double before = Error(volume, gains, target, frequency, rate, widths); gains[band] = candidate;
            double after = Error(volume, gains, target, frequency, rate, widths);
            if (after + 1e-9 < before) return true; gains[band] = original; return false;
        }
        private static bool ImproveWidth(int band, double delta, double volume, double[] gains, double[] target,
            float[] frequency, int rate, double[] widths)
        {
            double original = widths[band], candidate = Math.Max(0.2, Math.Min(5.0, original + delta));
            double before = Error(volume, gains, target, frequency, rate, widths); widths[band] = candidate;
            double after = Error(volume, gains, target, frequency, rate, widths);
            if (after + 1e-9 < before) return true; widths[band] = original; return false;
        }
        private static double Error(double volume, double[] gains, double[] target, float[] frequency, int rate, double[] widths)
        {
            double worst = 0, sum = 0;
            for (int i = 0; i < target.Length; i++) { double e = ResponseDb(frequency[i], volume, gains, frequency, rate, widths) - target[i]; worst = Math.Max(worst, Math.Abs(e)); sum += e * e; }
            return worst * worst * 10 + sum;
        }
        private static double MaximumError(double volume, double[] gains, double[] target, float[] frequency, int rate, double[] widths)
        {
            double worst = 0;
            for (int i = 0; i < target.Length; i++) worst = Math.Max(worst,
                Math.Abs(ResponseDb(frequency[i], volume, gains, frequency, rate, widths) - target[i]));
            return worst;
        }
        private static double ResponseDb(double frequency, double volume, double[] gains, float[] centers, int rate, double[] widths)
        {
            double result = volume;
            for (int i = 0; i < gains.Length; i++) result += PeakDb(frequency, centers[i], gains[i], rate, widths[i]);
            return result;
        }
        private static double PeakDb(double frequency, double center, double gainDb, int rate, double width)
        {
            double a = Math.Pow(10, gainDb / 40), w0 = 2 * Math.PI * center / rate;
            double q = 1 / width;
            double alpha = Math.Sin(w0) / (2 * q), w = 2 * Math.PI * Math.Min(rate * 0.499, frequency) / rate;
            double c = Math.Cos(w), s = Math.Sin(w);
            double b0 = 1 + alpha * a, b1 = -2 * Math.Cos(w0), b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a, a1 = b1, a2 = 1 - alpha / a;
            double nr = b0 + b1 * c + b2 * Math.Cos(2 * w), ni = -b1 * s - b2 * Math.Sin(2 * w);
            double dr = a0 + a1 * c + a2 * Math.Cos(2 * w), di = -a1 * s - a2 * Math.Sin(2 * w);
            return 10 * Math.Log10((nr * nr + ni * ni) / Math.Max(1e-20, dr * dr + di * di));
        }
    }
}
