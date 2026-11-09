# Guns Are Loud: Physical and Perceptual Model

## Purpose

Guns Are Loud is a model of first-person acoustic perception, not a replacement sound pack. EFT remains responsible for the weapon recording, shot timing, distance layers, occlusion, spatial propagation, mixer category, and room returns. The mod reads the local shot state, derives bounded control values, and adds or reshapes only the parts required to make caliber, room, stance, hearing overload, and active protection behave coherently.

The model is physically motivated but not an absolute simulation. EFT's normalized recordings do not carry calibrated pascals or dB SPL, so the model preserves relative ordering and time behavior in digital audio units. It must not be used to estimate safe exposure to real gunfire or the protection delivered by real equipment.

## Scope and invariants

Grenade events also feed a separate local hearing-exposure model and the world-audio headset path. The shot model runs only when `WeaponSoundPlayer.FireBullet` belongs to the local player in first person. It snapshots cartridge, projectile mass, muzzle velocity, the distance from the muzzle to the listener, weapon class, muzzle-device loudness, suppressor state, indoor/outdoor state, shoulder stance, and equipped headset before any audio-thread work.

The following constraints apply:

- no projectile, recoil, animation, ammunition, damage, health, or AI-hearing behavior is changed;
- observed-player gunshot sources and bullet fly-by sources are not modified; their playback is subject to the local headset and hearing response;
- EFT's original gunshot clips are never rewritten;
- a missed or cold derived layer is never replayed late;
- unknown audio routes and unsupported headsets fail open to stock behavior;
- disabling the mod clears its hearing state and bypasses its processing; only the output limiter described below remains on the listener, in its safety mode.

## Signal flow

```text
EFT weapon event
  -> original EFT dry and room paths
     (dry path: direct level correction towards the shot's target)
  -> mod-derived low-end layer (pitched copy of the same recording)
  -> EFT gun mixer and room returns
  -> Vanilla or Realistic active-headset route
  -> temporary hearing response
  -> output limiter (safety; or program limiter with Shot Density)
  -> player output

EFT explosion event
  -> borrowed spatial voice (explosion level correction towards the blast's target by distance)
  -> EFT gun mixer -> the same route, hearing response and limiter
```

The direct level correction is one scalar gain on the source that carries the local shot; it moves the recording's level and nothing else. The low-end layer is derived from the same first-person recording and retains its gun mixer category. The room path tunes EFT's existing pooled reverb sources. The hearing response is applied at the local listener because it represents the player's temporary perception rather than another sound in the world.

## Gunshot character

### Pitched copy

The added low end is a copy of EFT's selected report played through the same gun mixer group. The playback ratio is

```text
r = 2^(-s / 12)
```

where `s` is the pitch reduction in semitones. Slowing a recording by `r` lowers every frequency by the same ratio and stretches its time structure by `1 / r`: at the default 12 semitones, a 1.2 s report becomes a 2.4 s copy one octave lower. The copy then passes through matching second-order high-pass and low-pass filters with Q = 1, a user gain stage, an equal-power fade, cartridge weighting, normalization, and optional inherited occlusion.

The copy is additive: the original EFT report is still present. It does not create a new weapon event or re-enter gameplay code. It is never longer than its source recording stretched by `1 / r`, even where EFT reserves a longer playback window for a tail.

Copies play from one shared pool of voices owned by the mod. EFT reissues its pooled sources to unrelated sounds within a fraction of a second, so a copy that must outlive its round, such as a recorded tail, is not tied to the source that triggered it. This is the only low-end method; there is no live band-split approximation.

### Automatic fire

EFT automatic bodies are authored as looping banks rather than one newly queued clip per bullet. The mod therefore treats gameplay shot calls as the authoritative sequence and schedules one copy per real round.

During weapon warmup, the `CachedReport` route silently decodes one authored body interval and the corresponding recorded tail. `FullReportPerShot` schedules that composite report once for each real bullet. A 2 ms seam prevents a discontinuity while preserving the body/tail boundary and authored relative volume. When a complete report was scheduled, the later EFT trigger-release tail is not copied a second time. `TailAfterBurst` uses the short body, an optional bounded synthetic decay, and one processed recorded tail after trigger release. `BuiltInDSP` is a legacy comparison route and is not the production normalized path.

Until warmup has cached a weapon's report, its rounds play without the added layer. A missed or cold round is never replayed later.

#### Round clock

Copies are placed on a rolling clock rather than at the moment the main thread learns of a round, because that moment is quantized by frame time and by the audio buffer. The clock starts on the round's interval `T_beat = BeatLn / pitch`, the beat of the looping recording, with the pitch factor bounded to 0.965-1.045. Rounds arrive at the weapon's own fire rate, and the two can differ by a fifth or more: one rifle in testing reported a 97.2 ms beat and fired every 77.2 ms.

The clock therefore observes the burst. After `n` rounds observed at audio times `t_0 ... t_n`, the measured interval is

```text
T_obs = (t_n - t_0) / n
```

Averaging over the burst divides a single observation's timing error by `n`. With `b` the audio buffer duration, the clock switches to `T_obs` once `n >= 8` and

```text
|T_obs - T_beat| > max(0.01 * T_beat, 2 * b / n)
```

so a twenty-percent difference is followed within eight rounds and a one-percent difference within a few dozen. A gap of more than three beats starts a new measurement.

Each copy is then checked against the present `now` with a minimum scheduling lead of 4 ms:

```text
late budget  = min(b, 0.25 * T) * lateToleranceScale
early budget = min(b + 0.25 * T, 0.5 * T)
```

A copy later than the late budget is skipped, and the clock is moved to the present so the rounds held back by a stalled frame are not released together. A copy further ahead of its round than the early budget is brought back to the edge of that budget, and the clock with it; the round is real, so this copy still plays. Late copies that would land within half an interval of the previous start after clamping are dropped as a stack. At 1024 samples and 48 kHz, `b` is 21.3 ms.

#### Overlap budget

A pitched full report lasts several seconds, so at 600-900 rounds per minute every round would otherwise leave dozens of copies sounding. When a later round's copy starts, each earlier copy of the burst is asked to finish by

```text
release_time = start + overlapShots * T_beat
fade         = overlapShots * T_beat * fadePortion
```

The default is 6 rounds. A single shot and the last round of a burst have no successor and keep their full recorded tail.

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

For measured RMS `m`, target `t`, and the `Low-End Normalization` control `q` in dB, the body correction is

```text
requested_dB = clamp(20 log10(t / m), -12 dB, +12 dB)
gain         = 10^(requested_dB * clamp(q, 0, 18) / 12 / 20)
```

