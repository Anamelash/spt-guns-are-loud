using System;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class HeadphoneFrameActivity
    {
        private bool _sampled;
        private ulong _frames;
        private float _time;
        internal ulong Delta { get; private set; }
        internal bool Observe(bool available, ulong frames, float now)
        {
            bool advancing = available && _sampled && now > _time && now - _time <= 2f && frames > _frames;
            Delta = advancing ? frames - _frames : 0;
            _sampled = available;
            _frames = frames;
            _time = now;
            return advancing;
        }
    }

    // Read-only F12 drawer. Never writes config or injects a test sound.
    internal static class HeadphoneDiagnostics
    {
        private static readonly HeadphoneFrameActivity Activity = new HeadphoneFrameActivity();
        private static float _next;
        private static string _text = "DSP loading";
        private static bool _connected;
        private static GUIStyle _style;

        internal static string InitializationStatus(bool abi, bool registered, bool hook, bool attempted, string failure, int instances)
        {
            if (!abi) return "NOT LOADED — native DLL/ABI unavailable";
            if (!hook) return "ERROR — game mixer load hook was not installed";
            if (!string.IsNullOrEmpty(failure)) return "ERROR — mixer load failed: " + failure;
            if (!attempted) return registered
                ? "REGISTERED / WAITING — effect registered; raid mixer not requested yet"
                : "UNVERIFIED — DLL loaded; Unity registration not confirmed";
            return instances == 0 ? "ERROR — mixer requested but no native DSP instance" : null;
        }

        /// <summary>
        /// The headset DSP is off because another mod owns the master mixer:
        /// a known, intended state, shown in yellow rather than as an error.
        /// Null when this mod's own mixer route applies.
        /// </summary>
        internal static string ForeignMixerStatus(string yieldedTo, HeadphoneRouteFallback fallback, string owner,
            string reason = null)
        {
            if (!string.IsNullOrEmpty(yieldedTo))
                return "DSP off — " + yieldedTo + " mixer" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason);
            if (fallback != HeadphoneRouteFallback.ForeignMasterMixer) return null;
            return "DSP off — " +
                (string.IsNullOrEmpty(owner) || owner == HeadphoneRouteRuntime.UnknownMixerOwner ? "foreign" : owner) +
                " mixer";
        }

        /// <summary>"Sound Engine Backport" → "SEB": short enough for the status line.</summary>
        internal static string Initials(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var text = new System.Text.StringBuilder();
            foreach (string word in name.Split(' '))
                if (word.Length > 0 && char.IsLetter(word[0])) text.Append(char.ToUpperInvariant(word[0]));
            return text.Length > 1 ? text.ToString() : name;
        }

        internal static void Draw(ConfigEntryBase unused)
        {
            float now = Time.realtimeSinceStartup;
            if (now >= _next)
            {
                _next = now + 0.5f;
                Sample(now);
            }
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { wordWrap = true };
            Color before = GUI.contentColor;
            try
            {
                GUI.contentColor = _text == "DSP error" ? Color.red :
                    _text.StartsWith("DSP active", StringComparison.Ordinal) ||
                    _text.StartsWith("DSP ready", StringComparison.Ordinal) ? Color.green : Color.yellow;
                GUILayout.Label(_text, _style);
            }
            finally { GUI.contentColor = before; }
        }

        private static void Sample(float now)
        {
            _connected = false;
            try
            {
                bool abi = HeadphoneNativePlugin.IsPreloaded(out string nativeReason);
                bool registered = HeadphoneNativePlugin.IsRegistered(out string registrationReason);
                int instances = abi ? HeadphoneNativePlugin.InstanceCount : 0;
                ulong frames = 0;
                bool counter = abi && HeadphoneNativePlugin.TryGetProcessedFrames(out frames);
                bool advancing = Activity.Observe(counter, frames, now);
                var runtime = HeadphoneRouteRuntime.Instance;
                var route = runtime != null ? runtime.Status : default;
                string reason = "route runtime unavailable";
                bool verified = runtime != null && runtime.VerifyConnection(out reason);
                string foreign = ForeignMixerStatus(HeadphoneMixerAsset.YieldedTo, route.Fallback,
                    HeadphoneRouteRuntime.ForeignMixerOwner(), HeadphoneMixerAsset.YieldReason);
                if (foreign != null)
                {
                    _text = foreign;
                    return;
                }
                _connected = Plugin.ModConfig?.Enabled.Value == true && abi && instances == 1 && advancing && verified &&
                    route.Effective == HeadphoneMode.Realistic && route.Fallback == HeadphoneRouteFallback.None;
                string initialization = InitializationStatus(abi, registered, HeadphoneMixerAsset.HookInstalled,
                    HeadphoneMixerAsset.LoadAttempted, HeadphoneMixerAsset.LoadFailure, instances);
                bool transition = reason == "route transition in progress" ||
                    route.Fallback == HeadphoneRouteFallback.ProfileFitPending;
                bool routeError = route.Fallback == HeadphoneRouteFallback.MixerWriteFailed ||
                    route.Fallback == HeadphoneRouteFallback.IncompleteMixerRoute ||
                    route.Fallback == HeadphoneRouteFallback.MixerUnavailable ||
                    (route.Effective == HeadphoneMode.Realistic && !verified && !transition);
                bool error = !abi || !HeadphoneMixerAsset.HookInstalled ||
                    !string.IsNullOrEmpty(HeadphoneMixerAsset.LoadFailure) ||
                    (HeadphoneMixerAsset.LoadAttempted && (instances != 1 || !counter || routeError));
                _text = error ? "DSP error" :
                    !HeadphoneMixerAsset.LoadAttempted || transition ? "DSP loading" :
                    (_connected && initialization == null ? "DSP active" : "DSP ready") +
                    (HeadphoneMixerAsset.HostVariantActive ? " (" + Initials(Engine.SoundEngine.Mixer.DisplayName) + ")" : "");
            }
            catch (Exception)
            {
                Activity.Observe(false, 0, now);
                _text = "DSP error";
            }
        }
    }
}
