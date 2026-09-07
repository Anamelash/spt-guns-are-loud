using System;
using System.Threading;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal sealed class HearingExposureController : MonoBehaviour
    {
        private ModConfig _config;
        private HearingImpactProcessor _processor;
        private AudioListener _listener;
        private HearingExposureState _exposure = new HearingExposureState();
        private float _nextListenerSearchTime;
        private bool _reportedCompatibility;
        private bool _wasEnabled;
        private TuningSnapshot _tuning;
        private int _tuningDirty = 1;
        private int _resetExposureDirty;

        internal void Initialize(ModConfig config)
        {
            _config = config;
            _wasEnabled = config.Enabled.Value;
            config.Source.SettingChanged += OnSettingChanged;
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs args)
        {
            Interlocked.Exchange(ref _tuningDirty, 1);
            if (!_config.Enabled.Value ||
                (!ModConfig.IsResponseEnabled(ModConfig.NormalizeResponseControl(_config.HearingTrauma.Value)) &&
                 !ModConfig.IsResponseEnabled(ModConfig.NormalizeResponseControl(_config.Ringing.Value))))
            {
                Interlocked.Exchange(ref _resetExposureDirty, 1);
            }
        }

        internal TuningSnapshot CurrentTuning()
        {
            ConsumePendingReset();
            if (Interlocked.Exchange(ref _tuningDirty, 0) != 0 || _tuning == null)
                _tuning = _config.GetTuning();
            return _tuning;
        }

        internal void HandleShot(ShotProcessingState state)
        {
            ConsumePendingReset();
            if (_config == null || !_config.Enabled.Value || state?.Shot == null)
            {
                return;
            }

            ShotDescriptor shot = state.Shot;
            ExposureResult result = state.Exposure;
            TuningSnapshot tuning = state.Tuning;
            EnsureListenerProcessor(force: true);
            int masterProbeId = 0;
            if (_config.DiagnosticShotLog.Value && _processor != null)
            {
                masterProbeId = _processor.ArmMasterProbe(
                    tuning.AutomaticPitchedRoute,
                    state.AudioTuning.PitchedLayerLoopBeatSeconds > 0.001f,
                    AudioSettings.outputSampleRate);
            }

            if (tuning.ExposureEnabled)
            {
                ExposureState.Add(result.LeftDose, result.RightDose, tuning);
            }

            if (_config.DiagnosticShotLog.Value)
            {
                float bodyFrequency = DirectLoudnessModel.CalculatePressureFrequencyHz(result, tuning);
                float bodyBandGain = tuning.LowEndMode == GunshotLowEndMode.OriginalBand
                    ? LocalGunshotImpactFilter.CalculateBodyBandGain(state.DirectBodyGain, bodyFrequency)
                    : 0f;
                float pitchedLayerGain = tuning.LowEndMode == GunshotLowEndMode.PitchedCopy
                    ? PitchedGunshotLayer.CalculateLayerGain(
                        state.DirectBodyGain,
                        bodyFrequency,
                        tuning.PitchedLayerGainDb,
                        tuning.CaliberContrastPercent)
                    : 0f;
                Plugin.Log.LogInfo(
                    $"shot caliber={shot.AmmoCaliber} class={shot.WeaponClass} " +
                    $"energy={0.5f * shot.BulletMassGram / 1000f * shot.MuzzleVelocity * shot.MuzzleVelocity:0}J " +
                    $"mods={shot.SummedModLoudness:+#;-#;0} suppressed={shot.IsSuppressed} " +
                    $"indoor={shot.IsIndoor} leftStance={shot.IsLeftStance} " +
                    $"headphones={shot.HeadphonesName} profile={_config.Preset.Value} " +
                    $"headphonesActive={shot.HasActiveHeadphones} headphonesSource={shot.HeadphonesSource} " +
                    $"mixerHeadphones={shot.MixerHeadphonesName} " +
                    $"headphonesThreshold={shot.HeadphonesCompressorThresholdDb:0.0}dB " +
                    $"headphonesProtection={result.HeadphonesProtectionDb:0.0}dB " +
                    $"headphoneBodyCut={state.AudioTuning.HeadphonesDamping.BodyAttenuationDb:0.0}dB " +
                    $"headphoneTailDecay={state.AudioTuning.HeadphonesDamping.TailDbPerSecond:0.0}dB/s " +
                    $"headphoneReverbCut={state.AudioTuning.HeadphonesDamping.ReverbAttenuationDb:0.0}dB " +
                    $"directBoost={state.DirectBoostDb:0.0}dB body={state.DirectBodyGain:0.00} " +
                    $"lowEndMode={tuning.LowEndMode} autoRoute={tuning.AutomaticPitchedRoute} " +
                    $"masterProbe={masterProbeId} bodyBand={bodyBandGain:0.00} " +
                    $"bodyCutoff={LocalGunshotImpactFilter.CalculateBodyUpperCutoff(bodyFrequency):0}Hz " +
                    $"pitchedLayer={pitchedLayerGain:0.00} " +
                    $"audioSamples={state.TunedAudioSamples} severity={result.FinalSeverity:0.000} " +
                    $"doseL={ExposureState.LeftDose:0.000} doseR={ExposureState.RightDose:0.000}");
            }
        }

        internal void Shutdown()
        {
            if (_config != null) _config.Source.SettingChanged -= OnSettingChanged;
            ResetEffect();

            if (_processor != null)
            {
                _processor.ImmediateBypass();
                Destroy(_processor);
                _processor = null;
            }
            _listener = null;
        }

        private void Update()
        {
            using (PerformanceTrace.Measure(PerformanceArea.Hearing))
            {
                if (_config == null)
                {
                    return;
                }

                EnsureListenerProcessor();

                if (!_reportedCompatibility && _config.DiagnosticShotLog.Value)
                {
                    _reportedCompatibility = true;
                    Plugin.Log.LogInfo(
                        ShotDescriptorFactory.CompatibilityAvailable
                            ? "compatibility: BaseSoundPlayer.playersBridge resolved"
                            : "compatibility: BaseSoundPlayer.playersBridge missing; local-shot modules are disabled");
                }

                bool enabled = _config.Enabled.Value;
                if (!enabled)
                {
                    if (_wasEnabled)
                    {
                        ResetEffect();
                    }
                    _wasEnabled = false;
                    return;
                }
                _wasEnabled = true;

                TuningSnapshot tuning = CurrentTuning();
                DecayDose(Time.unscaledDeltaTime, tuning);
                ApplyProcessorTargets(tuning);
                DrainMasterProbeLogs();
            }
        }

        private void DrainMasterProbeLogs()
        {
            if (_processor == null || !_config.DiagnosticShotLog.Value)
            {
                return;
            }

            while (_processor.TryTakeMasterProbe(out ListenerBandTelemetry telemetry))
            {
                Plugin.Log.LogInfo(
                    $"listener audio probe={telemetry.ProbeId} stage=post-hearing " +
                    $"autoBank={telemetry.AutomaticBank} autoRoute={telemetry.Route} " +
                    $"windowAnchor={telemetry.WindowAnchor} concurrentAudio={telemetry.IncludesConcurrentAudio} " +
                    $"channelScope={telemetry.ChannelScope} channelsMeasured={telemetry.ChannelsMeasured} " +
                    $"filterResetPerBout={telemetry.FilterStateResetAtWindowBout} initialTransient={telemetry.IncludesInitialFilterTransient} " +
                    $"frames={telemetry.Frames} " +
                    $"preClampSamplePeak={telemetry.PreClampSamplePeak:0.000000} " +
                    $"postClampSamplePeak={telemetry.PostClampSamplePeak:0.000000} " +
                    $"preClampOver={telemetry.PreClampOverCount} postClampOver={telemetry.PostClampOverCount} " +
                    $"nonFinite={telemetry.NonFiniteCount} " +
                    $"band20-80={ToDb(telemetry.Rms20To80):0.0}dBFS " +
                    $"band80-160={ToDb(telemetry.Rms80To160):0.0}dBFS " +
                    $"band160-315={ToDb(telemetry.Rms160To315):0.0}dBFS " +
                    $"band315-2000={ToDb(telemetry.Rms315To2000):0.0}dBFS " +
                    $"total={ToDb(telemetry.RmsTotal):0.0}dBFS");
            }
        }

        private static float ToDb(float rms)
        {
            return 20f * Mathf.Log10(Mathf.Max(0.000000001f, rms));
        }

        private void EnsureListenerProcessor(bool force = false)
        {
            if (_listener != null && _processor != null)
            {
                return;
            }

            if (!force && Time.unscaledTime < _nextListenerSearchTime)
            {
                return;
            }
            _nextListenerSearchTime = Time.unscaledTime + 0.5f;

            _listener = AudioRuntimeLookup.Listener;
            if (_listener == null)
            {
                _processor = null;
                return;
            }

            _processor = _listener.GetComponent<HearingImpactProcessor>();
            if (_processor == null)
            {
                _processor = _listener.gameObject.AddComponent<HearingImpactProcessor>();
            }

            if (_config.DiagnosticShotLog.Value)
            {
                Plugin.Log.LogInfo($"listener processor attached to {_listener.gameObject.name}");
            }
        }

        private void DecayDose(float deltaTime, TuningSnapshot tuning)
        {
            ExposureState.Advance(deltaTime, tuning);
        }

        private void ApplyProcessorTargets(TuningSnapshot tuning)
        {
            if (_processor == null)
            {
                return;
            }

            HearingResponse response = HearingResponseModel.Calculate(
                ExposureState.LeftDose,
                ExposureState.RightDose,
                tuning,
                AudioSettings.outputSampleRate);

            _processor.SetTargets(
                response.ProcessingActive,
                response.HearingLeft,
                response.HearingRight,
                response.AttenuationLeftDb,
                response.AttenuationRightDb,
                response.CutoffLeftHz,
                response.CutoffRightHz,
                response.TinnitusLeft,
                response.TinnitusRight,
                tuning.TinnitusFrequencyHz,
                tuning.TinnitusPitchSpreadHz,
                AudioSettings.outputSampleRate);
        }

        private void ResetEffect()
        {
            ExposureState.Reset();
            _processor?.ImmediateBypass();
        }

        private void ConsumePendingReset()
        {
            if (Interlocked.Exchange(ref _resetExposureDirty, 0) != 0)
            {
                ResetEffect();
            }
        }

        private HearingExposureState ExposureState =>
            _exposure ?? (_exposure = new HearingExposureState());
    }
}
