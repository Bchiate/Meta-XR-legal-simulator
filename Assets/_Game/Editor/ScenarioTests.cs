using System;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// End-to-end scenario checks: each deck entry played with a scripted player (yells injected into the director, shots
/// fired through the same Hitbox → Intruder path the guns use), then the judge's call compared with the expected one.
///   In Play Mode: HIS > Debug > Run Scenario Tests.
///   Headless (no XR, no screen needed): scripts/unity-batch.sh ScenarioTests.RunHeadless  → [HIS] SCENARIO lines.
/// </summary>
public static partial class ScenarioTests
{
    struct Case { public string Name; public Tier Expect; public Func<Task> Act; public string Reason; public bool Doorway; public Action Setup; public bool Crawl; } // Reason: the banner must say this; Setup: before it's dealt (.Entries.cs); Crawl: RunCrawlHeadless

    static bool _running;
    static int _pass, _fail;

    [MenuItem("HIS/Debug/Run Scenario Tests")]
    public static async void RunInPlayMode()
    {
        if (!EditorApplication.isPlaying || _running) { Debug.LogWarning("[HIS] SCENARIO tests need Play Mode"); return; }
        await RunAll();
    }

    /// <summary>Batch entry: open the main scene, enter Play Mode, run every case, exit with the failure count.</summary>
    /// <summary>Batch entry: only the legs-out crawl cases (knife and empty-handed: clawing, grabbing, out of reach).</summary>
    public static void RunCrawlHeadless() { s_crawlOnly = s_doorwayOnly = true; RunHeadless(); }
    static bool s_crawlOnly;

    static System.Action s_beforePlay;
    static bool s_m9Only;

    /// <summary>Batch entry: just the M9 checks (DA/SA, slide lock, the animated reload).</summary>
    public static void RunM9Headless() { s_m9Only = true; RunHeadless(); }

