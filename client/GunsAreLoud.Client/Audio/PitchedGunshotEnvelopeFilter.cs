using System;
using System.Threading;
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
    /// Turns any donor clip into one short transient. This prevents an automatic
    /// weapon bank containing a recorded burst from replaying the entire burst.
    /// </summary>
    internal sealed class PitchedGunshotEnvelopeFilter : MonoBehaviour
    {
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

        internal static float ClampPostFilterGain(float gain) => Mathf.Clamp(gain, 0f, 125.89255f);

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || channels <= 0)
            {
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
                _levelMeter.Reset(_sampleRate);
                _bandMeter = Volatile.Read(ref _requestedBandMeter);
                _headphoneTail.Reset(_sampleRate, _headphoneTailDbPerSecond);
                if (_startImmediately)
                {
                    Volatile.Write(ref _onsetTriggered, 1);
                }
            }

            if (Completed)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }

            Interlocked.Increment(ref _callbackCount);
            _inputPeak = Mathf.Max(_inputPeak, BufferPeak(data));

            int sampleRate = _sampleRate;
            int frameCount = data.Length / channels;
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
                            return;
                        }
                        continue;
                    }
                }

                float envelope = CalculateEnvelope(
                    _frame,
                    sampleRate,
                    _durationSeconds,
                    _fadePercent);
                if (!_active || (envelope <= 0f && _frame > 0))
                {
                    CompleteAndClear(data, frameOffset);
                    RecordOutputPeak(data);
                    return;
                }

                float calibrationGain = CalculateCalibrationGain(_frame, sampleRate,
                    _decayStartSeconds, _bodyCalibrationGain, _decayCalibrationGain);
                float outputGain = envelope * _gainLinear * calibrationGain * _headphoneTail.Next();
                for (int channel = 0; channel < channels; channel++)
                {
                    data[frameOffset + channel] *= outputGain;
                }
                if (_measureLevels)
                {
                    _levelMeter.AddFrame(data, frameOffset, channels);
                    _bandMeter?.AddFrame(data, frameOffset, channels);
                }
                _frame++;
            }

            RecordOutputPeak(data);
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
