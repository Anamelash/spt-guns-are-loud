using System;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;
namespace GunsAreLoud.Tests
{
    internal sealed class HeadphoneReferenceTests
    {
        [TestCase("66b5f693acff495a294927e3", 13.6f, 41.4f, HeadsetEvidence.Measured)]
        [TestCase("66b5f6985891c84aab75ca76", 11.6f, 38.3f, HeadsetEvidence.Measured)]
        [TestCase("628e4e576d783146b124c64d", 28.6f, 32f, HeadsetEvidence.FamilySurrogate)]
        [TestCase("68bf405779c8186398099017", 32.8f, 37.9f, HeadsetEvidence.FamilySurrogate)]
        [TestCase("5e4d34ca86f774264f758330", 17.1f, 38.5f, HeadsetEvidence.FamilySurrogate)]
        [TestCase("5c165d832e2216398b5a7e36", 12.1f, 36.4f, HeadsetEvidence.FamilySurrogate)]
        public void SelectedReferenceReplacesPrototype(string id, float low, float high, HeadsetEvidence evidence)
        {
            Assert.That(HeadsetProfileRegistry.TryGet(id, out var p), Is.True);
            Assert.That(p.Passive.MeanAttenuationAt(0), Is.EqualTo(low));
            Assert.That(p.Passive.MeanAttenuationAt(p.Passive.BandCount - 1), Is.EqualTo(high));
            Assert.That(p.Passive.CurveEvidence, Is.EqualTo(evidence));
            Assert.That(p.Electronics.DynamicsEvidence, Is.EqualTo(HeadsetEvidence.Proposed));
            Assert.That(p.Passive.Source, Does.StartWith("https://"));
            for (int i = 0; i < p.TemplateIdCount; i++)
            {
                HeadsetProfileRegistry.TryGet(p.TemplateIdAt(i), out var alias);
                Assert.That(alias, Is.SameAs(p));
            }
        }
        [Test]
        public void MissingDeviationIsUnknownAndApvDoesNotReplaceMean()
        {
            HeadsetProfileRegistry.TryGet("66b5f693acff495a294927e3", out var v);
            Assert.That(float.IsNaN(v.Passive.StandardDeviationAt(0)), Is.True);
            HeadsetProfileRegistry.TryGet("628e4e576d783146b124c64d", out var iv);
            Assert.That(iv.Passive.AssumedProtectionAt(0), Is.EqualTo(23.3f));
            Assert.That(iv.Passive.MeanAttenuationAt(0), Is.EqualTo(28.6f));
        }
        [Test]
        public void InvalidSourceCurveCannotReachDsp()
        {
            Assert.Throws<ArgumentException>(() => new HeadsetPassiveProfile(new[] { 1000f, 125f },
                new[] { 20f, 15f }, null, "", "", "", HeadsetEvidence.Measured));
            Assert.Throws<ArgumentException>(() => new HeadsetPassiveProfile(new[] { 125f },
                new[] { float.NaN }, null, "", "", "", HeadsetEvidence.Measured));
        }
    }
}

