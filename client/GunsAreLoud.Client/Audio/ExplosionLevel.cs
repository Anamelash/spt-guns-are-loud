using System.Collections.Generic;
using EFT;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// The explosion level correction: the voice EFT borrows for an explosion
    /// is held to the shot level scale by its distance. The engine's own
    /// volume, its distance blend and its rolloff are read from the voice as
    /// it is about to play; the recording's 240 ms level comes from the same
    /// store as the shots'. One gain goes on both channels of the voice, in
    /// the same gain stage the local shot uses, and is taken off when the voice
    /// is returned. Main thread only.
    /// </summary>
    internal static class ExplosionLevel
    {
        private static readonly Dictionary<int, List<DirectLevelGainFilter>> Filters =
            new Dictionary<int, List<DirectLevelGainFilter>>();
        private static readonly Stack<List<DirectLevelGainFilter>> SpareLists = new Stack<List<DirectLevelGainFilter>>();
        private static readonly List<AudioSource> Sources = new List<AudioSource>(2);

        /// <summary>The last decision, for tests and inspection.</summary>
        internal static DirectLevelDecision LastDecision { get; private set; }

        internal static void Apply(SoundBank bank, float distance, BetterSource voice)
        {
            ModConfig config = Plugin.ModConfig;
            if (config == null || !config.Enabled.Value || voice == null) return;
            // A pooled voice: whatever the previous explosion left on it goes first.
            Release(voice);
            TuningSnapshot tuning = config.GetTuning();
            if (!tuning.ExplosionLevelEnabled) return;
            Player player = AudioRuntimeLookup.Audio?.ListenerPlayer;
            bool listenerIndoor = player != null && player.Environment == EnvironmentType.Indoor;
            float userGainDb = Engine.SoundEngine.Shots.UserGainDb;
            float targetDb = ExplosionLevelModel.TargetDb(distance, listenerIndoor, userGainDb);
            float strength = DirectLevelModel.Strength(tuning.DirectLevelNormalizationDb);

            voice.GetComponentsInChildren(true, Sources);
            ShotBudget combined = default;
            bool known = false;
            string source = DirectLevelStore.NoSource;
            string clips = string.Empty;
            foreach (AudioSource audio in Sources)
            {
                if (audio == null || audio.clip == null || !(audio.volume > 0f)) continue;
                float rolloff = Rolloff(audio, distance);
                float spatial = ExplosionLevelModel.SpatialGain(audio.spatialBlend, rolloff);
                float volume = audio.volume * spatial;
                bool measured = DirectLevelStore.TryGetBody(audio.clip.GetInstanceID(),
                    out float bodyDb, out float peakDb, out string clipSource);
                clips += $" clip={audio.clip.name} volume={audio.volume:0.000} blend={audio.spatialBlend:0.00} " +
                    $"rolloff={rolloff:0.0000}({audio.rolloffMode},{audio.minDistance:0.#}-{audio.maxDistance:0.#}m) " +
                    $"k240={(measured ? bodyDb.ToString("0.0") : "n/a")}dB peak={(measured ? peakDb.ToString("0.0") : "n/a")}dBFS source={clipSource}";
                if (!measured) continue;
                var budget = new ShotBudget(bodyDb, DirectLevelModel.Linear(peakDb), volume);
                combined = known ? DirectLevel.Combine(combined, budget) : budget;
                if (!known) source = clipSource;
                known = true;
            }
            Sources.Clear();

            DirectLevelDecision decision;
            if (known)
            {
                // No copy and no density: the recordings are dense already, so
                // the limiter is a safety here and the budget is the plain one.
                ShotBudgetSolution solution = DirectLevelModel.Solve(combined, targetDb, strength, 0f,
                    DirectLevelModel.PeakBudget, 0f, ExplosionLevelModel.MaximumLiftDb);
                decision = new DirectLevelDecision(DirectLevelModel.PlayingLevelDb(combined.AttackDb, combined.Volume),
                    targetDb, solution.GainDb, source, solution.CopyShare, solution.SumPeak, solution.BudgetBound);
                float gain = DirectLevelModel.Linear(decision.GainDb);
                List<DirectLevelGainFilter> filters = SpareLists.Count > 0 ? SpareLists.Pop() : new List<DirectLevelGainFilter>(2);
                voice.GetComponentsInChildren(true, Sources);
                foreach (AudioSource audio in Sources)
                {
                    if (audio == null) continue;
                    DirectLevelGainFilter filter = audio.GetComponent<DirectLevelGainFilter>();
                    if (filter == null) filter = audio.gameObject.AddComponent<DirectLevelGainFilter>();
                    filter.SetGain(gain);
                    filters.Add(filter);
                }
                Sources.Clear();
                Filters[voice.GetInstanceID()] = filters;
            }
            else decision = new DirectLevelDecision(float.NaN, targetDb, 0f, source);
            LastDecision = decision;

            if (!DetailedDiagnostics.TryBeginToken(DiagnosticEventKind.Grenade, out DiagnosticShotToken token)) return;
            float delay = voice is SuperSourceDistant distant ? distant.Delay : Mathf.Max(0f, distance) / 340f;
            int probe = Plugin.Runtime != null ? Plugin.Runtime.ArmExplosionProbe(token, delay) : 0;
            DetailedDiagnostics.Commit(token, DiagnosticEventKind.Grenade,
                $"explosion level bank={bank?.name} distance={distance:0.0}m listenerIndoor={listenerIndoor} " +
                $"L={ExplosionLevelModel.LevelIndex(distance):0.0} target={targetDb:0.0}dB userGain={userGainDb:0.0}dB " +
                $"M={(float.IsNaN(decision.PlayingDb) ? "n/a" : decision.PlayingDb.ToString("0.0"))}dB " +
                $"gain={decision.GainDb:0.0}dB budgetBound={decision.BudgetBound} " +
                $"peak={(float.IsNaN(decision.SumPeak) ? "n/a" : decision.SumPeak.ToString("0.000"))} " +
                $"strength={strength:0.00} source={decision.Source} delay={delay:0.000}s masterProbe={probe}{clips}");
        }

        /// <summary>Takes the correction off a voice that is returned or reused.</summary>
        internal static void Release(BetterSource voice)
        {
            if (voice == null || Filters.Count == 0) return;
            int key = voice.GetInstanceID();
            if (!Filters.TryGetValue(key, out List<DirectLevelGainFilter> filters)) return;
            Filters.Remove(key);
            foreach (DirectLevelGainFilter filter in filters)
                if (filter != null) filter.SetGain(1f);
            filters.Clear();
            SpareLists.Push(filters);
        }

        /// <summary>
        /// The engine's distance attenuation of this voice at this distance:
        /// its rolloff curve read from the source, evaluated as Unity does.
        /// </summary>
        internal static float Rolloff(AudioSource audio, float distance)
        {
            switch (audio.rolloffMode)
            {
                case AudioRolloffMode.Custom:
                {
                    AnimationCurve curve = audio.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    if (curve == null || curve.length == 0) return 1f;
                    float maximum = Mathf.Max(1e-3f, audio.maxDistance);
                    return Mathf.Clamp01(curve.Evaluate(Mathf.Clamp01(Mathf.Max(0f, distance) / maximum)));
                }
                case AudioRolloffMode.Linear:
                    return ExplosionLevelModel.RolloffGain(false, audio.minDistance, audio.maxDistance, distance);
                default:
                    return ExplosionLevelModel.RolloffGain(true, audio.minDistance, audio.maxDistance, distance);
            }
        }

        internal static void Clear()
        {
            Filters.Clear();
            SpareLists.Clear();
        }
    }
}
