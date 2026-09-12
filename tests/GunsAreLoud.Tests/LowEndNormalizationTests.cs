using System;
using System.IO;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class LowEndNormalizationTests
    {
        private const int Rate = 48000;
        private ModConfig _config;

        [SetUp]
        public void Setup()
        {
            var file = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            _config = new ModConfig(file);
            // Most tests isolate the calibrated 100% model from the release
            // preset, which intentionally uses the user's tuned filters and
            // cartridge contrast.
            _config.PitchedLayerSemitones.Value = 12f;
            _config.PitchedLayerHighpassHz.Value = 35f;
            _config.PitchedLayerLowpassHz.Value = 260f;
            _config.LowEndNormalizationDb.Value = ModConfig.LowEndNormalizationSpanDb;
            _config.CartridgeContrastDb.Value = ModConfig.CartridgeContrastSpanDb;
            LowEndNormalizationCache.Clear();
        }

        [TearDown]
        public void Cleanup() => LowEndNormalizationCache.Clear();

        private static float[] Tone(float amplitude, float frequency = 280f, int leadingFrames = 0)
        {
            var pcm = new float[Rate / 3 * 2];
            for (int frame = leadingFrames; frame < pcm.Length / 2; frame++)
            {
                float v = amplitude * (float)Math.Sin(2 * Math.PI * frequency * (frame - leadingFrames) / Rate);
                pcm[frame * 2] = pcm[frame * 2 + 1] = v;
            }
            return pcm;
        }

        private static float[] BodyAndDecay(float body, float decay, float outputSeconds = 1.1f)
        {
            // Build in native time so the boundary is 180 ms after pitch conversion,
            // matching the window used by the production analyser.
            float pitch = PitchedGunshotLayer.CalculatePitchRatio(12.01408f);
            int frames = (int)(Rate * outputSeconds * pitch) + 2;
            int boundary = (int)(Rate * LowEndLevelModel.WindowSeconds * pitch);
            var pcm = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                float amplitude = frame < boundary ? body : decay;
                float value = amplitude * (float)Math.Sin(2 * Math.PI * 280 * frame / Rate);
                pcm[frame * 2] = pcm[frame * 2 + 1] = value;
            }
            return pcm;
        }

        private LocalGunshotAudioTuning AudioTuning(bool normalizeBass = false)
        {
            TuningSnapshot t = _config.GetTuning();
            return new LocalGunshotAudioTuning(0, 0.3f, 99,
                AutomaticPitchedRoute.CachedReport, t.PitchedLayerSemitones,
                t.PitchedLayerHighpassHz, t.PitchedLayerLowpassHz, t.PitchedLayerFadePercent,
                t.AutomaticPitchedTailSeconds, t.PitchedLayerGainDb, t.PitchedLayerOcclusion,
                t.PitchedLayerOccludedLowpassHz, 0.08f, false, 0, 0, 0, 0,
                t.AutomaticTailMode, t.LowEndNormalizationPercent, t.CaliberContrastPercent, normalizeBass: normalizeBass);
        }

        [Test]
        public void AlternatingPistolVariantsMatchBassDespiteDifferentSpectralBalance()
        {
            _config.PitchedLayerHighpassHz.Value = 10;
            _config.PitchedLayerLowpassHz.Value = 2000;
            float[] a = Tone(.8f, 150), b = Tone(.4f, 150), texture = Tone(.5f, 1600);
            for (int i = 0; i < a.Length; i++) { a[i] += texture[i]; b[i] += texture[i]; }
            LowEndNormalizationCache.Register(1, Rate, a, 1, 0);
            LowEndNormalizationCache.Register(2, Rate, b, 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            var first = LowEndNormalizationCache.Evaluate(1, AudioTuning(true));
            var second = LowEndNormalizationCache.Evaluate(2, AudioTuning(true));
            Assert.That(first.Ready && second.Ready, Is.True);
            Assert.That(first.MeasuredRms * first.BodyGain, Is.EqualTo(.1f).Within(.0001));
            Assert.That(second.MeasuredRms * second.BodyGain, Is.EqualTo(.1f).Within(.0001));
            Assert.That(first.BodyGain, Is.LessThan(second.BodyGain));
            Assert.That(first.Limited || second.Limited, Is.False);
            Assert.That(LowEndNormalizationCache.Evaluate(1, AudioTuning(false)).BodyGain,
                Is.Not.EqualTo(first.BodyGain), "rifle/automatic calibration must retain its wide-band path");
        }

        private void RegisterPair()
        {
            LowEndNormalizationCache.Register(1, Rate, Tone(0.1f), 1, 0);
            LowEndNormalizationCache.Register(2, Rate, Tone(0.2f), 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
        }

        private float WarmOne(float[] pcm, int group = 0)
        {
            LowEndNormalizationCache.Clear();
            LowEndNormalizationCache.Register(1, Rate, pcm, 1, group);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            return LowEndNormalizationCache.Gain(1, AudioTuning(), out _);
        }

        private LowEndNormalizationResult WarmDynamics(float[] pcm)
        {
            LowEndNormalizationCache.Clear();
            LowEndNormalizationCache.Register(1, Rate, pcm, 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            return LowEndNormalizationCache.Evaluate(1, AudioTuning());
        }

        [Test]
        public void SameDecayUsesItsOwnCorrectionWhenNeitherRecoveryBoundIsActive()
        {
            LowEndNormalizationResult loudAttack = WarmDynamics(BodyAndDecay(0.59354f, 0.07f));
            LowEndNormalizationResult quieterAttack = WarmDynamics(BodyAndDecay(0.40f, 0.07f));

            Assert.That(loudAttack.DecayReady && quieterAttack.DecayReady, Is.True);
            Assert.That(loudAttack.BodyGain, Is.LessThan(quieterAttack.BodyGain));
            Assert.That(loudAttack.DecayGain, Is.EqualTo(quieterAttack.DecayGain).Within(0.015f),
                "The same decay must not inherit attenuation from an unrelated attack level.");
        }

        [Test]
        public void LoggedGlockLikeQuietDecayRecoveryIsBoundedRatherThanBroadlyAmplified()
        {
            LowEndNormalizationResult logged = WarmDynamics(BodyAndDecay(0.59354f, 0.01397f));
            Assert.That(logged.DecayReady, Is.True);
            Assert.That(logged.DecayGain, Is.GreaterThan(logged.BodyGain));
            Assert.That(logged.DecayGain, Is.LessThanOrEqualTo(logged.BodyGain * 1.995263f));
            Assert.That(logged.DecayGain, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void StrongDesertEagleLikeDecayNeverReceivesBroadAmplification()
        {
            // Logged Desert Eagle: 0.65329 attack and 0.04198 tail output.
            LowEndNormalizationResult result = WarmDynamics(BodyAndDecay(0.65329f, 0.04198f));
            Assert.That(result.DecayReady, Is.True);
            Assert.That(result.DecayGain, Is.InRange(result.BodyGain, 1f));
            Assert.That(result.DecayRms * result.DecayGain, Is.LessThanOrEqualTo(result.DecayRms + 0.000001f));
        }

        [Test]
        public void ShortSourceIsUnavailableDecayRatherThanMeasuredSilence()
        {
            LowEndNormalizationResult shortSource = WarmDynamics(BodyAndDecay(0.3f, 0.02f, 0.23f));
            Assert.That(shortSource.Ready, Is.True);
            Assert.That(shortSource.DecayReady, Is.False);
            Assert.That(shortSource.DecayRms, Is.Zero);
            Assert.That(shortSource.DecayGain, Is.EqualTo(shortSource.BodyGain));

            LowEndNormalizationResult longEnough = WarmDynamics(BodyAndDecay(0.3f, 0.02f, 1.10f));
            Assert.That(longEnough.DecayReady, Is.True);
            Assert.That(longEnough.DecayRms, Is.GreaterThan(0.001f));
        }

        [Test]
        public void BodyOnlyAutomaticCaptureDoesNotMeasureSlowPitchBodyAsDecay()
        {
            float pitch = PitchedGunshotLayer.CalculatePitchRatio(12.01408f);
            float nativeBodySeconds = 0.40f * pitch;
            float[] bodyOnly = BodyAndDecay(0.3f, 0.3f, 0.40f);
            LowEndNormalizationCache.Clear();
            LowEndNormalizationCache.Register(1, Rate, bodyOnly, 1f, 0, nativeBodySeconds);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            LowEndNormalizationResult result = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            Assert.That(result.Ready, Is.True);
            Assert.That(result.DecayReady, Is.False);
            Assert.That(result.DecayGain, Is.EqualTo(result.BodyGain));
        }

        [Test]
        public void ZeroPercentIsExactBodyAndDecayBypass()
        {
            WarmDynamics(BodyAndDecay(0.59354f, 0.01397f));
            _config.LowEndNormalizationDb.Value = 0;
            LowEndNormalizationResult result = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            Assert.That(result.Ready, Is.True);
            Assert.That(result.BodyGain, Is.EqualTo(1f));
            Assert.That(result.DecayGain, Is.EqualTo(1f));
        }

        [Test]
        public void SeparatelyWarmedRecordingsReachSameBaseLevel()
        {
            float[] quiet = Tone(0.1f), loud = Tone(0.2f);
            float first = LowEndLevelModel.Measure(quiet, Rate, 0.5f, 35, 260) * WarmOne(quiet);
            float second = LowEndLevelModel.Measure(loud, Rate, 0.5f, 35, 260) * WarmOne(loud);
            Assert.That(second, Is.EqualTo(first).Within(0.00001));
        }

        [Test]
        public void AddingOtherWeaponsCannotChangeExistingCorrection()
        {
            float original = WarmOne(Tone(0.1f));
            LowEndNormalizationCache.Register(2, Rate, Tone(0.3f), 1, 0);
            LowEndNormalizationCache.Register(3, Rate, Tone(0.5f), 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            Assert.That(LowEndNormalizationCache.Gain(1, AudioTuning(), out _),
                Is.EqualTo(original).Within(0.00001));
        }

        [Test]
        public void FullNormalizationEqualizesScaledRecordings()
        {
            RegisterPair();
            float a = LowEndNormalizationCache.Gain(1, AudioTuning(), out bool aReady);
            float b = LowEndNormalizationCache.Gain(2, AudioTuning(), out bool bReady);
            Assert.That(aReady && bReady, Is.True);
            Assert.That(a, Is.GreaterThan(1));
            Assert.That(b, Is.LessThan(1));
            Assert.That(0.1f * a, Is.EqualTo(0.2f * b).Within(0.00001));
        }

        [Test]
        public void DifferentSpectraReachTheSameProcessedBaseLevel()
        {
            float[] first = Tone(0.1f, 240f), second = Tone(0.2f, 560f);
            LowEndNormalizationCache.Register(1, Rate, first, 1, 0);
            LowEndNormalizationCache.Register(2, Rate, second, 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            float a = LowEndLevelModel.Measure(first, Rate, 0.5f, 35, 260) *
                LowEndNormalizationCache.Gain(1, AudioTuning(), out _);
            float b = LowEndLevelModel.Measure(second, Rate, 0.5f, 35, 260) *
                LowEndNormalizationCache.Gain(2, AudioTuning(), out _);
            Assert.That(a, Is.EqualTo(b).Within(0.00001));
        }

        [Test]
        public void NormalizedRifleOutweighsBassierPistolRecordingOnlyWhenContrastIsEnabled()
        {
            float[] pistol = Tone(0.3f, 240f), rifle = Tone(0.12f, 400f);
            LowEndNormalizationCache.Register(1, Rate, pistol, 1, 0);
            LowEndNormalizationCache.Register(2, Rate, rifle, 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            float p = LowEndLevelModel.Measure(pistol, Rate, 0.5f, 35, 260) *
                LowEndNormalizationCache.Gain(1, AudioTuning(), out _);
            float r = LowEndLevelModel.Measure(rifle, Rate, 0.5f, 35, 260) *
                LowEndNormalizationCache.Gain(2, AudioTuning(), out _);
            Func<float, float> ratio = contrast =>
                r * PitchedGunshotLayer.CalculateLayerGain(0.3f, 99, 18, contrast) /
                (p * PitchedGunshotLayer.CalculateLayerGain(0.3f, 113, 18, contrast));
            Assert.That(ratio(0), Is.EqualTo(1).Within(0.00001));
            Assert.That(ratio(100), Is.GreaterThan(1));
            Assert.That(ratio(200), Is.GreaterThan(ratio(100)));
        }

        [Test]
        public void LiveFilterKeepsChannelsIndependentAndIsLinear()
        {
            var first = new PitchedBandPassState(Rate, 35, 260);
            var second = new PitchedBandPassState(Rate, 35, 260);
            float[] pcm = Tone(0.2f);
            for (int frame = 0; frame < 4096; frame++)
            {
                float input = pcm[frame * 2];
                Assert.That(first.Process(input, 0) * 0.5f,
                    Is.EqualTo(second.Process(input * 0.5f, 0)).Within(0.000001));
                Assert.That(first.Process(0, 1), Is.Zero);
            }
        }

        [Test]
        public void UnknownOrClearedCacheNeverReplaysOrUsesStaleGain()
        {
            RegisterPair();
            Assert.That(LowEndNormalizationCache.Gain(99, AudioTuning(), out bool unknown), Is.EqualTo(1));
            Assert.That(unknown, Is.False);
            LowEndNormalizationCache.Clear();
            Assert.That(LowEndNormalizationCache.Gain(1, AudioTuning(), out bool cleared), Is.EqualTo(1));
            Assert.That(cleared, Is.False);
            Assert.That(LowEndNormalizationCache.SourceCount, Is.Zero);
        }

        [Test]
        public void PartialNormalizationBlendsInDecibels()
        {
            Assert.That(LowEndLevelModel.Correction(0.1f, 0.2f, 0), Is.EqualTo(1));
            Assert.That(LowEndLevelModel.Correction(0.1f, 0.2f, 50), Is.EqualTo(Math.Sqrt(2)).Within(0.00001));
            Assert.That(LowEndLevelModel.Correction(0.1f, 0.2f, 100), Is.EqualTo(2).Within(0.00001));
            Assert.That(LowEndLevelModel.Correction(0.1f, 0.2f, 150), Is.EqualTo(Math.Pow(2, 1.5)).Within(0.00001));
        }

        [Test]
        public void EnvelopeAcceptsAttenuationAndDoesNotCapNormalizationAtUserGainLimit()
        {
            Assert.That(PitchedGunshotEnvelopeFilter.ClampPostFilterGain(0.25f), Is.EqualTo(0.25f));
            Assert.That(PitchedGunshotEnvelopeFilter.ClampPostFilterGain(64f), Is.EqualTo(64f));
            Assert.That(PitchedGunshotEnvelopeFilter.ClampPostFilterGain(1000f), Is.EqualTo(125.89255f));
            Assert.That(PitchedGunshotEnvelopeFilter.ClampPostFilterGain(-1), Is.Zero);
        }

        [Test]
        public void CorrectionIsBoundedAndDoesNotAmplifySilenceOrInvalidLevels()
        {
            Assert.That(LowEndLevelModel.Correction(0, 0.1f, 100), Is.EqualTo(1));
            Assert.That(LowEndLevelModel.Correction(0.000001f, 0.1f, 100), Is.EqualTo(1));
            Assert.That(LowEndLevelModel.Correction(float.NaN, 0.1f, 100), Is.EqualTo(1));
            Assert.That(LowEndLevelModel.Correction(float.PositiveInfinity, 0.1f, 100), Is.EqualTo(1));
            Assert.That(LowEndLevelModel.Correction(0.001f, 1, 100), Is.EqualTo(3.98107).Within(0.00001));
            Assert.That(LowEndLevelModel.Correction(1, 0.001f, 100), Is.EqualTo(0.251189).Within(0.00001));
        }

        [Test]
        public void MeasurementUsesEnergyInSelectedBandAndPitch()
        {
            float[] pcm = Tone(0.2f, 900f);
            float lowPitch = LowEndLevelModel.Measure(pcm, Rate, 0.25f, 35, 260);
            float highPitch = LowEndLevelModel.Measure(pcm, Rate, 0.95f, 35, 260);
            float openBand = LowEndLevelModel.Measure(pcm, Rate, 0.95f, 35, 1800);
            float highCut = LowEndLevelModel.Measure(pcm, Rate, 0.25f, 280, 300);
            Assert.That(lowPitch, Is.GreaterThan(highPitch * 3));
            Assert.That(openBand, Is.GreaterThan(highPitch * 3));
            Assert.That(highCut, Is.LessThan(lowPitch));
            Assert.That(LowEndLevelModel.Measure(new float[Rate], Rate, 0.5f, 35, 260), Is.Zero);
        }

        [Test]
        public void LeadingAndTrailingSilenceDoNotDiluteImpactLevel()
        {
            float a = LowEndLevelModel.Measure(Tone(0.2f), Rate, 0.5f, 35, 260);
            float b = LowEndLevelModel.Measure(Tone(0.2f, 280, Rate / 20), Rate, 0.5f, 35, 260);
            Assert.That(b, Is.EqualTo(a).Within(a * 0.02f));
            float[] padded = new float[Rate * 4];
            Array.Copy(Tone(0.2f), padded, Tone(0.2f).Length);
            Assert.That(LowEndLevelModel.Measure(padded, Rate, 0.5f, 35, 260), Is.EqualTo(a).Within(0.00001));
        }

        [Test]
        public void StereoPolarityDoesNotCancelLevelMeasurement()
        {
            float[] pcm = Tone(0.2f);
            float expected = LowEndLevelModel.Measure(pcm, Rate, 0.5f, 35, 260);
            for (int i = 1; i < pcm.Length; i += 2) pcm[i] = -pcm[i];
            Assert.That(LowEndLevelModel.Measure(pcm, Rate, 0.5f, 35, 260), Is.EqualTo(expected).Within(0.00001));
        }

        [Test]
        public void CutoffEditRecalculatesLevelsWithoutReloadingRawPcmOrMovingTarget()
        {
            RegisterPair();
            float before = LowEndNormalizationCache.Gain(1, AudioTuning(), out _);
            int passes = LowEndNormalizationCache.MeasurementCount;
            _config.PitchedLayerLowpassHz.Value = 80;
            Assert.That(LowEndNormalizationCache.Gain(1, AudioTuning(), out bool pending), Is.EqualTo(1));
            Assert.That(pending, Is.False);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            float after = LowEndNormalizationCache.Gain(1, AudioTuning(), out bool ready);
            Assert.That(ready, Is.True);
            Assert.That(after, Is.GreaterThan(before));
            Assert.That(LowEndNormalizationCache.SourceCount, Is.EqualTo(2));
            Assert.That(LowEndNormalizationCache.MeasurementCount, Is.EqualTo(passes + 2));
        }

        [Test]
        public void OldQueuedSettingsCannotInvalidateNewAnalysis()
        {
            RegisterPair();
            LocalGunshotAudioTuning old = AudioTuning();
            _config.PitchedLayerSemitones.Value = 18;
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            int passes = LowEndNormalizationCache.MeasurementCount;
            LowEndNormalizationCache.Gain(1, old, out bool oldReady);
            LowEndNormalizationCache.Gain(1, AudioTuning(), out bool newReady);
            Assert.That(oldReady, Is.False);
            Assert.That(newReady, Is.True);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            Assert.That(LowEndNormalizationCache.MeasurementCount, Is.EqualTo(passes));
        }

        [Test]
        public void CompletedNormalizationWorkLeavesNoPerFrameSourceScan()
        {
            LowEndNormalizationCache.Register(1, Rate, Tone(0.1f), 1, 0);
            LowEndNormalizationCache.Register(2, Rate, Tone(0.2f), 1, 0);
            Assert.That(LowEndNormalizationCache.PendingCount, Is.EqualTo(2));
            for (int pass = 0; pass < 8 && LowEndNormalizationCache.PendingCount > 0; pass++)
                LowEndNormalizationCache.Refresh(_config.GetTuning());
            Assert.That(LowEndNormalizationCache.PendingCount, Is.Zero);
            int measurements = LowEndNormalizationCache.MeasurementCount;
            for (int pass = 0; pass < 100; pass++)
                LowEndNormalizationCache.Refresh(_config.GetTuning());
            Assert.That(LowEndNormalizationCache.MeasurementCount, Is.EqualTo(measurements));
            Assert.That(LowEndNormalizationCache.PendingCount, Is.Zero);
        }

        [Test]
        public void GainFadeAndOcclusionEditsDoNotGetNormalizedAway()
        {
            RegisterPair();
            float before = LowEndNormalizationCache.Gain(1, AudioTuning(), out _);
            int passes = LowEndNormalizationCache.MeasurementCount;
            _config.PitchedLayerGainDb.Value = 30;
            _config.PitchedLayerFadePercent.Value = 100;
            _config.PitchedLayerOcclusion.Value = PitchedLayerOcclusionMode.Enhanced;
            _config.PitchedLayerOccludedLowpassHz.Value = 50;
            _config.HeadphonesFit.Value = HeadphonesFitPreset.Tight;
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            Assert.That(LowEndNormalizationCache.Gain(1, AudioTuning(), out _), Is.EqualTo(before));
            Assert.That(LowEndNormalizationCache.MeasurementCount, Is.EqualTo(passes));
            Assert.That(AudioTuning().PitchedLayerGainDb, Is.EqualTo(30));
            Assert.That(AudioTuning().PitchedLayerFadePercent, Is.EqualTo(100));
            var shot = new ShotDescriptor { IsIndoor = true, HasActiveHeadphones = true,
                AmmoCaliber = "762x39", HeadphonesCompressorThresholdDb = -22 };
            float damping = IndoorHeadphonesModel.Calculate(shot, _config.GetTuning()).BodyGain;
            Assert.That(damping, Is.EqualTo(1), "The preserved legacy setting must not alter either headphone mode.");
        }

        [Test]
        public void SuppressedAndIndoorReferenceGroupsStaySeparate()
        {
            LowEndNormalizationCache.Register(1, Rate, Tone(0.2f), 1, 0);
            LowEndNormalizationCache.Register(2, Rate, Tone(0.01f), 1, 1);
            LowEndNormalizationCache.Register(3, Rate, Tone(0.4f), 1, 2);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            for (int i = 1; i <= 3; i++)
            {
                LowEndNormalizationResult result = LowEndNormalizationCache.Evaluate(i, AudioTuning());
                Assert.That(result.Ready, Is.True);
                Assert.That(result.Limited, Is.False);
                Assert.That(result.MeasuredRms * result.Gain,
                    Is.EqualTo(LowEndLevelModel.TargetRms(i - 1)).Within(0.00001));
            }
        }

        [Test]
        public void BankVolumeAndAliasesPreserveBothBodyAndDecayCorrections()
        {
            LowEndNormalizationCache.Register(1, Rate, BodyAndDecay(0.2f, 0.03f), 0.5f, 0);
            LowEndNormalizationCache.Register(2, Rate, BodyAndDecay(0.2f, 0.03f), 1f, 0);
            LowEndNormalizationCache.Alias(3, 1);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            LowEndNormalizationResult a = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            LowEndNormalizationResult b = LowEndNormalizationCache.Evaluate(2, AudioTuning());
            LowEndNormalizationResult alias = LowEndNormalizationCache.Evaluate(3, AudioTuning());
            Assert.That(0.5f * a.BodyGain, Is.EqualTo(b.BodyGain).Within(0.00001));
            Assert.That(a.DecayReady && b.DecayReady, Is.True);
            Assert.That(alias.BodyGain, Is.EqualTo(a.DecayGain),
                "A release-tail alias starts at decay and must not replay body attenuation first.");
            Assert.That(alias.DecayGain, Is.EqualTo(a.DecayGain));
            Assert.That(LowEndNormalizationCache.SourceCount, Is.EqualTo(2));
        }

        [Test]
        public void ContrastHasNoHiddenCaliberGainAtZeroAndGrowsAtTwoHundred()
        {
            TuningSnapshot tuning = _config.GetTuning();
            var pistol = new ShotDescriptor { AmmoCaliber = "9x19PARA", IsIndoor = true };
            var rifle = new ShotDescriptor { AmmoCaliber = "545x39", IsIndoor = true };
            float body = DirectLoudnessModel.CalculatePitchedBodyGain(pistol, tuning);
            Assert.That(DirectLoudnessModel.CalculatePitchedBodyGain(rifle, tuning), Is.EqualTo(body));
            Func<float, float> ratio = contrast =>
                PitchedGunshotLayer.CalculateLayerGain(body, 99, 0, contrast) /
                PitchedGunshotLayer.CalculateLayerGain(body, 113, 0, contrast);
            Assert.That(ratio(0), Is.EqualTo(1));
            Assert.That(ratio(100), Is.GreaterThan(1));
            Assert.That(ratio(200), Is.EqualTo(ratio(100) * ratio(100)).Within(0.00001));
            Assert.That(ratio(300), Is.EqualTo(ratio(100) * ratio(100) * ratio(100)).Within(0.00001));
            rifle.IsSuppressed = true;
            Assert.That(DirectLoudnessModel.CalculatePitchedBodyGain(rifle, tuning), Is.LessThan(body));
        }

        [Test]
        public void F12RangesDefaultsAndLiveValuesAreCorrect()
        {
            var release = new ModConfig(new ConfigFile(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false });

            Assert.That(release.LowEndNormalizationDb.Value, Is.EqualTo(12));
            Assert.That(release.CartridgeContrastDb.Value, Is.EqualTo(6));
            Assert.That(release.PitchedLayerLowpassHz.Value, Is.EqualTo(2000));
            Assert.That(release.AutomaticPitchedTailMs.Value, Is.EqualTo(30));
            Assert.That(((AcceptableValueRange<float>)release.LowEndNormalizationDb.Description.AcceptableValues).MaxValue, Is.EqualTo(18));
            Assert.That(((AcceptableValueRange<float>)release.CartridgeContrastDb.Description.AcceptableValues).MaxValue, Is.EqualTo(9));
            Assert.That(((AcceptableValueRange<float>)release.PitchedLayerLowpassHz.Description.AcceptableValues).MaxValue, Is.EqualTo(3000));
            Assert.That(((AcceptableValueRange<float>)release.AutomaticPitchedTailMs.Description.AcceptableValues).MaxValue, Is.EqualTo(600));

            release.LowEndNormalizationDb.Value = 18;
            release.CartridgeContrastDb.Value = 9;
            release.PitchedLayerLowpassHz.Value = 3000;
            release.AutomaticPitchedTailMs.Value = 600;
            TuningSnapshot maximum = release.GetTuning();
            Assert.That(maximum.LowEndNormalizationPercent, Is.EqualTo(150));
            Assert.That(maximum.CaliberContrastPercent, Is.EqualTo(300));
            Assert.That(maximum.PitchedLayerLowpassHz, Is.EqualTo(3000));
            Assert.That(maximum.AutomaticPitchedTailSeconds, Is.EqualTo(0.6f).Within(0.0001f));
        }

        [Test]
        public void FixedTargetsPreserveExplicitEnvironmentAndSuppressorDifferences()
        {
            Assert.That(LowEndLevelModel.TargetRms(0), Is.EqualTo(0.1f));
            Assert.That(20 * Math.Log10(LowEndLevelModel.TargetRms(2) / LowEndLevelModel.TargetRms(0)),
                Is.EqualTo(3).Within(0.00001));
            Assert.That(20 * Math.Log10(LowEndLevelModel.TargetRms(1) / LowEndLevelModel.TargetRms(0)),
                Is.EqualTo(-12).Within(0.00001));
            Assert.That(20 * Math.Log10(LowEndLevelModel.TargetRms(3) / LowEndLevelModel.TargetRms(2)),
                Is.EqualTo(-12).Within(0.00001));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EquipOrderClearAndRewarmCannotChangeSourceCorrection(bool reverse)
        {
            float expected = WarmOne(Tone(0.1f));
            LowEndNormalizationCache.Clear();
            int first = reverse ? 2 : 1, second = reverse ? 1 : 2;
            LowEndNormalizationCache.Register(first, Rate, Tone(first * 0.1f), 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            LowEndNormalizationCache.Register(second, Rate, Tone(second * 0.1f), 1, 0);
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            Assert.That(LowEndNormalizationCache.Gain(1, AudioTuning(), out _), Is.EqualTo(expected));
            Assert.That(WarmOne(Tone(0.1f)), Is.EqualTo(expected));
        }

        [Test]
        public void DiagnosticsExposeMeasuredLevelTargetAndCorrectionLimit()
        {
            WarmOne(Tone(0.001f));
            LowEndNormalizationResult result = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            Assert.That(result.Ready, Is.True);
            Assert.That(result.MeasuredRms, Is.GreaterThan(LowEndLevelModel.MinimumRms));
            Assert.That(result.TargetRms, Is.EqualTo(0.1f));
            Assert.That(result.Limited, Is.True);
            Assert.That(result.Gain, Is.EqualTo(3.98107).Within(0.00001));
            WarmOne(Tone(0.1f));
            Assert.That(LowEndNormalizationCache.Evaluate(1, AudioTuning()).Limited, Is.False);
            Assert.That(LowEndNormalizationCache.Evaluate(99, AudioTuning()).Ready, Is.False);
        }

        [Test]
        public void FixedAnchorDoesNotMoveWhenF12BandChanges()
        {
            RegisterPair();
            float target = LowEndNormalizationCache.Evaluate(1, AudioTuning()).TargetRms;
            _config.PitchedLayerSemitones.Value = 7.586855f;
            _config.PitchedLayerHighpassHz.Value = 71.26761f;
            _config.PitchedLayerLowpassHz.Value = 683.9437f;
            LowEndNormalizationCache.Refresh(_config.GetTuning());
            LowEndNormalizationResult result = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            Assert.That(result.TargetRms, Is.EqualTo(target));
            Assert.That(result.MeasuredRms * result.Gain, Is.EqualTo(target).Within(0.00001));
        }

        private static float[] FullPrefixTone(float amplitude)
        {
            // Longer than the retained analysis prefix, so one registration
            // costs the full per-source budget after truncation.
            var pcm = new float[(int)(Rate * 1.3f) * 2];
            for (int frame = 0; frame < pcm.Length / 2; frame++)
                pcm[frame * 2] = pcm[frame * 2 + 1] =
                    amplitude * (float)Math.Sin(2 * Math.PI * 280f * frame / Rate);
            return pcm;
        }

        private void Drain()
        {
            // The first pass is unconditional: it is what publishes an F12 band
            // change, and only then is there anything pending to measure.
            TuningSnapshot tuning = _config.GetTuning();
            for (int pass = 0; pass < 64; pass++)
            {
                LowEndNormalizationCache.Refresh(tuning);
                if (LowEndNormalizationCache.PendingCount == 0) break;
            }
        }

        // One registration retains the truncated analysis prefix, so the number
        // of sources that fit is fixed. Looping on the counter itself would
        // never end: trimming keeps it just under the budget by construction.
        private const int PrefixSamples = (int)(Rate * 1.25f) * 2;

        private int FillToRetentionBudget()
        {
            int capacity = (int)(LowEndNormalizationCache.MaximumRetainedSamples / PrefixSamples);
            for (int id = 1; id <= capacity; id++)
            {
                LowEndNormalizationCache.Register(id, Rate, FullPrefixTone(0.1f), 1, 0);
                Drain();
            }
            return capacity;
        }

        [Test]
        public void AnalysisPrefixesStayWithinTheRetentionBudget()
        {
            int registered = FillToRetentionBudget();
            LowEndNormalizationCache.Register(registered + 1, Rate, FullPrefixTone(0.1f), 1, 0);
            Drain();

            Assert.That(registered, Is.GreaterThan(1));
            Assert.That(LowEndNormalizationCache.RetainedSamples,
                Is.LessThanOrEqualTo(LowEndNormalizationCache.MaximumRetainedSamples));
            Assert.That(LowEndNormalizationCache.SourceCount, Is.EqualTo(registered + 1));
        }

        [Test]
        public void ReleasingAPrefixKeepsTheMeasurementItAlreadyPublished()
        {
            int registered = FillToRetentionBudget();
            LowEndNormalizationResult before = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            LowEndNormalizationCache.Register(registered + 1, Rate, FullPrefixTone(0.1f), 1, 0);
            Drain();
            LowEndNormalizationResult after = LowEndNormalizationCache.Evaluate(1, AudioTuning());

            Assert.That(before.Ready, Is.True);
            Assert.That(after.Ready, Is.True);
            Assert.That(after.Gain, Is.EqualTo(before.Gain));
            Assert.That(LowEndNormalizationCache.Evaluate(registered + 1, AudioTuning()).Ready, Is.True);
        }

        [Test]
        public void ReleasedPrefixFallsBackToNeutralInsteadOfAStaleBandMeasurement()
        {
            int registered = FillToRetentionBudget();
            LowEndNormalizationCache.Register(registered + 1, Rate, FullPrefixTone(0.1f), 1, 0);
            Drain();

            _config.PitchedLayerHighpassHz.Value = 71.26761f;
            _config.PitchedLayerLowpassHz.Value = 683.9437f;
            Drain();

            LowEndNormalizationResult released = LowEndNormalizationCache.Evaluate(1, AudioTuning());
            LowEndNormalizationResult retained =
                LowEndNormalizationCache.Evaluate(registered + 1, AudioTuning());

            Assert.That(released.Ready, Is.False, "A prefix that can no longer be re-measured must not report a stale band.");
            Assert.That(released.Gain, Is.EqualTo(1f));
            Assert.That(retained.Ready, Is.True, "A prefix still inside the budget re-measures for the new band.");
        }

        [TestCase("545x39", 0, 0)]
        [TestCase("545x39", 100, 3)]
        [TestCase("545x39", 200, 6)]
        [TestCase("762x39", 100, 3)]
        [TestCase("762x39", 200, 6)]
        public void ActualCartridgeModelGivesRiflesSpecifiedContrastOverVityaz(
            string rifleCaliber, float contrast, float expectedDb)
        {
            TuningSnapshot t = _config.GetTuning();
            var pistol = new ShotDescriptor { AmmoCaliber = "9x19PARA", IsIndoor = true };
            var rifle = new ShotDescriptor { AmmoCaliber = rifleCaliber, IsIndoor = true };
            Func<ShotDescriptor, float> gain = shot => PitchedGunshotLayer.CalculateLayerGain(
                DirectLoudnessModel.CalculatePitchedBodyGain(shot, t),
                DirectLoudnessModel.CalculatePressureFrequencyHz(ExposureModel.Calculate(shot, t), t),
                0, contrast);
            Assert.That(20 * Math.Log10(gain(rifle) / gain(pistol)), Is.EqualTo(expectedDb).Within(0.001));
        }
    }
}