For nonautomatic pistol copies, normalization measures the band below 180 Hz and applies a scalar gain to the existing copy, without adding a playback crossover. The lower correction bound is -24 dB for this route; the upper bound is +12 dB. Other routes use the ±12 dB bounds above.

At the calibrated 12 dB, the ordinary correction is limited to ±12 dB. Larger values scale that bounded request, reaching at most ±18 dB at 18 dB; 0 dB preserves the recorded differences. Signals below the minimum measurable RMS are never boosted.

The decay is conservative. A quiet decay may recover by at most 6 dB relative to an attenuated body and never above its native level. A strong decay keeps the body correction. If the body already requires gain above unity, the decay receives no additional recovery. Playback holds the body coefficient through the first 180 ms or the actual automatic body boundary, whichever is later, then moves to the decay coefficient over 30 ms.

Normalization excludes user copy gain, fade, suppressor attenuation, headset processing, and occlusion.

### Cartridge weight and copy level

After normalization the copy is weighted by cartridge, a straight line in the energy term through 0 dB at the reference cartridge:

```text
w(L_E) = clamp(k · L_E, −8 dB, +3.5 dB),   k = ContrastDb / 5.1 dB
```

5.1 dB is the distance from the median 9×19 round (`L_E = −6.2`) to the median 5.45×39 round (`L_E = −1.1`), so `Cartridge Contrast, dB` is exactly the level of an intermediate rifle cartridge above 9×19 in the added layer: 3 dB by default, 0 dB to remove the weighting, 9 dB at most. At the default, 9×19 is weighted −3.6 dB, 5.45×39 −0.6 dB, 7.62×51 +1.6 dB, and .338 Lapua Magnum and heavier sit at or near the +3.5 dB bound. The direct shot already carries the cartridge through its level correction, so this weighting adds spectral contrast in the low end on top of it; it does not turn the entire EFT gunshot up by those amounts.

The copy follows its donor shot. Its level is one product:

```text
copy = sourceVolume · g_d · c · N · 10^(w / 20) · bodyGain · 1.25 · userGain · headsetDamping · occlusion
```

`sourceVolume` is the volume the engine gives the donor shot, `g_d` the shot's direct level correction (linear), `c` the copy's share of the shot's peak budget (1 unless the budget binds, see below), `N` the low-end normalization above and `userGain` the `Copy Gain`. Nothing else is taken from the playing engine. A source volume stops at 1, so whatever the product holds above 1 is carried in the copy's own gain stage instead of being lost; below 1 the copy's gain stage stays at unity.

`Gunshot Impact` scales `bodyGain`:

```text
lift     = (base + indoor) · GunshotImpact
bodyGain = clamp(presetBodyGain · (0.5 + clamp(lift / 6.5 dB, 0, 1)), 0, 0.5)
```

The body gain does not depend on the cartridge; the cartridge enters the layer once, through `w`. The low-end addition uses bounded mixing at its own insert, but this is not a guarantee that the complete EFT output chain cannot overload.

## Replacement sound engines

Sound Engine Backport (SEB) 1.0.0 replaces the weapon banks and, with its live engine, plays each round of a non-legacy bank itself as a whole recording instead of through EFT's looping queue. The model keeps the same targets and asks the engine, not the recording, how a bank is played.

- **Body model per close clip.** A bank the live engine plays is a whole shot per round. On EFT's queue path a clip loops only where SEB itself loops it; every other clip is a whole shot. Whole-shot rounds get one added copy each, faded by `Automatic Report Overlap` when the next round follows.
- **Body length of a whole automatic shot.** The decay boundary is measured from the envelope: 10 ms RMS blocks from the onset, the first block after the peak that is 20 dB below it, bounded to 0.09–0.30 s of native time. Pistols, bolt actions and shotguns keep the 180 ms window.
- **Donor level.** Whatever volume SEB gives a shot, its mix scaling included, is part of `M`, the level the shot would play at uncorrected, and the direct level normalization takes it to the same target as a stock recording of the same cartridge. The copy follows the shot's own direct correction and the volume the engine plays it at; nothing else is read from the engine. The only SEB value read is the player's Gunshots slider, from SEB's public configuration (`GunshotsVolumeDb`), as `G_user` in the target: raising the slider raises the shot and its copy together, and the correction keeps the difference even where the source volume itself stops at 1, as far as the peak budget allows. At `Direct Level Normalization` 0 dB the shot and its copy stop growing together where the source volume reaches 1.
- **Variants that are still warming.** A bank of whole shots decodes in real time, so a round can play before its own variant is measured. Such a variant uses the geometric mean of the measured variants of the same bank and environment, for the direct level and for the layer's body, bass and decay levels, and their mean body length. With no measured variant yet, the shot and its copy play uncorrected. Variants measured in an earlier session come from the measurement cache at once.

In a headset, bursts of SEB rounds measure about 2–3 dB above the stock banks. Each SEB round carries its own room tail, and the overlapping tails sit below the headset compressor's threshold, so the headset takes less off them than off a stock looped burst. This is a property of the recordings and is not compensated.

## Shot level index

Every local shot gets one level index `L` in dB. It sets the loudness of the direct sound, the hearing dose, the headset caliber penalty and the cartridge weight of the added low end, so a cartridge sits at the same place on all of them.

```text
E   = ½ · m · v²                      payload mass m in kg, muzzle velocity v in m/s
L_E = 10 · log10(E / 1800 J)          energy term
L_d = 20 · log10(1 m / d)             distance term, d clamped to 0.3–2 m
L_m = k · ΣLoudness                   muzzle devices: the game's Loudness stat of every device on the weapon, summed
L   = L_E + L_d + L_m
```

