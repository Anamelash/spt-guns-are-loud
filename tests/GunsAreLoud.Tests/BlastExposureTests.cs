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
    }
}
