using BepInEx.Configuration;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GunsAreLoud.Client.Configuration
{
    // Exact name and public fields are required by the installed F12 manager.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public bool? IsAdvanced;
        public int? Order;
        public System.Action<ConfigEntryBase> CustomDrawer;
        public bool? HideDefaultButton;
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
        internal AutomaticPitchedRoute AutomaticPitchedRoute;
        internal AutomaticTailMode AutomaticTailMode;
        internal float AutomaticReportOverlapShots;
        internal float AutomaticLateToleranceScale;
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
        internal readonly ConfigEntry<bool> HearingLossEnabled;
        internal readonly ConfigEntry<bool> RingingEnabled;
        internal readonly ConfigEntry<float> ShotImpact;
        internal readonly ConfigEntry<float> GunshotContrastDb;
        internal readonly ConfigEntry<AutomaticPitchedRoute> AutomaticPitchedRoute;
        internal readonly ConfigEntry<AutomaticTailMode> AutomaticTailMode;
        internal readonly ConfigEntry<float> AutomaticReportOverlapShots;
        internal readonly ConfigEntry<float> AutomaticLateTolerancePercent;
        internal readonly ConfigEntry<float> PitchedLayerSemitones;
        internal readonly ConfigEntry<float> PitchedLayerHighpassHz;
        internal readonly ConfigEntry<float> PitchedLayerLowpassHz;
        internal readonly ConfigEntry<float> PitchedLayerFadePercent;
        internal readonly ConfigEntry<float> AutomaticPitchedTailMs;
        internal readonly ConfigEntry<float> PitchedLayerGainDb;
        internal readonly ConfigEntry<float> LowEndNormalizationDb;
        internal readonly ConfigEntry<float> CartridgeContrastDb;
        internal readonly ConfigEntry<PitchedLayerOcclusionMode> PitchedLayerOcclusion;
        internal readonly ConfigEntry<float> PitchedLayerOccludedLowpassHz;
        internal readonly ConfigEntry<float> IndoorEmphasis;
        internal readonly ConfigEntry<float> HearingLossDuration, RingingDuration;
        internal readonly ConfigEntry<float> HearingTrauma;
        internal readonly ConfigEntry<float> Ringing;
        internal readonly ConfigEntry<float> EarDifference;
        internal readonly ConfigEntry<HeadphonesFitPreset> HeadphonesFit;
        internal readonly ConfigEntry<HeadphoneMode> HeadphoneMode;
        internal readonly ConfigEntry<bool> PerformanceSummaryLog;
        internal readonly ConfigEntry<bool> DiagnosticShotLog;

        internal readonly ConfigEntry<float> BlastHearingStrength, BlastRingingStrength, BlastHearingDuration,
            BlastRingingDuration, BlastSevereDuration, BlastRadius, BlastIndoorScale;
        private TuningSnapshot _publishedTuning;

        /// <summary>
        /// Whether muffling can happen at all: the General toggle and the detailed
        /// intensity have to agree, and either alone switches the effect off.
        /// </summary>
        internal bool HearingLossActive =>
            HearingLossEnabled.Value && IsResponseEnabled(NormalizeResponseControl(HearingTrauma.Value));

        internal bool RingingActive =>
            RingingEnabled.Value && IsResponseEnabled(NormalizeResponseControl(Ringing.Value));

        private static ConfigEntry<float> BlastSetting(ConfigFile config, string name, float value, float min, float max, string description) =>
            Bind(config, "03. Explosions", name, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));

        internal ModConfig(ConfigFile config)
        {
            Source = config;
            BlastHearingStrength = BlastSetting(config, "Hearing Loss Strength, %", 100f, 0f, 200f, "Explosion hearing-loss strength. Zero disables it independently of ringing and duration.");
            BlastRingingStrength = BlastSetting(config, "Ringing Strength, %", 100f, 0f, 200f, "Explosion ringing level. Zero disables ringing independently of hearing loss.");
            BlastHearingDuration = BlastSetting(config, "Hearing Loss Duration, s", 45f, 0f, 600f, "Base duration of ordinary blast hearing loss. Distant blasts recover sooner.");
            BlastRingingDuration = BlastSetting(config, "Ringing Duration, s", 90f, 0f, 600f, "Base ringing duration. Zero disables ringing, including close blasts.");
            BlastSevereDuration = BlastSetting(config, "Close Blast Hearing Loss Duration, s", 180f, 0f, 600f, "Near-total hearing loss at maximum exposure. Recovery occupies the final 50 percent. Zero disables this severe phase.");
            BlastRadius = BlastSetting(config, "Outdoor Close Blast Radius, m", 5f, .5f, 20f, "Unprotected maximum-exposure radius. Outside it, exposure falls with squared distance. Headsets reduce exposure.");
            BlastIndoorScale = BlastSetting(config, "Indoor Radius Multiplier", 3f, 1f, 4f, "Indoor radius multiplier. Indoor exposure falls inversely with distance instead of squared distance.");

            Enabled = Bind(config,
                "01. General",
                "Enabled",
                true,
                "Master switch. Turn it off to restore EFT's stock audio path immediately.");

            Preset = Bind(config,
                "01. General",
                "Preset",
                LoudnessPreset.Balanced,
                "Balanced keeps the effect restrained; Loud makes gunfire clearly dominant; Punishing gives shots the strongest and longest impact. The controls below fine-tune the selected baseline.");

            ShotImpact = Percent(
                config,
                "02. Gunshots",
                "Gunshot Impact",
                160f,
                "Strength of the mod-added gunshot weight and caliber-dependent punch. This does not replace EFT's original weapon recording.");

            GunshotContrastDb = Bind(config,
                "02. Gunshots", "Gunshot Contrast, dB", 8f,
                new ConfigDescription(
                    "Attenuates the environment, footsteps, character speech, and other approved non-gun sounds while leaving gunshots unchanged. " +
                    "0 dB restores the original balance; 8 dB is the tuned default; 18 dB is heavy attenuation. Useful cues also become quieter. " +
                    "Music, UI, voice chat, and EFT's headset processing are not changed by this control.",
                    new AcceptableValueRange<float>(0f, 18f)));

            AutomaticPitchedRoute = Bind(config,
                "04. Low-level & debug",
                "Automatic Weapon Route",
                Configuration.AutomaticPitchedRoute.CachedReport,
                Advanced(
                    "CachedReport is the supported route: when the weapon is equipped it prepares one authored body interval plus its recorded tail, then plays each round through EFT's Gunshots mixer, with the calibrated level correction applied. " +
                    "BuiltInDSP is the legacy route, kept for comparison only. It costs less because it adds the low end inside the weapon's own audio callback instead of playing a copy, but it has its own filter response rather than the original recording's, gives no recorded tail per round, reserves 2 MiB per source, and level correction between weapons is switched off for it."));

            AutomaticTailMode = Bind(config,
                "04. Low-level & debug",
                "Automatic Report Shape",
                Configuration.AutomaticTailMode.FullReportPerShot,
                Advanced(
                    "FullReportPerShot gives every bullet its processed body and recorded tail, including the first shot of a burst. TailAfterBurst uses a short body with optional synthetic decay and plays the recorded tail only when the trigger is released. Applies to CachedReport."));

            AutomaticReportOverlapShots = Bind(config,
                "04. Low-level & debug",
                "Automatic Report Overlap, shots",
                6f,
                new ConfigDescription(
                    "How many rounds of automatic fire one per-bullet report copy may still be sounding over. " +
                    "A pitched-down full report lasts several seconds, so at a high rate of fire every bullet would " +
                    "otherwise leave dozens of copies playing at once. When a later round follows, an earlier copy fades " +
                    "out within this many fire intervals of its own start. A single shot and the last round of a burst " +
                    "have no successor and keep their full recorded tail. " +
                    "Higher values restore longer overlap during a burst at a proportional CPU cost.",
                    new AcceptableValueRange<float>(1f, 32f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            AutomaticLateTolerancePercent = Bind(config,
                "04. Low-level & debug",
                "Late Report Tolerance, %",
                100f,
                new ConfigDescription(
                    "How far behind its own round an added report copy may still be played before it is skipped instead. " +
                    "100% is one audio buffer or a quarter of the fire interval, whichever is shorter — about 21 ms at a 1024-sample buffer. " +
                    "Raise it if bursts sound thin on a machine with long frames: more copies are kept, but a kept copy lands further behind its round and can be heard as a double hit. " +
                    "Lower it for the opposite trade. The performance summary reports the outcome as autoBeats=on time/late/skipped/collapsed/early.",
                    new AcceptableValueRange<float>(25f, 400f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PitchedLayerSemitones = Bind(config,
                "04. Low-level & debug",
                "Pitch Reduction, semitones",
                12f,
                new ConfigDescription(
                    "Pitch reduction applied to the added copy. 12 semitones is one octave.",
                    new AcceptableValueRange<float>(1f, 24f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            LowEndNormalizationDb = Bind(config,
                "04. Low-level & debug", "Low-End Normalization, dB",
                Converted(config, "Low-End Normalization, %", LowEndNormalizationSpanDb,
                    0f, 1.5f * LowEndNormalizationSpanDb, LowEndNormalizationSpanDb),
                new ConfigDescription(
                    "How far the calibrated body correction may move one weapon's added low end towards the common target. " +
                    "0 dB preserves the recorded level differences; 12 dB is the calibrated setting; above it the correction is deliberately exaggerated. " +
                    "An attenuated body may be cut further than this — twice as far for pistols — and the recorded decay is measured separately and can recover by at most 6 dB relative to an attenuated body, never above its native level. " +
                    "Pitch and filter changes are included in the measurement; user gain, fade, suppression, and occlusion remain outside it. " +
                    "Applies to new copies after analysis is ready; BuiltInDSP is excluded.",
                    new AcceptableValueRange<float>(0f, 1.5f * LowEndNormalizationSpanDb),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            CartridgeContrastDb = Bind(config,
                "04. Low-level & debug", "Cartridge Contrast, dB",
                Converted(config, "Cartridge Contrast, %", CartridgeContrastSpanDb,
                    0f, 3f * CartridgeContrastSpanDb, 2f * CartridgeContrastSpanDb),
                new ConfigDescription(
                    "How far apart cartridge families sit in the added low-end layer, not in the complete gunshot. " +
                    "This is the level of an intermediate rifle cartridge above 9×19: 0 dB removes the weighting, 6 dB is the tuned default, 9 dB is the maximum. " +
                    "Recording differences can still dominate when normalization is partial or limited.",
                    new AcceptableValueRange<float>(0f, 3f * CartridgeContrastSpanDb),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PitchedLayerLowpassHz = Bind(config,
                "04. Low-level & debug",
                "Upper Cutoff (Low-Pass), Hz",
                2000f,
                new ConfigDescription(
                    "Removes frequencies above this point from the added copy. Lower values sound darker; higher values retain more of the recording's original character.",
                    new AcceptableValueRange<float>(80f, 3000f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PitchedLayerHighpassHz = Bind(config,
                "04. Low-level & debug",
                "Lower Cutoff (High-Pass), Hz",
                10f,
                new ConfigDescription(
                    "Removes frequencies below this point from the added copy. Use it to control subsonic energy, rumble, and DC.",
                    new AcceptableValueRange<float>(10f, 300f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PitchedLayerFadePercent = Bind(config,
                "04. Low-level & debug",
                "Fade-Out Portion, %",
                50f,
                new ConfigDescription(
                    "Portion of the copied report covered by its equal-power fade-out. This shapes the ending without changing clip length: 5% fades only the end; 100% fades almost from the start.",
                    new AcceptableValueRange<float>(5f, 100f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            AutomaticPitchedTailMs = Bind(config,
                "04. Low-level & debug",
                "Automatic Fallback Decay, ms",
                30f,
                new ConfigDescription(
                    "Synthetic decay added to the short authored body in TailAfterBurst or while a complete cached report is unavailable. FullReportPerShot uses EFT's recorded tail instead. 0 disables the synthetic decay.",
                    new AcceptableValueRange<float>(0f, 600f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PitchedLayerGainDb = Bind(config,
                "04. Low-level & debug",
                "Copy Gain, dB",
                20f,
                new ConfigDescription(
                    "Additional post-filter gain for the added copy, applied on top of Gunshot Impact. This is a positive-gain control and can reduce headroom at high settings.",
                    new AcceptableValueRange<float>(0f, 30f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            PitchedLayerOcclusion = Bind(config,
                "04. Low-level & debug",
                "Copy Occlusion",
                PitchedLayerOcclusionMode.Inherit,
                Advanced(
                    "Ignore leaves the added copy unobstructed. Inherit follows EFT's source occlusion. Enhanced applies a stronger gain reduction and closes the upper cutoff faster."));

            PitchedLayerOccludedLowpassHz = Bind(config,
                "04. Low-level & debug",
                "Fully Occluded Low-Pass, Hz",
                500f,
                new ConfigDescription(
                    "Upper cutoff approached by the added copy at full EFT occlusion. Applies to Inherit and Enhanced.",
                    new AcceptableValueRange<float>(50f, 1000f),
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            IndoorEmphasis = Percent(
                config,
                "02. Gunshots",
                "Indoor Emphasis",
                100f,
                "Strength of EFT's existing Meta XR early reflections and room tail, plus the additional hearing dose caused by indoor reflections. Applies only indoors.");

            HearingTrauma = Percent(
                config,
                "02. Gunshots",
                "Hearing Loss Intensity, %",
                100f,
                "Intensity of post-shot volume reduction and high-frequency loss. Recovery duration is controlled separately.");

            Ringing = Percent(
                config,
                "02. Gunshots",
                "Ringing Intensity, %",
                100f,
                "Level of post-shot ringing. Recovery duration is controlled separately. 0% removes tinnitus without weakening the gunshot or temporary hearing loss.");

            HearingLossDuration = Bind(config, "02. Gunshots", "Hearing Loss Duration, %", 100f,
                new ConfigDescription("Recovery duration multiplier for accumulated shot exposure. 100% uses the preset baseline; independent of intensity.", new AcceptableValueRange<float>(5f, 400f)));
            RingingDuration = Bind(config, "02. Gunshots", "Ringing Duration, %", 100f,
                new ConfigDescription("Ringing duration multiplier for accumulated shot exposure. 100% uses the preset baseline; independent of ringing level.", new AcceptableValueRange<float>(5f, 400f)));

            EarDifference = Percent(
                config,
                "02. Gunshots",
                "Left/Right Ear Difference",
                140f,
                "Scales the hearing-dose difference between shoulder stances. Rifles expose the muzzle-side ear more strongly; pistols remain nearly symmetrical.");

            HearingLossEnabled = Bind(config,
                "01. General",
                "Hearing Loss",
                true,
                "Whether your own gunfire and nearby explosions muffle your hearing for a while. " +
                "Off removes the muffling everywhere without touching ringing, the gunshots themselves, or headset protection. " +
                "The intensity and recovery of the muffling are set under Gunshots and Explosions.");

            RingingEnabled = Bind(config,
                "01. General",
                "Ringing",
                true,
                "Whether loud events leave a ringing tone. " +
                "Off removes the tone everywhere without touching the hearing loss, the gunshots themselves, or headset protection. " +
                "Its level and duration are set under Gunshots and Explosions.");

            HeadphoneMode = Bind(config,
                "01. General",
                "Headset Processing",
                Configuration.HeadphoneMode.Realistic,
                "Vanilla uses EFT's original active-headset processing. Realistic combines frequency-dependent passive isolation with a shared microphone/electronics path. " +
                "Unsupported headset profiles or incomplete routing fall back entirely to Vanilla and log the reason. Other Guns Are Loud effects remain independent.");

            HeadphonesFit = Bind(config,
                "01. General",
                "Headset Fit",
                HeadphonesFitPreset.Tight,
                "Loose represents a compromised seal; Normal uses the baseline protection estimate; Tight represents a good seal. This setting affects the gameplay hearing-dose model, not the Realistic headset filter curve.");

            Bind(config, "01. General", "Headset Diagnostics", "Live status (not a setting)",
                new ConfigDescription("Live native DSP and mixer connection checks. Counters measure processing calls, not perceived sound quality.", null,
                    new ConfigurationManagerAttributes { CustomDrawer = Audio.HeadphoneDiagnostics.Draw, HideDefaultButton = true }));

            PerformanceSummaryLog = Bind(config,
                "04. Low-level & debug",
                "Performance Summary Log",
                false,
                Advanced(
                    "Writes one bounded performance summary every 10 seconds to GunsAreLoud.Diagnostics.log in the mod's own plugin folder. It does not enable per-shot probes."));

            DiagnosticShotLog = Bind(config,
                "04. Low-level & debug",
                "Log Every Local Shot",
                false,
                Advanced(
                    "Writes rate-limited per-shot, routing, normalization, and listener-band diagnostics to GunsAreLoud.Diagnostics.log in the mod's own plugin folder. Enable only while collecting evidence. This switch always resets to off at the next client start."));
            // Detailed probes are session-scoped. A persisted F12 value from a
            // previous run must be disabled before any runtime handlers attach.
            DiagnosticShotLog.Value = false;
            // Settings that no longer exist. Binding and immediately removing them
            // is the same move the section migration makes, and it takes the stale
            // line out of an existing configuration file instead of leaving it to
            // be read back as an orphan for the rest of the mod's life.
            Discard<float>(config, "Legacy Indoor Headset Damping",
                "04. Low-level & debug", "10. Sound and Hearing");
            Discard<bool>(config, "Disable Components On Other Sounds",
                "04. Low-level & debug", "90. Diagnostics");
            Discard<bool>(config, "Soft Output Limiter",
                "04. Low-level & debug", "90. Diagnostics");
            // The original-band method and everything that served it are gone;
            // the pitched copy is the only low-end path now.
            Discard<string>(config, "Low-End Method",
                "04. Low-level & debug", "20. Low-End Layer");
            // This manager preserves ConfigFile enumeration order for categories.
            // Reinsert the same entry objects via the supported ICollection interface.
            var ordered = config.OrderBy(pair => pair.Key.Section, System.StringComparer.Ordinal).ToArray();
            config.Clear();
            foreach (var entry in ordered)
                ((ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)config).Add(entry);

            _publishedTuning = BuildTuning();
            Source.SettingChanged += PublishTuning;
        }

        // A preset or a slider drag raises SettingChanged once per entry, and the
        // manager writes several entries in the same frame. Invalidate here and
        // rebuild once, on the next reader, instead of once per entry.
        private void PublishTuning(object sender, SettingChangedEventArgs args) =>
            Volatile.Write(ref _publishedTuning, null);

        private static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T value, string description) =>
            Bind(config, section, key, value, new ConfigDescription(description));

        private static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T value, ConfigDescription description)
        {
            string oldSection = section, oldKey = key;
            if (section == "02. Gunshots") oldSection = "10. Sound and Hearing";
            else if (section == "03. Explosions") oldSection = "15. Explosions";
            else if (section == "04. Low-level & debug") oldSection =
                (key == "Log Every Local Shot" || key == "Performance Summary Log")
                    ? "90. Diagnostics" : "20. Low-End Layer";
            else if (key == "Headset Processing" || key == "Headset Fit") oldSection = "10. Sound and Hearing";
            if (key == "Hearing Loss Intensity, %") oldKey = "Temporary Hearing Loss";
            if (key == "Ringing Intensity, %") oldKey = "Tinnitus";
            if (key != "Hearing Loss Duration, %" && key != "Ringing Duration, %" && (oldSection != section || oldKey != key))
            {
                var old = config.Bind(oldSection, oldKey, value, description);
                value = old.Value;
                config.Remove(old.Definition);
            }
            var tags = description.Tags.Concat(new object[] { new ConfigurationManagerAttributes {
                IsAdvanced = section == "04. Low-level & debug", Order = SettingOrder(key)
            } }).ToArray();
            description = new ConfigDescription(description.Description, description.AcceptableValues, tags);
            // Existing destination values win on later loads; old values are only defaults.
            return config.Bind(section, key, value, description);
        }

        private static int SettingOrder(string key)
        {
            string[] keys = {
                "Enabled", "Preset", "Hearing Loss", "Ringing",
                "Headset Processing", "Headset Fit", "Headset Diagnostics",
                "Gunshot Impact", "Gunshot Contrast, dB", "Indoor Emphasis",
                "Hearing Loss Intensity, %", "Hearing Loss Duration, %",
                "Ringing Intensity, %", "Ringing Duration, %", "Left/Right Ear Difference",
                "Hearing Loss Strength, %", "Hearing Loss Duration, s",
                "Ringing Strength, %", "Ringing Duration, s", "Close Blast Hearing Loss Duration, s",
                "Outdoor Close Blast Radius, m", "Indoor Radius Multiplier",
                "Automatic Weapon Route", "Automatic Report Shape",
                "Automatic Report Overlap, shots", "Late Report Tolerance, %",
                "Pitch Reduction, semitones", "Lower Cutoff (High-Pass), Hz", "Upper Cutoff (Low-Pass), Hz",
                "Copy Gain, dB", "Low-End Normalization, dB", "Cartridge Contrast, dB",
                "Fade-Out Portion, %", "Automatic Fallback Decay, ms", "Copy Occlusion",
                "Fully Occluded Low-Pass, Hz",
                "Performance Summary Log", "Log Every Local Shot"
            };
            int index = System.Array.IndexOf(keys, key);
            return index < 0 ? 0 : 1000 - index; // Installed manager sorts descending.
        }

        internal TuningSnapshot GetTuning()
        {
            TuningSnapshot tuning = Volatile.Read(ref _publishedTuning);
            if (tuning != null) return tuning;
            tuning = BuildTuning();
            Volatile.Write(ref _publishedTuning, tuning);
            return tuning;
        }

        private TuningSnapshot BuildTuning()
        {
            ProfileValues profile = GetProfile(Preset.Value);
            float impact = Mathf.Clamp(ShotImpact.Value / 100f, 0f, 2f);
            float indoor = Mathf.Clamp(IndoorEmphasis.Value / 100f, 0f, 2f);
            // The General toggles gate the whole effect: with one off, nothing
            // downstream sees a dose, a target or a duration for it, whatever the
            // detailed controls say.
            float trauma = HearingLossEnabled.Value ? NormalizeResponseControl(HearingTrauma.Value) : 0f;
            float ringing = RingingEnabled.Value ? NormalizeResponseControl(Ringing.Value) : 0f;
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
                HearingLossDurationScale = HearingLossDuration.Value / 100f,
                TinnitusEnabled = IsResponseEnabled(ringing),
                TinnitusFrequencyHz = 6200f,
                TinnitusMaximumLevel = profile.TinnitusLevel * ringing,
                TinnitusThreshold = 0.08f,
                TinnitusPitchSpreadHz = 37f,
                TinnitusDurationScale = RingingDuration.Value / 100f,
                HeadphonesFitOffsetDb = GetHeadphonesFitOffsetDb(HeadphonesFit.Value),
                HeadphoneMode = HeadphoneMode.Value,
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
                AutomaticPitchedRoute = this.AutomaticPitchedRoute.Value,
                AutomaticTailMode = this.AutomaticTailMode.Value,
                AutomaticReportOverlapShots = AutomaticReportOverlapShots.Value,
                AutomaticLateToleranceScale = AutomaticLateTolerancePercent.Value / 100f,
                PitchedLayerSemitones = PitchedLayerSemitones.Value,
                PitchedLayerHighpassHz = PitchedLayerHighpassHz.Value,
                PitchedLayerLowpassHz = PitchedLayerLowpassHz.Value,
                PitchedLayerFadePercent = PitchedLayerFadePercent.Value,
                AutomaticPitchedTailSeconds = Mathf.Clamp(
                    AutomaticPitchedTailMs.Value / 1000f,
                    0f,
                    0.6f),
                PitchedLayerGainDb = PitchedLayerGainDb.Value,
                LowEndNormalizationPercent = LowEndNormalizationDb.Value * 100f / LowEndNormalizationSpanDb,
                CaliberContrastPercent = CartridgeContrastDb.Value * 100f / CartridgeContrastSpanDb,
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
            return Bind(config,
                section,
                key,
                value,
                new ConfigDescription(description, new AcceptableValueRange<float>(0f, 200f)));
        }

        /// <summary>
        /// The decibel span one hundred percent of the old control stood for.
        /// Both settings were already linear in decibels underneath; the percent
        /// only hid which decibels they were.
        /// </summary>
        internal const float LowEndNormalizationSpanDb = 12f;

        /// <summary>An intermediate rifle cartridge above 9x19, at one hundred percent.</summary>
        internal const float CartridgeContrastSpanDb = 3f;

        /// <summary>
        /// The starting value for a control that used to be a percentage: the
        /// converted setting from an existing configuration file, or the shipped
        /// default when the file has no such line. The old line is taken out
        /// either way, so the conversion happens exactly once.
        /// </summary>
        private static float Converted(
            ConfigFile config, string legacyKey, float spanDb,
            float minimumDb, float maximumDb, float defaultDb)
        {
            float percent = float.NaN;
            foreach (string section in new[] { "04. Low-level & debug", "20. Low-End Layer" })
            {
                var definition = new ConfigDefinition(section, legacyKey);
                ConfigEntry<float> legacy = config.Bind(definition, float.NaN);
                if (!float.IsNaN(legacy.Value)) percent = legacy.Value;
                config.Remove(definition);
            }
            return float.IsNaN(percent)
                ? defaultDb
                : Mathf.Clamp(percent * spanDb / 100f, minimumDb, maximumDb);
        }

        /// <summary>
        /// Drops a setting this version no longer has from the configuration file.
        /// </summary>
        private static void Discard<T>(ConfigFile config, string key, params string[] sections)
        {
            foreach (string section in sections)
            {
                var definition = new ConfigDefinition(section, key);
                config.Bind(definition, default(T));
                config.Remove(definition);
            }
        }

        private static ConfigDescription Advanced(string description)
        {
            return new ConfigDescription(
                description,
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true });
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
