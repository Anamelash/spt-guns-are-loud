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
        private readonly BlastExposureState _blasts = new BlastExposureState();
        private object _blastPlayer;
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
            if (!_config.Enabled.Value) Interlocked.Exchange(ref _resetExposureDirty, 1);
            else if (!ModConfig.IsResponseEnabled(ModConfig.NormalizeResponseControl(_config.HearingTrauma.Value)) &&
                !ModConfig.IsResponseEnabled(ModConfig.NormalizeResponseControl(_config.Ringing.Value)))
                Interlocked.CompareExchange(ref _resetExposureDirty, 2, 0);

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

        internal void HandleExplosion(float distance, bool sourceIndoor, Vector3 position)
        {
            var audio = AudioRuntimeLookup.Audio;
            var player = audio?.ListenerPlayer;
            if (_config == null || !_config.Enabled.Value || player == null || !player.IsYourPlayer) return;
            if (!ReferenceEquals(_blastPlayer, player)) { _blasts?.Reset(); _blastPlayer = player; }
            EnsureListenerProcessor(force: true);
            float protection = 0;
            var item = HeadphonesResolver.FindEquippedItem(player.Equipment);
            if (item != null)
            {
                if (HeadsetProfileRegistry.TryGet(item.TemplateId, out var profile))
                {
                    // Blast weighting is a low-band energy estimate, not NRR or the electronic limiter.
                    float energy = 0; int count = 0;
                    for (int i = 0; i < profile.Passive.BandCount; i++)
                        if (profile.Passive.FrequencyAt(i) <= 500)
                        { energy += Mathf.Pow(10, -profile.Passive.MeanAttenuationAt(i) / 10); count++; }
                    if (count > 0) protection = -10 * Mathf.Log10(Mathf.Max(.000001f, energy / count));
                }
                else protection = 10; // Explicit conservative estimate for unregistered worn protection.
            }
            // An indoor source must not give an outdoor listener indoor propagation.
            bool indoor = sourceIndoor && player.Environment == EnvironmentType.Indoor;
            float severity = BlastExposureState.Severity(distance, indoor, _config.BlastRadius.Value,
                _config.BlastIndoorScale.Value, protection);
            float transmission = 1;
            if (severity > .001f)
            {
                var head = player.PlayerBones?.Head?.Original;
                Vector3 center = head != null ? head.position : (_listener != null ? _listener.transform.position : player.Position + Vector3.up * 1.6f);
                transmission = BlastOcclusion.Transmission(position, center, player.Transform.Original);
                severity *= transmission;
            }
            // Preserve the first 120 ms of the arriving bang, then ramp physiology over 100 ms.
            _blasts.Add(Time.unscaledTime + Mathf.Max(0, distance) / 340f + .12f, severity,
                _config.BlastHearingDuration.Value, _config.BlastRingingDuration.Value, _config.BlastSevereDuration.Value, .1f);
            if (_config.DiagnosticShotLog.Value)
                Plugin.Log.LogInfo($"blast exposure distance={distance:0.0}m sourceIndoor={sourceIndoor} listenerIndoor={player.Environment == EnvironmentType.Indoor} indoor={indoor} transmission={transmission:0.000} protection={protection:0.0}dB severity={severity:0.000} severe={severity >= .999f} severeSeconds={_config.BlastSevereDuration.Value:0}");
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

                var currentPlayer = AudioRuntimeLookup.Audio?.ListenerPlayer;
                if (currentPlayer == null || !currentPlayer.IsYourPlayer || !ReferenceEquals(currentPlayer, _blastPlayer))
                { _blasts?.Reset(); _blastPlayer = currentPlayer; }
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

            var blast = _blasts.Sample(Time.unscaledTime);
            float loss = Mathf.Clamp01(blast.Hearing * _config.BlastHearingStrength.Value / 100f);
            float ring = Mathf.Min(.02f, blast.Ringing * .008f * _config.BlastRingingStrength.Value / 100f);
            float attenuation = (blast.Severe ? 60f : 35f) * loss;
            float cutoff = Mathf.Lerp(AudioSettings.outputSampleRate * .49f, 350f, loss);
            _processor.SetTargets(
                response.ProcessingActive || loss > .0001f || ring > .000001f,
                loss > 0 ? 1f : response.HearingLeft,
                loss > 0 ? 1f : response.HearingRight,
                Mathf.Max(response.AttenuationLeftDb, attenuation),
                Mathf.Max(response.AttenuationRightDb, attenuation),
                Mathf.Min(response.CutoffLeftHz, cutoff),
                Mathf.Min(response.CutoffRightHz, cutoff),
                Mathf.Max(response.TinnitusLeft, ring),
                Mathf.Max(response.TinnitusRight, ring),
                tuning.TinnitusFrequencyHz,
                tuning.TinnitusPitchSpreadHz,
                AudioSettings.outputSampleRate);
        }

        private void ResetEffect()
        {
            ExposureState.Reset();
            _blasts?.Reset();
            _blastPlayer = null;
            _processor?.ImmediateBypass();
        }

        private void ConsumePendingReset()
        {
            int reset = Interlocked.Exchange(ref _resetExposureDirty, 0);
            if (reset == 1) ResetEffect();
            else if (reset == 2) ExposureState.Reset();
        }

        private HearingExposureState ExposureState =>
            _exposure ?? (_exposure = new HearingExposureState());
    }
}
