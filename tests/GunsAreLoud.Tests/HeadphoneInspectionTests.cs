using System.Linq;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    public sealed class HeadphoneInspectionTests
    {
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("5aa2ba71e5b5b000137b758f", true)]
        [TestCase("5645bcc04bdc2d363b8b4572", false)]
        public void InspectionHandlesMissingOrDifferentEquippedHeadset(string routeTemplateId, bool matches)
        {
            var inspected = new EFT.MongoID("5aa2ba71e5b5b000137b758f");
            Assert.That(HeadphoneInspectionValues.MatchesEquippedTemplate(routeTemplateId, inspected), Is.EqualTo(matches));
        }

        [Test]
        public void VanillaHasOnlyReleaseAndGainWithoutRescalingOrApproximation()
        {
            var rows = HeadphoneInspectionValues.Build(null, 150, 6, true);
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows[0].Text, Is.EqualTo("150 мс"));
            Assert.That(rows[1].Text, Is.EqualTo("6 дБ"));
            Assert.That(rows.All(r => !r.Name.Contains("Vanilla") && !r.Name.Contains("Realistic")), Is.True);
        }

        [Test]
        public void EveryProfileExposesThreeSummaryBandsWithStableIdentity()
        {
            for (int p = 0; p < HeadsetProfileRegistry.ProfileCount; p++)
            {
                var profile = HeadsetProfileRegistry.ProfileAt(p);
                var rows = HeadphoneInspectionValues.Build(profile, 999, 999, false);
                Assert.That(rows.Count, Is.EqualTo(5), profile.ProfileId);
                Assert.That(rows.Select(r => r.Id).Distinct().Count(), Is.EqualTo(rows.Count));
                Assert.That(rows.Take(3).Select(r => r.Id), Is.EqualTo(new[] {
                    HeadphoneInspectionId.Low, HeadphoneInspectionId.Mid, HeadphoneInspectionId.High }));
                Assert.That(rows.Single(r => r.Id == HeadphoneInspectionId.Release).Value,
                    Is.EqualTo(profile.Electronics.ReleaseSeconds * 1000).Within(.0001));
                Assert.That(rows.Single(r => r.Id == HeadphoneInspectionId.Gain).Value, Is.EqualTo(profile.Electronics.QuietGainDb));
            }
        }

        [Test]
        public void MeasuredPassiveAndProposedElectronicsHaveIndependentStars()
        {
            HeadsetProfileRegistry.TryGet("5645bcc04bdc2d363b8b4572", out var profile);
            Assert.That(profile, Is.Not.Null);
            var rows = HeadphoneInspectionValues.Build(profile, 0, 0, false);
            Assert.That(rows.Where(r => (int)r.Id > 0).All(r => !r.Text.Contains("*")), Is.True);
            Assert.That(rows.Where(r => (int)r.Id < 0).All(r => r.Text.EndsWith("*")), Is.True);
        }

        [Test]
        public void FamilyDataIsMarkedAndBandsAverageExistingPointsWithoutDoubleCountingBoundaries()
        {
            HeadsetProfileRegistry.TryGet("5aa2ba71e5b5b000137b758f", out var profile);
            var rows = HeadphoneInspectionValues.Build(profile, 0, 0, true);
            Assert.That(rows.All(r => r.Text.EndsWith("*")), Is.True);
            Assert.That(rows[0].Name, Is.EqualTo("Низкие (63–500 Гц)"));
            Assert.That(rows[0].Value, Is.EqualTo((15.3f + 19.3f) / 2).Within(.0001));
            Assert.That(rows[1].Value, Is.EqualTo((24.4f + 29.1f) / 2).Within(.0001));
            Assert.That(rows[2].Value, Is.EqualTo((28.6f + 31.1f + 34f + 36.1f + 34.7f) / 5).Within(.0001));
            Assert.That(rows.All(r => !r.Name.Contains("Realistic")), Is.True);
        }
    }
}
