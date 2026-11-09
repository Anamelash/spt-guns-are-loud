using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Engine;
using GunsAreLoud.Client.Integrations.Seb;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// The sound engine as a module: the core asks the engine and the mixer
    /// host instead of knowing about any sound mod, the stock answers are what
    /// the mod always did, and Sound Engine Backport's answers follow SEB.
    /// </summary>
    [TestFixture]
    public sealed class SoundEngineModuleTests
    {
        [TearDown]
        public void RestoreStock() => SoundEngine.Reset();

        // ---- the boundary -------------------------------------------------

        private static string ClientRoot([CallerFilePath] string caller = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller) ?? "", "..", "..", "client", "GunsAreLoud.Client"));

        [Test]
        public void TheCoreNamesNoSoundModOutsideItsModuleFolder()
        {
            string root = ClientRoot();
            Assert.That(Directory.Exists(root), Is.True, root);
            string[] forbidden = { "SebIntegration", "SoundEngineBackport", "Integrations.Seb", "SebModule",
                "SebReflection", "SebShotEngine", "SebMixerHost" };
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(root.Length + 1).Replace('\\', '/');
                if (relative.StartsWith("Integrations/", StringComparison.Ordinal) ||
                    relative.StartsWith("obj/", StringComparison.Ordinal) ||
                    relative.StartsWith("bin/", StringComparison.Ordinal))
                    continue;
                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("///", StringComparison.Ordinal))
                        continue;
                    if (!forbidden.Any(word => line.Contains(word))) continue;
                    // Load and patch order are attributes; their GUIDs come from SebIds.
                    bool attribute = trimmed.StartsWith("[", StringComparison.Ordinal) && line.Contains("SebIds.");
                    // The plugin lists the modules it knows.
                    bool registry = relative == "Plugin.cs";
                    if (!attribute && !registry) offenders.Add($"{relative}:{index + 1}: {trimmed}");
                }
            }
            Assert.That(offenders, Is.Empty, "The core must ask SoundEngine, not name a sound mod.");
        }

        // ---- stock answers --------------------------------------------------

        [Test]
        public void WithoutAModuleTheStockEngineAndHostAreInPlace()
        {
            SoundEngine.Reset();
            Assert.That(SoundEngine.Shots, Is.SameAs(StockShotEngine.Instance));
            Assert.That(SoundEngine.Mixer, Is.SameAs(StockMixerHost.Instance));
            Assert.That(SoundEngine.Module, Is.Null);
            Assert.That(SoundEngine.Describe(), Is.EqualTo("sound engine: stock, master mixer: EFT"));
        }

        [Test]
        public void StockEngineLoopsEveryBodyAndLeavesTheDonorAlone()
        {
            IShotEngine stock = StockShotEngine.Instance;
            Assert.That(stock.ClassifyBody(null, null), Is.EqualTo(BodyModel.Loop));
            Assert.That(stock.PlaysShotsItself(null), Is.False);
            Assert.That(stock.UserGainDb, Is.Zero, "the stock game has no gunshot trim of its own");
            Assert.That(stock.ReleasesSourcesDirectly, Is.False);
            Assert.That(stock.SettingsNote, Is.Empty);
            Assert.That(stock.LoopBeatSeconds(null, 1f), Is.Zero);
        }

        [Test]
        public void SebEngineHandsThePlayersGunshotTrimToTheDirectTarget()
        {
            IShotEngine seb = new SebShotEngine();
            Assert.That(seb.UserGainDb, Is.EqualTo(SebModule.GunshotsVolumeDb));
        }

        [Test]
        public void StockHostOwnsNothingAndOffersNoVariant()
        {
            IMixerHost host = StockMixerHost.Instance;
            Assert.That(host.OwnsMaster || host.PredictsInstall || host.IsActive, Is.False);
            Assert.That(host.Variant, Is.Null);
            Assert.That(host.LoadReplacement(), Is.Null);
            Assert.That(host.TryInstall(null), Is.False);
            Assert.That(host.AmbientTrimDb, Is.Zero);
            Assert.That(host.NeutralRouteParameters, Is.Empty);
        }

        [Test]
        public void AttachFallsBackToStockWhenNoModuleFindsItsMod()
        {
            SoundEngine.Attach(null, new AbsentModule(), null);
            Assert.That(SoundEngine.Shots, Is.SameAs(StockShotEngine.Instance));
            Assert.That(SoundEngine.Module, Is.Null);
            SoundEngine.Attach(null, new ThrowingModule(), new PresentModule());
            Assert.That(SoundEngine.Module, Is.InstanceOf<PresentModule>(), "a failing module does not stop the next");
            Assert.That(SoundEngine.Shots, Is.SameAs(PresentModule.Engine));
        }

        [Test]
        public void HostOverridesFollowTheHostsTrimAndNeutralParameters()
        {
            var host = new FakeHost { Trim = -90f, Neutral = new[] { "A", "B" } };
            HeadphoneRouteOverrides overrides = HeadphoneRouteOverrides.ForHost(host);
            Assert.That(overrides.AmbientVolumeDb(), Is.EqualTo(-80f), "bounded at the mixer floor");
            host.Trim = -4f;
            Assert.That(overrides.AmbientVolumeDb(), Is.EqualTo(-4f), "read when the route applies, not captured");
            Assert.That(overrides.NeutralParameters, Is.EqualTo(new[] { "A", "B" }));
        }

        [Test]
        public void StatusLineAbbreviatesTheHostName()
        {
            Assert.That(HeadphoneDiagnostics.Initials("Sound Engine Backport"), Is.EqualTo("SEB"));
            Assert.That(HeadphoneDiagnostics.Initials("EFT"), Is.EqualTo("EFT"));
            Assert.That(HeadphoneDiagnostics.Initials(""), Is.Empty);
        }

        // ---- SEB's answers --------------------------------------------------

        [TestCase(true, false, false, TestName = "Live engine plays a bank whole")]
        [TestCase(true, true, false, TestName = "Live engine plays even a loop-named clip whole")]
        [TestCase(false, true, true, TestName = "V1 path loops a loop clip")]
        [TestCase(false, false, false, TestName = "V1 path plays a whole-shot clip whole")]
        public void SebClassifiesEachClipAsSebPlaysIt(bool liveEnginePlaysBank, bool loopClip, bool expectLoop)
        {
            Assert.That(SebShotEngine.Classify(liveEnginePlaysBank, loopClip),
                Is.EqualTo(expectLoop ? BodyModel.Loop : BodyModel.OneShot));
        }

        [Test]
        public void SebsLoopRuleIsPerClip()
        {
            Assert.That(SebReflection.IsLoopClipName("rpd_outdoor_far_loop_01"), Is.True);
            Assert.That(SebReflection.IsLoopClipName("rpd_outdoor_close_01"), Is.False);
            Assert.That(SebReflection.IsLoopClipName("ak74_outdoor_close_loop_tail"), Is.False);
        }

        [Test]
        public void SebsGunshotSliderIsReadFromItsFile()
        {
            const string text = "[Volume]\nAmbientVolumeDb = -4\nGunshotsVolumeDb = -2.5\n[Engine]\nGunshotsVolumeDb = 9\n";
            Assert.That(SebModule.ParseConfigFloat(text, "Volume", "GunshotsVolumeDb"), Is.EqualTo(-2.5f));
            Assert.That(SebModule.ParseConfigFloat(text, "Volume", "Missing"), Is.Null);
            Assert.That(SebModule.ParseConfigFloat("[Volume]\nGunshotsVolumeDb = loud\n", "Volume", "GunshotsVolumeDb"), Is.Null);
        }

        [Test]
        public void SebHostCarriesItsVariantAndPrecipitationParameters()
        {
            var host = new SebMixerHost();
            Assert.That(host.Variant, Is.SameAs(SebMixerHost.LiveVariant));
            Assert.That(host.NeutralRouteParameters, Is.EquivalentTo(new[] { "PrecipitationInVolume", "PrecipitationOutVolume" }));
            Assert.That(host.DisplayName, Is.EqualTo(SebIds.DisplayName));
        }

        // ---- copies of whole shots on EFT's queue ------------------------

        [Test]
        public void OnlyAWholeShotOfContinuousFireIsReleasedByLaterRounds()
        {
            Assert.That(LocalGunshotAudioTuningBox.BurstIntervalForSample(true, 0.09f), Is.EqualTo(0.09f));
            Assert.That(LocalGunshotAudioTuningBox.BurstIntervalForSample(false, 0.09f), Is.Zero, "looped body");
            Assert.That(LocalGunshotAudioTuningBox.BurstIntervalForSample(true, 0f), Is.Zero, "single fire");
            Assert.That(LocalGunshotAudioTuningBox.BurstIntervalForSample(false, 0f), Is.Zero);
        }

        // ---- body length from the envelope ------------------------------------

        private static float[] Stereo(int rate, float seconds, Func<float, float> amplitude, float lead = 0f)
        {
            int frames = (int)(rate * (seconds + lead));
            var data = new float[frames * 2];
            var random = new Random(7);
            for (int frame = 0; frame < frames; frame++)
            {
                float t = frame / (float)rate - lead;
                float a = t < 0f ? 0f : amplitude(t);
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                data[frame * 2] = data[frame * 2 + 1] = a * noise;
            }
            return data;
        }

        [Test]
        public void AClickKeepsTodaysBodyWindow()
        {
            float[] click = Stereo(48000, 1f, t => t < 0.005f ? 1f : 0f);
            Assert.That(LowEndLevelModel.EstimateBodySeconds(click, 48000),
                Is.EqualTo(LowEndLevelModel.WindowSeconds * 0.5f).Within(1e-6f));
        }

        [Test]
        public void AnExponentialAttackEndsWhereItIsTwentyDecibelsDown()
        {
            // -20 dB at 0.25 s: exp(-k t) = 0.1.
            float k = (float)(Math.Log(10.0) / 0.25);
            float[] shot = Stereo(48000, 1.2f, t => (float)Math.Exp(-k * t), lead: 0.03f);
            Assert.That(LowEndLevelModel.EstimateBodySeconds(shot, 48000), Is.EqualTo(0.25f).Within(0.02f),
                "measured from the onset, not from the start of the clip");
        }

        [Test]
        public void SteadyNoiseIsBoundedAtTheTop()
        {
            float[] steady = Stereo(48000, 1.2f, t => 0.5f);
            Assert.That(LowEndLevelModel.EstimateBodySeconds(steady, 48000),
                Is.EqualTo(LowEndLevelModel.MaximumMeasuredBodySeconds));
        }

        [Test]
        public void SilenceHasNoBody()
        {
            Assert.That(LowEndLevelModel.EstimateBodySeconds(new float[48000 * 2], 48000), Is.Zero);
            Assert.That(LowEndLevelModel.EstimateBodySeconds(null, 48000), Is.Zero);
        }

        // ---- fakes ------------------------------------------------------------

        private sealed class AbsentModule : ISoundEngineModule
        {
            public string Name => "absent";
            public bool TryAttach(BepInEx.Logging.ManualLogSource log, out IShotEngine shots, out IMixerHost mixer)
            { shots = null; mixer = null; return false; }
            public string Describe() => "";
        }

        private sealed class ThrowingModule : ISoundEngineModule
        {
            public string Name => "throwing";
            public bool TryAttach(BepInEx.Logging.ManualLogSource log, out IShotEngine shots, out IMixerHost mixer) =>
                throw new InvalidOperationException("boom");
            public string Describe() => "";
        }

        private sealed class PresentModule : ISoundEngineModule
        {
            internal static readonly IShotEngine Engine = new SebShotEngine();
            public string Name => "present";
            public bool TryAttach(BepInEx.Logging.ManualLogSource log, out IShotEngine shots, out IMixerHost mixer)
            { shots = Engine; mixer = null; return true; }
            public string Describe() => "";
        }

        private sealed class FakeHost : IMixerHost
        {
            internal float Trim;
            internal string[] Neutral = Array.Empty<string>();
            public string DisplayName => "Fake";
            public bool OwnsMaster => true;
            public bool PredictsInstall => true;
            public string InstallBlocker => null;
            public string VariantKey => "";
            public HeadphoneMixerVariant Variant => null;
            public UnityEngine.Audio.AudioMixer LoadReplacement() => null;
            public bool TryInstall(UnityEngine.Audio.AudioMixer variant) => false;
            public bool IsActive => true;
            public float AmbientTrimDb => Trim;
            public string[] NeutralRouteParameters => Neutral;
            public event Action SettingsChanged { add { } remove { } }
        }
    }
}
