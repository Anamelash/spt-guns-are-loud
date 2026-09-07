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

    internal sealed class HeadsetElectronicsProfile
    {
        internal readonly float QuietGainDb, ThresholdDbFs, KneeDb, Ratio;
        internal readonly float AttackSeconds, HoldSeconds, ReleaseSeconds, OutputCeiling;
        internal readonly float MicHighpassHz, MicLowpassHz;
        internal readonly bool StereoLinked;
        internal readonly HeadsetEvidence DynamicsEvidence, ResponseEvidence;

        internal HeadsetElectronicsProfile(float quietGainDb, float thresholdDbFs, float kneeDb,
            float ratio, float attackSeconds, float holdSeconds, float releaseSeconds,
            float outputCeiling, float micHighpassHz, float micLowpassHz, bool stereoLinked,
            HeadsetEvidence dynamicsEvidence, HeadsetEvidence responseEvidence)
        {
            QuietGainDb = quietGainDb; ThresholdDbFs = thresholdDbFs; KneeDb = kneeDb;
            Ratio = ratio; AttackSeconds = attackSeconds; HoldSeconds = holdSeconds;
            ReleaseSeconds = releaseSeconds; OutputCeiling = outputCeiling;
            MicHighpassHz = micHighpassHz; MicLowpassHz = micLowpassHz;
            StereoLinked = stereoLinked; DynamicsEvidence = dynamicsEvidence;
            ResponseEvidence = responseEvidence;
        }
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
        internal int TemplateIdCount => _itemTemplateIds.Length;
        internal string TemplateIdAt(int index) => _itemTemplateIds[index];
        internal int MissingDataCount => _missingData.Length;
        internal string MissingDataAt(int index) => _missingData[index];
        internal int AssumptionCount => _assumptions.Length;
        internal string AssumptionAt(int index) => _assumptions[index];
    }
}
