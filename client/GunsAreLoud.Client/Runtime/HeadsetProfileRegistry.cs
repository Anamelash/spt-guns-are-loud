using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Runtime
{
    internal static partial class HeadsetProfileRegistry
    {
        private static readonly Dictionary<string, HeadsetProfile> Profiles = Build();
        private static readonly HeadsetProfile[] UniqueProfiles = BuildUniqueProfiles();

        internal static int ProfileCount => UniqueProfiles.Length;
        internal static HeadsetProfile ProfileAt(int index) => UniqueProfiles[index];
        internal static bool TryGet(string templateId, out HeadsetProfile profile)
        {
            if (string.IsNullOrWhiteSpace(templateId)) { profile = null; return false; }
            return Profiles.TryGetValue(templateId, out profile);
        }

        private static Dictionary<string, HeadsetProfile> Build()
        {
            var result = new Dictionary<string, HeadsetProfile>(StringComparer.OrdinalIgnoreCase);
            Add(result, Sordin());
            Add(result, ComTacII());
            // Explicit temporary AMP -> RAC fallback; not a claim of equivalent hardware.
            // Shared profile also reuses the existing fit/cache and keeps approximation stars.
            Add(result, ProposedMuff("rac-proposed", "Ops-Core FAST RAC", new[] {
                "5a16b9fffcdbcb0176308b34",
                "252d9d1d2552909d0a76033c", // Epic's AMP Black
                "5b6e75274d865619855c447a", // Epic's AMP Tan
                "7e6c09774462ad3ef5025f08", // Epic's AMP Tan Camo
                "cd54f04eb7f0410772acbf35"  // Epic's AMP Black Camo
            }));
            Add(result, ProposedMuff("gssh-proposed", "GSSh-01 family", new[] { "5b432b965acfc47a8774094e" }));
            Add(result, ProposedMuff("tactical-sport-proposed", "Peltor Tactical Sport", new[] { "5c165d832e2216398b5a7e36" }));
            Add(result, ProposedMuff("razor-proposed", "Walker's Razor Digital", new[] { "5e4d34ca86f774264f758330", "69d3c156cdeff2f448010e2e" }));
            Add(result, ProposedMuff("xcel-proposed", "Walker's XCEL 500BT", new[] { "5f60cd6cf2bcbb675b00dac6" }));
            Add(result, ProposedMuff("m32-proposed", "Earmor M32", new[] { "6033fa48ffd42c541047f728", "693bb59250fafa102607aeb7" }));
            Add(result, ProposedInsert("comtac-iv-proposed", "Peltor ComTac IV Hybrid", new[] { "628e4e576d783146b124c64d" }));
            Add(result, ProposedMuff("liberator-proposed", "Safariland Liberator HP 2.0", new[] { "66b5f68de98be930d701c00e", "69c139c022093da1c50a8808", "69c14efe66c213ea750e1960", "69c14fa8b33aad3beb036878" }));
            Add(result, ProposedMuff("comtac-v-proposed", "Peltor ComTac V", new[] { "66b5f693acff495a294927e3", "66b5f69ea7f72d197e70bcdb" }));
            Add(result, ProposedMuff("comtac-vi-proposed", "Peltor ComTac VI", new[] { "66b5f6985891c84aab75ca76", "66b5f6a28ca68c6461709ed8", "69c1632ca078dbb51e0e31b5", "69c264c00f660b3f0d058fcf", "69c26593ea474c30ad069f8f", "69c163b17c7040819b086502" }));
            Add(result, ProposedInsert("tep-300-proposed", "Peltor TEP-300", new[] { "68bf405779c8186398099017" }));
            Add(result, Cens());
            ApplyReferenceProfiles(result);
            return result;
        }

        private static HeadsetProfile[] BuildUniqueProfiles()
        {
            var unique = new List<HeadsetProfile>();
            foreach (HeadsetProfile profile in Profiles.Values)
                if (!unique.Contains(profile)) unique.Add(profile);
            return unique.ToArray();
        }

        private static void Add(Dictionary<string, HeadsetProfile> target, HeadsetProfile profile)
        { for (int i = 0; i < profile.TemplateIdCount; i++) target[profile.TemplateIdAt(i)] = profile; }

        private static HeadsetProfile Sordin()
        {
            var passive = new HeadsetPassiveProfile(
                new[] { 125f, 250f, 500f, 1000f, 2000f, 3150f, 4000f, 6300f, 8000f },
                new[] { 15.3f, 19.3f, 24.4f, 29.1f, 28.6f, 31.1f, 34f, 36.1f, 34.7f },
                new[] { 3f, 2.1f, 2.8f, 2.8f, 2.4f, 3.3f, 3.2f, 3.3f, 2.9f },
                "ANSI", "https://psaafrica.co.za/wp-content/uploads/2025/03/Sordin-Supreme-Pro-X-TDS.pdf", "3", HeadsetEvidence.FamilySurrogate);
            return Create("sordin-pro-x-foam-family", new[] { "5aa2ba71e5b5b000137b758f" },
                "Sordin Supreme Pro-X", "modern family surrogate", "headband", "foam", passive);
        }

        private static HeadsetProfile ComTacII()
        {
            var passive = new HeadsetPassiveProfile(
                new[] { 125f, 250f, 500f, 1000f, 2000f, 3150f, 4000f, 6300f, 8000f },
                new[] { 14.5f, 17.7f, 26.3f, 31.3f, 29.8f, 36.7f, 35.1f, 37.5f, 35.4f },
                new[] { 3f, 2.9f, 2.8f, 2.6f, 3.2f, 2.7f, 2.5f, 2.8f, 3f },
                "ANSI S3.19-1974", "https://www.comhead.de/media/pdf/b5/5b/9c/00172_Peltor_Comtac_XP_XS_Anleitung_1268754380.pdf",
                "2 table B2", HeadsetEvidence.Measured);
            return Create("comtac-ii-ansi", new[] { "5645bcc04bdc2d363b8b4572" },
                "Peltor ComTac II", "MT15H69FB", "headband", "document configuration", passive);
        }

        private static HeadsetProfile Cens()
        {
            var passive = new HeadsetPassiveProfile(
                new[] { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f },
                new[] { 26.1f, 24.6f, 23.2f, 23.3f, 23.4f, 31.6f, 31.8f, 36.4f },
                new[] { 6.4f, 4.6f, 3.8f, 3.5f, 3.4f, 3.6f, 3.3f, 4.8f },
                "EN 352-2:2002", "https://www.censdigital.com/wp-content/uploads/2019/07/CENSProFlexGuide-UG510-EN-3.00.pdf",
                "18", HeadsetEvidence.FamilySurrogate);
            return Create("cens-proflex-series", new[] { "69413241b1ce1e5fbb09ed0a" },
                "CENS ProFlex", "series surrogate for DX5", "custom in-ear", "custom silicone", passive);
        }

        private static HeadsetProfile ProposedMuff(string id, string family, string[] ids) =>
            Create(id, ids, family, "unverified game revision", "item-specific", "unknown",
                new HeadsetPassiveProfile(new[] { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f },
                    new[] { 15f, 18f, 22f, 25f, 27f, 29f, 29f }, null,
                    "prototype", "headphone signal-chain blueprint", "section 7", HeadsetEvidence.Proposed));

        private static HeadsetProfile ProposedInsert(string id, string family, string[] ids) =>
            Create(id, ids, family, "unverified game revision", "in-ear", "unspecified tip",
                new HeadsetPassiveProfile(new[] { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f },
                    new[] { 18f, 20f, 23f, 25f, 27f, 29f, 29f }, null,
                    "prototype", "headphone signal-chain blueprint", "section 7", HeadsetEvidence.Proposed));

        private static HeadsetProfile Create(string id, string[] ids, string family, string revision,
            string mounting, string cushion, HeadsetPassiveProfile passive)
        {
            var electronics = new HeadsetElectronicsProfile(6f, -24f, 6f, 10f,
                0.0005f, 0.010f, 0.150f, 0.5f, 100f, 10000f, true,
                HeadsetEvidence.Proposed, HeadsetEvidence.Proposed);
            return new HeadsetProfile(id, ids, family, revision, mounting, cushion, passive, electronics,
                new[] { "measured electronics input-output curve", "device attack and release", "complete mic and speaker response" },
                new[] { "shared prototype electronics", "-24 dBFS threshold is an uncalibrated digital prototype and not physical SPL", "minimum-phase reconstruction from magnitude only" });
        }
    }
}
