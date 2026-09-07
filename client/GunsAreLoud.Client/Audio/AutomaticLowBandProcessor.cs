using System;
using System.Threading;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class AutomaticLowBandProcessor
    {
        internal const int MaximumEnvelopes = 16;
        internal const int EventCapacity = 32;
        internal const float BodyDurationSeconds = 0.120f;
        private readonly float[] _upperState = new float[8];
        private readonly float[] _lowerState = new float[8];
        private readonly Envelope[] _envelopes = new Envelope[MaximumEnvelopes];
        private readonly DspEvent[] _events = new DspEvent[EventCapacity];
        private int _writeSequence, _readSequence, _audioGeneration;
        private ResetRequest _resetRequest = new ResetRequest(0, 0.0);
        private volatile bool _enabled;
        private int _sampleRate = 48000;
        private float _upperCoefficient, _lowerCoefficient, _frameEnvelopeGain;
        private long _frameCursor;
        private double _streamStartDsp;
        private int _droppedEnvelopeCount;

        internal long FrameCursor => _frameCursor;
        internal int DroppedEnvelopeCount => _droppedEnvelopeCount;
        internal int Generation => Volatile.Read(ref _resetRequest).Generation;

        internal bool Configure(int sampleRate, float upperCoefficient, float lowerCoefficient)
        {
            _enabled = true;
            return Enqueue(new DspEvent(EventKind.Configure, Generation, 0, 0, 0,
                Math.Max(8000, sampleRate), upperCoefficient, lowerCoefficient));
        }

        internal void RequestReset(double streamStartDsp, bool enabled)
        {
            _enabled = enabled;
            ResetRequest prior = Volatile.Read(ref _resetRequest);
            Volatile.Write(ref _resetRequest,
                new ResetRequest(prior.Generation + 1, streamStartDsp));
        }

        internal void Disable()
        {
            _enabled = false;
            ResetRequest prior = Volatile.Read(ref _resetRequest);
            Volatile.Write(ref _resetRequest,
                new ResetRequest(prior.Generation + 1, prior.StreamStartDsp));
        }

        internal bool Trigger(double dspTime, float gain, float tailDbPerSecond)
        {
            return _enabled && gain > 0.001f && Enqueue(new DspEvent(
                EventKind.Trigger, Generation, dspTime, gain, tailDbPerSecond, 0, 0, 0));
        }

        internal void TriggerCurrentFrame(float gain, float tailDbPerSecond)
        {
            if (_enabled && gain > 0.001f)
                AddEnvelope(new DspEvent(EventKind.Trigger,
                    _audioGeneration,
                    _streamStartDsp + _frameCursor / (double)_sampleRate,
                    gain, tailDbPerSecond, 0, 0, 0));
        }

        internal void BeginFrame()
        {
            ConsumeControlRequests();
            ConsumeQueuedEvents();
            _frameEnvelopeGain = 0;
            if (!_enabled) return;
            for (int i = 0; i < _envelopes.Length; i++)
            {
                Envelope envelope = _envelopes[i];
                if (!envelope.Active || _frameCursor < envelope.StartFrame) continue;
                long age = _frameCursor - envelope.StartFrame;
                float shape = LocalGunshotImpactFilter.CalculateBodyEnvelope((int)age, _sampleRate);
                if (shape <= 0f && age > 0) { _envelopes[i].Active = false; continue; }
                _frameEnvelopeGain += envelope.Gain * shape * envelope.Headphone.Next();
                _envelopes[i] = envelope;
            }
        }

        internal float ProcessSample(float sample, int channel, int channels)
        {
            int stateChannel = channel % Math.Min(Math.Max(1, channels), _upperState.Length);
            float upper = _upperState[stateChannel] + _upperCoefficient * (sample - _upperState[stateChannel]);
            float lower = _lowerState[stateChannel] + _lowerCoefficient * (sample - _lowerState[stateChannel]);
            _upperState[stateChannel] = upper;
            _lowerState[stateChannel] = lower;
            return _enabled ? (upper - lower) * _frameEnvelopeGain : 0f;
        }

        internal void EndFrame() { _frameCursor++; }

        private bool Enqueue(DspEvent item)
        {
            int write = _writeSequence;
            if (write - Volatile.Read(ref _readSequence) >= EventCapacity) return false;
            _events[write % EventCapacity] = item;
            Volatile.Write(ref _writeSequence, write + 1);
            return true;
        }

        internal void ConsumeQueuedEvents()
        {
            int read = _readSequence, write = Volatile.Read(ref _writeSequence);
            while (read < write)
            {
                DspEvent item = _events[read % EventCapacity];
                if (item.Generation > _audioGeneration) break;
                if (item.Generation < _audioGeneration) { read++; continue; }
                if (item.Kind == EventKind.Configure)
                { _sampleRate = item.SampleRate; _upperCoefficient = item.UpperCoefficient; _lowerCoefficient = item.LowerCoefficient; }
                else if (_enabled) AddEnvelope(item);
                read++;
            }
            Volatile.Write(ref _readSequence, read);
        }

        internal void ConsumeControlRequests()
        {
            ResetRequest request = Volatile.Read(ref _resetRequest);
            if (_audioGeneration == request.Generation) return;
            ResetAudioState(request.StreamStartDsp);
            _audioGeneration = request.Generation;
        }

        private void ResetAudioState(double startDsp)
        {
            Array.Clear(_upperState, 0, _upperState.Length); Array.Clear(_lowerState, 0, _lowerState.Length);
            Array.Clear(_envelopes, 0, _envelopes.Length); _frameCursor = 0; _streamStartDsp = startDsp; _droppedEnvelopeCount = 0;
        }

        private void AddEnvelope(DspEvent item)
        {
            int free = -1;
            for (int i = 0; i < _envelopes.Length; i++) if (!_envelopes[i].Active) { free = i; break; }
            if (free < 0) { _droppedEnvelopeCount++; return; }
            var headphone = new HeadphoneTailEnvelope(); headphone.Reset(_sampleRate, item.TailDbPerSecond);
            _envelopes[free] = new Envelope
            {
                Active = true,
                StartFrame = Math.Max(_frameCursor, (long)Math.Round((item.DspTime - _streamStartDsp) * _sampleRate)),
                Gain = item.Gain,
                Headphone = headphone
            };
        }

        private enum EventKind { Configure, Trigger }
        private sealed class ResetRequest
        {
            internal readonly int Generation; internal readonly double StreamStartDsp;
            internal ResetRequest(int generation, double streamStartDsp)
            { Generation = generation; StreamStartDsp = streamStartDsp; }
        }
        private readonly struct DspEvent
        {
            internal readonly EventKind Kind; internal readonly int Generation; internal readonly double DspTime;
            internal readonly float Gain, TailDbPerSecond, UpperCoefficient, LowerCoefficient; internal readonly int SampleRate;
            internal DspEvent(EventKind kind, int generation, double dsp, float gain, float tail, int rate, float upper, float lower)
            { Kind = kind; Generation = generation; DspTime = dsp; Gain = gain; TailDbPerSecond = tail; SampleRate = rate; UpperCoefficient = upper; LowerCoefficient = lower; }
        }
        private struct Envelope
        { internal bool Active; internal long StartFrame; internal float Gain; internal HeadphoneTailEnvelope Headphone; }
    }
}
