using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Room;
using HIS.Security;
using HIS.UI;
using HIS.Weapons;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Scenario
{
    /// <summary>
    /// The Marshal night's intro, which is also the basic tutorial: THE HANDOVER, IN PERSON (Brett 0.1.57: "why is ortiz just
    /// coming up and in then out ... I thought ortiz was just apart of the story intro and tutorial which seemed to be missing";
    /// before that, 2026-10-04 after 0.1.55: "it should have more of a story feel to it"; research/26 §2's practice round,
    /// framed for the Marshal, research/27). The night calls it first (<c>if (MarshalBriefing.Due) yield return
    /// MarshalBriefing.Run();</c>):
    /// <list type="bullet">
    /// <item>WHEN: the first Marshal night after the app starts or after switching INTO Marshal mode (<see cref="EnteredMarshal"/>,
    /// from ScenarioDirector.SetMode), and any night the pause menu's "Weapons check" row asks for (<see cref="Request"/>). Not
    /// after a night's card (the next night goes straight to the 1:12 card), not after a hold-Menu restart. The Editor and the
    /// batch tests only when a test asks (<see cref="Request"/>, <see cref="RequestHandover"/>, <see cref="TestSessionRule"/>).</item>
    /// <item>WHO: Deputy Ortiz is IN THE ROOM from the start of that night — spawned with Ricky under the U.S. MARSHAL card,
    /// standing inside beside the front door on its latch side, clear of the leaf (Intruder.HandoverStandSpot), dressed as the
    /// deputy, no weapon out, turning to face you. He says every line IN PERSON from his head (his deputy voice, positional,
    /// the caption "Ortiz"; Intruder.HandoverSay). One voice at a time (a line waits for the one on air; Ricky's answer too).
    /// Labels (UI/WorldLabel, at most two up): "Deputy Ortiz" over his head while he says hello, then "Ricky" over Ricky while
    /// he names him. Practice-safe until he's removed: any hit on him is "Whoa! Watch it!" (IntruderHandover), on Ricky "Hey!
    /// Watch it!" (while <see cref="Running"/>) — no harm, nothing judged.</item>
    /// <item>THE LINES: "Marshal. I'm Ortiz." / "That's Ricky. He testifies Monday." / "Keep him breathing till two." — Ricky:
    /// "So you're the new guy." — then the DRILL when it's due (never done on this headset: the v2 PlayerPrefs flag, or asked
    /// for by the pause row), else the SHORT handover.</item>
    /// <item>THE DRILL (steps 1–10): "Weapons check before I go." (the skip label at the left hand from here), steps 1–8 as
    /// before in his in-person lines, step 9 "Get the door for me." — once you've opened it he walks out through the doorway,
    /// onto the porch and off past the camera's edge, and is removed (the visitors' WalkOffPorch → Vanish); through the doorway
    /// he says "Lock it behind me." ("Swing it shut" at the handle); the door latched shut behind him is step 9 done (shut on
    /// him too soon, he opens it himself and goes — never stuck). Step 10 ON THE RADIO (he's on the porch camera walking off):
    /// "That screen's your porch camera." / "Menu pauses. Hold it to start over." Done: "Good. See you at two." on the radio.</item>
    /// <item>THE SHORT HANDOVER (no drill due): after Ricky's line, "Two o'clock. Lock it behind me.", he opens the front door
    /// himself, walks out, pulls it shut behind him and walks off past the camera. About 15 s; only the two name labels.</item>
    /// <item>SKIP: holding Y for 1 s with nothing in the left hand (or only the knife) — in the drill or the short one: "Alright.
    /// Lock it behind me." and he leaves as in the short one. The night never waits for him to clear the porch: it goes on (the
    /// 1:12 card) once the door is shut behind him, or <see cref="SkipLeaveSeconds"/> s after the skip at most. A tap of Y stays
    /// a left-handed reload or pump.</item>
    /// </list>
    /// The drill's steps, worded to the real controls (Weapons/HandController, Firearm, Knife, Loadout, PlayerBody, Room/VirtualDoor,
    /// Scenario/CommandSystem, Core/GamePause); B becomes Y when that gun is in the LEFT hand:
    /// <list type="number">
    /// <item>1 "Stand up straight for me." (the height measure re-runs; head still and level for 1.5 s; the skip label stays
    /// at the left hand through it);</item>
    /// <item>2 "Sidearm, right hip. Draw it." — "Squeeze grip" at the right hip (the sidearm in a hand);</item>
    /// <item>3 "Put three in the target." — "Trigger" over the target (+ "the first pull is long" on a decocked M9; three
    /// practice rounds: the slide locks back);</item>
    /// <item>4 "You're dry. Reload." — "B: reload" at the gun (reloaded);</item>
    /// <item>5 "Holster it." — "Let go of grip here" at the right hip (holstered);</item>
    /// <item>6 "Long gun's on your shoulder." — "Over your shoulder / squeeze grip" in front of the right shoulder, its arrow
    /// pointing over it; drawn: "One round." — "Trigger" over the target; fired: the pump shotgun "Pump it." — "Tap B: pump"
    /// at the gun (one shot, one pump); the AR-15 (the pause menu's other Long gun) "Reload it." — "B: reload" (one shot, one
    /// reload);</item>
    /// <item>7 "Knife's on your left hip. Use it." — "Squeeze grip" at the left hip, then "Stab or slash" over the target (a
    /// blade lands on it; after <see cref="KnifeFallbackSeconds"/> s with the knife out, a fast swing anywhere does: a target
    /// the blade can't reach — half in a real wall, over furniture — never holds the check up);</item>
    /// <item>8 "Let me hear you call it." — "A / X: yell" at the right hand (any yell: a Marshal's are the "U.S. Marshals!" lines);</item>
    /// <item>9 "Get the door for me." — "Squeeze the handle, swing" at the handle (+ "with an empty hand" while both hands hold
    /// something: the lever only takes an empty hand); he walks out; "Lock it behind me." — "Swing it shut" (latched shut
    /// behind him);</item>
    /// <item>10 on the radio: "That screen's your porch camera." — "Porch camera" over the monitor (3 s; left out with no
    /// monitor), then "Menu pauses. Hold it to start over." — "Menu: pause" at the left hand (4 s).</item>
    /// </list>
    /// While a step's gun is out of the hands, its label goes back to where to draw it. Practice ammo: the pistol gets exactly
    /// three rounds for step 3, the long gun a full tube / magazine for step 6, and both guns are full again when the check
    /// ends, however it ends. A flat cardboard target (<see cref="PracticeTarget"/>) stands ~1 m inside the front door for the
    /// drill (never an Intruder, never judged), placed so Ortiz is never between you and it nor behind it as you see it.
    /// No Ortiz could be placed (no NavMesh spot by the door): the same handover goes out on the radio as in 0.1.57 (each line's
    /// vo.deputy.radio.* twin), and step 9 is the old "open, then shut".
    /// Every step logs "[HIS] Briefing: step N …" (its line — "(in person)" or "(radio)" — its label, done). A room clear
    /// mid-check (hold-Menu restart, mode switch, preview) or the night's coroutine stopping drops it cleanly (the target, the
    /// labels, the card and Ortiz go; nothing more is said) without marking it done — except on step 10, where it counts as
    /// done (its own line just taught hold-Menu).
    /// </summary>
    public static class MarshalBriefing
    {
        /// <summary>The mode card's words (ScenarioDirector.SetMode shows the same), opening the handover.</summary>
        public const string Card = "<b>U.S. MARSHAL</b>\n<size=70%>Safe house. Relief's at two.</size>";

        /// <summary>Whose voice the lines are (the caption's name).</summary>
        public const string Speaker = "Ortiz";

        // Ortiz IN PERSON (VoiceLines: vo.deputy.handover.*, his deputy voice from his head; the short handover's goodbye is the
        // check-in visit's old vo.deputy.leave take). Ricky's answer is Intruder.RickyNewGuyKey.
        const string KHello = "vo.deputy.handover.hello", KRicky = "vo.deputy.handover.ricky", KBreathing = "vo.deputy.handover.breathing",
                     KCheck = "vo.deputy.handover.check", KStand = "vo.deputy.handover.stand", KDraw = "vo.deputy.handover.draw",
                     KThree = "vo.deputy.handover.three", KDry = "vo.deputy.handover.dry", KHolster = "vo.deputy.handover.holster",
                     KLongGun = "vo.deputy.handover.longgun", KOneRound = "vo.deputy.handover.oneround", KPump = "vo.deputy.handover.pump",
                     KReloadIt = "vo.deputy.handover.reloadit", KKnife = "vo.deputy.handover.knife", KYell = "vo.deputy.handover.yell",
                     KDoor = "vo.deputy.handover.door", KLock = "vo.deputy.handover.lock", KSkipLine = "vo.deputy.handover.skip",
                     KLeave = "vo.deputy.leave";
        // ON THE RADIO (vo.deputy.radio.*: RadioVoice, the squelch and the radio band): step 10 and the sign-off — he's out on
        // the porch camera by then.
        const string RadioPrefix = "vo.deputy.radio.", HandoverPrefix = "vo.deputy.handover.";
        const string KCamera = "vo.deputy.radio.camera", KMenu = "vo.deputy.radio.menu", KDone = "vo.deputy.radio.done",
                     KOutFront = "vo.deputy.radio.outfront";

        // The labels' words: a few words of the real control (public: the tests read them).
        public const string WSkip = "Hold Y: skip the check";
        public const string WOrtiz = "Deputy Ortiz";
        public const string WRicky = "Ricky";
        public const string WGrip = "Squeeze grip";
        public const string WTrigger = "Trigger";
        public const string WTriggerDa = "Trigger\n<size=75%>the first pull is long</size>";   // the M9 decocked (research/26 §2)
        public const string WReloadB = "B: reload", WReloadY = "Y: reload";
        public const string WHolster = "Let go of grip here";
        public const string WShoulder = "Over your shoulder\n<size=75%>squeeze grip</size>";
        public const string WPumpB = "Tap B: pump", WPumpY = "Tap Y: pump";                     // tap pumps; holding B feeds shells
        public const string WKnife = "Stab or slash";
        public const string WYell = "A / X: yell\n<size=75%>hold it, flick the stick</size>";
        public const string WDoorOpen = "Squeeze the handle, swing";
        public const string WDoorOpenHands = "Squeeze the handle, swing\n<size=75%>with an empty hand</size>";   // both hands full
        public const string WDoorShut = "Swing it shut";
        public const string WMonitor = "Porch camera";
        public const string WMenu = "Menu: pause\n<size=75%>hold it: start over</size>";

        /// <summary>Ortiz in the room for the handover: a sober Innocent in his own (deputy) voice, captioned "Ortiz", who walks
        /// off the porch when he leaves (the visitors' WalksOff). Never a round's visitor: nothing judges him.</summary>
        static readonly ScenarioDef OrtizHandover = new()
        {
            Name = "Deputy Ortiz, the handover", Intent = Intent.Innocent, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.None,
            Identity = Identity.Officer, WhoItWas = "Deputy Ortiz, handing the witness over",
            VoicePrefix = "deputy", Speaker = Speaker, Sober = true, WalksOff = true,
        };

        /// <summary>v2 (Brett 0.1.57: the intro "seemed to be missing"): v1 was set by 0.1.55/0.1.56's monitor-text check, so the
        /// story handover never played on a headset that had done the old one.</summary>
        const string DoneKey = "his.marshal.weaponscheck.v2";
        const string CardKey = "briefing";
        const int CardStep = -1, LastStep = 10, PracticeRounds = 3;
        /// <summary>The step a skip's goodbye is on (<see cref="Step"/>): the labels and target are gone, Ortiz is on his way out.</summary>
        public const int LeaveStep = 11;
        const float CardSeconds = 2.5f, IntroSeconds = 3f, StillSeconds = 1.5f, PorchSeconds = 3f, MenuSeconds = 4f;
        const float SkipHoldSeconds = 1f, StillDrift = 0.06f, LevelPitch = 20f;
        /// <summary>After a skip, the most the night waits for the door shut behind Ortiz (s) before it goes on (the lead's A3).</summary>
        public const float SkipLeaveSeconds = 6f;
        /// <summary>The short handover's goodbye: the most it waits for the door shut behind him (s, from the goodbye line).</summary>
        public const float ShortLeaveSeconds = 14f;
        /// <summary>Step 9: the leaf open this far (°), or let go of, and Ortiz sets off through it.</summary>
        const float SendAngle = 30f;
        /// <summary>Step 7's way out (s with the knife out, and the blade tip's speed, m/s — a cut's, Knife.minHitSpeed): the
        /// cardboard may stand where the blade can't reach it (PracticeTarget never checks the real walls or furniture).</summary>
        public const float KnifeFallbackSeconds = 15f, KnifeSwingSpeed = 1.2f;
        /// <summary>The quiet between two lines (s); a line's length while its take isn't in yet (s per word, floor, cap).</summary>
        const float RadioGap = 0.35f, WordSeconds = 0.32f, MinLine = 1.0f, MaxLine = 3.2f;
        /// <summary>Frames without a step before the handover counts as orphaned (its coroutine was stopped): cleaned up.</summary>
        const int WatchdogFrames = 10;
        /// <summary>
        /// The sling's labels, in the body's chest frame (PlayerBody.Chest: the neck pivot, the body's yaw; m): the top of the
        /// right shoulder, where the slung long gun's stock waits for the hand (a hand's width above it: the arrow, as the eye sees
        /// it, points up and over the shoulder, not down at the hip — the 0.1.56 render), and the label's own spot in view, low
        /// and to the right in front of it (~0.43 m from the eyes, ~34° right, ~24° down) — the shoulder itself is out of view.
        /// </summary>
        static readonly Vector3 ShoulderLocal = new(0.17f, 0.08f, -0.03f), ShoulderLabelLocal = new(0.22f, -0.10f, 0.40f);

        // ───────────────────────────── public API ─────────────────────────────

        /// <summary>
        /// The handover should open this Marshal night: the drill is due (<see cref="DrillDue"/>), it was asked for
        /// (<see cref="RequestHandover"/>), or it's owed this session (the app's first Marshal night, or the first after switching
        /// into Marshal: <see cref="EnteredMarshal"/>). The Editor never reads the headset flag nor owes it on its own (as
        /// ScenarioDirector.SavedMode): Play Mode and the batch tests run it only when asked, so the Marshal smoke runs aren't held
        /// up by steps nobody can do.
        /// </summary>
        public static bool Due => DrillDue || s_handoverRequested || s_sessionOwed;

        /// <summary>The full weapons drill runs inside the handover: asked for (<see cref="Request"/>), or never done on this headset.</summary>
        public static bool DrillDue => s_requested || (!Application.isEditor && PlayerPrefs.GetInt(DoneKey, 0) == 0);

        /// <summary>The handover WITH the drill at the next Marshal night's start (the pause menu's "Weapons check" row; tests).</summary>
        public static void Request()
        {
            s_requested = true;
            Debug.Log("[HIS] Briefing: weapons check asked for");
        }

        /// <summary>Tests: the handover at the next Marshal night's start — the short one unless the drill is due.</summary>
        public static void RequestHandover()
        {
            s_handoverRequested = true;
            Debug.Log("[HIS] Briefing: the handover asked for");
        }

        /// <summary>
        /// ScenarioDirector.SetMode switched INTO Marshal mode: the next night opens with the handover (A1, Brett 0.1.57: Ortiz is
        /// the story's intro). On the headset; in the Editor only under <see cref="TestSessionRule"/>.
        /// </summary>
        public static void EnteredMarshal()
        {
            if (Application.isEditor && !TestSessionRule) return;
            s_sessionOwed = true;
            Debug.Log("[HIS] Briefing: into Marshal mode — the next night opens with the handover");
        }

        /// <summary>Tests: the session rule as on the headset (a switch into Marshal owes the handover), without the headset flag.</summary>
        public static bool TestSessionRule;
        /// <summary>The handover is owed this session (the app's first Marshal night, or the first after a switch into Marshal).</summary>
        public static bool SessionOwed => s_sessionOwed;

        /// <summary>
        /// Drop a running handover now, not marked done (ScenarioDirector.StopLoop may call it; else a room clear, or the watchdog
        /// within <see cref="WatchdogFrames"/> frames of its coroutine stopping, does the same).
        /// </summary>
        public static void Cancel() { if (Running) Abort("cancelled"); }

        /// <summary>A handover is running, its goodbye included (Ricky is practice-safe meanwhile: Intruder.PracticeHit).</summary>
        public static bool Running { get; private set; }
        /// <summary>The running (or the last) handover has the weapons drill in it (else the short one).</summary>
        public static bool Drill { get; private set; }
        /// <summary>The step on now: −1 the U.S. MARSHAL card, 0 the handover, 1–10 the drill, <see cref="LeaveStep"/> a skip's
        /// goodbye; −2 when none is running.</summary>
        public static int Step { get; private set; } = -2;
        /// <summary>The words of the line said last (Ortiz's, or Ricky's answer); null when none is running or nothing was said yet.</summary>
        public static string Line => s_line;
        /// <summary>The voice key of the line said last (null as <see cref="Line"/>).</summary>
        public static string LineKey => s_lineKey;
        /// <summary>The step's label at its thing now (null when none is up).</summary>
        public static string Label => LabelText(0);
        /// <summary>The second label (the handover's names: "Deputy Ortiz" over his head, then "Ricky" over his), null when none is up.</summary>
        public static string SideLabel => LabelText(1);
        /// <summary>The label object in <paramref name="slot"/> (0: the step's, 1: the handover's names), null when never made (tests).</summary>
        public static WorldLabel LabelObject(int slot) => slot >= 0 && slot < s_labels.Length ? s_labels[slot] : null;
        /// <summary>Labels up now (never more than two: the GPU budget).</summary>
        public static int LabelsUp => (LabelText(0) != null ? 1 : 0) + (LabelText(1) != null ? 1 : 0);
        /// <summary>The cardboard target (null when none is up: the short handover has none).</summary>
        public static PracticeTarget Target => s_target;
        /// <summary>Tonight's Ricky as the handover found him (null: none).</summary>
        public static IntruderT Ricky => s_ricky;
        /// <summary>Ortiz in the room for the running handover (null: none could be placed, he has gone, or none is running).</summary>
        public static IntruderT Ortiz => Running && s_ortiz != null ? s_ortiz : null;
        /// <summary>Where the last handover placed Ortiz (null: nowhere) and why (Intruder.HandoverStandSpot's reason), for tests and the log.</summary>
        public static Vector3? OrtizSpot { get; private set; }
        public static string OrtizSpotWhy { get; private set; }
        /// <summary>The player's eye when the last handover started (the stand spot and the target were placed from it).</summary>
        public static Vector3 EyeAtStart { get; private set; }
        /// <summary>Drills that reached the end / short handovers that did / handovers skipped with Y (tests).</summary>
        public static int Completed { get; private set; }
        public static int ShortDone { get; private set; }
        public static int Skips { get; private set; }
        /// <summary>The last handover that ended was skipped (hold Y).</summary>
        public static bool LastSkipped { get; private set; }

        /// <summary>
        /// Step 6 after its one shot, for <paramref name="gun"/>: Ortiz's line and the label at the gun. The AR-15 (magazine-fed)
        /// reloads ("Reload it." / "B: reload"); the pump shotgun pumps ("Pump it." / "Tap B: pump"); Y for the left hand.
        /// </summary>
        public static (string key, string label) LongGunAfterShot(Firearm gun, bool leftHand) =>
            IsRifle(gun) ? (KReloadIt, leftHand ? WReloadY : WReloadB) : (KPump, leftHand ? WPumpY : WPumpB);

        /// <summary>
        /// Tests: every drill step completes on its own after <see cref="TestStepSeconds"/> (no headset, no hands). The steps' own
        /// actions (practice ammo, the target, the re-measure, their lines and labels) still run. Step 9 with Ortiz in the room
        /// waits for him: he opens the door himself, walks out and pulls it to (about 5–8 s). The short handover is never cut.
        /// </summary>
        public static bool TestAutoComplete;
        public static float TestStepSeconds = 0.3f;
        /// <summary>Tests: as if Y were held for a second with an empty left hand.</summary>
        public static void TestSkip() { if (Running) s_skip = true; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Domain reload off: nothing from the last Play session (its objects are gone with the scene). On the headset the
            // app's first Marshal night owes the handover (A1); the Editor only when a test asks.
            GameSession.WorldCleared -= OnWorldCleared;
            s_requested = s_handoverRequested = s_fromRequest = s_skip = s_ended = false;
            s_sessionOwed = !Application.isEditor;
            Running = false; Drill = false; Step = -2; s_line = s_lineKey = null; s_target = null; s_ortiz = null;
            s_queue.Clear(); s_airUntil = 0f; s_ricky = null; s_door = null;
            for (int i = 0; i < s_labels.Length; i++) { s_labels[i] = null; s_tags[i] = Tag.None; }
            TestAutoComplete = false; TestStepSeconds = 0.3f; TestSessionRule = false;
            Completed = ShortDone = Skips = 0; LastSkipped = false; OrtizSpot = null; OrtizSpotWhy = null;
        }

        /// <summary>
        /// The handover, start to end (yield it from the night's coroutine; returns at once if one is already running). Skipped,
        /// finished or dropped, it leaves no target, no label and no card behind, and both guns loaded; Ortiz is out of the room
        /// (or on his way off the porch).
        /// </summary>
        public static IEnumerator Run()
        {
            if (Running) { Debug.LogWarning("[HIS] Briefing: a handover is already running — not started twice"); yield break; }
            int gen = Begin();
            int last = s_drill ? LastStep : 0;
            for (int step = CardStep; step <= last; step++)
            {
                if (!Live(gen)) yield break;
                Enter(step);
                while (true)
                {
                    Tick();
                    if (!Live(gen)) yield break;            // dropped: Abort has cleaned up
                    if (s_skip) break;
                    s_stepTime += Time.deltaTime;            // scaled: a pause freezes the handover
                    if (!s_stepSkipped) During(step);
                    if (Done(step)) break;
                    yield return null;
                }
                if (s_skip) break;
                if (step == CardStep) Notice.Hide(CardKey);
                else if (!s_stepSkipped) Debug.Log($"[HIS] Briefing: step {step} done after {s_stepTime:F1} s{DoneNote(step)}");
            }
            if (!Live(gen)) yield break;
            if (s_skip) { yield return SkipAndLeave(gen); yield break; }
            End(skipped: false, signOff: s_drill);
            Finish();
        }

        // ───────────────────────────── state ─────────────────────────────

        static bool s_requested, s_handoverRequested, s_sessionOwed, s_fromRequest, s_drill, s_ended;
        static bool s_skip, s_yArmed, s_stepSkipped, s_loadPending, s_da, s_modeCard;
        static bool s_lgDrawn, s_lgFired, s_menuSaid, s_monitor;
        static bool s_hadOrtiz, s_ortizStep9, s_ortizSent, s_lockSaid, s_leaveSaid, s_ortizReopened;
        static int s_gen, s_lastTick, s_shots0, s_reloads0, s_knife0, s_doorsOpened;
        static float s_stepTime, s_startTime, s_yHeld, s_stillFor, s_yell0, s_menuAt, s_knifeOutFor, s_leaveAt;
        static bool s_knifeSwing;
        static Vector3 s_stillFrom;
        static string s_line, s_lineKey;
        static PracticeTarget s_target;
        static Firearm s_pistol, s_longGun;
        static Knife s_knife;
        static PlayerBody s_body;
        static Transform s_head;
        static IntruderT s_ricky, s_ortiz;
        static VirtualDoor s_door;

        static bool Live(int gen) => Running && s_gen == gen;

        /// <summary>Ortiz is in the scene and on his feet (not gone, not removed).</summary>
        static bool OrtizHere => s_ortiz != null && s_ortiz.Current != IntruderT.State.Dead && s_ortiz.Current != IntruderT.State.Gone;
        /// <summary>Ortiz is through the doorway (or gone, or none was ever there).</summary>
        static bool OrtizOut => s_ortiz == null || s_ortiz.HandoverOut;

        static int Begin()
        {
            s_fromRequest = s_requested;
            s_drill = DrillDue;
            Drill = s_drill;
            // The handover is had: owed no more this session (the next night goes straight to the 1:12 card), asked for no more.
            s_requested = s_handoverRequested = s_sessionOwed = false;
            Running = true; s_ended = false;
            int gen = ++s_gen;
            Step = -2; s_line = s_lineKey = null; s_skip = false; s_yArmed = false; s_yHeld = 0f;
            s_queue.Clear(); s_airUntil = 0f;
            s_leaveSaid = s_ortizSent = s_lockSaid = s_ortizStep9 = s_ortizReopened = false; s_leaveAt = 0f;
            s_startTime = Time.time;
            s_lastTick = Time.frameCount;
            GameSession.WorldCleared -= OnWorldCleared;
            GameSession.WorldCleared += OnWorldCleared;
            var lo = Loadout.Instance;
            s_pistol = Weapon(lo, LoadoutSlot.Sidearm) as Firearm;
            s_longGun = Weapon(lo, LoadoutSlot.LongGun) as Firearm;
            s_knife = Weapon(lo, LoadoutSlot.Knife) as Knife;
            s_body = Object.FindAnyObjectByType<PlayerBody>();
            var cam = Camera.main;
            s_head = cam != null ? cam.transform : null;
            s_ricky = IntruderT.Protectee;
            if (s_ricky != null && s_ricky.Current == IntruderT.State.Dead) s_ricky = null;
            s_door = VirtualDoor.Instance != null ? VirtualDoor.Instance : VirtualDoor.All.Count > 0 ? VirtualDoor.All[0] : null;
            // Every door shut and whole, so Ortiz's way out and step 9's handle work (the night's own room clear usually did this already).
            foreach (var d in VirtualDoor.All) if (d != null && (d.Breached || d.IsOpen) && !d.HeldByPlayer) d.ResetDoor();
            var face = s_head != null ? s_head.position
                     : SafeSpot.Instance != null && SafeSpot.Instance.Spot.HasValue ? SafeSpot.Instance.Spot.Value : Vector3.zero;
            EyeAtStart = face;
            // Ortiz in the room first (his spot is picked against where the board would stand), then the board clear of him.
            s_ortiz = SpawnOrtiz(face);
            s_hadOrtiz = s_ortiz != null;
            s_target = s_drill ? PracticeTarget.Spawn(VirtualDoor.Instance, face, s_ricky != null ? s_ricky.transform.position : (Vector3?)null,
                                                      s_ortiz != null ? s_ortiz.transform.position : (Vector3?)null) : null;
            if (s_target != null) s_target.Watchdog = Watchdog;
            if (s_ortiz != null) s_ortiz.HandoverWatchdog = Watchdog;
            var mon = SecurityMonitor.Instance;
            // (The tests count "weapons check starts" / "short handover starts": keep the words.)
            Debug.Log($"[HIS] Briefing: {(s_drill ? $"weapons check starts ({(s_fromRequest ? "asked for" : "first on this headset")}, the handover in person)" : "short handover starts (no check due)")}" +
                      $"{(TestAutoComplete ? ", test auto-complete" : "")}: pistol {Name(s_pistol)}, long gun {Name(s_longGun)}" +
                      $"{(s_longGun != null ? (IsRifle(s_longGun) ? " (rifle)" : " (pump)") : "")}, knife {Name(s_knife)}, " +
                      $"target {(s_target != null ? "up" : s_drill ? "none (no door)" : "none (short)")}, {(s_ricky != null ? $"Ricky in his spot (#{s_ricky.Id})" : "no Ricky")}, " +
                      $"{(s_ortiz != null ? $"Ortiz in the room (#{s_ortiz.Id})" : "no Ortiz (the lines on the radio)")}, " +
                      $"{(mon != null && mon.Placed ? "a monitor" : "no monitor")}");
            return gen;
        }

        /// <summary>Ortiz in the room by the front door (Intruder.HandoverStandSpot, IntruderSpawner.SpawnHandoverDeputy); null: no spot, no spawner.</summary>
        static IntruderT SpawnOrtiz(Vector3 eye)
        {
            OrtizSpot = null; OrtizSpotWhy = null;
            var spawner = Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
            var door = VirtualDoor.Instance;
            if (spawner == null || door == null) { Debug.LogWarning($"[HIS] Ortiz: {(spawner == null ? "no spawner" : "no front door")} — the handover goes out on the radio"); return null; }
            var ricky = s_ricky != null ? s_ricky.transform.position : (Vector3?)null;
            if (!IntruderT.HandoverStandSpot(door, eye, ricky, s_drill, out var spot, out var why))
            {
                OrtizSpotWhy = why;
                Debug.LogWarning($"[HIS] Ortiz: {why} — the handover goes out on the radio");
                return null;
            }
            var facing = eye - spot; facing.y = 0f;
            var o = spawner.SpawnHandoverDeputy(OrtizHandover, spot, facing);
            if (o == null) return null;
            OrtizSpot = o.transform.position; OrtizSpotWhy = why;
            Debug.Log($"[HIS] Ortiz: stands at {o.transform.position:F2} — {why}");
            return o;
        }

        static WeaponBase Weapon(Loadout lo, LoadoutSlot kind)
        {
            if (lo == null) return null;
            int i = lo.SlotIndex(kind);
            return i >= 0 ? lo.SpawnedIn(i) : null;
        }

        static string Name(Object o) => o != null ? o.name : "none";

        /// <summary>A step starts: its line, its first label, its one-off actions (practice ammo, the re-measure, the counts it waits on).</summary>
        static void Enter(int step)
        {
            Step = step;
            s_stepTime = 0f;
            s_stepSkipped = false;
            SetLabel(0, step == 1 ? Tag.SkipY : Tag.None);   // (step 1 keeps the skip label the check's line put up)
            if (step != 0) SetLabel(1, Tag.None);
            switch (step)
            {
                case CardStep:
                    // SetMode's own mode card (the same words) went up with the switch into Marshal moments ago (up 2.5 s, the
                    // night starts 4 s on): no second one straight after it.
                    s_modeCard = Notice.Posted("mode") != null || ScenarioDirector.MarshalCardJustShown;
                    if (!s_modeCard) Notice.Show(CardKey, Card, 10);
                    Debug.Log($"[HIS] Briefing: U.S. MARSHAL card{(s_modeCard ? " (the mode switch's, just shown)" : "")}");
                    break;
                case 0:
                    // The handover, in person: his hello (his name over his head), Ricky named (Ricky's over his), his answer; then
                    // the check called (the drill), or the goodbye and out (the short one: he sets off as he says it).
                    Queue(KHello); Queue(KRicky); Queue(KBreathing);
                    if (s_ricky != null) Queue(IntruderT.RickyNewGuyKey, ricky: true);
                    if (s_drill) Queue(KCheck);              // (the skip label goes up with it: LabelsFor)
                    else Queue(KLeave, leave: true);
                    break;
                case 1:
                    if (s_body != null) s_body.Remeasure();
                    s_stillFor = 0f;
                    s_stillFrom = s_head != null ? s_head.position : Vector3.zero;
                    StepLine(KStand);
                    break;
                case 2: case 4: case 5:
                    if (s_pistol == null) { SkipStep(step, "no pistol"); break; }
                    if (step == 4 && !s_pistol.SlideLockedBack && !s_pistol.IsReloading)
                        s_pistol.LoadForPractice(0);         // reloaded early or still holding rounds: it IS empty for this line
                    StepLine(step == 2 ? KDraw : step == 4 ? KDry : KHolster);
                    break;
                case 3:
                    if (s_pistol == null) { SkipStep(step, "no pistol"); break; }
                    s_da = s_pistol.DoubleAction && !s_pistol.HammerCocked;
                    s_loadPending = !s_pistol.LoadForPractice(PracticeRounds); // (mid-reload: loaded once the reload is in)
                    s_shots0 = s_pistol.ShotsFired;
                    StepLine(KThree);
                    break;
                case 6:
                    if (s_longGun == null) { SkipStep(step, "no long gun"); break; }
                    s_longGun.LoadForPractice(s_longGun.FullLoad); // a shell to fire and one to pump in (a magazine to reload), whatever the last night left
                    s_shots0 = s_longGun.ShotsFired;
                    s_reloads0 = s_longGun.Reloads;
                    s_lgDrawn = s_lgFired = false;
                    StepLine(KLongGun);
                    break;
                case 7:
                    if (s_knife == null) { SkipStep(step, "no knife"); break; }
                    s_knife0 = s_target != null ? s_target.KnifeHits : 0;
                    s_knifeOutFor = 0f; s_knifeSwing = false;
                    StepLine(KKnife);
                    break;
                case 8:
                    if (CommandSystem.Instance == null) { SkipStep(step, "no yell system"); break; }
                    s_yell0 = Time.time;
                    StepLine(KYell);
                    break;
                case 9:
                    if (VirtualDoor.All.Count == 0 || s_door == null) { SkipStep(step, "no door"); break; }
                    s_doorsOpened = 0;
                    // "Get the door for me." — he leaves once you've opened it (no Ortiz: the old radio "Check the door. Open, then shut.").
                    s_ortizStep9 = OrtizHere;
                    s_ortizSent = s_lockSaid = false;
                    StepLine(KDoor);
                    break;
                case 10:
                    var mon = SecurityMonitor.Instance;
                    s_monitor = mon != null && mon.Placed;
                    s_menuAt = 0f;
                    s_menuSaid = !s_monitor;                 // no monitor: straight to the Menu line
                    StepLine(s_monitor ? KCamera : KMenu);
                    break;
            }
            PumpLine();                                      // on air this frame if nobody's talking
        }

        static void SkipStep(int step, string why)
        {
            s_stepSkipped = true;
            Debug.Log($"[HIS] Briefing: step {step} left out ({why})");
        }

        /// <summary>Every frame of a step: its label follows what's in the hands (a gun out of the hands sends it back to where
        /// to draw it), and the steps with two beats say their second line.</summary>
        static void During(int step)
        {
            switch (step)
            {
                case 2: SetLabel(0, Tag.Draw); break;
                case 3:
                    // (Decocked, the first pull is long: said until the first shot; every shot after it is single action.)
                    SetLabel(0, !s_pistol.IsHeld ? Tag.Draw : s_da && s_pistol.ShotsFired == s_shots0 ? Tag.TriggerDa : Tag.Trigger);
                    break;
                case 4: SetLabel(0, !s_pistol.IsHeld ? Tag.Draw : LeftHand(s_pistol) ? Tag.ReloadY : Tag.ReloadB); break;
                case 5: SetLabel(0, Tag.Holster); break;
                case 6: LongGun(); break;
                case 7: SetLabel(0, s_knife.IsHeld ? Tag.Knife : Tag.KnifeHip); break;
                case 8: SetLabel(0, Tag.Yell); break;
                case 9: DoorStep(); break;
                case 10:
                    if (!s_menuSaid)
                    {
                        SetLabel(0, Tag.Monitor);
                        if (s_stepTime >= PorchSeconds && !RadioBusy) { s_menuSaid = true; s_menuAt = s_stepTime; StepLine(KMenu); }
                    }
                    else SetLabel(0, Tag.Menu);
                    break;
            }
        }

        /// <summary>
        /// Step 9 with Ortiz in the room: the handle's label until the door is open (≥ <see cref="SendAngle"/>°, or let go of
        /// ajar), then he sets off through it (no label while he walks); through the doorway, "Lock it behind me." and "Swing it
        /// shut" at the handle. Without him: the old open-then-shut labels. (Test auto-complete: he opens it himself, and pulls it to.)
        /// </summary>
        static void DoorStep()
        {
            if (!s_ortizStep9)
            {
                SetLabel(0, s_doorsOpened != 0 ? Tag.DoorShut : HandsFull() ? Tag.DoorOpenHands : Tag.DoorOpen);
                return;
            }
            if (!s_ortizSent)
            {
                bool open = s_door.IsOpen && (s_door.OpenAngle >= SendAngle || !s_door.HeldByPlayer);
                if (open || TestAutoComplete || !OrtizHere)
                {
                    s_ortizSent = true;
                    if (OrtizHere) s_ortiz.HandoverLeave(openIfShut: true, pullShut: TestAutoComplete);
                    Debug.Log($"[HIS] Briefing: step 9: {(open ? $"the door open ({s_door.OpenAngle:F0}°)" : TestAutoComplete ? "test auto-complete" : "Ortiz gone")} — Ortiz walks out");
                }
            }
            if (s_ortizSent && OrtizHere && s_ortiz.HandoverOpenedDoor) s_ortizReopened = true;
            if (s_ortizSent && !s_lockSaid && OrtizOut) { s_lockSaid = true; StepLine(KLock); }
            SetLabel(0, s_lockSaid ? Tag.DoorShut : s_ortizSent ? Tag.None : HandsFull() ? Tag.DoorOpenHands : Tag.DoorOpen);
        }

        /// <summary>Step 6: the shoulder until it's drawn; "One round." and the target; after the shot, the pump or the reload.</summary>
        static void LongGun()
        {
            if (!s_longGun.IsHeld) { SetLabel(0, Tag.Shoulder); return; }
            if (!s_lgDrawn) { s_lgDrawn = true; StepLine(KOneRound); }
            if (s_longGun.ShotsFired <= s_shots0) { SetLabel(0, Tag.Trigger); return; }
            bool rifle = Rifle, left = LeftHand(s_longGun);
            if (!s_lgFired) { s_lgFired = true; StepLine(LongGunAfterShot(s_longGun, left).key); }
            SetLabel(0, rifle ? (left ? Tag.RifleY : Tag.RifleB) : (left ? Tag.PumpY : Tag.PumpB));
        }

        static bool LeftHand(WeaponBase w) => w.Hand != null && w.Hand.IsLeft;

        /// <summary>Both hands hold something (the door's lever only takes an empty hand: HandController).</summary>
        static bool HandsFull()
        {
            var cs = CommandSystem.Instance;
            return cs != null && cs.Left != null && cs.Right != null && cs.Left.Held != null && cs.Right.Held != null;
        }

        /// <summary>The long gun is magazine-fed (the AR-15), not the tube-fed pump shotgun: step 6 teaches B as its reload.</summary>
        static bool Rifle => IsRifle(s_longGun);
        static bool IsRifle(Firearm g) => g != null && !g.ShellByShell;

        /// <summary>The player did the step's thing (or its time is up).</summary>
        static bool Done(int step)
        {
            if (s_stepSkipped) return true;
            // (Auto-complete never cuts the short handover, nor Ortiz's walk out at step 9: he does those himself.)
            if (TestAutoComplete && !(step == 0 && !s_drill) && !(step == 9 && s_ortizStep9)) return s_stepTime >= TestStepSeconds;
            switch (step)
            {
                case CardStep:
                    // Ours for 2.5 s; after SetMode's (same words), on as soon as it has gone (at most 3 s).
                    return s_modeCard ? Notice.Posted("mode") == null || s_stepTime >= IntroSeconds : s_stepTime >= CardSeconds;
                case 0:
                    if (s_drill) return s_stepTime >= IntroSeconds && !RadioBusy;   // the handover said, every line of it
                    // The short one: his goodbye said, out through the doorway, the door shut behind him (or its time is up).
                    if (!s_leaveSaid) return false;
                    if (Time.time - s_leaveAt >= ShortLeaveSeconds) { Debug.Log("[HIS] Briefing: the short handover's goodbye ran out of time — on"); return true; }
                    return OrtizOut && (!s_hadOrtiz || s_door == null || !s_door.IsOpen) && !RadioBusy;
                case 1: return Stood();
                case 2: return s_pistol.IsHeld;
                case 3:
                    if (s_loadPending && s_pistol.LoadForPractice(PracticeRounds)) { s_loadPending = false; s_shots0 = s_pistol.ShotsFired; }
                    return !s_loadPending && (s_pistol.SlideLockedBack || s_pistol.ShotsFired - s_shots0 >= PracticeRounds);
                case 4: return !s_pistol.SlideLockedBack && s_pistol.RoundChambered && !s_pistol.IsReloading;
                case 5: return !s_pistol.IsHeld && s_pistol.Grabbable.CurrentHolster != null;
                case 6:
                    // Shotgun: one shot, then one pump. Rifle: one shot, then one B reload (it still has 29 in: the reload is the lesson).
                    if (Rifle) return s_longGun.ShotsFired > s_shots0 && s_longGun.Reloads > s_reloads0 && !s_longGun.IsReloading && s_longGun.RoundChambered;
                    return s_longGun.ShotsFired > s_shots0 && s_longGun.RoundChambered;
                case 7:
                    if (s_target == null) return s_knife.IsHeld;
                    if (s_target.KnifeHits > s_knife0) return true;
                    // The way out: a while with the knife out and the board still untouched, a fast swing anywhere counts.
                    if (s_knife.IsHeld) s_knifeOutFor += Time.deltaTime;
                    return s_knifeSwing = s_knifeOutFor >= KnifeFallbackSeconds && s_knife.IsHeld && s_knife.TipSpeed >= KnifeSwingSpeed;
                case 8: return CommandSystem.Instance == null || CommandSystem.Instance.LastTime > s_yell0;
                case 9:
                    if (!s_ortizStep9) return DoorWorked();
                    if (TestAutoComplete && s_stepTime > 25f) { Debug.LogWarning("[HIS] Briefing: step 9 (auto): Ortiz never got out — on"); return true; }
                    return s_lockSaid && OrtizOut && !s_door.IsOpen;      // out through it, and latched shut behind him
                default: return s_menuSaid && !RadioBusy && s_stepTime - s_menuAt >= MenuSeconds;
            }
        }

        /// <summary>
        /// Step 1: the head held still (within 6 cm) and level (±20°) for 1.5 s, and the height measure in. Each time the head
        /// moves, the measure starts over (PlayerBody.Remeasure), so it reads the height he settled at, not the one he left.
        /// </summary>
        static bool Stood()
        {
            if (s_head == null) return s_stepTime >= StillSeconds;
            var pos = s_head.position;
            var fwd = s_head.forward;
            float pitch = Mathf.Asin(Mathf.Clamp(-fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            if ((pos - s_stillFrom).sqrMagnitude > StillDrift * StillDrift || Mathf.Abs(pitch) > LevelPitch)
            {
                s_stillFrom = pos;
                s_stillFor = 0f;
                if (s_body != null) s_body.Remeasure();
                return false;
            }
            s_stillFor += Time.deltaTime;
            return s_stillFor >= StillSeconds && (s_body == null || !s_body.Measuring);
        }

        /// <summary>Step 9 without Ortiz: a door opened by hand (VirtualDoor.OpenedByPlayer) has latched shut again.</summary>
        static bool DoorWorked()
        {
            var doors = VirtualDoor.All;
            for (int i = 0; i < doors.Count && i < 31; i++)
            {
                var d = doors[i];
                if (d == null) continue;
                if (d.OpenedByPlayer) s_doorsOpened |= 1 << i;
                else if ((s_doorsOpened & (1 << i)) != 0 && !d.IsOpen) return true;
            }
            return false;
        }

        static string DoneNote(int step)
        {
            switch (step)
            {
                case 0:
                    string ricky = s_ricky != null ? $"Ricky {(s_ricky.Current == IntruderT.State.Dead ? "dead" : "in his spot")}, {s_ricky.ProtecteePracticeHits} practice hit(s) on him" : "no Ricky";
                    string ortiz = !s_hadOrtiz ? "no Ortiz (on the radio)" : s_drill ? $"Ortiz in the room, {(s_ortiz != null ? s_ortiz.HandoverPracticeHits : 0)} practice hit(s) on him"
                                 : $"the short handover: Ortiz {(OrtizOut ? "out" : "still in the doorway")}, the door {(s_door != null && s_door.IsOpen ? "still open" : "shut behind him")}";
                    return $" ({ricky}; {ortiz})";
                case 1:
                    return s_body != null ? $" (eyes at {s_body.MeasuredEye:F2} m, {PlayerHeight.StatureCm(s_body.MeasuredEye)} cm" +
                                            $"{(PlayerHeight.Auto ? "" : $"; the set {PlayerHeight.Cm} cm places the belt")})" : "";
                case 2: return s_pistol != null && s_pistol.Hand != null ? $" ({s_pistol.name} in the {(s_pistol.Hand.IsLeft ? "left" : "right")} hand)" : "";
                case 3: return s_target != null ? $" ({s_target.RoundHits} on the target)" : "";
                case 6: return s_longGun != null ? $" ({(Rifle ? "rifle: one shot, one reload" : "shotgun: one shot, one pump")})" : "";
                case 7: return s_knifeSwing ? $" (a fast swing after {s_knifeOutFor:F0} s with the knife out: the board out of reach)"
                             : s_target != null ? $" ({(s_target.LastBladeWasStab ? "a stab" : "a slash")})" : "";
                case 8: return CommandSystem.Instance != null ? $" ({CommandSystem.Instance.Last})" : "";
                case 9: return s_ortizStep9 ? $" (Ortiz out through the front door{(s_ortizReopened ? ", he opened it himself" : "")}, shut behind him)" : " (opened by hand, shut again)";
                default: return "";
            }
        }

        // ───────────────────────────── the lines ─────────────────────────────
        // One voice at a time: a line waits for the one on air (+ a short gap). A step's own line replaces anything not yet
        // said (a step the player already finished doesn't get its line late); the handover's lines queue up in order. Ortiz's
        // lines are in person (from his head) while he's in the room; vo.deputy.radio.* go over the radio (step 10, the sign-off).

        struct Beat { public string Key; public bool Ricky, Leave; }
        static readonly List<Beat> s_queue = new(8);
        /// <summary>Time.time the line on air ends (+ the gap). Game time: a pause freezes it with the audio.</summary>
        static float s_airUntil;

        static bool RadioBusy => s_queue.Count > 0 || Time.time < s_airUntil;

        /// <summary>A step's line: next on air, replacing any line not yet said.</summary>
        static void StepLine(string key) { s_queue.Clear(); s_queue.Add(new Beat { Key = key }); }

        static void Queue(string key, bool ricky = false, bool leave = false) => s_queue.Add(new Beat { Key = key, Ricky = ricky, Leave = leave });

        /// <summary>The next line on air once the last has finished.</summary>
        static void PumpLine()
        {
            if (s_queue.Count == 0 || Time.time < s_airUntil) return;
            var beat = s_queue[0];
            s_queue.RemoveAt(0);
            OnAir(beat);
        }

        /// <summary>A line on air now: Ortiz in person or on the radio (<see cref="SayLine"/>), or Ricky from his spot; its labels.</summary>
        static void OnAir(Beat beat)
        {
            float len;
            string said = beat.Key, how = "", who = Speaker;
            if (beat.Ricky)
            {
                if (s_ricky == null || s_ricky.Current == IntruderT.State.Dead) return; // (no Ricky: his line is left out)
                len = s_ricky.ProtecteeSay(beat.Key);
                who = "Ricky";
            }
            else len = SayLine(beat.Key, out said, out how);
            // The short handover's goodbye: he turns for the door as he says it (he opens it himself, pulls it to behind him).
            if (beat.Leave)
            {
                s_leaveSaid = true; s_leaveAt = Time.time;
                if (OrtizHere) s_ortiz.HandoverLeave(openIfShut: true, pullShut: true);
            }
            LabelsFor(beat.Key);
            if (said == null) { Debug.Log($"[HIS] Briefing: step {Step}: \"{VoiceLines.Text(beat.Key)}\" left out (no Ortiz in the room, no radio line for it)"); return; }
            var text = VoiceLines.Text(said);
            if (len <= 0f) len = Estimate(text);             // no take yet: about as long as the words (the caption still shows)
            s_airUntil = Time.time + len + RadioGap;
            s_line = text; s_lineKey = said;
            Debug.Log($"[HIS] Briefing: step {Step}, {who}{how}: \"{text}\"");
        }

        /// <summary>
        /// Ortiz says <paramref name="key"/>: a vo.deputy.radio.* line over the radio (RadioVoice: squelch, the radio band, the
        /// caption); any other in person from his head while he's in the room (Intruder.HandoverSay); without him, its radio twin
        /// (0.1.57's line) if it has one, else nothing (<paramref name="said"/> null). Returns the line's length (0: no take yet).
        /// </summary>
        static float SayLine(string key, out string said, out string how)
        {
            said = key; how = "";
            if (key.StartsWith(RadioPrefix, System.StringComparison.Ordinal)) { how = " (radio)"; return RadioVoice.Transmit(key, Speaker); }
            if (OrtizHere) { how = " (in person)"; return s_ortiz.HandoverSay(key); }
            var twin = RadioTwin(key);
            if (twin == null) { said = null; return 0f; }
            said = twin; how = " (radio)";
            return RadioVoice.Transmit(twin, Speaker);
        }

        /// <summary>The 0.1.57 radio line for an in-person one (no Ortiz in the room): hello → "Marshal, it's Ortiz. I'm out front.",
        /// the short goodbye → "Good. See you at two.", each step its own; none for "Lock it behind me." and the skip's.</summary>
        static string RadioTwin(string key)
        {
            if (key == KHello) return KOutFront;
            if (key == KLeave) return KDone;
            if (key == KLock || key == KSkipLine || key == null || !key.StartsWith(HandoverPrefix, System.StringComparison.Ordinal)) return null;
            var twin = RadioPrefix + key.Substring(HandoverPrefix.Length);
            return VoiceLines.Text(twin) != null ? twin : null;
        }

        /// <summary>The handover's labels ride its lines: Ortiz's name over his head with his hello, Ricky's while he's named; the
        /// skip offer once the check is called (never before: the story's first beat is not an offer to skip it).</summary>
        static void LabelsFor(string key)
        {
            if (key == KHello) SetLabel(1, OrtizHere ? Tag.Ortiz : Tag.None);
            else if (key == KRicky) SetLabel(1, s_ricky != null ? Tag.Ricky : Tag.None);
            else if (key == KCheck) { SetLabel(1, Tag.None); SetLabel(0, Tag.SkipY); }
            else if (key == KLeave) SetLabel(1, Tag.None);
        }

        static float Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return MinLine;
            int words = 1;
            foreach (char c in text) if (c == ' ') words++;
            return Mathf.Clamp(0.4f + words * WordSeconds, MinLine, MaxLine);
        }

        // ───────────────────────────── the labels ─────────────────────────────
        // Two slots (0: the step's, 1: the handover's names), each a WorldLabel made on first use and destroyed with the check.
        // A slot is only re-shown when what it should say changes, so nothing is allocated per frame.

        enum Tag { None, SkipY, Ortiz, Ricky, Draw, Trigger, TriggerDa, ReloadB, ReloadY, Holster, Shoulder, PumpB, PumpY, RifleB, RifleY, KnifeHip, Knife, Yell, DoorOpen, DoorOpenHands, DoorShut, Monitor, Menu }

        static readonly WorldLabel[] s_labels = new WorldLabel[2];
        static readonly Tag[] s_tags = new Tag[2];

        static string LabelText(int slot) => s_labels[slot] != null && s_labels[slot].Visible ? s_labels[slot].Text : null;

        static void SetLabel(int slot, Tag tag)
        {
            if (s_tags[slot] == tag) return;
            s_tags[slot] = tag;
            if (tag == Tag.None) { if (s_labels[slot] != null) s_labels[slot].Hide(); return; }
            if (!Words(tag, out var text, out var thing, out var side, out var place))
            {
                if (s_labels[slot] != null) s_labels[slot].Hide();
                Debug.Log($"[HIS] Briefing: step {Step}: no label for {tag} (nothing to point at)");
                return;
            }
            if (s_labels[slot] == null) s_labels[slot] = WorldLabel.Create(slot == 0 ? "(weapons check)" : "(handover)");
            s_labels[slot].Show(text, thing, side, place);
            Debug.Log($"[HIS] Briefing: step {Step} label \"{text.Replace("\n", " / ")}\" at {tag}");
        }

        /// <summary>What a tag says, what its arrow points at, and which side of it the label sits (false: nothing to point at).</summary>
        static bool Words(Tag tag, out string text, out System.Func<Vector3> thing, out WorldLabel.Side side, out System.Func<Vector3> place)
        {
            text = null; thing = null; side = WorldLabel.Side.Above; place = null;
            var cs = CommandSystem.Instance;
            switch (tag)
            {
                case Tag.SkipY:
                    if (cs == null || cs.Left == null) return false;
                    text = WSkip; thing = LeftHandPoint; side = WorldLabel.Side.Left; return true;
                case Tag.Ortiz:
                    if (!OrtizHere) return false;
                    text = WOrtiz; thing = OrtizPoint; return true;
                case Tag.Ricky:
                    if (s_ricky == null) return false;
                    text = WRicky; thing = RickyPoint; return true;
                case Tag.Draw:
                    if (s_body == null && s_pistol == null) return false;
                    text = WGrip; thing = RightHipPoint; side = WorldLabel.Side.Right; return true;
                case Tag.Holster:
                    if (s_body == null) return false;
                    text = WHolster; thing = RightHolsterPoint; side = WorldLabel.Side.Right; return true;
                case Tag.Trigger: case Tag.TriggerDa: case Tag.Knife:
                    if (s_target == null) return false;
                    text = tag == Tag.Trigger ? WTrigger : tag == Tag.TriggerDa ? WTriggerDa : WKnife; thing = TargetPoint; return true;
                case Tag.ReloadB: case Tag.ReloadY:
                    if (s_pistol == null) return false;
                    // Beside the gun on the side of the hand that holds it (B on the right controller, Y on the left): above it,
                    // the plate sank into the slide (the 0.1.56 render).
                    text = tag == Tag.ReloadB ? WReloadB : WReloadY; thing = PistolPoint;
                    side = tag == Tag.ReloadB ? WorldLabel.Side.Right : WorldLabel.Side.Left; return true;
                case Tag.Shoulder:
                    if (s_body == null) return false;
                    text = WShoulder; thing = ShoulderPoint; place = ShoulderLabelPoint; return true;
                case Tag.PumpB: case Tag.PumpY: case Tag.RifleB: case Tag.RifleY:
                    if (s_longGun == null) return false;
                    text = tag == Tag.PumpB ? WPumpB : tag == Tag.PumpY ? WPumpY : tag == Tag.RifleB ? WReloadB : WReloadY;
                    thing = LongGunPoint;
                    side = tag is Tag.PumpB or Tag.RifleB ? WorldLabel.Side.Right : WorldLabel.Side.Left; return true;   // (as the pistol's)
                case Tag.KnifeHip:
                    if (s_body == null && s_knife == null) return false;
                    text = WGrip; thing = LeftHipPoint; side = WorldLabel.Side.Left; return true;
                case Tag.Yell:
                    if (cs == null || cs.Right == null) return false;
                    text = WYell; thing = RightHandPoint; side = WorldLabel.Side.Right; return true;
                case Tag.DoorOpen: case Tag.DoorOpenHands: case Tag.DoorShut:
                    if (s_door == null) return false;
                    text = tag == Tag.DoorOpen ? WDoorOpen : tag == Tag.DoorOpenHands ? WDoorOpenHands : WDoorShut; thing = HandlePoint; return true;
                case Tag.Monitor:
                    if (SecurityMonitor.Instance == null) return false;
                    text = WMonitor; thing = MonitorPoint; return true;
                case Tag.Menu:
                    if (cs == null || cs.Left == null) return false;
                    text = WMenu; thing = LeftHandPoint; side = WorldLabel.Side.Left; return true;
            }
            return false;
        }

        // The things (world points, read every frame; each null-safe: a thing gone mid-step leaves the label where it was last).
        static Vector3 s_lastThing;
        static Vector3 Keep(Vector3 p) => s_lastThing = p;

        /// <summary>The holstered sidearm's grip (where the hand closes), else the right hip slot.</summary>
        static Vector3 RightHipPoint() =>
            Keep(s_pistol != null && !s_pistol.IsHeld && s_pistol.Grabbable.CurrentHolster != null ? s_pistol.Grabbable.GripPoint.position
                 : s_body != null ? s_body.RightHip.transform.position : s_lastThing);
        static Vector3 RightHolsterPoint() => Keep(s_body != null ? s_body.RightHip.transform.position : s_lastThing);
        /// <summary>
        /// The sheathed knife's handle, at its far end from the blade (beside the grip's middle, the plate sat under the pommel in
        /// the 0.1.56 render), else the left hip slot.
        /// </summary>
        static Vector3 LeftHipPoint()
        {
            if (s_knife != null && !s_knife.IsHeld && s_knife.Grabbable.CurrentHolster != null)
            {
                var grip = s_knife.Grabbable.GripPoint.position;
                var away = s_knife.BladeBase != null ? grip - s_knife.BladeBase.position : Vector3.zero;
                return Keep(away.sqrMagnitude > 1e-6f && away.sqrMagnitude < 0.04f ? grip + away.normalized * PommelReach : grip);
            }
            return Keep(s_body != null ? s_body.LeftHip.transform.position : s_lastThing);
        }
        /// <summary>From the knife's grip point to the end of its handle (m).</summary>
        const float PommelReach = 0.05f;
        /// <summary>Just over the cardboard head: the label sits above the board, never over its centre.</summary>
        static Vector3 TargetPoint() => Keep(s_target != null ? s_target.LabelPoint : s_lastThing);
        static Vector3 PistolPoint() => Keep(s_pistol != null ? s_pistol.transform.position : s_lastThing);
        /// <summary>The long gun at the hand that holds it (B / Y is on that controller).</summary>
        static Vector3 LongGunPoint() => Keep(s_longGun != null ? s_longGun.Grabbable.GripPoint.position : s_lastThing);
        static Vector3 ShoulderPoint() => Keep(s_body != null ? s_body.Chest.TransformPoint(ShoulderLocal) : s_lastThing);
        static Vector3 ShoulderLabelPoint() => s_body != null ? s_body.Chest.TransformPoint(ShoulderLabelLocal) : s_lastThing;
        static Vector3 LeftHandPoint() => Keep(HandPoint(true));
        static Vector3 RightHandPoint() => Keep(HandPoint(false));
        static Vector3 HandPoint(bool left)
        {
            var cs = CommandSystem.Instance;
            var h = cs != null ? (left ? cs.Left : cs.Right) : null;
            return h != null ? h.transform.position : s_lastThing;
        }
        /// <summary>The door's handle, a few cm into the room (the label never sinks into the leaf).</summary>
        static Vector3 HandlePoint() => Keep(s_door != null ? s_door.HandleWorld + s_door.Inward * 0.04f : s_lastThing);
        /// <summary>The top of the monitor's bezel, a cm off the screen.</summary>
        static Vector3 MonitorPoint()
        {
            var mon = SecurityMonitor.Instance;
            return Keep(mon != null ? mon.ScreenCenter + Vector3.up * (mon.BezelSize.y * 0.5f) + mon.Facing * 0.01f : s_lastThing);
        }
        /// <summary>Just over Ricky's head (crouched).</summary>
        static Vector3 RickyPoint() => Keep(s_ricky != null ? s_ricky.ProtecteeHeadPoint + Vector3.up * 0.12f : s_lastThing);
        /// <summary>Just over Ortiz's head (standing).</summary>
        static Vector3 OrtizPoint() => Keep(s_ortiz != null ? s_ortiz.HandoverHeadPoint + Vector3.up * 0.15f : s_lastThing);

        /// <summary>Every frame: the watchdog's stamp, the lines, and the Y hold that skips the whole handover.</summary>
        static void Tick()
        {
            s_lastTick = Time.frameCount;
            if (s_skip) return;
            PumpLine();
            var cs = CommandSystem.Instance;
            var left = cs != null ? cs.Left : null;
            // HandController freezes its buttons while the gate is shut (pause, panels): no hold counts then.
            if (left == null || !InputGate.GameplayActive || Time.deltaTime <= 0f) { s_yHeld = 0f; return; }
            bool y = left.SecondaryButton;
            // A Y still down from before (it cut the last night's breather short) never counts: seen up first.
            if (!s_yArmed) { s_yArmed = !y; return; }
            // (A knife in it: Y does nothing with a knife, and step 7 leaves it there — a hold still skips.)
            bool empty = (left.Held == null || (s_knife != null && left.Held == s_knife.Grabbable)) && left.Supporting == null && !left.OnDoor;
            s_yHeld = y && empty ? s_yHeld + Time.deltaTime : 0f;
            if (s_yHeld < SkipHoldSeconds) return;
            s_skip = true;
            left.HapticLight();
        }

        // ───────────────────────────── the end ─────────────────────────────

        /// <summary>
        /// Skipped (hold Y): done now (the labels, the target, the card and the practice ammo go), "Alright. Lock it behind me."
        /// and Ortiz leaves as in the short handover (out, the door pulled to behind him, off the porch). The night goes on once
        /// the door is shut behind him, or <see cref="SkipLeaveSeconds"/> s after the skip at most — never waiting for the porch.
        /// </summary>
        static IEnumerator SkipAndLeave(int gen)
        {
            End(skipped: true, signOff: false);
            Step = LeaveStep;
            s_queue.Clear(); s_airUntil = 0f;
            bool here = OrtizHere;
            if (here)
            {
                // (Already on his way out — step 9, or the short one's goodbye said: no second goodbye; he pulls it to now.)
                if (!s_ortiz.HandoverLeaving) { OnAir(new Beat { Key = KSkipLine }); s_ortiz.HandoverLeave(openIfShut: true, pullShut: true); }
                else s_ortiz.HandoverPullBehind();
            }
            float from = Time.time;
            while (Live(gen))
            {
                s_lastTick = Time.frameCount;                // (the watchdog: still stepped)
                bool shut = !here || s_door == null || !s_door.IsOpen;
                if (OrtizOut && shut) break;
                if (Time.time - from >= SkipLeaveSeconds) { Debug.Log($"[HIS] Briefing: after the skip, {SkipLeaveSeconds:F0} s and Ortiz {(OrtizOut ? "out, the door still open" : "not out yet")} — the night goes on"); break; }
                yield return null;
            }
            if (!Live(gen)) yield break;
            Debug.Log($"[HIS] Briefing: after the skip, the night goes on {Time.time - from:F1} s later{(here ? "" : " (no Ortiz)")}");
            Finish();
        }

        /// <summary>
        /// Finished or skipped: a drill is done on this headset; the clean-up (no label, target or card; both guns full);
        /// a finished drill, Ortiz signs off on the radio ("Good. See you at two.": he's out on the porch by now). Still running
        /// (<see cref="Running"/>) until <see cref="Finish"/>.
        /// </summary>
        static void End(bool skipped, bool signOff)
        {
            float took = Time.time - s_startTime;
            if (skipped) Debug.Log($"[HIS] Briefing: skipped (hold Y) at step {Step} after {took:F0} s ({(s_drill ? "the weapons check" : "the short handover")})");
            else if (s_drill) Debug.Log($"[HIS] Briefing: weapons check done in {took:F0} s" +
                                        (s_target != null ? $" ({s_target.RoundHits} rounds and {s_target.KnifeHits} blade(s) on the target)" : "") +
                                        (s_ricky != null ? $"; Ricky unharmed, {s_ricky.ProtecteePracticeHits} practice hit(s) on him" : ""));
            else Debug.Log($"[HIS] Briefing: short handover done in {took:F0} s");
            if (s_drill && !Application.isEditor) { PlayerPrefs.SetInt(DoneKey, 1); PlayerPrefs.Save(); }
            LastSkipped = skipped;
            if (skipped) Skips++; else if (s_drill) Completed++; else ShortDone++;
            s_ended = true;
            Cleanup();
            // (After the clean-up: nothing waits on it. It plays over the night's 1:12 card.)
            if (signOff) RadioVoice.Transmit(KDone, Speaker);
        }

        /// <summary>The room was cleared mid-handover (hold-Menu restart, mode switch, preview): drop it; a drill is still owed.</summary>
        static void OnWorldCleared() { if (Running) Abort("the room was cleared"); }

        /// <summary>The target's / Ortiz's frame check: the night's coroutine stopped stepping the handover (StopLoop and friends).</summary>
        static void Watchdog() { if (Running && Time.frameCount - s_lastTick > WatchdogFrames) Abort("its coroutine stopped"); }

        static void Abort(string why)
        {
            // A skip's goodbye cut short: it already counted (the clear that cut it takes Ortiz with the rest).
            if (s_ended) { Debug.Log($"[HIS] Briefing: the goodbye cut short ({why})"); Finish(); return; }
            // Step 10 is the last line and teaches hold-Menu: everything has been shown by then, so a drop there (the player
            // trying the restart it just taught) counts as done — else the reset would run the whole check again, every time.
            // (No sign-off: the room was just cleared under it.)
            if (s_drill && Step == LastStep) { Debug.Log($"[HIS] Briefing: dropped on the last line ({why}); counts as done"); End(skipped: false, signOff: false); Finish(); return; }
            Debug.Log($"[HIS] Briefing: dropped at step {Step} ({why}); {(s_drill ? "not marked done" : "the short handover")}");
            if (s_fromRequest) s_requested = true;           // asked for and not had: the next Marshal night runs it
            Cleanup();
            // Ortiz goes too (a room clear destroys him anyway; a stopped coroutine doesn't).
            if (s_ortiz != null) { Debug.Log("[HIS] Ortiz: removed with the dropped handover"); Object.Destroy(s_ortiz.gameObject); }
            s_ortiz = null;
            Finish();
        }

        /// <summary>No target, no label, no card; the shut doors whole again; both guns full. (Ortiz and the running state: <see cref="Finish"/>.)</summary>
        static void Cleanup()
        {
            for (int i = 0; i < s_labels.Length; i++)
            {
                if (s_labels[i] != null) Object.Destroy(s_labels[i].gameObject);
                s_labels[i] = null;
                s_tags[i] = Tag.None;
            }
            Notice.Hide(CardKey);
            if (s_target != null) { s_target.Watchdog = null; s_target.Remove(); }
            s_target = null;
            // A practice round that missed went through a shut leaf: the night's door starts whole (the holes go, porch side too).
            foreach (var d in VirtualDoor.All) if (d != null && !d.IsOpen && !d.HeldByPlayer) d.ResetDoor();
            // Practice ammo out, a full gun back (refused mid-reload or mid-pump: those end full / loaded anyway).
            if (s_pistol != null) s_pistol.LoadForPractice(s_pistol.FullLoad);
            if (s_longGun != null) s_longGun.LoadForPractice(s_longGun.FullLoad);
        }

        /// <summary>
        /// The handover is over: no longer running (Ricky is himself again from here: Intruder.PracticeHit), nothing more is said.
        /// Ortiz, if still in the room (a drill whose door step was left out), goes now by himself and pulls the door to; on his
        /// way off the porch he finishes it (the night's clear keeps him: Intruder.HandoverWalkingOff).
        /// </summary>
        static void Finish()
        {
            Running = false;
            Step = -2;
            s_line = s_lineKey = null;
            s_queue.Clear();
            s_airUntil = 0f;
            GameSession.WorldCleared -= OnWorldCleared;
            for (int i = 0; i < s_labels.Length; i++)
            {
                if (s_labels[i] != null) Object.Destroy(s_labels[i].gameObject);
                s_labels[i] = null;
                s_tags[i] = Tag.None;
            }
            if (s_ortiz != null)
            {
                s_ortiz.HandoverWatchdog = null;
                if (!s_ortiz.HandoverLeaving) { Debug.Log("[HIS] Ortiz: still in the room as the handover ends — he goes now"); s_ortiz.HandoverLeave(openIfShut: true, pullShut: true); }
            }
            s_ortiz = null;
            s_pistol = s_longGun = null; s_knife = null; s_body = null; s_head = null; s_ricky = null; s_door = null;
        }
    }
}
