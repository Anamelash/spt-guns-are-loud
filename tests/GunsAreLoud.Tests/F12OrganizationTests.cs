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
                if (pair.Key.Key == "Legacy Indoor Headset Damping")
                    Assert.That(type.GetProperty("Browsable").GetValue(parsed), Is.EqualTo(false));
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
    }
}
