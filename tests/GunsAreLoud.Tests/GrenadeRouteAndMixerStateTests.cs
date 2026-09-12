using System;
using System.Reflection;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;
namespace GunsAreLoud.Tests
{
    public sealed class GrenadeRouteAndMixerStateTests
    {
        [Test]
        public void SharedGlobalControlsFollowRaidFadeAndSubsequentMenuMute()
        {
            var menu = new ControlStore(); var raid = new ControlStore();
            menu.Values["nvqIkjL"] = -80;
            var bridge = new HeadphoneGlobalControlBridge(menu, raid);
            Assert.That(bridge.Tick(), Is.True);
            Assert.That(raid.Values["nvqIkjL"], Is.EqualTo(-80));
            raid.Values["nvqIkjL"] = -20;
            bridge.Tick();
            Assert.That(menu.Values["nvqIkjL"], Is.EqualTo(-20));
            raid.Values["nvqIkjL"] = 0;
            bridge.Tick();
            Assert.That(menu.Values["nvqIkjL"], Is.Zero);
            menu.Values["nvqIkjL"] = -80;
            bridge.Tick();
            Assert.That(raid.Values["nvqIkjL"], Is.EqualTo(-80));
            Assert.That(raid.Values.ContainsKey("GAL_ElectronicsWet"), Is.False);
            Assert.That(raid.Values.ContainsKey("GunsVolume"), Is.False);
        }

        [TestCase("mposwoH")]
        [TestCase("OnQSOHH")]
        [TestCase("OtWVUHN")]
        [TestCase("JlWLTVH")]
        [TestCase("spoTLkN")]
        public void PersistentMenuControlCannotBeOverwrittenByReplacementSnapshot(string control)
        {
            var menu = new ControlStore(); var replacement = new ControlStore();
            menu.Values[control] = -80f;
            replacement.Values[control] = 0f;
            var bridge = new HeadphoneGlobalControlBridge(menu, replacement);

            Assert.That(bridge.Tick(), Is.True);
            Assert.That(replacement.Values[control], Is.EqualTo(-80f));

            replacement.Values[control] = 0f;
            Assert.That(bridge.Tick(), Is.True);
            Assert.That(menu.Values[control], Is.EqualTo(-80f));
            Assert.That(replacement.Values[control], Is.EqualTo(-80f));

            menu.Values[control] = -15f;
            Assert.That(bridge.Tick(), Is.True);
            Assert.That(replacement.Values[control], Is.EqualTo(-15f));
        }

        [Test]
        public void MusicPreservesEftRaidFadeWithoutLosingMenuVolume()
        {
            var menu = new ControlStore(); var replacement = new ControlStore();
            menu.Values["mposwoH"] = -20f;
            replacement.Values["mposwoH"] = -20f;
            var bridge = new HeadphoneGlobalControlBridge(menu, replacement);

            Assert.That(bridge.Tick(), Is.True);
            replacement.Values["mposwoH"] = -80f;
            Assert.That(bridge.Tick(), Is.True);
            Assert.That(replacement.Values["mposwoH"], Is.EqualTo(-80f));
            Assert.That(menu.Values["mposwoH"], Is.EqualTo(-20f));

            replacement.Values["mposwoH"] = -20f;
            Assert.That(bridge.Tick(), Is.True);
            Assert.That(replacement.Values["mposwoH"], Is.EqualTo(-20f));
            Assert.That(menu.Values["mposwoH"], Is.EqualTo(-20f));
        }

        private sealed class ControlStore : GunsAreLoud.Client.Runtime.IMixerParameterStore
        {
            internal readonly System.Collections.Generic.Dictionary<string,float> Values = new System.Collections.Generic.Dictionary<string,float>();
            public bool TryGet(string key, out float value) { Values.TryGetValue(key, out value); return true; }
            public bool TrySet(string key, float value) { Values[key] = value; return true; }
        }

        [TestCase(0f, 0f, true)]
        [TestCase(-80f, 0f, false)]
        [TestCase(-12f, -12.00001f, true)]
        [TestCase(0f, float.NaN, false)]
        [TestCase(0f, float.PositiveInfinity, false)]
        public void LiveSettingsComparisonRejectsInvalidReadback(float expected, float actual, bool same)
        {
            Assert.That(HeadphoneMixerLiveState.Equivalent(expected, actual), Is.EqualTo(same));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void GrenadeScopeRestoresOuterStateEvenWhenPlaybackThrows(bool previous)
        {
            GrenadeAudioRoute.Emitting = !previous;
            var failure = new InvalidOperationException("playback failed");
            var finalizer = typeof(GrenadePlaybackPatch).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(finalizer.Invoke(null, new object[] { failure, previous }), Is.SameAs(failure));
            Assert.That(GrenadeAudioRoute.Emitting, Is.EqualTo(previous));
            GrenadeAudioRoute.Emitting = false;
        }
        [Test]
        public void GrenadePlaybackHookMatchesCurrentGameAndGunshotsAreNotEnvironment()
        {
            var method = typeof(BetterAudio).GetMethod(nameof(BetterAudio.PlayAtPointDistant));
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(BetterSource)));
            Assert.That(typeof(BetterSource).GetMethod(nameof(BetterSource.Release)).DeclaringType, Is.EqualTo(typeof(BetterSource)));
            Assert.That(GunshotContrastModel.ShouldAttenuate("Gunshots"), Is.False);
        }
    }
}
