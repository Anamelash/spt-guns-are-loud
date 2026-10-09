using System;
using System.Collections.Generic;
using System.Linq;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// The groups of one mixer, as the runtime can see them: every group, the
    /// groups whose full path contains a given sub-path, and a group's name.
    /// Unity's <c>AudioMixer.FindMatchingGroups</c> is the only query the player
    /// offers, so the mapping is written against exactly that and nothing more.
    /// </summary>
    internal interface IMixerGroupCatalog<TGroup>
    {
        IReadOnlyList<TGroup> All();
        IReadOnlyList<TGroup> Find(string subPath);
        string NameOf(TGroup group);
    }

    internal sealed class StockMixerGroupMapResult<TGroup>
    {
        internal StockMixerGroupMapResult(IReadOnlyDictionary<TGroup, TGroup> pairs,
            IReadOnlyList<string> skipped, string failure)
        {
            Pairs = pairs;
            Skipped = skipped;
            Failure = failure;
        }

        /// <summary>Stock group to its counterpart under the passive bus.</summary>
        internal IReadOnlyDictionary<TGroup, TGroup> Pairs { get; }

        /// <summary>World groups that could not be paired, with the reason.</summary>
        internal IReadOnlyList<string> Skipped { get; }

        /// <summary>Non-null when the whole map was abandoned.</summary>
        internal string Failure { get; }

        internal bool Available => Failure == null;
    }

    /// <summary>
    /// Pairs each stock mixer group with the same-named group of the replacement
    /// mixer, limited to the groups the generator reparented below the passive
    /// bus. Those are the world sounds that have to pass through a headset.
    /// <para>
    /// The stock contract guarantees every stock name survives in the replacement
    /// with the same multiplicity, so a name that is unique on both sides is its
    /// own proof. Names that appear more than once (the stock graph carries both
    /// <c>Occlusion</c> and <c>Occlusion/Occlusion</c>) are resolved through
    /// explicit sub-paths, deepest first, the way the contrast router resolves
    /// its inputs. Anything still ambiguous is left alone: a source that stays
    /// on the stock mixer sounds as it does today, a source moved to the wrong
    /// group would not.
    /// </para>
    /// </summary>
    internal static class StockMixerGroupMapper
    {
        internal const string PassiveBusName = "GAL Passive";
        internal const string PassiveBusPrefix = "World/" + PassiveBusName + "/";
        internal const string StockWorldPrefix = "World/";

        /// <summary>
        /// Replacement-side sub-paths that tell same-named groups apart. The
        /// contrast route table already lists every direct-source parent below
        /// the passive bus; the stock-side path is the same without the bus.
        /// </summary>
        internal static IEnumerable<string> DefaultDisambiguationPaths() =>
            GunshotContrastMixerRouteTable.Routes
                .Select(route => route.ParentPath)
                .Where(path => path.StartsWith(PassiveBusPrefix, StringComparison.Ordinal));

        internal static string StockPathOf(string replacementPath) =>
            replacementPath.StartsWith(PassiveBusPrefix, StringComparison.Ordinal)
                ? StockWorldPrefix + replacementPath.Substring(PassiveBusPrefix.Length)
                : replacementPath;

        internal static StockMixerGroupMapResult<TGroup> Build<TGroup>(
            IMixerGroupCatalog<TGroup> stock, IMixerGroupCatalog<TGroup> replacement,
            IEnumerable<string> disambiguationPaths = null)
        {
            var pairs = new Dictionary<TGroup, TGroup>();
            var skipped = new List<string>();
            if (stock == null || replacement == null)
                return new StockMixerGroupMapResult<TGroup>(pairs, skipped, "mixer catalog unavailable");

            // Only what hangs below the passive bus is in scope. If the lookup
            // cannot see that subtree, the mapping has nothing safe to say.
            var passive = new HashSet<TGroup>(replacement.Find(PassiveBusName)
                .Where(group => group != null && replacement.NameOf(group) != PassiveBusName));
            if (passive.Count == 0)
                return new StockMixerGroupMapResult<TGroup>(pairs, skipped, "passive bus subtree not found");

            // A stock group below World with no counterpart at all is still a
            // world route the player would miss, so it is reported, not ignored.
            var stockWorld = new HashSet<TGroup>(stock.Find(StockWorldPrefix).Where(group => group != null));
            Dictionary<string, List<TGroup>> stockByName = ByName(stock);
            Dictionary<string, List<TGroup>> replacementByName = ByName(replacement);
            var claimedStock = new HashSet<TGroup>();
            var claimedReplacement = new HashSet<TGroup>();

            foreach (string path in (disambiguationPaths ?? DefaultDisambiguationPaths())
                .Distinct().OrderByDescending(value => value.Length))
            {
                string name = LastSegment(path);
                if (!replacementByName.TryGetValue(name, out List<TGroup> candidates) || candidates.Count < 2)
                    continue; // unique names need no path
                TGroup[] stockMatches = stock.Find(StockPathOf(path))
                    .Where(group => group != null && stock.NameOf(group) == name && !claimedStock.Contains(group))
                    .ToArray();
                TGroup[] replacementMatches = replacement.Find(path)
                    .Where(group => group != null && replacement.NameOf(group) == name &&
                                    !claimedReplacement.Contains(group))
                    .ToArray();
                if (stockMatches.Length != 1 || replacementMatches.Length != 1) continue;
                claimedStock.Add(stockMatches[0]);
                claimedReplacement.Add(replacementMatches[0]);
                if (passive.Contains(replacementMatches[0]))
                    pairs[stockMatches[0]] = replacementMatches[0];
            }

            foreach (KeyValuePair<string, List<TGroup>> entry in stockByName)
            {
                string name = entry.Key;
                replacementByName.TryGetValue(name, out List<TGroup> counterparts);
                List<TGroup> stockLeft = entry.Value.Where(group => !claimedStock.Contains(group)).ToList();
                List<TGroup> replacementLeft = (counterparts ?? new List<TGroup>())
                    .Where(group => !claimedReplacement.Contains(group)).ToList();
                if (stockLeft.Count == 0) continue;
                bool inScope = replacementLeft.Any(passive.Contains) ||
                               (counterparts == null && stockLeft.Any(stockWorld.Contains));
                if (counterparts == null || counterparts.Count != entry.Value.Count)
                {
                    // Outside the passive bus a missing or extra group is the
                    // menu's business, not a world route the player would miss.
                    if (inScope) skipped.Add(name + ": multiplicity differs");
                    continue;
                }
                if (stockLeft.Count != 1 || replacementLeft.Count != 1)
                {
                    if (inScope) skipped.Add(name + ": ambiguous");
                    continue;
                }
                if (!passive.Contains(replacementLeft[0])) continue;
                pairs[stockLeft[0]] = replacementLeft[0];
            }

            return new StockMixerGroupMapResult<TGroup>(pairs, skipped, null);
        }

        private static Dictionary<string, List<TGroup>> ByName<TGroup>(IMixerGroupCatalog<TGroup> catalog)
        {
            var result = new Dictionary<string, List<TGroup>>(StringComparer.Ordinal);
            foreach (TGroup group in catalog.All())
            {
                if (group == null) continue;
                string name = catalog.NameOf(group) ?? "";
                if (!result.TryGetValue(name, out List<TGroup> list)) result[name] = list = new List<TGroup>();
                list.Add(group);
            }
            return result;
        }

        private static string LastSegment(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator < 0 ? path : path.Substring(separator + 1);
        }
    }
}
