using System;
using System.Diagnostics;
using System.Threading;
using GunsAreLoud.Client.Audio;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal enum PerformanceArea
    {
        Hearing,
        Warmup,
        Normalization,
        ContrastDiscovery,
        ContrastMaintenance,
        ContrastHooks,
        HeadphoneRoute,
        HookFireBullet,
        HookPlayOnLocal,
        HookPlayOnForeign,
        HookEnqueue,
        HookReleaseQueue,
        HookUpdatePitch,
        HookSpatialization,
        HookExplosion,
        EftPlayOn,
        EftSetMixerGroup,
        EftPlayScheduled,
        WarmupPlan,
        WarmupLoad,
        WarmupCompose,
        WarmupPublish,
        WarmupRegister,
        Count
    }

    internal sealed class PerformanceCounters
    {
        internal readonly long[] Total = new long[(int)PerformanceArea.Count];
        internal readonly long[] Maximum = new long[(int)PerformanceArea.Count];
        internal readonly long[] Calls = new long[(int)PerformanceArea.Count];

        internal void Record(PerformanceArea area, long ticks) => Record(area, ticks, true);

        /// <param name="countCall">False for the second half of a prefix/postfix
        /// pair, so one patched call stays one call in the summary.</param>
        internal void Record(PerformanceArea area, long ticks, bool countCall)
        {
            int index = (int)area;
            Total[index] += ticks;
            if (countCall) Calls[index]++;
            if (ticks > Maximum[index]) Maximum[index] = ticks;
        }

        internal void Clear()
        {
            Array.Clear(Total, 0, Total.Length);
            Array.Clear(Maximum, 0, Maximum.Length);
            Array.Clear(Calls, 0, Calls.Length);
        }
    }

    internal sealed class FrameTimeHistogram
    {
        private const int OverflowBucket = 251;
        private readonly int[] _buckets = new int[OverflowBucket + 1];
        private int _count;

        internal int Count => _count;

        internal void Record(double milliseconds)
        {
            int bucket = milliseconds <= 0
                ? 0
                : Math.Min(OverflowBucket, (int)Math.Ceiling(milliseconds));
            _buckets[bucket]++;
            _count++;
        }

        internal double Percentile(double percentile)
        {
            if (_count == 0) return 0;
            int target = Math.Max(1, (int)Math.Ceiling(_count * percentile));
            int cumulative = 0;
            for (int bucket = 0; bucket < _buckets.Length; bucket++)
            {
                cumulative += _buckets[bucket];
                if (cumulative >= target) return bucket;
            }
            return OverflowBucket;
        }

        internal void Clear()
        {
            Array.Clear(_buckets, 0, _buckets.Length);
            _count = 0;
        }
    }

    /// <summary>
    /// One G.A.L. audio callback type. Each managed filter reports into its own
    /// bucket, so the summary separates a costly filter from a cheap one that
    /// merely runs on every pooled source.
    /// </summary>
    internal enum AudioFilterKind
    {
        AutomaticCapture,
        AutomaticPitched,
        PitchedBand,
        PitchedTail,
        PitchedEnvelope,
        Hearing,
        Count
    }

    /// <summary>
    /// How often the master limiter had to act, and how hard. Written once per
    /// buffer by the audio thread and only while the summary is on; read once per
    /// window by the main thread. A window that reports no engaged buffer is the
    /// evidence that nothing reached the ceiling in it.
    /// </summary>
    internal static class MasterLimiterTrace
    {
        private const long GainScale = 1000L;
        private static long _engagedBuffers, _engagedFrames, _buffers;
        private static long _deepestGain = GainScale;
        private static long _inputPeak;

        internal static void Record(int engagedFrames, float deepestGain, float inputPeak)
        {
            if (!PerformanceTrace.Enabled) return;
            Interlocked.Increment(ref _buffers);
            if (inputPeak > 0f)
                PerformanceTrace.SetMaximum(ref _inputPeak, (long)(inputPeak * GainScale));
            if (engagedFrames <= 0) return;
            Interlocked.Increment(ref _engagedBuffers);
            Interlocked.Add(ref _engagedFrames, engagedFrames);
            SetMinimum(ref _deepestGain, (long)(Math.Max(0f, deepestGain) * GainScale));
        }

        internal static string Format()
        {
            long buffers = Interlocked.Exchange(ref _buffers, 0L);
            long engagedBuffers = Interlocked.Exchange(ref _engagedBuffers, 0L);
            long engagedFrames = Interlocked.Exchange(ref _engagedFrames, 0L);
            long deepest = Interlocked.Exchange(ref _deepestGain, GainScale);
            long peak = Interlocked.Exchange(ref _inputPeak, 0L);
            double reductionDb = deepest >= GainScale
                ? 0.0
                : 20.0 * Math.Log10(Math.Max(1L, deepest) / (double)GainScale);
            return $"limiter={engagedBuffers}/{buffers} frames={engagedFrames} " +
                $"maxReduction={reductionDb:0.0}dB peakIn={peak / (double)GainScale:0.00}";
        }

        internal static void Clear()
        {
            Interlocked.Exchange(ref _buffers, 0L);
            Interlocked.Exchange(ref _engagedBuffers, 0L);
            Interlocked.Exchange(ref _engagedFrames, 0L);
            Interlocked.Exchange(ref _deepestGain, GainScale);
            Interlocked.Exchange(ref _inputPeak, 0L);
        }

        private static void SetMinimum(ref long target, long value)
        {
            long current = Volatile.Read(ref target);
            while (value < current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    internal enum AutomaticScheduleOutcome
    {
        Scheduled,
        Late,
        Dropped,
        Collapsed,
        Early,
        Count
    }

    /// <summary>
    /// What happened to each round's added report. The one number that says
    /// whether the late-copy budget is right: dropped copies mean the main thread
    /// stalled past it, late ones mean it is being used. Early ones are copies
    /// the clock would have placed too far ahead of their round — a weapon that
    /// fires faster than its recording's beat — played at once instead.
    /// </summary>
    internal static class AutomaticScheduleTrace
    {
        private static readonly long[] Outcomes = new long[(int)AutomaticScheduleOutcome.Count];
        private static long _authoredReports;

        internal static void Record(AutomaticScheduleOutcome outcome)
        {
            if (!PerformanceTrace.Enabled) return;
            Interlocked.Increment(ref Outcomes[(int)outcome]);
        }

        /// <summary>
        /// One round played its whole authored report — body and recorded tail —
        /// rather than the short body of the Tail After Burst shape. The two
        /// shapes are otherwise hard to tell apart from the outside, and the
        /// full report is also what the mod falls back from when the cache is
        /// not ready yet, so the count says which one is actually sounding.
        /// </summary>
        internal static void RecordAuthoredReport()
        {
            if (!PerformanceTrace.Enabled) return;
            Interlocked.Increment(ref _authoredReports);
        }

        internal static string Format()
        {
            long scheduled = Interlocked.Exchange(ref Outcomes[0], 0L);
            long late = Interlocked.Exchange(ref Outcomes[1], 0L);
            long dropped = Interlocked.Exchange(ref Outcomes[2], 0L);
            long collapsed = Interlocked.Exchange(ref Outcomes[3], 0L);
            long early = Interlocked.Exchange(ref Outcomes[4], 0L);
            long reports = Interlocked.Exchange(ref _authoredReports, 0L);
            return $"autoBeats={scheduled}/{late}/{dropped}/{collapsed}/{early} fullReports={reports}";
        }

        internal static void Clear()
        {
            for (int index = 0; index < Outcomes.Length; index++)
                Interlocked.Exchange(ref Outcomes[index], 0L);
            Interlocked.Exchange(ref _authoredReports, 0L);
        }
    }

    internal readonly struct AudioFilterSample
    {
        internal readonly long Calls;
        internal readonly long Idle;
        internal readonly long Foreign;
        internal readonly long Overruns;
        internal readonly double TotalMilliseconds;
        internal readonly double P95Milliseconds;
        internal readonly double MaximumMilliseconds;

        internal AudioFilterSample(
            long calls,
            long idle,
            long foreign,
            long overruns,
            double totalMilliseconds,
            double p95Milliseconds,
            double maximumMilliseconds)
        {
            Calls = calls;
            Idle = idle;
            Foreign = foreign;
            Overruns = overruns;
            TotalMilliseconds = totalMilliseconds;
            P95Milliseconds = p95Milliseconds;
            MaximumMilliseconds = maximumMilliseconds;
        }
    }

    /// <summary>
    /// Per-type audio-thread accounting. Every entry point is a no-op while the
    /// trace is off: <see cref="Begin"/> returns zero and <see cref="Record"/>
    /// leaves on that zero, so an ordinary raid reads no Stopwatch, takes no lock
    /// and allocates nothing on the audio thread.
    /// </summary>
    internal static class AudioFilterTrace
    {
        internal const int Buckets = 128;
        private const long BucketsPerSecond = 20000; // 50 microseconds, as for contrast.
        private const int Kinds = (int)AudioFilterKind.Count;
        private static readonly long[] Ticks = new long[Kinds];
        private static readonly long[] Maximum = new long[Kinds];
        private static readonly long[] Calls = new long[Kinds];
        private static readonly long[] Idle = new long[Kinds];
        private static readonly long[] Foreign = new long[Kinds];
        private static readonly long[] Overruns = new long[Kinds];
        private static readonly long[][] Histogram = CreateHistogram();

        private static long[][] CreateHistogram()
        {
            var histogram = new long[Kinds][];
            for (int index = 0; index < Kinds; index++) histogram[index] = new long[Buckets];
            return histogram;
        }

        internal static long Begin() => PerformanceTrace.Enabled ? Stopwatch.GetTimestamp() : 0L;

        /// <param name="idle">The callback returned before doing any work.</param>
        /// <param name="foreign">The component sits on a pooled EFT source that is
        /// playing a sound this mod did not tag.</param>
        internal static void Record(
            AudioFilterKind kind,
            long start,
            int samples,
            int channels,
            bool idle = false,
            bool foreign = false)
        {
            if (start == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            if (elapsed < 0) elapsed = 0;
            int index = (int)kind;
            Interlocked.Increment(ref Calls[index]);
            if (idle) Interlocked.Increment(ref Idle[index]);
            if (foreign) Interlocked.Increment(ref Foreign[index]);
            Interlocked.Add(ref Ticks[index], elapsed);
            PerformanceTrace.SetMaximum(ref Maximum[index], elapsed);
            int bucket = (int)Math.Min(
                Buckets - 1, elapsed * BucketsPerSecond / Stopwatch.Frequency);
            Interlocked.Increment(ref Histogram[index][bucket]);
            int frames = channels > 0 ? samples / channels : 0;
            if (frames > 0 &&
                elapsed * PerformanceTrace.OutputSampleRate > (long)frames * Stopwatch.Frequency)
                Interlocked.Increment(ref Overruns[index]);
        }

        // Reused by the summary, which reads every kind once per window from the
        // main thread; the histogram itself is what the audio thread writes to.
        private static readonly long[] Snapshot = new long[Buckets];

        internal static AudioFilterSample Take(AudioFilterKind kind)
        {
            int index = (int)kind;
            long[] buckets = Histogram[index];
            long[] snapshot = Snapshot;
            long count = 0;
            for (int bucket = 0; bucket < Buckets; bucket++)
            {
                snapshot[bucket] = Interlocked.Exchange(ref buckets[bucket], 0);
                count += snapshot[bucket];
            }
            double p95 = 0;
            if (count > 0)
            {
                long target = Math.Max(1, (long)Math.Ceiling(count * 0.95));
                long cumulative = 0;
                for (int bucket = 0; bucket < Buckets; bucket++)
                {
                    cumulative += snapshot[bucket];
                    if (cumulative < target) continue;
                    p95 = (bucket + 1) * 1000.0 / BucketsPerSecond;
                    break;
                }
            }
            return new AudioFilterSample(
                Interlocked.Exchange(ref Calls[index], 0),
                Interlocked.Exchange(ref Idle[index], 0),
                Interlocked.Exchange(ref Foreign[index], 0),
                Interlocked.Exchange(ref Overruns[index], 0),
                PerformanceTrace.Milliseconds(Interlocked.Exchange(ref Ticks[index], 0)),
                p95,
                PerformanceTrace.Milliseconds(Interlocked.Exchange(ref Maximum[index], 0)));
        }

        internal static long CallCount(AudioFilterKind kind) => Volatile.Read(ref Calls[(int)kind]);

        internal static long IdleCount(AudioFilterKind kind) => Volatile.Read(ref Idle[(int)kind]);

        internal static long ForeignCount(AudioFilterKind kind) => Volatile.Read(ref Foreign[(int)kind]);

        internal static void Clear()
        {
            for (int index = 0; index < Kinds; index++)
            {
                Interlocked.Exchange(ref Ticks[index], 0);
                Interlocked.Exchange(ref Maximum[index], 0);
                Interlocked.Exchange(ref Calls[index], 0);
                Interlocked.Exchange(ref Idle[index], 0);
                Interlocked.Exchange(ref Foreign[index], 0);
                Interlocked.Exchange(ref Overruns[index], 0);
                long[] buckets = Histogram[index];
                for (int bucket = 0; bucket < Buckets; bucket++)
                    Interlocked.Exchange(ref buckets[bucket], 0);
            }
        }

        internal static string Name(AudioFilterKind kind)
        {
            switch (kind)
            {
                case AudioFilterKind.AutomaticCapture: return "beatCapture";
                case AudioFilterKind.AutomaticPitched: return "autoPitched";
                case AudioFilterKind.PitchedBand: return "pitchedBand";
                case AudioFilterKind.PitchedTail: return "pitchedTail";
                case AudioFilterKind.PitchedEnvelope: return "pitchedEnvelope";
                default: return "hearing";
            }
        }
    }

    internal static class PerformanceTrace
    {
        private const int AudioHistogramBuckets = 128;
        private const long AudioBucketsPerSecond = 20000; // 50 microseconds.
        private static readonly long[] AudioDurationBuckets = new long[AudioHistogramBuckets];

        internal static volatile bool Enabled;
        internal static readonly PerformanceCounters Work = new PerformanceCounters();
        internal static long AudioCallbacks, BufferChanges, AudioBytes, AudioTicks, AudioMaximumTicks;
        internal static int LastAudioSamples, LastAudioChannels, OutputSampleRate = 48000;

        internal readonly struct Scope : IDisposable
        {
            private readonly bool _active;
            private readonly long _start;
            private readonly PerformanceArea _area;

            internal Scope(PerformanceArea area)
            {
                _active = Enabled;
                _area = area;
                _start = _active ? Stopwatch.GetTimestamp() : 0;
            }

            public void Dispose()
            {
                if (_active) Work.Record(_area, Stopwatch.GetTimestamp() - _start);
            }
        }

        internal static Scope Measure(PerformanceArea area) => new Scope(area);
        internal static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        // Harmony prefix/postfix pairs cannot wrap the patched call in a using
        // block: they stamp here and close the interval in the postfix.
        internal static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        internal static void End(PerformanceArea area, long start, bool countCall = true)
        {
            if (start == 0) return;
            Work.Record(area, Stopwatch.GetTimestamp() - start, countCall);
        }

        // The callback caller has already allocated/supplied its buffer. A changed
        // identity is evidence of non-reuse, not proof of allocation (pooling exists).
        internal static void RecordAudio(
            float[] data,
            int channels,
            ref float[] previous,
            long start)
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            Interlocked.Increment(ref AudioCallbacks);
            if (!ReferenceEquals(data, previous)) Interlocked.Increment(ref BufferChanges);
            previous = data;
            if (data != null)
            {
                Interlocked.Add(ref AudioBytes, data.Length * 4L);
                Volatile.Write(ref LastAudioSamples, data.Length);
            }
            Volatile.Write(ref LastAudioChannels, Math.Max(1, channels));
            Interlocked.Add(ref AudioTicks, elapsed);
            SetMaximum(ref AudioMaximumTicks, elapsed);
            int bucket = (int)Math.Min(
                AudioHistogramBuckets - 1,
                elapsed * AudioBucketsPerSecond / Stopwatch.Frequency);
            Interlocked.Increment(ref AudioDurationBuckets[bucket]);
        }

        internal static void RecordAudio(float[] data, ref float[] previous, long start) =>
            RecordAudio(data, 2, ref previous, start);

        internal static void SetOutputSampleRate(int sampleRate)
        {
            Volatile.Write(ref OutputSampleRate, Math.Max(8000, sampleRate));
        }

        internal static void TakeAudioTiming(
            out double p95Milliseconds,
            out double p99Milliseconds,
            out double maximumMilliseconds)
        {
            var snapshot = new long[AudioHistogramBuckets];
            long count = 0;
            for (int index = 0; index < snapshot.Length; index++)
            {
                snapshot[index] = Interlocked.Exchange(ref AudioDurationBuckets[index], 0);
                count += snapshot[index];
            }
            p95Milliseconds = Percentile(snapshot, count, 0.95);
            p99Milliseconds = Percentile(snapshot, count, 0.99);
            maximumMilliseconds = Milliseconds(Interlocked.Exchange(ref AudioMaximumTicks, 0));
        }

        internal static void ClearAudio()
        {
            Interlocked.Exchange(ref AudioCallbacks, 0);
            Interlocked.Exchange(ref BufferChanges, 0);
            Interlocked.Exchange(ref AudioBytes, 0);
            Interlocked.Exchange(ref AudioTicks, 0);
            Interlocked.Exchange(ref AudioMaximumTicks, 0);
            for (int index = 0; index < AudioDurationBuckets.Length; index++)
                Interlocked.Exchange(ref AudioDurationBuckets[index], 0);
            Volatile.Write(ref LastAudioSamples, 0);
            Volatile.Write(ref LastAudioChannels, 0);
            AudioFilterTrace.Clear();
            MasterLimiterTrace.Clear();
            AutomaticScheduleTrace.Clear();
        }

        private static double Percentile(long[] buckets, long count, double percentile)
        {
            if (count <= 0) return 0;
            long target = Math.Max(1, (long)Math.Ceiling(count * percentile));
            long cumulative = 0;
            for (int index = 0; index < buckets.Length; index++)
            {
                cumulative += buckets[index];
                if (cumulative >= target)
                    return (index + 1) * 1000.0 / AudioBucketsPerSecond;
            }
            return buckets.Length * 1000.0 / AudioBucketsPerSecond;
        }

        internal static void SetMaximum(ref long target, long value)
        {
            long current = Volatile.Read(ref target);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    // Summary only: no per-frame logging, allocations, GC.Collect or game GC changes.
    internal sealed class PerformanceMonitor : MonoBehaviour
    {
        private readonly FrameTimeHistogram _frameTimes = new FrameTimeHistogram();
        private long _start, _lastFrame;
        private int _framesOver25, _slowFrames, _gcSlowFrames, _collections, _previousGc;
        private int _collections1, _collections2, _previousGc1, _previousGc2;
        private double _maxFrame, _maxGcFrame;
        private bool _modEnabled, _inGame;
        private float _contrast;
        private double _dspStart, _lastDsp, _maximumClockDriftMs;
        private int _maximumVoices, _maximumVoicePools;
        private int _configurationSampleRate, _configurationBufferSize;
        private int _configurationRealVoices, _configurationVirtualVoices;
        private AudioSpeakerMode _configurationSpeakerMode;

        private void LateUpdate()
        {
            bool trace = Plugin.ModConfig?.PerformanceSummaryLog.Value == true ||
                Plugin.ModConfig?.DiagnosticShotLog.Value == true;
            PerformanceTrace.Enabled = trace;
            if (!trace)
            {
                _start = 0;
                return;
            }

            long now = Stopwatch.GetTimestamp();
            int gc = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            bool mod = Plugin.ModConfig.Enabled.Value;
            float contrast = Plugin.ModConfig.GunshotContrastDb.Value;
            BetterAudio audio = AudioRuntimeLookup.Audio;
            bool inGame = audio != null && audio.ListenerPlayer != null &&
                audio.ListenerPlayer.IsYourPlayer;
            PerformanceTrace.SetOutputSampleRate(AudioSettings.outputSampleRate);
            LogAudioConfiguration();
            double dsp = AudioSettings.dspTime;
            if (_start == 0 || mod != _modEnabled || contrast != _contrast ||
                inGame != _inGame)
            {
                // Touched-source identities describe one raid, not a session.
                if (inGame != _inGame) GalSourceCensus.Clear();
                Reset(now, gc, gc1, gc2, dsp);
                _modEnabled = mod;
                _contrast = contrast;
                _inGame = inGame;
                return;
            }

            double frame = PerformanceTrace.Milliseconds(now - _lastFrame);
            _lastFrame = now;
            _lastDsp = dsp;
            int collections = Math.Max(0, gc - _previousGc);
            _previousGc = gc;
            _collections += collections;
            _collections1 += Math.Max(0, gc1 - _previousGc1);
            _previousGc1 = gc1;
            _collections2 += Math.Max(0, gc2 - _previousGc2);
            _previousGc2 = gc2;
            _frameTimes.Record(frame);
            _maxFrame = Math.Max(_maxFrame, frame);
            if (frame >= 25) _framesOver25++;
            if (collections != 0) _maxGcFrame = Math.Max(_maxGcFrame, frame);
            if (frame >= 50)
            {
                _slowFrames++;
                if (collections != 0) _gcSlowFrames++;
            }

            double seconds = PerformanceTrace.Milliseconds(now - _start) / 1000;
            // Positive drift means the audio clock advanced less than wall time:
            // the audio thread is behind, independently of the frame histogram.
            _maximumClockDriftMs = Math.Max(
                _maximumClockDriftMs, (seconds - (dsp - _dspStart)) * 1000.0);
            PitchedGunshotLayer.CountActiveVoices(out int voices, out int pools);
            _maximumVoices = Math.Max(_maximumVoices, voices);
            _maximumVoicePools = Math.Max(_maximumVoicePools, pools);
            if (seconds < 10) return;
            long callbacks = Interlocked.Exchange(ref PerformanceTrace.AudioCallbacks, 0);
            long changed = Interlocked.Exchange(ref PerformanceTrace.BufferChanges, 0);
            long bytes = Interlocked.Exchange(ref PerformanceTrace.AudioBytes, 0);
            long audioTicks = Interlocked.Exchange(ref PerformanceTrace.AudioTicks, 0);
            int samples = Volatile.Read(ref PerformanceTrace.LastAudioSamples);
            int channels = Math.Max(1, Volatile.Read(ref PerformanceTrace.LastAudioChannels));
            int sampleRate = Math.Max(8000, Volatile.Read(ref PerformanceTrace.OutputSampleRate));
            double bufferBudgetMs = samples <= 0
                ? 0
                : samples / (double)channels / sampleRate * 1000.0;
            PerformanceTrace.TakeAudioTiming(
                out double audioP95, out double audioP99, out double audioMax);
            PerformanceCounters work = PerformanceTrace.Work;
            GunshotContrastController contrastController = GunshotContrastController.Instance;
            DetailedDiagnostics.WritePerformance(
                $"window={seconds:0.0}s enabled={mod} inGame={inGame} contrast={contrast:0.0}dB " +
                $"contrastTracked={contrastController?.TrackedCount ?? 0} " +
                $"mixerRouted={contrastController?.MixerRoutedCount ?? 0} " +
                $"fallbackFilters={contrastController?.FallbackFilterCount ?? 0} " +
                $"fallbackFiltersCreated={contrastController?.FallbackFiltersCreated ?? 0} " +
                $"foreignMixerSources={contrastController?.ForeignMixerSources ?? 0} " +
                $"frames={_frameTimes.Count} p95Frame={_frameTimes.Percentile(.95):0.00}ms " +
                $"p99Frame={_frameTimes.Percentile(.99):0.00}ms maxFrame={_maxFrame:0.00}ms " +
                $"framesOver25={_framesOver25} framesOver50={_slowFrames} " +
                $"gc0={_collections} gcSlowFrames={_gcSlowFrames} maxGcFrame={_maxGcFrame:0.00}ms " +
                $"heapMiB={GC.GetTotalMemory(false) / 1048576.0:0.0} " +
                $"contrastCallbacksPerSec={callbacks / seconds:0.0} changedBuffers={changed}/{callbacks} " +
                $"bufferTrafficMiBPerSec={bytes / seconds / 1048576.0:0.00} " +
                $"contrastManagedDspMsPerSec={PerformanceTrace.Milliseconds(audioTicks) / seconds:0.00} " +
                $"contrastCallbackP95={audioP95:0.000}ms p99={audioP99:0.000}ms " +
                $"max={audioMax:0.000}ms bufferBudget={bufferBudgetMs:0.000}ms " +
                $"diagnosticQueue={DetailedDiagnostics.QueueCount}/{DetailedDiagnosticSession.DefaultQueueCapacity} " +
                $"normalizationPending={LowEndNormalizationCache.PendingCount} " +
                $"mainMsPerSec/maxCallMs " +
                $"hearing={Format(work, PerformanceArea.Hearing, seconds)} " +
                $"warmup={Format(work, PerformanceArea.Warmup, seconds)} " +
                $"normalization={Format(work, PerformanceArea.Normalization, seconds)} " +
                $"discovery={Format(work, PerformanceArea.ContrastDiscovery, seconds)} " +
                $"maintenance={Format(work, PerformanceArea.ContrastMaintenance, seconds)} " +
                $"hooks={Format(work, PerformanceArea.ContrastHooks, seconds)} " +
                $"gc1={_collections1} gc2={_collections2} " +
                $"audioClockRatio={(seconds > 0 ? (_lastDsp - _dspStart) / seconds : 0):0.0000} " +
                $"audioClockDriftMs={_maximumClockDriftMs:0.0} " +
                $"galVoices={_maximumVoices} galVoicePools={_maximumVoicePools} " +
                $"galVoicesCreated={PitchedGunshotLayer.CreatedVoiceCount} " +
                $"{GalSourceCensus.Format()} {MasterLimiterTrace.Format()} " +
                $"{AutomaticScheduleTrace.Format()}");
            DetailedDiagnostics.WritePerformance(
                $"audio filters window={seconds:0.0}s name=calls/idle/foreign msPerSec p95 max overBuffer " +
                FormatAudioFilters(seconds));
            DetailedDiagnostics.WritePerformance(
                $"hooks window={seconds:0.0}s name=msPerSec/maxCallMs/calls " +
                FormatHooks(work, seconds));
            Reset(now, gc, gc1, gc2, dsp);
        }

        // Called every traced frame: compare the values, never a formatted string,
        // so watching the configuration cannot itself produce garbage each frame.
        private void LogAudioConfiguration()
        {
            AudioConfiguration configuration = AudioSettings.GetConfiguration();
            if (configuration.sampleRate == _configurationSampleRate &&
                configuration.dspBufferSize == _configurationBufferSize &&
                configuration.numRealVoices == _configurationRealVoices &&
                configuration.numVirtualVoices == _configurationVirtualVoices &&
                configuration.speakerMode == _configurationSpeakerMode)
                return;
            _configurationSampleRate = configuration.sampleRate;
            _configurationBufferSize = configuration.dspBufferSize;
            _configurationRealVoices = configuration.numRealVoices;
            _configurationVirtualVoices = configuration.numVirtualVoices;
            _configurationSpeakerMode = configuration.speakerMode;
            DetailedDiagnostics.WritePerformance(
                $"audio configuration sampleRate={configuration.sampleRate} " +
                $"dspBufferSize={configuration.dspBufferSize} " +
                $"numRealVoices={configuration.numRealVoices} " +
                $"numVirtualVoices={configuration.numVirtualVoices} " +
                $"speakerMode={configuration.speakerMode}");
        }

        private static string FormatAudioFilters(double seconds)
        {
            var text = new System.Text.StringBuilder();
            for (int index = 0; index < (int)AudioFilterKind.Count; index++)
            {
                var kind = (AudioFilterKind)index;
                AudioFilterSample sample = AudioFilterTrace.Take(kind);
                if (sample.Calls == 0) continue;
                if (text.Length != 0) text.Append(' ');
                text.Append(AudioFilterTrace.Name(kind)).Append('=')
                    .Append(sample.Calls).Append('/')
                    .Append(sample.Idle).Append('/')
                    .Append(sample.Foreign).Append(' ')
                    .Append((sample.TotalMilliseconds / seconds).ToString("0.00")).Append(' ')
                    .Append(sample.P95Milliseconds.ToString("0.000")).Append(' ')
                    .Append(sample.MaximumMilliseconds.ToString("0.000")).Append(' ')
                    .Append(sample.Overruns);
            }
            return text.Length == 0 ? "none" : text.ToString();
        }

        private static string FormatHooks(PerformanceCounters work, double seconds)
        {
            var text = new System.Text.StringBuilder();
            for (int index = (int)PerformanceArea.HeadphoneRoute;
                index < (int)PerformanceArea.Count;
                index++)
            {
                var area = (PerformanceArea)index;
                if (work.Calls[index] == 0) continue;
                if (text.Length != 0) text.Append(' ');
                text.Append(HookName(area)).Append('=')
                    .Append(Format(work, area, seconds)).Append('/')
                    .Append(work.Calls[index]);
            }
            return text.Length == 0 ? "none" : text.ToString();
        }

        internal static string HookName(PerformanceArea area)
        {
            switch (area)
            {
                case PerformanceArea.HeadphoneRoute: return "headphoneRoute";
                case PerformanceArea.HookFireBullet: return "fireBullet";
                case PerformanceArea.HookPlayOnLocal: return "playOnLocal";
                case PerformanceArea.HookPlayOnForeign: return "playOnForeign";
                case PerformanceArea.HookEnqueue: return "enqueue";
                case PerformanceArea.HookReleaseQueue: return "releaseQueue";
                case PerformanceArea.HookUpdatePitch: return "updatePitch";
                case PerformanceArea.HookSpatialization: return "spatialization";
                case PerformanceArea.HookExplosion: return "explosion";
                case PerformanceArea.EftPlayOn: return "eftPlayOn";
                case PerformanceArea.EftSetMixerGroup: return "eftSetMixerGroup";
                case PerformanceArea.EftPlayScheduled: return "eftPlayScheduled";
                case PerformanceArea.WarmupPlan: return "warmupPlan";
                case PerformanceArea.WarmupLoad: return "warmupLoad";
                case PerformanceArea.WarmupCompose: return "warmupCompose";
                case PerformanceArea.WarmupPublish: return "warmupPublish";
                default: return "warmupRegister";
            }
        }

        private static string Format(
            PerformanceCounters work,
            PerformanceArea area,
            double seconds) =>
            $"{PerformanceTrace.Milliseconds(work.Total[(int)area]) / seconds:0.00}/" +
            $"{PerformanceTrace.Milliseconds(work.Maximum[(int)area]):0.00}";

        private void Reset(long now, int gc, int gc1, int gc2, double dsp)
        {
            _start = _lastFrame = now;
            _previousGc = gc;
            _previousGc1 = gc1;
            _previousGc2 = gc2;
            _dspStart = _lastDsp = dsp;
            _maximumClockDriftMs = 0;
            _maximumVoices = _maximumVoicePools = 0;
            _framesOver25 = _slowFrames = _gcSlowFrames = _collections = 0;
            _collections1 = _collections2 = 0;
            _maxFrame = _maxGcFrame = 0;
            _frameTimes.Clear();
            PerformanceTrace.Work.Clear();
            PerformanceTrace.ClearAudio();
        }

        private void OnDisable()
        {
            PerformanceTrace.Enabled = false;
            _start = 0;
        }
    }
}
