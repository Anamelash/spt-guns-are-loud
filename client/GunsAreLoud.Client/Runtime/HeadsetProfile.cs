using System;

namespace GunsAreLoud.Client.Runtime
{
    internal enum HeadsetEvidence { Measured, ManufacturerClaim, FamilySurrogate, Proposed }

    internal sealed class HeadsetPassiveProfile
    {
        private readonly float[] _frequenciesHz, _meanAttenuationDb, _standardDeviationDb, _assumedProtectionDb;
        internal readonly string Standard, Source, Page;
        internal readonly HeadsetEvidence CurveEvidence;

        internal HeadsetPassiveProfile(float[] frequenciesHz, float[] meanDb, float[] sdDb,
            string standard, string source, string page, HeadsetEvidence evidence, float[] apvDb = null)
        {
            if (frequenciesHz == null || meanDb == null || frequenciesHz.Length == 0 || frequenciesHz.Length != meanDb.Length)
                throw new ArgumentException("Passive frequencies and means must have matching nonempty arrays");
            if ((sdDb != null && sdDb.Length != meanDb.Length) || (apvDb != null && apvDb.Length != meanDb.Length))
                throw new ArgumentException("Passive uncertainty arrays must match frequency points");
            for (int i = 0; i < frequenciesHz.Length; i++)
                if (float.IsNaN(frequenciesHz[i]) || float.IsInfinity(frequenciesHz[i]) || frequenciesHz[i] <= 0 ||
                    (i > 0 && frequenciesHz[i] <= frequenciesHz[i - 1]) || float.IsNaN(meanDb[i]) ||
                    float.IsInfinity(meanDb[i]) || meanDb[i] < 0)
                    throw new ArgumentException("Invalid passive frequency or mean attenuation");
            _assumedProtectionDb = apvDb == null ? Array.Empty<float>() : (float[])apvDb.Clone();
            _frequenciesHz = (float[])frequenciesHz.Clone();
            _meanAttenuationDb = (float[])meanDb.Clone();
            _standardDeviationDb = sdDb == null ? Array.Empty<float>() : (float[])sdDb.Clone();
            Standard = standard ?? ""; Source = source ?? ""; Page = page ?? "";
            CurveEvidence = evidence;
        }
        internal int BandCount => _frequenciesHz.Length;
        internal float FrequencyAt(int index) => _frequenciesHz[index];
        internal float MeanAttenuationAt(int index) => _meanAttenuationDb[index];
        internal float StandardDeviationAt(int index) => index < _standardDeviationDb.Length ? _standardDeviationDb[index] : float.NaN;
        internal float AssumedProtectionAt(int index) => index < _assumedProtectionDb.Length ? _assumedProtectionDb[index] : float.NaN;
    }

    /// <summary>
    /// The colouring part of the electronic path: voicing, preamplifier noise,
    /// output saturation and converter delay. <see cref="Neutral"/> is the
    /// transparent prototype path; every control there is bypassed in the DSP.
    /// </summary>
    internal readonly struct HeadsetElectronicsCharacter
    {
        internal const float NoiseOffDbFs = -120f;
        internal const float NoiseMaximumDbFs = -30f;
        internal const float NeutralLowShelfHz = 200f;
        internal const float NeutralPresenceHz = 3200f;
        internal const float MaximumDelaySeconds = 0.008f;

        internal static readonly HeadsetElectronicsCharacter Neutral =
            new HeadsetElectronicsCharacter(0f, NeutralLowShelfHz, 0f, NeutralPresenceHz, NoiseOffDbFs, 0f, 0f);

        internal readonly float LowShelfDb, LowShelfHz, PresenceDb, PresenceHz, NoiseDbFs, Saturation, DelaySeconds;

        internal HeadsetElectronicsCharacter(float lowShelfDb, float lowShelfHz, float presenceDb,
            float presenceHz, float noiseDbFs, float saturation, float delaySeconds)
        {
            // Bounded to the native parameter ranges, so the profile and the
            // mixer can never disagree about a value the mixer would clamp.
            LowShelfDb = Clamp(lowShelfDb, -12f, 0f, 0f);
            LowShelfHz = Clamp(lowShelfHz, 60f, 500f, NeutralLowShelfHz);
            PresenceDb = Clamp(presenceDb, 0f, 12f, 0f);
            PresenceHz = Clamp(presenceHz, 1000f, 6000f, NeutralPresenceHz);
            NoiseDbFs = Clamp(noiseDbFs, NoiseOffDbFs, NoiseMaximumDbFs, NoiseOffDbFs);
            Saturation = Clamp(saturation, 0f, 1f, 0f);
            DelaySeconds = Clamp(delaySeconds, 0f, MaximumDelaySeconds, 0f);
        }

        internal bool IsNeutral =>
            LowShelfDb == 0f && PresenceDb == 0f && NoiseDbFs <= NoiseOffDbFs &&
            Saturation == 0f && DelaySeconds == 0f;

        /// <summary>
        /// The Hear-through Character control. Voicing and saturation scale
        /// linearly, noise moves by 20·log10(scale), and 0 switches every
        /// colouring stage off. Delay is the converter's, not a colour: it is
        /// kept as is above zero so dragging the control never restarts the
        /// delay line, and removed only at zero.
        /// </summary>
        internal HeadsetElectronicsCharacter Scaled(float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) return Neutral;
            float noise = NoiseDbFs <= NoiseOffDbFs
                ? NoiseOffDbFs
                : NoiseDbFs + 20f * (float)Math.Log10(scale);
            return new HeadsetElectronicsCharacter(LowShelfDb * scale, LowShelfHz, PresenceDb * scale,
                PresenceHz, noise, Saturation * scale, DelaySeconds);
        }

