using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class PitchedLevelMeterTests
    {
        [TestCase(44100)]
        [TestCase(48000)]
        public void AttackAndTailUseSeparateEnergyWindowsWithoutStereoCancellation(int rate)
        {
            var meter = new PitchedLevelMeter();
            meter.Reset(rate);
            var attack = new[] { 0.25f, -0.25f };
            var tail = new[] { 0.125f, -0.125f };
            int attackFrames = (int)(rate * LowEndLevelModel.WindowSeconds);
            for (int frame = 0; frame < attackFrames; frame++) meter.AddFrame(attack, 0, 2);
            for (int frame = 0; frame < rate / 2; frame++) meter.AddFrame(tail, 0, 2);
            Assert.That(meter.AttackFrames, Is.EqualTo(attackFrames));
            Assert.That(meter.TailFrames, Is.EqualTo(rate / 2));
            Assert.That(meter.AttackRms, Is.EqualTo(0.25f));
            Assert.That(meter.TailRms, Is.EqualTo(0.125f));
            meter.Reset(rate);
            Assert.That(meter.AttackRms, Is.Zero);
            Assert.That(meter.TailRms, Is.Zero);
            Assert.That(meter.AttackFrames + meter.TailFrames, Is.Zero);
        }

        [Test]
        public void QuietLongTailDoesNotDiluteAttackAndPartialWindowsExposeFrameCount()
        {
            var meter = new PitchedLevelMeter();
            meter.Reset(48000);
            var mono = new[] { 0.5f };
            for (int i = 0; i < 100; i++) meter.AddFrame(mono, 0, 1);
            Assert.That(meter.AttackFrames, Is.EqualTo(100));
            Assert.That(meter.AttackRms, Is.EqualTo(0.5f));
            for (int i = 100; i < 8640; i++) meter.AddFrame(mono, 0, 1);
            mono[0] = 0;
            for (int i = 0; i < 48000; i++) meter.AddFrame(mono, 0, 1);
            Assert.That(meter.AttackRms, Is.EqualTo(0.5f));
            Assert.That(meter.TailRms, Is.Zero);
        }
    }
}
