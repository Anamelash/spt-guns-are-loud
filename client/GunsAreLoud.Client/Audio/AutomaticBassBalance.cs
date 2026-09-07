using System;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct LowEndBandLevels
    {
        internal readonly float Wide, Bass, Texture;
        internal LowEndBandLevels(float wide, float bass, float texture)
        { Wide = wide; Bass = bass; Texture = texture; }
    }

    // Diagnostic-only fourth-order crossover. It measures the rendered copy;
    // it is never inserted into the playback signal path.
    internal sealed class BassCrossoverState
    {
        private readonly Section _low, _allPass;
        private readonly double[] _low1 = new double[4], _low2 = new double[4];
        private readonly double[] _all1 = new double[2], _all2 = new double[2];

        internal BassCrossoverState(int rate)
        {
            _low = new Section(Math.Max(8000, rate), false);
            _allPass = new Section(Math.Max(8000, rate), true);
        }

        internal void Process(float input, int channel, out float bass, out float texture)
        {
            int c = channel * 2;
            double low = _low.Process(input, ref _low1[c], ref _low2[c]);
            low = _low.Process(low, ref _low1[c + 1], ref _low2[c + 1]);
            double aligned = _allPass.Process(input, ref _all1[channel], ref _all2[channel]);
            bass = (float)low;
            texture = (float)(aligned - low);
        }

        private readonly struct Section
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;
            internal Section(int rate, bool allPass)
            {
                double omega = 2 * Math.PI * 180f / rate;
                double cosine = Math.Cos(omega), alpha = Math.Sin(omega) / Math.Sqrt(2);
                double denominator = 1 + alpha;
                _a1 = -2 * cosine / denominator;
                _a2 = (1 - alpha) / denominator;
                _b0 = allPass ? _a2 : (1 - cosine) / (2 * denominator);
                _b1 = allPass ? _a1 : (1 - cosine) / denominator;
                _b2 = allPass ? 1 : _b0;
            }
            internal double Process(double input, ref double first, ref double second)
            {
                double output = input * _b0 + first;
                first = input * _b1 - output * _a1 + second;
                second = input * _b2 - output * _a2;
                return output;
            }
        }
    }

    // A diagnostic measurement of the actual post-envelope copy, limited to
    // its first 180 ms. Created on the main thread and owned by the callback.
    internal sealed class BassAttackMeter
    {
        private readonly BassCrossoverState _split;
        private readonly int _limit;
        private int _frames;
        private double _bassEnergy, _textureEnergy;
        internal BassAttackMeter(int rate)
        { _split = new BassCrossoverState(rate); _limit = Math.Max(1, (int)(rate * LowEndLevelModel.WindowSeconds)); }
        internal float BassRms => _frames == 0 ? 0 : (float)Math.Sqrt(_bassEnergy / _frames);
        internal float TextureRms => _frames == 0 ? 0 : (float)Math.Sqrt(_textureEnergy / _frames);
        internal void AddFrame(float[] samples, int offset, int channels)
        {
            if (_frames >= _limit || channels < 1 || channels > 2) return;
            double bassSum = 0, textureSum = 0;
            for (int c = 0; c < channels; c++)
            {
                _split.Process(samples[offset + c], c, out float bass, out float texture);
                bassSum += bass * (double)bass;
                textureSum += texture * (double)texture;
            }
            _bassEnergy += bassSum / channels;
            _textureEnergy += textureSum / channels;
            _frames++;
        }
    }
}
