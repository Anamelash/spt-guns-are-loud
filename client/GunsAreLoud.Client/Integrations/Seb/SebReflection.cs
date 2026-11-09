using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using SoundBank = EFT.SoundBank;

namespace GunsAreLoud.Client.Integrations.Seb
{
    /// <summary>
    /// Every member of SEB this mod uses, resolved by name with its signature
    /// checked. A member that is not found turns off the one capability that
    /// needs it; each has a fallback that behaves as SEB 1.0.0 does or as the
    /// stock game does.
    /// </summary>
    internal static class SebReflection
    {
        private static Func<SoundBank, bool> _isLegacyBank;
        private static Func<AudioClip, bool> _isLoopClip;
        private static Func<bool> _liveMixerActive;
        private static Action<WeaponSoundPlayer> _applyBankReplacement;
        internal const string BankReplacementTypeName = "SoundEngineBackport.LiveEngine.LiveBankReplacement";
        private static MethodInfo _liveMixerLoad;
        private static FieldInfo _liveMixerField;
        private static FieldInfo _ambientTrimField;
        private static readonly Dictionary<int, bool> LegacyBankFallback = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> LoopClipCache = new Dictionary<int, bool>();

        internal static MethodInfo LiveMixerInstall { get; private set; }

        internal static bool BankProbe => _isLegacyBank != null;
        internal static bool LoopClipProbe => _isLoopClip != null;
        internal static bool MixerProbe => _liveMixerActive != null;
        internal static bool BankReplacementProbe => _applyBankReplacement != null;

        /// <summary>Every member the variant install goes through was found.</summary>
        internal static bool InstallHookAvailable =>
            LiveMixerInstall != null && _liveMixerLoad != null && _liveMixerField != null;

        internal static void Resolve(Assembly assembly)
        {
            Reset();
            Type patches = ResolveType(assembly, SebIds.PatchesTypeName);
            _isLegacyBank = StaticDelegate<Func<SoundBank, bool>>(patches, "IsLegacyBank", typeof(SoundBank), typeof(bool));
            _isLoopClip = StaticDelegate<Func<AudioClip, bool>>(patches, "IsLoopClip", typeof(AudioClip), typeof(bool));
            _applyBankReplacement = StaticDelegate<Action<WeaponSoundPlayer>>(
                ResolveType(assembly, BankReplacementTypeName), "Apply", typeof(WeaponSoundPlayer), typeof(void));

            Type mixer = ResolveType(assembly, SebIds.LiveMixerTypeName);
            if (mixer == null) return;
            MethodInfo active = AccessTools.PropertyGetter(mixer, "Active");
            if (active != null && active.IsStatic && active.ReturnType == typeof(bool))
            {
                try { _liveMixerActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active); }
                catch (Exception) { _liveMixerActive = null; }
            }
            MethodInfo install = AccessTools.Method(mixer, "Install", new[] { typeof(BetterAudio) });
            LiveMixerInstall = install != null && install.IsStatic ? install : null;
            MethodInfo load = AccessTools.Method(mixer, "Load", Type.EmptyTypes);
            _liveMixerLoad = load != null && load.IsStatic && load.ReturnType == typeof(AudioMixer) ? load : null;
            FieldInfo field = AccessTools.Field(mixer, "_mixer");
            _liveMixerField = field != null && field.IsStatic && field.FieldType == typeof(AudioMixer) ? field : null;
            FieldInfo trim = AccessTools.Field(mixer, "AmbientTrimDb");
            _ambientTrimField = trim != null && trim.IsStatic && trim.FieldType == typeof(float) ? trim : null;
        }

        internal static void Reset()
        {
            _isLegacyBank = null;
            _isLoopClip = null;
            _liveMixerActive = null;
            _applyBankReplacement = null;
            _liveMixerLoad = null;
            _liveMixerField = null;
            _ambientTrimField = null;
            LiveMixerInstall = null;
            LegacyBankFallback.Clear();
            LoopClipCache.Clear();
        }

