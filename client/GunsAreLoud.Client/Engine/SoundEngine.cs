using System;
using BepInEx.Logging;
using GunsAreLoud.Client.Audio;
using UnityEngine;
using UnityEngine.Audio;
using SoundBank = EFT.SoundBank;

namespace GunsAreLoud.Client.Engine
{
    /// <summary>How the engine that plays a shot treats one close body clip.</summary>
    internal enum BodyModel
    {
        /// <summary>A looped beat with a separate tail, cut into a report at warm-up.</summary>
        Loop,
        /// <summary>A whole shot per round, attack and tail in one clip.</summary>
        OneShot
    }

    /// <summary>
    /// Who plays the local player's gunshot and how. In the stock game it is
    /// EFT itself; a sound mod can take the role over. The core asks this
    /// instead of knowing about any such mod.
    /// </summary>
    internal interface IShotEngine
    {
        string Name { get; }

        /// <summary>How this close body clip of this bank will be played.</summary>
        BodyModel ClassifyBody(SoundBank body, AudioClip clip);

        /// <summary>
        /// The engine starts this bank's shots itself, synchronously and outside
        /// EFT's audio queue.
        /// </summary>
        bool PlaysShotsItself(SoundBank body);

        /// <summary>
        /// Lets the engine put its own banks on the weapon before they are read.
        /// An engine that swaps banks lazily would otherwise be seen with the
        /// game's banks until its first shot.
        /// </summary>
        void PrepareWeapon(WeaponSoundPlayer player);

        /// <summary>The loop beat a shot of this player is built on, or 0 when there is none.</summary>
        float LoopBeatSeconds(WeaponSoundPlayer player, float pitch);

        /// <summary>
        /// The player's own trim of gunshot loudness in the engine, in dB: kept on
        /// top of the direct level target, so the correction does not undo it.
        /// 0 when the engine has none.
        /// </summary>
        float UserGainDb { get; }

        /// <summary>The engine returns its sources with <c>Release()</c>, not through the queue.</summary>
        bool ReleasesSourcesDirectly { get; }

        /// <summary>Appended to the descriptions of settings the engine bypasses; empty for none.</summary>
        string SettingsNote { get; }
    }

    /// <summary>
    /// Whose mixer is <c>BetterAudio.Master</c>. In the stock game that is EFT's,
    /// which this mod may replace with its own; a sound mod can own it instead,
    /// in which case this mod can only offer its variant of that mod's mixer.
    /// </summary>
    internal interface IMixerHost
    {
        /// <summary>"EFT" for the game's own, or the owning mod's name.</summary>
        string DisplayName { get; }

        /// <summary>A mod, not EFT, owns the master mixer.</summary>
        bool OwnsMaster { get; }

        /// <summary>The host will install its own mixer while the raid's audio is created.</summary>
        bool PredictsInstall { get; }

        /// <summary>Why this mod cannot put its variant in, or null when it can.</summary>
        string InstallBlocker { get; }

        /// <summary>Identifies the host mixer the variant was built from (bundle hash); "" when none.</summary>
        string VariantKey { get; }

        /// <summary>This mod's variant for the host's mixer, or null.</summary>
        HeadphoneMixerVariant Variant { get; }

        /// <summary>The host's own mixer, loaded the way the host loads it.</summary>
        AudioMixer LoadReplacement();

        /// <summary>Puts this mod's variant where the host installs its mixer from.</summary>
        bool TryInstall(AudioMixer variant);

        /// <summary>The host's mixer is the master right now.</summary>
        bool IsActive { get; }

        /// <summary>The host's own ambient trim the headset route has to keep, in dB.</summary>
        float AmbientTrimDb { get; }

        /// <summary>Host mixer parameters a headset route holds at 0 dB while it owns the level.</summary>
        string[] NeutralRouteParameters { get; }

        /// <summary>A host setting that changes what a headset route owns has changed.</summary>
        event Action SettingsChanged;
    }

