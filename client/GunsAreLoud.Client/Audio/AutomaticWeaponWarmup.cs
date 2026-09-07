using System;
using System.Collections.Generic;
using System.Threading;
using EFT;
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
        private int _written;
        private int _complete;
        internal bool Complete => Volatile.Read(ref _complete) != 0;

        internal SilentPcmBuffer(int frames)
        {
            Frames = Math.Max(1, frames);
            Samples = new float[Frames * 2];
        }

        internal void ProcessAndSilence(float[] data, int channels)
        {
            if (data == null) return;
            try
            {
                if (channels <= 0 || Complete) return;
                int count = Math.Min(data.Length / channels, Frames - _written);
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
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();
        private readonly List<Job> _pending = new List<Job>();
        private readonly List<Recipe> _recipes = new List<Recipe>();
        private readonly HashSet<int> _plannedBodies = new HashSet<int>();
        private readonly Worker[] _workers = new Worker[2];
        private WeaponSoundPlayer _weapon;
        private int _rate;
        private float _nextDiscovery;
        private bool _failed;

        private void Awake() { _instances++; }

        private void OnDestroy()
        {
            if (--_instances == 0)
            {
                PitchedGunshotLayer.ReportPool?.StopAll();
                AutomaticBeatClipCache.Clear();
                LowEndNormalizationCache.Clear();
            }
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
                _recipes.Clear();
                _plannedBodies.Clear();
                _rate = rate;
            }
            if (Time.unscaledTime >= _nextDiscovery)
            {
                _nextDiscovery = Time.unscaledTime + 0.5f;
                // No listener is common during raid loading. Do not burn retries.
                if (AudioRuntimeLookup.Listener == null) return;
                SoundBank activeBody = _weapon.IsSilenced ? _weapon.BodySilenced : _weapon.Body;
                SoundBank activeTail = _weapon.IsSilenced ? _weapon.TailSilenced : _weapon.Tail;
                if (_weapon.IsAutoWeapon)
                {
                    PlanBank(activeBody, activeTail, (int)bridge.Environment, _weapon.IsSilenced);
                    PlanBank(_weapon.Body, _weapon.Tail, (int)bridge.Environment, false);
                    PlanBank(_weapon.BodySilenced, _weapon.TailSilenced, (int)bridge.Environment, true);
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
            for (int index = 0; index < _workers.Length; index++)
            {
                Worker worker = _workers[index];
                if (worker != null && worker.Job != null)
                {
                    if (AudioListener.pause) worker.Deadline += Time.unscaledDeltaTime;
                    Job job = worker.Job;
                    if (job.Buffer.Complete)
                    {
                        AutomaticBeatClipCache.FindOnsetFrame(job.Buffer.Samples, job.Frames, 2, out float peak);
                        job.Ready = peak >= 0.0005f;
                        worker.Stop();
                        if (!job.Ready) Retry(job, "silent PCM");
                        else if (job.Body)
                            AutomaticBeatClipCache.Publish(job.Clip, job.Frames / (float)_rate,
                                _rate, job.Frames, 2, job.Buffer.Samples, nativePitch: true);
                        if (job.Ready && job.Normalize)
                            LowEndNormalizationCache.Register(job.Clip, _rate, job.Buffer.Samples,
                                job.ReferenceVolume, job.Group,
                                job.Body ? job.Frames / (float)_rate : 0f);
                    }
                    else if (!AudioListener.pause && Time.unscaledTime > worker.Deadline)
                    {
                        worker.Stop();
                        Retry(job, "decode timeout");
                    }
                }
                if (worker != null && worker.Job != null) continue;
                Job next = null;
                int activeGroup = (int)bridge.Environment * 2 + (_weapon.IsSilenced ? 1 : 0);
                // Also prioritize LoadAudioData: selecting a job must not start
                // decoding every lower-priority bank as a side effect.
                for (int priority = 0; priority < 6 && next == null; priority++)
                {
                    foreach (Job candidate in _pending)
                    {
                        if (candidate.Ready || candidate.Running || candidate.Attempts >= 2 || candidate.Clip == null) continue;
                        if (WarmupJobPriority.Calculate(candidate.Body, candidate.Normalize,
                            candidate.Group == activeGroup) != priority) continue;
                        if (candidate.Clip.loadState == AudioDataLoadState.Unloaded) candidate.Clip.LoadAudioData();
                        if (candidate.Clip.loadState == AudioDataLoadState.Failed)
                        {
                            candidate.Attempts = 2;
                            Retry(candidate, "AudioClip load failed");
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
            foreach (Recipe recipe in _recipes)
            {
                if (recipe.Published || !recipe.Body.Ready || !recipe.Tail.Ready) continue;
                float[] pcm = AutomaticReportPcm.Compose(recipe.Body.Buffer.Samples,
                    recipe.Tail.Buffer.Samples, recipe.TailGain, (int)(_rate * 0.002f));
                AutomaticBeatClipCache.PublishReport(recipe.Body.Clip, recipe.Tail.Clip, _rate, pcm);
                LowEndNormalizationCache.Register(recipe.Body.Clip, _rate, pcm,
                    recipe.Body.ReferenceVolume, recipe.Body.Group,
                    recipe.Body.Frames / (float)_rate);
                // A release tail must inherit its body's correction, not have
                // its quiet decay independently boosted to a full attack level.
                LowEndNormalizationCache.Alias(recipe.Tail.Clip, recipe.Body.Clip);
                recipe.Published = AutomaticBeatClipCache.TryGetReport(recipe.Body.Clip, _rate, out _);
            }
        }

        private void PlanBank(SoundBank body, SoundBank tail, int currentEnvironment, bool suppressed)
        {
            if (body?.Environments == null) return;
            int count = body.HasEnvironment ? body.Environments.Length : 1;
            for (int pass = 0; pass < count; pass++)
            {
                int environment = (Math.Max(0, currentEnvironment) + pass) % count;
                AudioClip[] bodies = CloseClips(body, environment);
                AudioClip[] tails = CloseClips(tail, environment);
                for (int index = 0; index < bodies.Length; index++)
                {
                    AudioClip clip = bodies[index];
                    if (clip == null || AutomaticBeatClipCache.TryGetReport(clip, _rate, out _) ||
                        !_plannedBodies.Add(clip.GetInstanceID())) continue;
                    float beat = body.ClipLength > 0f ? body.ClipLength / 16f : clip.length / 16f;
                    Job bodyJob = AddJob(clip, Math.Max(1, (int)(beat * _rate)), true,
                        true, body.BaseVolume, environment * 2 + (suppressed ? 1 : 0));
                    AudioClip tailClip = tails.Length > 0 ? tails[index % tails.Length] : null;
                    if (tailClip != null)
                    {
                        Job tailJob = AddJob(tailClip, Math.Max(1, Mathf.RoundToInt(tailClip.length * _rate)), false,
                            group: environment * 2 + (suppressed ? 1 : 0));
                        _recipes.Add(new Recipe { Body = bodyJob, Tail = tailJob,
                            TailGain = tail.BaseVolume / Mathf.Max(0.001f, body.BaseVolume) });
                    }
                    if (Plugin.ModConfig?.DiagnosticShotLog.Value == true)
                        Plugin.Log.LogInfo($"automatic prewarm queued body={clip.name} tail={tailClip?.name ?? "none"}");
                }
            }
        }

        private void PlanOneShot(SoundBank bank, int currentEnvironment, bool suppressed)
        {
            if (bank?.Environments == null) return;
            int count = bank.HasEnvironment ? bank.Environments.Length : 1;
            for (int pass = 0; pass < count; pass++)
            {
                int environment = (Math.Max(0, currentEnvironment) + pass) % count;
                foreach (AudioClip clip in CloseClips(bank, environment))
                {
                    if (clip == null || LowEndNormalizationCache.Contains(clip) ||
                        !_plannedBodies.Add(clip.GetInstanceID())) continue;
                    // Only the raw prefix is needed for the normalizer. The live
                    // pistol/bolt-action copy still plays EFT's original clip.
                    int frames = Math.Max(1, (int)(Math.Min(1.25f, clip.length) * _rate));
                    AddJob(clip, frames, false, true, bank.BaseVolume,
                        environment * 2 + (suppressed ? 1 : 0));
                }
            }
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
            string key = $"{clip.GetInstanceID()}:{frames}";
            if (_jobs.TryGetValue(key, out Job existing)) return existing;
            var job = new Job { Clip = clip, Frames = frames, Body = body,
                Normalize = normalize, ReferenceVolume = referenceVolume, Group = group };
            _jobs.Add(key, job);
            _pending.Add(job);
            return job;
        }

        private static void Retry(Job job, string reason)
        {
            Plugin.Log.LogWarning($"automatic prewarm clip={job.Clip?.name} attempt={job.Attempts}/2: {reason}");
        }

        private void StopWorkers()
        {
            foreach (Worker worker in _workers) worker?.Stop(cancelled: true);
        }

        private void OnDisable()
        {
            StopWorkers();
            // Release raw scratch PCM; immutable published clips remain reusable.
            _jobs.Clear();
            _pending.Clear();
            _recipes.Clear();
            _plannedBodies.Clear();
            _failed = false;
            _nextDiscovery = 0f;
        }

        private sealed class Recipe
        {
            internal Job Body;
            internal Job Tail;
            internal float TailGain;
            internal bool Published;
        }

        private sealed class Job
        {
            internal AudioClip Clip;
            internal int Frames;
            internal bool Body;
            internal bool Normalize;
            internal float ReferenceVolume;
            internal int Group;
            internal bool Ready;
            internal bool Running;
            internal int Attempts;
            internal SilentPcmBuffer Buffer;
        }

        private sealed class Worker
        {
            private readonly AudioSource _source;
            private readonly SilentWeaponDecoder _decoder;
            internal Job Job;
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
                job.Buffer = new SilentPcmBuffer(job.Frames);
                Job = job;
                _decoder.Arm(job.Buffer);
                _source.clip = job.Clip;
                _source.timeSamples = 0;
                Deadline = Time.unscaledTime + job.Frames / (float)rate + 5f;
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
                    if (cancelled && !Job.Ready) Job.Attempts = Math.Max(0, Job.Attempts - 1);
                }
                Job = null;
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
        private void Update()
        {
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
