using System;
using System.Diagnostics;
using System.Threading;
using GunsAreLoud.Client.Audio;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal enum PerformanceArea { Hearing, Warmup, Normalization, ContrastDiscovery, ContrastMaintenance, ContrastHooks, Count }

    internal sealed class PerformanceCounters
    {
        internal readonly long[] Total = new long[(int)PerformanceArea.Count];
        internal readonly long[] Maximum = new long[(int)PerformanceArea.Count];
        internal void Record(PerformanceArea area, long ticks)
        {
            int index = (int)area;
            Total[index] += ticks;
            if (ticks > Maximum[index]) Maximum[index] = ticks;
        }
        internal void Clear() { Array.Clear(Total, 0, Total.Length); Array.Clear(Maximum, 0, Maximum.Length); }
    }

    internal static class PerformanceTrace
    {
        internal static volatile bool Enabled;
        internal static readonly PerformanceCounters Work = new PerformanceCounters();
        internal static long AudioCallbacks, BufferChanges, AudioBytes, AudioTicks;

        internal readonly struct Scope : IDisposable
        {
            private readonly bool _active;
            private readonly long _start;
            private readonly PerformanceArea _area;
            internal Scope(PerformanceArea area)
            {
                _active = Enabled; _area = area;
                _start = _active ? Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (_active) Work.Record(_area, Stopwatch.GetTimestamp() - _start);
            }
        }

        internal static Scope Measure(PerformanceArea area) => new Scope(area);
        internal static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // The callback caller has already allocated/supplied its buffer. A changed
        // identity is evidence of non-reuse, not proof of allocation (pooling exists).
        internal static void RecordAudio(float[] data, ref float[] previous, long start)
        {
            Interlocked.Increment(ref AudioCallbacks);
            if (!ReferenceEquals(data, previous)) Interlocked.Increment(ref BufferChanges);
            previous = data;
            if (data != null) Interlocked.Add(ref AudioBytes, data.Length * 4L);
            Interlocked.Add(ref AudioTicks, Stopwatch.GetTimestamp() - start);
        }

        internal static void ClearAudio()
        {
            Interlocked.Exchange(ref AudioCallbacks, 0); Interlocked.Exchange(ref BufferChanges, 0);
            Interlocked.Exchange(ref AudioBytes, 0); Interlocked.Exchange(ref AudioTicks, 0);
        }
    }

    // Summary only: no per-frame logging, allocations, GC.Collect or game GC changes.
    internal sealed class PerformanceMonitor : MonoBehaviour
    {
        private long _start, _lastFrame;
        private int _frames, _framesOver25, _slowFrames, _gcSlowFrames, _collections, _previousGc;
        private double _maxFrame, _maxGcFrame;
        private bool _modEnabled, _inGame;
        private float _contrast;

        private void LateUpdate()
        {
            bool diagnostic = Plugin.ModConfig?.DiagnosticShotLog.Value == true;
            PerformanceTrace.Enabled = diagnostic;
            if (!diagnostic) { _start = 0; return; }
            long now = Stopwatch.GetTimestamp();
            int gc = GC.CollectionCount(0);
            bool mod = Plugin.ModConfig.Enabled.Value;
            float contrast = Plugin.ModConfig.GunshotContrastDb.Value;
            BetterAudio audio = AudioRuntimeLookup.Audio;
            bool inGame = audio != null && audio.ListenerPlayer != null && audio.ListenerPlayer.IsYourPlayer;
            if (_start == 0 || mod != _modEnabled || contrast != _contrast || inGame != _inGame)
            {
                Reset(now, gc);
                _modEnabled = mod; _contrast = contrast; _inGame = inGame;
                return;
            }
            double frame = PerformanceTrace.Milliseconds(now - _lastFrame);
            _lastFrame = now;
            int collections = Math.Max(0, gc - _previousGc);
            _previousGc = gc;
            _collections += collections;
            _frames++;
            _maxFrame = Math.Max(_maxFrame, frame);
            if (frame >= 25) _framesOver25++;
            if (collections != 0) _maxGcFrame = Math.Max(_maxGcFrame, frame);
            if (frame >= 50) { _slowFrames++; if (collections != 0) _gcSlowFrames++; }
            double seconds = PerformanceTrace.Milliseconds(now - _start) / 1000;
            if (seconds < 10) return;
            long callbacks = Interlocked.Exchange(ref PerformanceTrace.AudioCallbacks, 0);
            long changed = Interlocked.Exchange(ref PerformanceTrace.BufferChanges, 0);
            long bytes = Interlocked.Exchange(ref PerformanceTrace.AudioBytes, 0);
            long audioTicks = Interlocked.Exchange(ref PerformanceTrace.AudioTicks, 0);
            var work = PerformanceTrace.Work;
            Plugin.Log.LogInfo($"performance window={seconds:0.0}s enabled={mod} inGame={inGame} contrast={contrast:0.0}dB " +
                $"inserts={GunshotContrastController.Instance?.TrackedCount ?? 0} " +
                $"frames={_frames} maxFrame={_maxFrame:0.00}ms framesOver25={_framesOver25} framesOver50={_slowFrames} " +
                $"gc0={_collections} gcSlowFrames={_gcSlowFrames} maxGcFrame={_maxGcFrame:0.00}ms " +
                $"heapMiB={GC.GetTotalMemory(false) / 1048576.0:0.0} " +
                $"contrastCallbacksPerSec={callbacks / seconds:0.0} changedBuffers={changed}/{callbacks} " +
                $"bufferTrafficMiBPerSec={bytes / seconds / 1048576.0:0.00} " +
                $"contrastManagedDspMsPerSec={PerformanceTrace.Milliseconds(audioTicks) / seconds:0.00} " +
                $"mainMsPerSec/maxCallMs " +
                $"hearing={Format(work, PerformanceArea.Hearing, seconds)} " +
                $"warmup={Format(work, PerformanceArea.Warmup, seconds)} " +
                $"normalization={Format(work, PerformanceArea.Normalization, seconds)} " +
                $"discovery={Format(work, PerformanceArea.ContrastDiscovery, seconds)} " +
                $"maintenance={Format(work, PerformanceArea.ContrastMaintenance, seconds)} " +
                $"hooks={Format(work, PerformanceArea.ContrastHooks, seconds)}");
            Reset(now, gc);
        }

        private static string Format(PerformanceCounters work, PerformanceArea area, double seconds) =>
            $"{PerformanceTrace.Milliseconds(work.Total[(int)area]) / seconds:0.00}/{PerformanceTrace.Milliseconds(work.Maximum[(int)area]):0.00}";

        private void Reset(long now, int gc)
        {
            _start = _lastFrame = now; _previousGc = gc;
            _frames = _framesOver25 = _slowFrames = _gcSlowFrames = _collections = 0;
            _maxFrame = _maxGcFrame = 0;
            PerformanceTrace.Work.Clear(); PerformanceTrace.ClearAudio();
        }

        private void OnDisable() { PerformanceTrace.Enabled = false; _start = 0; }
    }
}
