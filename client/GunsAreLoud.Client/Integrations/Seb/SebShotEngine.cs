using System;
using GunsAreLoud.Client.Engine;
using UnityEngine;
using SoundBank = EFT.SoundBank;

namespace GunsAreLoud.Client.Integrations.Seb
{
    /// <summary>
    /// How Sound Engine Backport plays the local player's shot. Its live engine
    /// starts every round of a non-legacy bank itself as a whole shot; a legacy
    /// bank, or every bank with the live engine off, goes through EFT's queue
    /// (SEB's V1 path), looped or whole by SEB's per-clip rule.
    /// </summary>
    internal sealed class SebShotEngine : IShotEngine
    {
        internal const string LiveEngineNote =
            "With Sound Engine Backport's live gun engine every round is a whole shot of its own and gets its own full copy: " +
            "this setting does not apply to it, only Automatic Report Overlap does.";

        public string Name => SebIds.DisplayName + " " + (string.IsNullOrEmpty(SebModule.Version) ? "?" : SebModule.Version) +
            (SebModule.LiveEngineEnabled ? " (live)" : " (V1)");

        public BodyModel ClassifyBody(SoundBank body, AudioClip clip) =>
            Classify(PlaysShotsItself(body), clip != null && SebReflection.IsLoopClip(clip));

        /// <summary>
        /// SEB's own decision for one close body clip: the live engine plays it
        /// whole; on the V1 path it loops only a clip its rule calls a loop.
        /// </summary>
        internal static BodyModel Classify(bool liveEnginePlaysBank, bool loopClip) =>
            liveEnginePlaysBank || !loopClip ? BodyModel.OneShot : BodyModel.Loop;

        public bool PlaysShotsItself(SoundBank body) =>
            SebModule.LiveEngineEnabled && body != null && !SebReflection.IsLegacyBank(body);

        // SEB swaps a weapon's banks in WeaponSoundPlayer.Init when its bundles
        // are already loaded, otherwise on the first shot. Its swap is idempotent
        // and refuses while the weapon fires; asking early only makes it sooner.
        public void PrepareWeapon(WeaponSoundPlayer player) => SebReflection.ApplyBankReplacement(player);

        // A live shot has no loop beat: BeatLn there is the clip length over
        // sixteen, not the fire interval.
        public float LoopBeatSeconds(WeaponSoundPlayer player, float pitch)
        {
            if (player == null) return 0f;
            if (PlaysShotsItself(player.Body)) return 0f;
            return StockShotEngine.Instance.LoopBeatSeconds(player, pitch);
        }

        public float UserGainDb => SebModule.GunshotsVolumeDb;

        public bool ReleasesSourcesDirectly => true;

        public string SettingsNote => SebModule.LiveEngineEnabled ? LiveEngineNote : "";
    }
}
