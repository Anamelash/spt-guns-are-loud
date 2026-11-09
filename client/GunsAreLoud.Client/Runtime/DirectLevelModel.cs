using System;
using GunsAreLoud.Client.Configuration;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// What is known of one shot event before it is corrected: the recording
    /// and the mod's low-end copy of it, as the warmup measured them, and how
    /// loud the engine is about to play the recording. Levels are on the scale
    /// of the recording itself (bank volume included); the engine's volume is
    /// kept apart in <see cref="Volume"/>.
    /// </summary>
    internal readonly struct ShotBudget
    {
        /// <summary>Attack level (K-weighted first window) of the recording, dB.</summary>
        internal readonly float AttackDb;
        /// <summary>Peak of the recording, linear.</summary>
        internal readonly float Peak;
        /// <summary>Attack level of the copy at ratio 1 to the recording, dB; NaN without a copy measurement.</summary>
        internal readonly float CopyAttackDb;
        /// <summary>Peak of the copy at ratio 1, linear.</summary>
        internal readonly float CopyPeak;
        /// <summary>Peak of recording + ratio · copy, at <see cref="ShotSumRatios.Values"/>; null without a copy measurement.</summary>
        internal readonly float[] SumPeaks;
        /// <summary>The copy's level relative to the recording before any correction: normalization times the layer chain.</summary>
        internal readonly float Ratio;
        /// <summary>The volume the engine gives the source for this shot.</summary>
        internal readonly float Volume;
        /// <summary>
        /// What the program limiter takes off the event's attack level, dB (at
        /// most 0), for the recording alone and at each measured ratio, at each
        /// of <see cref="ShotSumRatios.OverDb"/>; null when not measured.
        /// </summary>
        internal readonly float[] LimitLoss;

        internal ShotBudget(float attackDb, float peak, float volume, float ratio = 0f,
            float copyAttackDb = float.NaN, float copyPeak = 0f, float[] sumPeaks = null, float[] limitLoss = null)
        {
            AttackDb = attackDb;
            Peak = peak;
            Volume = volume;
            Ratio = ratio;
            CopyAttackDb = copyAttackDb;
            CopyPeak = copyPeak;
            SumPeaks = sumPeaks;
            LimitLoss = limitLoss;
        }

        internal bool HasCopy => SumPeaks != null && SumPeaks.Length == ShotSumRatios.Values.Length &&
            Ratio > 0f && !float.IsNaN(CopyAttackDb) && !float.IsInfinity(CopyAttackDb);

        internal bool HasLoss => LimitLoss != null && LimitLoss.Length == ShotSumRatios.LossLength;
    }

    /// <summary>The copy-to-recording ratios the aligned sum's peak is measured at.</summary>
    internal static class ShotSumRatios
    {
        internal static readonly float[] Values = { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };
        internal const float MaximumRatio = 16f;

        /// <summary>
        /// How far the event's peak stands above the output ceiling, dB, at the
        /// points the program limiter's attack loss is measured. 0 is the
        /// ceiling itself; the peak budget alone is about 2 dB over it.
        /// </summary>
        internal static readonly float[] OverDb = { 0f, 2f, 4f, 6f, 9f, 12f };

        /// <summary>Rows of the loss table: the recording alone, then each of <see cref="Values"/>.</summary>
        internal static int LossRows => Values.Length + 1;
        internal static int LossLength => LossRows * OverDb.Length;

        /// <summary>
        /// The attack loss of an event at this copy ratio whose peak stands this
        /// far over the ceiling: linear in both directions between the measured
        /// points, nothing at or under the ceiling, extended along the last
        /// segment above the table, never a gain.
        /// </summary>
        internal static float LimitLossDb(float[] loss, float ratio, float overDb)
        {
            if (loss == null || loss.Length != LossLength || float.IsNaN(overDb) || !(overDb > 0f)) return 0f;
            int rowLow = 0, rowHigh = 0;
            float rowFraction = 0f;
            if (ratio > 0f)
            {
                float previous = 0f;
                rowLow = rowHigh = LossRows - 1;
                for (int index = 0; index < Values.Length; index++)
                {
                    if (ratio <= Values[index])
                    {
                        rowLow = index;
                        rowHigh = index + 1;
                        rowFraction = (ratio - previous) / (Values[index] - previous);
                        break;
                    }
                    previous = Values[index];
                }
            }
            float low = Column(loss, rowLow, overDb);
            if (rowHigh == rowLow) return Math.Min(0f, low);
            float high = Column(loss, rowHigh, overDb);
            return Math.Min(0f, low + (high - low) * rowFraction);
        }

        private static float Column(float[] loss, int row, float overDb)
        {
            int offset = row * OverDb.Length;
            int last = OverDb.Length - 1;
            if (overDb >= OverDb[last])
            {
                float slope = (loss[offset + last] - loss[offset + last - 1]) / (OverDb[last] - OverDb[last - 1]);
                return loss[offset + last] + Math.Min(0f, slope) * (overDb - OverDb[last]);
            }
            for (int index = 1; index <= last; index++)
            {
                if (overDb <= OverDb[index])
                {
                    float a = loss[offset + index - 1], b = loss[offset + index];
                    return a + (b - a) * (overDb - OverDb[index - 1]) / (OverDb[index] - OverDb[index - 1]);
                }
            }
            return loss[offset];
        }

        /// <summary>Peak of recording + ratio · copy, interpolated between the measured ratios; the recording alone at 0.</summary>
        internal static float SumPeak(float directPeak, float[] sumPeaks, float ratio)
        {
            if (sumPeaks == null || sumPeaks.Length != Values.Length || !(ratio > 0f)) return directPeak;
            float previousRatio = 0f, previousPeak = directPeak;
            for (int index = 0; index < Values.Length; index++)
            {
                float point = Values[index];
                float peak = Math.Max(sumPeaks[index], previousPeak);
                if (ratio <= point)
                    return previousPeak + (peak - previousPeak) * (ratio - previousRatio) / (point - previousRatio);
                previousRatio = point;
                previousPeak = peak;
            }
            int last = Values.Length - 1;
            float slope = (Math.Max(sumPeaks[last], sumPeaks[last - 1]) - sumPeaks[last - 1]) / (Values[last] - Values[last - 1]);
            return previousPeak + Math.Max(0f, slope) * (ratio - previousRatio);
        }

        /// <summary>The largest ratio whose sum peak stays at or under the limit; 0 when the recording alone does not.</summary>
        internal static float RatioFor(float directPeak, float[] sumPeaks, float limit)
        {
            if (!(limit > directPeak)) return 0f;
            if (sumPeaks == null || sumPeaks.Length != Values.Length) return 0f;
            float previousRatio = 0f, previousPeak = directPeak;
            for (int index = 0; index < Values.Length; index++)
            {
                float point = Values[index];
                float peak = Math.Max(sumPeaks[index], previousPeak);
                if (peak > limit)
                    return previousRatio + (limit - previousPeak) / (peak - previousPeak) * (point - previousRatio);
                previousRatio = point;
                previousPeak = peak;
            }
            int last = Values.Length - 1;
            float slope = (previousPeak - Math.Max(sumPeaks[last - 1], directPeak)) / (Values[last] - Values[last - 1]);
            if (!(slope > 1e-6f)) return MaximumRatio;
            return Math.Min(MaximumRatio, previousRatio + (limit - previousPeak) / slope);
        }
    }

    /// <summary>What the correction decided for one shot event.</summary>
    internal readonly struct ShotBudgetSolution
    {
        /// <summary>The correction of the whole event (recording, tail, room and copy), dB.</summary>
        internal readonly float GainDb;
        /// <summary>The share of its ratio the copy keeps, linear, 1 when the budget did not bind on it.</summary>
        internal readonly float CopyShare;
        /// <summary>Predicted peak of the event at the listener, linear; NaN without a measurement.</summary>
        internal readonly float SumPeak;
        /// <summary>The peak budget, not the target, set the gain.</summary>
        internal readonly bool BudgetBound;
        /// <summary>How far the event's peak stands over the output ceiling at the program limiter, dB; NaN without density.</summary>
        internal readonly float OverDb;
        /// <summary>What the program limiter is predicted to take off the attack level, dB (at most 0); 0 without density.</summary>
        internal readonly float LimitLossDb;

        internal ShotBudgetSolution(float gainDb, float copyShare, float sumPeak, bool budgetBound,
            float overDb = float.NaN, float limitLossDb = 0f)
        {
            GainDb = gainDb;
            CopyShare = copyShare;
            SumPeak = sumPeak;
            BudgetBound = budgetBound;
            OverDb = overDb;
            LimitLossDb = limitLossDb;
        }

        internal static ShotBudgetSolution Unchanged => new ShotBudgetSolution(0f, 1f, float.NaN, false);
    }

    /// <summary>
    /// The level the player's own shot event plays at and the gain that moves it
    /// there. One event is the recording, its tail, the room EFT adds and the
    /// mod's low-end copy; one gain moves all of them. The event's attack level
    /// (K-weighted first 50 ms of the sum) follows the shot level index; its
    /// peak before the output limiter never exceeds one budget, so the limiter
    /// stays a safety. Pure functions only.
    /// </summary>
    internal static class DirectLevelModel
    {
        /// <summary>Peak of the event at the listener, linear, before the output limiter: at most −2 dB of limiting.</summary>
        internal const float PeakBudget = 1.25f;

        /// <summary>The output limiter's ceiling, linear: what "over" is measured against.</summary>
        internal const float OutputCeiling = 0.999f;

        /// <summary>
        /// Shot density: how far over the peak budget the event may go into the
        /// program limiter, dB. The limiter takes the crack down and lets the
        /// body and the room through, so the event gets denser; the attack level
        /// after the limiter is what the target is held on.
        /// </summary>
        internal const float DefaultDensityDb = 6f;
        internal const float MinimumDensityDb = 1f;
        internal const float MaximumDensityDb = 9f;

        /// <summary>
        /// The attack level the sparsest heavy recordings in the game (7.62×39
        /// and 7.62×51 assault rifles indoors, measured over the stock and
        /// Sound Engine Backport banks) reach at the peak budget with the default
        /// copy headroom. The scale's top: no event is asked to go above it.
        /// </summary>
        internal const float TopLevelDb = -4.5f;

        /// <summary>The level index at and above which the target is the top: 7.62×39 and 7.62×51 at 0.8–1.0 m.</summary>
        internal const float TopIndexDb = 2.5f;

        /// <summary>How far below the top a lighter cartridge sits per dB of level index: the smallest gap that keeps the order.</summary>
        internal const float SlopePerDb = 0.5f;

        /// <summary>A shot outdoors targets this much below the same shot indoors.</summary>
        internal const float OutdoorDb = -3f;

        /// <summary>How much of the peak budget the recording leaves to the copy by default, dB.</summary>
        internal const float DefaultCopyHeadroomDb = 3f;
        internal const float MaximumCopyHeadroomDb = 12f;

        internal const float MaximumCutDb = -12f;
        internal const float MaximumLiftDb = 12f;

        /// <summary>Full strength of the F12 control, in dB.</summary>
        internal const float NormalizationSpanDb = 12f;

        // Muzzle-energy bands that stand in for a cartridge class the table does not name.
        internal const float PistolEnergyLimitJoules = 900f;
        internal const float IntermediateEnergyLimitJoules = 2700f;
        internal const float RifleEnergyLimitJoules = 8000f;

        /// <summary>
        /// Energy target: the top less the slope times the distance of the
        /// index below the top, then the room and the player's own engine
        /// trim. The muzzle devices are in the index.
        /// </summary>
        internal static float EnergyTargetDb(float levelIndexDb, bool indoor, float userGainDb)
        {
            float level = Finite(levelIndexDb);
            float target = TopLevelDb - SlopePerDb * Math.Max(0f, TopIndexDb - level);
            return target + (indoor ? 0f : OutdoorDb) + Finite(userGainDb);
        }

        /// <summary>
        /// Baseline target: one fixed level per cartridge class below the top,
        /// moved by the muzzle devices at the scale's slope and never above the
        /// top; no room term.
        /// </summary>
        internal static float BaselineTargetDb(CaliberClass cartridgeClass, float deviceIndexDb, float userGainDb)
        {
            float beta = CaliberClassTable.BaselineLevel(cartridgeClass);
            if (!(beta > 0f)) beta = CaliberClassTable.BaselineLevel(CaliberClass.Intermediate);
            float target = TopLevelDb + (float)(20.0 * Math.Log10(beta)) + SlopePerDb * Finite(deviceIndexDb);
            return Math.Min(TopLevelDb, target) + Finite(userGainDb);
        }

        /// <summary>A class for a cartridge the table does not name, from its muzzle energy.</summary>
        internal static CaliberClass ClassFromEnergy(float energyJoules)
        {
            if (energyJoules < PistolEnergyLimitJoules) return CaliberClass.Pistol;
            if (energyJoules < IntermediateEnergyLimitJoules) return CaliberClass.Intermediate;
            if (energyJoules < RifleEnergyLimitJoules) return CaliberClass.Rifle;
            return CaliberClass.Heavy;
        }

        /// <summary>
        /// The class the baseline target uses: the table's, or the muzzle-energy
        /// band when the table does not know the caliber (then
        /// <paramref name="fromEnergy"/> is true). A round without a known
        /// energy counts as the reference cartridge.
        /// </summary>
        internal static CaliberClass ResolveClass(string caliber, ShotLevel level, out bool fromEnergy)
        {
            CaliberClass listed = CaliberClassTable.Classify(caliber);
            fromEnergy = listed == CaliberClass.Unknown;
            if (!fromEnergy) return listed;
            float energy = level.Fallback || !(level.EnergyJoules > 0f)
                ? ShotLevelIndex.ReferenceEnergyJoules
                : level.EnergyJoules;
            return ClassFromEnergy(energy);
        }

        internal static float TargetDb(DirectLevelTarget mode, ShotLevel level,
            string caliber, bool indoor, float userGainDb,
            out CaliberClass cartridgeClass, out bool classFromEnergy)
        {
            cartridgeClass = ResolveClass(caliber, level, out classFromEnergy);
            return mode == DirectLevelTarget.Baseline
                ? BaselineTargetDb(cartridgeClass, level.DeviceIndex, userGainDb)
                : EnergyTargetDb(level.LevelIndex, indoor, userGainDb);
        }

        /// <summary>q from the F12 value in dB (0-12).</summary>
        internal static float Strength(float normalizationDb) =>
            float.IsNaN(normalizationDb) ? 0f : Math.Max(0f, Math.Min(1f, normalizationDb / NormalizationSpanDb));

        /// <summary>
        /// The level the recording would play at uncorrected: its own level
        /// plus the volume its source plays it at.
        /// </summary>
        internal static float PlayingLevelDb(float measuredDb, float donorVolume) =>
            measuredDb + (float)(20.0 * Math.Log10(Math.Max(1e-6f, donorVolume)));

        /// <summary>Two levels heard together: the energy sum, 10·log10(10^(a/10) + 10^(b/10)).</summary>
        internal static float CombinePlayingDb(float a, float b)
        {
            if (float.IsNaN(a)) return b;
            if (float.IsNaN(b)) return a;
            return (float)(10.0 * Math.Log10(Math.Pow(10.0, a / 10.0) + Math.Pow(10.0, b / 10.0)));
        }

        /// <summary>Attack level of the event at gain 0 with the copy at this share of its ratio, on the recording's scale.</summary>
        internal static float EventAttackDb(ShotBudget shot, float copyShare)
        {
            if (!shot.HasCopy || !(copyShare > 0f)) return shot.AttackDb;
            float copy = shot.CopyAttackDb + (float)(20.0 * Math.Log10(copyShare * shot.Ratio));
            return CombinePlayingDb(shot.AttackDb, copy);
        }

        /// <summary>
        /// The gain of the event and the share of its ratio the copy keeps.
        /// The recording is moved towards the target; it never peaks above the
        /// budget less the copy headroom, and the whole event never peaks above
        /// the budget. When the sum would, the copy gives way first; the
        /// recording's level is the last thing to go. Strength scales both
        /// corrections; at 0 the event plays exactly as the engine would play it.
        /// </summary>
        internal static ShotBudgetSolution Solve(ShotBudget shot, float targetDb, float strength,
            float copyHeadroomDb = DefaultCopyHeadroomDb, float budget = PeakBudget, float densityDb = 0f,
            float maximumLiftDb = MaximumLiftDb)
        {
            if (!(strength > 0f) || float.IsNaN(targetDb) || float.IsInfinity(targetDb) ||
                float.IsNaN(shot.AttackDb) || float.IsInfinity(shot.AttackDb) || !(shot.Peak > 0f) || !(shot.Volume > 0f))
                return ShotBudgetSolution.Unchanged;
            float volumeDb = (float)(20.0 * Math.Log10(shot.Volume));
            float headroom = Math.Max(0f, Math.Min(MaximumCopyHeadroomDb, Finite(copyHeadroomDb)));
            // With density the event may stand this much higher at the program
            // limiter's input; the limiter brings its peak back under the budget.
            float density = Math.Max(0f, Math.Min(MaximumDensityDb, Finite(densityDb)));
            bool dense = density > 0f;
            float stageBudget = budget * (float)Math.Pow(10.0, density / 20.0);
            float directCap = stageBudget * (float)Math.Pow(10.0, -headroom / 20.0);
            float capDb = (float)(20.0 * Math.Log10(directCap / (shot.Volume * shot.Peak)));
            // The attack the limiter takes off is known only once the gain is;
            // it moves less than a dB per dB of gain, so the fixed point is near.
            bool modelLoss = dense && shot.HasCopy && shot.HasLoss;
            float lossDb = 0f;
            float share = 1f;
            float gainDb = 0f;
            bool bound = false;
            // Less copy lowers the event's attack level, so the recording may go a
            // little higher, which asks a little less copy again: a few rounds settle it.
            for (int iteration = 0; iteration < 12; iteration++)
            {
                float wanted = targetDb - volumeDb - EventAttackDb(shot, share) - lossDb;
                bound = wanted > capDb;
                float next = Math.Min(wanted, capDb);
                bool settled = iteration > 0 && Math.Abs(next - gainDb) < 0.01f;
                gainDb = next;
                if (!shot.HasCopy) break;
                float scale = shot.Volume * (float)Math.Pow(10.0, gainDb / 20.0);
                float peak = scale * ShotSumRatios.SumPeak(shot.Peak, shot.SumPeaks, share * shot.Ratio);
                if (peak > stageBudget * 1.0005f)
                {
                    float ratio = ShotSumRatios.RatioFor(shot.Peak, shot.SumPeaks, stageBudget / scale);
                    share = Math.Max(0f, Math.Min(1f, ratio / shot.Ratio));
                    bound = true;
                    if (modelLoss) lossDb = LossAt(shot, share, gainDb);
                    continue;
                }
                if (!modelLoss || settled) break;
                lossDb = LossAt(shot, share, gainDb);
            }
            gainDb = Math.Max(MaximumCutDb, Math.Min(Math.Max(0f, Finite(maximumLiftDb)), gainDb));
            if (shot.HasCopy)
            {
                // The gain is final; the copy's share is held to what fits under it.
                float scale = shot.Volume * (float)Math.Pow(10.0, gainDb / 20.0);
                float ratio = ShotSumRatios.RatioFor(shot.Peak, shot.SumPeaks, stageBudget / scale);
                share = Math.Min(share, Math.Max(0f, Math.Min(1f, ratio / shot.Ratio)));
            }
            float q = Math.Min(1f, strength);
            float appliedDb = q * gainDb;
            float appliedShare = share > 0f ? (float)Math.Pow(share, q) : (q >= 1f ? 0f : (float)Math.Pow(1e-3, q));
            float finalScale = shot.Volume * (float)Math.Pow(10.0, appliedDb / 20.0);
            float sumPeak = shot.HasCopy
                ? finalScale * ShotSumRatios.SumPeak(shot.Peak, shot.SumPeaks, appliedShare * shot.Ratio)
                : finalScale * shot.Peak;
            if (!dense) return new ShotBudgetSolution(appliedDb, appliedShare, sumPeak, bound);
            float overDb = (float)(20.0 * Math.Log10(Math.Max(1e-6f, sumPeak) / OutputCeiling));
            float finalLoss = modelLoss ? ShotSumRatios.LimitLossDb(shot.LimitLoss, appliedShare * shot.Ratio, overDb) : 0f;
            return new ShotBudgetSolution(appliedDb, appliedShare, sumPeak, bound, overDb, finalLoss);
        }

        /// <summary>The program limiter's attack loss for the event at this share and gain.</summary>
        private static float LossAt(ShotBudget shot, float share, float gainDb)
        {
            float scale = shot.Volume * (float)Math.Pow(10.0, gainDb / 20.0);
            float peak = scale * ShotSumRatios.SumPeak(shot.Peak, shot.SumPeaks, share * shot.Ratio);
            float overDb = (float)(20.0 * Math.Log10(Math.Max(1e-6f, peak) / OutputCeiling));
            return ShotSumRatios.LimitLossDb(shot.LimitLoss, share * shot.Ratio, overDb);
        }

        /// <summary>A gain in dB as a linear factor; exactly 1 at 0 dB.</summary>
        internal static float Linear(float gainDb) =>
            gainDb == 0f || float.IsNaN(gainDb) ? 1f : (float)Math.Pow(10.0, gainDb / 20.0);

        private static float Finite(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
    }
}
