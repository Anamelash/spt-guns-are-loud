using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using GunsAreLoud.Client.Configuration;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// The two report shapes. Full Report Per Shot gives every round the whole
    /// authored report — body and recorded tail — and Tail After Burst gives it a
    /// short body with a synthetic decay, keeping the recorded tail for the
    /// release. Only the config plumbing was covered before; these pin what the
    /// choice is actually worth, and where it cannot be heard.
    /// </summary>
    [TestFixture]
    public sealed class AutomaticReportShapeTests
    {
        private const float PitchRatio = 0.5f;      // one octave down
        private const float Beat = 0.09f;           // ~660 rounds per minute
        private const float ShortBodySpan = 0.056f; // one captured beat, as measured in a raid
        private const float ReportSpan = 1.167f;    // the same weapon's body plus recorded tail
        private const float SyntheticDecay = 0.03f;

        private static float ShortBody() =>
            PitchedGunshotLayer.CalculateCachedOutputDuration(
                ShortBodySpan, PitchRatio, SyntheticDecay);

        private static float FullReport() =>
            PitchedGunshotLayer.CalculateCachedOutputDuration(ReportSpan, PitchRatio, 0f);

        [Test]
        public void OneRoundOfAFullReportLastsFarLongerThanOneShortBody()
        {
            Assert.That(ShortBody(), Is.EqualTo(0.142f).Within(0.001f));
            Assert.That(FullReport(), Is.EqualTo(2.334f).Within(0.001f));
            Assert.That(FullReport(), Is.GreaterThan(ShortBody() * 10f),
                "If these ever come close, the setting has stopped meaning anything.");
        }

        /// <summary>
        /// What the player hears in the middle of a held burst, where the overlap
        /// budget cuts the full report short. This is the only stretch where the
        /// two shapes differ at all, and it is where the difference must be made.
        /// </summary>
        [Test]
        public void WhileTheTriggerIsHeldTheOverlapBudgetIsWhatSeparatesTheShapes()
        {
            const float OverlapShots = 6f;
            float boundedReport = System.Math.Min(FullReport(), Beat * OverlapShots);

            Assert.That(boundedReport, Is.EqualTo(0.54f).Within(0.001f),
                "A round of a held burst is faded out within the overlap budget…");
            Assert.That(boundedReport, Is.GreaterThan(ShortBody() * 3f),
                "…and even bounded it is several times the short body.");

            // Turn the budget down far enough and the shapes converge: at one
            // fire interval the full report is cut to about the short body, and
            // the choice stops being audible while the trigger is held.
            float tightlyBounded = System.Math.Min(FullReport(), Beat * 1f);
            Assert.That(tightlyBounded, Is.LessThan(ShortBody() * 1.5f));
        }

        /// <summary>
        /// Both shapes end a burst with the same recorded tail — Full Report Per
        /// Shot because the last round has no successor to cut it, Tail After
        /// Burst because that is when it plays its release copy. Anyone comparing
        /// the two by firing short bursts is comparing two identical endings.
        /// </summary>
        [Test]
        public void BothShapesEndABurstWithTheSameRecordedTail()
        {
            var fullReport = new AutomaticShotContext();
            fullReport.FullReportScheduled = true;
            Assert.That(fullReport.ShouldPlayReleaseCopy, Is.False,
                "The last round's own copy already carries the recorded tail.");

            var tailAfterBurst = new AutomaticShotContext();
            Assert.That(tailAfterBurst.FullReportScheduled, Is.False);
            Assert.That(tailAfterBurst.ShouldPlayReleaseCopy, Is.True,
                "So the release copy supplies the same recorded tail instead.");
        }

        /// <summary>
        /// Full Report Per Shot needs a composed report; until the weapon's
        /// warm-up has published one, a round falls back to exactly what Tail
        /// After Burst plays. The two settings are then indistinguishable, and
        /// nothing in the game says so — which is what the summary's
        /// <c>fullReports</c> count is for.
        /// </summary>
        [TestCase("FullReportPerShot", true, true)]
        [TestCase("FullReportPerShot", false, false)]
        [TestCase("TailAfterBurst", true, false)]
        [TestCase("TailAfterBurst", false, false)]
        public void AFullReportNeedsBothTheSettingAndAComposedReport(
            string shape, bool reportReady, bool expected)
        {
            var mode = (AutomaticTailMode)System.Enum.Parse(typeof(AutomaticTailMode), shape);
            bool usesFullReport = mode == AutomaticTailMode.FullReportPerShot && reportReady;
            Assert.That(usesFullReport, Is.EqualTo(expected));
        }
    }
}
