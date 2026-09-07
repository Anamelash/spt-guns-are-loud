using Audio.ReverbSubsystem;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// Main-thread diagnostic probe. It waits until after the scheduled onset so
    /// diagnostics describe actual source/filter activity rather than configured intent.
    /// </summary>
    internal sealed class LocalGunshotPlaybackProbe : MonoBehaviour
    {
        private SuperSource _source;
        private ReverbSuperSource _reverbSource;
        private LocalGunshotImpactFilter _filterA;
        private LocalGunshotImpactFilter _filterB;
        private AutomaticPitchedGunshotFilter _automaticFilterA;
        private AutomaticPitchedGunshotFilter _automaticFilterB;
        private AutomaticBeatCapture _captureA;
        private AutomaticBeatCapture _captureB;
        private string _clipName;
        private double _inspectAt;
        private AutomaticPitchedRoute _route;

        internal static void Schedule(
            SuperSource source,
            string clipName,
            double scheduledStart,
            AutomaticPitchedRoute route)
        {
            if (source == null || Plugin.ModConfig?.DiagnosticShotLog.Value != true)
            {
                return;
            }

            LocalGunshotPlaybackProbe probe = source.gameObject.AddComponent<LocalGunshotPlaybackProbe>();
            probe._source = source;
            probe._reverbSource = source as ReverbSuperSource;
            probe._filterA = source.source1?.GetComponent<LocalGunshotImpactFilter>();
            probe._filterB = source.source2?.GetComponent<LocalGunshotImpactFilter>();
            probe._automaticFilterA =
                source.source1?.GetComponent<AutomaticPitchedGunshotFilter>();
            probe._automaticFilterB =
                source.source2?.GetComponent<AutomaticPitchedGunshotFilter>();
            probe._captureA = source.source1?.GetComponent<AutomaticBeatCapture>();
            probe._captureB = source.source2?.GetComponent<AutomaticBeatCapture>();
            probe._clipName = clipName;
            probe._inspectAt = System.Math.Max(scheduledStart + 0.060, AudioSettings.dspTime + 0.060);
            probe._route = route;
        }

        private void Update()
        {
            if (AudioSettings.dspTime < _inspectAt)
            {
                return;
            }

            GunshotFilterTelemetry filterA = _filterA != null ? _filterA.GetTelemetry() : default;
            GunshotFilterTelemetry filterB = _filterB != null ? _filterB.GetTelemetry() : default;
            AutomaticPitchedTelemetry automaticA = _automaticFilterA != null
                ? _automaticFilterA.GetTelemetry()
                : default;
            AutomaticPitchedTelemetry automaticB = _automaticFilterB != null
                ? _automaticFilterB.GetTelemetry()
                : default;
            AutomaticBeatCaptureTelemetry captureA = _captureA != null
                ? _captureA.GetTelemetry()
                : default;
            AutomaticBeatCaptureTelemetry captureB = _captureB != null
                ? _captureB.GetTelemetry()
                : default;
            PitchedGunshotLayer pitchedLayer = _source?.GetComponent<PitchedGunshotLayer>();
            if (_source?.GetComponent<AutomaticBeatTimeline>()?.LastUsesAuthoredTail == true)
                pitchedLayer = PitchedGunshotLayer.ReportPool;
            PitchedLayerTelemetry pitched = pitchedLayer != null
                ? pitchedLayer.GetTelemetry()
                : default;
            int bodyCallbacks = filterA.CallbackCount + filterB.CallbackCount;
            float inputPeak = Mathf.Max(filterA.InputPeak, filterB.InputPeak);
            float addedPeak = Mathf.Max(filterA.AddedPeak, filterB.AddedPeak);
            string mixer = _source?.source1?.outputAudioMixerGroup != null
                ? _source.source1.outputAudioMixerGroup.name
                : "null";

            bool wetScheduledA = _reverbSource != null &&
                _reverbSource._reverbSourceA != null &&
                _reverbSource._reverbSourceA.enabled &&
                _reverbSource._reverbSourceA.clip != null;
            bool wetScheduledB = _reverbSource != null &&
                _reverbSource._reverbSourceB != null &&
                _reverbSource._reverbSourceB.enabled &&
                _reverbSource._reverbSourceB.clip != null;
            bool wetPlayingA = wetScheduledA && _reverbSource._reverbSourceA.isPlaying;
            bool wetPlayingB = wetScheduledB && _reverbSource._reverbSourceB.isPlaying;

            Plugin.Log.LogInfo(
                $"audio playback clip={_clipName} mixer={mixer} " +
                $"drySpatialBlend={(_source?.source1 != null ? _source.source1.spatialBlend : -1f):0.00} " +
                $"spatialization={(_source != null && _source.EnableSpatialization)} " +
                $"wetScheduledA={wetScheduledA} wetScheduledB={wetScheduledB} " +
                $"wetPlayingA={wetPlayingA} wetPlayingB={wetPlayingB} " +
                $"bodyCallbacks={bodyCallbacks} inputPeak={inputPeak:0.000} addedPeak={addedPeak:0.000} " +
                $"autoRoute={_route} " +
                $"autoDspCallbacks={automaticA.CallbackCount + automaticB.CallbackCount} " +
                $"autoOutputCallbacks={automaticA.OutputCallbackCount + automaticB.OutputCallbackCount} " +
                $"autoDspTriggers={automaticA.TriggerCount + automaticB.TriggerCount} " +
                $"autoDspOnsets={automaticA.OnsetCount + automaticB.OnsetCount} " +
                $"autoDspVoices={automaticA.ActiveVoices + automaticB.ActiveVoices} " +
                $"autoDspInputPeak={Mathf.Max(automaticA.InputPeak, automaticB.InputPeak):0.000} " +
                $"autoDspAddedPeak={Mathf.Max(automaticA.AddedPeak, automaticB.AddedPeak):0.000} " +
                $"autoDspPitch={Mathf.Max(automaticA.PitchRatio, automaticB.PitchRatio):0.000} " +
                $"autoSourceSpan={Mathf.Max(automaticA.SourceSpanMs, automaticB.SourceSpanMs):0}ms " +
                $"autoOutputDuration={Mathf.Max(automaticA.OutputDurationMs, automaticB.OutputDurationMs):0}ms " +
                $"pitchedScheduled={pitched.Scheduled} pitchedPlaying={pitched.Playing} " +
                $"cachedBeat={pitched.CachedAutomaticBeat} " +
                $"cacheArmed={captureA.Armed || captureB.Armed} " +
                $"cacheReady={captureA.Ready || captureB.Ready} " +
                $"cacheFrames={captureA.CapturedFrames + captureB.CapturedFrames}/" +
                $"{captureA.TargetFrames + captureB.TargetFrames} " +
                $"pitchRatio={pitched.PitchRatio:0.000} " +
                $"pitchBand={pitched.HighpassHz:0}-{pitched.LowpassHz:0}Hz " +
                $"sourceSpan={pitched.OriginalDurationMs:0}ms " +
                $"outputDuration={pitched.OutputDurationMs:0}ms " +
                $"tailDuration={pitched.TailDurationMs:0}ms " +
                $"fade={pitched.FadePercent:0}% layerGain={pitched.Gain:0.00} " +
                $"occlusion={pitched.Occlusion:0.00} " +
                $"pitchCallbacks={pitched.CallbackCount} pitchInputPeak={pitched.InputPeak:0.000} " +
                $"pitchOutputPeak={pitched.OutputPeak:0.000} pitchOnset={pitched.OnsetTriggered}");

            Destroy(this);
        }
    }
}
