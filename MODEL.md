# Guns Are Loud: Physical and Perceptual Model

## Purpose

Guns Are Loud is a model of first-person acoustic perception, not a replacement sound pack. EFT remains responsible for the weapon recording, shot timing, distance layers, occlusion, spatial propagation, mixer category, and room returns. The mod reads the local shot state, derives bounded control values, and adds or reshapes only the parts required to make caliber, room, stance, hearing overload, and active protection behave coherently.

The model is physically motivated but not an absolute simulation. EFT's normalized recordings do not carry calibrated pascals or dB SPL, so the model preserves relative ordering and time behavior in digital audio units. It must not be used to estimate safe exposure to real gunfire or the protection delivered by real equipment.

## Scope and invariants

Grenade events also feed a separate local hearing-exposure model and the world-audio headset path. The shot model runs only when `WeaponSoundPlayer.FireBullet` belongs to the local player in first person. It snapshots cartridge, projectile mass, muzzle velocity, weapon class, muzzle-device loudness, suppressor state, indoor/outdoor state, shoulder stance, and equipped headset before any audio-thread work.

The following constraints apply:

- no projectile, recoil, animation, ammunition, damage, health, or AI-hearing behavior is changed;
- observed-player gunshot sources and bullet fly-by sources are not modified; their playback is subject to the local headset and hearing response;
- EFT's original gunshot clips are never rewritten;
- a missed or cold derived layer is never replayed late;
- unknown audio routes and unsupported headsets fail open to stock behavior;
- disabling the mod clears its hearing state and bypasses its processing.

## Signal flow

```text
EFT weapon event
  -> original EFT dry and room paths
  -> optional mod-derived low-end layer
  -> EFT gun mixer and room returns
  -> Vanilla or Realistic active-headset route
  -> temporary hearing response
  -> player output
```

The low-end layer is derived from the same first-person recording and retains its gun mixer category. The room path tunes EFT's existing pooled reverb sources. The hearing response is applied at the local listener because it represents the player's temporary perception rather than another sound in the world.

## Gunshot character

### Original-band method

`OriginalBand` derives a short parallel band from the live EFT report. A 38 Hz high-pass is subtracted from a caliber-dependent low-pass between 170 and 310 Hz, producing a bounded low-frequency body without an oscillator or noise generator. Each real automatic shot gets its own 120 ms envelope, so a burst accumulates overlapping transients without restarting one shared envelope.

This method keeps the exact source timing and is also the timely fallback while a pitch-copy cache is cold.

### Pitched-copy method

`PitchedCopy` plays a synchronized copy of EFT's selected report through the same gun mixer group. The playback ratio is

```text
r = 2^(-s / 12)
```

where `s` is the pitch reduction in semitones. The copy then passes through matching second-order high-pass and low-pass filters with Q = 1, a user gain stage, an equal-power fade, cartridge weighting, normalization, and optional inherited occlusion.

The copy is additive: the original EFT report is still present. It does not create a new weapon event or re-enter gameplay code.

### Automatic fire

EFT automatic bodies are authored as looping banks rather than one newly queued clip per bullet. The mod therefore treats gameplay shot calls as the authoritative sequence and uses the bank's `BeatLn` only as its audio boundary.

During weapon warmup, the production `CachedReport` route silently decodes one authored body interval and the corresponding recorded tail. `FullReportPerShot` schedules that composite report once for each real bullet. A 2 ms seam prevents a discontinuity while preserving the body/tail boundary and authored relative volume. When a complete report was scheduled, the later EFT trigger-release tail is not copied a second time.

If the cache is not ready, the shot remains timely and uses `OriginalBand`; missed copies are never replayed. `TailAfterBurst` is an advanced alternative that uses the short body, optional bounded synthetic decay, and one processed recorded tail after trigger release. `BuiltInDSP` is a diagnostic comparison route and is not the production normalized path.

## Low-end level model

Normalization matches recording levels before cartridge weighting. It uses digital RMS measurements rather than compression, limiting, or physical SPL calibration.

The analysis uses the same pitch and band-pass implementation as playback. It measures stereo energy independently, so opposite channel polarity cannot cancel the estimate. Leading silence is skipped with 2 ms of retained pre-roll.

Two regions are evaluated:

- body: the first 180 ms of pitched output;
- representative decay: 220 ms beginning at 800 ms of pitched output, or after the authored automatic body when that boundary is later.

