using System;
using System.Collections.Generic;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct PitchedLayerTelemetry
    {
        internal readonly bool Scheduled;
        internal readonly bool Playing;
        internal readonly bool CachedAutomaticCopy;
        internal readonly float PitchRatio;
        internal readonly float HighpassHz;
        internal readonly float LowpassHz;
        internal readonly float OriginalDurationMs;
        internal readonly float OutputDurationMs;
        internal readonly float TailDurationMs;
        internal readonly float FadePercent;
        internal readonly float Gain;
        internal readonly float Occlusion;
        internal readonly int CallbackCount;
        internal readonly float InputPeak;
        internal readonly float OutputPeak;
        internal readonly bool OnsetTriggered;

        internal PitchedLayerTelemetry(
            bool scheduled,
            bool playing,
            bool cachedAutomaticCopy,
            float pitchRatio,
            float highpassHz,
            float lowpassHz,
            float originalDurationMs,
            float outputDurationMs,
            float tailDurationMs,
            float fadePercent,
            float gain,
            float occlusion,
            int callbackCount,
            float inputPeak,
            float outputPeak,
            bool onsetTriggered)
        {
            Scheduled = scheduled;
            Playing = playing;
            CachedAutomaticCopy = cachedAutomaticCopy;
            PitchRatio = pitchRatio;
            HighpassHz = highpassHz;
            LowpassHz = lowpassHz;
            OriginalDurationMs = originalDurationMs;
            OutputDurationMs = outputDurationMs;
            TailDurationMs = tailDurationMs;
            FadePercent = fadePercent;
            Gain = gain;
            Occlusion = occlusion;
            CallbackCount = callbackCount;
            InputPeak = inputPeak;
            OutputPeak = outputPeak;
            OnsetTriggered = onsetTriggered;
        }
    }

    /// <summary>
    /// Optional parallel copy of EFT's already-selected local gunshot clips.
    /// It never calls weapon/gameplay code and remains on the donor mixer group.
    /// </summary>
    internal sealed class PitchedGunshotLayer : MonoBehaviour
    {
        // Full authored tails overlap during automatic fire. The pool is bounded;
        // extreme pitch/rate combinations recycle the oldest voice.
        private const int MaximumVoices = 64;
        internal static PitchedGunshotLayer ReportPool { get; private set; }
        // Cached beat clips are destroyed when the last weapon releases them.
        // Every live pool must release its voices first, not only the report pool.
        private static readonly List<PitchedGunshotLayer> Layers = new List<PitchedGunshotLayer>();
        // Diagnostics only: a bounded pool that keeps creating voices is the
        // signal to look for, not the instantaneous count on its own.
        private static int _createdVoices;

        internal static int CreatedVoiceCount => _createdVoices;

        internal static void CountActiveVoices(out int voices, out int pools)
        {
            voices = 0;
            pools = 0;
            for (int index = 0; index < Layers.Count; index++)
            {
                PitchedGunshotLayer layer = Layers[index];
                if (layer == null) continue;
                pools++;
                Voice[] layerVoices = layer._voices;
                for (int voiceIndex = 0; voiceIndex < layerVoices.Length; voiceIndex++)
                {
                    Voice voice = layerVoices[voiceIndex];
                    if (voice != null && voice.Active) voices++;
                }
            }
        }

        private readonly Voice[] _voices = new Voice[MaximumVoices];
        private int _nextVoice;
        private bool _lastScheduled;
        private bool _lastCachedAutomaticCopy;
        private float _lastPitchRatio = 1f;
        private float _lastHighpassHz;
        private float _lastLowpassHz;
        private float _lastOriginalDurationMs;
        private float _lastOutputDurationMs;
        private float _lastTailDurationMs;
        private float _lastFadePercent;
        private float _lastGain;
        private float _lastOcclusion;
        private Voice _lastVoice;

        internal static bool Play(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            double scheduledStart,
            float originalDurationSeconds)
        {
            if (source == null ||
                tuning.DirectBodyGain <= 0.001f)
            {
                return false;
            }

            PitchedGunshotLayer layer = EnsureReportPool();
            if (layer == null) return false;

            return layer.PlayInternal(
                source,
                tuning,
                scheduledStart,
                originalDurationSeconds,
                default,
                default,
                false,
                detached: OutlivesSourceLease(cachedAutomaticCopy: false, isFullReport: false));
        }

        /// <summary>
        /// Whether a copy has to outlive the pooled source it was made from.
        /// <para>
        /// EFT re-issues that source to the next sound in the game — a casing, a
        /// footstep, anyone's shot — and a copy still bound to it is cut there and
        /// then. A copy is pitched down, so it always runs longer than the sound
        /// it came from: a full report by seconds, and the recorded tail played
        /// when the trigger is released by about as much again. Both must finish
        /// on their own.
        /// </para>
        /// The one copy that stays bound is the short authored body of a single
        /// round inside a burst: it is shorter than the interval to the next
        /// round, and stopping it when the source moves on is what keeps a burst
        /// from stacking.
        /// </summary>
        internal static bool OutlivesSourceLease(bool cachedAutomaticCopy, bool isFullReport)
        {
            return !cachedAutomaticCopy || isFullReport;
        }

        internal static bool PlayCachedAutomaticBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            double scheduledStart,
            CachedAutomaticCopy copyA,
            CachedAutomaticCopy copyB)
        {
            if (source == null ||
                tuning.DirectBodyGain <= 0.001f ||
                (copyA.Clip == null && copyB.Clip == null))
            {
                return false;
            }

            PitchedGunshotLayer layer = EnsureReportPool();
            if (layer == null) return false;

            float sourceSpanSeconds = Mathf.Max(
                CalculateCachedSourceDuration(copyA.SourceSpanSeconds, copyA.NativePitch, source.source1?.pitch ?? 1f),
                CalculateCachedSourceDuration(copyB.SourceSpanSeconds, copyB.NativePitch, source.source2?.pitch ?? 1f));
            return layer.PlayInternal(
                source,
                tuning,
                scheduledStart,
                sourceSpanSeconds,
                copyA,
                copyB,
                true,
                detached: OutlivesSourceLease(
                    cachedAutomaticCopy: true,
                    isFullReport: copyA.IsFullReport || copyB.IsFullReport));
        }

        internal static float CalculateOriginalDuration(
            double sampleStart,
            double sampleEnd,
            float loopBeatSeconds,
            AudioClip clipA,
            AudioClip clipB,
            float samplePitch)
        {
            float clipLength = Mathf.Max(
                clipA != null ? clipA.length : 0f,
                clipB != null ? clipB.length : 0f);
            return CalculateOriginalDuration(
                sampleStart,
                sampleEnd,
                loopBeatSeconds,
                clipLength,
                samplePitch);
        }

        internal static float CalculateOriginalDuration(
            double sampleStart,
            double sampleEnd,
            float loopBeatSeconds,
            float clipLength,
            float samplePitch)
        {
            if (sampleEnd > sampleStart)
            {
                // EFT's window is when it intends to be done with the sample, not
                // how long the recording is; for some tails it reserves the best
                // part of ten seconds. The copy reproduces the recording, so the
                // recording is the bound — without it the pitched copy of an m60
                // tail ran for ten seconds against a recording of one.
                float scheduled = (float)(sampleEnd - sampleStart);
                float recorded = clipLength / Mathf.Max(0.1f, samplePitch);
                return Mathf.Clamp(
                    clipLength > 0f ? Mathf.Min(scheduled, recorded) : scheduled, 0.01f, 10f);
            }

            if (loopBeatSeconds > 0.001f)
            {
                return Mathf.Clamp(loopBeatSeconds, 0.01f, 10f);
            }

            return Mathf.Clamp(clipLength / Mathf.Max(0.1f, samplePitch), 0.01f, 10f);
        }

        internal static float CalculateCachedSourceDuration(float span, bool nativePitch, float donorPitch)
        {
            return span / (nativePitch ? Mathf.Max(0.1f, donorPitch) : 1f);
        }

        internal static float CalculatePitchRatio(float semitonesDown)
        {
            return Mathf.Clamp(
                (float)Math.Pow(2.0, -Mathf.Clamp(semitonesDown, 1f, 24f) / 12.0),
                0.25f,
                0.95f);
        }

        internal static float CalculatePitchedDuration(
            float sourceSpanSeconds,
            float pitchRatio)
        {
            float sourceSpan = Mathf.Clamp(sourceSpanSeconds, 0.01f, 10f);
            float ratio = Mathf.Clamp(pitchRatio, 0.25f, 0.95f);
            return Mathf.Clamp(sourceSpan / ratio, 0.01f, 10f);
        }

        internal static float CalculateCachedOutputDuration(
            float sourceSpanSeconds,
            float pitchRatio,
            float tailSeconds)
        {
            return Mathf.Clamp(
                CalculatePitchedDuration(sourceSpanSeconds, pitchRatio) +
                Mathf.Clamp(tailSeconds, 0f, 0.6f),
                0.01f,
                10f);
        }

        private const float MinimumReleaseFadeSeconds = 0.02f;
        // Covers one DSP buffer of alignment between the envelope's first
        // callback and the scheduled start, so the hard source stop can never
        // cut a release ramp that is still sounding.
        private const double ReleaseStopMarginSeconds = 0.05;

        /// <summary>
        /// The longest an automatic copy may keep sounding once a later round of
        /// the same burst has followed it. A pitched-down full report is longer
        /// than the interval between rounds, so concurrency is a fixed number of
        /// fire intervals rather than a function of the rate of fire. Never
        /// applied to a copy on its own: a single shot and the last round of a
        /// burst have no successor and keep their whole recorded tail.
        /// </summary>
        internal static float CalculateAutomaticOverlapBound(
            float durationSeconds,
            float loopBeatSeconds,
            float overlapShots)
        {
            if (loopBeatSeconds <= 0.001f)
            {
                return durationSeconds;
            }

            float budget = Mathf.Clamp(loopBeatSeconds, 0.001f, 10f) *
                Mathf.Clamp(overlapShots, 1f, 32f);
            return Mathf.Clamp(Mathf.Min(durationSeconds, budget), 0.01f, 10f);
        }

        /// <summary>
        /// Fade used when a later round shortens an earlier copy: the configured
        /// fade-out portion of the overlap window, never shorter than a click-safe
        /// minimum and never longer than the window itself.
        /// </summary>
        internal static float CalculateReleaseFade(float windowSeconds, float fadePercent)
        {
            float window = Mathf.Max(0.001f, windowSeconds);
            float requested = window * Mathf.Clamp(fadePercent, 5f, 100f) * 0.01f;
            return Mathf.Clamp(requested, Mathf.Min(MinimumReleaseFadeSeconds, window), window);
        }

        internal static float CalculateLayerGain(
            float directBodyGain,
            float pressureFrequencyHz,
            float gainDb = 0f,
            float caliberContrastPercent = 100f)
        {
            // Current exposure model maps intermediate ammo to 99.05 Hz and
            // 9x19 to 111.65 Hz. Calibrate explicitly in dB: 3 dB difference
            // at 100%, 6 dB at 200%, instead of the previous 0.86/1.72 dB.
            // Keep the old heavy-caliber ceiling; add contrast by reducing the
            // pistol baseline rather than pushing every rifle into clipping.
            float caliberDb = Mathf.Clamp((99.05f - pressureFrequencyHz) * (3f / 12.6f),
                -6f, (float)(20.0 * Math.Log10(1.22)));
            float caliberWeight = (float)Math.Pow(10.0,
                caliberDb * Mathf.Clamp(caliberContrastPercent, 0f, 300f) / 2000.0);
            float userGain = CalculateUserGain(gainDb);
            return Mathf.Clamp(directBodyGain, 0f, 0.5f) * 1.25f * caliberWeight * userGain;
        }

        internal static float CalculateUserGain(float gainDb)
        {
            return (float)Math.Pow(10.0, Mathf.Clamp(gainDb, 0f, 30f) / 20.0);
        }

        internal static float CalculateOcclusionStrength(PitchedLayerOcclusionMode mode)
        {
            switch (mode)
            {
                case PitchedLayerOcclusionMode.Ignore:
                    return 0f;
                case PitchedLayerOcclusionMode.Enhanced:
                    return 1.75f;
                default:
                    return 1f;
            }
        }

        internal static float CalculateOccludedGain(
            float gain,
            float donorOcclusionVolumeFactor,
            PitchedLayerOcclusionMode mode)
        {
            float strength = CalculateOcclusionStrength(mode);
            if (strength <= 0f)
            {
                return gain;
            }

            return gain * Mathf.Pow(Mathf.Clamp01(donorOcclusionVolumeFactor), strength);
        }

        internal static float CalculateOccludedLowpass(
            float openLowpassHz,
            float occludedLowpassHz,
            float donorOcclusionVolumeFactor,
            PitchedLayerOcclusionMode mode)
        {
            float strength = CalculateOcclusionStrength(mode);
            float occlusion = Mathf.Clamp01(1f - donorOcclusionVolumeFactor);
            float blend = Mathf.Clamp01(occlusion * strength);
            float open = Mathf.Clamp(openLowpassHz, 80f, 3000f);
            float closed = Mathf.Clamp(occludedLowpassHz, 50f, open);
            return Mathf.Lerp(open, closed, blend);
        }

        internal PitchedLayerTelemetry GetTelemetry()
        {
            bool playing = false;
            foreach (Voice voice in _voices)
            {
                playing |= voice != null && voice.Active;
            }

            PitchedEnvelopeTelemetry envelope = _lastVoice != null
                ? _lastVoice.GetTelemetry()
                : default;

            return new PitchedLayerTelemetry(
                _lastScheduled,
                playing,
                _lastCachedAutomaticCopy,
                _lastPitchRatio,
                _lastHighpassHz,
                _lastLowpassHz,
                _lastOriginalDurationMs,
                _lastOutputDurationMs,
                _lastTailDurationMs,
                _lastFadePercent,
                _lastGain,
                _lastOcclusion,
                envelope.CallbackCount,
                envelope.InputPeak,
                envelope.OutputPeak,
                envelope.OnsetTriggered);
        }

        internal void StopAll()
        {
            foreach (Voice voice in _voices)
            {
                voice?.Stop();
            }
            _lastScheduled = false;
            _lastCachedAutomaticCopy = false;
        }

        /// <summary>
        /// Stops the copies that belong to one pooled EFT source, the way an
        /// in-source layer used to be stopped when that source was released or
        /// re-issued. Full reports are deliberately not bound to a source: their
        /// tail is longer than the lease and must finish.
        /// </summary>
        internal static void StopForSource(BetterSource source)
        {
            PitchedGunshotLayer pool = ReportPool;
            if (pool == null || source == null) return;
            int ownerId = source.GetInstanceID();
            foreach (Voice voice in pool._voices)
            {
                if (voice != null && voice.BelongsTo(ownerId)) voice.Stop();
            }
        }

        // One pool for every copy. Voices are 2D and read everything they need
        // from the donor at scheduling time, so they do not need to live on the
        // donor's GameObject, and the pooled sources stay free of our components.
        private static PitchedGunshotLayer EnsureReportPool()
        {
            if (ReportPool != null) return ReportPool;
            if (Plugin.Runtime == null) return null;
            var owner = new GameObject("GunsAreLoud.CompleteReportPool");
            owner.transform.SetParent(Plugin.Runtime.transform, false);
            ReportPool = owner.AddComponent<PitchedGunshotLayer>();
            return ReportPool;
        }

        private void Update()
        {
            if (Plugin.ModConfig?.Enabled.Value != true)
            {
                StopAll();
                return;
            }
            foreach (Voice voice in _voices)
            {
                voice?.StopIfEnvelopeCompleted();
            }
        }

        private void Awake() => GalSourceCensus.Created(GalComponentKind.PitchedLayer);

        private void OnEnable()
        {
            GalSourceCensus.Enabled(GalComponentKind.PitchedLayer);
            if (!Layers.Contains(this)) Layers.Add(this);
        }

        private void OnDisable()
        {
            GalSourceCensus.Disabled(GalComponentKind.PitchedLayer);
            Layers.Remove(this);
            StopAll();
        }

        private void OnDestroy() => GalSourceCensus.Destroyed(GalComponentKind.PitchedLayer);

        internal static void StopAllLayers()
        {
            for (int index = Layers.Count - 1; index >= 0; index--)
            {
                PitchedGunshotLayer layer = Layers[index];
                if (layer == null) Layers.RemoveAt(index);
                else layer.StopAll();
            }
        }

        private bool PlayInternal(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            double scheduledStart,
            float originalDurationSeconds,
            CachedAutomaticCopy copyA,
            CachedAutomaticCopy copyB,
            bool cachedAutomaticCopy,
            bool detached)
        {
            float pitchRatio = CalculatePitchRatio(tuning.PitchedLayerSemitones);
            float donorOcclusion = Mathf.Clamp01(source.OcclusionVolumeFactor);
            float lowpassHz = CalculateOccludedLowpass(
                tuning.PitchedLayerLowpassHz,
                tuning.PitchedLayerOccludedLowpassHz,
                donorOcclusion,
                tuning.PitchedLayerOcclusion);
            float highpassHz = Mathf.Clamp(
                tuning.PitchedLayerHighpassHz,
                10f,
                Mathf.Max(10f, lowpassHz - 10f));
            float openSourceGain = CalculateLayerGain(
                tuning.DirectBodyGain,
                tuning.PressureFrequencyHz,
                0f,
                tuning.CaliberContrastPercent);
            float sourceGain = CalculateOccludedGain(
                openSourceGain,
                donorOcclusion,
                tuning.PitchedLayerOcclusion);
            // Kept outside the normalizer: protection must not be gained back.
            float userGain = CalculateUserGain(tuning.PitchedLayerGainDb) * tuning.HeadphonesDamping.BodyGain;
            float totalGain = sourceGain * userGain;
            float fadePercent = Mathf.Clamp(tuning.PitchedLayerFadePercent, 5f, 100f);
            float sourceSpanSeconds = Mathf.Clamp(originalDurationSeconds, 0.01f, 10f);
            float excitationSeconds = CalculatePitchedDuration(sourceSpanSeconds, pitchRatio);
            float tailSeconds = cachedAutomaticCopy && !copyA.IsFullReport && !copyB.IsFullReport
                ? Mathf.Clamp(tuning.AutomaticPitchedTailSeconds, 0f, 0.6f)
                : 0f;
            // Every copy is scheduled whole. Only a later round of the same burst
            // shortens it (ReleasePredecessors), so a single shot and the last
            // round keep the full recorded tail: in FullReportPerShot that copy is
            // the only tail the player hears, the release copy being skipped.
            float durationSeconds = cachedAutomaticCopy
                ? CalculateCachedOutputDuration(
                    sourceSpanSeconds,
                    pitchRatio,
                    tailSeconds)
                : excitationSeconds;
            if (totalGain <= 0.001f)
            {
                return false;
            }

            AudioClip normalizationClipA = cachedAutomaticCopy ? copyA.OriginalClip : source.source1?.clip;
            AudioClip normalizationClipB = cachedAutomaticCopy ? copyB.OriginalClip : source.source2?.clip;
            LowEndNormalizationResult levelA = LowEndNormalizationCache.Evaluate(normalizationClipA, tuning);
            LowEndNormalizationResult levelB = LowEndNormalizationCache.Evaluate(normalizationClipB, tuning);
            bool readyA = levelA.Ready, readyB = levelB.Ready;
            if (tuning.PitchedLayerLoopBeatSeconds > 0f &&
                tuning.AutomaticPitchedRoute == AutomaticPitchedRoute.BuiltInDSP)
            {
                // The diagnostic in-source DSP has its own filter response and
                // no matching offline measurement. Do not claim normalization.
                readyA = readyB = false;
                levelA = levelB = new LowEndNormalizationResult(1f, false);
            }

            // One reservation covers the summary line and the per-channel
            // post-envelope lines. Two independent TryBegin calls competed for
            // the same rate gate and described different shots.
            bool measureLevels = DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.Normalization, out DiagnosticReservation reservation);
            Voice voice = GetNextVoice();
            if (!voice.Schedule(
                source.source1,
                source.source2,
                copyA.Clip,
                copyB.Clip,
                copyA.NativePitch,
                copyB.NativePitch,
                cachedAutomaticCopy,
                pitchRatio,
                highpassHz,
                lowpassHz,
                excitationSeconds,
                tailSeconds,
                durationSeconds,
                fadePercent,
                sourceGain,
                userGain,
                scheduledStart,
                tuning.HeadphonesDamping.TailDbPerSecond,
                levelA,
                levelB,
                measureLevels,
                reservation))
            {
                return false;
            }

            if (measureLevels)
                DetailedDiagnostics.Commit(
                    reservation,
                    $"low-end normalization voice={voice.DiagnosticId} clip={normalizationClipA?.name ?? normalizationClipB?.name} " +
                    // Reported in the same unit the F12 controls now use.
                    $"amount={tuning.LowEndNormalizationPercent * Configuration.ModConfig.LowEndNormalizationSpanDb / 100f:0.0}dB " +
                    $"contrast={tuning.CaliberContrastPercent * Configuration.ModConfig.CartridgeContrastSpanDb / 100f:0.0}dB " +
                    $"basis={(tuning.NormalizeBass ? "bass180" : "wide")} factorA={levelA.BodyGain:0.000} readyA={readyA} factorB={levelB.BodyGain:0.000} readyB={readyB} " +
                    $"decayGainA={levelA.DecayGain:0.000} decayReadyA={levelA.DecayReady} decayRmsA={levelA.DecayRms:0.00000} " +
                    $"decayTargetA={levelA.DecayTargetRms:0.00000} decayStartA={levelA.DecayStartSeconds * 1000:0}ms decayLimitedA={levelA.DecayLimited} " +
                    $"decayGainB={levelB.DecayGain:0.000} decayReadyB={levelB.DecayReady} decayRmsB={levelB.DecayRms:0.00000} " +
                    $"decayTargetB={levelB.DecayTargetRms:0.00000} decayStartB={levelB.DecayStartSeconds * 1000:0}ms decayLimitedB={levelB.DecayLimited} " +
                    $"rmsA={levelA.MeasuredRms:0.00000} targetA={levelA.TargetRms:0.00000} limitedA={levelA.Limited} " +
                    $"rmsB={levelB.MeasuredRms:0.00000} targetB={levelB.TargetRms:0.00000} limitedB={levelB.Limited} " +
                    $"headphoneBodyCut={tuning.HeadphonesDamping.BodyAttenuationDb:0.0}dB " +
                    $"headphoneTailDecay={tuning.HeadphonesDamping.TailDbPerSecond:0.0}dB/s");

            _lastScheduled = true;
            _lastCachedAutomaticCopy = cachedAutomaticCopy;
            _lastPitchRatio = pitchRatio;
            _lastHighpassHz = highpassHz;
            _lastLowpassHz = lowpassHz;
            _lastOriginalDurationMs = sourceSpanSeconds * 1000f;
            _lastOutputDurationMs = durationSeconds * 1000f;
            _lastTailDurationMs = tailSeconds * 1000f;
            _lastFadePercent = fadePercent;
            _lastGain = totalGain;
            _lastOcclusion = 1f - donorOcclusion;
            _lastVoice = voice;
            voice.BindOwner(detached ? 0 : source.GetInstanceID());
            if (cachedAutomaticCopy && tuning.PitchedLayerLoopBeatSeconds > 0.001f)
            {
                voice.MarkBurstCopy();
                ReleasePredecessors(
                    voice,
                    tuning.PitchedLayerLoopBeatSeconds,
                    tuning.AutomaticReportOverlapShots,
                    fadePercent);
            }
            return true;
        }

        // Called once a burst copy is scheduled. Earlier copies still sounding are
        // asked to finish within the overlap window of their own start; the newest
        // copy stays whole until a later round follows it, which is what keeps the
        // full tail on a single shot and on the last round of a burst.
        private void ReleasePredecessors(
            Voice latest,
            float loopBeatSeconds,
            float overlapShots,
            float fadePercent)
        {
            float window = CalculateAutomaticOverlapBound(10f, loopBeatSeconds, overlapShots);
            float fade = CalculateReleaseFade(window, fadePercent);
            double now = AudioSettings.dspTime;
            int released = 0;
            foreach (Voice voice in _voices)
            {
                if (voice == null || ReferenceEquals(voice, latest) || !voice.BurstCopy ||
                    !voice.Active || voice.Start > latest.Start)
                    continue;
                if (voice.ReleaseBy(voice.Start + window, fade, now)) released++;
            }

            if (released > 0 && DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.AutomaticTimeline, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(
                    reservation,
                    $"automatic report overlap released={released} " +
                    $"window={window * 1000f:0}ms fade={fade * 1000f:0}ms");
        }

        private Voice GetNextVoice()
        {
            for (int offset = 0; offset < MaximumVoices; offset++)
            {
                int index = (_nextVoice + offset) % MaximumVoices;
                Voice candidate = _voices[index];
                if (candidate == null)
                {
                    candidate = new Voice(transform, index);
                    _voices[index] = candidate;
                    _createdVoices++;
                }

                candidate.StopIfEnvelopeCompleted();
                if (!candidate.Active)
                {
                    _nextVoice = (index + 1) % MaximumVoices;
                    return candidate;
                }
            }

            int reusedIndex = _nextVoice;
            _nextVoice = (_nextVoice + 1) % MaximumVoices;
            _voices[reusedIndex].Stop();
            return _voices[reusedIndex];
        }

        private sealed class Voice
        {
            private readonly AudioSource _sourceA;
            private readonly AudioSource _sourceB;
            private readonly PitchedBandPassFilter _bandA;
            private readonly PitchedBandPassFilter _bandB;
            private readonly PitchedGunshotTailFilter _tailA;
            private readonly PitchedGunshotTailFilter _tailB;
            private readonly PitchedGunshotEnvelopeFilter _envelopeA;
            private readonly PitchedGunshotEnvelopeFilter _envelopeB;
            private bool _scheduledA;
            private bool _scheduledB;
            private double _scheduledEnd;
            private static long _nextDiagnosticId;
            private bool _measureLevels;
            private DiagnosticReservation _levelReservation;
            internal long DiagnosticId { get; private set; }

            internal Voice(Transform parent, int index)
            {
                CreateChannel(
                    parent,
                    $"GunsAreLoud.Pitched.{index}.A",
                    out _sourceA,
                    out _bandA,
                    out _tailA,
                    out _envelopeA);
                CreateChannel(
                    parent,
                    $"GunsAreLoud.Pitched.{index}.B",
                    out _sourceB,
                    out _bandB,
                    out _tailB,
                    out _envelopeB);
            }

            internal bool Active => _scheduledA || _scheduledB;

            private double _start;
            private bool _burstCopy;
            private int _ownerId;
            private double _releasedEnd = double.PositiveInfinity;

            internal double Start => _start;
            internal bool BurstCopy => _burstCopy;

            internal void MarkBurstCopy() => _burstCopy = true;

            /// <param name="ownerId">Instance id of the pooled EFT source whose
            /// release should also end this copy, or 0 for a copy that must finish
            /// on its own (a full report, whose tail outlives the lease).</param>
            internal void BindOwner(int ownerId) => _ownerId = ownerId;

            internal bool BelongsTo(int ownerId) =>
                _ownerId != 0 && _ownerId == ownerId && Active;

            /// <summary>
            /// Shortens this copy so it finishes by <paramref name="endDsp"/> with
            /// an equal-power fade. Returns false when it already ends at or before
            /// that time, so the same request repeated by every later round of the
            /// burst costs nothing and never moves an earlier release later.
            /// </summary>
            internal bool ReleaseBy(double endDsp, float fadeSeconds, double now)
            {
                if (!Active || endDsp >= _releasedEnd) return false;
                double fade = Math.Max(MinimumReleaseFadeSeconds, fadeSeconds);
                double end = Math.Max(endDsp, now + fade);
                if (end >= _scheduledEnd) return false;

                float startSeconds = (float)(end - fade - _start);
                if (_scheduledA) _envelopeA.ScheduleRelease(startSeconds, (float)fade);
                if (_scheduledB) _envelopeB.ScheduleRelease(startSeconds, (float)fade);
                double stopAt = end + ReleaseStopMarginSeconds;
                if (_scheduledA) _sourceA.SetScheduledEndTime(stopAt);
                if (_scheduledB) _sourceB.SetScheduledEndTime(stopAt);
                _scheduledEnd = stopAt;
                _releasedEnd = endDsp;
                return true;
            }

            internal bool Schedule(
                AudioSource donorA,
                AudioSource donorB,
                AudioClip overrideClipA,
                AudioClip overrideClipB,
                bool nativePitchA,
                bool nativePitchB,
                bool cachedAutomaticCopy,
                float pitchRatio,
                float highpassHz,
                float lowpassHz,
                float excitationSeconds,
                float tailSeconds,
                float durationSeconds,
                float fadePercent,
                float sourceGain,
                float userGain,
                double scheduledStart,
                float headphoneTailDbPerSecond,
                LowEndNormalizationResult calibrationA,
                LowEndNormalizationResult calibrationB,
                bool measureLevels,
                DiagnosticReservation levelReservation)
            {
                Stop();
                _measureLevels = measureLevels;
                _levelReservation = levelReservation;
                DiagnosticId = ++_nextDiagnosticId;
                double intendedEnd = scheduledStart + durationSeconds;
                double start = Math.Max(scheduledStart, AudioSettings.dspTime + 0.002);
                // A re-issued pooled voice starts whole: only its own successor
                // may shorten it, never a release aimed at its previous copy.
                _start = start;
                _burstCopy = false;
                _releasedEnd = double.PositiveInfinity;
                _scheduledEnd = Math.Max(start + 0.005, intendedEnd);
                float effectiveDurationSeconds = (float)(_scheduledEnd - start);
                _scheduledA = ScheduleChannel(
                    _sourceA,
                    _bandA,
                    _tailA,
                    _envelopeA,
                    donorA,
                    overrideClipA,
                    nativePitchA,
                    cachedAutomaticCopy,
                    pitchRatio,
                    highpassHz,
                    lowpassHz,
                    excitationSeconds,
                    tailSeconds,
                    effectiveDurationSeconds,
                    fadePercent,
                    sourceGain,
                    userGain,
                    start,
                    _scheduledEnd,
                    _measureLevels,
                    headphoneTailDbPerSecond,
                    calibrationA);
                _scheduledB = ScheduleChannel(
                    _sourceB,
                    _bandB,
                    _tailB,
                    _envelopeB,
                    donorB,
                    overrideClipB,
                    nativePitchB,
                    cachedAutomaticCopy,
                    pitchRatio,
                    highpassHz,
                    lowpassHz,
                    excitationSeconds,
                    tailSeconds,
                    effectiveDurationSeconds,
                    fadePercent,
                    sourceGain,
                    userGain,
                    start,
                    _scheduledEnd,
                    _measureLevels,
                    headphoneTailDbPerSecond,
                    calibrationB);
                return Active;
            }

            internal PitchedEnvelopeTelemetry GetTelemetry()
            {
                PitchedEnvelopeTelemetry a = _envelopeA.GetTelemetry();
                PitchedEnvelopeTelemetry b = _envelopeB.GetTelemetry();
                return new PitchedEnvelopeTelemetry(
                    a.CallbackCount + b.CallbackCount,
                    Mathf.Max(a.InputPeak, b.InputPeak),
                    Mathf.Max(a.OutputPeak, b.OutputPeak),
                    a.OnsetTriggered || b.OnsetTriggered);
            }

            internal void StopIfEnvelopeCompleted()
            {
                if (!Active)
                {
                    return;
                }

                if (AudioSettings.dspTime >= _scheduledEnd)
                {
                    Stop();
                    return;
                }

                bool aComplete = !_scheduledA || _envelopeA.Completed;
                bool bComplete = !_scheduledB || _envelopeB.Completed;
                if (aComplete && bComplete)
                {
                    Stop();
                }
            }

            internal void Stop()
            {
                if (Active && _measureLevels)
                {
                    // Main thread only. These are per-copy post-envelope samples,
                    // not the listener mix, and A/B are never summed as if uncorrelated.
                    if (_scheduledA) LogLevel("A", _sourceA, _envelopeA);
                    if (_scheduledB) LogLevel("B", _sourceB, _envelopeB);
                }
                StopChannel(_sourceA, _bandA, _tailA, _envelopeA);
                StopChannel(_sourceB, _bandB, _tailB, _envelopeB);
                _scheduledA = false;
                _scheduledB = false;
                _scheduledEnd = 0.0;
                _ownerId = 0;
                _measureLevels = false;
                _levelReservation = default;
            }

            private void LogLevel(string channel, AudioSource source, PitchedGunshotEnvelopeFilter envelope)
            {
                PitchedEnvelopeTelemetry t = envelope.GetTelemetry();
                DetailedDiagnostics.Commit(
                    _levelReservation,
                    $"low-end voice voice={DiagnosticId} channel={channel} clip={source.clip?.name} " +
                    $"stage=post-envelope attackRms={t.AttackRms:0.00000} attackFrames={t.AttackFrames} " +
                    $"bassAttackRms={t.BassAttackRms:0.00000} textureAttackRms={t.TextureAttackRms:0.00000} split=180Hz " +
                    $"tailRms={t.TailRms:0.00000} tailFrames={t.TailFrames} sourceVolume={source.volume:0.000} " +
                    $"outputPeak={t.OutputPeak:0.00000} callbacks={t.CallbackCount}");
            }

            private static void CreateChannel(
                Transform parent,
                string name,
                out AudioSource source,
                out PitchedBandPassFilter band,
                out PitchedGunshotTailFilter tail,
                out PitchedGunshotEnvelopeFilter envelope)
            {
                var channelObject = new GameObject(name);
                channelObject.transform.SetParent(parent, false);
                source = channelObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.spatialize = false;
                source.dopplerLevel = 0f;
                source.bypassEffects = false;
                source.bypassListenerEffects = false;
                source.bypassReverbZones = true;

                band = channelObject.AddComponent<PitchedBandPassFilter>();
                tail = channelObject.AddComponent<PitchedGunshotTailFilter>();
                envelope = channelObject.AddComponent<PitchedGunshotEnvelopeFilter>();
                // Created dormant: a voice costs nothing until it is scheduled.
                channelObject.SetActive(false);
            }

            private static bool ScheduleChannel(
                AudioSource target,
                PitchedBandPassFilter band,
                PitchedGunshotTailFilter tail,
                PitchedGunshotEnvelopeFilter envelope,
                AudioSource donor,
                AudioClip overrideClip,
                bool nativePitch,
                bool cachedAutomaticCopy,
                float pitchRatio,
                float highpassHz,
                float lowpassHz,
                float excitationSeconds,
                float tailSeconds,
                float durationSeconds,
                float fadePercent,
                float sourceGain,
                float userGain,
                double scheduledStart,
                double scheduledEnd,
                bool measureLevels,
                float headphoneTailDbPerSecond,
                LowEndNormalizationResult calibration)
            {
                AudioClip playbackClip = cachedAutomaticCopy ? overrideClip : donor?.clip;
                if (donor == null || playbackClip == null || donor.volume <= 0.0001f)
                {
                    envelope.Bypass();
                    return false;
                }

                // Wake the channel only once there is something to play on it.
                if (!target.gameObject.activeSelf) target.gameObject.SetActive(true);
                target.clip = playbackClip;
                target.outputAudioMixerGroup = donor.outputAudioMixerGroup;
                target.priority = donor.priority;
                target.mute = donor.mute;
                target.panStereo = donor.panStereo;
                target.ignoreListenerPause = donor.ignoreListenerPause;
                target.pitch = Mathf.Clamp(
                    cachedAutomaticCopy && !nativePitch ? pitchRatio : donor.pitch * pitchRatio,
                    0.1f,
                    3f);
                target.volume = Mathf.Clamp01(donor.volume * sourceGain);
                target.loop = false;
                target.timeSamples = 0;

                // Calibration is wide-band. Keep the authored band-pass response
                // without the former crossover's additional phase rotation.
                band.Configure(AudioRuntimeState.OutputSampleRate, highpassHz, lowpassHz);
                if (cachedAutomaticCopy)
                {
                    tail.Configure(
                        excitationSeconds,
                        tailSeconds,
                        AudioRuntimeState.OutputSampleRate);
                }
                else
                {
                    tail.Bypass();
                }
                envelope.Configure(
                    durationSeconds,
                    fadePercent,
                    userGain,
                    startImmediately: cachedAutomaticCopy,
                    sampleRate: AudioRuntimeState.OutputSampleRate,
                    measureLevels: measureLevels,
                    headphoneTailDbPerSecond: headphoneTailDbPerSecond,
                    bodyCalibrationGain: calibration.BodyGain,
                    decayCalibrationGain: calibration.DecayGain,
                    decayStartSeconds: calibration.DecayStartSeconds);
                target.PlayScheduled(scheduledStart);
                target.SetScheduledEndTime(scheduledEnd);
                return true;
            }

            private static void StopChannel(
                AudioSource source,
                PitchedBandPassFilter band,
                PitchedGunshotTailFilter tail,
                PitchedGunshotEnvelopeFilter envelope)
            {
                source.Stop();
                source.loop = false;
                source.clip = null;
                band.Bypass();
                tail.Bypass();
                envelope.Bypass();
                // Unity keeps calling the filters of an enabled source object and
                // keeps it in the voice budget even when it plays nothing. A pool
                // that has issued sixty-four copies would otherwise leave one
                // hundred and twenty-eight live sources in the graph for the rest
                // of the raid, competing with the game's own sounds.
                source.gameObject.SetActive(false);
            }
        }
    }
}
