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
            return levels.AllSilent ? Neutral : levels;
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
            EffectsReturns == other.EffectsReturns && HasTemplateData == other.HasTemplateData;

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
                return hash * 397 ^ HasTemplateData.GetHashCode();
            }
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "guns={0:0.#} player={1:0.#} observed={2:0.#} npc={3:0.#} technical={4:0.#} " +
            "nature={5:0.#} common={6:0.#} ambient={7:0.#} returns={8:0.#} source={9}",
            Guns, ClientPlayer, ObservedPlayer, Npc, EnvTechnical, EnvNature, EnvCommon,
            Ambient, EffectsReturns, HasTemplateData ? "template" : "neutral");
    }
}
