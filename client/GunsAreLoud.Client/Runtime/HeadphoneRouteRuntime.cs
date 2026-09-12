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
        private HeadphoneRouteStatus _lastStatusValue;
        private HeadsetSendLevels _lastSends;
        private bool _lastTransition;
        private bool _hasStatus;
        private float _nextFadePoll;
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
            // Any F12 change, not only this route's own: a mixer parameter is the
            // one piece of state that outlives a raid and shows up nowhere else,
            // so a setting that moves one and fails to move it back is invisible.
            // Recording every owned parameter on both sides of a change is what
            // makes that difference readable instead of guessed at.
            _config.Source.SettingChanged += OnAnySettingChanged;
            HeadphoneNativeEqCache.PreloadAll(AudioSettings.outputSampleRate);
        }

        private void OnModeChanged(object sender, EventArgs args) { _dirty = true; }

        private void OnAnySettingChanged(object sender, SettingChangedEventArgs args)
        {
            if (_backend == null) return;
            string key = args?.ChangedSetting?.Definition?.Key ?? "unknown";
            LogOwnedMixerParameters("before " + key);
            _pendingParameterDump = key;
            _parameterDumpFrame = Time.frameCount + 2;
        }

        private void LogOwnedMixerParameters(string when)
        {
            if (_backend == null) return;
            if (!DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.MixerSnapshot, out DiagnosticReservation reservation)) return;
            // Both systems that write this mixer, in one line: the headset route
            // and the gunshot contrast. Either can leave a value behind.
            string contrast = HeadphoneMixerAsset.ContrastRoutes?.DescribeLiveParameters() ?? "contrast=none";
            DetailedDiagnostics.Commit(
                reservation,
                $"mixer parameters {when}: route[{_backend.DescribeLiveParameters()}] " +
                $"contrast[{contrast}]");
        }

        private string _pendingParameterDump;
        private int _parameterDumpFrame = -1;

        private void Update()
        {
            using (PerformanceTrace.Measure(PerformanceArea.HeadphoneRoute)) Tick();
        }

        private void Tick()
        {
            // A couple of frames after the change, so every ramp the setting
            // started has been applied and the comparison is like for like.
            if (_pendingParameterDump != null && Time.frameCount >= _parameterDumpFrame)
            {
                LogOwnedMixerParameters("after " + _pendingParameterDump);
                _pendingParameterDump = null;
            }
            HeadphoneMixerAsset.SyncGlobalControls();
            _smoothStore?.Tick(Time.unscaledDeltaTime);
            RepairAfterTinnitusIfDue();
            if (_nativeUpdate)
            {
                // EFT's own fade lasts hundreds of milliseconds and finding out
                // whether it still runs walks the headset component graph. Poll it
                // at a coarse rate and reactivate once, after it ends.
                if (Time.unscaledTime < _nextFadePoll) return;
                _nextFadePoll = Time.unscaledTime + 0.05f;
                if (NativeFadeIsRunning() || _smoothStore?.IsTransitioning == true) return;
            }
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
            // From the worn item itself, not BetterAudio.CurrentHeadphonesTemplate:
            // EFT can keep its no-headset Default applied long after an equip.
            HeadsetSendLevels sends = HeadsetSendLevels.From(item?.Template);
            HeadphoneMode requested = _config.Enabled.Value
                ? _config.HeadphoneMode.Value
                : HeadphoneMode.Vanilla;
            _controller.Apply(requested, templateId, force, sends);
            LogStatus(_controller.Status, sends);
        }

        private static Headphones FindLocalHeadphones()
        {
            Player player = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null
                ? Singleton<GameWorld>.Instance.MainPlayer : null;
            return HeadphonesResolver.FindEquippedItem(player?.Equipment);
        }

        // Called five times a second. Compare the values, not a formatted line:
        // the status is unchanged almost every time, and building the string only
        // to throw it away was the whole cost of this path.
        private void LogStatus(HeadphoneRouteStatus status, HeadsetSendLevels sends)
        {
            bool transition = _smoothStore?.IsTransitioning == true;
            if (_hasStatus &&
                _lastStatusValue.Requested == status.Requested &&
                _lastStatusValue.Effective == status.Effective &&
                _lastStatusValue.TemplateId == status.TemplateId &&
                _lastStatusValue.ProfileId == status.ProfileId &&
                _lastStatusValue.Fallback == status.Fallback &&
                _lastStatusValue.Detail == status.Detail &&
                _lastTransition == transition &&
                _lastSends.Equals(sends))
                return;
            _hasStatus = true;
            _lastStatusValue = status;
            _lastTransition = transition;
            _lastSends = sends;
            Plugin.Log?.LogInfo("Headphone route: " +
                $"requested={status.Requested} effective={status.Effective} " +
                $"template={status.TemplateId} profile={status.ProfileId} fallback={status.Fallback} " +
                $"transition={transition} detail={status.Detail}" +
                (status.Effective == HeadphoneMode.Realistic ? " sends: " + sends : ""));
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
            // EFT applies a template exactly when the worn headset changes.
            HeadphonesResolver.Invalidate();
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

        // Resolved once per type instead of on every poll: AccessTools.Field is a
        // name lookup, and this used to run for every component, every frame that
        // EFT was fading a headset template.
        private static Type _headphonesOwner, _componentsOwner, _faderOwner, _fadingOwner;
        private static FieldInfo _headphonesField, _componentsField, _faderField;
        private static PropertyInfo _isFadingProperty;

        private static FieldInfo Field(object owner, ref Type cachedType, ref FieldInfo cached, string name)
        {
            Type type = owner.GetType();
            if (!ReferenceEquals(type, cachedType))
            {
                cachedType = type;
                cached = AccessTools.Field(type, name);
            }
            return cached;
        }

        private bool NativeFadeIsRunning()
        {
            if (_nativeController == null) return false;
            object headphones = Field(_nativeController, ref _headphonesOwner, ref _headphonesField, "_headphones")
                ?.GetValue(_nativeController);
            object components = headphones == null ? null
                : Field(headphones, ref _componentsOwner, ref _componentsField, "_components")?.GetValue(headphones);
            if (!(components is IEnumerable sequence)) return false;
            foreach (object component in sequence)
            {
                object fader = component == null ? null
                    : Field(component, ref _faderOwner, ref _faderField, "_fader")?.GetValue(component);
                if (fader == null) continue;
                Type faderType = fader.GetType();
                if (!ReferenceEquals(faderType, _fadingOwner))
                {
                    _fadingOwner = faderType;
                    _isFadingProperty = AccessTools.Property(faderType, "IsFading");
                }
                if (_isFadingProperty != null && _isFadingProperty.GetValue(fader) is bool fading && fading)
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
                _config.Source.SettingChanged -= OnAnySettingChanged;
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
