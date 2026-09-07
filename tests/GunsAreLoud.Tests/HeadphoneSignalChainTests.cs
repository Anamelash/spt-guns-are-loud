using System;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HeadphoneSignalChainTests
    {
        private const int Rate = 48000;

        [Test]
        public void UnknownAndEmptyTemplateIdsFailClosed()
        {
            Assert.That(HeadsetProfileRegistry.TryGet(null, out _), Is.False);
            Assert.That(HeadsetProfileRegistry.TryGet("", out _), Is.False);
            Assert.That(HeadsetProfileRegistry.TryGet("not-a-real-template", out _), Is.False);
        }

        [Test]
        public void ProfileConstructionDefensivelyCopiesItsMeasuredArrays()
        {
            float[] frequencies = { 125f, 1000f };
            float[] attenuation = { 12f, 24f };
            HeadsetProfile profile = Profile(frequencies, attenuation);
            frequencies[0] = 7777f;
            attenuation[0] = 99f;

            Assert.That(profile.Passive.FrequencyAt(0), Is.EqualTo(125f));
            Assert.That(profile.Passive.MeanAttenuationAt(0), Is.EqualTo(12f));
        }

        [Test]
        public void PassiveProtectionActsOnTheFirstImpulseWithElectronicsOff()
        {
            var state = new HeadphoneSignalChainState(Rate, Profile());
            float[] impulse = new float[1024 * 2];
            impulse[0] = impulse[1] = 1f;

            state.Process(impulse, 2, electronicsPowered: false);

            Assert.That(impulse[0], Is.Not.Zero, "passive protection must not wait for a detector");
            Assert.That(Math.Abs(impulse[0]), Is.LessThan(1f));
            Assert.That(Peak(impulse), Is.LessThan(1f));
        }

        [Test]
        public void PassiveSteadyStateFollowsTheSpecifiedFrequencyDifference()
        {
            HeadsetProfile profile = Profile(
                frequencies: new[] { 125f, 4000f },
                attenuation: new[] { 12f, 30f });
            double low = PassiveToneRms(profile, 125f);
            double high = PassiveToneRms(profile, 4000f);
            double measuredDifferenceDb = 20 * Math.Log10(low / high);

            Assert.That(measuredDifferenceDb, Is.EqualTo(18).Within(3.0));
        }

        [Test]
        public void CompressorStatePersistsAcrossEveryBlockOfALoudBurst()
        {
            var state = new HeadphoneSignalChainState(Rate, Profile());
            float firstReduction = 0;
            for (int block = 0; block < 40; block++)
            {
                float[] loud = Constant(127, 2, 0.9f);
                state.Process(loud, 2);
                if (block == 0) firstReduction = state.GetTelemetry().ElectronicsGainReductionDb;
            }

            float finalReduction = state.GetTelemetry().ElectronicsGainReductionDb;
            Assert.That(finalReduction, Is.GreaterThan(firstReduction));
            Assert.That(finalReduction, Is.GreaterThan(6f));
        }

        [Test]
        public void CallbackBlockSizeDoesNotChangeTheRenderedSignal()
        {
            float[] source = ToneWithImpulse(Rate / 4, 2, 330f, 0.08f);
            float[] whole = (float[])source.Clone();
            float[] chunked = (float[])source.Clone();
            new HeadphoneSignalChainState(Rate, Profile()).Process(whole, 2);

            var state = new HeadphoneSignalChainState(Rate, Profile());
            int sample = 0;
            while (sample < chunked.Length)
            {
                int count = Math.Min(2 * 113, chunked.Length - sample);
                var block = new float[count];
                Array.Copy(chunked, sample, block, 0, count);
                state.Process(block, 2);
                Array.Copy(block, 0, chunked, sample, count);
                sample += count;
            }

            Assert.That(chunked, Is.EqualTo(whole).Within(0.000001f));
        }

        [Test]
        public void CompressorTimingIsDefinedInSecondsAcrossSampleRates()
        {
            float[] reductions = new float[3];
            int[] rates = { 44100, 48000, 96000 };
            for (int i = 0; i < rates.Length; i++)
            {
                var state = new HeadphoneSignalChainState(rates[i], Profile());
                state.Process(Constant((int)(rates[i] * 0.025), 2, 0.9f), 2);
                reductions[i] = state.GetTelemetry().ElectronicsGainReductionDb;
            }

            Assert.That(Max(reductions) - Min(reductions), Is.LessThan(0.1f));
            Assert.That(Min(reductions), Is.GreaterThan(15f));
        }

        [Test]
        public void LoudImpulseOnOneChannelCompressesTheElectronicPathOnBothChannels()
        {
            HeadsetProfile profile = Profile(
                frequencies: new[] { 125f, 8000f },
                attenuation: new[] { 60f, 60f });
            int frames = Rate / 40;
            float[] linked = StereoTones(frames, 0.9f, 0.01f, 1000f);
            float[] quiet = StereoTones(frames, 0.01f, 0.01f, 1000f);
            new HeadphoneSignalChainState(Rate, profile).Process(linked, 2);
            new HeadphoneSignalChainState(Rate, profile).Process(quiet, 2);

            double linkedRight = ChannelRms(linked, 2, 1, frames / 2);
            double quietRight = ChannelRms(quiet, 2, 1, frames / 2);
            Assert.That(linkedRight, Is.LessThan(quietRight * 0.5));
        }

        [Test]
        public void UnlinkedProfileLeavesTheQuietChannelIndependent()
        {
            HeadsetProfile profile = Profile(
                frequencies: new[] { 125f, 8000f },
                attenuation: new[] { 60f, 60f },
                stereoLinked: false);
            int frames = Rate / 40;
            float[] split = StereoTones(frames, 0.9f, 0.01f, 1000f);
            float[] quiet = StereoTones(frames, 0.01f, 0.01f, 1000f);
            new HeadphoneSignalChainState(Rate, profile).Process(split, 2);
            new HeadphoneSignalChainState(Rate, profile).Process(quiet, 2);

            Assert.That(ChannelRms(split, 2, 1, frames / 2),
                Is.EqualTo(ChannelRms(quiet, 2, 1, frames / 2)).Within(0.00001));
        }

        [Test]
        public void NonFiniteInputCannotEscapeOrPoisonTheFollowingAudio()
        {
            var state = new HeadphoneSignalChainState(Rate, Profile());
            float[] corrupt = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f };
            state.Process(corrupt, 2);
            float[] clean = Constant(512, 2, 0.05f);
            state.Process(clean, 2);

            Assert.That(corrupt, Is.All.Matches<float>(Finite));
            Assert.That(clean, Is.All.Matches<float>(Finite));
            Assert.That(Peak(clean), Is.GreaterThan(0f));
        }

        [Test]
        public void ResetRemovesBurstReductionAndFilterHistory()
        {
            var state = new HeadphoneSignalChainState(Rate, Profile());
            state.Process(Constant(Rate / 20, 2, 0.9f), 2);
            Assert.That(state.GetTelemetry().ElectronicsGainReductionDb, Is.GreaterThan(0f));

            state.Reset();
            Assert.That(state.GetTelemetry().Frames, Is.Zero);
            Assert.That(state.GetTelemetry().ElectronicsGainReductionDb, Is.Zero);
            float[] silence = new float[512];
            state.Process(silence, 2, electronicsPowered: false);
            Assert.That(silence, Is.All.EqualTo(0f));
        }

        private static HeadsetProfile Profile(float[] frequencies = null, float[] attenuation = null,
            bool stereoLinked = true)
        {
            frequencies ??= new[] { 125f, 1000f, 4000f, 8000f };
            attenuation ??= new[] { 15f, 24f, 30f, 30f };
            var passive = new HeadsetPassiveProfile(frequencies, attenuation, null,
                "test", "analytical fixture", "", HeadsetEvidence.Proposed);
            var electronics = new HeadsetElectronicsProfile(6f, -24f, 6f, 10f,
                0.0005f, 0.010f, 0.150f, 0.5f, 100f, 10000f, stereoLinked,
                HeadsetEvidence.Proposed, HeadsetEvidence.Proposed);
            return new HeadsetProfile("test", new[] { "test-id" }, "test", "test", "headband", "foam",
                passive, electronics, Array.Empty<string>(), Array.Empty<string>());
        }

        private static double PassiveToneRms(HeadsetProfile profile, float frequency)
        {
            int frames = Rate;
            float[] signal = new float[frames];
            for (int i = 0; i < frames; i++)
                signal[i] = 0.5f * (float)Math.Sin(2 * Math.PI * frequency * i / Rate);
            new HeadphoneSignalChainState(Rate, profile).Process(signal, 1, electronicsPowered: false);
            double energy = 0;
            for (int i = Rate / 4; i < frames; i++) energy += signal[i] * (double)signal[i];
            return Math.Sqrt(energy / (frames - Rate / 4));
        }

        private static float[] Constant(int frames, int channels, float value)
        {
            var result = new float[frames * channels];
            for (int i = 0; i < result.Length; i++) result[i] = value;
            return result;
        }

        private static float[] ToneWithImpulse(int frames, int channels, float frequency, float amplitude)
        {
            var result = new float[frames * channels];
            for (int frame = 0; frame < frames; frame++)
                for (int channel = 0; channel < channels; channel++)
                    result[frame * channels + channel] = amplitude *
                        (float)Math.Sin(2 * Math.PI * frequency * frame / Rate);
            result[0] += 0.8f;
            return result;
        }

        private static float[] StereoTones(int frames, float left, float right, float frequency)
        {
            var result = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                float wave = (float)Math.Sin(2 * Math.PI * frequency * frame / Rate);
                result[frame * 2] = left * wave;
                result[frame * 2 + 1] = right * wave;
            }
            return result;
        }

        private static double ChannelRms(float[] values, int channels, int channel, int startFrame)
        {
            double energy = 0;
            int frames = values.Length / channels;
            for (int frame = startFrame; frame < frames; frame++)
            {
                double value = values[frame * channels + channel];
                energy += value * value;
            }
            return Math.Sqrt(energy / Math.Max(1, frames - startFrame));
        }

        private static float Min(float[] values)
        {
            float result = float.PositiveInfinity;
            foreach (float value in values) result = Math.Min(result, value);
            return result;
        }

        private static float Max(float[] values)
        {
            float result = float.NegativeInfinity;
            foreach (float value in values) result = Math.Max(result, value);
            return result;
        }

        private static float Peak(float[] values)
        {
            float result = 0;
            foreach (float value in values) result = Math.Max(result, Math.Abs(value));
            return result;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
