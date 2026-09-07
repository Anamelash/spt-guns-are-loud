using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Audio;
using GunsAreLoud.Client.Runtime;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class GunshotContrastController : MonoBehaviour
    {
        private sealed class Entry
        {
            internal int Id;
            internal AudioSource Source;
            internal GunshotContrastFilter Filter;
            internal AudioMixerGroup Group;
            internal string GroupName;
        }

        internal static GunshotContrastController Instance { get; private set; }
        internal int TrackedCount => _entries.Count;
        private readonly Dictionary<int, LinkedListNode<Entry>> _entries = new Dictionary<int, LinkedListNode<Entry>>();
        private readonly LinkedList<Entry> _rotation = new LinkedList<Entry>();
        private readonly List<AudioSource> _tree = new List<AudioSource>();
        private readonly List<AudioSource> _layout = new List<AudioSource>();
        private readonly GunshotContrastState _state = new GunshotContrastState();
        private LinkedListNode<Entry> _cursor;
        private IncrementalAudioDiscovery _discovery;
        private float _nextLog, _lastDb = float.NaN, _attenuation = 1f;
        private bool _active;

        private void OnEnable()
        {
            Instance = this;
            _discovery = new IncrementalAudioDiscovery(Track, gameObject.scene);
        }

        private void UpdateContext()
        {
            BetterAudio audio = AudioRuntimeLookup.Audio;
            bool inGame = audio != null && audio.ListenerPlayer != null && audio.ListenerPlayer.IsYourPlayer;
            float db = Plugin.ModConfig?.GunshotContrastDb.Value ?? 0f;
            if (db != _lastDb) { _lastDb = db; _attenuation = GunshotContrastModel.Gain(db); }
            bool active = inGame && Plugin.ModConfig?.Enabled.Value == true && _attenuation < 1f;
            // All existing callbacks see F12/disable/menu changes together; no
            // whole-registry sweep or delayed per-source settings cache required.
            _state.Gain = active ? _attenuation : 1f;
            if (_active && !active) _discovery?.Reset();
            _active = active;
        }

        internal void RefreshSourceTree(BetterSource source)
        {
            using (PerformanceTrace.Measure(PerformanceArea.ContrastHooks))
            {
                if (source == null) return;
                UpdateContext();
                if (!_active && _entries.Count == 0) return;
                // EFT pool routing/playback remains synchronous, before its first sample.
                source.GetComponentsInChildren(true, _tree);
                foreach (AudioSource audio in _tree) Track(audio);
            }
        }

        internal void RefreshSource(AudioSource source)
        {
            UpdateContext();
            if (_active || _entries.Count != 0) Track(source);
        }

        private void Track(AudioSource source)
        {
            if (source == null) return;
            int id = source.GetInstanceID();
            if (_entries.TryGetValue(id, out LinkedListNode<Entry> node))
            {
                if (node.Value.Source == source && node.Value.Filter != null)
                {
                    Refresh(node.Value);
                    return;
                }
                Remove(node);
            }
            AudioMixerGroup group = source.outputAudioMixerGroup;
            GunshotContrastFilter filter = source.GetComponent<GunshotContrastFilter>();
            string groupName = group != null ? group.name : null;
            if (!_active || (!GunshotContrastModel.ShouldAttenuate(groupName) || GrenadeAudioRoute.IsExplosion(source)))
            {
                if (filter != null) filter.Bind(_state, false);
                return;
            }
            // A Unity filter belongs to the object, not a particular AudioSource.
            source.GetComponents(_layout);
            if (!GunshotContrastModel.SafeSourceLayout(_layout.Count))
            {
                if (filter != null) filter.Bind(_state, false);
                return;
            }
            if (filter == null) filter = source.gameObject.AddComponent<GunshotContrastFilter>();
            var entry = new Entry { Id = id, Source = source, Filter = filter, Group = group, GroupName = groupName };
            _entries.Add(id, _rotation.AddLast(entry));
            filter.Bind(_state, true);
        }

        private void Refresh(Entry entry)
        {
            AudioMixerGroup group = entry.Source.outputAudioMixerGroup;
            if (group != entry.Group)
            {
                entry.Group = group;
                entry.GroupName = group != null ? group.name : null;
            }
            entry.Source.GetComponents(_layout);
            entry.Filter.Bind(_state, GunshotContrastModel.SafeSourceLayout(_layout.Count) &&
                GunshotContrastModel.ShouldAttenuate(entry.GroupName) && !GrenadeAudioRoute.IsExplosion(entry.Source));
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            if (_cursor == node) _cursor = node.Next;
            _entries.Remove(node.Value.Id);
            _rotation.Remove(node);
        }

        private void LateUpdate()
        {
            UpdateContext();
            using (PerformanceTrace.Measure(PerformanceArea.ContrastDiscovery))
                if (_active) _discovery.Tick();

            // Incremental pruning and fallback reroutes, including inactive pools.
            // Actual EFT reroutes still go through the immediate hooks above.
            using (PerformanceTrace.Measure(PerformanceArea.ContrastMaintenance))
            {
                long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5000; // 0.2 ms
                int remaining = System.Math.Min(8, _entries.Count);
                while (remaining-- > 0 && Stopwatch.GetTimestamp() < deadline)
                {
                    if (_cursor == null) _cursor = _rotation.First;
                    if (_cursor == null) break;
                    LinkedListNode<Entry> node = _cursor;
                    _cursor = node.Next;
                    Entry entry = node.Value;
                    if (!_active)
                    {
                        // Unity still invokes a bypassed OnAudioFilterRead. Zero must
                        // detach the DSP component, not merely set its multiplier to one.
                        if (entry.Filter != null) { entry.Filter.SetGain(1f); Destroy(entry.Filter); }
                        Remove(node);
                    }
                    else if (entry.Source == null || entry.Filter == null) Remove(node);
                    else if (_active) Refresh(entry);
                }
            }
            if (Plugin.ModConfig?.DiagnosticShotLog.Value == true && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 10f;
                int attenuated = 0, callbacks = 0;
                foreach (Entry entry in _rotation)
                {
                    if (entry.Filter == null) continue;
                    if (entry.Filter.Gain < 1f) attenuated++;
                    callbacks += entry.Filter.CallbackCount;
                }
                Plugin.Log.LogInfo($"gunshot contrast active={_active} attenuation={_lastDb:0.0}dB " +
                    $"tracked={_entries.Count} attenuated={attenuated} callbacks={callbacks} stage=pre-mixer discovery=incremental");
            }
        }

        internal void Shutdown()
        {
            _state.Gain = 1f;
            foreach (Entry entry in _rotation)
                if (entry.Filter != null) { entry.Filter.SetGain(1f); Destroy(entry.Filter); }
            _entries.Clear(); _rotation.Clear(); _cursor = null;
            _tree.Clear(); _layout.Clear();
            _discovery?.Reset();
            if (Instance == this) Instance = null;
        }

        private void OnDisable() => Shutdown();
        private void OnDestroy() => Shutdown();
    }
}
