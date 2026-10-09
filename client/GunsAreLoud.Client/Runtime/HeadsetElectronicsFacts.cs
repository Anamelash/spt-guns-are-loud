using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// Published electronics data per headset profile, as stated by the maker
    /// (see docs/reference/headphones/research-*.md). A fact bound to a field
    /// wins over every class value and template modulation. Values in dB(A),
    /// critical levels or modes have no calibrated dBFS meaning and are kept
    /// as notes: shown in inspection, never written to the DSP.
    /// </summary>
    internal static class HeadsetElectronicsFacts
    {
        private const string SordinSheet =
            "https://cdn.shopify.com/s/files/1/0936/4842/1246/files/Sordin_Supreme_Pro-X_Aug2024.pdf?v=1761638864";
        private const string GsshCard = "https://armytur.ru/ratnik/garnitura-6m2";
        private const string RazorPage = "https://www.walkersgameear.com/razor-x-trm-digital-muffs/";
        private const string SportTacSheet =
            "https://multimedia.3m.com/mws/media/940033O/fp3584-sporttac-pdf.pdf?fn=FP3584_SportTac.pdf";
        private const string EarmorManual =
            "https://www.earmor.com/wp-content/uploads/2024/01/M32-Plus_EN_Usermanual_WebV1.pdf";
        private const string LiberatorPage = "https://safariland.com/products/liberator-hp-hearing-protection-lib_hp";

        private static readonly Dictionary<string, HeadsetElectronicsFact[]> Facts =
            new Dictionary<string, HeadsetElectronicsFact[]>(StringComparer.Ordinal)
            {
                ["sordin-pro-x-foam-family"] = new[]
                {
                    Value(HeadsetElectronicsField.MicHighpass, 100f, "microphone range 100 Hz–10 kHz", SordinSheet),
                    Value(HeadsetElectronicsField.MicLowpass, 10000f, "microphone range 100 Hz–10 kHz", SordinSheet),
                    Note("sound limit / compression 82 dB(A)", SordinSheet)
                },
                ["gssh-proposed"] = new[]
                {
                    Value(HeadsetElectronicsField.MicHighpass, 300f, "working range not narrower than 300–7000 Hz", GsshCard),
                    Value(HeadsetElectronicsField.MicLowpass, 7000f, "working range not narrower than 300–7000 Hz", GsshCard),
                    Note("external signal limiting threshold 115 dB", GsshCard)
                },
                ["razor-digital-bt-family-reference"] = new[]
                {
                    Value(HeadsetElectronicsField.Attack, 0.020f, "sound activated compression 0.02 s", RazorPage)
                },
                // The research notes attribute these critical levels to SportTac, not ComTac II.
                ["sporttac-family-reference"] = new[]
                {
                    Note("critical levels H 113 / M 104 / L 91 dB(A)", SportTacSheet)
                },
                ["m32-proposed"] = new[]
                {
                    Note("activation level 82 dB (M32 Plus manual)", EarmorManual)
                },
                ["liberator-proposed"] = new[]
                {
                    Note("Move mode adds 4–9 dB of protection", LiberatorPage)
                }
            };

        internal static HeadsetElectronicsFact[] For(string profileId) =>
            profileId != null && Facts.TryGetValue(profileId, out HeadsetElectronicsFact[] facts)
                ? (HeadsetElectronicsFact[])facts.Clone()
                : Array.Empty<HeadsetElectronicsFact>();

        internal static IEnumerable<KeyValuePair<string, HeadsetElectronicsFact[]>> All => Facts;

        private static HeadsetElectronicsFact Value(HeadsetElectronicsField field, float value, string note, string source) =>
            new HeadsetElectronicsFact(field, value, note, HeadsetEvidence.ManufacturerClaim, source);

        private static HeadsetElectronicsFact Note(string note, string source) =>
            new HeadsetElectronicsFact(null, 0f, note, HeadsetEvidence.ManufacturerClaim, source);
    }
}
