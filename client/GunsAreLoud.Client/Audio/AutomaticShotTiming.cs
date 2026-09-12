using System;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal static class AutomaticRouteTransition
    {
        internal static bool ShouldRebind(bool active, int previousRoute, int selectedRoute)
        {
            return !active || previousRoute != selectedRoute;
        }
    }

    internal enum AutomaticShotScheduleResult
    {
        Scheduled,
        LateWithinTolerance,
        TooLate,
        TooEarly
    }

    /// <summary>
    /// Pure rolling automatic-fire clock. Interval changes affect only the next
    /// unplayed interval; elapsed boundaries are never recalculated.
    /// </summary>
    internal sealed class AutomaticShotTiming
    {
        private double _lastBoundary;
        private double _nextBoundary;
        private float _interval;
        private double _followingBoundary = double.NaN;
        private bool _started;

        internal void Begin(double firstBoundary, float interval = 0.1f)
        {
            _lastBoundary = firstBoundary;
            _interval = Math.Max(0.001f, interval);
            _nextBoundary = firstBoundary + _interval;
            _followingBoundary = double.NaN;
            _started = true;
            _observedFrom = double.NaN;
            _observedRounds = 0;
        }

        internal double FirstBoundary => _lastBoundary;

        private double _observedFrom = double.NaN;
        private double _observedLast;
        private int _observedRounds;

        /// <summary>Rounds a burst must have fired before its own pace is trusted.</summary>
        internal const int MinimumObservedRounds = 8;

        /// <summary>
        /// The smallest difference from the reported interval the clock follows,
        /// as a share of it. Anything finer is left to the late and early budgets.
        /// </summary>
        internal const double ObservedIntervalFloor = 0.01;

        /// <summary>
        /// True once the clock runs on the pace the burst is actually firing at
        /// rather than the interval the game reports.
        /// </summary>
        internal bool FollowsObservedPace { get; private set; }

        /// <summary>
        /// Notes that a round was fired now and returns the interval the clock
        /// should run on for the rest of the burst.
        /// <para>
        /// The reported interval is the beat of the weapon's looping recording,
        /// corrected by a pitch the game clamps to a few percent. The rounds come
        /// at the weapon's own fire rate, which can be a fifth faster than that
        /// beat. A clock on the beat then falls behind by the difference every
        /// round; each copy is placed later than the last, and a long burst ends
        /// with its final copies still queued seconds ahead — heard as a shot
        /// after the trigger was released. The average over the burst is immune
        /// to frame jitter in a way a single interval is not.
        /// </para>
        /// </summary>
        /// <param name="clockResolutionSeconds">How coarsely <paramref name="now"/>
        /// moves — the audio buffer. Two of them over the rounds observed is how
        /// far the average can be off by timing alone, so a smaller difference is
        /// not trusted yet; a real one of a millisecond is followed within a few
        /// dozen rounds, a real one of twenty within the first eight.</param>
        internal float ObserveRound(double now, float reportedBeatSeconds, double clockResolutionSeconds)
        {
            float reported = Math.Max(0.001f, reportedBeatSeconds);
            // A gap of several beats is a new burst on the same timeline, not a
            // slow round: start measuring again rather than average across it.
            if (double.IsNaN(_observedFrom) || now < _observedLast ||
                now - _observedLast > reported * 3.0)
            {
                _observedFrom = now;
                _observedLast = now;
                _observedRounds = 0;
                FollowsObservedPace = false;
                return reported;
            }

            _observedLast = now;
            _observedRounds++;
            FollowsObservedPace = false;
            if (_observedRounds < MinimumObservedRounds) return reported;
            double observed = (now - _observedFrom) / _observedRounds;
            double noise = Math.Max(reported * ObservedIntervalFloor,
                2.0 * Math.Max(0.0, clockResolutionSeconds) / _observedRounds);
            if (observed < reported * 0.5 || observed > reported * 2.0 ||
                Math.Abs(observed - reported) <= noise)
                return reported;
            FollowsObservedPace = true;
            return (float)observed;
        }

        internal double Advance(float currentBeatSeconds)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The automatic shot clock has not started.");
            }

            _interval = Math.Max(0.001f, currentBeatSeconds);
            _lastBoundary = _nextBoundary;
            _nextBoundary = double.IsNaN(_followingBoundary)
                ? _lastBoundary + _interval : _followingBoundary;
            _followingBoundary = double.NaN;
            return _lastBoundary;
        }

        internal void ChangeInterval(double changeTime, float newBeatSeconds)
        {
            if (!_started) return;
            float nextInterval = Math.Max(0.001f, newBeatSeconds);
            if (changeTime <= _lastBoundary)
            {
                _nextBoundary = _lastBoundary + nextInterval;
            }
            else if (changeTime <= _nextBoundary)
            {
                double remainingPhase = (_nextBoundary - changeTime) / _interval;
                _nextBoundary = changeTime + remainingPhase * nextInterval;
            }
            else
            {
                double elapsedPhase = (changeTime - _nextBoundary) / _interval;
                double fractionalPhase = elapsedPhase - Math.Floor(elapsedPhase);
                _followingBoundary = changeTime + (1.0 - fractionalPhase) * nextInterval;
            }
            _interval = nextInterval;
        }

        /// <summary>
        /// Puts the clock back on the present after a copy had to be dropped.
        /// <para>
        /// The rolling clock free-runs on the interval the game reports, and that
        /// interval is not exactly the one the game fires on: the error is a
        /// millisecond or two per round, always the same sign, and it accumulates.
        /// <see cref="CatchUp"/> cannot correct it — it only walks a clock whose
        /// next boundary is already in the past, and a clock that is merely
        /// running fast always has its next boundary in the future. So the lag
        /// grew round by round until every copy of a held burst was past the late
        /// budget and dropped, and the added low end fell silent a couple of
        /// seconds into the burst.
        /// </para>
        /// Re-anchoring here costs the one copy that was already being dropped and
        /// puts the next round back on time.
        /// </summary>
        internal void Rebase(double now, float beatSeconds)
        {
            if (!_started) return;
            _interval = Math.Max(0.001f, beatSeconds);
            _followingBoundary = double.NaN;
            _lastBoundary = now;
            _nextBoundary = now + _interval;
        }

        internal void CatchUp(double now, float beatSeconds)
        {
            if (!_started) return;
            _interval = Math.Max(0.001f, beatSeconds);
            _followingBoundary = double.NaN;
            CatchUpPast(now);
        }

        private void CatchUpPast(double time)
        {
            if (_nextBoundary > time) return;
            double elapsed = time - _nextBoundary;
            long steps = (long)Math.Floor(elapsed / _interval) + 1L;
            _lastBoundary = _nextBoundary + (steps - 1L) * _interval;
            _nextBoundary += steps * _interval;
            while (_nextBoundary <= time + 1e-7)
            {
                _lastBoundary = _nextBoundary;
                _nextBoundary += _interval;
            }
        }

        internal static AutomaticShotScheduleResult Classify(
            double requestedStart,
            double now,
            double minimumLeadSeconds,
            double lateToleranceSeconds,
            double earlyToleranceSeconds = double.PositiveInfinity)
        {
            double lateness = now + Math.Max(0.0, minimumLeadSeconds) - requestedStart;
            if (lateness <= 0.0)
            {
                return -lateness > Math.Max(0.0, earlyToleranceSeconds)
                    ? AutomaticShotScheduleResult.TooEarly
                    : AutomaticShotScheduleResult.Scheduled;
            }

            return lateness <= Math.Max(0.0, lateToleranceSeconds)
                ? AutomaticShotScheduleResult.LateWithinTolerance
                : AutomaticShotScheduleResult.TooLate;
        }

        /// <summary>
        /// How much of one fire interval a copy may still be late by, when the
        /// F12 control is unavailable. Also the shipped default of that control.
        /// </summary>
        internal const double LateToleranceIntervalFraction = 0.25;

        /// <summary>
        /// How late a copy may be and still be worth playing: one audio buffer, or
        /// this share of the fire interval, whichever is shorter.
        /// <para>
        /// The old budget was two buffers plus half an interval — wide enough that
        /// a main-thread stall was answered with a handful of copies fired off one
        /// after another, well behind the shots they belong to. A copy that cannot
        /// be placed inside this window is dropped and the clock is moved to the
        /// present instead, so the backlog is never replayed as a volley.
        /// </para>
        /// </summary>
        /// <param name="scale">The F12 control, as a fraction of the budget above.
        /// It multiplies the finished budget rather than the interval share, so
        /// the one-buffer term cannot clamp it away: at 1 the budget is exactly
        /// the shipped one, and above it a copy may land further behind its round
        /// instead of being dropped.</param>
        internal static double ProvisionalLateTolerance(
            int dspBufferFrames, int sampleRate, float beatSeconds, float scale = 1f)
        {
            double bufferSeconds = Math.Max(1, dspBufferFrames) / (double)Math.Max(8000, sampleRate);
            double intervalShare =
                Math.Max(0.001f, beatSeconds) * LateToleranceIntervalFraction;
            return Math.Min(bufferSeconds, intervalShare) * Math.Max(0f, scale);
        }

        /// <summary>
        /// How far ahead of its own round a copy may be placed and still count as
        /// on time: one audio buffer plus a quarter of the fire interval, but
        /// never more than half of it.
        /// <para>
        /// The main thread reads an audio clock that moves a buffer at a time, so
        /// a clock on the right pace can look up to a buffer early. Anything past
        /// that is a clock running slower than the weapon, and left alone it only
        /// grows. A copy beyond this budget is pulled back to its edge and the
        /// clock with it — the round is real, so unlike a late copy it is not lost.
        /// </para>
        /// </summary>
        internal static double ProvisionalEarlyTolerance(
            int dspBufferFrames, int sampleRate, float beatSeconds)
        {
            double bufferSeconds = Math.Max(1, dspBufferFrames) / (double)Math.Max(8000, sampleRate);
            double interval = Math.Max(0.001f, beatSeconds);
            return Math.Min(bufferSeconds + interval * LateToleranceIntervalFraction, interval * 0.5);
        }

        internal static double ResolveStart(
            double requestedStart, double now, double minimumLeadSeconds, bool requiresSchedulingLead)
        {
            return requiresSchedulingLead
                ? Math.Max(requestedStart, now + Math.Max(0.0, minimumLeadSeconds))
                : requestedStart;
        }

        /// <summary>
        /// True when a late shot was pulled forward onto a start that is already
        /// occupied by the previous copy. Only late shots can collapse: an
        /// on-time boundary keeps its own place on the timeline even when the
        /// interval is short.
        /// </summary>
        internal static bool CollapsesOntoPreviousStart(
            double resolvedStart,
            double requestedStart,
            double previousStart,
            float beatSeconds)
        {
            if (double.IsNegativeInfinity(previousStart) ||
                resolvedStart <= requestedStart + 1e-9)
            {
                return false;
            }

            double separation = resolvedStart - previousStart;
            return separation >= 0.0 && separation < Math.Max(0.001f, beatSeconds) * 0.5;
        }
    }
}
