using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using NUnit.Framework;
namespace GunsAreLoud.Tests
{
    public sealed class F12OrganizationTests
    {
        [Test]
        public void InstalledManagerReadsAdvancedAndOrderAndCategorySequence()
        {
            var file = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var config = new ModConfig(file);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(@"D:\Games\SPT_4.1.5\BepInEx\plugins\spt\ConfigurationManager\ConfigurationManager.dll"));
            var type = assembly.GetType("ConfigurationManager.ConfigSettingEntry", true);
            foreach (var pair in file)
            {
                var parsed = Activator.CreateInstance(type, new object[] { pair.Value, null });
                Assert.That(type.GetProperty("IsAdvanced").GetValue(parsed),
                    Is.EqualTo(pair.Key.Section == "04. Low-level & debug"));
                if (pair.Key.Key == "Enabled")
                    Assert.That(type.GetProperty("Order").GetValue(parsed), Is.EqualTo(1000));
                if (pair.Key.Key == "Headset Diagnostics")
                {
                    Assert.That(type.GetProperty("CustomDrawer").GetValue(parsed), Is.Not.Null);
                    Assert.That(type.GetProperty("HideDefaultButton").GetValue(parsed), Is.EqualTo(true));
                }
            }
            Assert.That(file.Select(p => p.Key.Section).Distinct(), Is.EqualTo(new[] {
                "01. General", "02. Gunshots", "03. Explosions", "04. Low-level & debug" }));
        }

