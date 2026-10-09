using System;
using System.Collections.Generic;
using System.Linq;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Pairing stock mixer groups with the replacement's passive subtree. Runs
    /// against a catalog that answers like Unity's FindMatchingGroups: a group
    /// matches when its full path contains the sub-path. The BTR engine and the
    /// rain blender reference the stock asset directly; whether they reach the
    /// headset depends on this pairing being right and, where it cannot be,
    /// on it saying nothing.
    /// </summary>
    [TestFixture]
    public sealed class StockMixerGroupMapTests
    {
        private sealed class Group
        {
            internal readonly string Path;
            internal Group(string path) { Path = path; }
            internal string Name => Path.Substring(Path.LastIndexOf('/') + 1);
            public override string ToString() => Path;
        }

        private sealed class Catalog : IMixerGroupCatalog<Group>
        {
            private readonly List<Group> _groups;
            internal Catalog(IEnumerable<string> paths) { _groups = paths.Select(p => new Group(p)).ToList(); }
            internal Group At(string path) => _groups.Single(g => g.Path == path);
            public IReadOnlyList<Group> All() => _groups;
            // Unity prefixes every path with the master group's name.
            public IReadOnlyList<Group> Find(string subPath) =>
                _groups.Where(g => ("Master/" + g.Path).Contains(subPath)).ToList();
            public string NameOf(Group group) => group?.Name;
        }

        private static readonly string[] StockPaths =
        {
            "Master", "World",
            "World/NonspatialBypass",
            "World/Guns", "World/Guns/Gunshots", "World/Guns/Instrumental",
            "World/Main", "World/Main/Environment",
            "World/Main/Environment/TechnicalSounds",
            "World/Main/Environment/TechnicalSounds/Vehicles",
            "World/Main/Environment/TechnicalSounds/Vehicles/VehicleOut",
            "World/Main/Environment/TechnicalSounds/Hideout",
            "World/Main/Ambient", "World/Main/Ambient/Rain",
            "World/Main/Ambient/AmbientOut", "World/Main/Ambient/AmbientOut/PrecipitationAmbientOut",
            "World/Occlusion", "World/Occlusion/Occlusion", "World/Occlusion/SimpleOccluded",
            "World/Headphones", "World/Headphones/GunCompressor",
            "World/Voip", "UI", "Music", "Inventory"
        };

        private static IEnumerable<string> ReplacementPaths(IEnumerable<string> stock)
        {
            foreach (string path in stock)
            {
                // The generator wraps these four dry World children in GAL Passive.
                bool passive = path.StartsWith("World/NonspatialBypass", StringComparison.Ordinal) ||
                    path.StartsWith("World/Guns", StringComparison.Ordinal) ||
                    path.StartsWith("World/Main", StringComparison.Ordinal) ||
                    path.StartsWith("World/Occlusion", StringComparison.Ordinal);
                yield return passive ? "World/GAL Passive/" + path.Substring("World/".Length) : path;
            }
            yield return "World/GAL Passive";
            yield return "World/GAL Electronics";
            yield return "World/GAL Passive/Main/Ambient/Rain/GAL Contrast Input";
            yield return "World/GAL Passive/Occlusion/GAL Contrast Input";
            yield return "World/GAL Passive/Occlusion/Occlusion/GAL Contrast Input";
        }

        private static StockMixerGroupMapResult<Group> Build(Catalog stock, Catalog replacement) =>
            StockMixerGroupMapper.Build(stock, replacement);

        [TestCase("World/Main/Environment/TechnicalSounds/Vehicles/VehicleOut")]
        [TestCase("World/Main/Ambient/Rain")]
        [TestCase("World/Main/Ambient/AmbientOut/PrecipitationAmbientOut")]
        [TestCase("World/Main/Environment/TechnicalSounds/Hideout")]
        [TestCase("World/Guns/Gunshots")]
        [TestCase("World/Occlusion/SimpleOccluded")]
        [TestCase("World/NonspatialBypass")]
        public void WorldGroupsPairWithTheirPassiveCounterparts(string stockPath)
        {
            var stock = new Catalog(StockPaths);
            var replacement = new Catalog(ReplacementPaths(StockPaths));

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Available, Is.True, result.Failure);
            Assert.That(result.Pairs.TryGetValue(stock.At(stockPath), out Group mapped), Is.True,
                "A source on this stock group plays outside every headset path until it is moved.");
            Assert.That(mapped.Path, Is.EqualTo("World/GAL Passive/" + stockPath.Substring("World/".Length)));
        }

        [TestCase("Master")]
        [TestCase("World")]
        [TestCase("World/Headphones")]
        [TestCase("World/Headphones/GunCompressor")]
        [TestCase("World/Voip")]
        [TestCase("UI")]
        [TestCase("Music")]
        [TestCase("Inventory")]
        public void GroupsOutsideThePassiveBusAreLeftToTheMenu(string stockPath)
        {
            var stock = new Catalog(StockPaths);
            var replacement = new Catalog(ReplacementPaths(StockPaths));

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Pairs.ContainsKey(stock.At(stockPath)), Is.False,
                "Only what the generator reparented below GAL Passive is a world route; " +
                "the menu's own groups stay on the stock asset.");
            Assert.That(result.Skipped.Any(entry => entry.StartsWith(stock.At(stockPath).Name + ":")), Is.False,
                "An out-of-scope group is not a skipped world route either.");
        }

        [Test]
        public void DuplicateOcclusionNamesResolveByPathNotByName()
        {
            var stock = new Catalog(StockPaths);
            var replacement = new Catalog(ReplacementPaths(StockPaths));

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Pairs[stock.At("World/Occlusion/Occlusion")].Path,
                Is.EqualTo("World/GAL Passive/Occlusion/Occlusion"));
            Assert.That(result.Pairs[stock.At("World/Occlusion")].Path,
                Is.EqualTo("World/GAL Passive/Occlusion"));
            Assert.That(result.Skipped, Is.Empty);
        }

        [Test]
        public void PairsAreKeyedByStockGroupsOnly()
        {
            var stock = new Catalog(StockPaths);
            var replacement = new Catalog(ReplacementPaths(StockPaths));

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Pairs.Keys, Is.SubsetOf(stock.All()),
                "A replacement group of the same name must never be looked up as a stock one.");
            Assert.That(result.Pairs.Values, Is.SubsetOf(replacement.All()));
            Assert.That(result.Pairs.Values.Select(g => g.Path), Is.All.StartsWith("World/GAL Passive/"));
        }

        [Test]
        public void MultiplicityMismatchSkipsThatNameAndKeepsTheRest()
        {
            var stock = new Catalog(StockPaths);
            var replacement = new Catalog(ReplacementPaths(StockPaths)
                .Where(path => path != "World/GAL Passive/Main/Ambient/Rain"));

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Available, Is.True);
            Assert.That(result.Pairs.ContainsKey(stock.At("World/Main/Ambient/Rain")), Is.False);
            Assert.That(result.Skipped, Has.Member("Rain: multiplicity differs"));
            Assert.That(result.Pairs.ContainsKey(
                stock.At("World/Main/Environment/TechnicalSounds/Vehicles/VehicleOut")), Is.True,
                "One unpaired group must not cost the others their route.");
        }

        [Test]
        public void UnresolvableDuplicatesAreSkippedAsAmbiguous()
        {
            string[] stockPaths = StockPaths
                .Concat(new[] { "World/Main/Ambient/Loops", "World/Main/Environment/Loops" }).ToArray();
            var stock = new Catalog(stockPaths);
            var replacement = new Catalog(ReplacementPaths(stockPaths));

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Pairs.ContainsKey(stock.At("World/Main/Ambient/Loops")), Is.False);
            Assert.That(result.Pairs.ContainsKey(stock.At("World/Main/Environment/Loops")), Is.False);
            Assert.That(result.Skipped, Has.Member("Loops: ambiguous"),
                "Two same-named groups with no path to tell them apart would be a coin toss; " +
                "a source moved to the wrong one is worse than one left alone.");
        }

        [Test]
        public void WithoutThePassiveBusTheWholeMapIsAbandoned()
        {
            var stock = new Catalog(StockPaths);
            var replacement = new Catalog(StockPaths);

            StockMixerGroupMapResult<Group> result = Build(stock, replacement);

            Assert.That(result.Available, Is.False);
            Assert.That(result.Failure, Does.Contain("passive bus"));
            Assert.That(result.Pairs, Is.Empty);
        }

        [Test]
        public void MissingCatalogFailsOpen()
        {
            StockMixerGroupMapResult<Group> result = StockMixerGroupMapper.Build<Group>(null, null);

            Assert.That(result.Available, Is.False);
            Assert.That(result.Pairs, Is.Empty);
        }

        [Test]
        public void DisambiguationPathsComeFromTheContrastTableBelowThePassiveBus()
        {
            string[] paths = StockMixerGroupMapper.DefaultDisambiguationPaths().ToArray();

            Assert.That(paths, Is.Not.Empty);
            Assert.That(paths, Is.All.StartsWith(StockMixerGroupMapper.PassiveBusPrefix));
            Assert.That(paths, Has.Member("World/GAL Passive/Occlusion/Occlusion"));
            Assert.That(StockMixerGroupMapper.StockPathOf("World/GAL Passive/Occlusion/Occlusion"),
                Is.EqualTo("World/Occlusion/Occlusion"));
            Assert.That(StockMixerGroupMapper.StockPathOf("Inventory"), Is.EqualTo("Inventory"));
        }
    }
}
