using System;
using System.Collections.Generic;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct CachedAutomaticBeat
    {
        internal readonly AudioClip Clip;
        internal readonly AudioClip OriginalClip;
        internal readonly float SourceSpanSeconds;
        internal readonly float OnsetSeconds;
        internal readonly bool HasAuthoredTail;
        internal readonly bool NativePitch;

        internal CachedAutomaticBeat(
            AudioClip clip,
            float sourceSpanSeconds,
            float onsetSeconds,
            bool hasAuthoredTail = false,
            bool nativePitch = false,
            AudioClip originalClip = null)
        {
            Clip = clip;
            OriginalClip = originalClip;
            SourceSpanSeconds = sourceSpanSeconds;
            OnsetSeconds = onsetSeconds;
            HasAuthoredTail = hasAuthoredTail;
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
    internal static class AutomaticBeatClipCache
    {
        // The raw BeatLn remains untouched. This PCM-only padding keeps the
        // AudioSource alive while the derived tail filter decays after it.
        internal const float MaximumSilencePaddingSeconds = 0.65f;

        private static readonly Dictionary<CacheKey, CachedAutomaticBeat> Entries =
            new Dictionary<CacheKey, CachedAutomaticBeat>();
        private static readonly Dictionary<CacheKey, CachedAutomaticBeat> Reports =
            new Dictionary<CacheKey, CachedAutomaticBeat>();

        internal static bool TryGetReport(AudioClip body, int rate, out CachedAutomaticBeat report)
        {
            report = default;
            return body != null && Reports.TryGetValue(CacheKey.Create(body, 0f, rate), out report);
        }

        internal static void PublishReport(AudioClip body, AudioClip tail, int rate, float[] pcm)
        {
            CacheKey key = CacheKey.Create(body, 0f, rate);
            if (Reports.ContainsKey(key)) return;
            int frames = pcm.Length / 2;
            AudioClip clip = AudioClip.Create($"GunsAreLoud.Report.{body.name}", frames, 2, rate, false);
            clip.hideFlags = HideFlags.DontSave;
            if (!clip.SetData(pcm, 0))
            {
                UnityEngine.Object.Destroy(clip);
                return;
            }
            Reports.Add(key, new CachedAutomaticBeat(clip, frames / (float)rate, 0f,
                hasAuthoredTail: true, nativePitch: true, originalClip: body));
            if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                Plugin.Log.LogInfo($"automatic report ready body={body.name} tail={tail.name} " +
                    $"span={frames * 1000f / rate:0}ms rate={rate}");
        }

        internal static bool TryGet(
            AudioClip sourceClip,
            float sourceSpanSeconds,
            int sampleRate,
            out CachedAutomaticBeat beat)
        {
            beat = default;
            return sourceClip != null && Entries.TryGetValue(
                CacheKey.Create(sourceClip, sourceSpanSeconds, sampleRate),
                out beat);
        }

        internal static bool Contains(
            AudioClip sourceClip,
            float sourceSpanSeconds,
            int sampleRate)
        {
            return TryGet(sourceClip, sourceSpanSeconds, sampleRate, out _);
        }

        internal static void Publish(
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
            if (Entries.ContainsKey(key))
            {
                return;
            }

            int onsetFrame = FindOnsetFrame(pcm, frames, channels, out float peak);
            if (peak < 0.0005f || onsetFrame >= frames)
            {
                if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                {
                    Plugin.Log.LogWarning(
                        $"automatic beat rejected clip={sourceClip.name} " +
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
                $"GunsAreLoud.Beat.{sourceClip.name}",
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

            Entries.Add(
                key,
                new CachedAutomaticBeat(
                    cached,
                    contentFrames / (float)sampleRate,
                    trimFrames / (float)sampleRate,
                    nativePitch: nativePitch, originalClip: sourceClip));
            if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
            {
                Plugin.Log.LogInfo(
                    $"automatic beat cached clip={sourceClip.name} " +
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
            foreach (CachedAutomaticBeat beat in Entries.Values)
            {
                if (beat.Clip != null)
                {
                    UnityEngine.Object.Destroy(beat.Clip);
                }
            }
            Entries.Clear();
            foreach (CachedAutomaticBeat report in Reports.Values)
                if (report.Clip != null) UnityEngine.Object.Destroy(report.Clip);
            Reports.Clear();
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
            if (AutomaticBeatClipCache.Contains(
                sourceClip,
                span,
                rate))
            {
                Volatile.Write(ref _state, Published);
                return false;
            }

            _capturedFrames = 0;
            _pcm = new float[_targetFrames * _targetChannels];
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
                _pcm = null;
                _capturedFrames = 0;
            }
        }

        internal AutomaticBeatCaptureTelemetry GetTelemetry()
        {
            int state = Volatile.Read(ref _state);
            bool cached = _sourceClip != null && AutomaticBeatClipCache.Contains(
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
            AutomaticBeatClipCache.Publish(
                sourceClip,
                _sourceSpanSeconds,
                _sampleRate,
                frames,
                channels,
                pcm);
            _pcm = null;
            Volatile.Write(ref _state, Published);
        }

        private void OnDisable()
        {
            Cancel();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (Volatile.Read(ref _state) != Armed ||
                data == null ||
                channels <= 0 ||
                _pcm == null)
            {
                return;
            }

            int generation = Volatile.Read(ref _generation);
            float[] pcm = _pcm;
            int capturedFrames = Volatile.Read(ref _capturedFrames);
            int targetFrames = _targetFrames;
            int targetChannels = _targetChannels;
            if (pcm == null)
            {
                return;
            }

            int bufferFrames = data.Length / channels;
            int startFrame = 0;
            if (startFrame >= bufferFrames)
            {
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
                return;
            }

            capturedFrames += framesToCopy;
            Volatile.Write(ref _capturedFrames, capturedFrames);
            if (capturedFrames >= targetFrames)
            {
                Volatile.Write(ref _state, Complete);
            }
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

        private SuperSource _source;
        private LocalGunshotAudioTuning _tuning;
        private AudioClip _clipA;
        private AudioClip _clipB;
        private float _beatSeconds;
        private int _nextShotIndex;
        private double _sequenceStart;
        private bool _active;
        private readonly AutomaticShotTiming _shotTiming = new AutomaticShotTiming();
        internal bool LastUsesAuthoredTail { get; private set; }
        internal bool Active => _active && _source != null;

        internal void Bind(
            SuperSource source, LocalGunshotAudioTuning tuning, AudioClip clipA,
            AudioClip clipB, double sequenceStart, float beatSeconds)
        {
            _source = source; _tuning = tuning; _clipA = clipA; _clipB = clipB;
            _beatSeconds = Mathf.Max(0.001f, beatSeconds); _nextShotIndex = 1;
            _sequenceStart = sequenceStart;
            _active = true; _shotTiming.Begin(sequenceStart, _beatSeconds);
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
            return Schedule(shotIndex, _shotTiming.Advance(_beatSeconds), context);
        }

        internal void StopTimeline()
        {
            _active = false;
            _nextShotIndex = 0;
            LastUsesAuthoredTail = false;
        }

        internal void UpdateInterval(double changeTime, float beatSeconds)
        {
            if (_active && Mathf.Abs(_beatSeconds - beatSeconds) > 0.000001f)
            { _shotTiming.ChangeInterval(changeTime, beatSeconds); _beatSeconds = beatSeconds; }
        }

        private bool Schedule(int shotIndex, double boundary, AutomaticShotContext context)
        {
            LastUsesAuthoredTail = false;
            int sampleRate = AudioSettings.outputSampleRate;
            bool reportAReady = AutomaticBeatClipCache.TryGetReport(_clipA, sampleRate, out CachedAutomaticBeat reportA);
            bool reportBReady = AutomaticBeatClipCache.TryGetReport(_clipB, sampleRate, out CachedAutomaticBeat reportB);
            bool completeReport = _tuning.AutomaticTailMode == Configuration.AutomaticTailMode.FullReportPerShot &&
                (_clipA == null || reportAReady) && (_clipB == null || reportBReady);
            AutomaticBeatClipCache.TryGet(
                _clipA,
                _beatSeconds,
                sampleRate,
                out CachedAutomaticBeat beatA);
            AutomaticBeatClipCache.TryGet(
                _clipB,
                _beatSeconds,
                sampleRate,
                out CachedAutomaticBeat beatB);
            if (completeReport)
            {
                beatA = reportA;
                beatB = reportB;
            }
            if (beatA.Clip == null && beatB.Clip == null)
            {
                double fallbackNow = AudioSettings.dspTime;
                AudioSettings.GetDSPBufferSize(out int fallbackBuffer, out _);
                bool timely = AutomaticShotTiming.Classify(boundary, fallbackNow, 0,
                    AutomaticShotTiming.ProvisionalLateTolerance(fallbackBuffer, sampleRate, _beatSeconds))
                    != AutomaticShotScheduleResult.TooLate;
                bool fallback = timely && LocalGunshotAudioProcessor.PlayAutomaticFallback(
                    _source, _tuning, boundary, shotIndex == 0 ? _sequenceStart : fallbackNow);
                if (!timely) _shotTiming.CatchUp(fallbackNow, _beatSeconds);
                if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                    Plugin.Log.LogInfo($"automatic cache cold clip={_clipA?.name ?? _clipB?.name} " +
                        $"shot={shotIndex} fallbackOriginalBand={fallback} timely={timely}; no delayed replay");
                return fallback;
            }

            LocalGunshotAudioProcessor.RetireAutomaticFallback(_source);

            float onsetSeconds = CalculateBlendedOnset(
                beatA,
                beatB,
                _source.source1 != null ? _source.source1.volume : 0f,
                _source.source2 != null ? _source.source2.volume : 0f);
            double requestedStart = boundary + onsetSeconds;
            double now = AudioSettings.dspTime;
            AudioSettings.GetDSPBufferSize(out int bufferFrames, out _);
            AutomaticShotScheduleResult scheduleResult = AutomaticShotTiming.Classify(
                requestedStart,
                now,
                MinimumSchedulingLeadSeconds,
                AutomaticShotTiming.ProvisionalLateTolerance(
                    bufferFrames, sampleRate, _beatSeconds));
            if (scheduleResult == AutomaticShotScheduleResult.TooLate)
            {
                _shotTiming.CatchUp(now, _beatSeconds);
                if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                    Plugin.Log.LogWarning(
                        $"automatic beat skipped shot={shotIndex}: late by " +
                        $"{(now + MinimumSchedulingLeadSeconds - requestedStart) * 1000.0:0.0}ms");
                return false;
            }
            double scheduledStart = AutomaticShotTiming.ResolveStart(
                requestedStart, now, MinimumSchedulingLeadSeconds, true);
            bool played = PitchedGunshotLayer.PlayCachedAutomaticBeat(
                _source,
                _tuning,
                scheduledStart,
                beatA,
                beatB);
            if (context != null) context.AuthoredTailScheduled = played && completeReport;
            LastUsesAuthoredTail = played && completeReport;

            if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
            {
                Plugin.Log.LogInfo(
                    $"automatic beat timeline shot={shotIndex} played={played} authoredTail={completeReport} " +
                    $"beat={_beatSeconds * 1000f:0.0}ms onset={onsetSeconds * 1000f:0.0}ms " +
                    $"lead={(requestedStart - now) * 1000.0:0.0}ms " +
                    $"schedule={scheduleResult} late={Math.Max(0.0, scheduledStart - requestedStart) * 1000.0:0.0}ms");
            }
            return played;
        }

        internal static float CalculateBlendedOnset(
            CachedAutomaticBeat beatA,
            CachedAutomaticBeat beatB,
            float volumeA,
            float volumeB)
        {
            if (beatA.Clip == null)
            {
                return beatB.OnsetSeconds;
            }
            if (beatB.Clip == null)
            {
                return beatA.OnsetSeconds;
            }

            float a = Mathf.Max(0f, volumeA);
            float b = Mathf.Max(0f, volumeB);
            float sum = a + b;
            return sum <= 0.0001f
                ? Mathf.Min(beatA.OnsetSeconds, beatB.OnsetSeconds)
                : (beatA.OnsetSeconds * a + beatB.OnsetSeconds * b) / sum;
        }

        private void OnDisable()
        {
            StopTimeline();
        }
    }
}
