# Changes from vanilla

## 1.0.0 - installation and audio routing

- Includes the BepInEx preloader and native DSP; no separate compilation or game-file registration is needed on the supported player build.
- Synchronizes global volume controls between menu and replacement mixers, fixing muted world audio while interface sounds play.
- Adds concise F12 statuses: DSP loading, DSP ready, DSP active and DSP error.
- Rejects incomplete mixer loading and requires the mixer asset during compilation.

## Gunfire

- Your first-person shots have a fuller low end and more pronounced impact, while retaining EFT's original weapon recordings.
- Cartridge families sound more distinct: rifle rounds carry more low-end weight than pistol rounds, and suppressors reduce the added impact.
- Automatic fire adds weight to each shot throughout a burst.
- Indoor shots have a stronger room presence and cause greater hearing exposure.
- Gunfire stands out more against footsteps, speech, and ambience. The contrast control lowers these competing sounds.

## Hearing loss and ringing

- Your shots cause temporary muffling and ringing. Sustained fire builds up exposure and takes longer to recover from.
- Shoulder-fired weapons affect the two ears differently; switching shoulders mirrors the difference. Pistols produce a smaller difference.
- Nearby grenade explosions can cause much stronger, longer-lasting hearing loss and ringing. A close blast can leave hearing heavily reduced for several minutes.
- Blast effects weaken with distance, more quickly outdoors than indoors. Walls and other obstacles shielding your head from the blast reduce their strength.
- Severe blast effects hold for the first half of their duration, then gradually fade over the second half. The initial explosion is allowed to sound before the hearing loss develops.

## Active headsets

- Realistic mode combines frequency-dependent passive isolation with amplified electronic listening. Loud events suppress the electronic path; quiet surroundings return as it recovers.
- Headset models differ in how they attenuate and color outside sound. Profiles use published attenuation data where applicable, with identified approximations where measurements are unavailable.
- The headset path processes world audio, including gunfire and grenade explosions. Headset protection also reduces hearing exposure.
- Supported modded headset variants use the corresponding profiles. Ops-Core AMP variants use the FAST RAC profile; unknown models retain Vanilla processing.
- Inspection shows average passive attenuation for low, mid, and high frequencies, plus compressor release and gain. Vanilla mode shows only release and gain. An asterisk marks an approximation or a transferred family profile.

## Controls

- F12 offers Vanilla and Realistic headset modes, headset fit, gunshot balance, and separate hearing-loss and ringing intensity/duration controls for gunshots and explosions.
- Close-blast duration, outdoor radius, and indoor radius multiplier can be adjusted separately.
- Settings are grouped into General, Gunshots, Explosions, and Low-level & debug. Advanced reveals the low-level controls.

These changes affect the local player's sound and perception. Ballistics, damage, AI hearing, and EFT's original sound assets are unchanged.
