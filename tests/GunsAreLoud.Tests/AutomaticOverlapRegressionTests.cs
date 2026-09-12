using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Sustained automatic fire used to leave one multi-second pitched report
    /// copy per bullet sounding, and a main-thread stall collapsed several late
    /// copies onto one start. Both are bounded here, in pure timing terms.
    /// </summary>
    [TestFixture]
    public sealed class AutomaticOverlapRegressionTests
    {
        // 12 semitones down doubles a composed body+tail report.
        private const float ReportSeconds = 1.6f;
        private const float PitchRatio = 0.5f;

        [Test]
        public void PitchedFullReportIsLongerThanTheIntervalBetweenRounds()
        {
            Assert.That(
                PitchedGunshotLayer.CalculateCachedOutputDuration(ReportSeconds, PitchRatio, 0f),
                Is.EqualTo(3.2f).Within(0.0001f));
        }

        [TestCase(0.1f, 6f, 0.6f, Description = "600 rpm")]
        [TestCase(0.05f, 6f, 0.3f, Description = "1200 rpm")]
        [TestCase(0.2f, 6f, 1.2f, Description = "300 rpm")]
        [TestCase(0.1f, 32f, 3.2f, Description = "budget above the report keeps it whole")]
        public void ConcurrencyIsAFixedNumberOfFireIntervalsRatherThanTheRateOfFire(
            float beatSeconds, float overlapShots, float expectedSeconds)
        {
            float duration = PitchedGunshotLayer.CalculateCachedOutputDuration(
                ReportSeconds, PitchRatio, 0f);

            Assert.That(
                PitchedGunshotLayer.CalculateAutomaticOverlapBound(duration, beatSeconds, overlapShots),
                Is.EqualTo(expectedSeconds).Within(0.0001f));
        }

        [Test]
        public void ConcurrentCopiesStayWithinTheConfiguredBudgetAtAnyRateOfFire()
        {
            for (float beat = 0.03f; beat <= 0.5f; beat += 0.01f)
            {
                float duration = PitchedGunshotLayer.CalculateAutomaticOverlapBound(
                    PitchedGunshotLayer.CalculateCachedOutputDuration(ReportSeconds, PitchRatio, 0f),
                    beat,
                    6f);
                Assert.That(duration / beat, Is.LessThanOrEqualTo(6f + 0.0001f),
                    "beat=" + beat + "s must not leave more than six copies sounding");
            }
        }

        [Test]
        public void ReleaseTailAfterTheBurstIsNotBounded()
        {
            // The trigger-release tail is a one-shot sample: it carries no loop
            // beat, so the burst budget must never shorten it.
            Assert.That(
                PitchedGunshotLayer.CalculateAutomaticOverlapBound(3.2f, 0f, 6f),
                Is.EqualTo(3.2f).Within(0.0001f));
        }

        [Test]
        public void OnTimeShotsKeepTheirOwnStartEvenOnAShortInterval()
        {
            Assert.That(
                AutomaticShotTiming.CollapsesOntoPreviousStart(10.05, 10.05, 10.0, 0.1f),
                Is.False);
        }

        [Test]
        public void FirstShotOfASequenceIsNeverTreatedAsACollapse()
        {
            Assert.That(
                AutomaticShotTiming.CollapsesOntoPreviousStart(
                    10.004, 9.9, double.NegativeInfinity, 0.1f),
                Is.False);
        }

        [Test]
        public void LateCopiesPulledOntoOneStartAreDroppedInsteadOfStacked()
        {
            // A 90 ms stall delivers three FireBullet calls at once. Every
            // requested boundary is in the past and clamps to the same lead.
            const double now = 100.0;
            const double lead = 0.004;
            double previous = double.NegativeInfinity;
            int scheduled = 0;
            foreach (double requested in new[] { 99.93, 100.0 - 0.03, 99.99 })
            {
                double resolved = AutomaticShotTiming.ResolveStart(requested, now, lead, true);
                if (AutomaticShotTiming.CollapsesOntoPreviousStart(resolved, requested, previous, 0.1f))
                {
                    continue;
                }
                scheduled++;
                previous = resolved;
            }

            Assert.That(scheduled, Is.EqualTo(1),
                "Three late copies clamped to one instant must sound as one report.");
        }

        [Test]
        public void RecoveredTimelineSchedulesNormallyAgainAfterTheStall()
        {
            double previous = 100.004;
            double requested = 100.104;
            double resolved = AutomaticShotTiming.ResolveStart(requested, 100.0, 0.004, true);

            Assert.That(resolved, Is.EqualTo(requested).Within(0.000001));
            Assert.That(
                AutomaticShotTiming.CollapsesOntoPreviousStart(resolved, requested, previous, 0.1f),
                Is.False);
        }

        private const int Rate = 48000;

        private static PitchedGunshotEnvelopeFilter WholeCopy(float seconds)
        {
            var filter = (PitchedGunshotEnvelopeFilter)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(PitchedGunshotEnvelopeFilter));
            filter.Configure(seconds, 50f, 1f, startImmediately: true, sampleRate: Rate);
            return filter;
        }

        private static System.Action<float[], int> Callback(PitchedGunshotEnvelopeFilter filter) =>
            (System.Action<float[], int>)System.Delegate.CreateDelegate(
                typeof(System.Action<float[], int>), filter,
                typeof(PitchedGunshotEnvelopeFilter).GetMethod(
                    "OnAudioFilterRead",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));

        // Whole callbacks of a constant stereo input; returns the left output per frame.
        private static float[] Render(System.Action<float[], int> callback, int frames)
        {
            const int buffer = 512;
            var output = new float[frames];
            for (int cursor = 0; cursor < frames; cursor += buffer)
            {
                int count = System.Math.Min(buffer, frames - cursor);
                var data = new float[count * 2];
                for (int index = 0; index < data.Length; index++) data[index] = 0.25f;
                callback(data, 2);
                for (int frame = 0; frame < count; frame++) output[cursor + frame] = data[frame * 2];
            }
            return output;
        }

        [Test]
        public void CopyWithoutASuccessorKeepsItsWholeRecordedTail()
        {
            // A single shot, or the last round of a burst: nothing asks it to
            // release, so the 3.5 s pitched report still sounds well past the
            // 0.6 s window an earlier candidate cut every copy to.
            PitchedGunshotEnvelopeFilter copy = WholeCopy(3.5f);
            float[] output = Render(Callback(copy), (int)(Rate * 1.2f));

            Assert.That(output[(int)(Rate * 0.7f)], Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(output[output.Length - 1], Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(copy.Completed, Is.False);
        }

        [Test]
        public void EarlierCopyFadesSmoothlyAndEndsInsideItsWindowWhenARoundFollows()
        {
            PitchedGunshotEnvelopeFilter copy = WholeCopy(3.5f);
            System.Action<float[], int> callback = Callback(copy);
            int offset = (int)(Rate * 0.1f);
            Render(callback, offset);

            // The next round arrives one beat later: release over the last half
            // of a six-beat window, matching the mid-burst sound already tested.
            copy.ScheduleRelease(0.3f, 0.3f);
            float[] output = Render(callback, (int)(Rate * 0.6f));

            Assert.That(output[(int)(Rate * 0.29f) - offset], Is.EqualTo(0.25f).Within(0.00001f),
                "Nothing changes before the release starts.");
            Assert.That(output[(int)(Rate * 0.45f) - offset],
                Is.EqualTo(0.25f * (float)System.Math.Cos(System.Math.PI / 4)).Within(0.0005f),
                "The release is the same equal-power curve as the envelope's own fade.");
            Assert.That(output[output.Length - 1], Is.Zero);
            Assert.That(copy.Completed, Is.True);

            float largestStep = 0f;
            for (int frame = 1; frame < output.Length; frame++)
                largestStep = System.Math.Max(largestStep, System.Math.Abs(output[frame] - output[frame - 1]));
            Assert.That(largestStep, Is.LessThan(0.001f), "A release must not click.");
        }

        [Test]
        public void ReleaseAimedAtAFinishedCopyNeverShortensTheVoiceThatReusesIt()
        {
            PitchedGunshotEnvelopeFilter channel = WholeCopy(3.5f);
            System.Action<float[], int> callback = Callback(channel);
            Render(callback, (int)(Rate * 0.05f));
            channel.ScheduleRelease(0f, 0.02f);

            // The pooled voice is re-issued before the audio thread saw the request.
            channel.Configure(3.5f, 50f, 1f, startImmediately: true, sampleRate: Rate);
            float[] output = Render(callback, (int)(Rate * 0.4f));

            Assert.That(output[output.Length - 1], Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(channel.Completed, Is.False);
        }

        [Test]
        public void ReleaseGainIsEqualPowerAndEndsInSilence()
        {
            Assert.That(PitchedGunshotEnvelopeFilter.CalculateReleaseGain(0, 1000), Is.EqualTo(1f));
            Assert.That(PitchedGunshotEnvelopeFilter.CalculateReleaseGain(500, 1000),
                Is.EqualTo((float)System.Math.Cos(System.Math.PI / 4)).Within(0.0001f));
            Assert.That(PitchedGunshotEnvelopeFilter.CalculateReleaseGain(1000, 1000), Is.Zero);
            Assert.That(PitchedGunshotEnvelopeFilter.CalculateReleaseGain(5, 0), Is.Zero);
        }

        [TestCase(0.6f, 50f, 0.3f)]
        [TestCase(0.6f, 5f, 0.03f)]
        [TestCase(0.03f, 5f, 0.02f, Description = "click-safe minimum")]
        [TestCase(0.01f, 50f, 0.01f, Description = "never longer than the window itself")]
        public void ReleaseFadeIsTheConfiguredPortionOfTheWindowWithAClickSafeMinimum(
            float windowSeconds, float fadePercent, float expectedSeconds)
        {
            Assert.That(PitchedGunshotLayer.CalculateReleaseFade(windowSeconds, fadePercent),
                Is.EqualTo(expectedSeconds).Within(0.0001f));
        }
    }
}
