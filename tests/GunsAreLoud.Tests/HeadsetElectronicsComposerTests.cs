using System;
using System.Collections.Generic;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HeadsetElectronicsComposerTests
    {
        private const string Sordin = "5aa2ba71e5b5b000137b758f";
        private const string ComTacII = "5645bcc04bdc2d363b8b4572";
        private const string Gssh = "5b432b965acfc47a8774094e";
        private const string Razor = "5e4d34ca86f774264f758330";
        private const string ComTacV = "66b5f693acff495a294927e3";
        private const string Amp = "252d9d1d2552909d0a76033c";

        // Family assignments by registry profile id.
        private static readonly Dictionary<string, HeadsetElectronicsClass> ExpectedClasses =
            new Dictionary<string, HeadsetElectronicsClass>
            {
                ["sordin-pro-x-foam-family"] = HeadsetElectronicsClass.D1,
                ["comtac-ii-ansi"] = HeadsetElectronicsClass.A1,
                ["comtac-iv-ultrafit-reference"] = HeadsetElectronicsClass.H1,
                ["comtac-v-foam-reference"] = HeadsetElectronicsClass.D1,
                ["comtac-vi-foam-reference"] = HeadsetElectronicsClass.D1,
                ["rac-proposed"] = HeadsetElectronicsClass.D1,
                ["gssh-proposed"] = HeadsetElectronicsClass.A1,
                ["sporttac-family-reference"] = HeadsetElectronicsClass.C1,
                ["razor-digital-bt-family-reference"] = HeadsetElectronicsClass.C1,
                ["xcel-proposed"] = HeadsetElectronicsClass.C2,
                ["m32-proposed"] = HeadsetElectronicsClass.C1,
                ["liberator-proposed"] = HeadsetElectronicsClass.D1,
                ["tep-300-ultrafit-reference"] = HeadsetElectronicsClass.I1,
                ["cens-proflex-series"] = HeadsetElectronicsClass.I1
            };

        [Test]
        public void EveryRegistryProfileHasItsPlannedConstructionClass()
        {
            Assert.That(HeadsetProfileRegistry.ProfileCount, Is.EqualTo(ExpectedClasses.Count));
            for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
            {
                HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(i);
                Assert.That(HeadsetElectronicsComposer.TryGetClass(profile.ProfileId, out var type), Is.True, profile.ProfileId);
                Assert.That(type, Is.EqualTo(ExpectedClasses[profile.ProfileId]), profile.ProfileId);
            }
        }

        [Test]
        public void DeviceElectronicsFollowClassFamilyAndFacts()
        {
            HeadsetElectronicsProfile sordin = Device(Sordin);
            Assert.That(sordin.QuietGainDb, Is.EqualTo(4f));
            Assert.That(sordin.PresenceDb, Is.EqualTo(3f));
            Assert.That(sordin.MicFilterOrder, Is.EqualTo(2), "every class has 12 dB/oct band edges");
            Assert.That(sordin.NoiseDbFs, Is.EqualTo(-72f + HeadsetElectronicsComposer.NoiseAnchorDb));
            Assert.That(sordin.DelaySeconds, Is.EqualTo(0.003f).Within(1e-7f));
            Assert.That(sordin.MicHighpassHz, Is.EqualTo(100f));
            Assert.That(sordin.MicLowpassHz, Is.EqualTo(10000f));

            HeadsetElectronicsProfile gssh = Device(Gssh);
            Assert.That(gssh.MicHighpassHz, Is.EqualTo(300f));
            Assert.That(gssh.MicLowpassHz, Is.EqualTo(7000f));
            Assert.That(gssh.NoiseDbFs, Is.EqualTo(-58f + HeadsetElectronicsComposer.NoiseAnchorDb));
            Assert.That(gssh.Saturation, Is.EqualTo(0.7f));
            Assert.That(gssh.DelaySeconds, Is.Zero, "analog class");

            HeadsetElectronicsProfile amp = Device(Amp);
            Assert.That(amp.PresenceDb, Is.EqualTo(2f));
            Assert.That(amp.NoiseDbFs, Is.EqualTo(-74f + HeadsetElectronicsComposer.NoiseAnchorDb));

            HeadsetElectronicsProfile razor = Device(Razor);
            Assert.That(razor.QuietGainDb, Is.EqualTo(9f));
            Assert.That(razor.AttackSeconds, Is.EqualTo(0.020f));
        }

        [Test]
        public void TemplateMovesOnlyClassFieldsWithinTheirBounds()
        {
            // ComTac II: analog military class, every modulated field from the class.
            HeadsetElectronicsProfile comtac = Compose(ComTacII, Template(5f, 35f, 215f, 0.15f, 200f));
            Assert.That(comtac.QuietGainDb, Is.EqualTo(5f), "the common template gain leaves the class gain");
            Assert.That(comtac.ReleaseSeconds * 1000f, Is.EqualTo(215f).Within(1e-3f));
            Assert.That(comtac.AttackSeconds * 1000f, Is.EqualTo(35f).Within(1e-3f), "analog attack follows the template");
            Assert.That(comtac.Saturation, Is.EqualTo(0.45f).Within(1e-6f));
            Assert.That(comtac.MicHighpassHz, Is.EqualTo(200f));

            HeadsetElectronicsProfile louder = Compose(ComTacII, Template(8f, 35f, 215f, 0.15f, 200f));
            Assert.That(louder.QuietGainDb, Is.EqualTo(5f + 1.2f).Within(1e-5f));

            // ComTac V: digital class keeps its converter-speed attack.
            HeadsetElectronicsProfile digital = Compose(ComTacV, Template(5f, 20f, 140f, 0.1f, 180f));
            Assert.That(digital.AttackSeconds * 1000f, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(digital.ReleaseSeconds * 1000f, Is.EqualTo(140f).Within(1e-3f));
            Assert.That(digital.MicHighpassHz, Is.EqualTo(180f));

            // Family values are a reasoned choice; the template does not move them.
            HeadsetElectronicsProfile gssh = Compose(Gssh, Template(15f, 35f, 255f, 0.15f, 200f));
            Assert.That(gssh.Saturation, Is.EqualTo(0.7f));
            HeadsetElectronicsProfile razor = Compose(Razor, Template(15f, 1f, 320f, 0.14f, 250f));
            Assert.That(razor.QuietGainDb, Is.EqualTo(9f));
        }

        [Test]
        public void ItemsOfOneFamilyDifferOnlyThroughTheirTemplates()
        {
            // Epic's AMP (gain 8) and the base FAST RAC (gain 5) share one profile.
            HeadsetElectronicsProfile amp = Compose(Amp, Template(8f, 35f, 160f, 0.05f, 200f));
            HeadsetElectronicsProfile rac = Compose("5a16b9fffcdbcb0176308b34", Template(5f, 20f, 130f, 0.05f, 180f));
            Assert.That(amp.QuietGainDb - rac.QuietGainDb, Is.EqualTo(1.2f).Within(1e-5f));
            Assert.That(amp.ReleaseSeconds, Is.GreaterThan(rac.ReleaseSeconds));
            Assert.That(amp.NoiseDbFs, Is.EqualTo(rac.NoiseDbFs));
        }

        [Test]
        public void CompositionNeverTouchesThePassiveProfile()
        {
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            HeadsetProfile worn = HeadsetElectronicsComposer.Compose(profile, Template(15f, 40f, 320f, 0.2f, 300f));
            Assert.That(worn.Passive, Is.SameAs(profile.Passive));
            Assert.That(worn.ProfileId, Is.EqualTo(profile.ProfileId));
        }

        [Test]
        public void ProfileWithoutAClassKeepsItsElectronics()
        {
            HeadsetElectronicsProfile prototype = HeadphoneElectronicsGoldenTests.PrototypeElectronics();
            Assert.That(HeadsetElectronicsComposer.Compose("not-a-profile", prototype, Template(15f, 40f, 320f, 0.2f, 300f)),
                Is.SameAs(prototype));
        }

        [Test]
        public void TemplateElectronicsAreReadBesideTheSends()
        {
            var template = new EFT.InventoryLogic.HeadphonesTemplate
            {
                GunsCompressorSendLevel = -7f, CompressorGain = 8f, CompressorAttack = 35f,
                CompressorRelease = 160f, Distortion = 0.05f, HighpassFreq = 200
            };
            HeadsetSendLevels sends = HeadsetSendLevels.From(template);
            Assert.That(sends.Electronics.HasData, Is.True);
            Assert.That(sends.Electronics.CompressorGainDb, Is.EqualTo(8f));
            Assert.That(sends.Electronics.CompressorReleaseMs, Is.EqualTo(160f));
            Assert.That(sends.Electronics.HighpassHz, Is.EqualTo(200f));
            Assert.That(sends.ToString(), Does.Contain("gain=8"));

            Assert.That(HeadsetSendLevels.From(new EFT.InventoryLogic.HeadphonesTemplate()).Electronics.HasData, Is.False);
            var nan = new HeadsetTemplateElectronics(float.NaN, 1f, 1f, 1f, 1f);
            Assert.That(nan.Equals(new HeadsetTemplateElectronics(float.NaN, 1f, 1f, 1f, 1f)), Is.True,
                "a NaN template must not reactivate the route on every poll");
        }

        [Test]
        public void ControllerReactivatesForTheSliderAndTheTemplateButNotOnEveryPoll()
        {
            var backend = new RecordingBackend();
            var controller = new HeadphoneRouteController(backend);
            var template = new EFT.InventoryLogic.HeadphonesTemplate
            {
                GunsCompressorSendLevel = -7f, CompressorGain = 5f, CompressorAttack = 35f,
                CompressorRelease = 215f, Distortion = 0.15f, HighpassFreq = 200
            };
            HeadsetSendLevels sends = HeadsetSendLevels.From(template);
            Assert.That(controller.Apply(HeadphoneMode.Realistic, ComTacII, false, sends, 1f), Is.True);
            Assert.That(controller.Apply(HeadphoneMode.Realistic, ComTacII, false, sends, 1f), Is.True);
            Assert.That(backend.Profiles.Count, Is.EqualTo(1));
            Assert.That(backend.Profiles[0].Electronics.AttackSeconds * 1000f, Is.EqualTo(35f).Within(1e-3f),
                "the backend receives the worn item's composed electronics");

            Assert.That(controller.Apply(HeadphoneMode.Realistic, ComTacII, false, sends, 0.5f), Is.True);
            Assert.That(backend.Profiles.Count, Is.EqualTo(2));
            Assert.That(backend.Profiles[1].Electronics.PresenceDb, Is.EqualTo(3f));
            Assert.That(backend.Profiles[1].Electronics.MicFilterOrder, Is.EqualTo(2), "the slider leaves the band");

            template.CompressorRelease = 160f;
            Assert.That(controller.Apply(HeadphoneMode.Realistic, ComTacII, false, HeadsetSendLevels.From(template), 0.5f), Is.True);
            Assert.That(backend.Profiles.Count, Is.EqualTo(3));
            Assert.That(backend.Profiles[2].Electronics.ReleaseSeconds * 1000f, Is.EqualTo(160f).Within(1e-3f));
        }

        internal static HeadsetTemplateElectronics Template(float gain, float attackMs, float releaseMs,
            float distortion, float highpassHz) =>
            new HeadsetTemplateElectronics(gain, attackMs, releaseMs, distortion, highpassHz);

        private static HeadsetElectronicsProfile Device(string templateId)
        {
            Assert.That(HeadsetProfileRegistry.TryGet(templateId, out HeadsetProfile profile), Is.True);
            return profile.Electronics;
        }

        private static HeadsetElectronicsProfile Compose(string templateId, HeadsetTemplateElectronics template)
        {
            Assert.That(HeadsetProfileRegistry.TryGet(templateId, out HeadsetProfile profile), Is.True);
            return HeadsetElectronicsComposer.Compose(profile, template).Electronics;
        }

        private sealed class RecordingBackend : IHeadphoneRouteBackend
        {
            internal readonly List<HeadsetProfile> Profiles = new List<HeadsetProfile>();
            public bool TryActivate(HeadsetProfile profile, HeadsetSendLevels sends, out string reason)
            { Profiles.Add(profile); reason = ""; return true; }
            public bool TryRestore(out string reason) { reason = ""; return true; }
        }
    }
}
