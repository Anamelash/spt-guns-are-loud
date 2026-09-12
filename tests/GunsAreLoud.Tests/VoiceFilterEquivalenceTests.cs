using System;
using System.Reflection;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Stage 4 of the 1.0.1 performance plan changes how the voice filters are
    /// computed, not what they produce. Every test here compares the filter output
    /// against the per-sample formula it replaced.
    /// </summary>
    [TestFixture]
    public sealed class VoiceFilterEquivalenceTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int Rate = 48000;

        private static Action<float[], int> Callback(object filter) =>
            (Action<float[], int>)Delegate.CreateDelegate(
                typeof(Action<float[], int>), filter,
                filter.GetType().GetMethod("OnAudioFilterRead", Hidden));

        [TestCase(3.5f, 35f)]
        [TestCase(0.6f, 100f)]
        [TestCase(0.12f, 5f)]
        [TestCase(1.0f, 50f)]
        public void EnvelopeSectionsReproduceThePerSampleShape(float duration, float fadePercent)
        {
            EnvelopeSections sections = EnvelopeSections.Create(duration, fadePercent, Rate);
            var ramp = new CosineRamp();
            int frames = (int)(duration * Rate) + 64;
            double worst = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                // The recurrence is re-seeded once per buffer in the filter; do the
                // same here so the test measures what the callback actually does.
                if (frame % 1024 == 0) ramp.Invalidate();
                float expected = PitchedGunshotEnvelopeFilter.CalculateEnvelope(
                    frame, Rate, duration, fadePercent);
                float actual = sections.Evaluate(frame, ref ramp);
                worst = Math.Max(worst, Math.Abs(expected - actual));
            }
            Assert.That(worst, Is.LessThan(1e-4),
                $"duration={duration} fade={fadePercent}: worst deviation {worst}");
        }

        [Test]
        public void EnvelopeFilterAppliesTheSameGainCurveAsTheFormula()
        {
            var filter = (PitchedGunshotEnvelopeFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedGunshotEnvelopeFilter));
            const float duration = 0.4f;
            const float fade = 35f;
            const float gain = 2f;
            filter.Configure(duration, fade, gain, startImmediately: true, sampleRate: Rate);
            Action<float[], int> callback = Callback(filter);

            const int channels = 2;
            const int frames = 1024;
            var data = new float[frames * channels];
            int frame = 0;
            double worst = 0;
            for (int buffer = 0; buffer < 16 && !filter.Completed; buffer++)
            {
                for (int index = 0; index < data.Length; index++) data[index] = 0.5f;
                callback(data, channels);
                for (int inner = 0; inner < frames; inner++, frame++)
                {
                    float envelope = PitchedGunshotEnvelopeFilter.CalculateEnvelope(
                        frame, Rate, duration, fade);
                    if (envelope <= 0f && frame > 0) break;
                    float expected = 0.5f * envelope * gain;
                    worst = Math.Max(worst, Math.Abs(expected - data[inner * channels]));
                }
            }
            Assert.That(worst, Is.LessThan(1e-4), $"worst deviation {worst}");
        }

        [Test]
        public void BandPassKeepsItsCoefficientsAndSettlesToSilenceAfterAnImpulse()
        {
            var filter = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedBandPassFilter));
            filter.Configure(Rate, 40f, 400f);
            Action<float[], int> callback = Callback(filter);
            var reference = new PitchedBandPassState(Rate, 40f, 400f);

            const int channels = 2;
            var data = new float[512 * channels];
            for (int index = 0; index < data.Length; index++)
                data[index] = index < 8 ? 1f : 0f;
            var expected = new float[data.Length];
            for (int frame = 0; frame + channels <= data.Length; frame += channels)
                for (int channel = 0; channel < channels; channel++)
                    expected[frame + channel] = reference.Process(data[frame + channel], channel);

            callback(data, channels);
            for (int index = 0; index < data.Length; index++)
                Assert.That(data[index], Is.EqualTo(expected[index]).Within(1e-6f),
                    $"sample {index}");

            // After the impulse has decayed the filter must not become slower than
            // it is on signal: a biquad left grinding on denormal state can cost
            // an order of magnitude more per buffer on x86.
            var buffer = new float[1024 * channels];
            for (int pass = 0; pass < 200; pass++)
            {
                Array.Clear(buffer, 0, buffer.Length);
                callback(buffer, channels);
            }
            var silent = System.Diagnostics.Stopwatch.StartNew();
            for (int pass = 0; pass < 200; pass++)
            {
                Array.Clear(buffer, 0, buffer.Length);
                callback(buffer, channels);
            }
            silent.Stop();
            var loud = System.Diagnostics.Stopwatch.StartNew();
            for (int pass = 0; pass < 200; pass++)
            {
                for (int index = 0; index < buffer.Length; index++) buffer[index] = 0.5f;
                callback(buffer, channels);
            }
            loud.Stop();
            Assert.That(silent.Elapsed.TotalMilliseconds,
                Is.LessThan(loud.Elapsed.TotalMilliseconds * 3 + 1),
                "A silent buffer must cost about what a loud one costs.");
        }

        [Test]
        public void RingingKeepsTheSameToneAsAPerSampleSine()
        {
            var processor = (HearingImpactProcessor)FormatterServices.GetUninitializedObject(
                typeof(HearingImpactProcessor));
            const float frequency = 6200f;
            processor.SetTargets(true, 1f, 1f, 0f, 0f, 20000f, 20000f, 1f, 1f,
                frequency, 0f, Rate);

            const int channels = 2;
            const int frames = 1024;
            var data = new float[frames * channels];
            // Twenty buffers: long enough for the smoothing to settle and for any
            // drift in the recurrence to show up against the exact phase.
            double phase = 0;
            double step = Math.PI * 2.0 * frequency / Rate;
            double worstCorrelation = 1;
            for (int buffer = 0; buffer < 20; buffer++)
            {
                Array.Clear(data, 0, data.Length);
                processor.ProcessAudioBufferForTests(data, channels);
                if (buffer >= 4)
                {
                    // In a silent buffer the ring is the only output, so it must
                    // stay a scaled copy of the exact sine. Correlation ignores the
                    // level the channel state has smoothed to and catches drift,
                    // a wrong step, or a phase that restarts every buffer.
                    double dot = 0, produced = 0, exact = 0;
                    for (int frame = 0; frame < frames; frame++)
                    {
                        double expected = Math.Sin(phase + step * frame);
                        double actual = data[frame * channels];
                        dot += expected * actual;
                        produced += actual * actual;
                        exact += expected * expected;
                    }
                    double correlation = dot / Math.Sqrt(Math.Max(1e-18, produced * exact));
                    worstCorrelation = Math.Min(worstCorrelation, correlation);
                }
                phase += step * frames;
            }
            Assert.That(worstCorrelation, Is.GreaterThan(0.999),
                $"The recurrence must stay on the tone; worst correlation {worstCorrelation}");
        }

        [Test, NonParallelizable]
        // Measured with GC.GetTotalMemory, which sees the whole heap: another
        // fixture allocating on its own thread at the same time lands in this
        // number and failed the run roughly once in three.
        public void ReschedulingAVoiceFilterDoesNotAllocate()
        {
            var tail = (PitchedGunshotTailFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedGunshotTailFilter));
            var band = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedBandPassFilter));
            // Two schedules fill both rotating slots; everything after that reuses
            // the delay lines and the coefficients.
            for (int warmup = 0; warmup < 4; warmup++)
            {
                tail.Configure(0.05f, 0.4f, Rate);
                band.Configure(Rate, 40f, 400f);
            }

            long before = GC.GetTotalMemory(true);
            const int schedules = 2000;
            for (int schedule = 0; schedule < schedules; schedule++)
            {
                tail.Configure(0.05f, 0.4f, Rate);
                band.Configure(Rate, 40f, 400f);
            }
            long after = GC.GetTotalMemory(false);
            TestContext.Progress.WriteLine($"per schedule: {(after - before) / (double)schedules:0} bytes");
            // The bound is what separates "reused the delay lines" from "allocated
            // a new one": a comb line is tens of kilobytes per schedule. Anything
            // in the hundreds of bytes is another fixture's allocation landing in
            // the same heap reading, which is not what this test is about.
            Assert.That((after - before) / (double)schedules, Is.LessThan(4096),
                "A burst schedules a copy per round; its comb delay lines are tens " +
                "of kilobytes and must be reused.");
        }

    }
}
