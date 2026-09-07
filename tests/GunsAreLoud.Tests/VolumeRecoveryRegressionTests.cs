using System;
using System.Reflection;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class VolumeRecoveryRegressionTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(44100, 1, 127)]
        [TestCase(48000, 2, 511)]
        [TestCase(96000, 2, 73)]
        public void WideBandPlaybackIsExactlyTheAuthoredBandPassWithoutCrossoverPhase(
            int rate, int channels, int callbackFrames)
        {
            var component = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedBandPassFilter));
            component.Configure(rate, 46.76057f, 2000f);
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), component,
                typeof(PitchedBandPassFilter).GetMethod("OnAudioFilterRead", Hidden));
            var reference = new PitchedBandPassState(rate, 46.76057f, 2000f);
            var random = new Random(9137);

            for (int pass = 0; pass < 19; pass++)
            {
                int frames = pass == 18 ? 31 : callbackFrames;
                var actual = new float[frames * channels];
                var expected = new float[actual.Length];
                for (int i = 0; i < actual.Length; i++) actual[i] = expected[i] = (float)(random.NextDouble() * 2 - 1);
                callback(actual, channels);
                for (int frame = 0; frame < frames; frame++)
                    for (int channel = 0; channel < channels; channel++)
                        expected[frame * channels + channel] = reference.Process(expected[frame * channels + channel], channel);
                Assert.That(actual, Is.EqualTo(expected).Within(0.0000001f));
                Assert.That(actual, Is.All.Matches<float>(v => !float.IsNaN(v) && !float.IsInfinity(v)));
            }
        }

        [TestCase(44100, 1, 137)]
        [TestCase(48000, 2, 509)]
        [TestCase(96000, 2, 61)]
        public void BodyToDecayRecoveryIsChunkAndChannelSafeWithFinitePeaks(
            int rate, int channels, int callbackFrames)
        {
            var filter = NewEnvelope(rate, 0.25118864f, 1f, 0f);
            var callback = Callback(filter);
            int cursor = 0, totalFrames = (int)(rate * 0.35f);
            float peak = 0;
            while (cursor < totalFrames)
            {
                int frames = Math.Min(callbackFrames, totalFrames - cursor);
                var data = new float[frames * channels];
                for (int frame = 0; frame < frames; frame++)
                    for (int channel = 0; channel < channels; channel++) data[frame * channels + channel] = 0.25f;
                callback(data, channels);
                foreach (float value in data)
                {
                    Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False);
                    peak = Math.Max(peak, Math.Abs(value));
                }
                cursor += frames;
            }
            Assert.That(peak, Is.LessThanOrEqualTo(0.250001f));
            Assert.That(PitchedGunshotEnvelopeFilter.CalculateCalibrationGain(
                (int)(rate * 0.10), rate, 0.18f, 0.25118864f, 1f), Is.EqualTo(0.25118864f));
            Assert.That(PitchedGunshotEnvelopeFilter.CalculateCalibrationGain(
                (int)(rate * 0.24), rate, 0.18f, 0.25118864f, 1f), Is.EqualTo(1f));
        }

        [Test]
        public void ExistingVoiceKeepsItsCalibrationAndHeadphoneDecaySnapshot()
        {
            const int rate = 48000;
            var actual = NewEnvelope(rate, 0.4f, 0.9f, 8f);
            var reference = NewEnvelope(rate, 0.4f, 0.9f, 8f);
            Action<float[], int> actualCallback = Callback(actual), referenceCallback = Callback(reference);

            for (int pass = 0; pass < 40; pass++)
            {
                var a = Constant(257, 2, 0.2f); var b = Constant(257, 2, 0.2f);
                actualCallback(a, 2); referenceCallback(b, 2);
                Assert.That(a, Is.EqualTo(b).Within(0.0000001f));
            }

            // A later voice/F12 snapshot must not mutate the already configured callback state.
            var later = NewEnvelope(rate, 1f, 1f, 0f);
            Callback(later)(Constant(19, 2, 0.2f), 2);
            for (int pass = 0; pass < 40; pass++)
            {
                var a = Constant(191, 2, 0.2f); var b = Constant(191, 2, 0.2f);
                actualCallback(a, 2); referenceCallback(b, 2);
                Assert.That(a, Is.EqualTo(b).Within(0.0000001f));
            }
        }

        private static PitchedGunshotEnvelopeFilter NewEnvelope(
            int rate, float bodyGain, float decayGain, float headphoneDecay)
        {
            var filter = (PitchedGunshotEnvelopeFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedGunshotEnvelopeFilter));
            filter.Configure(0.5f, 5f, 1f, startImmediately: true, sampleRate: rate,
                headphoneTailDbPerSecond: headphoneDecay,
                bodyCalibrationGain: bodyGain, decayCalibrationGain: decayGain);
            return filter;
        }

        private static Action<float[], int> Callback(PitchedGunshotEnvelopeFilter filter) =>
            (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), filter,
                typeof(PitchedGunshotEnvelopeFilter).GetMethod("OnAudioFilterRead", Hidden));

        private static float[] Constant(int frames, int channels, float value)
        {
            var data = new float[frames * channels];
            for (int i = 0; i < data.Length; i++) data[i] = value;
            return data;
        }
    }
}
