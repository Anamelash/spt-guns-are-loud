using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class GunshotContrastTests
    {
        [TestCase("Guns")]
        [TestCase("Gunshots")]
        [TestCase("Occluded")]
        [TestCase("OccludedTail")]
        [TestCase("GunCompressor")]
        [TestCase("Compressor")]
        [TestCase("Headphones")]
        [TestCase("Returns")]
        [TestCase("Reverb")]
        [TestCase("TarkovReverb")]
        [TestCase("Main")]
        [TestCase("World")]
        [TestCase("UI")]
        [TestCase("Music")]
        [TestCase("Chat")]
        [TestCase("Voip")]
        [TestCase("VoipReverb")]
        [TestCase("UnknownModGroup")]
        [TestCase(null)]
        public void GunsSharedReturnsAndCommunicationsStayUntouched(string group)
        {
            Assert.That(GunshotContrastModel.RouteGain(group, true, true, 18), Is.EqualTo(1));
        }

        [TestCase("ClientPlayerMovement")]
        [TestCase("ObservedPlayerMovement")]
        [TestCase("ClientPlayerSpeech")]
        [TestCase("Environment")]
        [TestCase("Ambient")]
        [TestCase("Rain")]
        [TestCase("AmbientOutDayBypass")]
        [TestCase("Occlusion")]
        [TestCase("Instrumental")]
        [TestCase("Inventory")]
        public void NonGunSourcesReceiveSingleRequestedAttenuation(string group)
        {
            float gain = GunshotContrastModel.RouteGain(group, true, true, 6);
            Assert.That(20 * Math.Log10(gain), Is.EqualTo(-6).Within(0.00001));
        }

        [TestCase(0, 1)]
        [TestCase(6, 0.501187234)]
        [TestCase(18, 0.12589254)]
        [TestCase(-10, 1)]
        [TestCase(100, 0.12589254)]
        public void GainHasCorrectDecibelScaleAndBounds(float db, double expected)
        {
            Assert.That(GunshotContrastModel.Gain(db), Is.EqualTo(expected).Within(0.000001));
        }

        [Test]
        public void InvalidValuesFailOpen()
        {
            Assert.That(GunshotContrastModel.Gain(float.NaN), Is.EqualTo(1));
            Assert.That(GunshotContrastModel.Gain(float.PositiveInfinity), Is.EqualTo(1));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void DisabledModAndLeavingGameplayRestoreUnity(bool enabled, bool inGame)
        {
            Assert.That(GunshotContrastModel.RouteGain("Ambient", enabled, inGame, 18), Is.EqualTo(1));
        }

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        public void MultipleAudioSourcesCannotAccidentallyShareAFilter(int sourceCount, bool safe)
        {
            Assert.That(GunshotContrastModel.SafeSourceLayout(sourceCount), Is.EqualTo(safe));
        }

        [Test]
        public void MixerInputTableCoversOnlyExplicitEligibleSourceGroups()
        {
            ContrastRouteSpec[] routes = GunshotContrastMixerRouteTable.Routes;
            Assert.That(routes, Has.Length.EqualTo(52));
            Assert.That(routes.Select(route => route.ParentPath).Distinct().Count(), Is.EqualTo(routes.Length));
            Assert.That(routes.Select(route => route.InputPath).Distinct().Count(), Is.EqualTo(routes.Length));
            Assert.That(routes.Select(route => route.Parameter).Distinct().Count(), Is.EqualTo(routes.Length));
            Assert.That(routes.All(route => GunshotContrastModel.ShouldAttenuate(route.ParentName)), Is.True);
            Assert.That(routes.Count(route => route.ParentName == "Occlusion"), Is.EqualTo(2));
            Assert.That(routes.Any(route => route.ParentName == "Inventory"), Is.True);
            Assert.That(routes.Any(route => route.ParentName == "Instrumental"), Is.True);
            Assert.That(routes.Any(route => route.ParentName == "Voip"), Is.False);
            Assert.That(routes.Any(route => route.ParentName == "Returns"), Is.False);
            Assert.That(routes.Any(route => route.ParentName == "Gunshots"), Is.False);
        }

        [Test]
        public void StereoRatioPolarityAndTimingArePreservedWithoutClipping()
        {
            float[] data = { 0f, 0f, 2f, -1f, 0.5f, -0.25f, 0f, 0f };
            float[] original = (float[])data.Clone();
            float gain = GunshotContrastModel.Gain(6);
            GunshotContrastModel.Apply(data, gain);
            for (int i = 0; i < data.Length; i++) Assert.That(data[i], Is.EqualTo(original[i] * gain));
            Assert.That(data[2], Is.GreaterThan(1), "The insert must not introduce a limiter.");
        }

        [Test]
        public void RealCallbackUsesFreshGainAndProtectsReroutedGunImmediately()
        {
            // No scene/native Unity calls needed: exercise the actual managed callback.
            var filter = (GunshotContrastFilter)FormatterServices.GetUninitializedObject(typeof(GunshotContrastFilter));
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), filter,
                typeof(GunshotContrastFilter).GetMethod("OnAudioFilterRead", BindingFlags.NonPublic | BindingFlags.Instance));
            filter.SetGain(GunshotContrastModel.RouteGain("Ambient", true, true, 6));
            float[] first = { 1f, -0.5f };
            callback(first, 2);
            Assert.That(first[0], Is.EqualTo(GunshotContrastModel.Gain(6)));
            Assert.That(filter.CallbackCount, Is.EqualTo(1));
            filter.SetGain(GunshotContrastModel.RouteGain("Gunshots", true, true, 18));
            float[] gun = { 1.2f, -1.2f, 0f, 0f };
            float[] original = (float[])gun.Clone();
            callback(gun, 2);
            Assert.That(gun, Is.EqualTo(original));
            filter.SetGain(0.5f);
            typeof(GunshotContrastFilter).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(filter, null);
            callback(gun, 2);
            Assert.That(gun, Is.EqualTo(original));
            Assert.That(filter.CallbackCount, Is.EqualTo(1));
            filter.SetGain(GunshotContrastModel.RouteGain("Ambient", true, true, 0));
            callback(gun, 2);
            Assert.That(gun, Is.EqualTo(original));
        }

        [Test]
        public void F12DefaultAndLiveChangesDoNotTouchOtherTuning()
        {
            var config = new ModConfig(new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false });
            TuningSnapshot before = config.GetTuning();
            Assert.That(config.GunshotContrastDb.Value, Is.EqualTo(8));
            config.GunshotContrastDb.Value = 100;
            Assert.That(config.GunshotContrastDb.Value, Is.EqualTo(18));
            config.GunshotContrastDb.Value = 0;
            Assert.That(GunshotContrastModel.RouteGain("Environment", true, true, config.GunshotContrastDb.Value), Is.EqualTo(1));
            Assert.That(config.GetTuning().PitchedLayerGainDb, Is.EqualTo(before.PitchedLayerGainDb));
            Assert.That(config.GetTuning().LowEndNormalizationPercent, Is.EqualTo(before.LowEndNormalizationPercent));
            Assert.That(config.GetTuning().MasterSeverityScale, Is.EqualTo(before.MasterSeverityScale));
        }

        /// <summary>
        /// Teardown at the end of a raid undoes two things that outlive it: the
        /// attenuation written into the mixer, and the group every tracked source
        /// was routed away from. Unity may already have taken the mixer or a
        /// source apart by then, so each step has to be able to fail on its own
        /// without skipping the others. It did not: one exception in the
        /// neutralize left the whole game attenuated and every source still
        /// routed through the contrast input for the rest of the session.
        /// </summary>
        [Test]
        public void TeardownGuardsEachStepSoOneFailureCannotSkipTheRest()
        {
            MethodInfo shutdown = typeof(GunshotContrastController).GetMethod(
                "Shutdown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(shutdown, Is.Not.Null);
            Assert.That(shutdown.GetMethodBody().ExceptionHandlingClauses.Count,
                Is.GreaterThanOrEqualTo(2),
                "The mixer neutralize and the per-source detach are each guarded.");

            MethodInfo neutralize = typeof(GunshotContrastMixerRouter).GetMethod(
                "Neutralize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(neutralize, Is.Not.Null);
            Assert.That(neutralize.GetMethodBody().ExceptionHandlingClauses.Count,
                Is.GreaterThanOrEqualTo(1),
                "A route that cannot be written must not stop the rest from being reset.");
        }
    }
}
