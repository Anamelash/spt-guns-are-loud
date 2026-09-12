using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Stage 3 of the 1.0.1 performance plan: what the main thread does between
    /// shots. These cover the parts that can be decided without the engine; the
    /// rest is read from the summary the stage-1 build prints.
    /// </summary>
    [TestFixture]
    public sealed class MainThreadBudgetTests
    {
        [TestCase("AssaultRifle", "LongGun")]
        [TestCase("assaultCarbine", "LongGun")]
        [TestCase("SHOTGUN", "LongGun")]
        [TestCase("MachineGun", "LongGun")]
        [TestCase("Pistol", "Pistol")]
        [TestCase("revolver", "Pistol")]
        [TestCase("SMG", "Compact")]
        [TestCase("pdw", "Compact")]
        [TestCase("GrenadeLauncher", "Heavy")]
        [TestCase("SpecialWeapon", "Heavy")]
        [TestCase("", "Unknown")]
        [TestCase("somethingElse", "Unknown")]
        public void WeaponClassIsClassifiedWithoutLoweringTheString(string weaponClass, string expected)
        {
            Assert.That(ShotDescriptorFactory.ClassifyWeapon(weaponClass).ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void SeveralSettingChangesInOneFrameProduceOneTuningSnapshot()
        {
            var file = new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);
            TuningSnapshot before = config.GetTuning();

            // A preset click writes many entries; each one raises SettingChanged.
            config.PitchedLayerGainDb.Value += 1f;
            config.PitchedLayerSemitones.Value += 1f;
            config.LowEndNormalizationDb.Value -= 1f;

            TuningSnapshot after = config.GetTuning();
            Assert.That(after, Is.Not.SameAs(before));
            Assert.That(config.GetTuning(), Is.SameAs(after),
                "The snapshot is rebuilt once, by the first reader after the change.");
            Assert.That(after.PitchedLayerGainDb, Is.EqualTo(before.PitchedLayerGainDb + 1f));
            Assert.That(after.PitchedLayerSemitones, Is.EqualTo(before.PitchedLayerSemitones + 1f));
        }

        [TestCase("Audio/GunshotContrastController.cs", @"\boriginal\.name\b")]
        [TestCase("Audio/GunshotContrastController.cs", @"entry\.Source\.GetComponent<GunshotContrastFilter>")]
        public void ContrastMaintenanceDoesNotRepeatPerFrameLookups(string file, string forbidden)
        {
            Assert.That(ReadSource(file), Does.Not.Match(forbidden),
                "Group names and the entry's own filter are resolved once, not on every pass.");
        }

        [Test]
        public void DiscoveryStopsSweepingUntilASceneChangesOrContrastIsReactivated()
        {
            string source = ReadSource("Audio/IncrementalAudioDiscovery.cs");
            Assert.That(source, Does.Match(@"if \(_sweepComplete\) return;"),
                "A completed sweep must not keep walking the same hierarchy.");
            Assert.That(ReadSource("Audio/GunshotContrastController.cs"),
                Does.Match(@"SceneManager\.sceneLoaded \+="),
                "Only a scene load or unload can add sources the routing hooks never see.");
        }

        [TestCase("Audio/AutomaticCopyCache.cs")]
        [TestCase("Audio/AutomaticShotTiming.cs")]
        [TestCase("Audio/LocalGunshotAudioProcessor.cs")]
        [TestCase("Audio/PitchedGunshotLayer.cs")]
        [TestCase("Runtime/HearingExposureController.cs")]
        public void AudioEngineSettingsAreReadFromTheCachedRuntimeState(string file)
        {
            Assert.That(ReadSource(file),
                Does.Not.Match(@"AudioSettings\.(outputSampleRate|GetDSPBufferSize)"),
                "Sample rate and buffer size change only with the device configuration.");
        }

        private static string ReadSource(string file)
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GunsAreLoud.sln")))
                directory = directory.Parent;
            Assert.That(directory, Is.Not.Null, "This source regression check runs from the repository.");
            return File.ReadAllText(
                Path.Combine(directory.FullName, "client/GunsAreLoud.Client", file));
        }
    }
}