- **Velocity.** `v` is the muzzle velocity of the fired weapon as the game computes it for that weapon and round, so parts that change muzzle velocity move `E` with it.
- **Payload mass.** For a single projectile, `m` is the bullet. For a shot shell, `m` is the median bullet mass of the single-projectile rounds of the same caliber in the game's item database (the mean of the two middle values for an even count), because the game's pellet masses and counts do not describe the charge. A median is used so that one outlying round, such as an unusually heavy slug added by another mod, does not move the whole caliber. A caliber without single-projectile rounds keeps pellet mass times pellet count.
- **Reference.** 1800 J is an intermediate rifle cartridge: 5.45×39, 5.56×45 and 7.62×39 rounds sit at 1.7–2.2 kJ, so their `L_E` is close to 0 dB.
- **Distance.** `d` is measured on every shot from the weapon's muzzle to the listener. Spherical spreading makes the blast 6 dB louder per halving of that distance. A held muzzle is never closer than a compact pistol held up to the face (0.3 m), and beyond 2 m the listener is not where the shooter is (a free camera or spectator view), so the term is held at those bounds.
- **Fallback.** A round without mass or velocity gets the single fallback `L_E = 0`, the reference cartridge.
- **Muzzle devices.** The game rates every muzzle device with one number, `Loudness`: suppressors from −12 (a short moderator) to −39 (an integral suppressor), with most cans at −20 to −30; flash hiders 0; compensators and brakes +5 to +40. The game itself uses the number only for what the AI hears; the player's own suppressed shot plays a separate recording that is on average only 2.5 dB quieter than the unsuppressed one, whatever the device. Here the sum of the devices' numbers enters the level, so a big can and a moderator differ and a brake raises the shot. `k` is `Muzzle Device Influence, dB` (Gunshots, 0.4 by default, 0–1) divided by the 0.5 dB per dB slope of the target below: the setting is the audible change per point, the index carries twice that. At the default a −25 suppressor stands 20 dB lower on the scale, which the target renders as 10 dB at the ear, close to the 20–35 dB such a device takes off the muzzle blast and well below the 6 dB a fixed suppressed flag used to give every device. A brake at +10 adds 8 dB to the index, 4 dB at the ear until the top of the scale. The hearing dose below follows the same level, so a suppressed pistol-caliber or intermediate shot leaves no dose and a brake adds to it. 0 ignores the devices.

A cartridge added by another mod therefore needs no table entry: it is placed by what it fires. A .700 Nitro Express lands next to the 12.7 mm rounds, a mod's pistol round next to the game's pistols.

Typical values, for the median round of each caliber at its nominal muzzle velocity and an estimated muzzle distance for its weapon class (in a raid the game supplies the actual `v` and `d` of every shot):

| Cartridge | `L_E`, dB | typical `d`, m | `L`, dB |
| --- | ---: | ---: | ---: |
| 26×75 flare | −14.5 | 0.55 | −10.1 |
| 9×18PM | −8.1 | 0.55 | −2.9 |
| 9×19 | −6.2 | 0.55 | −1.0 |
| 40×46 grenade | −4.3 | 0.80 | −2.4 |
| 5.45×39 | −1.1 | 0.90 | −0.2 |
| 5.56×45 | −0.2 | 0.90 | +0.7 |
| 12 ga | +1.0 | 1.00 | +1.0 |
| 7.62×39 | +0.9 | 0.90 | +1.8 |
| 7.62×51 | +2.7 | 0.90 | +3.6 |
| .338 Lapua Magnum | +5.6 | 1.10 | +4.8 |
| .700 Nitro Express | +8.2 | 1.00 | +8.2 |
| 12.7×99 (.50 BMG) | +9.8 | 1.10 | +9.0 |

### Why muzzle energy and distance

Ideal blast scaling describes a muzzle blast by a length proportional to the cube root of the propellant gas energy, and peak overpressure as a function of distance divided by that length. In the far field pressure falls as `1/r`, which gives 0.67 dB of peak level per dB of energy; closer to the muzzle the pressure falls faster and the slope rises to about 1.0–1.3 (Fansler 1998). Bullet kinetic energy stands in for the gas energy: within a class of firearms the ballistic efficiency is similar, so the two differ by a roughly constant factor.

Published peak levels at the shooter's ear agree with that range. A fit of peak level against `10·log10(E)` gives about 1.3 dB per dB for long guns and about 0.7 for pistols, with a residual scatter of 3–4 dB; the same barrel fired with subsonic and supersonic loads gives up to about 1.5 dB per dB. The model uses a slope of exactly 1, inside both the theoretical and the measured range.

At equal energy, pistols measure about 10 dB louder at the shooter's ear than long guns. Part of that is geometry: a pistol's muzzle is about 0.55 m from the ear, a rifle's 0.9–1.1 m, and spherical spreading alone accounts for about 5 dB. The rest comes from the short barrel venting more unburnt propellant, which the game does not describe. The model keeps the part it can observe, the distance, and leaves the rest out, so pistols sit close to intermediate rifles and below them.

### Sources

- Fansler K. S. (1998). *Description of Muzzle Blast by Modified Ideal Scaling Models.* Shock and Vibration 5(1). The scaling of muzzle-blast level with energy and distance, and the theoretical range of the slope.
- Meinke D. K. et al. (2014). *Auditory risk estimates for youth target shooting.* International Journal of Audiology 53 Suppl 2. Rifles, shotguns and pistols at the shooter's ear: the slope of level against energy, the pistol-versus-long-gun offset, and the 12 ga anchor (155–161.5 dB for 2¾–3 inch shells), which places the shell next to 7.62×39 as its slug does.
- Murphy W. J., Campbell A. R., Flamme G. A. et al. (2018). *Developing a method to assess noise reduction of firearm suppressors for small-caliber weapons.* Proceedings of Meetings on Acoustics 33:040004. Thirteen firearms with two loads each, microphones 15 cm from both ears: the pistol-versus-long-gun offset at equal energy, and the .338 Lapua Magnum anchor (163.3 / 161.8 dB at the left / right ear of a bolt-action rifle).
- Murphy W. J., Flamme G. A., Zechmann E. L. et al. (2018). *The reduction of gunshot noise and auditory risk through the use of firearm suppressors and low-velocity ammunition.* International Journal of Audiology 57(sup1). The same .223 and .308 rifles with subsonic and supersonic ammunition: the slope within one barrel.
- Flamme G. A. et al. (2011). *Auditory risk to unprotected bystanders exposed to firearm noise.* Journal of the American Academy of Audiology 22:93–103. Long guns and shotguns 1 m from the shooter, including 12 ga loads.
- Pääkkönen R. et al. (1998). *Noise attenuation of communication hearing protectors against impulses from assault rifle.* Military Medicine. An RK 62 (7.62×39) at the entrance of the shooter's ear, 156 dB C-weighted peak: a reference for the intermediate cartridge.
- Institute of Medicine (2006). *Noise and Military Service: Implications for Hearing Loss and Tinnitus.* Peaks at the shooter's position for service weapons (M9 and M16A2 both 157 dB). No measurement of a shoulder-fired .50 BMG at the ear was found, so the top of the scale is the same energy law extrapolated.

### Not modelled

- Barrel length as such. It enters only through the muzzle velocity the game reports and the muzzle distance.
- Muzzle devices beyond the game's own loudness value (`Mod.Loudness`). The number is the game's rating, not a measurement: two cans at the same number sound the same here.
- Absolute dB SPL. The index is relative; published pascals are not converted to digital levels.
- What room reflections add to the first 50 ms. The direct level is measured from the recording and does not depend on the room, apart from the fixed room term of the target.

