using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Engine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Integrations.Seb
{
    /// <summary>
    /// Sound Engine Backport's live mixer (EFT 1.0) as the master: SEB loads it
    /// in <c>LiveMixer.Install</c>, chains it into the stock mixer and routes
    /// sources onto it. This mod's variant of it goes into the slot SEB loads from.
    /// </summary>
    internal sealed class SebMixerHost : IMixerHost
    {
        internal const string LiveResourceName = "GunsAreLoud.Client.Assets.gal_headphone_mixer_live";
        internal const string LiveAssetPath = "Assets/GunsAreLoud/AudioLive/MasterMixer.mixer";
        internal const string LiveContractResource = "GunsAreLoud.Client.Assets.headphone-mixer-live-contract.txt";

        /// <summary>SEB's own FindMixerGroup prefix turns NonspatialBypass into this.</summary>
        internal const string SebBypassLookup = "World/Bypass";

        /// <summary>The same group on the variant, the only path that contains it.</summary>
        internal const string VariantBypassLookup = "GAL Passive/Bypass";

        /// <summary>The variant of SEB's live mixer; it renames NonspatialBypass to Bypass and adds groups.</summary>
        internal static readonly HeadphoneMixerVariant LiveVariant = new HeadphoneMixerVariant
        {
            Name = "live for " + SebIds.DisplayName,
            ResourceName = LiveResourceName,
            AssetPath = LiveAssetPath,
            ContractResource = LiveContractResource,
            ContractParameters = 132,
            BypassGroup = "Bypass",
            Routes = GunshotContrastMixerRouteTable.LiveRoutes,
            StockAliases = new Dictionary<string, string>(StringComparer.Ordinal) { { "NonspatialBypass", "Bypass" } },
            // SEB chains its live mixer into the stock one and mirrors the
            // global controls itself.
            BridgeGlobalControls = false,
            OriginalBypassLookup = SebBypassLookup,
            CandidateBypassLookup = VariantBypassLookup,
            RejectOnLookupMismatch = true
        };

        // SEB lowers precipitation for a worn headset; in Realistic the passive
        // bus is that headset, so the cut would be a second one.
        private static readonly string[] NeutralParameters = { "PrecipitationInVolume", "PrecipitationOutVolume" };

        private ConfigEntry<float> _subscribedAmbient;
        private Action _settingsChanged;

        public string DisplayName => SebIds.DisplayName;
        public bool OwnsMaster => SebModule.Present;
        public bool PredictsInstall => SebModule.PredictsLiveMixerInstall;
        public string InstallBlocker => SebModule.IntegrationBlocker;
        public string VariantKey => SebModule.BundleSha256 ?? "";
        public HeadphoneMixerVariant Variant => LiveVariant;
        public AudioMixer LoadReplacement() => SebReflection.LoadLiveMixer();
        public bool TryInstall(AudioMixer variant) => SebReflection.TrySetLiveMixer(variant);
        public bool IsActive => SebReflection.IsLiveMixerActive;

        public float AmbientTrimDb =>
            SebReflection.AmbientTrimDb ?? SebModule.AmbientVolumeEntry?.Value ?? 0f;

        public string[] NeutralRouteParameters => NeutralParameters;

        public event Action SettingsChanged
        {
            add
            {
                _settingsChanged += value;
                ConfigEntry<float> entry = SebModule.AmbientVolumeEntry;
                if (entry != null && _subscribedAmbient == null)
                {
                    _subscribedAmbient = entry;
                    entry.SettingChanged += OnAmbientChanged;
                }
            }
            remove
            {
                _settingsChanged -= value;
                if (_settingsChanged == null && _subscribedAmbient != null)
                {
                    _subscribedAmbient.SettingChanged -= OnAmbientChanged;
                    _subscribedAmbient = null;
                }
            }
        }

        private void OnAmbientChanged(object sender, EventArgs args) => _settingsChanged?.Invoke();
    }
}
