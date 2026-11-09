using System;

namespace GunsAreLoud.Client.Runtime
{
    /// <summary>
    /// Where an explosion stands on the shot level scale, by its distance, and
    /// how loud the engine is about to play it at the listener. The target is
    /// held on the K-weighted level of the first 240 ms: a blast is not an
    /// impulse, its energy arrives over a quarter of a second (the game's far
    /// recordings peak 220–300 ms after their onset), so the attack window of
    /// a shot would measure its rumble and not its level. Pure functions only.
    /// </summary>
    internal static class ExplosionLevelModel
    {
        /// <summary>The window the explosion level is measured and targeted on, seconds.</summary>
        internal const float WindowSeconds = 0.24f;

        /// <summary>
        /// A hand grenade (0.1 kg of TNT equivalent: RGD-5 110 g, F-1 60 g, M67
        /// about 0.23 kg) at this distance stands at <see cref="ReferenceIndexDb"/>
        /// on the shot scale: its peak overpressure there is 1.4–2.1 kPa
        /// (157–160 dB) by Kingery–Bulmash and Kinney–Graham, the level of a
        /// 7.62×39 rifle at the shooter's ear, which the scale puts at L 1.8–2.5.
        /// </summary>
        internal const float ReferenceDistanceMeters = 20f;
        internal const float ReferenceIndexDb = 4f;

        /// <summary>
        /// Level per decade of distance: 7.5 dB per halving of distance, between
        /// the far field of a blast (6 dB, pressure as 1/r) and the intermediate
        /// range of the hand grenade (8–9 dB at scaled distances of 10–40 m/kg^⅓).
        /// </summary>
        internal const float DecadeSlopeDb = 25f;

        /// <summary>A blast is never nearer than at the listener's feet.</summary>
        internal const float MinimumDistanceMeters = 1f;

        /// <summary>
        /// How far an explosion may be lifted over what the engine gives it. The
        /// engine keeps a tenth of a distant blast two-dimensional, so its own
        /// level never falls more than about 20 dB; this covers every distance
        /// at which the blast is still louder than the quietest shot of the scale.
        /// </summary>
        internal const float MaximumLiftDb = 24f;

        /// <summary>The shot level index of an explosion at this distance, dB.</summary>
        internal static float LevelIndex(float distanceMeters)
        {
            float distance = float.IsNaN(distanceMeters) || float.IsInfinity(distanceMeters)
                ? ReferenceDistanceMeters : Math.Max(MinimumDistanceMeters, distanceMeters);
            return ReferenceIndexDb + DecadeSlopeDb * (float)Math.Log10(ReferenceDistanceMeters / distance);
        }

        /// <summary>
        /// The level the explosion targets at the listener, on the attack
        /// scale of the shots: the energy target of its level index, with the
        /// room term of the listener's environment and the engine's own trim.
        /// </summary>
        internal static float TargetDb(float distanceMeters, bool listenerIndoor, float userGainDb) =>
            DirectLevelModel.EnergyTargetDb(LevelIndex(distanceMeters), listenerIndoor, userGainDb);

        /// <summary>
        /// The engine's distance attenuation of a source whose rolloff is a
        /// curve of distance (logarithmic or linear). Unity holds a source at
        /// its maximum distance's value beyond it.
        /// </summary>
        internal static float RolloffGain(bool logarithmic, float minDistance, float maxDistance, float distance)
        {
            float minimum = Math.Max(1e-3f, minDistance);
            float maximum = Math.Max(minimum, maxDistance);
            float d = Math.Min(Math.Max(0f, distance), maximum);
            if (logarithmic) return Math.Min(1f, minimum / Math.Max(minimum, d));
            if (maximum <= minimum) return d <= minimum ? 1f : 0f;
            return Math.Max(0f, Math.Min(1f, 1f - (d - minimum) / (maximum - minimum)));
        }

        /// <summary>
        /// What reaches the listener of a source's volume: the two-dimensional
        /// share at full level and the spatial share through the rolloff.
        /// </summary>
        internal static float SpatialGain(float spatialBlend, float rolloffGain)
        {
            float blend = Math.Max(0f, Math.Min(1f, Finite(spatialBlend)));
            float rolloff = Math.Max(0f, Math.Min(1f, Finite(rolloffGain)));
            return 1f - blend + blend * rolloff;
        }

        private static float Finite(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
    }
}