## Direct level normalization

EFT's recordings, and those of a replacement sound pack, are mastered to their own levels: uncorrected, a pistol can play louder than an assault rifle, and two 12 ga shotguns can sit several dB apart. The recordings also differ in density. A pistol or shotgun recording is often limited to within a few dB of full scale for its whole attack, while an assault rifle recording is a short crack followed by a much quieter room, 7–11 dB below its peak in the first 50 ms. At equal peak the dense recording is the louder one, and no gain can change that; only a change of the recording's shape can, which is what Shot Density does (below).

The player's own shot is therefore handled as one **event**: the recording, its release tail, the room EFT adds through its wet sources and the mod's low-end copy. One gain moves all of them, so their proportions are not changed by the correction. The event's attack level follows the shot level index, and the event's peak before the output limiter never exceeds one budget. Where the level and the budget disagree, the budget wins and the copy gives way before the recording.

### Measure

Each recording is measured once:

- the **attack level**, `−0.691 + 10·log10(sum of the mean squares of the K-weighted channels)` over the first 50 ms of one shot, following ITU-R BS.1770 with the K-weighting computed for the clip's own sample rate. A mono recording counts as both channels; a shot shorter than the window is measured over the whole window, with silence after its end. The window covers the muzzle blast and the first reflections; the room a recording carries past that is not part of its level, because the room is modelled separately, by the room term of the target and by EFT's wet path;
- the **peak**, the largest per-frame `max(|L|, |R|)` of the event, in dBFS.

Both are measured on one **shot event**. A recording that holds a whole shot is its own event. EFT's automatic bodies are looping banks; for them one shot is one beat of the loop followed by the recorded tail at its authored relative volume, composed the same way as the cached report of the added layer.

The copy is measured against the same recording, in the layer's analysis band: the recording played at the layer's pitch through its band-pass with the layer's 2 ms attack, starting with the shot. Its peak and its attack level are taken at a ratio of 1 to the recording, and the peak of `recording + r · copy`, aligned at the shot, at the ratios 0.25, 0.5, 0.75, 1, 1.5, 2, 3 and 4. A copy peaks in its body, within the first third of a second, while the recording is still near full scale, so the two peaks add almost linearly; the measured sums say exactly how much. Changing the pitch or a cutoff measures the copy again.

For Shot Density, the same measurement also records what the program limiter takes off the event's attack level: the recording alone and the recording with the copy at each of those ratios are scaled so that the event's peak stands 0, 2, 4, 6, 9 and 12 dB over the output ceiling, passed through the program limiter exactly as the listener runs it, and the attack level of what comes out is compared with the attack level of what went in. The result is a table of losses, in dB, never a gain.

### Target

`Direct Level Target` selects the target `T`, in dB on the scale of the attack level, for the event as it plays:

```text
Energy:    T = −4.5 − 0.5 · max(0, 2.5 − L) − 3·outdoor + G_user
Baseline:  T = min(−4.5, −4.5 + 20·log10(β) + 0.5 · L_m) + G_user
```

- `−4.5` dB is the top of the scale: the attack level the sparsest heavy recordings of the game (7.62×39 and 7.62×51 assault rifles indoors, measured over the stock banks and those of Sound Engine Backport) reach at the peak budget with the default copy headroom. It is the loud end of the medium. Every round at or above `L = 2.5` (7.62×39 and 7.62×51 at 0.8–1.0 m) targets it; heavier rounds differ through the hearing dose, the headset penalty and the cartridge weight of the added low end, not through the level of the recording.
- `0.5` dB per dB of level index below the top is the smallest gap that keeps the order audible: 5.45×39 sits about 1.3 dB under 7.62×39, 9×18PM about 2.3 dB under it.
- `outdoor` is 0 or 1. A shot outdoors targets 3 dB less than the same shot indoors. A suppressor has no term of its own: it is in `L` through `L_m`, so a −25 can takes the target 10 dB down at the default influence and a moderator at −12 about 5 dB; in Baseline the devices move the class level at the same slope, never above the top.
- `G_user` is the player's own gunshot trim in a replacement sound engine, 0 dB with the stock engine. It stays on top of the target, so the correction does not undo it.
- `Baseline` uses one fixed level per cartridge class: `β` = 0.7 for pistols, 0.8 for intermediate cartridges, 0.9 for full-power rifles and shotgun shells, 1.0 for heavy rounds and launchers. A caliber the class table does not list is placed by muzzle energy: below 900 J pistol, below 2.7 kJ intermediate, below 8 kJ rifle, otherwise heavy; a round without a known energy counts as the reference cartridge. Baseline has no room term.

Typical indoor Energy targets, with `L` from the table above: 9×18PM −6.8 dB, 9×19 −6.3 dB, 5.45×39 −5.9 dB, 5.56×45 −5.4 dB, 7.62×39 −4.9 dB, 7.62×51 and heavier −4.5 dB; outdoors 3 dB less.

### Correction

```text
M        = attack level + 20·log10(source volume)
K_event  = 10·log10(10^(M/10) + 10^((copy attack level + 20·log10(c · r · source volume))/10))
cap      = 20·log10(1.25 · 10^(−h/20) / (source volume · peak))
g        = min(T − K_event, cap)
```

- `M` is the level the recording would play at uncorrected: its attack level plus the volume the engine gives the source for this shot before occlusion, which includes the bank volume and anything the engine adds.
- `r` is the copy's level relative to the recording before the correction: its low-end normalization times the layer chain (body gain, cartridge weight, `Copy Gain`, headset damping). `c` is the copy's **share**, 1 unless the budget binds.
- `1.25` is the **peak budget**: the peak of the event at the listener before the output limiter, which the limiter then takes down by at most about 2 dB. `h` is `Copy Headroom, dB` (3 by default): the recording alone never peaks above the budget less this headroom, so there is room for the copy next to a recording at the top of the scale.
- The event is moved to its target unless the cap stops it. Then the peak of `recording + c · r · copy` at that gain is read from the measured sums; if it exceeds the budget, `c` is lowered until it fits, which lowers `K_event` a little and lets the recording rise a little, and the two settle in a few rounds. The recording's level is the last thing to give way.
- `Direct Level Normalization, dB` sets the strength `q = clamp(value / 12 dB, 0, 1)`: the gain in dB and the copy's share in dB are both scaled by `q`. At 12 dB, the default, the correction is complete; at 0 dB the event plays exactly as the engine would play it. A recording is cut by at most 12 dB and lifted by at most 12 dB; in practice the budget bounds every lift well below that, since the close recordings of the game peak near full scale.

