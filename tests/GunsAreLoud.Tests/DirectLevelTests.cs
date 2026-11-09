using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class DirectLevelTests
    {
        private const int Rate = 48000;
        private readonly List<string> _files = new List<string>();

        [SetUp]
        public void SetUp()
        {
            DirectLevelStore.Clear();
            LowEndNormalizationCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            DirectLevelStore.Clear();
            LowEndNormalizationCache.Clear();
            foreach (string file in _files)
            {
                if (File.Exists(file)) File.Delete(file);
                if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp");
            }
            _files.Clear();
        }

        private string TempFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "GunsAreLoud.LevelCache." + Guid.NewGuid().ToString("N") + ".json");
            _files.Add(path);
            return path;
        }

        private static float[] Sine(float amplitude, float frequency, float seconds, int rate = Rate)
        {
            int frames = (int)(rate * seconds);
            var pcm = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
                pcm[frame * 2] = pcm[frame * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * frame / rate);
            return pcm;
        }

        // A gunshot-like event: broadband noise under a fast attack and an exponential decay.
        private static float[] Shot(float seconds, int seed, float peak = 0.9f, float decaySeconds = 0.15f)
        {
            var random = new Random(seed);
            int frames = (int)(Rate * seconds);
            var pcm = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                double t = frame / (double)Rate;
                double envelope = Math.Min(1.0, t / 0.002) * Math.Exp(-t / decaySeconds);
                for (int channel = 0; channel < 2; channel++)
                    pcm[frame * 2 + channel] = (float)(peak * envelope * (random.NextDouble() * 2 - 1));
            }
            return pcm;
        }

        private static LocalGunshotAudioTuning Tuning(float targetDb, float strength,
            float copyHeadroomDb = DirectLevelModel.DefaultCopyHeadroomDb) =>
            new LocalGunshotAudioTuning(0, 0.3f, 99, AutomaticPitchedRoute.CachedReport, 12, 10, 2000, 50, 0.03f,
                0, PitchedLayerOcclusionMode.Inherit, 500, 0, false, 0, 0, 0, 0,
                directTargetDb: targetDb, directStrength: strength, directCopyHeadroomDb: copyHeadroomDb);

        /// <summary>A recording alone: no copy measured yet.</summary>
        private static ShotBudget Recording(float attackDb, float peak, float volume) =>
            new ShotBudget(attackDb, peak, volume);

        /// <summary>A recording with a copy whose aligned sum peaks at recording + ratio × copy.</summary>
        private static ShotBudget WithCopy(float attackDb, float peak, float volume, float ratio, float copyAttackDb,
            float copyPeak = 1f)
        {
            var sums = new float[ShotSumRatios.Values.Length];
            for (int index = 0; index < sums.Length; index++) sums[index] = peak + ShotSumRatios.Values[index] * copyPeak;
            return new ShotBudget(attackDb, peak, volume, ratio, copyAttackDb, copyPeak, sums);
        }

        // ----- meter -----

        [Test]
        public void KWeightedWindowOfAOneKilohertzSineMatchesItsLevel()
        {
            // BS.1770: -0.691 dB offsets the weighting's +0.691 dB at 1 kHz, so a
            // stereo sine reads its own peak level in dB.
            float measured = DirectLevelMeter.MeasureAttackWindow(Sine(0.1f, 1000f, 1f), 2, Rate);
            double analytic = -0.691 + 20.0 * Math.Log10(0.1) + KWeightingCoefficients.For(Rate).ResponseDb(1000.0);

            Assert.That(measured, Is.EqualTo((float)analytic).Within(0.1f));
            Assert.That(measured, Is.EqualTo(-20f).Within(0.1f));
        }

        [Test]
        public void KWeightingIsComputedForTheClipRate()
        {
            float at44 = DirectLevelMeter.MeasureAttackWindow(Sine(0.1f, 1000f, 1f, 44100), 2, 44100);
            float at48 = DirectLevelMeter.MeasureAttackWindow(Sine(0.1f, 1000f, 1f, 48000), 2, 48000);

            Assert.That(at44, Is.EqualTo(at48).Within(0.05f));
        }

        [Test]
        public void AttackWindowDoesNotDependOnClipLength()
        {
            float[] longShot = Shot(3f, 7);
            float[] shortShot = new float[(int)(Rate * 0.5f) * 2];
            Array.Copy(longShot, shortShot, shortShot.Length);
            float[] veryShort = new float[(int)(Rate * 0.1f) * 2];
            Array.Copy(longShot, veryShort, veryShort.Length);
            float[] padded = new float[longShot.Length];
            Array.Copy(veryShort, padded, veryShort.Length);

            Assert.That(DirectLevelMeter.MeasureAttackWindow(shortShot, 2, Rate),
                Is.EqualTo(DirectLevelMeter.MeasureAttackWindow(longShot, 2, Rate)).Within(1e-4f));
            // A clip shorter than the window is measured over the whole window.
            Assert.That(DirectLevelMeter.MeasureAttackWindow(veryShort, 2, Rate),
                Is.EqualTo(DirectLevelMeter.MeasureAttackWindow(padded, 2, Rate)).Within(1e-4f));
        }

        [Test]
        public void BeatAndTailCompositeMeasuresLikeTheSameShotAsOneClip()
        {
            float[] whole = Shot(1.5f, 11);
            int beatFrames = (int)(Rate * 0.075f);
            const float tailGain = 0.7f;
            var body = new float[beatFrames * 2];
            Array.Copy(whole, body, body.Length);
            var tail = new float[whole.Length - body.Length];
            for (int index = 0; index < tail.Length; index++) tail[index] = whole[body.Length + index] / tailGain;

            float[] composite = AutomaticReportPcm.Compose(body, tail, tailGain, 0);

            Assert.That(DirectLevelMeter.MeasureAttackWindow(composite, 2, Rate),
                Is.EqualTo(DirectLevelMeter.MeasureAttackWindow(whole, 2, Rate)).Within(1e-3f));
            Assert.That(DirectLevelMeter.MeasurePeakDb(composite, 2),
                Is.EqualTo(DirectLevelMeter.MeasurePeakDb(whole, 2)).Within(1e-3f));

            // The production 2 ms seam barely moves either measure.
            float[] seamed = AutomaticReportPcm.Compose(body, tail, tailGain, (int)(Rate * 0.002f));
            Assert.That(DirectLevelMeter.MeasureAttackWindow(seamed, 2, Rate),
                Is.EqualTo(DirectLevelMeter.MeasureAttackWindow(whole, 2, Rate)).Within(0.1f));
        }

        [Test]
        public void PeakOfTheHistogramIsTheExactPeak()
        {
            foreach (int seed in new[] { 1, 2, 3 })
            {
                float[] shot = Shot(2f, seed, 0.8f, 0.3f);
                var histogram = new PeakHistogram();
                histogram.AddStereo(shot, shot.Length / 2);
                float exact = 0f;
                for (int index = 0; index < shot.Length; index++) exact = Math.Max(exact, Math.Abs(shot[index]));

                Assert.That(histogram.Peak, Is.EqualTo(exact));
                Assert.That(histogram.PeakDb, Is.EqualTo(20f * (float)Math.Log10(exact)).Within(1e-4f));
                Assert.That(DirectLevelMeter.MeasurePeakDb(shot, 2), Is.EqualTo(histogram.PeakDb).Within(1e-5f));
            }
            Assert.That(float.IsNaN(new PeakHistogram().PeakDb), Is.True);
        }

        [Test]
        public void DecoderHistogramFollowsTheWholeClipPastTheKeptPrefix()
        {
            float[] shot = Shot(1.0f, 5);
            int frames = shot.Length / 2;
            var buffer = new SilentPcmBuffer(frames / 4, frames);
            for (int start = 0; start < frames; start += 1024)
            {
                int count = Math.Min(1024, frames - start);
                var block = new float[count * 2];
                Array.Copy(shot, start * 2, block, 0, block.Length);
                buffer.ProcessAndSilence(block, 2);
                Assert.That(block, Is.All.EqualTo(0f), "the decoder must stay silent");
            }

            Assert.That(buffer.Complete, Is.True);
            Assert.That(buffer.HistogramComplete, Is.True);
            Assert.That(buffer.Histogram.PeakDb,
                Is.EqualTo(DirectLevelMeter.MeasurePeakDb(shot, 2)).Within(1e-4f));
        }

        // ----- target -----

        [Test]
        public void EnergyTargetFollowsTheLevelIndexUpToTheTop()
        {
            const float top = DirectLevelModel.TopLevelDb;
            Assert.That(DirectLevelModel.EnergyTargetDb(2.5f, true, 0f), Is.EqualTo(top).Within(1e-5f));
            Assert.That(DirectLevelModel.EnergyTargetDb(9f, true, 0f), Is.EqualTo(top).Within(1e-5f),
                "heavier than the top is the top: the medium has no louder end");
            Assert.That(DirectLevelModel.EnergyTargetDb(0f, true, 0f), Is.EqualTo(top - 1.25f).Within(1e-5f));
            Assert.That(DirectLevelModel.EnergyTargetDb(-2.1f, true, 0f), Is.EqualTo(top - 2.3f).Within(1e-5f));
            Assert.That(DirectLevelModel.EnergyTargetDb(2.5f, false, 0f), Is.EqualTo(top - 3f).Within(1e-5f),
                "outdoors sits 3 dB under the same shot indoors");
            Assert.That(DirectLevelModel.EnergyTargetDb(-2.1f, false, 0f), Is.EqualTo(top - 5.3f).Within(1e-5f));
            Assert.That(DirectLevelModel.EnergyTargetDb(float.NaN, true, 0f),
                Is.EqualTo(DirectLevelModel.EnergyTargetDb(0f, true, 0f)).Within(1e-5f));
        }

        [Test]
        public void MuzzleDevicesAndEngineTrimMoveTheEnergyTarget()
        {
            const float top = DirectLevelModel.TopLevelDb;
            // A -25 can at the default influence (0.4 dB audible per point = 0.8 dB
            // of the index) takes a 7.62x39 at the top of the scale 10 dB down.
            float indexPerLoudness = ModConfig.DefaultMuzzleDeviceDb / DirectLevelModel.SlopePerDb;
            ShotLevel bare = ShotLevelIndex.Compute(7.9f, 715f, 0.9f);
            ShotLevel canned = ShotLevelIndex.Compute(7.9f, 715f, 0.9f, ShotLevelIndex.DeviceIndex(-25, indexPerLoudness));
            Assert.That(canned.DeviceIndex, Is.EqualTo(-20f).Within(1e-4f));
            Assert.That(canned.LevelIndex, Is.EqualTo(bare.LevelIndex - 20f).Within(1e-4f));
            Assert.That(DirectLevelModel.EnergyTargetDb(canned.LevelIndex, true, 0f),
                Is.EqualTo(top - 0.5f * (DirectLevelModel.TopIndexDb - canned.LevelIndex)).Within(1e-4f));
            Assert.That(DirectLevelModel.EnergyTargetDb(canned.LevelIndex, true, 0f),
                Is.EqualTo(DirectLevelModel.EnergyTargetDb(bare.LevelIndex, true, 0f) - 10f).Within(0.3f),
                "a -25 suppressor is 10 dB at the ear");
            // A moderator at -12 is about 5 dB; a brake at +10 raises the shot until the top.
            ShotLevel moderated = ShotLevelIndex.Compute(4f, 900f, 0.9f, ShotLevelIndex.DeviceIndex(-12, indexPerLoudness));
            ShotLevel braked = ShotLevelIndex.Compute(4f, 900f, 0.9f, ShotLevelIndex.DeviceIndex(10, indexPerLoudness));
            ShotLevel plain = ShotLevelIndex.Compute(4f, 900f, 0.9f);
            Assert.That(DirectLevelModel.EnergyTargetDb(moderated.LevelIndex, true, 0f),
                Is.EqualTo(DirectLevelModel.EnergyTargetDb(plain.LevelIndex, true, 0f) - 4.8f).Within(1e-4f));
            Assert.That(DirectLevelModel.EnergyTargetDb(braked.LevelIndex, true, 0f), Is.EqualTo(top).Within(1e-4f));
            Assert.That(DirectLevelModel.EnergyTargetDb(2.5f, true, 4f), Is.EqualTo(top + 4f).Within(1e-5f),
                "the player's trim stays on top of the top");
            Assert.That(DirectLevelModel.EnergyTargetDb(0f, true, -6f), Is.EqualTo(top - 7.25f).Within(1e-5f));
        }


        [Test]
        public void BaselineTargetHasOneLevelPerClass()
        {
            const float top = DirectLevelModel.TopLevelDb;
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Pistol, 0f, 0f),
                Is.EqualTo(top + 20f * (float)Math.Log10(0.7)).Within(1e-4f));
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Intermediate, 0f, 0f),
                Is.EqualTo(top + 20f * (float)Math.Log10(0.8)).Within(1e-4f));
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Rifle, 0f, 0f),
                Is.EqualTo(top + 20f * (float)Math.Log10(0.9)).Within(1e-4f));
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Heavy, 0f, 0f), Is.EqualTo(top).Within(1e-4f));
            // The muzzle devices move the class level at the scale's slope, never above the top.
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Intermediate, -20f, 0f),
                Is.EqualTo(DirectLevelModel.BaselineTargetDb(CaliberClass.Intermediate, 0f, 0f) - 10f).Within(1e-4f));
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Pistol, 40f, 0f), Is.EqualTo(top).Within(1e-4f));
            Assert.That(DirectLevelModel.BaselineTargetDb(CaliberClass.Heavy, 0f, 2f), Is.EqualTo(top + 2f).Within(1e-4f));
        }

        [Test]
        public void BaselineClassFallsBackToMuzzleEnergyForUnknownCalibers()
        {
            CaliberClass Class(float massGram, float velocity, out bool fromEnergy) =>
                DirectLevelModel.ResolveClass("ModdedPlasma", ShotLevelIndex.Compute(massGram, velocity, 0.9f), out fromEnergy);

            Assert.That(Class(8f, 300f, out bool energy), Is.EqualTo(CaliberClass.Pistol)); // 360 J
            Assert.That(energy, Is.True);
            Assert.That(Class(4f, 900f, out _), Is.EqualTo(CaliberClass.Intermediate)); // 1620 J
            Assert.That(Class(10f, 850f, out _), Is.EqualTo(CaliberClass.Rifle)); // 3612 J
            Assert.That(Class(45f, 900f, out _), Is.EqualTo(CaliberClass.Heavy)); // 18225 J
            Assert.That(Class(0f, 0f, out _), Is.EqualTo(CaliberClass.Intermediate),
                "a round without energy counts as the reference cartridge");

            Assert.That(DirectLevelModel.ClassFromEnergy(899f), Is.EqualTo(CaliberClass.Pistol));
            Assert.That(DirectLevelModel.ClassFromEnergy(900f), Is.EqualTo(CaliberClass.Intermediate));
            Assert.That(DirectLevelModel.ClassFromEnergy(2700f), Is.EqualTo(CaliberClass.Rifle));
            Assert.That(DirectLevelModel.ClassFromEnergy(8000f), Is.EqualTo(CaliberClass.Heavy));

            Assert.That(DirectLevelModel.ResolveClass("Caliber9x19PARA", ShotLevelIndex.Compute(45f, 900f, 0.9f),
                out bool listed), Is.EqualTo(CaliberClass.Pistol));
            Assert.That(listed, Is.False, "a listed caliber keeps its class whatever its energy");
        }

        [Test]
        public void TargetModeSelectsTheFormula()
        {
            ShotLevel level = ShotLevelIndex.Compute(3.4f, 880f, 0.9f);
            float energy = DirectLevelModel.TargetDb(DirectLevelTarget.Energy, level,
                "Caliber545x39", false, 0f, out _, out _);
            float baseline = DirectLevelModel.TargetDb(DirectLevelTarget.Baseline, level,
                "Caliber545x39", false, 0f, out CaliberClass cartridge, out bool fromEnergy);

            Assert.That(energy, Is.EqualTo(DirectLevelModel.EnergyTargetDb(level.LevelIndex, false, 0f)).Within(1e-5f));
            Assert.That(cartridge, Is.EqualTo(CaliberClass.Intermediate));
            Assert.That(fromEnergy, Is.False);
            Assert.That(baseline, Is.EqualTo(DirectLevelModel.BaselineTargetDb(CaliberClass.Intermediate, 0f, 0f)).Within(1e-5f));
        }

        // ----- gain -----

        [Test]
        public void DonorVolumeIsCompensatedInProportionToStrength()
        {
            // Measured at the target with the bank at full volume; the engine plays it at 0.75.
            // The recording peaks low enough for the budget to stay out of the way.
            ShotBudget shot = Recording(-7f, 0.3f, 0.75f);
            float full = DirectLevelModel.Solve(shot, -7f, 1f).GainDb;
            float half = DirectLevelModel.Solve(shot, -7f, 0.5f).GainDb;

            Assert.That(full, Is.EqualTo(-20f * (float)Math.Log10(0.75)).Within(1e-4f));
            Assert.That(DirectLevelModel.PlayingLevelDb(-7f, 0.75f) + full, Is.EqualTo(-7f).Within(1e-4f));
            Assert.That(half, Is.EqualTo(full * 0.5f).Within(1e-4f));
            Assert.That(DirectLevelModel.Solve(shot, -7f, 1f).BudgetBound, Is.False);
            Assert.That(DirectLevelModel.Solve(shot, -7f, 1f).CopyShare, Is.EqualTo(1f));
        }

        [Test]
        public void GainIsHeldBetweenTheCutAndLiftLimits()
        {
            Assert.That(DirectLevelModel.Solve(Recording(0f, 1f, 1f), -20f, 1f).GainDb, Is.EqualTo(-12f).Within(1e-5f));
            Assert.That(DirectLevelModel.Solve(Recording(0f, 1f, 1f), -20f, 0.5f).GainDb, Is.EqualTo(-6f).Within(1e-5f));
            Assert.That(DirectLevelModel.Solve(Recording(-40f, 0.01f, 1f), 0f, 1f).GainDb, Is.EqualTo(12f).Within(1e-5f),
                "a quiet recording with peak to spare is still lifted by at most 12 dB");
            Assert.That(DirectLevelModel.Solve(Recording(-6f, 0.3f, 1f), -5f, 1f).GainDb, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void PeakBudgetCapsTheRecordingBelowTheBudgetLessTheHeadroom()
        {
            // A sparse rifle recording at full scale, played at 0.75 by the engine,
            // asked for far more than its peak allows.
            ShotBudget shot = Recording(-12f, 1f, 0.75f);
            ShotBudgetSolution solution = DirectLevelModel.Solve(shot, 20f, 1f, 3f);
            float cap = 20f * (float)Math.Log10(DirectLevelModel.PeakBudget * Math.Pow(10.0, -3.0 / 20.0) / 0.75);

            Assert.That(solution.GainDb, Is.EqualTo(cap).Within(1e-4f));
            Assert.That(solution.BudgetBound, Is.True);
            Assert.That(solution.SumPeak, Is.EqualTo(DirectLevelModel.PeakBudget * (float)Math.Pow(10.0, -3.0 / 20.0)).Within(1e-4f));
            // Without headroom the recording may use the whole budget.
            Assert.That(DirectLevelModel.Solve(shot, 20f, 1f, 0f).GainDb,
                Is.EqualTo(20f * (float)Math.Log10(DirectLevelModel.PeakBudget / 0.75)).Within(1e-4f));
            // A recording whose peak is not measured yet is taken at full scale.
            DirectLevelStore.Set(3, -12f, float.NaN, "playback");
            Assert.That(DirectLevel.Decide(Tuning(20f, 1f), 3, 0.75f).GainDb, Is.EqualTo(cap).Within(1e-4f));
        }

        [Test]
        public void CopyGivesWayBeforeTheRecording()
        {
            // Recording + ratio × copy peaks at 1 + ratio: at the unit ratio the
            // pair would reach 2.0 where the budget is 1.25.
            ShotBudget shot = WithCopy(-6f, 1f, 1f, 1f, -20f);
            ShotBudgetSolution bound = DirectLevelModel.Solve(shot, -7f, 1f, 3f);

            Assert.That(bound.BudgetBound, Is.True);
            Assert.That(bound.CopyShare, Is.LessThan(1f));
            Assert.That(bound.CopyShare, Is.GreaterThan(0f));
            Assert.That(bound.SumPeak, Is.EqualTo(DirectLevelModel.PeakBudget).Within(2e-3f));
            // The recording itself still goes where the target puts it.
            float expected = -7f - DirectLevelModel.EventAttackDb(shot, bound.CopyShare);
            Assert.That(bound.GainDb, Is.EqualTo(expected).Within(0.05f));

            // A generous budget leaves the copy whole.
            ShotBudgetSolution free = DirectLevelModel.Solve(shot, -7f, 1f, 3f, budget: 10f);
            Assert.That(free.CopyShare, Is.EqualTo(1f));
            Assert.That(free.BudgetBound, Is.False);
            Assert.That(free.SumPeak, Is.EqualTo(2f * DirectLevelModel.Linear(free.GainDb)).Within(1e-4f));

            // Half strength: half the correction in dB, half the copy's cut in dB.
            ShotBudgetSolution half = DirectLevelModel.Solve(shot, -7f, 0.5f, 3f);
            Assert.That(half.GainDb, Is.EqualTo(bound.GainDb * 0.5f).Within(1e-4f));
            Assert.That(20f * Math.Log10(half.CopyShare), Is.EqualTo(10f * Math.Log10(bound.CopyShare)).Within(1e-3f));
        }

        [Test]
        public void TheCopyAddsToTheEventsAttackLevel()
        {
            ShotBudget shot = WithCopy(-10f, 1f, 1f, 2f, -16f);
            // The copy at ratio 2 is -16 + 6 = -10 dB: as loud as the recording, +3 dB together.
            Assert.That(DirectLevelModel.EventAttackDb(shot, 1f), Is.EqualTo(-10f + 10f * (float)Math.Log10(2)).Within(0.02f));
            Assert.That(DirectLevelModel.EventAttackDb(shot, 0f), Is.EqualTo(-10f));
            Assert.That(DirectLevelModel.EventAttackDb(Recording(-10f, 1f, 1f), 1f), Is.EqualTo(-10f));
        }

        [Test]
        public void SumPeakInterpolatesBetweenTheMeasuredRatiosAndInverts()
        {
            var sums = new float[ShotSumRatios.Values.Length];
            for (int index = 0; index < sums.Length; index++) sums[index] = 0.8f + 0.5f * ShotSumRatios.Values[index];

            Assert.That(ShotSumRatios.SumPeak(0.8f, sums, 0f), Is.EqualTo(0.8f));
            Assert.That(ShotSumRatios.SumPeak(0.8f, sums, 1f), Is.EqualTo(1.3f).Within(1e-5f));
            Assert.That(ShotSumRatios.SumPeak(0.8f, sums, 0.6f), Is.EqualTo(1.1f).Within(1e-5f));
            Assert.That(ShotSumRatios.SumPeak(0.8f, sums, 6f), Is.EqualTo(3.8f).Within(1e-4f), "linear past the last point");
            Assert.That(ShotSumRatios.RatioFor(0.8f, sums, 1.3f), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(ShotSumRatios.RatioFor(0.8f, sums, 1.1f), Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(ShotSumRatios.RatioFor(0.8f, sums, 3.8f), Is.EqualTo(6f).Within(1e-3f));
            Assert.That(ShotSumRatios.RatioFor(0.8f, sums, 0.8f), Is.EqualTo(0f), "no room for any copy");
            Assert.That(ShotSumRatios.SumPeak(0.8f, null, 1f), Is.EqualTo(0.8f));
        }

        [Test]
        public void SumMeterMeasuresTheCopyAgainstTheRecording()
        {
            float[] shot = Shot(0.6f, 11, 0.9f, 0.05f);
            ShotSumMeasure measure = ShotSumMeter.Measure(shot, Rate, 0.5f, 10f, 2000f);

            Assert.That(measure.Valid, Is.True);
            Assert.That(measure.CopyPeak, Is.GreaterThan(0f));
            Assert.That(float.IsNaN(measure.CopyAttackDb), Is.False);
            float directPeak = DirectLevelModel.Linear(DirectLevelMeter.MeasurePeakDb(shot, 2));
            float previous = directPeak;
            for (int index = 0; index < measure.SumPeaks.Length; index++)
            {
                Assert.That(measure.SumPeaks[index], Is.GreaterThanOrEqualTo(previous - 1e-6f),
                    "more copy never lowers the aligned peak of this pair");
                Assert.That(measure.SumPeaks[index],
                    Is.LessThanOrEqualTo(directPeak + ShotSumRatios.Values[index] * measure.CopyPeak + 1e-5f),
                    "the aligned peak is at most the sum of the two peaks");
                previous = measure.SumPeaks[index];
            }
            Assert.That(ShotSumMeter.Measure(new float[2], Rate, 0.5f, 10f, 2000f).Valid, Is.False);
        }

        [Test]
        public void ZeroStrengthLeavesTheShotExactlyAsItIs()
        {
            Assert.That(DirectLevelModel.Strength(0f), Is.EqualTo(0f));
            Assert.That(DirectLevelModel.Strength(6f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(DirectLevelModel.Strength(12f), Is.EqualTo(1f));
            ShotBudgetSolution none = DirectLevelModel.Solve(WithCopy(-3f, 1f, 0.75f, 2f, -6f), -15f, 0f);
            Assert.That(none.GainDb, Is.EqualTo(0f));
            Assert.That(none.CopyShare, Is.EqualTo(1f));
            Assert.That(DirectLevelModel.Linear(none.GainDb), Is.EqualTo(1f));
            Assert.That(DirectLevelModel.Solve(Recording(float.NaN, 1f, 1f), -7f, 1f).GainDb, Is.EqualTo(0f),
                "an unmeasured recording is not corrected");

            DirectLevelStore.Set(1, -3f, 0f, "playback");
            DirectLevelDecision decision = DirectLevel.Decide(Tuning(-15f, 0f), 1, 0.75f);
            Assert.That(decision.Linear, Is.EqualTo(1f));
            Assert.That(decision.CopyLinear, Is.EqualTo(1f));
        }

        // ----- density -----

        /// <summary>A loss table: more loss for a higher ratio row and a higher peak over the ceiling.</summary>
        private static float[] LossTable(float perRow = 0.5f, float perOverDb = 0.25f)
        {
            var loss = new float[ShotSumRatios.LossLength];
            for (int row = 0; row < ShotSumRatios.LossRows; row++)
                for (int column = 0; column < ShotSumRatios.OverDb.Length; column++)
                    loss[row * ShotSumRatios.OverDb.Length + column] = -(row * perRow) - ShotSumRatios.OverDb[column] * perOverDb;
            return loss;
        }

        private static float[] FlatLoss(float value)
        {
            var loss = new float[ShotSumRatios.LossLength];
            for (int index = 0; index < loss.Length; index++) loss[index] = value;
            return loss;
        }

        [Test]
        public void LimitLossInterpolatesBetweenRatiosAndOversAndIsNeverAGain()
        {
            float[] loss = LossTable();
            Assert.That(ShotSumRatios.LimitLossDb(loss, 0f, 2f), Is.EqualTo(-0.5f).Within(1e-5f), "the recording alone, row 0");
            Assert.That(ShotSumRatios.LimitLossDb(loss, 0.5f, 4f), Is.EqualTo(-1f - 1f).Within(1e-5f), "row 2, over 4");
            Assert.That(ShotSumRatios.LimitLossDb(loss, 0.375f, 3f), Is.EqualTo(-1.5f).Within(1e-5f), "between rows 1 and 2, between overs 2 and 4");
            Assert.That(ShotSumRatios.LimitLossDb(loss, 1f, 0f), Is.Zero, "at the ceiling nothing is taken");
            Assert.That(ShotSumRatios.LimitLossDb(loss, 1f, -3f), Is.Zero, "under the ceiling nothing is taken");
            Assert.That(ShotSumRatios.LimitLossDb(loss, 0f, 15f), Is.EqualTo(-3.75f).Within(1e-5f), "extended along the last segment");
            Assert.That(ShotSumRatios.LimitLossDb(loss, 8f, 2f), Is.EqualTo(-(8 * 0.5f) - 0.5f).Within(1e-5f), "above the table: the last row");
            Assert.That(ShotSumRatios.LimitLossDb(FlatLoss(0.4f), 1f, 6f), Is.Zero, "a table that claims a gain is read as nothing");
            Assert.That(ShotSumRatios.LimitLossDb(null, 1f, 6f), Is.Zero);
            Assert.That(ShotSumRatios.LimitLossDb(new float[3], 1f, 6f), Is.Zero);
        }

        [Test]
        public void WithoutDensityTheSolutionIsTodaysAndWithItTheBudgetMovesUpByTheDensity()
        {
            ShotBudget shot = WithCopy(-10f, 1f, 1f, 1f, -12f);
            ShotBudgetSolution today = DirectLevelModel.Solve(shot, -2f, 1f);
            ShotBudgetSolution same = DirectLevelModel.Solve(shot, -2f, 1f, DirectLevelModel.DefaultCopyHeadroomDb,
                DirectLevelModel.PeakBudget, 0f);
            Assert.That(same.GainDb, Is.EqualTo(today.GainDb));
            Assert.That(same.CopyShare, Is.EqualTo(today.CopyShare));
            Assert.That(same.SumPeak, Is.EqualTo(today.SumPeak));
            Assert.That(float.IsNaN(same.OverDb), Is.True);
            Assert.That(today.BudgetBound, Is.True, "a recording at full scale cannot reach -2 dB under the cap");

            ShotBudgetSolution dense = DirectLevelModel.Solve(shot, -2f, 1f, DirectLevelModel.DefaultCopyHeadroomDb,
                DirectLevelModel.PeakBudget, 6f);
            Assert.That(dense.BudgetBound, Is.True);
            Assert.That(dense.GainDb, Is.EqualTo(today.GainDb + 6f).Within(0.01f),
                "the cap and the budget stand 6 dB higher at the program limiter's input");
            Assert.That(dense.SumPeak, Is.LessThanOrEqualTo(DirectLevelModel.PeakBudget * 2f * 1.001f));
            Assert.That(dense.SumPeak, Is.GreaterThan(DirectLevelModel.PeakBudget));
            Assert.That(dense.OverDb, Is.GreaterThan(6f));
            Assert.That(dense.LimitLossDb, Is.Zero, "no loss table: nothing is predicted");
        }

        [Test]
        public void WithDensityTheGainCoversWhatTheLimiterTakesOffTheAttack()
        {
            // Hot event: a full-scale recording with a copy of its own size, so
            // the event stands well over the ceiling at the target.
            var sums = new float[ShotSumRatios.Values.Length];
            for (int index = 0; index < sums.Length; index++) sums[index] = 1f + ShotSumRatios.Values[index];
            var shot = new ShotBudget(-2f, 1f, 1f, 1f, -8f, 1f, sums, FlatLoss(-1.5f));

            ShotBudgetSolution dense = DirectLevelModel.Solve(shot, -1f, 1f, DirectLevelModel.DefaultCopyHeadroomDb,
                DirectLevelModel.PeakBudget, 6f);

            float eventAttack = DirectLevelModel.EventAttackDb(shot, 1f);
            Assert.That(dense.CopyShare, Is.EqualTo(1f), "the stage budget holds the whole copy");
            Assert.That(dense.LimitLossDb, Is.EqualTo(-1.5f).Within(1e-4f));
            Assert.That(dense.GainDb, Is.EqualTo(-1f - eventAttack + 1.5f).Within(0.02f),
                "the target is held on the attack after the limiter, so the gain is higher by the loss");
            Assert.That(dense.OverDb, Is.GreaterThan(7f));
            Assert.That(dense.BudgetBound, Is.False);

            ShotBudgetSolution plain = DirectLevelModel.Solve(shot, -1f, 1f);
            Assert.That(plain.GainDb, Is.LessThan(dense.GainDb - 2f), "today the same shot is cut to the budget and its copy shared down");
        }

        [Test]
        public void MeasuredLossIsSmallForACrackOverABodyAndLargeForADenseOpening()
        {
            const int Rate = 44100;
            int window = DirectLevelMeter.WindowFrames(Rate);
            int lookahead = (int)(Rate * MasterLimiterState.ProgramLookaheadSeconds);
            int opening = window + lookahead;
            KWeightingCoefficients coefficients = KWeightingCoefficients.For(Rate);
            var copy = new float[opening * 2];
            var sums = new float[ShotSumRatios.Values.Length];

            // A crack: 1 ms at full scale, then a body at a fifth of it.
            var crack = new float[opening * 2];
            int crackFrames = Rate / 1000;
            for (int frame = 0; frame < opening; frame++)
            {
                float value = frame < crackFrames ? (frame % 2 == 0 ? 1f : -1f) : 0.2f * (float)Math.Sin(frame * 0.07);
                crack[frame * 2] = crack[frame * 2 + 1] = value;
            }
            for (int index = 0; index < sums.Length; index++) sums[index] = 1f;
            float[] crackLoss = ShotSumMeter.MeasureLimitLoss(crack, copy, window, lookahead, Rate, 1f, sums, ref coefficients);
            Assert.That(crackLoss.Length, Is.EqualTo(ShotSumRatios.LossLength));
            int overs = ShotSumRatios.OverDb.Length;
            Assert.That(crackLoss[0], Is.GreaterThanOrEqualTo(-0.3f), "at the ceiling the crack barely touches the knee");
            Assert.That(crackLoss[overs - 1], Is.LessThan(-0.5f), "12 dB over, the crack is taken down");
            Assert.That(crackLoss[overs - 1], Is.GreaterThan(-6f), "but the body passes, so most of the attack energy stays");
            for (int column = 1; column < overs; column++)
                Assert.That(crackLoss[column], Is.LessThanOrEqualTo(crackLoss[column - 1] + 1e-4f), "more over, more loss");

            // A dense opening: the whole window at one level, so the whole window is over.
            var dense = new float[opening * 2];
            for (int frame = 0; frame < opening; frame++)
                dense[frame * 2] = dense[frame * 2 + 1] = 0.8f * (frame % 2 == 0 ? 1f : -1f);
            for (int index = 0; index < sums.Length; index++) sums[index] = 0.8f;
            float[] denseLoss = ShotSumMeter.MeasureLimitLoss(dense, copy, window, lookahead, Rate, 0.8f, sums, ref coefficients);
            Assert.That(denseLoss[overs - 1], Is.LessThan(-9f), "12 dB over on every frame: nearly all of it is taken");
            Assert.That(denseLoss[overs - 1], Is.GreaterThanOrEqualTo(-12.5f));
        }

        // ----- store -----

        [Test]
        public void ReleaseTailPlaysWithItsBodysCorrection()
        {
            // Body bank at 0.9, tail bank at 0.6: the tail is filed under the body's
            // event at 0.6 / 0.9 of its volume.
            DirectLevelStore.Set(10, -4f, -1f, "playback");
            DirectLevelStore.Alias(20, 10, 0.6f / 0.9f);
            LocalGunshotAudioTuning tuning = Tuning(-8f, 1f);

            DirectLevelDecision body = DirectLevel.Decide(tuning, 10, 0.9f);
            DirectLevelDecision tail = DirectLevel.Decide(tuning, 20, 0.6f);

            Assert.That(body.GainDb, Is.Not.EqualTo(0f));
            Assert.That(tail.GainDb, Is.EqualTo(body.GainDb).Within(1e-4f));
            Assert.That(tail.Source, Is.EqualTo("playback"));
        }

        [Test]
        public void UnmeasuredVariantBorrowsItsSiblingsMeanLevel()
        {
            DirectLevelStore.Siblings(1, 2, 3);
            DirectLevelStore.Set(1, -6f, -3f, "getdata");
            DirectLevelStore.Set(2, -10f, -5f, "cache");

            Assert.That(DirectLevelStore.TryGet(3, out float level, out float peak, out _, out string source), Is.True);
            Assert.That(level, Is.EqualTo(-8f).Within(1e-5f));
            Assert.That(peak, Is.EqualTo(-4f).Within(1e-5f));
            Assert.That(source, Is.EqualTo(DirectLevelStore.SiblingSource));
            Assert.That(DirectLevelStore.TryGet(4, out _, out _, out _, out string none), Is.False);
            Assert.That(none, Is.EqualTo(DirectLevelStore.NoSource));
        }

        [Test]
        public void PendingPeakMeasureDoesNotHideTheAttackWindow()
        {
            DirectLevelStore.Set(5, -6f, float.NaN, "playback");
            Assert.That(DirectLevelStore.Contains(5), Is.True);
            Assert.That(DirectLevelStore.TryGet(5, out float window, out float pending, out _, out _), Is.True);
            Assert.That(window, Is.EqualTo(-6f));
            Assert.That(float.IsNaN(pending), Is.True);
            DirectLevelStore.Set(5, float.NaN, -2f, "playback");
            Assert.That(DirectLevelStore.TryGet(5, out window, out float peak, out _, out _), Is.True);
            Assert.That(window, Is.EqualTo(-6f));
            Assert.That(peak, Is.EqualTo(-2f));
        }

        [Test]
        public void TwoBlendedClipsAreDecidedOnTheirSummedEnergy()
        {
            DirectLevelStore.Set(1, -6f, -3f, "playback");
            DirectLevelStore.Set(2, -12f, -9f, "playback");
            LocalGunshotAudioTuning tuning = Tuning(-10f, 1f);

            DirectLevelDecision both = DirectLevel.Decide(tuning, 1, 0.5f, 2, 0.5f);
            float a = DirectLevelModel.PlayingLevelDb(-6f, 0.5f), b = DirectLevelModel.PlayingLevelDb(-12f, 0.5f);
            float sum = (float)(10 * Math.Log10(Math.Pow(10, a / 10) + Math.Pow(10, b / 10)));
            Assert.That(DirectLevelModel.CombinePlayingDb(a, b), Is.EqualTo(sum).Within(1e-4f));
            Assert.That(both.PlayingDb, Is.EqualTo(sum).Within(1e-4f));
            Assert.That(both.GainDb, Is.EqualTo(-10f - sum).Within(1e-4f), "the pair peaks low enough for the budget to stay out of the way");

            // Clip1 silent: the decision is Clip2's at its own volume.
            DirectLevelDecision second = DirectLevel.Decide(tuning, 1, 0f, 2, 0.8f);
            Assert.That(second.PlayingDb, Is.EqualTo(DirectLevelModel.PlayingLevelDb(-12f, 0.8f)).Within(1e-4f));
            // Clip2 silent: Clip1 alone, exactly as a single clip.
            Assert.That(DirectLevel.Decide(tuning, 1, 0.9f, 2, 0f).GainDb,
                Is.EqualTo(DirectLevel.Decide(tuning, 1, 0.9f).GainDb));
            // Both silent: no correction.
            DirectLevelDecision none = DirectLevel.Decide(tuning, 1, 0f, 2, 0f);
            Assert.That(none.Linear, Is.EqualTo(1f));
            Assert.That(float.IsNaN(none.PlayingDb), Is.True);
            // An unmeasured second clip leaves the decision to the first.
            Assert.That(DirectLevel.Decide(tuning, 1, 0.9f, 3, 0.1f).GainDb,
                Is.EqualTo(DirectLevel.Decide(tuning, 1, 0.9f).GainDb));
        }

        [Test]
        public void ChangedRecordingReplacesBothMeasures()
        {
            DirectLevelStore.Set(8, -6f, -3f, "cache");
            DirectLevelStore.Replace(8, -9f, float.NaN, "playback");
            Assert.That(DirectLevelStore.TryGet(8, out float window, out float peak, out _, out string source), Is.True);
            Assert.That(window, Is.EqualTo(-9f));
            Assert.That(source, Is.EqualTo("playback"));
            Assert.That(float.IsNaN(peak), Is.True, "a stale peak measure is not kept next to a new attack window");
        }


        // ----- cache -----

        private static LevelCacheEntry Layer() => new LevelCacheEntry
        {
            HasLayer = true, BaseVolume = 0.85f, Level = 0.1234f, BassLevel = 0.0456f, DecayLevel = 0.0078f,
            DecayReady = true, NativeBodySeconds = 0.0825f, Group = 3, BodySource = "loopBeat", Rate = 48000,
            BandPitch = 0.5f, BandHigh = 10f, BandLow = 2000f,
            CopyPeak = 0.61f, CopyAttackDb = -13.5f, SumPeaks = new[] { 1.05f, 1.1f, 1.2f, 1.3f, 1.5f, 1.7f, 2.1f, 2.5f },
            LimitLoss = LossTable()
        };

        [Test]
        public void CacheRoundTripsThroughItsFile()
        {
            string path = TempFile();
            string key = LevelCache.ClipKey("m4a1_outdoor_close_loop", 123456, 44100, 2, LevelCache.HashTag(0xDEADBEEF12345678UL));
            string stream = LevelCache.EventKey(LevelCache.ClipKey("ak74_loop", 1000, 44100, 2, LevelCache.StreamTag),
                LevelCache.ClipKey("ak74 \"tail\"", 2000, 44100, 1, LevelCache.StreamTag), 0.75f);
            var store = new LevelCacheStore(path, "1.2.0", () => 0.0);
            store.PutDirect(key, -7.25f, -3.5f, 0.85f);
            store.PutLayer(key, Layer());
            store.PutDirect(stream, -9.125f, -6f, 1f);
            store.PutPrefix(stream, "0123456789abcdef", 48000);
            Assert.That(store.Flush(), Is.True);

            var loaded = new LevelCacheStore(path, "1.2.0", () => 0.0);
            Assert.That(loaded.Load(), Is.EqualTo("loaded"));
            Assert.That(loaded.Count, Is.EqualTo(2));
            Assert.That(loaded.TryGet(key, out LevelCacheEntry entry), Is.True);
            Assert.That(entry.AttackWindowDb, Is.EqualTo(-7.25f));
            Assert.That(entry.PeakDb, Is.EqualTo(-3.5f));
            Assert.That(entry.BaseVolume, Is.EqualTo(0.85f));
            Assert.That(entry.HasLayer, Is.True);
            Assert.That(entry.Level, Is.EqualTo(0.1234f));
            Assert.That(entry.BassLevel, Is.EqualTo(0.0456f));
            Assert.That(entry.DecayLevel, Is.EqualTo(0.0078f));
            Assert.That(entry.DecayReady, Is.True);
            Assert.That(entry.NativeBodySeconds, Is.EqualTo(0.0825f));
            Assert.That(entry.Group, Is.EqualTo(3));
            Assert.That(entry.BodySource, Is.EqualTo("loopBeat"));
            Assert.That(entry.Rate, Is.EqualTo(48000));
            Assert.That(entry.BandMatches(0.5f, 10f, 2000f), Is.True);
            Assert.That(entry.BandMatches(0.5f, 35f, 260f), Is.False);
            Assert.That(entry.HasSum, Is.True);
            Assert.That(entry.CopyPeak, Is.EqualTo(0.61f));
            Assert.That(entry.CopyAttackDb, Is.EqualTo(-13.5f));
            Assert.That(entry.SumPeaks, Is.EqualTo(new[] { 1.05f, 1.1f, 1.2f, 1.3f, 1.5f, 1.7f, 2.1f, 2.5f }));
            Assert.That(entry.HasLoss, Is.True);
            Assert.That(entry.LimitLoss, Is.EqualTo(LossTable()).Within(1e-5f));
            Assert.That(loaded.TryGet(stream, out LevelCacheEntry streamed), Is.True);
            Assert.That(streamed.HasLayer, Is.False);
            Assert.That(streamed.AttackWindowDb, Is.EqualTo(-9.125f));
            Assert.That(streamed.PrefixHash, Is.EqualTo("0123456789abcdef"));
            Assert.That(streamed.PrefixRate, Is.EqualTo(48000));
            Assert.That(entry.PrefixHash, Is.Null);
        }

        [Test]
        public void ResetDropsEveryEntryAndRewritesTheFileEmpty()
        {
            string path = TempFile();
            double now = 0;
            var store = new LevelCacheStore(path, "1.2.0", () => now);
            store.PutDirect("a|1|44100|2|stream", -6f, -3f, 1f);
            store.PutLayer("a|1|44100|2|stream", Layer());
            Assert.That(store.Flush(), Is.True);
            Assert.That(new LevelCacheStore(path, "1.2.0", () => 0).Load(), Is.EqualTo("loaded"));

            Assert.That(store.Reset(), Is.EqualTo(1));
            Assert.That(store.Count, Is.Zero);
            Assert.That(store.TryGet("a|1|44100|2|stream", out _), Is.False);
            Assert.That(store.Dirty, Is.True);
            Assert.That(store.Tick(), Is.True, "the empty file is written at once");
            var loaded = new LevelCacheStore(path, "1.2.0", () => 0);
            Assert.That(loaded.Load(), Is.EqualTo("loaded"));
            Assert.That(loaded.Count, Is.Zero);
            Assert.That(store.Reset(), Is.Zero, "nothing left to drop");
        }

        [Test]
        public void KeyOfAChangedRecordingMissesTheCache()
        {
            var store = new LevelCacheStore(null, "1.2.0", () => 0.0);
            var samples = new[] { 0.1f, -0.2f, 0.3f, -0.4f };
            ulong hash = LevelCache.Fnv1a(samples, samples.Length);
            store.PutDirect(LevelCache.ClipKey("glock_close", 2, 44100, 2, LevelCache.HashTag(hash)), -6f, -2f, 1f);

            var changed = (float[])samples.Clone();
            changed[3] = -0.41f;
            Assert.That(LevelCache.Fnv1a(changed, changed.Length), Is.Not.EqualTo(hash));
            Assert.That(LevelCache.Fnv1a((float[])samples.Clone(), samples.Length), Is.EqualTo(hash));
            Assert.That(store.TryGet(LevelCache.ClipKey("glock_close", 2, 44100, 2, LevelCache.HashTag(hash)), out _), Is.True);
            Assert.That(store.TryGet(LevelCache.ClipKey("glock_close", 2, 44100, 2,
                LevelCache.HashTag(LevelCache.Fnv1a(changed, changed.Length))), out _), Is.False);
            Assert.That(store.TryGet(LevelCache.ClipKey("glock_close", 3, 44100, 2, LevelCache.HashTag(hash)), out _), Is.False);
            Assert.That(store.TryGet(LevelCache.ClipKey("glock_close", 2, 48000, 2, LevelCache.HashTag(hash)), out _), Is.False);
            Assert.That(store.TryGet(LevelCache.ClipKey("glock_close", 2, 44100, 1, LevelCache.HashTag(hash)), out _), Is.False);
            Assert.That(store.TryGet(LevelCache.ClipKey("glock_close", 2, 44100, 2, LevelCache.StreamTag), out _), Is.False);
        }

        [Test]
        public void DamagedOrForeignCacheFileIsReplacedWithoutThrowing()
        {
            foreach (string text in new[] { "{ not json", "", "[1,2,3]", "{\"format\": 99, \"entries\": {}}",
                "{\"format\": 1, \"entries\": {}}", "{\"format\": 2}",
                "{\"format\": 2, \"entries\": {\"a\": {\"k50\": \"x\"}} trailing" })
            {
                string path = TempFile();
                File.WriteAllText(path, text);
                double now = 0;
                var store = new LevelCacheStore(path, "1.2.0", () => now);
                string state = null;
                Assert.DoesNotThrow(() => state = store.Load(), text);
                Assert.That(state, Does.EndWith("replaced"), text);
                Assert.That(store.Count, Is.Zero, text);
                Assert.That(store.Dirty, Is.True, text);
                now = 10;
                Assert.That(store.Tick(), Is.True, text);
                Assert.That(new LevelCacheStore(path, "1.2.0", () => 0).Load(), Is.EqualTo("loaded"), text);
            }
        }

        [Test]
        public void NewMeasurementsAreWrittenTogetherAfterAShortDelay()
        {
            string path = TempFile();
            double now = 100;
            var store = new LevelCacheStore(path, "1.2.0", () => now);
            store.PutDirect("a|1|44100|2|stream", -6f, -3f, 1f);
            now += 0.5;
            store.PutDirect("b|1|44100|2|stream", -7f, -4f, 1f);
            store.PutLayer("b|1|44100|2|stream", Layer());
            Assert.That(store.Tick(), Is.False);
            Assert.That(File.Exists(path), Is.False);
            now = 100 + LevelCacheStore.FlushDelaySeconds + 0.01;
            Assert.That(store.Tick(), Is.True);
            Assert.That(store.WriteCount, Is.EqualTo(1));
            Assert.That(store.Tick(), Is.False);
            // The same values again are not a change worth a write.
            store.PutDirect("a|1|44100|2|stream", -6f, -3f, 1f);
            store.PutLayer("b|1|44100|2|stream", Layer());
            Assert.That(store.Dirty, Is.False);
            now += 100;
            Assert.That(store.Tick(), Is.False);
            Assert.That(store.WriteCount, Is.EqualTo(1));
            Assert.That(File.Exists(path + ".tmp"), Is.False);
        }

        [Test]
        public void FailedWriteIsRetriedAndThenAbandoned()
        {
            string path = TempFile();
            Directory.CreateDirectory(path); // A directory where the file should be: every write fails.
            try
            {
                double now = 0;
                var store = new LevelCacheStore(path, "1.2.0", () => now);
                store.PutDirect("a|1|44100|2|stream", -6f, -3f, 1f);
                for (int attempt = 1; attempt <= LevelCacheStore.MaximumWriteFailures; attempt++)
                {
                    now += LevelCacheStore.RetryDelaySeconds + 1;
                    Assert.That(store.Tick(), Is.False);
                    Assert.That(store.LastWriteError, Is.Not.Null);
                    Assert.That(store.Dirty, Is.EqualTo(attempt < LevelCacheStore.MaximumWriteFailures));
                    if (attempt < LevelCacheStore.MaximumWriteFailures)
                    {
                        now += 1;
                        Assert.That(store.Tick(), Is.False, "the next attempt waits for the retry delay");
                    }
                }
                Assert.That(store.WritingAbandoned, Is.True);
                Assert.That(store.WriteCount, Is.Zero);
            }
            finally
            {
                Directory.Delete(path, true);
            }
        }

        [Test]
        public void CachedLayerLevelsAreReadyOnlyInTheirBand()
        {
            var file = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);
            TuningSnapshot tuning = config.GetTuning();
            LowEndNormalizationCache.BandOf(tuning.PitchedLayerSemitones, tuning.PitchedLayerHighpassHz,
                tuning.PitchedLayerLowpassHz, out float pitch, out float high, out float low);
            LevelCacheEntry entry = Layer();
            entry.BandPitch = pitch; entry.BandHigh = high; entry.BandLow = low;
            entry.Group = 0;

            LowEndNormalizationCache.RegisterMeasured(7, entry, 0.5f);
            LowEndNormalizationCache.Refresh(tuning);

            Assert.That(LowEndNormalizationCache.IsCurrent(7), Is.True);
            config.PitchedLayerLowpassHz.Value = 1500f;
            LowEndNormalizationCache.Refresh(config.GetTuning());
            Assert.That(LowEndNormalizationCache.IsCurrent(7), Is.False,
                "levels without samples cannot be re-measured for another band");
            config.PitchedLayerLowpassHz.Value = tuning.PitchedLayerLowpassHz;
            LowEndNormalizationCache.Refresh(config.GetTuning());
            Assert.That(LowEndNormalizationCache.IsCurrent(7), Is.True, "and are valid again in their own band");
        }

        [Test]
        public void CopyRatioTakesTheBodyCalibrationNotTheRecoveredDecay()
        {
            var file = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false };
            var config = new ModConfig(file);
            TuningSnapshot snapshot = config.GetTuning();
            LowEndNormalizationCache.Refresh(snapshot);
            LevelCacheEntry entry = Layer();
            LowEndNormalizationCache.BandOf(snapshot.PitchedLayerSemitones, snapshot.PitchedLayerHighpassHz,
                snapshot.PitchedLayerLowpassHz, out entry.BandPitch, out entry.BandHigh, out entry.BandLow);
            // A loud body (cut to its target) with a faint tail: the decay gain
            // recovers to twice the body gain, as the heavy rifle recordings do.
            entry.Group = 0; entry.Level = 0.5f; entry.BassLevel = 0.5f; entry.DecayLevel = 0.001f;
            LowEndNormalizationCache.RegisterMeasured(8, entry, 0.5f);
            LocalGunshotAudioTuning tuning = new LocalGunshotAudioTuning(0, 0.3f, 99, AutomaticPitchedRoute.CachedReport,
                snapshot.PitchedLayerSemitones, snapshot.PitchedLayerHighpassHz, snapshot.PitchedLayerLowpassHz, 50, 0.03f,
                0, PitchedLayerOcclusionMode.Inherit, 500, 0, false, 0, 0, 0, 0,
                lowEndNormalizationPercent: 100f, directTargetDb: -6f, directStrength: 1f);

            LowEndNormalizationResult normalization = LowEndNormalizationCache.Evaluate(8, tuning);
            Assert.That(normalization.Ready, Is.True);
            Assert.That(normalization.BodyGain, Is.LessThan(1f));
            Assert.That(normalization.DecayGain, Is.EqualTo(normalization.BodyGain * 2f).Within(0.01f));

            float layer = PitchedGunshotLayer.CalculateLayerGain(0.3f, 0f, 0f, tuning.CartridgeContrastDb) *
                PitchedGunshotLayer.CalculateUserGain(0f);
            Assert.That(DirectLevel.CopyRatio(tuning, 8), Is.EqualTo(layer * normalization.BodyGain).Within(1e-5f),
                "the copy's attack and peak play at the body gain; the louder decay comes after both");
        }

        // ----- layer -----

        [Test]
        public void CopyFollowsTheDirectCorrectionAndKeepsWhatTheVolumeCannotCarry()
        {
            // Without a correction below 1: the volume and a neutral filter.
            Assert.That(PitchedGunshotLayer.SplitCopyGain(0.8f, 0.5f, 1f, out float volume), Is.EqualTo(1f));
            Assert.That(volume, Is.EqualTo(0.4f).Within(1e-6f));
            // The copy's own gain past 1 is kept too, not only the correction.
            Assert.That(PitchedGunshotLayer.SplitCopyGain(1f, 1.5f, 1f, out volume), Is.EqualTo(1.5f).Within(1e-6f));
            Assert.That(volume, Is.EqualTo(1f));

            // A lift within the volume range stays in the volume.
            Assert.That(PitchedGunshotLayer.SplitCopyGain(0.8f, 0.5f, 2f, out volume), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(volume, Is.EqualTo(0.8f).Within(1e-6f));

            // Past 1, the rest goes into the copy's filter gain.
            float filter = PitchedGunshotLayer.SplitCopyGain(0.8f, 1f, 2f, out volume);
            Assert.That(volume, Is.EqualTo(1f));
            Assert.That(filter, Is.EqualTo(1.6f).Within(1e-5f));
            Assert.That(volume * filter, Is.EqualTo(0.8f * 1f * 2f).Within(1e-5f));

            // A cut lowers the volume only.
            Assert.That(PitchedGunshotLayer.SplitCopyGain(0.8f, 1f, 0.5f, out volume), Is.EqualTo(1f));
            Assert.That(volume, Is.EqualTo(0.4f).Within(1e-6f));
        }

        [TestCase(3f)]
        [TestCase(6f)]
        public void CartridgeContrastIsTheConfiguredLevelFromNineByNineteenToTheIntermediateRound(float contrastDb)
        {
            float pistol = PitchedGunshotLayer.CalculateCartridgeContrastDb(
                ShotLevelIndex.ContrastPistolEnergyIndex, contrastDb);
            float intermediate = PitchedGunshotLayer.CalculateCartridgeContrastDb(
                ShotLevelIndex.ContrastIntermediateEnergyIndex, contrastDb);
            Assert.That(intermediate - pistol, Is.EqualTo(contrastDb).Within(1e-4f));
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(-6.2f, contrastDb) -
                PitchedGunshotLayer.CalculateCartridgeContrastDb(-1.1f, contrastDb), Is.EqualTo(-contrastDb).Within(1e-4f));
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(0f, contrastDb), Is.Zero,
                "the reference cartridge is unweighted");
            Assert.That(ShotLevelIndex.ContrastSpanDb, Is.EqualTo(5.1f).Within(1e-5f));
        }

        [Test]
        public void CartridgeContrastIsHeldBetweenItsBounds()
        {
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(-30f, 3f), Is.EqualTo(-8f));
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(30f, 3f), Is.EqualTo(3.5f));
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(-30f, 9f), Is.EqualTo(-8f));
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(9.83f, 3f), Is.EqualTo(3.5f),
                "a median .50 BMG round is at the upper bound");
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(-13f, 3f),
                Is.EqualTo(-13f * 3f / 5.1f).Within(1e-4f), "just inside the lower bound");
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(-6.2f, 0f), Is.Zero, "0 dB removes the weighting");
            Assert.That(PitchedGunshotLayer.CalculateCartridgeContrastDb(float.NaN, 3f), Is.Zero);
        }

        [TestCase(1f, 0f, 1f, TestName = "Copy gain without overflow")]
        [TestCase(0.9f, 12f, 1.6f, TestName = "Copy gain past the source volume ceiling")]
        public void CopyGainIsDonorVolumeTimesDirectNormalizationContrastAndUserGain(
            float donorVolume, float userGainDb, float directGain)
        {
            const float body = 0.4f, energyIndex = -1.1f, contrastDb = 3f, normalization = 0.8f, damping = 0.7f;
            float layer = PitchedGunshotLayer.CalculateLayerGain(body, energyIndex, 0f, contrastDb) *
                PitchedGunshotLayer.CalculateUserGain(userGainDb) * damping;
            float filter = PitchedGunshotLayer.SplitCopyGain(donorVolume, layer, directGain, out float volume);
            float heard = volume * filter * normalization;

            float contrast = (float)Math.Pow(10.0, energyIndex * contrastDb / 5.1 / 20.0);
            float userGain = (float)Math.Pow(10.0, userGainDb / 20.0);
            float expected = donorVolume * directGain * normalization * contrast * userGain * body * 1.25f * damping;
            Assert.That(heard, Is.EqualTo(expected).Within(expected * 1e-5f));
            Assert.That(volume, Is.LessThanOrEqualTo(1f));
            if (donorVolume * layer * directGain > 1f)
                Assert.That(filter, Is.GreaterThan(1f), "what the volume cannot carry goes to the filter");
            else
                Assert.That(filter, Is.EqualTo(1f));
        }

        [TestCase(1f)]
        [TestCase(0f)]
        public void UnderSebsSliderTheCopyMovesWithTheDirectSoundOnly(float strength)
        {
            // SEB's preset puts the shot at 0.75; at +6 dB on its slider it asks
            // for 1.5 and Unity plays 1.0. The direct target carries the slider.
            const float measuredDb = -4f, preset = 0.75f, levelIndex = -1f;
            float layer = PitchedGunshotLayer.CalculateLayerGain(0.4f, -1.1f);

            float DirectDb(float donorVolume, float userGainDb, out float gain)
            {
                float target = DirectLevelModel.EnergyTargetDb(levelIndex, false, userGainDb);
                // A recording peaking at half scale: the budget stays out of the way.
                float gainDb = DirectLevelModel.Solve(Recording(measuredDb, 0.5f, donorVolume), target, strength).GainDb;
                gain = DirectLevelModel.Linear(gainDb);
                return DirectLevelModel.PlayingLevelDb(measuredDb, donorVolume) + gainDb;
            }

            float CopyDb(float donorVolume, float gain)
            {
                float filter = PitchedGunshotLayer.SplitCopyGain(donorVolume, layer, gain, out float volume);
                return (float)(20.0 * Math.Log10(volume * filter));
            }

            float directAtZero = DirectDb(preset, 0f, out float gainAtZero);
            float directAtSix = DirectDb(Math.Min(1f, preset * 2f), 6.0206f, out float gainAtSix);
            float directMove = directAtSix - directAtZero;
            float copyMove = CopyDb(Math.Min(1f, preset * 2f), gainAtSix) - CopyDb(preset, gainAtZero);

            Assert.That(copyMove, Is.EqualTo(directMove).Within(1e-3f));
            if (strength > 0f)
                Assert.That(directMove, Is.EqualTo(6.0206f).Within(1e-3f), "the correction restores the slider past the volume ceiling");
            else
                Assert.That(directMove, Is.EqualTo((float)(20.0 * Math.Log10(1.0 / preset))).Within(1e-3f),
                    "uncorrected, both stop where Unity's ceiling stops the shot");
        }
    }
}
