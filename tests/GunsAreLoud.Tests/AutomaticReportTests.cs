using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public class AutomaticReportTests
    {
        [Test]
        public void WarmupCopiesOnlyRequestedBeatAndAlwaysSilencesOutput()
        {
            var capture = new SilentPcmBuffer(2);
            float[] callback = { 1f, 2f, 3f, 4f, 99f, 99f };
            capture.ProcessAndSilence(callback, 2);
            Assert.That(capture.Samples, Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
            Assert.That(capture.Complete, Is.True);
            Assert.That(callback, Is.All.Zero);
            float[] afterCompletion = { 7f, 8f };
            capture.ProcessAndSilence(afterCompletion, 2);
            Assert.That(afterCompletion, Is.All.Zero);
            Assert.That(capture.Samples, Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));
        }

        [Test]
        public void WarmupAccumulatesBuffersAndDuplicatesMonoWithoutLeak()
        {
            var capture = new SilentPcmBuffer(3);
            float[] first = { 0.2f, 0.4f };
            float[] second = { 0.8f, 99f };
            capture.ProcessAndSilence(first, 1);
            Assert.That(capture.Complete, Is.False);
            capture.ProcessAndSilence(second, 1);
            Assert.That(capture.Samples, Is.EqualTo(new[] { 0.2f, 0.2f, 0.4f, 0.4f, 0.8f, 0.8f }));
            Assert.That(first, Is.All.Zero);
            Assert.That(second, Is.All.Zero);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void WarmupMalformedCallbackStillCannotReachListener(int channels)
        {
            var capture = new SilentPcmBuffer(2);
            float[] callback = { 1f, -1f };
            capture.ProcessAndSilence(callback, channels);
            Assert.That(callback, Is.All.Zero);
            Assert.That(capture.Complete, Is.False);
        }

        [Test]
        public void ReportContainsOneBodyThenFullAuthoredTailWithBankGain()
        {
            float[] body = { 1f, 1f, 0.4f, 0.4f };
            float[] tail = { 0.2f, 0.2f, 0.1f, 0.1f, 0.05f, 0.05f };
            float[] report = AutomaticReportPcm.Compose(body, tail, 2f, 0);
            Assert.That(report, Is.EqualTo(new[] { 1f, 1f, 0.4f, 0.4f, 0.4f, 0.4f, 0.2f, 0.2f, 0.1f, 0.1f }));
            Assert.That(body[0], Is.EqualTo(1f));
            Assert.That(tail[0], Is.EqualTo(0.2f));
        }

        [Test]
        public void SeamSmoothingDoesNotMoveBeatBoundaryOrChangeReportLength()
        {
            float[] body = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
            float[] tail = { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
            float[] report = AutomaticReportPcm.Compose(body, tail, 1f, 2);
            Assert.That(report.Length, Is.EqualTo(body.Length + tail.Length));
            Assert.That(report[0], Is.EqualTo(1f));
            Assert.That(report[6], Is.Zero);
            Assert.That(report[8], Is.Zero);
            Assert.That(report[12], Is.EqualTo(0.5f));
        }

        [Test]
        public void WarmedReportAppliesDonorPitchExactlyOnceToFullDuration()
        {
            float adjusted = PitchedGunshotLayer.CalculateCachedSourceDuration(1.6f, true, 1.025f);
            float duration = PitchedGunshotLayer.CalculatePitchedDuration(adjusted, 0.5f);
            Assert.That(duration, Is.EqualTo(1.6f / (1.025f * 0.5f)).Within(0.0001f));
            Assert.That(PitchedGunshotLayer.CalculateCachedSourceDuration(0.1f, false, 1.025f), Is.EqualTo(0.1f));
        }

        [Test]
        public void ReleaseCopyIsSkippedOnlyForTheShotThatScheduledItsOwnTail()
        {
            var first = new AutomaticShotContext { AuthoredTailScheduled = true };
            var cold = new AutomaticShotContext();
            Assert.That(first.ShouldPlayReleaseCopy, Is.False);
            Assert.That(cold.ShouldPlayReleaseCopy, Is.True);
            cold.AuthoredTailScheduled = true;
            Assert.That(cold.ShouldPlayReleaseCopy, Is.False);
        }

        [Test]
        public void QueuedBodyAndReleaseTailRetainSameShotContextAcrossLaterShots()
        {
            var body = new SuperAudioSample(null, null, 1f, 10.0, 0.0);
            var tail = new SuperAudioSample(null, null, 1f, 10.1, 11.5);
            var first = new AutomaticShotContext();
            LocalGunshotSampleRegistry.Register(body, default, first);
            LocalGunshotSampleRegistry.Register(tail, default, first);
            Assert.That(LocalGunshotSampleRegistry.TryTake(body, out _, out var bodyContext), Is.True);
            bodyContext.AuthoredTailScheduled = true;
            var nextBurst = new AutomaticShotContext();
            Assert.That(LocalGunshotSampleRegistry.TryTake(tail, out _, out var tailContext), Is.True);
            Assert.That(tailContext.ShouldPlayReleaseCopy, Is.False);
            Assert.That(nextBurst.ShouldPlayReleaseCopy, Is.True);
            Assert.That(LocalGunshotSampleRegistry.TryTake(tail, out _, out _), Is.False);
        }
    }
}
