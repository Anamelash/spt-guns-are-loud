using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Patches;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class ExposureModelTests
    {
        private string _configPath;
        private ModConfig _config;
        private TuningSnapshot _tuning;

        [SetUp]
        public void SetUp()
        {
            _configPath = Path.Combine(
                Path.GetTempPath(),
                "GunsAreLoud.Tests." + Guid.NewGuid().ToString("N") + ".cfg");
            var configFile = new ConfigFile(_configPath, false)
            {
                SaveOnConfigSet = false
            };
            _config = new ModConfig(configFile);
            _tuning = _config.GetTuning();
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_configPath))
            {
                File.Delete(_configPath);
            }
        }

        [Test]
        public void RightShoulderLongGunExposesLeftEarMore()
        {
            ShotDescriptor shot = CreateShot();
            shot.IsLeftStance = false;
            shot.WeaponCategory = WeaponCategory.LongGun;

            ExposureResult result = ExposureModel.Calculate(shot, _tuning);

            Assert.That(result.LeftDose, Is.GreaterThan(result.RightDose));
        }

        [Test]
        public void LeftStanceMirrorsLongGunExposure()
        {
            ShotDescriptor rightStance = CreateShot();
            rightStance.IsLeftStance = false;
            rightStance.WeaponCategory = WeaponCategory.LongGun;
            ShotDescriptor leftStance = CreateShot();
            leftStance.IsLeftStance = true;
            leftStance.WeaponCategory = WeaponCategory.LongGun;

            ExposureResult right = ExposureModel.Calculate(rightStance, _tuning);
            ExposureResult left = ExposureModel.Calculate(leftStance, _tuning);

            Assert.That(left.LeftDose, Is.EqualTo(right.RightDose).Within(0.0001f));
            Assert.That(left.RightDose, Is.EqualTo(right.LeftDose).Within(0.0001f));
        }

        [Test]
        public void PistolIsMoreSymmetricThanLongGun()
        {
            ShotDescriptor pistol = CreateShot();
            pistol.WeaponCategory = WeaponCategory.Pistol;
            ShotDescriptor rifle = CreateShot();
            rifle.WeaponCategory = WeaponCategory.LongGun;

            ExposureResult pistolResult = ExposureModel.Calculate(pistol, _tuning);
            ExposureResult rifleResult = ExposureModel.Calculate(rifle, _tuning);

            Assert.That(pistolResult.EarAsymmetry, Is.LessThan(rifleResult.EarAsymmetry));
        }

        [Test]
        public void IndoorShotProducesMoreDoseThanOutdoorShot()
        {
            ShotDescriptor outdoor = CreateShot();
            outdoor.IsIndoor = false;
            ShotDescriptor indoor = CreateShot();
            indoor.IsIndoor = true;

            ExposureResult outdoorResult = ExposureModel.Calculate(outdoor, _tuning);
            ExposureResult indoorResult = ExposureModel.Calculate(indoor, _tuning);

            Assert.That(indoorResult.FinalSeverity, Is.GreaterThan(outdoorResult.FinalSeverity));
        }

        [Test]
        public void SuppressorAndHeadphonesReduceButDoNotEraseDose()
        {
            ShotDescriptor unprotected = CreateShot();
            ShotDescriptor protectedShot = CreateShot();
            protectedShot.IsSuppressed = true;
            protectedShot.HasActiveHeadphones = true;
            protectedShot.HeadphonesCompressorThresholdDb = -23f;

            ExposureResult open = ExposureModel.Calculate(unprotected, _tuning);
            ExposureResult protectedResult = ExposureModel.Calculate(protectedShot, _tuning);

            Assert.That(protectedResult.FinalSeverity, Is.GreaterThan(0f));
            Assert.That(protectedResult.FinalSeverity, Is.LessThan(open.FinalSeverity));
        }

        [Test]
        public void HeadphonesUseEquippedTemplateCompressorThreshold()
        {
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Normal;
            _tuning = _config.GetTuning();
            ShotDescriptor weakerTemplate = CreateShot();
            weakerTemplate.HasActiveHeadphones = true;
            weakerTemplate.HeadphonesCompressorThresholdDb = -20f;
            ShotDescriptor strongerTemplate = CreateShot();
            strongerTemplate.HasActiveHeadphones = true;
            strongerTemplate.HeadphonesCompressorThresholdDb = -25f;

            ExposureResult weaker = ExposureModel.Calculate(weakerTemplate, _tuning);
            ExposureResult stronger = ExposureModel.Calculate(strongerTemplate, _tuning);

            Assert.That(weaker.HeadphonesProtectionDb, Is.EqualTo(20f));
            Assert.That(stronger.HeadphonesProtectionDb, Is.EqualTo(25f));
            Assert.That(stronger.FinalSeverity, Is.LessThan(weaker.FinalSeverity));
        }

        [Test]
        public void NormalHeadphonesPreventSingleOutdoorIntermediateShotFromCrossingTinnitusThreshold()
        {
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Normal;
            _tuning = _config.GetTuning();
            ShotDescriptor shot = CreateShot();
            shot.HasActiveHeadphones = true;
            shot.HeadphonesCompressorThresholdDb = -23f;

            ExposureResult result = ExposureModel.Calculate(shot, _tuning);
            float tinnitusDoseThreshold = _tuning.MaximumDose * _tuning.TinnitusThreshold;

            Assert.That(result.FinalSeverity, Is.LessThan(tinnitusDoseThreshold));
        }

        [Test]
        public void IndoorReflectionsRetainMoreResidualExposureThanOutdoorShot()
        {
            ShotDescriptor outdoor = CreateShot();
            outdoor.HasActiveHeadphones = true;
            outdoor.HeadphonesCompressorThresholdDb = -23f;
            ShotDescriptor indoor = CreateShot();
            indoor.HasActiveHeadphones = true;
            indoor.HeadphonesCompressorThresholdDb = -23f;
            indoor.IsIndoor = true;

            ExposureResult outdoorResult = ExposureModel.Calculate(outdoor, _tuning);
            ExposureResult indoorResult = ExposureModel.Calculate(indoor, _tuning);

            Assert.That(indoorResult.FinalSeverity, Is.GreaterThan(outdoorResult.FinalSeverity));
            Assert.That(indoorResult.FinalSeverity, Is.GreaterThan(0f));
        }

        [Test]
        public void PoorHeadphonesFitProtectsLessThanNormalFit()
        {
            ShotDescriptor shot = CreateShot();
            shot.HasActiveHeadphones = true;
            shot.HeadphonesCompressorThresholdDb = -23f;
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Loose;
            ExposureResult poor = ExposureModel.Calculate(shot, _config.GetTuning());
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Normal;
            ExposureResult normal = ExposureModel.Calculate(shot, _config.GetTuning());

            Assert.That(poor.HeadphonesProtectionDb, Is.LessThan(normal.HeadphonesProtectionDb));
            Assert.That(poor.FinalSeverity, Is.GreaterThan(normal.FinalSeverity));
        }

        [Test]
        public void MissingTemplateThresholdUsesCalibratedFallback()
        {
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Normal;
            _tuning = _config.GetTuning();
            ShotDescriptor shot = CreateShot();
            shot.HasActiveHeadphones = true;
            shot.HeadphonesCompressorThresholdDb = 0f;

            ExposureResult result = ExposureModel.Calculate(shot, _tuning);

            Assert.That(result.HeadphonesProtectionDb, Is.EqualTo(_tuning.DefaultHeadphonesProtectionDb));
        }

        [Test]
        public void LargeCalibersRetainMoreLowFrequencyExposureThroughHeadphones()
        {
            ShotDescriptor intermediate = CreateShot();
            intermediate.HasActiveHeadphones = true;
            intermediate.HeadphonesCompressorThresholdDb = -23f;
            ShotDescriptor fullPower = CreateShot();
            fullPower.HasActiveHeadphones = true;
            fullPower.HeadphonesCompressorThresholdDb = -23f;
            fullPower.AmmoCaliber = "762x51";
            ShotDescriptor shotgun = CreateShot();
            shotgun.HasActiveHeadphones = true;
            shotgun.HeadphonesCompressorThresholdDb = -23f;
            shotgun.AmmoCaliber = "12g";
            ShotDescriptor heavy = CreateShot();
            heavy.HasActiveHeadphones = true;
            heavy.HeadphonesCompressorThresholdDb = -23f;
            heavy.AmmoCaliber = "127x99";

            float intermediateDb = ExposureModel.Calculate(intermediate, _tuning).HeadphonesProtectionDb;
            float fullPowerDb = ExposureModel.Calculate(fullPower, _tuning).HeadphonesProtectionDb;
            float shotgunDb = ExposureModel.Calculate(shotgun, _tuning).HeadphonesProtectionDb;
            float heavyDb = ExposureModel.Calculate(heavy, _tuning).HeadphonesProtectionDb;

            Assert.That(intermediateDb, Is.GreaterThan(fullPowerDb));
            Assert.That(fullPowerDb, Is.GreaterThan(shotgunDb));
            Assert.That(shotgunDb, Is.GreaterThan(heavyDb));
        }

        [Test]
        public void ModLoudnessChangesSeverityInExpectedDirection()
        {
            ShotDescriptor quiet = CreateShot();
            quiet.SummedModLoudness = -30;
            ShotDescriptor loud = CreateShot();
            loud.SummedModLoudness = 30;

            ExposureResult quietResult = ExposureModel.Calculate(quiet, _tuning);
            ExposureResult loudResult = ExposureModel.Calculate(loud, _tuning);

            Assert.That(loudResult.FinalSeverity, Is.GreaterThan(quietResult.FinalSeverity));
        }

        [Test]
        public void UnknownCaliberUsesConfiguredFallback()
        {
            ShotDescriptor shot = CreateShot();
            shot.AmmoCaliber = "ModdedPlasma";
            shot.BulletMassGram = 0f;
            shot.MuzzleVelocity = 0f;

            ExposureResult result = ExposureModel.Calculate(shot, _tuning);

            Assert.That(result.CaliberBaseline, Is.EqualTo(_tuning.UnknownCaliberSeverity));
        }

        [Test]
        public void ShotDescriptorDoesNotRetainUnityWeaponObjects()
        {
            bool hasUnityObjectReference = typeof(ShotDescriptor)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType));

            Assert.That(hasUnityObjectReference, Is.False,
                "Shot descriptors must contain snapshots only and must not retain weapon-hierarchy objects.");
        }

        [Test]
        public void F12SurfaceIncludesRequestedAudioControls()
        {
            int configEntryFields = typeof(ModConfig)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Count(field => field.FieldType.IsGenericType &&
                                field.FieldType.GetGenericTypeDefinition() == typeof(ConfigEntry<>));

            Assert.That(configEntryFields, Is.EqualTo(34));
            Assert.That(_config.HeadphoneMode, Is.Not.Null);
            Assert.That(_config.GunshotContrastDb, Is.Not.Null);
            Assert.That(_config.IndoorHeadphonesDampingPercent, Is.Not.Null);
            Assert.That(_config.LowEndNormalizationPercent, Is.Not.Null);
            Assert.That(_config.CaliberContrastPercent, Is.Not.Null);
            Assert.That(_config.AutomaticTailMode, Is.Not.Null);
            Assert.That(_config.LowEndMode, Is.Not.Null);
            Assert.That(_config.AutomaticPitchedRoute, Is.Not.Null);
            Assert.That(_config.PitchedLayerSemitones, Is.Not.Null);
            Assert.That(_config.PitchedLayerHighpassHz, Is.Not.Null);
            Assert.That(_config.PitchedLayerLowpassHz, Is.Not.Null);
            Assert.That(_config.PitchedLayerFadePercent, Is.Not.Null);
            Assert.That(_config.AutomaticPitchedTailMs, Is.Not.Null);
            Assert.That(_config.PitchedLayerGainDb, Is.Not.Null);
            Assert.That(_config.PitchedLayerOcclusion, Is.Not.Null);
            Assert.That(_config.PitchedLayerOccludedLowpassHz, Is.Not.Null);
        }

        [Test]
        public void ReleaseDefaultsMatchTheTunedConfiguration()
        {
            Assert.That(_config.Enabled.Value, Is.True);
            Assert.That(_config.Preset.Value, Is.EqualTo(LoudnessPreset.Balanced));
            Assert.That(_config.ShotImpact.Value, Is.EqualTo(160f).Within(0.0001f));
            Assert.That(_config.GunshotContrastDb.Value, Is.EqualTo(8f));
            Assert.That(_config.IndoorEmphasis.Value, Is.EqualTo(100f));
            Assert.That(_config.HearingTrauma.Value, Is.EqualTo(100f).Within(0.0001f));
            Assert.That(_config.Ringing.Value, Is.EqualTo(100f).Within(0.0001f));
            Assert.That(_config.EarDifference.Value, Is.EqualTo(140f).Within(0.0001f));
            Assert.That(_config.HeadphoneMode.Value, Is.EqualTo(HeadphoneMode.Realistic));
            Assert.That(_config.HeadphonesFit.Value, Is.EqualTo(HeadphonesFitPreset.Tight));
            Assert.That(_config.LowEndMode.Value, Is.EqualTo(GunshotLowEndMode.PitchedCopy));
            Assert.That(_config.AutomaticPitchedRoute.Value, Is.EqualTo(AutomaticPitchedRoute.CachedReport));
            Assert.That(_config.AutomaticTailMode.Value, Is.EqualTo(AutomaticTailMode.FullReportPerShot));
            Assert.That(_config.PitchedLayerSemitones.Value, Is.EqualTo(12f).Within(0.0001f));
            Assert.That(_config.LowEndNormalizationPercent.Value, Is.EqualTo(100f));
            Assert.That(_config.CaliberContrastPercent.Value, Is.EqualTo(200f));
            Assert.That(_config.PitchedLayerLowpassHz.Value, Is.EqualTo(2000f));
            Assert.That(_config.PitchedLayerHighpassHz.Value, Is.EqualTo(10.00001f).Within(0.0001f));
            Assert.That(_config.PitchedLayerFadePercent.Value, Is.EqualTo(50f).Within(0.0001f));
            Assert.That(_config.AutomaticPitchedTailMs.Value, Is.EqualTo(30f));
            Assert.That(_config.PitchedLayerGainDb.Value, Is.EqualTo(20f).Within(0.0001f));
            Assert.That(_config.PitchedLayerOcclusion.Value, Is.EqualTo(PitchedLayerOcclusionMode.Inherit));
            Assert.That(_config.PitchedLayerOccludedLowpassHz.Value, Is.EqualTo(500.4695f).Within(0.0001f));
            Assert.That(_config.HearingLossDuration.Value, Is.EqualTo(100f));
            Assert.That(_config.RingingDuration.Value, Is.EqualTo(100f));
            Assert.That(_config.BlastHearingStrength.Value, Is.EqualTo(100f));
            Assert.That(_config.BlastRingingStrength.Value, Is.EqualTo(100f));
            Assert.That(_config.BlastHearingDuration.Value, Is.EqualTo(45f));
            Assert.That(_config.BlastRingingDuration.Value, Is.EqualTo(90f));
            Assert.That(_config.BlastSevereDuration.Value, Is.EqualTo(180f));
            Assert.That(_config.BlastRadius.Value, Is.EqualTo(5f));
            Assert.That(_config.BlastIndoorScale.Value, Is.EqualTo(3f));
            Assert.That(_config.IndoorHeadphonesDampingPercent.Value, Is.EqualTo(100f));
            Assert.That(_config.DiagnosticShotLog.Value, Is.True);
        }

        [Test]
        public void ComplexAndDiagnosticControlsAreAdvanced()
        {
            ConfigEntryBase[] advanced =
            {
                _config.LowEndMode,
                _config.AutomaticPitchedRoute,
                _config.AutomaticTailMode,
                _config.PitchedLayerSemitones,
                _config.LowEndNormalizationPercent,
                _config.CaliberContrastPercent,
                _config.PitchedLayerLowpassHz,
                _config.PitchedLayerHighpassHz,
                _config.PitchedLayerFadePercent,
                _config.AutomaticPitchedTailMs,
                _config.PitchedLayerGainDb,
                _config.PitchedLayerOcclusion,
                _config.PitchedLayerOccludedLowpassHz,
                _config.DiagnosticShotLog
            };

            foreach (ConfigEntryBase entry in advanced)
            {
                Assert.That(entry.Description.Tags, Has.Some.Matches<object>(tag =>
                    tag.GetType().GetField("IsAdvanced")?.GetValue(tag) is bool value && value),
                    $"{entry.Definition.Section} / {entry.Definition.Key} must be Advanced.");
            }
        }

        [Test]
        public void F12SurfaceIsEntirelyEnglish()
        {
            ConfigEntryBase[] entries = typeof(ModConfig)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => typeof(ConfigEntryBase).IsAssignableFrom(field.FieldType))
                .Select(field => (ConfigEntryBase)field.GetValue(_config))
                .ToArray();

            foreach (ConfigEntryBase entry in entries)
            {
                string text = entry.Definition.Section + " " + entry.Definition.Key + " " +
                    entry.Description.Description + " " + entry.DefaultValue;
                Assert.That(text.Any(ch => ch >= '\u0400' && ch <= '\u04ff'), Is.False,
                    $"{entry.Definition.Section} / {entry.Definition.Key} contains Cyrillic text.");
            }
        }

        [Test]
        public void LargerCaliberReceivesMoreDirectBoost()
        {
            ShotDescriptor pistol = CreateShot();
            pistol.AmmoCaliber = "9x19PARA";
            pistol.WeaponCategory = WeaponCategory.Pistol;
            ShotDescriptor heavy = CreateShot();
            heavy.AmmoCaliber = "127x99";
            heavy.WeaponCategory = WeaponCategory.Heavy;

            ExposureResult pistolExposure = ExposureModel.Calculate(pistol, _tuning);
            ExposureResult heavyExposure = ExposureModel.Calculate(heavy, _tuning);
            float pistolBoost = DirectLoudnessModel.CalculateBoostDb(pistol, pistolExposure, _tuning);
            float heavyBoost = DirectLoudnessModel.CalculateBoostDb(heavy, heavyExposure, _tuning);

            Assert.That(heavyBoost, Is.GreaterThan(pistolBoost));
        }

        [Test]
        public void F12AutomaticVolumeDefaultsToEveryShotAndRetainsLegacyChoice()
        {
            Assert.That(_config.GetTuning().AutomaticTailMode, Is.EqualTo(AutomaticTailMode.FullReportPerShot));
            _config.AutomaticTailMode.Value = AutomaticTailMode.TailAfterBurst;
            Assert.That(_config.GetTuning().AutomaticTailMode, Is.EqualTo(AutomaticTailMode.TailAfterBurst));
        }

        [Test]
        public void IndoorShotReceivesMoreDirectBoost()
        {
            ShotDescriptor outdoor = CreateShot();
            ShotDescriptor indoor = CreateShot();
            indoor.IsIndoor = true;

            ExposureResult outdoorExposure = ExposureModel.Calculate(outdoor, _tuning);
            ExposureResult indoorExposure = ExposureModel.Calculate(indoor, _tuning);

            Assert.That(
                DirectLoudnessModel.CalculateBoostDb(indoor, indoorExposure, _tuning),
                Is.GreaterThan(DirectLoudnessModel.CalculateBoostDb(outdoor, outdoorExposure, _tuning)));
        }

        [Test]
        public void HeadphonesDoNotDoubleReduceModDirectBoost()
        {
            ShotDescriptor unprotected = CreateShot();
            ShotDescriptor protectedShot = CreateShot();
            protectedShot.HasActiveHeadphones = true;
            protectedShot.HeadphonesCompressorThresholdDb = -23f;

            ExposureResult openExposure = ExposureModel.Calculate(unprotected, _tuning);
            ExposureResult protectedExposure = ExposureModel.Calculate(protectedShot, _tuning);

            Assert.That(
                DirectLoudnessModel.CalculateBoostDb(protectedShot, protectedExposure, _tuning),
                Is.EqualTo(DirectLoudnessModel.CalculateBoostDb(unprotected, openExposure, _tuning)));
        }

        [Test]
        public void ZeroShotImpactLeavesDirectVolumeUnchanged()
        {
            _config.ShotImpact.Value = 0f;
            TuningSnapshot tuning = _config.GetTuning();
            ShotDescriptor shot = CreateShot();
            ExposureResult exposure = ExposureModel.Calculate(shot, tuning);
            float boost = DirectLoudnessModel.CalculateBoostDb(shot, exposure, tuning);
            float frequency = DirectLoudnessModel.CalculatePressureFrequencyHz(exposure, tuning);

            Assert.That(boost, Is.EqualTo(0f));
            Assert.That(tuning.DirectBodyGain, Is.EqualTo(0f));
            Assert.That(LocalGunshotImpactFilter.CalculateBodyBandGain(tuning.DirectBodyGain, frequency), Is.EqualTo(0f));
        }

        [Test]
        public void LocalShotAudioTuningIsRegisteredOnlyInsideShotScope()
        {
            var tuning = new LocalGunshotAudioTuning(
                6f,
                0.2f,
                90f,
                GunshotLowEndMode.OriginalBand,
                AutomaticPitchedRoute.CachedReport,
                12f,
                35f,
                0.25f,
                260f,
                35f,
                0f,
                PitchedLayerOcclusionMode.Inherit,
                160f,
                0.1f,
                true,
                1f,
                2f,
                -1f,
                0.7f);

            DirectGunshotAudioPatch.EndLocalShot();
            Assert.That(DirectGunshotAudioPatch.TryGetCurrentTuning(out _), Is.False);

            DirectGunshotAudioPatch.BeginLocalShot(tuning);
            Assert.That(DirectGunshotAudioPatch.TryGetCurrentTuning(out LocalGunshotAudioTuning registered), Is.True);
            int registeredSamples = DirectGunshotAudioPatch.EndLocalShot();

            Assert.That(registeredSamples, Is.EqualTo(0));
            Assert.That(registered.DirectBoostDb, Is.EqualTo(6f));
            Assert.That(DirectGunshotAudioPatch.TryGetCurrentTuning(out _), Is.False);
        }

        [Test]
        public void PitchedLowEndSettingsAreExposedThroughTuningSnapshot()
        {
            _config.LowEndMode.Value = GunshotLowEndMode.PitchedCopy;
            _config.AutomaticPitchedRoute.Value = AutomaticPitchedRoute.BuiltInDSP;
            _config.PitchedLayerSemitones.Value = 9f;
            _config.PitchedLayerHighpassHz.Value = 42f;
            _config.PitchedLayerLowpassHz.Value = 320f;
            _config.PitchedLayerFadePercent.Value = 65f;
            _config.AutomaticPitchedTailMs.Value = 320f;
            _config.PitchedLayerGainDb.Value = 3f;
            _config.PitchedLayerOcclusion.Value = PitchedLayerOcclusionMode.Enhanced;
            _config.PitchedLayerOccludedLowpassHz.Value = 140f;

            TuningSnapshot tuning = _config.GetTuning();

            Assert.That(tuning.LowEndMode, Is.EqualTo(GunshotLowEndMode.PitchedCopy));
            Assert.That(tuning.AutomaticPitchedRoute, Is.EqualTo(AutomaticPitchedRoute.BuiltInDSP));
            Assert.That(tuning.PitchedLayerSemitones, Is.EqualTo(9f));
            Assert.That(tuning.PitchedLayerHighpassHz, Is.EqualTo(42f));
            Assert.That(tuning.PitchedLayerLowpassHz, Is.EqualTo(320f));
            Assert.That(tuning.PitchedLayerFadePercent, Is.EqualTo(65f));
            Assert.That(tuning.AutomaticPitchedTailSeconds, Is.EqualTo(0.32f).Within(0.0001f));
            Assert.That(tuning.PitchedLayerGainDb, Is.EqualTo(3f));
            Assert.That(tuning.PitchedLayerOcclusion, Is.EqualTo(PitchedLayerOcclusionMode.Enhanced));
            Assert.That(tuning.PitchedLayerOccludedLowpassHz, Is.EqualTo(140f));
        }

        [Test]
        public void PitchedLayerUsesSemitoneRatioAndCaliberWeightedGain()
        {
            float octaveDown = PitchedGunshotLayer.CalculatePitchRatio(12f);
            float twoOctavesDown = PitchedGunshotLayer.CalculatePitchRatio(24f);
            float rifleGain = PitchedGunshotLayer.CalculateLayerGain(0.4f, 99f);
            float heavyGain = PitchedGunshotLayer.CalculateLayerGain(0.4f, 68f);
            float boostedGain = PitchedGunshotLayer.CalculateLayerGain(0.4f, 99f, 6f);
            float gainAtTwelve = PitchedGunshotLayer.CalculateLayerGain(0.4f, 99f, 12f);
            float gainAtThirty = PitchedGunshotLayer.CalculateLayerGain(0.4f, 99f, 30f);

            Assert.That(octaveDown, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(twoOctavesDown, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(heavyGain, Is.GreaterThan(rifleGain));
            Assert.That(heavyGain, Is.LessThanOrEqualTo(0.7625f).Within(0.0001f));
            Assert.That(boostedGain, Is.GreaterThan(rifleGain * 1.99f));
            Assert.That(gainAtTwelve, Is.GreaterThan(boostedGain));
            Assert.That(gainAtThirty, Is.GreaterThan(gainAtTwelve * 7.9f));
        }

        [Test]
        public void PitchedLayerDurationUsesStockEndOrAutomaticBeat()
        {
            float oneShot = PitchedGunshotLayer.CalculateOriginalDuration(
                10.0, 10.42, 0f, 0f, 1f);
            float automaticBeat = PitchedGunshotLayer.CalculateOriginalDuration(
                10.0, 0.0, 0.075f, 1.2f, 1f);

            Assert.That(oneShot, Is.EqualTo(0.42f).Within(0.0001f));
            Assert.That(automaticBeat, Is.EqualTo(0.075f).Within(0.0001f));
        }

        [Test]
        public void PitchedLayerConsumesFullSourceSpanAfterPitchReduction()
        {
            float ratio = PitchedGunshotLayer.CalculatePitchRatio(10.4f);
            float sourceSpan = 0.1f;
            float outputDuration = PitchedGunshotLayer.CalculatePitchedDuration(
                sourceSpan,
                ratio);

            Assert.That(ratio, Is.EqualTo(0.5484f).Within(0.0001f));
            Assert.That(outputDuration, Is.EqualTo(0.1823f).Within(0.0002f));
            Assert.That(outputDuration * ratio, Is.EqualTo(sourceSpan).Within(0.0001f));
        }

        [Test]
        public void CachedAutomaticTailExtendsOutputWithoutExpandingCapturedBeat()
        {
            float ratio = PitchedGunshotLayer.CalculatePitchRatio(10.4f);
            float sourceBeat = 0.1f;
            float duration = PitchedGunshotLayer.CalculateCachedOutputDuration(
                sourceBeat,
                ratio,
                0.25f);

            Assert.That(duration, Is.EqualTo(0.4323f).Within(0.0002f));
            Assert.That(sourceBeat, Is.EqualTo(0.1f));
        }

        [Test]
        public void ExtendedAutomaticTailRangeReachesSixHundredMilliseconds()
        {
            float duration = PitchedGunshotLayer.CalculateCachedOutputDuration(0.1f, 0.5f, 0.6f);
            Assert.That(duration, Is.EqualTo(0.8f).Within(0.0001f));
        }

        [Test]
        public void AutomaticTailFeedbackDecaysByRequestedEndpoint()
        {
            float shortTail = PitchedGunshotTailFilter.CalculateFeedback(0.02f, 0.1f);
            float longTail = PitchedGunshotTailFilter.CalculateFeedback(0.02f, 0.4f);
            float disabled = PitchedGunshotTailFilter.CalculateFeedback(0.02f, 0f);

            Assert.That(longTail, Is.GreaterThan(shortTail));
            Assert.That(shortTail, Is.InRange(0f, 0.99f));
            Assert.That(longTail, Is.InRange(0f, 0.99f));
            Assert.That(disabled, Is.EqualTo(0f));
        }

        [Test]
        public void AutomaticDspLayerDetectsRisingTransientAndReadsSourceAtPitchRatio()
        {
            bool attack = AutomaticPitchedGunshotFilter.IsOnset(0.02f, 0.04f, 64);
            bool tailFluctuation = AutomaticPitchedGunshotFilter.IsOnset(0.02f, 0.025f, 64);
            bool coldStart = AutomaticPitchedGunshotFilter.IsOnset(0f, 0.5f, 0);
            bool warmupTail = AutomaticPitchedGunshotFilter.IsOnset(0.02f, 0.5f, 63);
            double sourcePosition = AutomaticPitchedGunshotFilter.AdvanceSourcePosition(
                1000.0, 0.549f, 480);

            Assert.That(attack, Is.True);
            Assert.That(tailFluctuation, Is.False);
            Assert.That(coldStart, Is.True);
            Assert.That(warmupTail, Is.False);
            Assert.That(sourcePosition, Is.EqualTo(1263.52).Within(0.001));
        }

        [Test]
        public void AutomaticBeatCaptureFindsTheReportOnsetInsideCapturedBeat()
        {
            float[] pcm = new float[200];
            pcm[80] = 0.01f;
            pcm[100] = 0.5f;

            int onset = AutomaticBeatClipCache.FindOnsetFrame(
                pcm,
                100,
                2,
                out float peak);

            Assert.That(onset, Is.EqualTo(50));
            Assert.That(peak, Is.EqualTo(0.5f));
        }

        [Test]
        public void AutomaticBeatBoundaryUsesOneSixteenthOfAuthoredLoop()
        {
            // AKM bank: 70,568 samples / 16 = 4,410 frames plus remainder.
            // At 3,700 frames into a beat the next authored impulse is about 16 ms away.
            double delay = AutomaticBeatTiming.CalculateNextBoundaryDelaySeconds(
                3700,
                70568,
                44100,
                1f);
            double atBoundary = AutomaticBeatTiming.CalculateNextBoundaryDelaySeconds(
                4410,
                70568,
                44100,
                1f);

            Assert.That(delay, Is.EqualTo(710.0 / 44100.0).Within(0.000001));
            Assert.That(atBoundary, Is.EqualTo(0.0));
        }

        [Test]
        public void AutomaticShotTimelineNeverDropsASequenceIndex()
        {
            double first = AutomaticBeatTiming.CalculateShotBoundary(20.0, 0, 0.1f);
            double second = AutomaticBeatTiming.CalculateShotBoundary(20.0, 1, 0.1f);
            double fifth = AutomaticBeatTiming.CalculateShotBoundary(20.0, 4, 0.1f);

            Assert.That(first, Is.EqualTo(20.0).Within(0.000001));
            Assert.That(second, Is.EqualTo(20.1).Within(0.000001));
            Assert.That(fifth, Is.EqualTo(20.4).Within(0.000001));
        }

        [Test]
        public void PitchedLayerFadeShapesTailWithoutDefiningDuration()
        {
            float start = PitchedGunshotEnvelopeFilter.CalculateEnvelope(0, 48000, 0.2f, 35f);
            float attackEnd = PitchedGunshotEnvelopeFilter.CalculateEnvelope(96, 48000, 0.2f, 35f);
            float sustain = PitchedGunshotEnvelopeFilter.CalculateEnvelope(4800, 48000, 0.2f, 35f);
            float longFadeTail = PitchedGunshotEnvelopeFilter.CalculateEnvelope(8640, 48000, 0.2f, 35f);
            float shortFadeTail = PitchedGunshotEnvelopeFilter.CalculateEnvelope(8640, 48000, 0.2f, 10f);
            float end = PitchedGunshotEnvelopeFilter.CalculateEnvelope(9600, 48000, 0.2f, 35f);

            Assert.That(start, Is.EqualTo(0f));
            Assert.That(attackEnd, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(sustain, Is.EqualTo(1f));
            Assert.That(longFadeTail, Is.InRange(0.3f, 0.6f));
            Assert.That(shortFadeTail, Is.EqualTo(1f));
            Assert.That(end, Is.EqualTo(0f).Within(0.000001f));
        }

        [Test]
        public void PitchedLayerOcclusionCanBeIgnoredInheritedOrStrengthened()
        {
            float ignoredGain = PitchedGunshotLayer.CalculateOccludedGain(
                1f, 0.5f, PitchedLayerOcclusionMode.Ignore);
            float inheritedGain = PitchedGunshotLayer.CalculateOccludedGain(
                1f, 0.5f, PitchedLayerOcclusionMode.Inherit);
            float strongGain = PitchedGunshotLayer.CalculateOccludedGain(
                1f, 0.5f, PitchedLayerOcclusionMode.Enhanced);
            float ignoredCutoff = PitchedGunshotLayer.CalculateOccludedLowpass(
                260f, 160f, 0.5f, PitchedLayerOcclusionMode.Ignore);
            float inheritedCutoff = PitchedGunshotLayer.CalculateOccludedLowpass(
                260f, 160f, 0.5f, PitchedLayerOcclusionMode.Inherit);
            float strongCutoff = PitchedGunshotLayer.CalculateOccludedLowpass(
                260f, 160f, 0.5f, PitchedLayerOcclusionMode.Enhanced);

            Assert.That(ignoredGain, Is.EqualTo(1f));
            Assert.That(inheritedGain, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(strongGain, Is.LessThan(inheritedGain));
            Assert.That(ignoredCutoff, Is.EqualTo(260f));
            Assert.That(inheritedCutoff, Is.EqualTo(210f).Within(0.0001f));
            Assert.That(strongCutoff, Is.LessThan(inheritedCutoff));
        }

        [Test]
        public void ClipDerivedBodyEnvelopeStartsAndEndsAtZeroAndBoundsTheMix()
        {
            float envelopeStart = LocalGunshotImpactFilter.CalculateBodyEnvelope(0, 48000);
            float envelopeBody = LocalGunshotImpactFilter.CalculateBodyEnvelope(120, 48000);
            float envelopeEnd = LocalGunshotImpactFilter.CalculateBodyEnvelope(5760, 48000);
            float positivePeak = LocalGunshotImpactFilter.MixBounded(0.9f, 0.5f);
            float negativePeak = LocalGunshotImpactFilter.MixBounded(-0.9f, -0.5f);

            Assert.That(envelopeStart, Is.EqualTo(0f).Within(0.000001f));
            Assert.That(envelopeBody, Is.GreaterThan(0.5f));
            Assert.That(envelopeEnd, Is.EqualTo(0f));
            Assert.That(positivePeak, Is.GreaterThan(0.9f));
            Assert.That(positivePeak, Is.LessThanOrEqualTo(1f));
            Assert.That(negativePeak, Is.LessThan(-0.9f));
            Assert.That(negativePeak, Is.GreaterThanOrEqualTo(-1f));
        }

        [Test]
        public void ClipDerivedBodyHasNoFixedPitchAndGrowsWithCaliber()
        {
            float rifleGain = LocalGunshotImpactFilter.CalculateBodyBandGain(0.48f, 99f);
            float heavyGain = LocalGunshotImpactFilter.CalculateBodyBandGain(0.48f, 68f);
            float rifleCutoff = LocalGunshotImpactFilter.CalculateBodyUpperCutoff(99f);
            float heavyCutoff = LocalGunshotImpactFilter.CalculateBodyUpperCutoff(68f);

            Assert.That(rifleGain, Is.GreaterThan(1f));
            Assert.That(heavyGain, Is.GreaterThan(rifleGain));
            Assert.That(heavyCutoff, Is.LessThan(rifleCutoff));
            Assert.That(heavyCutoff, Is.EqualTo(170f));
        }

        [Test]
        public void LargerCaliberUsesLowerPressureFrequency()
        {
            ShotDescriptor rifle = CreateShot();
            ShotDescriptor heavy = CreateShot();
            heavy.AmmoCaliber = "127x99";
            heavy.WeaponCategory = WeaponCategory.Heavy;

            float rifleFrequency = DirectLoudnessModel.CalculatePressureFrequencyHz(
                ExposureModel.Calculate(rifle, _tuning),
                _tuning);
            float heavyFrequency = DirectLoudnessModel.CalculatePressureFrequencyHz(
                ExposureModel.Calculate(heavy, _tuning),
                _tuning);

            Assert.That(heavyFrequency, Is.LessThan(rifleFrequency));
            Assert.That(heavyFrequency, Is.InRange(68f, 122f));
            Assert.That(
                LocalGunshotImpactFilter.CalculateBodyUpperCutoff(heavyFrequency),
                Is.LessThan(LocalGunshotImpactFilter.CalculateBodyUpperCutoff(rifleFrequency)));
        }

        [Test]
        public void PunishingIndoorProfileCreatesAudibleRoomResponse()
        {
            _config.Preset.Value = LoudnessPreset.Punishing;
            _config.IndoorEmphasis.Value = 200f;
            TuningSnapshot tuning = _config.GetTuning();

            Assert.That(tuning.IndoorRoomStrength, Is.EqualTo(2f));
            Assert.That(tuning.IndoorEarlyReflectionsSendDb, Is.EqualTo(5f));
            Assert.That(tuning.IndoorReverbSendDb, Is.EqualTo(2f));
            Assert.That(tuning.IndoorReverbReach, Is.EqualTo(0.9f).Within(0.0001f));
        }

        [Test]
        public void PunishingIndoorHeavyShotRespectsDirectBoostCap()
        {
            _config.Preset.Value = LoudnessPreset.Punishing;
            _config.ShotImpact.Value = 200f;
            _config.IndoorEmphasis.Value = 200f;
            TuningSnapshot tuning = _config.GetTuning();
            ShotDescriptor shot = CreateShot();
            shot.AmmoCaliber = "127x99";
            shot.WeaponCategory = WeaponCategory.Heavy;
            shot.IsIndoor = true;
            ExposureResult exposure = ExposureModel.Calculate(shot, tuning);

            float boost = DirectLoudnessModel.CalculateBoostDb(shot, exposure, tuning);
            float frequency = DirectLoudnessModel.CalculatePressureFrequencyHz(exposure, tuning);
            float bodyBandGain = LocalGunshotImpactFilter.CalculateBodyBandGain(0.5f, frequency);

            Assert.That(boost, Is.EqualTo(6.5f));
            Assert.That(bodyBandGain, Is.GreaterThan(1.5f));
            Assert.That(bodyBandGain, Is.LessThanOrEqualTo(2.08f).Within(0.0001f));
        }

        private static ShotDescriptor CreateShot()
        {
            return new ShotDescriptor
            {
                AmmoCaliber = "545x39",
                BulletMassGram = 3.4f,
                MuzzleVelocity = 900f,
                ProjectileCount = 1,
                WeaponClass = "assaultRifle",
                WeaponCategory = WeaponCategory.LongGun,
                IsSuppressed = false,
                SummedModLoudness = 0,
                IsIndoor = false,
                IsLeftStance = false,
                HasActiveHeadphones = false
            };
        }
    }
}
