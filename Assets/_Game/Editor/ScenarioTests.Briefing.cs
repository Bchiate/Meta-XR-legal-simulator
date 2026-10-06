using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The Marshal weapons check (Scenario/MarshalBriefing; research/26 §2, research/27; Brett 2026-10-03), just enough to know it
/// works: the steps run on their own (<see cref="MarshalBriefing.TestAutoComplete"/>: no headset, no hands), so this checks the
/// plumbing, not the player's moves. Into Marshal mode with the check asked for: the card and steps 0–10 in order, a label at
/// the thing for each step that has one (the handover's "Deputy Ortiz" over him at step 0 — Brett 0.1.57: he's in the room,
/// in person — never on the monitor, at most two up), Ortiz in the room until he walks out at step 9 (on his own under the
/// test hook: he opens the door himself), the cardboard target up for it and taking a round (a hole, never judged), then gone
/// with the labels, Ortiz out of the room, both guns full. The pause menu's "Weapons check" row: listed in Marshal mode only,
/// and it starts the night over with the check; held Y (TestSkip) skips it (his goodbye: the night goes on within 6 s). A
/// mode switch mid-check drops it cleanly (Ortiz goes with the room) and it's still owed.
///   Just this: scripts/unity-batch.sh ScenarioTests.RunBriefingHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunBriefingHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 300f;
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] SCENARIO watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += async s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            _running = true; _pass = _fail = 0;
            try
            {
                HIS.Core.PlayerState.GodMode = true;     // (a night that gets as far as its waves can't end the run)
                ScenarioDirector.WaitForForced = true;   // no scenario rounds before the switch
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await BriefingChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally
            {
                HIS.Core.PlayerState.GodMode = false;
                _running = false;
                Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed");
            }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task BriefingChecks()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("briefing: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        var pause = HIS.Core.GamePause.Instance;

        // The pause menu's row: not in Scenarios.
        if (pause != null && pause.DebugMenuText().Contains("Weapons check")) ExtraFail("briefing: no Weapons check row in Scenarios", "listed");
        else ExtraPass("briefing: no Weapons check row in Scenarios", pause != null ? "not listed" : "no pause menu");

        var steps = new List<int>();
        var stepRx = new Regex(@"^\[HIS\] Briefing: step (\d+) (done|left out)");
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            var m = stepRx.Match(msg);
            if (m.Success) lock (steps) steps.Add(int.Parse(m.Groups[1].Value));
        };
        Application.logMessageReceived += onLog;
        try
        {
            // 1. The first check, played through.
            MarshalBriefing.TestAutoComplete = true;
            MarshalBriefing.Request();
            int completed = MarshalBriefing.Completed;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            if (!await WaitFor(() => MarshalBriefing.Running, 30))
            { ExtraFail("briefing: the Marshal night starts with the check", $"never started ({dir.Status}; the night must yield MarshalBriefing.Run() when Due)"); return; }
            ExtraPass("briefing: the Marshal night starts with the check", dir.Status);

            bool targetSeen = false, onMonitor = false, hit = false, shot = false, ortizInRoom = MarshalBriefing.Ortiz != null, ortizOutBy10 = false;
            int mostLabels = 0;
            var labelSteps = new HashSet<int>();
            string hitInfo = "no target";
            while (MarshalBriefing.Running && EditorApplication.isPlaying)
            {
                var t = MarshalBriefing.Target;
                if (t != null)
                {
                    targetSeen = true;
                    if (!shot && MarshalBriefing.Step >= 0)
                    {
                        shot = true;
                        // A round square on its chest, from the room side: counted, a hole, never an Intruder or a verdict.
                        int verdicts = dir.Completed;
                        var face = t.transform.forward;
                        var at = t.transform.position + Vector3.up * 1.2f + face * 0.006f;
                        t.ApplyDamage(new HIS.Weapons.DamageInfo { Amount = 34f, Type = HIS.Weapons.DamageType.Bullet, Point = at, Normal = face, Direction = -face, Penetration = 1f });
                        int marks = 0;
                        foreach (var r in t.GetComponentsInChildren<MeshRenderer>()) if (r.name == "Mark" && r.gameObject.activeSelf) marks++;
                        hit = t.RoundHits == 1 && marks == 1 && t.GetComponentInParent<HIS.Intruder.Intruder>() == null && dir.Completed == verdicts;
                        hitInfo = $"{t.RoundHits} hit(s), {marks} hole(s), verdicts {dir.Completed - verdicts}";
                    }
                }
                // The handover: the words go on labels at the things (and Ortiz's captions), never on the monitor. (Step 0's is
                // the second slot's: "Deputy Ortiz" over him with his hello.)
                var mon = HIS.Security.SecurityMonitor.Instance;
                if ((mon != null && mon.BigText != null) || HIS.UI.Notice.Posted("briefing.step") != null) onMonitor = true;
                if (MarshalBriefing.Label != null || MarshalBriefing.SideLabel != null) labelSteps.Add(MarshalBriefing.Step);
                var o = MarshalBriefing.Ortiz;
                if (MarshalBriefing.Step >= 0 && MarshalBriefing.Step < 9) ortizInRoom &= o != null && !o.HandoverLeaving;
                if (MarshalBriefing.Step == 10) ortizOutBy10 |= o == null || o.HandoverOut;
                mostLabels = Math.Max(mostLabels, MarshalBriefing.LabelsUp);
                await Wait(0.05f);
            }
            await Wait(0.1f);                            // (Destroy lands at the end of the frame)
            List<int> got; lock (steps) got = new List<int>(steps);
            bool inOrder = got.Count == 11;
            for (int i = 0; inOrder && i < 11; i++) inOrder = got[i] == i;
            string stepInfo = string.Join(",", got);
            if (MarshalBriefing.Completed == completed + 1 && inOrder) ExtraPass("briefing: steps 0–10 in order, to the end", stepInfo);
            else ExtraFail("briefing: steps 0–10 in order, to the end", $"steps [{stepInfo}], completed {MarshalBriefing.Completed - completed}, skipped {MarshalBriefing.LastSkipped}");
            // Labels at the things for the steps that point at one (the handover's skip hint, the hip, the target, the gun, the
            // shoulder, the knife, the hand, the handle, the monitor / the Menu hand); never on the monitor; at most two at once.
            var wantLabels = new[] { 0, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var missingLabels = new List<int>();
            foreach (int s in wantLabels) if (!labelSteps.Contains(s)) missingLabels.Add(s);
            string labelInfo = $"labels on steps [{string.Join(",", labelSteps)}], most at once {mostLabels}, on the monitor {onMonitor}";
            if (missingLabels.Count == 0 && mostLabels <= 2 && !onMonitor) ExtraPass("briefing: a label at the thing for each step, never the monitor", labelInfo);
            else ExtraFail("briefing: a label at the thing for each step, never the monitor", $"missing on [{string.Join(",", missingLabels)}]; {labelInfo}");
            if (targetSeen && hit) ExtraPass("briefing: the target takes a round (a hole, not judged)", hitInfo);
            else ExtraFail("briefing: the target takes a round (a hole, not judged)", $"seen {targetSeen}: {hitInfo}");
            // Ortiz in the room through steps 0–8, out through the door by step 10 (he opens it himself under the test hook).
            if (ortizInRoom && ortizOutBy10) ExtraPass("briefing: Ortiz in the room until the door step, out by step 10", "in, then out");
            else ExtraFail("briefing: Ortiz in the room until the door step, out by step 10", $"in the room through 0–8 {ortizInRoom}, out by step 10 {ortizOutBy10}");
            AfterCheck("briefing: played through");

            // 2. The pause row: listed in Marshal mode, and it starts the night over with the check; held Y skips it.
            if (pause != null)
            {
                if (pause.DebugMenuText().Contains("Weapons check")) ExtraPass("briefing: the Weapons check row in Marshal mode", "listed");
                else ExtraFail("briefing: the Weapons check row in Marshal mode", "not listed");
                int skips = MarshalBriefing.Skips;
                pause.StartWeaponsCheck();
                if (!await WaitFor(() => MarshalBriefing.Running && MarshalBriefing.Step >= 2, 30))
                    ExtraFail("briefing: the row starts the night over with the check", $"not running ({dir.Status})");
                else
                {
                    ExtraPass("briefing: the row starts the night over with the check", $"step {MarshalBriefing.Step}");
                    MarshalBriefing.TestSkip();
                    await WaitFor(() => !MarshalBriefing.Running, MarshalBriefing.SkipLeaveSeconds + 3f);   // (his goodbye: ≤ 6 s)
                    await Wait(0.1f);
                    if (!MarshalBriefing.Running && MarshalBriefing.LastSkipped && MarshalBriefing.Skips == skips + 1) ExtraPass("briefing: held Y skips it", "skipped");
                    else ExtraFail("briefing: held Y skips it", $"running {MarshalBriefing.Running}, skipped {MarshalBriefing.LastSkipped}");
                    AfterCheck("briefing: skipped");
                }
            }

            // 3. A mode switch mid-check (as the pause menu's Mode row): dropped cleanly, still owed.
            MarshalBriefing.Request();
            UnityEngine.Object.FindAnyObjectByType<HIS.Core.GameSession>()?.ResetSession("test: briefing again");
            if (!await WaitFor(() => MarshalBriefing.Running && MarshalBriefing.Step >= 1, 30))
                ExtraFail("briefing: a mode switch drops it", $"not running ({dir.Status})");
            else
            {
                dir.SetMode(ScenarioDirector.Mode.Scenarios);
                await Wait(0.3f);
                if (!MarshalBriefing.Running && MarshalBriefing.Due) ExtraPass("briefing: a mode switch drops it", "dropped, still owed");
                else ExtraFail("briefing: a mode switch drops it", $"running {MarshalBriefing.Running}, due {MarshalBriefing.Due}");
                AfterCheck("briefing: dropped");
            }
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            MarshalBriefing.TestAutoComplete = false;
        }
    }

    /// <summary>After a check, however it ended: no target, no line, no label, no card, Ortiz out of the room (gone, or out past the
    /// door on his way off the porch), and both guns full.</summary>
    static void AfterCheck(string what)
    {
        var ortiz = HIS.Intruder.Intruder.HandoverDeputy;
        bool ortizOut = ortiz == null || ortiz.HandoverOut;
        var mon = HIS.Security.SecurityMonitor.Instance;
        bool noTarget = MarshalBriefing.Target == null && GameObject.Find("[HIS] PracticeTarget") == null;
        bool noLabel = MarshalBriefing.LabelsUp == 0 && MarshalBriefing.LabelObject(0) == null && MarshalBriefing.LabelObject(1) == null;
        bool noLine = MarshalBriefing.Line == null && noLabel && (mon == null || mon.BigText == null) && HIS.UI.Notice.Posted("briefing.step") == null;
        bool noCard = HIS.UI.Notice.Posted("briefing") == null;
        var lo = HIS.Weapons.Loadout.Instance;
        string guns = "no loadout"; bool full = true;
        if (lo != null)
        {
            guns = "";
            foreach (var kind in new[] { HIS.Weapons.LoadoutSlot.Sidearm, HIS.Weapons.LoadoutSlot.LongGun })
            {
                int i = lo.SlotIndex(kind);
                if (i < 0 || !(lo.SpawnedIn(i) is HIS.Weapons.Firearm g)) continue;
                int rounds = g.RoundsInMag + (g.RoundChambered ? 1 : 0);
                full &= rounds == g.FullLoad || g.IsReloading;
                guns += $"{g.name} {rounds}/{g.FullLoad} ";
            }
        }
        string info = $"target gone {noTarget}, line gone {noLine}, card gone {noCard}, Ortiz {(ortiz == null ? "gone" : ortizOut ? "out, walking off" : "still in the room")}, {guns.Trim()}";
        if (noTarget && noLine && noCard && full && ortizOut) ExtraPass(what + ": cleaned up", info); else ExtraFail(what + ": cleaned up", info);
    }
}
