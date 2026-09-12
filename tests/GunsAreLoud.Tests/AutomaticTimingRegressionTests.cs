using System;
using System.Collections.Generic;
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
        public void ProvisionalLateBudgetIsOneBufferOrAQuarterIntervalWhicheverIsShorter()
        {
            // Short buffer, ordinary interval: the buffer is the binding limit.
            Assert.That(AutomaticShotTiming.ProvisionalLateTolerance(512, 48000, 0.1f),
                Is.EqualTo(0.010667).Within(0.00001));
            // Long buffer, fast weapon: a quarter of the interval binds instead,
            // so a copy can never land in the middle of the following round.
            Assert.That(AutomaticShotTiming.ProvisionalLateTolerance(2048, 48000, 0.06f),
                Is.EqualTo(0.015).Within(0.00001));
            Assert.That(AutomaticShotTiming.ProvisionalLateTolerance(1024, 48000, 0.09f),
                Is.LessThan(0.0214),
                "The 1.0.0 budget here was 87ms — most of a whole fire interval.");
        }

        /// <summary>
        /// Stage 4.8 of the 1.0.1 performance plan. A stalling main thread hands
        /// the timeline several rounds at once, all of them already in the past.
        /// None of them may be played behind its own boundary by more than the
        /// budget, and none may be stacked onto the previous copy's start.
        /// </summary>
        [TestCase(0.05)]
        [TestCase(0.12)]
        [TestCase(0.25)]
        public void AStallingFrameNeverReleasesItsHeldBackCopiesAsAVolley(double frameSeconds)
        {
            const float Beat = 0.09f;
            double tolerance = AutomaticShotTiming.ProvisionalLateTolerance(1024, 48000, Beat);
            var timing = new AutomaticShotTiming();
            double now = 100.0;
            timing.Begin(now, Beat);

            var played = new List<double>();
            double worstLateness = 0.0;
            int fired = 0, dropped = 0;
            double weaponClock = now;          // the game fires on its own clock…
            for (int frame = 0; frame < 60; frame++)
            {
                double previous = now;
                now += frameSeconds;           // …the frame only decides when we hear of it.
                while (weaponClock + Beat <= now)
                {
                    weaponClock += Beat;
                    if (weaponClock <= previous - Beat * 4) continue;
                    fired++;
                    double boundary = timing.Advance(Beat);
                    AutomaticShotScheduleResult result =
                        AutomaticShotTiming.Classify(boundary, now, 0.0, tolerance);
                    if (result == AutomaticShotScheduleResult.TooLate)
                    {
                        dropped++;
                        timing.CatchUp(now, Beat);
                        continue;
                    }

                    double start = Math.Max(boundary, now);
                    worstLateness = Math.Max(worstLateness, now - boundary);
                    if (AutomaticShotTiming.CollapsesOntoPreviousStart(
                        start, boundary,
                        played.Count == 0 ? double.NegativeInfinity : played[played.Count - 1],
                        Beat))
                    {
                        timing.CatchUp(now, Beat);
                        continue;
                    }
                    played.Add(start);
                }
            }

            Assert.That(worstLateness, Is.LessThanOrEqualTo(tolerance + 1e-9),
                "No copy may be played further behind its round than the budget.");
            Assert.That(played, Is.Not.Empty,
                "Fire must still be heard through a stall: dropping the round that " +
                "cannot be placed moves the clock to the present, so the next one fits.");
            Assert.That(played.Count + dropped, Is.EqualTo(fired));
            for (int index = 1; index < played.Count; index++)
                Assert.That(played[index] - played[index - 1],
                    Is.GreaterThanOrEqualTo(Beat * 0.5 - 1e-9),
                    $"copies {index - 1} and {index} landed on top of each other");
        }

        /// <summary>
        /// The interval the game reports is not exactly the one it fires on: the
        /// error is a millisecond or two per round and always the same sign. Free
        /// running, the clock falls further behind every round until every copy is
        /// past the late budget, and the added low end goes silent a couple of
        /// seconds into a held burst. Dropping a copy has to put the clock back on
        /// the present, or the burst never recovers.
        /// </summary>
        [TestCase(0.0017)]   // as measured in the range: ~10 ms per six rounds
        [TestCase(-0.0017)]  // and the same the other way
        public void AClockThatDriftsFromTheRealFireRateRecoversInsteadOfGoingSilent(
            double driftPerRound)
        {
            const float ReportedBeat = 0.09f;
            double realInterval = ReportedBeat + driftPerRound;
            double tolerance = AutomaticShotTiming.ProvisionalLateTolerance(
                1024, 48000, ReportedBeat);
            var timing = new AutomaticShotTiming();
            double now = 500.0;
            timing.Begin(now, ReportedBeat);

            int played = 0, dropped = 0, consecutiveDrops = 0, worstRun = 0;
            for (int round = 0; round < 200; round++)
            {
                now += realInterval;
                double boundary = timing.Advance(ReportedBeat);
                if (AutomaticShotTiming.Classify(boundary, now, 0.0, tolerance) ==
                    AutomaticShotScheduleResult.TooLate)
                {
                    dropped++;
                    consecutiveDrops++;
                    worstRun = Math.Max(worstRun, consecutiveDrops);
                    timing.Rebase(now, ReportedBeat);
                    continue;
                }
                consecutiveDrops = 0;
                played++;
            }

            Assert.That(worstRun, Is.LessThanOrEqualTo(1),
                "A drifting clock must never drop two rounds in a row: one drop " +
                "re-anchors it, and the next round is on time again.");
            Assert.That(played, Is.GreaterThan(150),
                $"Most of a long burst must still be heard; played {played}, dropped {dropped}.");
        }

        /// <summary>
        /// What one tester heard as a shot two seconds after a long burst. The
        /// clock ran on the recording's beat, 97.2 ms, while the rifle fired every
        /// 77.2 ms: each copy was placed 20 ms further ahead of its round than the
        /// last, and after 98 rounds the final copy — a whole report, attack
        /// included — was still queued 1.97 s out. The log showed exactly that
        /// lead. Nothing corrected it, because only a late copy moved the clock.
        /// </summary>
        [TestCase(0.0972, 0.0772, 98)]   // the reported rifle, as logged
        [TestCase(0.0518, 0.0456, 120)]  // a machine pistol, ~6 ms per round
        [TestCase(0.0930, 0.0850, 100)]  // a machine gun, ~8 ms per round
        [TestCase(0.0900, 0.0900, 200)]  // a weapon whose beat is right
        [TestCase(0.0900, 0.0883, 200)]  // the small drift the late budget already handles
        public void AWeaponFiringFasterThanItsBeatNeverQueuesACopyAfterTheBurst(
            double reportedBeat, double realInterval, int rounds)
        {
            const int Buffer = 1024, Rate = 48000;
            const double MinimumLead = 0.004;
            double bufferSeconds = Buffer / (double)Rate;
            var jitter = new Random(1234);
            double Observed(double shotTime) =>
                // The main thread hears of a round up to a frame later, and reads
                // an audio clock that only moves a buffer at a time.
                Math.Floor((shotTime + jitter.NextDouble() * 0.017) / bufferSeconds) * bufferSeconds;

            double unfixedLead = 0.0;
            {
                var unfixed = new AutomaticShotTiming();
                double first = Observed(100.0);
                unfixed.Begin(first, (float)reportedBeat);
                double boundary = first;
                for (int round = 1; round < rounds; round++)
                    boundary = unfixed.Advance((float)reportedBeat);
                unfixedLead = boundary - Observed(100.0 + (rounds - 1) * realInterval);
            }

            var timing = new AutomaticShotTiming();
            double worstLead = double.NegativeInfinity, lastStart = 0.0, lastRound = 0.0;
            double worstUnevenness = 0.0, previousStart = double.NaN;
            int played = 0, early = 0;
            for (int round = 0; round < rounds; round++)
            {
                double shotTime = 100.0 + round * realInterval;
                double now = Observed(shotTime);
                float interval;
                double boundary;
                if (round == 0)
                {
                    timing.Begin(now, (float)reportedBeat);
                    interval = timing.ObserveRound(now, (float)reportedBeat, bufferSeconds);
                    boundary = now;
                }
                else
                {
                    interval = timing.ObserveRound(now, (float)reportedBeat, bufferSeconds);
                    boundary = timing.Advance(interval);
                }

                double lateTolerance = AutomaticShotTiming.ProvisionalLateTolerance(Buffer, Rate, interval);
                double earlyTolerance = AutomaticShotTiming.ProvisionalEarlyTolerance(Buffer, Rate, interval);
                AutomaticShotScheduleResult result = AutomaticShotTiming.Classify(
                    boundary, now, MinimumLead, lateTolerance, earlyTolerance);
                if (result == AutomaticShotScheduleResult.TooLate)
                {
                    timing.Rebase(now, interval);
                    previousStart = double.NaN;
                    continue;
                }
                if (result == AutomaticShotScheduleResult.TooEarly)
                {
                    early++;
                    boundary = now + MinimumLead + earlyTolerance;
                    timing.Rebase(boundary, interval);
                }

                double start = AutomaticShotTiming.ResolveStart(boundary, now, MinimumLead, true);
                worstLead = Math.Max(worstLead, start - now);
                played++;
                if (round >= AutomaticShotTiming.MinimumObservedRounds * 2 && !double.IsNaN(previousStart))
                    worstUnevenness = Math.Max(worstUnevenness, Math.Abs(start - previousStart - realInterval));
                previousStart = start;
                lastStart = start;
                lastRound = now;
            }

            if (realInterval < reportedBeat * 0.95)
                Assert.That(unfixedLead, Is.GreaterThan(0.5),
                    "The scenario must reproduce the bug: on the reported beat alone the " +
                    "last copy is queued far beyond the burst.");
            double budget = AutomaticShotTiming.ProvisionalEarlyTolerance(Buffer, Rate, (float)reportedBeat);
            Assert.That(worstLead, Is.LessThanOrEqualTo(budget + MinimumLead + 1e-9),
                $"No copy may be placed further ahead of its round than the early budget; worst {worstLead * 1000:0.0} ms.");
            Assert.That(lastStart - lastRound, Is.LessThanOrEqualTo(budget + MinimumLead + 1e-9),
                "The last round's copy must start with the last round, not after the burst.");
            Assert.That(played, Is.GreaterThanOrEqualTo(rounds * 0.95),
                "Keeping copies on time must not cost the burst its rounds.");
            TestContext.WriteLine($"played={played}/{rounds} early={early} worstLead={worstLead * 1000:0.0}ms " +
                $"unevenness={worstUnevenness * 1000:0.0}ms unfixedLead={unfixedLead * 1000:0}ms");
            Assert.That(worstUnevenness, Is.LessThanOrEqualTo(bufferSeconds + 1e-9),
                "Once the burst's pace is known the copies follow it: no two land further " +
                "from the weapon's own spacing than one audio buffer.");
        }

        [Test]
        public void ObservedPaceIgnoresJitterAndAGapBetweenBursts()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(0.0, 0.09f);
            float interval = 0f;
            for (int round = 0; round <= AutomaticShotTiming.MinimumObservedRounds; round++)
                interval = timing.ObserveRound(round * 0.09 + (round % 2 == 0 ? 0.0 : 0.012), 0.09f, 0.0213);
            Assert.That(interval, Is.EqualTo(0.09f), "Frame jitter alone is not a different pace.");
            Assert.That(timing.FollowsObservedPace, Is.False);

            for (int round = 0; round <= AutomaticShotTiming.MinimumObservedRounds; round++)
                interval = timing.ObserveRound(10.0 + round * 0.072, 0.09f, 0.0213);
            Assert.That(interval, Is.EqualTo(0.072f).Within(0.0005f),
                "A burst that fires a fifth faster than its beat is followed at its own pace…");
            Assert.That(timing.FollowsObservedPace, Is.True);

            Assert.That(timing.ObserveRound(20.0, 0.09f, 0.0213), Is.EqualTo(0.09f),
                "…and a pause of several beats starts the measurement over.");
            Assert.That(timing.FollowsObservedPace, Is.False);
        }

        [TestCase(1.000, 1.000, AutomaticShotScheduleResult.Scheduled)]
        [TestCase(1.040, 1.000, AutomaticShotScheduleResult.Scheduled)]
        [TestCase(1.060, 1.000, AutomaticShotScheduleResult.TooEarly)]
        public void EarlyPolicyOnlyTriggersBeyondItsBudget(
            double requested, double now, AutomaticShotScheduleResult expected)
        {
            Assert.That(
                AutomaticShotTiming.Classify(requested, now, 0.0, 0.05, 0.05),
                Is.EqualTo(expected));
        }

        [Test]
        public void EarlyBudgetIsABufferAndAQuarterIntervalButNeverHalfAnInterval()
        {
            Assert.That(AutomaticShotTiming.ProvisionalEarlyTolerance(1024, 48000, 0.0972f),
                Is.EqualTo(0.02133 + 0.0243).Within(0.0001));
            Assert.That(AutomaticShotTiming.ProvisionalEarlyTolerance(1024, 48000, 0.05f),
                Is.EqualTo(0.025).Within(0.00001),
                "On a fast weapon half the interval binds, so a copy never sits nearer the next round than its own.");
        }

        [Test]
        public void RebasingPutsTheNextRoundBackOnTheGridWithoutReplayingTheBacklog()
        {
            var timing = new AutomaticShotTiming();
            timing.Begin(10.0, 0.09f);
            timing.Advance(0.09f);

            timing.Rebase(12.5, 0.09f);

            Assert.That(timing.Advance(0.09f), Is.EqualTo(12.59).Within(0.000001),
                "The next boundary is one interval after the present…");
            Assert.That(timing.Advance(0.09f), Is.EqualTo(12.68).Within(0.000001),
                "…and the grid continues from there, with nothing owed.");
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
            var first = new AutomaticShotContext(routing) { FullReportScheduled = true };
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