### Density

Sparse recordings put most of their first 50 ms into a crack 1–3 ms long and the rest into a room 7–11 dB below it. At the peak budget the crack is what sets the gain, so the body and the room of an assault rifle play well below what a dense recording of a pistol or a shotgun plays at the same target. `Shot Density` (Gunshots, on by default) changes the shape rather than the gain: the event is allowed to stand `D` dB over the peak budget at the input of the output limiter, which runs as a **program limiter** while the switch is on (see below), and the limiter takes the crack down while the body and the room pass. `D` is `Shot Density, dB` (6 by default, 1–9, under `Low-level & debug`).

```text
stage budget = 1.25 · 10^(D/20)
cap          = 20·log10(stage budget · 10^(−h/20) / (source volume · peak))
g            = min(T − K_event − loss(c · r, over), cap)
over         = 20·log10(peak of the event at gain g / 0.999)
```

The target is held on the attack level as it **leaves** the limiter: `loss` is read from the measured table at the copy's effective ratio `c · r` and at how far over the ceiling the event stands at the gain being tried, linearly between the measured points; the gain and the loss settle in a few rounds. The copy's share is cut against the stage budget as before. So a dense recording, whose peak is close to its attack level, reaches its target before it stands over the ceiling and is not changed at all; a sparse one is driven into the limiter until its attack, crack rounded off, body and room up, lands on the same target. The order of the cartridges is the same with the switch on or off; what changes is how much of each shot is crack and how much is body. With the switch off the limiter is a safety and the event never exceeds the budget of 1.25, so the recordings keep the shape they were shipped with.

The gain is one scalar on each channel of the source that carries the shot and on the wet room sources EFT schedules with it, set when the shot starts and held until the source is handed to another sound. The release tail of an automatic burst is filed under the shot event it ends and inherits the body's correction, so its quiet decay is not lifted to an attack level on its own. When EFT blends two clips on one source by distance, one gain is decided on their sum: energies add, peaks add. A variant of a bank that is not measured yet borrows the mean of its measured siblings: the mean level in dB (the geometric mean of their amplitudes) and the mean of their peaks and sums. A recording whose peak is not in yet is taken at full scale, where every close recording of the game peaks. The copy plays at the event's gain times its share.

### Measurement cache

Recordings are measured when the weapon is equipped, on a thread of the mod's own, so a bank of thirty recordings costs the game no frame time. A clip decompressed in memory is read directly; any other clip is decoded by playing it silently in the background. The results are kept in `GunsAreLoud.LevelCache.json`, in the mod's plugin folder next to `GunsAreLoud.Diagnostics.log`:

- per recording, or per stock shot event of a looping bank: the attack level, the peak, and the bank volume it was measured at; for an explosion recording also its 240 ms level;
- the analysis of the added layer: body, bass and decay levels, the body length of a whole-shot recording, the copy's peak and attack level, the peaks of the aligned sums, the program limiter's loss table, and the analysis band (pitch and cutoffs) they were measured in. Layer levels are reused only in the same band; changing the pitch or a cutoff measures them again.

Entries are keyed by the recording's content. A key names the clip, its length, sample rate and channel count and, for a clip read directly, a hash of its decoded samples. A clip decoded by playing it is still decoded at equip time, and the hash of its first 1.25 s is compared with the stored one; a mismatch replaces both measures. A changed sound pack is therefore measured again rather than corrected with old values. New measurements are written together a moment after they arrive, and a damaged or foreign file, or one written by a version that measured differently, is replaced. Deleting the file is safe: recordings are then measured again, and `Reset Level Cache` under `Low-level & debug` does the same from F12, dropping the session's measurements with it so the weapon in hand is decoded and measured again at once. With the file in place, a weapon fired in an earlier session has its direct correction and its full low end from its first shot.

## Explosion level

Explosions are placed on the same scale as the shots, by their distance. The game plays an explosion through a borrowed spatial voice: the bank's recording (a close, a distant or a far one, two of them blended between 10–30 m and 60–200 m) at the bank's volume, a tenth to nine tenths of it spatial by distance, the spatial share attenuated by the voice's rolloff. Beyond about 30 m that attenuation puts a hand grenade 15–20 dB under the player's own pistol shot, while a grenade at that distance is as loud at the ear as a rifle at the shoulder.

`Explosion Level` (Explosions, on by default) holds the explosion to a target of its own:

```text
L_x = 4 + 25 · log10(20 m / max(1 m, d))
T_x = −4.5 − 0.5 · max(0, 2.5 − L_x) − 3·outdoor + G_user
```

- `d` is the distance from the explosion to the listener as the game reports it when it plays the sound.
- A hand grenade (0.1 kg of TNT equivalent: the RGD-5 carries 110 g, the F-1 60 g, the M67 about 0.23 kg) at **20 m** stands at `L_x = 4`, the top of the shot scale: its peak overpressure there is 1.4–2.1 kPa (157–160 dB) by the Kingery–Bulmash and Kinney–Graham blast curves, the level of a 7.62×39 rifle at the shooter's ear, which the scale puts at `L` 1.8–2.5. So a grenade within 20 m targets the top like the heavy rifles, at 30 m it sits with the intermediate rifles, at 40 m with the 9×18 pistol, at 80 m with the flare.
- **25 dB per decade** of distance, 7.5 dB per halving, lies between the far field of a blast (6 dB, pressure as `1/r`) and the intermediate range of a hand grenade (8–9 dB at scaled distances of 10–40 m/kg^⅓).
- `outdoor` and `G_user` are the room term and the engine trim of the shot target. An explosion has no suppressor term; the room is the listener's.

The target is held on the **K-weighted level of the first 240 ms** of the event, not on the 50 ms attack of the shots: a blast is not an impulse, its energy arrives over a quarter of a second (the game's far recordings peak 220–300 ms after their onset), so the attack window would measure its rumble and not its level. The 240 ms level of each explosion recording is measured as the shots' attack is, and kept in the measurement cache beside it (`k240`).

The level the engine is about to play is read from the voice itself: each of its two sources' volume, its spatial blend and its rolloff at `d` (Unity's logarithmic, linear or custom curve, held beyond the maximum distance). The two-dimensional share passes at full level; the spatial share through the rolloff.

```text
M_x  = k240 + 20·log10(volume · ((1 − blend) + blend · rolloff(d)))     per source; two sources add as energies
gain = T_x − M_x, then the peak budget of 1.25 as for a shot, and a lift of at most 24 dB
```

