using System.Diagnostics;
using System.Reflection;
using System.Threading;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HeadphoneNativeEqCacheTests
    {
        [Test]
        public void PublicationGateRejectsOutOfToleranceAndNonfiniteFits()
        {
            Assert.That(HeadsetProfileRegistry.TryGet("5645bcc04bdc2d363b8b4572", out HeadsetProfile profile), Is.True);
            HeadphoneNativeEqFit valid = HeadphoneNativeEqFit.Calculate(profile.Passive, 48000);
            Assert.That(HeadphoneNativeEqCache.IsPublishable(profile, 48000, valid), Is.True);

            HeadphoneNativeEqFit excessiveError = ConstructFit(profile.Passive, 2f, float.NaN);
            Assert.That(HeadphoneNativeEqCache.IsPublishable(profile, 48000, excessiveError), Is.False);
            HeadphoneNativeEqFit nonfiniteControl = ConstructFit(profile.Passive, 0.1f, float.PositiveInfinity);
            Assert.That(HeadphoneNativeEqCache.IsPublishable(profile, 48000, nonfiniteControl), Is.False);
            Assert.That(HeadphoneNativeEqCache.IsPublishable(profile, 48000, null), Is.False);
        }

        [Test]
        public void PreloadIsIdempotentAndPublishesEveryUniqueProfileWithoutBlockingLookup()
        {
            const int rate = 32000; // isolates this test from production 44.1/48 kHz cache state
            Assert.That(HeadsetProfileRegistry.TryGet("5645bcc04bdc2d363b8b4572", out HeadsetProfile first), Is.True);

            var lookup = Stopwatch.StartNew();
            bool initiallyReady = HeadphoneNativeEqCache.TryGet(first, rate, out _);
            lookup.Stop();
            Assert.That(initiallyReady, Is.False);
            Assert.That(lookup.ElapsedMilliseconds, Is.LessThan(100), "cache miss must not calculate synchronously");

            HeadphoneNativeEqCache.PreloadAll(rate);
            HeadphoneNativeEqCache.PreloadAll(rate);
            var timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < 15000)
            {
                bool allReady = true;
                for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
                    allReady &= HeadphoneNativeEqCache.TryGet(HeadsetProfileRegistry.ProfileAt(i), rate, out _);
                if (allReady) break;
                Thread.Sleep(10);
            }

            for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
            {
                HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(i);
                Assert.That(HeadphoneNativeEqCache.TryGet(profile, rate, out HeadphoneNativeEqFit fit), Is.True,
                    profile.ProfileId + " was not published by preload");
                Assert.That(fit, Is.Not.Null);
                Assert.That(HeadphoneNativeEqCache.TryGet(profile, rate, out HeadphoneNativeEqFit second), Is.True);
                Assert.That(second, Is.SameAs(fit), "lookup should reuse the immutable fit");
            }
        }

        private static HeadphoneNativeEqFit ConstructFit(HeadsetPassiveProfile passive,
            float maximumError, float firstGainOverride)
        {
            int count = passive.BandCount;
            var frequencies = new float[count];
            var gains = new float[count];
            var widths = new float[count];
            var errors = new float[count];
            for (int i = 0; i < count; i++)
            {
                frequencies[i] = passive.FrequencyAt(i);
                gains[i] = 1f;
                widths[i] = 1f;
            }
            if (!float.IsNaN(firstGainOverride)) gains[0] = firstGainOverride;
            ConstructorInfo constructor = typeof(HeadphoneNativeEqFit).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(float), typeof(float), typeof(float[]), typeof(float[]), typeof(float[]), typeof(float[]) }, null);
            Assert.That(constructor, Is.Not.Null);
            return (HeadphoneNativeEqFit)constructor.Invoke(new object[]
                { -20f, maximumError, frequencies, gains, widths, errors });
        }
    }
}
