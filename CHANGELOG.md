# Changes from vanilla

## 1.0.3 - automatic fire timing

- Fixes an extra shot heard a second or two after a long burst with some automatic weapons. Many of them fire faster than the beat of their own recording, and the added report copies followed that beat, so each was placed a little later than the one before; after a long burst the last copy was still queued seconds ahead and sounded once the trigger was released. The copies now follow the rate the weapon actually fires at, and one that would still land too far ahead of its round is brought forward instead.

## 1.0.2 - automatic fire, performance and settings clean-up

- General adds Hearing Loss and Ringing switches, both on. Either one turns its whole effect off — for your own gunfire and for explosions alike — without touching the other, the gunshots themselves, or headset protection.
- Two advanced controls now read in decibels instead of percent: Low-End Normalization (12 dB is the calibrated setting) and Cartridge Contrast (6 dB between an intermediate rifle cartridge and 9×19). They always worked in decibels underneath. An existing configuration file is converted on first load and keeps sounding exactly as it did.
- In Tail After Burst, the recorded tail played when you release the trigger no longer loses its end. The copy was tied to the game audio source it came from, and the game hands that source to the next sound within a fraction of a second; the longer the weapon's tail, the more of it was cut.
- An added copy now lasts as long as the recording it reproduces. For some tails the game reserves a window of several seconds against a recording of about one, and the copy was stretched to fill it.
- Removes the Original Band low-end method and its F12 setting, Low-End Method. The pitched copy of the game's own recording is now the only way the added low end is made; an existing configuration file loses the line on first load. While a weapon's cache is still warming, those first rounds play without the added layer instead of a live approximation.
- Fixes the whole game staying quieter and duller after a raid, most obvious with an active headset, until the client was restarted. Leaving a raid could fail part-way through undoing the gunshot contrast: the attenuation stayed written into the mixer and every tracked sound stayed routed through it. Each step of that teardown is now independent, so one failure cannot skip the rest.
- The performance summary now also reports each audio callback type separately, audio-clock lag, generation 1 and 2 collections, added report voices, and how many components this mod keeps on the game's pooled audio sources. It stays off unless you enable it.
- Sounds that are not your own shots no longer carry this mod's audio processing on the game's shared audio sources: its components are switched off there, added copies play from one shared pool. Your own shots sound the same.
- Reduces steady per-frame work: the scan for game audio sources now stops when it has covered the loaded scenes and runs again only when a scene changes, headset route and hearing values are recomputed only when they actually change, and explosions, weapon data and settings changes no longer allocate on every use.
- The added gunshot layers cost a fraction of what they did on the audio thread: between shots the low-end filter is nearly free, the copy's shape and the ringing tone are advanced instead of recomputed for every sample, and scheduling a copy no longer allocates. The sound of every one of them is unchanged.
- Sustained automatic fire no longer loses its added low end a couple of seconds in. The rolling shot clock runs on the interval the game reports, which is a millisecond or two off the one it fires on; the error accumulated every round until every copy was too late to place and all of them were skipped. Dropping one copy now puts the clock back on the present, so at most one round in a burst is affected.
- During a frame-rate stall, an added report that can no longer be placed close enough to its own round is skipped instead of played behind it. The budget is one audio buffer or a quarter of the fire interval, whichever is shorter; previously it was wide enough for a copy to land almost a whole round late, and for a stall to be answered with several copies in a row. Advanced adds Late Report Tolerance to widen or tighten that budget; the performance summary reports the outcome per round.
- The loudest shots no longer crackle. Output that would pass full scale is now turned down smoothly instead of being cut flat, which is audible on a close indoor shot fired without a headset. It acts only in the last half a decibel below full scale, so everything quieter — including every shot heard through a headset — is unchanged.

## 1.0.1 - long-raid stability and headset fixes

- Fixes audio breaking up, overlapping and stuttering after several minutes in a raid, worst during sustained automatic fire.
- Automatic fire keeps a bounded number of added report copies sounding at once. Advanced adds Automatic Report Overlap to tune it; single shots and the last round of a burst keep their full tail.
- Fixes a freshly equipped active headset sounding like earplugs until the next weapon switch.
- A close grenade's ringing no longer grows louder when you fire afterwards.
- Gunshot contrast uses mixer routes instead of per-sound filters. A contrast route problem no longer disables the headset path, and turning contrast off restores the original routing.
- Diagnostics are off by default and write to a separate bounded file, `BepInEx/plugins/GunsAreLoud/GunsAreLoud.Diagnostics.log`. Log Every Local Shot resets to off at every start; Performance Summary Log adds a ten-second summary.

## 1.0.0 - installation and audio routing

- Includes the BepInEx preloader and native DSP; no separate compilation or game-file registration is needed on the supported player build.
- Synchronizes global volume controls between menu and replacement mixers, fixing muted world audio while interface sounds play.
- Adds concise F12 statuses: DSP loading, DSP ready, DSP active and DSP error.
- Rejects incomplete mixer loading and requires the mixer asset during compilation.

## Gunfire

- Your first-person shots have a fuller low end and more pronounced impact, while retaining EFT's original weapon recordings.
- Cartridge families sound more distinct: rifle rounds carry more low-end weight than pistol rounds, and suppressors reduce the added impact.
- Automatic fire adds weight to each shot throughout a burst. Once the next round follows, an earlier added report fades out within a fixed number of fire intervals, so a long burst stays a burst instead of stacking dozens of overlapping copies. Single shots and the last round of a burst keep their full recorded tail.
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
- General has Hearing Loss and Ringing switches that turn either effect off for gunfire and explosions alike.
- Close-blast duration, outdoor radius, and indoor radius multiplier can be adjusted separately.
- Settings are grouped into General, Gunshots, Explosions, and Low-level & debug. Advanced reveals the low-level controls.
- Advanced adds Automatic Report Overlap: how many rounds one added automatic report may still be sounding over. Higher values restore longer overlap at a proportional CPU cost.

These changes affect the local player's sound and perception. Ballistics, damage, AI hearing, and EFT's original sound assets are unchanged.
