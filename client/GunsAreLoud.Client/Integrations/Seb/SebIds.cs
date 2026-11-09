namespace GunsAreLoud.Client.Integrations.Seb
{
    /// <summary>
    /// Compile-time names of Sound Engine Backport. The only part of the SEB
    /// module the core may name: load order and patch order are attributes and
    /// cannot be decided at runtime.
    /// </summary>
    internal static class SebIds
    {
        // The source declares the first; the 1.0.1 release notes announce the
        // second. Both are accepted until one of them is confirmed gone.
        internal const string ClientGuid = "red.soundenginebackport.client";
        internal const string ReleaseGuid = "red.soundenginebackport";
        internal static readonly string[] KnownGuids = { ClientGuid, ReleaseGuid };

        internal const string DisplayName = "Sound Engine Backport";
        internal const string BundleFileName = "live_mastermixer.bundle";
        internal const string PatchesTypeName = "SoundEngineBackport.WeaponSoundPlayerPatches";
        internal const string LiveMixerTypeName = "SoundEngineBackport.LiveEngine.LiveMixer";

        /// <summary>
        /// The one live mixer bundle the embedded variant was generated from.
        /// Any other is a different mixer: the variant stays out, SEB installs
        /// its own and the headset route reports why it is off.
        /// </summary>
        internal const string SupportedLiveBundleSha256 =
            "67623c0ff15ff485eaebb67417d763443bb7c5287c1a20440fca3d35e1fa3fbb";
    }
}
