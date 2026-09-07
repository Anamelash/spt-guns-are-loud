using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Runtime
{
    internal static class MixerParameterRamp
    {
        internal static float Evaluate(float start, float target, float elapsed,
            float duration, out bool complete)
        {
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.0001f, duration));
            complete = t >= 1f;
            float smooth = t * t * (3f - 2f * t);
            return Mathf.Lerp(start, target, smooth);
        }
    }

    // Main-thread parameter ramp. Mixer parameters use different units, so the
    // interpolation is performed in each parameter's native domain. Smoothstep
    // gives zero slope at both ends and avoids discontinuities at F12 changes.
    internal sealed class SmoothedMixerParameterStore : IMixerParameterStore
    {
        private struct Ramp
        {
            internal float Start, Target, Elapsed;
        }

        private readonly AudioMixer _mixer;
        private readonly float _duration;
        private readonly Dictionary<string, Ramp> _ramps = new Dictionary<string, Ramp>();
        private readonly List<string> _keys = new List<string>(64);
        private bool _hasDeferredReset;
        private float _deferredReset;

        internal SmoothedMixerParameterStore(AudioMixer mixer, float durationSeconds = 0.08f)
        {
            _mixer = mixer;
            _duration = Mathf.Max(0.02f, durationSeconds);
        }

        internal bool IsTransitioning => _ramps.Count != 0 || _hasDeferredReset;

        public bool TryGet(string name, out float value)
        {
            value = 0f;
            return _mixer != null && _mixer.GetFloat(name, out value);
        }

        public bool TrySet(string name, float value)
        {
            if (_mixer == null || !_mixer.GetFloat(name, out float current)) return false;
            // Reset is a discrete generation token. Interpolating it would
            // publish many fractional generations and repeatedly reset native
            // detector state during one transition.
            if (RequiresImmediateSet(name))
            {
                _ramps.Remove(name);
                float wet = Current("GAL_ElectronicsWet");
                float volume = Current("GAL_ElectronicsVolume");
                if (ShouldDeferReset(value, current, wet, volume))
                {
                    _deferredReset = value;
                    _hasDeferredReset = true;
                    return true;
                }
                _hasDeferredReset = false;
                return _mixer.SetFloat(name, value);
            }
            _ramps[name] = new Ramp { Start = current, Target = value, Elapsed = 0f };
            return true;
        }

        internal static bool RequiresImmediateSet(string name) =>
            name == "GAL_ElectronicsReset";

        internal static bool ShouldDeferReset(float targetReset, float currentReset,
            float wet, float volumeDb) => targetReset <= currentReset &&
            wet > 0.0001f && volumeDb > -79.5f;

        private float Current(string name) =>
            _mixer.GetFloat(name, out float value) ? value : 0f;

        internal void Tick(float deltaSeconds)
        {
            if (_mixer == null) return;
            if (_ramps.Count == 0) { TryCommitDeferredReset(); return; }
            _keys.Clear();
            foreach (string key in _ramps.Keys) _keys.Add(key);
            foreach (string key in _keys)
            {
                Ramp ramp = _ramps[key];
                ramp.Elapsed += Mathf.Max(0f, deltaSeconds);
                float value = MixerParameterRamp.Evaluate(ramp.Start, ramp.Target,
                    ramp.Elapsed, _duration, out bool complete);
                _mixer.SetFloat(key, value);
                if (complete) _ramps.Remove(key); else _ramps[key] = ramp;
            }
            TryCommitDeferredReset();
        }

        private void TryCommitDeferredReset()
        {
            if (!_hasDeferredReset) return;
            float wet = Current("GAL_ElectronicsWet");
            float volume = Current("GAL_ElectronicsVolume");
            if (wet > 0.0001f && volume > -79.5f) return;
            if (_mixer.SetFloat("GAL_ElectronicsReset", _deferredReset))
                _hasDeferredReset = false;
        }

        internal void Flush()
        {
            if (_mixer == null) { _ramps.Clear(); _hasDeferredReset = false; return; }
            foreach (KeyValuePair<string, Ramp> item in _ramps)
                _mixer.SetFloat(item.Key, item.Value.Target);
            _ramps.Clear();
            if (_hasDeferredReset &&
                _mixer.SetFloat("GAL_ElectronicsReset", _deferredReset))
                _hasDeferredReset = false;
        }

        // EFT owns its stock parameters during ApplyTemplate. Keep only the
        // custom passive-bus restoration so the two faders never write the
        // same parameter, while both branches still crossfade smoothly.
        internal void RetainOnlyPrefix(string prefix)
        {
            _keys.Clear();
            foreach (string key in _ramps.Keys)
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) _keys.Add(key);
            foreach (string key in _keys) _ramps.Remove(key);
        }
    }
}
