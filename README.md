# Mixed Reality Legal Simulator

A passthrough mixed-reality simulator for Meta Quest about self-defense law. A home intrusion plays out in the player's own room: the intruder comes in through a real doorway that the headset found in its room scan. In the main Scenarios mode, each time the player uses force on an intruder, the game checks that moment against a simplified model of California self-defense law, using only what the player could see at the time. The round then ends with a legal outcome: a perfect result (no force used), a justified call, a close call, or a bad call with the charge it would bring.

Content note: contains simulated violence.

Not legal advice: the rules are a simplified game model inspired by California law.

Built with Unity 6, URP, OpenXR, the Meta XR Core SDK and MR Utility Kit (v205), for Quest 3 and Quest 3S.

## How the legal judgment works

The judge (`Assets/_Game/Scripts/Scenario/Judge.cs`) is a pure C# static class with no Unity types, so it can be unit-tested headless. It runs at two levels:

1. **Per use of force.** `JudgeFacts` rates each hit the player lands. It returns *Good*, *Good with a close-call tag*, or *Bad*, plus a plain-English reason for the debrief.
2. **Per scenario.** `Evaluate` combines those calls into the outcome of the round.

**Evidence, not intent.** Every intruder has a hidden intent: attacker, burglar, surrenderer, innocent visitor or gunman. The judge never reads it. Each frame the intruder records an `IntruderFacts` snapshot of what the player could observe:

- weapon in hand and distance
- whether he is advancing (closing faster than 0.3 m/s), attacking or fleeing
- posture (standing, hands up, prone) and whether he is down
- whether he said who he is, and whether he forced the door
- whether he is behind the closed door, and whether the door camera showed him armed in the last 3 s
- whether his gun's muzzle is within 10° of the player's head or chest, is being raised, or has fired
- timers since the last threat and since he stopped moving with empty hands

When a hit lands, the snapshot from just *before* the damage is attached to a `ForceEvent`. All shotgun pellets in one frame count as one event, and only the player's own force is recorded. The reaction-grace clock is driven by the judge itself: each frame the recorder asks whether a hit now would be plainly justified, which keeps the recorder and the judge from drifting apart.

**The rule table.** Rules are checked in order and the first match wins:

| # | Situation | Call |
|---|---|---|
| 0 | Fired through the closed door | Good if the door camera showed him armed; close call if he was kicking it in (home presumption only); otherwise Bad ("fired blind") |
| 0b | His gun is pointed at you, being raised or firing | Good |
| 1 | Active threat: attacking, armed and advancing, armed within 2.5 m and not fleeing or surrendering, or down but armed and still coming | Good |
| 1b | He forced his way into your home and is not down, surrendering or fleeing | Good if he is advancing or armed, or has stood still unarmed for 3 s or less |
| 2 | Within 1 s after the threat ended | Good (close call after 0.5 s) |
| 3 | Already down | Bad |
| 4 | Surrendering | Bad with empty hands; close call if he still holds the weapon |
| 5 | Fleeing | Bad, even if armed |
| 6 | Identified himself, hands empty | Bad |
| 7 | No forced entry, or no home presumption (Marshal mode) | Bad if unarmed; close call if armed but not coming at you |
| 8 | Forced entry, standing still unarmed | Close call from 3 to 8 s, Bad from 8 s |

A close call still counts as justified, but the banner flags it.

The order follows the main ideas of California self-defense law: fear must be reasonable, judged from what the defender perceived (PC 197/198, CALCRIM 505); the home-intrusion presumption, often called the castle doctrine (PC 198.5, CALCRIM 3477); the right to use force ends when the danger ends (CALCRIM 3474); and no deadly force just to protect property or against a non-violent burglar who is leaving (*People v. Ceballos*; *Kortum v. Alkire*). Rule 5 applies that last idea more strictly than the law: force on anyone running away is a bad call, even if he is armed. The rule table was designed from primary-source research into California statutes, jury instructions and case law, then simplified for play. The timing and distance thresholds are tunables that the code labels as house rules, not law.

**Outcomes.**

