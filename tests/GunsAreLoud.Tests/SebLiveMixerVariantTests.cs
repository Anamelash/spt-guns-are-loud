using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Integrations.Seb;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// This mod's variant of Sound Engine Backport's live mixer: how it is
    /// found, checked and installed, and what the headset route owns on it.
    /// </summary>
    [TestFixture]
    public sealed class SebLiveMixerVariantTests
    {
        private const string Sordin = "5aa2ba71e5b5b000137b758f";

        [Test]
        public void SebsBypassLookupIsRedirectedOnlyOnTheVariant()
        {
            HeadphoneMixerVariant live = SebMixerHost.LiveVariant, stock = HeadphoneMixerAsset.StockVariant;
            Assert.That(HeadphoneMixerAsset.MapLookup("World/Bypass", live), Is.EqualTo("GAL Passive/Bypass"));
            Assert.That(HeadphoneMixerAsset.MapLookup("World/Bypass", stock), Is.EqualTo("World/Bypass"));
            Assert.That(HeadphoneMixerAsset.MapLookup("World/Bypass", null), Is.EqualTo("World/Bypass"));
            Assert.That(HeadphoneMixerAsset.MapLookup("NonspatialBypass", live), Is.EqualTo("NonspatialBypass"),
                "Only SEB's rewritten path is ours to redirect; SEB's own prefix runs first.");
            Assert.That(HeadphoneMixerAsset.MapLookup("Guns/Gunshots", live), Is.EqualTo("Guns/Gunshots"));
            HarmonyAfter after = typeof(SebLiveMixerLookupPatch).GetCustomAttribute<HarmonyAfter>();
            Assert.That(after?.info.after, Is.SupersetOf(SebIds.KnownGuids));
        }

        [Test]
        public void LookupMismatchNamesTheGroupItLandedOn()
        {
            Assert.That(HeadphoneMixerAsset.DescribeLookupMismatch("Guns/Gunshots", "Gunshots", "Gunshots", "Gunshots"),
                Is.Null);
            Assert.That(HeadphoneMixerAsset.DescribeLookupMismatch("Guns/Gunshots", "Gunshots", "Gunshots",
                "GunshotsVehicleIn"), Is.EqualTo("Guns/Gunshots resolves to GunshotsVehicleIn instead of Gunshots"));
            Assert.That(HeadphoneMixerAsset.DescribeLookupMismatch("NonspatialBypass", "Bypass", "Bypass", null),
                Is.EqualTo("NonspatialBypass resolves to nothing"));
            Assert.That(HeadphoneMixerAsset.DescribeLookupMismatch("World", "World", "WorldSFX", "WorldSFX"),
                Is.EqualTo("World resolves to WorldSFX instead of World"));
        }

        [Test]
        public void ParityCoversEveryLookupBetterAudioMakes()
        {
            var lookups = new HashSet<string>();
            foreach (MethodBase method in MethodsOf(typeof(BetterAudio)))
            {
                string pending = null;
                Walk(method, (opcode, token) =>
                {
                    if (opcode == OpCodes.Ldstr) { pending = method.Module.ResolveString(token); return; }
                    if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && pending != null)
                    {
                        MethodBase called = null;
                        try { called = method.Module.ResolveMethod(token); } catch (Exception) { }
                        if (called?.Name == nameof(BetterAudio.FindMixerGroup)) lookups.Add(pending);
                    }
                    pending = null;
                });
            }
            Assert.That(lookups, Is.EquivalentTo(HeadphoneMixerAsset.BetterAudioLookups),
                "BetterAudio's mixer lookups changed: the parity check must cover exactly these.");
        }

        [Test]
        public void LiveRoutesExtendTheStockTableWithSebsOwnGroups()
        {
            ContrastRouteSpec[] stock = GunshotContrastMixerRouteTable.Routes;
            ContrastRouteSpec[] live = GunshotContrastMixerRouteTable.LiveRoutes;
            Assert.That(live.Take(stock.Length).Select(r => r.Parameter), Is.EqualTo(stock.Select(r => r.Parameter)));
            Assert.That(live, Has.Length.EqualTo(stock.Length + GunshotContrastMixerRouteTable.LiveOnlyRoutes.Length));
            Assert.That(live.Select(r => r.Parameter).Distinct().Count(), Is.EqualTo(live.Length));
            Assert.That(live.Select(r => r.ParentPath).Distinct().Count(), Is.EqualTo(live.Length));
            foreach (ContrastRouteSpec route in GunshotContrastMixerRouteTable.LiveOnlyRoutes)
            {
                Assert.That(route.ParentPath, Does.StartWith("World/GAL Passive/"));
                Assert.That(GunshotContrastModel.ShouldAttenuate(route.ParentName), Is.True, route.ParentName);
            }
            Assert.That(GunshotContrastMixerRouteTable.For(false), Is.SameAs(stock));
            Assert.That(GunshotContrastMixerRouteTable.For(true), Is.SameAs(live));
        }

        [Test]
        public void StockBypassPairsWithTheLiveMixersRenamedGroup()
        {
            var stock = new Catalog(new[] { "Master", "World", "World/NonspatialBypass", "World/Guns",
                "World/Guns/Gunshots", "World/Occlusion", "World/Occlusion/Occlusion" });
            var live = new Catalog(new[] { "Master", "World", "World/GAL Passive", "World/GAL Passive/Bypass",
                "World/GAL Passive/Guns", "World/GAL Passive/Guns/Gunshots", "World/GAL Passive/Guns/Impacts",
                "World/GAL Passive/Occlusion", "World/GAL Passive/Occlusion/Occlusion", "World/GAL Electronics" });
            var result = StockMixerGroupMapper.Build(stock, live,
                StockMixerGroupMapper.DefaultDisambiguationPaths(GunshotContrastMixerRouteTable.LiveRoutes),
                SebMixerHost.LiveVariant.StockAliases);
            Assert.That(result.Available, Is.True, result.Failure);
            Assert.That(result.Pairs[stock.At("World/NonspatialBypass")], Is.SameAs(live.At("World/GAL Passive/Bypass")));
            Assert.That(result.Pairs[stock.At("World/Occlusion/Occlusion")],
                Is.SameAs(live.At("World/GAL Passive/Occlusion/Occlusion")));
            Assert.That(result.Skipped, Is.Empty);

            var unaliased = StockMixerGroupMapper.Build(stock, live);
            Assert.That(unaliased.Pairs.ContainsKey(stock.At("World/NonspatialBypass")), Is.False,
                "Without the alias the renamed group must stay unpaired, never guessed.");
        }

        [Test]
        public void VariantsDifferOnlyWhereTheMixersDo()
        {
            HeadphoneMixerVariant stock = HeadphoneMixerAsset.StockVariant, live = SebMixerHost.LiveVariant;
            Assert.That(stock.RequiredGroupPaths, Does.Contain("World/GAL Passive/NonspatialBypass"));
            Assert.That(live.RequiredGroupPaths, Does.Contain("World/GAL Passive/Bypass"));
            Assert.That(stock.BridgeGlobalControls && !live.BridgeGlobalControls, Is.True,
                "SEB chains its mixer into the stock one and mirrors the global controls itself.");
            Assert.That(live.RejectOnLookupMismatch, Is.True);
            Assert.That(live.OriginalBypassLookup, Is.EqualTo(SebMixerHost.SebBypassLookup));
            Assert.That(live.CandidateBypassLookup, Is.EqualTo(SebMixerHost.VariantBypassLookup));
            Assert.That(stock.Routes, Is.SameAs(GunshotContrastMixerRouteTable.Routes));
            Assert.That(live.Routes, Is.SameAs(GunshotContrastMixerRouteTable.LiveRoutes));
        }

        [Test]
        public void LiveContractIsTheStockContractPlusSebsParameters()
        {
            string[] stock = Contract("GunsAreLoud.Client.Assets.headphone-mixer-stock-contract.txt");
            string[] live = Contract(SebMixerHost.LiveVariant.ContractResource);
            string[] liveParameters = live.Where(l => l.StartsWith("parameter|")).Select(l => l.Substring(10)).ToArray();
            Assert.That(liveParameters, Has.Length.EqualTo(SebMixerHost.LiveVariant.ContractParameters));
            Assert.That(live.Where(l => l.StartsWith("snapshot|")), Is.EquivalentTo(
                new[] { "snapshot|Indoor", "snapshot|Outdoor", "snapshot|Bunker" }));
            // Names are CRC aliases; the mixer resolves a name by its CRC.
            var liveHashes = new HashSet<uint>(liveParameters.Select(Crc32));
            Assert.That(liveHashes, Has.Count.EqualTo(liveParameters.Length));
            foreach (string name in stock.Where(l => l.StartsWith("parameter|")).Select(l => l.Substring(10)))
                Assert.That(liveHashes.Contains(Crc32(name)), Is.True, "stock parameter missing from live: " + name);
            foreach (string name in new[] { "AmbientVolume", "PrecipitationInVolume", "PrecipitationOutVolume",
                "MasterVolume", "GunsVolume", "HeadphonesMixerVolume" })
                Assert.That(liveHashes.Contains(Crc32(name)), Is.True, name);
        }

        [Test]
        public void RealisticOnSebsMixerKeepsTheAmbienceTrimAndOwnsPrecipitation()
        {
            var store = new Store();
            store.Values["AmbientVolume"] = -12f;
            store.Values["PrecipitationInVolume"] = -5f;
            store.Values["PrecipitationOutVolume"] = -5f;
            float trim = -4f;
            var overrides = new HeadphoneRouteOverrides
            {
                AmbientVolumeDb = () => trim,
                NeutralParameters = new[] { "PrecipitationInVolume", "PrecipitationOutVolume" }
            };
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, Fits.Instance, overrides);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);

            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out string reason), Is.True, reason);
            Assert.That(store.Values["AmbientVolume"], Is.EqualTo(-4f));
            Assert.That(store.Values["PrecipitationInVolume"], Is.Zero);
            Assert.That(store.Values["PrecipitationOutVolume"], Is.Zero);
            Assert.That(route.TryFindDrift(out _, out _, out _), Is.False);

            store.Values["AmbientVolume"] = -16f; // SEB's own write with the headset template
            Assert.That(route.TryFindDrift(out string drifted, out float expected, out _), Is.True);
            Assert.That(drifted, Is.EqualTo("AmbientVolume"));
            Assert.That(expected, Is.EqualTo(-4f));

            trim = -10f;
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out reason), Is.True, reason);
            Assert.That(store.Values["AmbientVolume"], Is.EqualTo(-10f), "a moved trim reaches the mixer");

            Assert.That(route.TryRestore(out reason), Is.True, reason);
            Assert.That(store.Values["AmbientVolume"], Is.EqualTo(-12f));
            Assert.That(store.Values["PrecipitationInVolume"], Is.EqualTo(-5f));
            Assert.That(store.Values["PrecipitationOutVolume"], Is.EqualTo(-5f));
        }

        [Test]
        public void StockRouteStillOwnsAmbientAtZeroAndLeavesPrecipitationAlone()
        {
            var store = new Store();
            store.Values["AmbientVolume"] = -12f;
            store.Values["PrecipitationInVolume"] = -5f;
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, Fits.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out string reason), Is.True, reason);
            Assert.That(store.Values["AmbientVolume"], Is.Zero);
            Assert.That(store.Values["PrecipitationInVolume"], Is.EqualTo(-5f));
        }

        [Test]
        public void BothMixerVariantsShipInTheClient()
        {
            Assert.That(HeadphoneMixerAsset.VariantEmbedded(SebMixerHost.LiveVariant), Is.True);
            foreach (string resource in new[] { HeadphoneMixerAsset.ResourceName, SebMixerHost.LiveResourceName })
                using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(resource))
                {
                    Assert.That(stream, Is.Not.Null, resource);
                    Assert.That(stream.Length, Is.GreaterThan(10000), resource);
                }
        }

        [Test]
        public void SebIntegrationIsBlockedWithAReasonWhenAbsent()
        {
            if (SebModule.Present) Assert.Ignore("Detection ran with SEB present.");
            Assert.That(SebModule.Mode, Is.EqualTo(SebMode.Absent));
            Assert.That(SebModule.PredictsIntegration, Is.False);
            Assert.That(SebModule.IntegrationBlocker, Does.Contain("absent"));
        }

        [Test]
        public void InstallPatchIsGatedOnEveryMemberItTouches()
        {
            MethodInfo prepare = AccessTools.Method(typeof(SebLiveMixerInstallPatch), "Prepare");
            Assert.That(prepare.Invoke(null, null), Is.EqualTo(SebModule.Present && SebModule.InstallHookAvailable));
        }

        // --- Against the installed SEB ---------------------------------------

        private static string SebDirectory()
        {
            string game = typeof(SebLiveMixerVariantTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "SptGameDir")?.Value;
            string directory = string.IsNullOrEmpty(game) ? null
                : Path.Combine(game, "BepInEx", "plugins", "SoundEngineBackport");
            if (directory == null || !Directory.Exists(directory))
                Assert.Ignore("Sound Engine Backport is not installed in the game folder.");
            return directory;
        }

        [Test]
        public void TheInstalledLiveMixerIsTheOneTheVariantWasBuiltFrom()
        {
            string bundle = Path.Combine(SebDirectory(), SebIds.BundleFileName);
            Assert.That(SebModule.TryHash(bundle), Is.EqualTo(SebIds.SupportedLiveBundleSha256),
                "SEB's live mixer changed: regenerate the live variant (build/headphones/README.md) and pin the new hash.");
        }

        [Test]
        public void SebRewritesTheBypassLookupToThePathThisModRedirects()
        {
            Assembly seb = Assembly.Load(File.ReadAllBytes(Path.Combine(SebDirectory(), "SoundEngineBackportClient.dll")));
            Type mixer = seb.GetType(SebIds.LiveMixerTypeName, true);
            var strings = new List<string>();
            MethodInfo map = AccessTools.Method(mixer, "MapGroupName");
            Walk(map, (opcode, token) => { if (opcode == OpCodes.Ldstr) strings.Add(map.Module.ResolveString(token)); });
            Assert.That(strings, Does.Contain(SebMixerHost.SebBypassLookup));
            Assert.That(strings, Does.Contain("NonspatialBypass"));
        }

        // --- helpers -----------------------------------------------------------

        private static string[] Contract(string resource)
        {
            using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(resource))
            {
                Assert.That(stream, Is.Not.Null, resource);
                using (var reader = new StreamReader(stream))
                    return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static uint Crc32(string value)
        {
            uint crc = 0xffffffffu;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(value))
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            return ~crc;
        }

        private static IEnumerable<MethodBase> MethodsOf(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (MethodBase method in type.GetMethods(flags)) yield return method;
            foreach (MethodBase method in type.GetConstructors(flags)) yield return method;
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (MethodBase method in MethodsOf(nested)) yield return method;
        }

        private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null)).OfType<OpCode>()
            .ToDictionary(opcode => opcode.Value, opcode => opcode);

        private static void Walk(MethodBase method, Action<OpCode, int> visit)
        {
            byte[] il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception) { return; }
            if (il == null) return;
            int offset = 0;
            while (offset < il.Length)
            {
                short value = il[offset++];
                if (value == 0xfe) value = (short)(0xfe00 | il[offset++]);
                if (!Opcodes.TryGetValue(value, out OpCode opcode)) return;
                int size;
                switch (opcode.OperandType)
                {
                    case OperandType.InlineNone: size = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: size = 1; break;
                    case OperandType.InlineVar: size = 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: size = 8; break;
                    case OperandType.InlineSwitch: size = 4 + BitConverter.ToInt32(il, offset) * 4; break;
                    default: size = 4; break;
                }
                visit(opcode, size == 4 && opcode.OperandType != OperandType.InlineSwitch
                    ? BitConverter.ToInt32(il, offset) : 0);
                offset += size;
            }
        }

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
            public IReadOnlyList<Group> Find(string subPath) =>
                _groups.Where(g => ("Master/" + g.Path).Contains(subPath)).ToList();
            public string NameOf(Group group) => group?.Name;
        }

        private sealed class Fits : IHeadphoneNativeEqProvider
        {
            internal static readonly Fits Instance = new Fits();
            public bool TryGet(HeadsetProfile profile, int sampleRate, out HeadphoneNativeEqFit fit)
            {
                fit = HeadphoneNativeEqFit.Calculate(profile.Passive, sampleRate);
                return true;
            }
        }

        private sealed class Store : IMixerParameterStore
        {
            internal readonly Dictionary<string, float> Values = new Dictionary<string, float>();
            public bool TryGet(string name, out float value)
            {
                if (!Values.TryGetValue(name, out value)) Values[name] = value = 0f;
                return true;
            }
            public bool TrySet(string name, float value) { Values[name] = value; return true; }
        }
    }
}
