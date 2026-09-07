using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal static class LowEndLevelModel
    {
        internal const float WindowSeconds = 0.18f;
        internal const float RepresentativeDecayStartSeconds = 0.80f;
        internal const float DecayWindowSeconds = 0.22f;
        internal const float MinimumDecaySeconds = 0.08f;
        internal const float MinimumRms = 0.0005f;

        // Digital layer calibration, not physical SPL. Never derive this anchor
        // from a weapon recording, cache contents or the order of equipping guns.
        internal const float BaseTargetRms = 0.1f;

        internal static float TargetRms(int group)
        {
            // Warmup groups: environment * 2 + suppressed; EFT Outdoor=0, Indoor=1.
            // Keep deliberate environment/suppressor differences outside matching.
            float environment = group / 2 == 1 ? 1.4125375f : 1f; // indoor +3 dB
            float suppressor = (group & 1) != 0 ? 0.25118864f : 1f; // suppressed -12 dB
            return BaseTargetRms * environment * suppressor;
        }

        internal static float DecayTargetRms(int group) => TargetRms(group) * 0.2f;

        internal static LowEndDynamicsLevels MeasureDynamics(
            float[] stereo, int rate, float pitch, float highpass, float lowpass,
            float decayStartSeconds = WindowSeconds)
        {
            if (stereo == null || stereo.Length < 4 || rate < 8000) return default;
            pitch = Math.Max(0.1f, Math.Min(3f, pitch));
            int frames = stereo.Length / 2;
            float peak = 0f;
            for (int i = 0; i < stereo.Length; i++) peak = Math.Max(peak, Math.Abs(stereo[i]));
            if (peak < 0.00005f) return default;
            int onset = 0;
            float threshold = Math.Max(0.00005f, peak * 0.01f);
            while (onset < frames && Math.Max(Math.Abs(stereo[onset * 2]),
                Math.Abs(stereo[onset * 2 + 1])) < threshold) onset++;
            int start = Math.Max(0, onset - (int)(rate * 0.002f));
            int bodyFrames = (int)(rate * WindowSeconds);
            decayStartSeconds = Math.Max(WindowSeconds, decayStartSeconds);
            int decayStart = (int)(rate * decayStartSeconds);
            int decayEnd = (int)(rate * (decayStartSeconds + DecayWindowSeconds));
            int minimumDecay = (int)(rate * MinimumDecaySeconds);
            var bodyFilter = new PitchedBandPassState(rate, highpass, lowpass);
            var decayFilter = new PitchedBandPassState(rate, highpass, lowpass);
            double bodyEnergy = 0, decayEnergy = 0;
            int bodySamples = 0, decaySamples = 0;
            for (int output = 0; output < decayEnd; output++)
            {
                double position = start + output * (double)pitch;
                int frame = (int)position;
                if (frame + 1 >= frames) break; // Never turn unavailable PCM into measured silence.
                float fraction = (float)(position - frame);
                for (int channel = 0; channel < 2; channel++)
                {
                    float first = stereo[frame * 2 + channel];
                    float second = stereo[(frame + 1) * 2 + channel];
                    float input = first + (second - first) * fraction;
                    if (output < bodyFrames)
                    {
                        float value = bodyFilter.Process(input, channel);
                        bodyEnergy += value * (double)value; bodySamples++;
                    }
                    else if (output >= decayStart)
                    {
                        // Measure the decay itself, independently of filter ringing
                        // left by an unrelated attack level.
                        float value = decayFilter.Process(input, channel);
                        decayEnergy += value * (double)value; decaySamples++;
                    }
                }
            }
            float body = bodySamples == 0 ? 0f : (float)Math.Sqrt(bodyEnergy / bodySamples);
            bool decayReady = decaySamples >= minimumDecay * 2;
            float decay = !decayReady ? 0f : (float)Math.Sqrt(decayEnergy / decaySamples);
            decayReady = decayReady && decay >= MinimumRms && !float.IsNaN(decay) && !float.IsInfinity(decay);
            return new LowEndDynamicsLevels(body, decay, decayReady, bodySamples / 2, decaySamples / 2);
        }

        internal static float Measure(float[] stereo, int rate, float pitch, float highpass, float lowpass)
            => MeasureBands(stereo, rate, pitch, highpass, lowpass).Wide;

        internal static LowEndBandLevels MeasureBands(float[] stereo, int rate, float pitch, float highpass, float lowpass)
        {
            if (stereo == null || stereo.Length < 4 || rate < 8000) return default;
            pitch = Math.Max(0.1f, Math.Min(3f, pitch));
            var filter = new PitchedBandPassState(rate, highpass, lowpass);
            var split = new BassCrossoverState(rate);
            int frames = stereo.Length / 2;
            float peak = 0f;
            for (int i = 0; i < stereo.Length; i++) peak = Math.Max(peak, Math.Abs(stereo[i]));
            if (peak < 0.00005f) return default;
            int onset = 0;
            float threshold = Math.Max(0.00005f, peak * 0.01f);
            while (onset < frames && Math.Max(Math.Abs(stereo[onset * 2]),
                Math.Abs(stereo[onset * 2 + 1])) < threshold) onset++;
            int start = Math.Max(0, onset - (int)(rate * 0.002f));
            int count = (int)(rate * WindowSeconds);
            double energy = 0, bassEnergy = 0, textureEnergy = 0;
            for (int output = 0; output < count; output++)
            {
                double position = start + output * (double)pitch;
                int frame = (int)position;
                float fraction = (float)(position - frame);
                for (int channel = 0; channel < 2; channel++)
                {
                    float first = frame < frames ? stereo[frame * 2 + channel] : 0f;
                    float second = frame + 1 < frames ? stereo[(frame + 1) * 2 + channel] : 0f;
                    float value = filter.Process(first + (second - first) * fraction, channel);
                    energy += value * (double)value;
                    split.Process(value, channel, out float bass, out float texture);
                    bassEnergy += bass * (double)bass;
                    textureEnergy += texture * (double)texture;
                }
            }
            return new LowEndBandLevels((float)Math.Sqrt(energy / (count * 2.0)),
                (float)Math.Sqrt(bassEnergy / (count * 2.0)),
                (float)Math.Sqrt(textureEnergy / (count * 2.0)));
        }

        internal static float Correction(float measured, float target, float percent, float minimumDb = -12f)
        {
            if (float.IsNaN(measured) || float.IsNaN(target) || float.IsInfinity(measured) ||
                float.IsInfinity(target) || measured < MinimumRms || target < MinimumRms) return 1f;
            double db = Math.Max(minimumDb, Math.Min(12.0, 20.0 * Math.Log10(target / measured)));
            return (float)Math.Pow(10.0, db * Math.Max(0f, Math.Min(150f, percent)) / 2000.0);
        }

    }

    internal readonly struct LowEndDynamicsLevels
    {
        internal readonly float Body, Decay;
        internal readonly bool DecayReady;
        internal readonly int BodyFrames, DecayFrames;
        internal LowEndDynamicsLevels(float body, float decay, bool decayReady, int bodyFrames, int decayFrames)
        { Body = body; Decay = decay; DecayReady = decayReady; BodyFrames = bodyFrames; DecayFrames = decayFrames; }
    }

    internal readonly struct LowEndNormalizationResult
    {
        internal readonly float Gain, BodyGain, DecayGain, MeasuredRms, TargetRms, DecayRms, DecayTargetRms,
            DecayStartSeconds;
        internal readonly bool Ready, DecayReady, Limited, DecayLimited;

        internal LowEndNormalizationResult(float gain, bool ready, float measured = 0, float target = 0, float minimumBodyDb = -12f)
        {
            Gain = BodyGain = gain; DecayGain = gain; Ready = ready; DecayReady = false;
            MeasuredRms = measured; TargetRms = target; DecayRms = DecayTargetRms = 0f;
            DecayStartSeconds = LowEndLevelModel.WindowSeconds;
            Limited = ready && measured >= LowEndLevelModel.MinimumRms && target > 0 &&
                (20.0 * Math.Log10(target / measured) < minimumBodyDb || 20.0 * Math.Log10(target / measured) > 12.0);
            DecayLimited = false;
        }

        internal LowEndNormalizationResult(float bodyGain, float decayGain, bool ready, bool decayReady,
            float bodyRms, float bodyTarget, float decayRms, float decayTarget, float decayStartSeconds,
            bool decayLimited = false, float minimumBodyDb = -12f)
        {
            Gain = BodyGain = bodyGain; DecayGain = decayGain; Ready = ready; DecayReady = decayReady;
            MeasuredRms = bodyRms; TargetRms = bodyTarget; DecayRms = decayRms; DecayTargetRms = decayTarget;
            DecayStartSeconds = decayStartSeconds;
            Limited = ready && bodyRms >= LowEndLevelModel.MinimumRms && bodyTarget > 0 &&
                (20.0 * Math.Log10(bodyTarget / bodyRms) < minimumBodyDb || 20.0 * Math.Log10(bodyTarget / bodyRms) > 12.0);
            DecayLimited = decayLimited || (decayReady && decayRms >= LowEndLevelModel.MinimumRms &&
                decayTarget > 0 && Math.Abs(20.0 * Math.Log10(decayTarget / decayRms)) > 12.0);
        }
    }

    // Main-thread only. Raw analysis PCM survives F12 edits; only the derived levels
    // are invalidated. Every source has a fixed target independent of other sources.
    internal static class LowEndNormalizationCache
    {
        private sealed class Source
        {
            internal float[] Pcm;
            internal int Rate, Group;
            internal float Volume;
            internal float Level, BassLevel;
            internal float DecayLevel;
            internal bool DecayReady;
            internal float NativeBodySeconds;
            internal int Revision = -1;
        }

        private static readonly Dictionary<int, Source> Sources = new Dictionary<int, Source>();
        private static readonly Dictionary<int, int> Aliases = new Dictionary<int, int>();
        private static float _pitch, _high, _low;
        private static int _revision;
        internal static int SourceCount => Sources.Count;
        internal static int MeasurementCount { get; private set; }

        internal static bool Contains(AudioClip clip) => clip != null && Sources.ContainsKey(clip.GetInstanceID());

        internal static void Register(AudioClip clip, int rate, float[] pcm, float volume, int group,
            float nativeBodySeconds = 0f)
        {
            if (clip != null) Register(clip.GetInstanceID(), rate, pcm, volume, group, nativeBodySeconds);
        }

        internal static void Register(int id, int rate, float[] pcm, float volume, int group,
            float nativeBodySeconds = 0f)
        {
            if (pcm == null || pcm.Length < 4 || rate < 8000) return;
            // Keep only the analysis prefix, not multi-second tails. It is raw,
            // native-pitch, stereo PCM; no user gain or occlusion is baked in.
            // Enough for the latest bounded body boundary plus a pitched decay window.
            int samples = Math.Min(pcm.Length & ~1, (int)(rate * 1.25f) * 2);
            var prefix = new float[samples];
            Array.Copy(pcm, prefix, samples);
            Sources[id] = new Source { Pcm = prefix, Rate = rate, Volume = volume, Group = group,
                NativeBodySeconds = Math.Max(0f, nativeBodySeconds) };
        }

        internal static void Alias(AudioClip clip, AudioClip body)
        {
            if (clip != null && body != null && clip != body)
                Alias(clip.GetInstanceID(), body.GetInstanceID());
        }

        internal static void Alias(int clip, int body) { if (clip != body) Aliases[clip] = body; }

        internal static void Refresh(TuningSnapshot tuning)
        {
            if (tuning == null) return;
            SetBand(tuning.PitchedLayerSemitones, tuning.PitchedLayerHighpassHz, tuning.PitchedLayerLowpassHz);
            // Budget the background work. Never decode or scan all weapon banks
            // in FireBullet; missing measurements use neutral correction, not old F12.
            int budget = 4;
            foreach (Source source in Sources.Values)
            {
                if (source.Revision == _revision) continue;
                Measure(source);
                if (--budget == 0) break;
            }
        }

        private static void SetBand(float semitones, float high, float low)
        {
            float pitch = PitchedGunshotLayer.CalculatePitchRatio(semitones);
            high = Math.Max(10f, Math.Min(low - 10f, high));
            if (_pitch == pitch && _high == high && _low == low) return;
            _pitch = pitch; _high = high; _low = low;
            _revision++;
        }

        private static void Measure(Source source)
        {
            float playbackDecayStart = source.NativeBodySeconds > 0f
                ? Math.Max(LowEndLevelModel.WindowSeconds, source.NativeBodySeconds / Math.Max(0.1f, _pitch))
                : LowEndLevelModel.WindowSeconds;
            float measurementDecayStart = Math.Max(
                LowEndLevelModel.RepresentativeDecayStartSeconds, playbackDecayStart);
            LowEndDynamicsLevels dynamics = LowEndLevelModel.MeasureDynamics(
                source.Pcm, source.Rate, _pitch, _high, _low, measurementDecayStart);
            source.Level = dynamics.Body * source.Volume;
            source.BassLevel = LowEndLevelModel.MeasureBands(source.Pcm, source.Rate, _pitch, _high, _low).Bass * source.Volume;
            source.DecayLevel = dynamics.Decay * source.Volume;
            source.DecayReady = dynamics.DecayReady;
            source.Revision = _revision;
            MeasurementCount++;
        }

        internal static float Gain(AudioClip clip, LocalGunshotAudioTuning tuning, out bool ready)
        {
            LowEndNormalizationResult result = Evaluate(clip, tuning);
            ready = result.Ready;
            return result.Gain;
        }

        internal static float Gain(int id, LocalGunshotAudioTuning tuning, out bool ready)
        {
            LowEndNormalizationResult result = Evaluate(id, tuning);
            ready = result.Ready;
            return result.Gain;
        }

        internal static LowEndNormalizationResult Evaluate(AudioClip clip, LocalGunshotAudioTuning tuning) =>
            clip != null ? Evaluate(clip.GetInstanceID(), tuning) : new LowEndNormalizationResult(1f, false);

        internal static LowEndNormalizationResult Evaluate(int id, LocalGunshotAudioTuning tuning)
        {
            if (tuning.LowEndNormalizationPercent <= 0f) return new LowEndNormalizationResult(1f, true);
            // Queued samples may hold previous F12 snapshots. Never let them
            // roll back the shared analysis revision or use mismatched levels.
            if (_pitch != PitchedGunshotLayer.CalculatePitchRatio(tuning.PitchedLayerSemitones) ||
                _high != Math.Max(10f, Math.Min(tuning.PitchedLayerLowpassHz - 10f, tuning.PitchedLayerHighpassHz)) ||
                _low != tuning.PitchedLayerLowpassHz) return new LowEndNormalizationResult(1f, false);
            bool decayOnly = Aliases.TryGetValue(id, out int body);
            if (decayOnly) id = body;
            if (!Sources.TryGetValue(id, out Source source) || source.Revision != _revision)
                return new LowEndNormalizationResult(1f, false);
            float target = LowEndLevelModel.TargetRms(source.Group);
            // Single pistol variants can have similar full-band peaks but very different bass.
            // Match measured bass with a scalar only: no new phase-altering playback crossover.
            float bodyLevel = tuning.NormalizeBass ? source.BassLevel : source.Level;
            float bodyGain = LowEndLevelModel.Correction(bodyLevel, target, tuning.LowEndNormalizationPercent,
                tuning.NormalizeBass ? -24f : -12f);
            if (!source.DecayReady)
                return new LowEndNormalizationResult(bodyGain, true, bodyLevel, target, tuning.NormalizeBass ? -24f : -12f);
            float decayTarget = LowEndLevelModel.DecayTargetRms(source.Group);
            float requestedDecay = LowEndLevelModel.Correction(
                source.DecayLevel, decayTarget, tuning.LowEndNormalizationPercent);
            // Recovery only: a strong tail keeps the body correction. A quiet
            // tail may recover by at most 6 dB and never above native level.
            float decayGain = bodyGain > 1f
                ? bodyGain
                : Math.Max(bodyGain, Math.Min(Math.Min(1f, bodyGain * 1.9952623f), requestedDecay));
            float decayStart = source.NativeBodySeconds > 0f
                ? Math.Max(LowEndLevelModel.WindowSeconds, source.NativeBodySeconds / Math.Max(0.1f, _pitch))
                : LowEndLevelModel.WindowSeconds;
            // An aliased release clip starts at the authored tail itself. It must
            // not replay the composed report's body interval before using decay gain.
            float playbackBodyGain = decayOnly ? decayGain : bodyGain;
            return new LowEndNormalizationResult(playbackBodyGain, decayGain, true, true,
                bodyLevel, target, source.DecayLevel, decayTarget, decayStart,
                Math.Abs(decayGain - requestedDecay) > 0.00001f, tuning.NormalizeBass ? -24f : -12f);
        }

        internal static void Clear()
        {
            Sources.Clear(); Aliases.Clear();
            _revision++;
            MeasurementCount = 0;
        }
    }

    internal sealed class LowEndNormalizationMaintenance : MonoBehaviour
    {
        private ConfigFile _source;
        private TuningSnapshot _tuning;
        private int _dirty = 1;

        private void OnEnable()
        {
            _source = Plugin.ModConfig?.Source;
            if (_source != null) _source.SettingChanged += OnSettingChanged;
            Interlocked.Exchange(ref _dirty, 1);
        }

        private void OnDisable()
        {
            if (_source != null) _source.SettingChanged -= OnSettingChanged;
            _source = null;
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs args) =>
            Interlocked.Exchange(ref _dirty, 1);

        private void Update()
        {
            if (Plugin.ModConfig?.Enabled.Value != true) return;
            if (Interlocked.Exchange(ref _dirty, 0) != 0 || _tuning == null)
                _tuning = Plugin.ModConfig.GetTuning();
            using (PerformanceTrace.Measure(PerformanceArea.Normalization))
                LowEndNormalizationCache.Refresh(_tuning);
        }
    }
}
