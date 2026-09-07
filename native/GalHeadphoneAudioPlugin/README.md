# GAL Headphone Electronics native DSP

Windows x64 Unity native audio effect implementing the common, stereo-linked
electronic headphone branch. A single persistent effect instance receives the
replacement mixer's 12 world-audio feeds. It exposes 12 controls: microphone
high-pass and low-pass, quiet gain, threshold, ratio, knee, attack, hold,
release, ceiling, wet and reset. `Wet=0` is an exact, state-neutral bypass. The
threshold is dBFS prototype data, not physical SPL.

Run `./build.ps1`. The build fetches Unity Technologies' official `NativeAudioPlugins` repository and compiles against `NativeCode/AudioPluginInterface.h`; the tested SDK revision was `bc7893edbba4c8a592777e590e34f21a11d762b4`.

Unity registration uses the documented `UnityGetAudioEffectDefinitions` export.
The DLL must be available to Unity's native plug-in importer/player before a
mixer containing `GAL Headphone Electronics` is loaded. ABI tests and Unity
Editor enumeration are prerequisite evidence; they do not establish that the
effect is installed in EFT or accepted in a live raid.

EFT exposes no supported managed API for late native-audio-effect registration.
The integration route is install-time: add the DLL name to the player's
`BuildSettings.preloadedPlugins` and place the DLL in the player's
`Plugins/x86_64` directory before process launch. The guarded dry-run/apply/
restore workflow is documented in `build/headphones/README.md`; a client-only
BepInEx package does not perform these changes.

The passive ParamEQ controls live in the mixer rather than this DSP. Unity
2022.3 calibration identifies `Frequency gain` as the biquad coefficient
`A = 10^(gainDb / 40)` and the parameter labelled `Octave range` as inverse Q
(`1 / Q`), not literal octave bandwidth. Those semantics must be preserved by
the profile-to-mixer fit.
