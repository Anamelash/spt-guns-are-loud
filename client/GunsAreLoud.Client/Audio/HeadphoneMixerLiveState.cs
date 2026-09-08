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

    // The menu and BetterAudio retain different mixer objects. These global
    // controls represent one shared setting: preserve changes from either owner.
    internal sealed class HeadphoneGlobalControlBridge
    {
        private readonly Runtime.IMixerParameterStore _original, _replacement;
        private readonly System.Collections.Generic.Dictionary<string, float> _last =
            new System.Collections.Generic.Dictionary<string, float>();
        // Original compiled names (CRC aliases of MasterVolume, InGame, UIVolume,
        // ChatVolume, MusicVolume, HideoutVolume), verified against group GUIDs.
        private static readonly string[] Controls = { "OtWVUHN", "nvqIkjL", "JlWLTVH", "spoTLkN", "mposwoH", "OnQSOHH" };
        internal HeadphoneGlobalControlBridge(Runtime.IMixerParameterStore original, Runtime.IMixerParameterStore replacement)
        { _original = original; _replacement = replacement; }

        internal bool Tick()
        {
            bool ok = true;
            foreach (string name in Controls)
            {
                if (!_original.TryGet(name, out float source) || !_replacement.TryGet(name, out float target) ||
                    float.IsNaN(source) || float.IsInfinity(source) || float.IsNaN(target) || float.IsInfinity(target))
                { ok = false; continue; }
                bool first = !_last.TryGetValue(name, out float previous);
                // Menu/user changes win a simultaneous conflict; otherwise a
                // raid fade on BetterAudio is propagated back to menu owners.
                float desired = first || !HeadphoneMixerLiveState.Equivalent(previous, source) ? source : target;
                bool wrote = true;
                if (!HeadphoneMixerLiveState.Equivalent(source, desired)) wrote &= _original.TrySet(name, desired);
                if (!HeadphoneMixerLiveState.Equivalent(target, desired)) wrote &= _replacement.TrySet(name, desired);
                if (wrote) _last[name] = desired;
                ok &= wrote;
            }
            return ok;
        }
    }
}