    /// <summary>A sound mod this one knows how to work with.</summary>
    internal interface ISoundEngineModule
    {
        string Name { get; }

        /// <summary>Finds the mod; when found, supplies its shot engine and mixer host.</summary>
        bool TryAttach(ManualLogSource log, out IShotEngine shots, out IMixerHost mixer);

        /// <summary>One detection line for the log.</summary>
        string Describe();
    }

    /// <summary>
    /// The engine and mixer host in effect for this run. Without a known sound
    /// mod both are the stock implementations, which are exactly what the mod
    /// does on its own.
    /// </summary>
    internal static class SoundEngine
    {
        internal static IShotEngine Shots { get; private set; } = StockShotEngine.Instance;
        internal static IMixerHost Mixer { get; private set; } = StockMixerHost.Instance;
        internal static ISoundEngineModule Module { get; private set; }

        internal static void Attach(ManualLogSource log, params ISoundEngineModule[] known)
        {
            Reset();
            if (known == null) return;
            foreach (ISoundEngineModule module in known)
            {
                if (module == null) continue;
                try
                {
                    if (!module.TryAttach(log, out IShotEngine shots, out IMixerHost mixer)) continue;
                    Shots = shots ?? StockShotEngine.Instance;
                    Mixer = mixer ?? StockMixerHost.Instance;
                    Module = module;
                    return;
                }
                catch (Exception error)
                {
                    log?.LogWarning("Sound engine module " + module.Name + " failed to attach: " + error.Message);
                }
            }
        }

        /// <summary>For tests: put a specific engine in place.</summary>
        internal static void Use(IShotEngine shots, IMixerHost mixer = null, ISoundEngineModule module = null)
        {
            Shots = shots ?? StockShotEngine.Instance;
            Mixer = mixer ?? StockMixerHost.Instance;
            Module = module;
        }

        internal static void Reset()
        {
            Shots = StockShotEngine.Instance;
            Mixer = StockMixerHost.Instance;
            Module = null;
        }

        internal static string Describe() =>
            $"sound engine: {Shots.Name}, master mixer: {Mixer.DisplayName}";
    }

    /// <summary>EFT playing its own shots: what the mod has always assumed.</summary>
    internal sealed class StockShotEngine : IShotEngine
    {
        internal static readonly StockShotEngine Instance = new StockShotEngine();

        public string Name => "stock";

        // An automatic weapon's body is always a loop in the stock game; pistols
        // and bolt actions never reach this question.
        public BodyModel ClassifyBody(SoundBank body, AudioClip clip) => BodyModel.Loop;

        public bool PlaysShotsItself(SoundBank body) => false;

        public void PrepareWeapon(WeaponSoundPlayer player) { }

        public float LoopBeatSeconds(WeaponSoundPlayer player, float pitch) =>
            player == null ? 0f : player.BeatLn / Mathf.Clamp(pitch, 0.965f, 1.045f);

        public float UserGainDb => 0f;

        public bool ReleasesSourcesDirectly => false;

        public string SettingsNote => "";
    }

    /// <summary>EFT's own master mixer, which this mod may replace with its own.</summary>
    internal sealed class StockMixerHost : IMixerHost
    {
        internal static readonly StockMixerHost Instance = new StockMixerHost();

        public string DisplayName => "EFT";
        public bool OwnsMaster => false;
        public bool PredictsInstall => false;
        public string InstallBlocker => "no sound engine mod";
        public string VariantKey => "";
        public HeadphoneMixerVariant Variant => null;
        public AudioMixer LoadReplacement() => null;
        public bool TryInstall(AudioMixer variant) => false;
        public bool IsActive => false;
        public float AmbientTrimDb => 0f;
        public string[] NeutralRouteParameters => Array.Empty<string>();

        public event Action SettingsChanged { add { } remove { } }
    }
}
