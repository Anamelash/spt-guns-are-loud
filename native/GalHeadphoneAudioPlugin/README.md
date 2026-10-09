> Current archive installation uses the included BepInEx preloader and compiled DSP, without game-file patches. The loader requires the exact supported UnityPlayer hash from SPT 4.1.5. Metadata registration instructions below describe the legacy development workflow only.

# GAL Headphone Electronics native DSP

Windows x64 Unity native audio effect implementing the common, stereo-linked
electronic headphone branch. A single persistent effect instance receives the
replacement mixer's 12 world-audio feeds. ABI 3 (`GAL_HeadphonesAbiVersion`,
plugin version `0x00010003`) exposes 20 controls. Slots 0–11 are the ABI 1
set and keep their indices: microphone high-pass and low-pass, quiet gain,
threshold, ratio, knee, attack, hold, release, ceiling, wet and reset. Slots
12–18 (ABI 2) colour the path: low shelf gain and corner, presence gain and
centre (Q 1.2), noise (dBFS RMS), saturation and delay (ms). Slot 19 (ABI 3) is
the band order: 1 keeps the first-order microphone edges, 2 replaces them with
an RBJ Butterworth high-pass and low-pass at the same corners (12 dB/oct).

Processing order: microphone high-pass and low-pass, white self-noise (an own
xorshift32 per channel), RBJ low shelf and presence peak, the linked detector
and gain, the output stage, the delay line, then the wet mix. Noise enters
before the detector, so it follows the quiet gain and sinks under
compression. Saturation 0 is the ABI 1 hard clamp; above 0 the signal is
linear to `ceiling * (1 - saturation)` and bends into the ceiling with a tanh
knee. Delay is a whole number of samples, up to 8 ms at 192 kHz. A control at
its default (shelf 0, presence 0, noise -120, saturation 0, delay 0, band order
1) leaves its stage out, and the tests prove the output then equals ABI 1 bit
for bit.
`Wet=0` is an exact, state-neutral bypass. Reset clears the filters, delay line
and noise generators. The threshold is dBFS prototype data, not physical SPL.

The build is reproducible from the repository path; `build.ps1` prints the
artifact's SHA-256, which the preloader and `build/package.ps1` pin.

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
