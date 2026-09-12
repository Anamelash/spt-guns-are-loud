using Audio.ReverbSubsystem;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal static class LocalGunshotPlaybackProbe
    {
        internal static void Schedule(
            SuperSource source,
            string clipName,
            double scheduledStart,
            AutomaticPitchedRoute route,
            DiagnosticShotToken diagnosticShot)
        {
            if (source == null || !DetailedDiagnostics.IsActive(diagnosticShot))
                return;
            LocalGunshotPlaybackProbeScheduler.Instance?.TrySchedule(
                source, clipName, scheduledStart, route, diagnosticShot);
        }
    }

    /// <summary>
    /// One main-thread scheduler owns a fixed set of short-lived inspection jobs.
    /// Per-shot MonoBehaviour creation/destruction is intentionally avoided.
    /// </summary>
    internal sealed class LocalGunshotPlaybackProbeScheduler : MonoBehaviour
    {
        private const int MaximumPendingProbes = 16;
        private readonly ProbeJob[] _jobs = new ProbeJob[MaximumPendingProbes];
        private int _pending;

        internal static LocalGunshotPlaybackProbeScheduler Instance { get; private set; }
        internal int PendingCount => _pending;
        internal int Capacity => _jobs.Length;

        private void Awake()
        {
            Instance = this;
        }

        internal bool TrySchedule(
            SuperSource source,
            string clipName,
            double scheduledStart,
            AutomaticPitchedRoute route,
            DiagnosticShotToken diagnosticShot)
        {
            if (source == null || !DetailedDiagnostics.IsActive(diagnosticShot))
                return false;
            for (int index = 0; index < _jobs.Length; index++)
            {
                if (_jobs[index].Active) continue;
                _jobs[index] = ProbeJob.Create(
                    source, clipName, scheduledStart, route, diagnosticShot);
                _pending++;
                return true;
            }

            if (DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.Warning, out DiagnosticReservation reservation))
                DetailedDiagnostics.Commit(
                    reservation,
                    "playback probe capacity reached; newest probe dropped");
            return false;
        }

        private void Update()
        {
            if (_pending == 0) return;
            if (!DetailedDiagnostics.Enabled ||
                AudioRuntimeLookup.Audio?.ListenerPlayer == null)
            {
                Clear();
                return;
            }

            double now = AudioSettings.dspTime;
            for (int index = 0; index < _jobs.Length; index++)
            {
                if (!_jobs[index].Active || now < _jobs[index].InspectAt) continue;
                ProbeJob job = _jobs[index];
                _jobs[index] = default;
                _pending--;
                try { job.Inspect(); }
                catch (System.Exception error)
                {
                    if (DetailedDiagnostics.TryBegin(
                        DiagnosticEventKind.Warning, out DiagnosticReservation reservation))
                        DetailedDiagnostics.Commit(
                            reservation,
                            "playback probe failed: " + error.GetType().Name + ": " + error.Message);
                }
            }
        }

        internal void Clear()
        {
            if (_pending == 0) return;
            System.Array.Clear(_jobs, 0, _jobs.Length);
            _pending = 0;
        }

        private void OnDestroy()
        {
            Clear();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private struct ProbeJob
        {
            internal bool Active;
            internal double InspectAt;
            private SuperSource _source;
            private ReverbSuperSource _reverbSource;
            private AutomaticPitchedGunshotFilter _automaticFilterA;
            private AutomaticPitchedGunshotFilter _automaticFilterB;
            private AutomaticBeatCapture _captureA;
            private AutomaticBeatCapture _captureB;
            private string _clipName;
            private AutomaticPitchedRoute _route;
            private DiagnosticShotToken _diagnosticShot;

            internal static ProbeJob Create(
                SuperSource source,
                string clipName,
                double scheduledStart,
                AutomaticPitchedRoute route,
                DiagnosticShotToken diagnosticShot)
            {
                GalSourceBinding binding = GalSourceBinding.Find(source);
                return new ProbeJob
                {
                    Active = true,
                    _source = source,
                    _reverbSource = source as ReverbSuperSource,
                    _automaticFilterA = binding?.PitchedFilter(source.source1),
                    _automaticFilterB = binding?.PitchedFilter(source.source2),
                    _captureA = binding?.Capture(source.source1),
                    _captureB = binding?.Capture(source.source2),
                    _clipName = clipName,
                    InspectAt = System.Math.Max(
                        scheduledStart + 0.060,
                        AudioSettings.dspTime + 0.060),
                    _route = route,
                    _diagnosticShot = diagnosticShot
                };
            }

            internal void Inspect()
            {
                if (!DetailedDiagnostics.IsActive(_diagnosticShot)) return;
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
                // Every copy now plays from the one shared pool, whatever its route.
                PitchedGunshotLayer pitchedLayer = PitchedGunshotLayer.ReportPool;
                PitchedLayerTelemetry pitched = pitchedLayer != null
                    ? pitchedLayer.GetTelemetry()
                    : default;
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

                DetailedDiagnostics.Commit(
                    _diagnosticShot,
                    DiagnosticEventKind.PlaybackProbe,
                    $"audio playback clip={_clipName} mixer={mixer} " +
                    $"drySpatialBlend={(_source?.source1 != null ? _source.source1.spatialBlend : -1f):0.00} " +
                    $"spatialization={(_source != null && _source.EnableSpatialization)} " +
                    $"wetScheduledA={wetScheduledA} wetScheduledB={wetScheduledB} " +
                    $"wetPlayingA={wetPlayingA} wetPlayingB={wetPlayingB} " +
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
                    $"cachedBeat={pitched.CachedAutomaticCopy} " +
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
            }
        }
    }
}
