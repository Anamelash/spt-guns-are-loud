using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture, NonParallelizable]
    public sealed class PerformanceTraceTests
    {
        [SetUp, TearDown]
        public void Reset()
        {
            PerformanceTrace.Enabled = false;
            PerformanceTrace.ClearAudio(); PerformanceTrace.Work.Clear();
        }

        [Test]
        public void WorkCountersKeepSeparateTotalsAndMaxima()
        {
            var counters = new PerformanceCounters();
            counters.Record(PerformanceArea.Warmup, 10);
            counters.Record(PerformanceArea.Warmup, 4);
            counters.Record(PerformanceArea.ContrastHooks, 100);
            Assert.That(counters.Total[(int)PerformanceArea.Warmup], Is.EqualTo(14));
            Assert.That(counters.Maximum[(int)PerformanceArea.Warmup], Is.EqualTo(10));
            Assert.That(counters.Total[(int)PerformanceArea.ContrastHooks], Is.EqualTo(100));
            counters.Clear();
            Assert.That(counters.Total, Is.All.Zero);
            Assert.That(counters.Maximum, Is.All.Zero);
        }

        [Test]
        public void DisabledDiagnosticScopeDoesNotRecord()
        {
            using (PerformanceTrace.Measure(PerformanceArea.Hearing)) { }
            Assert.That(PerformanceTrace.Work.Total, Is.All.Zero);
            Assert.That(PerformanceTrace.Milliseconds(Stopwatch.Frequency), Is.EqualTo(1000));
        }

        [Test]
        public void BufferIdentityTelemetryDoesNotConfuseBytesWithAllocations()
        {
            float[] previous = null;
            float[] first = { 1, -1 };
            float[] second = { 0.5f, -0.5f };
            PerformanceTrace.RecordAudio(first, ref previous, Stopwatch.GetTimestamp());
            PerformanceTrace.RecordAudio(first, ref previous, Stopwatch.GetTimestamp());
            PerformanceTrace.RecordAudio(second, ref previous, Stopwatch.GetTimestamp());
            Assert.That(PerformanceTrace.AudioCallbacks, Is.EqualTo(3));
            Assert.That(PerformanceTrace.BufferChanges, Is.EqualTo(2));
            Assert.That(PerformanceTrace.AudioBytes, Is.EqualTo(24));
            Assert.That(first, Is.EqualTo(new[] { 1f, -1f }));
            PerformanceTrace.ClearAudio();
            Assert.That(PerformanceTrace.AudioCallbacks, Is.Zero);
            Assert.That(PerformanceTrace.AudioBytes, Is.Zero);
        }

        [Test]
        public void NeutralCallbackStillReportsItsExistenceUntilComponentIsDetached()
        {
            var filter = (GunshotContrastFilter)FormatterServices.GetUninitializedObject(typeof(GunshotContrastFilter));
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), filter,
                typeof(GunshotContrastFilter).GetMethod("OnAudioFilterRead", BindingFlags.NonPublic | BindingFlags.Instance));
            filter.SetGain(1);
            PerformanceTrace.Enabled = true;
            float[] samples = { 1.2f, -1.2f };
            callback(samples, 2);
            callback(samples, 2);
            Assert.That(samples, Is.EqualTo(new[] { 1.2f, -1.2f }));
            Assert.That(PerformanceTrace.AudioCallbacks, Is.EqualTo(2));
            Assert.That(PerformanceTrace.BufferChanges, Is.EqualTo(1));
            Assert.That(filter.CallbackCount, Is.Zero, "Attenuated callbacks and all callbacks are distinct counters.");
            PerformanceTrace.Enabled = false;
            callback(samples, 2);
            Assert.That(PerformanceTrace.AudioCallbacks, Is.EqualTo(2));
        }

        [Test]
        public void FrameHistogramReportsBoundedPercentilesWithoutSamplesAllocations()
        {
            var histogram = new FrameTimeHistogram();
            for (int value = 1; value <= 100; value++) histogram.Record(value);
            Assert.That(histogram.Count, Is.EqualTo(100));
            Assert.That(histogram.Percentile(.95), Is.EqualTo(95));
            Assert.That(histogram.Percentile(.99), Is.EqualTo(99));
            histogram.Clear();
            Assert.That(histogram.Count, Is.Zero);
            Assert.That(histogram.Percentile(.99), Is.Zero);
        }

        [Test]
        public void AudioTimingReportsPercentilesAndBufferFormat()
        {
            PerformanceTrace.Enabled = true;
            float[] previous = null;
            float[] samples = new float[1024];
            for (int index = 0; index < 20; index++)
                PerformanceTrace.RecordAudio(
                    samples, 2, ref previous,
                    Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10000);
            PerformanceTrace.TakeAudioTiming(out double p95, out double p99, out double maximum);
            Assert.That(p95, Is.GreaterThan(0));
            Assert.That(p99, Is.GreaterThanOrEqualTo(p95));
            Assert.That(maximum, Is.GreaterThan(0));
            Assert.That(PerformanceTrace.LastAudioSamples, Is.EqualTo(1024));
            Assert.That(PerformanceTrace.LastAudioChannels, Is.EqualTo(2));
        }
    }
}
