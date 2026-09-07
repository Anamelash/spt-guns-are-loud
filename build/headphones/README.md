# Headphone mixer generator

Use Unity 2022.3.43f1. Create a minimal project at
`docs/internal/headphone-model-2026-09-07/mixer-project`, copy the verified
original mixer to `Assets/Validation/VanillaMasterMixer.mixer`, and copy
`MixerRouteGenerator.cs` and `MixerOfflineValidation.cs` to `Assets/Editor`.
The generator makes fresh baseline/candidate copies from that immutable input.
Both the game's Meta XR reflection plugin and the reviewed GAL audio plugin
must be preloaded in the Editor before opening this project.

Run:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\2022.3.43f1\Editor\Unity.exe' `
  -batchmode -quit `
  -projectPath 'D:\Games\SPT mods\spt-guns-are-loud\docs\internal\headphone-model-2026-09-07\mixer-project' `
  -executeMethod GAL.HeadphoneMixerGenerator.Build `
  -logFile 'D:\Games\SPT mods\spt-guns-are-loud\docs\internal\headphone-model-2026-09-07\mixer-project.log'
```

The generator wraps every dry child of `World` except `Headphones` in a
`GAL Passive` bus. Its nine ParamEQ effects are neutral in every original
snapshot. The stock headphone subtree remains intact. Twelve new parallel sends
feed one `GAL Electronics` receive and one persistent native processor. Nine
routes follow the existing Guns, player, NPC, environment, ambient and
effects-return category sends; three direct routes cover NonspatialBypass, Voip
and Occlusion. In Vanilla, the new sends and electronic bus are silent, and the
passive bus is neutral.

The generated bundle is a prototype until the offline renderer confirms that
Vanilla is identical and the fitted passive curve meets its error bound.

The native renderer loads compiled bundles rather than Editor mixer controllers,
whose exposed-name lookup differs from the game's hash lookup.
`verify_compiled_mixer.py` compares stock groups, effects, sends, exposed
parameters and all snapshot values to the current game's compiled constants
by GUID. It permits the documented passive reparenting and inserted nodes in
the serialized effect list; it does not excuse changes to stock send targets.

Native Unity 2022.3 calibration is essential. ParamEQ `Frequency gain` is the
biquad amplitude coefficient `A = 10^(gainDb / 40)`, so a control value of 2
produces approximately +12.0412 dB at the band center. The parameter labelled
`Octave range` behaves as inverse Q (`1 / Q`), not as a bandwidth measured in
octaves. Native spectral, shared-detector and Vanilla-equivalence checks remain
required before embedding or installing a candidate bundle.

## Native effect registration

Unity 2022.3 has no managed runtime API for registering a new native mixer
effect. `install-native-plugin.ps1` can register the pinned
`AudioPluginGalHeadphones.dll` in the exact supported isolated installation's
serialized `BuildSettings.preloadedPlugins` before the player starts. The
ordinary client-only package does not run this installer or satisfy this native
prerequisite; without it, Realistic mode must remain on its Vanilla fallback.

The default is a read-only dry run:

```powershell
./build/headphones/install-native-plugin.ps1
```

`-Apply` is permitted only when the pinned DLL hash matches and the game is
closed. It backs up the original metadata and DLL state, verifies that only
BuildSettings object 11 changes, reloads the serialized output, and atomically
replaces the file.
`-Restore` requires exact installed hashes and restores only that backup. The
tool never closes the game or edits the Guns Are Loud configuration.
