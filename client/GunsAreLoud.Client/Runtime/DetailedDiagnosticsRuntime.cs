using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal sealed class DetailedDiagnosticsRuntime : MonoBehaviour
    {
        private ModConfig _config;
        private bool _shutdown;

        internal void Initialize(ModConfig config, string path)
        {
            _config = config;
            DetailedDiagnostics.Start(path);
            DetailedDiagnostics.SetEnabled(config?.DiagnosticShotLog.Value == true);
            if (config != null) config.Source.SettingChanged += OnSettingChanged;
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs args)
        {
            if (_config == null || !ReferenceEquals(args.ChangedSetting, _config.DiagnosticShotLog))
                return;
            bool enabled = _config.DiagnosticShotLog.Value;
            DetailedDiagnostics.SetEnabled(enabled);
            if (!enabled)
            {
                LocalGunshotPlaybackProbeScheduler.Instance?.Clear();
                Plugin.Runtime?.CancelDiagnosticProbes();
            }
        }

        private void Update()
        {
            if (DetailedDiagnostics.TryTakeFailure(out string failure))
                Plugin.Log?.LogWarning("Detailed diagnostics writer disabled: " + failure);
        }

        internal void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            if (_config != null) _config.Source.SettingChanged -= OnSettingChanged;
            _config = null;
            LocalGunshotPlaybackProbeScheduler.Instance?.Clear();
            Plugin.Runtime?.CancelDiagnosticProbes();
            if (!DetailedDiagnostics.Shutdown(250))
                Plugin.Log?.LogWarning("Detailed diagnostics writer did not stop within 250 ms; game shutdown continues.");
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