The correction is the shot's solver with no copy and no density: the explosion recordings are dense already (the close ones hold their 240 ms within 2 dB of full scale), so the limiter stays a safety and the plain budget applies. One gain goes on both sources of the voice, in the same gain stage the local shot uses, and comes off when the voice is returned. `Direct Level Normalization, dB` scales the correction as it scales the shots'. The lift bound is twice the shots' because the engine keeps a tenth of a distant blast two-dimensional, so its own level never falls more than about 20 dB; 24 dB covers every distance at which the blast is still above the quietest shot of the scale.

The explosion recordings are measured when a raid starts: every bank the effects table plays through the grenade voices, in both environments and at every distance, decoded as the weapons' recordings are, two at a time in the background, and cached. An explosion whose recording is not measured yet plays as the game plays it; a variant not measured yet borrows the mean of its measured siblings. Occlusion stays the game's: the gain is decided on the unoccluded voice, so a blast behind a wall still arrives as the game attenuates it.

With `Log Every Local Shot`, each explosion writes a line with its distance, `L_x`, target, the engine's level and the gain, and arms a listener probe whose window opens when the sound arrives (`delayFrames`), so `postK0-240` of that probe is the level to compare with the target.

## Hearing exposure


### Per-shot severity

The severity of a shot is a straight line in the shot level index, never below zero:

```text
baseSeverity = max(0, 0.866 + 0.0751 · L)
```

The line runs through three long-gun anchors at an estimated muzzle distance: 5.45×39 at 0.9 m (`L = −0.2`) gives 0.86, 7.62×51 at 0.9 m (`L = +3.6`) gives 1.12, and .50 BMG at 1.1 m (`L = +9.0`) gives 1.55. Everything else follows from the same line: a 9×19 pistol at 0.55 m (`L = −1.0`) gives about 0.79, a 12 ga shell about 0.94, a 40 mm grenade launcher (`L = −2.4`) about 0.69, a .700 Nitro Express about 1.48, a 26×75 flare (`L = −10.1`) about 0.11, and the toy gun 0. The fallback round (`L_E = 0`) is scored as the reference cartridge at its actual distance.

Kinetic energy is a proxy for muzzle-blast energy, not the same quantity: propellant, barrel geometry and action also matter. The muzzle devices are in `L` itself (`L_m`), so a suppressor takes the severity down the same line as a lighter round or a longer barrel: the line reaches zero at `L = −11.5`, about 140 dB at the ear on the scale's anchors, the peak level below which an unprotected ear tolerates impulses. At the default influence a −25 can puts a 9×19 or a 5.45×39 shot below that line, so suppressed fire of those calibers leaves no dose, a −12 moderator on a 5.56 rifle leaves about a fifth of the unsuppressed severity, and a brake adds to it.

Without a headset, the per-shot severity is

```text
severity = baseSeverity
         * indoorMultiplier
         * presetExposureScale
```

The `Balanced` profile uses an indoor multiplier of 1.35 and an exposure scale of 0.78. The accumulator is capped at 4.0.

With an active headset, the direct part and the indoor part of the dose are attenuated separately:

```text
P      = clamp(templateProtection + fitOffset − caliberPenalty, 10 dB, 30 dB)
factor = 10^(−P / 20) + (indoorMultiplier − 1) · 10^(−max(10 dB, P − 6 dB) / 20)
```

`templateProtection` comes from the equipped EFT headset's compressor threshold, `fitOffset` from `Headset Fit`. Reflections reach the ear through a weaker seal, so the indoor part loses 6 dB of protection. `factor` replaces `indoorMultiplier` in the product above.

### Headset caliber penalty

The more energy a round carries, the more of its blast lies at low frequencies, where ear cups attenuate least. The penalty takes protection away by cartridge, from the energy term alone: the charge sets the spectrum, the distance only the level.

```text
caliberPenalty = clamp(C · (L_E + 0.84) / 10.67, 0, C + 2 dB)
```

`C` is `Headset Caliber Penalty, dB` (General, 6 dB by default, 0–10 dB). The line reaches `C` at the median .50 BMG round (`L_E = +9.83`) and crosses zero at `L_E = −0.84`, where the same line through 7.62×51 (`L_E = +2.7`, a third of the .50 BMG penalty) meets the axis. Pistols and 5.45×39 take nothing; at the default, 7.62×39 and 12 ga take about 1 dB, 7.62×51 2 dB, .338 Lapua Magnum 3.6 dB and a .700 Nitro Express 5.1 dB. Rounds above .50 BMG may exceed `C` by at most 2 dB. At 0 every cartridge gets the same protection. The penalty affects the hearing-dose model only, not the Realistic headset filter curve.

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

At 0%, either effect is fully bypassed. The General `Hearing Loss` and `Ringing` switches turn one effect off for both gunfire and explosions without changing the other effect, the gunshots, or headset protection. Turning both effects off, or disabling the mod, clears the accumulated state rather than preserving a hidden dose for later.

## Indoor response

Indoor state comes from EFT's binary environment flag. It increases exposure and tunes EFT's existing Meta XR early-reflection and reverb-only sources. The room-return model does not estimate room geometry, materials, or RT60, and it does not replace the room with generated noise or a synthetic reverberator.

`Indoor Emphasis` scales both the audible room contribution and the additional indoor hearing dose.

## Gunshot contrast

`Gunshot Contrast` attenuates approved non-gun source groups by

```text
gain = 10^(-contrast_dB / 20)
```

Gun dry and wet routes, the mod's low-end layer, UI, music, and voice chat are excluded. Unknown or shared routes are left unchanged. This preserves the gunshot level and creates contrast by lowering competing world sound, which also means useful cues such as footsteps and character speech become quieter.

The attenuation is written to dedicated routes in the mod's mixer, 52 in the current asset, so it costs nothing per sound. Groups without a route receive a per-source fallback filter. Leaving a raid undoes each route and each tracked source independently, so one failed step cannot leave the attenuation in the mixer.

## Output limiter

The listener's final buffer passes through a stereo-linked peak limiter with a soft knee:

```text
knee    = 0.95  (-0.45 dBFS)
ceiling = 0.999 (-0.009 dBFS)

safety mode  (Shot Density off):  attack instantaneous, release 200 ms
program mode (Shot Density on):   lookahead 2 ms, release 10 ms
```

Below the knee the buffer is untouched. Between the knee and the ceiling the gain follows a smooth curve instead of a hard clip, which removes the crackle of a close unprotected indoor shot summed with its added layer. Signals heard through an active headset normally never reach it.

