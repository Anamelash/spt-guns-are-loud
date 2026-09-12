using System.Collections.Generic;
using System.Text;

namespace GunsAreLoud.Client.Runtime
{
    internal enum GalComponentKind
    {
        AutomaticPitched,
        BeatCapture,
        PitchedLayer,
        BeatTimeline,
        ContrastFilter,
        SourceBinding,
        Count
    }

    /// <summary>
    /// How many G.A.L. components live on EFT's pooled audio sources, how many of
    /// them are enabled, and how many distinct sources this mod has touched. All
    /// callers are Unity lifecycle methods and routing hooks on the main thread.
    /// The counters answer one question only: does the attached set keep growing
    /// over a raid, or does it plateau.
    /// </summary>
    internal static class GalSourceCensus
    {
        private static readonly int[] Live = new int[(int)GalComponentKind.Count];
        private static readonly int[] Active = new int[(int)GalComponentKind.Count];
        private static readonly HashSet<int> Sources = new HashSet<int>();

        internal static int LiveCount(GalComponentKind kind) => Live[(int)kind];

        internal static int ActiveCount(GalComponentKind kind) => Active[(int)kind];

        internal static int SourceCount => Sources.Count;

        internal static void Created(GalComponentKind kind) => Live[(int)kind]++;

        internal static void Destroyed(GalComponentKind kind)
        {
            int index = (int)kind;
            if (Live[index] > 0) Live[index]--;
        }

        internal static void Enabled(GalComponentKind kind) => Active[(int)kind]++;

        internal static void Disabled(GalComponentKind kind)
        {
            int index = (int)kind;
            if (Active[index] > 0) Active[index]--;
        }

        // The identity set only grows while the trace is on: an ordinary raid
        // must not accumulate instance ids it will never report.
        internal static void NoteSource(int instanceId)
        {
            if (PerformanceTrace.Enabled) Sources.Add(instanceId);
        }

        internal static void Clear()
        {
            Sources.Clear();
        }

        internal static string Format()
        {
            var text = new StringBuilder("galSources=");
            text.Append(Sources.Count);
            for (int index = 0; index < (int)GalComponentKind.Count; index++)
            {
                if (Live[index] == 0 && Active[index] == 0) continue;
                text.Append(' ').Append(Name((GalComponentKind)index)).Append('=')
                    .Append(Active[index]).Append('/').Append(Live[index]);
            }
            return text.ToString();
        }

        private static string Name(GalComponentKind kind)
        {
            switch (kind)
            {
                case GalComponentKind.AutomaticPitched: return "autoPitchedFilter";
                case GalComponentKind.BeatCapture: return "beatCapture";
                case GalComponentKind.PitchedLayer: return "pitchedLayer";
                case GalComponentKind.BeatTimeline: return "beatTimeline";
                case GalComponentKind.ContrastFilter: return "contrastFilter";
                default: return "sourceBinding";
            }
        }
    }
}
