using System;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// Extends an isolated automatic BeatLn into silence. Four damped combs are
    /// excited only by the already band-passed report; no oscillator, repeated
    /// clip, or neighbouring authored shot is introduced.
    /// </summary>
    internal sealed class PitchedGunshotTailFilter : MonoBehaviour
    {
        private const int MaximumChannels = 2;
        private const float TailInputLowpassHz = 320f;
        private const float FeedbackDampingHz = 420f;
        private const float TailOutputLowpassHz = 260f;
        private const float TailMix = 0.82f;

        private static readonly float[] CombDelaySeconds =
        {
            0.0131f,
            0.0179f,
            0.0237f,
            0.0311f
        };

        private TailState _state;
        private TailState _spare;

        internal void Configure(
            float excitationSeconds,
            float tailSeconds,
            int sampleRate)
        {
            float tail = Mathf.Clamp(tailSeconds, 0f, 0.6f);
            if (tail <= 0.001f)
            {
                Bypass();
                return;
            }

            float excitation = Mathf.Clamp(excitationSeconds, 0.01f, 2f);
            int rate = Mathf.Max(8000, sampleRate);
            // Two states rotate: the one handed back here was replaced at the
            // previous schedule, at least one shot ago, so no callback can still
            // be reading it. Its delay lines are several tens of kilobytes and
            // used to be allocated again for every copy of every burst.
            TailState state = _spare;
            if (state != null && state.Fits(rate)) state.Reconfigure(excitation, tail, rate);
            else state = new TailState(excitation, tail, rate);
            _spare = Volatile.Read(ref _state);
            Volatile.Write(ref _state, state);
        }

        internal void Bypass()
        {
            Volatile.Write(ref _state, null);
        }

        internal static float CalculateFeedback(
            float delaySeconds,
            float tailSeconds)
        {
            if (delaySeconds <= 0f || tailSeconds <= 0f)
            {
                return 0f;
            }

            // Each comb reaches roughly -50 dB by the requested tail endpoint.
            return Mathf.Clamp(
                (float)Math.Pow(10.0, -2.5 * delaySeconds / tailSeconds),
                0f,
                0.985f);
        }

        private void OnDisable()
        {
            Bypass();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            long trace = AudioFilterTrace.Begin();
            TailState state = Volatile.Read(ref _state);
            if (state == null || data == null || channels <= 0)
            {
                AudioFilterTrace.Record(
                    AudioFilterKind.PitchedTail, trace,
                    data == null ? 0 : data.Length, channels, idle: true);
                return;
            }

            int frameCount = data.Length / channels;
            int stateChannels = Mathf.Min(channels, MaximumChannels);
            for (int frame = 0; frame < frameCount; frame++)
            {
                bool tailPhase = state.Frame >= state.ExcitationFrames;
                int tailFrame = state.Frame - state.ExcitationFrames;
                bool tailActive = tailPhase && tailFrame < state.TailFrames;
                float tailWindow = tailActive
                    ? Mathf.Sqrt(1f - Mathf.Clamp01(tailFrame / (float)state.TailFrames))
                    : 0f;
                int frameOffset = frame * channels;

                for (int channel = 0; channel < channels; channel++)
                {
                    int stateChannel = channel % stateChannels;
                    int sampleIndex = frameOffset + channel;
                    float input = data[sampleIndex];
                    float lowInput = state.InputLow[stateChannel] +
                        state.InputCoefficient *
                        (input - state.InputLow[stateChannel]);
                    state.InputLow[stateChannel] = lowInput;

                    float combSum = 0f;
                    for (int comb = 0; comb < state.Lines.Length; comb++)
                    {
                        int lineIndex =
                            state.Positions[comb] * MaximumChannels + stateChannel;
                        int dampingIndex = comb * MaximumChannels + stateChannel;
                        float delayed = state.Lines[comb][lineIndex];
                        float damped = state.Damping[dampingIndex] +
                            state.DampingCoefficient *
                            (delayed - state.Damping[dampingIndex]);
                        state.Damping[dampingIndex] = damped;
                        state.Lines[comb][lineIndex] =
                            lowInput * 0.25f + damped * state.Feedback[comb];
                        combSum += delayed;
                    }

                    float diffuse = combSum / state.Lines.Length;
                    float lowTail = state.OutputLow[stateChannel] +
                        state.OutputCoefficient *
                        (diffuse - state.OutputLow[stateChannel]);
                    state.OutputLow[stateChannel] = lowTail;
                    if (tailActive)
                    {
                        data[sampleIndex] = input + lowTail * TailMix * tailWindow;
                    }
                }

                for (int comb = 0; comb < state.Positions.Length; comb++)
                {
                    int next = state.Positions[comb] + 1;
                    state.Positions[comb] = next < state.DelayFrames[comb] ? next : 0;
                }
                state.Frame++;
            }
            AudioFilterTrace.Record(AudioFilterKind.PitchedTail, trace, data.Length, channels);
        }

        private sealed class TailState
        {
            internal readonly float[][] Lines;
            internal readonly int[] DelayFrames;
            internal readonly int[] Positions;
            internal readonly float[] Feedback;
            // One dimension: a comb/channel pair is addressed by arithmetic, not
            // by the bounds check and stride multiply of a rectangular array.
            internal readonly float[] Damping;
            internal readonly float[] InputLow;
            internal readonly float[] OutputLow;
            internal float InputCoefficient;
            internal float DampingCoefficient;
            internal float OutputCoefficient;
            internal int ExcitationFrames;
            internal int TailFrames;
            internal int SampleRate;
            internal int Frame;

            internal TailState(
                float excitationSeconds,
                float tailSeconds,
                int sampleRate)
            {
                int count = CombDelaySeconds.Length;
                Lines = new float[count][];
                DelayFrames = new int[count];
                Positions = new int[count];
                Feedback = new float[count];
                Damping = new float[count * MaximumChannels];
                InputLow = new float[MaximumChannels];
                OutputLow = new float[MaximumChannels];
                for (int comb = 0; comb < count; comb++)
                {
                    int frames = Mathf.Max(1, Mathf.RoundToInt(CombDelaySeconds[comb] * sampleRate));
                    DelayFrames[comb] = frames;
                    Lines[comb] = new float[frames * MaximumChannels];
                }
                Reconfigure(excitationSeconds, tailSeconds, sampleRate);
            }

            internal bool Fits(int sampleRate) => SampleRate == sampleRate;

            /// <summary>
            /// Re-arms this state for another copy. The delay lines keep their
            /// storage and are cleared; only the coefficients are recomputed, so a
            /// burst does not allocate a fresh set of combs per round.
            /// </summary>
            internal void Reconfigure(float excitationSeconds, float tailSeconds, int sampleRate)
            {
                SampleRate = sampleRate;
                InputCoefficient = LowBandCoefficients.Lowpass(
                    sampleRate,
                    TailInputLowpassHz);
                DampingCoefficient = LowBandCoefficients.Lowpass(
                    sampleRate,
                    FeedbackDampingHz);
                OutputCoefficient = LowBandCoefficients.Lowpass(
                    sampleRate,
                    TailOutputLowpassHz);
                ExcitationFrames = Mathf.Max(
                    1,
                    Mathf.RoundToInt(excitationSeconds * sampleRate));
                TailFrames = Mathf.Max(1, Mathf.RoundToInt(tailSeconds * sampleRate));
                Frame = 0;
                Array.Clear(Damping, 0, Damping.Length);
                Array.Clear(InputLow, 0, InputLow.Length);
                Array.Clear(OutputLow, 0, OutputLow.Length);
                Array.Clear(Positions, 0, Positions.Length);
                for (int comb = 0; comb < Lines.Length; comb++)
                {
                    float delay = CombDelaySeconds[comb];
                    int frames = Mathf.Max(1, Mathf.RoundToInt(delay * sampleRate));
                    DelayFrames[comb] = frames;
                    if (Lines[comb] == null || Lines[comb].Length < frames * MaximumChannels)
                        Lines[comb] = new float[frames * MaximumChannels];
                    else
                        Array.Clear(Lines[comb], 0, Lines[comb].Length);
                    Feedback[comb] = CalculateFeedback(delay, tailSeconds);
                }
            }
        }
    }
}
