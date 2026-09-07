using System;
using System.Reflection;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;
namespace GunsAreLoud.Tests
{
    public sealed class GrenadeRouteAndMixerStateTests
    {
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
