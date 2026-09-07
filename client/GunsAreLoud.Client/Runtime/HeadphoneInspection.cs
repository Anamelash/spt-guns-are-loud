using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using GunsAreLoud.Client.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    // UI-only attributes: never write templates, mixer controls or persistent item data.
    internal sealed class HeadphoneInspection : MonoBehaviour
    {
        private sealed class Stamp { internal string Key; }
        private static readonly ConditionalWeakTable<Item, Stamp> Stamps = new ConditionalWeakTable<Item, Stamp>();
        private static readonly HashSet<ItemSpecificationPanel> Panels = new HashSet<ItemSpecificationPanel>();
        private static readonly FieldInfo InspectedItem = AccessTools.Field(typeof(ItemSpecificationPanel), "_item");
        private float _nextPoll;
        private string _lastKey;
        private static string Culture => LocalizationManager._instance?.Culture ?? "en";
        private static bool Russian => Culture.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        private static HeadphoneMode Requested => Plugin.ModConfig != null && Plugin.ModConfig.Enabled.Value
            ? Plugin.ModConfig.HeadphoneMode.Value : HeadphoneMode.Vanilla;

        private static string StateKey
        {
            get
            {
                var status = HeadphoneRouteRuntime.Instance?.Status ?? default;
                return Requested + "|" + Culture + "|" + status.TemplateId + "|" + status.Effective + "|" + status.Fallback;
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.25f;
            string key = StateKey;
            if (key == _lastKey) return;
            _lastKey = key;
            foreach (var panel in new List<ItemSpecificationPanel>(Panels))
            {
                if (panel == null || !panel.isActiveAndEnabled) { Panels.Remove(panel); continue; }
                panel.RecreateAttributeBars();
            }
        }

        private void OnDestroy() { Panels.Clear(); }

        internal static void Prepare(Item item)
        {
            if (item == null) return;
            HeadsetProfileRegistry.TryGet(item.TemplateId, out var profile);
            var template = item.Template as HeadphonesTemplate;
            if (profile == null && template == null) return;
            string key = StateKey;
            Stamp stamp = Stamps.GetValue(item, _ => new Stamp());
            if (stamp.Key == key) return;
            bool russian = Russian;
            string note = "";
            bool realistic = Requested == HeadphoneMode.Realistic && profile != null;
            var status = HeadphoneRouteRuntime.Instance?.Status ?? default;
            if (Requested == HeadphoneMode.Realistic && profile == null)
                note = russian ? "Профиль Realistic отсутствует. Показаны параметры Vanilla." : "No Realistic profile. Showing Vanilla parameters.";
            // Runtime status describes equipped gear only, not a shop item being previewed.
            if (realistic && HeadphoneInspectionValues.MatchesEquippedTemplate(status.TemplateId, item.TemplateId) && status.Effective != HeadphoneMode.Realistic &&
                status.Fallback != HeadphoneRouteFallback.None && status.Fallback != HeadphoneRouteFallback.NoHeadset)
            {
                realistic = false;
                note = russian ? "Realistic пока не применён. Показаны параметры Vanilla." : "Realistic is not applied yet. Showing Vanilla parameters.";
            }
            item.Attributes.RemoveAll(a => a.Id is HeadphoneInspectionId);
            if (!realistic && template == null) { stamp.Key = key; return; }
            var rows = HeadphoneInspectionValues.Build(realistic ? profile : null,
                template?.CompressorRelease ?? 0, template?.CompressorGain ?? 0, russian);
            foreach (var row in rows)
            {
                string tooltip = note;
                if (string.IsNullOrEmpty(tooltip) && (int)row.Id > 0)
                    tooltip = russian ? "Пассивное ослабление в режиме Realistic." : "Passive attenuation in Realistic mode.";
                item.Attributes.Add(new ItemAttribute(row.Id)
                {
                    Name = row.Name,
                    DisplayNameFunc = () => row.Name,
                    Base = () => row.Value,
                    StringValue = () => row.Text,
                    // Compact panels cache FullStringValue even before examination.
                    FullStringValue = () => "",
                    Tooltip = () => tooltip,
                    DisplayType = () => EItemAttributeDisplayType.Compact
                });
            }
            stamp.Key = key;
        }

        [HarmonyPatch(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.RecreateAttributeBars))]
        private static class RecreatePatch
        {
            private static void Prefix(ItemSpecificationPanel __instance)
            {
                Panels.Add(__instance);
                Prepare(InspectedItem.GetValue(__instance) as Item);
            }
        }

        [HarmonyPatch(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Compare))]
        private static class ComparePatch
        {
            private static void Prefix(Item compareItem) { Prepare(compareItem); }
        }

        [HarmonyPatch(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.SyncCompareAttributes))]
        private static class SyncComparePatch
        {
            private static void Prefix(Item compareItem) { Prepare(compareItem); }
        }

        [HarmonyPatch(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Close))]
        private static class ClosePatch
        {
            private static void Prefix(ItemSpecificationPanel __instance) { Panels.Remove(__instance); }
        }

        [HarmonyPatch(typeof(StaticIcons), nameof(StaticIcons.GetAttributeIcon))]
        private static class IconPatch
        {
            private static bool Prefix(Enum id, ref Sprite __result)
            {
                if (!(id is HeadphoneInspectionId)) return true;
                __result = null;
                return false;
            }
        }
    }
}
