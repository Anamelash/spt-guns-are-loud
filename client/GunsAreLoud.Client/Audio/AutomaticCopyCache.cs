using System;
using System.Collections.Generic;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct CachedAutomaticCopy
    {
        internal readonly AudioClip Clip;
        internal readonly AudioClip OriginalClip;
        internal readonly float SourceSpanSeconds;
        internal readonly float OnsetSeconds;
        internal readonly bool IsFullReport;
        internal readonly bool NativePitch;

        internal CachedAutomaticCopy(
            AudioClip clip,
            float sourceSpanSeconds,
            float onsetSeconds,
            bool isFullReport = false,
            bool nativePitch = false,
            AudioClip originalClip = null)
        {
            Clip = clip;
            OriginalClip = originalClip;
            SourceSpanSeconds = sourceSpanSeconds;
            OnsetSeconds = onsetSeconds;
            IsFullReport = isFullReport;
            NativePitch = nativePitch;
        }
    }

    internal readonly struct AutomaticBeatCaptureTelemetry
    {
        internal readonly bool Armed;
        internal readonly bool Ready;
        internal readonly int CapturedFrames;
        internal readonly int TargetFrames;

        internal AutomaticBeatCaptureTelemetry(
            bool armed,
            bool ready,
            int capturedFrames,
            int targetFrames)
        {
            Armed = armed;
            Ready = ready;
            CapturedFrames = capturedFrames;
            TargetFrames = targetFrames;
        }
    }

    /// <summary>
    /// Main-thread cache of short PCM clips captured from one authored beat of an
    /// automatic weapon loop. No AudioClip.GetData call is made against EFT clips.
    /// </summary>
    internal static class AutomaticCopyCache
    {
        // The raw BeatLn remains untouched. This PCM-only padding keeps the
        // AudioSource alive while the derived tail filter decays after it.
        internal const float MaximumSilencePaddingSeconds = 0.65f;

        /// <summary>One fire interval of the weapon body: what every round of a
        /// held burst plays.</summary>
        private static readonly Dictionary<CacheKey, CachedAutomaticCopy> RoundBodies =
            new Dictionary<CacheKey, CachedAutomaticCopy>();
        /// <summary>That same body with the recorded tail glued after it: what one
        /// round plays when it has to carry the whole report on its own.</summary>
        private static readonly Dictionary<CacheKey, CachedAutomaticCopy> FullReports =
            new Dictionary<CacheKey, CachedAutomaticCopy>();

        internal static bool TryGetFullReport(AudioClip body, int rate, out CachedAutomaticCopy report)
        {
            report = default;
            return body != null && FullReports.TryGetValue(CacheKey.Create(body, 0f, rate), out report);
        }

        internal static void PublishFullReport(AudioClip body, AudioClip tail, int rate, float[] pcm)
        {
            CacheKey key = CacheKey.Create(body, 0f, rate);
            if (FullReports.ContainsKey(key)) return;
            int frames = pcm.Length / 2;
            AudioClip clip = AudioClip.Create($"GunsAreLoud.FullReport.{body.name}", frames, 2, rate, false);
            clip.hideFlags = HideFlags.DontSave;
            if (!clip.SetData(pcm, 0))
            {
                UnityEngine.Object.Destroy(clip);
                return;
            }
            FullReports.Add(key, new CachedAutomaticCopy(clip, frames / (float)rate, 0f,
                isFullReport: true, nativePitch: true, originalClip: body));
            if (DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.AutomaticCache, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(
                    reservation,
                    $"automatic full report ready body={body.name} tail={tail.name} " +
                    $"span={frames * 1000f / rate:0}ms rate={rate}");
        }

        internal static bool TryGetRoundBody(
            AudioClip sourceClip,
            float sourceSpanSeconds,
            int sampleRate,
            out CachedAutomaticCopy beat)
        {
            beat = default;
            return sourceClip != null && RoundBodies.TryGetValue(
                CacheKey.Create(sourceClip, sourceSpanSeconds, sampleRate),
                out beat);
        }

        internal static bool ContainsRoundBody(
            AudioClip sourceClip,
            float sourceSpanSeconds,
            int sampleRate)
        {
            return TryGetRoundBody(sourceClip, sourceSpanSeconds, sampleRate, out _);
        }

        internal static void PublishRoundBody(
            AudioClip sourceClip,
            float sourceSpanSeconds,
            int sampleRate,
            int frames,
            int channels,
            float[] pcm,
            bool nativePitch = false)
        {
            if (sourceClip == null || pcm == null || frames <= 0 || channels <= 0)
            {
                return;
            }

            CacheKey key = CacheKey.Create(sourceClip, sourceSpanSeconds, sampleRate);
            if (RoundBodies.ContainsKey(key))
            {
                return;
            }

            int onsetFrame = FindOnsetFrame(pcm, frames, channels, out float peak);
            if (peak < 0.0005f || onsetFrame >= frames)
            {
                if (DetailedDiagnostics.TryBegin(
                    DiagnosticEventKind.AutomaticCache, out DiagnosticReservation rejectedReservation))
                {
                    DetailedDiagnostics.Commit(
                        rejectedReservation,
                        $"automatic round body rejected clip={sourceClip.name} " +
                        $"frames={frames} peak={peak:0.000000}: captured PCM is silent");
                }
                return;
            }

            int prerollFrames = Mathf.Min(onsetFrame, Mathf.CeilToInt(sampleRate * 0.002f));
            int trimFrames = onsetFrame - prerollFrames;
            int contentFrames = frames - trimFrames;
            float[] alignedPcm = new float[contentFrames * channels];
            Array.Copy(
                pcm,
                trimFrames * channels,
                alignedPcm,
                0,
                alignedPcm.Length);

            int silenceFrames = Mathf.CeilToInt(
                MaximumSilencePaddingSeconds * sampleRate);
            AudioClip cached = AudioClip.Create(
                $"GunsAreLoud.RoundBody.{sourceClip.name}",
                contentFrames + silenceFrames,
                channels,
                sampleRate,
                false);
            cached.hideFlags = HideFlags.DontSave;
            if (!cached.SetData(alignedPcm, 0))
            {
                UnityEngine.Object.Destroy(cached);
                return;
            }

            RoundBodies.Add(
                key,
                new CachedAutomaticCopy(
                    cached,
                    contentFrames / (float)sampleRate,
                    trimFrames / (float)sampleRate,
                    nativePitch: nativePitch, originalClip: sourceClip));
            if (DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.AutomaticCache, out DiagnosticReservation cachedReservation))
            {
                DetailedDiagnostics.Commit(
                    cachedReservation,
                    $"automatic round body cached clip={sourceClip.name} " +
                    $"frames={contentFrames}/{frames} channels={channels} rate={sampleRate} " +
                    $"span={contentFrames * 1000f / sampleRate:0.0}ms " +
                    $"onset={trimFrames * 1000f / sampleRate:0.0}ms peak={peak:0.000} " +
                    $"silence={silenceFrames * 1000f / sampleRate:0}ms");
            }
        }

        internal static int FindOnsetFrame(
            float[] pcm,
            int frames,
            int channels,
            out float peak)
        {
            peak = 0f;
            if (pcm == null || frames <= 0 || channels <= 0)
            {
                return Math.Max(0, frames);
            }

            int sampleCount = Math.Min(pcm.Length, frames * channels);
            for (int sample = 0; sample < sampleCount; sample++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(pcm[sample]));
            }

            float threshold = Mathf.Max(0.0025f, peak * 0.04f);
            for (int frame = 0; frame < frames; frame++)
            {
                int offset = frame * channels;
                for (int channel = 0; channel < channels && offset + channel < sampleCount; channel++)
                {
                    if (Mathf.Abs(pcm[offset + channel]) >= threshold)
                    {
                        return frame;
                    }
                }
            }

            return frames;
        }

        internal static void Clear()
        {
            foreach (CachedAutomaticCopy roundBody in RoundBodies.Values)
            {
                if (roundBody.Clip != null)
                {
                    UnityEngine.Object.Destroy(roundBody.Clip);
                }
            }
            RoundBodies.Clear();
            foreach (CachedAutomaticCopy report in FullReports.Values)
                if (report.Clip != null) UnityEngine.Object.Destroy(report.Clip);
            FullReports.Clear();
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            private readonly int _clipId;
            private readonly int _sampleRate;

            private CacheKey(int clipId, int sampleRate)
            {
                _clipId = clipId;
                _sampleRate = sampleRate;
            }

            internal static CacheKey Create(
                AudioClip clip,
                float sourceSpanSeconds,
                int sampleRate)
            {
                int rate = Mathf.Max(8000, sampleRate);
                return new CacheKey(clip.GetInstanceID(), rate);
            }

            public bool Equals(CacheKey other)
            {
                return _clipId == other._clipId &&
                    _sampleRate == other._sampleRate;
            }

            public override bool Equals(object obj)
            {
                return obj is CacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _clipId;
                    hash = hash * 397 ^ _sampleRate;
                    return hash;
                }
            }
        }
    }

    /// <summary>
    /// Audio-thread tap armed before SuperAudioSample.PlayOn. It copies a fixed
    /// frame count from the beginning of the source-domain PCM callback. The
    /// callback is already scoped to this AudioSource; applying the future DSP
    /// schedule a second time skips the report attack.
    /// </summary>
    internal sealed class AutomaticBeatCapture : MonoBehaviour
    {
        private const int MaximumChannels = 2;
        private const int Idle = 0;
        private const int Armed = 1;
        private const int Complete = 2;
        private const int Published = 3;

        private int _state;
        private int _generation;
        private AudioClip _sourceClip;
        private float _sourceSpanSeconds;
        private int _sampleRate;
        private int _targetFrames;
        private int _targetChannels;
        private int _capturedFrames;
        private float[] _pcm;

        internal bool Arm(
            AudioClip sourceClip,
            double scheduledStart,
            float sourceSpanSeconds,
            int sampleRate)
        {
            if (sourceClip == null || sourceSpanSeconds <= 0.001f)
            {
                return false;
            }

            int rate = Mathf.Max(8000, sampleRate);
            float span = Mathf.Clamp(sourceSpanSeconds, 0.01f, 0.5f);
            int state = Volatile.Read(ref _state);
            if (state == Armed)
            {
                return ReferenceEquals(_sourceClip, sourceClip) &&
                    Mathf.Abs(_sourceSpanSeconds - span) < 0.0005f;
            }
            if (state == Complete)
            {
                // Update will publish the completed audio-thread buffer. Do not
                // overwrite it if a pooled source is reassigned in the meantime.
                return false;
            }

            _sourceClip = sourceClip;
            _sourceSpanSeconds = span;
            _sampleRate = rate;
            _targetFrames = Mathf.Max(1, Mathf.RoundToInt(span * rate));
            _targetChannels = Mathf.Clamp(sourceClip.channels, 1, MaximumChannels);
            if (AutomaticCopyCache.ContainsRoundBody(
                sourceClip,
                span,
                rate))
            {
                Volatile.Write(ref _state, Published);
                return false;
            }

            _capturedFrames = 0;
            int samples = _targetFrames * _targetChannels;
            // This scratch buffer belongs to this component alone, so reusing it
            // cannot collide with another capture that is still finishing; it is
            // released when the component is switched off on a foreign sound.
            if (_pcm == null || _pcm.Length < samples) _pcm = new float[samples];
            else Array.Clear(_pcm, 0, samples);
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _state, Armed);
            return true;
        }

        internal void Cancel()
        {
            if (Volatile.Read(ref _state) == Armed)
            {
                Interlocked.Increment(ref _generation);
                Volatile.Write(ref _state, Idle);
                _capturedFrames = 0;
            }
        }

        // A completed capture is published by Update on the main thread. Switching
        // the component off before that would silently drop a warmed cache entry.
        internal bool AwaitingPublish => Volatile.Read(ref _state) == Complete;

        internal AutomaticBeatCaptureTelemetry GetTelemetry()
        {
            int state = Volatile.Read(ref _state);
            bool cached = _sourceClip != null && AutomaticCopyCache.ContainsRoundBody(
                _sourceClip,
                _sourceSpanSeconds,
                _sampleRate);
            return new AutomaticBeatCaptureTelemetry(
                state == Armed,
                cached,
                Volatile.Read(ref _capturedFrames),
                _targetFrames);
        }

        private void Update()
        {
            if (Volatile.Read(ref _state) != Complete)
            {
                return;
            }

            AudioClip sourceClip = _sourceClip;
            float[] pcm = _pcm;
            int frames = _targetFrames;
            int channels = _targetChannels;
            AutomaticCopyCache.PublishRoundBody(
                sourceClip,
                _sourceSpanSeconds,
                _sampleRate,
                frames,
                channels,
                pcm);
            _pcm = null;
            Volatile.Write(ref _state, Published);
            // The cache now answers for this clip. Nothing here has work again
            // until a cold clip arms it, and ArmCapture switches it back on.
            enabled = false;
        }

        private void Awake() => GalSourceCensus.Created(GalComponentKind.BeatCapture);

        private void OnEnable() => GalSourceCensus.Enabled(GalComponentKind.BeatCapture);

        private void OnDisable()
        {
            GalSourceCensus.Disabled(GalComponentKind.BeatCapture);
            Cancel();
            // Released here rather than on every cancel: a burst re-arms this same
            // component within milliseconds and reuses the buffer.
            _pcm = null;
        }

        private void OnDestroy() => GalSourceCensus.Destroyed(GalComponentKind.BeatCapture);

        private void OnAudioFilterRead(float[] data, int channels)
        {
            long trace = AudioFilterTrace.Begin();
            if (Volatile.Read(ref _state) != Armed ||
                data == null ||
                channels <= 0 ||
                _pcm == null)
            {
                // Not armed: this pooled source is playing something else, and the
                // capture component is pure overhead on that buffer.
                AudioFilterTrace.Record(
                    AudioFilterKind.AutomaticCapture,
                    trace,
                    data == null ? 0 : data.Length,
                    channels,
                    idle: true,
                    foreign: true);
                return;
            }

            int generation = Volatile.Read(ref _generation);
            float[] pcm = _pcm;
            int capturedFrames = Volatile.Read(ref _capturedFrames);
            int targetFrames = _targetFrames;
            int targetChannels = _targetChannels;
            if (pcm == null)
            {
                AudioFilterTrace.Record(
                    AudioFilterKind.AutomaticCapture, trace, data.Length, channels, idle: true);
                return;
            }

            int bufferFrames = data.Length / channels;
            int startFrame = 0;
            if (startFrame >= bufferFrames)
            {
                AudioFilterTrace.Record(
                    AudioFilterKind.AutomaticCapture, trace, data.Length, channels, idle: true);
                return;
            }

            int availableFrames = bufferFrames - startFrame;
            int framesToCopy = Math.Min(
                availableFrames,
                targetFrames - capturedFrames);
            for (int frame = 0; frame < framesToCopy; frame++)
            {
                int inputOffset = (startFrame + frame) * channels;
                int outputOffset = (capturedFrames + frame) * targetChannels;
                for (int channel = 0; channel < targetChannels; channel++)
                {
                    pcm[outputOffset + channel] =
                        data[inputOffset + (channel % channels)];
                }
            }

            if (generation != Volatile.Read(ref _generation) ||
                Volatile.Read(ref _state) != Armed)
            {
                AudioFilterTrace.Record(
                    AudioFilterKind.AutomaticCapture, trace, data.Length, channels);
                return;
            }

            capturedFrames += framesToCopy;
            Volatile.Write(ref _capturedFrames, capturedFrames);
            if (capturedFrames >= targetFrames)
            {
                Volatile.Write(ref _state, Complete);
            }
            AudioFilterTrace.Record(
                AudioFilterKind.AutomaticCapture, trace, data.Length, channels);
        }
    }

    internal static class AutomaticBeatTiming
    {
        private const int BeatsPerLoop = 16;

        // Used only when binding an already-running source after a route change.
        // The current real shot belongs to the current beat; starting on the
        // next beat would shift every subsequent extra attack by one shot.
        internal static double CalculateCurrentBoundary(
            double now, int timeSamples, int clipSamples, int clipFrequency, float pitch)
        {
            if (clipSamples <= 0 || clipFrequency <= 0) return now;
            int beatFrames = Math.Max(1, clipSamples / BeatsPerLoop);
            int phase = PositiveModulo(timeSamples, clipSamples) % beatFrames;
            return now - phase / (clipFrequency * (double)Mathf.Max(0.1f, pitch));
        }

        internal static double CalculateNextBoundaryDelaySeconds(
            int timeSamples,
            int clipSamples,
            int clipFrequency,
            float pitch)
        {
            if (clipSamples <= 0 || clipFrequency <= 0)
            {
                return -1.0;
            }

            int beatFrames = Math.Max(1, clipSamples / BeatsPerLoop);
            int position = PositiveModulo(timeSamples, clipSamples);
            int phase = position % beatFrames;
            int remaining = phase == 0 ? 0 : beatFrames - phase;
            return remaining / (clipFrequency * (double)Mathf.Max(0.1f, pitch));
        }

        internal static double CalculateShotBoundary(
            double sequenceStart,
            int shotIndex,
            float beatSeconds)
        {
            return sequenceStart + Math.Max(0, shotIndex) * Math.Max(0.001f, beatSeconds);
        }

        private static int PositiveModulo(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }
    }

    /// <summary>
    /// One authoritative timeline per active automatic body loop. FireBullet
    /// continuation calls advance the shot index; they are never discarded due
    /// to the AudioSource phase observed on the main thread.
    /// </summary>
    internal sealed class AutomaticBeatTimeline : MonoBehaviour
    {
        private const double MinimumSchedulingLeadSeconds = 0.004;

        private static double ClockResolutionSeconds =>
            AudioRuntimeState.BufferFrames / (double)Math.Max(8000, AudioRuntimeState.OutputSampleRate);

        private SuperSource _source;
        private LocalGunshotAudioTuning _tuning;
        private AudioClip _clipA;
        private AudioClip _clipB;
        private float _beatSeconds;
        private float _clockInterval;
        private int _nextShotIndex;
        private double _sequenceStart;
        private double _lastScheduledStart = double.NegativeInfinity;
        private bool _active;
        private readonly AutomaticShotTiming _shotTiming = new AutomaticShotTiming();
        internal bool LastUsedFullReport { get; private set; }
        internal bool Active => _active && _source != null;

        internal void Bind(
            SuperSource source, LocalGunshotAudioTuning tuning, AudioClip clipA,
            AudioClip clipB, double sequenceStart, float beatSeconds)
        {
            _source = source; _tuning = tuning; _clipA = clipA; _clipB = clipB;
            _beatSeconds = Mathf.Max(0.001f, beatSeconds); _nextShotIndex = 1;
            _sequenceStart = sequenceStart;
            _lastScheduledStart = double.NegativeInfinity;
            _active = true; _shotTiming.Begin(sequenceStart, _beatSeconds);
            _clockInterval = _shotTiming.ObserveRound(AudioSettings.dspTime, _beatSeconds, ClockResolutionSeconds);
        }

        internal bool Begin(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            AudioClip clipA,
            AudioClip clipB,
            double sequenceStart,
            float beatSeconds,
            AutomaticShotContext context = null)
        {
            Bind(source, tuning, clipA, clipB, sequenceStart, beatSeconds);
            return Schedule(0, sequenceStart, context);
        }

        internal bool TriggerNext(LocalGunshotAudioTuning tuning, AutomaticShotContext context = null)
        {
            if (!_active || _source == null)
            {
                return false;
            }

            _tuning = tuning;
            int shotIndex = _nextShotIndex++;
            _beatSeconds = Mathf.Max(0.001f, tuning.PitchedLayerLoopBeatSeconds);
            _clockInterval = _shotTiming.ObserveRound(AudioSettings.dspTime, _beatSeconds, ClockResolutionSeconds);
            return Schedule(shotIndex, _shotTiming.Advance(_clockInterval), context);
        }

        internal void StopTimeline()
        {
            _active = false;
            _nextShotIndex = 0;
            _lastScheduledStart = double.NegativeInfinity;
            LastUsedFullReport = false;
        }

        internal void UpdateInterval(double changeTime, float beatSeconds)
        {
            if (!_active || Mathf.Abs(_beatSeconds - beatSeconds) <= 0.000001f) return;
            _beatSeconds = beatSeconds;
            // A burst already running on its own measured pace keeps it: the
            // recording's pitch says nothing about how fast the weapon fires.
            if (!_shotTiming.FollowsObservedPace)
            { _shotTiming.ChangeInterval(changeTime, beatSeconds); _clockInterval = beatSeconds; }
        }

        private bool Schedule(int shotIndex, double boundary, AutomaticShotContext context)
        {
            LastUsedFullReport = false;
            int sampleRate = AudioRuntimeState.OutputSampleRate;
            bool fullReportAReady = AutomaticCopyCache.TryGetFullReport(_clipA, sampleRate, out CachedAutomaticCopy fullReportA);
            bool fullReportBReady = AutomaticCopyCache.TryGetFullReport(_clipB, sampleRate, out CachedAutomaticCopy fullReportB);
            bool usesFullReport = _tuning.AutomaticTailMode == Configuration.AutomaticTailMode.FullReportPerShot &&
                (_clipA == null || fullReportAReady) && (_clipB == null || fullReportBReady);
            AutomaticCopyCache.TryGetRoundBody(
                _clipA,
                _beatSeconds,
                sampleRate,
                out CachedAutomaticCopy copyA);
            AutomaticCopyCache.TryGetRoundBody(
                _clipB,
                _beatSeconds,
                sampleRate,
                out CachedAutomaticCopy copyB);
            if (usesFullReport)
            {
                copyA = fullReportA;
                copyB = fullReportB;
            }
            if (copyA.Clip == null && copyB.Clip == null)
            {
                double fallbackNow = AudioSettings.dspTime;
                int fallbackBuffer = AudioRuntimeState.BufferFrames;
                AutomaticShotScheduleResult coldResult = AutomaticShotTiming.Classify(boundary, fallbackNow, 0,
                    AutomaticShotTiming.ProvisionalLateTolerance(
                        fallbackBuffer, sampleRate, _clockInterval, _tuning.AutomaticLateToleranceScale),
                    AutomaticShotTiming.ProvisionalEarlyTolerance(fallbackBuffer, sampleRate, _clockInterval));
                bool timely = coldResult != AutomaticShotScheduleResult.TooLate;
                // A cold cache plays nothing added for this round rather than an
                // approximation: the pitched copy is the only low-end path. The
                // clock still has to keep up, so the first warm round is on time.
                if (!timely) _shotTiming.CatchUp(fallbackNow, _clockInterval);
                else if (coldResult == AutomaticShotScheduleResult.TooEarly)
                    _shotTiming.Rebase(fallbackNow + AutomaticShotTiming.ProvisionalEarlyTolerance(
                        fallbackBuffer, sampleRate, _clockInterval), _clockInterval);
                DiagnosticShotToken diagnosticShot = context?.DiagnosticShot ?? default;
                if (DetailedDiagnostics.IsActive(diagnosticShot))
                    DetailedDiagnostics.Commit(
                        diagnosticShot,
                        DiagnosticEventKind.AutomaticTimeline,
                        $"automatic cache cold clip={_clipA?.name ?? _clipB?.name} " +
                        $"shot={shotIndex} timely={timely}; no delayed replay");
                return false;
            }

            float onsetSeconds = CalculateBlendedOnset(
                copyA,
                copyB,
                _source.source1 != null ? _source.source1.volume : 0f,
                _source.source2 != null ? _source.source2.volume : 0f);
            double requestedStart = boundary + onsetSeconds;
            double now = AudioSettings.dspTime;
            int bufferFrames = AudioRuntimeState.BufferFrames;
            AutomaticShotScheduleResult scheduleResult = AutomaticShotTiming.Classify(
                requestedStart,
                now,
                MinimumSchedulingLeadSeconds,
                AutomaticShotTiming.ProvisionalLateTolerance(
                    bufferFrames, sampleRate, _clockInterval, _tuning.AutomaticLateToleranceScale),
                AutomaticShotTiming.ProvisionalEarlyTolerance(bufferFrames, sampleRate, _clockInterval));
            if (scheduleResult == AutomaticShotScheduleResult.TooLate)
            {
                // The clock moves to the present rather than keeping the missed
                // boundary, so the copies a stall held back are never released
                // together behind the rounds they belong to — and a clock that has
                // drifted ahead of the game is put back on the present here.
                _shotTiming.Rebase(now, _clockInterval);
                AutomaticScheduleTrace.Record(AutomaticScheduleOutcome.Dropped);
                DiagnosticShotToken diagnosticShot = context?.DiagnosticShot ?? default;
                if (DetailedDiagnostics.IsActive(diagnosticShot))
                    DetailedDiagnostics.Commit(
                        diagnosticShot,
                        DiagnosticEventKind.AutomaticTimeline,
                        $"automatic copy skipped shot={shotIndex}: late by " +
                        $"{(now + MinimumSchedulingLeadSeconds - requestedStart) * 1000.0:0.0}ms");
                return false;
            }
            double earlyBy = 0.0;
            if (scheduleResult == AutomaticShotScheduleResult.TooEarly)
            {
                // The clock is behind the weapon. Pull this round back to the edge
                // of the budget — never leave it at a boundary that may lie seconds
                // ahead, after the trigger has been released. The edge rather than
                // the present: one reading of the audio clock is a buffer coarse,
                // and anchoring on it would push the next round past the late budget.
                earlyBy = requestedStart - now;
                requestedStart = now + MinimumSchedulingLeadSeconds +
                    AutomaticShotTiming.ProvisionalEarlyTolerance(bufferFrames, sampleRate, _clockInterval);
                _shotTiming.Rebase(requestedStart - onsetSeconds, _clockInterval);
            }
            double scheduledStart = AutomaticShotTiming.ResolveStart(
                requestedStart, now, MinimumSchedulingLeadSeconds, true);
            // A main-thread stall delivers several FireBullet calls in one frame.
            // Each is late, so each is clamped to the same earliest start and the
            // copies would sound as one stack instead of a burst. Keep the first.
            if (AutomaticShotTiming.CollapsesOntoPreviousStart(
                scheduledStart, requestedStart, _lastScheduledStart, _clockInterval))
            {
                _shotTiming.CatchUp(now, _clockInterval);
                AutomaticScheduleTrace.Record(AutomaticScheduleOutcome.Collapsed);
                DiagnosticShotToken collapsedShot = context?.DiagnosticShot ?? default;
                if (DetailedDiagnostics.IsActive(collapsedShot))
                    DetailedDiagnostics.Commit(
                        collapsedShot,
                        DiagnosticEventKind.AutomaticTimeline,
                        $"automatic copy dropped shot={shotIndex}: late copies collapsed onto " +
                        $"{(scheduledStart - _lastScheduledStart) * 1000.0:0.0}ms after the previous start");
                return false;
            }
            bool played = PitchedGunshotLayer.PlayCachedAutomaticBeat(
                _source,
                _tuning,
                scheduledStart,
                copyA,
                copyB);
            if (played)
            {
                _lastScheduledStart = scheduledStart;
                if (usesFullReport) AutomaticScheduleTrace.RecordAuthoredReport();
                AutomaticScheduleTrace.Record(
                    scheduleResult == AutomaticShotScheduleResult.Scheduled
                        ? AutomaticScheduleOutcome.Scheduled
                        : scheduleResult == AutomaticShotScheduleResult.TooEarly
                            ? AutomaticScheduleOutcome.Early
                            : AutomaticScheduleOutcome.Late);
            }
            if (context != null) context.FullReportScheduled = played && usesFullReport;
            LastUsedFullReport = played && usesFullReport;

            DiagnosticShotToken timelineDiagnosticShot = context?.DiagnosticShot ?? default;
            if (DetailedDiagnostics.IsActive(timelineDiagnosticShot))
            {
                DetailedDiagnostics.Commit(
                    timelineDiagnosticShot,
                    DiagnosticEventKind.AutomaticTimeline,
                    $"automatic copy timeline shot={shotIndex} played={played} fullReport={usesFullReport} " +
                    $"beat={_beatSeconds * 1000f:0.0}ms clock={_clockInterval * 1000f:0.0}ms " +
                    $"observedPace={_shotTiming.FollowsObservedPace} onset={onsetSeconds * 1000f:0.0}ms " +
                    $"lead={(requestedStart - now) * 1000.0:0.0}ms earlyBy={earlyBy * 1000.0:0.0}ms " +
                    $"schedule={scheduleResult} late={Math.Max(0.0, scheduledStart - requestedStart) * 1000.0:0.0}ms");
            }
            return played;
        }

        internal static float CalculateBlendedOnset(
            CachedAutomaticCopy copyA,
            CachedAutomaticCopy copyB,
            float volumeA,
            float volumeB)
        {
            if (copyA.Clip == null)
            {
                return copyB.OnsetSeconds;
            }
            if (copyB.Clip == null)
            {
                return copyA.OnsetSeconds;
            }

            float a = Mathf.Max(0f, volumeA);
            float b = Mathf.Max(0f, volumeB);
            float sum = a + b;
            return sum <= 0.0001f
                ? Mathf.Min(copyA.OnsetSeconds, copyB.OnsetSeconds)
                : (copyA.OnsetSeconds * a + copyB.OnsetSeconds * b) / sum;
        }

        private void Awake() => GalSourceCensus.Created(GalComponentKind.BeatTimeline);

        private void OnEnable() => GalSourceCensus.Enabled(GalComponentKind.BeatTimeline);

        private void OnDisable()
        {
            GalSourceCensus.Disabled(GalComponentKind.BeatTimeline);
            StopTimeline();
        }

        private void OnDestroy() => GalSourceCensus.Destroyed(GalComponentKind.BeatTimeline);
    }
}
