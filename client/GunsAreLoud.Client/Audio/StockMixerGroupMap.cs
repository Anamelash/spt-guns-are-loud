using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class AudioMixerGroupCatalog : IMixerGroupCatalog<AudioMixerGroup>
    {
        private readonly AudioMixer _mixer;
        private readonly Dictionary<AudioMixerGroup, string> _names = new Dictionary<AudioMixerGroup, string>();

        internal AudioMixerGroupCatalog(AudioMixer mixer) { _mixer = mixer; }

        public IReadOnlyList<AudioMixerGroup> All() => _mixer.FindMatchingGroups("") ?? Array.Empty<AudioMixerGroup>();

        public IReadOnlyList<AudioMixerGroup> Find(string subPath) =>
            _mixer.FindMatchingGroups(subPath) ?? Array.Empty<AudioMixerGroup>();

        // UnityEngine.Object.name allocates on every read; the map is built once
        // but asks for each name several times.
        public string NameOf(AudioMixerGroup group)
        {
            if (group == null) return null;
            if (_names.TryGetValue(group, out string name)) return name;
            name = group.name;
            _names[group] = name;
            return name;
        }
    }

    /// <summary>
    /// Moves audio sources that reference the stock mixer onto the replacement.
    /// <para>
    /// The replacement mixer is installed when BetterAudio loads
    /// <c>Audio/MasterMixer</c>, so every source that asks BetterAudio for its
    /// group lands on it. A prefab whose group is serialized (the BTR engine,
    /// precipitation and wind blenders, scene ambient emitters, synchronized
    /// loops, radio broadcasts, trigger sounds with a forced group) still points
    /// at the stock asset, which stays alive for the menu. Nothing processes
    /// those sources there: EFT writes the worn headset's template to
    /// <c>BetterAudio.Master</c> only, and the passive bus and electronics exist
    /// only in the replacement. They were heard straight through the headset.
    /// </para>
    /// The map changes a source's output group and nothing else: it never writes
    /// a mixer parameter, so in Vanilla the moved source sounds exactly as it did
    /// on the stock asset, and in Realistic it takes the same path as every
    /// other world sound.
    /// </summary>
    internal sealed class StockMixerGroupMap
    {
        private readonly AudioMixer _stock;
        private readonly Dictionary<AudioMixerGroup, AudioMixerGroup> _pairs;
        private readonly HashSet<string> _skippedNames;
        private readonly HashSet<string> _loggedGroups = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<AudioMixerGroup, string> _names = new Dictionary<AudioMixerGroup, string>();
        private readonly List<AudioSource> _buffer = new List<AudioSource>(8);

        private StockMixerGroupMap(AudioMixer stock, StockMixerGroupMapResult<AudioMixerGroup> result)
        {
            _stock = stock;
            _pairs = result.Pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
            Skipped = result.Skipped;
            _skippedNames = new HashSet<string>(result.Skipped
                .Select(entry => entry.Substring(0, Math.Max(0, entry.IndexOf(':')))), StringComparer.Ordinal);
        }

        internal int PairCount => _pairs.Count;
        internal IReadOnlyList<string> Skipped { get; }
        /// <summary>Sources moved so far; a source moved twice counts twice.</summary>
        internal int RemappedSources { get; private set; }
        /// <summary>Sources seen on a stock world group that has no pair.</summary>
        internal int UnmappedSources { get; private set; }

        internal static StockMixerGroupMap TryCreate(AudioMixer stock, AudioMixer replacement, out string failure)
        {
            failure = null;
            if (stock == null || replacement == null || stock == replacement)
            { failure = "stock and replacement mixers must both exist and differ"; return null; }
            StockMixerGroupMapResult<AudioMixerGroup> result;
            try
            {
                result = StockMixerGroupMapper.Build(
                    new AudioMixerGroupCatalog(stock), new AudioMixerGroupCatalog(replacement));
            }
            catch (Exception error)
            {
                failure = error.Message;
                return null;
            }
            if (!result.Available) { failure = result.Failure; return null; }
            if (result.Pairs.Count == 0) { failure = "no stock world group could be paired"; return null; }
            return new StockMixerGroupMap(stock, result);
        }

        /// <summary>
        /// The replacement group for a stock world group. False for null, for a
        /// replacement group of any name, and for a stock group that has no pair.
        /// </summary>
        internal bool TryMap(AudioMixerGroup group, out AudioMixerGroup mapped)
        {
            mapped = null;
            return group != null && _pairs.TryGetValue(group, out mapped) && mapped != null;
        }

        /// <summary>True when the group belongs to the stock mixer.</summary>
        internal bool IsStock(AudioMixerGroup group) => group != null && group.audioMixer == _stock;

        /// <summary>Moves one source if it sits on a paired stock group.</summary>
        internal bool Remap(AudioSource source)
        {
            if (source == null) return false;
            AudioMixerGroup group = source.outputAudioMixerGroup;
            if (!TryMap(group, out AudioMixerGroup mapped))
            {
                NoteUnmapped(group);
                return false;
            }
            source.outputAudioMixerGroup = mapped;
            RemappedSources++;
            LogOnce(group, "remapped");
            return true;
        }

        /// <summary>Moves every source on the component's object and below it.</summary>
        internal int RemapChildren(Component owner)
        {
            if (owner == null) return 0;
            int moved = 0;
            owner.GetComponentsInChildren(true, _buffer);
            foreach (AudioSource source in _buffer)
                if (Remap(source)) moved++;
            _buffer.Clear();
            return moved;
        }

        // A stock group outside the passive scope (UI, music, the menu) is
        // expected there and stays quiet. A world group the mapper had to skip is
        // the one case worth a line: it is a sound that still bypasses the headset.
        private void NoteUnmapped(AudioMixerGroup group)
        {
            if (!IsStock(group)) return;
            string name = NameOf(group);
            if (!_skippedNames.Contains(name)) return;
            UnmappedSources++;
            LogOnce(group, "left on stock mixer");
        }

        private void LogOnce(AudioMixerGroup group, string outcome)
        {
            string name = NameOf(group);
            if (!_loggedGroups.Add(outcome + "|" + name)) return;
            Plugin.Log?.LogInfo($"Foreign mixer route {outcome}: group={name} " +
                $"stockMixer={(_stock != null ? _stock.name : "none")} pairs={_pairs.Count}");
        }

        private string NameOf(AudioMixerGroup group)
        {
            if (group == null) return "none";
            if (_names.TryGetValue(group, out string name)) return name;
            name = group.name;
            _names[group] = name;
            return name;
        }

        internal string Describe() =>
            $"pairs={_pairs.Count} remapped={RemappedSources} unmapped={UnmappedSources} " +
            $"skipped=[{string.Join(", ", Skipped)}]";
    }
}
