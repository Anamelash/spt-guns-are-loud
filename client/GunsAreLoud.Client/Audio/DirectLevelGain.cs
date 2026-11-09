using System;
using System.Collections.Generic;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// Measured levels of this session's recordings, by clip id: the attack
    /// level and the peak of each shot event. Main thread only. A release tail
    /// is filed under the shot event it ends, with its gain relative to the
    /// body, so it plays with the body's correction; a variant not measured yet
    /// borrows the mean level of its measured siblings (the geometric mean of
    /// their amplitudes).
    /// </summary>
    internal static class DirectLevelStore
    {
        internal const string SiblingSource = "sibling";
        internal const string NoSource = "none";

        private sealed class Entry
        {
            internal float AttackDb = float.NaN;
            internal float PeakDb = float.NaN;
            /// <summary>K-weighted level of the first 240 ms, dB: measured for explosion recordings only.</summary>
            internal float BodyDb = float.NaN;
            internal string Source;
        }

        private readonly struct TailAlias
        {
            internal readonly int Body;
            internal readonly float Gain;
            internal TailAlias(int body, float gain) { Body = body; Gain = gain; }
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, TailAlias> Aliases = new Dictionary<int, TailAlias>();
        private static readonly Dictionary<int, int[]> SiblingSets = new Dictionary<int, int[]>();

        internal static int Count => Entries.Count;

        /// <summary>Records what is known of one recording; a NaN leaves that measure as it was.</summary>
        internal static void Set(int id, float attackDb, float peakDb, string source, float bodyDb = float.NaN)
        {
            if (!Entries.TryGetValue(id, out Entry entry)) Entries[id] = entry = new Entry();
            if (!float.IsNaN(attackDb)) entry.AttackDb = attackDb;
            if (!float.IsNaN(peakDb)) entry.PeakDb = peakDb;
            if (!float.IsNaN(bodyDb)) entry.BodyDb = bodyDb;
            if (source != null) entry.Source = source;
        }

        internal static void Set(AudioClip clip, float attackDb, float peakDb, string source, float bodyDb = float.NaN)
        {
            if (clip != null) Set(clip.GetInstanceID(), attackDb, peakDb, source, bodyDb);
        }

        /// <summary>The recording's 240 ms level and peak are known: what an explosion needs.</summary>
        internal static bool HasBody(AudioClip clip) =>
            clip != null && Entries.TryGetValue(clip.GetInstanceID(), out Entry entry) &&
            !float.IsNaN(entry.BodyDb) && !float.IsNaN(entry.PeakDb);

        /// <summary>
        /// The 240 ms level and peak of a recording, its own or the mean of its
        /// measured siblings'. False when neither is known.
        /// </summary>
        internal static bool TryGetBody(int id, out float bodyDb, out float peakDb, out string source)
        {
            if (Entries.TryGetValue(id, out Entry entry) && !float.IsNaN(entry.BodyDb) && !float.IsNaN(entry.PeakDb))
            {
                bodyDb = entry.BodyDb;
                peakDb = entry.PeakDb;
                source = entry.Source ?? NoSource;
                return true;
            }
            if (SiblingSets.TryGetValue(id, out int[] set))
            {
                double body = 0, peak = 0;
                int measured = 0;
                foreach (int other in set)
                {
                    if (other == id || !Entries.TryGetValue(other, out Entry sibling)) continue;
                    if (float.IsNaN(sibling.BodyDb) || float.IsNaN(sibling.PeakDb)) continue;
                    body += sibling.BodyDb;
                    peak += sibling.PeakDb;
                    measured++;
                }
                if (measured > 0)
                {
                    bodyDb = (float)(body / measured);
                    peakDb = (float)(peak / measured);
                    source = SiblingSource;
                    return true;
                }
            }
            bodyDb = float.NaN;
            peakDb = float.NaN;
            source = NoSource;
            return false;
        }

        internal static bool Contains(int id) =>
            Entries.TryGetValue(id, out Entry entry) && !float.IsNaN(entry.AttackDb);

        internal static bool IsComplete(AudioClip clip) =>
            clip != null && Entries.TryGetValue(clip.GetInstanceID(), out Entry entry) &&
            !float.IsNaN(entry.AttackDb) && !float.IsNaN(entry.PeakDb);

        /// <summary>The tail clip plays as the end of the body's event, at <paramref name="tailGain"/> of the body's volume.</summary>
        internal static void Alias(int tail, int body, float tailGain)
        {
            if (tail != body && tailGain > 0f) Aliases[tail] = new TailAlias(body, tailGain);
        }

        /// <summary>The clip is known as the release tail of a shot event.</summary>
        internal static bool IsTail(int id) => Aliases.ContainsKey(id);

        internal static bool IsTail(AudioClip clip) => clip != null && IsTail(clip.GetInstanceID());

        /// <summary>Overwrites both measures of a recording, for one whose samples turned out to have changed.</summary>
        internal static void Replace(int id, float attackDb, float peakDb, string source, float bodyDb = float.NaN)
        {
            Entries[id] = new Entry { AttackDb = attackDb, PeakDb = peakDb, BodyDb = bodyDb, Source = source };
        }

        internal static void Alias(AudioClip tail, AudioClip body, float tailGain)
        {
            if (tail != null && body != null && tail != body) Alias(tail.GetInstanceID(), body.GetInstanceID(), tailGain);
        }

        internal static void Siblings(AudioClip[] clips)
        {
            if (clips == null) return;
            int count = 0;
            int[] existing = null;
            bool known = true;
            foreach (AudioClip clip in clips)
            {
                if (clip == null) continue;
                count++;
                if (!SiblingSets.TryGetValue(clip.GetInstanceID(), out int[] set)) known = false;
                else if (existing == null) existing = set;
                else if (!ReferenceEquals(existing, set)) known = false;
            }
            // Re-declaring the same set (every planning pass) is free.
            if (count < 2 || known && existing != null && existing.Length == count) return;
            var ids = new int[count];
            int index = 0;
            foreach (AudioClip clip in clips) if (clip != null) ids[index++] = clip.GetInstanceID();
            Siblings(ids);
        }

        internal static void Siblings(params int[] ids)
        {
            if (ids == null || ids.Length < 2) return;
            int[] set = (int[])ids.Clone();
            foreach (int id in set) SiblingSets[id] = set;
        }

        /// <summary>
        /// The measured attack level and peak of a recording, its own or its
        /// siblings'. <paramref name="volumeScale"/> is what the playing volume
        /// is divided by before it is added (a tail's gain relative to its body;
        /// 1 otherwise). The peak is NaN when only the attack is known yet.
        /// </summary>
        internal static bool TryGet(int id, out float attackDb, out float peakDb, out float volumeScale, out string source)
        {
            volumeScale = 1f;
            if (Aliases.TryGetValue(id, out TailAlias alias))
            {
                id = alias.Body;
                volumeScale = alias.Gain;
            }
            if (Entries.TryGetValue(id, out Entry entry) && !float.IsNaN(entry.AttackDb))
            {
                attackDb = entry.AttackDb;
                peakDb = entry.PeakDb;
                source = entry.Source ?? NoSource;
                return true;
            }
            if (SiblingSets.TryGetValue(id, out int[] set))
            {
                double attack = 0, peak = 0;
                int measured = 0, peaks = 0;
                foreach (int other in set)
                {
                    if (other == id || !Entries.TryGetValue(other, out Entry sibling)) continue;
                    if (float.IsNaN(sibling.AttackDb)) continue;
                    attack += sibling.AttackDb;
                    measured++;
                    if (float.IsNaN(sibling.PeakDb)) continue;
                    peak += sibling.PeakDb;
                    peaks++;
                }
                if (measured > 0)
                {
                    attackDb = (float)(attack / measured);
                    peakDb = peaks > 0 ? (float)(peak / peaks) : float.NaN;
                    source = SiblingSource;
                    return true;
                }
            }
            attackDb = float.NaN;
            peakDb = float.NaN;
            source = NoSource;
            return false;
        }

        /// <summary>Counts the times the store was cleared: what was planned against an earlier life of it is planned again.</summary>
        internal static int Generation { get; private set; }

        internal static void Clear()
        {
            Entries.Clear();
            Aliases.Clear();
            SiblingSets.Clear();
            Generation++;
        }
    }

    /// <summary>What the direct level correction decided for one played sample.</summary>
    internal readonly struct DirectLevelDecision
    {
        /// <summary>The recording's measured attack level plus the volume it plays at (M), dB; NaN without a measurement.</summary>
        internal readonly float PlayingDb;
        internal readonly float TargetDb;
        /// <summary>The correction of the event, dB: recording, tail, room and copy all move by it.</summary>
        internal readonly float GainDb;
        /// <summary>The share of its ratio the copy keeps, linear; 1 when the peak budget did not bind on it.</summary>
        internal readonly float CopyShare;
        /// <summary>Predicted peak of the event at the listener before the limiter, linear; NaN without a copy measurement.</summary>
        internal readonly float SumPeak;
        /// <summary>The peak budget, not the target, set the gain.</summary>
        internal readonly bool BudgetBound;
        /// <summary>Where the measurement came from: cache, getdata, playback, sibling or none.</summary>
        internal readonly string Source;
        internal readonly bool Decided;
        /// <summary>Shot density the event was decided with, dB; 0 when the limiter is a safety only.</summary>
        internal readonly float DensityDb;
        /// <summary>How far the event's peak stands over the output ceiling at the program limiter, dB; NaN without density.</summary>
        internal readonly float OverDb;
        /// <summary>What the program limiter is predicted to take off the attack level, dB.</summary>
        internal readonly float LimitLossDb;

        internal DirectLevelDecision(float playingDb, float targetDb, float gainDb, string source,
            float copyShare = 1f, float sumPeak = float.NaN, bool budgetBound = false,
            float densityDb = 0f, float overDb = float.NaN, float limitLossDb = 0f)
        {
            PlayingDb = playingDb;
            TargetDb = targetDb;
            GainDb = gainDb;
            CopyShare = float.IsNaN(copyShare) ? 1f : Math.Max(0f, Math.Min(1f, copyShare));
            SumPeak = sumPeak;
            BudgetBound = budgetBound;
            Source = source ?? DirectLevelStore.NoSource;
            Decided = true;
            DensityDb = densityDb;
            OverDb = overDb;
            LimitLossDb = limitLossDb;
        }

        /// <summary>The event's correction as a linear factor.</summary>
        internal float Linear => DirectLevelModel.Linear(GainDb);

        /// <summary>The copy's factor: the event's correction times the copy's share.</summary>
        internal float CopyLinear => Decided ? Linear * CopyShare : 1f;

        internal static DirectLevelDecision None => default;

        internal string Describe(string mode) =>
            $"directMeasured={(float.IsNaN(PlayingDb) ? "n/a" : PlayingDb.ToString("0.0"))}dB " +
            $"directTarget={TargetDb:0.0}dB gDirect={GainDb:+0.0;-0.0;0.0}dB " +
            $"copyShare={DirectLevelMeter.ToDb(Decided ? CopyShare : 1f):+0.0;-0.0;0.0}dB " +
            $"sumPeak={(float.IsNaN(SumPeak) ? "n/a" : SumPeak.ToString("0.00"))} " +
            $"budget={(BudgetBound ? "bound" : "free")} mode={mode} " +
            $"density={DensityDb:0.0}dB over={(float.IsNaN(OverDb) ? "n/a" : OverDb.ToString("0.0"))}dB " +
            $"limitLoss={LimitLossDb:0.0}dB " +
            $"pcm={(Decided ? Source : DirectLevelStore.NoSource)}";
    }

    internal static class DirectLevel
    {
        /// <summary>
        /// The correction for a sample of this shot. <paramref name="donorVolume"/>
        /// is the volume the engine gives the source for <paramref name="clip"/>
        /// before occlusion: the bank's volume and anything the engine adds.
        /// </summary>
        internal static DirectLevelDecision Decide(LocalGunshotAudioTuning tuning, AudioClip clip, float donorVolume)
        {
            if (clip == null) return new DirectLevelDecision(float.NaN, tuning.DirectTargetDb, 0f, DirectLevelStore.NoSource);
            return Decide(tuning, clip.GetInstanceID(), donorVolume);
        }

        internal static DirectLevelDecision Decide(LocalGunshotAudioTuning tuning, int clipId, float donorVolume)
        {
            if (!TryBudget(tuning, clipId, donorVolume, out ShotBudget shot, out string source))
                return new DirectLevelDecision(float.NaN, tuning.DirectTargetDb, 0f, source);
            return Decide(tuning, shot, source);
        }

        private static DirectLevelDecision Decide(LocalGunshotAudioTuning tuning, ShotBudget shot, string source)
        {
            ShotBudgetSolution solution = DirectLevelModel.Solve(shot, tuning.DirectTargetDb, tuning.DirectStrength,
                tuning.DirectCopyHeadroomDb, DirectLevelModel.PeakBudget, tuning.DirectDensityDb);
            return new DirectLevelDecision(DirectLevelModel.PlayingLevelDb(shot.AttackDb, shot.Volume),
                tuning.DirectTargetDb, solution.GainDb, source, solution.CopyShare, solution.SumPeak, solution.BudgetBound,
                tuning.DirectDensityDb, solution.OverDb, solution.LimitLossDb);
        }

        /// <summary>
        /// The copy's level relative to the recording before any correction:
        /// its normalization times the layer chain the shot plays it with. The
        /// correction itself is not in it.
        /// </summary>
        internal static float CopyRatio(LocalGunshotAudioTuning tuning, int clipId)
        {
            float layer = PitchedGunshotLayer.CalculateLayerGain(tuning.DirectBodyGain, tuning.EnergyIndex, 0f,
                tuning.CartridgeContrastDb) * PitchedGunshotLayer.CalculateUserGain(tuning.PitchedLayerGainDb) *
                tuning.HeadphonesDamping.BodyGain;
            // The copy's attack and its peak play at the body calibration; the
            // decay gain only takes over after the body, when the event has
            // long passed its peak and its attack window.
            LowEndNormalizationResult normalization = LowEndNormalizationCache.Evaluate(clipId, tuning);
            float calibration = normalization.Ready ? normalization.BodyGain : 1f;
            return layer * calibration;
        }

        /// <summary>
        /// What the stores know of this recording and its copy, at the volume the
        /// engine plays it. False without an attack measurement.
        /// </summary>
        internal static bool TryBudget(LocalGunshotAudioTuning tuning, int clipId, float donorVolume,
            out ShotBudget shot, out string source)
        {
            shot = default;
            if (!DirectLevelStore.TryGet(clipId, out float attackDb, out float peakDb, out float volumeScale, out source))
                return false;
            if (!(donorVolume > 0f)) return false;
            float volume = donorVolume / Math.Max(1e-4f, volumeScale);
            // A recording whose peak is not in yet is taken at full scale: every
            // close recording of the game peaks there.
            float peak = float.IsNaN(peakDb) ? 1f : DirectLevelModel.Linear(peakDb);
            if (LowEndNormalizationCache.TryGetCopyBudget(clipId, tuning, out ShotSumMeasure copy))
                shot = new ShotBudget(attackDb, peak, volume, CopyRatio(tuning, clipId), copy.CopyAttackDb, copy.CopyPeak,
                    copy.SumPeaks, copy.LimitLoss);
            else
                shot = new ShotBudget(attackDb, peak, volume);
            return true;
        }

        /// <summary>
        /// The correction for a source that plays two clips at once (EFT's
        /// distance blend): one gain goes on both channels, so it is decided on
        /// their sum, each clip at its own volume. A clip at volume 0 does not
        /// count; with both at 0 there is nothing to correct.
        /// </summary>
        internal static DirectLevelDecision Decide(LocalGunshotAudioTuning tuning,
            int clipA, float volumeA, int clipB, float volumeB)
        {
            bool useA = clipA != 0 && volumeA > 0f;
            bool useB = clipB != 0 && volumeB > 0f;
            if (!useA && !useB)
                return new DirectLevelDecision(float.NaN, tuning.DirectTargetDb, 0f, DirectLevelStore.NoSource);
            if (!useB) return Decide(tuning, clipA, volumeA);
            if (!useA) return Decide(tuning, clipB, volumeB);
            bool knownA = TryBudget(tuning, clipA, volumeA, out ShotBudget a, out string sourceA);
            bool knownB = TryBudget(tuning, clipB, volumeB, out ShotBudget b, out string sourceB);
            // A clip without a measurement leaves the decision to the other one.
            if (!knownA && !knownB) return new DirectLevelDecision(float.NaN, tuning.DirectTargetDb, 0f, sourceA);
            if (!knownB) return Decide(tuning, a, sourceA);
            if (!knownA) return Decide(tuning, b, sourceB);
            return Decide(tuning, Combine(a, b), sourceA);
        }

        /// <summary>Two clips on one source as one event at volume 1: energies add, peaks add.</summary>
        internal static ShotBudget Combine(ShotBudget a, ShotBudget b)
        {
            float attack = DirectLevelModel.CombinePlayingDb(
                DirectLevelModel.PlayingLevelDb(a.AttackDb, a.Volume), DirectLevelModel.PlayingLevelDb(b.AttackDb, b.Volume));
            float peak = a.Volume * a.Peak + b.Volume * b.Peak;
            if (!a.HasCopy && !b.HasCopy) return new ShotBudget(attack, peak, 1f);
            // The copy follows the louder recording's ratio, and so does the
            // limiter's loss table; a clip without a copy measurement contributes
            // its recording only.
            float ratio = a.HasCopy ? a.Ratio : b.Ratio;
            float[] loss = a.HasCopy ? a.LimitLoss : b.LimitLoss;
            float copyAttack = float.NaN, copyPeak = 0f;
            var sums = new float[ShotSumRatios.Values.Length];
            Accumulate(a, ratio, ref copyAttack, ref copyPeak, sums);
            Accumulate(b, ratio, ref copyAttack, ref copyPeak, sums);
            return new ShotBudget(attack, peak, 1f, ratio, copyAttack, copyPeak, sums, loss);
        }

        private static void Accumulate(ShotBudget part, float ratio, ref float copyAttack, ref float copyPeak, float[] sums)
        {
            if (!part.HasCopy)
            {
                for (int index = 0; index < sums.Length; index++) sums[index] += part.Volume * part.Peak;
                return;
            }
            // Measured at its own ratio scale; re-expressed at the shared ratio.
            float scale = part.Ratio / ratio;
            copyAttack = DirectLevelModel.CombinePlayingDb(copyAttack,
                DirectLevelModel.PlayingLevelDb(part.CopyAttackDb, part.Volume * scale));
            copyPeak += part.Volume * scale * part.CopyPeak;
            for (int index = 0; index < sums.Length; index++)
                sums[index] += part.Volume * ShotSumRatios.SumPeak(part.Peak, part.SumPeaks, ShotSumRatios.Values[index] * scale);
        }

        internal static DirectLevelDecision Decide(LocalGunshotAudioTuning tuning,
            AudioClip clipA, float volumeA, AudioClip clipB, float volumeB) =>
            Decide(tuning, clipA != null ? clipA.GetInstanceID() : 0, volumeA,
                clipB != null ? clipB.GetInstanceID() : 0, volumeB);
    }

    /// <summary>
    /// The direct level correction on one channel of an EFT source carrying
    /// the local shot: a single scalar gain, set from the main thread when the
    /// shot is played and kept until the source is handed to another sound.
    /// Sits after any capture on the channel, so cached copies are taken from
    /// the recording as it is.
    /// </summary>
    internal sealed class DirectLevelGainFilter : MonoBehaviour
    {
        private float _gain = 1f;

        internal float Gain => Volatile.Read(ref _gain);

        internal void SetGain(float gain)
        {
            if (float.IsNaN(gain) || float.IsInfinity(gain) || gain < 0f) gain = 1f;
            Volatile.Write(ref _gain, gain);
        }

        private void Awake() => GalSourceCensus.Created(GalComponentKind.DirectLevelGain);

        private void OnEnable() => GalSourceCensus.Enabled(GalComponentKind.DirectLevelGain);

        private void OnDisable()
        {
            GalSourceCensus.Disabled(GalComponentKind.DirectLevelGain);
            Volatile.Write(ref _gain, 1f);
        }

        private void OnDestroy() => GalSourceCensus.Destroyed(GalComponentKind.DirectLevelGain);

        private void OnAudioFilterRead(float[] data, int channels)
        {
            long trace = AudioFilterTrace.Begin();
            float gain = Volatile.Read(ref _gain);
            if (data == null || gain == 1f)
            {
                AudioFilterTrace.Record(AudioFilterKind.DirectLevel, trace, data == null ? 0 : data.Length,
                    channels, idle: true);
                return;
            }
            for (int index = 0; index < data.Length; index++) data[index] *= gain;
            AudioFilterTrace.Record(AudioFilterKind.DirectLevel, trace, data.Length, channels);
        }
    }
}