        /// <summary>
        /// SEB plays a legacy bank through EFT's queue (its V1 path); every other
        /// bank its live engine plays itself.
        /// </summary>
        internal static bool IsLegacyBank(SoundBank bank)
        {
            if (_isLegacyBank != null)
            {
                try { return _isLegacyBank(bank); }
                catch (Exception) { _isLegacyBank = null; }
            }
            return IsLegacyBankByClipNames(bank);
        }

        /// <summary>
        /// On its V1 path SEB loops a clip or plays it whole by this rule, per
        /// chosen clip, not per bank.
        /// </summary>
        internal static bool IsLoopClip(AudioClip clip)
        {
            if (clip == null) return false;
            int id = clip.GetInstanceID();
            if (LoopClipCache.TryGetValue(id, out bool cached)) return cached;
            bool loop;
            if (_isLoopClip != null)
            {
                try { loop = _isLoopClip(clip); }
                catch (Exception) { _isLoopClip = null; loop = IsLoopClipName(clip.name); }
            }
            else loop = IsLoopClipName(clip.name);
            LoopClipCache[id] = loop;
            return loop;
        }

        /// <summary>
        /// SEB 1.0.0's own rule, for when its method cannot be reached: a clip
        /// named "_loop" and not "tail" is a loop.
        /// </summary>
        internal static bool IsLoopClipName(string name) =>
            !string.IsNullOrEmpty(name) &&
            name.IndexOf("_loop", StringComparison.OrdinalIgnoreCase) >= 0 &&
            name.IndexOf("tail", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>SEB's own bank swap for this weapon; nothing when it cannot be reached.</summary>
        internal static void ApplyBankReplacement(WeaponSoundPlayer player)
        {
            if (_applyBankReplacement == null || player == null) return;
            try { _applyBankReplacement(player); }
            catch (Exception) { _applyBankReplacement = null; }
        }

        internal static bool IsLiveMixerActive
        {
            get
            {
                if (_liveMixerActive == null) return false;
                try { return _liveMixerActive(); }
                catch (Exception) { return false; }
            }
        }

        internal static AudioMixer LoadLiveMixer() => _liveMixerLoad?.Invoke(null, null) as AudioMixer;

        internal static bool TrySetLiveMixer(AudioMixer mixer)
        {
            if (_liveMixerField == null) return false;
            try
            {
                _liveMixerField.SetValue(null, mixer);
                return ReferenceEquals(_liveMixerField.GetValue(null), mixer);
            }
            catch (Exception) { return false; }
        }

        /// <summary>SEB's "Ambience" trim as its live mixer uses it, or null when unreadable.</summary>
        internal static float? AmbientTrimDb
        {
            get
            {
                try
                {
                    if (_ambientTrimField?.GetValue(null) is float trim) return trim;
                }
                catch (Exception) { }
                return null;
            }
        }

        private static bool IsLegacyBankByClipNames(SoundBank bank)
        {
            if (bank == null || bank.Environments == null || bank.Environments.Length == 0) return true;
            int id = bank.GetInstanceID();
            if (LegacyBankFallback.TryGetValue(id, out bool legacy)) return legacy;
            legacy = false;
            foreach (var environment in bank.Environments)
            {
                if (environment?.Clips == null) continue;
                foreach (var variety in environment.Clips)
                {
                    if (variety?.Clips == null) continue;
                    foreach (AudioClip clip in variety.Clips)
                        if (clip != null && IsLoopClipName(clip.name)) { legacy = true; break; }
                    if (legacy) break;
                }
                if (legacy) break;
            }
            LegacyBankFallback[id] = legacy;
            return legacy;
        }

        private static T StaticDelegate<T>(Type type, string name, Type parameter, Type result) where T : Delegate
        {
            if (type == null) return null;
            MethodInfo method = AccessTools.Method(type, name, new[] { parameter });
            if (method == null || !method.IsStatic || method.ReturnType != result) return null;
            try { return (T)Delegate.CreateDelegate(typeof(T), method); }
            catch (Exception) { return null; }
        }

        private static Type ResolveType(Assembly assembly, string name)
        {
            try { return assembly?.GetType(name, false) ?? AccessTools.TypeByName(name); }
            catch (Exception) { return null; }
        }
    }
}
