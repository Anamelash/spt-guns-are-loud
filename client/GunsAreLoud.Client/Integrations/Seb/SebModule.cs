using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using GunsAreLoud.Client.Engine;

namespace GunsAreLoud.Client.Integrations.Seb
{
    internal enum SebMode
    {
        Absent,
        // SEB's raw live mixer, or none: this mod's mixer route is off.
        Coexist,
        // This mod's variant of SEB's live mixer is what SEB installs.
        Integrated
    }

    /// <summary>
    /// Sound Engine Backport (SPT-SEB): finds the plugin, reads its settings and
    /// resolves what this mod needs from it. Everything is by name: this
    /// assembly holds no reference to SEB, and with SEB absent no line of this
    /// module runs beyond the lookup.
    /// </summary>
    internal sealed class SebModule : ISoundEngineModule
    {
        private static ConfigEntry<bool> _liveEngineEntry;
        private static ConfigEntry<bool> _liveMixerEntry;
        private static ConfigEntry<float> _ambientVolumeEntry;
        private static ConfigEntry<float> _gunshotsVolumeEntry;
        private static bool _liveEngineFromFile = true;
        private static bool _liveMixerFromFile = true;
        private static float? _gunshotsVolumeFromFile;

        internal static bool Present { get; private set; }
        internal static string Guid { get; private set; } = "";
        internal static string Version { get; private set; } = "";
        internal static string BundlePath { get; private set; } = "";
        internal static string BundleSha256 { get; private set; } = "";
        internal static Assembly Assembly { get; private set; }

        /// <summary>This module holds the engine and mixer roles for the run.</summary>
        internal static bool Attached => SoundEngine.Module is SebModule;

        internal static bool LiveEngineEnabled => Present && (_liveEngineEntry?.Value ?? _liveEngineFromFile);

        internal static bool LiveMixerEnabled => Present && (_liveMixerEntry?.Value ?? _liveMixerFromFile);

        internal static bool BundlePresent => !string.IsNullOrEmpty(BundlePath) && File.Exists(BundlePath);

        /// <summary>
        /// SEB swaps <c>BetterAudio.Master</c> for its own mixer while the raid's
        /// audio is created, after this mod's replacement would have been loaded.
        /// Its live mixer only installs with the setting on and its bundle beside
        /// it; anything else leaves the stock mixer, and ours, in place.
        /// </summary>
        internal static bool PredictsLiveMixerInstall => LiveMixerEnabled && BundlePresent;

        internal static SebMode Mode =>
            !Present ? SebMode.Absent : PredictsIntegration ? SebMode.Integrated : SebMode.Coexist;

        /// <summary>The installed live mixer is the one the embedded variant was built from.</summary>
        internal static bool BundleSupported =>
            string.Equals(BundleSha256, SebIds.SupportedLiveBundleSha256, StringComparison.OrdinalIgnoreCase);

        internal static bool InstallHookAvailable => SebReflection.InstallHookAvailable;

        /// <summary>
        /// SEB will install a live mixer, and it can be this mod's variant of it:
        /// its bundle is the supported one and its install can be reached.
        /// </summary>
        internal static bool PredictsIntegration =>
            PredictsLiveMixerInstall && BundleSupported && InstallHookAvailable;

        /// <summary>Why the variant cannot be used, or null when it can.</summary>
        internal static string IntegrationBlocker =>
            !Present ? SebIds.DisplayName + " absent" :
            !LiveMixerEnabled ? "live mixer disabled in " + SebIds.DisplayName :
            !BundlePresent ? "live mixer bundle missing" :
            !BundleSupported ? "SEB bundle " + Short(BundleSha256, 8) + " has no GAL mixer" :
            !InstallHookAvailable ? "SEB live mixer install not reachable" : null;

        /// <summary>SEB's "Ambience" setting, to follow it while a headset route owns the level.</summary>
        internal static ConfigEntry<float> AmbientVolumeEntry => _ambientVolumeEntry;

        /// <summary>SEB's "Gunshots" slider in dB: the player's own trim, not part of SEB's calibration.</summary>
        internal static float GunshotsVolumeDb =>
            _gunshotsVolumeEntry?.Value ?? _gunshotsVolumeFromFile ?? 0f;

        /// <summary>Where <see cref="GunshotsVolumeDb"/> comes from: entry, file or default.</summary>
        internal static string GunshotsVolumeSource =>
            _gunshotsVolumeEntry != null ? "entry" : _gunshotsVolumeFromFile.HasValue ? "file" : "default";

        // ----- ISoundEngineModule -----

        public string Name => SebIds.DisplayName;

        public bool TryAttach(ManualLogSource log, out IShotEngine shots, out IMixerHost mixer)
        {
            shots = null;
            mixer = null;
            Detect();
            if (!Present) return false;
            shots = new SebShotEngine();
            mixer = new SebMixerHost();
            return true;
        }

        public string Describe() =>
            DescribeDetection(Version, LiveEngineEnabled, LiveMixerEnabled, BundleSha256, Mode,
                SebReflection.BankProbe, SebReflection.MixerProbe, IntegrationBlocker ?? "ready") +
            $" loopClipProbe={(SebReflection.LoopClipProbe ? "seb" : "fallback")}" +
            $" bankSwapProbe={(SebReflection.BankReplacementProbe ? "seb" : "none")}" +
            $" gunshotsVolume={GunshotsVolumeDb:0.0}dB({GunshotsVolumeSource})";

        // ----- detection -----