        private static float Clamp(float value, float min, float max, float fallback) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));
    }

    internal sealed class HeadsetElectronicsProfile
    {
        internal readonly float QuietGainDb, ThresholdDbFs, KneeDb, Ratio;
        internal readonly float AttackSeconds, HoldSeconds, ReleaseSeconds, OutputCeiling;
        internal readonly float MicHighpassHz, MicLowpassHz;
        // Slope of the microphone band edges: 1 is first order (6 dB/oct, the
        // prototype), 2 is a Butterworth pair (12 dB/oct). A device trait.
        internal readonly int MicFilterOrder;
        internal readonly HeadsetElectronicsCharacter Character;
        internal readonly bool StereoLinked;
        internal readonly HeadsetEvidence DynamicsEvidence, ResponseEvidence, NoiseEvidence;

        internal float LowShelfDb => Character.LowShelfDb;
        internal float LowShelfHz => Character.LowShelfHz;
        internal float PresenceDb => Character.PresenceDb;
        internal float PresenceHz => Character.PresenceHz;
        internal float NoiseDbFs => Character.NoiseDbFs;
        internal float Saturation => Character.Saturation;
        internal float DelaySeconds => Character.DelaySeconds;

        // The prototype signature: a transparent path with no colouring.
        internal HeadsetElectronicsProfile(float quietGainDb, float thresholdDbFs, float kneeDb,
            float ratio, float attackSeconds, float holdSeconds, float releaseSeconds,
            float outputCeiling, float micHighpassHz, float micLowpassHz, bool stereoLinked,
            HeadsetEvidence dynamicsEvidence, HeadsetEvidence responseEvidence)
            : this(quietGainDb, thresholdDbFs, kneeDb, ratio, attackSeconds, holdSeconds,
                releaseSeconds, outputCeiling, micHighpassHz, micLowpassHz, stereoLinked,
                dynamicsEvidence, responseEvidence, HeadsetElectronicsCharacter.Neutral,
                HeadsetEvidence.Proposed)
        { }

        // Published device facts, including those kept only as notes; a fact
        // bound to a field also sets that field's evidence.
        private readonly HeadsetElectronicsFact[] _facts;

        internal HeadsetElectronicsProfile(float quietGainDb, float thresholdDbFs, float kneeDb,
            float ratio, float attackSeconds, float holdSeconds, float releaseSeconds,
            float outputCeiling, float micHighpassHz, float micLowpassHz, bool stereoLinked,
            HeadsetEvidence dynamicsEvidence, HeadsetEvidence responseEvidence,
            HeadsetElectronicsCharacter character, HeadsetEvidence noiseEvidence,
            HeadsetElectronicsFact[] facts = null, int micFilterOrder = 1)
        {
            QuietGainDb = quietGainDb; ThresholdDbFs = thresholdDbFs; KneeDb = kneeDb;
            Ratio = ratio; AttackSeconds = attackSeconds; HoldSeconds = holdSeconds;
            ReleaseSeconds = releaseSeconds; OutputCeiling = outputCeiling;
            MicHighpassHz = micHighpassHz; MicLowpassHz = micLowpassHz;
            StereoLinked = stereoLinked; DynamicsEvidence = dynamicsEvidence;
            ResponseEvidence = responseEvidence; Character = character;
            NoiseEvidence = noiseEvidence;
            _facts = facts == null ? Array.Empty<HeadsetElectronicsFact>() : (HeadsetElectronicsFact[])facts.Clone();
            MicFilterOrder = micFilterOrder >= 2 ? 2 : 1;
        }

        internal int FactCount => _facts.Length;
        internal HeadsetElectronicsFact FactAt(int index) => _facts[index];

        /// <summary>The source quality of one field: its own fact, else its group's evidence.</summary>
        internal HeadsetEvidence EvidenceOf(HeadsetElectronicsField field)
        {
            foreach (HeadsetElectronicsFact fact in _facts)
                if (fact.Field == field) return fact.Evidence;
            switch (field)
            {
                case HeadsetElectronicsField.QuietGain:
                case HeadsetElectronicsField.Attack:
                case HeadsetElectronicsField.Release:
                case HeadsetElectronicsField.Saturation:
                    return DynamicsEvidence;
                case HeadsetElectronicsField.Noise:
                    return NoiseEvidence;
                default:
                    return ResponseEvidence;
            }
        }

        /// <summary>One log line of the values the route writes, in their DSP units.</summary>
        internal string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "gain={0:0.##}dB band={1:0}-{2:0}Hz attack={3:0.##}ms release={4:0}ms presence={5:0.##}dB@{6:0}Hz " +
            "shelf={7:0.##}dB@{8:0}Hz noise={9:0.#}dBFS saturation={10:0.##} delay={11:0.##}ms order={13} facts={12}",
            QuietGainDb, MicHighpassHz, MicLowpassHz, AttackSeconds * 1000f, ReleaseSeconds * 1000f,
            PresenceDb, PresenceHz, LowShelfDb, LowShelfHz, NoiseDbFs, Saturation, DelaySeconds * 1000f, _facts.Length, MicFilterOrder);

        /// <summary>The same device with its colouring scaled; 1 returns this instance.</summary>
        internal HeadsetElectronicsProfile WithCharacterScale(float scale)
        {
            if (scale == 1f) return this;
            return new HeadsetElectronicsProfile(QuietGainDb, ThresholdDbFs, KneeDb, Ratio,
                AttackSeconds, HoldSeconds, ReleaseSeconds, OutputCeiling, MicHighpassHz,
                MicLowpassHz, StereoLinked, DynamicsEvidence, ResponseEvidence,
                Character.Scaled(scale), NoiseEvidence, _facts, MicFilterOrder);
        }
    }

    internal enum HeadsetElectronicsField
    {
        QuietGain, MicHighpass, MicLowpass, Attack, Release, Saturation, LowShelf, Presence, Noise, Delay
    }

    /// <summary>
    /// A published electronics fact. With a <see cref="Field"/> it fixes that
    /// DSP value; without one it is a note (dB(A) limits, critical levels,
    /// modes) whose units are not the DSP's and which is shown, not applied.
    /// </summary>
    internal sealed class HeadsetElectronicsFact
    {
        internal readonly HeadsetElectronicsField? Field;
        internal readonly float Value;
        internal readonly string Note, Source;
        internal readonly HeadsetEvidence Evidence;

        internal HeadsetElectronicsFact(HeadsetElectronicsField? field, float value, string note,
            HeadsetEvidence evidence, string source)
        {
            Field = field; Value = value; Note = note ?? ""; Evidence = evidence; Source = source ?? "";
        }

        internal bool IsNote => Field == null;
    }

    internal sealed class HeadsetProfile
    {
        internal readonly string ProfileId, PhysicalFamily, Revision, Mounting, CushionOrTip;
        private readonly string[] _itemTemplateIds, _missingData, _assumptions;
        internal readonly HeadsetPassiveProfile Passive;
        internal readonly HeadsetElectronicsProfile Electronics;

        internal HeadsetProfile(string profileId, string[] itemTemplateIds, string physicalFamily,
            string revision, string mounting, string cushionOrTip, HeadsetPassiveProfile passive,
            HeadsetElectronicsProfile electronics, string[] missingData, string[] assumptions)
        {
            ProfileId = profileId; _itemTemplateIds = (string[])itemTemplateIds.Clone();
            PhysicalFamily = physicalFamily; Revision = revision; Mounting = mounting;
            CushionOrTip = cushionOrTip; Passive = passive; Electronics = electronics;
            _missingData = (string[])missingData.Clone(); _assumptions = (string[])assumptions.Clone();
        }
        /// <summary>
        /// The same headset with different electronics. Passive data, identity
        /// and the cached passive fit (keyed by profile id) are shared.
        /// </summary>
        internal HeadsetProfile WithElectronics(HeadsetElectronicsProfile electronics) =>
            ReferenceEquals(electronics, Electronics) ? this
                : new HeadsetProfile(ProfileId, _itemTemplateIds, PhysicalFamily, Revision, Mounting,
                    CushionOrTip, Passive, electronics, _missingData, _assumptions);

        internal int TemplateIdCount => _itemTemplateIds.Length;
        internal string TemplateIdAt(int index) => _itemTemplateIds[index];
        internal int MissingDataCount => _missingData.Length;
        internal string MissingDataAt(int index) => _missingData[index];
        internal int AssumptionCount => _assumptions.Length;
        internal string AssumptionAt(int index) => _assumptions[index];
    }
}
