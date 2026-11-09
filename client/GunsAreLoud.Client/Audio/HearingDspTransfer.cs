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
        internal float Tinnitus => _tinnitus;

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
            return Process(dry, targetWet, targetGain, targetAlpha, targetTinnitus,
                ringSample, smoothing, limited: false, preClamp: out preClamp);
        }

        /// <param name="limited">True when a limiter runs after this stage and
        /// owns the ceiling. The sample is then only sanitised, never cut, so the
        /// limiter sees the real peak instead of a squared-off one.</param>
        internal float Process(
            float dry,
            float targetWet,
            float targetGain,
            float targetAlpha,
            float targetTinnitus,
            float ringSample,
            float smoothing,
            bool limited,
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
            float output = limited
                ? HearingDspTransfer.Sanitize(preClamp, out bool nonFinite)
                : HearingDspTransfer.SanitizeAndClamp(preClamp, out nonFinite);
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

        /// <summary>
        /// Replaces what is not a number, and nothing else. The stage that owns
        /// the ceiling gets the true peak; cutting here first would hand it a
        /// signal that is already squared off.
        /// </summary>
        internal static float Sanitize(float sample, out bool nonFinite)
        {
            nonFinite = float.IsNaN(sample) || float.IsInfinity(sample);
            if (float.IsNaN(sample)) return 0f;
            if (float.IsPositiveInfinity(sample)) return 1f;
            if (float.IsNegativeInfinity(sample)) return -1f;
            return sample;
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

    /// <summary>
    /// Stereo-linked peak limiter on the master buffer — the last stage before
    /// the output, and the one that owns the ceiling.
    /// <para>
    /// It replaces the hard cut that used to square off the top of a close shot.
    /// Above <see cref="Knee"/> the transfer bends smoothly towards
    /// <see cref="Ceiling"/> instead of stopping at it, and the gain the loudest
    /// frame needs is taken at once and given back slowly, so a whole report is
    /// turned down rather than distorted. The reduction a frame needs is applied
    /// on that same frame, so no sample can leave above the ceiling.
    /// </para>
    /// Below the knee it does nothing at all: two absolute values and a
    /// comparison per frame, the gain stays exactly 1, and the buffer is not
    /// written. Ordinary game audio therefore passes through bit-exact, and so
    /// does every shot heard through a headset, where the protection stage has
    /// already taken the level down.
    /// </summary>
    internal struct MasterLimiterState
    {
        internal const float Ceiling = 0.999f;
        internal const float Knee = 0.95f;
        /// <summary>Safety mode: a whole report is turned down and the gain given back over this time.</summary>
        internal const double SafetyReleaseSeconds = 0.2;
        /// <summary>
        /// Program mode: the gain reaches the frame that needs it along a ramp
        /// this long, so a crack is rounded rather than stepped, and comes back
        /// within the crack's own length, so neither the body of the shot nor
        /// the world under it is held down.
        /// </summary>
        internal const double ProgramLookaheadSeconds = 0.002;
        internal const double ProgramReleaseSeconds = 0.01;
        // An exponential release approaches its target asymptotically, and near
        // unity the step it adds falls under a float's resolution — the gain then
        // stops advancing a hair short and every buffer for the rest of the
        // session is multiplied by something that is not quite one. A floor on
        // the step lands it exactly, whatever the sample rate.
        private const float MinimumReleaseStep = 1e-5f;

        private float _gain;
        private float _release;
        private int _rate;
        private bool _initialized;
        private bool _program;
        // Program mode only: the audio waits one lookahead for its gain. The
        // promised gain of every waiting frame sits in a ring one slot longer
        // than the delay; the frame leaving takes its slot's promise with it.
        private int _lookahead;
        private int _channels;
        private float[] _delay;
        private float[] _pending;
        private int _slot;
        private int _delaySlot;

        internal float Gain => _initialized ? _gain : 1f;
        internal bool Program => _program;
        /// <summary>How many frames later a sample leaves than it arrived: the lookahead in program mode, none in safety mode.</summary>
        internal int DelayFrames => _program ? _lookahead : 0;

        internal void Reset()
        {
            _gain = 1f;
            _initialized = true;
            if (_pending != null) for (int index = 0; index < _pending.Length; index++) _pending[index] = 1f;
        }

        /// <summary>
        /// Switches between the safety limiter and the program limiter. The
        /// switch drops the carried gain and the waiting audio: one lookahead of
        /// silence, at a settings change only.
        /// </summary>
        internal void SetProgram(bool program)
        {
            if (_program == program && _initialized) return;
            _program = program;
            _rate = 0;
            Reset();
            if (_delay != null) Array.Clear(_delay, 0, _delay.Length);
            _slot = 0;
            _delaySlot = 0;
        }

        /// <summary>
        /// Limits one buffer in place. Returns how many frames needed reduction;
        /// zero means not one sample of the buffer was touched.
        /// </summary>
        internal int Process(
            float[] data, int channels, int sampleRate,
            out float inputPeak, out float deepestGain)
        {
            inputPeak = 0f;
            deepestGain = 1f;
            if (data == null || channels < 1) return 0;
            Configure(sampleRate, channels);
            return _program
                ? ProcessProgram(data, channels, out inputPeak, out deepestGain)
                : ProcessSafety(data, channels, out inputPeak, out deepestGain);
        }

        private int ProcessSafety(float[] data, int channels, out float inputPeak, out float deepestGain)
        {
            inputPeak = 0f;
            deepestGain = 1f;
            int engaged = 0;
            float gain = _gain;
            for (int frame = 0; frame + channels <= data.Length; frame += channels)
            {
                float left = data[frame];
                float right = channels > 1 ? data[frame + 1] : left;
                float magnitude = Math.Abs(left);
                float other = Math.Abs(right);
                if (other > magnitude) magnitude = other;
                if (float.IsNaN(magnitude) || float.IsInfinity(magnitude))
                {
                    for (int channel = 0; channel < channels; channel++)
                        data[frame + channel] = HearingDspTransfer.SanitizeAndClamp(
                            data[frame + channel], out _);
                    engaged++;
                    continue;
                }
                if (magnitude > inputPeak) inputPeak = magnitude;

                float needed = magnitude > Knee ? SoftGain(magnitude) : 1f;
                if (needed < gain)
                {
                    gain = needed;                                      // attack: at once
                }
                else if (gain < needed)
                {
                    float step = (needed - gain) * _release;            // release: slowly
                    if (step < MinimumReleaseStep) step = MinimumReleaseStep;
                    gain += step;
                    if (gain > needed) gain = needed;
                }
                if (gain >= 1f)
                {
                    gain = 1f;
                    continue;
                }

                engaged++;
                if (gain < deepestGain) deepestGain = gain;
                for (int channel = 0; channel < channels; channel++)
                    data[frame + channel] *= gain;
            }
            _gain = gain;
            return engaged;
        }

        /// <summary>
        /// Program mode. Every arriving frame promises a gain to itself and,
        /// along a linear ramp, to the frames of the lookahead before it; the
        /// frame that leaves (one lookahead old) takes the lowest promise made
        /// to it. The attack is therefore a ramp that lands exactly on the
        /// frame that needs it, the release is the program release, and no
        /// sample leaves above the ceiling.
        /// </summary>
        private int ProcessProgram(float[] data, int channels, out float inputPeak, out float deepestGain)
        {
            inputPeak = 0f;
            deepestGain = 1f;
            int engaged = 0;
            float gain = _gain;
            int lookahead = _lookahead;
            int slots = lookahead + 1;
            float[] pending = _pending;
            float[] delay = _delay;
            int slot = _slot;
            int delaySlot = _delaySlot;
            for (int frame = 0; frame + channels <= data.Length; frame += channels)
            {
                float magnitude = 0f;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = data[frame + channel];
                    if (float.IsNaN(value) || float.IsInfinity(value))
                    {
                        value = HearingDspTransfer.SanitizeAndClamp(value, out _);
                        data[frame + channel] = value;
                    }
                    if (channel < 2)
                    {
                        float other = Math.Abs(value);
                        if (other > magnitude) magnitude = other;
                    }
                }
                if (magnitude > inputPeak) inputPeak = magnitude;
                if (magnitude > Knee)
                {
                    float needed = SoftGain(magnitude);
                    for (int back = 0; back <= lookahead; back++)
                    {
                        int promised = slot - back;
                        if (promised < 0) promised += slots;
                        float ramp = 1f - (1f - needed) * (1f - back / (float)slots);
                        if (ramp < pending[promised]) pending[promised] = ramp;
                    }
                }
                // The frame leaving now is one lookahead old: its slot is the one
                // the next arriving frame will take.
                int leaving = slot + 1 == slots ? 0 : slot + 1;
                float target = pending[leaving];
                pending[leaving] = 1f;
                if (target < gain)
                {
                    gain = target;                                      // attack: along the ramp
                }
                else if (gain < target)
                {
                    float step = (target - gain) * _release;            // release: within a crack
                    if (step < MinimumReleaseStep) step = MinimumReleaseStep;
                    gain += step;
                    if (gain > target) gain = target;
                }
                if (gain >= 1f) gain = 1f;
                else
                {
                    engaged++;
                    if (gain < deepestGain) deepestGain = gain;
                }
                if (lookahead > 0)
                {
                    int waiting = delaySlot * channels;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        float arrived = data[frame + channel];
                        float old = delay[waiting + channel];
                        delay[waiting + channel] = arrived;
                        data[frame + channel] = gain < 1f ? old * gain : old;
                    }
                    delaySlot = delaySlot + 1 == lookahead ? 0 : delaySlot + 1;
                }
                else if (gain < 1f)
                {
                    for (int channel = 0; channel < channels; channel++) data[frame + channel] *= gain;
                }
                slot = leaving;
            }
            _gain = gain;
            _slot = slot;
            _delaySlot = delaySlot;
            return engaged;
        }

        /// <summary>
        /// The gain that puts a frame of this magnitude on the soft curve. It is
        /// 1 exactly at the knee and never lets the result reach the ceiling, so
        /// the transfer is continuous and the output is bounded by construction.
        /// </summary>
        private static float SoftGain(float magnitude)
        {
            const float Span = Ceiling - Knee;
            float above = (magnitude - Knee) / Span;
            return (Knee + Span * (above / (1f + above))) / magnitude;
        }

        private void Configure(int sampleRate, int channels)
        {
            int rate = Math.Max(8000, sampleRate);
            if (!_initialized) Reset();
            if (rate == _rate && (!_program || channels == _channels)) return;
            bool rateChanged = rate != _rate;
            _rate = rate;
            _release = (float)(1.0 - Math.Exp(-1.0 / (rate * (_program ? ProgramReleaseSeconds : SafetyReleaseSeconds))));
            if (!_program) return;
            // A new rate or channel count: the rings are rebuilt and the waiting
            // audio is dropped; either change already means a glitch of its own.
            _lookahead = (int)(rate * ProgramLookaheadSeconds);
            _channels = channels;
            if (rateChanged || _pending == null || _pending.Length != _lookahead + 1)
            {
                _pending = new float[_lookahead + 1];
                for (int index = 0; index < _pending.Length; index++) _pending[index] = 1f;
            }
            int delayLength = _lookahead * channels;
            if (_delay == null || _delay.Length != delayLength) _delay = new float[delayLength];
            else Array.Clear(_delay, 0, _delay.Length);
            _slot = 0;
            _delaySlot = 0;
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
        internal int TargetFrames => _targetFrames;
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
