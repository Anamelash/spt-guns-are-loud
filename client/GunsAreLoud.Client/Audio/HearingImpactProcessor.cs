using System;
using System.Threading;
using GunsAreLoud.Client.Configuration;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal enum ListenerProbeWindowAnchor
    {
        ShotHandlerCallback
    }

    internal enum ListenerProbeChannelScope
    {
        FrontPair
    }

    internal readonly struct ListenerBandTelemetry
    {
        internal readonly int ProbeId;
        internal readonly AutomaticPitchedRoute Route;
        internal readonly bool AutomaticBank;
        internal readonly int Frames;
        internal readonly float Rms20To80;
        internal readonly float Rms80To160;
        internal readonly float Rms160To315;
        internal readonly float Rms315To2000;
        internal readonly float RmsTotal;
        internal readonly float PreClampSamplePeak;
        internal readonly float PostClampSamplePeak;
        internal readonly int PreClampOverCount;
        internal readonly int PostClampOverCount;
        internal readonly int NonFiniteCount;
        internal readonly ListenerProbeWindowAnchor WindowAnchor;
        internal readonly bool IncludesConcurrentAudio;
        internal readonly int ChannelsMeasured;
        internal readonly ListenerProbeChannelScope ChannelScope;
        internal readonly bool FilterStateResetAtWindowBout;
        internal readonly bool IncludesInitialFilterTransient;

        internal ListenerBandTelemetry(
            int probeId,
            AutomaticPitchedRoute route,
            bool automaticBank,
            int frames,
            float rms20To80,
            float rms80To160,
            float rms160To315,
            float rms315To2000,
            float rmsTotal,
            float preClampSamplePeak,
            float postClampSamplePeak,
            int preClampOverCount,
            int postClampOverCount,
            int nonFiniteCount,
            int channelsMeasured)
        {
            ProbeId = probeId;
            Route = route;
            AutomaticBank = automaticBank;
            Frames = frames;
            Rms20To80 = rms20To80;
            Rms80To160 = rms80To160;
            Rms160To315 = rms160To315;
            Rms315To2000 = rms315To2000;
            RmsTotal = rmsTotal;
            PreClampSamplePeak = preClampSamplePeak;
            PostClampSamplePeak = postClampSamplePeak;
            PreClampOverCount = preClampOverCount;
            PostClampOverCount = postClampOverCount;
            NonFiniteCount = nonFiniteCount;
            WindowAnchor = ListenerProbeWindowAnchor.ShotHandlerCallback;
            IncludesConcurrentAudio = true;
            ChannelsMeasured = channelsMeasured;
            ChannelScope = ListenerProbeChannelScope.FrontPair;
            FilterStateResetAtWindowBout = true;
            IncludesInitialFilterTransient = true;
        }
    }

    /// <summary>
    /// Listener-stage physiological effects only. Gunshot character is processed
    /// on EFT's tagged weapon sources so this stage never introduces a synthetic tone.
    /// </summary>
    internal sealed class HearingImpactProcessor : MonoBehaviour
    {
        private const int MaximumDiagnosticProbes = 8;
        private const float DiagnosticWindowSeconds = 0.24f;

        private MasterProbe[] _masterProbes = CreateMasterProbes();
        private volatile bool _hearingEnabled;
        private volatile float _targetWetLeft;
        private volatile float _targetWetRight;
        private volatile float _targetGainLeft = 1f;
        private volatile float _targetGainRight = 1f;
        private volatile float _targetAlphaLeft = 1f;
        private volatile float _targetAlphaRight = 1f;
        private volatile float _targetTinnitusLeft;
        private volatile float _targetTinnitusRight;
        private volatile float _targetTinnitusFrequency = 6200f;
        private volatile float _targetTinnitusSpread = 37f;
        private volatile int _targetSampleRate = 48000;

        private HearingDspChannelState _leftState;
        private HearingDspChannelState _rightState;
        private double _phaseLeft;
        private double _phaseRight;
        private int _nextProbeId;
        private int _activeProbeCount;
        private int _probeResetGeneration;
        private int _audioProbeResetGeneration;
        private int _lastChannelLayout;
        private int _bypassGeneration;
        private int _audioBypassGeneration;
        private float _probeLp20Left;
        private float _probeLp20Right;
        private float _probeLp80Left;
        private float _probeLp80Right;
        private float _probeLp160Left;
        private float _probeLp160Right;
        private float _probeLp315Left;
        private float _probeLp315Right;
        private float _probeLp2000Left;
        private float _probeLp2000Right;

        internal void SetTargets(
            bool enabled,
            float wetLeft,
            float wetRight,
            float attenuationDbLeft,
            float attenuationDbRight,
            float cutoffHzLeft,
            float cutoffHzRight,
            float tinnitusLeft,
            float tinnitusRight,
            float tinnitusFrequency,
            float tinnitusSpread,
            int outputSampleRate)
        {
            _targetWetLeft = Mathf.Clamp01(wetLeft);
            _targetWetRight = Mathf.Clamp01(wetRight);
            _targetGainLeft = HearingDspTransfer.DbToLinear(-Mathf.Max(0f, attenuationDbLeft));
            _targetGainRight = HearingDspTransfer.DbToLinear(-Mathf.Max(0f, attenuationDbRight));
            _targetAlphaLeft = HearingDspTransfer.CutoffToAlpha(cutoffHzLeft, outputSampleRate);
            _targetAlphaRight = HearingDspTransfer.CutoffToAlpha(cutoffHzRight, outputSampleRate);
            _targetTinnitusLeft = Mathf.Max(0f, tinnitusLeft);
            _targetTinnitusRight = Mathf.Max(0f, tinnitusRight);
            _targetTinnitusFrequency = Mathf.Max(20f, tinnitusFrequency);
            _targetTinnitusSpread = Mathf.Max(0f, tinnitusSpread);
            _targetSampleRate = Math.Max(8000, outputSampleRate);
            _hearingEnabled = enabled;
        }

        internal void ImmediateBypass()
        {
            _hearingEnabled = false;
            _targetWetLeft = 0f;
            _targetWetRight = 0f;
            _targetGainLeft = 1f;
            _targetGainRight = 1f;
            _targetAlphaLeft = 1f;
            _targetAlphaRight = 1f;
            _targetTinnitusLeft = 0f;
            _targetTinnitusRight = 0f;
            Interlocked.Increment(ref _bypassGeneration);
        }

        internal int ArmMasterProbe(
            AutomaticPitchedRoute route,
            bool automaticBank,
            int outputSampleRate)
        {
            EnsureMasterProbes();
            int probeId = Interlocked.Increment(ref _nextProbeId);
            int targetFrames = Mathf.Max(
                1,
                Mathf.RoundToInt(Mathf.Max(8000, outputSampleRate) * DiagnosticWindowSeconds));
            for (int index = 0; index < _masterProbes.Length; index++)
            {
                MasterProbe probe = _masterProbes[index];
                if (Interlocked.CompareExchange(ref probe.State, -1, 0) != 0)
                {
                    continue;
                }

                probe.Reset(probeId, route, automaticBank, targetFrames);
                if (Interlocked.Increment(ref _activeProbeCount) == 1)
                {
                    Interlocked.Increment(ref _probeResetGeneration);
                }
                Volatile.Write(ref probe.State, 1);
                return probeId;
            }
            return 0;
        }

        internal bool TryTakeMasterProbe(out ListenerBandTelemetry telemetry)
        {
            EnsureMasterProbes();
            for (int index = 0; index < _masterProbes.Length; index++)
            {
                MasterProbe probe = _masterProbes[index];
                if (Volatile.Read(ref probe.State) != 2)
                {
                    continue;
                }

                int frames = Math.Max(1, probe.Frames);
                telemetry = new ListenerBandTelemetry(
                    probe.ProbeId,
                    probe.Route,
                    probe.AutomaticBank,
                    probe.Frames,
                    (float)Math.Sqrt(probe.Sum20To80 / frames),
                    (float)Math.Sqrt(probe.Sum80To160 / frames),
                    (float)Math.Sqrt(probe.Sum160To315 / frames),
                    (float)Math.Sqrt(probe.Sum315To2000 / frames),
                    (float)Math.Sqrt(probe.SumTotal / frames),
                    probe.PeakWindow.Measurement.PreClampSamplePeak,
                    probe.PeakWindow.Measurement.PostClampSamplePeak,
                    probe.PeakWindow.Measurement.PreClampOverCount,
                    probe.PeakWindow.Measurement.PostClampOverCount,
                    probe.PeakWindow.Measurement.NonFiniteCount,
                    probe.ChannelsMeasured);
                Volatile.Write(ref probe.State, 0);
                return true;
            }

            telemetry = default;
            return false;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (data == null || data.Length == 0 || channels <= 0)
            {
                return;
            }

            int sampleRate = Math.Max(8000, _targetSampleRate);
            int bypassGeneration = Volatile.Read(ref _bypassGeneration);
            if (_audioBypassGeneration != bypassGeneration)
            {
                _audioBypassGeneration = bypassGeneration;
                ResetHearingAudioThreadState();
            }
            bool hearingEnabled = _hearingEnabled;
            bool probesArmed = Volatile.Read(ref _activeProbeCount) > 0;
            if (!hearingEnabled && !probesArmed)
            {
                ResetHearingAudioThreadState();
                return;
            }
            if (!hearingEnabled)
            {
                ResetHearingAudioThreadState();
            }

            float smoothing = hearingEnabled
                ? 1f - (float)Math.Exp(-1.0 / (sampleRate * 0.004))
                : 0f;
            float targetFrequency = FiniteOr(_targetTinnitusFrequency, 6200f);
            float targetSpread = Math.Max(0f, FiniteOr(_targetTinnitusSpread, 37f));
            double frequencyLeft = Math.Max(20.0, targetFrequency - targetSpread * 0.5f);
            double frequencyRight = Math.Max(20.0, targetFrequency + targetSpread * 0.5f);
            double phaseStepLeft = Math.PI * 2.0 * frequencyLeft / sampleRate;
            double phaseStepRight = Math.PI * 2.0 * frequencyRight / sampleRate;
            int channelLayout = channels == 1 ? 1 : 2;
            if (hearingEnabled && _lastChannelLayout != 0 && _lastChannelLayout != channelLayout)
            {
                ResetHearingAudioThreadState();
            }
            _lastChannelLayout = channelLayout;
            if (probesArmed) ConsumeProbeResetGeneration();
            float probeAlpha20 = probesArmed ? HearingDspTransfer.CutoffToAlpha(20f, sampleRate) : 0f;
            float probeAlpha80 = probesArmed ? HearingDspTransfer.CutoffToAlpha(80f, sampleRate) : 0f;
            float probeAlpha160 = probesArmed ? HearingDspTransfer.CutoffToAlpha(160f, sampleRate) : 0f;
            float probeAlpha315 = probesArmed ? HearingDspTransfer.CutoffToAlpha(315f, sampleRate) : 0f;
            float probeAlpha2000 = probesArmed ? HearingDspTransfer.CutoffToAlpha(2000f, sampleRate) : 0f;

            for (int frame = 0; frame < data.Length; frame += channels)
            {
                float preClampLeft = data[frame];
                float preClampRight = channels > 1 ? data[frame + 1] : preClampLeft;
                if (hearingEnabled)
                {
                    if (double.IsNaN(_phaseLeft) || double.IsInfinity(_phaseLeft)) _phaseLeft = 0.0;
                    if (double.IsNaN(_phaseRight) || double.IsInfinity(_phaseRight)) _phaseRight = 0.0;
                    ApplyHearingEffects(data, frame, channels, smoothing, out preClampLeft, out preClampRight);

                    _phaseLeft += phaseStepLeft;
                    _phaseRight += phaseStepRight;
                    if (_phaseLeft >= Math.PI * 2.0)
                    {
                        _phaseLeft -= Math.PI * 2.0;
                    }
                    if (_phaseRight >= Math.PI * 2.0)
                    {
                        _phaseRight -= Math.PI * 2.0;
                    }
                }

                if (probesArmed) AccumulateMasterProbe(
                    data,
                    frame,
                    channels,
                    preClampLeft,
                    preClampRight,
                    probeAlpha20,
                    probeAlpha80,
                    probeAlpha160,
                    probeAlpha315,
                    probeAlpha2000);
            }
        }

        private void AccumulateMasterProbe(
            float[] data,
            int frame,
            int channels,
            float preClampLeft,
            float preClampRight,
            float alpha20,
            float alpha80,
            float alpha160,
            float alpha315,
            float alpha2000)
        {
            ConsumeProbeResetGeneration();
            float left = data[frame];
            float right = channels > 1 ? data[frame + 1] : left;
            float diagnosticLeft = IsFinite(left) ? left : 0f;
            float diagnosticRight = channels > 1
                ? (IsFinite(right) ? right : 0f)
                : diagnosticLeft;

            _probeLp20Left += alpha20 * (diagnosticLeft - _probeLp20Left);
            _probeLp20Right += alpha20 * (diagnosticRight - _probeLp20Right);
            _probeLp80Left += alpha80 * (diagnosticLeft - _probeLp80Left);
            _probeLp80Right += alpha80 * (diagnosticRight - _probeLp80Right);
            _probeLp160Left += alpha160 * (diagnosticLeft - _probeLp160Left);
            _probeLp160Right += alpha160 * (diagnosticRight - _probeLp160Right);
            _probeLp315Left += alpha315 * (diagnosticLeft - _probeLp315Left);
            _probeLp315Right += alpha315 * (diagnosticRight - _probeLp315Right);
            _probeLp2000Left += alpha2000 * (diagnosticLeft - _probeLp2000Left);
            _probeLp2000Right += alpha2000 * (diagnosticRight - _probeLp2000Right);

            float band20To80Left = _probeLp80Left - _probeLp20Left;
            float band20To80Right = _probeLp80Right - _probeLp20Right;
            float band80To160Left = _probeLp160Left - _probeLp80Left;
            float band80To160Right = _probeLp160Right - _probeLp80Right;
            float band160To315Left = _probeLp315Left - _probeLp160Left;
            float band160To315Right = _probeLp315Right - _probeLp160Right;
            float band315To2000Left = _probeLp2000Left - _probeLp315Left;
            float band315To2000Right = _probeLp2000Right - _probeLp315Right;

            double energy20To80 = StereoEnergy(band20To80Left, band20To80Right);
            double energy80To160 = StereoEnergy(band80To160Left, band80To160Right);
            double energy160To315 = StereoEnergy(band160To315Left, band160To315Right);
            double energy315To2000 = StereoEnergy(band315To2000Left, band315To2000Right);
            double totalEnergy = StereoEnergy(diagnosticLeft, diagnosticRight);
            for (int index = 0; index < _masterProbes.Length; index++)
            {
                MasterProbe probe = _masterProbes[index];
                if (Volatile.Read(ref probe.State) != 1)
                {
                    continue;
                }

                probe.Sum20To80 += energy20To80;
                probe.Sum80To160 += energy80To160;
                probe.Sum160To315 += energy160To315;
                probe.Sum315To2000 += energy315To2000;
                probe.SumTotal += totalEnergy;
                int channelsMeasured = Math.Min(channels, 2);
                probe.ChannelsMeasured = Math.Max(probe.ChannelsMeasured, channelsMeasured);
                probe.PeakWindow.AddFrame(
                    preClampLeft,
                    left,
                    preClampRight,
                    right,
                    channelsMeasured);
                if (probe.PeakWindow.Complete)
                {
                    Volatile.Write(ref probe.State, 2);
                    Interlocked.Decrement(ref _activeProbeCount);
                }
            }
        }

        private static double StereoEnergy(float left, float right)
        {
            return ((double)left * left + (double)right * right) * 0.5;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static float FiniteOr(float value, float fallback) =>
            IsFinite(value) ? value : fallback;

        private void ApplyHearingEffects(
            float[] data,
            int frame,
            int channels,
            float smoothing,
            out float preClampLeft,
            out float preClampRight)
        {
            if (channels == 1)
            {
                float dry = data[frame];
                float ring = (float)Math.Sin(_phaseLeft);
                data[frame] = _leftState.Process(
                    dry,
                    (_targetWetLeft + _targetWetRight) * 0.5f,
                    (_targetGainLeft + _targetGainRight) * 0.5f,
                    (_targetAlphaLeft + _targetAlphaRight) * 0.5f,
                    (_targetTinnitusLeft + _targetTinnitusRight) * 0.5f,
                    ring,
                    smoothing,
                    out preClampLeft);
                preClampRight = preClampLeft;
                return;
            }

            float dryLeft = data[frame];
            float dryRight = data[frame + 1];
            data[frame] = _leftState.Process(dryLeft, _targetWetLeft, _targetGainLeft,
                _targetAlphaLeft, _targetTinnitusLeft, (float)Math.Sin(_phaseLeft), smoothing,
                out preClampLeft);
            data[frame + 1] = _rightState.Process(dryRight, _targetWetRight, _targetGainRight,
                _targetAlphaRight, _targetTinnitusRight, (float)Math.Sin(_phaseRight), smoothing,
                out preClampRight);

            float surroundWet = (_leftState.Wet + _rightState.Wet) * 0.5f;
            float surroundGain = (_leftState.Gain + _rightState.Gain) * 0.5f;
            for (int channel = 2; channel < channels; channel++)
            {
                data[frame + channel] *= 1f + (surroundGain - 1f) * surroundWet;
            }
        }

        private void ResetHearingAudioThreadState()
        {
            _leftState.Reset();
            _rightState.Reset();
        }

        private void ResetProbeAudioThreadState()
        {
            _probeLp20Left = _probeLp20Right = 0f;
            _probeLp80Left = _probeLp80Right = 0f;
            _probeLp160Left = _probeLp160Right = 0f;
            _probeLp315Left = _probeLp315Right = 0f;
            _probeLp2000Left = _probeLp2000Right = 0f;
        }

        private void ConsumeProbeResetGeneration()
        {
            int generation = Volatile.Read(ref _probeResetGeneration);
            if (_audioProbeResetGeneration == generation) return;
            _audioProbeResetGeneration = generation;
            ResetProbeAudioThreadState();
        }

        internal void ProcessAudioBufferForTests(float[] data, int channels)
        {
            OnAudioFilterRead(data, channels);
        }

        private void EnsureMasterProbes()
        {
            if (_masterProbes == null)
            {
                _masterProbes = CreateMasterProbes();
            }
        }

        private static MasterProbe[] CreateMasterProbes()
        {
            var probes = new MasterProbe[MaximumDiagnosticProbes];
            for (int index = 0; index < probes.Length; index++)
            {
                probes[index] = new MasterProbe();
            }
            return probes;
        }

        private sealed class MasterProbe
        {
            internal int State;
            internal int ProbeId;
            internal AutomaticPitchedRoute Route;
            internal bool AutomaticBank;
            internal int Frames => PeakWindow.Frames;
            internal double Sum20To80;
            internal double Sum80To160;
            internal double Sum160To315;
            internal double Sum315To2000;
            internal double SumTotal;
            internal AudioPeakWindow PeakWindow;
            internal int ChannelsMeasured;

            internal void Reset(
                int probeId,
                AutomaticPitchedRoute route,
                bool automaticBank,
                int targetFrames)
            {
                ProbeId = probeId;
                Route = route;
                AutomaticBank = automaticBank;
                PeakWindow.Reset(targetFrames);
                ChannelsMeasured = 0;
                Sum20To80 = 0.0;
                Sum80To160 = 0.0;
                Sum160To315 = 0.0;
                Sum315To2000 = 0.0;
                SumTotal = 0.0;
            }
        }
    }
}
