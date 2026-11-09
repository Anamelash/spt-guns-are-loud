using System;
using System.Collections.Generic;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// The explosion level: where a blast stands on the shot scale by its
    /// distance, what the engine is about to play it at, and the gain that
    /// takes it to its target under the same budget as a shot.
    /// </summary>
    [TestFixture]
    public sealed class ExplosionLevelTests
    {
        [SetUp]
        public void SetUp() => DirectLevelStore.Clear();

        [TearDown]
        public void TearDown() => DirectLevelStore.Clear();

        // ----- level index and target -----

        [Test]
        public void AHandGrenadeAtTwentyMetresStandsAtTheReferenceAndFallsSevenAndAHalfDecibelsPerDoubling()
        {
            Assert.That(ExplosionLevelModel.LevelIndex(20f), Is.EqualTo(4f).Within(1e-4f));
            Assert.That(ExplosionLevelModel.LevelIndex(10f), Is.EqualTo(4f + 7.526f).Within(0.01f));
            Assert.That(ExplosionLevelModel.LevelIndex(40f), Is.EqualTo(4f - 7.526f).Within(0.01f));
            Assert.That(ExplosionLevelModel.LevelIndex(200f), Is.EqualTo(4f - 25f).Within(1e-3f));
            // Never nearer than at the feet; a distance that is not a number is the reference.
            Assert.That(ExplosionLevelModel.LevelIndex(0f), Is.EqualTo(ExplosionLevelModel.LevelIndex(1f)));
            Assert.That(ExplosionLevelModel.LevelIndex(float.NaN), Is.EqualTo(4f));
            for (float d = 2f; d < 400f; d *= 1.5f)
                Assert.That(ExplosionLevelModel.LevelIndex(d), Is.GreaterThan(ExplosionLevelModel.LevelIndex(d * 1.5f)));
        }

        [Test]
        public void WithinTwentyMetresTheTargetIsTheTopOfTheScaleAndBeyondItFollowsTheSlope()
        {
            float top = DirectLevelModel.TopLevelDb;
            Assert.That(ExplosionLevelModel.TargetDb(5f, true, 0f), Is.EqualTo(top));
            Assert.That(ExplosionLevelModel.TargetDb(20f, true, 0f), Is.EqualTo(top));
            // 30 m: L = 4 − 25·log10(1.5) = −0.40 → 0.5 dB per dB under 2.5.
            Assert.That(ExplosionLevelModel.TargetDb(30f, true, 0f), Is.EqualTo(top - 0.5f * 2.902f).Within(0.01f));
            Assert.That(ExplosionLevelModel.TargetDb(40f, true, 0f), Is.EqualTo(top - 0.5f * 6.026f).Within(0.01f));
            // The room and the engine trim stand on top, as for a shot.
            Assert.That(ExplosionLevelModel.TargetDb(30f, false, 0f),
                Is.EqualTo(ExplosionLevelModel.TargetDb(30f, true, 0f) + DirectLevelModel.OutdoorDb).Within(1e-5f));
            Assert.That(ExplosionLevelModel.TargetDb(30f, true, 2f),
                Is.EqualTo(ExplosionLevelModel.TargetDb(30f, true, 0f) + 2f).Within(1e-5f));
            // A grenade at 40 m sits where a 9×18 pistol sits at the shooter's ear.
            float pistol = DirectLevelModel.EnergyTargetDb(-2.9f, true, 0f);
            Assert.That(ExplosionLevelModel.TargetDb(40f, true, 0f), Is.EqualTo(pistol).Within(0.35f));
        }

        // ----- the engine's distance law -----

        [Test]
        public void RolloffFollowsUnityAndHoldsBeyondTheMaximumDistance()
        {
            Assert.That(ExplosionLevelModel.RolloffGain(true, 1f, 1000f, 0.5f), Is.EqualTo(1f));
            Assert.That(ExplosionLevelModel.RolloffGain(true, 1f, 1000f, 10f), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(ExplosionLevelModel.RolloffGain(true, 1f, 1000f, 5000f), Is.EqualTo(0.001f).Within(1e-6f));
            Assert.That(ExplosionLevelModel.RolloffGain(false, 1f, 101f, 51f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(ExplosionLevelModel.RolloffGain(false, 1f, 101f, 500f), Is.EqualTo(0f));
        }

        [Test]
        public void TheTwoDimensionalShareOfTheVoiceDoesNotAttenuate()
        {
            Assert.That(ExplosionLevelModel.SpatialGain(0f, 0.01f), Is.EqualTo(1f));
            Assert.That(ExplosionLevelModel.SpatialGain(1f, 0.01f), Is.EqualTo(0.01f).Within(1e-6f));
            Assert.That(ExplosionLevelModel.SpatialGain(0.5f, 0.1f), Is.EqualTo(0.55f).Within(1e-6f));
            // A grenade 30 m away is nine tenths spatial: its own level sits about 17 dB under its recording.
            float gain = ExplosionLevelModel.SpatialGain(0.9f, ExplosionLevelModel.RolloffGain(true, 1f, 1000f, 30f));
            Assert.That(20f * Math.Log10(gain), Is.EqualTo(-17.7f).Within(0.2f));
        }

        // ----- the correction -----

        private static ShotBudget Close(float volumeAtListener) =>
            new ShotBudget(0f, 1f, volumeAtListener);

        [Test]
        public void ACloseBlastIsCutToTheTopOfTheScaleAndADistantOneIsLiftedToItsPlace()
        {
            // The close recording of the game: K 0–240 at 0 dB, peak at full scale.
            // At 5 m the engine plays it almost entirely two-dimensional.
            float spatial = ExplosionLevelModel.SpatialGain(5f / 30f, ExplosionLevelModel.RolloffGain(true, 1f, 1000f, 5f));
            ShotBudgetSolution close = DirectLevelModel.Solve(Close(spatial), ExplosionLevelModel.TargetDb(5f, true, 0f), 1f,
                0f, DirectLevelModel.PeakBudget, 0f, ExplosionLevelModel.MaximumLiftDb);
            Assert.That(close.GainDb, Is.EqualTo(DirectLevelModel.TopLevelDb - 20f * (float)Math.Log10(spatial)).Within(0.01f));
            Assert.That(close.BudgetBound, Is.False);
            Assert.That(close.GainDb, Is.LessThan(0f), "a close blast of the game is louder than the top of the scale");

            // At 40 m the engine has taken it about 18 dB down; the target is the pistol's.
            float far = ExplosionLevelModel.SpatialGain(0.9f, ExplosionLevelModel.RolloffGain(true, 1f, 1000f, 40f));
            ShotBudgetSolution distant = DirectLevelModel.Solve(Close(far), ExplosionLevelModel.TargetDb(40f, true, 0f), 1f,
                0f, DirectLevelModel.PeakBudget, 0f, ExplosionLevelModel.MaximumLiftDb);
            float expected = ExplosionLevelModel.TargetDb(40f, true, 0f) - 20f * (float)Math.Log10(far);
            Assert.That(expected, Is.GreaterThan(9f).And.LessThan(ExplosionLevelModel.MaximumLiftDb));
            Assert.That(distant.GainDb, Is.EqualTo(expected).Within(0.01f));
            Assert.That(distant.BudgetBound, Is.False);
            Assert.That(far * DirectLevelModel.Linear(distant.GainDb), Is.LessThan(DirectLevelModel.PeakBudget));
        }

        [Test]
        public void TheLiftStopsAtTheExplosionBoundAndAtThePeakBudget()
        {
            // A quiet distant recording asks for more than the bound allows.
            ShotBudget faint = new ShotBudget(-30f, 0.05f, 0.1f);
            ShotBudgetSolution bounded = DirectLevelModel.Solve(faint, -6f, 1f, 0f, DirectLevelModel.PeakBudget, 0f,
                ExplosionLevelModel.MaximumLiftDb);
            Assert.That(bounded.GainDb, Is.EqualTo(ExplosionLevelModel.MaximumLiftDb).Within(1e-5f));
            // The same recording under the shot's own bound stops at 12 dB: existing callers are unchanged.
            Assert.That(DirectLevelModel.Solve(faint, -6f, 1f).GainDb, Is.EqualTo(DirectLevelModel.MaximumLiftDb).Within(1e-5f));

            // A recording at full scale whose 240 ms level is low cannot be lifted past the budget.
            ShotBudget sparse = new ShotBudget(-20f, 1f, 1f);
            ShotBudgetSolution capped = DirectLevelModel.Solve(sparse, -4.5f, 1f, 0f, DirectLevelModel.PeakBudget, 0f,
                ExplosionLevelModel.MaximumLiftDb);
            Assert.That(capped.BudgetBound, Is.True);
            Assert.That(capped.GainDb, Is.EqualTo(20f * (float)Math.Log10(DirectLevelModel.PeakBudget)).Within(1e-4f));
        }

        [Test]
        public void TwoBlendedRecordingsAreOneEventAtTheirOwnVolumes()
        {
            // Close and distant clip of one voice, the blend half way: energies add, peaks add.
            ShotBudget close = new ShotBudget(0f, 1f, 0.5f);
            ShotBudget distant = new ShotBudget(-2f, 1f, 0.5f);
            ShotBudget both = DirectLevel.Combine(close, distant);
            Assert.That(both.Volume, Is.EqualTo(1f));
            Assert.That(both.Peak, Is.EqualTo(1f).Within(1e-6f));
            float expected = DirectLevelModel.CombinePlayingDb(
                DirectLevelModel.PlayingLevelDb(0f, 0.5f), DirectLevelModel.PlayingLevelDb(-2f, 0.5f));
            Assert.That(both.AttackDb, Is.EqualTo(expected).Within(1e-5f));
            Assert.That(both.HasCopy, Is.False);
        }

        // ----- the store -----

        [Test]
        public void TheStoreKeepsTheBodyWindowBesideTheAttackAndLendsItToSiblings()
        {
            DirectLevelStore.Set(1, -3f, 0f, "playback", -1f);
            DirectLevelStore.Set(2, -12f, -6f, "playback");
            Assert.That(DirectLevelStore.TryGetBody(1, out float body, out float peak, out string source), Is.True);
            Assert.That(body, Is.EqualTo(-1f));
            Assert.That(peak, Is.EqualTo(0f));
            Assert.That(source, Is.EqualTo("playback"));
            Assert.That(DirectLevelStore.TryGetBody(2, out _, out _, out _), Is.False,
                "a shot recording has an attack and a peak but no body window");
            // A variant not measured yet borrows the mean of its measured siblings.
            DirectLevelStore.Set(3, -4f, -2f, "playback", -3f);
            DirectLevelStore.Siblings(1, 3, 4);
            Assert.That(DirectLevelStore.TryGetBody(4, out body, out peak, out source), Is.True);
            Assert.That(body, Is.EqualTo(-2f).Within(1e-6f));
            Assert.That(peak, Is.EqualTo(-1f).Within(1e-6f));
            Assert.That(source, Is.EqualTo(DirectLevelStore.SiblingSource));
            // The attack path is unchanged by the body.
            Assert.That(DirectLevelStore.TryGet(1, out float attack, out _, out _, out _), Is.True);
            Assert.That(attack, Is.EqualTo(-3f));
        }

        [Test]
        public void TheCacheRoundTripsTheBodyWindowAndLeavesEntriesWithoutOneAlone()
        {
            var entries = new Dictionary<string, LevelCacheEntry>
            {
                ["gren|1|44100|2|stream"] = new LevelCacheEntry { AttackWindowDb = -0.3f, PeakDb = 0f, BodyWindowDb = -1.4f, BaseVolume = 1f },
                ["akm|1|44100|2|stream"] = new LevelCacheEntry { AttackWindowDb = -8f, PeakDb = -0.5f, BaseVolume = 1f }
            };
            string text = LevelCacheStore.Serialize(entries, "test");
            Assert.That(text, Does.Contain("\"k240\": -1.4"));
            var parsed = new Dictionary<string, LevelCacheEntry>();
            Assert.That(LevelCacheStore.TryDeserialize(text, parsed, out string reason), Is.True, reason);
            Assert.That(parsed["gren|1|44100|2|stream"].HasBody, Is.True);
            Assert.That(parsed["gren|1|44100|2|stream"].BodyWindowDb, Is.EqualTo(-1.4f).Within(1e-6f));
            Assert.That(parsed["akm|1|44100|2|stream"].HasDirect, Is.True);
            Assert.That(parsed["akm|1|44100|2|stream"].HasBody, Is.False);
        }

        // ----- the probe -----

        [Test]
        public void ADelayedProbeOpensItsWindowWhenTheSoundArrives()
        {
            HearingImpactProcessor processor = (HearingImpactProcessor)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(HearingImpactProcessor));
            // 8 kHz: the window is 1920 frames; the sound flies 960 frames.
            int id = processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000, default, 960);
            var first = new float[1920];
            for (int i = 960; i < first.Length; i++) first[i] = 0.5f;
            processor.ProcessAudioBufferForTests(first, 1);
            Assert.That(processor.TryTakeMasterProbe(out _), Is.False, "the window has only half its frames");
            var second = new float[1920];
            for (int i = 0; i < second.Length; i++) second[i] = 0.5f;
            processor.ProcessAudioBufferForTests(second, 1);
            Assert.That(processor.TryTakeMasterProbe(out ListenerBandTelemetry telemetry), Is.True);
            Assert.That(telemetry.ProbeId, Is.EqualTo(id));
            Assert.That(telemetry.DelayFrames, Is.EqualTo(960));
            Assert.That(telemetry.Frames, Is.EqualTo(1920));
            // Only what came after the delay is in the window: a constant 0.5, not 0.5 diluted by silence.
            Assert.That(telemetry.RmsTotal, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(telemetry.PostSamplePeak, Is.EqualTo(0.5f).Within(1e-3f));
        }
    }
}
