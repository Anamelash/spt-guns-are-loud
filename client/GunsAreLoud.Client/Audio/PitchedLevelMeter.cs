using System;

namespace GunsAreLoud.Client.Audio
{
    // Audio-thread-owned, allocation-free meter. Mean stereo energy avoids phase
    // cancellation. The first 180 ms and remaining tail are measured separately.
    internal struct PitchedLevelMeter
    {
        private int _attackLimit;
        private double _attackEnergy, _tailEnergy;
        internal int AttackFrames { get; private set; }
        internal int TailFrames { get; private set; }
        internal float AttackRms => Rms(_attackEnergy, AttackFrames);
        internal float TailRms => Rms(_tailEnergy, TailFrames);

        internal void Reset(int sampleRate)
        {
            _attackLimit = Math.Max(1, (int)(sampleRate * LowEndLevelModel.WindowSeconds));
            _attackEnergy = _tailEnergy = 0;
            AttackFrames = TailFrames = 0;
        }

        internal void AddFrame(float[] samples, int offset, int channels)
        {
            double energy = 0;
            for (int i = 0; i < channels; i++)
                energy += samples[offset + i] * (double)samples[offset + i];
            energy /= channels;
            if (AttackFrames < _attackLimit)
            {
                _attackEnergy += energy;
                AttackFrames++;
            }
            else
            {
                _tailEnergy += energy;
                TailFrames++;
            }
        }

        private static float Rms(double energy, int frames) =>
            frames > 0 ? (float)Math.Sqrt(energy / frames) : 0f;
    }
}
