using System;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HearingDspRegressionTests
    {
        [Test]
        public void HalfWetMinusFiveDbProducesMeasuredLowFrequencyTransfer()
        {
            var state = new HearingDspChannelState();
            float output = state.Process(
                0.5f,
                0.5f,
                HearingDspTransfer.DbToLinear(-5f),
                1f,
                0f,
                0f,
                1f,
                out float preClamp);

            double measuredDb = 20.0 * Math.Log10(output / 0.5f);
            Assert.That(measuredDb, Is.EqualTo(-2.145).Within(0.002));
            Assert.That(preClamp, Is.EqualTo(output).Within(0.000001f));
        }

        [Test]
        public void NeutralPathPreservesFiniteSamples()
        {
            var state = new HearingDspChannelState();
            float[] samples = { -0.9f, -0.25f, 0f, 0.25f, 0.9f };
            foreach (float sample in samples)
            {
                float output = state.Process(sample, 0f, 0.1f, 0.01f, 0f, 0f, 1f, out _);
                Assert.That(output, Is.EqualTo(sample));
            }
        }

        [Test]
        public void IndependentChannelStatesDoNotLeakAcrossStereo()
        {
            var left = new HearingDspChannelState();
            var right = new HearingDspChannelState();
            float gain = HearingDspTransfer.DbToLinear(-12f);

            float leftOutput = left.Process(0.8f, 1f, gain, 1f, 0f, 0f, 1f, out _);
            float rightOutput = right.Process(0f, 1f, gain, 1f, 0f, 0f, 1f, out _);

            Assert.That(leftOutput, Is.GreaterThan(0f));
            Assert.That(rightOutput, Is.EqualTo(0f));
        }

        [TestCase(float.NaN, 0f)]
        [TestCase(float.PositiveInfinity, 1f)]
        [TestCase(float.NegativeInfinity, -1f)]
        public void NonFiniteInputCannotReachOutputOrPoisonFollowingSample(float input, float expected)
        {
            var state = new HearingDspChannelState();
            float output = state.Process(input, 1f, 1f, 1f, 0f, 0f, 1f, out float raw);
            float recovered = state.Process(0.25f, 0f, 1f, 1f, 0f, 0f, 1f, out _);

            Assert.That(output, Is.EqualTo(expected));
            Assert.That(float.IsNaN(raw) || float.IsInfinity(raw), Is.True);
            Assert.That(recovered, Is.EqualTo(0.25f));
        }

        [Test]
        public void NonFiniteTargetCannotPoisonFollowingFiniteProcessing()
        {
            var state = new HearingDspChannelState();
            state.Process(0.25f, float.NaN, float.PositiveInfinity, float.NaN,
                float.NegativeInfinity, float.NaN, float.NaN, out _);
            float recovered = state.Process(0.25f, 0f, 1f, 1f, 0f, 0f, 1f, out _);

            Assert.That(recovered, Is.EqualTo(0.25f));
            Assert.That(float.IsNaN(recovered) || float.IsInfinity(recovered), Is.False);
        }

        [Test]
        public void ProcessorBypassLeavesFiniteBufferUntouchedWithoutProbe()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            float[] buffer = { -0.5f, 0.25f, 0.75f, -0.125f };
            float[] expected = (float[])buffer.Clone();

            processor.ProcessAudioBufferForTests(buffer, 2);

            Assert.That(buffer, Is.EqualTo(expected));
        }

        [Test]
        public void ImmediateBypassGenerationResetsStateEvenWhenReenabledBeforeCallback()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            processor.SetTargets(true, 1f, 1f, 12f, 12f, 200f, 200f,
                0f, 0f, 6200f, 37f, 48000);
            float[] first = { 0.8f, 0f };
            processor.ProcessAudioBufferForTests(first, 2);

            processor.ImmediateBypass();
            processor.SetTargets(true, 0f, 0f, 0f, 0f, 20000f, 20000f,
                0f, 0f, 6200f, 37f, 48000);
            float[] afterReenable = { 0f, 0f };
            processor.ProcessAudioBufferForTests(afterReenable, 2);

            Assert.That(afterReenable[0], Is.EqualTo(0f));
            Assert.That(afterReenable[1], Is.EqualTo(0f));
        }

        [Test]
        public void ChannelLayoutChangesResetBothChannelHistories()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            processor.SetTargets(true, 1f, 1f, 12f, 12f, 200f, 200f,
                0f, 0f, 6200f, 37f, 48000);
            processor.ProcessAudioBufferForTests(new[] { 0f, 0.8f }, 2);
            processor.ProcessAudioBufferForTests(new[] { 0f }, 1);
            float[] stereoAfterMono = { 0f, 0f };

            processor.ProcessAudioBufferForTests(stereoAfterMono, 2);

            Assert.That(stereoAfterMono[0], Is.EqualTo(0f));
            Assert.That(stereoAfterMono[1], Is.EqualTo(0f));
        }

        [Test]
        public void InvalidOscillatorTargetsCannotPoisonLaterValidPhase()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            processor.SetTargets(true, 0f, 0f, 0f, 0f, 20000f, 20000f,
                0.1f, 0.1f, float.NaN, float.PositiveInfinity, 48000);
            float[] invalidTargetOutput = { 0f, 0f, 0f, 0f };
            processor.ProcessAudioBufferForTests(invalidTargetOutput, 2);

            processor.SetTargets(true, 0f, 0f, 0f, 0f, 20000f, 20000f,
                0.1f, 0.1f, 6200f, 37f, 48000);
            float[] recovered = { 0f, 0f, 0f, 0f };
            processor.ProcessAudioBufferForTests(recovered, 2);

            foreach (float sample in recovered)
                Assert.That(float.IsNaN(sample) || float.IsInfinity(sample), Is.False);
            Assert.That(Math.Abs(recovered[2]) + Math.Abs(recovered[3]), Is.GreaterThan(0f));
        }

        private static HearingImpactProcessor UninitializedProcessor()
        {
            return (HearingImpactProcessor)FormatterServices.GetUninitializedObject(
                typeof(HearingImpactProcessor));
        }
    }
}
