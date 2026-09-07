using System;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class HeadphoneElectronicPath
    {
        private readonly int _rate, _holdFrames;
        private readonly HeadsetElectronicsProfile _profile;
        private readonly double _attack, _release, _hpPole, _lpPole;
        private readonly float[] _hpInput = new float[2], _hpOutput = new float[2], _low = new float[2];
        private readonly double[] _gainDb = new double[2];
        private readonly int[] _hold = new int[2];
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
        }

        internal void BeginFrame(float left, float right)
        {
            if (_profile.StereoLinked)
            {
                UpdateGain(0, Math.Max(SafeAbs(left), SafeAbs(right)));
                _gainDb[1] = _gainDb[0]; _hold[1] = _hold[0];
            }
            else
            {
                UpdateGain(0, SafeAbs(left)); UpdateGain(1, SafeAbs(right));
            }
            _reductionDb = (float)Math.Max(0, _profile.QuietGainDb - Math.Min(_gainDb[0], _gainDb[1]));
        }

        internal float ProcessSample(float input, int channel)
        {
            if (channel < 0 || channel > 1 || float.IsNaN(input) || float.IsInfinity(input)) input = 0;
            float high = (float)(_hpPole * (_hpOutput[channel] + input - _hpInput[channel]));
            _hpInput[channel] = input; _hpOutput[channel] = high;
            float low = (float)((1 - _lpPole) * high + _lpPole * _low[channel]);
            _low[channel] = low;
            double output = low * Math.Pow(10, _gainDb[channel] / 20);
            double ceiling = Math.Max(0, Math.Min(1, _profile.OutputCeiling));
            if (output > ceiling) output = ceiling;
            else if (output < -ceiling) output = -ceiling;
            return double.IsNaN(output) || double.IsInfinity(output) ? 0f : (float)output;
        }

        internal void Reset()
        {
            Array.Clear(_hpInput, 0, 2); Array.Clear(_hpOutput, 0, 2); Array.Clear(_low, 0, 2);
            _gainDb[0] = _gainDb[1] = _profile.QuietGainDb;
            _hold[0] = _hold[1] = 0; _reductionDb = 0;
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
        private static float SafeAbs(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Math.Abs(value);
    }
}
