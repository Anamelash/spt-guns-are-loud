using System;
using System.Threading;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct GunshotFilterTelemetry
    {
        internal readonly int CallbackCount;
        internal readonly float InputPeak;
        internal readonly float AddedPeak;

        internal GunshotFilterTelemetry(int callbackCount, float inputPeak, float addedPeak)
        {
            CallbackCount = callbackCount;
            InputPeak = inputPeak;
            AddedPeak = addedPeak;
        }
    }

    /// <summary>
    /// Adds a short, low-frequency parallel band derived from the weapon clip itself.
    /// It deliberately does not synthesize an oscillator: pitch and transient shape
    /// remain those of EFT's original recording.
    /// </summary>
    internal sealed class LocalGunshotImpactFilter : MonoBehaviour
    {
        private const float LowerCutoffHz = 38f;
        private const float OnsetThreshold = 0.006f;
        private const float BodyDurationSeconds = AutomaticLowBandProcessor.BodyDurationSeconds;
        private readonly AutomaticLowBandProcessor _processor = new AutomaticLowBandProcessor();

        private volatile bool _processingEnabled;
        private volatile float _bodyBandGain;
        private volatile float _upperCoefficient = 0.02f;
        private volatile float _lowerCoefficient = 0.005f;
        private volatile int _sampleRate = 48000;
        private bool _armed;
        private int _armedFrames;
        private int _armRequestSequence;
        private int _audioArmRequestSequence;
        private int _requestedArm;
        private volatile bool _needsStreamReset = true;
        internal bool NeedsStreamReset => _needsStreamReset;
        private double _expiresAt;

        private int _callbackCount;
        private float _lastInputPeak;
        private float _lastAddedPeak;
        private volatile float _headphoneTailDbPerSecond;

        internal void Configure(float bodyGain, float pressureFrequencyHz, int sampleRate,
            float headphoneBodyGain = 1f, float headphoneTailDbPerSecond = 0f,
            bool armOnset = true)
        {
            int rate = Mathf.Max(8000, sampleRate);
            _expiresAt = 0;
            float upperCutoff = CalculateBodyUpperCutoff(pressureFrequencyHz);

            _bodyBandGain = CalculateBodyBandGain(bodyGain, pressureFrequencyHz) * headphoneBodyGain;
            _headphoneTailDbPerSecond = headphoneTailDbPerSecond;
            _sampleRate = rate;
            _upperCoefficient = CalculateLowpassCoefficient(rate, upperCutoff);
            _lowerCoefficient = CalculateLowpassCoefficient(rate, LowerCutoffHz);
            _processingEnabled = _bodyBandGain > 0.001f;
            if (_processingEnabled)
                _processor.Configure(rate, _upperCoefficient, _lowerCoefficient);
            else
                Bypass();

            Interlocked.Exchange(ref _callbackCount, 0);
            Volatile.Write(ref _lastInputPeak, 0f);
            Volatile.Write(ref _lastAddedPeak, 0f);
            Volatile.Write(ref _requestedArm, _processingEnabled && armOnset ? 1 : 0);
            Interlocked.Increment(ref _armRequestSequence);
        }

        internal bool Trigger(double scheduledDspTime)
        {
            if (!_processingEnabled)
            {
                return false;
            }

            Volatile.Write(ref _requestedArm, 0);
            Interlocked.Increment(ref _armRequestSequence);
            return _processor.Trigger(
                scheduledDspTime,
                _bodyBandGain,
                _headphoneTailDbPerSecond);
        }

        internal void BeginStream(double scheduledStart)
        {
            _processor.RequestReset(scheduledStart, _processingEnabled);
            if (_processingEnabled)
                _processor.Configure(_sampleRate, _upperCoefficient, _lowerCoefficient);
            _needsStreamReset = false;
        }

        internal void Bypass()
        {
            _expiresAt = 0;
            _processingEnabled = false;
            _bodyBandGain = 0f;
            Volatile.Write(ref _requestedArm, 0);
            Interlocked.Increment(ref _armRequestSequence);
            _processor.Disable();
            _needsStreamReset = true;
        }

        internal GunshotFilterTelemetry GetTelemetry()
        {
            return new GunshotFilterTelemetry(
                Volatile.Read(ref _callbackCount),
                Volatile.Read(ref _lastInputPeak),
                Volatile.Read(ref _lastAddedPeak));
        }

        private void OnDisable()
        {
            Bypass();
        }

        private void Update()
        {
            if (_processingEnabled && Plugin.ModConfig?.Enabled.Value != true)
                Bypass();
            else if (_expiresAt > 0)
                ExpireAt(AudioSettings.dspTime);
        }

        internal void FinishAfterCurrentEnvelopes(double now)
        {
            if (_processingEnabled && _expiresAt <= 0)
                _expiresAt = now + BodyDurationSeconds + 0.02;
        }

        internal void ExpireAt(double now)
        {
            if (_expiresAt > 0 && now >= _expiresAt) Bypass();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_processingEnabled || data == null || channels <= 0)
            {
                return;
            }

            Interlocked.Increment(ref _callbackCount);
            ConsumeArmRequest();
            int frameCount = data.Length / channels;
            int sampleRate = _sampleRate;
            float bodyBandGain = _bodyBandGain;
            float callbackInputPeak = 0f;
            float callbackAddedPeak = 0f;

            for (int frame = 0; frame < frameCount; frame++)
            {
                _processor.BeginFrame();
                int frameOffset = frame * channels;
                float inputPeak = FramePeak(data, frameOffset, channels);
                callbackInputPeak = Mathf.Max(callbackInputPeak, inputPeak);

                if (_armed)
                {
                    if (inputPeak > OnsetThreshold)
                    {
                        _armed = false;
                        _processor.TriggerCurrentFrame(
                            bodyBandGain,
                            _headphoneTailDbPerSecond);
                    }
                    else if (++_armedFrames >= sampleRate / 4)
                    {
                        _armed = false;
                    }
                }

                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleIndex = frameOffset + channel;
                    float sample = data[sampleIndex];
                    float addition = _processor.ProcessSample(sample, channel, channels);
                    callbackAddedPeak = Mathf.Max(callbackAddedPeak, Mathf.Abs(addition));
                    data[sampleIndex] = MixBounded(sample, addition);
                }
                _processor.EndFrame();
            }

            Volatile.Write(ref _lastInputPeak, callbackInputPeak);
            Volatile.Write(ref _lastAddedPeak, callbackAddedPeak);
        }

        private void ConsumeArmRequest()
        {
            int sequence = Volatile.Read(ref _armRequestSequence);
            if (_audioArmRequestSequence == sequence) return;
            _armed = Volatile.Read(ref _requestedArm) != 0;
            _armedFrames = 0;
            _audioArmRequestSequence = sequence;
        }

        internal static float CalculateBodyUpperCutoff(float pressureFrequencyHz)
        {
            return Mathf.Clamp(pressureFrequencyHz * 2.5f, 170f, 310f);
        }

        internal static float CalculateBodyBandGain(float bodyGain, float pressureFrequencyHz)
        {
            float caliberPosition = Mathf.InverseLerp(122f, 68f, pressureFrequencyHz);
            float caliberWeight = Mathf.Lerp(0.80f, 1.30f, caliberPosition);
            return Mathf.Clamp(bodyGain, 0f, 0.5f) * 3.2f * caliberWeight;
        }

        internal static float CalculateBodyEnvelope(int frame, int sampleRate)
        {
            if (frame < 0 || sampleRate <= 0)
            {
                return 0f;
            }

            float time = frame / (float)sampleRate;
            if (time >= BodyDurationSeconds)
            {
                return 0f;
            }

            float attack = 1f - (float)Math.Exp(-time * 850f);
            float release = time <= 0.090f
                ? 1f
                : Mathf.Clamp01((BodyDurationSeconds - time) / 0.030f);
            return attack * release * (float)Math.Exp(-time * 9f);
        }

        internal static float CalculateLowpassCoefficient(int sampleRate, float cutoffHz)
        {
            float rate = Mathf.Max(8000, sampleRate);
            float cutoff = Mathf.Clamp(cutoffHz, 20f, rate * 0.45f);
            return 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / rate);
        }

        internal static float MixBounded(float dry, float addition)
        {
            dry = Mathf.Clamp(dry, -1f, 1f);
            if (addition >= 0f)
            {
                return Mathf.Clamp(dry + addition * (1f - Mathf.Max(0f, dry)), -1f, 1f);
            }

            return Mathf.Clamp(dry + addition * (1f + Mathf.Min(0f, dry)), -1f, 1f);
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
    }
}
