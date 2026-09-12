using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Audio
{
    // Routes only explicitly mapped direct sources. The original group remains
    // the parent, so its effect order and sends are unchanged.
    internal sealed class GunshotContrastMixerRouter
    {
        private sealed class Route
        {
            internal ContrastRouteSpec Spec;
            internal AudioMixerGroup Original;
            internal AudioMixerGroup Input;
        }

        private const float RampSeconds = 0.08f;
        private readonly AudioMixer _mixer;
        private readonly Route[] _routes;
        private readonly Dictionary<AudioMixerGroup, Route> _byOriginal;
        private readonly Dictionary<AudioMixerGroup, Route> _byInput;
        private float _currentDb;
        private float _startDb;
        private float _targetDb;
        private float _elapsed = RampSeconds;

        private GunshotContrastMixerRouter(AudioMixer mixer, Route[] routes)
        {
            _mixer = mixer;
            _routes = routes;
            _byOriginal = routes.ToDictionary(route => route.Original);
            _byInput = routes.ToDictionary(route => route.Input);
        }

        internal bool Healthy { get; private set; } = true;
        internal string Failure { get; private set; }
        internal int Count => _routes.Length;
        internal float CurrentDb => _currentDb;
        internal bool Ramping => _elapsed < RampSeconds;

        internal static bool TryCreate(AudioMixer mixer, out GunshotContrastMixerRouter router, out string failure)
        {
            router = null;
            failure = null;
            if (mixer == null) { failure = "replacement mixer unavailable"; return false; }
            try
            {
                var claimedOriginals = new HashSet<AudioMixerGroup>();
                var resolved = new List<Route>();
                // The stock graph contains both Occlusion and Occlusion/Occlusion.
                // Resolve the deeper path first, then remove it from the parent's
                // suffix matches instead of trusting group-name uniqueness.
                foreach (ContrastRouteSpec spec in GunshotContrastMixerRouteTable.Routes
                    .OrderByDescending(value => value.ParentPath.Length))
                {
                    AudioMixerGroup[] originals = mixer.FindMatchingGroups(spec.ParentPath)
                        .Where(group => group != null && group.name == spec.ParentName && !claimedOriginals.Contains(group))
                        .ToArray();
                    AudioMixerGroup[] inputs = mixer.FindMatchingGroups(spec.InputPath)
                        .Where(group => group != null && group.name == ContrastRouteSpec.InputGroupName)
                        .ToArray();
                    if (originals.Length != 1 || inputs.Length != 1)
                        throw new InvalidOperationException($"route {spec.ParentPath} resolved original={originals.Length} input={inputs.Length}");
                    if (!mixer.GetFloat(spec.Parameter, out float value) ||
                        float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 0.0001f)
                        throw new InvalidOperationException("route parameter is missing or non-neutral: " + spec.Parameter);
                    claimedOriginals.Add(originals[0]);
                    resolved.Add(new Route { Spec = spec, Original = originals[0], Input = inputs[0] });
                }
                if (resolved.Count != GunshotContrastMixerRouteTable.Routes.Length)
                    throw new InvalidOperationException("contrast route table is incomplete");
                router = new GunshotContrastMixerRouter(mixer, resolved.ToArray());
                return true;
            }
            catch (Exception error)
            {
                failure = error.Message;
                return false;
            }
        }

        internal bool TryGetInput(AudioMixerGroup original, out AudioMixerGroup input)
        {
            input = null;
            if (!Healthy || original == null || !_byOriginal.TryGetValue(original, out Route route)) return false;
            input = route.Input;
            return true;
        }

        internal bool TryGetOriginal(AudioMixerGroup input, out AudioMixerGroup original)
        {
            original = null;
            if (input == null || !_byInput.TryGetValue(input, out Route route)) return false;
            original = route.Original;
            return true;
        }

        internal void SetTarget(float attenuationDb)
        {
            float target = -Math.Max(0f, Math.Min(18f, attenuationDb));
            if (Math.Abs(target - _targetDb) <= 0.0001f) return;
            _startDb = _currentDb;
            _targetDb = target;
            _elapsed = 0f;
        }

        internal void Tick(float unscaledDeltaTime)
        {
            if (!Healthy || _elapsed >= RampSeconds) return;
            _elapsed = Math.Min(RampSeconds, _elapsed + Math.Max(0f, unscaledDeltaTime));
            float value = _startDb + (_targetDb - _startDb) * (_elapsed / RampSeconds);
            Apply(value);
        }

        /// <summary>
        /// Puts every routed group back to no attenuation.
        /// <para>
        /// This runs from the controller's OnDisable at the end of a raid, when
        /// Unity may already have taken the mixer apart. A mixer parameter
        /// outlives the raid, so a neutralize that gives up part-way leaves the
        /// game attenuated until the client is restarted — and under an active
        /// headset route, which attenuates again on top, that is the difference
        /// between quiet and barely audible. So it reaches every route it still
        /// can, and it never throws.
        /// </para>
        /// </summary>
        internal void Neutralize()
        {
            _targetDb = _startDb = 0f;
            _elapsed = RampSeconds;
            _currentDb = 0f;
            if (_mixer == null) return;
            foreach (Route route in _routes)
            {
                try
                {
                    _mixer.SetFloat(route.Spec.Parameter, 0f);
                }
                catch (Exception error)
                {
                    Healthy = false;
                    Failure = "neutralize failed: " + route.Spec.Parameter + ": " + error.Message;
                }
            }
        }

        /// <summary>
        /// What this router believes it has written, and what the mixer actually
        /// holds. The two can drift apart — <see cref="Apply"/> skips a write when
        /// the value already matches its own belief — and a drift here is an
        /// attenuation nothing will ever take off again.
        /// </summary>
        internal string DescribeLiveParameters()
        {
            var text = new System.Text.StringBuilder();
            text.Append("healthy=").Append(Healthy)
                .Append(" believedDb=").Append(_currentDb.ToString("0.###"))
                .Append(" targetDb=").Append(_targetDb.ToString("0.###"))
                .Append(" routes=").Append(_routes.Length);
            if (_mixer == null) return text.Append(" mixer=none").ToString();
            foreach (Route route in _routes)
            {
                text.Append(' ').Append(route.Spec.Parameter).Append('=');
                if (_mixer.GetFloat(route.Spec.Parameter, out float value))
                    text.Append(value.ToString("0.###"));
                else text.Append('?');
            }
            return text.ToString();
        }

        private void Apply(float value)
        {
            if (Math.Abs(value - _currentDb) <= 0.0001f) return;
            if (_mixer == null)
            {
                Healthy = false;
                Failure = "mixer unavailable";
                return;
            }
            foreach (Route route in _routes)
            {
                if (_mixer.SetFloat(route.Spec.Parameter, value)) continue;
                Healthy = false;
                Failure = "mixer write failed: " + route.Spec.Parameter;
                foreach (Route reset in _routes) _mixer.SetFloat(reset.Spec.Parameter, 0f);
                _currentDb = 0f;
                return;
            }
            _currentDb = value;
        }
    }
}