| Outcome | When | Charge shown |
|---|---|---|
| PERFECT | No force was used | – |
| GOOD CALL | Every hit was justified; flagged as a close call if any hit was borderline | – |
| BAD CALL | Any hit was unjustified; the first bad reason is shown | Voluntary manslaughter if an unjustified hit killed him; assault with a deadly weapon for any other unjustified weapon hit; battery if the only unjustified hits were punches |
| YOU DIDN'T SURVIVE | The player was killed | – |

After a bad call in Scenarios mode, police respond. To surrender, the player holsters or drops every weapon and raises both controllers to chin height or above for half a second. Aiming at the officers escalates the standoff. An arrest ends on a charge card that lists California Penal Code sections with rough middle-term sentence estimates (`Sentencing.cs`). The firearm enhancement (PC 12022.5(a)) is added only when an unjustified shot hit a living person. Verdict banners carry the disclaimer *"Simplified game rules inspired by California law (2026). Not legal advice."* (Marshal mode uses a federal variant).

**Scope.** The judge scores force, not every action. Yelled commands change how intruders behave, but they are not verdict inputs, and missed shots or retreating are not scored. Restraint is rewarded through the PERFECT outcome. Police officers and Marshal-mode attackers are never judged, and Sandbox has no judge.

### Modes

- **Scenarios.** Deals from a deck of ten encounters, plus a rare false alarm: a delivery driver who knocks and leaves. The deck includes cases where the right call is not to shoot: a drunk neighbor at the wrong house, intruders who surrender, burglars who run, and an armed robber who gives up his gun if told. Pacing rules avoid dealing the same encounter twice in a row or the same kind of intruder three times in a row.
- **Marshal.** The player is a deputy U.S. Marshal guarding a protected witness overnight in a safe house. It is not the player's home, so the home presumption is switched off at rules 0, 1b and 7, and a forced door alone no longer justifies force. Waves of hitmen come for the witness; they are not judged. Unarmed visitors who arrive between the waves are judged like any intruder, and harming the witness is always a bad call. A bad call ends the night with a booking that adds a federal civil-rights count (18 U.S.C. 242). A deputy briefs the player in person and runs a short weapons drill as the tutorial.
- **Sandbox.** Free play with no judging.

### Tests

- **`JudgeSelfTest`:** 180 checks, covering the per-hit rules, gunman cases, scenario aggregation, the Marshal role and the wound model. It runs in batch mode with no headset and no art assets.
- **`ScenarioTests`:** more than 30 suites play whole encounters headless. They script yells, fire shots through the same hitbox path the guns use, and compare the verdict with the expected one.

## Mixed reality

### Passthrough and rendering

- **Passthrough-only.** The app uses a single `OVRPassthroughLayer` underlay, with cameras clearing to transparent. Tracking is floor-level; the app renders and steps physics at 72 Hz.
- **Rendering.** Vulkan, single-pass instanced, URP Mobile (MSAA 4x, render scale 0.8).
- **Depth API occlusion.** `EnvironmentDepthManager` uses soft occlusion with hand removal, so the player's real hands don't hide a held weapon. Custom URP shaders wrap Meta's occlusion in a small HLSL include.
- **Passthrough restyling.** The feed is restyled at runtime: color LUTs for red and blue police lights after a bad call and for a camera flash, and passthrough brightness and saturation for dimming.

### Fitting the scenario to the room

This uses MRUK scene data (the Scene API). The app creates no spatial anchors of its own.

