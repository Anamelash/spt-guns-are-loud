using Comfort.Common;
using EFT;
using UnityEngine;

namespace GunsAreLoud.Client.Audio
{
    internal static class AudioRuntimeLookup
    {
        internal static BetterAudio Audio => Singleton<BetterAudio>.Instantiated
            ? Singleton<BetterAudio>.Instance : null;

        internal static AudioListener Listener
        {
            get
            {
                BetterAudio audio = Audio;
                if (audio != null && audio.AudioListener != null) return audio.AudioListener;
                // EFT owns this object. No scene-wide lookup, including during loading.
                var manager = Singleton<AudioListenerConsistencyManager>.Instantiated
                    ? Singleton<AudioListenerConsistencyManager>.Instance : null;
                return manager != null ? manager.GetComponent<AudioListener>() : null;
            }
        }

        internal static WeaponSoundPlayer HeldWeapon
        {
            get
            {
                BetterAudio audio = Audio;
                Player player = audio != null ? audio.ListenerPlayer : null;
                return player != null && player.IsYourPlayer &&
                    player.HandsController is Player.FirearmController hands
                    ? hands.WeaponSoundPlayer : null;
            }
        }
    }
}