        [Test]
        public void OldValuesMoveAndNewValuesWinOnReload()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg");
            try
            {
                File.WriteAllText(path, "[10. Sound and Hearing]\nTemporary Hearing Loss = 73\nTinnitus = 84\nHeadset Processing = Vanilla\n[20. Low-End Layer]\nPitch Reduction, semitones = 16\n[15. Explosions]\nRinging Duration, s = 123\n");
                var file = new ConfigFile(path, false) { SaveOnConfigSet = false };
                var config = new ModConfig(file);
                Assert.That(config.HearingTrauma.Value, Is.EqualTo(73));
                Assert.That(config.Ringing.Value, Is.EqualTo(84));
                Assert.That(config.HeadphoneMode.Value, Is.EqualTo(HeadphoneMode.Vanilla));
                Assert.That(config.PitchedLayerSemitones.Value, Is.EqualTo(16));
                Assert.That(config.BlastRingingDuration.Value, Is.EqualTo(123));
                Assert.That(file.Keys.Select(k => k.Section).Distinct().OrderBy(s => s), Is.EqualTo(new[] {
                    "01. General", "02. Gunshots", "03. Explosions", "04. Low-level & debug" }));
                config.HearingTrauma.Value = 61; file.Save();
                var reload = new ModConfig(new ConfigFile(path, false) { SaveOnConfigSet = false });
                Assert.That(reload.HearingTrauma.Value, Is.EqualTo(61));
                Assert.That(reload.BlastRingingDuration.Value, Is.EqualTo(123));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        [Test]
        public void DurationAndIntensityControlsAreIndependent()
        {
            var file = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var config = new ModConfig(file); var baseline = config.GetTuning();
            config.HearingLossDuration.Value = 200; config.RingingDuration.Value = 50;
            var longer = config.GetTuning();
            Assert.That(longer.HearingLossDurationScale, Is.EqualTo(2));
            Assert.That(longer.TinnitusDurationScale, Is.EqualTo(.5f));
            Assert.That(longer.MaximumAttenuationDb, Is.EqualTo(baseline.MaximumAttenuationDb));
            Assert.That(longer.TinnitusMaximumLevel, Is.EqualTo(baseline.TinnitusMaximumLevel));
            config.HearingTrauma.Value = 30; config.Ringing.Value = 40;
            Assert.That(config.GetTuning().HearingLossDurationScale, Is.EqualTo(2));
            Assert.That(config.GetTuning().TinnitusDurationScale, Is.EqualTo(.5f));
        }

        [Test]
        public void DetailedDiagnosticsResetOnEveryClientConfigurationLoad()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg");
            try
            {
                File.WriteAllText(path,
                    "[01. General]\nEnabled = false\n" +
                    "[04. Low-level & debug]\nLog Every Local Shot = true\n");
                var first = new ModConfig(new ConfigFile(path, false) { SaveOnConfigSet = false });
                Assert.That(first.Enabled.Value, Is.False, "Unrelated settings must be preserved.");
                Assert.That(first.DiagnosticShotLog.Value, Is.False);

                first.DiagnosticShotLog.Value = true;
                Assert.That(first.DiagnosticShotLog.Value, Is.True,
                    "F12 may enable detailed diagnostics for the current process.");
                first.Source.Save();

                var nextStart = new ModConfig(
                    new ConfigFile(path, false) { SaveOnConfigSet = false });
                Assert.That(nextStart.DiagnosticShotLog.Value, Is.False,
                    "A new client configuration load represents a new process start.");
                Assert.That(nextStart.Enabled.Value, Is.False);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void TuningSnapshotIsReusedUntilASettingActuallyChanges()
        {
            var file = new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);
            TuningSnapshot first = config.GetTuning();
            Assert.That(config.GetTuning(), Is.SameAs(first));

            config.PitchedLayerGainDb.Value = config.PitchedLayerGainDb.Value + 1f;
            TuningSnapshot changed = config.GetTuning();
            Assert.That(changed, Is.Not.SameAs(first));
            Assert.That(changed.PitchedLayerGainDb, Is.EqualTo(first.PitchedLayerGainDb + 1f));
            Assert.That(config.GetTuning(), Is.SameAs(changed));
        }

        /// <summary>
        /// Settings this version dropped. Each was a comparison switch or a value
        /// nothing reads any more, and an existing configuration file still has a
        /// line for it — that line must not survive the next load, or every user
        /// keeps a setting the mod no longer honours.
        /// </summary>
        [TestCase("Legacy Indoor Headset Damping", "100")]
        [TestCase("Disable Components On Other Sounds", "false")]
        [TestCase("Soft Output Limiter", "false")]
        [TestCase("Low-End Method", "OriginalBand")]
        public void SettingsRemovedInThisVersionAreTakenOutOfAnExistingFile(
            string key, string value)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg");
            try
            {
                File.WriteAllText(path, $"[04. Low-level & debug]\n{key} = {value}\n");
                var file = new ConfigFile(path, false) { SaveOnConfigSet = false };
                var unused = new ModConfig(file);

                Assert.That(file.Keys.Select(definition => definition.Key), Has.No.Member(key));
                file.Save();
                Assert.That(File.ReadAllText(path), Does.Not.Contain(key));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>
        /// Two controls stopped being percentages of an unnamed thing and became
        /// the decibels they always were underneath. A configuration file written
        /// by an earlier build must keep sounding the same: the old value is
        /// converted once, and the percent line does not come back.
        /// </summary>
        [TestCase("Low-End Normalization", 100f, 12f)]
        [TestCase("Low-End Normalization", 150f, 18f)]
        [TestCase("Low-End Normalization", 0f, 0f)]
        [TestCase("Low-End Normalization", 75f, 9f)]
        [TestCase("Cartridge Contrast", 200f, 6f)]
        [TestCase("Cartridge Contrast", 300f, 9f)]
        [TestCase("Cartridge Contrast", 0f, 0f)]
        [TestCase("Cartridge Contrast", 150f, 4.5f)]
        public void APercentageFromAnEarlierBuildBecomesTheSameSettingInDecibels(
            string name, float percent, float expectedDb)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg");
            try
            {
                File.WriteAllText(path,
                    $"[04. Low-level & debug]\n{name}, % = {percent.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
                var file = new ConfigFile(path, false) { SaveOnConfigSet = false };
                var config = new ModConfig(file);

                ConfigEntry<float> converted = name == "Cartridge Contrast"
                    ? config.CartridgeContrastDb
                    : config.LowEndNormalizationDb;
                Assert.That(converted.Value, Is.EqualTo(expectedDb).Within(0.001f));
                Assert.That(file.Keys.Select(key => key.Key), Has.No.Member($"{name}, %"));
                file.Save();
                Assert.That(File.ReadAllText(path), Does.Not.Contain($"{name}, %"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void TheDecibelControlsLeaveTheAudioPathExactlyWhereThePercentagesDid()
        {
            var file = new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);

            // The defaults are the shipped 100% and 200%, in the unit the DSP uses.
            TuningSnapshot tuning = config.GetTuning();
            Assert.That(tuning.LowEndNormalizationPercent, Is.EqualTo(100f).Within(0.001f));
            Assert.That(tuning.CaliberContrastPercent, Is.EqualTo(200f).Within(0.001f));

            config.LowEndNormalizationDb.Value = 0f;
            config.CartridgeContrastDb.Value = 9f;
            tuning = config.GetTuning();
            Assert.That(tuning.LowEndNormalizationPercent, Is.Zero);
            Assert.That(tuning.CaliberContrastPercent, Is.EqualTo(300f).Within(0.001f));
        }

        /// <summary>
        /// The two switches in General must reach everything: own gunfire, blasts,
        /// and the accumulated dose. Either one off leaves the other untouched.
        /// </summary>
        [Test]
        public void TheGeneralTogglesSwitchTheirEffectOffWithoutTouchingTheOther()
        {
            var file = new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);

            Assert.That(config.HearingLossEnabled.Value, Is.True);
            Assert.That(config.RingingEnabled.Value, Is.True);
            Assert.That(config.HearingLossActive && config.RingingActive, Is.True);
            TuningSnapshot both = config.GetTuning();
            Assert.That(both.HearingLossEnabled && both.TinnitusEnabled, Is.True);
            Assert.That(both.ExposureEnabled, Is.True);

            config.HearingLossEnabled.Value = false;
            TuningSnapshot ringingOnly = config.GetTuning();
            Assert.That(ringingOnly.HearingLossEnabled, Is.False);
            Assert.That(ringingOnly.MaximumAttenuationDb, Is.Zero, "Nothing may muffle…");
            Assert.That(ringingOnly.TinnitusEnabled, Is.True, "…and ringing is untouched.");
            Assert.That(ringingOnly.TinnitusMaximumLevel, Is.EqualTo(both.TinnitusMaximumLevel));
            Assert.That(ringingOnly.ExposureEnabled, Is.True,
                "The dose still accumulates while one of the two effects is on.");

            config.RingingEnabled.Value = false;
            TuningSnapshot neither = config.GetTuning();
            Assert.That(neither.TinnitusEnabled, Is.False);
            Assert.That(neither.TinnitusMaximumLevel, Is.Zero);
            Assert.That(neither.ExposureEnabled, Is.False,
                "With both off there is nothing to accumulate a dose for.");
            Assert.That(config.HearingLossActive || config.RingingActive, Is.False);

            // The gunshot itself is not a hearing effect and must not move.
            Assert.That(neither.BaseDirectBoostDb, Is.EqualTo(both.BaseDirectBoostDb));
            Assert.That(neither.DirectBodyGain, Is.EqualTo(both.DirectBodyGain));
            Assert.That(neither.PitchedLayerGainDb, Is.EqualTo(both.PitchedLayerGainDb));
        }

        [Test]
        public void TheGeneralTogglesAreOrdinaryVisibleSettings()
        {
            var file = new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var unused = new ModConfig(file);

            foreach (string key in new[] { "Hearing Loss", "Ringing" })
            {
                ConfigDefinition definition = file.Keys.First(entry => entry.Key == key);
                Assert.That(definition.Section, Is.EqualTo("01. General"),
                    "A player looking for the on/off switch must not need Advanced.");
            }
        }

        [Test]
        public void LateReportToleranceIsAnAdvancedControlThatScalesTheShippedBudget()
        {
            var file = new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);

            Assert.That(config.GetTuning().AutomaticLateToleranceScale, Is.EqualTo(1f),
                "The default must reproduce the budget that was confirmed by ear.");
            ConfigDefinition definition = file.Keys
                .First(key => key.Key == "Late Report Tolerance, %");
            Assert.That(definition.Section, Is.EqualTo("04. Low-level & debug"));

            config.AutomaticLateTolerancePercent.Value = 200f;
            Assert.That(config.GetTuning().AutomaticLateToleranceScale, Is.EqualTo(2f));
        }
    }
}
