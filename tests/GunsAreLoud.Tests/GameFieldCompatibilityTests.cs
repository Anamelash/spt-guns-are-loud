using GunsAreLoud.Client.Patches;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// The accessors this mod resolves out of the game assembly at load. They are
    /// fail-closed by design: when one cannot be built the local-shot path simply
    /// does nothing, which is safe but invisible — a raid then sounds vanilla and
    /// only the log says why. These tests make that failure loud instead.
    /// </summary>
    [TestFixture]
    public sealed class GameFieldCompatibilityTests
    {
        [Test]
        public void LocalShotDetectionResolvesAgainstTheShippedGameAssembly()
        {
            Assert.That(ShotDescriptorFactory.CompatibilityAvailable, Is.True,
                "Without BaseSoundPlayer.playersBridge no shot is ever recognised as the " +
                "local player's, and the whole mod goes quiet.");
            Assert.That(ShotDescriptorFactory.FastBridgeAccessAvailable, Is.True,
                "The field is declared as the bridge interface; asking Harmony for the " +
                "concrete PlayerBridge type throws and falls back to reflection.");
        }

        [Test]
        public void AutomaticWeaponQueueFieldsResolveAgainstTheShippedGameAssembly()
        {
            Assert.That(EftAudioFields.QueueAvailable, Is.True,
                "Without WeaponSoundPlayer._queue the automatic copy never finds its source.");
            Assert.That(EftAudioFields.LastSourceAvailable, Is.True,
                "Without SuperBetterAudioQueue._lastSource the burst cannot pick the " +
                "source EFT actually played on.");
        }
    }
}