- **Room scan.** `RoomScanner` waits for MRUK and catalogs `DOOR_FRAME` and `WINDOW_FRAME` anchors, walls and floor. It re-queries the device while the headset is worn and ranks doors to prefer one at least 2 m from the player.
- **Door choice.** In `DoorPicker` the player confirms the intruder's door and an optional second door. The choice is saved per room, keyed by scene-anchor UUIDs.
- **Virtual door.** `VirtualDoor` builds trim, a door leaf with a lever handle, and a dark void behind it over the scanned frame. The void is drawn through a stencil portal, and a depth mask stops the real door from hiding the virtual one. The hinge side comes from the room outline (swing clearance plus the nearest corner), and the pause menu can flip it. The door can be knocked on, kicked in, shot through, or opened by hand.
- **Navigation.** MRUK `SceneNavigation` bakes a NavMesh at runtime from the floor, with furniture carved out. `DoorwayNav` bakes a small apron behind each door and joins it to the room with a `NavMeshLink`, so intruders walk in through the real doorway. Invisible `EffectMesh` occluders and colliders cover the walls and ceiling with door holes cut. When the headset re-sends the room after a resume or re-scan, the holes are re-cut and the NavMesh is re-baked.
- **Outside the door.** A procedural night porch and street (a backyard for a side door) sits on its own layer. It is drawn only through the open doorway, using stencil-tested render features.
- **Security monitor.** Each door has a virtual security camera rendering to a texture, with an IR look, smart zoom and motion detection. It feeds a wall screen placed beside the front door, checked against walls, furniture and the door's swing. Players can watch someone walk up the path before he knocks.
- **Comfort and placement.** A floor ring marks a safe spot at least 2 m from the door, and warnings appear near real walls and furniture. Banners and boards are raycast against the scanned walls so they stay in front of them.

### Interaction

Touch controllers through `OVRInput`, with a custom grab system. Hand tracking is not used for gameplay.

- **Holsters.** The pistol sits on the right hip, the knife on the left hip and the long gun on the back. The body frame is estimated from the head.
- **Grabbing.** Proximity grab with a short input buffer, or distance grab by pointing; a two-handed support grip on the shotgun pump or rifle handguard. Letting go throws the weapon with the hand's velocity, or holsters it over a matching slot.
- **Weapons.** Each gun tracks its real chamber and magazine state: B/Y reloads, the stick click racks or pumps, and the slide locks back on empty. The M9 has an analog double-action/single-action trigger. The knife stabs or slashes depending on how the blade moves. Player height and grip-angle trims are set in the pause menu.

### AI behavior

- **Structure.** Each intruder is a coroutine state machine, and his behavior ("brain") is chosen by his hidden intent. Police officers, Marshal-mode attackers, the witness and the briefing deputy run inside an exception-guarded coroutine stack, so a fault can't silently freeze them mid-wave.
- **Entries.** Each round rolls an entry style: pounding, rushing, knock-then-kick, quiet prying, or a false alarm. Kicks are timed to the animation's contact frame. The player can talk through the door and gets intent-specific replies, or open it on him.
- **Burglars.** When a burglar notices the player's aim, he freezes, then makes a weighted choice: back away, bolt, give up (more likely under aim and a command), hide behind real scanned furniture, or draw a knife if the exit is blocked.
- **Gunmen.** A gunman always gives a visible, audible tell (0.7–0.8 s) before his first shot. Whether he obeys a command comes from a compliance model adapted from the tactical game SWAT 4: P(comply) = fit × (1 − morale), where fit is how readily his type of intruder obeys that particular command and morale is his hidden resolve, adjusted for aim, distance, wounds, shots and repeated commands. He rolls again only when the pressure changes (aim, wounds, distance band) or after 3 s, so mashing the command button can't force a surrender.
- **Police.** Officers arrive on the door camera, knock and announce, and read the player's stance every frame. They escalate from verbal warnings to a Taser to handcuffs, with controller haptics, and fire only after the player fires or after a spoken warning.
- **Hit reactions.** Per-bone hitboxes drive reactions by body part. Falls are animation-driven and steered toward open floor with raycasts against the scanned room, so bodies land clear of real walls and furniture.

### Engineering

- **Build processor.** `HISBuildProcessor` stamps the version code and git SHA, forces MRUK to live device data, strips editor-only rooms and debug visuals, syncs `CREDITS.md` into the in-game credits, and patches `boot.config` for XR.
- **Performance.** `PerfWatch` compares GPU and CPU time with the 13.9 ms frame budget. `ThermalGovernor` reads Android thermal headroom through JNI and scales effects, foveation and occlusion quality across Normal, Warm and Hot tiers.
- **Procedural vehicles.** `MeshKit` builds every vehicle and the porch set from code as vertex-colored meshes: police cars, a SWAT truck and civilian cars, each car under 3,500 triangles, with no extra materials.
- **Voice pipeline.** `VoiceLines.cs` is the single source for subtitles and audio. `scripts/make_tts.py` renders each line with local neural TTS models and checks every take with speech recognition.
- **Audio.** Sound effects play from a pooled 24-voice player that steals voices by priority. Sounds behind the door are low-pass filtered.

