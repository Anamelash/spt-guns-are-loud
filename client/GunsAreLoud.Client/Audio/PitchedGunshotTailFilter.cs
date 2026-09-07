using System;
using System.Threading;
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

            var state = new TailState(
                Mathf.Clamp(excitationSeconds, 0.01f, 2f),
                tail,
                Mathf.Max(8000, sampleRate));
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
            TailState state = Volatile.Read(ref _state);
            if (state == null || data == null || channels <= 0)
            {
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
                        float delayed = state.Lines[comb][lineIndex];
                        float damped = state.Damping[comb, stateChannel] +
                            state.DampingCoefficient *
                            (delayed - state.Damping[comb, stateChannel]);
                        state.Damping[comb, stateChannel] = damped;
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
        }

        private sealed class TailState
        {
            internal readonly float[][] Lines;
            internal readonly int[] DelayFrames;
            internal readonly int[] Positions;
            internal readonly float[] Feedback;
            internal readonly float[,] Damping;
            internal readonly float[] InputLow;
            internal readonly float[] OutputLow;
            internal readonly float InputCoefficient;
            internal readonly float DampingCoefficient;
            internal readonly float OutputCoefficient;
            internal readonly int ExcitationFrames;
            internal readonly int TailFrames;
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
                Damping = new float[count, MaximumChannels];
                InputLow = new float[MaximumChannels];
                OutputLow = new float[MaximumChannels];
                InputCoefficient = LocalGunshotImpactFilter.CalculateLowpassCoefficient(
                    sampleRate,
                    TailInputLowpassHz);
                DampingCoefficient = LocalGunshotImpactFilter.CalculateLowpassCoefficient(
                    sampleRate,
                    FeedbackDampingHz);
                OutputCoefficient = LocalGunshotImpactFilter.CalculateLowpassCoefficient(
                    sampleRate,
                    TailOutputLowpassHz);
                ExcitationFrames = Mathf.Max(
                    1,
                    Mathf.RoundToInt(excitationSeconds * sampleRate));
                TailFrames = Mathf.Max(1, Mathf.RoundToInt(tailSeconds * sampleRate));

                for (int comb = 0; comb < count; comb++)
                {
                    float delay = CombDelaySeconds[comb];
                    int frames = Mathf.Max(1, Mathf.RoundToInt(delay * sampleRate));
                    DelayFrames[comb] = frames;
                    Lines[comb] = new float[frames * MaximumChannels];
                    Feedback[comb] = CalculateFeedback(delay, tailSeconds);
                }
            }
        }
    }
}
