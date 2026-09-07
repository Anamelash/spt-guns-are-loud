using System;

namespace GunsAreLoud.Client.Runtime
{
    internal enum HeadsetEvidence { Measured, ManufacturerClaim, FamilySurrogate, Proposed }

    internal sealed class HeadsetPassiveProfile
    {
        private readonly float[] _frequenciesHz, _meanAttenuationDb, _standardDeviationDb;
        internal readonly string Standard, Source, Page;
        internal readonly HeadsetEvidence CurveEvidence;

        internal HeadsetPassiveProfile(float[] frequenciesHz, float[] meanDb, float[] sdDb,
            string standard, string source, string page, HeadsetEvidence evidence)
        {
            _frequenciesHz = (float[])frequenciesHz.Clone();
            _meanAttenuationDb = (float[])meanDb.Clone();
            _standardDeviationDb = sdDb == null ? Array.Empty<float>() : (float[])sdDb.Clone();
            Standard = standard ?? ""; Source = source ?? ""; Page = page ?? "";
            CurveEvidence = evidence;
        }
        internal int BandCount => _frequenciesHz.Length;
        internal float FrequencyAt(int index) => _frequenciesHz[index];
        internal float MeanAttenuationAt(int index) => _meanAttenuationDb[index];
        internal float StandardDeviationAt(int index) => index < _standardDeviationDb.Length ? _standardDeviationDb[index] : 0f;
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
