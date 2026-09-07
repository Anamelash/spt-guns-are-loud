using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    internal sealed class HearingControlRegressionTests
    {
        private readonly List<string> _configPaths = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string path in _configPaths)
                if (File.Exists(path)) File.Delete(path);
            _configPaths.Clear();
        }

        [TestCase(LoudnessPreset.Balanced)]
        [TestCase(LoudnessPreset.Loud)]
        [TestCase(LoudnessPreset.Punishing)]
        public void CrossMatrixKeepsBaseDoseAndRecoveryIndependentOfOutputControls(LoudnessPreset preset)
        {
            TuningSnapshot baseline = Tuning(preset, 100f, 100f);
            float[] controls = { 0f, 50f, 100f, 200f };

            foreach (float trauma in controls)
            foreach (float ringing in controls)
            {
                TuningSnapshot tuning = Tuning(preset, trauma, ringing);
                HearingResponse response = HearingResponseModel.Calculate(2.4f, 1.8f, tuning, 48000);
                HearingResponse sameTrauma = HearingResponseModel.Calculate(
                    2.4f, 1.8f, Tuning(preset, trauma, 100f), 48000);
                HearingResponse sameRinging = HearingResponseModel.Calculate(
                    2.4f, 1.8f, Tuning(preset, 100f, ringing), 48000);
                Assert.That(tuning.MasterSeverityScale, Is.EqualTo(baseline.MasterSeverityScale).Within(0.000001f));
                Assert.That(tuning.FastRecoverySeconds, Is.EqualTo(baseline.FastRecoverySeconds).Within(0.000001f));
                Assert.That(tuning.SlowRecoverySeconds, Is.EqualTo(baseline.SlowRecoverySeconds).Within(0.000001f));
                Assert.That(tuning.ExposureEnabled, Is.EqualTo(trauma > 0f || ringing > 0f));
                Assert.That(tuning.HearingLossEnabled, Is.EqualTo(trauma > 0f));
                Assert.That(tuning.TinnitusEnabled, Is.EqualTo(ringing > 0f));
                Assert.That(response.AttenuationLeftDb,
                    Is.EqualTo(sameTrauma.AttenuationLeftDb).Within(0.000001f));
                Assert.That(response.CutoffLeftHz,
                    Is.EqualTo(sameTrauma.CutoffLeftHz).Within(0.000001f));
                Assert.That(response.TinnitusLeft,
                    Is.EqualTo(sameRinging.TinnitusLeft).Within(0.000001f));
            }
        }

        [TestCase(LoudnessPreset.Balanced)]
        [TestCase(LoudnessPreset.Loud)]
        [TestCase(LoudnessPreset.Punishing)]
        public void RingingDoesNotChangeHearingLossTimeline(LoudnessPreset preset)
        {
            float[] reference = HearingTimeline(Tuning(preset, 100f, 0f));
            float[] cutoffReference = CutoffTimeline(Tuning(preset, 100f, 0f));
            foreach (float ringing in new[] { 50f, 100f, 200f })
            {
                AssertTimelineEqual(reference, HearingTimeline(Tuning(preset, 100f, ringing)));
                AssertTimelineEqual(cutoffReference, CutoffTimeline(Tuning(preset, 100f, ringing)));
            }
        }

        [TestCase(LoudnessPreset.Balanced)]
        [TestCase(LoudnessPreset.Loud)]
        [TestCase(LoudnessPreset.Punishing)]
        public void TraumaDoesNotChangeTinnitusTimeline(LoudnessPreset preset)
        {
            float[] reference = TinnitusTimeline(Tuning(preset, 0f, 100f));
            foreach (float trauma in new[] { 50f, 100f, 200f })
                AssertTimelineEqual(reference, TinnitusTimeline(Tuning(preset, trauma, 100f)));
        }

        [TestCase(LoudnessPreset.Balanced, 7f, 2800f, 0.014f)]
        [TestCase(LoudnessPreset.Loud, 10f, 1500f, 0.022f)]
        [TestCase(LoudnessPreset.Punishing, 14f, 900f, 0.03f)]
        public void DefaultControlsRetainLegacyTransferFunctions(
            LoudnessPreset preset,
            float maximumAttenuationDb,
            float minimumLowpassHz,
            float tinnitusMaximumLevel)
        {
            TuningSnapshot tuning = Tuning(preset, 100f, 100f);
            const float normalizedDose = 0.4f;
            HearingResponse response = HearingResponseModel.Calculate(
                normalizedDose * tuning.MaximumDose,
                normalizedDose * tuning.MaximumDose,
                tuning,
                48000);

            float expectedHearing = (float)Math.Pow(normalizedDose, 0.58f);
            float amount = (normalizedDose - 0.08f) / (1f - 0.08f);
            float expectedTinnitus = tinnitusMaximumLevel * (float)Math.Pow(amount, 0.7f);
            Assert.That(response.HearingLeft, Is.EqualTo(expectedHearing).Within(0.000001f));
            Assert.That(response.AttenuationLeftDb,
                Is.EqualTo(maximumAttenuationDb * expectedHearing).Within(0.000001f));
            Assert.That(response.CutoffLeftHz,
                Is.EqualTo(23520f + (minimumLowpassHz - 23520f) * expectedHearing).Within(0.001f));
            Assert.That(response.TinnitusLeft, Is.EqualTo(expectedTinnitus).Within(0.000001f));
        }

        [TestCase(LoudnessPreset.Balanced, 5.4f)]
        [TestCase(LoudnessPreset.Loud, 8.4f)]
        [TestCase(LoudnessPreset.Punishing, 12f)]
        public void DefaultControlsRetainExactLegacySlowRecovery(
            LoudnessPreset preset,
            float expectedSeconds)
        {
            Assert.That(
                Tuning(preset, 100f, 100f).SlowRecoverySeconds,
                Is.EqualTo(expectedSeconds).Within(0.000001f));
        }

        [TestCase(LoudnessPreset.Balanced)]
        [TestCase(LoudnessPreset.Loud)]
        [TestCase(LoudnessPreset.Punishing)]
        public void EachControlOwnsItsAmplitudeAndPerceivedDuration(LoudnessPreset preset)
        {
            float hearing50 = LastAudible(HearingTimeline(Tuning(preset, 50f, 100f)), 0.01f);
            float hearing100 = LastAudible(HearingTimeline(Tuning(preset, 100f, 100f)), 0.01f);
            float hearing200 = LastAudible(HearingTimeline(Tuning(preset, 200f, 100f)), 0.01f);
            float tinnitus50 = LastAudible(TinnitusTimeline(Tuning(preset, 100f, 50f)), 0.00001f);
            float tinnitus100 = LastAudible(TinnitusTimeline(Tuning(preset, 100f, 100f)), 0.00001f);
            float tinnitus200 = LastAudible(TinnitusTimeline(Tuning(preset, 100f, 200f)), 0.00001f);

            Assert.That(HearingTimeline(Tuning(preset, 50f, 100f))[0],
                Is.LessThan(HearingTimeline(Tuning(preset, 200f, 100f))[0]));
            Assert.That(TinnitusTimeline(Tuning(preset, 100f, 50f))[0],
                Is.LessThan(TinnitusTimeline(Tuning(preset, 100f, 200f))[0]));

            Assert.That(hearing50, Is.LessThanOrEqualTo(hearing100));
            Assert.That(hearing100, Is.LessThanOrEqualTo(hearing200));
            Assert.That(tinnitus50, Is.LessThanOrEqualTo(tinnitus100));
            Assert.That(tinnitus100, Is.LessThanOrEqualTo(tinnitus200));
        }

        [Test]
        public void DisabledIntervalClearsDoseBeforeLiveReenable()
        {
            TuningSnapshot enabled = Tuning(LoudnessPreset.Loud, 100f, 100f);
            TuningSnapshot disabled = Tuning(LoudnessPreset.Loud, 0f, 0f);
            var state = new HearingExposureState();
            state.Add(2f, 1f, enabled);
            state.Advance(0.016f, disabled);
            state.Advance(0.016f, enabled);

            Assert.That(state.LeftDose, Is.Zero);
            Assert.That(state.RightDose, Is.Zero);
            HearingResponse response = HearingResponseModel.Calculate(
                state.LeftDose, state.RightDose, enabled, 48000);
            Assert.That(response.ProcessingActive, Is.False);
        }

        [Test]
        public void MasterOffOnBetweenUpdatesConsumesLatchedReset()
        {
            ModConfig config;
            HearingExposureController controller = Controller(out config);
            HearingExposureState state = ControllerState(controller);
            state.Add(2f, 1f, controller.CurrentTuning());

            config.Enabled.Value = false;
            config.Enabled.Value = true;
            controller.CurrentTuning();

            Assert.That(state.LeftDose, Is.Zero);
            Assert.That(state.RightDose, Is.Zero);
        }

        [TestCase(0f, true)]
        [TestCase(0.05f, true)]
        [TestCase(0.1f, true)]
        [TestCase(0.11f, false)]
        public void BothControlsCrossingActivationThresholdLatchesResetBetweenUpdates(
            float transientPercent,
            bool expectedReset)
        {
            ModConfig config;
            HearingExposureController controller = Controller(out config);
            HearingExposureState state = ControllerState(controller);
            state.Add(2f, 1f, controller.CurrentTuning());

            config.HearingTrauma.Value = transientPercent;
            config.Ringing.Value = transientPercent;
            config.HearingTrauma.Value = 100f;
            config.Ringing.Value = 100f;
            controller.CurrentTuning();

            Assert.That(state.LeftDose == 0f, Is.EqualTo(expectedReset));
            Assert.That(state.RightDose == 0f, Is.EqualTo(expectedReset));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TurningOffOnlyOneResponseRetainsSharedDose(bool turnOffTrauma)
        {
            ModConfig config;
            HearingExposureController controller = Controller(out config);
            HearingExposureState state = ControllerState(controller);
            state.Add(2f, 1f, controller.CurrentTuning());

            if (turnOffTrauma) config.HearingTrauma.Value = 0f;
            else config.Ringing.Value = 0f;
            controller.CurrentTuning();

            Assert.That(state.LeftDose, Is.EqualTo(2f));
            Assert.That(state.RightDose, Is.EqualTo(1f));
        }

        private float[] HearingTimeline(TuningSnapshot tuning)
        {
            var state = SeededState(tuning);
            var values = new float[41];
            for (int second = 0; second < values.Length; second++)
            {
                HearingResponse response = HearingResponseModel.Calculate(
                    state.LeftDose, state.RightDose, tuning, 48000);
                values[second] = response.AttenuationLeftDb;
                state.Advance(1f, tuning);
            }
            return values;
        }

        private float[] CutoffTimeline(TuningSnapshot tuning)
        {
            var state = SeededState(tuning);
            var values = new float[41];
            for (int second = 0; second < values.Length; second++)
            {
                values[second] = HearingResponseModel.Calculate(
                    state.LeftDose, state.RightDose, tuning, 48000).CutoffLeftHz;
                state.Advance(1f, tuning);
            }
            return values;
        }

        private float[] TinnitusTimeline(TuningSnapshot tuning)
        {
            var state = SeededState(tuning);
            var values = new float[41];
            for (int second = 0; second < values.Length; second++)
            {
                values[second] = HearingResponseModel.Calculate(
                    state.LeftDose, state.RightDose, tuning, 48000).TinnitusLeft;
                state.Advance(1f, tuning);
            }
            return values;
        }

        private static HearingExposureState SeededState(TuningSnapshot tuning)
        {
            var state = new HearingExposureState();
            state.Add(2.4f, 1.8f, tuning);
            return state;
        }

        private TuningSnapshot Tuning(LoudnessPreset preset, float trauma, float ringing)
        {
            string path = Path.Combine(Path.GetTempPath(), "GunsAreLoud.Hearing." + Guid.NewGuid().ToString("N") + ".cfg");
            _configPaths.Add(path);
            var config = new ModConfig(new ConfigFile(path, false) { SaveOnConfigSet = false });
            config.Preset.Value = preset;
            config.HearingTrauma.Value = trauma;
            config.Ringing.Value = ringing;
            return config.GetTuning();
        }

        private HearingExposureController Controller(out ModConfig config)
        {
            string path = Path.Combine(Path.GetTempPath(), "GunsAreLoud.Hearing.Controller." + Guid.NewGuid().ToString("N") + ".cfg");
            _configPaths.Add(path);
            config = new ModConfig(new ConfigFile(path, false) { SaveOnConfigSet = false });
            var controller = (HearingExposureController)FormatterServices.GetUninitializedObject(
                typeof(HearingExposureController));
            controller.Initialize(config);
            return controller;
        }

        private static HearingExposureState ControllerState(HearingExposureController controller)
        {
            PropertyInfo property = typeof(HearingExposureController).GetProperty(
                "ExposureState",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(property, Is.Not.Null);
            return (HearingExposureState)property.GetValue(controller, null);
        }

        private static void AssertTimelineEqual(float[] expected, float[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(0.000001f), "timeline sample " + i);
        }

        private static float LastAudible(float[] timeline, float threshold)
        {
            for (int i = timeline.Length - 1; i >= 0; i--)
                if (timeline[i] > threshold) return i;
            return -1f;
        }
    }
}
