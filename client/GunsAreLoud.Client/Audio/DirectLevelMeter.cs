using System;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// The two stages of the ITU-R BS.1770-4 K-weighting (a high shelf and a
    /// high-pass), computed for any sample rate the way libebur128 does it, so a
    /// 44.1 kHz recording and a 48 kHz output are weighted alike.
    /// </summary>
    internal struct KWeightingCoefficients
    {
        internal double ShelfB0, ShelfB1, ShelfB2, ShelfA1, ShelfA2;
        internal double PassB0, PassB1, PassB2, PassA1, PassA2;
        internal int Rate;

        internal static KWeightingCoefficients For(int rate)
        {
            rate = Math.Max(8000, rate);
            var c = new KWeightingCoefficients { Rate = rate };
            double f0 = 1681.974450955533;
            double gain = 3.999843853973347;
            double q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / rate);
            double vh = Math.Pow(10.0, gain / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + k / q + k * k;
            c.ShelfB0 = (vh + vb * k / q + k * k) / a0;
            c.ShelfB1 = 2.0 * (k * k - vh) / a0;
            c.ShelfB2 = (vh - vb * k / q + k * k) / a0;
            c.ShelfA1 = 2.0 * (k * k - 1.0) / a0;
            c.ShelfA2 = (1.0 - k / q + k * k) / a0;

            f0 = 38.13547087602444;
            q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / rate);
            a0 = 1.0 + k / q + k * k;
            c.PassB0 = 1.0;
            c.PassB1 = -2.0;
            c.PassB2 = 1.0;
            c.PassA1 = 2.0 * (k * k - 1.0) / a0;
            c.PassA2 = (1.0 - k / q + k * k) / a0;
            return c;
        }

        /// <summary>Magnitude of the whole weighting at one frequency, in dB.</summary>
        internal double ResponseDb(double frequencyHz)
        {
            double w = 2.0 * Math.PI * frequencyHz / Rate;
            return 20.0 * Math.Log10(Magnitude(ShelfB0, ShelfB1, ShelfB2, ShelfA1, ShelfA2, w) *
                Magnitude(PassB0, PassB1, PassB2, PassA1, PassA2, w));
        }

        private static double Magnitude(double b0, double b1, double b2, double a1, double a2, double w)
        {
            double cos1 = Math.Cos(w), sin1 = Math.Sin(w), cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
            double nr = b0 + b1 * cos1 + b2 * cos2, ni = -(b1 * sin1 + b2 * sin2);
            double dr = 1.0 + a1 * cos1 + a2 * cos2, di = -(a1 * sin1 + a2 * sin2);
            return Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
        }
    }

    /// <summary>One channel of the K-weighting filter. A value type: no allocation on any thread.</summary>
    internal struct KWeightingChannel
    {
        private double _s1, _s2, _p1, _p2;

        internal void Reset() { _s1 = _s2 = _p1 = _p2 = 0.0; }

        internal double Process(double x, ref KWeightingCoefficients c)
        {
            // Transposed direct form II, shelf then high-pass.
            double y = c.ShelfB0 * x + _s1;
            _s1 = c.ShelfB1 * x - c.ShelfA1 * y + _s2;
            _s2 = c.ShelfB2 * x - c.ShelfA2 * y;
            double z = c.PassB0 * y + _p1;
            _p1 = c.PassB1 * y - c.PassA1 * z + _p2;
            _p2 = c.PassB2 * y - c.PassA2 * z;
            return z;
        }
    }

    /// <summary>
    /// Histogram of the per-frame peak max(|L|,|R|) on a log scale, from which a
    /// percentile is read without keeping the samples, and the exact peak.
    /// Fixed storage: it can be filled from the audio thread.
    /// </summary>
    internal sealed class PeakHistogram
    {
        internal const int Bins = 512;
        internal const double FloorDb = -96.0;
        internal const double BinWidthDb = -FloorDb / Bins;

        // Index 0 holds everything below the floor, including digital silence.
        private readonly long[] _counts = new long[Bins + 1];
        private long _total;
        private float _peak;

        internal long Total => _total;

        /// <summary>The largest per-frame peak seen, linear.</summary>
        internal float Peak => _peak;

        /// <summary>The largest per-frame peak seen, in dBFS; NaN when empty.</summary>
        internal float PeakDb => _total <= 0 ? float.NaN : DirectLevelMeter.ToDb(_peak);

        internal void Reset()
        {
            Array.Clear(_counts, 0, _counts.Length);
            _total = 0;
            _peak = 0f;
        }

        internal void Add(float peak)
        {
            int bin = 0;
            if (peak > 0f && !float.IsInfinity(peak))
            {
                if (peak > _peak) _peak = peak;
                double db = 20.0 * Math.Log10(peak);
                if (db >= FloorDb)
                    bin = 1 + Math.Min(Bins - 1, (int)((db - FloorDb) / BinWidthDb));
            }
            else if (float.IsInfinity(peak)) bin = Bins;
            _counts[bin]++;
            _total++;
        }

        internal void AddStereo(float[] stereo, int frames)
        {
            if (stereo == null) return;
            frames = Math.Min(frames, stereo.Length / 2);
            for (int frame = 0; frame < frames; frame++)
                Add(Math.Max(Math.Abs(stereo[frame * 2]), Math.Abs(stereo[frame * 2 + 1])));
        }

        /// <summary>The level below which the given share of frames lies, in dBFS, interpolated inside its bin; NaN when empty.</summary>
        internal float PercentileDb(double share)
        {
            if (_total <= 0) return float.NaN;
            double target = Math.Max(0.0, Math.Min(1.0, share)) * _total;
            double cumulative = 0;
            for (int bin = 0; bin <= Bins; bin++)
            {
                long count = _counts[bin];
                if (count == 0) continue;
                if (cumulative + count < target) { cumulative += count; continue; }
                if (bin == 0) return (float)FloorDb;
                double fraction = (target - cumulative) / count;
                return (float)(FloorDb + (bin - 1 + fraction) * BinWidthDb);
            }
            return 0f;
        }
    }

    /// <summary>
    /// Clip-intrinsic level of one shot event: the K-weighted energy of its
    /// attack (the first 50 ms, BS.1770 sum over the two channels) and its
    /// peak. The playing volume is not in either; it is added when the shot plays.
    /// </summary>
    internal static class DirectLevelMeter
    {
        internal const float WindowSeconds = 0.050f;
        internal const float SilentLevelDb = -120f;

        internal static int WindowFrames(int rate) => (int)(Math.Max(8000, rate) * (double)WindowSeconds);

        /// <summary>
        /// K-weighted level of the attack window of interleaved PCM in dB
        /// (-0.691 + 10·log10 of the summed channel mean squares). A mono
        /// recording counts as both channels; an event shorter than the window
        /// is measured over the whole window, as silence after its end.
        /// </summary>
        internal static float MeasureAttackWindow(float[] pcm, int channels, int rate) =>
            MeasureWindowDb(pcm, channels, rate, WindowSeconds);

        /// <summary>
        /// K-weighted level of the first <paramref name="seconds"/> of
        /// interleaved PCM in dB, measured as <see cref="MeasureAttackWindow"/>
        /// measures the attack window.
        /// </summary>
        internal static float MeasureWindowDb(float[] pcm, int channels, int rate, float seconds)
        {
            if (pcm == null || channels <= 0 || rate < 8000 || !(seconds > 0f)) return float.NaN;
            KWeightingCoefficients coefficients = KWeightingCoefficients.For(rate);
            var left = new KWeightingChannel();
            var right = new KWeightingChannel();
            int frames = pcm.Length / channels;
            int window = Math.Max(1, (int)(Math.Max(8000, rate) * (double)seconds));
            double sum = 0;
            for (int frame = 0; frame < window; frame++)
            {
                double l = frame < frames ? pcm[frame * channels] : 0.0;
                double r = frame < frames ? pcm[frame * channels + (channels > 1 ? 1 : 0)] : 0.0;
                double kl = left.Process(l, ref coefficients);
                double kr = right.Process(r, ref coefficients);
                sum += kl * kl + kr * kr;
            }
            return Level(sum, window);
        }

        /// <summary>-0.691 + 10·log10(summed channel energy / frames), floored for silence.</summary>
        internal static float Level(double summedChannelEnergy, int frames)
        {
            if (frames <= 0 || double.IsNaN(summedChannelEnergy)) return float.NaN;
            double meanSquare = summedChannelEnergy / frames;
            if (!(meanSquare > 1e-12)) return SilentLevelDb;
            return (float)(-0.691 + 10.0 * Math.Log10(meanSquare));
        }

        internal static float ToDb(float linear) =>
            linear > 1e-6f ? (float)(20.0 * Math.Log10(linear)) : SilentLevelDb;

        /// <summary>Samples that carry a real signal: a peak above -80 dBFS and not one constant value.</summary>
        internal static bool IsSignal(float[] samples)
        {
            if (samples == null || samples.Length == 0) return false;
            float minimum = samples[0], maximum = samples[0], peak = 0f;
            for (int index = 0; index < samples.Length; index++)
            {
                float value = samples[index];
                if (float.IsNaN(value) || float.IsInfinity(value)) return false;
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
                float magnitude = Math.Abs(value);
                if (magnitude > peak) peak = magnitude;
            }
            return peak > 1e-4f && maximum - minimum > 1e-6f;
        }

        /// <summary>Interleaved PCM as two channels: mono is doubled, channels past the second are left out.</summary>
        internal static float[] ToStereo(float[] samples, int channels)
        {
            if (samples == null || channels <= 0) return Array.Empty<float>();
            if (channels == 2) return samples;
            int frames = samples.Length / channels;
            var stereo = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                stereo[frame * 2] = samples[frame * channels];
                stereo[frame * 2 + 1] = samples[frame * channels + (channels > 1 ? 1 : 0)];
            }
            return stereo;
        }

        /// <summary>Peak of max(|L|,|R|) over the whole interleaved event, in dBFS.</summary>
        internal static float MeasurePeakDb(float[] pcm, int channels)
        {
            if (pcm == null || channels <= 0) return float.NaN;
            int frames = pcm.Length / channels;
            float peak = 0f;
            for (int frame = 0; frame < frames; frame++)
            {
                float l = Math.Abs(pcm[frame * channels]);
                float r = channels > 1 ? Math.Abs(pcm[frame * channels + 1]) : l;
                float value = l > r ? l : r;
                if (value > peak && !float.IsInfinity(value)) peak = value;
            }
            return frames == 0 ? float.NaN : ToDb(peak);
        }
    }

    /// <summary>What the sum meter found for one recording and its copy.</summary>
    internal readonly struct ShotSumMeasure
    {
        /// <summary>Peak of the copy at ratio 1 (the pitched, band-passed recording with the copy's attack), linear.</summary>
        internal readonly float CopyPeak;
        /// <summary>K-weighted level of the copy's first attack window at ratio 1, dB.</summary>
        internal readonly float CopyAttackDb;
        /// <summary>Peak of recording + ratio · copy, aligned at the shot, at <see cref="ShotSumRatios.Values"/>.</summary>
        internal readonly float[] SumPeaks;
        /// <summary>
        /// What the program limiter takes off the event's attack level, dB (at
        /// most 0): the recording alone, then each ratio, each at the peaks over
        /// the ceiling of <see cref="ShotSumRatios.OverDb"/>; null when not measured.
        /// </summary>
        internal readonly float[] LimitLoss;

        internal ShotSumMeasure(float copyPeak, float copyAttackDb, float[] sumPeaks, float[] limitLoss = null)
        {
            CopyPeak = copyPeak;
            CopyAttackDb = copyAttackDb;
            SumPeaks = sumPeaks;
            LimitLoss = limitLoss;
        }

        internal bool Valid => SumPeaks != null && SumPeaks.Length == ShotSumRatios.Values.Length &&
            !float.IsNaN(CopyAttackDb);

        internal bool HasLoss => LimitLoss != null && LimitLoss.Length == ShotSumRatios.LossLength;
    }

    /// <summary>
    /// Measures the mod's copy of a recording as it will be heard with the
    /// recording: the copy is the recording played at the layer's pitch through
    /// its band-pass, with the layer's 2 ms attack, starting with the shot. The
    /// copy's own peak and attack level, and the peak of the aligned sum at a
    /// few copy-to-recording ratios, let the shot be budgeted before it plays.
    /// Raw PCM in, raw units out: volumes and gains are applied at the shot.
    /// </summary>
    internal static class ShotSumMeter
    {
        internal const float AttackSeconds = 0.002f;

        /// <summary>
        /// How far into the event the aligned sum is followed. A copy peaks in
        /// its body, within the first third of a second of the shot; the
        /// recording's own peak is at its onset.
        /// </summary>
        internal const float ScanSeconds = 0.75f;

        internal static ShotSumMeasure Measure(float[] stereo, int rate, float pitch, float highpassHz, float lowpassHz)
        {
            if (stereo == null || stereo.Length < 4 || rate < 8000) return default;
            pitch = Math.Max(0.1f, Math.Min(3f, pitch));
            int frames = stereo.Length / 2;
            int outputs = Math.Min((int)(frames / pitch), (int)(rate * ScanSeconds));
            float[] ratios = ShotSumRatios.Values;
            var sums = new float[ratios.Length];
            var band = new PitchedBandPassState(rate, highpassHz, lowpassHz);
            KWeightingCoefficients coefficients = KWeightingCoefficients.For(rate);
            var kLeft = new KWeightingChannel();
            var kRight = new KWeightingChannel();
            int window = DirectLevelMeter.WindowFrames(rate);
            int attackFrames = Math.Max(1, (int)(rate * AttackSeconds));
            // The event's opening, kept for the limiter: the attack window plus
            // the limiter's lookahead, so the last frames of the window get the
            // gain the frames just after them ask for, as they do at the listener.
            int lookahead = (int)(rate * MasterLimiterState.ProgramLookaheadSeconds);
            int opening = window + lookahead;
            var directOpening = new float[opening * 2];
            var copyOpening = new float[opening * 2];
            double kSum = 0;
            float copyPeak = 0f;
            float directPeak = 0f;
            for (int output = 0; output < outputs; output++)
            {
                double position = output * (double)pitch;
                int frame = (int)position;
                if (frame + 1 >= frames) break;
                float fraction = (float)(position - frame);
                float attack = output < attackFrames
                    ? (float)Math.Sin(output / (double)attackFrames * Math.PI * 0.5)
                    : 1f;
                for (int channel = 0; channel < 2; channel++)
                {
                    float first = stereo[frame * 2 + channel];
                    float second = stereo[(frame + 1) * 2 + channel];
                    float input = first + (second - first) * fraction;
                    float copy = band.Process(input, channel) * attack;
                    float direct = output < frames ? stereo[output * 2 + channel] : 0f;
                    float magnitude = Math.Abs(copy);
                    if (magnitude > copyPeak) copyPeak = magnitude;
                    float directMagnitude = Math.Abs(direct);
                    if (directMagnitude > directPeak) directPeak = directMagnitude;
                    if (output < window)
                    {
                        double k = channel == 0 ? kLeft.Process(copy, ref coefficients) : kRight.Process(copy, ref coefficients);
                        kSum += k * k;
                    }
                    if (output < opening)
                    {
                        directOpening[output * 2 + channel] = direct;
                        copyOpening[output * 2 + channel] = copy;
                    }
                    for (int index = 0; index < ratios.Length; index++)
                    {
                        float sum = Math.Abs(direct + ratios[index] * copy);
                        if (sum > sums[index]) sums[index] = sum;
                    }
                }
            }
            float[] loss = MeasureLimitLoss(directOpening, copyOpening, window, lookahead, rate, directPeak, sums, ref coefficients);
            return new ShotSumMeasure(copyPeak, DirectLevelMeter.Level(kSum, window), sums, loss);
        }

        /// <summary>
        /// What the program limiter takes off the attack level of the event,
        /// for the recording alone and at each ratio, when the event's peak
        /// stands at each of <see cref="ShotSumRatios.OverDb"/> over the
        /// ceiling: the attack level of the limited opening less that of the
        /// same opening unlimited. The limiter is the one the listener runs.
        /// </summary>
        internal static float[] MeasureLimitLoss(float[] directOpening, float[] copyOpening, int window, int lookahead,
            int rate, float directPeak, float[] sums, ref KWeightingCoefficients coefficients)
        {
            float[] ratios = ShotSumRatios.Values;
            float[] overs = ShotSumRatios.OverDb;
            var loss = new float[ShotSumRatios.LossLength];
            int opening = window + lookahead;
            var buffer = new float[opening * 2];
            var kLeft = new KWeightingChannel();
            var kRight = new KWeightingChannel();
            for (int row = 0; row < ShotSumRatios.LossRows; row++)
            {
                float ratio = row == 0 ? 0f : ratios[row - 1];
                float eventPeak = row == 0 ? directPeak : sums[row - 1];
                if (!(eventPeak > 0f)) continue;
                // The attack level of the unlimited event at this ratio, at gain 1.
                kLeft.Reset();
                kRight.Reset();
                double unlimited = 0;
                for (int frame = 0; frame < window; frame++)
                {
                    double left = kLeft.Process(directOpening[frame * 2] + ratio * copyOpening[frame * 2], ref coefficients);
                    double right = kRight.Process(directOpening[frame * 2 + 1] + ratio * copyOpening[frame * 2 + 1], ref coefficients);
                    unlimited += left * left + right * right;
                }
                float unlimitedDb = DirectLevelMeter.Level(unlimited, window);
                for (int column = 0; column < overs.Length; column++)
                {
                    float scale = MasterLimiterState.Ceiling * (float)Math.Pow(10.0, overs[column] / 20.0) / eventPeak;
                    for (int index = 0; index < buffer.Length; index++)
                        buffer[index] = scale * (directOpening[index] + ratio * copyOpening[index]);
                    var limiter = new MasterLimiterState();
                    limiter.SetProgram(true);
                    limiter.Process(buffer, 2, rate, out _, out _);
                    // The limited opening leaves one lookahead late; its attack
                    // window is the input's, read where it comes out.
                    int delay = limiter.DelayFrames;
                    kLeft.Reset();
                    kRight.Reset();
                    double limited = 0;
                    for (int frame = delay; frame < delay + window && frame < opening; frame++)
                    {
                        double left = kLeft.Process(buffer[frame * 2], ref coefficients);
                        double right = kRight.Process(buffer[frame * 2 + 1], ref coefficients);
                        limited += left * left + right * right;
                    }
                    float limitedDb = DirectLevelMeter.Level(limited, window);
                    float scaleDb = (float)(20.0 * Math.Log10(scale));
                    float value = limitedDb - (unlimitedDb + scaleDb);
                    loss[row * overs.Length + column] = float.IsNaN(value) ? 0f : Math.Min(0f, value);
                }
            }
            return loss;
        }
    }
}
