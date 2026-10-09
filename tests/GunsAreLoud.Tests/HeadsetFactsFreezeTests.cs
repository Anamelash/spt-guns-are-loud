using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    // Guards published headset data against the electronics character work.
    // A passive change needs a deliberate snapshot edit whose commit message
    // names the source; nothing derived from classes or templates may move it.
    [TestFixture]
    public sealed class HeadsetFactsFreezeTests
    {
        private const string PassiveSnapshot = "headset-passive-freeze.json";

        [Test]
        public void PassiveProfilesMatchTheFrozenSnapshotExactly()
        {
            string path = TestFixtureFiles.PathOf(PassiveSnapshot);
            string current = TestFixtureFiles.Normalize(DescribePassiveProfiles());
            if (TestFixtureFiles.UpdateRequested)
            {
                File.WriteAllText(path, current, new UTF8Encoding(false));
                Assert.Inconclusive("Passive snapshot rewritten: " + path);
            }
            Assert.That(File.Exists(path), Is.True, "missing frozen passive snapshot " + path);
            string frozen = TestFixtureFiles.Normalize(File.ReadAllText(path));
            Assert.That(current, Is.EqualTo(frozen),
                "a passive curve, uncertainty, evidence or source changed; update the snapshot only with a cited source");
        }

        // Every electronics fact bound to a DSP field, as published. A new fact
        // needs a row here; a changed value fails until the row is corrected
        // with its source.
        private static readonly (string TemplateId, HeadsetElectronicsField Field, float Value, string Source)[] FrozenFacts =
        {
            ("5aa2ba71e5b5b000137b758f", HeadsetElectronicsField.MicHighpass, 100f, "Sordin_Supreme_Pro-X_Aug2024.pdf"),
            ("5aa2ba71e5b5b000137b758f", HeadsetElectronicsField.MicLowpass, 10000f, "Sordin_Supreme_Pro-X_Aug2024.pdf"),
            ("5b432b965acfc47a8774094e", HeadsetElectronicsField.MicHighpass, 300f, "armytur.ru/ratnik/garnitura-6m2"),
            ("5b432b965acfc47a8774094e", HeadsetElectronicsField.MicLowpass, 7000f, "armytur.ru/ratnik/garnitura-6m2"),
            ("5e4d34ca86f774264f758330", HeadsetElectronicsField.Attack, 0.020f, "walkersgameear.com/razor-x-trm-digital-muffs"),
            ("69d3c156cdeff2f448010e2e", HeadsetElectronicsField.Attack, 0.020f, "walkersgameear.com/razor-x-trm-digital-muffs")
        };

        private static IEnumerable<HeadsetTemplateElectronics> ExtremeTemplates()
        {
            yield return default;
            float[] edge = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1000f, 0f, 1000f };
            foreach (float value in edge)
            {
                yield return new HeadsetTemplateElectronics(value, 35f, 215f, 0.15f, 200f);
                yield return new HeadsetTemplateElectronics(5f, value, 215f, 0.15f, 200f);
                yield return new HeadsetTemplateElectronics(5f, 35f, value, 0.15f, 200f);
                yield return new HeadsetTemplateElectronics(5f, 35f, 215f, value, 200f);
                yield return new HeadsetTemplateElectronics(5f, 35f, 215f, 0.15f, value);
                yield return new HeadsetTemplateElectronics(value, value, value, value, value);
            }
            // Every real SPT template (inventory.json, 2026-09-07) spans these.
            foreach (float gain in new[] { 3f, 5f, 8f })
                yield return new HeadsetTemplateElectronics(gain, 15f, 120f, 0.05f, 180f);
            yield return new HeadsetTemplateElectronics(8f, 40f, 320f, 0.18f, 280f);
        }

        [Test]
        public void PublishedElectronicsFactsSurviveEveryTemplate()
        {
            foreach (var fact in FrozenFacts)
            {
                Assert.That(HeadsetProfileRegistry.TryGet(fact.TemplateId, out HeadsetProfile profile), Is.True, fact.TemplateId);
                HeadsetEvidence evidence = profile.Electronics.EvidenceOf(fact.Field);
                Assert.That(evidence == HeadsetEvidence.Measured || evidence == HeadsetEvidence.ManufacturerClaim,
                    Is.True, $"{profile.ProfileId} {fact.Field} evidence {evidence}");
                bool sourced = false;
                for (int i = 0; i < profile.Electronics.FactCount; i++)
                    sourced |= profile.Electronics.FactAt(i).Field == fact.Field &&
                        profile.Electronics.FactAt(i).Source.Contains(fact.Source);
                Assert.That(sourced, Is.True, $"{profile.ProfileId} {fact.Field} source");
                foreach (HeadsetTemplateElectronics template in ExtremeTemplates())
                    foreach (float character in new[] { 0f, 1f, 2f })
                    {
                        HeadsetElectronicsProfile worn = HeadsetElectronicsComposer.Compose(profile, template)
                            .Electronics.WithCharacterScale(character);
                        Assert.That(FieldValue(worn, fact.Field), Is.EqualTo(fact.Value),
                            $"{profile.ProfileId} {fact.Field} with {template} at {character}");
                    }
            }

            // No bound fact may exist without a frozen row.
            int bound = 0;
            foreach (var entry in HeadsetElectronicsFacts.All)
                foreach (HeadsetElectronicsFact fact in entry.Value)
                    if (!fact.IsNote) bound++;
            Assert.That(bound, Is.EqualTo(5), "Sordin band (2), GSSh-01 band (2), Walker's Razor attack (1)");
        }

        [Test]
        public void TemplateModulationStaysInsideClassAndDspBounds()
        {
            for (int p = 0; p < HeadsetProfileRegistry.ProfileCount; p++)
            {
                HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(p);
                HeadsetElectronicsProfile device = profile.Electronics;
                foreach (HeadsetTemplateElectronics template in ExtremeTemplates())
                {
                    HeadsetElectronicsProfile worn = HeadsetElectronicsComposer.Compose(profile, template).Electronics;
                    string label = profile.ProfileId + " " + template;
                    foreach (float value in new[] { worn.QuietGainDb, worn.MicHighpassHz, worn.MicLowpassHz,
                        worn.AttackSeconds, worn.ReleaseSeconds, worn.PresenceDb, worn.LowShelfDb,
                        worn.NoiseDbFs, worn.Saturation, worn.DelaySeconds })
                        Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False, label);
                    Assert.That(Math.Abs(worn.QuietGainDb - device.QuietGainDb), Is.LessThanOrEqualTo(2f + 1e-5f), label);
                    Assert.That(worn.ReleaseSeconds, Is.InRange(0.120f - 1e-6f, 0.320f + 1e-6f), label);
                    Assert.That(worn.AttackSeconds, Is.InRange(0.0005f - 1e-7f, 0.040f + 1e-6f), label);
                    Assert.That(worn.Saturation, Is.InRange(0f, Math.Max(0.6f, device.Saturation)), label);
                    Assert.That(worn.MicHighpassHz, Is.InRange(100f, 300f), label);
                    Assert.That(worn.MicLowpassHz, Is.InRange(7000f, 10000f), label);
                    Assert.That(worn.MicLowpassHz, Is.GreaterThan(worn.MicHighpassHz), label);
                    // Fields the template never moves.
                    Assert.That(worn.PresenceDb, Is.EqualTo(device.PresenceDb), label);
                    Assert.That(worn.LowShelfDb, Is.EqualTo(device.LowShelfDb), label);
                    Assert.That(worn.NoiseDbFs, Is.EqualTo(device.NoiseDbFs), label);
                    Assert.That(worn.DelaySeconds, Is.EqualTo(device.DelaySeconds), label);
                    Assert.That(worn.OutputCeiling, Is.EqualTo(device.OutputCeiling), label);
                    Assert.That(worn.ThresholdDbFs, Is.EqualTo(device.ThresholdDbFs), label);
                    // Native parameter ranges.
                    Assert.That(worn.QuietGainDb, Is.InRange(-24f, 24f), label);
                    Assert.That(worn.PresenceDb, Is.InRange(0f, 12f), label);
                    Assert.That(worn.LowShelfDb, Is.InRange(-12f, 0f), label);
                    Assert.That(worn.NoiseDbFs, Is.InRange(-120f, -30f), label);
                    Assert.That(worn.DelaySeconds, Is.InRange(0f, 0.008f), label);
                }
            }
        }

        [Test]
        public void ZeroCharacterWritesTheLegacyValuesAndNeutralColouring()
        {
            var template = new EFT.InventoryLogic.HeadphonesTemplate
            {
                GunsCompressorSendLevel = -7f, CompressorGain = 8f, CompressorAttack = 35f,
                CompressorRelease = 160f, Distortion = 0.15f, HighpassFreq = 200
            };
            HeadsetSendLevels sends = HeadsetSendLevels.From(template);
            for (int p = 0; p < HeadsetProfileRegistry.ProfileCount; p++)
            {
                HeadsetProfile profile = HeadsetProfileRegistry.ProfileAt(p);
                var store = new RecordingStore();
                var route = new TransactionalMixerHeadphoneRoute(store, 48000, new FitProvider());
                var controller = new HeadphoneRouteController(route);
                Assert.That(controller.Apply(Client.Configuration.HeadphoneMode.Realistic, profile.TemplateIdAt(0),
                    false, sends, 0f), Is.True, profile.ProfileId);

                HeadsetElectronicsProfile e = HeadsetElectronicsComposer.Compose(profile, sends.Electronics).Electronics;
                // The ABI 1 mapping, unchanged: profile values in mixer units.
                var legacy = new Dictionary<string, float>
                {
                    ["GAL_ElectronicsMicHP"] = e.MicHighpassHz, ["GAL_ElectronicsMicLP"] = e.MicLowpassHz,
                    ["GAL_ElectronicsQuietGain"] = e.QuietGainDb, ["GAL_ElectronicsThreshold"] = e.ThresholdDbFs,
                    ["GAL_ElectronicsRatio"] = e.Ratio, ["GAL_ElectronicsKnee"] = e.KneeDb,
                    ["GAL_ElectronicsAttack"] = e.AttackSeconds * 1000f, ["GAL_ElectronicsHold"] = e.HoldSeconds * 1000f,
                    ["GAL_ElectronicsRelease"] = e.ReleaseSeconds * 1000f, ["GAL_ElectronicsCeiling"] = e.OutputCeiling,
                    ["GAL_ElectronicsWet"] = 1f
                };
                foreach (var item in legacy)
                    Assert.That(store.Values[item.Key], Is.EqualTo(item.Value), profile.ProfileId + " " + item.Key);
                Assert.That(store.Values["GAL_ElectronicsReset"], Is.EqualTo(1f), profile.ProfileId);
                foreach (var parameter in TransactionalMixerHeadphoneRoute.CharacterParameters)
                    Assert.That(store.Values[parameter.Name], Is.EqualTo(parameter.Neutral),
                        profile.ProfileId + " " + parameter.Name);
            }
        }

        private static float FieldValue(HeadsetElectronicsProfile e, HeadsetElectronicsField field)
        {
            switch (field)
            {
                case HeadsetElectronicsField.MicHighpass: return e.MicHighpassHz;
                case HeadsetElectronicsField.MicLowpass: return e.MicLowpassHz;
                case HeadsetElectronicsField.Attack: return e.AttackSeconds;
                case HeadsetElectronicsField.Release: return e.ReleaseSeconds;
                case HeadsetElectronicsField.QuietGain: return e.QuietGainDb;
                case HeadsetElectronicsField.Saturation: return e.Saturation;
                case HeadsetElectronicsField.Presence: return e.PresenceDb;
                case HeadsetElectronicsField.LowShelf: return e.LowShelfDb;
                case HeadsetElectronicsField.Noise: return e.NoiseDbFs;
                default: return e.DelaySeconds;
            }
        }

        private sealed class RecordingStore : IMixerParameterStore
        {
            internal readonly Dictionary<string, float> Values = new Dictionary<string, float>();
            public bool TryGet(string name, out float value)
            {
                if (!Values.TryGetValue(name, out value)) Values[name] = value = -11f;
                return true;
            }
            public bool TrySet(string name, float value) { Values[name] = value; return true; }
        }

        private sealed class FitProvider : Client.Audio.IHeadphoneNativeEqProvider
        {
            public bool TryGet(HeadsetProfile profile, int sampleRate, out Client.Audio.HeadphoneNativeEqFit fit)
            {
                fit = Client.Audio.HeadphoneNativeEqFit.Calculate(profile.Passive, sampleRate);
                return true;
            }
        }

        internal static string DescribePassiveProfiles()
        {
            var profiles = new List<HeadsetProfile>();
            for (int i = 0; i < HeadsetProfileRegistry.ProfileCount; i++)
                profiles.Add(HeadsetProfileRegistry.ProfileAt(i));
            var text = new StringBuilder();
            text.Append("{\n  \"profiles\": [\n");
            HeadsetProfile[] ordered = profiles.OrderBy(p => p.ProfileId, StringComparer.Ordinal).ToArray();
            for (int p = 0; p < ordered.Length; p++)
            {
                HeadsetPassiveProfile passive = ordered[p].Passive;
                text.Append("    {\n");
                Field(text, "profileId", Quote(ordered[p].ProfileId));
                Field(text, "evidence", Quote(passive.CurveEvidence.ToString()));
                Field(text, "standard", Quote(passive.Standard));
                Field(text, "source", Quote(passive.Source));
                Field(text, "page", Quote(passive.Page));
                Field(text, "frequenciesHz", Array(passive.BandCount, passive.FrequencyAt));
                Field(text, "meanDb", Array(passive.BandCount, passive.MeanAttenuationAt));
                Field(text, "sdDb", Array(passive.BandCount, passive.StandardDeviationAt));
                Field(text, "apvDb", Array(passive.BandCount, passive.AssumedProtectionAt), last: true);
                text.Append(p == ordered.Length - 1 ? "    }\n" : "    },\n");
            }
            text.Append("  ]\n}\n");
            return text.ToString();
        }

        private static void Field(StringBuilder text, string name, string value, bool last = false) =>
            text.Append("      \"").Append(name).Append("\": ").Append(value).Append(last ? "\n" : ",\n");

        private static string Array(int count, Func<int, float> value)
        {
            var parts = new string[count];
            for (int i = 0; i < count; i++) parts[i] = Number(value(i));
            return "[" + string.Join(", ", parts) + "]";
        }

        // Round-trip formatting: the snapshot compares exact float values.
        private static string Number(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);

        private static string Quote(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
