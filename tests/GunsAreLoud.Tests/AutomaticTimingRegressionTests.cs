using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    internal class AutomaticTimingRegressionTests
    {
        [Test]
        public void RebindingRunningSourceKeepsCurrentShotAndFollowingBeat()
        {
            // 16 beats of 4800 frames; observation is 5 ms into the second beat.
            double current = AutomaticBeatTiming.CalculateCurrentBoundary(
                0.105, 5040, 76800, 48000, 1f);
            var timing = new AutomaticShotTiming();
            timing.Begin(current, 0.1f);
            Assert.That(current, Is.EqualTo(0.1).Within(0.000001));
            Assert.That(timing.Advance(0.1f), Is.EqualTo(0.2).Within(0.000001));
            Assert.That(AutomaticBeatTiming.CalculateCurrentBoundary(
                1.6, 76800, 76800, 48000, 1f), Is.EqualTo(1.6).Within(0.000001));
        }

        [Test]
        public void ConstantPitchProducesOneStrictlySequentialBoundaryPerShot()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(10.0);

            Assert.That(timing.FirstBoundary, Is.EqualTo(10.0));
            Assert.That(timing.Advance(0.1f), Is.EqualTo(10.1).Within(0.000001));
            Assert.That(timing.Advance(0.1f), Is.EqualTo(10.2).Within(0.000001));
            Assert.That(timing.Advance(0.1f), Is.EqualTo(10.3).Within(0.000001));
        }

        [Test]
        public void PitchChangePreservesElapsedPhaseAndChangesOnlyFutureInterval()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(20.0);
            double second = timing.Advance(0.1f);
            timing.ChangeInterval(20.15, 0.08f);
            double third = timing.Advance(0.08f);
            double fourth = timing.Advance(0.08f);

            Assert.That(second, Is.EqualTo(20.1).Within(0.000001));
            Assert.That(third, Is.EqualTo(20.19).Within(0.000001));
            Assert.That(fourth, Is.EqualTo(20.27).Within(0.000001));
        }

        [TestCase(1.100, 1.000, AutomaticShotScheduleResult.Scheduled)]
        [TestCase(1.000, 1.020, AutomaticShotScheduleResult.LateWithinTolerance)]
        [TestCase(1.000, 1.051, AutomaticShotScheduleResult.TooLate)]
        public void LatePolicySchedulesOnceOrDropsWithoutMovingToAnotherBoundary(
            double requested, double now, AutomaticShotScheduleResult expected)
        {
            Assert.That(
                AutomaticShotTiming.Classify(requested, now, 0.0, 0.05),
                Is.EqualTo(expected));
        }

        [Test]
        public void ProvisionalLateBudgetUsesDspBufferAndCurrentBeat()
        {
            Assert.That(AutomaticShotTiming.ProvisionalLateTolerance(512, 48000, 0.1f),
                Is.EqualTo(0.071333).Within(0.00001));
        }

        [Test]
        public void OriginalBandKeepsExactSourceBoundaryWhileScheduledVoiceGetsLead()
        {
            Assert.That(AutomaticShotTiming.ResolveStart(10.0, 10.002, 0.004, false),
                Is.EqualTo(10.0));
            Assert.That(AutomaticShotTiming.ResolveStart(10.0, 10.002, 0.004, true),
                Is.EqualTo(10.006).Within(0.000001));
        }

        [Test]
        public void HitchDropsBacklogAndNextRealShotResumesAtFutureLoopPhase()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(0.0, 0.1f);
            Assert.That(timing.Advance(0.1f), Is.EqualTo(0.1).Within(0.000001));
            timing.CatchUp(0.5, 0.1f);

            Assert.That(timing.Advance(0.1f), Is.EqualTo(0.6).Within(0.000001));
            Assert.That(timing.Advance(0.1f), Is.EqualTo(0.7).Within(0.000001));
        }

        [Test]
        public void PitchChangeAtBoundaryReplacesEntirePendingInterval()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(20.0, 0.1f);
            timing.ChangeInterval(20.0, 0.08f);
            Assert.That(timing.Advance(0.08f), Is.EqualTo(20.08).Within(0.000001));
        }

        [Test]
        public void LatePitchObservationCatchesUpOldPhaseBeforeChangingRemainder()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(0.0, 0.1f);
            timing.ChangeInterval(0.25, 0.08f);
            Assert.That(timing.Advance(0.08f), Is.EqualTo(0.1).Within(0.000001));
            Assert.That(timing.Advance(0.08f), Is.EqualTo(0.29).Within(0.000001));
        }

        [Test]
        public void UnchangedPitchAfterBoundaryDoesNotConsumePendingRealShot()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(0.0, 0.1f);
            // Integration components suppress this unchanged update.
            Assert.That(timing.Advance(0.1f), Is.EqualTo(0.1).Within(0.000001));
            Assert.That(timing.Advance(0.1f), Is.EqualTo(0.2).Within(0.000001));
        }

        [Test]
        public void GenuinePitchChangeAfterBoundaryPreservesCurrentShotThenFuturePhase()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(0.0, 0.1f);
            timing.ChangeInterval(0.105, 0.08f);
            Assert.That(timing.Advance(0.08f), Is.EqualTo(0.1).Within(0.000001));
            Assert.That(timing.Advance(0.08f), Is.EqualTo(0.181).Within(0.000001));
        }

        [TestCase(true, 1, 1, false)]
        [TestCase(false, 1, 1, true)]
        [TestCase(true, 1, 2, true)]
        public void RouteTransitionRebindsOnlyForMissingOrChangedRoute(
            bool active, int previous, int selected, bool expected)
        {
            Assert.That(AutomaticRouteTransition.ShouldRebind(active, previous, selected),
                Is.EqualTo(expected));
        }

        [Test]
        public void BurstRoutingIsSharedButPerShotTailFlagsAreIndependent()
        {
            var routing = new AutomaticBurstRouting();
            var first = new AutomaticShotContext(routing) { AuthoredTailScheduled = true };
            var second = new AutomaticShotContext(routing);

            Assert.That(first.Routing, Is.SameAs(second.Routing));
            Assert.That(first.ShouldPlayReleaseCopy, Is.False);
            Assert.That(second.ShouldPlayReleaseCopy, Is.True);
        }

        [Test]
        public void NewBurstRestartsAtItsOwnFirstBoundary()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(3.0);
            timing.Advance(0.1f);
            timing.Begin(8.0, 0.12f);

            Assert.That(timing.FirstBoundary, Is.EqualTo(8.0));
            Assert.That(timing.Advance(0.12f), Is.EqualTo(8.12).Within(0.000001));
        }
    }
}
