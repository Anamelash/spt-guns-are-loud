using System;
using System.Collections.Generic;
using System.Globalization;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HeadphoneNativeEqFitTests
    {
        private static IEnumerable<TestCaseData> EveryUniqueProfileAndSupportedRate()
        {
            for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
            {
                HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(i);
                yield return new TestCaseData(i, 44100).SetName($"NativeFit_{profile.ProfileId}_44100");
                yield return new TestCaseData(i, 48000).SetName($"NativeFit_{profile.ProfileId}_48000");
            }
        }

        [TestCaseSource(nameof(EveryUniqueProfileAndSupportedRate))]
        public void EveryUniqueProfileProducesFiniteBoundedNativeControls(int profileIndex, int rate)
        {
            HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(profileIndex);
            HeadphoneNativeEqFit fit = HeadphoneNativeEqFit.Calculate(profile.Passive, rate);

            Assert.That(fit.BandCount, Is.EqualTo(profile.Passive.BandCount));
            Assert.That(fit.BaseVolumeDb, Is.InRange(-80f, 0f));
            Assert.That(fit.MaximumAnchorErrorDb, Is.LessThanOrEqualTo(1.5f));
            float observedMaximum = 0f;
            for (int band = 0; band < fit.BandCount; band++)
            {
                Assert.That(Finite(fit.FrequencyAt(band)), Is.True);
                Assert.That(fit.FrequencyAt(band), Is.GreaterThan(0f));
                Assert.That(Finite(fit.LinearGainAt(band)), Is.True);
                Assert.That(fit.LinearGainAt(band), Is.InRange(0.05f, 3f));
                Assert.That(fit.OctaveRangeAt(band), Is.InRange(0.2f, 5f));
                Assert.That(Finite(fit.AnchorErrorDbAt(band)), Is.True);
                double independentlyRendered = ResponseDb(fit.FrequencyAt(band), fit, rate);
                double expectedError = independentlyRendered + profile.Passive.MeanAttenuationAt(band);
                Assert.That(fit.AnchorErrorDbAt(band), Is.EqualTo(expectedError).Within(0.0005),
                    "reported fit error must match the independently evaluated biquad cascade");
                observedMaximum = Math.Max(observedMaximum, Math.Abs(fit.AnchorErrorDbAt(band)));
            }
            Assert.That(fit.MaximumAnchorErrorDb, Is.EqualTo(observedMaximum).Within(0.0001f));
            TestContext.Progress.WriteLine($"{profile.ProfileId} {rate}Hz max-anchor-error={fit.MaximumAnchorErrorDb:F4}dB");
            if (rate == 48000)
            {
                var bands = new string[fit.BandCount];
                for (int band = 0; band < fit.BandCount; band++)
                    bands[band] = "{\"frequencyHz\":" + fit.FrequencyAt(band).ToString("R", CultureInfo.InvariantCulture) +
                        ",\"linearGain\":" + fit.LinearGainAt(band).ToString("R", CultureInfo.InvariantCulture) +
                        ",\"octaveRange\":" + fit.OctaveRangeAt(band).ToString("R", CultureInfo.InvariantCulture) +
                        ",\"targetAttenuationDb\":" + profile.Passive.MeanAttenuationAt(band).ToString("R", CultureInfo.InvariantCulture) + "}";
                TestContext.Progress.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "FIT_JSON {{\"templateId\":\"{0}\",\"sampleRate\":48000,\"baseVolumeDb\":{1:R},\"bands\":[{2}]}}",
                    profile.ProfileId, fit.BaseVolumeDb, string.Join(",", bands)));
            }
        }

        private static double ResponseDb(double frequency, HeadphoneNativeEqFit fit, int rate)
        {
            double response = fit.BaseVolumeDb;
            for (int band = 0; band < fit.BandCount; band++)
            {
                double center = fit.FrequencyAt(band);
                // Native 2022.3 fixture: control 2 -> +12.0412 dB at center.
                double amplitude = fit.LinearGainAt(band);
                double centerRadians = 2 * Math.PI * center / rate;
                // Native sweep width=2 at 500 Hz gives +7.48497 dB for
                // center=1000, gain=2: the control is inverse Q.
                double q = 1 / fit.OctaveRangeAt(band);
                double alpha = Math.Sin(centerRadians) / (2 * q);
                double radians = 2 * Math.PI * Math.Min(rate * 0.499, frequency) / rate;
                double cosine = Math.Cos(radians), sine = Math.Sin(radians);
                double b0 = 1 + alpha * amplitude;
                double b1 = -2 * Math.Cos(centerRadians);
                double b2 = 1 - alpha * amplitude;
                double a0 = 1 + alpha / amplitude;
                double a1 = b1;
                double a2 = 1 - alpha / amplitude;
                double numeratorReal = b0 + b1 * cosine + b2 * Math.Cos(2 * radians);
                double numeratorImaginary = -b1 * sine - b2 * Math.Sin(2 * radians);
                double denominatorReal = a0 + a1 * cosine + a2 * Math.Cos(2 * radians);
                double denominatorImaginary = -a1 * sine - a2 * Math.Sin(2 * radians);
                response += 10 * Math.Log10(
                    (numeratorReal * numeratorReal + numeratorImaginary * numeratorImaginary) /
                    Math.Max(1e-20, denominatorReal * denominatorReal + denominatorImaginary * denominatorImaginary));
            }
            return response;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
