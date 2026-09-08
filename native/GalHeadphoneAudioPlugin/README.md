> Current archive installation uses the included BepInEx preloader and compiled DSP, without game-file patches. The loader requires the exact supported UnityPlayer hash from SPT 4.1.5. Metadata registration instructions below describe the legacy development workflow only.

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

EFT exposes no public managed API for native audio-effect registration. The
release includes `GunsAreLoud.Preloader.dll`, which registers this DSP through
a hash-guarded internal UnityPlayer function during BepInEx preload. It leaves
game metadata untouched. The build-specific entry point and registry layout
are documented by the preloader source; other player hashes fail closed.

The old `BuildSettings.preloadedPlugins` patch in `build/headphones` is retained
only for the historical development installation. It is not used by the release.

The passive ParamEQ controls live in the mixer rather than this DSP. Unity
2022.3 calibration identifies `Frequency gain` as the biquad coefficient
`A = 10^(gainDb / 40)` and the parameter labelled `Octave range` as inverse Q
(`1 / Q`), not literal octave bandwidth. Those semantics must be preserved by
the profile-to-mixer fit.
