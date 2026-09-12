using BepInEx;
using BepInEx.Logging;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using System.IO;

namespace GunsAreLoud.Client
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.anamelash.gunsareloud";
        public const string Name = "Guns Are Loud";
        public const string Version = "1.0.3";

        internal static ManualLogSource Log { get; private set; }

        internal static Harmony HarmonyInstance { get; private set; }

        internal static ModConfig ModConfig { get; private set; }

        internal static HearingExposureController Runtime { get; private set; }

        internal const string DiagnosticsFileName = "GunsAreLoud.Diagnostics.log";

        private DetailedDiagnosticsRuntime _diagnostics;

        /// <summary>
        /// The mod's own files belong beside the mod, not in the shared BepInEx
        /// root next to every other plugin's output. Resolve the folder from our
        /// own assembly rather than a hardcoded name: the DLL may sit directly
        /// in plugins/, or in a differently named folder, and on a case-
        /// sensitive filesystem "GunsAreLoud" and "gunsareloud" are not the same
        /// directory.
        /// </summary>
        internal static string ResolveModFile(string fileName)
        {
            string directory = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Path.Combine(BepInEx.Paths.PluginPath, "GunsAreLoud");
            }

            return Path.GetFullPath(Path.Combine(directory, fileName));
        }

        private void Awake()
        {
            Log = Logger;
            ModConfig = new ModConfig(Config);
            AudioRuntimeState.Initialize();
            _diagnostics = gameObject.AddComponent<DetailedDiagnosticsRuntime>();
            _diagnostics.Initialize(ModConfig, ResolveModFile(DiagnosticsFileName));
            gameObject.AddComponent<LocalGunshotPlaybackProbeScheduler>();
            Runtime = gameObject.AddComponent<HearingExposureController>();
            Runtime.Initialize(ModConfig);
            gameObject.AddComponent<HeadphoneRouteRuntime>().Initialize(ModConfig);
            gameObject.AddComponent<HeadphoneInspection>();
            gameObject.AddComponent<AutomaticWarmupDiscovery>();
            gameObject.AddComponent<LowEndNormalizationMaintenance>();
            gameObject.AddComponent<GunshotContrastController>();
            gameObject.AddComponent<PerformanceMonitor>();

            HarmonyInstance = new Harmony(Guid);
            HarmonyInstance.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void OnDestroy()
        {
            AudioRuntimeState.Shutdown();
            Runtime?.Shutdown();
            LocalGunshotPlaybackProbeScheduler.Instance?.Clear();
            HeadphoneRouteRuntime.Instance?.Shutdown();
            GunshotContrastController.Instance?.Shutdown();
            HarmonyInstance?.UnpatchSelf();
            AutomaticCopyCache.Clear();
            LowEndNormalizationCache.Clear();
            Runtime = null;
            _diagnostics?.Shutdown();
            _diagnostics = null;
        }
    }
}
