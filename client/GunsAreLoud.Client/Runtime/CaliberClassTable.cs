using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>Coarse cartridge class used for the fixed baseline loudness levels.</summary>
    internal enum CaliberClass
    {
        Unknown,
        Pistol,
        Intermediate,
        Rifle,
        Heavy
    }

    /// <summary>
    /// Maps SPT caliber identifiers (with or without the "Caliber" prefix, case-insensitive)
    /// to a <see cref="CaliberClass"/> and the fixed baseline level of that class.
    /// Calibers that are not listed classify as <see cref="CaliberClass.Unknown"/>.
    /// </summary>
    internal static class CaliberClassTable
    {
        private const string Prefix = "Caliber";

        private const float PistolLevel = 0.7f;
        private const float IntermediateLevel = 0.8f;
        private const float RifleLevel = 0.9f;
        private const float HeavyLevel = 1.0f;

        private static readonly Dictionary<string, CaliberClass> Classes = Build();

        /// <summary>Returns the class of a caliber, or Unknown for null, empty or unlisted values.</summary>
        internal static CaliberClass Classify(string caliber)
        {
            if (string.IsNullOrEmpty(caliber))
                return CaliberClass.Unknown;

            string key = caliber;
            if (key.Length > Prefix.Length && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                key = key.Substring(Prefix.Length);

            CaliberClass result;
            return Classes.TryGetValue(key, out result) ? result : CaliberClass.Unknown;
        }

        /// <summary>Returns the baseline level of a class; 0 for Unknown so callers can detect it.</summary>
        internal static float BaselineLevel(CaliberClass c)
        {
            switch (c)
            {
                case CaliberClass.Pistol: return PistolLevel;
                case CaliberClass.Intermediate: return IntermediateLevel;
                case CaliberClass.Rifle: return RifleLevel;
                case CaliberClass.Heavy: return HeavyLevel;
                default: return 0f;
            }
        }

        private static Dictionary<string, CaliberClass> Build()
        {
            var map = new Dictionary<string, CaliberClass>(StringComparer.OrdinalIgnoreCase);

            // Pistol: handgun, PDW and revolver cartridges, the toy gun and the .410 shell
            // (at or below roughly 1 kJ of muzzle energy, except the .50 AE magnum pistol).
            Add(map, CaliberClass.Pistol,
                "20x1mm", "9x18pm", "46x30", "9x19para", "762x25tt", "9x21", "1143x23acp",
                "410g", "102x22", "11x33r", "57x28", "9x33r", "127x33");

            // Intermediate: intermediate rifle cartridges and their subsonic or special relatives
            // (roughly 0.7 to 2.7 kJ).
            Add(map, CaliberClass.Intermediate,
                "9x39", "545x39", "556x45nato", "58x42", "792x33", "762x39", "762x35",
                "366tkm", "127x55");

            // Rifle: full-power rifle and machine-gun cartridges plus the shotgun shells
            // (shells are grouped with rifles by design; 12 ga and 20 ga energy sits between 7.62x39 and 7.62x51).
            Add(map, CaliberClass.Rifle,
                "12g", "20g", "23x75", "762x51", "65x52", "784x49", "762x54r", "68x51",
                "792x57", "762x63", "93x64", "762x67b", "86x70", "86x63");

            // Heavy: anti-materiel rifle rounds, the large-bore dangerous-game round, and grenade-type
            // or flare rounds (the latter classified by launcher size, not by muzzle energy).
            Add(map, CaliberClass.Heavy,
                "26x75", "40x46", "40mmru", "30x29", "1036x77", "178x89", "127x108",
                "127x99", "25x59");

            return map;
        }

        private static void Add(Dictionary<string, CaliberClass> map, CaliberClass cls, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                map[keys[i]] = cls;
        }
    }
}
