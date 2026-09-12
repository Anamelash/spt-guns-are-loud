using System.Threading;
using System.Diagnostics;
using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class GunshotContrastState
    {
        internal volatile float Gain = 1f;
    }

    // One level multiplier only: no clipping, timing, resampling or mixer writes.
    internal sealed class GunshotContrastFilter : MonoBehaviour
    {
        private volatile float _gain = 1f;
        private volatile GunshotContrastState _state;
        private volatile bool _eligible;
        private int _callbacks;
        private float[] _previousBuffer;
        internal int CallbackCount => Volatile.Read(ref _callbacks);
        internal float Gain
        {
            get
            {
                GunshotContrastState state = _state;
                return state == null ? _gain : (_eligible ? state.Gain : 1f);
            }
        }
        internal void SetGain(float gain) { _gain = gain; _state = null; }

        internal void Bind(GunshotContrastState state, bool eligible)
        {
            if (!ReferenceEquals(_state, state)) { _eligible = false; _state = state; }
            _eligible = eligible;
        }

        private void Awake() => GalSourceCensus.Created(GalComponentKind.ContrastFilter);

        private void OnEnable()
        {
            GalSourceCensus.Enabled(GalComponentKind.ContrastFilter);
            if (_state != null) GunshotContrastController.Instance?.RefreshSource(GetComponent<AudioSource>());
        }

        private void OnDisable()
        {
            GalSourceCensus.Disabled(GalComponentKind.ContrastFilter);
            _eligible = false; _gain = 1f; _previousBuffer = null;
        }

        private void OnDestroy() => GalSourceCensus.Destroyed(GalComponentKind.ContrastFilter);

        private void OnAudioFilterRead(float[] data, int channels)
        {
            bool diagnostic = PerformanceTrace.Enabled;
            long start = diagnostic ? Stopwatch.GetTimestamp() : 0;
            float gain = Gain;
            if (gain < 1f)
            {
                GunshotContrastModel.Apply(data, gain);
                Interlocked.Increment(ref _callbacks);
            }
            if (diagnostic) PerformanceTrace.RecordAudio(data, channels, ref _previousBuffer, start);
            else _previousBuffer = null;
        }
    }
}
