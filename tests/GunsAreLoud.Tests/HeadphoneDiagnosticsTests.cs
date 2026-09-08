using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    public class HeadphoneDiagnosticsTests
    {
        [Test]
        public void MenuBeforeMixerRequestIsWaitingOnlyWithConfirmedRegistration()
        {
            Assert.That(HeadphoneDiagnostics.InitializationStatus(true, true, true, false, null, 0),
                Does.StartWith("REGISTERED / WAITING"));
            Assert.That(HeadphoneDiagnostics.InitializationStatus(true, false, true, false, null, 0),
                Does.StartWith("UNVERIFIED"));
            Assert.That(HeadphoneDiagnostics.InitializationStatus(true, true, false, false, null, 0),
                Does.StartWith("ERROR"));
        }

        [Test]
        public void ActualMixerFailureCannotBeHiddenAsWaitingOrReady()
        {
            Assert.That(HeadphoneDiagnostics.InitializationStatus(true, true, true, true, "broken asset", 0),
                Does.Contain("mixer load failed: broken asset"));
            Assert.That(HeadphoneDiagnostics.InitializationStatus(true, true, true, true, null, 0),
                Does.StartWith("ERROR"));
            Assert.That(HeadphoneDiagnostics.InitializationStatus(true, true, true, true, null, 1), Is.Null);
        }

        [Test]
        public void HistoricalNonzeroCounterDoesNotProveCurrentProcessing()
        {
            var activity = new HeadphoneFrameActivity();
            Assert.That(activity.Observe(true, 100000, 1), Is.False);
            Assert.That(activity.Observe(true, 100000, 1.5f), Is.False);
            Assert.That(activity.Observe(true, 100512, 2), Is.True);
            Assert.That(activity.Delta, Is.EqualTo(512));
            Assert.That(activity.Observe(true, 100512, 2.5f), Is.False);
        }

        [Test]
        public void ReopenResetAndMissingExportInvalidatePreviousEvidence()
        {
            var activity = new HeadphoneFrameActivity();
            activity.Observe(true, 100, 1);
            Assert.That(activity.Observe(true, 200, 10), Is.False);
            Assert.That(activity.Observe(true, 0, 10.5f), Is.False);
            Assert.That(activity.Observe(false, 0, 11), Is.False);
            Assert.That(activity.Observe(true, 100, 11.5f), Is.False);
            Assert.That(activity.Observe(true, 200, 12), Is.True);
        }
    }
}
