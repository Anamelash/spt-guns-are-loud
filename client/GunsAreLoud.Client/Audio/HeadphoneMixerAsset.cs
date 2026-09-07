using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Audio
{
    // Loaded before BetterAudio constructs its pools. Never swap group identities
    // when the user changes the mode in an existing raid.
    internal static class HeadphoneMixerAsset
    {
        internal const string ResourceName = "GunsAreLoud.Client.Assets.gal_headphone_mixer";
        internal const string AssetPath = "Assets/GunsAreLoud/Audio/MasterMixer.mixer";
        private static AssetBundle _bundle;
        private static AudioMixer _mixer;
        private static bool _attempted;

        internal static bool Owns(AudioMixer mixer) => _mixer != null && mixer == _mixer;

        internal static async Task<AudioMixer> LoadMaster(BetterAudio audio, string path, CancellationTokenSource tokenSource)
        {
            AudioMixer original = await audio.LoadObjectAsync<AudioMixer>(path, tokenSource);
            if (original == null || tokenSource.IsCancellationRequested || path != "Audio/MasterMixer") return original;
            return GetOrLoad(original) ?? original;
        }

        internal static AudioMixer GetOrLoad(AudioMixer original)
        {
            if (_attempted) return _mixer;
            _attempted = true;
            try
            {
                if (!HeadphoneNativePlugin.IsPreloaded(out string nativeReason))
                    throw new InvalidOperationException(nativeReason);
                using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null) return null;
                    using (var bytes = new MemoryStream())
                    {
                        stream.CopyTo(bytes);
                        _bundle = AssetBundle.LoadFromMemory(bytes.ToArray());
                    }
                }
                if (_bundle == null) throw new InvalidOperationException("headphone mixer bundle did not load");
                var candidate = _bundle.LoadAsset<AudioMixer>(AssetPath);
                if (candidate == null) throw new InvalidOperationException("headphone mixer asset missing");
                foreach (string parameter in new[] { "GAL_PassiveVolume", "GunsVolume", "HeadphonesMixerVolume", "CompressorThreshold" })
                    if (!candidate.GetFloat(parameter, out _))
                        throw new InvalidOperationException("headphone mixer parameter missing: " + parameter);
                ValidateStockContract(original, candidate);
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
                foreach (string path in new[] { "World/GAL Passive/NonspatialBypass", "World/GAL Passive/Guns/Gunshots",
                    "World/GAL Passive/Main/Environment", "World/GAL Passive/Occlusion/SimpleOccluded", "World/Headphones/GunCompressor",
                    "World/GAL Electronics" })
                    if (candidate.FindMatchingGroups(path).Length == 0)
                        throw new InvalidOperationException("headphone route group missing: " + path);
                _mixer = candidate;
                Plugin.Log?.LogInfo("Headphone replacement mixer loaded before audio source pools; native instances=" +
                    HeadphoneNativePlugin.InstanceCount);
                return _mixer;
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Headphone mixer unavailable; preserving EFT mixer: " + error.Message);
                if (_bundle != null) _bundle.Unload(true);
                _bundle = null;
                return null;
            }
        }

        private static void ValidateStockContract(AudioMixer original, AudioMixer candidate)
        {
            if (original == null) throw new InvalidOperationException("original mixer unavailable for comparison");
            using (Stream stream = typeof(HeadphoneMixerAsset).Assembly.GetManifestResourceStream(
                "GunsAreLoud.Client.Assets.headphone-mixer-stock-contract.txt"))
            {
                if (stream == null) throw new InvalidOperationException("stock mixer contract missing");
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
                    if (parameters != 104 || snapshots != 3)
                        throw new InvalidOperationException("stock mixer contract incomplete");
                }
            }
            // The added passive parent changes prefixes, while EFT resolves
            // suffix paths. Preserve every stock group name and multiplicity.
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
            return code;
        }
    }
}
