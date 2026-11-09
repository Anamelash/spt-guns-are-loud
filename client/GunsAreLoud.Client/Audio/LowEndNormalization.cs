using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        internal const float MaximumMeasuredBodySeconds = 0.30f;

        /// <summary>
        /// The attack of a whole-shot recording, in native seconds from its
        /// onset: 10 ms RMS envelope, its peak, and the first block after it
        /// that is 20 dB down. Bounded to [half the body window, 0.30 s]; the
        /// lower bound is today's window at pitch 0.5, so a short attack keeps
        /// the old switch point. 0 when there is nothing to measure.
        /// </summary>
        internal static float EstimateBodySeconds(float[] stereo, int rate)
        {
            if (stereo == null || stereo.Length < 4 || rate < 8000) return 0f;
            int frames = stereo.Length / 2;
            float peak = 0f;
            for (int i = 0; i < stereo.Length; i++) peak = Math.Max(peak, Math.Abs(stereo[i]));
            if (peak < 0.00005f) return 0f;
            float threshold = Math.Max(0.00005f, peak * 0.01f);
            int onset = 0;
            while (onset < frames && Math.Max(Math.Abs(stereo[onset * 2]),
                Math.Abs(stereo[onset * 2 + 1])) < threshold) onset++;
            int block = Math.Max(1, rate / 100);
            int blocks = (frames - onset) / block;
            if (blocks < 2) return 0f;
            var envelope = new double[blocks];
            int peakBlock = 0;
            for (int b = 0; b < blocks; b++)
            {
                double energy = 0;
                int first = (onset + b * block) * 2;
                for (int i = 0; i < block * 2; i++) energy += stereo[first + i] * (double)stereo[first + i];
                envelope[b] = Math.Sqrt(energy / (block * 2));
                if (envelope[b] > envelope[peakBlock]) peakBlock = b;
            }
            double limit = envelope[peakBlock] * 0.1;
            float seconds = MaximumMeasuredBodySeconds;
            for (int b = peakBlock + 1; b < blocks; b++)
                if (envelope[b] < limit) { seconds = b * block / (float)rate; break; }
            return Math.Min(MaximumMeasuredBodySeconds, Math.Max(WindowSeconds * 0.5f, seconds));
        }

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
            internal int Id;
            internal float[] Pcm;
            internal int Rate, Group;
            internal float Volume;
            internal float Level, BassLevel;
            internal float DecayLevel;
            internal bool DecayReady;
            internal float NativeBodySeconds;
            internal string BodySource = "default";
            internal int Revision = -1;
            internal bool Queued;
            // A sibling estimate only: measured siblings it was built from and
            // the measurement count it was built at.
            internal int Siblings;
            internal int Stamp = -1;
            // The band the levels above were measured in, and the persistent
            // cache entry they are written to (null: not cached).
            internal bool HasBand;
            internal float BandPitch, BandHigh, BandLow;
            internal string CacheKey;
            // The copy and its aligned sum with the recording, as the sum meter
            // measured them in this band (raw units: no volume, no gain).
            internal ShotSumMeasure Sum;
        }

        internal const string SiblingBodySource = "sibling";

        // Analysis PCM is only needed to re-measure after an F12 band change.
        // A long raid with many banks would otherwise keep every prefix alive:
        // the oldest measured ones are released and simply stop re-measuring.
        internal const long MaximumRetainedSamples = 6L * 1024L * 1024L;

        private static readonly Dictionary<int, Source> Sources = new Dictionary<int, Source>();
        private static readonly Dictionary<int, int> Aliases = new Dictionary<int, int>();
        private static readonly Queue<Source> Pending = new Queue<Source>();
        private static readonly List<Source> Retained = new List<Source>();
        // The variants of one bank in one environment, by clip id. A variant
        // played before its own warm-up finished borrows its measured
        // siblings' level instead of playing uncorrected.
        private static readonly Dictionary<int, int[]> SiblingSets = new Dictionary<int, int[]>();
        private static readonly Dictionary<int, Source> SiblingEstimates = new Dictionary<int, Source>();
        private static long _retainedSamples;
        private static float _pitch, _high, _low;
        private static int _revision;
        internal static int SourceCount => Sources.Count;
        /// <summary>Sources queued, being measured, or measured and not yet published.</summary>
        internal static int PendingCount => Pending.Count + Volatile.Read(ref _inFlight) + FinishedCount;
        internal static long RetainedSamples => _retainedSamples;
        internal static int MeasurementCount { get; private set; }

        internal static bool Contains(AudioClip clip) => clip != null && Sources.ContainsKey(clip.GetInstanceID());

        /// <summary>The clip has levels measured in the current analysis band.</summary>
        internal static bool IsCurrent(AudioClip clip) => clip != null && IsCurrent(clip.GetInstanceID());

        internal static bool IsCurrent(int id) =>
            Sources.TryGetValue(id, out Source source) && source.Revision == _revision;

        /// <summary>
        /// Declares the clips as variants of one bank in one environment: until
        /// one of them is measured it is corrected by the geometric mean of the
        /// measured others. Re-declaring the same set is free.
        /// </summary>
        internal static void Siblings(AudioClip[] clips)
        {
            if (clips == null) return;
            int count = 0;
            int[] existing = null;
            bool known = true;
            foreach (AudioClip clip in clips)
            {
                if (clip == null) continue;
                count++;
                if (!SiblingSets.TryGetValue(clip.GetInstanceID(), out int[] set)) known = false;
                else if (existing == null) existing = set;
                else if (!ReferenceEquals(existing, set)) known = false;
            }
            if (count < 2 || known && existing != null && existing.Length == count) return;
            var ids = new int[count];
            int index = 0;
            foreach (AudioClip clip in clips)
                if (clip != null) ids[index++] = clip.GetInstanceID();
            DeclareSiblings(ids);
        }

        internal static void Siblings(params int[] ids)
        {
            if (ids != null) DeclareSiblings((int[])ids.Clone());
        }

        private static void DeclareSiblings(int[] ids)
        {
            if (ids.Length < 2) return;
            foreach (int id in ids)
            {
                SiblingSets[id] = ids;
                SiblingEstimates.Remove(id);
            }
        }

        /// <summary>
        /// The level a variant that is not measured yet borrows from its
        /// measured siblings (geometric means), or null when none is measured.
        /// </summary>
        private static Source SiblingEstimate(int id)
        {
            if (!SiblingSets.TryGetValue(id, out int[] set)) return null;
            if (SiblingEstimates.TryGetValue(id, out Source cached) &&
                cached.Revision == _revision && cached.Stamp == MeasurementCount)
                return cached.Siblings > 0 ? cached : null;
            int measured = 0, decayMeasured = 0, summed = 0, lossSummed = 0;
            double level = 0, bass = 0, decay = 0, bodySeconds = 0, copyPeak = 0, copyAttack = 0;
            var sums = new double[ShotSumRatios.Values.Length];
            var lossSums = new double[ShotSumRatios.LossLength];
            Source first = null;
            foreach (int other in set)
            {
                if (other == id || !Sources.TryGetValue(other, out Source sibling) ||
                    sibling.Revision != _revision) continue;
                if (first == null) first = sibling;
                measured++;
                level += Math.Log(Math.Max(sibling.Level, 1e-9f));
                bass += Math.Log(Math.Max(sibling.BassLevel, 1e-9f));
                bodySeconds += sibling.NativeBodySeconds;
                if (sibling.Sum.Valid)
                {
                    summed++;
                    copyPeak += sibling.Sum.CopyPeak;
                    copyAttack += sibling.Sum.CopyAttackDb;
                    for (int point = 0; point < sums.Length; point++) sums[point] += sibling.Sum.SumPeaks[point];
                    if (sibling.Sum.HasLoss)
                    {
                        lossSummed++;
                        for (int cell = 0; cell < lossSums.Length; cell++) lossSums[cell] += sibling.Sum.LimitLoss[cell];
                    }
                }
                if (!sibling.DecayReady) continue;
                decayMeasured++;
                decay += Math.Log(Math.Max(sibling.DecayLevel, 1e-9f));
            }
            var estimate = new Source { Id = id, Revision = _revision, Stamp = MeasurementCount,
                Siblings = measured, BodySource = SiblingBodySource };
            if (first != null)
            {
                estimate.Rate = first.Rate;
                estimate.Group = first.Group;
                estimate.Volume = first.Volume;
                estimate.Level = (float)Math.Exp(level / measured);
                estimate.BassLevel = (float)Math.Exp(bass / measured);
                estimate.NativeBodySeconds = (float)(bodySeconds / measured);
                estimate.DecayReady = decayMeasured > 0;
                estimate.DecayLevel = decayMeasured > 0 ? (float)Math.Exp(decay / decayMeasured) : 0f;
                if (summed > 0)
                {
                    var meanSums = new float[sums.Length];
                    for (int point = 0; point < sums.Length; point++) meanSums[point] = (float)(sums[point] / summed);
                    float[] meanLoss = null;
                    if (lossSummed > 0)
                    {
                        meanLoss = new float[lossSums.Length];
                        for (int cell = 0; cell < lossSums.Length; cell++) meanLoss[cell] = (float)(lossSums[cell] / lossSummed);
                    }
                    estimate.Sum = new ShotSumMeasure((float)(copyPeak / summed), (float)(copyAttack / summed), meanSums, meanLoss);
                }
            }
            SiblingEstimates[id] = estimate;
            return measured > 0 ? estimate : null;
        }

        internal static void Register(AudioClip clip, int rate, float[] pcm, float volume, int group,
            float nativeBodySeconds = 0f, string bodySource = null, string cacheKey = null, bool keepMeasured = true)
        {
            if (clip != null)
                Register(clip.GetInstanceID(), rate, pcm, volume, group, nativeBodySeconds, bodySource, cacheKey, keepMeasured);
        }

        /// <summary>Where the clip's body length came from: loopBeat, measured or default.</summary>
        internal static string BodySource(AudioClip clip)
        {
            if (clip == null) return "none";
            int id = clip.GetInstanceID();
            if (Aliases.TryGetValue(id, out int body)) id = body;
            if (Sources.TryGetValue(id, out Source source) && source.Revision == _revision) return source.BodySource;
            if (SiblingEstimate(id) != null) return SiblingBodySource;
            return source != null ? source.BodySource : "none";
        }

        internal static void Register(int id, int rate, float[] pcm, float volume, int group,
            float nativeBodySeconds = 0f, string bodySource = null, string cacheKey = null, bool keepMeasured = true)
        {
            if (pcm == null || pcm.Length < 4 || rate < 8000) return;
            // Keep only the analysis prefix, not multi-second tails. It is raw,
            // native-pitch, stereo PCM; no user gain or occlusion is baked in.
            // Enough for the latest bounded body boundary plus a pitched decay window.
            int samples = Math.Min(pcm.Length & ~1, (int)(rate * 1.25f) * 2);
            var prefix = new float[samples];
            Array.Copy(pcm, prefix, samples);
            var source = new Source { Id = id, Pcm = prefix, Rate = rate, Volume = volume, Group = group,
                NativeBodySeconds = Math.Max(0f, nativeBodySeconds),
                BodySource = bodySource ?? (nativeBodySeconds > 0f ? "loopBeat" : "default"), CacheKey = cacheKey };
            if (Sources.TryGetValue(id, out Source replaced))
            {
                ReleasePcm(replaced);
                // The same event already measured in this band (from the
                // persistent cache, and confirmed by the caller): keep its levels, so the copy stays ready,
                // and hold the samples only for a later band change.
                if (keepMeasured && cacheKey != null && replaced.CacheKey == cacheKey && replaced.Revision == _revision &&
                    replaced.HasBand && replaced.BandPitch == _pitch && replaced.BandHigh == _high &&
                    replaced.BandLow == _low)
                {
                    source.Level = replaced.Level;
                    source.BassLevel = replaced.BassLevel;
                    source.DecayLevel = replaced.DecayLevel;
                    source.DecayReady = replaced.DecayReady;
                    source.HasBand = true;
                    source.BandPitch = replaced.BandPitch;
                    source.BandHigh = replaced.BandHigh;
                    source.BandLow = replaced.BandLow;
                    source.Sum = replaced.Sum;
                    source.Revision = _revision;
                }
            }
            Sources[id] = source;
            Retained.Add(source);
            _retainedSamples += prefix.Length;
            TrimRetention();
            Queue(source);
        }

        private static void ReleasePcm(Source source)
        {
            if (source?.Pcm == null) return;
            _retainedSamples -= source.Pcm.Length;
            source.Pcm = null;
        }

        private static void TrimRetention()
        {
            while (Retained.Count > 1 && _retainedSamples > MaximumRetainedSamples)
            {
                Source oldest = Retained[0];
                // Never release a prefix that has not produced its measurement yet.
                if (oldest.Pcm != null && oldest.Revision != _revision) break;
                Retained.RemoveAt(0);
                ReleasePcm(oldest);
            }
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
            Publish();
            // A measurement is arithmetic over the retained prefix and nothing
            // else, so it runs on the measurement thread; a frame pays only for
            // handing the queue over and for publishing what has come back. A
            // weapon's bank is thirty-odd recordings, and one of them costs
            // several milliseconds with the limiter loss table.
            while (Pending.Count > 0)
            {
                Source source = Pending.Dequeue();
                if (!Sources.TryGetValue(source.Id, out Source current) ||
                    !ReferenceEquals(current, source) ||
                    source.Pcm == null ||
                    source.Revision == _revision)
                {
                    source.Queued = false;
                    continue;
                }
                // Queued stays set until the result is published, so a band
                // change meanwhile does not queue the source a second time.
                Submit(new MeasurementJob
                {
                    Source = source, Pcm = source.Pcm, Rate = source.Rate, Volume = source.Volume,
                    NativeBodySeconds = source.NativeBodySeconds, Pitch = _pitch, High = _high, Low = _low
                });
            }
        }

        /// <summary>
        /// Measures everything queued before returning: hands the queue over,
        /// waits for the measurement thread and publishes. For tests and tools;
        /// the game never waits.
        /// </summary>
        internal static void CompletePending(TuningSnapshot tuning, int timeoutMilliseconds = 30000)
        {
            long start = Stopwatch.GetTimestamp();
            for (;;)
            {
                Refresh(tuning);
                if (PendingCount == 0) return;
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency > timeoutMilliseconds)
                    throw new TimeoutException("level measurements did not complete in time");
                Thread.Sleep(1);
            }
        }

        // ----- measurement thread -----

        private sealed class MeasurementJob
        {
            internal Source Source;
            internal float[] Pcm;
            internal int Rate;
            internal float Volume, NativeBodySeconds;
            internal float Pitch, High, Low;
            internal float Level, BassLevel, DecayLevel;
            internal bool DecayReady;
            internal ShotSumMeasure Sum;
            internal string Failure;
        }

        private static readonly Queue<MeasurementJob> Jobs = new Queue<MeasurementJob>();
        private static readonly Queue<MeasurementJob> Finished = new Queue<MeasurementJob>();
        private static Thread _worker;
        private static int _inFlight;
        // Measures on the calling thread when no thread can be started.
        private static bool _inline;

        private static int FinishedCount { get { lock (Finished) return Finished.Count; } }

        private static void Submit(MeasurementJob job)
        {
            if (_inline)
            {
                Run(job);
                Publish(job);
                return;
            }
            Interlocked.Increment(ref _inFlight);
            lock (Jobs)
            {
                Jobs.Enqueue(job);
                if (_worker == null) StartWorker();
                Monitor.Pulse(Jobs);
            }
            if (_worker == null) RunInline();
        }

        private static void StartWorker()
        {
            try
            {
                var worker = new Thread(WorkerLoop)
                {
                    IsBackground = true, Name = "GAL level measurement", Priority = System.Threading.ThreadPriority.BelowNormal
                };
                worker.Start();
                _worker = worker;
            }
            catch (Exception exception)
            {
                _inline = true;
                Plugin.Log?.LogWarning($"level measurement thread unavailable, measuring inline: {exception.Message}");
            }
        }

        private static void RunInline()
        {
            for (;;)
            {
                MeasurementJob job;
                lock (Jobs)
                {
                    if (Jobs.Count == 0) return;
                    job = Jobs.Dequeue();
                }
                Run(job);
                Interlocked.Decrement(ref _inFlight);
                Publish(job);
            }
        }

        private static void WorkerLoop()
        {
            for (;;)
            {
                MeasurementJob job;
                lock (Jobs)
                {
                    while (Jobs.Count == 0) Monitor.Wait(Jobs);
                    job = Jobs.Dequeue();
                }
                Run(job);
                lock (Finished) Finished.Enqueue(job);
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void Run(MeasurementJob job)
        {
            try
            {
                float playbackDecayStart = job.NativeBodySeconds > 0f
                    ? Math.Max(LowEndLevelModel.WindowSeconds, job.NativeBodySeconds / Math.Max(0.1f, job.Pitch))
                    : LowEndLevelModel.WindowSeconds;
                float measurementDecayStart = Math.Max(
                    LowEndLevelModel.RepresentativeDecayStartSeconds, playbackDecayStart);
                LowEndDynamicsLevels dynamics = LowEndLevelModel.MeasureDynamics(
                    job.Pcm, job.Rate, job.Pitch, job.High, job.Low, measurementDecayStart);
                job.Level = dynamics.Body * job.Volume;
                job.BassLevel = LowEndLevelModel.MeasureBands(job.Pcm, job.Rate, job.Pitch, job.High, job.Low).Bass * job.Volume;
                job.DecayLevel = dynamics.Decay * job.Volume;
                job.DecayReady = dynamics.DecayReady;
                job.Sum = ShotSumMeter.Measure(job.Pcm, job.Rate, job.Pitch, job.High, job.Low);
            }
            catch (Exception exception)
            {
                job.Failure = exception.Message;
            }
        }

        private static void Publish()
        {
            for (;;)
            {
                MeasurementJob job;
                lock (Finished)
                {
                    if (Finished.Count == 0) return;
                    job = Finished.Dequeue();
                }
                Publish(job);
            }
        }

        private static void Publish(MeasurementJob job)
        {
            Source source = job.Source;
            source.Queued = false;
            if (!Sources.TryGetValue(source.Id, out Source current) ||
                !ReferenceEquals(current, source) ||
                source.Revision == _revision)
                return;
            if (job.Failure != null)
            {
                Plugin.Log?.LogWarning($"level measurement skipped: {job.Failure}");
                return;
            }
            // The band changed while this ran: these levels are for the old one.
            if (job.Pitch != _pitch || job.High != _high || job.Low != _low)
            {
                Queue(source);
                return;
            }
            source.Level = job.Level;
            source.BassLevel = job.BassLevel;
            source.DecayLevel = job.DecayLevel;
            source.DecayReady = job.DecayReady;
            source.Sum = job.Sum;
            source.Revision = _revision;
            source.HasBand = true;
            source.BandPitch = job.Pitch;
            source.BandHigh = job.High;
            source.BandLow = job.Low;
            MeasurementCount++;
            if (source.CacheKey != null) LevelCache.PutLayer(source.CacheKey, CacheEntry(source));
        }

        private static void SetBand(float semitones, float high, float low)
        {
            float pitch = PitchedGunshotLayer.CalculatePitchRatio(semitones);
            high = Math.Max(10f, Math.Min(low - 10f, high));
            if (_pitch == pitch && _high == high && _low == low) return;
            _pitch = pitch; _high = high; _low = low;
            _revision++;
            foreach (Source source in Sources.Values)
            {
                // Levels taken from the persistent cache have no samples to
                // re-measure; they stay valid exactly in the band they were
                // measured in.
                if (source.Pcm == null && source.HasBand && source.BandPitch == pitch &&
                    source.BandHigh == high && source.BandLow == low)
                    source.Revision = _revision;
                else Queue(source);
            }
        }

        private static void Queue(Source source)
        {
            // A released prefix keeps the measurement it already published; it
            // simply cannot be re-measured for a new band and falls back to
            // neutral correction, exactly like a clip that was never warmed.
            if (source == null || source.Queued || source.Pcm == null ||
                source.Revision == _revision) return;
            source.Queued = true;
            Pending.Enqueue(source);
        }

        private static LevelCacheEntry CacheEntry(Source source)
        {
            float volume = source.Volume > 0f ? source.Volume : 1f;
            return new LevelCacheEntry
            {
                HasLayer = true,
                BaseVolume = source.Volume,
                Level = source.Level / volume,
                BassLevel = source.BassLevel / volume,
                DecayLevel = source.DecayLevel / volume,
                DecayReady = source.DecayReady,
                NativeBodySeconds = source.NativeBodySeconds,
                Group = source.Group,
                BodySource = source.BodySource,
                Rate = source.Rate,
                BandPitch = source.BandPitch,
                BandHigh = source.BandHigh,
                BandLow = source.BandLow,
                CopyPeak = source.Sum.CopyPeak,
                CopyAttackDb = source.Sum.Valid ? source.Sum.CopyAttackDb : float.NaN,
                SumPeaks = source.Sum.Valid ? (float[])source.Sum.SumPeaks.Clone() : null,
                LimitLoss = source.Sum.Valid && source.Sum.HasLoss ? (float[])source.Sum.LimitLoss.Clone() : null
            };
        }

        /// <summary>
        /// The analysis band a tuning measures in, exactly as the cache keeps it.
        /// </summary>
        internal static void BandOf(float semitones, float highpassHz, float lowpassHz,
            out float pitch, out float high, out float low)
        {
            pitch = PitchedGunshotLayer.CalculatePitchRatio(semitones);
            low = lowpassHz;
            high = Math.Max(10f, Math.Min(low - 10f, highpassHz));
        }

        /// <summary>
        /// Registers levels measured in an earlier session, without samples.
        /// Ready at once in the band they were measured in; they cannot be
        /// re-measured for another band. <paramref name="volume"/> is the bank
        /// volume now, which the clip's own levels are scaled by.
        /// </summary>
        internal static void RegisterMeasured(int id, LevelCacheEntry entry, float volume, string cacheKey = null)
        {
            if (entry == null || !entry.HasLayer) return;
            var source = new Source
            {
                Id = id, Rate = entry.Rate, Volume = volume, Group = entry.Group,
                NativeBodySeconds = Math.Max(0f, entry.NativeBodySeconds),
                BodySource = entry.BodySource ?? "default",
                Level = entry.Level * volume,
                BassLevel = entry.BassLevel * volume,
                DecayLevel = entry.DecayLevel * volume,
                DecayReady = entry.DecayReady,
                HasBand = true,
                BandPitch = entry.BandPitch, BandHigh = entry.BandHigh, BandLow = entry.BandLow,
                CacheKey = cacheKey,
                Sum = entry.HasSum
                    ? new ShotSumMeasure(entry.CopyPeak, entry.CopyAttackDb, (float[])entry.SumPeaks.Clone(),
                        entry.HasLoss ? (float[])entry.LimitLoss.Clone() : null)
                    : default
            };
            source.Revision = source.BandPitch == _pitch && source.BandHigh == _high && source.BandLow == _low
                ? _revision : -1;
            if (Sources.TryGetValue(id, out Source replaced)) ReleasePcm(replaced);
            Sources[id] = source;
            if (source.Revision == _revision) MeasurementCount++;
        }

        internal static void RegisterMeasured(AudioClip clip, LevelCacheEntry entry, float volume, string cacheKey = null)
        {
            if (clip != null) RegisterMeasured(clip.GetInstanceID(), entry, volume, cacheKey);
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

        /// <summary>
        /// The copy's peak and attack level and the aligned sum's peaks for this
        /// recording, measured in the band of <paramref name="tuning"/>; a
        /// variant not measured yet takes the mean of its measured siblings.
        /// False when nothing is measured.
        /// </summary>
        internal static bool TryGetCopyBudget(int id, LocalGunshotAudioTuning tuning, out ShotSumMeasure sum)
        {
            sum = default;
            if (_pitch != PitchedGunshotLayer.CalculatePitchRatio(tuning.PitchedLayerSemitones) ||
                _high != Math.Max(10f, Math.Min(tuning.PitchedLayerLowpassHz - 10f, tuning.PitchedLayerHighpassHz)) ||
                _low != tuning.PitchedLayerLowpassHz) return false;
            if (Aliases.TryGetValue(id, out int body)) id = body;
            if (!Sources.TryGetValue(id, out Source source) || source.Revision != _revision)
            {
                source = SiblingEstimate(id);
                if (source == null) return false;
            }
            if (!source.Sum.Valid) return false;
            sum = source.Sum;
            return true;
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
            {
                source = SiblingEstimate(id);
                if (source == null) return new LowEndNormalizationResult(1f, false);
            }
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
            Sources.Clear(); Aliases.Clear(); Pending.Clear(); Retained.Clear();
            SiblingSets.Clear(); SiblingEstimates.Clear();
            _retainedSamples = 0;
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
