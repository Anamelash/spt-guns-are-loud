using System;
using System.Collections.Generic;
using GunsAreLoud.Client.Audio;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Runtime
{
    internal interface IMixerParameterStore
    {
        bool TryGet(string name, out float value);
        bool TrySet(string name, float value);
    }

    internal sealed class UnityMixerParameterStore : IMixerParameterStore
    {
        private readonly AudioMixer _mixer;
        internal UnityMixerParameterStore(AudioMixer mixer) { _mixer = mixer; }
        public bool TryGet(string name, out float value)
        {
            value = 0f;
            return _mixer != null && _mixer.GetFloat(name, out value);
        }
        public bool TrySet(string name, float value) =>
            _mixer != null && _mixer.SetFloat(name, value);
    }

    // Parameter contract of the replacement MasterMixer. Every parameter is
    // validated before the first write; any failed write restores the snapshot.
    internal sealed class TransactionalMixerHeadphoneRoute : IHeadphoneRouteBackend
    {
        internal const string PassiveVolume = "GAL_PassiveVolume";
        internal const string PassiveLowGain = "GAL_PassiveBand1Gain";
        internal const string PassiveMidGain = "GAL_PassiveBand4Gain";
        internal const string PassiveHighGain = "GAL_PassiveBand7Gain";
        internal const string PassiveLowFrequency = "GAL_PassiveBand1Frequency";
        internal const string PassiveMidFrequency = "GAL_PassiveBand4Frequency";
        internal const string PassiveHighFrequency = "GAL_PassiveBand7Frequency";

        private static readonly string[] Required =
        {
            PassiveVolume, PassiveLowGain, PassiveMidGain, PassiveHighGain,
            PassiveLowFrequency, PassiveMidFrequency, PassiveHighFrequency,
            "GAL_PassiveBand1Q", "GAL_PassiveBand2Gain", "GAL_PassiveBand2Frequency", "GAL_PassiveBand2Q",
            "GAL_PassiveBand3Gain", "GAL_PassiveBand3Frequency", "GAL_PassiveBand3Q", "GAL_PassiveBand4Q",
            "GAL_PassiveBand5Gain", "GAL_PassiveBand5Frequency", "GAL_PassiveBand5Q",
            "GAL_PassiveBand6Gain", "GAL_PassiveBand6Frequency", "GAL_PassiveBand6Q", "GAL_PassiveBand7Q",
            "GAL_PassiveBand8Gain", "GAL_PassiveBand8Frequency", "GAL_PassiveBand8Q",
            "GAL_PassiveBand9Gain", "GAL_PassiveBand9Frequency", "GAL_PassiveBand9Q",
            "GAL_ElectronicsVolume", "GAL_ElectronicsGunsSend",
            "GAL_ElectronicsClientPlayerSend", "GAL_ElectronicsObservedPlayerSend",
            "GAL_ElectronicsNpcSend", "GAL_ElectronicsEnvTechnicalSend",
            "GAL_ElectronicsEnvNatureSend", "GAL_ElectronicsEnvCommonSend",
            "GAL_ElectronicsAmbientSend", "GAL_ElectronicsEffectsReturnsSend",
            "GAL_ElectronicsNonspatialBypassSend", "GAL_ElectronicsVoipSend",
            "GAL_ElectronicsOcclusionSend",
            "GAL_ElectronicsMicHP", "GAL_ElectronicsMicLP", "GAL_ElectronicsQuietGain",
            "GAL_ElectronicsThreshold", "GAL_ElectronicsRatio", "GAL_ElectronicsKnee",
            "GAL_ElectronicsAttack", "GAL_ElectronicsHold", "GAL_ElectronicsRelease",
            "GAL_ElectronicsCeiling", "GAL_ElectronicsWet", "GAL_ElectronicsReset",
            "GunsVolume", "OcclusionVolume", "EnvironmentVolume", "AmbientVolume",
            "EffectsReturnsGroupVolume", "OutEnvironmentVolume",
            "HeadphonesMixerVolume"
        };

        private readonly IMixerParameterStore _mixer;
        private readonly int _sampleRate;
        private readonly IHeadphoneNativeEqProvider _fits;
        private readonly Dictionary<string, float> _vanilla = new Dictionary<string, float>();
        private bool _active;
        private Dictionary<string, float> _expected;
        private bool _restorePending;
        private int _resetGeneration;

        internal TransactionalMixerHeadphoneRoute(IMixerParameterStore mixer)
            : this(mixer, AudioSettings.outputSampleRate, HeadphoneNativeEqCacheProvider.Instance) { }

        internal TransactionalMixerHeadphoneRoute(IMixerParameterStore mixer, int sampleRate)
            : this(mixer, sampleRate, HeadphoneNativeEqCacheProvider.Instance) { }

        internal TransactionalMixerHeadphoneRoute(IMixerParameterStore mixer, int sampleRate,
            IHeadphoneNativeEqProvider fits)
        {
            _mixer = mixer;
            _sampleRate = Math.Max(8000, sampleRate);
            _fits = fits ?? HeadphoneNativeEqCacheProvider.Instance;
        }

        public bool TryActivate(HeadsetProfile profile, out string reason)
        {
            if (profile?.Passive == null || profile.Electronics == null)
            { reason = "profile lacks passive or electronics path"; return false; }
            if (!_fits.TryGet(profile, _sampleRate, out HeadphoneNativeEqFit fit))
            { reason = "profile-fit-pending"; return false; }
            if (_restorePending)
            { reason = "mixer-restore-pending"; return false; }
            if (!TrySnapshot(out reason)) return false;

            int nextReset = _resetGeneration == 16777215 ? 1 : _resetGeneration + 1;
            var values = BuildValues(profile, fit, nextReset);
            foreach (KeyValuePair<string, float> item in values)
            {
                if (!_mixer.TrySet(item.Key, item.Value))
                {
                    _restorePending = !Rollback();
                    reason = "mixer rejected parameter " + item.Key;
                    return false;
                }
            }
            _active = true;
            _expected = values;
            _resetGeneration = nextReset;
            reason = "";
            return true;
        }

        public bool TryRestore(out string reason)
        {
            bool ok = Rollback();
            reason = ok ? "" : "one or more vanilla mixer parameters could not be restored";
            return ok;
        }

        internal bool VerifyActive(out string reason)
        {
            if (!_active || _restorePending || _expected == null)
            { reason = "route not active"; return false; }
            foreach (var item in _expected)
            {
                if (!_mixer.TryGet(item.Key, out float actual) || float.IsNaN(actual) || float.IsInfinity(actual) ||
                    Math.Abs(actual - item.Value) > Math.Max(0.001f, Math.Abs(item.Value) * 0.0001f))
                { reason = "mixer readback mismatch: " + item.Key; return false; }
            }
            reason = "all profile and routing parameters verified";
            return true;
        }

        private bool TrySnapshot(out string reason)
        {
            if (_active) { reason = ""; return true; }
            _vanilla.Clear();
            foreach (string name in Required)
            {
                if (!_mixer.TryGet(name, out float value))
                { _vanilla.Clear(); reason = "required mixer parameter missing: " + name; return false; }
                _vanilla[name] = value;
            }
            reason = "";
            return true;
        }

        private bool Rollback()
        {
            bool ok = true;
            foreach (KeyValuePair<string, float> item in _vanilla)
                ok &= _mixer.TrySet(item.Key, item.Value);
            // Retain the complete original snapshot after a partial restore so a
            // later retry can repair every parameter instead of accepting a
            // half-restored route as Vanilla.
            if (ok)
            {
                _vanilla.Clear();
                _active = false;
                _expected = null;
                _restorePending = false;
            }
            else _restorePending = true;
            return ok;
        }

        private static Dictionary<string, float> BuildValues(HeadsetProfile profile,
            HeadphoneNativeEqFit fit, int resetGeneration)
        {
            HeadsetPassiveProfile passive = profile.Passive;
            HeadsetElectronicsProfile electronics = profile.Electronics;
            var values = new Dictionary<string, float>
            {
                [PassiveVolume] = fit.BaseVolumeDb,
                // The custom passive parent now owns isolation. Stock per-category
                // dry attenuation must be neutral or it would be applied twice.
                ["GunsVolume"] = 0f,
                ["OcclusionVolume"] = 0f,
                ["EnvironmentVolume"] = 0f,
                ["AmbientVolume"] = 0f,
                ["EffectsReturnsGroupVolume"] = 0f,
                ["OutEnvironmentVolume"] = 0f,
                ["HeadphonesMixerVolume"] = -80f,
                ["GAL_ElectronicsVolume"] = 0f,
                ["GAL_ElectronicsGunsSend"] = 0f,
                ["GAL_ElectronicsClientPlayerSend"] = 0f,
                ["GAL_ElectronicsObservedPlayerSend"] = 0f,
                ["GAL_ElectronicsNpcSend"] = 0f,
                ["GAL_ElectronicsEnvTechnicalSend"] = 0f,
                ["GAL_ElectronicsEnvNatureSend"] = 0f,
                ["GAL_ElectronicsEnvCommonSend"] = 0f,
                ["GAL_ElectronicsAmbientSend"] = 0f,
                ["GAL_ElectronicsEffectsReturnsSend"] = 0f,
                ["GAL_ElectronicsNonspatialBypassSend"] = 0f,
                ["GAL_ElectronicsVoipSend"] = 0f,
                ["GAL_ElectronicsOcclusionSend"] = 0f,
                ["GAL_ElectronicsMicHP"] = electronics.MicHighpassHz,
                ["GAL_ElectronicsMicLP"] = electronics.MicLowpassHz,
                ["GAL_ElectronicsQuietGain"] = electronics.QuietGainDb,
                ["GAL_ElectronicsThreshold"] = electronics.ThresholdDbFs,
                ["GAL_ElectronicsRatio"] = electronics.Ratio,
                ["GAL_ElectronicsKnee"] = electronics.KneeDb,
                ["GAL_ElectronicsAttack"] = electronics.AttackSeconds * 1000f,
                ["GAL_ElectronicsHold"] = electronics.HoldSeconds * 1000f,
                ["GAL_ElectronicsRelease"] = electronics.ReleaseSeconds * 1000f,
                ["GAL_ElectronicsCeiling"] = electronics.OutputCeiling,
                ["GAL_ElectronicsWet"] = 1f,
                ["GAL_ElectronicsReset"] = resetGeneration
            };
            for (int band = 1; band <= 9; band++)
            {
                values[$"GAL_PassiveBand{band}Gain"] = 1f;
                values[$"GAL_PassiveBand{band}Q"] = 1f;
            }
            int bands = Math.Min(9, fit.BandCount);
            for (int i = 0; i < bands; i++)
            {
                int band = i + 1;
                values[$"GAL_PassiveBand{band}Gain"] = fit.LinearGainAt(i);
                values[$"GAL_PassiveBand{band}Frequency"] = fit.FrequencyAt(i);
                values[$"GAL_PassiveBand{band}Q"] = fit.OctaveRangeAt(i);
            }
            return values;
        }

    }
}
