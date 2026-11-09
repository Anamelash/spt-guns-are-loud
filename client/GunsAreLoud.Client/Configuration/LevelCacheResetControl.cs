using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using UnityEngine;

namespace GunsAreLoud.Client.Configuration
{
    /// <summary>
    /// The F12 button that forgets every measured recording: the persistent
    /// cache is emptied and the weapon in hand is measured again. Drawn by the
    /// Configuration Manager on the main thread.
    /// </summary>
    internal static class LevelCacheResetControl
    {
        private static string _status = "";
        private static float _statusUntil;
        private static GUIStyle _style;

        internal static void Draw(ConfigEntryBase unused)
        {
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { wordWrap = true };
            if (GUILayout.Button("Reset measurements", GUILayout.ExpandWidth(false)))
            {
                int dropped = Reset();
                _status = $"{dropped} entries dropped; recordings are measured again as weapons are equipped";
                _statusUntil = Time.realtimeSinceStartup + 8f;
            }
            if (Time.realtimeSinceStartup < _statusUntil) GUILayout.Label(_status, _style);
        }

        /// <summary>Empties the persistent cache and the session's measurements. Returns the number of cached entries dropped.</summary>
        internal static int Reset()
        {
            int dropped = LevelCache.Reset();
            AutomaticWeaponWarmup.ForgetMeasurements();
            Plugin.Log?.LogInfo($"level cache reset from F12: {dropped} entries dropped, recordings are measured again");
            return dropped;
        }
    }
}
