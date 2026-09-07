# Guns Are Loud

Guns Are Loud keeps EFT's weapon recordings and gives first-person shots more weight, clearer caliber differences, stronger indoor presence, and temporary hearing loss and ringing. Active headsets combine passive isolation with an electronic listening path; grenade blasts also pass through the headset model and can affect hearing for much longer than gunfire.

The mod includes headset profiles drawn from a documented reference, support for identified modded headset variants, distance- and barrier-dependent blast exposure, and separate intensity and duration controls for shot and explosion effects. Close blasts can cause prolonged hearing loss with a plateau followed by gradual recovery.

F12 is organized into General, Gunshots, Explosions, and Low-level & debug. Enable Advanced to see the low-level controls. General offers Vanilla and Realistic headset processing. Headset inspection shows the corresponding compressor values; Realistic also shows passive attenuation averaged into low, mid, and high bands. An asterisk identifies an approximation or transferred family profile.

The mod changes the local player's sound and perception. It does not alter ballistics, damage, AI hearing, remote gunshot sources, or EFT's original sound assets. Headset characteristics are supplied on the client; no server-side item changes are required. Unknown headsets or unavailable processing routes fall back to Vanilla.

Read [CHANGELOG.md](CHANGELOG.md) for the player-visible differences from vanilla, [MODEL.md](MODEL.md) for the calculations and limitations, and the [headset reference](docs/reference/headphones/README.md) for source data and calibration choices. The model uses relative digital levels, not calibrated real-world sound pressure.

## Installation

The client archive targets SPT 4.1.3. With the game closed, extract it into the SPT installation root so that `GunsAreLoud.Client.dll` is under `BepInEx/plugins/GunsAreLoud`. Existing configuration files are preserved.

**The client archive alone does not activate Realistic headset processing.** It requires the native GAL audio effect to be registered before Unity starts. Without that prerequisite, headset audio uses Vanilla fallback. The [native registration tooling](build/headphones/README.md) is locked to the development installation and is not a portable end-user installer. Gunshot and explosion hearing features do not require that native headset effect.
