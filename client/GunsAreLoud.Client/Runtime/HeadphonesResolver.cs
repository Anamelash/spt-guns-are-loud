using System;
using EFT.InventoryLogic;

namespace GunsAreLoud.Client.Runtime
{
    internal readonly struct EquippedHeadphonesState
    {
        internal readonly bool Active;
        internal readonly string Name, MixerName, Source;
        internal readonly float ThresholdDb;

        internal EquippedHeadphonesState(bool active, string name, string mixerName, string source, float threshold)
        {
            Active = active; Name = name; MixerName = mixerName; Source = source; ThresholdDb = threshold;
        }
    }

    internal static class HeadphonesResolver
    {
        // The headwear branch walks a collection, and this is asked once per shot
        // and five times a second by the route poll. EFT applies a headset template
        // whenever the worn set changes, which invalidates this; the short expiry
        // is the backstop for any equip path that does not.
        private const float CacheSeconds = 0.5f;
        private static InventoryEquipment _cachedEquipment;
        private static Headphones _cachedItem;
        private static float _cacheExpiry;

        internal static void Invalidate()
        {
            _cachedEquipment = null;
            _cachedItem = null;
            _cacheExpiry = 0f;
        }

        internal static HeadphonesTemplate FindEquipped(InventoryEquipment equipment)
            => FindEquippedItem(equipment)?.Template;

        internal static Headphones FindEquippedItem(InventoryEquipment equipment)
        {
            if (equipment == null) return null;
            if (ReferenceEquals(equipment, _cachedEquipment) &&
                UnityEngine.Time.unscaledTime < _cacheExpiry)
                return _cachedItem;
            Headphones resolved = Resolve(equipment);
            _cachedEquipment = equipment;
            _cachedItem = resolved;
            _cacheExpiry = UnityEngine.Time.unscaledTime + CacheSeconds;
            return resolved;
        }

        private static Headphones Resolve(InventoryEquipment equipment)
        {
            if (equipment.GetSlot(EquipmentSlot.Earpiece)?.ContainedItem is Headphones earpiece)
                return earpiece;
            // Same slots and precedence as EFT.Player.UpdatePhonesReally. Never
            // scan pockets/backpack and mistake carried headphones for worn ones.
            if (equipment.GetSlot(EquipmentSlot.Headwear)?.ContainedItem is CompoundItem headwear)
                foreach (Item item in headwear.GetAllItemsFromCollection())
                    if (item is Headphones mounted) return mounted;
            return null;
        }

        internal static EquippedHeadphonesState Resolve(
            HeadphonesTemplate equipped, HeadphonesTemplate mixer, bool equipmentKnown)
        {
            string mixerName = Name(mixer);
            HeadphonesTemplate selected = equipmentKnown ? equipped : IsActiveMixer(mixer) ? mixer : null;
            return new EquippedHeadphonesState(selected != null, Name(selected), mixerName,
                equipmentKnown ? "equipment" : "mixer-fallback", selected?.CompressorThreshold ?? 0f);
        }

        private static string Name(HeadphonesTemplate template) =>
            template == null ? "None" : string.IsNullOrEmpty(template.ShortName) ? "Unknown" : template.ShortName;

        private static bool IsActiveMixer(HeadphonesTemplate template) => template != null &&
            !string.IsNullOrWhiteSpace(template.ShortName) &&
            !string.Equals(template.ShortName, "Default", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(template.ShortName, "LowMute", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(template.ShortName, "StrongMute", StringComparison.OrdinalIgnoreCase);
    }
}
