using BepInEx;
using BepInEx.Logging;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;

namespace GunsAreLoud.Client
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.anamelash.gunsareloud";
        public const string Name = "Guns Are Loud";
        public const string Version = "0.23.2";

        internal static ManualLogSource Log { get; private set; }

        internal static Harmony HarmonyInstance { get; private set; }

        internal static ModConfig ModConfig { get; private set; }

        internal static HearingExposureController Runtime { get; private set; }

        private void Awake()
        {
            Log = Logger;
            ModConfig = new ModConfig(Config);
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
            Runtime?.Shutdown();
            HeadphoneRouteRuntime.Instance?.Shutdown();
            GunshotContrastController.Instance?.Shutdown();
            HarmonyInstance?.UnpatchSelf();
            AutomaticBeatClipCache.Clear();
            LowEndNormalizationCache.Clear();
            Runtime = null;
        }
    }
}
