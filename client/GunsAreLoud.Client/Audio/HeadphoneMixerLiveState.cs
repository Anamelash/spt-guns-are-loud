using System;
namespace GunsAreLoud.Client.Audio
{
    internal static class HeadphoneMixerLiveState
    {
        internal static bool Equivalent(float expected, float actual) =>
            !float.IsNaN(expected) && !float.IsInfinity(expected) &&
            !float.IsNaN(actual) && !float.IsInfinity(actual) &&
            Math.Abs(expected - actual) <= 0.0001f + Math.Abs(expected) * 0.00001f;
    }

    // The menu and BetterAudio retain different mixer objects. Persistent user
    // controls are owned by the original/menu mixer and copied one-way. InGame
    // is owned by BetterAudio; Music combines its raid fade with the user limit.
    internal sealed class HeadphoneGlobalControlBridge
    {
        private readonly Runtime.IMixerParameterStore _original, _replacement;
        private readonly System.Collections.Generic.Dictionary<string, float> _last =
            new System.Collections.Generic.Dictionary<string, float>();
        private bool _hasMusicSource;
        private float _lastMusicSource;
        // Original compiled names (CRC aliases of MasterVolume, UIVolume,
        // ChatVolume and HideoutVolume), verified against group GUIDs.
        private static readonly string[] MenuControls = { "OtWVUHN", "JlWLTVH", "spoTLkN", "OnQSOHH" };
        private const string MusicControl = "mposwoH";
        private const string InGameControl = "nvqIkjL";
        internal HeadphoneGlobalControlBridge(Runtime.IMixerParameterStore original, Runtime.IMixerParameterStore replacement)
        { _original = original; _replacement = replacement; }

        internal bool Tick()
        {
            bool ok = true;
            foreach (string name in MenuControls)
            {
                if (!_original.TryGet(name, out float source) || !_replacement.TryGet(name, out float target) ||
                    float.IsNaN(source) || float.IsInfinity(source) || float.IsNaN(target) || float.IsInfinity(target))
                { ok = false; continue; }
                if (!HeadphoneMixerLiveState.Equivalent(target, source)) ok &= _replacement.TrySet(name, source);
            }

            if (!_original.TryGet(MusicControl, out float originalMusic) ||
                !_replacement.TryGet(MusicControl, out float replacementMusic) ||
                float.IsNaN(originalMusic) || float.IsInfinity(originalMusic) ||
                float.IsNaN(replacementMusic) || float.IsInfinity(replacementMusic))
                return false;
            // A menu setting change must take effect immediately. Otherwise the
            // quieter owner wins: BetterAudio may fade music out for a raid, but
            // its raw post-raid slider value may never override a menu-side mute.
            float desiredMusic = !_hasMusicSource ||
                !HeadphoneMixerLiveState.Equivalent(_lastMusicSource, originalMusic)
                ? originalMusic
                : Math.Min(originalMusic, replacementMusic);
            bool musicWritten = HeadphoneMixerLiveState.Equivalent(replacementMusic, desiredMusic) ||
                _replacement.TrySet(MusicControl, desiredMusic);
            if (musicWritten)
            {
                _hasMusicSource = true;
                _lastMusicSource = originalMusic;
            }
            ok &= musicWritten;

            if (!_original.TryGet(InGameControl, out float originalInGame) ||
                !_replacement.TryGet(InGameControl, out float replacementInGame) ||
                float.IsNaN(originalInGame) || float.IsInfinity(originalInGame) ||
                float.IsNaN(replacementInGame) || float.IsInfinity(replacementInGame))
                return false;

            bool first = !_last.TryGetValue(InGameControl, out float previous);
            // Menu changes win a simultaneous conflict; otherwise propagate the
            // raid fade from the BetterAudio-owned replacement mixer.
            float desired = first || !HeadphoneMixerLiveState.Equivalent(previous, originalInGame)
                ? originalInGame
                : replacementInGame;
            bool wrote = true;
            if (!HeadphoneMixerLiveState.Equivalent(originalInGame, desired))
                wrote &= _original.TrySet(InGameControl, desired);
            if (!HeadphoneMixerLiveState.Equivalent(replacementInGame, desired))
                wrote &= _replacement.TrySet(InGameControl, desired);
            if (wrote) _last[InGameControl] = desired;
            ok &= wrote;
            return ok;
        }
    }
}