The fixed digital body targets are:

```text
outdoor unsuppressed = 0.100 RMS
indoor unsuppressed  = outdoor + 3 dB
suppressed           = matching environment - 12 dB
decay target         = 20% of the body target
```

For measured RMS `m`, target `t`, and normalization control `q`, the body correction is

```text
requested_dB = clamp(20 log10(t / m), -12 dB, +12 dB)
gain         = 10^(requested_dB * clamp(q, 0, 150) / 100 / 20)
```

For nonautomatic pistol copies, normalization measures the band below 180 Hz and applies a scalar gain to the existing copy, without adding a playback crossover. The lower correction bound is -24 dB for this route; the upper bound is +12 dB. Other routes use the ±12 dB bounds above.

At 100%, the ordinary correction is limited to ±12 dB. Values above 100% scale that bounded request, reaching at most ±18 dB at 150%. Signals below the minimum measurable RMS are never boosted.

The decay is conservative. A quiet decay may recover by at most 6 dB relative to an attenuated body and never above its native level. A strong decay keeps the body correction. If the body already requires gain above unity, the decay receives no additional recovery. Playback holds the body coefficient through the first 180 ms or the actual automatic body boundary, whichever is later, then moves to the decay coefficient over 30 ms.

Normalization excludes user copy gain, fade, suppressor attenuation, headset processing, and occlusion.

## Cartridge ordering and direct impact

The exposure model assigns baseline severity by cartridge family:

| Family | Baseline severity |
| --- | ---: |
| Rimfire and micro | 0.35 |
| Service pistol and PDW | 0.58 |
| Intermediate rifle | 0.86 |
| Shotgun | 1.02 |
| Full-power rifle | 1.12 |
| Heavy and exceptional | 1.55 |
| Unknown fallback | 0.75 |

The pitched layer applies a separate cartridge-family contrast after normalization. At 100%, an intermediate rifle cartridge is about 3 dB above 9×19 in the added layer; 200% gives about 6 dB; 300% gives about 9 dB. This weighting does not turn the entire EFT gunshot up by those amounts.

`Gunshot Impact` scales the profile's direct lift and low-end contribution. Direct lift is bounded at 6.5 dB before conversion to linear gain. Suppressors retain only 38% of that lift. The low-end addition uses bounded mixing at its own insert, but this is not a guarantee that the complete EFT output chain cannot overload.

## Hearing exposure

### Per-shot severity

Projectile kinetic energy is used only as a bounded correction:

```text
E_joule = 0.5 * mass_kg * velocity^2
energyCorrection = clamp(log10(E_joule / 1800) * 0.18, -0.25, +0.25)
```

Kinetic energy is an ordering hint, not muzzle-blast energy: propellant, barrel geometry, action, and muzzle device also matter. EFT's summed `Mod.Loudness` contributes a bounded multiplier, and a suppressor applies a residual factor rather than deleting exposure.

Without a headset, the per-shot severity is

```text
severity = max(0, caliberBaseline + energyCorrection)
         * muzzleDeviceMultiplier
         * suppressorMultiplier
         * indoorMultiplier
         * presetExposureScale
```

The `Balanced` profile uses an indoor multiplier of 1.35 and an exposure scale of 0.78. The accumulator is capped at 4.0.

### Left and right ears

Long guns give the model-designated exposed ear a larger share of the dose. The side mirrors with shoulder stance; compact weapons use a smaller difference, and pistols remain nearly symmetrical. Each ear receives

```text
dose_exposed  = severity * (1 + asymmetry)
dose_shielded = severity * (1 - asymmetry)
```

Long-gun asymmetry is bounded at 0.25 even if the F12 scale is increased.

### Accumulation and recovery

Every local shot adds to both ear accumulators. Recovery is exponential, with the time constant increasing as the accumulator fills:

```text
n   = dose / maximumDose
tau = fastRecovery + (slowRecovery - fastRecovery) * n
dose(t + dt) = dose(t) * exp(-dt / tau)
```

This makes isolated shots recover quickly while sustained fire lingers. The durations are gameplay time scales, not physiological recovery times.

### Temporary hearing loss and tinnitus

Temporary hearing loss maps normalized dose through a response curve, then applies independent left/right attenuation and low-pass cutoff. Tinnitus uses a separate threshold, strength, and duration mapping. F12 provides independent intensity and duration controls for hearing loss and ringing. Both begin from shot-derived exposure, but changing one effect does not change the other effect's intensity or recovery scale.

