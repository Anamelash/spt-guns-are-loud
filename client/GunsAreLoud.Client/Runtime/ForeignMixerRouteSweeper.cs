using GunsAreLoud.Client.Audio;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// The safety net under the routing patches: walks the loaded scenes within
    /// the discovery budget and moves any plain audio source still parented to a
    /// stock world group. Scene objects whose group was set in the editor and
    /// never touched by code have no other way onto the replacement mixer.
    /// <para>
    /// A sweep runs when the map first exists, after every scene load or unload,
    /// and again on a slow timer for objects spawned between those events. Each
    /// pass is budgeted by <see cref="IncrementalAudioDiscovery"/> and costs
    /// nothing once complete.
    /// </para>
    /// </summary>
    internal sealed class ForeignMixerRouteSweeper : MonoBehaviour
    {
        internal const float ResweepIntervalSeconds = 20f;

        private IncrementalAudioDiscovery _discovery;
        private StockMixerGroupMap _map;
        private float _nextResweep;
        private int _lastRemapped = -1;

        private void OnEnable()
        {
            _discovery = new IncrementalAudioDiscovery(Track, gameObject.scene);
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _discovery?.Reset();
            _discovery = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _discovery?.MarkDirty();

        private void OnSceneUnloaded(Scene scene) => _discovery?.MarkDirty();

        private void LateUpdate()
        {
            StockMixerGroupMap map = HeadphoneMixerAsset.StockGroupMap;
            if (map == null || _discovery == null) return;
            if (!ReferenceEquals(map, _map))
            {
                _map = map;
                _lastRemapped = -1;
                _discovery.MarkDirty();
            }
            using (PerformanceTrace.Measure(PerformanceArea.ForeignRouteSweep))
            {
                float now = Time.unscaledTime;
                if (_discovery.SweepComplete)
                {
                    if (now < _nextResweep) return;
                    _discovery.MarkDirty();
                }
                _discovery.Tick();
                if (!_discovery.SweepComplete) return;
                // Just finished: hold the next pass back, and say so only when
                // this one actually moved something.
                _nextResweep = now + ResweepIntervalSeconds;
                if (_map.RemappedSources == _lastRemapped) return;
                _lastRemapped = _map.RemappedSources;
                Plugin.Log?.LogInfo("Foreign mixer route sweep complete: " + _map.Describe());
            }
        }

        private void Track(AudioSource source) => _map?.Remap(source);
    }
}
