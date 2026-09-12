using System;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct PitchedEnvelopeTelemetry
    {
        internal readonly int CallbackCount;
        internal readonly float InputPeak;
        internal readonly float OutputPeak;
        internal readonly bool OnsetTriggered;
        internal readonly float AttackRms, TailRms;
        internal readonly int AttackFrames, TailFrames;
        internal readonly float BassAttackRms, TextureAttackRms;

        internal PitchedEnvelopeTelemetry(
            int callbackCount,
            float inputPeak,
            float outputPeak,
            bool onsetTriggered,
            float attackRms = 0, float tailRms = 0,
            int attackFrames = 0, int tailFrames = 0,
            float bassAttackRms = 0, float textureAttackRms = 0)
        {
            CallbackCount = callbackCount;
            InputPeak = inputPeak;
            OutputPeak = outputPeak;
            OnsetTriggered = onsetTriggered;
            AttackRms = attackRms; TailRms = tailRms;
            AttackFrames = attackFrames; TailFrames = tailFrames;
            BassAttackRms = bassAttackRms; TextureAttackRms = textureAttackRms;
        }
    }

    /// <summary>
    /// A cosine sampled at a fixed angular step, advanced by the two-term
    /// recurrence instead of a call per sample. It is re-seeded from the exact
    /// value whenever the frame it holds is not the frame being asked for, so it
    /// cannot drift across buffers or survive a voice being re-scheduled.
    /// </summary>
    internal struct CosineRamp
    {
        private int _nextFrame;
        private float _cos, _sin, _stepCos, _stepSin;
        private bool _seeded;

        /// <summary>Forces the next value to come from the exact formula.</summary>
        internal void Invalidate() => _seeded = false;

        internal float Cosine(int frame, double angle, double step)
        {
            if (!_seeded || _nextFrame != frame)
            {
                _cos = (float)Math.Cos(angle);
                _sin = (float)Math.Sin(angle);
                _stepCos = (float)Math.Cos(step);
                _stepSin = (float)Math.Sin(step);
                _nextFrame = frame + 1;
                _seeded = true;
                return _cos;
            }

            float cos = _cos * _stepCos - _sin * _stepSin;
            float sin = _sin * _stepCos + _cos * _stepSin;
            _cos = cos;
            _sin = sin;
            _nextFrame = frame + 1;
            return cos;
        }
    }

    /// <summary>
    /// The envelope's three sections expressed in frames: attack, a flat body and
    /// the fade. Only the attack and the fade need a shape, and both are a quarter
    /// cosine, so the body costs nothing at all.
    /// </summary>
    internal readonly struct EnvelopeSections
    {
        private readonly int _sampleRate;
        private readonly float _attackEndFrames, _fadeStartFrames, _endFrames;
        private readonly float _attackDuration, _fadeStart, _fadeDuration;

        private EnvelopeSections(int sampleRate, float attackDuration, float fadeStart,
            float fadeDuration, float duration)
        {
            _sampleRate = sampleRate;
            _attackDuration = attackDuration;
            _fadeStart = fadeStart;
            _fadeDuration = fadeDuration;
            _attackEndFrames = attackDuration * sampleRate;
            _fadeStartFrames = fadeStart * sampleRate;
            _endFrames = duration * sampleRate;
        }

        internal static EnvelopeSections Create(float durationSeconds, float fadePercent, int sampleRate)
        {
            float duration = Mathf.Clamp(durationSeconds, 0.01f, 10f);
            float attackDuration = Mathf.Min(0.002f, duration * 0.2f);
            float requestedFade = duration * Mathf.Clamp(fadePercent, 5f, 100f) * 0.01f;
            float fadeDuration = Mathf.Min(requestedFade, duration - attackDuration);
            return new EnvelopeSections(Math.Max(8000, sampleRate), attackDuration,
                duration - fadeDuration, fadeDuration, duration);
        }

        internal float Evaluate(int frame, ref CosineRamp fade)
        {
            if (frame < 0 || _endFrames <= 0f || _fadeDuration < 0f) return 0f;
            if (frame >= _endFrames) return 0f;
            if (frame < _attackEndFrames)
            {
                fade.Invalidate();
                return Mathf.Sin(frame / (float)_sampleRate / _attackDuration * Mathf.PI * 0.5f);
            }
            if (frame <= _fadeStartFrames)
            {
                fade.Invalidate();
                return 1f;
            }
            // The seed repeats the original expression exactly, including its
            // float division, so a re-seeded frame is bit-comparable.
            float time = frame / (float)_sampleRate;
            float progress = Mathf.Clamp01((time - _fadeStart) / _fadeDuration);
            return fade.Cosine(frame, progress * Math.PI * 0.5,
                Math.PI * 0.5 / (_fadeDuration * (double)_sampleRate));
        }
    }

    /// <summary>
    /// Turns any donor clip into one short transient. This prevents an automatic
    /// weapon bank containing a recorded burst from replaying the entire burst.
    /// </summary>
    internal sealed class PitchedGunshotEnvelopeFilter : MonoBehaviour
    {
        private CosineRamp _fadeRamp;

        private const float OnsetThreshold = 0.004f;

        private volatile float _durationSeconds = 0.1f;
        private volatile float _fadePercent = 35f;
        private volatile float _gainLinear = 1f;
        private volatile float _bodyCalibrationGain = 1f;
        private volatile float _decayCalibrationGain = 1f;
        private volatile float _decayStartSeconds = LowEndLevelModel.WindowSeconds;
        private volatile bool _startImmediately;
        private int _generation;
        private int _audioGeneration = -1;
        private bool _armed;
        private bool _active;
        private int _frame;
        private int _armedFrames;
        private int _completed = 1;
        private int _callbackCount;
        private volatile float _inputPeak;
        private volatile float _outputPeak;
        private int _onsetTriggered;
        private int _sampleRate = 48000;
        private volatile bool _measureLevels;
        private PitchedLevelMeter _levelMeter;
        private volatile float _attackRms, _tailRms;
        private int _attackFrames, _tailFrames;
        private volatile float _headphoneTailDbPerSecond;
        private HeadphoneTailEnvelope _headphoneTail;
        private BassAttackMeter _requestedBandMeter, _bandMeter;
        private volatile float _bassAttackRms, _textureAttackRms;
        // Early release, requested by the main thread when a later round of the
        // same burst takes over. Tagged with the voice generation, so a request
        // aimed at a finished copy can never shorten the voice that reuses it.
        private int _releaseRequest;
        private int _audioReleaseRequest;
        private volatile int _releaseGeneration;
        private volatile float _releaseStartSeconds;
        private volatile float _releaseFadeSeconds;
        private bool _releasing;
        private int _releaseStartFrame;
        private int _releaseFadeFrames;

        internal bool Completed => Volatile.Read(ref _completed) != 0;

        internal PitchedEnvelopeTelemetry GetTelemetry()
        {
            return new PitchedEnvelopeTelemetry(
                Volatile.Read(ref _callbackCount),
                _inputPeak,
                _outputPeak,
                Volatile.Read(ref _onsetTriggered) != 0,
                _attackRms, _tailRms, Volatile.Read(ref _attackFrames), Volatile.Read(ref _tailFrames),
                _bassAttackRms, _textureAttackRms);
        }

        internal void Configure(
            float durationSeconds,
            float fadePercent,
            float gainLinear,
            bool startImmediately = false,
            int sampleRate = 48000,
            bool measureLevels = false,
            float headphoneTailDbPerSecond = 0f,
            float bodyCalibrationGain = 1f,
            float decayCalibrationGain = 1f,
            float decayStartSeconds = LowEndLevelModel.WindowSeconds)
        {
            _durationSeconds = Mathf.Clamp(durationSeconds, 0.01f, 10f);
            _fadePercent = Mathf.Clamp(fadePercent, 5f, 100f);
            // User gain (0..30 dB) times bounded normalization (-12..+12 dB).
            // A floor of 1 would silently disable attenuation at low user gain.
            _gainLinear = ClampPostFilterGain(gainLinear);
            _bodyCalibrationGain = ClampCalibrationGain(bodyCalibrationGain);
            _decayCalibrationGain = ClampCalibrationGain(decayCalibrationGain);
            _decayStartSeconds = Math.Max(LowEndLevelModel.WindowSeconds, decayStartSeconds);
            _startImmediately = startImmediately;
            _sampleRate = Math.Max(8000, sampleRate);
            _measureLevels = measureLevels;
            Volatile.Write(ref _requestedBandMeter, measureLevels ? new BassAttackMeter(_sampleRate) : null);
            _bassAttackRms = _textureAttackRms = 0;
            _headphoneTailDbPerSecond = headphoneTailDbPerSecond;
            _attackRms = _tailRms = 0;
            Volatile.Write(ref _attackFrames, 0);
            Volatile.Write(ref _tailFrames, 0);
            Volatile.Write(ref _callbackCount, 0);
            _inputPeak = 0f;
            _outputPeak = 0f;
            Volatile.Write(ref _onsetTriggered, 0);
            Volatile.Write(ref _completed, 0);
            Interlocked.Increment(ref _generation);
        }

        internal void Bypass()
        {
            Volatile.Write(ref _completed, 1);
            Interlocked.Increment(ref _generation);
        }

        /// <summary>
        /// Fades the current copy out from <paramref name="startSeconds"/> after
        /// its onset over <paramref name="fadeSeconds"/>, then completes it. Main
        /// thread only; the audio thread latches the request at its next callback
        /// and never begins the fade earlier than the frame it is rendering.
        /// </summary>
        internal void ScheduleRelease(float startSeconds, float fadeSeconds)
        {
            _releaseStartSeconds = Math.Max(0f, startSeconds);
            _releaseFadeSeconds = Math.Max(0.001f, fadeSeconds);
            _releaseGeneration = Volatile.Read(ref _generation);
            Interlocked.Increment(ref _releaseRequest);
        }

        internal static float ClampPostFilterGain(float gain) => Mathf.Clamp(gain, 0f, 125.89255f);

        // Same equal-power shape as the envelope's own fade, so an early release
        // sounds like a shorter copy rather than a gate.
        internal static float CalculateReleaseGain(int elapsedFrames, int fadeFrames)
        {
            if (fadeFrames <= 0 || elapsedFrames >= fadeFrames) return 0f;
            if (elapsedFrames <= 0) return 1f;
            return Mathf.Cos(elapsedFrames / (float)fadeFrames * Mathf.PI * 0.5f);
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            long trace = AudioFilterTrace.Begin();
            if (data == null || channels <= 0)
            {
                AudioFilterTrace.Record(
                    AudioFilterKind.PitchedEnvelope, trace, 0, channels, idle: true);
                return;
            }

            int generation = Volatile.Read(ref _generation);
            if (_audioGeneration != generation)
            {
                _audioGeneration = generation;
                _armed = !_startImmediately;
                _active = _startImmediately;
                _frame = 0;
                _armedFrames = 0;
                _releasing = false;
                _levelMeter.Reset(_sampleRate);
                _bandMeter = Volatile.Read(ref _requestedBandMeter);
                _headphoneTail.Reset(_sampleRate, _headphoneTailDbPerSecond);
                if (_startImmediately)
                {
                    Volatile.Write(ref _onsetTriggered, 1);
                }
            }

            int releaseRequest = Volatile.Read(ref _releaseRequest);
            if (_audioReleaseRequest != releaseRequest)
            {
                _audioReleaseRequest = releaseRequest;
                // A request tagged with an earlier generation belonged to the copy
                // this pooled voice played before; drop it rather than apply it.
                if (_releaseGeneration == generation)
                {
                    int releaseRate = Math.Max(8000, _sampleRate);
                    _releaseStartFrame = Math.Max(_frame,
                        (int)Math.Round(_releaseStartSeconds * releaseRate));
                    _releaseFadeFrames = Math.Max(1,
                        (int)Math.Round(_releaseFadeSeconds * releaseRate));
                    _releasing = true;
                }
            }

            if (Completed)
            {
                Array.Clear(data, 0, data.Length);
                // A finished copy still receives every buffer until its pooled
                // voice is stopped: that is exactly the cost to watch here.
                AudioFilterTrace.Record(
                    AudioFilterKind.PitchedEnvelope, trace, data.Length, channels, idle: true);
                return;
            }

            Interlocked.Increment(ref _callbackCount);
            _inputPeak = Mathf.Max(_inputPeak, BufferPeak(data));

            int sampleRate = _sampleRate;
            int frameCount = data.Length / channels;
            // Read the volatile configuration once per buffer and turn the
            // envelope's section boundaries into frame counts. Inside a section
            // the shape is either a constant or a cosine that advances by a fixed
            // angle, so neither needs a transcendental per sample.
            float duration = _durationSeconds;
            float fadePercent = _fadePercent;
            float gainLinear = _gainLinear;
            float decayStartSeconds = _decayStartSeconds;
            float bodyCalibration = _bodyCalibrationGain;
            float decayCalibration = _decayCalibrationGain;
            bool measureLevels = _measureLevels;
            EnvelopeSections sections = EnvelopeSections.Create(duration, fadePercent, sampleRate);
            // One exact value per buffer bounds the recurrence to this buffer's
            // frames, so a seconds-long fade cannot accumulate drift.
            _fadeRamp.Invalidate();
            for (int frame = 0; frame < frameCount; frame++)
            {
                int frameOffset = frame * channels;
                if (_armed)
                {
                    if (FramePeak(data, frameOffset, channels) > OnsetThreshold)
                    {
                        _armed = false;
                        _active = true;
                        _frame = 0;
                        Volatile.Write(ref _onsetTriggered, 1);
                    }
                    else
                    {
                        ClearFrame(data, frameOffset, channels);
                        if (++_armedFrames >= sampleRate / 4)
                        {
                            CompleteAndClear(data, frameOffset + channels);
                            RecordOutputPeak(data);
                            AudioFilterTrace.Record(
                                AudioFilterKind.PitchedEnvelope, trace, data.Length, channels);
                            return;
                        }
                        continue;
                    }
                }

                float envelope = sections.Evaluate(_frame, ref _fadeRamp);
                if (!_active || (envelope <= 0f && _frame > 0))
                {
                    CompleteAndClear(data, frameOffset);
                    RecordOutputPeak(data);
                    AudioFilterTrace.Record(
                        AudioFilterKind.PitchedEnvelope, trace, data.Length, channels);
                    return;
                }

                float releaseGain = 1f;
                if (_releasing && _frame >= _releaseStartFrame)
                {
                    int elapsed = _frame - _releaseStartFrame;
                    if (elapsed >= _releaseFadeFrames)
                    {
                        CompleteAndClear(data, frameOffset);
                        RecordOutputPeak(data);
                        AudioFilterTrace.Record(
                            AudioFilterKind.PitchedEnvelope, trace, data.Length, channels);
                        return;
                    }
                    releaseGain = CalculateReleaseGain(elapsed, _releaseFadeFrames);
                }

                // Constant on both sides of a thirty-millisecond transition; only
                // inside it is the raised cosine worth evaluating.
                float calibrationGain = bodyCalibration == decayCalibration
                    ? bodyCalibration
                    : CalculateCalibrationGain(_frame, sampleRate,
                        decayStartSeconds, bodyCalibration, decayCalibration);
                float outputGain = envelope * gainLinear * calibrationGain *
                    _headphoneTail.Next() * releaseGain;
                for (int channel = 0; channel < channels; channel++)
                {
                    data[frameOffset + channel] *= outputGain;
                }
                if (measureLevels)
                {
                    _levelMeter.AddFrame(data, frameOffset, channels);
                    _bandMeter?.AddFrame(data, frameOffset, channels);
                }
                _frame++;
            }

            RecordOutputPeak(data);
            AudioFilterTrace.Record(
                AudioFilterKind.PitchedEnvelope, trace, data.Length, channels);
        }

        internal static float CalculateEnvelope(
            int frame,
            int sampleRate,
            float durationSeconds,
            float fadePercent)
        {
            if (frame < 0 || sampleRate <= 0 || durationSeconds <= 0f || fadePercent <= 0f)
            {
                return 0f;
            }

            float duration = Mathf.Clamp(durationSeconds, 0.01f, 10f);
            float time = frame / (float)sampleRate;
            if (time >= duration)
            {
                return 0f;
            }

            float attackDuration = Mathf.Min(0.002f, duration * 0.2f);
            if (time < attackDuration)
            {
                return Mathf.Sin(time / attackDuration * Mathf.PI * 0.5f);
            }

            float requestedFade = duration * Mathf.Clamp(fadePercent, 5f, 100f) * 0.01f;
            float fadeDuration = Mathf.Min(requestedFade, duration - attackDuration);
            float fadeStart = duration - fadeDuration;
            if (time <= fadeStart)
            {
                return 1f;
            }

            float progress = Mathf.Clamp01((time - fadeStart) / fadeDuration);
            return Mathf.Cos(progress * Mathf.PI * 0.5f);
        }

        internal static float ClampCalibrationGain(float gain)
        {
            if (float.IsNaN(gain) || float.IsInfinity(gain)) return 1f;
            return Math.Max(0.25118864f, Math.Min(3.9810717f, gain));
        }

        internal static float CalculateCalibrationGain(
            int frame, int sampleRate, float decayStartSeconds, float bodyGain, float decayGain)
        {
            if (frame < 0 || sampleRate <= 0) return bodyGain;
            double time = frame / (double)sampleRate;
            double start = Math.Max(LowEndLevelModel.WindowSeconds, decayStartSeconds);
            const double transition = 0.03;
            if (time <= start) return bodyGain;
            if (time >= start + transition) return decayGain;
            double progress = (time - start) / transition;
            double weight = 0.5 - 0.5 * Math.Cos(Math.PI * progress);
            return (float)(bodyGain + (decayGain - bodyGain) * weight);
        }

        private void CompleteAndClear(float[] data, int fromIndex)
        {
            _active = false;
            _armed = false;
            Volatile.Write(ref _completed, 1);
            if (fromIndex < data.Length)
            {
                Array.Clear(data, fromIndex, data.Length - fromIndex);
            }
        }

        private static float FramePeak(float[] data, int frameOffset, int channels)
        {
            float peak = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(data[frameOffset + channel]));
            }
            return peak;
        }

        private void RecordOutputPeak(float[] data)
        {
            _outputPeak = Mathf.Max(_outputPeak, BufferPeak(data));
            if (_measureLevels)
            {
                _attackRms = _levelMeter.AttackRms;
                _tailRms = _levelMeter.TailRms;
                _bassAttackRms = _bandMeter?.BassRms ?? 0;
                _textureAttackRms = _bandMeter?.TextureRms ?? 0;
                Volatile.Write(ref _attackFrames, _levelMeter.AttackFrames);
                Volatile.Write(ref _tailFrames, _levelMeter.TailFrames);
            }
        }

        private static float BufferPeak(float[] data)
        {
            float peak = 0f;
            for (int index = 0; index < data.Length; index++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(data[index]));
            }
            return peak;
        }

        private static void ClearFrame(float[] data, int frameOffset, int channels)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                data[frameOffset + channel] = 0f;
            }
        }
    }
}
