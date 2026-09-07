using System;
using System.Collections.Generic;

namespace GunsAreLoud.Client.Audio
{
    internal static class GunshotContrastModel
    {
        // Verified non-gun source groups. Do NOT infer children of World/Main:
        // they include shared gun returns and headphones. Unknown routes fail open.
        private static readonly HashSet<string> AttenuatedGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            "Environment", "ClientPlayer", "ClientPlayerMovement", "ClientPlayerSpeech",
            "ClientPlayerSelfSpeechReverb", "ObservedPlayer", "ObservedPlayerMovement",
            "ObservedPlayerSpeech", "NPC", "VehicleInSpeech", "TechnicalSounds", "NatureSounds",
            "CommonSounds", "Vehicles", "VehicleIn", "VehicleOut", "Hideout", "Inventory", "Instrumental",
            "Ambient", "AmbCommonEffects", "AmbientIn", "AmbientOut", "Rain", "Radio", "RadioLessVerb",
            "OccludedIn", "AmbientInDayGroupBypass", "AmbientInNightGroupBypass", "CommonAmbInEffects",
            "CommonAmbInBypass", "PrecipitationAmbientIn", "CommonAmbOutEffects", "CommonAmbOutBypass",
            "PrecipitationAmbientOut", "OutEnvironment", "AmbientOutDay", "AmbientOutNight",
            "AmbientOutDayEffects", "AmbientOutDayBypass", "AmbientOutNightEffects", "AmbientOutNightBypass",
            "RadioIn", "RadioInLessVerb", "RadioOut", "EventRadio", "VehicleInRadio",
            "Occlusion", "LowerOccluded", "UpperOccluded", "SimpleOccluded"
        };

        internal static bool ShouldAttenuate(string group) => group != null && AttenuatedGroups.Contains(group);
        internal static bool SafeSourceLayout(int sourcesOnObject) => sourcesOnObject == 1;

        internal static float Gain(float attenuationDb)
        {
            if (float.IsNaN(attenuationDb) || float.IsInfinity(attenuationDb)) return 1f;
            return (float)Math.Pow(10, -Math.Max(0f, Math.Min(18f, attenuationDb)) / 20.0);
        }

        internal static float RouteGain(string group, bool enabled, bool inGame, float attenuationDb) =>
            enabled && inGame && ShouldAttenuate(group) ? Gain(attenuationDb) : 1f;

        internal static void Apply(float[] data, float gain)
        {
            if (data == null || gain >= 1f || gain < 0f || float.IsNaN(gain)) return;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
        }
    }
}
