# Changes from vanilla

## 1.2.0 - loudness by cartridge, Sound Engine Backport

- How loud your own shot is now follows the cartridge: the muzzle energy of the round and how far the muzzle is from your ear. A pistol sits below an assault rifle, a 7.62×51 battle rifle above it, and a .338 or .50 caliber rifle at the top. Indoors the shot is a little louder, and a suppressed shot plays 6 dB quieter than the same shot without one. Only the level moves; each recording keeps its own character, tail included.
- Fixes pistols playing louder than assault rifles. EFT's recordings are mastered to whatever level they were mastered to, so a Makarov came out well above an AK-74M and two 12 gauge shotguns several dB apart. G.A.L. now measures every recording once and brings the whole shot to the level of its cartridge: the recording, its tail, the room and the added low end move together. A shot never peaks above a fixed budget before the output limiter, so a loud recording is turned down, a quiet one is lifted only as far as its peak allows, and where the recording and the added low end together would exceed the budget, the low end gives way first.
- The stock sounds and replacement sound packs come out at the same loudness for the same cartridge, since a pack's recordings are measured and corrected the same way.
- Suppressors and brakes sound like the device you fitted. The game plays one suppressed recording whatever the can, barely quieter than the unsuppressed one, and the mod used to take every suppressor down by the same 6 dB. Now the device's own Loudness rating moves your shot: an integral suppressor or a big can takes it well down, a short moderator only a little, a compensator or a brake raises it. The hearing after-effects follow the same scale, so suppressed pistol-caliber and intermediate fire no longer deafens you and a brake adds to the dose. `Muzzle Device Influence, dB` under Gunshots (0.4 by default) sets how much a point of the rating is worth; 0 ignores the devices.
- Explosions are as loud as they are close. The game fades a blast out with distance the way it fades out footsteps, so a grenade thirty metres away ended up quieter than your own pistol. The new `Explosion Level` switch under Explosions (on by default) puts blasts on the same loudness scale as the shots: a hand grenade within about twenty metres is as loud as the heaviest rifle at your shoulder, at thirty metres like a rifle, at forty like a pistol, and it keeps falling off with distance beyond. Walls and occlusion still work as the game intends. Off keeps the game's own levels.
- Your own shots are denser. Most rifle recordings are a short crack with a much quieter room behind it, and at full scale that crack is all a gain can give you. The new `Shot Density` switch under Gunshots (on by default) rounds the crack off and lets the body and the room of the shot through at full level, the way most games master their gunshots, so a rifle has the weight of its report and not only its snap. The loudness of the attack still follows the cartridge, so pistols stay below rifles; pistols and shotguns, whose recordings are already dense, are not changed. A steady sound under a rifle shot dips by a decibel or two for a quarter of a second. Off keeps the recordings' own shape. `Shot Density, dB` under Low-level & debug (6 by default, 1 to 9) sets how far the shot is driven.
- Hearing loss and ringing follow the same cartridge-and-distance scale. A pistol fired next to your face now hurts almost as much as an assault rifle, and the Desert Eagle's .50 AE lands among the heavy rifles. Grenade launchers rank below a rifle for your ears, and a flare pistol barely registers. A 12 gauge shell is ranked by the slug of its gauge, next to 7.62×39.
- Heavier rounds get through a headset's ear cups in proportion to their energy, set by the new `Headset Caliber Penalty, dB` under General (6 dB by default, up to 10). The value is the protection a .50 BMG round takes away; 7.62×51 takes about a third of it, and pistols and 5.45×39 take none. 0 gives every cartridge the same protection. It affects hearing loss and ringing, not how the headset sounds.
- Fixes cartridges added by other mods being treated as an unknown light round. A .700 Nitro Express left no ringing in a headset and did less to your hearing than an assault rifle; it now ranks next to the 12.7 mm rounds, for its loudness, its hearing damage and its added low end alike. Any other mod's round lands where the energy of what it fires puts it.
- Low-level & debug adds three controls. `Direct Level Normalization, dB` (12 by default) sets how strongly the loudness follows the cartridge; 0 plays the recordings as shipped. `Direct Level Target` picks Energy (the continuous scale above, the default) or Baseline (four fixed levels: pistol, intermediate, rifle, heavy). `Copy Headroom, dB` (3 by default) sets how much of a shot's peak budget the recording leaves to the added low end: 0 gives the recording the whole budget and cuts the low end of loud rifles hard, more headroom keeps more low end and makes those shots a little quieter.
- `Cartridge Contrast, dB` now defaults to 3 dB and follows the round's energy on the same scale, so heavier rounds get more added low end and lighter ones less. Pistols already sit lower through their loudness, so the added low end needs less of a gap of its own. A configuration file from an earlier version keeps the value it holds; reset the setting to get the new default.
- Measurements are remembered between sessions in `GunsAreLoud.LevelCache.json`, next to the diagnostics log in the mod's plugin folder. A weapon you have fired before has its full low end and its corrected loudness from the first shot; a new weapon or a changed sound pack is measured when you pick it up. The file is safe to delete, and `Reset Level Cache` under Low-level & debug empties it from F12 and measures the weapon in hand again.
- Works together with Sound Engine Backport (SEB) 1.0.0, its live gun engine and its live mixer included. Fixes G.A.L. losing its added low end and Realistic headset processing with SEB installed, and hearing loss from your own shots depending on which mod happened to act first.
- With SEB, your shots are SEB's recordings with the added low end, cartridge and suppressor differences, indoor emphasis, hearing loss, ringing and Gunshot Contrast on top, at the same loudness as the stock sounds for the same cartridge. The added layer follows every shot, including the first seconds after you pick up a weapon. SEB's Gunshots volume stays yours: G.A.L. reads it from SEB's settings and keeps your shots and their added layer that much louder or quieter.
- SEB's live engine plays every round of automatic fire as a whole recording, and every round gets its own added copy. Automatic Weapon Route and Automatic Report Shape do not apply to those weapons; Automatic Report Overlap does, and their F12 descriptions say so while SEB is running.
- Realistic headsets work inside SEB's mixer; Headset Diagnostics shows `DSP active (SEB)`. SEB's Ambience setting keeps working with a headset on. SEB's per-headset rain level is not used in Realistic, where the ear cups decide how loud rain is.
- In a headset, automatic bursts are about 2–3 dB louder with SEB than with the stock sounds. Every SEB round carries its own room tail, and the tails pile up below the level where the headset's electronics start turning sound down. This is how SEB's recordings behave and is left as it is.
- With an SEB version whose mixer G.A.L. does not know, Realistic is unavailable and Headset Diagnostics shows `DSP off — Sound Engine Backport mixer` with the reason. Turning off SEB's live mixer in its own settings brings Realistic back.

