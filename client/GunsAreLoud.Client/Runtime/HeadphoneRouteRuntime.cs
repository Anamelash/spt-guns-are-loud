using System;
using System.Collections;
using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.ActiveHeadphones;
using EFT.InventoryLogic;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Runtime
{
    internal sealed class HeadphoneRouteRuntime : MonoBehaviour
    {
        internal static HeadphoneRouteRuntime Instance { get; private set; }

        private ModConfig _config;
        private HeadphoneRouteController _controller;
        private TransactionalMixerHeadphoneRoute _backend;
        private AudioMixer _boundMixer;
        private int _boundSampleRate;
        private SmoothedMixerParameterStore _smoothStore;
        private float _nextPoll;
        private bool _dirty = true;
        private bool _nativeUpdate;
        private object _nativeController;
        private string _lastStatus = "";
        private float _tinnitusRepairAt;
        private bool _tinnitusRepairPending;

        internal HeadphoneRouteStatus Status => _controller != null
            ? _controller.Status
            : default;

        internal bool VerifyConnection(out string reason)
        {
            if (!HeadphoneMixerAsset.Owns(_boundMixer) || _backend == null ||
                !Singleton<BetterAudio>.Instantiated || Singleton<BetterAudio>.Instance == null ||
                Singleton<BetterAudio>.Instance.Master != _boundMixer)
            { reason = "GAL mixer is not bound to the game audio route"; return false; }
            if (_smoothStore?.IsTransitioning == true || _nativeUpdate)
            { reason = "route transition in progress"; return false; }
            return _backend.VerifyActive(out reason);
        }

        internal void Initialize(ModConfig config)
        {
            Instance = this;
            _config = config;
            _config.HeadphoneMode.SettingChanged += OnModeChanged;
            _config.Enabled.SettingChanged += OnModeChanged;
            HeadphoneNativeEqCache.PreloadAll(AudioSettings.outputSampleRate);
        }

        private void OnModeChanged(object sender, EventArgs args) { _dirty = true; }

        private void Update()
        {
            HeadphoneMixerAsset.SyncGlobalControls();
            _smoothStore?.Tick(Time.unscaledDeltaTime);
            RepairAfterTinnitusIfDue();
            if (_nativeUpdate && (NativeFadeIsRunning() || _smoothStore?.IsTransitioning == true))
                return;
            if (!_dirty && Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.2f;
            Refresh(_nativeUpdate);
            _nativeUpdate = false;
            _dirty = false;
        }

        private void Refresh(bool force)
        {
            AudioMixer mixer = Singleton<BetterAudio>.Instantiated &&
                Singleton<BetterAudio>.Instance != null
                ? Singleton<BetterAudio>.Instance.Master : null;
            int sampleRate = AudioSettings.outputSampleRate;
            if (_controller == null || mixer != _boundMixer || sampleRate != _boundSampleRate)
            {
                _controller?.RestoreForShutdown();
                _smoothStore?.Flush();
                _boundMixer = mixer;
                _boundSampleRate = sampleRate;
                HeadphoneNativeEqCache.PreloadAll(sampleRate);
                _smoothStore = HeadphoneMixerAsset.Owns(mixer) && mixer != null
                    ? new SmoothedMixerParameterStore(mixer)
                    : null;
                _backend = _smoothStore != null ? new TransactionalMixerHeadphoneRoute(_smoothStore, sampleRate) : null;
                _controller = new HeadphoneRouteController(_backend);
                force = true;
            }

            Headphones item = FindLocalHeadphones();
            string templateId = item?.StringTemplateId ?? "";
            HeadphoneMode requested = _config.Enabled.Value
                ? _config.HeadphoneMode.Value
                : HeadphoneMode.Vanilla;
            _controller.Apply(requested, templateId, force);
            LogStatus(_controller.Status);
        }

        private static Headphones FindLocalHeadphones()
        {
            Player player = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null
                ? Singleton<GameWorld>.Instance.MainPlayer : null;
            return HeadphonesResolver.FindEquippedItem(player?.Equipment);
        }

        private void LogStatus(HeadphoneRouteStatus status)
        {
            string value = $"requested={status.Requested} effective={status.Effective} " +
                $"template={status.TemplateId} profile={status.ProfileId} fallback={status.Fallback} " +
                $"transition={(_smoothStore?.IsTransitioning == true)} detail={status.Detail}";
            if (value == _lastStatus) return;
            _lastStatus = value;
            Plugin.Log?.LogInfo("Headphone route: " + value);
        }

        internal void BeforeNativeTemplateUpdate()
        {
            _controller?.RestoreBeforeNativeTemplateUpdate();
            // Native ApplyTemplate starts its own fades for stock parameters
            // immediately after this prefix. Yield those names to EFT and keep
            // only the disjoint GAL passive-bus fade.
            _smoothStore?.RetainOnlyPrefix("GAL_");
        }

        internal void AfterNativeTemplateUpdate(ActiveHeadphonesController controller)
        {
            _nativeController = controller;
            _nativeUpdate = true;
            _dirty = true;
        }

        internal void AfterTinnitusStarted(float durationSeconds)
        {
            // BetterAudio writes GunsVolume on every tinnitus frame and leaves it
            // at the active EFT template's DryVolume. Preserve that temporary
            // hearing effect, then restore only the Realistic route's owned dry
            // gun value. Full activation here would unnecessarily reset the
            // persistent electronics detector.
            // Match BetterAudio.StartTinnitusEffect: its coroutine runs for
            // max(15 seconds, requested time * 2) and repeated calls extend it.
            _tinnitusRepairAt = Time.time + Mathf.Max(15f,
                Mathf.Max(0f, durationSeconds) * 2f) + 0.1f;
            _tinnitusRepairPending = true;
        }

        private void RepairAfterTinnitusIfDue()
        {
            if (!_tinnitusRepairPending || Time.time < _tinnitusRepairAt) return;
            _tinnitusRepairPending = false;
            if (_controller == null || _controller.Status.Effective != HeadphoneMode.Realistic)
                return;
            if (_smoothStore == null || !_smoothStore.TrySet("GunsVolume", 0f))
                Plugin.Log?.LogWarning("Headphone route could not restore GunsVolume after tinnitus");
        }

        private bool NativeFadeIsRunning()
        {
            if (_nativeController == null) return false;
            object headphones = AccessTools.Field(_nativeController.GetType(), "_headphones")
                ?.GetValue(_nativeController);
            object components = headphones == null ? null
                : AccessTools.Field(headphones.GetType(), "_components")?.GetValue(headphones);
            if (!(components is IEnumerable sequence)) return false;
            foreach (object component in sequence)
            {
                object fader = component == null ? null
                    : AccessTools.Field(component.GetType(), "_fader")?.GetValue(component);
                PropertyInfo property = fader == null ? null
                    : AccessTools.Property(fader.GetType(), "IsFading");
                if (property != null && property.GetValue(fader) is bool fading && fading)
                    return true;
            }
            _nativeController = null;
            return false;
        }

        internal void Shutdown()
        {
            if (_config != null)
            {
                _config.HeadphoneMode.SettingChanged -= OnModeChanged;
                _config.Enabled.SettingChanged -= OnModeChanged;
            }
            _controller?.RestoreForShutdown();
            _smoothStore?.Flush();
            _controller = null;
            _backend = null;
            _smoothStore = null;
            _boundMixer = null;
            _boundSampleRate = 0;
            _tinnitusRepairPending = false;
            _tinnitusRepairAt = 0f;
            if (Instance == this) Instance = null;
        }

        private void OnDestroy() { Shutdown(); }
    }

    [HarmonyPatch(typeof(ActiveHeadphonesController), nameof(ActiveHeadphonesController.ApplyTemplate))]
    internal static class HeadphoneNativeTemplatePatch
    {
        private static void Prefix() => HeadphoneRouteRuntime.Instance?.BeforeNativeTemplateUpdate();
        private static void Postfix(ActiveHeadphonesController __instance) =>
            HeadphoneRouteRuntime.Instance?.AfterNativeTemplateUpdate(__instance);
    }

    [HarmonyPatch(typeof(BetterAudio), nameof(BetterAudio.StartTinnitusEffect))]
    internal static class HeadphoneTinnitusRoutePatch
    {
        private static void Postfix(float time) =>
            HeadphoneRouteRuntime.Instance?.AfterTinnitusStarted(time);
    }
}