In **safety mode** it is a safety stage, not a loudness model: the direct level normalization keeps the player's own shot, copy included, under a peak budget of 1.25 before this stage, so a single shot is taken down by about 2 dB at most, and the gain is given back over 200 ms, so a whole report is turned down rather than distorted.

In **program mode** the same curve is reached along a 2 ms ramp that lands exactly on the frame that needs it (the audio leaves the stage 2 ms later than it arrived, including the 2 ms of silence when the mode is switched), and the gain is given back within 10 ms, the length of a crack. A shot driven over the ceiling by Shot Density therefore comes out with its crack rounded off and its body and room at full level, and a steady sound under a rifle shot dips by about one to two decibels for a quarter of a second rather than being held down for the whole report. The shot model knows what this stage takes off each recording's attack and plans for it (see Density), so the stage is part of the model while the switch is on.

## Active-headset model

### Two physical paths

`Realistic` models two simultaneous paths from the outside field to each ear:

```text
p_pass(t) = H_passive(f) * p_world(t)
p_elec(t) = H_speaker(f) * [g(t) * H_mic(f) * p_world(t)]
p_ear(t)  = p_pass(t) + p_elec(t)
```

The passive path is always present and frequency dependent. The electronic path represents external microphones, band limitation, level-dependent gain, and the internal speakers. Loud sound reduces the shared stereo-linked electronic gain; attack, hold, and recovery continue across every source and every bullet instead of resetting per shot.

All headsets share a -24 dBFS threshold, 6 dB knee, 10:1 ratio, 10 ms hold and a 0.5 linear output ceiling. These are engineering defaults, not measured specifications; in particular, dBFS is not dB SPL.

### Electronics character

A real hear-through path is not transparent. Its chain is

```text
microphone band -> + preamplifier noise -> voicing (low shelf, presence peak)
  -> level-dependent gain -> output stage (soft saturation under the ceiling) -> converter delay
```

