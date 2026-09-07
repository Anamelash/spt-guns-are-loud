using System;

namespace GunsAreLoud.Client.Audio
{
    // Per-voice, output-time envelope. Does not crop a source or choose BeatLn.
    // One multiply per output frame; never advances while waiting for an onset.
    internal struct HeadphoneTailEnvelope
    {
        internal const float HoldSeconds = 0.06f;
        private int _holdFrames, _frame;
        private double _step, _gain;

        internal void Reset(int sampleRate, float dbPerSecond)
        {
            int rate = Math.Max(8000, sampleRate);
            _holdFrames = (int)(rate * HoldSeconds);
            _frame = 0;
            _gain = 1;
            _step = Math.Pow(10, -Math.Max(0, dbPerSecond) / (20.0 * rate));
        }

        internal float Next()
        {
            float result = (float)_gain;
            if (_frame++ >= _holdFrames) _gain *= _step;
            return result;
        }
    }
}
