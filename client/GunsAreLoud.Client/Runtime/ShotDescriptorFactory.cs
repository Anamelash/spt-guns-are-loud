using System;
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
        private static readonly FieldInfo PlayersBridgeField =
            AccessTools.Field(typeof(BaseSoundPlayer), "playersBridge");

        internal static bool CompatibilityAvailable => PlayersBridgeField != null;

        internal static bool TryGetLocalBridge(WeaponSoundPlayer soundPlayer,
            out BaseSoundPlayer.PlayerBridge bridge)
        {
            bridge = soundPlayer != null
                ? PlayersBridgeField?.GetValue(soundPlayer) as BaseSoundPlayer.PlayerBridge
                : null;
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

            if (soundPlayer == null || ammo == null || PlayersBridgeField == null)
            {
                return false;
            }

            var bridge = PlayersBridgeField.GetValue(soundPlayer) as BaseSoundPlayer.PlayerBridge;
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

            int summedModLoudness = 0;
            foreach (Mod mod in weapon.Mods)
            {
                if (mod != null)
                {
                    summedModLoudness += mod.Loudness;
                }
            }

            string weaponClass = weapon.Template?.weapClass ?? string.Empty;
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
                WeaponCategory = ClassifyWeapon(weaponClass),
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

        private static WeaponCategory ClassifyWeapon(string weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass))
            {
                return WeaponCategory.Unknown;
            }

            string value = weaponClass.ToLowerInvariant();
            if (value.Contains("pistol") || value.Contains("revolver"))
            {
                return WeaponCategory.Pistol;
            }

            if (value.Contains("smg") || value.Contains("pdw"))
            {
                return WeaponCategory.Compact;
            }

            if (value.Contains("grenade") || value.Contains("special"))
            {
                return WeaponCategory.Heavy;
            }

            if (value.Contains("rifle") ||
                value.Contains("carbine") ||
                value.Contains("shotgun") ||
                value.Contains("machinegun"))
            {
                return WeaponCategory.LongGun;
            }

            return WeaponCategory.Unknown;
        }
    }
}
