using System;
using System.Globalization;
using EFT.InventoryLogic;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// Category send levels into the GAL electronic path, taken from the worn
    /// headset's own template. EFT's live mixer is not a valid source: it only
    /// reflects that template once EFT applies it, and an equip can leave EFT's
    /// no-headset Default (every send at -80 dB) applied until the next slot
    /// change or chambering weapon draw. Copying that state disconnected the
    /// electronics and left passive isolation only.
    /// <c>default</c> is the neutral 0 dB route.
    /// </summary>
    internal readonly struct HeadsetSendLevels : IEquatable<HeadsetSendLevels>
    {
        private const float MinimumDb = -80f;
        private const float MaximumDb = 20f;
        // EFT's own "no signal" level; a template at or below it on every
        // category carries no send data rather than an intentionally muted path.
        private const float SilentDb = -79.5f;

        internal static readonly HeadsetSendLevels Neutral = default;

        internal readonly float Guns;
        internal readonly float ClientPlayer;
        internal readonly float ObservedPlayer;
        internal readonly float Npc;
        internal readonly float EnvTechnical;
        internal readonly float EnvNature;
        internal readonly float EnvCommon;
        internal readonly float Ambient;
        internal readonly float EffectsReturns;
        internal readonly bool HasTemplateData;
        // The worn template's own compressor settings, read in the same place
        // as the sends; they modulate the electronics class within its bounds.
        internal readonly HeadsetTemplateElectronics Electronics;

        internal HeadsetSendLevels(
            float guns,
            float clientPlayer,
            float observedPlayer,
            float npc,
            float envTechnical,
            float envNature,
            float envCommon,
            float ambient,
            float effectsReturns)
        {
            Guns = Sanitize(guns);
            ClientPlayer = Sanitize(clientPlayer);
            ObservedPlayer = Sanitize(observedPlayer);
            Npc = Sanitize(npc);
            EnvTechnical = Sanitize(envTechnical);
            EnvNature = Sanitize(envNature);
            EnvCommon = Sanitize(envCommon);
            Ambient = Sanitize(ambient);
            EffectsReturns = Sanitize(effectsReturns);
            HasTemplateData = true;
            Electronics = default;
        }

        private HeadsetSendLevels(HeadsetSendLevels sends, HeadsetTemplateElectronics electronics)
        {
            this = sends;
            Electronics = electronics;
        }

        internal static HeadsetSendLevels From(HeadphonesTemplate template)
        {
            if (template == null) return Neutral;
            var levels = new HeadsetSendLevels(
                template.GunsCompressorSendLevel,
                template.ClientPlayerCompressorSendLevel,
                template.ObservedPlayerCompressorSendLevel,
                template.NpcCompressorSendLevel,
                template.EnvTechnicalCompressorSendLevel,
                template.EnvNatureCompressorSendLevel,
                template.EnvCommonCompressorSendLevel,
                template.AmbientCompressorSendLevel,
                template.EffectsReturnsCompressorSendLevel);
            // A cloned item without send data inherits -80 on every category.
            // Treat it as unknown and keep the electronic path audible.
            var electronics = HeadsetTemplateElectronics.From(template);
            return new HeadsetSendLevels(levels.AllSilent ? Neutral : levels, electronics);
        }

        internal bool AllSilent =>
            Guns <= SilentDb && ClientPlayer <= SilentDb && ObservedPlayer <= SilentDb &&
            Npc <= SilentDb && EnvTechnical <= SilentDb && EnvNature <= SilentDb &&
            EnvCommon <= SilentDb && Ambient <= SilentDb && EffectsReturns <= SilentDb;

        private static float Sanitize(float db) =>
            float.IsNaN(db) || float.IsInfinity(db) ? 0f : Math.Max(MinimumDb, Math.Min(MaximumDb, db));

        public bool Equals(HeadsetSendLevels other) =>
            Guns == other.Guns && ClientPlayer == other.ClientPlayer &&
            ObservedPlayer == other.ObservedPlayer && Npc == other.Npc &&
            EnvTechnical == other.EnvTechnical && EnvNature == other.EnvNature &&
            EnvCommon == other.EnvCommon && Ambient == other.Ambient &&
            EffectsReturns == other.EffectsReturns && HasTemplateData == other.HasTemplateData &&
            Electronics.Equals(other.Electronics);

        public override bool Equals(object obj) => obj is HeadsetSendLevels other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Guns.GetHashCode();
                hash = hash * 397 ^ ClientPlayer.GetHashCode();
                hash = hash * 397 ^ ObservedPlayer.GetHashCode();
                hash = hash * 397 ^ Npc.GetHashCode();
                hash = hash * 397 ^ EnvTechnical.GetHashCode();
                hash = hash * 397 ^ EnvNature.GetHashCode();
                hash = hash * 397 ^ EnvCommon.GetHashCode();
                hash = hash * 397 ^ Ambient.GetHashCode();
                hash = hash * 397 ^ EffectsReturns.GetHashCode();
                hash = hash * 397 ^ HasTemplateData.GetHashCode();
                return hash * 397 ^ Electronics.GetHashCode();
            }
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "guns={0:0.#} player={1:0.#} observed={2:0.#} npc={3:0.#} technical={4:0.#} " +
            "nature={5:0.#} common={6:0.#} ambient={7:0.#} returns={8:0.#} source={9}",
            Guns, ClientPlayer, ObservedPlayer, Npc, EnvTechnical, EnvNature, EnvCommon,
            Ambient, EffectsReturns, HasTemplateData ? "template" : "neutral") + Electronics;
    }

    /// <summary>
    /// BSG's own electronics settings on the worn template. They are game
    /// design, not device data: the composer uses them only to vary a
    /// construction class within fixed bounds, never to override a fact.
    /// <c>default</c> carries none.
    /// </summary>
    internal readonly struct HeadsetTemplateElectronics : IEquatable<HeadsetTemplateElectronics>
    {
        internal readonly float CompressorGainDb, CompressorAttackMs, CompressorReleaseMs, Distortion, HighpassHz;
        internal readonly bool HasData;

        internal HeadsetTemplateElectronics(float compressorGainDb, float compressorAttackMs,
            float compressorReleaseMs, float distortion, float highpassHz)
        {
            CompressorGainDb = compressorGainDb; CompressorAttackMs = compressorAttackMs;
            CompressorReleaseMs = compressorReleaseMs; Distortion = distortion; HighpassHz = highpassHz;
            HasData = true;
        }

        internal static HeadsetTemplateElectronics From(HeadphonesTemplate template)
        {
            if (template == null) return default;
            var values = new HeadsetTemplateElectronics(template.CompressorGain, template.CompressorAttack,
                template.CompressorRelease, template.Distortion, template.HighpassFreq);
            // A clone without electronics data inherits zeros everywhere except
            // the high-pass, whose class default is 100 Hz.
            return values.CompressorGainDb == 0f && values.CompressorAttackMs == 0f &&
                values.CompressorReleaseMs == 0f && values.Distortion == 0f
                ? default : values;
        }

        // float.Equals, not ==: a NaN in a modded template must still compare
        // equal to itself, or the route would reactivate on every poll.
        public bool Equals(HeadsetTemplateElectronics other) =>
            HasData == other.HasData && CompressorGainDb.Equals(other.CompressorGainDb) &&
            CompressorAttackMs.Equals(other.CompressorAttackMs) &&
            CompressorReleaseMs.Equals(other.CompressorReleaseMs) &&
            Distortion.Equals(other.Distortion) && HighpassHz.Equals(other.HighpassHz);

        public override bool Equals(object obj) => obj is HeadsetTemplateElectronics other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = HasData.GetHashCode();
                hash = hash * 397 ^ CompressorGainDb.GetHashCode();
                hash = hash * 397 ^ CompressorAttackMs.GetHashCode();
                hash = hash * 397 ^ CompressorReleaseMs.GetHashCode();
                hash = hash * 397 ^ Distortion.GetHashCode();
                return hash * 397 ^ HighpassHz.GetHashCode();
            }
        }

        public override string ToString() => !HasData ? "" : string.Format(CultureInfo.InvariantCulture,
            " template gain={0:0.#} attack={1:0.#} release={2:0.#} distortion={3:0.###} highpass={4:0.#}",
            CompressorGainDb, CompressorAttackMs, CompressorReleaseMs, Distortion, HighpassHz);
    }
}