    public static void RunHeadless()
    {
        HISEditorMenu.OpenMain();
        s_beforePlay?.Invoke(); // a sub-suite's scene tweak (the two-door room), unsaved, before Play Mode
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 2400f; // (+7 gunman cases, slow cores)
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] SCENARIO watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += async s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            await RunAll();
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task RunAll()
    {
        _running = true; _pass = _fail = 0;
        try
        {
            HIS.Core.PlayerState.GodMode = true;
            ScenarioDirector.WaitForForced = true;
            ScenarioDirector.VariedEntries = false;   // the fixed 0.1.17 door unless a case opts in (ScenarioTests.Entries.cs)
            PoliceResponse.Enabled = false;            // the verdict right away, as these cases expect (a police case opts in)
            if (!await WaitFor(() => ScenarioDirector.Instance != null, 30)) return;
            if (s_badShowOnly) { if (await ConfirmDoorForDoorwayRun()) await BadShowChecks(); return; } // ScenarioTests.BadShow.cs
            if (s_m9Only) { await M9DoubleAction(); return; }
            if (!s_doorwayOnly)
            {
                await SafetyChecks(); // credits, door confirm, safe-spot ring, near-surface warning (ScenarioTests.Safety.cs)
                await LoadoutSwap();
                await M9DoubleAction();
                await BadShowChecks(); // the pause menu's bad-call show picker + preview, every mode, skip (ScenarioTests.BadShow.cs)
            }
            else if (!await ConfirmDoorForDoorwayRun()) return;
            if (s_twoDoorsOnly || s_standoffTwoDoors) await TwoDoorChecks(); // the second door, before any case (ScenarioTests.TwoDoors.cs)
            var cases = new[]
            {
                new Case { Name = "Home invader", Expect = Tier.Good, Reason = "coming at you", Doorway = true, Act = async () =>
                    { var w = WatchDoorway(); await WaitFor(() => State() is not (IntruderT.State.Outside or IntruderT.State.BreakIn or IntruderT.State.Entering), 40);
                      w.Stop(); w.Report("doorway in (kicked in)", 0); await WaitFor(() => Dist() < 2.6f, 40);
                      for (int i = 0; i < 3; i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); } } },
                new Case { Name = "Knife attacker", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => Dist() < 3f, 40); Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine); } },
                new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = true, Act = async () =>
                    { var w = WatchDoorway(); await WaitFor(() => State() == IntruderT.State.Idle, 40); w.Report("doorway in (burglar)", 0);
                      await Wait(1f); Yell(Command.GetOut);
                      await WaitFor(() => State() == IntruderT.State.Gone, 30); w.Stop(); w.Report("doorway out (runs off)", 1); } },
                new Case { Name = "Burglar with a knife", Expect = Tier.Bad, Act = async () =>
                    { await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f); Yell(Command.GetOut);
                      await WaitFor(() => State() == IntruderT.State.Fleeing, 10); await Wait(0.8f); // past the 1 s grace from the turn
                      for (int i = 0; i < 3; i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.25f); } } },
                new Case { Name = "Surrenders", Expect = Tier.Perfect, Act = async () =>
                    { await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f); Yell(Command.ShowHands);
                      await Wait(2f); Yell(Command.GetDown); } },
                new Case { Name = "Surrenders (knife)", Expect = Tier.Bad, Act = async () =>
                    { await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f); Yell(Command.DropWeapon);
                      await WaitFor(() => State() == IntruderT.State.Complying, 10); await Wait(1.5f);
                      for (int i = 0; i < 3; i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.25f); } } },
                new Case { Name = "Wrong house", Expect = Tier.Bad, Doorway = true, Act = async () => // (in the doorway set: the next case starts on its body)
                    { await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f); Yell(Command.WhoIsThere);
                      await WaitFor(() => State() == IntruderT.State.Complying, 10); await Wait(1.5f);
                      for (int i = 0; i < 3; i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.25f); } } },
                new Case { Name = "Wrong house", Expect = Tier.Perfect, Doorway = true, Act = async () =>
                    { var w = WatchDoorway(); await WaitFor(() => State() == IntruderT.State.Idle, 40); w.Report("doorway in (opened, drunk)", 0);
                      await Wait(1f); Yell(Command.WhoIsThere);
                      await WaitFor(() => State() == IntruderT.State.Complying, 10); await Wait(1.5f); Yell(Command.GetOut);
                      await WaitFor(() => State() == IntruderT.State.Gone, 40); w.Stop(); w.Report("doorway out (walks home)", 1); } },
                // Brett 0.1.10 (#2): legs shot out, he keeps coming with the knife and claws at you from the floor — killing him
                // there is a good call. His body must die face down on the floor (ProneDeath from the crawl, logged), not freeze.
                new Case { Crawl = true, Name = "Knife attacker", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => Alive() && Dist() < 3.5f, 40); if (!await LegsOut()) return;
                      bool reached = await WaitFor(() => State() == IntruderT.State.Attacking, 25);
                      if (!reached) { ExtraFail("floor attack", $"never reached you crawling ({Live()?.Summary})"); return; }
                      bool lunged = await WaitForLog("floor lunge", 4f);
                      if (!lunged) ExtraFail("floor attack", "no floor lunge within reach"); else ExtraPass("floor attack", "knife man lunges from the floor");
                      var facts = Live().Facts;
                      if (!(facts.Down && facts.Attacking)) ExtraFail("floor attack facts", $"Down {facts.Down} Attacking {facts.Attacking}");
                      await KillAndCheckBody("crawl death (floor attack)"); } },
                // Legs out close, knife still in hand, crawling at you from inside reach + 0.5 m: still a threat (good).
                new Case { Crawl = true, Name = "Knife attacker", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => Alive() && Dist() < 2.8f, 40); if (!await LegsOut()) return;
                      await WaitFor(() => Live() != null && Live().Facts.Advancing, 6); await Wait(1.2f); // past the 1 s grace, still closing
                      var f = Live().Facts;
                      Debug.Log($"[HIS] SCENARIO crawling knife man: dist {f.Distance:F2} advancing {f.Advancing} attacking {f.Attacking} down {f.Down}");
                      await KillAndCheckBody("crawl death (crawling at you)"); } },
                // Brett 0.1.10 (#1): the unarmed home invader with his legs shot out is no longer a threat crawling at you from
                // out of reach: finishing him is a bad call (the leg shots themselves were good). Killed 1.5 s after the fall (past
                // the 1 s grace), before the crawl can bring him within grabbing reach.
                new Case { Crawl = true, Name = "Home invader", Expect = Tier.Bad, Act = async () =>
                    { await WaitFor(() => Alive() && Dist() < 3.2f, 40); if (!await LegsOut()) return; await Wait(1.5f);
                      var f = Live().Facts;
                      if (f.Attacking) ExtraFail("unarmed crawl out of reach", $"already grabbing at {f.Distance:F2} m");
                      await KillAndCheckBody("crawl death (unarmed)"); } },
                // Brett 0.1.19: "when they get to you they just look up at you". Empty-handed, legs out, he crawls into reach and
                // grabs at your ankles (a fist hit on your leg) — attacking you, so shooting him then is a good call.
                new Case { Crawl = true, Name = "Home invader", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => Alive() && Dist() < 3f, 40); if (!await LegsOut()) return;
                      bool reached = await WaitFor(() => State() == IntruderT.State.Attacking, 25);
                      if (!reached) { ExtraFail("floor grab", $"never reached you crawling ({Live()?.Summary})"); return; }
                      if (!await WaitForLog("floor lunge (grab", 4f)) { ExtraFail("floor grab", "no grab within reach"); return; }
                      bool hit = await WaitForLog("FloorGrab (lands)", 2f);
                      if (!hit) ExtraFail("floor grab lands", "the grab never hit your legs"); else ExtraPass("floor grab lands", "grabbed at your legs from the floor");
                      var facts = Live().Facts;
                      if (!(facts.Down && facts.Attacking)) ExtraFail("floor grab facts", $"Down {facts.Down} Attacking {facts.Attacking}");
                      await KillAndCheckBody("crawl death (floor grab)"); } },
                // Brett 0.1.10 (#4): shot blind through the door while the knife attacker was breaking it in (the pounding before
                // the kicks), then killed charging in: GOOD, close call (fired blind), not "fired through a closed door".
                new Case { Name = "Knife attacker", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => State() == IntruderT.State.BreakIn, 40); await Wait(0.8f);
                      int done = ScenarioDirector.Instance.Completed;
                      Shoot(1, 20f, HumanBodyBones.Hips);
                      var wounds = Live().GetComponent<HIS.Intruder.WoundSystem>();
                      if (wounds != null && wounds.ShownBodyWounds > 0) ExtraFail("door wound hidden", "a wound shows on him behind the closed door");
                      await WaitFor(() => Dist() < 3f, 40);
                      // The round through the door is on his body once he's in (hidden while he was out there).
                      if (wounds != null && wounds.ShownBodyWounds == 0) ExtraFail("door wound shows", "came in with no wound from the door round");
                      else ExtraPass("door wound shows", $"{wounds?.ShownBodyWounds} wound(s) on him as he comes in");
                      Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine);
                      await WaitFor(() => ScenarioDirector.Instance.Completed > done, 60);
                      var v = ScenarioDirector.Instance.LastVerdict.GetValueOrDefault();
                      if (v.Tier == Tier.Good && !v.CloseCall) ExtraFail("door shot mid-break-in", "expected a close call, got a clean good: " + v.Reason);
                      else if (v.Tier == Tier.Good) ExtraPass("door shot mid-break-in", "close call — " + v.Reason); } },
                // A burst through the door at a burglar kicking it in: the first round makes him give up and turn away, the next
                // ones land a split second later on a man the player can't see stop — same reaction grace as everywhere (GOOD,
                // close: fired blind), not BAD for the follow-up rounds.
                new Case { Name = "Burglar who runs", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => State() == IntruderT.State.BreakIn, 40); await Wait(0.8f); ClearLog();
                      Shoot(1, 20f, HumanBodyBones.Hips);
                      if (!await WaitForLog("shot through the door, runs off", 3f)) { ExtraFail("door burst grace", "never gave up forcing"); return; }
                      await Wait(0.3f); Shoot(1, 20f, HumanBodyBones.Chest);
                      var v = await VerdictWhenDone();
                      if (v.Tier == Tier.Good && v.CloseCall) ExtraPass("door burst grace", v.Reason); else ExtraFail("door burst grace", $"{v.Tier} — {v.Reason}"); } },
                // Killed through the door before any kick landed: he drops behind it, out of sight — the door holds, and his
                // body stops taking rounds (no phantom hits on an unseen corpse), no pool out there.
                new Case { Name = "Burglar who runs", Expect = Tier.Good, Act = async () =>
                    { await WaitFor(() => State() == IntruderT.State.BreakIn, 40); await Wait(0.8f);
                      var i = Live(); Shoot(1, 40f, HumanBodyBones.Head);
                      await Wait(0.5f);
                      var door = HIS.Room.VirtualDoor.Instance;
                      bool hitboxesOff = Array.TrueForAll(i.GetComponentsInChildren<Collider>(), c => !c.enabled);
                      string info = $"dead {i.Current == IntruderT.State.Dead}, hidden {i.BehindDoor}, door open {door != null && door.IsOpen}, hitboxes off {hitboxesOff}";
                      if (i.Current == IntruderT.State.Dead && i.BehindDoor && hitboxesOff && (door == null || !door.IsOpen)) ExtraPass("killed behind the door", info);
                      else ExtraFail("killed behind the door", info); } },
            };
            bool ringChecked = false;
            foreach (var c in WithOpenDoorCases(WithStandoffCases(WithLethalCases(WithTwoDoorCases(WithEntryCases(WithWoundCases(WithDoorBloodCases(WithGunmanCases(cases)))))))))   // + the gunman (ScenarioTests.Gunman.cs), blood under the door (.DoorBlood.cs), your wounds (.Wounds.cs), opening the door yourself (.OpenDoor.cs)
            {
                if (!EditorApplication.isPlaying) break;
                if (s_crawlOnly ? !c.Crawl : s_doorwayOnly && !c.Doorway) continue;
                var dir = ScenarioDirector.Instance;
                int done = dir.Completed;
                ScenarioDirector.VariedEntries = false; ScenarioDirector.ForcedStyle = null; IntruderT.TestDoorRoll = null;
                IntruderT.TestBurglarChoice = null; IntruderT.TestNoticeDelay = null; ScenarioDirector.ForcedDoor = null;
                IntruderT.StreetApproach = false; IntruderT.TestSwitchDoor = null; IntruderT.TestRingBell = null; // (a case opts in)
                HIS.Intruder.Hitbox.LethalRounds = false; // these cases are built on graded hit points (a case opts in)
                PoliceResponse.Enabled = false; PoliceResponse.TestStance = null; PoliceResponse.TestFired = false; // (a standoff case opts in)
                c.Setup?.Invoke();
                ScenarioDirector.ForcedNext = c.Name;
                Debug.Log($"[HIS] SCENARIO start: {c.Name} (expect {c.Expect})");
                // The forced scenario starts after the current one finishes: wait for its intruder.
                if (!await WaitFor(() => dir.Record != null && dir.Record.Def.Name == c.Name && dir.Intruder != null && dir.Completed == done, 120))
                { Fail(c, "never started"); continue; }
                if (!ringChecked) { ringChecked = true; RingAtScenarioStart(); }
                await c.Act();
                if (!await WaitFor(() => dir.Completed > done, 90)) { Fail(c, "never ended"); continue; }
                var v = dir.LastVerdict.GetValueOrDefault();
                string fit = await BannerFit();
                if (fit != null) Fail(c, "banner text off its plate: " + fit);
                else if (c.Reason != null && (v.Reason == null || !v.Reason.Contains(c.Reason))) Fail(c, $"banner reason should say '{c.Reason}', got: {v.Reason}");
                else if (v.Tier == c.Expect) { _pass++; Debug.Log($"[HIS] SCENARIO PASS {c.Name}: {v.Tier}{(v.CloseCall ? " (close)" : "")} — {v.Reason}"); }
                else Fail(c, $"got {v.Tier} — {v.Reason}");
                // Verdict and banner are checked: skip the rest of the show like a player's B/Y (skipDelay instead of the
                // show + betweenDelay) — about 5 s a case, minutes a run. The show itself is covered by RunBadShowHeadless.
                // Only while this show is up, so a request can't linger and cut the next case's banner before BannerFit.
                var shown = HIS.Outcome.OutcomeShow.Instance;
                if (shown != null && shown.BannerUp) dir.RequestSkip();
            }
        }
        catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
        finally
        {
            ScenarioDirector.WaitForForced = false;
            ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = null;   // back to how the game plays
            ResetGunHooks();
            IntruderT.FrameProbe = null; // doorway watches (ScenarioTests.Doorway.cs)
            Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed");
            _running = false;
        }
    }

    /// <summary>
    /// The loadout menu's swap path, before any incident runs: the picked sidearm sits in the right hip; a new pick waits
    /// for the next room clear, then the old gun is gone and the new one is holstered; the old pick comes back the same
    /// way. A test-only option reuses the current prefab, so this checks the swap path alone (M9DoubleAction picks the M9).
    /// </summary>
    static async Task LoadoutSwap()
    {
        const string name = "Loadout swap";
        var lo = HIS.Weapons.Loadout.Instance;
        var body = UnityEngine.Object.FindAnyObjectByType<HIS.Weapons.PlayerBody>();
        int s = lo != null ? lo.SlotIndex(HIS.Weapons.LoadoutSlot.Sidearm) : -1;
        string Check(bool ok, string why) => ok ? null : why;
        string err = s < 0 || body == null ? "no loadout / sidearm slot / body" : null;
        if (err == null && !await WaitFor(() => lo.SpawnedIn(s) != null, 10)) err = "no sidearm spawned";
        var opts = err == null ? lo.Slots[s].Options : null;
        if (err == null)
        {
            int orig = lo.Picked(s);
            var first = lo.SpawnedIn(s);
            Debug.Log($"[HIS] SCENARIO loadout: sidearm {first.name}; options " +
                      string.Join(", ", Array.ConvertAll(opts, o => o.Label + (o.Available ? "" : " (coming soon)"))));
            err = Check(body.RightHip.Occupant == first.Grabbable, "sidearm not in the right hip");
            if (err == null)
            {
                lo.Slots[s].Options = new System.Collections.Generic.List<HIS.Weapons.Loadout.Option>(opts)
                    { new() { Id = "test.copy", Label = "Test copy", Prefab = opts[orig].Prefab } }.ToArray();
                lo.Refresh();
                lo.Pick(s, "test.copy");
                err = Check(lo.Pending(s) && lo.SpawnedIn(s) == first, "pick applied before the room was cleared");
            }
            if (err == null)
            {
                HIS.Core.GameSession.ClearWorld(resetHealth: false);
                await Wait(0.2f);
                var next = lo.SpawnedIn(s);
                err = Check(first == null && next != null && !lo.Pending(s) && body.RightHip.Occupant == next.Grabbable,
                            "swap on clear failed (old gone / new holstered)");
            }
            lo.Pick(s, opts[orig].Id); // restore the saved pick and the real option list whatever happened
            HIS.Core.GameSession.ClearWorld(resetHealth: false);
            await Wait(0.2f);
            lo.Slots[s].Options = opts;
            lo.Refresh();
            if (err == null)
                err = Check(lo.Picked(s) == orig && !lo.Pending(s) && lo.SpawnedIn(s) != null && body.RightHip.Occupant == lo.SpawnedIn(s).Grabbable,
                            "swap back failed");
        }
        if (err == null) { _pass++; Debug.Log($"[HIS] SCENARIO PASS {name}"); }
        else { _fail++; Debug.LogError($"[HIS] SCENARIO FAIL {name}: {err}"); }
    }

    /// <summary>
    /// The M9 from the loadout, fired through Firearm's DA/SA trigger (the same entry the hand feeds its analog pull into):
    /// picked + room cleared → it's the holstered sidearm, decocked. A 0.6 pull doesn't fire the first (double-action) shot
    /// but rolls the hammer part way back; 0.9 fires it and the slide leaves the hammer cocked; then 0.6 fires the
    /// single-action follow-up. Decocker → double action again. Emptied → slide locked, trigger dead; B reload → chambered,
    /// still cocked. The old pick is restored.
    /// </summary>
    static async Task M9DoubleAction()
    {
        const string name = "M9 DA/SA";
        var lo = HIS.Weapons.Loadout.Instance;
        var body = UnityEngine.Object.FindAnyObjectByType<HIS.Weapons.PlayerBody>();
        int s = lo != null ? lo.SlotIndex(HIS.Weapons.LoadoutSlot.Sidearm) : -1;
        string err = s < 0 || body == null ? "no loadout / sidearm slot / body" : null;
        string Check(bool ok, string why) => err ?? (ok ? null : why);
        var opts = err == null ? lo.Slots[s].Options : null;
        int orig = err == null ? lo.Picked(s) : -1;
        int m9 = err == null ? Array.FindIndex(opts, o => o.Id == "m9") : -1;
        err = Check(m9 >= 0 && opts[m9].Available, "M9 not selectable (listed as coming soon?)");
        if (err == null)
        {
            lo.Pick(s, "m9");
            HIS.Core.GameSession.ClearWorld(resetHealth: false);
            await Wait(0.3f);
            var gun = lo.SpawnedIn(s) as HIS.Weapons.Firearm;
            err = Check(gun != null && gun.DisplayName == "M9" && gun.DoubleAction && body.RightHip.Occupant == gun.Grabbable, "M9 not the holstered sidearm after the pick");
            if (err == null)
            {
                int shots = gun.ShotsFired, mag = gun.RoundsInMag;
                err = Check(!gun.HammerCocked && gun.RoundChambered, "M9 should start decocked with a round chambered");
                gun.SqueezeTrigger(0.6f, null); await Wait(0.15f);
                float travel = gun.HammerTravel;
                err = Check(gun.ShotsFired == shots && !gun.HammerCocked, "a 0.6 double-action pull fired");
                err = Check(travel > 0.5f && travel < 0.9f, $"hammer should roll back part way with the DA pull (travel {travel:F2})");
                gun.SqueezeTrigger(0.9f, null); await Wait(0.05f);
                err = Check(gun.ShotsFired == shots + 1 && gun.RoundsInMag == mag - 1 && gun.HammerCocked, "0.9 DA pull didn't fire / hammer not recocked");
                gun.SqueezeTrigger(0f, null); await Wait(0.25f);
                err = Check(gun.HammerTravel > 0.99f, "hammer not drawn cocked after the shot");
                gun.SqueezeTrigger(0.6f, null);
                err = Check(gun.ShotsFired == shots + 2 && gun.HammerCocked, "0.6 single-action pull didn't fire");
                gun.SqueezeTrigger(0f, null); await Wait(0.25f);
                gun.Decock(null); await Wait(0.3f);
                gun.SqueezeTrigger(0.6f, null);
                err = Check(!gun.HammerCocked && gun.ShotsFired == shots + 2, "decocker didn't bring back double action");
                gun.SqueezeTrigger(0f, null);
                Debug.Log($"[HIS] SCENARIO m9: DA broke at 0.9 (0.6 rolled the hammer {travel:P0} back), SA at 0.6; decocker → DA; {gun.Status}");
                for (int i = 0; i < 40 && err == null && !gun.SlideLockedBack; i++)
                {
                    await Wait(0.2f); gun.SqueezeTrigger(0.9f, null); await Wait(0.02f); gun.SqueezeTrigger(0f, null);
                }
                int fired = gun.ShotsFired;
                err = Check(gun.SlideLockedBack && gun.HammerCocked && !gun.RoundChambered, "emptied M9 should lock back with the hammer cocked");
                await Wait(0.2f); gun.SqueezeTrigger(0.9f, null); gun.SqueezeTrigger(0f, null);
                err = Check(gun.ShotsFired == fired, "fired with the slide locked back");
                // research/20 option 1: the mag slides out and drops (a loose copy that falls), a fresh one seats, the slide slams home.
                Transform magPart = null;
                foreach (var t in gun.GetComponentsInChildren<Transform>(true)) if (t.name == "Magazine") { magPart = t; break; }
                var magRest = magPart != null ? magPart.localPosition : Vector3.zero;
                gun.Reload(null);
                await Wait(0.3f);
                var drop = UnityEngine.Object.FindAnyObjectByType<HIS.Weapons.DroppedMag>();
                float dropY0 = drop != null ? drop.transform.position.y : 0f;
                err = Check(magPart != null && !magPart.gameObject.activeSelf && drop != null, $"magPart out: magazine {(magPart == null ? "missing" : magPart.gameObject.activeSelf ? "still in the grip" : "out")}, dropped copy {(drop != null ? "yes" : "none")}");
                await Wait(0.7f);
                err = Check(magPart != null && magPart.gameObject.activeSelf && (magPart.localPosition - magRest).magnitude < 0.005f, "the fresh magPart isn't seated at 1.0 s");
                err = Check(drop == null || drop.transform.position.y < dropY0 - 0.2f, $"the dropped magPart didn't fall ({dropY0:F2} → {(drop != null ? drop.transform.position.y : 0f):F2})");
                await Wait(1f);
                err = Check(gun.RoundChambered && !gun.SlideLockedBack && gun.HammerCocked && gun.RoundsInMag == 14, $"reload didn't chamber ({gun.Status})");
                Debug.Log($"[HIS] SCENARIO m9: {fired - shots} rounds to slide lock, reload → {gun.Status}");
            }
        }
        if (orig >= 0)
        {
            lo.Pick(s, opts[orig].Id); HIS.Core.GameSession.ClearWorld(resetHealth: false); await Wait(0.3f);
            var back = lo.SpawnedIn(s);
            // The M9 is the default sidearm since 0.1.15 (Loadout): back to it is one M9 in the holster, else no M9 left anywhere.
            bool origM9 = opts[orig].Id == "m9";
            int m9s = Array.FindAll(UnityEngine.Object.FindObjectsByType<HIS.Weapons.Firearm>(FindObjectsInactive.Include), f => f.DisplayName == "M9").Length;
            err = Check(back != null && (back.DisplayName == "M9") == origM9 && body.RightHip.Occupant == back.Grabbable && m9s == (origM9 ? 1 : 0),
                        $"swap back to {opts[orig].Label} failed (holstered: {(back != null ? back.DisplayName : "none")}, {m9s} M9(s) in the world)");
        }
        if (err == null) { _pass++; Debug.Log($"[HIS] SCENARIO PASS {name}"); }
        else { _fail++; Debug.LogError($"[HIS] SCENARIO FAIL {name}: {err}"); }
    }

    // ── Brett 0.1.10 helpers: legs out, the kill on the floor and what his body does after it.

    static readonly System.Collections.Generic.List<string> s_intruderLog = new();
    static void OnIntruderLog(string msg, string stack, LogType t) { if (msg.StartsWith("[HIS] Intruder")) lock (s_intruderLog) s_intruderLog.Add(msg); }
    static async Task<bool> WaitForLog(string fragment, float timeout)
    {
        Application.logMessageReceived -= OnIntruderLog; Application.logMessageReceived += OnIntruderLog;
        return await WaitFor(() => { lock (s_intruderLog) return s_intruderLog.Exists(l => l.Contains(fragment)); }, timeout);
    }
    static void ClearLog() { Application.logMessageReceived -= OnIntruderLog; Application.logMessageReceived += OnIntruderLog; lock (s_intruderLog) s_intruderLog.Clear(); }

    /// <summary>Both thighs in one frame (two ~25-damage rounds: legs out at 45) until he's crawling (up to 3 tries).</summary>
    static async Task<bool> LegsOut()
    {
        ClearLog();
        for (int i = 0; i < 3 && Live() != null && Live().CurrentGait != HIS.Intruder.IntruderAnimator.Gait.Crawl; i++)
        { Shoot(2, 36f, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg); await Wait(0.3f); }
        bool ok = Live() != null && Live().CurrentGait == HIS.Intruder.IntruderAnimator.Gait.Crawl;
        if (!ok) ExtraFail("legs out", $"not crawling after leg shots ({Live()?.Summary})");
        return ok;
    }

    /// <summary>Head shots until he's dead, then: a death kind logged for the crawl path, a face-down corpse on the floor, asleep.</summary>
    static async Task KillAndCheckBody(string what)
    {
        var i = Live();
        if (i == null) { ExtraFail(what, "no intruder"); return; }
        ClearLog();
        for (int k = 0; k < 4 && i.Current != IntruderT.State.Dead; k++) { Shoot(1, 40f, HumanBodyBones.Head); await Wait(0.1f); }
        if (i.Current != IntruderT.State.Dead) { ExtraFail(what, "head shots didn't kill him: " + i.Summary); return; }
        bool logged = await WaitForLog("death Prone (from the Crawl", 3f);
        var anim = i.GetComponentInChildren<Animator>();
        await WaitFor(() => i == null || anim == null || !anim.enabled, 4f);
        if (i == null || anim == null) { ExtraFail(what, "body gone before it settled"); return; }
        var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        var head = anim.GetBoneTransform(HumanBodyBones.Head);
        float hipsY = hips.position.y - i.transform.position.y, headY = head.position.y - i.transform.position.y;
        string info = $"logged {logged}, asleep {!anim.enabled}, hips {hipsY:F2} m, head {headY:F2} m";
        if (logged && !anim.enabled && hipsY < 0.25f && headY < 0.3f) ExtraPass(what, info);
        else { string tail; lock (s_intruderLog) tail = string.Join(" | ", s_intruderLog); ExtraFail(what, info + " — " + tail); }
    }

    /// <summary>This scenario's intruder is in (the director can still point at the last scenario's corpse for a moment).</summary>
    static bool Alive() => Live() != null && State() is not (IntruderT.State.Dead or IntruderT.State.Gone);

    static void ExtraPass(string what, string info) { _pass++; Debug.Log($"[HIS] SCENARIO PASS {what} ({info})"); }
    static void ExtraFail(string what, string why) { _fail++; Debug.LogError($"[HIS] SCENARIO FAIL {what}: {why}"); }

    // The verdict banner (shown a moment into the outcome show) must hold all of its text; null = fits or never shown.
    static async Task<string> BannerFit()
    {
        HIS.Outcome.VerdictBanner banner = null;
        if (!await WaitFor(() => (banner = UnityEngine.Object.FindAnyObjectByType<HIS.Outcome.VerdictBanner>()) != null && banner.Visible, 10)) return null;
        await Wait(0.6f); // scale-in done
        bool fits = banner.DebugFits(out string info);
        Debug.Log($"[HIS] SCENARIO banner: {info}");
        return fits ? null : info;
    }

    static void Fail(Case c, string why) { _fail++; Debug.LogError($"[HIS] SCENARIO FAIL {c.Name}: expected {c.Expect}, {why}"); }

    /// <summary>The verdict of the scenario now running, once it ends.</summary>
    static async Task<Verdict> VerdictWhenDone()
    {
        int done = ScenarioDirector.Instance.Completed;
        await WaitFor(() => ScenarioDirector.Instance.Completed > done, 60);
        return ScenarioDirector.Instance.LastVerdict.GetValueOrDefault();
    }

    static IntruderT Live() => ScenarioDirector.Instance != null ? ScenarioDirector.Instance.Intruder : null;
    static IntruderT.State State() { var i = Live(); return i != null ? i.Current : IntruderT.State.Outside; }
    static float Dist() { var i = Live(); return i != null && i.Current is not IntruderT.State.Outside and not IntruderT.State.BreakIn ? i.Facts.Distance : 99f; }
    static void Yell(Command c) => ScenarioDirector.Instance.Inject(c);
    static void Shoot(int pellets, float dmg, params HumanBodyBones[] bones) => IntruderDebugMenu.Shoot(pellets, dmg, bones);

    static async Task Wait(float s) => await Task.Delay(Mathf.RoundToInt(s * 1000));

    static async Task<bool> WaitFor(Func<bool> cond, float timeout)
    {
        for (float t = 0; t < timeout; t += 0.1f)
        {
            if (!EditorApplication.isPlaying) return false;
            if (cond()) return true;
            await Wait(0.1f);
        }
        return false;
    }
}
