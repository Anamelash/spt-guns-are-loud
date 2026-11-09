using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GunsAreLoud.Client.Engine;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// One mixer the headphone route can be built on, with everything that
    /// differs between them: the game's own (stock), or the variant a sound
    /// engine module supplies for the mixer it installs.
    /// </summary>
    internal sealed class HeadphoneMixerVariant
    {
        internal string Name;
        internal string ResourceName;
        internal string AssetPath;
        internal string ContractResource;
        internal int ContractParameters;
        internal string BypassGroup;
        internal ContrastRouteSpec[] Routes;
        internal IReadOnlyDictionary<string, string> StockAliases;
        // The stock mixer is the game's last stage in both cases. Ours replaces
        // it in the stock variant, so the global controls have to be bridged;
        // SEB chains its live mixer into the stock one and mirrors them itself.
        internal bool BridgeGlobalControls;
        // How EFT's lookup "NonspatialBypass" arrives at this mixer, and what
        // it has to be on this mixer to find the same group.
        internal string OriginalBypassLookup;
        internal string CandidateBypassLookup;
        internal bool RejectOnLookupMismatch;

        internal string[] RequiredGroupPaths => new[] {
            "World/GAL Passive/" + BypassGroup, "World/GAL Passive/Guns/Gunshots",
            "World/GAL Passive/Main/Environment", "World/GAL Passive/Occlusion/SimpleOccluded",
            "World/Headphones/GunCompressor", "World/GAL Electronics" };
    }

    // Loaded before BetterAudio constructs its pools. Never swap group identities
    // when the user changes the mode in an existing raid.
    internal static class HeadphoneMixerAsset
    {
        internal const string ResourceName = "GunsAreLoud.Client.Assets.gal_headphone_mixer";
        internal const string AssetPath = "Assets/GunsAreLoud/Audio/MasterMixer.mixer";

        internal static readonly HeadphoneMixerVariant StockVariant = new HeadphoneMixerVariant {
            Name = "stock", ResourceName = ResourceName, AssetPath = AssetPath,
            ContractResource = "GunsAreLoud.Client.Assets.headphone-mixer-stock-contract.txt",
            ContractParameters = 104, BypassGroup = "NonspatialBypass",
            Routes = GunshotContrastMixerRouteTable.Routes, StockAliases = null,
            BridgeGlobalControls = true,
            OriginalBypassLookup = "NonspatialBypass", CandidateBypassLookup = "NonspatialBypass",
            // On the stock mixer a lookup mismatch is reported, not rejected.
            RejectOnLookupMismatch = false
        };


        /// <summary>
        /// Every group path BetterAudio looks up when it creates the raid's
        /// sound managers. <c>FindMixerGroup</c> takes the first group whose
        /// path contains the string, so a variant must answer each of them with
        /// the group the original mixer answers with.
        /// </summary>
        internal static readonly string[] BetterAudioLookups =
        {
            "Ambient/AmbCommonEffects", "AmbientIn", "AmbientIn/CommonAmbInEffects", "AmbientOut",
            "AmbientOut/CommonAmbOutEffects", "ClientPlayer/ClientPlayerMovement",
            "ClientPlayer/ClientPlayerSelfSpeechReverb", "ClientPlayer/ClientPlayerSpeech", "Guns/Gunshots",
            "Guns/Instrumental", "Guns/Occluded", "LowerOccluded", "Main/Environment/CommonSounds",
            "Main/Environment/NatureSounds", "Main/Environment/TechnicalSounds",
            "Main/Environment/TechnicalSounds/Vehicles/VehicleIn", "Main/Environment/TechnicalSounds/Vehicles/VehicleOut",
            "Main/Mute", "Master", "NPC/VehicleInSpeech", "NonspatialBypass", "ObservedPlayer/ObservedPlayerMovement",
            "ObservedPlayer/ObservedPlayerSpeech", "Occlusion/SimpleOccluded", "OutEnvironment", "UpperOccluded",
            "Voip", "World"
        };

        private static AssetBundle _bundle;
        private static AudioMixer _mixer;
        private static bool _attempted;
        private static HeadphoneGlobalControlBridge _globalControls;
        internal static GunshotContrastMixerRouter ContrastRoutes { get; private set; }
        /// <summary>
        /// Stock world group to replacement group, for sources whose prefab
        /// references the stock asset directly. Null when the replacement did not
        /// load or the pairing was abandoned; those sources then stay where they
        /// are, as they did before the map existed.
        /// </summary>
        internal static StockMixerGroupMap StockGroupMap { get; private set; }
        /// <summary>The variant loaded, or null.</summary>
        internal static HeadphoneMixerVariant ActiveVariant { get; private set; }
        /// <summary>The variant loaded is the mixer host's, not the stock one.</summary>
        internal static bool HostVariantActive =>
            _mixer != null && ActiveVariant != null && !ReferenceEquals(ActiveVariant, StockVariant);
        private static float _nextControlLog;
        private static float _nextControlSync;
        private static string _lastControlLog;
        // Twelve mixer parameter reads and writes. A menu slider does not need
        // them every frame; ten times a second still tracks a drag, and keeps the
        // in-game fade this also mirrors from stepping audibly.
        internal const float ControlSyncIntervalSeconds = 0.1f;

        internal static void SyncGlobalControls()
        {
            if (_globalControls == null || _mixer == null) return;
            if (Time.unscaledTime < _nextControlSync) return;
            _nextControlSync = Time.unscaledTime + ControlSyncIntervalSeconds;
            bool ok = _globalControls.Tick();
            if (Time.unscaledTime < _nextControlLog) return;
            _nextControlLog = Time.unscaledTime + 1f;
            _mixer.GetFloat("nvqIkjL", out float inGame);
            _mixer.GetFloat("OtWVUHN", out float master);
            _mixer.GetFloat("mposwoH", out float music);
            _mixer.GetFloat("OnQSOHH", out float hideout);
            _mixer.GetFloat("MainVolume", out float main);
            _mixer.GetFloat("GunsVolume", out float guns);
            string value = $"bridge={ok} InGame={inGame:0.0}dB Master={master:0.0}dB " +
                $"Music={music:0.0}dB Hideout={hideout:0.0}dB Main={main:0.0}dB Guns={guns:0.0}dB";
            if (value == _lastControlLog) return;
            _lastControlLog = value;
            Plugin.Log?.LogInfo("Headphone global mixer controls: " + value);
        }
        internal static bool LoadAttempted => _attempted;
        internal static string LoadFailure { get; private set; }
        internal static bool HookInstalled { get; set; }

        internal static bool Owns(AudioMixer mixer) => _mixer != null && mixer == _mixer;

        /// <summary>
        /// Which mod's master mixer this one stood aside for, or null. Set when
        /// the mixer host installs its own mixer and this mod's variant of it
        /// cannot be used: loading the stock variant as well would only leave
        /// it, and its native DSP instance, orphaned the moment the host
        /// replaces <c>BetterAudio.Master</c>.
        /// </summary>
        internal static string YieldedTo { get; private set; }

        /// <summary>Why the variant for the host's mixer is not in use, when it is not.</summary>
        internal static string YieldReason { get; private set; }

        internal static bool Yielded => YieldedTo != null;

        // Stood aside at the load hook for the host's install, which decides.
        private static bool _awaitingHostInstall;

        private static AudioMixer _stockMaster;

        /// <summary>
        /// A master mixer that is neither the game's own nor this mod's: another
        /// mod replaced it, and nothing routed through our mixer can be heard.
        /// </summary>
        internal static bool IsForeignMaster(AudioMixer mixer) =>
            mixer != null && !Owns(mixer) && mixer != _stockMaster && !IsResourceStockMaster(mixer);

        private static bool IsResourceStockMaster(AudioMixer mixer)
        {
            try { return mixer == EFT.Settings.Sound.SoundSettingsController.MasterMixer; }
            catch (Exception) { return false; }
        }

        private static AudioMixer ResourceStockMaster()
        {
            try { return EFT.Settings.Sound.SoundSettingsController.MasterMixer; }
            catch (Exception) { return null; }
        }

        internal static bool VariantEmbedded(HeadphoneMixerVariant variant)
        {
            if (variant?.ResourceName == null) return false;
            using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(variant.ResourceName))
                return stream != null;
        }

        internal static async Task<AudioMixer> LoadMaster(BetterAudio audio, string path, CancellationTokenSource tokenSource)
        {
            Plugin.Log?.LogInfo("Headphone mixer load hook reached: " + path);
            AudioMixer original = await audio.LoadObjectAsync<AudioMixer>(path, tokenSource);
            if (original == null || tokenSource.IsCancellationRequested || path != "Audio/MasterMixer") return original;
            _stockMaster = original;
            IMixerHost host = SoundEngine.Mixer;
            if (!_attempted && host.PredictsInstall)
            {
                string blocker = host.InstallBlocker ??
                    (VariantEmbedded(host.Variant) ? null : "live variant missing from client DLL");
                if (blocker == null)
                {
                    // The host replaces the master in CreateSoundManagers. Its
                    // install will read our variant instead of its own mixer.
                    _awaitingHostInstall = true;
                    YieldedTo = null;
                    YieldReason = null;
                    Plugin.Log?.LogInfo("Headphone replacement mixer deferred to " +
                        host.DisplayName + " live mixer install");
                    return original;
                }
                Yield(blocker);
                return original;
            }
            return GetOrLoad(original) ?? original;
        }

        private static void Yield(string reason)
        {
            YieldedTo = SoundEngine.Mixer.DisplayName;
            YieldReason = reason;
            Plugin.Log?.LogWarning("Headphone replacement mixer yielded to " + YieldedTo +
                " live mixer (" + reason + "); Realistic headphones and mixer contrast stay off");
        }

        /// <summary>
        /// Runs first in the host's mixer install. Loads the host's mixer the
        /// way the host does, builds this mod's variant of it, checks it against
        /// that mixer, and leaves it in the slot the host installs from. The
        /// host then makes it the master and routes onto it as it would its
        /// own. Any failure leaves the host's mixer untouched.
        /// </summary>
        internal static void InstallIntoHost(BetterAudio audio)
        {
            if (!_awaitingHostInstall) return;
            _awaitingHostInstall = false;
            IMixerHost host = SoundEngine.Mixer;
            string key = host.VariantKey ?? "";
            string shortKey = key.Substring(0, Math.Min(8, key.Length));
            AudioMixer raw;
            try { raw = host.LoadReplacement(); }
            catch (Exception error) { Yield(host.DisplayName + " mixer load failed: " + error.Message); return; }
            if (raw == null) { Yield(host.DisplayName + " mixer did not load"); return; }
            AudioMixer variant = LoadVariant(host.Variant, raw, ResourceStockMaster() ?? _stockMaster);
            if (variant == null)
            {
                string failure = LoadFailure;
                // A variant that does not fit is a known state with SEB's own
                // mixer still working, not a broken route: allow the stock
                // variant to be reclaimed if SEB then fails as well.
                LoadFailure = null;
                _attempted = false;
                Yield("GAL mixer for " + host.DisplayName + " bundle " + shortKey + " unavailable: " + failure);
                return;
            }
            if (!host.TryInstall(variant))
            {
                Unload();
                Yield(host.DisplayName + " mixer slot could not be written");
                return;
            }
            Plugin.Log?.LogInfo("Headphone replacement mixer loaded (live variant for " + host.DisplayName + " " +
                shortKey + "); native instances=" + HeadphoneNativePlugin.InstanceCount);
        }

        /// <summary>
        /// Runs after the host's own prefix on <c>CreateSoundManagers</c>. If this
        /// mod stood aside but the stock mixer is still the master — the host's
        /// mixer did not load, a snapshot was missing, its install threw — load ours now:
        /// the source pools are created after this, so it is not yet too late.
        /// </summary>
        internal static void ReclaimIfYieldedInVain(BetterAudio audio)
        {
            bool stoodAside = Yielded || _awaitingHostInstall || HostVariantActive;
            if (!stoodAside || audio == null || _stockMaster == null || audio.Master != _stockMaster) return;
            string yieldedTo = YieldedTo ?? SoundEngine.Mixer.DisplayName;
            YieldedTo = null;
            YieldReason = null;
            _awaitingHostInstall = false;
            // The host took our variant and then did not install it. Its bundle
            // holds the one native DSP instance the stock variant expects.
            if (HostVariantActive) Unload();
            AudioMixer candidate = GetOrLoad(_stockMaster);
            if (candidate == null) return;
            AudioMixerSnapshot[] snapshots = Enum.GetNames(typeof(EnvironmentType))
                .Select(name => candidate.FindSnapshot(name)).ToArray();
            if (snapshots.Any(snapshot => snapshot == null))
            {
                Plugin.Log?.LogWarning("Headphone replacement mixer reclaim abandoned: snapshot missing");
                return;
            }
            audio.Master = candidate;
            audio.Snapshots = snapshots;
            Plugin.Log?.LogInfo("Headphone replacement mixer reclaimed: " + yieldedTo +
                " left the stock mixer in place");
        }

        /// <summary>
        /// The lookup BetterAudio is about to make, on a host's variant: the
        /// host rewrote NonspatialBypass to its own path, which the passive bus
        /// moved; the variant's candidate path is that same group, and the only
        /// one whose path contains it.
        /// </summary>
        internal static string MapLookup(string groupName, HeadphoneMixerVariant variant) =>
            variant != null && !ReferenceEquals(variant, StockVariant) &&
            !string.Equals(variant.OriginalBypassLookup, variant.CandidateBypassLookup, StringComparison.Ordinal) &&
            string.Equals(groupName, variant.OriginalBypassLookup, StringComparison.Ordinal)
                ? variant.CandidateBypassLookup : groupName;

        internal static AudioMixer GetOrLoad(AudioMixer original) =>
            _attempted ? _mixer : LoadVariant(StockVariant, original, original);

        private static void Unload()
        {
            if (_bundle != null) _bundle.Unload(true);
            _bundle = null;
            _mixer = null;
            _attempted = false;
            ActiveVariant = null;
            _globalControls = null;
            ContrastRoutes = null;
            StockGroupMap = null;
        }

        /// <param name="original">The mixer the variant was generated from, as the
        /// game holds it now: the contract, the live settings and the lookups
        /// are checked against it.</param>
        /// <param name="stockForMap">The game's own mixer, whose groups prefabs
        /// reference and which the map moves sources from.</param>
        private static AudioMixer LoadVariant(HeadphoneMixerVariant variant, AudioMixer original, AudioMixer stockForMap)
        {
            if (_attempted) return _mixer;
            _attempted = true;
            try
            {
                if (!HeadphoneNativePlugin.IsPreloaded(out string nativeReason))
                    throw new InvalidOperationException(nativeReason);
                using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(variant.ResourceName))
                {
                    if (stream == null) throw new InvalidOperationException("embedded headphone mixer is missing from client DLL");
                    using (var bytes = new MemoryStream())
                    {
                        stream.CopyTo(bytes);
                        _bundle = AssetBundle.LoadFromMemory(bytes.ToArray());
                    }
                }
                if (_bundle == null) throw new InvalidOperationException("headphone mixer bundle did not load");
                var candidate = _bundle.LoadAsset<AudioMixer>(variant.AssetPath);
                if (candidate == null) throw new InvalidOperationException("headphone mixer asset missing");
                foreach (string parameter in new[] { "GAL_PassiveVolume", "GunsVolume", "HeadphonesMixerVolume", "CompressorThreshold" })
                    if (!candidate.GetFloat(parameter, out _))
                        throw new InvalidOperationException("headphone mixer parameter missing: " + parameter);
                ValidateContract(variant, original, candidate);
                if (HeadphoneNativePlugin.InstanceCount != 1)
                    throw new InvalidOperationException("expected one native headphone DSP instance, found " + HeadphoneNativePlugin.InstanceCount);
                if (!candidate.GetFloat("GAL_PassiveVolume", out float passiveVolume) || Math.Abs(passiveVolume) > 0.00001f)
                    throw new InvalidOperationException("passive bus is not neutral by default");
                for (int band = 1; band <= 9; band++)
                {
                    string prefix = "GAL_PassiveBand" + band;
                    if (!candidate.GetFloat(prefix + "Gain", out float gain) || Math.Abs(gain - 1f) > 0.00001f ||
                        !candidate.GetFloat(prefix + "Frequency", out float frequency) || !(frequency > 0f) ||
                        !candidate.GetFloat(prefix + "Q", out float width) || !(width > 0f))
                        throw new InvalidOperationException("passive band contract invalid: " + band);
                }
                // The DSP ABI 2 colouring controls: present, and neutral until a
                // route writes them, so the asset on its own changes nothing.
                foreach (var parameter in Runtime.TransactionalMixerHeadphoneRoute.CharacterParameters)
                    if (!candidate.GetFloat(parameter.Name, out float value) ||
                        Math.Abs(value - parameter.Neutral) > 0.0001f)
                        throw new InvalidOperationException("electronics character contract invalid: " + parameter.Name);
                if (!candidate.GetFloat(Runtime.TransactionalMixerHeadphoneRoute.ElectronicsBandOrder, out float bandOrder) ||
                    Math.Abs(bandOrder - Runtime.TransactionalMixerHeadphoneRoute.NeutralBandOrder) > 0.0001f)
                    throw new InvalidOperationException("electronics band order contract invalid");
                foreach (string path in variant.RequiredGroupPaths)
                    if (candidate.FindMatchingGroups(path).Length == 0)
                        throw new InvalidOperationException("headphone route group missing: " + path);
                string parity = CheckLookupParity(original, candidate, variant);
                if (parity != null)
                {
                    if (variant.RejectOnLookupMismatch)
                        throw new InvalidOperationException("Headphone mixer lookup parity rejected: " + parity);
                    Plugin.Log?.LogWarning("Headphone mixer lookup parity: " + parity);
                }
                else
                    Plugin.Log?.LogInfo($"Headphone mixer lookup parity: {BetterAudioLookups.Length} paths ok");
                // Contrast routing is an optimisation over the per-source filter
                // fallback, not part of the headphone contract. An incomplete
                // route table must not cost the player the whole headphone path.
                bool contrastReady = GunshotContrastMixerRouter.TryCreate(
                    candidate, variant.Routes, out GunshotContrastMixerRouter contrastRoutes, out string contrastFailure);
                if (!contrastReady)
                    Plugin.Log?.LogWarning(
                        "Gunshot contrast mixer routes unavailable; per-source fallback filters will be used: " +
                        contrastFailure);
                _mixer = candidate;
                ActiveVariant = variant;
                ContrastRoutes = contrastReady ? contrastRoutes : null;
                // Sources whose prefab references the stock asset are the BTR,
                // precipitation, wind and the like. Without this pairing they
                // play outside every headset path; with a bad pairing they would
                // play on the wrong group, so it fails open per group and as a whole.
                StockGroupMap = StockMixerGroupMap.TryCreate(stockForMap, candidate, variant.Routes,
                    variant.StockAliases, out string mapFailure);
                if (StockGroupMap == null)
                    Plugin.Log?.LogWarning(
                        "Stock mixer group map unavailable; sources referencing the stock mixer stay there: " +
                        mapFailure);
                else
                    Plugin.Log?.LogInfo("Stock mixer group map ready: " + StockGroupMap.Describe());
                if (variant.BridgeGlobalControls)
                {
                    _globalControls = new HeadphoneGlobalControlBridge(
                        new Runtime.UnityMixerParameterStore(original), new Runtime.UnityMixerParameterStore(candidate));
                    if (!_globalControls.Tick()) throw new InvalidOperationException("global mixer control bridge initialization failed");
                    Plugin.Log?.LogInfo("Headphone replacement mixer loaded before audio source pools; native instances=" +
                        HeadphoneNativePlugin.InstanceCount);
                }
                return _mixer;
            }
            catch (Exception error)
            {
                LoadFailure = error.Message;
                Plugin.Log?.LogWarning("Headphone mixer unavailable (" + variant.Name + "); preserving the original mixer: " +
                    error.Message);
                if (_bundle != null) _bundle.Unload(true);
                _bundle = null;
                _mixer = null;
                ActiveVariant = null;
                _globalControls = null;
                ContrastRoutes = null;
                StockGroupMap = null;
                return null;
            }
        }

        /// <summary>
        /// Null when every lookup BetterAudio makes resolves to the same group on
        /// both mixers; otherwise the first that does not, and why.
        /// </summary>
        internal static string CheckLookupParity(AudioMixer original, AudioMixer candidate, HeadphoneMixerVariant variant)
        {
            foreach (string lookup in BetterAudioLookups)
            {
                bool bypass = lookup == "NonspatialBypass";
                string originalPath = bypass ? variant.OriginalBypassLookup : lookup;
                string candidatePath = bypass ? variant.CandidateBypassLookup : lookup;
                AudioMixerGroup expected = original.FindMatchingGroups(originalPath).FirstOrDefault();
                AudioMixerGroup actual = candidate.FindMatchingGroups(candidatePath).FirstOrDefault();
                string reason = DescribeLookupMismatch(lookup, LeafOf(originalPath), expected?.name, actual?.name);
                if (reason != null) return reason;
            }
            return null;
        }

        /// <summary>
        /// Group identities are not visible at runtime, so a lookup is judged
        /// by the group it lands on: the leaf it asked for, on both mixers.
        /// </summary>
        internal static string DescribeLookupMismatch(string lookup, string leaf, string originalName, string candidateName)
        {
            if (originalName == null) return lookup + " resolves to nothing on the original mixer";
            if (candidateName == null) return lookup + " resolves to nothing";
            if (!string.Equals(originalName, candidateName, StringComparison.Ordinal))
                return $"{lookup} resolves to {candidateName} instead of {originalName}";
            if (!string.Equals(candidateName, leaf, StringComparison.Ordinal))
                return $"{lookup} resolves to {candidateName} instead of {leaf}";
            return null;
        }

        private static string LeafOf(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator < 0 ? path : path.Substring(separator + 1);
        }

        private static void ValidateContract(HeadphoneMixerVariant variant, AudioMixer original, AudioMixer candidate)
        {
            if (original == null) throw new InvalidOperationException("original mixer unavailable for comparison");
            using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(variant.ContractResource))
            {
                if (stream == null) throw new InvalidOperationException(variant.Name + " mixer contract missing");
                using (var reader = new StreamReader(stream))
                {
                    int parameters = 0, snapshots = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("parameter|", StringComparison.Ordinal))
                        {
                            string name = line.Substring(10);
                            if (!original.GetFloat(name, out float before) || !candidate.GetFloat(name, out float after) ||
                                float.IsNaN(before) || float.IsNaN(after) || float.IsInfinity(before) || float.IsInfinity(after))
                                throw new InvalidOperationException("stock mixer parameter unavailable/nonfinite: " + name);
                            // The original asset may already carry settings from the menu (e.g. Chat).
                            // Transfer only differing live values; overriding all parameters would freeze snapshots.
                            if (!HeadphoneMixerLiveState.Equivalent(before, after))
                            {
                                if (!candidate.SetFloat(name, before) || !candidate.GetFloat(name, out float copied) ||
                                    !HeadphoneMixerLiveState.Equivalent(before, copied))
                                    throw new InvalidOperationException("stock mixer state transfer failed: " + name);
                                Plugin.Log?.LogInfo($"Headphone mixer live setting transferred: {name} {after:R} -> {before:R}");
                            }
                            parameters++;
                        }
                        else if (line.StartsWith("snapshot|", StringComparison.Ordinal))
                        {
                            string name = line.Substring(9);
                            if (original.FindSnapshot(name) == null || candidate.FindSnapshot(name) == null)
                                throw new InvalidOperationException("stock mixer snapshot missing: " + name);
                            snapshots++;
                        }
                    }
                    if (parameters != variant.ContractParameters || snapshots != 3)
                        throw new InvalidOperationException(variant.Name + " mixer contract incomplete");
                }
            }
            // The added passive parent changes prefixes, while EFT resolves
            // suffix paths. Preserve every original group name and multiplicity.
            var stockGroups = original.FindMatchingGroups("").GroupBy(group => group.name);
            var candidateGroups = candidate.FindMatchingGroups("");
            foreach (var group in stockGroups)
                if (candidateGroups.Count(value => value.name == group.Key) != group.Count())
                    throw new InvalidOperationException("stock mixer group mismatch: " + group.Key);
        }
    }

    [HarmonyPatch]
    internal static class HeadphoneMixerLoadPatch
    {
        private static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(typeof(BetterAudio), "PreloadCoroutine");
            var stateMachine = method?.GetCustomAttribute<AsyncStateMachineAttribute>();
            return stateMachine == null ? null : AccessTools.Method(stateMachine.StateMachineType, "MoveNext");
        }

        private static bool Prepare() => TargetMethod() != null;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = code.Where(instruction =>
                (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo method && method.DeclaringType == typeof(BetterAudio) &&
                method.Name == "LoadObjectAsync" && method.IsGenericMethod &&
                method.GetGenericArguments().SequenceEqual(new[] { typeof(AudioMixer) })).ToArray();
            if (matches.Length != 1)
            {
                Plugin.Log?.LogWarning("Headphone mixer load hook unsupported; preserving EFT loader");
                return code;
            }
            matches[0].opcode = OpCodes.Call;
            matches[0].operand = AccessTools.Method(typeof(HeadphoneMixerAsset), nameof(HeadphoneMixerAsset.LoadMaster));
            HeadphoneMixerAsset.HookInstalled = true;
            Plugin.Log?.LogInfo("Headphone mixer load hook installed");
            return code;
        }
    }
}
