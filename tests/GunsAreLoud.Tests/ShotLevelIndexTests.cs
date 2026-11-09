using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class ShotLevelIndexTests
    {
        [TestCase(0.55f, 5.193f)]
        [TestCase(1.0f, 0f)]
        [TestCase(1.1f, -0.828f)]
        public void DistanceTermIsSphericalSpreadingFromOneMetre(float distance, float expected)
        {
            Assert.That(ShotLevelIndex.DistanceIndex(distance), Is.EqualTo(expected).Within(0.001f));
        }

        [Test]
        public void DistanceIsHeldBetweenAHeldPistolAndTwoMetres()
        {
            Assert.That(ShotLevelIndex.DistanceIndex(0.05f), Is.EqualTo(ShotLevelIndex.DistanceIndex(0.3f)));
            Assert.That(ShotLevelIndex.DistanceIndex(0.3f), Is.EqualTo(10.458f).Within(0.001f));
            Assert.That(ShotLevelIndex.DistanceIndex(50f), Is.EqualTo(ShotLevelIndex.DistanceIndex(2f)));
            Assert.That(ShotLevelIndex.DistanceIndex(2f), Is.EqualTo(-6.021f).Within(0.001f));
            Assert.That(ShotLevelIndex.Compute(3.4f, 900f, 0.1f).MuzzleDistance, Is.EqualTo(0.3f));
        }

        [Test]
        public void EnergyTermIsZeroAtTheReferenceCartridge()
        {
            // 4 g at 948.7 m/s is 1800 J.
            ShotLevel level = ShotLevelIndex.Compute(4f, 948.683f, 1f);
            Assert.That(level.EnergyJoules, Is.EqualTo(1800f).Within(0.1f));
            Assert.That(level.EnergyIndex, Is.EqualTo(0f).Within(0.001f));
            Assert.That(level.LevelIndex, Is.EqualTo(0f).Within(0.001f));
            Assert.That(level.Fallback, Is.False);

            ShotLevel tenTimes = ShotLevelIndex.Compute(40f, 948.683f, 0.5f);
            Assert.That(tenTimes.EnergyIndex, Is.EqualTo(10f).Within(0.001f));
            Assert.That(tenTimes.LevelIndex, Is.EqualTo(10f + 6.021f).Within(0.001f));
        }

        [Test]
        public void MuzzleDevicesEnterTheLevelAtTheConfiguredScaleAndNowhereElse()
        {
            ShotLevel plain = ShotLevelIndex.Compute(4f, 948.683f, 1f);
            ShotLevel canned = ShotLevelIndex.Compute(4f, 948.683f, 1f, ShotLevelIndex.DeviceIndex(-30, 0.8f));
            Assert.That(canned.DeviceIndex, Is.EqualTo(-24f).Within(1e-4f));
            Assert.That(canned.LevelIndex, Is.EqualTo(plain.LevelIndex - 24f).Within(1e-4f));
            Assert.That(canned.EnergyIndex, Is.EqualTo(plain.EnergyIndex), "the charge, and so the spectrum, is the round's");
            Assert.That(canned.DistanceIndex, Is.EqualTo(plain.DistanceIndex));
            Assert.That(ShotLevelIndex.DeviceIndex(-30, 0f), Is.Zero, "0 ignores the devices");
            Assert.That(ShotLevelIndex.DeviceIndex(20, 0.8f), Is.EqualTo(16f).Within(1e-4f), "a brake raises the level");
            Assert.That(ShotLevelIndex.Compute(4f, 948.683f, 1f, float.NaN).LevelIndex, Is.EqualTo(plain.LevelIndex));
        }

        [TestCase(0f, 900f)]
        [TestCase(3.4f, 0f)]
        [TestCase(0f, 0f)]
        public void ARoundWithoutMassOrVelocityIsTheReferenceCartridgeAndFlagged(float mass, float velocity)
        {
            ShotLevel level = ShotLevelIndex.Compute(mass, velocity, 0.9f);
            Assert.That(level.Fallback, Is.True);
            Assert.That(level.EnergyIndex, Is.EqualTo(0f));
            Assert.That(level.EnergyJoules, Is.EqualTo(0f));
            Assert.That(level.DistanceIndex, Is.EqualTo(ShotLevelIndex.DistanceIndex(0.9f)));
        }

        [Test]
        public void MedianMassIgnoresAnOutlyingRound()
        {
            Assert.That(ShotLevelIndex.MedianMassGram(new[] { 19f, 28f, 33f, 45f }), Is.EqualTo(30.5f));
            Assert.That(ShotLevelIndex.MedianMassGram(new[] { 45f, 19f, 28f }), Is.EqualTo(28f));
            Assert.That(ShotLevelIndex.MedianMassGram(new[] { 0f, 28f, -1f }), Is.EqualTo(28f));
            Assert.That(ShotLevelIndex.MedianMassGram(new float[0]), Is.EqualTo(0f));
        }

        [Test]
        public void AShotShellIsRankedByTheSingleProjectileOfItsCaliber()
        {
            Assert.That(ShotLevelIndex.ResolvePayloadMassGram(3.4f, 1, 28.3f), Is.EqualTo(3.4f));
            Assert.That(ShotLevelIndex.ResolvePayloadMassGram(0.09f, 8, 28.3f), Is.EqualTo(28.3f));
            Assert.That(ShotLevelIndex.ResolvePayloadMassGram(0.5f, 8, 0f), Is.EqualTo(4f));
        }

        [TestCase(0f, -6f, 0f)]
        [TestCase(0f, 2.7f, 0f)]
        [TestCase(0f, 9.83f, 0f)]
        [TestCase(6f, -6f, 0f)]
        [TestCase(6f, 2.7f, 1.991f)]
        [TestCase(6f, 9.83f, 6f)]
        [TestCase(6f, 40f, 8f)]
        public void HeadsetPenaltyFollowsTheEnergyTerm(float penaltyAtHeavy, float energyIndex, float expected)
        {
            Assert.That(ExposureModel.HeadsetCaliberPenaltyDb(energyIndex, penaltyAtHeavy),
                Is.EqualTo(expected).Within(0.005f));
        }

        [Test]
        public void CartridgeScaleRunsFromARimfireRoundToFiftyBmg()
        {
            Assert.That(DirectLoudnessModel.CartridgePosition(-11.09f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(DirectLoudnessModel.CartridgePosition(9.83f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(DirectLoudnessModel.CartridgePosition(-30f), Is.EqualTo(0f));
            Assert.That(DirectLoudnessModel.CartridgePosition(20f), Is.EqualTo(1f));
            Assert.That(ShotLevelIndex.EnergyIndex(140f), Is.EqualTo(-11.09f).Within(0.01f));
        }

        [Test]
        public void SeverityPassesThroughTheLongGunAnchors()
        {
            Assert.That(ExposureModel.BaseSeverity(-0.2f), Is.EqualTo(0.86f).Within(0.01f));
            Assert.That(ExposureModel.BaseSeverity(3.6f), Is.EqualTo(1.12f).Within(0.02f));
            Assert.That(ExposureModel.BaseSeverity(9.0f), Is.EqualTo(1.55f).Within(0.02f));
            Assert.That(ExposureModel.BaseSeverity(-50f), Is.EqualTo(0f));
        }
    }
}
