using GunsAreLoud.Client.Runtime;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    /// <summary>
    /// One lookup per pooled EFT source instead of nine to thirty-two.
    /// <para>
    /// EFT re-issues the same <see cref="BetterSource"/> objects for every sound
    /// in the game, ours and everyone else's. Each routing and playback hook used
    /// to call <c>GetComponent</c> for every component this mod may have attached
    /// to the source and to its two audio channels; this container resolves each
    /// of them once and remembers both the hit and the miss. It also records
    /// whether the source is currently carrying one of our shots, so a foreign
    /// playback costs a single lookup and a flag test.
    /// </para>
    /// It holds references only. Nothing here processes audio, and it is attached
    /// to the same GameObject as EFT's source, so it dies with the pool.
    /// </summary>
    internal sealed class GalSourceBinding : MonoBehaviour
    {
        private const int PitchedBit = 2;
        private const int CaptureBit = 4;
        private const int BeatTimelineBit = 1;

        private readonly AudioSource[] _channels = new AudioSource[2];
        private readonly AutomaticPitchedGunshotFilter[] _pitchedFilters =
            new AutomaticPitchedGunshotFilter[2];
        private readonly AutomaticBeatCapture[] _captures = new AutomaticBeatCapture[2];
        private readonly int[] _resolved = new int[2];
        private AutomaticBeatTimeline _beatTimeline;
        private int _resolvedTimelines;
        private bool _armed;

        /// <summary>The binding of a source this mod has already touched, or null.</summary>
        internal static GalSourceBinding Find(BetterSource source) =>
            source == null ? null : source.GetComponent<GalSourceBinding>();

        /// <summary>
        /// The binding for a source that is about to carry one of our shots.
        /// Marks it armed, so the next foreign playback knows there is work to undo.
        /// </summary>
        internal static GalSourceBinding Of(BetterSource source)
        {
            if (source == null) return null;
            GalSourceBinding binding = source.GetComponent<GalSourceBinding>();
            if (binding == null) binding = source.gameObject.AddComponent<GalSourceBinding>();
            binding._armed = true;
            return binding;
        }

        internal bool Armed => _armed;

        private void Awake() => GalSourceCensus.Created(GalComponentKind.SourceBinding);

        private void OnEnable() => GalSourceCensus.Enabled(GalComponentKind.SourceBinding);

        private void OnDisable() => GalSourceCensus.Disabled(GalComponentKind.SourceBinding);

        private void OnDestroy() => GalSourceCensus.Destroyed(GalComponentKind.SourceBinding);

        /// <summary>
        /// Returns true once per armed period: the first bypass after a shot has
        /// real work, every repeat until the next shot has none.
        /// </summary>
        internal bool MarkBypassed()
        {
            if (!_armed) return false;
            _armed = false;
            return true;
        }

        internal void MarkArmed() => _armed = true;

        internal AutomaticPitchedGunshotFilter PitchedFilter(AudioSource channel)
        {
            int slot = Slot(channel);
            if (slot < 0) return null;
            if (!Resolve(slot, PitchedBit, _pitchedFilters[slot]))
                _pitchedFilters[slot] = channel.GetComponent<AutomaticPitchedGunshotFilter>();
            return _pitchedFilters[slot];
        }

        internal AutomaticPitchedGunshotFilter EnsurePitchedFilter(AudioSource channel)
        {
            AutomaticPitchedGunshotFilter filter = PitchedFilter(channel);
            if (filter == null)
            {
                filter = channel.gameObject.AddComponent<AutomaticPitchedGunshotFilter>();
                _pitchedFilters[Slot(channel)] = filter;
            }
            return filter;
        }

        internal AutomaticBeatCapture Capture(AudioSource channel)
        {
            int slot = Slot(channel);
            if (slot < 0) return null;
            if (!Resolve(slot, CaptureBit, _captures[slot]))
                _captures[slot] = channel.GetComponent<AutomaticBeatCapture>();
            return _captures[slot];
        }

        internal AutomaticBeatCapture EnsureCapture(AudioSource channel)
        {
            AutomaticBeatCapture capture = Capture(channel);
            if (capture == null)
            {
                capture = channel.gameObject.AddComponent<AutomaticBeatCapture>();
                _captures[Slot(channel)] = capture;
            }
            return capture;
        }

        internal AutomaticBeatTimeline BeatTimeline()
        {
            if (!ResolveTimeline(BeatTimelineBit, _beatTimeline))
                _beatTimeline = GetComponent<AutomaticBeatTimeline>();
            return _beatTimeline;
        }

        internal AutomaticBeatTimeline EnsureBeatTimeline()
        {
            AutomaticBeatTimeline timeline = BeatTimeline();
            if (timeline == null) _beatTimeline = timeline = gameObject.AddComponent<AutomaticBeatTimeline>();
            return timeline;
        }

        // A remembered miss is a null reference; a destroyed component is a live
        // reference that compares equal to null through Unity's operator. Only the
        // second needs looking up again.
        private bool Resolve(int slot, int bit, UnityEngine.Object cached)
        {
            if ((_resolved[slot] & bit) != 0)
            {
                if (ReferenceEquals(cached, null) || cached != null) return true;
                _resolved[slot] &= ~bit;
            }
            _resolved[slot] |= bit;
            return false;
        }

        private bool ResolveTimeline(int bit, UnityEngine.Object cached)
        {
            if ((_resolvedTimelines & bit) != 0)
            {
                if (ReferenceEquals(cached, null) || cached != null) return true;
                _resolvedTimelines &= ~bit;
            }
            _resolvedTimelines |= bit;
            return false;
        }

        // source1 and source2 keep their identity for the life of a pooled source.
        // Rebinding is a safety net for an unexpected re-issue, not a normal path.
        private int Slot(AudioSource channel)
        {
            if (channel == null) return -1;
            if (ReferenceEquals(_channels[0], channel)) return 0;
            if (ReferenceEquals(_channels[1], channel)) return 1;
            int slot = ReferenceEquals(_channels[0], null) ? 0 :
                ReferenceEquals(_channels[1], null) ? 1 : 0;
            _channels[slot] = channel;
            _pitchedFilters[slot] = null;
            _captures[slot] = null;
            _resolved[slot] = 0;
            return slot;
        }
    }
}
