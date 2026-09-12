using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture, NonParallelizable]
    /// <summary>
    /// The per-buffer budgets stage 4 of the 1.0.1 performance plan was measured
    /// against, kept as a regression guard. The bounds are roughly twice what the
    /// owner's machine measures, so they catch a filter that goes back to a
    /// transcendental per sample without failing on a slower machine.
    /// </summary>
    public sealed class VoiceFilterBudgetTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int Frames = 1024;
        private const int Channels = 2;
        private const int Buffers = 4000;

        private static Action<float[], int> Callback(object filter) =>
            (Action<float[], int>)Delegate.CreateDelegate(
                typeof(Action<float[], int>), filter,
                filter.GetType().GetMethod("OnAudioFilterRead", Hidden));

        private static double Run(string name, object filter, Func<int, bool> reconfigure)
        {
            var data = new float[Frames * Channels];
            Action<float[], int> callback = Callback(filter);
            // Warm the JIT.
            var source = new float[data.Length];
            Fill(source);
            for (int index = 0; index < 20; index++) { Array.Copy(source, data, data.Length); callback(data, Channels); }
            reconfigure?.Invoke(-1);
            var watch = Stopwatch.StartNew();
            for (int index = 0; index < Buffers; index++)
            {
                if (reconfigure != null && reconfigure(index)) { }
                Array.Copy(source, data, data.Length);
                callback(data, Channels);
            }
            watch.Stop();
            double microsecondsPerBuffer = watch.Elapsed.TotalMilliseconds * 1000.0 / Buffers;
            TestContext.Progress.WriteLine($"{name}: {microsecondsPerBuffer:0.0} us/buffer");
            return microsecondsPerBuffer;
        }

        private static void Fill(float[] data)
        {
            for (int index = 0; index < data.Length; index++)
                data[index] = 0.4f * (float)Math.Sin(index * 0.05);
        }

        [Test]
        public void EveryVoiceFilterStaysWithinItsPerBufferBudget()
        {
            var band = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(typeof(PitchedBandPassFilter));
            band.Configure(48000, 40f, 400f);
            double bandPass = Run("bandPass", band, null);

            var tail = (PitchedGunshotTailFilter)FormatterServices.GetUninitializedObject(typeof(PitchedGunshotTailFilter));
            tail.Configure(0.05f, 0.4f, 48000);
            double tailCost = Run("tail", tail, null);

            var envelope = (PitchedGunshotEnvelopeFilter)FormatterServices.GetUninitializedObject(typeof(PitchedGunshotEnvelopeFilter));
            envelope.Configure(3.5f, 35f, 2f, startImmediately: true, sampleRate: 48000);
            double envelopeCost = Run("envelope", envelope, index =>
            {
                // Keep it inside its own envelope: a finished copy only clears.
                if (index % 150 == 0) envelope.Configure(3.5f, 35f, 2f, startImmediately: true, sampleRate: 48000);
                return true;
            });

            var hearing = (HearingImpactProcessor)FormatterServices.GetUninitializedObject(typeof(HearingImpactProcessor));
            hearing.SetTargets(true, 1f, 1f, 12f, 10f, 3000f, 2500f, 0.2f, 0.18f, 6200f, 37f, 48000);
            double hearingActive = Run("hearingActive", hearing, null);
            hearing.SetTargets(false, 0f, 0f, 0f, 0f, 20000f, 20000f, 0f, 0f, 6200f, 37f, 48000);
            double hearingIdle = Run("hearingIdle", hearing, null);

            // 1024 stereo frames are 21.3 ms of audio. The bounds are about twice
            // what the plan asked for, which is what separates "a shape evaluated
            // per buffer" from "a sine and a cosine per sample".
            Assert.Multiple(() =>
            {
                Assert.That(bandPass, Is.LessThan(20), "band-pass");
                Assert.That(tailCost, Is.LessThan(40), "tail");
                Assert.That(envelopeCost, Is.LessThan(60), "envelope");
                Assert.That(hearingActive, Is.LessThan(100), "hearing with ringing");
                Assert.That(hearingIdle, Is.LessThan(5), "hearing with nothing to do");
            });
        }
    }
}