## 1.1.2 - antivirus false positive

- Nothing changes in the sound. The headset electronics file in 1.1.0 was flagged as a trojan by BitDefender and by the scanners built on its engine (Emsisoft, GData, eScan, VIPRE, ALYac, Arcabit), and Nexus Mods quarantined the archive on that verdict. The file is a Unity audio effect, and the detection keyed on the file's name rather than on anything it does: the same code under another name scans clean, and an empty file under the old name is flagged. The file is now `GalHeadphoneElectronics.dll`.
- All three files in the archive change in this version. Replace all of them: a mixed installation keeps the headset on Vanilla. The old `AudioPluginGalHeadphones.dll` is no longer used and can be deleted.

## 1.1.0 - headset electronics and grenades

- In Realistic, an active headset no longer sounds like perfectly clear open ears. Its electronics now carry the character of the real device: the band its microphones pass, with steep edges, a lift in the presence region around 3 kHz and a little less low end, a faint hiss in a quiet room that disappears under ambience and gunfire, and loud sounds rounded off into the limit instead of cut flat. A premium digital Sordin, ComTac V or VI or Ops-Core is the cleanest and quietest. Consumer models such as the Walker's Razor or Earmor M32 amplify more, hiss more and react slower, so the first crack of a shot gets through before they clamp; the analog GSSh-01 and ComTac II react slowly too, and the GSSh-01 has the narrow 300-7000 Hz band of its own specification.
- How much each headset amplifies quiet sounds, and how fast it recovers after a loud one, now differs by headset and follows the game's own settings for each variant instead of being the same for all.
- Where makers publish a figure (the Sordin microphone range, the GSSh-01 working range, the Walker's Razor response time) it is used exactly as published. Everything else is an estimate from the headset's construction.
- General adds Hear-through Character, 100% by default. 0% removes the hiss and colouring while keeping amplification, band limits and protection; up to 200% exaggerates them. Vanilla is unaffected.
- Headset inspection adds the compressor attack, the microphone band, the electronics noise and the colouring, and shows the maker's own figures in the tooltip. A star still marks an estimate rather than published data.
- A headset now softens a grenade's stun instead of nearly cancelling it. Ordinary ear cups take about half of the effect off, so a close blast indoors still muffles your hearing and rings, though without the minutes-long near-deafness of an unprotected ear. High-rated in-ear protection shields more. Without protection the exposure is unchanged.
- A grenade's hearing loss no longer silences everything: a dull, quiet low end stays audible while the higher frequencies drop away, and it clears as the effect recovers. A close blast on bare ears pushes it furthest.
- Fixes the BTR's engine, rain, wind and a few other world sounds being heard straight through an active headset, as if it were not worn, in Vanilla and Realistic alike. Those sounds now pass through the headset like everything else, from the moment they start. The same applies to scene ambience, synchronized loops, radio broadcasts and trigger sounds.
- All three files in the archive change in this version. Replace all of them: a mixed installation keeps the headset on Vanilla.

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
