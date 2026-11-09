using BepInEx;
using BepInEx.Logging;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Engine;
using GunsAreLoud.Client.Integrations.Seb;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using System.IO;

namespace GunsAreLoud.Client
{
    [BepInPlugin(Guid, Name, Version)]
    // Load after Sound Engine Backport when it is installed, so its settings
    // are bound and its plugin is listed by the time this one decides how to
    // coexist with it. Soft: without it nothing changes.
    [BepInDependency(SebIds.ClientGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(SebIds.ReleaseGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.anamelash.gunsareloud";
        public const string Name = "Guns Are Loud";
        public const string Version = "1.2.0";

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
            // Before any patch is applied: a sound engine module's patches ask
            // for it in Prepare, and the mixer load and settings text depend on it.
            SoundEngine.Attach(Log, new SebModule());
            ModConfig = new ModConfig(Config);
            AudioRuntimeState.Initialize();
            _diagnostics = gameObject.AddComponent<DetailedDiagnosticsRuntime>();
            _diagnostics.Initialize(ModConfig, ResolveModFile(DiagnosticsFileName));
            InitializeLevelCache();
            gameObject.AddComponent<LocalGunshotPlaybackProbeScheduler>();
            Runtime = gameObject.AddComponent<HearingExposureController>();
            Runtime.Initialize(ModConfig);
            gameObject.AddComponent<HeadphoneRouteRuntime>().Initialize(ModConfig);
            gameObject.AddComponent<HeadphoneInspection>();
            gameObject.AddComponent<AutomaticWarmupDiscovery>();
            gameObject.AddComponent<ExplosionWarmup>();
            gameObject.AddComponent<LowEndNormalizationMaintenance>();
            gameObject.AddComponent<GunshotContrastController>();
            gameObject.AddComponent<ForeignMixerRouteSweeper>();
            gameObject.AddComponent<PerformanceMonitor>();

            HarmonyInstance = new Harmony(Guid);
            HarmonyInstance.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{Name} {Version} loaded");
            Log.LogInfo(SoundEngine.Describe());
            if (SoundEngine.Module != null) Log.LogInfo(SoundEngine.Module.Describe());
        }

        /// <summary>
        /// Recordings measured in earlier sessions, from the file beside the
        /// diagnostics log. One line says what was found.
        /// </summary>
        private static void InitializeLevelCache()
        {
            var store = new LevelCacheStore(ResolveModFile(LevelCache.FileName), Version,
                () => UnityEngine.Time.realtimeSinceStartup);
            string state;
            try { state = store.Load(); }
            catch (System.Exception error) { state = "unreadable (" + error.GetType().Name + "), replaced"; }
            LevelCache.Initialize(store);
            Log.LogInfo($"level cache: {store.Count} entries, {state} ({store.Path})");
        }

        private void OnDestroy()
        {
            LevelCache.Shutdown();
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
            SoundEngine.Reset();
        }
    }
}
