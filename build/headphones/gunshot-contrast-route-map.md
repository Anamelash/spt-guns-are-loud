# Gunshot contrast mixer route map (1.0.0 performance work)

Date: 2026-09-10. Source graph: SPT 4.1.5 `Audio/MasterMixer`.

## Insertion contract

Each entry in `GunshotContrastMixerRouteTable` names one exact existing group.
The generator creates an immediate child named `GAL Contrast Input` with one
compiled attenuation fader, exposed as `GAL_ContrastRoute00..51`. A direct
`AudioSource` moves to that child; its former output remains the parent. The
gain therefore occurs before every original parent effect and send. No stock
effect, send target, snapshot value or group identity is replaced.

At 0 dB the input is neutral. Runtime changes all 52 faders together over an
80 ms ramp. The source-level `GunshotContrastFilter` remains only as a fail-open
fallback when the replacement mixer or an explicit route is unavailable.

## Explicit inputs

| Area | Direct-source parent groups |
| --- | --- |
| Player | `ClientPlayer`, `ClientPlayerMovement`, `ClientPlayerSpeech`, `ClientPlayerSelfSpeechReverb` |
| Observed player | `ObservedPlayer`, `ObservedPlayerMovement`, `ObservedPlayerSpeech` |
| NPC | `NPC`, `VehicleInSpeech` |
| Environment | `Environment`, `TechnicalSounds`, `NatureSounds`, `CommonSounds`, `Vehicles`, `VehicleIn`, `VehicleOut`, `VehicleInRadio`, `Hideout` |
| Ambient | `Ambient`, `AmbCommonEffects`, `AmbientIn`, `AmbientOut`, `Rain`, `Radio`, `RadioLessVerb`, `OccludedIn`, both indoor bypasses, both common indoor branches, indoor precipitation, both common outdoor branches, outdoor precipitation, `OutEnvironment`, day/night parents, day/night effect and bypass branches, `RadioIn`, `RadioInLessVerb`, `RadioOut`, `EventRadio` |
| Occlusion | outer `World/Occlusion`, inner `World/Occlusion/Occlusion`, `LowerOccluded`, `UpperOccluded`, `SimpleOccluded` |
| Other direct sources | `Inventory`, `Guns/Instrumental` |

The two groups named `Occlusion` are separate object routes. Runtime resolves
the deeper exact path first and never treats a name alone as an identity. The
complete 52-path list and exposed-parameter mapping is canonical in
`client/GunsAreLoud.Client/Audio/GunshotContrastMixerRouteTable.cs`.

## Explicit exclusions

- `Guns`, `Gunshots`, `Occluded`, `OccludedTail`, headphone compressors and the
  shared headphone buses.
- Grenade voices. Their borrowed source is restored to its original parent as
  soon as the per-issuance grenade marker appears; returning a source to a pool
  clears that marker.
- Observed-player `Voip`, `VoipReverb`, UI, music and chat.
- `Main`, `World`, `Returns`, all shared reverb/delay returns and every unknown
  mod-added route.

## Verification recorded for this map

- Compiled comparison: stock 78 groups / 156 effects preserved; candidate 233
  effects; 12 headphone-electronics sends unchanged; 52 contrast inputs; no
  stock-graph differences.
- Every compiled contrast input has exactly one attenuation effect and 0 dB in
  all snapshots.
- Unity offline render (Sordin / Outdoor): seven representative parent chains
  are transparent at 0 dB. A -6 dB input matches the former direct-source gain
  with maximum absolute error `5.109608e-5` and RMS error `5.534757e-6`.
- The actual runtime resolver finds 52 unique pairs in Unity PlayMode and its
  0 -> -3 -> -6 -> 0 dB ramp readback matches all exposed faders.

Actual EFT source coverage, grenade pool reuse, headset/no-headset sound and
Vanilla/Realistic behavior still require the isolated in-game test. Until that
passes, the mixer step is a validated candidate, not accepted raid behavior.
