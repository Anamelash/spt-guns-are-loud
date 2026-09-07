using System;
using System.Reflection;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public class AutomaticLowBandRegressionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void CancellingLiveCaptureBeforeFallbackRejectsAddedBassButKeepsCompletedRawAudio(bool complete)
        {
            var capture = (AutomaticBeatCapture)FormatterServices.GetUninitializedObject(typeof(AutomaticBeatCapture));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(AutomaticBeatCapture);
            var pcm = new float[4];
            type.GetField("_state", flags).SetValue(capture, 1);
            type.GetField("_targetFrames", flags).SetValue(capture, 4);
            type.GetField("_targetChannels", flags).SetValue(capture, 1);
            type.GetField("_pcm", flags).SetValue(capture, pcm);
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), capture,
                type.GetMethod("OnAudioFilterRead", flags));
            float[] raw = complete ? new[] { 0.1f, 0.2f, 0.3f, 0.4f } : new[] { 0.1f, 0.2f };
            callback(raw, 1);
            capture.Cancel(); // FallbackChannel cancels the downstream tap before enabling its addition.
            callback(new[] { 0.8f, 0.9f, 1f, 1f }, 1);
            Assert.That(pcm, Is.EqualTo(complete ? raw : new[] { 0.1f, 0.2f, 0f, 0f }));
            Assert.That(type.GetField("_state", flags).GetValue(capture), Is.EqualTo(complete ? 2 : 0));
            Assert.That(type.GetField("_pcm", flags).GetValue(capture), complete ? Is.SameAs(pcm) : Is.Null);
        }

        [Test]
        public void CacheBecomingReadyLetsTheFallbackFinishThenReturnsDrySamples()
        {
            var filter = (LocalGunshotImpactFilter)FormatterServices.GetUninitializedObject(typeof(LocalGunshotImpactFilter));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LocalGunshotImpactFilter).GetField("_processor", flags).SetValue(filter, new AutomaticLowBandProcessor());
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), filter,
                typeof(LocalGunshotImpactFilter).GetMethod("OnAudioFilterRead", flags));
            filter.Configure(0.2f, 99, 48000, armOnset: false);
            filter.BeginStream(10);
            filter.Trigger(10);
            filter.FinishAfterCurrentEnvelopes(10);
            filter.FinishAfterCurrentEnvelopes(10.1); // cache-ready notification must not extend it
            filter.ExpireAt(10.1);
            float[] attack = new float[512];
            for (int i = 0; i < attack.Length; i++) attack[i] = 0.25f;
            callback(attack, 1);
            Assert.That(attack[100], Is.GreaterThan(0.25f));
            filter.ExpireAt(10.15);
            float[] dry = { -1.4f, 0.25f, 1.4f };
            callback(dry, 1);
            Assert.That(dry, Is.EqualTo(new[] { -1.4f, 0.25f, 1.4f }));
            Assert.That(filter.NeedsStreamReset, Is.True);
        }

        [Test]
        public void ActualFilterZeroGainBypassesAndResumesOnlyWithFreshStreamAnchor()
        {
            var filter = (LocalGunshotImpactFilter)FormatterServices.GetUninitializedObject(
                typeof(LocalGunshotImpactFilter));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LocalGunshotImpactFilter).GetField("_processor", flags)
                .SetValue(filter, new AutomaticLowBandProcessor());
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>),
                filter, typeof(LocalGunshotImpactFilter).GetMethod("OnAudioFilterRead", flags));
            filter.Configure(0.2f, 99f, 48000, armOnset: false);
            filter.BeginStream(10);
            filter.Trigger(10);
            var initial = new float[512];
            for (int i = 0; i < initial.Length; i++) initial[i] = 0.25f;
            callback(initial, 1);
            Assert.That(initial[100], Is.GreaterThan(0.25f));

            filter.Configure(0f, 99f, 48000, armOnset: false);
            float[] dry = { -1.4f, 0.25f, 1.4f };
            callback(dry, 1);
            Assert.That(dry, Is.EqualTo(new[] { -1.4f, 0.25f, 1.4f }));
            filter.Configure(0.2f, 99f, 48000, armOnset: false);
            Assert.That(filter.NeedsStreamReset, Is.True);
            filter.BeginStream(20);
            Assert.That(filter.NeedsStreamReset, Is.False);
            filter.Trigger(20);
            var resumed = new float[512];
            for (int i = 0; i < resumed.Length; i++) resumed[i] = 0.25f;
            callback(resumed, 1);
            Assert.That(resumed, Is.EqualTo(initial));
        }

        [TestCase(44100)] [TestCase(48000)] [TestCase(96000)]
        public void TimedEventsProduceFilteredOutputForEveryShot(int rate)
        {
            var processor = Create(rate);
            processor.Trigger(0, 1f, 0f);
            processor.Trigger(0.1, 1f, 0f);
            processor.Trigger(0.2, 1f, 0f);
            float[] energy = ProcessSegments(processor, rate, rate * 3 / 10, 1);
            Assert.That(energy, Is.All.GreaterThan(0.001f));
        }

        [Test]
        public void OverlapUsesIndependentGainSnapshotsAndDoesNotResetState()
        {
            const int rate = 48000;
            var overlap = Create(rate); var single = Create(rate);
            overlap.Trigger(0, 0.5f, 0f); overlap.Trigger(0.06, 1f, 0f);
            single.Trigger(0, 0.5f, 0f);
            Assert.That(ProcessPeak(overlap, rate, rate * 12 / 100, 2),
                Is.GreaterThan(ProcessPeak(single, rate, rate * 12 / 100, 2) * 1.05f));
        }

        [Test]
        public void PositiveDampingMatchesOpenVoiceThroughHoldThenReducesTail()
        {
            const int rate = 48000;
            var open = Create(rate); var damped = Create(rate);
            open.Trigger(0, 1f, 0f); damped.Trigger(0, 1f, 120f);
            float openHold = ProcessPeak(open, rate, rate * 6 / 100, 1);
            float dampedHold = ProcessPeak(damped, rate, rate * 6 / 100, 1);
            Assert.That(dampedHold, Is.EqualTo(openHold).Within(0.00001f));
            ProcessPeak(open, rate, rate * 2 / 100, 1);
            ProcessPeak(damped, rate, rate * 2 / 100, 1);
            float openTail = ProcessPeak(open, rate, rate * 2 / 100, 1);
            float dampedTail = ProcessPeak(damped, rate, rate * 2 / 100, 1);
            Assert.That(dampedTail, Is.LessThan(openTail * 0.95f));
        }

        [Test]
        public void DisableGenerationIsConsumedOnAudioThreadAndMakesOutputNeutral()
        {
            const int rate = 48000;
            var processor = Create(rate); processor.Trigger(0, 1f, 0f);
            ProcessPeak(processor, rate, 256, 1);
            int generation = processor.Generation; processor.Disable();
            Assert.That(processor.Generation, Is.GreaterThan(generation));
            Assert.That(ProcessPeak(processor, rate, 1024, 1), Is.Zero);
        }

        [Test]
        public void ResetAnchorsFirstEventToStreamFrameZero()
        {
            const int rate = 48000;
            var processor = new AutomaticLowBandProcessor();
            processor.RequestReset(12.5, true);
            processor.Configure(rate, Coefficient(rate, 250), Coefficient(rate, 38));
            processor.Trigger(12.5, 1f, 0f);
            float exactEnergy = ProcessConstantEnergy(processor, 128);

            var delayed = new AutomaticLowBandProcessor();
            delayed.RequestReset(12.5, true);
            delayed.Configure(rate, Coefficient(rate, 250), Coefficient(rate, 38));
            delayed.Trigger(12.51, 1f, 0f);
            float delayedEnergy = ProcessConstantEnergy(delayed, 128);
            Assert.That(exactEnergy, Is.GreaterThan(0.0001f));
            Assert.That(delayedEnergy, Is.Zero);
        }

        [Test]
        public void DisableThenReenableBeforeCallbackCannotReplayOldGenerationTrigger()
        {
            const int rate = 48000;
            var processor = Create(rate);
            processor.Trigger(0, 1f, 0f);
            processor.Disable();
            processor.Configure(rate, Coefficient(rate, 250), Coefficient(rate, 38));

            Assert.That(ProcessPeak(processor, rate, 1024, 1), Is.Zero);
        }

        [Test]
        public void SaturatedQueueCannotLoseGenerationResetOrStreamAnchor()
        {
            const int rate = 48000;
            var processor = Create(rate);
            for (int i = 0; i < AutomaticLowBandProcessor.EventCapacity + 8; i++)
                processor.Trigger(i / 1000.0, 1f, 0f);
            processor.RequestReset(50.0, true);

            processor.BeginFrame();
            Assert.That(processor.FrameCursor, Is.Zero);
            Assert.That(processor.DroppedEnvelopeCount, Is.Zero);
            processor.EndFrame();
        }

        [Test]
        public void FutureGenerationEventWaitsUntilConsumerAppliesItsReset()
        {
            const int rate = 48000;
            var processor = Create(rate);
            processor.ConsumeControlRequests();
            processor.ConsumeQueuedEvents();

            processor.RequestReset(4.0, true);
            processor.Configure(rate, Coefficient(rate, 250), Coefficient(rate, 38));
            processor.Trigger(4.0, 1f, 0f);
            processor.ConsumeQueuedEvents();
            Assert.That(ProcessConstantEnergy(processor, 128), Is.GreaterThan(0.0001f));
        }

        private static AutomaticLowBandProcessor Create(int rate)
        {
            var p = new AutomaticLowBandProcessor();
            p.RequestReset(0, true);
            p.Configure(rate, Coefficient(rate, 250), Coefficient(rate, 38)); return p;
        }
        private static float Coefficient(int rate, float hz) =>
            LocalGunshotImpactFilter.CalculateLowpassCoefficient(rate, hz);

        private static float[] ProcessSegments(AutomaticLowBandProcessor p, int rate, int frames, int channels)
        {
            var energy = new float[3]; int segment = frames / 3;
            for (int frame = 0; frame < frames; frame++)
            {
                p.BeginFrame(); float input = Sine(rate, p.FrameCursor);
                for (int c = 0; c < channels; c++) energy[Math.Min(2, frame / segment)] +=
                    Math.Abs(p.ProcessSample(input, c, channels));
                p.EndFrame();
            }
            return energy;
        }
        private static float ProcessPeak(AutomaticLowBandProcessor p, int rate, int frames, int channels)
        {
            float peak = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                p.BeginFrame(); float input = Sine(rate, p.FrameCursor);
                for (int c = 0; c < channels; c++) peak = Math.Max(peak, Math.Abs(p.ProcessSample(input, c, channels)));
                p.EndFrame();
            }
            return peak;
        }
        private static float Sine(int rate, long frame) =>
            0.25f * (float)Math.Sin(2.0 * Math.PI * 100.0 * frame / rate);

        private static float ProcessConstantEnergy(AutomaticLowBandProcessor processor, int frames)
        {
            float energy = 0f;
            for (int frame = 0; frame < frames; frame++)
            {
                processor.BeginFrame();
                energy += Math.Abs(processor.ProcessSample(0.25f, 0, 1));
                processor.EndFrame();
            }
            return energy;
        }
    }
}