At 0%, either effect is fully bypassed. Disabling both effects or disabling the mod clears accumulated state rather than preserving a hidden dose for later.

## Indoor response

Indoor state comes from EFT's binary environment flag. It increases exposure and tunes EFT's existing Meta XR early-reflection and reverb-only sources. The room-return model does not estimate room geometry, materials, or RT60, and it does not replace the room with generated noise or a synthetic reverberator.

`Indoor Emphasis` scales both the audible room contribution and the additional indoor hearing dose.

## Gunshot contrast

`Gunshot Contrast` attenuates approved non-gun source groups by

```text
gain = 10^(-contrast_dB / 20)
```

Gun dry and wet routes, the mod's low-end layer, UI, music, and voice chat are excluded. Unknown or shared routes are left unchanged. This preserves the gunshot level and creates contrast by lowering competing world sound, which also means useful cues such as footsteps and character speech become quieter.

## Active-headset model

### Two physical paths

`Realistic` models two simultaneous paths from the outside field to each ear:

```text
p_pass(t) = H_passive(f) * p_world(t)
p_elec(t) = H_speaker(f) * [g(t) * H_mic(f) * p_world(t)]
p_ear(t)  = p_pass(t) + p_elec(t)
```

The passive path is always present and frequency dependent. The electronic path represents external microphones, band limitation, level-dependent gain, and the internal speakers. Loud sound reduces the shared stereo-linked electronic gain; attack, hold, and recovery continue across every source and every bullet instead of resetting per shot.

The electronic model uses +6 dB quiet gain, a -24 dBFS threshold, 6 dB knee, 10:1 ratio, 0.5 ms attack, 10 ms hold, 150 ms release, a 0.5 linear output ceiling, and a 100-10,000 Hz microphone band. These are engineering defaults, not measured specifications of every represented headset. In particular, dBFS is not dB SPL.

### Passive profiles and evidence

Passive attenuation is interpolated in log-frequency from per-profile band data and fitted to stable minimum-phase filters. Evidence is stored with each profile:

- ComTac II, V, and VI use selected published attenuation tables;
- ComTac IV and TEP-300 use explicitly selected tip configurations;
- Tactical Sport, Razor Digital, Sordin, and CENS use identified family, revision, or configuration transfers;
- products without applicable measurements retain proposed curves; ordinary M32 does not inherit M32 Plus specifications;
- modded Ops-Core AMP variants use the FAST RAC profile as a fallback.

The [headset reference](docs/reference/headphones/README.md) inventories 29 installed-game items across 15 physical models/families, including modded items. The [runtime calibration](docs/reference/headphones/runtime-calibration-0.20.0.md) records the selected source tables. Full mean-attenuation points drive the filters; SD and APV remain separate evidence and are not subtracted again. NRR/SNR, speaker bandwidth, and advertised output levels do not supply missing compressor parameters.

The `Headset Fit` F12 setting adjusts the separate gameplay exposure estimate derived from the equipped EFT headset threshold. It does not rewrite the measured or surrogate frequency curve used by the `Realistic` audio path.

The model covers world audio, including spatial speech and VOIP. UI, music, inventory-interface sounds, and nonspatial chat are outside the external acoustic field. If the headset ID is unknown, the mixer route is incomplete, or the native DSP is unavailable, the entire headset route returns to `Vanilla`.

## Headset inspection

Inspection reads the same client-side profile as the Realistic DSP; no server item-template changes are required. Realistic displays passive attenuation as three arithmetic averages of the available reference points: low (63 Hz to below 500 Hz), mid (500 Hz to below 2 kHz), and high (2–8 kHz), plus compressor release and quiet gain. These averages simplify the interface only; audio calculations retain the full curve.

Vanilla displays only its compressor release and gain. Characteristic labels do not carry a mode prefix. An asterisk marks a family-surrogate or proposed value, not every value calculated from a documented curve. Unknown headset profiles retain the stock route.

## Explosion exposure and recovery

Grenade playback enters the headset world-audio path. Its hearing after-effect is separate from shot accumulation. Let `d` be distance in metres, `R` the outdoor close-blast radius, `M` the indoor radius multiplier, and `P` the passive low-band protection estimate in dB:

