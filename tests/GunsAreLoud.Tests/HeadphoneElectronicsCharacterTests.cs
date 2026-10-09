using System;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    // The managed mirror of DSP v2. Same measurements as the native suite.
    [TestFixture]
    public sealed class HeadphoneElectronicsCharacterTests
    {
        private const int Rate = HeadphoneElectronicsGoldenTests.Rate;

        private static readonly HeadsetElectronicsCharacter Coloured =
            new HeadsetElectronicsCharacter(-4f, 180f, 4f, 2800f, -58f, 0.6f, 0.003f);

        [Test]
        public void ExplicitNeutralCharacterRendersExactlyLikeThePrototypeSignature()
        {
            HeadsetElectronicsProfile legacy = HeadphoneElectronicsGoldenTests.PrototypeElectronics();
            HeadsetElectronicsProfile explicitNeutral = With(legacy, HeadsetElectronicsCharacter.Neutral);
            foreach (string signal in HeadphoneElectronicsGoldenTests.Signals)
                Assert.That(Render(explicitNeutral, signal), Is.EqualTo(Render(legacy, signal)), signal);
        }

        [Test]
        public void ZeroCharacterIsNeutralWhateverTheProfileCarries()
        {
            HeadsetElectronicsProfile coloured = With(HeadphoneElectronicsGoldenTests.PrototypeElectronics(), Coloured);
            HeadsetElectronicsProfile off = coloured.WithCharacterScale(0f);
            Assert.That(off.Character.IsNeutral, Is.True);
            HeadsetElectronicsProfile neutral = With(coloured, HeadsetElectronicsCharacter.Neutral);
            foreach (string signal in HeadphoneElectronicsGoldenTests.Signals)
            {
                Assert.That(Render(off, signal), Is.EqualTo(Render(neutral, signal)), signal);
                if (signal != "silence")
                    Assert.That(Render(coloured, signal), Is.Not.EqualTo(Render(neutral, signal)),
                        "the comparison must be able to see colouring: " + signal);
            }
        }

        [Test]
        public void CharacterScaleFollowsTheF12Definition()
        {
            HeadsetElectronicsCharacter doubled = Coloured.Scaled(2f);
            Assert.That(doubled.PresenceDb, Is.EqualTo(8f));
            Assert.That(doubled.LowShelfDb, Is.EqualTo(-8f));
            Assert.That(doubled.Saturation, Is.EqualTo(1f), "saturation clamps to the DSP range");
            Assert.That(doubled.NoiseDbFs, Is.EqualTo(-58f + 20f * (float)Math.Log10(2)).Within(1e-4f));
            Assert.That(doubled.DelaySeconds, Is.EqualTo(Coloured.DelaySeconds), "delay is not a colour");
            Assert.That(doubled.PresenceHz, Is.EqualTo(Coloured.PresenceHz));
            Assert.That(doubled.LowShelfHz, Is.EqualTo(Coloured.LowShelfHz));

            HeadsetElectronicsCharacter half = Coloured.Scaled(0.5f);
            Assert.That(half.PresenceDb, Is.EqualTo(2f));
            Assert.That(half.NoiseDbFs, Is.EqualTo(-58f - 6.0206f).Within(1e-3f));
            Assert.That(Coloured.Scaled(1f).PresenceDb, Is.EqualTo(Coloured.PresenceDb));
            Assert.That(new HeadsetElectronicsCharacter(-12f, 200f, 12f, 3200f, -30f, 1f, 0.008f).Scaled(2f).PresenceDb,
                Is.EqualTo(12f));
            Assert.That(new HeadsetElectronicsCharacter(0f, 200f, 0f, 3200f, -35f, 0f, 0f).Scaled(2f).NoiseDbFs,
                Is.EqualTo(-30f), "noise clamps to the DSP range");

            foreach (float scale in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                Assert.That(Coloured.Scaled(scale).IsNeutral, Is.True, scale.ToString());
            Assert.That(HeadsetElectronicsCharacter.Neutral.Scaled(2f).IsNeutral, Is.True,
                "a profile without noise gains none at 200 %");
        }

        [Test]
        public void CharacterValuesAreBoundedToTheNativeRanges()
        {
            var wild = new HeadsetElectronicsCharacter(6f, 5f, -3f, 90000f, 10f, 3f, 1f);
            Assert.That(wild.LowShelfDb, Is.EqualTo(0f));
            Assert.That(wild.LowShelfHz, Is.EqualTo(60f));
            Assert.That(wild.PresenceDb, Is.EqualTo(0f));
            Assert.That(wild.PresenceHz, Is.EqualTo(6000f));
            Assert.That(wild.NoiseDbFs, Is.EqualTo(-30f));
            Assert.That(wild.Saturation, Is.EqualTo(1f));
            Assert.That(wild.DelaySeconds, Is.EqualTo(0.008f));
            var broken = new HeadsetElectronicsCharacter(float.NaN, float.NaN, float.NaN, float.NaN,
                float.NaN, float.NaN, float.NaN);
            Assert.That(broken.IsNeutral, Is.True);
        }

        [Test]
        public void SelfNoiseHasTheRequestedRmsAndIndependentChannels()
        {
            HeadsetElectronicsProfile profile = Linear(new HeadsetElectronicsCharacter(0f, 200f, 0f, 3200f, -60f, 0f, 0f));
            float[] output = Render(profile, new float[Rate * 2 * 2]);
            Assert.That(Db(ChannelRms(output, Rate, 0)), Is.EqualTo(-60).Within(0.5));
            Assert.That(Db(ChannelRms(output, Rate, 1)), Is.EqualTo(-60).Within(0.5));
            double cross = 0, left = 0, right = 0;
            for (int frame = Rate; frame < output.Length / 2; frame++)
            {
                cross += output[frame * 2] * (double)output[frame * 2 + 1];
                left += output[frame * 2] * (double)output[frame * 2];
                right += output[frame * 2 + 1] * (double)output[frame * 2 + 1];
            }
            Assert.That(Math.Abs(cross / Math.Sqrt(left * right)), Is.LessThan(0.02));
        }

        [Test]
        public void SelfNoiseFollowsQuietGainAndSinksUnderCompression()
        {
            var noisy = new HeadsetElectronicsCharacter(0f, 200f, 0f, 3200f, -50f, 0f, 0f);
            HeadsetElectronicsProfile profile = With(HeadphoneElectronicsGoldenTests.PrototypeElectronics(), noisy, ceiling: 1f);
            float[] idle = Render(profile, new float[Rate * 2]);
            float[] loudLeft = new float[Rate * 2];
            for (int frame = 0; frame < Rate; frame++)
                loudLeft[frame * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * frame / Rate));
            float[] compressed = Render(profile, loudLeft);

            Assert.That(Db(ChannelRms(idle, Rate / 2, 1)), Is.EqualTo(-50 + 6).Within(0.5),
                "noise enters before the quiet gain");
            Assert.That(Db(ChannelRms(compressed, Rate / 2, 1)),
                Is.LessThan(Db(ChannelRms(idle, Rate / 2, 1)) - 10), "the linked detector compresses the noise too");
        }

        [Test]
        public void PresenceAndShelfProduceTheirDesignedGains()
        {
            HeadsetElectronicsProfile flat = Linear(HeadsetElectronicsCharacter.Neutral);
            HeadsetElectronicsProfile presence = Linear(new HeadsetElectronicsCharacter(0f, 200f, 6f, 3200f, -120f, 0f, 0f));
            HeadsetElectronicsProfile shelf = Linear(new HeadsetElectronicsCharacter(-6f, 200f, 0f, 3200f, -120f, 0f, 0f));

            Assert.That(ToneDb(presence, 3200) - ToneDb(flat, 3200), Is.EqualTo(6).Within(0.3));
            Assert.That(ToneDb(presence, 200) - ToneDb(flat, 200), Is.EqualTo(0).Within(0.3));
            Assert.That(ToneDb(shelf, 200) - ToneDb(flat, 200), Is.EqualTo(-3).Within(0.3));
            Assert.That(ToneDb(shelf, 40) - ToneDb(flat, 40), Is.EqualTo(-6).Within(0.5));
            Assert.That(ToneDb(shelf, 4000) - ToneDb(flat, 4000), Is.EqualTo(0).Within(0.1));
        }

        [Test]
        public void BandOrderTwoGivesButterworthEdgesAtTheSameCorners()
        {
            HeadsetElectronicsProfile first = Linear(HeadsetElectronicsCharacter.Neutral, 200f, 4000f, 1);
            HeadsetElectronicsProfile steep = Linear(HeadsetElectronicsCharacter.Neutral, 200f, 4000f, 2);
            double reference = ToneDb(steep, 900);
            Assert.That(ToneDb(steep, 4000) - reference, Is.EqualTo(-3).Within(0.3));
            // Bilinear Butterworth: the analog ratio f/fc becomes tan(pi f/fs) / tan(pi fc/fs).
            double warped = Math.Tan(Math.PI * 8000 / Rate) / Math.Tan(Math.PI * 4000 / Rate);
            Assert.That(ToneDb(steep, 8000) - reference, Is.EqualTo(-10 * Math.Log10(1 + Math.Pow(warped, 4))).Within(0.3));
            Assert.That(ToneDb(steep, 200) - reference, Is.EqualTo(-3).Within(0.3));
            Assert.That(ToneDb(steep, 100) - reference, Is.EqualTo(-12.3).Within(0.8));
            Assert.That(ToneDb(steep, 8000), Is.LessThan(ToneDb(first, 8000) - 4), "steeper than the first-order edge");
            Assert.That(steep.WithCharacterScale(0f).MicFilterOrder, Is.EqualTo(2), "the band is the device's, not a colour");
        }

        [Test]
        public void SaturationIsMonotonicAndNeverReachesPastTheCeiling()
        {
            for (double x = -2; x <= 2; x += 1.0 / 2048)
                Assert.That(HeadphoneElectronicPath.Limit(x, 0.5, 0), Is.EqualTo(Math.Max(-0.5, Math.Min(0.5, x))));
            foreach (double s in new[] { 0.2, 0.6, 1.0 })
            {
                double previous = -1;
                for (double x = 0; x <= 4; x += 1.0 / 4096)
                {
                    double y = HeadphoneElectronicPath.Limit(x, 0.5, s);
                    Assert.That(y, Is.GreaterThanOrEqualTo(previous));
                    Assert.That(y, Is.LessThanOrEqualTo(0.5));
                    Assert.That(HeadphoneElectronicPath.Limit(-x, 0.5, s), Is.EqualTo(-y));
                    previous = y;
                }
                Assert.That(HeadphoneElectronicPath.Limit(0.5 * (1 - s) * 0.99, 0.5, s), Is.EqualTo(0.5 * (1 - s) * 0.99));
            }

            HeadsetElectronicsProfile driven = new HeadsetElectronicsProfile(24f, 0f, 0f, 1f,
                0.0005f, 0.010f, 0.150f, 0.4f, 100f, 10000f, true,
                HeadsetEvidence.Proposed, HeadsetEvidence.Proposed,
                new HeadsetElectronicsCharacter(0f, 200f, 0f, 3200f, -120f, 0.6f, 0f), HeadsetEvidence.Proposed);
            float[] output = Render(driven, HeadphoneElectronicsGoldenTests.Signal("sine1k-20dBFS"));
            Assert.That(output, Is.All.Matches<float>(v => !float.IsNaN(v) && Math.Abs(v) <= 0.4f));
        }

        [TestCase(44100, 0.002f)]
        [TestCase(48000, 0.002f)]
        [TestCase(96000, 0.008f)]
        public void DelayShiftsTheElectronicOutputByWholeSamples(int rate, float seconds)
        {
            HeadsetElectronicsProfile plain = HeadphoneElectronicsGoldenTests.PrototypeElectronics();
            HeadsetElectronicsProfile late = With(plain, new HeadsetElectronicsCharacter(0f, 200f, 0f, 3200f, -120f, 0f, seconds));
            float[] impulse = new float[rate * 2];
            impulse[0] = impulse[1] = 1f;
            float[] reference = Render(plain, impulse, rate), delayed = Render(late, impulse, rate);
            int shift = (int)Math.Round(seconds * (double)rate, MidpointRounding.AwayFromZero);
            for (int frame = 0; frame < rate; frame++)
                for (int channel = 0; channel < 2; channel++)
                    Assert.That(delayed[frame * 2 + channel],
                        Is.EqualTo(frame < shift ? 0f : reference[(frame - shift) * 2 + channel]));
        }

        [Test]
        public void NonFiniteInputDoesNotPoisonAnyCharacterStage()
        {
            HeadsetElectronicsProfile coloured = With(HeadphoneElectronicsGoldenTests.PrototypeElectronics(), Coloured);
            float[] signal = HeadphoneElectronicsGoldenTests.Signal("pink-12dBFS");
            signal[200] = float.NaN; signal[201] = float.NegativeInfinity; signal[4000] = float.PositiveInfinity;
            float[] output = Render(coloured, signal);
            Assert.That(output, Is.All.Matches<float>(v => !float.IsNaN(v) && !float.IsInfinity(v)));
            Assert.That(ChannelRms(output, Rate / 2, 0), Is.GreaterThan(0.001));
        }

        [Test]
        public void ResetRestartsNoiseFiltersAndDelayDeterministically()
        {
            HeadsetElectronicsProfile coloured = With(HeadphoneElectronicsGoldenTests.PrototypeElectronics(), Coloured);
            float[] signal = HeadphoneElectronicsGoldenTests.Signal("pink-12dBFS");
            var path = new HeadphoneElectronicPath(Rate, coloured);
            float[] first = Run(path, signal);
            path.Reset();
            Assert.That(Run(path, signal), Is.EqualTo(first));
        }

        private static HeadsetElectronicsProfile With(HeadsetElectronicsProfile source,
            HeadsetElectronicsCharacter character, float? ceiling = null) =>
            new HeadsetElectronicsProfile(source.QuietGainDb, source.ThresholdDbFs, source.KneeDb, source.Ratio,
                source.AttackSeconds, source.HoldSeconds, source.ReleaseSeconds, ceiling ?? source.OutputCeiling,
                source.MicHighpassHz, source.MicLowpassHz, source.StereoLinked, source.DynamicsEvidence,
                source.ResponseEvidence, character, source.NoiseEvidence);

        // No dynamics and no band limits: a level change belongs to the character stage alone.
        private static HeadsetElectronicsProfile Linear(HeadsetElectronicsCharacter character,
            float highpassHz = 1f, float lowpassHz = 22000f, int bandOrder = 1) =>
            new HeadsetElectronicsProfile(0f, 0f, 0f, 1f, 0.0005f, 0.010f, 0.150f, 1f, highpassHz, lowpassHz, true,
                HeadsetEvidence.Proposed, HeadsetEvidence.Proposed, character, HeadsetEvidence.Proposed, null, bandOrder);

        private static float[] Render(HeadsetElectronicsProfile profile, string signal) =>
            HeadphoneElectronicsGoldenTests.Render(profile, HeadphoneElectronicsGoldenTests.Signal(signal));

        private static float[] Render(HeadsetElectronicsProfile profile, float[] stereo, int rate = Rate) =>
            Run(new HeadphoneElectronicPath(rate, profile), stereo);

        private static float[] Run(HeadphoneElectronicPath path, float[] stereo)
        {
            var output = new float[stereo.Length];
            for (int frame = 0; frame < stereo.Length / 2; frame++)
            {
                path.BeginFrame(stereo[frame * 2], stereo[frame * 2 + 1]);
                output[frame * 2] = path.ProcessSample(stereo[frame * 2], 0);
                output[frame * 2 + 1] = path.ProcessSample(stereo[frame * 2 + 1], 1);
            }
            return output;
        }

        private static double ToneDb(HeadsetElectronicsProfile profile, double hz)
        {
            var tone = new float[Rate * 2];
            for (int frame = 0; frame < Rate; frame++)
                tone[frame * 2] = tone[frame * 2 + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * hz * frame / Rate));
            return Db(ChannelRms(Render(profile, tone), Rate / 2, 0));
        }

        private static double ChannelRms(float[] values, int startFrame, int channel)
        {
            double energy = 0;
            int frames = values.Length / 2;
            for (int frame = startFrame; frame < frames; frame++) energy += values[frame * 2 + channel] * (double)values[frame * 2 + channel];
            return Math.Sqrt(energy / Math.Max(1, frames - startFrame));
        }

        private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-30, value));
    }
}
