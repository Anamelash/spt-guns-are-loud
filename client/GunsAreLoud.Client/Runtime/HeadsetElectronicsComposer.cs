using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Runtime
{
    internal enum HeadsetElectronicsClass
    {
        /// <summary>Over-ear, digital, premium.</summary>
        D1,
        /// <summary>Over-ear, analog, military.</summary>
        A1,
        /// <summary>Over-ear, consumer, analog.</summary>
        C1,
        /// <summary>Over-ear, consumer, digital.</summary>
        C2,
        /// <summary>In-ear, digital.</summary>
        I1,
        /// <summary>Hybrid: in-ear under a cup.</summary>
        H1
    }

    /// <summary>
    /// Derives a headset's electronics where makers publish almost nothing.
    /// Priority per field: a published fact; else the family's own value; else
    /// the construction class, which alone the worn template may move within
    /// fixed bounds. Every derived value is <see cref="HeadsetEvidence.Proposed"/>.
    /// The passive profile is never read or changed here.
    /// </summary>
    internal static class HeadsetElectronicsComposer
    {
        private sealed class ClassSpec
        {
            internal readonly float QuietGainDb, HighpassHz, LowpassHz, PresenceDb, LowShelfDb, NoiseDbFs;
            internal readonly float AttackMs, ReleaseMs, Saturation, DelayMs;
            internal readonly bool Analog;

            internal ClassSpec(float quietGainDb, float highpassHz, float lowpassHz, float presenceDb,
                float lowShelfDb, float noiseDbFs, float attackMs, float releaseMs, float saturation,
                float delayMs, bool analog)
            {
                QuietGainDb = quietGainDb; HighpassHz = highpassHz; LowpassHz = lowpassHz;
                PresenceDb = presenceDb; LowShelfDb = lowShelfDb; NoiseDbFs = noiseDbFs;
                AttackMs = attackMs; ReleaseMs = releaseMs; Saturation = saturation; DelayMs = delayMs;
                Analog = analog;
            }
        }

        // Construction classes, set by ear against reviews ("sharper", "tinny",
        // "hiss at high volume"). Presence and shelf corners are not part of a
        // class: every class uses the neutral 3.2 kHz peak and 200 Hz shelf. Every
        // class limits the band with 12 dB/oct edges; first-order edges and a
        // presence of a dB or two leave the electronics nearly flat above 1 kHz.
        internal const int ClassBandOrder = 2;
        private static readonly Dictionary<HeadsetElectronicsClass, ClassSpec> Classes =
            new Dictionary<HeadsetElectronicsClass, ClassSpec>
            {
                [HeadsetElectronicsClass.D1] = new ClassSpec(4f, 100f, 10000f, 3f, -2f, -72f, 0.5f, 150f, 0.2f, 3f, false),
                [HeadsetElectronicsClass.A1] = new ClassSpec(5f, 150f, 8000f, 6f, -3f, -64f, 1f, 215f, 0.4f, 0f, true),
                [HeadsetElectronicsClass.C1] = new ClassSpec(8f, 180f, 8000f, 8f, -4f, -58f, 20f, 265f, 0.6f, 0f, true),
                [HeadsetElectronicsClass.C2] = new ClassSpec(6f, 150f, 9000f, 6f, -3f, -66f, 0.5f, 200f, 0.4f, 3f, false),
                [HeadsetElectronicsClass.I1] = new ClassSpec(3f, 200f, 8000f, 4f, 0f, -66f, 0.5f, 160f, 0.3f, 4f, false),
                [HeadsetElectronicsClass.H1] = new ClassSpec(4f, 200f, 8000f, 4f, -1f, -66f, 0.5f, 160f, 0.3f, 3f, false)
            };

        private sealed class Family
        {
            internal readonly HeadsetElectronicsClass Class;
            internal readonly Dictionary<HeadsetElectronicsField, float> Values;

            internal Family(HeadsetElectronicsClass type, params (HeadsetElectronicsField Field, float Value)[] values)
            {
                Class = type;
                Values = new Dictionary<HeadsetElectronicsField, float>();
                foreach (var value in values) Values[value.Field] = value.Value;
            }
        }

        // Family assignments, keyed by the registry's final profile ids. A family
        // value is a reasoned choice for that family, so the template does not
        // move it; quiet gain is in dB, noise in dBFS.
        private static readonly Dictionary<string, Family> Families = new Dictionary<string, Family>(StringComparer.Ordinal)
        {
            ["sordin-pro-x-foam-family"] = new Family(HeadsetElectronicsClass.D1),
            ["comtac-ii-ansi"] = new Family(HeadsetElectronicsClass.A1),
            ["comtac-iv-ultrafit-reference"] = new Family(HeadsetElectronicsClass.H1),
            ["comtac-v-foam-reference"] = new Family(HeadsetElectronicsClass.D1),
            ["comtac-vi-foam-reference"] = new Family(HeadsetElectronicsClass.D1),
            ["rac-proposed"] = new Family(HeadsetElectronicsClass.D1,
                (HeadsetElectronicsField.Presence, 2f), (HeadsetElectronicsField.Noise, -74f)),
            ["gssh-proposed"] = new Family(HeadsetElectronicsClass.A1,
                (HeadsetElectronicsField.Noise, -58f), (HeadsetElectronicsField.Saturation, 0.7f)),
            ["sporttac-family-reference"] = new Family(HeadsetElectronicsClass.C1,
                (HeadsetElectronicsField.QuietGain, 3f)),
            ["razor-digital-bt-family-reference"] = new Family(HeadsetElectronicsClass.C1,
                (HeadsetElectronicsField.QuietGain, 9f)),
            ["xcel-proposed"] = new Family(HeadsetElectronicsClass.C2),
            ["m32-proposed"] = new Family(HeadsetElectronicsClass.C1,
                (HeadsetElectronicsField.Noise, -60f)),
            ["liberator-proposed"] = new Family(HeadsetElectronicsClass.D1,
                (HeadsetElectronicsField.QuietGain, 6f), (HeadsetElectronicsField.Noise, -68f)),
            ["tep-300-ultrafit-reference"] = new Family(HeadsetElectronicsClass.I1,
                (HeadsetElectronicsField.Noise, -64f)),
            ["cens-proflex-series"] = new Family(HeadsetElectronicsClass.I1,
                (HeadsetElectronicsField.QuietGain, 4f), (HeadsetElectronicsField.MicLowpass, 9000f),
                (HeadsetElectronicsField.Noise, -68f))
        };

        // SPT's templates carry CompressorGain 3, 5 or 8 (docs/reference/headphones/
        // inventory.json); 5 is the common value, so the template moves gain
        // around it and items of one family differ as their templates do.
        internal const float TemplateGainCentreDb = 5f;
        internal const float TemplateGainSpanDb = 2f;
        internal const float TemplateReleaseMinMs = 120f, TemplateReleaseMaxMs = 320f;
        internal const float TemplateAttackMinMs = 1f, TemplateAttackMaxMs = 40f;
        internal const float TemplateSaturationMax = 0.6f;
        internal const float TemplateHighpassMinHz = 150f, TemplateHighpassMaxHz = 300f;

        // Listening anchor for the derived noise levels: the class table is
        // relative, and this one offset sets the absolute level so the hiss sits
        // just above threshold in a quiet hideout at normal volume.
        internal const float NoiseAnchorDb = -6f;

        internal static bool TryGetClass(string profileId, out HeadsetElectronicsClass type)
        {
            if (profileId != null && Families.TryGetValue(profileId, out Family family))
            { type = family.Class; return true; }
            type = default;
            return false;
        }

        /// <summary>The device without any template: facts, family and class.</summary>
        internal static HeadsetElectronicsProfile Device(string profileId, HeadsetElectronicsProfile prototype) =>
            Compose(profileId, prototype, default);

        internal static HeadsetProfile Compose(HeadsetProfile profile, HeadsetTemplateElectronics template) =>
            profile == null ? null
                : profile.WithElectronics(Compose(profile.ProfileId, profile.Electronics, template));

        /// <summary>
        /// The electronics for one worn item. A profile with no assigned class
        /// keeps <paramref name="prototype"/> unchanged.
        /// </summary>
        internal static HeadsetElectronicsProfile Compose(string profileId, HeadsetElectronicsProfile prototype,
            HeadsetTemplateElectronics template)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            if (profileId == null || !Families.TryGetValue(profileId, out Family family)) return prototype;
            ClassSpec spec = Classes[family.Class];
            HeadsetElectronicsFact[] facts = HeadsetElectronicsFacts.For(profileId);

            float quietGain = Pick(HeadsetElectronicsField.QuietGain, spec.QuietGainDb, family, facts, out bool fromClass);
            if (fromClass && template.HasData && Finite(template.CompressorGainDb))
                quietGain += Clamp((template.CompressorGainDb - TemplateGainCentreDb) / 5f * 2f,
                    -TemplateGainSpanDb, TemplateGainSpanDb);

            float release = Pick(HeadsetElectronicsField.Release, spec.ReleaseMs / 1000f, family, facts, out fromClass);
            if (fromClass && template.HasData && Finite(template.CompressorReleaseMs))
                release = Clamp(template.CompressorReleaseMs, TemplateReleaseMinMs, TemplateReleaseMaxMs) / 1000f;

            float attack = Pick(HeadsetElectronicsField.Attack, spec.AttackMs / 1000f, family, facts, out fromClass);
            // Digital paths keep their converter-speed attack.
            if (fromClass && spec.Analog && template.HasData && Finite(template.CompressorAttackMs))
                attack = Clamp(template.CompressorAttackMs, TemplateAttackMinMs, TemplateAttackMaxMs) / 1000f;

            float saturation = Pick(HeadsetElectronicsField.Saturation, spec.Saturation, family, facts, out fromClass);
            if (fromClass && template.HasData && Finite(template.Distortion))
                saturation = Clamp(template.Distortion * 3f, 0f, TemplateSaturationMax);

            float highpass = Pick(HeadsetElectronicsField.MicHighpass, spec.HighpassHz, family, facts, out fromClass);
            if (fromClass && template.HasData && Finite(template.HighpassHz))
                highpass = Clamp(template.HighpassHz, TemplateHighpassMinHz, TemplateHighpassMaxHz);

            float lowpass = Pick(HeadsetElectronicsField.MicLowpass, spec.LowpassHz, family, facts, out _);
            float presence = Pick(HeadsetElectronicsField.Presence, spec.PresenceDb, family, facts, out _);
            float shelf = Pick(HeadsetElectronicsField.LowShelf, spec.LowShelfDb, family, facts, out _);
            float noise = Pick(HeadsetElectronicsField.Noise, spec.NoiseDbFs, family, facts, out _);
            if (!IsFact(HeadsetElectronicsField.Noise, facts)) noise += NoiseAnchorDb;
            float delayMs = Pick(HeadsetElectronicsField.Delay, spec.DelayMs, family, facts, out _);

            var character = new HeadsetElectronicsCharacter(shelf, HeadsetElectronicsCharacter.NeutralLowShelfHz,
                presence, HeadsetElectronicsCharacter.NeutralPresenceHz, noise, saturation, delayMs / 1000f);
            return new HeadsetElectronicsProfile(quietGain, prototype.ThresholdDbFs, prototype.KneeDb,
                prototype.Ratio, attack, prototype.HoldSeconds, release,
                prototype.OutputCeiling, highpass, Math.Max(highpass, lowpass), prototype.StereoLinked,
                HeadsetEvidence.Proposed, HeadsetEvidence.Proposed, character, HeadsetEvidence.Proposed, facts, ClassBandOrder);
        }

        // Values are in profile units (seconds for attack and release), so a
        // fact reaches the profile without any arithmetic on it.
        private static float Pick(HeadsetElectronicsField field, float classValue, Family family,
            HeadsetElectronicsFact[] facts, out bool fromClass)
        {
            fromClass = false;
            foreach (HeadsetElectronicsFact fact in facts)
                if (fact.Field == field) return fact.Value;
            if (family.Values.TryGetValue(field, out float value)) return value;
            fromClass = true;
            return classValue;
        }

        private static bool IsFact(HeadsetElectronicsField field, HeadsetElectronicsFact[] facts)
        {
            foreach (HeadsetElectronicsFact fact in facts)
                if (fact.Field == field) return true;
            return false;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}
