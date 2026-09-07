using System;

namespace GunsAreLoud.Client.Audio
{
    internal struct HearingDspChannelState
    {
        private float _wet;
        private float _gain;
        private float _alpha;
        private float _lowpass;
        private float _tinnitus;
        private bool _initialized;

        internal float Wet => _wet;
        internal float Gain => _gain;

        internal void Reset()
        {
            _wet = 0f;
            _gain = 1f;
            _alpha = 1f;
            _lowpass = 0f;
            _tinnitus = 0f;
            _initialized = true;
        }

        internal float Process(
            float dry,
            float targetWet,
            float targetGain,
            float targetAlpha,
            float targetTinnitus,
            float ringSample,
            float smoothing,
            out float preClamp)
        {
            if (!_initialized)
            {
                Reset();
            }
            if (float.IsNaN(dry) || float.IsInfinity(dry))
            {
                _lowpass = 0f;
                preClamp = dry;
                return HearingDspTransfer.SanitizeAndClamp(preClamp, out _);
            }
            targetWet = FiniteOr(targetWet, 0f);
            targetGain = FiniteOr(targetGain, 1f);
            targetAlpha = FiniteOr(targetAlpha, 1f);
            targetTinnitus = FiniteOr(targetTinnitus, 0f);
            ringSample = FiniteOr(ringSample, 0f);
            smoothing = FiniteOr(smoothing, 0f);
            _wet += (targetWet - _wet) * smoothing;
            _gain += (targetGain - _gain) * smoothing;
            _alpha += (targetAlpha - _alpha) * smoothing;
            _tinnitus += (targetTinnitus - _tinnitus) * smoothing;
            _lowpass += _alpha * (dry - _lowpass);

            preClamp = dry + ((_lowpass * _gain) - dry) * _wet + ringSample * _tinnitus;
            float output = HearingDspTransfer.SanitizeAndClamp(preClamp, out bool nonFinite);
            if (nonFinite)
            {
                Reset();
            }
            return output;
        }

        private static float FiniteOr(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        }
    }

    internal static class HearingDspTransfer
    {
        internal static float DbToLinear(float db)
        {
            return (float)Math.Pow(10.0, db / 20.0);
        }

        internal static float CutoffToAlpha(float cutoffHz, int sampleRate)
        {
            int safeRate = Math.Max(8000, sampleRate);
            float nyquist = Math.Max(100f, safeRate * 0.5f);
            float cutoff = Math.Max(20f, Math.Min(cutoffHz, nyquist * 0.98f));
            return 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / safeRate);
        }

        internal static float SanitizeAndClamp(float sample, out bool nonFinite)
        {
            nonFinite = float.IsNaN(sample) || float.IsInfinity(sample);
            if (float.IsNaN(sample))
            {
                return 0f;
            }
            if (sample > 1f)
            {
                return 1f;
            }
            if (sample < -1f)
            {
                return -1f;
            }
            return sample;
        }
    }

    internal struct AudioPeakMeasurement
    {
        internal float PreClampSamplePeak;
        internal float PostClampSamplePeak;
        internal int PreClampOverCount;
        internal int PostClampOverCount;
        internal int NonFiniteCount;

        internal void Add(float preClamp, float postClamp)
        {
            bool preFinite = !(float.IsNaN(preClamp) || float.IsInfinity(preClamp));
            bool postFinite = !(float.IsNaN(postClamp) || float.IsInfinity(postClamp));
            if (!preFinite || !postFinite)
            {
                NonFiniteCount++;
            }
            if (preFinite)
            {
                float absolute = Math.Abs(preClamp);
                PreClampSamplePeak = Math.Max(PreClampSamplePeak, absolute);
                if (absolute > 1f) PreClampOverCount++;
            }
            if (postFinite)
            {
                float absolute = Math.Abs(postClamp);
                PostClampSamplePeak = Math.Max(PostClampSamplePeak, absolute);
                if (absolute > 1f) PostClampOverCount++;
            }
        }
    }

    internal struct AudioPeakWindow
    {
        private int _targetFrames;
        internal int Frames { get; private set; }
        internal AudioPeakMeasurement Measurement;
        internal bool Complete => _targetFrames > 0 && Frames >= _targetFrames;

        internal void Reset(int targetFrames)
        {
            _targetFrames = Math.Max(1, targetFrames);
            Frames = 0;
            Measurement = default;
        }

        internal void AddFrame(
            float preLeft,
            float postLeft,
            float preRight,
            float postRight,
            int channelsMeasured = 2)
        {
            if (Complete) return;
            Measurement.Add(preLeft, postLeft);
            if (channelsMeasured > 1)
            {
                Measurement.Add(preRight, postRight);
            }
            Frames++;
        }
    }
}
