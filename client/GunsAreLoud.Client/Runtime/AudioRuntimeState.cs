using System.Threading;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// The two audio-engine values this mod reads constantly and that only change
    /// when the device configuration does: output sample rate and DSP buffer size.
    /// Both are native property calls; the hearing controller, every filter
    /// configuration and every automatic-timing decision used to make one per call.
    /// <para>
    /// <c>AudioSettings.dspTime</c> is deliberately not cached here. It advances
    /// while a frame runs, and scheduling a copy against a stale clock would place
    /// it in the past on a long frame.
    /// </para>
    /// </summary>
    internal static class AudioRuntimeState
    {
        private static int _sampleRate = 48000;
        private static int _bufferFrames = 1024;
        private static int _bufferCount = 2;
        private static bool _subscribed;

        internal static int OutputSampleRate => Volatile.Read(ref _sampleRate);

        internal static int BufferFrames => Volatile.Read(ref _bufferFrames);

        internal static int BufferCount => Volatile.Read(ref _bufferCount);

        internal static void Initialize()
        {
            Refresh();
            if (_subscribed) return;
            _subscribed = true;
            AudioSettings.OnAudioConfigurationChanged += OnConfigurationChanged;
        }

        internal static void Shutdown()
        {
            if (!_subscribed) return;
            _subscribed = false;
            AudioSettings.OnAudioConfigurationChanged -= OnConfigurationChanged;
        }

        // Unity raises this for a device change and for an explicit SetConfiguration;
        // deviceWasChanged is not distinguished because both invalidate the values.
        private static void OnConfigurationChanged(bool deviceWasChanged) => Refresh();

        internal static void Refresh()
        {
            Volatile.Write(ref _sampleRate, Mathf.Max(8000, AudioSettings.outputSampleRate));
            AudioSettings.GetDSPBufferSize(out int frames, out int count);
            Volatile.Write(ref _bufferFrames, Mathf.Max(16, frames));
            Volatile.Write(ref _bufferCount, Mathf.Max(1, count));
        }
    }
}
