using System;
using System.Threading;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct AutomaticPitchedTelemetry
    {
        internal readonly AutomaticPitchedRoute Route;
        internal readonly int CallbackCount;
        internal readonly int OutputCallbackCount;
        internal readonly int TriggerCount;
        internal readonly int OnsetCount;
        internal readonly int ActiveVoices;
        internal readonly float InputPeak;
        internal readonly float AddedPeak;
        internal readonly float PitchRatio;
        internal readonly float SourceSpanMs;
        internal readonly float OutputDurationMs;

        internal AutomaticPitchedTelemetry(
            AutomaticPitchedRoute route,
            int callbackCount,
            int outputCallbackCount,
            int triggerCount,
            int onsetCount,
            int activeVoices,
            float inputPeak,
            float addedPeak,
            float pitchRatio,
            float sourceSpanMs,
            float outputDurationMs)
        {
            Route = route;
            CallbackCount = callbackCount;
            OutputCallbackCount = outputCallbackCount;
            TriggerCount = triggerCount;
            OnsetCount = onsetCount;
            ActiveVoices = activeVoices;
            InputPeak = inputPeak;
            AddedPeak = addedPeak;
            PitchRatio = pitchRatio;
            SourceSpanMs = sourceSpanMs;
            OutputDurationMs = outputDurationMs;
        }
    }

    /// <summary>
    /// Diagnostic comparator that captures and pitch-shifts the automatic body
    /// loop inside the donor callback. The production cached route is implemented
    /// by AutomaticBeatCapture and never enters this filter.
    /// </summary>
    internal sealed class AutomaticPitchedGunshotFilter : MonoBehaviour
    {
        private const int MaximumChannels = 2;
        private const int MaximumVoices = 4;
        // Enough history for the worst F12 pitch ratio to finish a 500 ms
        // source-domain span at common 48/96 kHz output rates without overwrite.
        private const int RingFrameCapacity = 262144;
        private const int DetectorWarmupFrames = 64;
        private const float OnsetThreshold = 0.012f;
        private const float OnsetRatio = 1.35f;
        private const float OnsetMargin = 0.003f;
        private const float PreRollSeconds = 0.006f;
        private const float MaximumArmSeconds = 0.25f;

        private readonly float[] _ring = new float[RingFrameCapacity * MaximumChannels];
        private readonly DspVoice[] _voices =
        {
            new DspVoice(),
            new DspVoice(),
            new DspVoice(),
            new DspVoice()
        };

        private volatile bool _processingEnabled;
        private volatile AutomaticPitchedRoute _route;
        private volatile int _sampleRate = 48000;
        private volatile float _pitchRatio = 0.5f;
        private volatile float _highpassCoefficient = 0.005f;
        private volatile float _lowpassCoefficient = 0.02f;
        private volatile float _sourceSpanSeconds = 0.1f;
        private volatile float _durationSeconds = 0.2f;
        private volatile float _fadePercent = 35f;
        private volatile float _gain = 1f;
        private volatile float _headphoneTailDbPerSecond;
        private int _generation;
        private int _triggerGeneration;
        private int _pendingTriggers;

        private int _audioGeneration = -1;
        private int _audioTriggerGeneration = -1;
        private long _absoluteFrame;
        private int _capturedChannels = 1;
        private int _detectorWarmup;
        private int _armedFrames;
        private float _detectorEnvelope;
        private int _nextVoice;

        private int _callbackCount;
        private int _outputCallbackCount;
        private int _triggerCount;
        private int _onsetCount;
        private int _activeVoiceCount;
        private volatile float _inputPeak;
        private volatile float _addedPeak;

        internal void Configure(
            AutomaticPitchedRoute route,
            float pitchRatio,
            float highpassHz,
            float lowpassHz,
            float sourceSpanSeconds,
            float fadePercent,
            float gain,
            int sampleRate,
            float headphoneTailDbPerSecond = 0f)
        {
            int rate = Mathf.Max(8000, sampleRate);
            float ratio = Mathf.Clamp(pitchRatio, 0.25f, 0.95f);
            float upper = Mathf.Clamp(lowpassHz, 80f, rate * 0.45f);
            float lower = Mathf.Clamp(highpassHz, 10f, Mathf.Max(10f, upper - 10f));
            float sourceSpan = Mathf.Clamp(sourceSpanSeconds, 0.01f, 0.5f);
            bool routeChanged = _route != route;

            _route = route;
            _sampleRate = rate;
            _pitchRatio = ratio;
            _highpassCoefficient = LocalGunshotImpactFilter.CalculateLowpassCoefficient(rate, lower);
            _lowpassCoefficient = LocalGunshotImpactFilter.CalculateLowpassCoefficient(rate, upper);
            _sourceSpanSeconds = sourceSpan;
            _durationSeconds = PitchedGunshotLayer.CalculatePitchedDuration(
                sourceSpan,
                ratio);
            _fadePercent = Mathf.Clamp(fadePercent, 5f, 100f);
            _gain = Mathf.Clamp(gain, 0f, 31.62278f);
            _headphoneTailDbPerSecond = headphoneTailDbPerSecond;

            if (!_processingEnabled || routeChanged)
            {
                _processingEnabled = true;
                Interlocked.Increment(ref _generation);
            }
        }

        internal void Trigger()
        {
            if (!_processingEnabled)
            {
                return;
            }

            Interlocked.Increment(ref _pendingTriggers);
            Interlocked.Increment(ref _triggerCount);
            Interlocked.Increment(ref _triggerGeneration);
            Interlocked.Exchange(ref _callbackCount, 0);
            Interlocked.Exchange(ref _outputCallbackCount, 0);
            Interlocked.Exchange(ref _onsetCount, 0);
            _inputPeak = 0f;
            _addedPeak = 0f;
        }

        internal void Bypass()
        {
            _processingEnabled = false;
            Interlocked.Exchange(ref _pendingTriggers, 0);
            Interlocked.Increment(ref _generation);
        }

        internal AutomaticPitchedTelemetry GetTelemetry()
        {
            return new AutomaticPitchedTelemetry(
                _route,
                Volatile.Read(ref _callbackCount),
                Volatile.Read(ref _outputCallbackCount),
                Volatile.Read(ref _triggerCount),
                Volatile.Read(ref _onsetCount),
                Volatile.Read(ref _activeVoiceCount),
                _inputPeak,
                _addedPeak,
                _pitchRatio,
                _sourceSpanSeconds * 1000f,
                _durationSeconds * 1000f);
        }

        internal static bool IsOnset(float previousEnvelope, float peak, int warmupFrames)
        {
            if (peak <= OnsetThreshold)
            {
                return false;
            }

            bool coldStart = warmupFrames < DetectorWarmupFrames &&
                previousEnvelope < OnsetThreshold * 0.25f;
            bool warmedRisingEdge = warmupFrames >= DetectorWarmupFrames &&
                peak > previousEnvelope * OnsetRatio + OnsetMargin;
            return coldStart || warmedRisingEdge;
        }

        internal static double AdvanceSourcePosition(
            double sourcePosition,
            float pitchRatio,
            int outputFrames)
        {
            return sourcePosition + Mathf.Clamp(pitchRatio, 0.25f, 0.95f) *
                Mathf.Max(0, outputFrames);
        }

        private void OnDisable()
        {
            Bypass();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_processingEnabled || data == null || channels <= 0)
            {
                return;
            }

            int generation = Volatile.Read(ref _generation);
            if (_audioGeneration != generation)
            {
                ResetAudioState();
                _audioGeneration = generation;
            }

            int triggerGeneration = Volatile.Read(ref _triggerGeneration);
            if (_audioTriggerGeneration != triggerGeneration)
            {
                _audioTriggerGeneration = triggerGeneration;
                _armedFrames = 0;
            }

            Interlocked.Increment(ref _callbackCount);
            int stateChannels = Mathf.Min(channels, MaximumChannels);
            Volatile.Write(ref _capturedChannels, stateChannels);
            int frameCount = data.Length / channels;
            int sampleRate = _sampleRate;
            float callbackInputPeak = 0f;
            float callbackAddedPeak = 0f;
            float detectorRelease = 1f - 1f / Mathf.Max(1f, sampleRate * 0.02f);

            for (int frame = 0; frame < frameCount; frame++)
            {
                int frameOffset = frame * channels;
                float inputPeak = FramePeak(data, frameOffset, channels);
                callbackInputPeak = Mathf.Max(callbackInputPeak, inputPeak);

                WriteInputFrame(data, frameOffset, channels, stateChannels);

                float previousEnvelope = _detectorEnvelope;
                if (Volatile.Read(ref _pendingTriggers) > 0 &&
                    IsOnset(previousEnvelope, inputPeak, _detectorWarmup))
                {
                    if (Interlocked.Decrement(ref _pendingTriggers) < 0)
                    {
                        Interlocked.Exchange(ref _pendingTriggers, 0);
                    }
                    else
                    {
                        StartVoice(sampleRate);
                        Interlocked.Increment(ref _onsetCount);
                    }
                    _armedFrames = 0;
                }
                else if (Volatile.Read(ref _pendingTriggers) > 0 &&
                    ++_armedFrames >= Mathf.RoundToInt(sampleRate * MaximumArmSeconds))
                {
                    Interlocked.Exchange(ref _pendingTriggers, 0);
                    _armedFrames = 0;
                }

                _detectorEnvelope = Mathf.Max(inputPeak, previousEnvelope * detectorRelease);
                _detectorWarmup++;

                callbackAddedPeak = Mathf.Max(
                    callbackAddedPeak,
                    RenderVoicesAtFrame(
                        data,
                        frameOffset,
                        channels,
                        stateChannels,
                        _absoluteFrame + 1));

                _absoluteFrame++;
            }

            _inputPeak = Mathf.Max(_inputPeak, callbackInputPeak);
            _addedPeak = Mathf.Max(_addedPeak, callbackAddedPeak);
            Volatile.Write(ref _activeVoiceCount, CountActiveVoices());
        }

        private float RenderVoicesAtFrame(
            float[] data,
            int frameOffset,
            int channels,
            int stateChannels,
            long availableExclusive)
        {
            float addedPeak = 0f;
            for (int voiceIndex = 0; voiceIndex < _voices.Length; voiceIndex++)
            {
                DspVoice voice = _voices[voiceIndex];
                if (!voice.Active)
                {
                    continue;
                }

                float envelope = PitchedGunshotEnvelopeFilter.CalculateEnvelope(
                    voice.OutputFrame,
                    voice.SampleRate,
                    voice.DurationSeconds,
                    voice.FadePercent);
                if (envelope <= 0f && voice.OutputFrame > 0)
                {
                    voice.Active = false;
                    continue;
                }

                envelope *= voice.HeadphoneTail.Next();
                for (int channel = 0; channel < channels; channel++)
                {
                    int stateChannel = channel % stateChannels;
                    int sampleIndex = frameOffset + channel;
                    float sourceSample = ReadRingInterpolated(
                        voice.SourcePosition,
                        stateChannel,
                        availableExclusive);
                    float upper = voice.UpperState[stateChannel] +
                        voice.LowpassCoefficient *
                        (sourceSample - voice.UpperState[stateChannel]);
                    float lower = voice.LowerState[stateChannel] +
                        voice.HighpassCoefficient *
                        (sourceSample - voice.LowerState[stateChannel]);
                    voice.UpperState[stateChannel] = upper;
                    voice.LowerState[stateChannel] = lower;

                    float addition = (upper - lower) * voice.Gain * envelope;
                    addedPeak = Mathf.Max(addedPeak, Mathf.Abs(addition));
                    data[sampleIndex] += addition;
                }

                voice.SourcePosition += voice.PitchRatio;
                voice.OutputFrame++;
            }
            return addedPeak;
        }

        private void StartVoice(int sampleRate)
        {
            DspVoice voice = GetNextVoice();
            int preRollFrames = Mathf.RoundToInt(sampleRate * PreRollSeconds);
            voice.Start(
                _absoluteFrame - preRollFrames,
                _pitchRatio,
                _highpassCoefficient,
                _lowpassCoefficient,
                _durationSeconds,
                _fadePercent,
                _gain,
                sampleRate,
                _headphoneTailDbPerSecond);
        }

        private DspVoice GetNextVoice()
        {
            for (int offset = 0; offset < MaximumVoices; offset++)
            {
                int index = (_nextVoice + offset) % MaximumVoices;
                if (!_voices[index].Active)
                {
                    _nextVoice = (index + 1) % MaximumVoices;
                    return _voices[index];
                }
            }

            DspVoice reused = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % MaximumVoices;
            reused.Active = false;
            return reused;
        }

        private int CountActiveVoices()
        {
            int count = 0;
            for (int index = 0; index < _voices.Length; index++)
            {
                if (_voices[index].Active)
                {
                    count++;
                }
            }
            return count;
        }

        private void WriteInputFrame(
            float[] data,
            int frameOffset,
            int channels,
            int stateChannels)
        {
            int ringFrame = PositiveModulo(_absoluteFrame, RingFrameCapacity);
            for (int channel = 0; channel < stateChannels; channel++)
            {
                _ring[ringFrame * MaximumChannels + channel] =
                    data[frameOffset + (channel % channels)];
            }
        }

        private float ReadRingInterpolated(
            double absolutePosition,
            int channel,
            long availableExclusive)
        {
            long firstFrame = (long)Math.Floor(absolutePosition);
            float fraction = (float)(absolutePosition - firstFrame);
            float first = ReadRing(firstFrame, channel, availableExclusive);
            float second = ReadRing(firstFrame + 1, channel, availableExclusive);
            return Mathf.Lerp(first, second, fraction);
        }

        private float ReadRing(long absoluteFrame, int channel, long availableExclusive)
        {
            if (absoluteFrame < 0 ||
                absoluteFrame >= availableExclusive ||
                availableExclusive - absoluteFrame >= RingFrameCapacity)
            {
                return 0f;
            }

            int ringFrame = PositiveModulo(absoluteFrame, RingFrameCapacity);
            return _ring[ringFrame * MaximumChannels + (channel % MaximumChannels)];
        }

        private void ResetAudioState()
        {
            Array.Clear(_ring, 0, _ring.Length);
            for (int index = 0; index < _voices.Length; index++)
            {
                _voices[index].Active = false;
            }

            _absoluteFrame = 0;
            _capturedChannels = 1;
            _detectorWarmup = 0;
            _armedFrames = 0;
            _detectorEnvelope = 0f;
            _nextVoice = 0;
            Volatile.Write(ref _activeVoiceCount, 0);
        }

        private static int PositiveModulo(long value, int modulus)
        {
            long result = value % modulus;
            return (int)(result < 0 ? result + modulus : result);
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

        private sealed class DspVoice
        {
            internal readonly float[] UpperState = new float[MaximumChannels];
            internal readonly float[] LowerState = new float[MaximumChannels];
            internal bool Active;
            internal double SourcePosition;
            internal float PitchRatio;
            internal float HighpassCoefficient;
            internal float LowpassCoefficient;
            internal float DurationSeconds;
            internal float FadePercent;
            internal float Gain;
            internal int SampleRate;
            internal int OutputFrame;
            internal HeadphoneTailEnvelope HeadphoneTail;

            internal void Start(
                double sourcePosition,
                float pitchRatio,
                float highpassCoefficient,
                float lowpassCoefficient,
                float durationSeconds,
                float fadePercent,
                float gain,
                int sampleRate,
                float headphoneTailDbPerSecond)
            {
                Array.Clear(UpperState, 0, UpperState.Length);
                Array.Clear(LowerState, 0, LowerState.Length);
                SourcePosition = sourcePosition;
                PitchRatio = pitchRatio;
                HighpassCoefficient = highpassCoefficient;
                LowpassCoefficient = lowpassCoefficient;
                DurationSeconds = durationSeconds;
                FadePercent = fadePercent;
                Gain = gain;
                SampleRate = sampleRate;
                OutputFrame = 0;
                HeadphoneTail.Reset(sampleRate, headphoneTailDbPerSecond);
                Active = true;
            }
        }
    }
}
