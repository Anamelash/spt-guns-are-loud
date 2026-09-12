using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace GunsAreLoud.Client.Runtime
{
    internal static class ShotDescriptorFactory
    {
        // A resolved field reference instead of FieldInfo.GetValue on the firing
        // path. The field is declared as the bridge interface, not as the concrete
        // PlayerBridge, and Harmony requires the exact declared type; the cast to
        // the concrete bridge is the one the FieldInfo version also performed.
        // Kept for a game build where the fast accessor cannot be built: local
        // shots must keep working through plain reflection rather than stop.
        private static readonly FieldInfo PlayersBridgeField =
            AccessTools.Field(typeof(BaseSoundPlayer), "playersBridge");
        private static readonly AccessTools.FieldRef<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>
            PlayersBridgeRef = ResolvePlayersBridge();

        private static AccessTools.FieldRef<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>
            ResolvePlayersBridge()
        {
            FieldInfo field = PlayersBridgeField;
            if (field == null)
            {
                Plugin.Log?.LogWarning(
                    "local shot detection unavailable: BaseSoundPlayer.playersBridge is missing");
                return null;
            }
            try
            {
                return AccessTools.FieldRefAccess<BaseSoundPlayer, BaseSoundPlayer.IObserverToPlayerBridge>(field);
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning(
                    "local shot detection falls back to reflection: " + error.Message);
                return null;
            }
        }

        internal static bool CompatibilityAvailable => PlayersBridgeField != null;

        /// <summary>True when the shot path avoids reflection entirely.</summary>
        internal static bool FastBridgeAccessAvailable => PlayersBridgeRef != null;

        private static BaseSoundPlayer.PlayerBridge Bridge(WeaponSoundPlayer soundPlayer)
        {
            if (soundPlayer == null) return null;
            if (PlayersBridgeRef != null) return PlayersBridgeRef(soundPlayer) as BaseSoundPlayer.PlayerBridge;
            return PlayersBridgeField?.GetValue(soundPlayer) as BaseSoundPlayer.PlayerBridge;
        }

        internal static bool TryGetLocalBridge(WeaponSoundPlayer soundPlayer,
            out BaseSoundPlayer.PlayerBridge bridge)
        {
            bridge = Bridge(soundPlayer);
            return bridge?._player != null && bridge._player.IsYourPlayer &&
                bridge.PointOfView == EPointOfView.FirstPerson;
        }

        internal static bool TryCreate(
            WeaponSoundPlayer soundPlayer,
            Ammo ammo,
            Vector3 shotPosition,
            Vector3 shotDirection,
            out ShotDescriptor descriptor)
        {
            descriptor = null;

            if (soundPlayer == null || ammo == null)
            {
                return false;
            }

            BaseSoundPlayer.PlayerBridge bridge = Bridge(soundPlayer);
            if (bridge == null || bridge.PointOfView != EPointOfView.FirstPerson)
            {
                return false;
            }

            Player player = bridge._player;
            if (player == null || !player.IsYourPlayer)
            {
                return false;
            }

            var weapon = bridge.Weapon as Weapon;
            if (weapon == null)
            {
                return false;
            }

            Transform fireport = null;
            bool isLeftStance = false;
            if (player.ProceduralWeaponAnimation != null)
            {
                isLeftStance = player.ProceduralWeaponAnimation.InLeftStance;
                if (player.ProceduralWeaponAnimation.HandsContainer != null)
                {
                    fireport = player.ProceduralWeaponAnimation.HandsContainer.Fireport;
                }
            }

            WeaponAudioFacts facts = GetFacts(weapon);
            int summedModLoudness = facts.SummedModLoudness;
            string weaponClass = facts.WeaponClass;
            HeadphonesTemplate mixerHeadphones = Singleton<BetterAudio>.Instantiated &&
                Singleton<BetterAudio>.Instance != null
                ? Singleton<BetterAudio>.Instance.CurrentHeadphonesTemplate : null;
            InventoryEquipment equipment = player.Equipment;
            EquippedHeadphonesState headphones = HeadphonesResolver.Resolve(
                HeadphonesResolver.FindEquipped(equipment), mixerHeadphones, equipment != null);

            descriptor = new ShotDescriptor
            {
                DspTime = AudioSettings.dspTime,
                AmmoCaliber = ammo.Caliber ?? string.Empty,
                BulletMassGram = Mathf.Max(0f, ammo.BulletMassGram),
                MuzzleVelocity = Mathf.Max(0f, weapon.TotalVelocity > 0f ? weapon.TotalVelocity : ammo.InitialSpeed),
                ProjectileCount = Mathf.Max(1, ammo.ProjectileCount),
                WeaponClass = weaponClass,
                WeaponCategory = facts.Category,
                WeaponTemplateId = weapon.StringTemplateId ?? string.Empty,
                IsSuppressed = soundPlayer.IsSilenced,
                SummedModLoudness = summedModLoudness,
                IsIndoor = bridge.Environment == EnvironmentType.Indoor,
                IsLeftStance = isLeftStance,
                MuzzlePosition = fireport != null ? fireport.position : shotPosition,
                MuzzleForward = fireport != null ? fireport.forward : shotDirection.normalized,
                HasActiveHeadphones = headphones.Active,
                HeadphonesName = headphones.Name,
                MixerHeadphonesName = headphones.MixerName,
                HeadphonesSource = headphones.Source,
                HeadphonesCompressorThresholdDb = headphones.ThresholdDb
            };

            return true;
        }

        /// <summary>
        /// What a shot needs to know about the weapon itself, rather than about
        /// this particular round. It changes only when the weapon is modified, so
        /// it is resolved per weapon instance and re-read when the mod count does
        /// not match what was cached.
        /// </summary>
        private sealed class WeaponAudioFacts
        {
            internal int ModCount;
            internal int SummedModLoudness;
            internal string WeaponClass;
            internal WeaponCategory Category;
        }

        private static readonly ConditionalWeakTable<Weapon, WeaponAudioFacts> Facts =
            new ConditionalWeakTable<Weapon, WeaponAudioFacts>();

        private static WeaponAudioFacts GetFacts(Weapon weapon)
        {
            int modCount = 0;
            int summedModLoudness = 0;
            foreach (Mod mod in weapon.Mods)
            {
                modCount++;
                if (mod != null) summedModLoudness += mod.Loudness;
            }

            if (Facts.TryGetValue(weapon, out WeaponAudioFacts cached) &&
                cached.ModCount == modCount &&
                cached.SummedModLoudness == summedModLoudness)
                return cached;

            string weaponClass = weapon.Template?.weapClass ?? string.Empty;
            var facts = new WeaponAudioFacts
            {
                ModCount = modCount,
                SummedModLoudness = summedModLoudness,
                WeaponClass = weaponClass,
                Category = ClassifyWeapon(weaponClass)
            };
            if (cached != null) Facts.Remove(weapon);
            Facts.Add(weapon, facts);
            return facts;
        }

        // Case-insensitive comparison instead of a lowered copy: the class string
        // came from the template and is re-read for every shot.
        private static bool Contains(string value, string token) =>
            value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static WeaponCategory ClassifyWeapon(string weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass))
            {
                return WeaponCategory.Unknown;
            }

            if (Contains(weaponClass, "pistol") || Contains(weaponClass, "revolver"))
            {
                return WeaponCategory.Pistol;
            }

            if (Contains(weaponClass, "smg") || Contains(weaponClass, "pdw"))
            {
                return WeaponCategory.Compact;
            }

            if (Contains(weaponClass, "grenade") || Contains(weaponClass, "special"))
            {
                return WeaponCategory.Heavy;
            }

            if (Contains(weaponClass, "rifle") ||
                Contains(weaponClass, "carbine") ||
                Contains(weaponClass, "shotgun") ||
                Contains(weaponClass, "machinegun"))
            {
                return WeaponCategory.LongGun;
            }

            return WeaponCategory.Unknown;
        }
    }
}