## Supported headsets and controls

| Headset | Support |
|---|---|
| Meta Quest 3 | Supported |
| Meta Quest 3S | Supported |
| Quest 2, Quest Pro | Not targeted |

Passthrough is a required feature, and the app needs the headset's spatial-data (scene) permission, which MR Utility Kit requests at launch. The headset needs a Space Setup scan with at least one door marked (*Settings > Physical Space*).

| Action | Input |
|---|---|
| Grab, holster, open the door | Grip (point at a weapon to distance-grab) |
| Fire | Trigger |
| Reload (shotgun: tap to pump or load, hold to keep loading) | B / Y while holding a gun |
| Rack, pump or decock | Thumbstick click while holding a gun |
| The command that fits the moment | Tap A / X |
| "Show me your hands" / "Drop the weapon" / "Get on the ground" / "Get out of my house" | Hold A / X, flick the stick up / left / down / right |
| Surrender to police | Empty both hands (holster or drop), raise both controllers to chin height or above for 0.5 s |
| Skip the verdict show | Left Y |
| Pause / restart | Menu button / hold Menu for 1.5 s |
| Pause menu | Stick up / down picks a row, left / right changes it |
| Choose mode (Scenarios / Marshal / Sandbox) | Pause menu > Mode (applies on resume) |
| Switch Scenarios ⇄ Sandbox | Hold both thumbsticks in for 0.6 s |
| Choose doors at start | Stick left / right to cycle, trigger or A / X to confirm; B / Y at the second-door step for front door only |

## Build and run

| Component | Version |
|---|---|
| Unity | 6000.6.0f1 with Android Build Support (OpenJDK, Android SDK and NDK) |
| Meta XR Core SDK, MR Utility Kit | 205.0.0 |
| Meta Interaction SDK (OVR) | 205.0.0 (installed; the game uses its own grab system) |
| OpenXR | Unity OpenXR 1.18.0, Unity OpenXR: Meta 2.6.1, XR Management 4.7.0 |
| Render pipeline / navigation | URP 17.6.0 / AI Navigation 2.0.14 |
| Android player | IL2CPP ARM64, Vulkan, min API 32, target API 34 |

