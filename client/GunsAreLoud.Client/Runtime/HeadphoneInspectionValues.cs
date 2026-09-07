using System;
using System.Collections.Generic;
using System.Globalization;

namespace GunsAreLoud.Client.Runtime
{
    // Stable identities for the three display bands; DSP curves remain unchanged.
    internal enum HeadphoneInspectionId { Gain = -1, Release = -2, Low = 1, Mid = 2, High = 3 }

    internal sealed class HeadphoneInspectionValue
    {
        internal readonly HeadphoneInspectionId Id;
        internal readonly string Name, Text;
        internal readonly float Value;
        internal HeadphoneInspectionValue(HeadphoneInspectionId id, string name, float value, string unit, bool approximate, bool russian)
        {
            Id = id; Name = name; Value = value;
            Text = value.ToString("0.##", CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US")) + " " + unit + (approximate ? "*" : "");
        }
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
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Release,
                (russian ? "Восстановление компрессора" : "Compressor release"),
                profile == null ? vanillaReleaseMs : profile.Electronics.ReleaseSeconds * 1000f, ms,
                profile != null && Approximate(profile.Electronics.DynamicsEvidence), russian));
            rows.Add(new HeadphoneInspectionValue(HeadphoneInspectionId.Gain,
                (russian ? "Усиление компрессора" : "Compressor gain"),
                profile == null ? vanillaGainDb : profile.Electronics.QuietGainDb, db,
                profile != null && Approximate(profile.Electronics.DynamicsEvidence), russian));
            return rows;
        }

        internal static bool Approximate(HeadsetEvidence evidence) =>
            evidence == HeadsetEvidence.FamilySurrogate || evidence == HeadsetEvidence.Proposed;
    }
}
