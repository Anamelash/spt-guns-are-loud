using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using GunsAreLoud.Client.Runtime;
using Systems.Effects;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// Measures the game's explosion recordings when a raid starts, so the
    /// first explosion already plays at its level: every sound bank the effects
    /// table plays through the grenade voices, in every environment and at
    /// every distance. A clip decompressed in memory is read directly; any
    /// other is decoded by playing it silently, as the weapons are. Levels
    /// come from the persistent cache when it has them and the decode then
    /// only confirms the recording is the one they were taken from.
    /// </summary>
    internal sealed class ExplosionWarmup : MonoBehaviour
    {
        internal const float PrefixSeconds = 1.25f;
        private const int WorkerCount = 2;
        private const float DiscoveryInterval = 1f;

        private readonly List<Job> _pending = new List<Job>();
        private readonly List<DirectRead> _directReads = new List<DirectRead>();
        private readonly HashSet<int> _planned = new HashSet<int>();
        private readonly Worker[] _workers = new Worker[WorkerCount];
        private Effects _effects;
        private int _storeGeneration = -1;
        private float _nextDiscovery;
        private int _rate;

        private void Update()
        {
            if (Plugin.ModConfig?.Enabled.Value != true) return;
            if (Time.unscaledTime >= _nextDiscovery)
            {
                _nextDiscovery = Time.unscaledTime + DiscoveryInterval;
                if (AudioRuntimeLookup.Listener != null) Plan();
            }
            bool loadedThisFrame = false;
            for (int index = 0; index < _workers.Length; index++)
            {
                Worker worker = _workers[index];
                if (worker != null && worker.Job != null)
                {
                    Job job = worker.Job;
                    SilentPcmBuffer decoded = worker.Buffer;
                    if (AudioListener.pause) worker.Deadline += Time.unscaledDeltaTime;
                    if (decoded.Complete)
                    {
                        worker.Stop();
                        if (DirectLevelMeter.IsSignal(decoded.Samples)) Publish(job, decoded.Samples);
                        else Retry(job, "silent PCM");
                    }
                    else if (!AudioListener.pause && Time.unscaledTime > worker.Deadline)
                    {
                        worker.Stop();
                        Retry(job, "decode timeout");
                    }
                    else continue;
                }
                Job next = null;
                for (int pendingIndex = 0; pendingIndex < _pending.Count; pendingIndex++)
                {
                    Job candidate = _pending[pendingIndex];
                    if (candidate.Clip == null || candidate.Running) continue;
                    if (candidate.Clip.loadState == AudioDataLoadState.Unloaded)
                    {
                        // One load per frame: a clip that does not load in the
                        // background decodes inside this call.
                        if (loadedThisFrame) continue;
                        loadedThisFrame = true;
                        candidate.Clip.LoadAudioData();
                    }
                    if (candidate.Clip.loadState == AudioDataLoadState.Failed)
                    {
                        _pending.RemoveAt(pendingIndex--);
                        Note(candidate.Clip, "failed", " (AudioClip load failed)");
                        continue;
                    }
                    if (candidate.Clip.loadState != AudioDataLoadState.Loaded) continue;
                    next = candidate;
                    break;
                }
                if (next == null) continue;
                if (worker == null) _workers[index] = worker = new Worker(transform, index);
                _pending.Remove(next);
                worker.Start(next, _rate);
            }
            ProcessDirectRead(ref loadedThisFrame);
        }

        /// <summary>
        /// Every grenade bank the effects table can play, planned once per
        /// effects table and once per life of the level store.
        /// </summary>
        private void Plan()
        {
            if (!Singleton<Effects>.Instantiated) return;
            Effects effects = Singleton<Effects>.Instance;
            if (effects == null || effects.EffectsArray == null) return;
            if (!ReferenceEquals(effects, _effects) || _storeGeneration != DirectLevelStore.Generation)
            {
                DropAllWork();
                _effects = effects;
                _storeGeneration = DirectLevelStore.Generation;
            }
            _rate = Math.Max(8000, AudioRuntimeState.OutputSampleRate);
            int banks = 0, recordings = 0;
            var seen = new HashSet<int>();
            foreach (Effects.Effect effect in effects.EffectsArray)
            {
                SoundBank bank = effect?.Sound;
                if (bank == null || bank.SourceType != BetterAudio.AudioSourceGroupType.Grenades ||
                    bank.Environments == null || !seen.Add(bank.GetInstanceID())) continue;
                banks++;
                int environments = bank.HasEnvironment ? bank.Environments.Length : 1;
                for (int environment = 0; environment < environments; environment++)
                {
                    EnvironmentVariety variety = bank.Environments[environment];
                    if (variety?.Clips == null) continue;
                    foreach (DistanceVarity distance in variety.Clips)
                    {
                        AudioClip[] clips = distance?.Clips;
                        if (clips == null || clips.Length == 0) continue;
                        // Variants of one bank at one distance: one played before
                        // its turn borrows the measured ones' level.
                        DirectLevelStore.Siblings(clips);
                        foreach (AudioClip clip in clips)
                            if (PlanClip(clip, bank.BaseVolume)) recordings++;
                    }
                }
            }
            if (recordings > 0 && DetailedDiagnostics.TryBegin(DiagnosticEventKind.AutomaticWarmup,
                    out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(reservation, $"explosion warmup banks={banks} recordings={recordings} queued");
        }

        private bool PlanClip(AudioClip clip, float volume)
        {
            if (clip == null || !_planned.Add(clip.GetInstanceID())) return false;
            if (clip.loadType == AudioClipLoadType.DecompressOnLoad)
            {
                _directReads.Add(new DirectRead { Clip = clip, Volume = volume });
                return true;
            }
            string key = AutomaticWeaponWarmup.StreamKey(clip);
            if (LevelCache.TryGet(key, out LevelCacheEntry entry) && entry.HasBody)
            {
                DirectLevelStore.Set(clip, entry.AttackWindowDb, entry.PeakDb, "cache", entry.BodyWindowDb);
                Note(clip, "cache", " verify=decode");
            }
            _pending.Add(new Job
            {
                Clip = clip, Volume = volume, Key = key,
                Frames = Math.Max(1, (int)(Math.Min(PrefixSeconds, clip.length) * _rate))
            });
            return true;
        }

        private void Publish(Job job, float[] samples)
        {
            bool matches = AutomaticWeaponWarmup.CachedPrefixMatches(job.Key, samples, _rate, out string hash);
            if (matches && LevelCache.TryGet(job.Key, out LevelCacheEntry entry) && entry.HasBody)
            {
                DirectLevelStore.Set(job.Clip, entry.AttackWindowDb, entry.PeakDb, "cache", entry.BodyWindowDb);
                return;
            }
            PublishMeasured(job.Clip, job.Key, job.Volume, samples, _rate, hash, "playback");
        }

        private static void PublishMeasured(AudioClip clip, string key, float volume, float[] stereo, int rate,
            string hash, string source)
        {
            float attack = DirectLevelMeter.MeasureAttackWindow(stereo, 2, rate);
            float body = DirectLevelMeter.MeasureWindowDb(stereo, 2, rate, ExplosionLevelModel.WindowSeconds);
            float peak = DirectLevelMeter.MeasurePeakDb(stereo, 2);
            DirectLevelStore.Replace(clip.GetInstanceID(), attack, peak, source, body);
            LevelCache.PutDirect(key, attack, peak, volume, body);
            if (hash != null) LevelCache.PutPrefix(key, hash, rate);
            Note(clip, source, $" k50={attack:0.0}dB k240={body:0.0}dB peak={peak:0.0}dBFS");
        }

        private void Retry(Job job, string reason)
        {
            job.Attempts++;
            if (job.Attempts < 2) _pending.Add(job);
            else Note(job.Clip, "failed", $" ({reason})");
        }

        // One clip read per frame, on the main thread.
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
                clip.LoadAudioData();
            }
            if (clip.loadState == AudioDataLoadState.Loading)
            {
                if (read.Deadline <= 0f) read.Deadline = Time.unscaledTime + 10f;
                if (Time.unscaledTime < read.Deadline) return;
            }
            _directReads.RemoveAt(0);
            if (clip.loadState == AudioDataLoadState.Loaded &&
                AutomaticWeaponWarmup.TryReadClip(clip, out float[] stereo, out ulong hash))
            {
                string key = LevelCache.ClipKey(clip.name, clip.samples, clip.frequency, clip.channels,
                    LevelCache.HashTag(hash));
                if (LevelCache.TryGet(key, out LevelCacheEntry entry) && entry.HasBody)
                {
                    DirectLevelStore.Set(clip, entry.AttackWindowDb, entry.PeakDb, "cache", entry.BodyWindowDb);
                    Note(clip, "cache");
                }
                else PublishMeasured(clip, key, read.Volume, stereo, clip.frequency, null, "getdata");
                return;
            }
            // Not readable directly after all: the playback path.
            string streamKey = AutomaticWeaponWarmup.StreamKey(clip);
            if (LevelCache.TryGet(streamKey, out LevelCacheEntry cached) && cached.HasBody)
                DirectLevelStore.Set(clip, cached.AttackWindowDb, cached.PeakDb, "cache", cached.BodyWindowDb);
            _pending.Add(new Job
            {
                Clip = clip, Volume = read.Volume, Key = streamKey,
                Frames = Math.Max(1, (int)(Math.Min(PrefixSeconds, clip.length) * _rate))
            });
        }

        private static void Note(AudioClip clip, string path, string detail = null)
        {
            if (clip == null) return;
            if (DetailedDiagnostics.TryBegin(DiagnosticEventKind.AutomaticWarmup, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(reservation, $"explosion level measure clip={clip.name} pcm={path}{detail}");
        }

        private void DropAllWork()
        {
            foreach (Worker worker in _workers) worker?.Stop();
            _pending.Clear();
            _directReads.Clear();
            _planned.Clear();
        }

        private void OnDisable() => DropAllWork();

        private sealed class Job
        {
            internal AudioClip Clip;
            internal float Volume;
            internal string Key;
            internal int Frames;
            internal int Attempts;
            internal bool Running;
        }

        private sealed class DirectRead
        {
            internal AudioClip Clip;
            internal float Volume;
            internal float Deadline;
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
                var child = new GameObject($"GunsAreLoud.SilentExplosion.{index}");
                child.transform.SetParent(parent, false);
                // The sink is installed before anything can play through the source.
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
                job.Running = true;
                Buffer = new SilentPcmBuffer(job.Frames);
                Job = job;
                _decoder.Arm(Buffer);
                _source.clip = job.Clip;
                _source.timeSamples = 0;
                Deadline = Time.unscaledTime + job.Frames / (float)rate + 5f;
                _source.Play();
            }

            internal void Stop()
            {
                _source.Stop();
                _source.clip = null;
                _decoder.Arm(null);
                if (Job != null) Job.Running = false;
                Job = null;
                Buffer = null;
            }
        }
    }
}
