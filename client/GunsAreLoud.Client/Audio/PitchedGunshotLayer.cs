using System;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal readonly struct PitchedLayerTelemetry
    {
        internal readonly bool Scheduled;
        internal readonly bool Playing;
        internal readonly bool CachedAutomaticBeat;
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
            bool cachedAutomaticBeat,
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
            CachedAutomaticBeat = cachedAutomaticBeat;
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

        private readonly Voice[] _voices = new Voice[MaximumVoices];
        private int _nextVoice;
        private bool _lastScheduled;
        private bool _lastCachedAutomaticBeat;
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
                tuning.LowEndMode != GunshotLowEndMode.PitchedCopy ||
                tuning.DirectBodyGain <= 0.001f)
            {
                return false;
            }

            PitchedGunshotLayer layer = source.GetComponent<PitchedGunshotLayer>();
            if (layer == null)
            {
                layer = source.gameObject.AddComponent<PitchedGunshotLayer>();
            }

            return layer.PlayInternal(
                source,
                tuning,
                scheduledStart,
                originalDurationSeconds,
                default,
                default,
                false);
        }

        internal static bool PlayCachedAutomaticBeat(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            double scheduledStart,
            CachedAutomaticBeat beatA,
            CachedAutomaticBeat beatB)
        {
            if (source == null ||
                tuning.LowEndMode != GunshotLowEndMode.PitchedCopy ||
                tuning.DirectBodyGain <= 0.001f ||
                (beatA.Clip == null && beatB.Clip == null))
            {
                return false;
            }

            bool fullReport = beatA.HasAuthoredTail || beatB.HasAuthoredTail;
            PitchedGunshotLayer layer = fullReport ? ReportPool : source.GetComponent<PitchedGunshotLayer>();
            if (layer == null)
            {
                // EFT recycles its body source at release, before a lowered tail
                // has finished. Full report voices must outlive that source lease.
                if (fullReport)
                {
                    if (Plugin.Runtime == null) return false;
                    var owner = new GameObject("GunsAreLoud.CompleteReportPool");
                    owner.transform.SetParent(Plugin.Runtime.transform, false);
                    ReportPool = layer = owner.AddComponent<PitchedGunshotLayer>();
                }
                else layer = source.gameObject.AddComponent<PitchedGunshotLayer>();
            }

            float sourceSpanSeconds = Mathf.Max(
                CalculateCachedSourceDuration(beatA.SourceSpanSeconds, beatA.NativePitch, source.source1?.pitch ?? 1f),
                CalculateCachedSourceDuration(beatB.SourceSpanSeconds, beatB.NativePitch, source.source2?.pitch ?? 1f));
            return layer.PlayInternal(
                source,
                tuning,
                scheduledStart,
                sourceSpanSeconds,
                beatA,
                beatB,
                true);
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
                return Mathf.Clamp((float)(sampleEnd - sampleStart), 0.01f, 10f);
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
                _lastCachedAutomaticBeat,
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
            _lastCachedAutomaticBeat = false;
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

        private void OnDisable()
        {
            StopAll();
        }

        private bool PlayInternal(
            SuperSource source,
            LocalGunshotAudioTuning tuning,
            double scheduledStart,
            float originalDurationSeconds,
            CachedAutomaticBeat beatA,
            CachedAutomaticBeat beatB,
            bool cachedAutomaticBeat)
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
            float tailSeconds = cachedAutomaticBeat && !beatA.HasAuthoredTail && !beatB.HasAuthoredTail
                ? Mathf.Clamp(tuning.AutomaticPitchedTailSeconds, 0f, 0.6f)
                : 0f;
            float durationSeconds = cachedAutomaticBeat
                ? CalculateCachedOutputDuration(
                    sourceSpanSeconds,
                    pitchRatio,
                    tailSeconds)
                : excitationSeconds;
            if (totalGain <= 0.001f)
            {
                return false;
            }

            AudioClip normalizationClipA = cachedAutomaticBeat ? beatA.OriginalClip : source.source1?.clip;
            AudioClip normalizationClipB = cachedAutomaticBeat ? beatB.OriginalClip : source.source2?.clip;
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

            Voice voice = GetNextVoice();
            if (!voice.Schedule(
                source.source1,
                source.source2,
                beatA.Clip,
                beatB.Clip,
                beatA.NativePitch,
                beatB.NativePitch,
                cachedAutomaticBeat,
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
                levelB))
            {
                return false;
            }

            if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                Plugin.Log.LogInfo($"low-end normalization voice={voice.DiagnosticId} clip={normalizationClipA?.name ?? normalizationClipB?.name} " +
                    $"amount={tuning.LowEndNormalizationPercent:0}% contrast={tuning.CaliberContrastPercent:0}% " +
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
            _lastCachedAutomaticBeat = cachedAutomaticBeat;
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
            return true;
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

            internal bool Schedule(
                AudioSource donorA,
                AudioSource donorB,
                AudioClip overrideClipA,
                AudioClip overrideClipB,
                bool nativePitchA,
                bool nativePitchB,
                bool cachedAutomaticBeat,
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
                LowEndNormalizationResult calibrationB)
            {
                Stop();
                _measureLevels = Plugin.ModConfig?.DiagnosticShotLog.Value == true;
                DiagnosticId = ++_nextDiagnosticId;
                double intendedEnd = scheduledStart + durationSeconds;
                double start = Math.Max(scheduledStart, AudioSettings.dspTime + 0.002);
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
                    cachedAutomaticBeat,
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
                    cachedAutomaticBeat,
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
                StopChannel(_sourceA, _tailA, _envelopeA);
                StopChannel(_sourceB, _tailB, _envelopeB);
                _scheduledA = false;
                _scheduledB = false;
                _scheduledEnd = 0.0;
            }

            private void LogLevel(string channel, AudioSource source, PitchedGunshotEnvelopeFilter envelope)
            {
                PitchedEnvelopeTelemetry t = envelope.GetTelemetry();
                Plugin.Log.LogInfo($"low-end voice voice={DiagnosticId} channel={channel} clip={source.clip?.name} " +
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
            }

            private static bool ScheduleChannel(
                AudioSource target,
                PitchedBandPassFilter band,
                PitchedGunshotTailFilter tail,
                PitchedGunshotEnvelopeFilter envelope,
                AudioSource donor,
                AudioClip overrideClip,
                bool nativePitch,
                bool cachedAutomaticBeat,
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
                AudioClip playbackClip = cachedAutomaticBeat ? overrideClip : donor?.clip;
                if (donor == null || playbackClip == null || donor.volume <= 0.0001f)
                {
                    envelope.Bypass();
                    return false;
                }

                target.clip = playbackClip;
                target.outputAudioMixerGroup = donor.outputAudioMixerGroup;
                target.priority = donor.priority;
                target.mute = donor.mute;
                target.panStereo = donor.panStereo;
                target.ignoreListenerPause = donor.ignoreListenerPause;
                target.pitch = Mathf.Clamp(
                    cachedAutomaticBeat && !nativePitch ? pitchRatio : donor.pitch * pitchRatio,
                    0.1f,
                    3f);
                target.volume = Mathf.Clamp01(donor.volume * sourceGain);
                target.loop = false;
                target.timeSamples = 0;

                // Calibration is wide-band. Keep the authored band-pass response
                // without the former crossover's additional phase rotation.
                band.Configure(AudioSettings.outputSampleRate, highpassHz, lowpassHz);
                if (cachedAutomaticBeat)
                {
                    tail.Configure(
                        excitationSeconds,
                        tailSeconds,
                        AudioSettings.outputSampleRate);
                }
                else
                {
                    tail.Bypass();
                }
                envelope.Configure(
                    durationSeconds,
                    fadePercent,
                    userGain,
                    startImmediately: cachedAutomaticBeat,
                    sampleRate: AudioSettings.outputSampleRate,
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
                PitchedGunshotTailFilter tail,
                PitchedGunshotEnvelopeFilter envelope)
            {
                source.Stop();
                source.loop = false;
                source.clip = null;
                tail.Bypass();
                envelope.Bypass();
            }
        }
    }
}
