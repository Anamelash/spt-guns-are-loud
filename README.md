# Guns Are Loud

Guns Are Loud keeps EFT's weapon recordings and gives first-person shots more weight, clearer caliber differences, stronger indoor presence, and temporary hearing loss and ringing. Active headsets combine passive isolation with an electronic listening path; grenade blasts also pass through the headset model and can affect hearing for much longer than gunfire.

The mod includes headset profiles drawn from a documented reference, support for identified modded headset variants, distance- and barrier-dependent blast exposure, and separate intensity and duration controls for shot and explosion effects. Close blasts can cause prolonged hearing loss with a plateau followed by gradual recovery.

F12 is organized into General, Gunshots, Explosions, and Low-level & debug. Enable Advanced to see the low-level controls. General offers Vanilla and Realistic headset processing. Headset inspection shows the corresponding compressor values; Realistic also shows passive attenuation averaged into low, mid, and high bands. An asterisk identifies an approximation or transferred family profile.

The mod changes the local player's sound and perception. It does not alter ballistics, damage, AI hearing, remote gunshot sources, or EFT's original sound assets. Headset characteristics are supplied on the client; no server-side item changes are required. Unknown headsets or unavailable processing routes fall back to Vanilla.

Read [CHANGELOG.md](CHANGELOG.md) for the player-visible differences from vanilla, [MODEL.md](MODEL.md) for the calculations and limitations, and the [headset reference](docs/reference/headphones/README.md) for source data and calibration choices. The model uses relative digital levels, not calibrated real-world sound pressure.

## Installation

Version 1.0.0.

The archive targets SPT 4.1.5 on Windows x64 with the verified Unity 2022.3.43f1 player build. Close the game and extract the archive into the SPT installation root. Merge the BepInEx folders; do not replace the whole BepInEx directory.

Included components:

- `BepInEx/plugins/GunsAreLoud/GunsAreLoud.Client.dll` (client and embedded mixer).
- `BepInEx/patchers/GunsAreLoud/GunsAreLoud.Preloader.dll` (early registration).
- `BepInEx/patchers/GunsAreLoud/AudioPluginGalHeadphones.dll` (native DSP).

Launch through the SPT launcher. No Unity Editor, compilation, separate installer or game-file patch is required. Existing configuration is preserved. The preloader checks exact UnityPlayer and DSP hashes; other player builds are not automatically supported.

F12 / General / Headset Diagnostics displays `DSP loading`, `DSP ready`, `DSP active` or `DSP error`. Active requires a verified Realistic route and advancing native processing counters. Loading is expected before the raid mixer is requested. Ready means the DSP is available but active processing is not currently confirmed, including Vanilla/no-headset use.

## Uninstallation

Close the game and remove `BepInEx/plugins/GunsAreLoud` and `BepInEx/patchers/GunsAreLoud`. Optionally remove `BepInEx/config/com.anamelash.gunsareloud.cfg` to reset settings. This archive does not modify globalgamemanagers or install files in the game's native Plugins directory.

Older development installations with manual native registration must first restore their own original metadata backup and remove the legacy native DLL. Do not remove that DLL while leaving its old preload entry in game metadata.