```text
outdoor exposure = (R / max(0.25, d))^2 * 10^(-P / 10)
indoor exposure  = (R * M / max(0.25, d)) * 10^(-P / 10)
severity         = clamp(exposure, 0, 1) * barrierTransmission
```

The indoor branch requires both the explosion source and the listener to be indoors. A grenade inside a building does not grant the indoor multiplier to a player outside. These are relative gameplay exposure laws, not calibrated blast-pressure predictions; the indoor branch falls as inverse distance.

For a potentially relevant blast, three rays run to the player's head from a vertical equilateral triangle facing the player horizontally. Its side is 2 m, one vertex points upward, and its lower edge is 10 cm above the grenade position. A barrier counts only when all three rays intersect the same identified surface. Each common concrete surface multiplies exposure by 0.5; each other common surface by 0.75. Repeated hits on the same surface are deduplicated, and player/body equipment colliders are excluded. This approximation modifies hearing exposure, not EFT's original sound propagation, and does not resolve wall thickness, diffraction, or reflection paths.

Exposure begins after `d / 340 + 0.12` seconds and ramps in over 0.1 seconds, allowing the initial explosion transient to precede the hearing loss. Ordinary events recover linearly; their configured hearing-loss and ringing durations scale with the square root of severity. At maximum severity, the close-blast duration extends the enabled effects: after onset, the first half holds a plateau and the second half recovers linearly. Overlapping blast responses use their maximum rather than an unbounded sum.

Explosion hearing loss and ringing each have independent strength and duration settings. The close-blast duration is a separate control. Default values are 45 s hearing loss, 90 s ringing, and 180 s close-blast recovery; the outdoor radius is 5 m and the indoor multiplier is 3. Protection and intervening barriers can prevent maximum severity even inside the nominal close-blast radius.

## F12 organization and defaults

Sections are ordered `General`, `Gunshots`, `Explosions`, then `Low-level & debug`. Low-level controls are advanced entries, hidden until the Configuration Manager's Advanced toggle is enabled. General contains the master switch, preset, headset mode, and fit. Shot and explosion effects have their own sections.

Default settings are Balanced, Realistic, Tight fit; shot impact 160%, contrast 8 dB, indoor emphasis 100%, hearing-loss/ringing intensity and duration 100%, and ear difference 140%. The added layer defaults to PitchedCopy / CachedReport / FullReportPerShot, 12 semitones down, normalization 100%, cartridge contrast 200%, 10.00001–2000 Hz filtering, 50% fade, 30 ms fallback decay, +20 dB copy gain, inherited occlusion, and a 500.4695 Hz fully occluded cutoff. Per-shot logging is enabled by default. Existing saved configuration values take precedence over these defaults.

## Limitations and interpretation

- Digital RMS targets are mix calibration, not sound pressure.
- The cartridge model cannot infer propellant mass, muzzle pressure, barrel length, or exact directivity from every EFT template.
- Binary indoor/outdoor state cannot describe room size or materials.
- Minimum-phase filters approximate magnitude-only headset tables; they do not reproduce measured impulse phase.
- Electronic gain and timing values without product-specific measurements are engineering approximations.
- Local bounded stages do not prove final device headroom after EFT's complete nonlinear mixer and the user's operating-system audio chain.
- Tests and offline renders establish deterministic implementation behavior. Audible balance, transitions, and comfort require controlled in-raid listening with the installed build.


## Native loading and global controls (1.0.0)

The packaged BepInEx preloader loads the DSP before raid mixer assets and registers its audio definitions through an internal UnityPlayer function. Both binaries are SHA-256 guarded. Registration uses memory only; game metadata is untouched. Unsupported builds fail closed. Instance creation and advancing DSP callbacks are checked separately from DLL presence.

Menu and BetterAudio retain separate mixer objects. Master, InGame, UI, Chat, Music and Hideout volume controls are synchronized in both directions, preserving raid fades and subsequent settings changes. Headset profile/category controls are excluded.

The complete package was installed by the user from the 0.23.3 candidate archive after removing the legacy installation and restoring original game metadata. All three installed DLLs matched the archive. Normal launcher/raid logs confirmed BepInEx registration, one native DSP instance, ComTac V Realistic activation without fallback, return to Vanilla after headset removal, and restored global world volume. The user reported working sound. Version 1.0.0 promotes this implementation with release version and documentation changes; it does not claim exhaustive acoustic validation of every headset or scene.
