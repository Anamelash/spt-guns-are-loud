using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// Per-caliber facts read from the game's item templates. Each caliber is
    /// resolved once, on the main thread, the first time a shot of it is fired,
    /// and kept for the rest of the session.
    /// </summary>
    internal static class AmmoDatabase
    {
        private static readonly Dictionary<string, float> SingleProjectileMass =
            new Dictionary<string, float>();

        /// <summary>
        /// The payload mass of a round: its bullet for a single projectile,
        /// otherwise the median single projectile of its caliber across every
        /// ammunition template the game has loaded.
        /// </summary>
        internal static float PayloadMassGram(AmmoTemplate template)
        {
            if (template == null) return 0f;
            float caliberMass = template.ProjectileCount > 1
                ? SingleProjectileMassGram(template.Caliber)
                : 0f;
            return ShotLevelIndex.ResolvePayloadMassGram(
                template.BulletMassGram, template.ProjectileCount, caliberMass);
        }

        private static float SingleProjectileMassGram(string caliber)
        {
            string key = caliber ?? string.Empty;
            if (SingleProjectileMass.TryGetValue(key, out float cached)) return cached;
            if (!Singleton<ItemFactory>.Instantiated || Singleton<ItemFactory>.Instance?.ItemTemplates == null)
                return 0f;

            float median = ShotLevelIndex.MedianMassGram(SingleProjectileMasses(
                Singleton<ItemFactory>.Instance.ItemTemplates.Values, key));
            SingleProjectileMass[key] = median;
            Plugin.Log?.LogInfo(median > 0f
                ? $"caliber {key}: shot shells ranked by the median single projectile, {median:0.0} g"
                : $"caliber {key}: no single-projectile rounds, shot shells keep pellet mass x count");
            return median;
        }

        private static IEnumerable<float> SingleProjectileMasses(
            IEnumerable<ItemTemplate> templates,
            string caliber)
        {
            foreach (ItemTemplate template in templates)
            {
                if (template is AmmoTemplate ammo &&
                    ammo.ProjectileCount <= 1 &&
                    string.Equals(ammo.Caliber, caliber, System.StringComparison.Ordinal))
                    yield return ammo.BulletMassGram;
            }
        }
    }
}
