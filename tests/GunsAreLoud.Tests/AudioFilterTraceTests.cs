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
    public sealed class AudioFilterTraceTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp, TearDown]
        public void Reset()
        {
            PerformanceTrace.Enabled = false;
            PerformanceTrace.ClearAudio();
            PerformanceTrace.Work.Clear();
            GalSourceCensus.Clear();
        }

        private static Action<float[], int> Callback(object filter) =>
            (Action<float[], int>)Delegate.CreateDelegate(
                typeof(Action<float[], int>), filter,
                filter.GetType().GetMethod("OnAudioFilterRead", Hidden));

        [Test]
        public void DisabledTraceLeavesEveryAudioFilterCounterUntouched()
        {
            var band = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedBandPassFilter));
            band.Configure(48000, 40f, 400f);
            var buffer = new float[256];
            Callback(band)(buffer, 2);
            Assert.That(AudioFilterTrace.CallCount(AudioFilterKind.PitchedBand), Is.Zero);
            AudioFilterSample sample = AudioFilterTrace.Take(AudioFilterKind.PitchedBand);
            Assert.That(sample.Calls, Is.Zero);
            Assert.That(sample.TotalMilliseconds, Is.Zero);
        }

        [Test]
        public void EveryPitchedVoiceFilterReportsUnderItsOwnKind()
        {
            var band = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedBandPassFilter));
            band.Configure(48000, 40f, 400f);
            var tail = (PitchedGunshotTailFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedGunshotTailFilter));
            var envelope = (PitchedGunshotEnvelopeFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedGunshotEnvelopeFilter));
            PerformanceTrace.Enabled = true;
            var buffer = new float[256];
            Callback(band)(buffer, 2);
            Callback(tail)(buffer, 2);
            Callback(envelope)(buffer, 2);
            Assert.That(AudioFilterTrace.CallCount(AudioFilterKind.PitchedBand), Is.EqualTo(1));
            Assert.That(AudioFilterTrace.CallCount(AudioFilterKind.PitchedTail), Is.EqualTo(1));
            Assert.That(AudioFilterTrace.CallCount(AudioFilterKind.PitchedEnvelope), Is.EqualTo(1));
            // Bypassed tail and unconfigured envelope did no work in this buffer.
            Assert.That(AudioFilterTrace.IdleCount(AudioFilterKind.PitchedTail), Is.EqualTo(1));
            Assert.That(AudioFilterTrace.IdleCount(AudioFilterKind.PitchedBand), Is.Zero);
        }

        [Test]
        public void SampleReportsPercentileMaximumAndBufferOverrunsThenClears()
        {
            PerformanceTrace.Enabled = true;
            PerformanceTrace.SetOutputSampleRate(48000);
            long slowStart = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 100; // 10 ms.
            for (int index = 0; index < 10; index++)
                AudioFilterTrace.Record(
                    AudioFilterKind.Hearing, slowStart, samples: 256, channels: 2);
            AudioFilterSample sample = AudioFilterTrace.Take(AudioFilterKind.Hearing);
            Assert.That(sample.Calls, Is.EqualTo(10));
            Assert.That(sample.MaximumMilliseconds, Is.GreaterThanOrEqualTo(9));
            Assert.That(sample.P95Milliseconds, Is.GreaterThan(0));
            Assert.That(sample.Overruns, Is.EqualTo(10),
                "128 stereo frames at 48 kHz are 2.7 ms of audio; a 10 ms callback cannot keep up.");
            Assert.That(AudioFilterTrace.Take(AudioFilterKind.Hearing).Calls, Is.Zero);
        }

        [Test]
        public void FastCallbackWithinTheBufferBudgetIsNotAnOverrun()
        {
            PerformanceTrace.Enabled = true;
            PerformanceTrace.SetOutputSampleRate(48000);
            AudioFilterTrace.Record(
                AudioFilterKind.PitchedEnvelope, Stopwatch.GetTimestamp(),
                samples: 2048, channels: 2);
            Assert.That(AudioFilterTrace.Take(AudioFilterKind.PitchedEnvelope).Overruns, Is.Zero);
        }

        [Test]
        public void PrefixPostfixPairsCountOneCallAndSumBothHalves()
        {
            var counters = new PerformanceCounters();
            counters.Record(PerformanceArea.HookPlayOnLocal, 30);
            counters.Record(PerformanceArea.HookPlayOnLocal, 12, countCall: false);
            Assert.That(counters.Calls[(int)PerformanceArea.HookPlayOnLocal], Is.EqualTo(1));
            Assert.That(counters.Total[(int)PerformanceArea.HookPlayOnLocal], Is.EqualTo(42));
            Assert.That(counters.Maximum[(int)PerformanceArea.HookPlayOnLocal], Is.EqualTo(30));
            counters.Clear();
            Assert.That(counters.Calls, Is.All.Zero);
        }

        [Test]
        public void DisabledTraceRecordsNoHookTime()
        {
            long start = PerformanceTrace.Begin();
            Assert.That(start, Is.Zero);
            PerformanceTrace.End(PerformanceArea.HookFireBullet, start);
            Assert.That(PerformanceTrace.Work.Total, Is.All.Zero);
            Assert.That(PerformanceTrace.Work.Calls, Is.All.Zero);
        }

        [Test]
        public void CensusSeparatesLiveFromEnabledInstancesAndCountsTouchedSources()
        {
            GalSourceCensus.Created(GalComponentKind.AutomaticPitched);
            GalSourceCensus.Created(GalComponentKind.AutomaticPitched);
            GalSourceCensus.Enabled(GalComponentKind.AutomaticPitched);
            GalSourceCensus.Enabled(GalComponentKind.AutomaticPitched);
            GalSourceCensus.Disabled(GalComponentKind.AutomaticPitched);
            Assert.That(GalSourceCensus.LiveCount(GalComponentKind.AutomaticPitched), Is.EqualTo(2));
            Assert.That(GalSourceCensus.ActiveCount(GalComponentKind.AutomaticPitched), Is.EqualTo(1));

            GalSourceCensus.NoteSource(11);
            Assert.That(GalSourceCensus.SourceCount, Is.Zero,
                "An untraced raid must not accumulate instance ids it never reports.");
            PerformanceTrace.Enabled = true;
            GalSourceCensus.NoteSource(11);
            GalSourceCensus.NoteSource(11);
            GalSourceCensus.NoteSource(12);
            Assert.That(GalSourceCensus.SourceCount, Is.EqualTo(2));
            Assert.That(GalSourceCensus.Format(), Does.Contain("galSources=2"));
            Assert.That(GalSourceCensus.Format(), Does.Contain("autoPitchedFilter=1/2"));

            GalSourceCensus.Destroyed(GalComponentKind.AutomaticPitched);
            GalSourceCensus.Destroyed(GalComponentKind.AutomaticPitched);
            GalSourceCensus.Disabled(GalComponentKind.AutomaticPitched);
            Assert.That(GalSourceCensus.LiveCount(GalComponentKind.AutomaticPitched), Is.Zero);
            Assert.That(GalSourceCensus.ActiveCount(GalComponentKind.AutomaticPitched), Is.Zero);
            GalSourceCensus.Clear();
            Assert.That(GalSourceCensus.SourceCount, Is.Zero);
        }
    }
}
