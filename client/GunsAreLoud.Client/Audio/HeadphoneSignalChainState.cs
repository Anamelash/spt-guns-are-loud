using System;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct HeadphoneSignalTelemetry
    {
        internal readonly long Frames;
        internal readonly float InputPeak, OutputPeak, ElectronicsGainReductionDb;
        internal HeadphoneSignalTelemetry(long frames, float inputPeak, float outputPeak, float reduction)
        { Frames = frames; InputPeak = inputPeak; OutputPeak = outputPeak; ElectronicsGainReductionDb = reduction; }
    }

    internal sealed class HeadphoneSignalChainState
    {
        private readonly HeadphonePassiveFilter _passive;
        private readonly HeadphoneElectronicPath _electronic;
        private long _frames;
        private float _inputPeak, _outputPeak;

        internal HeadphoneSignalChainState(int sampleRate, HeadsetProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            _passive = new HeadphonePassiveFilter(sampleRate, profile.Passive);
            _electronic = new HeadphoneElectronicPath(sampleRate, profile.Electronics);
        }

        internal void Process(float[] interleaved, int channels, bool electronicsPowered = true)
        {
            if (interleaved == null || channels < 1 || channels > 2) return;
            int frames = interleaved.Length / channels;
            for (int frame = 0; frame < frames; frame++)
            {
                int offset = frame * channels;
                float left = Safe(interleaved[offset]);
                float right = channels == 2 ? Safe(interleaved[offset + 1]) : left;
                _inputPeak = Math.Max(_inputPeak, Math.Max(Math.Abs(left), Math.Abs(right)));
                if (electronicsPowered) _electronic.BeginFrame(left, right);
                for (int channel = 0; channel < channels; channel++)
                {
                    float input = Safe(interleaved[offset + channel]);
                    float output = _passive.ProcessSample(input, channel);
                    if (electronicsPowered) output += _electronic.ProcessSample(input, channel);
                    if (float.IsNaN(output) || float.IsInfinity(output)) output = 0;
                    interleaved[offset + channel] = output;
                    _outputPeak = Math.Max(_outputPeak, Math.Abs(output));
                }
                _frames++;
            }
        }

        internal void Reset()
        {
            _passive.Reset(); _electronic.Reset();
            _frames = 0; _inputPeak = _outputPeak = 0;
        }

        internal HeadphoneSignalTelemetry GetTelemetry() =>
            new HeadphoneSignalTelemetry(_frames, _inputPeak, _outputPeak, _electronic.GainReductionDb);

        private static float Safe(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
    }
}