        internal static void Detect()
        {
            Reset();
            PluginInfo info = null;
            foreach (string guid in SebIds.KnownGuids)
            {
                if (Chainloader.PluginInfos != null && Chainloader.PluginInfos.TryGetValue(guid, out info) && info != null)
                {
                    Guid = guid;
                    break;
                }
                info = null;
            }
            if (info == null) return;

            Present = true;
            Version = info.Metadata?.Version?.ToString() ?? "";
            Assembly = info.Instance != null ? info.Instance.GetType().Assembly : null;
            string directory = string.IsNullOrEmpty(info.Location) ? "" : Path.GetDirectoryName(info.Location) ?? "";
            BundlePath = string.IsNullOrEmpty(directory) ? "" : Path.Combine(directory, SebIds.BundleFileName);
            BundleSha256 = TryHash(BundlePath);

            ConfigFile config = info.Instance != null ? info.Instance.Config : null;
            if (config == null || !config.TryGetEntry("Engine", "LiveEngineEnabled", out _liveEngineEntry)) _liveEngineEntry = null;
            if (config == null || !config.TryGetEntry("Engine", "LiveMixerEnabled", out _liveMixerEntry)) _liveMixerEntry = null;
            if (config == null || !config.TryGetEntry("Volume", "AmbientVolumeDb", out _ambientVolumeEntry)) _ambientVolumeEntry = null;
            if (config == null || !config.TryGetEntry("Volume", "GunshotsVolumeDb", out _gunshotsVolumeEntry)) _gunshotsVolumeEntry = null;
            if (_liveEngineEntry == null || _liveMixerEntry == null || _gunshotsVolumeEntry == null)
            {
                // SEB binds its settings in its own Awake, which the soft
                // dependency puts before ours; its file is the fallback for an
                // order that did not hold. A setting it does not mention keeps
                // SEB's own default.
                string text = TryReadConfig(Guid);
                _liveEngineFromFile = ParseConfigBool(text, "Engine", "LiveEngineEnabled") ?? true;
                _liveMixerFromFile = ParseConfigBool(text, "Engine", "LiveMixerEnabled") ?? true;
                _gunshotsVolumeFromFile = ParseConfigFloat(text, "Volume", "GunshotsVolumeDb");
            }

            SebReflection.Resolve(Assembly);
        }

        internal static string DescribeDetection(string version, bool liveEngine, bool liveMixer,
            string bundleSha, SebMode mode, bool bankProbe, bool mixerProbe, string variant = null) =>
            $"{SebIds.DisplayName} {(string.IsNullOrEmpty(version) ? "?" : version)} detected: " +
            $"liveEngine={liveEngine} liveMixer={liveMixer} " +
            $"bundleSha={(string.IsNullOrEmpty(bundleSha) ? "missing" : bundleSha.Substring(0, Math.Min(16, bundleSha.Length)))} " +
            $"mode={mode} bankProbe={(bankProbe ? "seb" : "fallback")} mixerProbe={(mixerProbe ? "seb" : "none")}" +
            (variant == null ? "" : " galVariant=" + variant);

        /// <summary>
        /// The value of one boolean entry in a BepInEx .cfg text, or null when
        /// the section or key is absent or the value is not a boolean.
        /// </summary>
        internal static bool? ParseConfigBool(string text, string section, string key)
        {
            string value = ReadConfigValue(text, section, key);
            return value != null && bool.TryParse(value, out bool parsed) ? parsed : (bool?)null;
        }

        /// <summary>The value of one number entry in a BepInEx .cfg text, or null.</summary>
        internal static float? ParseConfigFloat(string text, string section, string key)
        {
            string value = ReadConfigValue(text, section, key);
            return value != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed : (float?)null;
        }

        private static string ReadConfigValue(string text, string section, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string current = null;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                    if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        current = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        continue;
                    }
                    if (!string.Equals(current, section, StringComparison.Ordinal)) continue;
                    int equals = trimmed.IndexOf('=');
                    if (equals <= 0) continue;
                    if (!string.Equals(trimmed.Substring(0, equals).Trim(), key, StringComparison.Ordinal)) continue;
                    return trimmed.Substring(equals + 1).Trim();
                }
            }
            return null;
        }

        private static string TryReadConfig(string guidFile)
        {
            try
            {
                foreach (string guid in new[] { guidFile, SebIds.ClientGuid, SebIds.ReleaseGuid })
                {
                    if (string.IsNullOrEmpty(guid)) continue;
                    string path = Path.Combine(BepInEx.Paths.ConfigPath, guid + ".cfg");
                    if (File.Exists(path)) return File.ReadAllText(path);
                }
            }
            catch (Exception) { }
            return null;
        }

        internal static string TryHash(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var text = new System.Text.StringBuilder(hash.Length * 2);
                    foreach (byte value in hash) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                    return text.ToString();
                }
            }
            catch (Exception) { return ""; }
        }

        internal static string Short(string value, int length) =>
            string.IsNullOrEmpty(value) ? "missing" : value.Substring(0, Math.Min(length, value.Length));

        private static void Reset()
        {
            Present = false;
            Guid = "";
            Version = "";
            BundlePath = "";
            BundleSha256 = "";
            Assembly = null;
            _liveEngineEntry = null;
            _liveMixerEntry = null;
            _ambientVolumeEntry = null;
            _gunshotsVolumeEntry = null;
            _liveEngineFromFile = true;
            _liveMixerFromFile = true;
            _gunshotsVolumeFromFile = null;
            SebReflection.Reset();
        }
    }
}
