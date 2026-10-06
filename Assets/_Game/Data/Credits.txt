# Credits

Home Invasion Simulator uses the third-party work below. Every CC BY item must stay credited. This file is also the in-game
credits (pause menu > Credits): it is copied into `Assets/_Game/Data/Credits.txt` by every scene rebuild and player build
(`CreditsSync`), and the game shows everything from the first `## ` heading on — keep entries as `- ` bullets under `## `
headings; the text above the first heading is not shown. Sources of truth: `Assets/ThirdParty/**/LICENSE.md` and `Assets/ThirdParty/Audio/SOURCES.csv`
(clip slicing in `Assets/_Game/Audio/Clips/manifest.json`).

## 3D models (Sketchfab, CC BY 4.0)

- **M9 pistol** — "Beretta M9A1 w/ Slide lock" (https://sketchfab.com/3d-models/00b62f6007c44267bb29673ee7cb24e6) by KaL-ABIZZARE (@WPthai) is licensed under Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/). Modified: brand markings removed, textures resized, accessories removed, threaded barrel stub trimmed, barrel darkened. Trademarks are not covered by the licence; the game calls it "M9".
- **Glock pistol** — "G19 Pistol, Game Ready, Free version" (https://sketchfab.com/3d-models/e3412d9803f04bdaa97ad9b68ed665d7) by Rotuma, licensed under Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/). Modified: re-rigged into parts, rescaled.
- **Shotgun** — "Remington 870 (PBR|GR)" (https://sketchfab.com/3d-models/26e37df4c38c4e8a9da8adeb4b66bff6) by tris.blend (@tris09), licensed under Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/). Modified: re-rigged into parts, rescaled.
- **AR-15 rifle** — "AR-15 style rifle" (https://sketchfab.com/3d-models/ar-15-style-rifle-50d33435445e439c95e3b36e9d4bd798) by SpatialNeglect (https://sketchfab.com/jeandiz) is licensed under Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/). Modified: optic, GPS and sling strap removed, red-dot sight added, re-rigged into parts, rescaled, textures resized, magazine window made opaque.
- **Knife** — "Fighting Ka-Bar knifes" (https://sketchfab.com/3d-models/b3c3a8c7c28349268f6631567cf01d8a) by vitvitskyi, licensed under Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/). Modified: one variant used, textures capped at 2K.

## Characters and animation

- Intruder character ("Alex") and animations from **Mixamo** (https://www.mixamo.com) by Adobe, used under the Mixamo terms (royalty-free in games; no attribution required, credited anyway). Not redistributed in the source repository; see the README to restore them.
- Player gun hands: **Meta** OculusHand models from the Meta XR Core SDK, used under the Meta Platform Technologies SDK license.

## Font

- **Liberation Sans** (all in-game text, via TextMesh Pro): digitized data copyright (c) 2010 Google Corporation, copyright (c) 2012 Red Hat, Inc. (Reserved Font Name Liberation), licensed under the SIL Open Font License 1.1 (https://openfontlicense.org).

## Sound

- **The Free Firearm Sound Library** by Ben Jaszczak et al. (https://github.com/buddingmonkey/FreeFirearmsSFXLibrary), CC0 1.0 — Benelli Nova 12ga pump (stand-in for Remington 870); Walther PPQ 9mm (stand-in for Glock 19). Sliced, trimmed and normalised.
- **Freesound** (https://freesound.org) contributors, CC0 1.0 (no attribution required; credited with thanks). Most were sliced, trimmed or normalised:
  - conleec: "MISC_Police_Siren_Wailer_Static_001.wav" (https://freesound.org/people/conleec/sounds/159753/)
  - ftpalad: "Keys Unlocking Door.aif" (https://freesound.org/people/ftpalad/sounds/119918/)
  - Project_Trident: "Wooden Floor Body Slams" (https://freesound.org/people/Project_Trident/sounds/128103/)
  - JustInvoke: "Bullet Blood 4" (https://freesound.org/people/JustInvoke/sounds/138481/)
  - tekgnosis: "police" (https://freesound.org/people/tekgnosis/sounds/144702/)
  - conleec: "MISC_Police_Siren_In_Stop_Officer_001.wav" (https://freesound.org/people/conleec/sounds/159755/)
  - qubodup: "Kicking/Forcing/Breaking Wooden Door" (https://freesound.org/people/qubodup/sounds/160213/)
  - audiosmedia: "Party horn.wav" (https://freesound.org/people/audiosmedia/sounds/170583/)
  - swiftoid: "Bedroom Door Close" (https://freesound.org/people/swiftoid/sounds/184307/)
  - Lunardrive: "Four Heartbeats HQ_BeatSmith.wav" (https://freesound.org/people/Lunardrive/sounds/22416/); "Single Heartbeat Clean HQ_BeatSmith.wav" (https://freesound.org/people/Lunardrive/sounds/22440/)
  - speedygonzo: "Footsteps_sneakers on wood.wav" (https://freesound.org/people/speedygonzo/sounds/235718/)
  - DSPena: "Punches and hits" (https://freesound.org/people/DSPena/sounds/241950/)
  - jhumbucker: "Door pounding - increasing intensity" (https://freesound.org/people/jhumbucker/sounds/250539/)
  - hannagreen: "Noisy-foodsteps-Sneakers-on-Carpet_mono.wav" (https://freesound.org/people/hannagreen/sounds/256215/)
  - HunteR4708: "Knock on the door" (https://freesound.org/people/HunteR4708/sounds/256513/)
  - Talitha5: "Locked door being forced open.wav" (https://freesound.org/people/Talitha5/sounds/262538/)
  - Streety: "partypopper.flac" (https://freesound.org/people/Streety/sounds/26349/)
  - jemburyturner: "Big door creak.wav" (https://freesound.org/people/jemburyturner/sounds/273466/)
  - GryffDavid: "Bouncing Shell Casings (Various Sizes)" (https://freesound.org/people/GryffDavid/sounds/318964/)
  - Mediaman57: "Knocking_on_Door_Rattle_knob.wav" (https://freesound.org/people/Mediaman57/sounds/347142/)
  - Abyssmal: "slashkut.wav" (https://freesound.org/people/Abyssmal/sounds/35213/); "throatslashadd.wav" (https://freesound.org/people/Abyssmal/sounds/35214/)
  - krnash: "Wooden Door Slamming Open.wav" (https://freesound.org/people/krnash/sounds/389811/)
  - Lightnessko: "Keys jingling" (https://freesound.org/people/Lightnessko/sounds/390324/)
  - plasterbrain: "Tada Fanfare A" (https://freesound.org/people/plasterbrain/sounds/397355/)
  - yitibit: "Loud Knocking-Banging Interior Room Door.wav" (https://freesound.org/people/yitibit/sounds/402441/)
  - acidsnowflake: "Small pistol gunshot indoors" (https://freesound.org/people/acidsnowflake/sounds/402789/)
  - _stubb: "Blood Spatter_Squelch_Near_Mono" (https://freesound.org/people/_stubb/sounds/406582/)
  - neilsher: "Knife Stab Pull.wav" (https://freesound.org/people/neilsher/sounds/411742/)
  - kyles: "door wood old heavy kick open good impact 1 with rattle 1 without.wav" (https://freesound.org/people/kyles/sounds/452609/); "door wood old with inlaid glass impact hit rattle from inside.wav" (https://freesound.org/people/kyles/sounds/452610/)
  - kooust: "bodyfall on carpet.wav" (https://freesound.org/people/kooust/sounds/455536/)
  - brunoboselli: "Night Ambience" (https://freesound.org/people/brunoboselli/sounds/459675/)
  - Kinoton: "Face Punch  Body Impact  4x" (https://freesound.org/people/Kinoton/sounds/503277/)
  - ale-batec: "Male Fight Grunts" (https://freesound.org/people/ale-batec/sounds/511023/)
  - JovianSounds: "Radio Sign Off / Squelch" (https://freesound.org/people/JovianSounds/sounds/524205/)
  - MarStan: "Knocking out the door by foot" (https://freesound.org/people/MarStan/sounds/533858/)
  - MrFossy: "Voice_AdultMale_DeathScream_01.wav (pack: Voice - Male Grunts and Screams)" (https://freesound.org/people/MrFossy/sounds/547184/)
  - cookies+policy: "Megaphone OnOff From Trigger 1" (https://freesound.org/people/cookies+policy/sounds/556360/)
  - ralph.whitehead: "Footsteps Running On Wooden Floor Fast Pace.wav" (https://freesound.org/people/ralph.whitehead/sounds/565713/)
  - TRP: "Door knocks wood close 77mel 191026.wav" (https://freesound.org/people/TRP/sounds/573835/)
  - unfa: "Short Male Pain Grunts" (https://freesound.org/people/unfa/sounds/610991/)
  - Metrolynn: "empty shotgun shells/casings hitting ground bouncing" (https://freesound.org/people/Metrolynn/sounds/620929/)
  - Duasun: "Gore sounds" (https://freesound.org/people/Duasun/sounds/631895/)
  - magnuswaker: "GROSS IMPACT" (https://freesound.org/people/magnuswaker/sounds/641040/)
  - TheGreenEyedGhost: "Pulling_pistol_from_holster_and_securing_it_back_again_1" (https://freesound.org/people/TheGreenEyedGhost/sounds/707791/)
  - dleverett: "Door Opening Creaking" (https://freesound.org/people/dleverett/sounds/710510/)
  - Nox_Sound: "Voice_Crowd_Small_Expression_Applause_Cheer_Stereo" (https://freesound.org/people/Nox_Sound/sounds/752709/)
  - TotallyPhilip: "Remington 870 12 gauge shotgun pump racking cha chock" (https://freesound.org/people/TotallyPhilip/sounds/755495/)
  - BlondPanda: "Car_Door_Closing_Dull_04" (https://freesound.org/people/BlondPanda/sounds/778421/)
  - riippumattog: "drunk mumble" (https://freesound.org/people/riippumattog/sounds/842496/)
  - Stoniac: "Glock17_Slide" (https://freesound.org/people/Stoniac/sounds/842731/)
  - CHallSmith: "Knife Stabs into Chicken Breasts #1" (https://freesound.org/people/CHallSmith/sounds/870738/)

## Voices

- All voice lines are generated locally with text-to-speech models: Qwen3-TTS-12Hz-1.7B-VoiceDesign (Apache-2.0), Chatterbox and Chatterbox-Turbo by Resemble AI (MIT), and Dia-1.6B by Nari Labs (Apache-2.0).
