using System;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Stage 4.7 of the 1.0.1 performance plan: the output ceiling stops being a
    /// hard cut. These fix the two properties that make that safe to ship — the
    /// output is still bounded, and anything below the knee is not touched at all.
    /// </summary>
    [TestFixture]
    public sealed class MasterLimiterTests
    {
        private const int Rate = 48000;

        [Test]
        public void ALoudReportLeavesBelowFullScaleInsteadOfSquaredOff()
        {
            var limiter = new MasterLimiterState();
            float[] buffer = Report(peak: 1.8f);
            float arrived = Peak(buffer);
            float[] clipped = HardClip(buffer);

            int engaged = limiter.Process(
                buffer, 2, Rate, out float inputPeak, out float deepestGain);

            Assert.That(engaged, Is.GreaterThan(0));
            Assert.That(arrived, Is.GreaterThan(1f));
            Assert.That(inputPeak, Is.EqualTo(arrived).Within(0.0001f),
                "The reported peak is the one that arrived, not the one that left.");
            Assert.That(deepestGain, Is.LessThan(1f));
            Assert.That(Peak(buffer), Is.LessThanOrEqualTo(1f),
                "Nothing may leave above full scale.");
            // A hard cut holds several consecutive samples at exactly the ceiling;
            // that flat top is the crackle. The limiter must not produce one.
            Assert.That(LongestFlatTop(buffer), Is.LessThan(LongestFlatTop(clipped)));
        }

        [Test]
        public void NoSampleCanLeaveAboveTheCeilingWhateverArrives()
        {
            var limiter = new MasterLimiterState();
            var random = new Random(20260912);
            for (int block = 0; block < 40; block++)
            {
                var buffer = new float[512];
                float scale = 0.2f + block * 0.35f;
                for (int index = 0; index < buffer.Length; index++)
                    buffer[index] = (float)(random.NextDouble() * 2.0 - 1.0) * scale;

                limiter.Process(buffer, 2, Rate, out _, out _);

                Assert.That(Peak(buffer), Is.LessThanOrEqualTo(1f),
                    $"block {block} at scale {scale}");
            }
        }

        [Test]
        public void OrdinaryGameAudioPassesThroughUntouched()
        {
            var limiter = new MasterLimiterState();
            float[] buffer = Report(peak: MasterLimiterState.Knee - 0.01f);
            var original = (float[])buffer.Clone();

            int engaged = limiter.Process(buffer, 2, Rate, out _, out float deepestGain);

            Assert.That(engaged, Is.Zero, "Below the knee the limiter does no work.");
            Assert.That(deepestGain, Is.EqualTo(1f));
            Assert.That(buffer, Is.EqualTo(original),
                "A buffer under the knee must come out bit-exact — this is every " +
                "sound in the game that is not a close shot, and every shot heard " +
                "through a headset.");
        }

        [Test]
        public void TheGainIsSharedByBothChannelsSoTheImageDoesNotShift()
        {
            var limiter = new MasterLimiterState();
            // Left is loud, right is quiet. Per-channel limiting would pull the
            // image left by leaving the right channel alone.
            var buffer = new float[] { 1.6f, 0.4f, 1.6f, 0.4f };

            limiter.Process(buffer, 2, Rate, out _, out _);

            Assert.That(buffer[1] / buffer[0], Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(buffer[3] / buffer[2], Is.EqualTo(0.25f).Within(1e-5f));
        }

        [Test]
        public void GainIsGivenBackGraduallyNotAtTheNextBuffer()
        {
            var limiter = new MasterLimiterState();
            limiter.Process(Report(peak: 2.0f), 2, Rate, out _, out _);
            float afterShot = limiter.Gain;
            Assert.That(afterShot, Is.LessThan(1f));

            var quiet = new float[512];
            for (int index = 0; index < quiet.Length; index++) quiet[index] = 0.1f;
            limiter.Process(quiet, 2, Rate, out _, out _);

            Assert.That(limiter.Gain, Is.GreaterThan(afterShot),
                "The gain recovers…");
            Assert.That(limiter.Gain, Is.LessThan(1f),
                "…but not inside one buffer: a step back to unity is a click.");
        }

        [Test]
        public void ATransferWithoutAStepIsWhatMakesItSoftRatherThanACut()
        {
            // Continuity at the knee: a sample just under it and a sample just
            // over it must not land on visibly different gains.
            var below = new float[] { MasterLimiterState.Knee - 0.0005f, 0f };
            var above = new float[] { MasterLimiterState.Knee + 0.0005f, 0f };
            new MasterLimiterState().Process(below, 2, Rate, out _, out _);
            new MasterLimiterState().Process(above, 2, Rate, out _, out _);

            Assert.That(above[0] - below[0], Is.EqualTo(0.001f).Within(0.0002f));
        }

        [Test]
        public void NonFiniteSamplesAreReplacedRatherThanMultiplied()
        {
            var limiter = new MasterLimiterState();
            var buffer = new float[] { float.NaN, float.PositiveInfinity, 0.2f, 0.2f };

            limiter.Process(buffer, 2, Rate, out _, out _);

            Assert.That(buffer[0], Is.EqualTo(0f));
            Assert.That(buffer[1], Is.EqualTo(1f));
            Assert.That(float.IsNaN(limiter.Gain), Is.False,
                "One bad sample must not poison the gain for the rest of the raid.");
        }

        [Test]
        public void ResetDropsTheCarriedGain()
        {
            var limiter = new MasterLimiterState();
            limiter.Process(Report(peak: 2.0f), 2, Rate, out _, out _);
            Assert.That(limiter.Gain, Is.LessThan(1f));

            limiter.Reset();

            Assert.That(limiter.Gain, Is.EqualTo(1f));
        }

        [Test]
        public void AHearingStageThatIsFollowedByTheLimiterDoesNotCutFirst()
        {
            var cutting = new HearingDspChannelState();
            var limited = new HearingDspChannelState();
            cutting.Reset();
            limited.Reset();

            // Wet zero: the stage is a pass-through apart from its ceiling.
            float cut = cutting.Process(1.6f, 0f, 1f, 1f, 0f, 0f, 1f, out _);
            float kept = limited.Process(1.6f, 0f, 1f, 1f, 0f, 0f, 1f,
                limited: true, preClamp: out _);

            Assert.That(cut, Is.EqualTo(1f), "The old behaviour, still there when the limiter is off.");
            Assert.That(kept, Is.EqualTo(1.6f),
                "With the limiter on, the ceiling belongs to the limiter alone — " +
                "cutting here first would hand it a signal already squared off.");
        }

        [Test]
        public void TheGainReturnsAllTheWayToUnityAndStopsTouchingTheBuffer()
        {
            // The release approaches unity asymptotically. Left alone it stalls a
            // hair below it, once the step it adds falls under a float's
            // resolution near 1 — and then every buffer for the rest of the
            // session is multiplied by a gain that is not quite one.
            var limiter = new MasterLimiterState();
            limiter.Process(Report(peak: 2.0f), 2, Rate, out _, out _);
            Assert.That(limiter.Gain, Is.LessThan(1f));

            var quiet = new float[1024 * 2];
            for (int index = 0; index < quiet.Length; index++) quiet[index] = 0.1f;
            int engaged = 1;
            for (int buffer = 0; buffer < 200 && engaged > 0; buffer++)
                engaged = limiter.Process(quiet, 2, Rate, out _, out _);

            Assert.That(limiter.Gain, Is.EqualTo(1f),
                "Two seconds after the shot the limiter must be out of the way.");
            var untouched = (float[])quiet.Clone();
            Assert.That(limiter.Process(quiet, 2, Rate, out _, out _), Is.Zero);
            Assert.That(quiet, Is.EqualTo(untouched));
        }

        [Test, NonParallelizable]
        public void TheUntouchedPathStaysWithinTheListenerFilterFloorCost()
        {
            // The limiter runs on every master buffer, including the ones with no
            // hearing effect at all, so the path that does nothing must cost
            // almost nothing. The bound is roughly twice what the owner's machine
            // measures, as elsewhere in the stage-4 budgets.
            const int Buffers = 4000;
            var limiter = new MasterLimiterState();
            var quiet = new float[1024 * 2];
            for (int index = 0; index < quiet.Length; index++)
                quiet[index] = 0.4f * (float)Math.Sin(index * 0.05);

            for (int index = 0; index < 50; index++)
                limiter.Process(quiet, 2, Rate, out _, out _);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < Buffers; index++)
                limiter.Process(quiet, 2, Rate, out _, out _);
            watch.Stop();

            double microsecondsPerBuffer =
                watch.Elapsed.TotalMilliseconds * 1000.0 / Buffers;
            TestContext.Progress.WriteLine($"limiter idle: {microsecondsPerBuffer:0.0} us/buffer");
            Assert.That(microsecondsPerBuffer, Is.LessThan(12.0));
        }

        private static float[] Report(float peak)
        {
            // One decaying burst, the shape of a gunshot body.
            var buffer = new float[1024];
            for (int frame = 0; frame < buffer.Length; frame += 2)
            {
                double time = frame / 2.0 / Rate;
                float envelope = (float)Math.Exp(-time * 90.0);
                float value = peak * envelope * (float)Math.Sin(2.0 * Math.PI * 120.0 * time);
                buffer[frame] = value;
                buffer[frame + 1] = value;
            }
            return buffer;
        }

        private static float[] HardClip(float[] source)
        {
            var clipped = (float[])source.Clone();
            for (int index = 0; index < clipped.Length; index++)
                clipped[index] = Math.Max(-1f, Math.Min(1f, clipped[index]));
            return clipped;
        }

        private static float Peak(float[] buffer)
        {
            float peak = 0f;
            foreach (float sample in buffer) peak = Math.Max(peak, Math.Abs(sample));
            return peak;
        }

        private static int LongestFlatTop(float[] buffer)
        {
            int longest = 0, run = 0;
            for (int index = 0; index < buffer.Length; index++)
            {
                bool flat = index > 0 && Math.Abs(buffer[index]) > 0.9f &&
                    Math.Abs(Math.Abs(buffer[index]) - Math.Abs(buffer[index - 1])) < 1e-6f;
                run = flat ? run + 1 : 0;
                if (run > longest) longest = run;
            }
            return longest;
        }
    }
}
