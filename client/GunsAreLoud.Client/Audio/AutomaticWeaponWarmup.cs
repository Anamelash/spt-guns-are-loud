using System;
using System.Collections.Generic;
using System.Threading;
using EFT;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Engine;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    // Managed-only callback state: no Unity objects, logging, allocation or locks.
    internal sealed class SilentPcmBuffer
    {
        internal readonly float[] Samples;
        internal readonly int Frames;
        // The peak histogram runs on past the kept prefix, over the whole clip,
        // without keeping its samples.
        internal readonly int HistogramFrames;
        internal readonly PeakHistogram Histogram;
        private int _written;
        private int _measured;
        private int _complete;
        private int _histogramComplete;
        internal bool Complete => Volatile.Read(ref _complete) != 0;
        internal bool TracksHistogram => Histogram != null;
        internal bool HistogramComplete => Volatile.Read(ref _histogramComplete) != 0;
        internal int MeasuredFrames => Volatile.Read(ref _measured);

        internal SilentPcmBuffer(int frames, int histogramFrames = 0)
        {
            Frames = Math.Max(1, frames);
            Samples = new float[Frames * 2];
            if (histogramFrames > 0)
            {
                HistogramFrames = histogramFrames;
                Histogram = new PeakHistogram();
            }
        }

        internal void ProcessAndSilence(float[] data, int channels)
        {
            if (data == null) return;
            try
            {
                if (channels <= 0) return;
                int available = data.Length / channels;
                if (Histogram != null && !HistogramComplete)
                {
                    int measure = Math.Min(available, HistogramFrames - _measured);
                    for (int frame = 0; frame < measure; frame++)
                    {
                        float left = Math.Abs(data[frame * channels]);
                        float right = Math.Abs(data[frame * channels + (channels > 1 ? 1 : 0)]);
                        Histogram.Add(left > right ? left : right);
                    }
                    _measured += measure;
                    if (_measured >= HistogramFrames) Volatile.Write(ref _histogramComplete, 1);
                }
                if (Complete) return;
                int count = Math.Min(available, Frames - _written);
                for (int frame = 0; frame < count; frame++)
                {
                    Samples[(_written + frame) * 2] = data[frame * channels];
                    Samples[(_written + frame) * 2 + 1] = data[frame * channels + (channels > 1 ? 1 : 0)];
                }
                _written += count;
                if (_written == Frames) Volatile.Write(ref _complete, 1);
            }
            finally
            {
                // Mandatory even when idle, complete, malformed, or throwing.
                Array.Clear(data, 0, data.Length);
            }
        }
    }

    internal sealed class SilentWeaponDecoder : MonoBehaviour
    {
        private SilentPcmBuffer _buffer;
        internal void Arm(SilentPcmBuffer buffer) => Volatile.Write(ref _buffer, buffer);
        private void OnAudioFilterRead(float[] data, int channels)
        {
            SilentPcmBuffer buffer = Volatile.Read(ref _buffer);
            if (buffer != null) buffer.ProcessAndSilence(data, channels);
            else if (data != null) Array.Clear(data, 0, data.Length);
        }
    }

    internal static class AutomaticReportPcm
    {
        internal static float[] Compose(float[] body, float[] tail, float tailGain, int seamFrames)
        {
            if (body == null || tail == null || body.Length % 2 != 0 || tail.Length % 2 != 0)
                throw new ArgumentException("Stereo body and tail PCM are required.");
            int bodyFrames = body.Length / 2;
            int tailFrames = tail.Length / 2;
            int seam = Math.Max(0, Math.Min(seamFrames, Math.Min(bodyFrames, tailFrames)));
            float[] result = new float[body.Length + tail.Length];
            Array.Copy(body, result, body.Length);
            for (int frame = 0; frame < tailFrames; frame++)
            {
                float fadeIn = seam == 0 ? 1f : Math.Min(1f, frame / (float)seam);
                for (int channel = 0; channel < 2; channel++)
                    result[body.Length + frame * 2 + channel] =
                        tail[frame * 2 + channel] * tailGain * fadeIn;
            }
            for (int frame = 0; frame < seam; frame++)
            {
                float fadeOut = (seam - frame - 1f) / seam;
                for (int channel = 0; channel < 2; channel++)
                    result[(bodyFrames - seam + frame) * 2 + channel] *= fadeOut;
            }
            return result;
        }
    }

    /// <summary>
    /// Equip-time decode, independent of gameplay queues and SoundBank RNG.
    /// Two silent workers prepare current-environment close clips first, then
    /// the other environment and suppressor variant. All cache PCM is pitch=1.
    /// </summary>
    internal sealed class AutomaticWeaponWarmup : MonoBehaviour
    {
        private static int _instances;
        private readonly Dictionary<JobKey, Job> _jobs = new Dictionary<JobKey, Job>();
        private readonly List<Job> _pending = new List<Job>();
        private readonly List<Recipe> _recipes = new List<Recipe>();
        private readonly HashSet<int> _plannedBodies = new HashSet<int>();
        private readonly Worker[] _workers = new Worker[2];
        private WeaponSoundPlayer _weapon;
        private int _rate;
        private float _nextDiscovery;
        private bool _failed;
        private long _bankFingerprint;

        private static bool _releasePending;
        private static bool _remeasurePending;

        // Under a replacement sound engine, how each bank is played goes to the
        // main log once per session: it is the first thing to look at when
        // that engine changes, and the detailed log is rate-limited and off.
        private static readonly HashSet<string> ReportedBanks = new HashSet<string>();

        private void Awake() { _instances++; }

        private void OnDestroy()
        {
            // A weapon swap destroys the old component before or after the new
            // one is attached. Clearing here would wipe the cache for every swap
            // and force a full re-decode mid-raid, so defer the decision a frame.
            if (--_instances == 0) _releasePending = true;
        }

        /// <summary>
        /// Frees the decoded caches once no local weapon is warmed any more.
        /// Every pitched pool releases its voices first: the cached clips are
        /// destroyed here and a stolen voice must not be left playing one.
        /// </summary>
        internal static bool ReleaseCachesIfIdle()
        {
            if (!_releasePending) return false;
            _releasePending = false;
            if (_instances != 0) return false;
            PitchedGunshotLayer.StopAllLayers();
            AutomaticCopyCache.Clear();
            LowEndNormalizationCache.Clear();
            DirectLevelStore.Clear();
            return true;
        }

        /// <summary>
        /// Forgets every measurement of this session, so the held weapon's
        /// recordings are decoded and measured again from scratch. The pitched
        /// pools release their voices first: the cached clips are destroyed here.
        /// </summary>
        internal static void ForgetMeasurements()
        {
            PitchedGunshotLayer.StopAllLayers();
            AutomaticCopyCache.Clear();
            LowEndNormalizationCache.Clear();
            DirectLevelStore.Clear();
            ReportedPaths.Clear();
            _remeasurePending = true;
        }

        internal static bool Attach(WeaponSoundPlayer weapon)
        {
            if (weapon == null || !weapon.isActiveAndEnabled ||
                !ShotDescriptorFactory.TryGetLocalBridge(weapon, out _)) return false;
            AutomaticWeaponWarmup warmup = weapon.GetComponent<AutomaticWeaponWarmup>();
            if (warmup == null) warmup = weapon.gameObject.AddComponent<AutomaticWeaponWarmup>();
            warmup._weapon = weapon;
            return true;
        }

        private void Update()
        {
            if (_failed) return;
            try { using (PerformanceTrace.Measure(PerformanceArea.Warmup)) Tick(); }
            catch (Exception exception)
            {
                StopWorkers();
                _failed = true;
                Plugin.Log.LogWarning($"automatic prewarm stopped safely: {exception.Message}");
            }
        }

        private void Tick()
        {
            if (_weapon == null || !ShotDescriptorFactory.TryGetLocalBridge(_weapon, out var bridge) ||
                Plugin.ModConfig?.Enabled.Value != true)
            {
                StopWorkers();
                return;
            }
            int rate = AudioSettings.outputSampleRate;
            if (_rate != rate)
            {
                StopWorkers();
                _jobs.Clear();
                _pending.Clear();
                _directReads.Clear();
                _recipes.Clear();
                _plannedBodies.Clear();
                _rate = rate;
            }
            if (_remeasurePending)
            {
                _remeasurePending = false;
                DropAllWork();
            }
            if (Time.unscaledTime >= _nextDiscovery)
            {
                _nextDiscovery = Time.unscaledTime + 0.5f;
                // No listener is common during raid loading. Do not burn retries.
                if (AudioRuntimeLookup.Listener == null) return;
                using (PerformanceTrace.Measure(PerformanceArea.WarmupPlan))
                {
                    // The engine may still be putting its own banks on the weapon;
                    // work queued for the banks it replaced will never be heard.
                    SoundEngine.Shots.PrepareWeapon(_weapon);
                    long fingerprint = BankFingerprint(_weapon);
                    if (_bankFingerprint != 0 && fingerprint != _bankFingerprint) DropWorkForReplacedBanks();
                    _bankFingerprint = fingerprint;
                    SoundBank activeBody = _weapon.IsSilenced ? _weapon.BodySilenced : _weapon.Body;
                    SoundBank activeTail = _weapon.IsSilenced ? _weapon.TailSilenced : _weapon.Tail;
                    if (_weapon.IsAutoWeapon)
                    {
                        PlanBody(activeBody, activeTail, (int)bridge.Environment, _weapon.IsSilenced);
                        PlanBody(_weapon.Body, _weapon.Tail, (int)bridge.Environment, false);
                        PlanBody(_weapon.BodySilenced, _weapon.TailSilenced, (int)bridge.Environment, true);
                    }
                    else
                    {
                        PlanOneShot(activeBody, (int)bridge.Environment, _weapon.IsSilenced);
                        PlanOneShot(_weapon.Body, (int)bridge.Environment, false);
                        PlanOneShot(_weapon.BodySilenced, (int)bridge.Environment, true);
                    }
                    PlanOneShot(_weapon.IsSilenced ? _weapon.DoubletSilenced : _weapon.Doublet,
                        (int)bridge.Environment, _weapon.IsSilenced);
                    PlanOneShot(_weapon.Doublet, (int)bridge.Environment, false);
                    PlanOneShot(_weapon.DoubletSilenced, (int)bridge.Environment, true);
                }
            }
            bool loadedThisFrame = false;
            for (int index = 0; index < _workers.Length; index++)
            {
                Worker worker = _workers[index];
                if (worker != null && worker.Job != null)
                {
                    if (AudioListener.pause) worker.Deadline += Time.unscaledDeltaTime;
                    Job job = worker.Job;
                    SilentPcmBuffer decoded = worker.Buffer;
                    if (job.PrefixDone)
                    {
                        // The prefix is published; the worker only finishes the
                        // whole-clip peak histogram of a one-shot.
                        // The histogram is read only once the worker no longer feeds it.
                        if (decoded.HistogramComplete)
                        {
                            worker.Stop();
                            FinishPeakMeasure(job, decoded);
                        }
                        else if (!AudioListener.pause && Time.unscaledTime > worker.Deadline)
                        {
                            worker.Stop();
                            // A clip may end a few frames short of its nominal length.
                            if (decoded.MeasuredFrames >= decoded.HistogramFrames * 0.9) FinishPeakMeasure(job, decoded);
                            else if (job.MeasureDirect)
                            {
                                // Measured again from the start on the next pass.
                                job.Ready = false;
                                job.PrefixDone = false;
                                job.Attempts = Math.Max(0, job.Attempts - 1);
                                if (!_pending.Contains(job)) _pending.Add(job);
                            }
                        }
                    }
                    else if (decoded.Complete)
                    {
                        AutomaticCopyCache.FindOnsetFrame(job.Buffer.Samples, job.Frames, 2, out float peak);
                        job.Ready = peak >= 0.0005f;
                        if (!job.Ready)
                        {
                            worker.Stop();
                            Retry(job, "silent PCM");
                            if (job.Attempts >= 2) CompleteJob(job);
                            else job.Buffer = null;
                        }
                        else
                        {
                            job.PrefixDone = true;
                            bool keepLevels = PublishDirectFromPrefix(job, decoded);
                            if (!job.MeasureDirect || decoded.HistogramComplete)
                            {
                                worker.Stop();
                                FinishPeakMeasure(job, decoded);
                            }
                            if (job.Body)
                                using (PerformanceTrace.Measure(PerformanceArea.WarmupPublish))
                                    AutomaticCopyCache.PublishRoundBody(job.Clip, job.Frames / (float)_rate,
                                        _rate, job.Frames, 2, job.Buffer.Samples, nativePitch: true);
                            // A loop beat that will be composed with its tail does not
                            // displace levels this event already has from the cache.
                            bool composed = job.Body && job.RecipeConsumers > 0;
                            if (job.Normalize && !(composed && LowEndNormalizationCache.IsCurrent(job.Clip)))
                                using (PerformanceTrace.Measure(PerformanceArea.WarmupRegister))
                                {
                                    // A whole shot standing in for an automatic loop
                                    // beat switches to its decay where its own attack
                                    // ends, not after a beat-length window.
                                    float bodySeconds = job.Body ? job.Frames / (float)_rate
                                        : job.MeasureBody ? LowEndLevelModel.EstimateBodySeconds(job.Buffer.Samples, _rate)
                                        : 0f;
                                    LowEndNormalizationCache.Register(job.Clip, _rate, job.Buffer.Samples,
                                        job.ReferenceVolume, job.Group, bodySeconds,
                                        job.Body ? "loopBeat" : job.MeasureBody && bodySeconds > 0f ? "measured" : "default",
                                        composed ? null : job.CacheKey, keepLevels);
                                }
                            CompleteJob(job);
                        }
                    }
                    else if (!AudioListener.pause && Time.unscaledTime > worker.Deadline)
                    {
                        worker.Stop();
                        Retry(job, "decode timeout");
                        if (job.Attempts >= 2) CompleteJob(job);
                        else job.Buffer = null;
                    }
                }
                if (worker != null && worker.Job != null) continue;
                Job next = null;
                int activeGroup = (int)bridge.Environment * 2 + (_weapon.IsSilenced ? 1 : 0);
                // Also prioritize LoadAudioData: selecting a job must not start
                // decoding every lower-priority bank as a side effect.
                for (int priority = 0; priority < 6 && next == null; priority++)
                {
                    for (int pendingIndex = 0; pendingIndex < _pending.Count; pendingIndex++)
                    {
                        Job candidate = _pending[pendingIndex];
                        if (candidate.Ready || candidate.Attempts >= 2 || candidate.Clip == null)
                        {
                            _pending.RemoveAt(pendingIndex--);
                            continue;
                        }
                        if (candidate.Running) continue;
                        if (WarmupJobPriority.Calculate(candidate.Body, candidate.Normalize,
                            candidate.Group == activeGroup) != priority) continue;
                        if (candidate.Clip.loadState == AudioDataLoadState.Unloaded)
                        {
                            // A clip that does not load in the background decodes
                            // inside this call. One per frame, whichever kind it
                            // is, so a bank of them cannot be started at once.
                            if (loadedThisFrame) continue;
                            loadedThisFrame = true;
                            using (PerformanceTrace.Measure(PerformanceArea.WarmupLoad))
                                candidate.Clip.LoadAudioData();
                        }
                        if (candidate.Clip.loadState == AudioDataLoadState.Failed)
                        {
                            candidate.Attempts = 2;
                            Retry(candidate, "AudioClip load failed");
                            _pending.RemoveAt(pendingIndex--);
                            continue;
                        }
                        if (candidate.Clip.loadState != AudioDataLoadState.Loaded) continue;
                        next = candidate;
                        break;
                    }
                }
                if (next == null) continue;
                if (worker == null) _workers[index] = worker = new Worker(transform, index);
                worker.Start(next, _rate);
            }
            ProcessDirectRead(ref loadedThisFrame);
            bool composedThisFrame = false;
            for (int recipeIndex = _recipes.Count - 1; recipeIndex >= 0; recipeIndex--)
            {
                Recipe recipe = _recipes[recipeIndex];
                if (Failed(recipe.Body) || Failed(recipe.Tail))
                {
                    ReleaseRecipe(recipe);
                    _recipes.RemoveAt(recipeIndex);
                    continue;
                }
                if (!recipe.Body.Ready || !recipe.Tail.Ready) continue;
                // Composing and publishing a report is the heaviest single step of
                // a weapon swap. One per frame: the next ready recipe waits for the
                // next Tick rather than stacking several megabyte copies together.
                if (composedThisFrame) break;
                composedThisFrame = true;
                float[] pcm;
                using (PerformanceTrace.Measure(PerformanceArea.WarmupCompose))
                    pcm = AutomaticReportPcm.Compose(recipe.Body.Buffer.Samples,
                        recipe.Tail.Buffer.Samples, recipe.TailGain, (int)(_rate * 0.002f));
                using (PerformanceTrace.Measure(PerformanceArea.WarmupPublish))
                    AutomaticCopyCache.PublishFullReport(recipe.Body.Clip, recipe.Tail.Clip, _rate, pcm);
                using (PerformanceTrace.Measure(PerformanceArea.WarmupRegister))
                {
                    // The composed report is the stock single shot: the event whose
                    // direct level the correction works from. Its key holds no
                    // content hash, so levels taken from the cache stand only if
                    // the decoded report starts exactly as the one they came from.
                    bool cachedEvent = CachedPrefixMatches(recipe.EventKey, pcm, _rate, out string reportHash);
                    LowEndNormalizationCache.Register(recipe.Body.Clip, _rate, pcm,
                        recipe.Body.ReferenceVolume, recipe.Body.Group,
                        recipe.Body.Frames / (float)_rate, cacheKey: recipe.EventKey, keepMeasured: cachedEvent);
                    if (!cachedEvent || !DirectLevelStore.IsComplete(recipe.Body.Clip))
                        PublishDirect(recipe.Body.Clip, recipe.EventKey, recipe.Body.ReferenceVolume,
                            DirectLevelMeter.MeasureAttackWindow(pcm, 2, _rate),
                            DirectLevelMeter.MeasurePeakDb(pcm, 2), "playback", reportHash, _rate,
                            replace: true);
                }
                // A release tail must inherit its body's correction, not have
                // its quiet decay independently boosted to a full attack level.
                LowEndNormalizationCache.Alias(recipe.Tail.Clip, recipe.Body.Clip);
                if (!AutomaticCopyCache.TryGetFullReport(recipe.Body.Clip, _rate, out _)) continue;
                ReleaseRecipe(recipe);
                _recipes.RemoveAt(recipeIndex);
            }
        }

        /// <summary>
        /// An automatic weapon's close body clips, each prepared the way the
        /// engine playing it will play it: a loop beat with its tail, cut into a
        /// cached report, or a whole shot of which only the normalizer's prefix
        /// is needed, exactly as for a pistol. The stock engine loops every one.
        /// </summary>
        private void PlanBody(SoundBank body, SoundBank tail, int currentEnvironment, bool suppressed)
        {
            if (body?.Environments == null) return;
            IShotEngine engine = SoundEngine.Shots;
            int count = body.HasEnvironment ? body.Environments.Length : 1;
            for (int pass = 0; pass < count; pass++)
            {
                int environment = (Math.Max(0, currentEnvironment) + pass) % count;
                AudioClip[] bodies = CloseClips(body, environment);
                AudioClip[] tails = CloseClips(tail, environment);
                // A whole SEB bank takes seconds to decode in real time: a
                // variant fired before its turn borrows its siblings' level.
                LowEndNormalizationCache.Siblings(bodies);
                DirectLevelStore.Siblings(bodies);
                int loops = 0, oneShots = 0, planned = 0;
                for (int index = 0; index < bodies.Length; index++)
                {
                    AudioClip clip = bodies[index];
                    if (clip == null) continue;
                    if (engine.ClassifyBody(body, clip) == BodyModel.Loop)
                    {
                        loops++;
                        if (PlanLoopClip(body, tail, tails, index, clip, environment, suppressed)) planned++;
                    }
                    else
                    {
                        oneShots++;
                        if (PlanOneShotClip(body, clip, environment, suppressed, measureBody: true)) planned++;
                    }
                }
                if (planned == 0) continue;
                string line =
                    $"warmup bank={body.name} env={environment} suppressed={suppressed} " +
                    $"model={(oneShots == 0 ? "loop" : loops == 0 ? "one-shot" : "mixed")} " +
                    $"loops={loops} oneShots={oneShots} variants={bodies.Length} " +
                    $"reference={body.BaseVolume:0.000} plays={(engine.PlaysShotsItself(body) ? "engine" : "queue")} " +
                    $"engine={engine.Name}";
                if (SoundEngine.Module != null && ReportedBanks.Add(body.name + "/" + environment + "/" + suppressed))
                    Plugin.Log?.LogInfo(line);
                if (DetailedDiagnostics.TryBegin(
                    DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                    DetailedDiagnostics.Commit(reservation, line);
            }
        }

        private bool PlanLoopClip(SoundBank body, SoundBank tail, AudioClip[] tails, int index, AudioClip clip,
            int environment, bool suppressed)
        {
            if (AutomaticCopyCache.TryGetFullReport(clip, _rate, out _) ||
                !_plannedBodies.Add(clip.GetInstanceID())) return false;
            float beat = body.ClipLength > 0f ? body.ClipLength / 16f : clip.length / 16f;
            int group = environment * 2 + (suppressed ? 1 : 0);
            AudioClip tailClip = tails.Length > 0 ? tails[index % tails.Length] : null;
            float tailGain = tailClip != null ? tail.BaseVolume / Mathf.Max(0.001f, body.BaseVolume) : 0f;
            // The stock single shot is one beat of the loop and the tail after
            // it: one event, measured and cached as one.
            string eventKey = LevelCache.EventKey(StreamKey(clip), tailClip != null ? StreamKey(tailClip) : null, tailGain);
            if (tailClip != null) DirectLevelStore.Alias(tailClip, clip, tailGain);
            if (ApplyCached(clip, eventKey, body.BaseVolume, group, out bool layerCached))
                NotePath(clip, "cache", layerCached ? null : " layer=decode");
            if (layerCached && tailClip != null) LowEndNormalizationCache.Alias(tailClip, clip);
            // The report itself is still decoded: the copy plays it.
            Job bodyJob = AddJob(clip, Math.Max(1, (int)(beat * _rate)), true,
                true, body.BaseVolume, group);
            bodyJob.CacheKey = eventKey;
            if (tailClip != null)
            {
                Job tailJob = AddJob(tailClip, Math.Max(1, Mathf.RoundToInt(tailClip.length * _rate)), false,
                    group: group);
                bodyJob.RecipeConsumers++;
                tailJob.RecipeConsumers++;
                _recipes.Add(new Recipe { Body = bodyJob, Tail = tailJob, TailGain = tailGain, EventKey = eventKey });
            }
            else bodyJob.MeasureDirectBeat = true;
            if (DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(
                    reservation,
                    $"automatic prewarm queued body={clip.name} tail={tailClip?.name ?? "none"}");
            return true;
        }

        private void PlanOneShot(SoundBank bank, int currentEnvironment, bool suppressed)
        {
            if (bank?.Environments == null) return;
            int count = bank.HasEnvironment ? bank.Environments.Length : 1;
            for (int pass = 0; pass < count; pass++)
            {
                int environment = (Math.Max(0, currentEnvironment) + pass) % count;
                AudioClip[] clips = CloseClips(bank, environment);
                DirectLevelStore.Siblings(clips);
                foreach (AudioClip clip in clips)
                    if (clip != null) PlanOneShotClip(bank, clip, environment, suppressed);
            }
        }

        private bool PlanOneShotClip(SoundBank bank, AudioClip clip, int environment, bool suppressed,
            bool measureBody = false)
        {
            // A clip planned again without a finished direct measure (its decode
            // was cut short) is measured again.
            if (LowEndNormalizationCache.Contains(clip) && DirectLevelStore.IsComplete(clip) ||
                !_plannedBodies.Add(clip.GetInstanceID())) return false;
            int group = environment * 2 + (suppressed ? 1 : 0);
            // A clip decompressed in memory is read directly; its key needs the
            // samples, so the cache is consulted once they are read.
            if (clip.loadType == AudioClipLoadType.DecompressOnLoad)
            {
                _directReads.Add(new DirectRead { Clip = clip, Volume = bank.BaseVolume, Group = group,
                    MeasureBody = measureBody });
                return true;
            }
            AddOneShotJob(clip, bank.BaseVolume, group, measureBody, StreamKey(clip), consultCache: true);
            return true;
        }

        /// <summary>
        /// The playback path of a one-shot: only the raw prefix is kept for the
        /// normalizer, while the peak histogram runs over the whole clip. The
        /// live copy still plays the engine's original clip.
        /// </summary>
        private void AddOneShotJob(AudioClip clip, float volume, int group, bool measureBody, string key,
            bool consultCache)
        {
            // A cached clip is ready at once, and still decoded in the background:
            // its key holds no content hash, so the decoded prefix is checked
            // against the one it was measured from.
            if (consultCache && ApplyCached(clip, key, volume, group, out bool layerCached))
                NotePath(clip, "cache", layerCached ? " verify=decode" : " layer=decode");
            int frames = Math.Max(1, (int)(Math.Min(1.25f, clip.length) * _rate));
            Job job = AddJob(clip, frames, false, true, volume, group);
            // Only an automatic weapon's whole-shot body: pistols, bolt actions
            // and shotguns keep the window they were tuned with.
            job.MeasureBody |= measureBody;
            job.CacheKey = key;
            {
                job.MeasureDirect = true;
                // A clip can end a few frames short of its nominal length once
                // resampled; the last 10 ms of a tail do not move its percentile.
                job.HistogramFrames = Math.Max(frames, (int)(clip.length * _rate) - _rate / 100);
            }
        }

        internal static string StreamKey(AudioClip clip) =>
            LevelCache.ClipKey(clip.name, clip.samples, clip.frequency, clip.channels, LevelCache.StreamTag);

        /// <summary>
        /// Registers what the persistent cache holds for this recording or event.
        /// Returns whether the direct level was there; <paramref name="layerCached"/>
        /// tells whether the copy's levels were too, measured in the current band.
        /// </summary>
        private static bool ApplyCached(AudioClip clip, string key, float volume, int group, out bool layerCached)
        {
            layerCached = false;
            if (!LevelCache.TryGet(key, out LevelCacheEntry entry)) return false;
            if (entry.HasDirect) DirectLevelStore.Set(clip, entry.AttackWindowDb, entry.PeakDb, "cache");
            TuningSnapshot tuning = Plugin.ModConfig?.GetTuning();
            if (tuning != null && entry.HasLayer)
            {
                LowEndNormalizationCache.BandOf(tuning.PitchedLayerSemitones, tuning.PitchedLayerHighpassHz,
                    tuning.PitchedLayerLowpassHz, out float pitch, out float high, out float low);
                if (entry.BandMatches(pitch, high, low))
                {
                    LevelCacheEntry current = entry.Clone();
                    current.Group = group;
                    LowEndNormalizationCache.RegisterMeasured(clip, current, volume, key);
                    layerCached = true;
                }
            }
            return entry.HasDirect;
        }

        private static void PublishDirect(AudioClip clip, string key, float volume, float attackWindowDb,
            float peakDb, string source, string prefixHash = null, int prefixRate = 0, bool replace = false)
        {
            if (replace && clip != null) DirectLevelStore.Replace(clip.GetInstanceID(), attackWindowDb, peakDb, source);
            else DirectLevelStore.Set(clip, attackWindowDb, peakDb, source);
            LevelCache.PutDirect(key, attackWindowDb, peakDb, volume);
            LevelCache.PutPrefix(key, prefixHash, prefixRate);
            NotePath(clip, source,
                $" k50={attackWindowDb:0.0}dB peak={peakDb:0.0}dBFS");
        }

        /// <summary>
        /// The attack window of a one-shot is known once its prefix is in; a
        /// loop beat without a tail is the whole event on its own.
        /// </summary>
        /// <returns>Whether levels already held for this clip from the cache
        /// still stand: false once the decoded prefix shows the recording changed.</returns>
        private bool PublishDirectFromPrefix(Job job, SilentPcmBuffer decoded)
        {
            if (job.CacheKey == null || !job.MeasureDirectBeat && !job.MeasureDirect) return true;
            bool matches = CachedPrefixMatches(job.CacheKey, decoded.Samples, _rate, out string hash);
            job.PrefixHash = hash;
            if (job.MeasureDirectBeat)
            {
                if (!matches)
                    PublishDirect(job.Clip, job.CacheKey, job.ReferenceVolume,
                        DirectLevelMeter.MeasureAttackWindow(decoded.Samples, 2, _rate),
                        DirectLevelMeter.MeasurePeakDb(decoded.Samples, 2), "playback", hash, _rate,
                        replace: true);
                return matches;
            }
            if (!job.MeasureDirect) return true;
            if (matches && DirectLevelStore.IsComplete(job.Clip))
            {
                // The cached measure is of this very recording: nothing to redo.
                job.MeasureDirect = false;
                return true;
            }
            job.AttackWindowDb = DirectLevelMeter.MeasureAttackWindow(decoded.Samples, 2, _rate);
            DirectLevelStore.Replace(job.Clip.GetInstanceID(), job.AttackWindowDb, float.NaN, "playback");
            return false;
        }

        private void FinishPeakMeasure(Job job, SilentPcmBuffer decoded)
        {
            if (!job.MeasureDirect || decoded.Histogram == null || float.IsNaN(job.AttackWindowDb)) return;
            job.MeasureDirect = false;
            PublishDirect(job.Clip, job.CacheKey, job.ReferenceVolume, job.AttackWindowDb,
                decoded.Histogram.PeakDb, "playback", job.PrefixHash, _rate,
                replace: true);
        }

        /// <summary>
        /// Whether the persistent entry was measured from exactly these decoded
        /// samples: the hash of the first 1.25 s at the same output rate.
        /// </summary>
        internal static bool CachedPrefixMatches(string key, float[] pcm, int rate, out string hash)
        {
            int count = Math.Min(pcm.Length, (int)(rate * 1.25f) * 2);
            hash = LevelCache.HashTag(LevelCache.Fnv1a(pcm, count));
            return LevelCache.TryGet(key, out LevelCacheEntry entry) && entry.HasDirect &&
                entry.PrefixHash == hash && entry.PrefixRate == rate;
        }

        // One clip read per frame, on the main thread. A clip that cannot be
        // read, or reads as silence or a constant, goes to the playback path.
        private readonly List<DirectRead> _directReads = new List<DirectRead>();

        private sealed class DirectRead
        {
            internal AudioClip Clip;
            internal float Volume;
            internal int Group;
            internal bool MeasureBody;
            internal float Deadline;
        }

        private void ProcessDirectRead(ref bool loadedThisFrame)
        {
            while (_directReads.Count > 0 && _directReads[0].Clip == null) _directReads.RemoveAt(0);
            if (_directReads.Count == 0) return;
            DirectRead read = _directReads[0];
            AudioClip clip = read.Clip;
            if (clip.loadState == AudioDataLoadState.Unloaded)
            {
                if (loadedThisFrame) return;
                loadedThisFrame = true;
                read.Deadline = Time.unscaledTime + 10f;
                using (PerformanceTrace.Measure(PerformanceArea.WarmupLoad))
                    clip.LoadAudioData();
            }
            if (clip.loadState == AudioDataLoadState.Loading)
            {
                if (read.Deadline <= 0f) read.Deadline = Time.unscaledTime + 10f;
                if (Time.unscaledTime < read.Deadline) return;
            }
            _directReads.RemoveAt(0);
            string key = null;
            using (PerformanceTrace.Measure(PerformanceArea.WarmupRegister))
            {
                if (clip.loadState == AudioDataLoadState.Loaded &&
                    TryReadClip(clip, out float[] stereo, out ulong hash))
                {
                    key = LevelCache.ClipKey(clip.name, clip.samples, clip.frequency, clip.channels,
                        LevelCache.HashTag(hash));
                    if (ApplyCached(clip, key, read.Volume, read.Group, out bool layerCached))
                        NotePath(clip, "cache", layerCached ? null : " layer=getdata");
                    else
                        PublishDirect(clip, key, read.Volume,
                            DirectLevelMeter.MeasureAttackWindow(stereo, 2, clip.frequency),
                            DirectLevelMeter.MeasurePeakDb(stereo, 2), "getdata");
                    if (!layerCached)
                    {
                        // The same prefix the playback path keeps, at the clip's own rate.
                        int prefixFrames = Math.Min(stereo.Length / 2, (int)(clip.frequency * 1.25f));
                        var prefix = new float[prefixFrames * 2];
                        Array.Copy(stereo, prefix, prefix.Length);
                        float bodySeconds = read.MeasureBody
                            ? LowEndLevelModel.EstimateBodySeconds(prefix, clip.frequency) : 0f;
                        LowEndNormalizationCache.Register(clip, clip.frequency, prefix, read.Volume, read.Group,
                            bodySeconds, read.MeasureBody && bodySeconds > 0f ? "measured" : "default", key);
                    }
                    return;
                }
            }
            NotePath(clip, "playback", " (not readable directly)");
            AddOneShotJob(clip, read.Volume, read.Group, read.MeasureBody, StreamKey(clip), consultCache: true);
        }

        /// <summary>
        /// The decoded samples of a clip decompressed in memory, as stereo, and
        /// their hash. Accepted only when they hold a real signal: not silent and
        /// not a constant.
        /// </summary>
        internal static bool TryReadClip(AudioClip clip, out float[] stereo, out ulong hash)
        {
            stereo = null;
            hash = 0;
            int channels = clip.channels;
            int frames = clip.samples;
            if (channels <= 0 || frames <= 0 || (long)channels * frames > 64L * 1024 * 1024) return false;
            var data = new float[frames * channels];
            bool read;
            try { read = clip.GetData(data, 0); }
            catch (Exception) { return false; }
            if (!read || !DirectLevelMeter.IsSignal(data)) return false;
            hash = LevelCache.Fnv1a(data, data.Length);
            stereo = DirectLevelMeter.ToStereo(data, channels);
            return true;
        }

        private static readonly HashSet<int> ReportedPaths = new HashSet<int>();

        /// <summary>Where one clip's measurement came from, once per clip: cache, getdata or playback.</summary>
        private static void NotePath(AudioClip clip, string path, string detail = null)
        {
            if (clip == null || !ReportedPaths.Add(clip.GetInstanceID())) return;
            if (DetailedDiagnostics.TryBegin(DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(reservation, $"level measure clip={clip.name} pcm={path}{detail}");
        }

        private static AudioClip[] CloseClips(SoundBank bank, int environment)
        {
            if (bank?.Environments == null || bank.Environments.Length == 0) return Array.Empty<AudioClip>();
            int index = bank.HasEnvironment ? Math.Min(environment, bank.Environments.Length - 1) : 0;
            var variety = bank.Environments[index];
            return variety?.Clips != null && variety.Clips.Length > 0
                ? variety.Clips[0]?.Clips ?? Array.Empty<AudioClip>() : Array.Empty<AudioClip>();
        }

        private Job AddJob(AudioClip clip, int frames, bool body,
            bool normalize = false, float referenceVolume = 1f, int group = 0)
        {
            var key = new JobKey(clip.GetInstanceID(), frames);
            if (_jobs.TryGetValue(key, out Job existing)) return existing;
            var job = new Job { Clip = clip, Frames = frames, Body = body,
                Normalize = normalize, ReferenceVolume = referenceVolume, Group = group };
            _jobs.Add(key, job);
            _pending.Add(job);
            return job;
        }

        // Keeps exactly the jobs that are still usable as they stand: decoded, with
        // their buffer, ready to be composed when the same weapon comes back.
        // Everything else is dropped, so nothing can later compose from a buffer
        // this released.
        private readonly List<JobKey> _staleJobs = new List<JobKey>();

        private void ReleaseUnfinishedJobs()
        {
            _staleJobs.Clear();
            foreach (KeyValuePair<JobKey, Job> pair in _jobs)
            {
                Job job = pair.Value;
                job.Running = false;
                job.RecipeConsumers = 0;
                if (job.Ready && job.Buffer != null && job.Clip != null) continue;
                job.Buffer = null;
                _staleJobs.Add(pair.Key);
            }
            foreach (JobKey key in _staleJobs) _jobs.Remove(key);
            _staleJobs.Clear();
        }

        private void CompleteJob(Job job)
        {
            _pending.Remove(job);
            if (!job.Ready || job.RecipeConsumers == 0) job.Buffer = null;
        }

        private static bool Failed(Job job) =>
            job == null || job.Clip == null || (!job.Ready && job.Attempts >= 2);

        private static void ReleaseRecipe(Recipe recipe)
        {
            ReleaseRecipeJob(recipe.Body);
            ReleaseRecipeJob(recipe.Tail);
            recipe.Body = null;
            recipe.Tail = null;
        }

        private static void ReleaseRecipeJob(Job job)
        {
            if (job == null) return;
            job.RecipeConsumers = Math.Max(0, job.RecipeConsumers - 1);
            if (job.RecipeConsumers == 0 && !job.Running) job.Buffer = null;
        }

        private static void Retry(Job job, string reason)
        {
            if (DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(
                    reservation,
                    $"automatic prewarm clip={job.Clip?.name} attempt={job.Attempts}/2: {reason}");
        }

        private static long BankFingerprint(WeaponSoundPlayer weapon)
        {
            long hash = 17;
            foreach (SoundBank bank in new[] { weapon.Body, weapon.BodySilenced, weapon.Tail,
                weapon.TailSilenced, weapon.Doublet, weapon.DoubletSilenced })
                hash = hash * 31 + (bank != null ? bank.GetInstanceID() : 0);
            return hash == 0 ? 1 : hash;
        }

        // After the measurements were forgotten: every job, finished or not, is
        // dropped and the held weapon is planned again from its banks.
        private void DropAllWork()
        {
            StopWorkers();
            ReleaseUnfinishedJobs();
            _jobs.Clear();
            _directReads.Clear();
            _pending.Clear();
            _recipes.Clear();
            _plannedBodies.Clear();
            _bankFingerprint = 0;
            _nextDiscovery = 0f;
            if (DetailedDiagnostics.TryBegin(DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(reservation, $"warmup measurements reset on {_weapon?.name}: planned again");
        }

        private void DropWorkForReplacedBanks()
        {
            StopWorkers();
            ReleaseUnfinishedJobs();
            _directReads.Clear();
            _pending.Clear();
            _recipes.Clear();
            _plannedBodies.Clear();
            if (DetailedDiagnostics.TryBegin(DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(reservation,
                    $"warmup banks replaced on {_weapon?.name}: unfinished work for the old banks dropped");
        }

        private void StopWorkers()
        {
            foreach (Worker worker in _workers)
            {
                if (worker == null) continue;
                Job job = worker.Job;
                // A one-shot cut off while only its peak histogram was left has
                // published its prefix but not its whole direct measure: it is
                // decoded again from the start.
                bool remeasure = job != null && job.PrefixDone && job.MeasureDirect;
                if (remeasure) { job.Ready = false; job.PrefixDone = false; }
                worker.Stop(cancelled: true);
                if (remeasure && !_pending.Contains(job)) _pending.Add(job);
            }
        }

        private void OnDisable()
        {
            StopWorkers();
            // Release raw scratch PCM; immutable published clips remain reusable.
            // A job that is already decoded keeps its buffer: a weapon swap back
            // and forth used to throw away finished work and decode it again.
            _directReads.Clear();
            ReleaseUnfinishedJobs();
            _pending.Clear();
            _recipes.Clear();
            _plannedBodies.Clear();
            _failed = false;
            _nextDiscovery = 0f;
            _bankFingerprint = 0;
        }

        private sealed class Recipe
        {
            internal Job Body;
            internal Job Tail;
            internal float TailGain;
            internal string EventKey;
        }

        private readonly struct JobKey : IEquatable<JobKey>
        {
            private readonly int _clip;
            private readonly int _frames;

            internal JobKey(int clip, int frames)
            {
                _clip = clip;
                _frames = frames;
            }

            public bool Equals(JobKey other) =>
                _clip == other._clip && _frames == other._frames;
            public override bool Equals(object obj) =>
                obj is JobKey other && Equals(other);
            public override int GetHashCode() =>
                unchecked((_clip * 397) ^ _frames);
        }

        private sealed class Job
        {
            internal AudioClip Clip;
            internal int Frames;
            internal bool Body;
            internal bool Normalize;
            internal bool MeasureBody;
            internal float ReferenceVolume;
            internal int Group;
            internal bool Ready;
            internal bool Running;
            internal int Attempts;
            internal SilentPcmBuffer Buffer;
            internal int RecipeConsumers;
            // The persistent cache entry this job's measurements go to.
            internal string CacheKey;
            // A one-shot whose direct level is measured from this decode: the
            // attack window from the prefix, the peak over the whole clip.
            internal bool MeasureDirect;
            // A loop beat without a tail: the beat is the whole event.
            internal bool MeasureDirectBeat;
            internal int HistogramFrames;
            internal bool PrefixDone;
            internal float AttackWindowDb = float.NaN;
            internal string PrefixHash;
        }

        private sealed class Worker
        {
            private readonly AudioSource _source;
            private readonly SilentWeaponDecoder _decoder;
            internal Job Job;
            internal SilentPcmBuffer Buffer;
            internal float Deadline;

            internal Worker(Transform parent, int index)
            {
                var child = new GameObject($"GunsAreLoud.SilentWarmup.{index}");
                child.transform.SetParent(parent, false);
                // Install the unconditional sink before playback can ever start.
                _decoder = child.AddComponent<SilentWeaponDecoder>();
                _source = child.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.loop = false;
                _source.spatialBlend = 0f;
                _source.spatialize = false;
                _source.bypassEffects = false;
                _source.bypassListenerEffects = true;
                _source.bypassReverbZones = true;
                _source.priority = 0;
                _source.volume = 1f;
                _source.mute = false;
                _source.pitch = 1f;
            }

            internal void Start(Job job, int rate)
            {
                job.Attempts++;
                job.Running = true;
                job.Buffer = new SilentPcmBuffer(job.Frames, job.MeasureDirect ? job.HistogramFrames : 0);
                job.PrefixDone = false;
                Job = job;
                Buffer = job.Buffer;
                _decoder.Arm(job.Buffer);
                _source.clip = job.Clip;
                _source.timeSamples = 0;
                Deadline = Time.unscaledTime + Math.Max(job.Frames, Buffer.HistogramFrames) / (float)rate + 5f;
                _source.Play();
            }

            internal void Stop(bool cancelled = false)
            {
                _source.Stop();
                _source.clip = null;
                _decoder.Arm(null);
                if (Job != null)
                {
                    Job.Running = false;
                    if (cancelled && !Job.Ready) Job.Buffer = null;
                    if (cancelled && !Job.Ready) Job.Attempts = Math.Max(0, Job.Attempts - 1);
                }
                Job = null;
                Buffer = null;
            }
        }
    }

    [HarmonyPatch(typeof(WeaponSoundPlayer), nameof(WeaponSoundPlayer.Init))]
    internal static class AutomaticWeaponWarmupPatch
    {
        private static void Postfix(WeaponSoundPlayer __instance)
        {
            try { AutomaticWeaponWarmup.Attach(__instance); }
            catch (Exception exception) { Plugin.Log.LogWarning($"automatic prewarm attach: {exception.Message}"); }
        }
    }

    // Init can precede first-person POV. Follow the actual held weapon instead
    // of searching every loaded object (including all bots) once per second.
    internal sealed class AutomaticWarmupDiscovery : MonoBehaviour
    {
        private WeaponSoundPlayer _attached;
        private bool _cacheWriteFailureLogged;

        private void Update()
        {
            AutomaticWeaponWarmup.ReleaseCachesIfIdle();
            if (LevelCache.Tick() == false && !_cacheWriteFailureLogged && LevelCache.Store?.LastWriteError != null)
            {
                _cacheWriteFailureLogged = true;
                Plugin.Log?.LogWarning($"level cache not written (retrying later): {LevelCache.Store.LastWriteError}");
            }
            if (Plugin.ModConfig?.Enabled.Value != true) { _attached = null; return; }
            WeaponSoundPlayer weapon = AudioRuntimeLookup.HeldWeapon;
            if (weapon == null) { _attached = null; return; }
            if (weapon == _attached) return;
            try
            {
                if (AutomaticWeaponWarmup.Attach(weapon)) _attached = weapon;
            }
            catch (Exception exception)
            {
                _attached = weapon; // Fail once for this equip, not a warning every frame.
                Plugin.Log.LogWarning($"automatic prewarm discovery: {exception.Message}");
            }
        }
    }
}
