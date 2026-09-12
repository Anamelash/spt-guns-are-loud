using System;
using System.IO;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;
namespace GunsAreLoud.Tests
{
    public sealed class BlastExposureTests
    {
        [Test]
        public void OutdoorExposureFallsWithSquareAndIndoorWithDistance()
        {
            Assert.That(BlastExposureState.Severity(8, false, 4, 2, 0), Is.EqualTo(.25f));
            Assert.That(BlastExposureState.Severity(16, false, 4, 2, 0), Is.EqualTo(.0625f));
            Assert.That(BlastExposureState.Severity(16, true, 4, 2, 0), Is.EqualTo(.5f));
            Assert.That(BlastExposureState.Severity(32, true, 4, 2, 0), Is.EqualTo(.25f));
            Assert.That(BlastExposureState.Severity(8, true, 4, 2, 0), Is.EqualTo(1));
        }
        [Test]
        public void ProtectionUsesEnergyRatioAndZeroDistanceIsBounded()
        {
            Assert.That(BlastExposureState.Severity(4, false, 4, 2, 10), Is.EqualTo(.1f).Within(.0001));
            Assert.That(BlastExposureState.Severity(0, false, 4, 2, 0), Is.EqualTo(1));
            Assert.That(BlastExposureState.Severity(float.NaN, false, 4, 2, 0), Is.Zero);
        }
        [Test]
        public void NearBlastHoldsThenRecoversAndEndsExactly()
        {
            var state = new BlastExposureState(); state.Add(1, 1, 45, 90, 180);
            Assert.That(state.Sample(0).Hearing, Is.Zero, "arrival delay");
            Assert.That(state.Sample(91).Hearing, Is.EqualTo(1).Within(.0001));
            Assert.That(state.Sample(136).Hearing, Is.EqualTo(.5f).Within(.0001));
            Assert.That(state.Sample(181).Hearing, Is.Zero);
            Assert.That(state.Sample(181).Ringing, Is.Zero);
        }
        [Test]
        public void RingingAndHearingHaveSeparateDurations()
        {
            var state = new BlastExposureState(); state.Add(0, .25f, 40, 100, 180);
            Assert.That(state.Sample(20).Hearing, Is.Zero);
            Assert.That(state.Sample(20).Ringing, Is.GreaterThan(0));
            Assert.That(state.Sample(50).Ringing, Is.Zero);
        }
        [Test]
        public void WeakRepeatDoesNotRefreshSevereBlastAndResetClearsEverything()
        {
            var state = new BlastExposureState(); state.Add(0, 1, 45, 90, 180);
            state.Add(175, .01f, 45, 90, 180);
            Assert.That(state.Sample(181).Severe, Is.False);
            Assert.That(state.Sample(181).Hearing, Is.Zero);
            state.Reset(); Assert.That(state.Sample(182).Ringing, Is.Zero);
        }
        [Test]
        public void ZeroRingingDurationRemainsSilentEvenForCloseBlast()
        {
            var state = new BlastExposureState(); state.Add(0, 1, 45, 0, 180);
            Assert.That(state.Sample(1).Ringing, Is.Zero);
            Assert.That(state.Sample(1).Hearing, Is.EqualTo(1));
        }

        private static TuningSnapshot Tuning(LoudnessPreset preset, float gunshotRinging = 100f)
        {
            var config = new ModConfig(new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false });
            config.Preset.Value = preset;
            config.Ringing.Value = gunshotRinging;
            return config.GetTuning();
        }

        [Test]
        public void FullBlastRingsAtLeastAsLoudAsTheLoudestGunshotRingingOnEveryPreset()
        {
            foreach (LoudnessPreset preset in Enum.GetValues(typeof(LoudnessPreset)))
            foreach (float gunshotRinging in new[] { 0f, 50f, 100f, 200f })
            {
                TuningSnapshot tuning = Tuning(preset, gunshotRinging);
                // Two unprotected rifle shots saturate the dose; that ringing must
                // stay masked by a full-strength blast instead of rising above it.
                float gunshotAtMaximumDose = HearingResponseModel.Calculate(
                    tuning.MaximumDose, tuning.MaximumDose, tuning, 48000).TinnitusLeft;
                float blast = BlastExposureState.RingLevel(1f, 100f, HearingResponseModel.TinnitusCeiling(tuning));
                Assert.That(blast, Is.GreaterThanOrEqualTo(gunshotAtMaximumDose - 1e-6f),
                    preset + " with gunshot ringing " + gunshotRinging + "%");
            }
        }

        [Test]
        public void BalancedCloseBlastNoLongerRingsQuieterThanTwoRifleShots()
        {
            // 2026-09-11 log: blast at 3.6 m without protection, then doseL=4.000.
            // The fixed blast level was 0.008 against a 0.014 gunshot ceiling.
            TuningSnapshot tuning = Tuning(LoudnessPreset.Balanced);
            float ceiling = HearingResponseModel.TinnitusCeiling(tuning);
            Assert.That(ceiling, Is.EqualTo(.014f).Within(1e-6f));
            Assert.That(BlastExposureState.RingLevel(1f, 100f, ceiling), Is.EqualTo(ceiling).Within(1e-6f));
        }

        [Test]
        public void ExplosionStrengthAndEnvelopeStillScaleTheRinging()
        {
            Assert.That(BlastExposureState.RingLevel(1f, 50f, .014f), Is.EqualTo(.007f).Within(1e-6f));
            Assert.That(BlastExposureState.RingLevel(.5f, 100f, .014f), Is.EqualTo(.007f).Within(1e-6f));
            Assert.That(BlastExposureState.RingLevel(1f, 200f, .014f), Is.EqualTo(.028f).Within(1e-6f));
            Assert.That(BlastExposureState.RingLevel(1f, 0f, .014f), Is.Zero);
            Assert.That(BlastExposureState.RingLevel(0f, 100f, .014f), Is.Zero);
        }

        [Test]
        public void WithoutGunshotRingingTheBlastKeepsItsOwnLevel()
        {
            Assert.That(BlastExposureState.RingLevel(1f, 100f, 0f), Is.EqualTo(BlastExposureState.BaseRingLevel));
            Assert.That(BlastExposureState.RingLevel(1f, 100f, float.NaN), Is.EqualTo(BlastExposureState.BaseRingLevel));
            Assert.That(BlastExposureState.RingLevel(float.NaN, 100f, .014f), Is.Zero);
            Assert.That(HearingResponseModel.TinnitusCeiling(Tuning(LoudnessPreset.Balanced, 0f)), Is.Zero);
        }
    }
}
