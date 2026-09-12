using System;
using HarmonyLib;

namespace GunsAreLoud.Client.Patches
{
    /// <summary>
    /// Resolved once at load instead of a reflection call per shot. Fail-closed:
    /// if a game update renames or removes a field, the accessor stays null and
    /// every caller behaves as it did when reflection returned nothing.
    /// </summary>
    internal static class EftAudioFields
    {
        private static readonly AccessTools.FieldRef<WeaponSoundPlayer, SuperBetterAudioQueue> QueueRef =
            Resolve<WeaponSoundPlayer, SuperBetterAudioQueue>("_queue");
        private static readonly AccessTools.FieldRef<SuperBetterAudioQueue, int> LastSourceRef =
            Resolve<SuperBetterAudioQueue, int>("_lastSource");

        internal static bool QueueAvailable => QueueRef != null;

        internal static bool LastSourceAvailable => LastSourceRef != null;

        internal static SuperBetterAudioQueue Queue(WeaponSoundPlayer soundPlayer) =>
            soundPlayer == null || QueueRef == null ? null : QueueRef(soundPlayer);

        internal static int LastSource(SuperBetterAudioQueue queue) =>
            queue == null || LastSourceRef == null ? -1 : LastSourceRef(queue);

        private static AccessTools.FieldRef<TObject, TField> Resolve<TObject, TField>(string name)
            where TObject : class
        {
            try
            {
                return AccessTools.Field(typeof(TObject), name) == null
                    ? null
                    : AccessTools.FieldRefAccess<TObject, TField>(name);
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning(
                    $"audio field {typeof(TObject).Name}.{name} unavailable: {error.Message}");
                return null;
            }
        }
    }
}
