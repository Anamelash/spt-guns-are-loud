using System;
using System.IO;
using BepInEx.Configuration;
using EFT.InventoryLogic;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class IndoorHeadphonesTests
    {
        private ModConfig _config;
        private ShotDescriptor _shot;

        [SetUp]
        public void Setup()
        {
            _config = new ModConfig(new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false });
            _shot = new ShotDescriptor { AmmoCaliber = "762x39", IsIndoor = true,
                HasActiveHeadphones = true, HeadphonesCompressorThresholdDb = -22 };
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void OutdoorsOrNoHeadphonesAreExactlyNeutral(bool indoor, bool headphones)
        {
            _shot.IsIndoor = indoor; _shot.HasActiveHeadphones = headphones;
            _config.IndoorHeadphonesDampingPercent.Value = 200;
            AssertNeutral(IndoorHeadphonesModel.Calculate(_shot, _config.GetTuning()));
        }

        private static void AssertNeutral(IndoorHeadphonesDamping d)
        {
            Assert.That(d.BodyGain, Is.EqualTo(1));
            Assert.That(d.TailDbPerSecond, Is.Zero);
            Assert.That(d.EarlyAttenuationDb, Is.Zero);
            Assert.That(d.ReverbAttenuationDb, Is.Zero);
            Assert.That(d.ReachMultiplier, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(100)]
        [TestCase(200)]
        public void LegacyControlIsNeutralAtEveryStoredValue(int percent)
        {
            _config.IndoorHeadphonesDampingPercent.Value = percent;
            var d = IndoorHeadphonesModel.Calculate(_shot, _config.GetTuning());
            AssertNeutral(d);
        }

        [Test]
        public void FitAndEquippedTemplateCannotReactivateLegacyDamping()
        {
            _config.HearingTrauma.Value = _config.Ringing.Value = 0;
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Loose;
            AssertNeutral(IndoorHeadphonesModel.Calculate(_shot, _config.GetTuning()));
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Tight;
            AssertNeutral(IndoorHeadphonesModel.Calculate(_shot, _config.GetTuning()));
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Normal;
            _shot.HeadphonesCompressorThresholdDb = -28;
            AssertNeutral(IndoorHeadphonesModel.Calculate(_shot, _config.GetTuning()));
        }

        [Test]
        public void LegacyValueIsPreservedButInactive()
        {
            TuningSnapshot before = _config.GetTuning();
            Assert.That(before.IndoorHeadphonesDampingPercent, Is.EqualTo(100));
            _config.IndoorHeadphonesDampingPercent.Value = 999;
            TuningSnapshot after = _config.GetTuning();
            Assert.That(after.IndoorHeadphonesDampingPercent, Is.EqualTo(200));
            Assert.That(after.PitchedLayerFadePercent, Is.EqualTo(before.PitchedLayerFadePercent));
            Assert.That(after.PitchedLayerGainDb, Is.EqualTo(before.PitchedLayerGainDb));
            Assert.That(after.PitchedLayerSemitones, Is.EqualTo(before.PitchedLayerSemitones));
            Assert.That(after.MasterSeverityScale, Is.EqualTo(before.MasterSeverityScale));
            _config.IndoorHeadphonesDampingPercent.Value = 0;
            AssertNeutral(IndoorHeadphonesModel.Calculate(_shot, _config.GetTuning()));
        }

        [Test]
        public void LegacyControlIsHiddenFromConfigurationManager()
        {
            object[] tags = _config.IndoorHeadphonesDampingPercent.Description.Tags;
            Assert.That(tags, Has.Some.Matches<object>(tag =>
                tag.GetType().GetField("Browsable")?.GetValue(tag) is bool visible && !visible));
        }

        [Test]
        public void HeadphoneSignalChainUsesTunedRealisticDefault()
        {
            TuningSnapshot tuning = _config.GetTuning();

            Assert.That(_config.HeadphoneMode.Value, Is.EqualTo(HeadphoneMode.Realistic));
            Assert.That(tuning.HeadphoneMode, Is.EqualTo(HeadphoneMode.Realistic));
        }

        [Test]
        public void SwitchingHeadphoneModePreservesLegacyValuesAndUnrelatedGunTuning()
        {
            _config.IndoorHeadphonesDampingPercent.Value = 173f;
            _config.PitchedLayerGainDb.Value = 11.5f;
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Loose;
            TuningSnapshot vanilla = _config.GetTuning();

            _config.HeadphoneMode.Value = HeadphoneMode.Realistic;
            TuningSnapshot realistic = _config.GetTuning();
            _config.HeadphoneMode.Value = HeadphoneMode.Vanilla;
            TuningSnapshot restored = _config.GetTuning();

            Assert.That(realistic.HeadphoneMode, Is.EqualTo(HeadphoneMode.Realistic));
            Assert.That(restored.HeadphoneMode, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(restored.IndoorHeadphonesDampingPercent,
                Is.EqualTo(vanilla.IndoorHeadphonesDampingPercent));
            Assert.That(restored.PitchedLayerGainDb, Is.EqualTo(vanilla.PitchedLayerGainDb));
            Assert.That(restored.HeadphonesFitOffsetDb, Is.EqualTo(vanilla.HeadphonesFitOffsetDb));
        }

        [Test]
        public void RoomAttenuationIsAppliedAfterMixAndNeverRaisesSend()
        {
            Assert.That(IndoorHeadphonesModel.RoomSend(-20, -4, 0.5f, 0), Is.EqualTo(-12));
            Assert.That(IndoorHeadphonesModel.RoomSend(-20, -4, 0.5f, 8), Is.EqualTo(-12));
            Assert.That(IndoorHeadphonesModel.RoomSend(-80, -4, 0, 8), Is.EqualTo(-80));
        }

        [TestCase(8000)]
        [TestCase(44100)]
        [TestCase(48000)]
        [TestCase(96000)]
        public void TailEnvelopePreservesAttackThenFadesSmoothlyInOutputTime(int rate)
        {
            var envelope = new HeadphoneTailEnvelope();
            envelope.Reset(rate, 48);
            int hold = (int)(rate * HeadphoneTailEnvelope.HoldSeconds);
            float previous = 1;
            for (int frame = 0; frame <= hold + rate; frame++)
            {
                float value = envelope.Next();
                if (frame <= hold) Assert.That(value, Is.EqualTo(1));
                Assert.That(value, Is.InRange(0f, previous));
                Assert.That(previous - value, Is.LessThan(0.001));
                previous = value;
            }
            Assert.That(previous, Is.EqualTo(Math.Pow(10, -48.0 / 20)).Within(0.000001));
        }

        [Test]
        public void ZeroIsIdentityAndOverlappingShotsHaveIndependentEnvelopes()
        {
            var first = new HeadphoneTailEnvelope(); first.Reset(48000, 48);
            for (int i = 0; i < 24000; i++) first.Next();
            var second = new HeadphoneTailEnvelope(); second.Reset(48000, 48);
            Assert.That(second.Next(), Is.EqualTo(1));
            Assert.That(first.Next(), Is.LessThan(0.1f));
            first.Reset(48000, 0);
            for (int i = 0; i < 480000; i++) Assert.That(first.Next(), Is.EqualTo(1));
        }

        [Test]
        public void EquippedHeadphonesWinOverDefaultMixerWithoutChangingEitherTemplate()
        {
            var item = new HeadphonesTemplate { ShortName = "TestHeadset", CompressorThreshold = -24 };
            var mixer = new HeadphonesTemplate { ShortName = "Default", CompressorThreshold = 0 };
            var state = HeadphonesResolver.Resolve(item, mixer, true);
            Assert.That(state.Active, Is.True);
            Assert.That(state.Name, Is.EqualTo("TestHeadset"));
            Assert.That(state.MixerName, Is.EqualTo("Default"));
            Assert.That(state.Source, Is.EqualTo("equipment"));
            Assert.That(state.ThresholdDb, Is.EqualTo(-24));
            Assert.That(mixer.CompressorThreshold, Is.Zero);
        }

        [Test]
        public void RemovingHeadphonesDoesNotKeepProtectionFromStaleMixer()
        {
            var mixer = new HeadphonesTemplate { ShortName = "TestHeadset", CompressorThreshold = -24 };
            Assert.That(HeadphonesResolver.Resolve(null, mixer, true).Active, Is.False);
            Assert.That(HeadphonesResolver.Resolve(null, mixer, false).Active, Is.True);
        }

        [TestCase("Default")]
        [TestCase("LowMute")]
        [TestCase("StrongMute")]
        [TestCase(null)]
        [TestCase("")]
        public void HelmetMuteIsNotAnActiveHeadsetFallback(string name)
        {
            var mixer = new HeadphonesTemplate { ShortName = name, CompressorThreshold = -20 };
            Assert.That(HeadphonesResolver.Resolve(null, mixer, false).Active, Is.False);
        }
    }
}