- **Band and voicing.** The outer-cup microphone and the small speaker in a closed cup limit the band and lift the 2–5 kHz presence region, which owners describe as "sharper" or "tinny". The presence peak is fixed at 3.2 kHz with Q 1.2; the second-order low shelf turns at 200 Hz. Every headset class limits the band with Butterworth edges (12 dB per octave) at its microphone corners; first-order edges left a stated 300–7000 Hz range almost unheard. Only an unknown headset keeps the first-order prototype band.
- **Self-noise.** Microphone and preamplifier noise enters before the gain, so it rises with the quiet gain and sinks when loud sound compresses the path. Detection thresholds measured with electronic protectors in quiet rooms are higher than without them and are explained by this noise; in background noise they match ([Thompson, Gallagher and Pryor, JASA 2023](https://doi.org/10.1121/10.0023764)). The level is a listening calibration, not a measurement: classes keep their relative order, and one anchor sets the whole scale so the hiss stays just above threshold in a quiet hideout.
- **Output stage.** At saturation 0 the ceiling is a hard limit. Above 0 the signal is linear up to `ceiling * (1 - saturation)` and bends into the ceiling with a tanh knee, monotonically and without ever reaching it.
- **Delay.** Digital paths add a few milliseconds, applied in whole samples to the electronic path only.

Makers publish almost none of this. Each field is taken, in order of precedence, from:

1. a **published fact** of the device, never overridden: the Sordin Supreme Pro-X microphone range 100 Hz–10 kHz, the GSSh-01 working range 300–7000 Hz, and the Walker's Razor compression time of 0.02 s used as attack. Published values in other units (Sordin's 82 dB(A) sound limit, the GSSh-01 115 dB threshold, SportTac critical levels, the M32 Plus 82 dB activation level, the Liberator Move-mode figure) are shown in inspection but not mapped to the DSP, because they have no calibrated dBFS meaning;
2. a **family value** for a few headsets, such as the quieter amplification of the Tactical Sport or the hotter one of the Walker's Razor;
3. a **construction class**: over-ear digital premium, over-ear analog military, over-ear consumer analog or digital, in-ear digital, or hybrid. Classes set quiet gain, band, voicing, noise, attack, release, saturation and delay.

Only class values may be moved by the worn item's EFT template, inside fixed bounds: quiet gain by up to ±2 dB around the common template gain, release to the template's 120–320 ms, analog attack to its 1–40 ms, saturation to three times its distortion (at most 0.6), and the microphone high-pass to its 150–300 Hz. Items of one family thereby differ as their templates do, while the absolute calibration stays with the model. Every derived value is marked as proposed.

The F12 `Hear-through Character` control scales the colouring only: voicing and saturation linearly, noise by `20·log10(scale)` dB, with 0 % switching every colouring stage off. Gain, band, dynamics and, above 0 %, delay belong to the device and do not move with it.

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

### Sources that reference the stock mixer

The replacement mixer is installed when BetterAudio loads `Audio/MasterMixer`, and every source that asks BetterAudio for a group lands on it. Some prefabs carry a serialized group instead: the BTR engine and movement loops, the precipitation and wind blenders, scene ambient emitters, synchronized loops, radio broadcasts, trigger sounds with a forced group, and sources under a custom occluder output. Those references resolve to the stock asset, which stays alive for the menu. Nothing processes a source there: EFT writes the worn headset's template to `BetterAudio.Master` only, and the passive bus and electronics exist only in the replacement.

At mixer load the mod pairs each stock group below `World` with its namesake below `GAL Passive`. A name unique in both mixers is its own proof; same-named groups such as `Occlusion` and `Occlusion/Occlusion` are told apart through the contrast route paths, deepest first. A group with no unambiguous pair is left alone and listed once in the log. Patches on the game's assignment points move a source as it is set up, and a budgeted scene sweep moves any that no code touches. The move changes only the source's output group and writes no mixer parameter, so in `Vanilla` the sound is unchanged and in `Realistic` it takes the same path as every other world sound.

## Headset inspection

Inspection reads the same client-side profile as the Realistic DSP; no server item-template changes are required. Realistic displays passive attenuation as three arithmetic averages of the available reference points: low (63 Hz to below 500 Hz), mid (500 Hz to below 2 kHz), and high (2–8 kHz). These averages simplify the interface only; audio calculations retain the full curve. The electronics rows show the inspected item's own values at the current `Hear-through Character`: compressor release, quiet gain and attack, microphone band, electronics noise and colouring (the presence lift). Published notes of the device appear in their tooltip.

Vanilla displays only its compressor release and gain. Characteristic labels do not carry a mode prefix. An asterisk marks a family-surrogate or proposed value, judged per value: the Sordin and GSSh-01 microphone bands and the Walker's Razor attack are published and carry none. Unknown headset profiles retain the stock route.

## Explosion exposure and recovery

Grenade playback enters the headset world-audio path. Its hearing after-effect is separate from shot accumulation. Let `d` be distance in metres, `R` the outdoor close-blast radius, `M` the indoor radius multiplier, and `P` the passive low-band protection estimate in dB:

```text
outdoor exposure = (R / max(0.25, d))^2
indoor exposure  = R * M / max(0.25, d)
severity         = clamp(exposure, 0, 1) * max(0, 1 - P / 35) * barrierTransmission
```

Protection takes its share in decibels: hearing after-effects grow roughly linearly with the level reaching the ear in dB, so 17.5 dB of ear cups halve the effect and 35 dB remove it. A close indoor grenade still delivers 140 dB and more through ordinary muffs, so a headset reduces the stun and keeps a wearer out of the severe phase rather than cancelling it.

At full strength, blast hearing loss lowers everything by 18 dB and closes a one-pole low-pass towards 250 Hz; the severe phase, reached only without protection, lowers it by 27 dB towards 175 Hz. A dull, quiet low end remains while the highs disappear: at the severe peak about -28 dB at 100 Hz, -42 dB at 1 kHz and -54 dB at 4 kHz.

The indoor branch requires both the explosion source and the listener to be indoors. A grenade inside a building does not grant the indoor multiplier to a player outside. These are relative gameplay exposure laws, not calibrated blast-pressure predictions; the indoor branch falls as inverse distance.

For a potentially relevant blast, three rays run to the player's head from a vertical equilateral triangle facing the player horizontally. Its side is 2 m, one vertex points upward, and its lower edge is 10 cm above the grenade position. A barrier counts only when all three rays intersect the same identified surface. Each common concrete surface multiplies exposure by 0.5; each other common surface by 0.75. Repeated hits on the same surface are deduplicated, and player/body equipment colliders are excluded. This approximation modifies hearing exposure, not EFT's original sound propagation, and does not resolve wall thickness, diffraction, or reflection paths.

Exposure begins after `d / 340 + 0.12` seconds and ramps in over 0.1 seconds, allowing the initial explosion transient to precede the hearing loss. Ordinary events recover linearly; their configured hearing-loss and ringing durations scale with the square root of severity. At maximum severity, the close-blast duration extends the enabled effects: after onset, the first half holds a plateau and the second half recovers linearly. Overlapping blast responses use their maximum rather than an unbounded sum.

Explosion hearing loss and ringing each have independent strength and duration settings. The close-blast duration is a separate control. Default values are 45 s hearing loss, 90 s ringing, and 180 s close-blast recovery; the outdoor radius is 5 m and the indoor multiplier is 3. Protection and intervening barriers can prevent maximum severity even inside the nominal close-blast radius.

## F12 organization and defaults

Sections are ordered `General`, `Gunshots`, `Explosions`, then `Low-level & debug`. Low-level controls are advanced entries, hidden until the Configuration Manager's Advanced toggle is enabled. General contains the master switch, preset, the Hearing Loss and Ringing switches, headset mode, fit, Hear-through Character and the headset caliber penalty. Shot and explosion effects have their own sections; `Shot Density` and `Muzzle Device Influence, dB` are in Gunshots, `Explosion Level` in Explosions. The direct level controls (`Direct Level Normalization, dB`, `Direct Level Target`, `Copy Headroom, dB`, `Shot Density, dB`) are under `Low-level & debug`, next to the low-end layer.

Default settings are Balanced, Realistic, Tight fit, Hearing Loss and Ringing on, Hear-through Character 100%, headset caliber penalty 6 dB; shot impact 160%, contrast 8 dB, Shot Density on at 6 dB, Muzzle Device Influence 0.4 dB, Explosion Level on, indoor emphasis 100%, hearing-loss/ringing intensity and duration 100%, and ear difference 140%. The direct level defaults to 12 dB normalization (full correction), the Energy target and the AttackWindow measure. The added layer defaults to CachedReport / FullReportPerShot, 6-round report overlap, 100% late tolerance, 12 semitones down, 12 dB normalization, 3 dB cartridge contrast, 10–2000 Hz filtering, 50% fade, 30 ms fallback decay, +20 dB copy gain, inherited occlusion, and a 500 Hz fully occluded cutoff. Both diagnostic logs are off; per-shot logging resets to off at every start. Existing saved configuration values take precedence over these defaults, and settings stored in percent by earlier versions are converted to decibels on first load without changing the sound.

## Limitations and interpretation

- Digital RMS and K-weighted targets are mix calibration, not sound pressure. The shot level index orders shots by relative level; it does not predict dB SPL at the ear.
- The cartridge model uses projectile energy and muzzle distance. It cannot infer propellant mass, muzzle pressure, barrel length, muzzle-device effect beyond EFT's loudness value, or exact directivity from EFT templates.
- The direct level correction is bounded by the peak budget: a recording that would need to peak above it to reach its target stays below the target, and the added low end is reduced first. Lifts are also bounded to 12 dB.
- Binary indoor/outdoor state cannot describe room size or materials.
- Minimum-phase filters approximate magnitude-only headset tables; they do not reproduce measured impulse phase.
- Electronic gain and timing values without product-specific measurements are engineering approximations.
- Local bounded stages do not prove final device headroom after EFT's complete nonlinear mixer and the user's operating-system audio chain.
- Tests and offline renders establish deterministic implementation behavior. Audible balance, transitions, and comfort require controlled in-raid listening with the installed build.


## Native loading and global controls (1.0.0)

The packaged BepInEx preloader loads the DSP before raid mixer assets and registers its audio definitions through an internal UnityPlayer function. Both binaries are SHA-256 guarded. Registration uses memory only; game metadata is untouched. Unsupported builds fail closed. Instance creation and advancing DSP callbacks are checked separately from DLL presence.

Menu and BetterAudio retain separate mixer objects. Master, InGame, UI, Chat, Music and Hideout volume controls are synchronized in both directions, preserving raid fades and subsequent settings changes. Headset profile/category controls are excluded.

The complete package was installed by the user from the 0.23.3 candidate archive after removing the legacy installation and restoring original game metadata. All three installed DLLs matched the archive. Normal launcher/raid logs confirmed BepInEx registration, one native DSP instance, ComTac V Realistic activation without fallback, return to Vanilla after headset removal, and restored global world volume. The user reported working sound. Version 1.0.0 promotes this implementation with release version and documentation changes; it does not claim exhaustive acoustic validation of every headset or scene.
