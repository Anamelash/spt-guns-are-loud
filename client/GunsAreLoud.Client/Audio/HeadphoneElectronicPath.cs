using System;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// Managed mirror of the native electronics DSP for tests and diagnostics;
    /// the game's sound comes from the native effect. Character stages follow
    /// the native order and PRNG: noise after the microphone filters, then low
    /// shelf and presence peak, the stereo detector, the output stage and the
    /// delay line. As before, the detector omits the microphone band limits;
    /// it sees the noise and voicing. Neutral character reproduces the v1
    /// renders bit for bit because every neutral stage is skipped, not run.
    /// </summary>
    internal sealed class HeadphoneElectronicPath
    {
        private const double PresenceQ = 1.2;
        private const int DelayCapacity = 2048;
        private readonly int _rate, _holdFrames;
        private readonly HeadsetElectronicsProfile _profile;
        private readonly double _attack, _release, _hpPole, _lpPole;
        private readonly float[] _hpInput = new float[2], _hpOutput = new float[2], _low = new float[2];
        private readonly double[] _gainDb = new double[2];
        private readonly int[] _hold = new int[2];
        private readonly bool _noiseOn;
        private readonly float _noiseScale, _saturation;
        private readonly uint[] _noise = new uint[2];
        private readonly float[] _frameNoise = new float[2];
        private readonly Biquad _shelf, _presence, _bandHigh, _bandLow;
        private readonly BiquadState[] _bandHighState = new BiquadState[2], _bandLowState = new BiquadState[2];
        // Audio and detector paths filter different signals, so each keeps its own history.
        private readonly BiquadState[] _shelfAudio = new BiquadState[2], _presenceAudio = new BiquadState[2];
        private readonly BiquadState[] _shelfDetector = new BiquadState[2], _presenceDetector = new BiquadState[2];
        private readonly int _delayFrames;
        private readonly float[][] _delay = { new float[DelayCapacity], new float[DelayCapacity] };
        private readonly int[] _delayWrite = new int[2];
        private float _reductionDb;

        internal float GainReductionDb => _reductionDb;

        internal HeadphoneElectronicPath(int sampleRate, HeadsetElectronicsProfile profile)
        {
            _rate = Math.Max(8000, sampleRate); _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _attack = Coefficient(profile.AttackSeconds); _release = Coefficient(profile.ReleaseSeconds);
            _holdFrames = Math.Max(0, (int)(_rate * Math.Max(0, profile.HoldSeconds)));
            _hpPole = Math.Exp(-2 * Math.PI * Math.Max(1, profile.MicHighpassHz) / _rate);
            _lpPole = Math.Exp(-2 * Math.PI * Math.Min(_rate * 0.45, Math.Max(profile.MicHighpassHz, profile.MicLowpassHz)) / _rate);
            _gainDb[0] = _gainDb[1] = profile.QuietGainDb;
            if (profile.MicFilterOrder >= 2)
            {
                double highpass = Math.Max(1, profile.MicHighpassHz);
                _bandHigh = Biquad.Butterworth(true, _rate, highpass);
                _bandLow = Biquad.Butterworth(false, _rate, Math.Min(_rate * 0.45, Math.Max(highpass, profile.MicLowpassHz)));
            }

            HeadsetElectronicsCharacter character = profile.Character;
            _noiseOn = character.NoiseDbFs > HeadsetElectronicsCharacter.NoiseOffDbFs;
            _noiseScale = _noiseOn ? 1.7320508f * (float)Math.Pow(10, character.NoiseDbFs / 20.0) : 0f;
            if (character.LowShelfDb != 0f)
                _shelf = Biquad.LowShelf(_rate, Math.Min(character.LowShelfHz, _rate * 0.45), character.LowShelfDb);
            if (character.PresenceDb != 0f)
                _presence = Biquad.Peak(_rate, Math.Min(character.PresenceHz, _rate * 0.45), character.PresenceDb, PresenceQ);
            _saturation = character.Saturation;
            _delayFrames = Math.Min(DelayCapacity - 1, (int)Math.Round((double)character.DelaySeconds * _rate,
                MidpointRounding.AwayFromZero));
            ResetCharacter();
        }

        internal void BeginFrame(float left, float right)
        {
            left = Safe(left); right = Safe(right);
            if (_noiseOn)
            {
                _frameNoise[0] = _noiseScale * Uniform(ref _noise[0]);
                _frameNoise[1] = _noiseScale * Uniform(ref _noise[1]);
            }
            float detectedLeft = Voice(left, 0, _shelfDetector, _presenceDetector);
            float detectedRight = Voice(right, 1, _shelfDetector, _presenceDetector);
            if (_profile.StereoLinked)
            {
                UpdateGain(0, Math.Max(Math.Abs(detectedLeft), Math.Abs(detectedRight)));
                _gainDb[1] = _gainDb[0]; _hold[1] = _hold[0];
            }
            else
            {
                UpdateGain(0, Math.Abs(detectedLeft)); UpdateGain(1, Math.Abs(detectedRight));
            }
            _reductionDb = (float)Math.Max(0, _profile.QuietGainDb - Math.Min(_gainDb[0], _gainDb[1]));
        }

        internal float ProcessSample(float input, int channel)
        {
            if (channel < 0 || channel > 1) return 0f;
            input = Safe(input);
            float low;
            if (_bandHigh != null)
                low = (float)_bandLow.Run(ref _bandLowState[channel], _bandHigh.Run(ref _bandHighState[channel], input));
            else
            {
                float high = (float)(_hpPole * (_hpOutput[channel] + input - _hpInput[channel]));
                _hpInput[channel] = input; _hpOutput[channel] = high;
                low = (float)((1 - _lpPole) * high + _lpPole * _low[channel]);
                _low[channel] = low;
            }
            float microphone = Voice(low, channel, _shelfAudio, _presenceAudio);
            double output = microphone * Math.Pow(10, _gainDb[channel] / 20);
            double ceiling = Math.Max(0, Math.Min(1, _profile.OutputCeiling));
            output = Limit(output, ceiling, _saturation);
            float electronic = double.IsNaN(output) || double.IsInfinity(output) ? 0f : (float)output;
            int write = _delayWrite[channel];
            _delay[channel][write] = electronic;
            if (_delayFrames > 0) electronic = _delay[channel][(write - _delayFrames) & (DelayCapacity - 1)];
            _delayWrite[channel] = (write + 1) & (DelayCapacity - 1);
            return electronic;
        }

        internal void Reset()
        {
            Array.Clear(_hpInput, 0, 2); Array.Clear(_hpOutput, 0, 2); Array.Clear(_low, 0, 2);
            _gainDb[0] = _gainDb[1] = _profile.QuietGainDb;
            _hold[0] = _hold[1] = 0; _reductionDb = 0;
            ResetCharacter();
        }

        /// <summary>
        /// Output stage shared with the native DSP: an exact clamp without
        /// saturation, otherwise linear to ceiling·(1 − s) and a tanh knee.
        /// </summary>
        internal static double Limit(double value, double ceiling, double saturation)
        {
            if (!(saturation > 0))
            {
                if (value > ceiling) return ceiling;
                return value < -ceiling ? -ceiling : value;
            }
            double knee = ceiling * (1 - Math.Min(saturation, 1));
            double magnitude = Math.Abs(value);
            if (magnitude <= knee) return value;
            double range = ceiling - knee;
            double shaped = Math.Min(ceiling, knee + range * Math.Tanh((magnitude - knee) / range));
            return value < 0 ? -shaped : shaped;
        }

        /// <summary>Per-channel xorshift32 seed; identical to the native DSP.</summary>
        internal static uint NoiseSeed(int channel)
        {
            uint seed = unchecked(0x9E3779B9u * (uint)(channel + 1) + 0x7F4A7C15u);
            return seed == 0 ? 1u : seed;
        }

        private float Voice(float value, int channel, BiquadState[] shelf, BiquadState[] presence)
        {
            if (_noiseOn) value += _frameNoise[channel];
            if (_shelf != null) value = (float)_shelf.Run(ref shelf[channel], value);
            if (_presence != null) value = (float)_presence.Run(ref presence[channel], value);
            return value;
        }

        private void ResetCharacter()
        {
            for (int channel = 0; channel < 2; channel++)
            {
                _noise[channel] = NoiseSeed(channel);
                _frameNoise[channel] = 0f;
                _shelfAudio[channel] = _presenceAudio[channel] = default;
                _shelfDetector[channel] = _presenceDetector[channel] = default;
                _bandHighState[channel] = _bandLowState[channel] = default;
                Array.Clear(_delay[channel], 0, DelayCapacity);
                _delayWrite[channel] = 0;
            }
        }

        private static float Uniform(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return unchecked((int)state) * (1f / 2147483648f);
        }

        private void UpdateGain(int channel, float detector)
        {
            double levelDb = 20 * Math.Log10(Math.Max(0.0000001, detector));
            double target = StaticGain(levelDb);
            if (target < _gainDb[channel])
            {
                _gainDb[channel] = _attack * _gainDb[channel] + (1 - _attack) * target;
                _hold[channel] = _holdFrames;
            }
            else if (_hold[channel] > 0) _hold[channel]--;
            else _gainDb[channel] = _release * _gainDb[channel] + (1 - _release) * target;
        }

        private double StaticGain(double inputDb)
        {
            double threshold = _profile.ThresholdDbFs, knee = Math.Max(0, _profile.KneeDb);
            double ratio = Math.Max(1, _profile.Ratio), over = inputDb - threshold;
            double reduction;
            if (knee <= 0) reduction = over > 0 ? -over * (1 - 1 / ratio) : 0;
            else if (over <= -knee / 2) reduction = 0;
            else if (over >= knee / 2) reduction = -over * (1 - 1 / ratio);
            else { double x = over + knee / 2; reduction = -(1 - 1 / ratio) * x * x / (2 * knee); }
            return _profile.QuietGainDb + reduction;
        }

        private double Coefficient(float seconds) => seconds <= 0 ? 0 : Math.Exp(-1.0 / (_rate * seconds));
        private static float Safe(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;

        private struct BiquadState { internal double X1, X2, Y1, Y2; }

        // RBJ cookbook sections, coefficients and state in double as in the native DSP.
        private sealed class Biquad
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;

            private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
            { _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0; }

            internal static Biquad LowShelf(double rate, double hz, double db)
            {
                double a = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / rate, c = Math.Cos(w);
                double sq = 2 * Math.Sqrt(a) * (Math.Sin(w) / 2 * Math.Sqrt(2.0));
                return new Biquad(a * ((a + 1) - (a - 1) * c + sq), 2 * a * ((a - 1) - (a + 1) * c),
                    a * ((a + 1) - (a - 1) * c - sq), (a + 1) + (a - 1) * c + sq,
                    -2 * ((a - 1) + (a + 1) * c), (a + 1) + (a - 1) * c - sq);
            }

            // Butterworth band edge (Q 1/sqrt 2), as the native band order 2.
            internal static Biquad Butterworth(bool highpass, double rate, double hz)
            {
                double w = 2 * Math.PI * hz / rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.70710678118654752);
                double edge = highpass ? (1 + c) / 2 : (1 - c) / 2;
                return new Biquad(edge, highpass ? -2 * edge : 2 * edge, edge, 1 + alpha, -2 * c, 1 - alpha);
            }

            internal static Biquad Peak(double rate, double hz, double db, double q)
            {
                double a = Math.Pow(10, db / 40), w = 2 * Math.PI * hz / rate, c = Math.Cos(w);
                double alpha = Math.Sin(w) / (2 * q);
                return new Biquad(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a);
            }

            internal double Run(ref BiquadState s, double x)
            {
                double y = _b0 * x + _b1 * s.X1 + _b2 * s.X2 - _a1 * s.Y1 - _a2 * s.Y2;
                y += 1e-20; y -= 1e-20;
                if (double.IsNaN(y) || double.IsInfinity(y)) { s = default; return 0; }
                s.X2 = s.X1; s.X1 = x; s.Y2 = s.Y1; s.Y1 = y;
                return y;
            }
        }
    }
}
