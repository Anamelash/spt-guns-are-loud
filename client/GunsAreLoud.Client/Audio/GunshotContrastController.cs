using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
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
            internal AudioMixerGroup OriginalGroup;
            internal AudioMixerGroup RoutedGroup;
            internal string GroupName;
            internal bool MixerRouted;
        }

        internal static GunshotContrastController Instance { get; private set; }
        internal int TrackedCount => _entries.Count;
        internal int MixerRoutedCount => CountEntries(true);
        internal int FallbackFilterCount => CountEntries(false);
        internal int FallbackFiltersCreated => _fallbackFiltersCreated;
        private readonly HashSet<string> _loggedFallbackGroups = new HashSet<string>();
        private readonly Dictionary<AudioMixerGroup, string> _groupNames =
            new Dictionary<AudioMixerGroup, string>();
        private int _fallbackFiltersCreated;
        // Sources whose group belongs to a mixer this mod did not replace: they
        // are outside our routing graph, so only a per-source filter can reach
        // them. Counted separately to decide whether that is worth keeping.
        internal int ForeignMixerSources => _foreignMixerSources.Count;
        private readonly HashSet<int> _foreignMixerSources = new HashSet<int>();
        private readonly Dictionary<int, LinkedListNode<Entry>> _entries = new Dictionary<int, LinkedListNode<Entry>>();
        private readonly LinkedList<Entry> _rotation = new LinkedList<Entry>();
        private readonly List<AudioSource> _tree = new List<AudioSource>();
        private readonly List<AudioSource> _layout = new List<AudioSource>();
        private readonly GunshotContrastState _state = new GunshotContrastState();
        private LinkedListNode<Entry> _cursor;
        private IncrementalAudioDiscovery _discovery;
        private GunshotContrastMixerRouter _routes;
        private float _lastDb = float.NaN, _attenuation = 1f;
        private bool _active;
        private bool _routeFailureLogged;
        private int _contextFrame = -1;

        private void OnEnable()
        {
            Instance = this;
            _contextFrame = -1;
            _discovery = new IncrementalAudioDiscovery(Track, gameObject.scene);
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // A loaded or unloaded scene is the only thing that can introduce audio
        // sources the routing hooks never touch. Everything else arrives through
        // SetMixerGroup and PlayScheduled.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _discovery?.MarkDirty();

        private void OnSceneUnloaded(Scene scene) => _discovery?.MarkDirty();

        private int CountEntries(bool mixerRouted)
        {
            int count = 0;
            foreach (Entry entry in _rotation)
                if (entry.MixerRouted == mixerRouted && (mixerRouted || entry.Filter != null)) count++;
            return count;
        }

        // Routing and playback hooks call this many times per frame, once per
        // sound. Nothing it reads changes inside a frame, so resolve it once.
        private void UpdateContext()
        {
            int frame = Time.frameCount;
            if (_contextFrame == frame) return;
            _contextFrame = frame;
            ResolveContext();
        }

        private void ResolveContext()
        {
            GunshotContrastMixerRouter available = HeadphoneMixerAsset.ContrastRoutes;
            if (!ReferenceEquals(_routes, available))
            {
                _routes = available;
                _routeFailureLogged = false;
                if (_routes != null)
                    Plugin.Log?.LogInfo("Gunshot contrast mixer routes ready: " + _routes.Count);
            }

            BetterAudio audio = AudioRuntimeLookup.Audio;
            bool inGame = audio != null && audio.ListenerPlayer != null && audio.ListenerPlayer.IsYourPlayer;
            float db = Plugin.ModConfig?.GunshotContrastDb.Value ?? 0f;
            if (db != _lastDb) { _lastDb = db; _attenuation = GunshotContrastModel.Gain(db); }
            bool active = inGame && Plugin.ModConfig?.Enabled.Value == true && _attenuation < 1f;
            _state.Gain = active ? _attenuation : 1f;
            if (_routes != null)
            {
                _routes.SetTarget(active ? db : 0f);
                if (!_routes.Healthy && !_routeFailureLogged)
                {
                    _routeFailureLogged = true;
                    Plugin.Log?.LogWarning("Gunshot contrast mixer route failed; using per-source fallback: " +
                        _routes.Failure);
                }
            }
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
                if (node.Value.Source == source)
                {
                    if (!Configure(node.Value)) Remove(node);
                    return;
                }
                Detach(node.Value);
                Remove(node);
            }
            var entry = new Entry { Id = id, Source = source, Filter = source.GetComponent<GunshotContrastFilter>() };
            if (!Configure(entry)) return;
            _entries.Add(id, _rotation.AddLast(entry));
        }

        private bool Configure(Entry entry)
        {
            AudioSource source = entry.Source;
            if (source == null) return false;
            AudioMixerGroup assigned = source.outputAudioMixerGroup;
            AudioMixerGroup original = assigned;
            AudioMixerGroup mappedOriginal = null;
            bool assignedToInput = _routes != null && _routes.TryGetOriginal(assigned, out mappedOriginal);
            if (assignedToInput) original = mappedOriginal;
            else if (entry.MixerRouted && assigned == entry.RoutedGroup && entry.OriginalGroup != null)
            {
                original = entry.OriginalGroup;
                assignedToInput = true;
            }

            string groupName = GroupName(original);
            bool eligible = _active && GunshotContrastModel.ShouldAttenuate(groupName) &&
                !GrenadeAudioRoute.IsExplosion(source);
            if (eligible && _routes != null && _routes.Healthy &&
                _routes.TryGetInput(original, out AudioMixerGroup input))
            {
                if (assigned != input) source.outputAudioMixerGroup = input;
                DestroyFilter(entry);
                entry.OriginalGroup = original;
                entry.RoutedGroup = input;
                entry.GroupName = groupName;
                entry.MixerRouted = true;
                return true;
            }

            // A grenade can be marked after a pooled source was assigned. Restore
            // its actual parent route immediately; the exemption is per issuance.
            if (assignedToInput && original != null) source.outputAudioMixerGroup = original;
            entry.OriginalGroup = original;
            entry.RoutedGroup = null;
            entry.GroupName = groupName;
            entry.MixerRouted = false;
            if (!eligible)
            {
                DestroyFilter(entry);
                return false;
            }

            // Preserve the old insert only when the replacement mixer is absent,
            // invalid, or the direct-source route was deliberately left unknown.
            if (entry.Filter == null)
            {
                // The layout check only decides whether a filter may be created;
                // an entry that already owns one has passed it.
                source.GetComponents(_layout);
                if (!GunshotContrastModel.SafeSourceLayout(_layout.Count)) return false;
                entry.Filter = source.gameObject.AddComponent<GunshotContrastFilter>();
                LogFallbackFilter(entry, original);
            }
            entry.Filter.Bind(_state, true);
            return true;
        }

        // Why this source could not be routed through the replacement mixer: the
        // group it is parented to, and whether that group belongs to our own mixer
        // at all. One line per group, not per source: a single unrouted group can
        // own hundreds of pooled sources, and the count is in the summary.
        private void LogFallbackFilter(Entry entry, AudioMixerGroup original)
        {
            _fallbackFiltersCreated++;
            bool ownMixer = original != null && HeadphoneMixerAsset.Owns(original.audioMixer);
            if (!ownMixer) _foreignMixerSources.Add(entry.Id);
            if (!_loggedFallbackGroups.Add(entry.GroupName ?? "none")) return;
            Plugin.Log?.LogInfo(
                $"Gunshot contrast fallback filter: group={entry.GroupName ?? "none"} " +
                // Once per group: reading the mixer's own name here is not a hot path.
                $"ownMixer={ownMixer} mixer={(original?.audioMixer != null ? original.audioMixer.name : "none")} " +
                $"routesHealthy={(_routes?.Healthy ?? false)} routes={(_routes?.Count ?? 0)}");
        }

        // UnityEngine.Object.name is a native call that allocates a new string on
        // every read. Configure runs for every tracked source, several times per
        // frame; the mixer's groups are fixed for the session, so read each name
        // once and compare the cached instance afterwards.
        private string GroupName(AudioMixerGroup group)
        {
            if (group == null) return null;
            if (_groupNames.TryGetValue(group, out string name)) return name;
            name = group.name;
            _groupNames[group] = name;
            return name;
        }

        // Leaving a source parented to a GAL input group is inaudible while the
        // faders sit at 0 dB, but it is still our routing. Give it back whenever
        // the entry stops being ours, so disabling the mod really is vanilla.
        private void Detach(Entry entry)
        {
            RestoreRoute(entry);
            DestroyFilter(entry);
        }

        private void RestoreRoute(Entry entry)
        {
            if (!entry.MixerRouted) return;
            entry.MixerRouted = false;
            AudioSource source = entry.Source;
            if (source != null && entry.OriginalGroup != null &&
                source.outputAudioMixerGroup == entry.RoutedGroup)
                source.outputAudioMixerGroup = entry.OriginalGroup;
            entry.RoutedGroup = null;
        }

        // The entry resolved its filter once, when it was created. A routed entry
        // that never had one must not pay a component lookup on every pass.
        private void DestroyFilter(Entry entry)
        {
            if (entry.Filter == null) return;
            entry.Filter.SetGain(1f);
            Destroy(entry.Filter);
            entry.Filter = null;
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
            // Ramp once per frame. Routing hooks may call UpdateContext many times
            // in one frame and must not shorten the transition.
            _routes?.Tick(Time.unscaledDeltaTime);
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
                    if (!_active || entry.Source == null)
                    {
                        Detach(entry);
                        Remove(node);
                    }
                    else if (!Configure(entry)) Remove(node);
                }
            }
            if (DetailedDiagnostics.TryBegin(
                DiagnosticEventKind.Contrast, out DiagnosticReservation reservation))
            {
                int callbacks = 0;
                foreach (Entry entry in _rotation)
                    if (entry.Filter != null) callbacks += entry.Filter.CallbackCount;
                DetailedDiagnostics.Commit(
                    reservation,
                    $"gunshot contrast active={_active} attenuation={_lastDb:0.0}dB " +
                    $"tracked={_entries.Count} mixerRouted={MixerRoutedCount} fallbackFilters={FallbackFilterCount} " +
                    $"callbacks={callbacks} mixerDb={(_routes?.CurrentDb ?? 0f):0.00} ramp={_routes?.Ramping ?? false}");
            }
        }

        internal void Shutdown()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _state.Gain = 1f;
            // Teardown runs from OnDisable at the end of a raid, where Unity may
            // already have taken the mixer or a source apart. Every step here
            // undoes something that outlives the raid — the mixer attenuation,
            // and each source's own original group — so one failing step must
            // not skip the rest. It used to: a single exception in the
            // neutralize left the whole game attenuated and every tracked source
            // still routed through the contrast input, for the rest of the
            // client session.
            try { _routes?.Neutralize(); }
            catch (Exception error)
            { Plugin.Log?.LogWarning($"contrast neutralize failed: {error.Message}"); }
            foreach (Entry entry in _rotation)
            {
                try { Detach(entry); }
                catch (Exception error)
                { Plugin.Log?.LogWarning($"contrast detach failed: {error.Message}"); }
            }
            _entries.Clear(); _rotation.Clear(); _cursor = null;
            _tree.Clear(); _layout.Clear();
            _foreignMixerSources.Clear();
            _contextFrame = -1;
            _discovery?.Reset();
            if (Instance == this) Instance = null;
        }

        private void OnDisable() => Shutdown();
        private void OnDestroy() => Shutdown();
    }
}