1. **Open the project.** Clone the repo and open it in Unity 6000.6.0f1. The Package Manager fetches the Meta packages from Meta's registry (`https://npm.developer.oculus.com`, already listed in `Packages/manifest.json`), and no sign-in is needed. Project, XR and Android manifest settings are committed. In the Editor, MRUK loads its bundled sample living room (`MeshLivingRoom1`); on the headset it uses your own scan.
2. **Restore the excluded assets.** The project opens and compiles without the Mixamo character, and the judge tests run, but the game isn't playable until the character is restored (see [Third-party assets](#third-party-assets)).
3. **Build the APK.** Use the menu **HIS > Build Quest APK** (HIS is the project's internal code prefix), or **HIS > Build Quest APK (Development)** for a development build. Menu builds are written to the `Builds/` folder. Or build headless:
   ```bash
   # "Unity" = path to the 6000.6.0f1 editor binary
   Unity -batchmode -quit -projectPath . -buildTarget Android \
     -executeMethod BuildScript.BuildQuest -outputPath Builds/app.apk   # add -dev for a development build
   ```
   Builds use Unity's debug signing key; no keystore is needed for sideloading.
4. **Sideload.** Enable developer mode for the headset in the Meta Horizon mobile app, connect it over USB and accept the debugging prompt. Then run `adb install -r -g <path to the .apk you built>` and launch the app from *Library > Unknown Sources*, where it is listed under the project's working title. On macOS, `scripts/deploy.sh` installs the default menu-build output, launches it and tails the log.
5. **Run the tests.** `Unity -batchmode -nographics -projectPath . -executeMethod JudgeSelfTest.Run -logFile Logs/judge.log` runs the judge tests with no headset or assets. It exits with code 0 when every check passes, and the log reports `[HIS] JUDGE n/n passed`. Once the characters are restored, most end-to-end suites run the same way (for example `-executeMethod ScenarioTests.RunHeadless`); leave out `-quit`, because they enter Play Mode and exit on their own.

## Third-party assets

No Asset Store or paid packages are used. Full credits, including the model modifications, are in [`CREDITS.md`](CREDITS.md), and the game shows them under *Pause > Credits*.

**Included, with attribution**

| Asset | Author | License |
|---|---|---|
| [Beretta M9A1](https://sketchfab.com/3d-models/00b62f6007c44267bb29673ee7cb24e6) | KaL-ABIZZARE (@WPthai) | CC BY 4.0 |
| [G19 pistol](https://sketchfab.com/3d-models/e3412d9803f04bdaa97ad9b68ed665d7) | Rotuma | CC BY 4.0 |
| [Remington 870](https://sketchfab.com/3d-models/26e37df4c38c4e8a9da8adeb4b66bff6) | tris.blend (@tris09) | CC BY 4.0 |
| [AR-15 style rifle](https://sketchfab.com/3d-models/ar-15-style-rifle-50d33435445e439c95e3b36e9d4bd798) | SpatialNeglect | CC BY 4.0 |
| [Ka-Bar knife](https://sketchfab.com/3d-models/b3c3a8c7c28349268f6631567cf01d8a) | vitvitskyi | CC BY 4.0 |
| Sound effects from [Freesound](https://freesound.org) and the [Free Firearm Sound Library](https://github.com/buddingmonkey/FreeFirearmsSFXLibrary) | various (see `Assets/ThirdParty/Audio/SOURCES.csv`) | CC0 |
| Liberation Sans font | Google, Red Hat | SIL OFL 1.1 |
| Voice lines, generated locally with Qwen3-TTS, Chatterbox and Chatterbox-Turbo, and Dia | – | Models under Apache-2.0 / MIT |

Sound effects not listed above are synthesized by `scripts/prepare_sfx.py`.

**Not included**

- **Mixamo character "Alex" and 79 animations (Adobe).** These are free to use in games, but the raw FBX files may not be redistributed ([Mixamo FAQ](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html)). To restore them:
  1. Sign in at [mixamo.com](https://www.mixamo.com).
  2. Download **Alex** as *FBX for Unity*, *T-pose*, *With Skin*, and save it as `Assets/ThirdParty/Mixamo/Alex/Alex.fbx`.
  3. With Alex selected, download each clip below as *FBX for Unity*, *Without Skin*, *30 fps*, with no keyframe reduction (*In Place* for walk and run cycles). Save each one as `Assets/ThirdParty/Mixamo/Animations/<File>.fbx`.
  4. In Unity, run **HIS > Reimport Mixamo Assets**, then **HIS > Build Alex Prefab**, then **HIS > Build Main Scene**.
- **Meta XR SDK** (including the player's hand models). The Package Manager downloads it under the [Oculus SDK License](https://developers.meta.com/horizon/licenses/oculussdk/).

<details>
<summary>Mixamo clip list (file name = Mixamo search name)</summary>

BackAwayFear = Walk Backwards (Slowly Backing Away In Fear) · BayonetStab = Bayonet Stab · CaughtReact = Reacting (Being Surprised And Looking Right) · CrossPunch = Cross Punch · CrouchToProne = Crouch To Prone · CrouchedWalk = Crouched Walking · DeathFromFrontHeadshot = Death From Front Headshot · DeathFromTheBack = Death From The Back · DeathFromTheFront = Death From The Front · DizzyIdle = Dizzy Idle · Driving = Driving · DrunkHiccup = Drunk Idle Variation (Hiccup) · DrunkIdle = Drunk Idle · DrunkRun = Drunk Run Forward · DrunkStumble = Drunk Idle Variation (Stumble) · DrunkTryDoor = Opening (Attempting To Open A Door) · DrunkTurn = Drunk Turn · DrunkWalk = Drunk Walk · DrunkWalkBack = Drunk Walk Backwards · DuckCover = Ducking (Ducking For Cover From Standing Idle) · ExitingCar = Exiting Car · FallToKnees = Praying (Falling To Knees In Prayer) · FightIdle = Fight Idle · GetUpFromBack = Getting Up · GetUpFromStomach = Getting Up (Getting Up From Stomach) · HandsForward = Hands Forward Gesture · HandsUpIdle = Standing Block Idle · HeadHit = Head Hit · HitOnLegs = Hit On Legs · HitToBody = Hit To Body · Hook = Hook · Idle = Idle · InjuredWalking = Injured Walking · JabCross = Jab Cross · KneelDeath = Dying (Shot To The Chest Falling On Two Knees) · KneelHandsOnHead = Female Crouch Pose (On Knees, Arms Raised) · KneelIdle = Kneeling Idle · KneelToStand = Standing (Standing Up From A Kneeling Position) · KnifeIdle = Knife Idle · KnockedDownStomach = Knocked Down (To Stomach) · LookAroundNervous = Look Around (Nervously) · LookOverShoulder = Look Over Shoulder · LowCrouchIdle = Crouching Idle (Low) · MmaKick = Mma Kick · PistolDraw = Drawing Gun (From Lower Back) · PistolFire = Shooting (Firing A Gun) · PistolGrabFloor = Grabbing Pistol (On The Floor And Taking Aim) · PistolHit = Hit Reaction (While Holding A Pistol) · PistolIdleAimed = Pistol Idle (Idle With Aimed Pistol) · PistolIdleReady = Pistol Idle (Ready Alert Two Hand Pistol Grip) · PistolLower = Pistol Aim (To Holster Idle) · PistolRun = Pistol Run · PistolWalkAimed = Pistol Walk (With An Aimed Pistol) · PointThumb = Pointing (Behind With Thumb) · ProneDeath = Prone Death · PutDown = Putting Down (An Object) · Rummaging = Rummaging · RunLookBack = Run Look Back · Running = Running · ShoveReaction = Shove Reaction · Shrug = Shrugging · SittingIdle = Sitting Idle · SlashAdvance = Slash Advance · SneakForward = Sneaking Forward · Stabbing = Stabbing · StandingDeathBackward01 = Standing Death Backward 01 · StandingDeathForward01 = Standing Death Forward 01 · StandingMeleeAttackKickVer1 = Standing Melee Attack Kick Ver. 1 · StandingReactLargeGut = Standing React Large Gut · StandingReactSmallFromHeadshot = Standing React Small From Headshot · StomachHit = Stomach Hit · StumbleBackFall = Stumble Backwards (Slip And Fall Backwards) · Terrified = Terrified · TossAway = Throw (From A Standard Pose) · TripFall = Tripping (Walk To Tripping And Falling) · WalkNormal = Walking (Male Standard Walk) · Walking = Walking · YellItsMe = Yelling Out (Stepping Forward And Yelling Out) · ZombieCrawl = Zombie Crawl

Where a file name differs from Mixamo's title, rename the download to the file name.
</details>

## Project structure

The code uses the project's working namespace, `HIS.*`.

```
Assets/_Game/
  Scripts/
    Scenario/   judge, scenario director, police response, sentencing, commands, voice lines
    Intruder/   intruder state machine, behaviors, hitboxes, doorway navigation
    Police/     police officers and Marshal-mode attackers
    Room/       MRUK room scan, door picker, virtual door, room geometry, comfort helpers
    Security/   door cameras, wall monitor, porch set, procedural vehicles (MeshKit)
    Weapons/    hands, grabbing, holsters, firearms, knife
    Outcome/    verdict banner and outcome staging
    Core/       bootstrap, pause menu, audio, performance and thermal management
    UI/         subtitles, notices, credits
  Editor/       scene and prefab builders, build pipeline, headless test suites
  Shaders/      URP shaders with Depth API occlusion and the door portal
  Scenes/Main.unity
Assets/ThirdParty/   Sketchfab models (CC BY 4.0), audio provenance; Mixamo goes here when restored
scripts/        build and deploy helpers (macOS), voice and sound generation, headset tools
CREDITS.md      third-party credits (also shown in game)
```
