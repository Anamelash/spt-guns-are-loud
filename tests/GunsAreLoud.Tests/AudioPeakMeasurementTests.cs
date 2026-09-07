using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using System.Runtime.Serialization;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class AudioPeakMeasurementTests
    {
        [Test]
        public void RawAndPostClampSamplePeaksRemainDistinct()
        {
            var measurement = new AudioPeakMeasurement();
            measurement.Add(1.4f, 1f);
            measurement.Add(-1.2f, -1f);
            measurement.Add(0.7f, 0.7f);

            Assert.That(measurement.PreClampSamplePeak, Is.EqualTo(1.4f));
            Assert.That(measurement.PostClampSamplePeak, Is.EqualTo(1f));
            Assert.That(measurement.PreClampOverCount, Is.EqualTo(2));
            Assert.That(measurement.PostClampOverCount, Is.Zero);
        }

        [Test]
        public void HearingTransferExposesRawSampleBeforeExistingClamp()
        {
            var state = new HearingDspChannelState();
            float postClamp = state.Process(0.8f, 1f, 2f, 1f, 0f, 0f, 1f, out float preClamp);

            Assert.That(preClamp, Is.EqualTo(1.6f).Within(0.000001f));
            Assert.That(postClamp, Is.EqualTo(1f));
        }

        [Test]
        public void NonFiniteSamplesAreCountedWithoutBecomingPeaks()
        {
            var measurement = new AudioPeakMeasurement();
            measurement.Add(float.NaN, 0f);
            measurement.Add(float.PositiveInfinity, 1f);

            Assert.That(measurement.NonFiniteCount, Is.EqualTo(2));
            Assert.That(measurement.PreClampSamplePeak, Is.Zero);
            Assert.That(measurement.PostClampSamplePeak, Is.EqualTo(1f));
        }

        [Test]
        public void DiagnosticWindowCompletesAtConfiguredFrameCount()
        {
            var window = new AudioPeakWindow();
            window.Reset(3);

            window.AddFrame(0.2f, 0.2f, 0.3f, 0.3f);
            window.AddFrame(1.2f, 1f, -0.4f, -0.4f);
            Assert.That(window.Complete, Is.False);
            window.AddFrame(0.5f, 0.5f, 0.6f, 0.6f);
            window.AddFrame(2f, 1f, 2f, 1f);

            Assert.That(window.Complete, Is.True);
            Assert.That(window.Frames, Is.EqualTo(3));
            Assert.That(window.Measurement.PreClampSamplePeak, Is.EqualTo(1.2f));
            Assert.That(window.Measurement.PreClampOverCount, Is.EqualTo(1));
        }

        [Test]
        public void ProcessorProbeCompletesAndCountsMonoSamplesOnce()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            int id = processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000);
            var buffer = new float[1920];
            for (int i = 0; i < buffer.Length; i++) buffer[i] = 1.2f;

            processor.ProcessAudioBufferForTests(buffer, 1);

            Assert.That(processor.TryTakeMasterProbe(out ListenerBandTelemetry telemetry), Is.True);
            Assert.That(telemetry.ProbeId, Is.EqualTo(id));
            Assert.That(telemetry.Frames, Is.EqualTo(1920));
            Assert.That(telemetry.ChannelsMeasured, Is.EqualTo(1));
            Assert.That(telemetry.PreClampOverCount, Is.EqualTo(1920));
            Assert.That(telemetry.ChannelScope, Is.EqualTo(ListenerProbeChannelScope.FrontPair));
        }

        [Test]
        public void NewProbeBoutDoesNotInheritFrozenBandFilterState()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000);
            var first = new float[1920];
            for (int i = 0; i < first.Length; i++) first[i] = 0.8f;
            processor.ProcessAudioBufferForTests(first, 1);
            Assert.That(processor.TryTakeMasterProbe(out _), Is.True);

            processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000);
            var silence = new float[1920];
            processor.ProcessAudioBufferForTests(silence, 1);

            Assert.That(processor.TryTakeMasterProbe(out ListenerBandTelemetry telemetry), Is.True);
            Assert.That(telemetry.Rms20To80, Is.Zero);
            Assert.That(telemetry.Rms80To160, Is.Zero);
            Assert.That(telemetry.Rms160To315, Is.Zero);
            Assert.That(telemetry.Rms315To2000, Is.Zero);
        }

        [Test]
        public void BypassProbeCountsNonFiniteInputWithoutChangingOrPoisoningDryBuffer()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000);
            var invalid = new float[1920];
            invalid[0] = float.NaN;
            processor.ProcessAudioBufferForTests(invalid, 1);

            Assert.That(float.IsNaN(invalid[0]), Is.True);
            Assert.That(processor.TryTakeMasterProbe(out ListenerBandTelemetry first), Is.True);
            Assert.That(first.NonFiniteCount, Is.EqualTo(1));

            processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000);
            var silence = new float[1920];
            processor.ProcessAudioBufferForTests(silence, 1);
            Assert.That(processor.TryTakeMasterProbe(out ListenerBandTelemetry second), Is.True);
            Assert.That(second.RmsTotal, Is.Zero);
            Assert.That(second.NonFiniteCount, Is.Zero);
        }

        [Test]
        public void InvalidStereoRightChannelContributesZeroInsteadOfCopiedLeftEnergy()
        {
            HearingImpactProcessor processor = UninitializedProcessor();
            processor.ArmMasterProbe(AutomaticPitchedRoute.BuiltInDSP, false, 8000);
            var buffer = new float[1920 * 2];
            for (int frame = 0; frame < 1920; frame++)
            {
                buffer[frame * 2] = 1f;
                buffer[frame * 2 + 1] = float.NaN;
            }

            processor.ProcessAudioBufferForTests(buffer, 2);

            Assert.That(processor.TryTakeMasterProbe(out ListenerBandTelemetry telemetry), Is.True);
            Assert.That(telemetry.RmsTotal, Is.EqualTo(0.70710678f).Within(0.000001f));
            Assert.That(telemetry.NonFiniteCount, Is.EqualTo(1920));
            Assert.That(telemetry.ChannelsMeasured, Is.EqualTo(2));
        }

        private static HearingImpactProcessor UninitializedProcessor()
        {
            return (HearingImpactProcessor)FormatterServices.GetUninitializedObject(
                typeof(HearingImpactProcessor));
        }
    }
}
