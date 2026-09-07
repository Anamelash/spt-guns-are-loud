using BepInEx.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Configuration
{
    // Optional Configuration Manager consumes tag members through reflection.
    internal sealed class LegacyConfigurationManagerAttributes
    {
        public bool Browsable => false;
    }

    // Optional Configuration Manager consumes tag members through reflection.
    internal sealed class AdvancedConfigurationManagerAttributes
    {
        public bool IsAdvanced => true;
    }

    internal enum LoudnessPreset
    {
        Balanced,
        Loud,
        Punishing
    }

    internal enum HeadphonesFitPreset
    {
        Loose,
        Normal,
        Tight
    }

    internal enum HeadphoneMode
    {
        Vanilla,
        Realistic
    }

    internal enum GunshotLowEndMode
    {
        OriginalBand,
        PitchedCopy
    }

    internal enum PitchedLayerOcclusionMode
    {
        Ignore,
        Inherit,
        Enhanced
    }

    internal enum AutomaticPitchedRoute
    {
        BuiltInDSP,
        CachedReport
    }

    internal enum AutomaticTailMode
    {
        FullReportPerShot,
        TailAfterBurst
    }

    internal sealed class TuningSnapshot
    {
        internal bool ExposureEnabled;
        internal float MasterSeverityScale;
        internal float IndoorDoseMultiplier;
        internal float MaximumDose;
        internal float FastRecoverySeconds;
        internal float SlowRecoverySeconds;
        internal float EnergyInfluence;
        internal float MaximumEnergyCorrection;
        internal float ModLoudnessInfluence;
        internal float SuppressedMultiplier;
        internal float RimfireSeverity;
        internal float PistolSeverity;
        internal float IntermediateSeverity;
        internal float FullPowerSeverity;
        internal float ShotgunSeverity;
        internal float HeavySeverity;
        internal float UnknownCaliberSeverity;
        internal bool EarModelEnabled;
        internal float LongGunAsymmetry;
        internal float CompactGunAsymmetry;
        internal float PistolAsymmetry;
        internal float MaximumAsymmetry;
        internal bool HearingLossEnabled;
        internal float MaximumAttenuationDb;
        internal float MinimumLowpassHz;
        internal float HearingResponseCurve;
        internal float HearingLossDurationScale;
        internal bool TinnitusEnabled;
        internal float TinnitusFrequencyHz;
        internal float TinnitusMaximumLevel;
        internal float TinnitusThreshold;
        internal float TinnitusPitchSpreadHz;
        internal float TinnitusDurationScale;
        internal float HeadphonesFitOffsetDb;
        internal HeadphoneMode HeadphoneMode;
        internal float IndoorHeadphonesDampingPercent;
        internal float DefaultHeadphonesProtectionDb;
        internal float MinimumHeadphonesProtectionDb;
        internal float MaximumHeadphonesProtectionDb;
        internal float IndoorHeadphonesPenaltyDb;
        internal float FullPowerHeadphonesPenaltyDb;
        internal float ShotgunHeadphonesPenaltyDb;
        internal float HeavyHeadphonesPenaltyDb;
        internal float BaseDirectBoostDb;
        internal float CaliberBoostSpreadDb;
        internal float IndoorDirectBoostDb;
        internal float MaximumDirectBoostDb;
        internal float DirectBodyGain;
        internal GunshotLowEndMode LowEndMode;
        internal AutomaticPitchedRoute AutomaticPitchedRoute;
        internal AutomaticTailMode AutomaticTailMode;
        internal float PitchedLayerSemitones;
        internal float PitchedLayerHighpassHz;
        internal float PitchedLayerLowpassHz;
        internal float PitchedLayerFadePercent;
        internal float AutomaticPitchedTailSeconds;
        internal float PitchedLayerGainDb;
        internal float LowEndNormalizationPercent;
        internal float CaliberContrastPercent;
        internal PitchedLayerOcclusionMode PitchedLayerOcclusion;
        internal float PitchedLayerOccludedLowpassHz;
        internal float IndoorRoomStrength;
        internal float IndoorEarlyReflectionsSendDb;
        internal float IndoorReverbSendDb;
        internal float IndoorReverbReach;
    }

    internal sealed class ModConfig
    {
        internal readonly ConfigFile Source;
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<LoudnessPreset> Preset;
        internal readonly ConfigEntry<float> ShotImpact;
        internal readonly ConfigEntry<float> GunshotContrastDb;
        internal readonly ConfigEntry<GunshotLowEndMode> LowEndMode;
        internal readonly ConfigEntry<AutomaticPitchedRoute> AutomaticPitchedRoute;
        internal readonly ConfigEntry<AutomaticTailMode> AutomaticTailMode;
        internal readonly ConfigEntry<float> PitchedLayerSemitones;
        internal readonly ConfigEntry<float> PitchedLayerHighpassHz;
        internal readonly ConfigEntry<float> PitchedLayerLowpassHz;
        internal readonly ConfigEntry<float> PitchedLayerFadePercent;
        internal readonly ConfigEntry<float> AutomaticPitchedTailMs;
        internal readonly ConfigEntry<float> PitchedLayerGainDb;
        internal readonly ConfigEntry<float> LowEndNormalizationPercent;
        internal readonly ConfigEntry<float> CaliberContrastPercent;
        internal readonly ConfigEntry<PitchedLayerOcclusionMode> PitchedLayerOcclusion;
        internal readonly ConfigEntry<float> PitchedLayerOccludedLowpassHz;
        internal readonly ConfigEntry<float> IndoorEmphasis;
        internal readonly ConfigEntry<float> HearingTrauma;
        internal readonly ConfigEntry<float> Ringing;
        internal readonly ConfigEntry<float> EarDifference;
        internal readonly ConfigEntry<HeadphonesFitPreset> HeadphonesFit;
        internal readonly ConfigEntry<HeadphoneMode> HeadphoneMode;
        internal readonly ConfigEntry<float> IndoorHeadphonesDampingPercent;
        internal readonly ConfigEntry<bool> DiagnosticShotLog;

        internal ModConfig(ConfigFile config)
        {
            Source = config;
            Enabled = config.Bind(
                "01. General",
                "Enabled",
                true,
                "Master switch. Turn it off to restore EFT's stock audio path immediately.");

            Preset = config.Bind(
                "01. General",
                "Preset",
                LoudnessPreset.Balanced,
                "Balanced keeps the effect restrained; Loud makes gunfire clearly dominant; Punishing gives shots the strongest and longest impact. The controls below fine-tune the selected baseline.");

            ShotImpact = Percent(
                config,
                "10. Sound and Hearing",
                "Gunshot Impact",
                179.8122f,
                "Strength of the mod-added gunshot weight and caliber-dependent punch. This does not replace EFT's original weapon recording.");

            GunshotContrastDb = config.Bind(
                "10. Sound and Hearing", "Gunshot Contrast, dB", 6f,
                new ConfigDescription(
                    "Attenuates the environment, footsteps, character speech, and other approved non-gun sounds while leaving gunshots unchanged. " +
                    "0 dB restores the original balance; 6 dB is the tuned default; 18 dB is heavy attenuation. Useful cues also become quieter. " +
                    "Music, UI, voice chat, and EFT's headset processing are not changed by this control.",
                    new AcceptableValueRange<float>(0f, 18f)));

            LowEndMode = config.Bind(
                "20. Low-End Layer",
                "Low-End Method",
                GunshotLowEndMode.PitchedCopy,
                Advanced(
                    "OriginalBand reinforces a short low-frequency band inside the live EFT report. PitchedCopy adds a synchronized, pitch-shifted and band-limited copy of EFT's own recording."));

            AutomaticPitchedRoute = config.Bind(
                "20. Low-End Layer",
                "Automatic Weapon Route",
                Configuration.AutomaticPitchedRoute.CachedReport,
                Advanced(
                    "CachedReport prepares one authored body interval plus its recorded tail when the weapon is equipped, then plays it through EFT's Gunshots mixer. BuiltInDSP is a diagnostic comparison path inside the live automatic source."));

            AutomaticTailMode = config.Bind(
                "20. Low-End Layer",
                "Automatic Report Shape",
                Configuration.AutomaticTailMode.FullReportPerShot,
                Advanced(
                    "FullReportPerShot gives every bullet its processed body and recorded tail, including the first shot of a burst. TailAfterBurst uses a short body with optional synthetic decay and plays the recorded tail only when the trigger is released. Applies to CachedReport."));

            PitchedLayerSemitones = config.Bind(
                "20. Low-End Layer",
                "Pitch Reduction, semitones",
                12.01408f,
                new ConfigDescription(
                    "Pitch reduction applied to the added copy. 12 semitones is one octave. Applies only to PitchedCopy.",
                    new AcceptableValueRange<float>(1f, 24f),
                    new AdvancedConfigurationManagerAttributes()));

            LowEndNormalizationPercent = config.Bind(
                "20. Low-End Layer", "Low-End Normalization, %", 100f,
                new ConfigDescription(
                    "0% preserves the recorded level differences; 100% applies the calibrated body correction, capped at ±12 dB, before cartridge weighting. " +
                    "The recorded decay is measured separately and can recover by at most 6 dB relative to an attenuated body, never above its native level. " +
                    "Values above 100% deliberately exaggerate the body correction, up to ±18 dB at 150%. Pitch and filter changes are included in the measurement; user gain, fade, suppression, and occlusion remain outside it. " +
                    "Applies to new PitchedCopy voices after analysis is ready; BuiltInDSP is excluded.",
                    new AcceptableValueRange<float>(0f, 150f),
                    new AdvancedConfigurationManagerAttributes()));

            CaliberContrastPercent = config.Bind(
                "20. Low-End Layer", "Cartridge Contrast, %", 200f,
                new ConfigDescription(
                    "Controls the cartridge-family level difference in the added low-end layer, not the complete gunshot. " +
                    "0% removes the weighting; 100% places an intermediate rifle cartridge about 3 dB above 9×19; 200% gives about 6 dB; 300% gives about 9 dB. " +
                    "Recording differences can still dominate when normalization is partial or limited.",
                    new AcceptableValueRange<float>(0f, 300f),
                    new AdvancedConfigurationManagerAttributes()));

            PitchedLayerLowpassHz = config.Bind(
                "20. Low-End Layer",
                "Upper Cutoff (Low-Pass), Hz",
                2000f,
                new ConfigDescription(
                    "Removes frequencies above this point from PitchedCopy. Lower values sound darker; higher values retain more of the recording's original character.",
                    new AcceptableValueRange<float>(80f, 3000f),
                    new AdvancedConfigurationManagerAttributes()));

            PitchedLayerHighpassHz = config.Bind(
                "20. Low-End Layer",
                "Lower Cutoff (High-Pass), Hz",
                10.00001f,
                new ConfigDescription(
                    "Removes frequencies below this point from PitchedCopy. Use it to control subsonic energy, rumble, and DC.",
                    new AcceptableValueRange<float>(10f, 300f),
                    new AdvancedConfigurationManagerAttributes()));

            PitchedLayerFadePercent = config.Bind(
                "20. Low-End Layer",
                "Fade-Out Portion, %",
                50.93896f,
                new ConfigDescription(
                    "Portion of the copied report covered by its equal-power fade-out. This shapes the ending without changing clip length: 5% fades only the end; 100% fades almost from the start.",
                    new AcceptableValueRange<float>(5f, 100f),
                    new AdvancedConfigurationManagerAttributes()));

            AutomaticPitchedTailMs = config.Bind(
                "20. Low-End Layer",
                "Automatic Fallback Decay, ms",
                400f,
                new ConfigDescription(
                    "Synthetic decay added to the short authored body in TailAfterBurst or while a complete cached report is unavailable. FullReportPerShot uses EFT's recorded tail instead. 0 disables the synthetic decay.",
                    new AcceptableValueRange<float>(0f, 600f),
                    new AdvancedConfigurationManagerAttributes()));

            PitchedLayerGainDb = config.Bind(
                "20. Low-End Layer",
                "Copy Gain, dB",
                20.28169f,
                new ConfigDescription(
                    "Additional post-filter gain for PitchedCopy, applied on top of Gunshot Impact. This is a positive-gain control and can reduce headroom at high settings.",
                    new AcceptableValueRange<float>(0f, 30f),
                    new AdvancedConfigurationManagerAttributes()));

            PitchedLayerOcclusion = config.Bind(
                "20. Low-End Layer",
                "Copy Occlusion",
                PitchedLayerOcclusionMode.Enhanced,
                Advanced(
                    "Ignore leaves the added copy unobstructed. Inherit follows EFT's source occlusion. Enhanced applies a stronger gain reduction and closes the upper cutoff faster."));

            PitchedLayerOccludedLowpassHz = config.Bind(
                "20. Low-End Layer",
                "Fully Occluded Low-Pass, Hz",
                500.4695f,
                new ConfigDescription(
                    "Upper cutoff approached by PitchedCopy at full EFT occlusion. Applies to Inherit and Enhanced.",
                    new AcceptableValueRange<float>(50f, 1000f),
                    new AdvancedConfigurationManagerAttributes()));

            IndoorEmphasis = Percent(
                config,
                "10. Sound and Hearing",
                "Indoor Emphasis",
                100f,
                "Strength of EFT's existing Meta XR early reflections and room tail, plus the additional hearing dose caused by indoor reflections. Applies only indoors.");

            HearingTrauma = Percent(
                config,
                "10. Sound and Hearing",
                "Temporary Hearing Loss",
                104.2253f,
                "Strength and recovery time of the temporary post-shot volume reduction and high-frequency loss.");

            Ringing = Percent(
                config,
                "10. Sound and Hearing",
                "Tinnitus",
                106.1033f,
                "Level and recovery time of post-shot ringing. 0% removes tinnitus without weakening the gunshot or temporary hearing loss.");

            EarDifference = Percent(
                config,
                "10. Sound and Hearing",
                "Left/Right Ear Difference",
                140.3756f,
                "Scales the hearing-dose difference between shoulder stances. Rifles expose the muzzle-side ear more strongly; pistols remain nearly symmetrical.");

            HeadphoneMode = config.Bind(
                "10. Sound and Hearing",
                "Headset Processing",
                Configuration.HeadphoneMode.Realistic,
                "Vanilla uses EFT's original active-headset processing. Realistic combines frequency-dependent passive isolation with a shared microphone/electronics path. " +
                "Unsupported headset profiles or incomplete routing fall back entirely to Vanilla and log the reason. Other Guns Are Loud effects remain independent.");

            HeadphonesFit = config.Bind(
                "10. Sound and Hearing",
                "Headset Fit",
                HeadphonesFitPreset.Tight,
                "Loose represents a compromised seal; Normal uses the baseline protection estimate; Tight represents a good seal. This setting affects the gameplay hearing-dose model, not the Realistic headset filter curve.");

            // Keep the original section/key so existing cfg values survive migration.
            // Configuration Manager recognizes the tag by reflection and hides it.
            IndoorHeadphonesDampingPercent = config.Bind(
                "10. Sound and Hearing", "Legacy Indoor Headset Damping", 100f,
                new ConfigDescription(
                    "Retained only for old configuration files. It is inactive: Vanilla keeps EFT's original headset path, while Realistic uses one complete physical profile without an extra body, tail, or room adjustment.",
                    new AcceptableValueRange<float>(0f, 200f),
                    new LegacyConfigurationManagerAttributes()));

            DiagnosticShotLog = config.Bind(
                "90. Diagnostics",
                "Log Every Local Shot",
                true,
                Advanced(
                    "Writes per-shot caliber, profile, left/right dose, low-end routing, normalization, listener-band, and performance diagnostics to the BepInEx log. Enable only when collecting evidence; it can produce a large log."));
        }

        internal TuningSnapshot GetTuning()
        {
            ProfileValues profile = GetProfile(Preset.Value);
            float impact = Mathf.Clamp(ShotImpact.Value / 100f, 0f, 2f);
            float indoor = Mathf.Clamp(IndoorEmphasis.Value / 100f, 0f, 2f);
            float trauma = NormalizeResponseControl(HearingTrauma.Value);
            float ringing = NormalizeResponseControl(Ringing.Value);
            float earDifference = Mathf.Clamp(EarDifference.Value / 100f, 0f, 2f);

            return new TuningSnapshot
            {
                ExposureEnabled = IsResponseEnabled(trauma) || IsResponseEnabled(ringing),
                MasterSeverityScale = profile.ExposureScale,
                IndoorDoseMultiplier = 1f + (profile.IndoorDoseMultiplier - 1f) * indoor,
                MaximumDose = 4f,
                FastRecoverySeconds = profile.FastRecoverySeconds,
                SlowRecoverySeconds = profile.SlowRecoverySeconds * 1.2f,
                EnergyInfluence = 0.18f,
                MaximumEnergyCorrection = 0.25f,
                ModLoudnessInfluence = 0.35f,
                SuppressedMultiplier = 0.38f,
                RimfireSeverity = 0.35f,
                PistolSeverity = 0.58f,
                IntermediateSeverity = 0.86f,
                FullPowerSeverity = 1.12f,
                ShotgunSeverity = 1.02f,
                HeavySeverity = 1.55f,
                UnknownCaliberSeverity = 0.75f,
                EarModelEnabled = earDifference > 0.001f,
                LongGunAsymmetry = Mathf.Min(0.25f, 0.12f * earDifference),
                CompactGunAsymmetry = Mathf.Min(0.2f, 0.06f * earDifference),
                PistolAsymmetry = Mathf.Min(0.1f, 0.015f * earDifference),
                MaximumAsymmetry = 0.25f,
                HearingLossEnabled = IsResponseEnabled(trauma),
                MaximumAttenuationDb = profile.MaximumAttenuationDb * trauma,
                MinimumLowpassHz = Mathf.Lerp(22000f, profile.MinimumLowpassHz, Mathf.Clamp01(trauma)),
                HearingResponseCurve = 0.58f,
                HearingLossDurationScale = ResponseDurationScale(trauma),
                TinnitusEnabled = IsResponseEnabled(ringing),
                TinnitusFrequencyHz = 6200f,
                TinnitusMaximumLevel = profile.TinnitusLevel * ringing,
                TinnitusThreshold = 0.08f,
                TinnitusPitchSpreadHz = 37f,
                TinnitusDurationScale = ResponseDurationScale(ringing),
                HeadphonesFitOffsetDb = GetHeadphonesFitOffsetDb(HeadphonesFit.Value),
                HeadphoneMode = HeadphoneMode.Value,
                IndoorHeadphonesDampingPercent = IndoorHeadphonesDampingPercent.Value,
                DefaultHeadphonesProtectionDb = 22f,
                MinimumHeadphonesProtectionDb = 10f,
                MaximumHeadphonesProtectionDb = 30f,
                IndoorHeadphonesPenaltyDb = 6f,
                FullPowerHeadphonesPenaltyDb = 2f,
                ShotgunHeadphonesPenaltyDb = 3f,
                HeavyHeadphonesPenaltyDb = 6f,
                BaseDirectBoostDb = profile.BaseDirectBoostDb * impact,
                CaliberBoostSpreadDb = profile.CaliberBoostSpreadDb * impact,
                IndoorDirectBoostDb = profile.IndoorDirectBoostDb * impact * indoor,
                MaximumDirectBoostDb = 6.5f,
                DirectBodyGain = Mathf.Clamp(profile.DirectBodyGain * impact, 0f, 0.5f),
                LowEndMode = this.LowEndMode.Value,
                AutomaticPitchedRoute = this.AutomaticPitchedRoute.Value,
                AutomaticTailMode = this.AutomaticTailMode.Value,
                PitchedLayerSemitones = PitchedLayerSemitones.Value,
                PitchedLayerHighpassHz = PitchedLayerHighpassHz.Value,
                PitchedLayerLowpassHz = PitchedLayerLowpassHz.Value,
                PitchedLayerFadePercent = PitchedLayerFadePercent.Value,
                AutomaticPitchedTailSeconds = Mathf.Clamp(
                    AutomaticPitchedTailMs.Value / 1000f,
                    0f,
                    0.6f),
                PitchedLayerGainDb = PitchedLayerGainDb.Value,
                LowEndNormalizationPercent = LowEndNormalizationPercent.Value,
                CaliberContrastPercent = CaliberContrastPercent.Value,
                PitchedLayerOcclusion = this.PitchedLayerOcclusion.Value,
                PitchedLayerOccludedLowpassHz = PitchedLayerOccludedLowpassHz.Value,
                IndoorRoomStrength = indoor,
                IndoorEarlyReflectionsSendDb = profile.IndoorEarlyReflectionsSendDb + 2f * Mathf.Max(0f, indoor - 1f),
                IndoorReverbSendDb = profile.IndoorReverbSendDb + 2f * Mathf.Max(0f, indoor - 1f),
                IndoorReverbReach = Mathf.Clamp01(profile.IndoorReverbReach + 0.15f * Mathf.Max(0f, indoor - 1f))
            };
        }

        private static ConfigEntry<float> Percent(
            ConfigFile config,
            string section,
            string key,
            float value,
            string description)
        {
            return config.Bind(
                section,
                key,
                value,
                new ConfigDescription(description, new AcceptableValueRange<float>(0f, 200f)));
        }

        private static ConfigDescription Advanced(string description)
        {
            return new ConfigDescription(
                description,
                null,
                new AdvancedConfigurationManagerAttributes());
        }

        private static float ResponseDurationScale(float control)
        {
            return 0.5f + 0.5f * Mathf.Clamp(control, 0f, 2f);
        }

        internal static float NormalizeResponseControl(float percent)
        {
            return Mathf.Clamp(percent / 100f, 0f, 2f);
        }

        internal static bool IsResponseEnabled(float normalizedControl)
        {
            return normalizedControl > 0.001f;
        }

        private static ProfileValues GetProfile(LoudnessPreset preset)
        {
            switch (preset)
            {
                case LoudnessPreset.Balanced:
                    return new ProfileValues(2f, 1f, 0.7f, 0.78f, 1.35f, 7f, 2800f, 1f, 4.5f, 0.014f, 0.08f, -1f, -4f, 0.35f);
                case LoudnessPreset.Punishing:
                    return new ProfileValues(4.4f, 2f, 1.7f, 1.18f, 1.75f, 14f, 900f, 1.4f, 10f, 0.03f, 0.24f, 3f, 0f, 0.75f);
                default:
                    return new ProfileValues(3.4f, 1.6f, 1.25f, 1f, 1.55f, 10f, 1500f, 1.2f, 7f, 0.022f, 0.16f, 1f, -2f, 0.55f);
            }
        }

        private static float GetHeadphonesFitOffsetDb(HeadphonesFitPreset preset)
        {
            switch (preset)
            {
                case HeadphonesFitPreset.Loose:
                    return -8f;
                case HeadphonesFitPreset.Tight:
                    return 3f;
                default:
                    return 0f;
            }
        }

        private readonly struct ProfileValues
        {
            internal readonly float BaseDirectBoostDb;
            internal readonly float CaliberBoostSpreadDb;
            internal readonly float IndoorDirectBoostDb;
            internal readonly float ExposureScale;
            internal readonly float IndoorDoseMultiplier;
            internal readonly float MaximumAttenuationDb;
            internal readonly float MinimumLowpassHz;
            internal readonly float FastRecoverySeconds;
            internal readonly float SlowRecoverySeconds;
            internal readonly float TinnitusLevel;
            internal readonly float DirectBodyGain;
            internal readonly float IndoorEarlyReflectionsSendDb;
            internal readonly float IndoorReverbSendDb;
            internal readonly float IndoorReverbReach;

            internal ProfileValues(
                float baseDirectBoostDb,
                float caliberBoostSpreadDb,
                float indoorDirectBoostDb,
                float exposureScale,
                float indoorDoseMultiplier,
                float maximumAttenuationDb,
                float minimumLowpassHz,
                float fastRecoverySeconds,
                float slowRecoverySeconds,
                float tinnitusLevel,
                float directBodyGain,
                float indoorEarlyReflectionsSendDb,
                float indoorReverbSendDb,
                float indoorReverbReach)
            {
                BaseDirectBoostDb = baseDirectBoostDb;
                CaliberBoostSpreadDb = caliberBoostSpreadDb;
                IndoorDirectBoostDb = indoorDirectBoostDb;
                ExposureScale = exposureScale;
                IndoorDoseMultiplier = indoorDoseMultiplier;
                MaximumAttenuationDb = maximumAttenuationDb;
                MinimumLowpassHz = minimumLowpassHz;
                FastRecoverySeconds = fastRecoverySeconds;
                SlowRecoverySeconds = slowRecoverySeconds;
                TinnitusLevel = tinnitusLevel;
                DirectBodyGain = directBodyGain;
                IndoorEarlyReflectionsSendDb = indoorEarlyReflectionsSendDb;
                IndoorReverbSendDb = indoorReverbSendDb;
                IndoorReverbReach = indoorReverbReach;
            }
        }
    }
}
