using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GunsAreLoud.Client.Runtime
{
    // Stable identities for the display rows; DSP curves remain unchanged.
    internal enum HeadphoneInspectionId
    {
        Gain = -1, Release = -2, Attack = -3, MicrophoneBand = -4, Noise = -5, Colour = -6,
        Low = 1, Mid = 2, High = 3
    }

    internal sealed class HeadphoneInspectionValue
    {
        internal readonly HeadphoneInspectionId Id;
        internal readonly string Name, Text;
        internal readonly float Value;
        internal HeadphoneInspectionValue(HeadphoneInspectionId id, string name, float value, string unit, bool approximate, bool russian)
            : this(id, name, value, Number(value, russian) + " " + unit, approximate) { }

        internal HeadphoneInspectionValue(HeadphoneInspectionId id, string name, float value, string text, bool approximate)
        {
            Id = id; Name = name; Value = value;
            Text = text + (approximate ? "*" : "");
        }

        internal static string Number(float value, bool russian) =>
            value.ToString("0.##", CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US"));
    }

    internal static class HeadphoneInspectionValues
    {
        // Route status can be empty before a headset is equipped. Never parse it as MongoID.
        internal static bool MatchesEquippedTemplate(string routeTemplateId, EFT.MongoID inspectedTemplateId) =>
            !string.IsNullOrEmpty(routeTemplateId) &&
            string.Equals(routeTemplateId, inspectedTemplateId.ToString(), StringComparison.Ordinal);

        internal static List<HeadphoneInspectionValue> Build(HeadsetProfile profile, float vanillaReleaseMs, float vanillaGainDb, bool russian)
        {
            var rows = new List<HeadphoneInspectionValue>();
            string db = russian ? "дБ" : "dB", ms = russian ? "мс" : "ms";
            if (profile != null)
            {
                var passive = profile.Passive;
                for (int band = 0; band < 3; band++)
                {
                    float sum = 0;
                    int count = 0;
                    for (int i = 0; i < passive.BandCount; i++)
                    {
                        float frequency = passive.FrequencyAt(i);
                        int group = frequency < 500 ? 0 : frequency < 2000 ? 1 : 2;
                        if (frequency < 63 || frequency > 8000 || group != band) continue;
                        sum += passive.MeanAttenuationAt(i);
                        count++;
                    }
                    if (count == 0) continue;
                    string[] names = russian
                        ? new[] { "Низкие (63–500 Гц)", "Средние (500 Гц–2 кГц)", "Высокие (2–8 кГц)" }
                        : new[] { "Low (63–500 Hz)", "Mid (500 Hz–2 kHz)", "High (2–8 kHz)" };
                    rows.Add(new HeadphoneInspectionValue((HeadphoneInspectionId)(band + 1), names[band],
                        sum / count, db, Approximate(passive.CurveEvidence), russian));
                }
            }
            HeadsetElectronicsProfile electronics = profile?.Electronics;
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Release,
                (russian ? "Восстановление компрессора" : "Compressor release"),
                electronics == null ? vanillaReleaseMs : electronics.ReleaseSeconds * 1000f, ms,
                electronics != null && Approximate(electronics.EvidenceOf(HeadsetElectronicsField.Release)), russian));
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Gain,
                (russian ? "Усиление компрессора" : "Compressor gain"),
                electronics == null ? vanillaGainDb : electronics.QuietGainDb, db,
                electronics != null && Approximate(electronics.EvidenceOf(HeadsetElectronicsField.QuietGain)), russian));
            if (electronics == null) return rows;

            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Attack,
                russian ? "Атака компрессора" : "Compressor attack",
                electronics.AttackSeconds * 1000f, ms,
                Approximate(electronics.EvidenceOf(HeadsetElectronicsField.Attack)), russian));
            string hz = russian ? "Гц" : "Hz";
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.MicrophoneBand,
                russian ? "Полоса микрофона" : "Microphone band",
                electronics.MicLowpassHz,
                HeadphoneInspectionValue.Number(electronics.MicHighpassHz, russian) + "–" +
                HeadphoneInspectionValue.Number(electronics.MicLowpassHz, russian) + " " + hz,
                Approximate(electronics.EvidenceOf(HeadsetElectronicsField.MicHighpass)) ||
                Approximate(electronics.EvidenceOf(HeadsetElectronicsField.MicLowpass))));
            bool noiseOff = electronics.NoiseDbFs <= HeadsetElectronicsCharacter.NoiseOffDbFs;
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Noise,
                russian ? "Шум электроники" : "Electronics noise",
                electronics.NoiseDbFs,
                noiseOff ? (russian ? "нет" : "off") : HeadphoneInspectionValue.Number(electronics.NoiseDbFs, russian) + " " + db,
                !noiseOff && Approximate(electronics.EvidenceOf(HeadsetElectronicsField.Noise))));
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Colour,
                russian ? "Окраска" : "Colouring",
                electronics.PresenceDb, "+" + HeadphoneInspectionValue.Number(electronics.PresenceDb, russian) + " " + db,
                Approximate(electronics.EvidenceOf(HeadsetElectronicsField.Presence))));
            return rows;
        }

        /// <summary>
        /// Published electronics facts for the tooltip of the electronics rows,
        /// including those kept only as notes; empty when there are none.
        /// </summary>
        internal static string ElectronicsNotes(HeadsetElectronicsProfile electronics, bool russian)
        {
            if (electronics == null || electronics.FactCount == 0) return "";
            var text = new StringBuilder(russian ? "Данные производителя: " : "Manufacturer data: ");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < electronics.FactCount; i++)
            {
                string note = electronics.FactAt(i).Note;
                if (note.Length == 0 || !seen.Add(note)) continue;
                if (seen.Count > 1) text.Append("; ");
                text.Append(note);
            }
            text.Append(russian ? ". Звёздочка — оценка, а не данные изделия." : ". A star marks an estimate, not device data.");
            return text.ToString();
        }

        internal static bool Approximate(HeadsetEvidence evidence) =>
            evidence == HeadsetEvidence.FamilySurrogate || evidence == HeadsetEvidence.Proposed;
    }
}
