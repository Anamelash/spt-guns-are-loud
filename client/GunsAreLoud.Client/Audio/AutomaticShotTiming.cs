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
        TooLate
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
        }

        internal double FirstBoundary => _lastBoundary;

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
            double lateToleranceSeconds)
        {
            double lateness = now + Math.Max(0.0, minimumLeadSeconds) - requestedStart;
            if (lateness <= 0.0)
            {
                return AutomaticShotScheduleResult.Scheduled;
            }

            return lateness <= Math.Max(0.0, lateToleranceSeconds)
                ? AutomaticShotScheduleResult.LateWithinTolerance
                : AutomaticShotScheduleResult.TooLate;
        }

        internal static double ProvisionalLateTolerance(
            int dspBufferFrames, int sampleRate, float beatSeconds)
        {
            double bufferSeconds = Math.Max(1, dspBufferFrames) / (double)Math.Max(8000, sampleRate);
            return bufferSeconds * 2.0 + Math.Max(0.001f, beatSeconds) * 0.5;
        }

        internal static double ResolveStart(
            double requestedStart, double now, double minimumLeadSeconds, bool requiresSchedulingLead)
        {
            return requiresSchedulingLead
                ? Math.Max(requestedStart, now + Math.Max(0.0, minimumLeadSeconds))
                : requestedStart;
        }
    }

    internal sealed class AutomaticImpactTimeline : MonoBehaviour
    {
        private const double MinimumLeadSeconds = 0.004;
        private readonly AutomaticShotTiming _timing = new AutomaticShotTiming();
        private SuperSource _source;
        private bool _active;
        private float _beatSeconds;
        internal bool Active => _active && _source != null;

        internal bool Begin(SuperSource source, LocalGunshotAudioTuning tuning, double firstBoundary)
        {
            _source = source;
            _active = source != null;
            _beatSeconds = tuning.PitchedLayerLoopBeatSeconds;
            _timing.Begin(firstBoundary, tuning.PitchedLayerLoopBeatSeconds);
            return _active && TriggerAt(firstBoundary);
        }

        internal bool TriggerNext(LocalGunshotAudioTuning tuning)
        {
            if (!_active || _source == null)
            {
                return false;
            }

            double boundary = _timing.Advance(tuning.PitchedLayerLoopBeatSeconds);
            _beatSeconds = tuning.PitchedLayerLoopBeatSeconds;
            return TriggerAt(boundary);
        }

        internal void UpdateInterval(double changeTime, float beatSeconds)
        {
            if (_active && Mathf.Abs(_beatSeconds - beatSeconds) > 0.000001f)
            { _timing.ChangeInterval(changeTime, beatSeconds); _beatSeconds = beatSeconds; }
        }

        internal void StopTimeline()
        {
            _active = false;
            _source = null;
        }

        private bool TriggerAt(double boundary)
        {
            double now = AudioSettings.dspTime;
            AudioSettings.GetDSPBufferSize(out int bufferFrames, out _);
            AutomaticShotScheduleResult result = AutomaticShotTiming.Classify(
                boundary, now, 0.0,
                AutomaticShotTiming.ProvisionalLateTolerance(
                    bufferFrames, AudioSettings.outputSampleRate, _beatSeconds));
            if (result == AutomaticShotScheduleResult.TooLate)
            {
                _timing.CatchUp(now, _beatSeconds);
                if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                    Plugin.Log.LogWarning(
                        $"automatic original-band attack skipped: late by " +
                        $"{(now - boundary) * 1000.0:0.0}ms");
                return false;
            }

            double start = AutomaticShotTiming.ResolveStart(
                boundary, now, MinimumLeadSeconds, false);
            bool first = _source.source1?.GetComponent<LocalGunshotImpactFilter>()?.Trigger(start) == true;
            bool second = _source.source2?.GetComponent<LocalGunshotImpactFilter>()?.Trigger(start) == true;
            return first || second;
        }

        private void OnDisable()
        {
            StopTimeline();
        }
    }
}
