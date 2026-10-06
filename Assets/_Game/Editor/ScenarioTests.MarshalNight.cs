using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Marshal milestone B (research/27 §6 step 11; Brett 2026-10-03: "finish the ricky and stuff for the full marshall story";
/// "dont waste too much time on tests beyond making sure it works"), the mob night since Brett 0.1.57 ("it should be mostly
/// the mob coming after him"). Two runs, one door:
///   - <see cref="RunMarshalNightHeadless"/>: the whole night, in its order, every visitor left alone. The weapons check first
///     (asked for; each step done by its test hook), Ricky there from before it; the 1:12 card; a Y in the quiet after it,
///     and the first car still gives its whole warning (engine, VEHICLE, Ricky, a car door) before anyone is out; FIVE hitman
///     waves of 2 / 3 / 2 / 3 / 3 (13); the pizza courier after wave 1 and Sal after wave 3 (each after the street tidy, each
///     walking up to a shut door — Sal's kicked in by wave 3 and swung shut — each PERFECT with no show at all (Brett 0.1.58:
///     "when he leaves it shouldnt give the good call banner"; night 1's only show is its card), Sal in the
///     room for his visit); the clock at each wave's call 1:15 / 1:24 / 1:33 / 1:42 / 1:51 and as each visitor walks up 1:20
///     / 1:38; no Ortiz check-in (no such round, no radio call before two); the hitmen's cars parked through the visitors
///     (each wave's call finds every earlier car still out; no "PoliceCars: cleared" before the cut); wave 3's last man,
///     killed fighting in the room, still lying there with his blood through Sal's whole visit; one Ricky, ducking at the first
///     car; the 2:00 AM cut; the relief; WITNESS DELIVERED with "of 13" and "Visitors: 3, no bad calls."; the second night
///     opens without the check and (no Y) its first car is called 26–32 s after its 1:12 card; back in Scenarios the Marshal
///     state is gone (no Ricky, the real clock, the homeowner judge). Every hitman goes down as he reports out of the car,
///     except wave 3's: its finder as he takes the role at the door, the other man once he's fighting in the room.
///   - <see cref="RunMarshalFinderHeadless"/>: the finder left alone with the player out of his reach kills Ricky: THE TRIAL
///     IS OFF (WitnessKilled), never the "you shot Ricky" booking (no Mugshot mode), and no round at Ricky sooner than 2.5 s
///     after his "in the room". Then a reset mid-wave 2 on the next night: the squad is called off and exactly one Ricky is left.
/// The bad calls (you shoot Ricky; you shoot the courier between the waves; Ricky hit during Sal's visit) are
/// ScenarioTests.MarshalCalls.cs; the visitors' own walk-ups and leaving, ScenarioTests.MarshalVisitors.cs; the weapons
/// check's own steps, ScenarioTests.MarshalBriefing.cs.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunMarshalNightHeadless (and RunMarshalFinderHeadless)
/// </summary>
public static partial class ScenarioTests
{
    public static void RunMarshalNightHeadless() => RunMarshal(MarshalFullNight, 1500f);   // (five waves since Brett 0.1.57: was 1100)
    public static void RunMarshalFinderHeadless() => RunMarshal(MarshalFinder, 700f);

    /// <summary>Play Mode in the main scene, the door confirmed, then <paramref name="body"/>; the SCENARIO summary line and exit code.</summary>
    static void RunMarshal(Func<Task> body, float watchdog)
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + watchdog;
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
                HIS.Core.PlayerState.GodMode = true;     // their rounds land; the night goes on
                ScenarioDirector.WaitForForced = true;   // no scenario rounds before the switch
                PoliceResponse.Enabled = false;          // (no visitor is shot here: no bad call to answer)
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await body();
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

    /// <summary>Living Rickys in the scene (a destroyed one reads as null).</summary>
    static int RickyCount()
    {
        int n = 0;
        foreach (var i in UnityEngine.Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude)) if (i != null && i.IsProtectee) n++;
        return n;
    }

    static void MarshalCheck(bool ok, string what, string info) { if (ok) ExtraPass(what, info); else ExtraFail(what, info); }

    /// <summary>Round names as the logs print them (ScenarioDirector.Marshal's defs): the relief, and the check-in that's gone
    /// since Brett 0.1.57 (the courier's and Sal's: ScenarioTests.MarshalVisitors.cs).</summary>
    const string ReliefName = "Deputy Ortiz, the relief", OrtizCheckInName = "Deputy Ortiz checks in";

    /// <summary>A dead squad man lying in the room: drawn in it, and inside every door's plane.</summary>
    static bool InRoomBody(IntruderT o) =>
        o != null && o.Current == IntruderT.State.Dead && !o.BehindDoor && !HIS.Intruder.DoorwayNav.AnyBeyond(o.transform.position);

    /// <summary>Where <paramref name="o"/> is, for a report.</summary>
    static string BodyAt(IntruderT o) =>
        o == null ? "removed" : $"{o.Current}, {(InRoomBody(o) ? "lying in the room" : o.BehindDoor ? "outside" : "not in the room")} at {o.transform.position:F2}";

    /// <summary>World blood decals now (BloodDecals.DebugSummary's count; 0: none).</summary>
    static int BloodCount()
    {
        var s = HIS.Intruder.BloodDecals.DebugSummary;
        int sp = s.IndexOf(' ');
        return sp > 0 && int.TryParse(s.Substring(0, sp), out int n) ? n : 0;
    }

    /// <summary>The story clock as "h:mm" ("real": no story running).</summary>
    static string ClockNow() { var n = StoryClock.Now; return n.HasValue ? $"{n.Value.Hour}:{n.Value.Minute:00}" : "real"; }

    static async Task MarshalFullNight()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("marshal night: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }

        int quiet = 0, noShow = 0, rickySpawns = 0, ducks = 0, failures = 0, cut = 0, relieved = 0, outOfCar = 0;
        int checksRun = 0, checksDone = 0, checkIns = 0, carsCleared = 0;
        float checkDoneAt = -1f, rickyAt = -1f, checkStartAt = -1f, cutAt = -1f, night2FirstCall = -1f;
        bool rickyMissedCheck = false;
        var shows = new List<string>();
        var stepsDone = new HashSet<int>();
        var verdicts = new Dictionary<string, string>();                 // night 1: round name → its tier (the VERDICT line)
        var safeHouseAt = new List<float>();                             // each night's 1:12 card
        var radioAt = new List<float>();                                 // "Ortiz radios ahead" (only the relief's, after two)
        var calls = new Dictionary<int, (int Men, float At, string Clock, int CarsOut)>(); // night 1's waves as called
        var spawned = new Dictionary<string, (float At, string Clock, string Door)>();     // night 1's visitors as they set off
        var tidied = new Dictionary<string, (float At, string Text)>();  // the street tidy before each of them
        var beaten = new Dictionary<int, float>();
        var visits = new HashSet<string>();                              // "visits (…)": he came in for his visit
        string firstFailure = null;
        var stepRx = new Regex(@"^\[HIS\] Briefing: step (\d+) done");
        var verdictRx = new Regex(@"^\[HIS\] VERDICT #\d+ (.+?): (Perfect|Good|Bad|YouDied)");
        var waveRx = new Regex(@"^\[HIS\] Hitmen: wave (\d) \(.*\) called: (\d) front \+ (\d) side");
        var beatenRx = new Regex(@"^\[HIS\] Hitmen: wave (\d) beaten");
        var spawnRx = new Regex(@"^\[HIS\] Intruder #\d+: (Pizza courier|Sal, Ricky's brother|Deputy Ortiz, the relief) \(");
        var tidyRx = new Regex(@"^\[HIS\] Hitmen: the street tidied for (.+?) — ");
        var visitRx = new Regex(@"^\[HIS\] Intruder #\d+: visits \((.+), \d+ s\)");   // ("visits (Sal, Ricky's brother, 8 s)")
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            if (type == LogType.Exception || msg.Contains("brain failed") || msg.Contains("brain stopped")) { failures++; firstFailure ??= msg; }
            // The handover's Ortiz stuck in the room, then gone where he stood (a pop in front of you): as bad as a brain failure.
            if (msg.StartsWith("[HIS] Ortiz: blocked") || msg.StartsWith("[HIS] Ortiz: no way out") || msg.Contains("Ortiz never got out"))
            { failures++; firstFailure ??= msg; }
            if (!msg.StartsWith("[HIS] ")) return;
            float now = Time.time;
            int night = safeHouseAt.Count;                               // (0: the weapons check, before the first card)
            if (msg.Contains(": safe house, 1:12 AM")) { safeHouseAt.Add(now); return; }
            if (msg.StartsWith("[HIS] Outcome show: ") && night == 1) { shows.Add(msg.Substring(20)); if (msg.Contains("(quiet)")) quiet++; }
            if (msg.StartsWith("[HIS] Scenario #") && msg.Contains(": no show (left alone)") && night == 1) noShow++;
            if (msg.StartsWith("[HIS] Ricky (#")) { rickySpawns++; if (rickyAt < 0f) rickyAt = now; }
            if (msg.StartsWith("[HIS] Ricky: the first car") && night == 1) ducks++;
            if (msg.Contains("2:00 AM — the street is cleared") && night == 1) { cut++; if (cutAt < 0f) cutAt = now; }
            if (msg.Contains("Relieved")) relieved++;
            if (msg.StartsWith("[HIS] Briefing: weapons check starts")) { checksRun++; if (checkStartAt < 0f) checkStartAt = now; }
            if (msg.StartsWith("[HIS] Briefing: weapons check done")) { checksDone++; if (checkDoneAt < 0f) checkDoneAt = now; }
            if (msg.Contains(OrtizCheckInName)) checkIns++;
            if (msg.StartsWith("[HIS] Marshal: Ortiz radios ahead") && night == 1) radioAt.Add(now);
            if (msg.StartsWith("[HIS] PoliceCars: cleared") && night == 1 && calls.Count > 0 && cutAt < 0f) carsCleared++;
            var m = stepRx.Match(msg); if (m.Success) stepsDone.Add(int.Parse(m.Groups[1].Value));
            m = verdictRx.Match(msg); if (m.Success && night == 1) verdicts[m.Groups[1].Value] = m.Groups[2].Value;
            m = waveRx.Match(msg);
            if (m.Success)
            {
                int w = int.Parse(m.Groups[1].Value);
                var pc = HIS.Security.PoliceCars.Instance;
                if (night == 1 && !calls.ContainsKey(w))
                    calls[w] = (int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value), now, ClockNow(), pc != null ? pc.ActiveCars : -1);
                if (night == 2 && w == 1 && night2FirstCall < 0f) night2FirstCall = now;
            }
            m = beatenRx.Match(msg); if (m.Success && night == 1) beaten[int.Parse(m.Groups[1].Value)] = now;
            m = spawnRx.Match(msg);
            if (m.Success && night == 1 && !spawned.ContainsKey(m.Groups[1].Value))
            {
                var f = HIS.Room.VirtualDoor.Instance;
                spawned[m.Groups[1].Value] = (now, ClockNow(), f == null ? "no door" : f.IsOpen ? "open" : f.Breached ? "shut but broken" : "shut");
            }
            m = tidyRx.Match(msg); if (m.Success && night == 1) tidied[m.Groups[1].Value] = (now, msg.Substring(msg.IndexOf('—') + 1).Trim());
            m = visitRx.Match(msg); if (m.Success && night == 1) visits.Add(m.Groups[1].Value);
        };
        // research/27 §6 step 11: every hitman goes down as he reports out of the car (in the room, fighting or a finder: the
        // same, should one get that far — a car shot up, say). Night 1's wave 3 is let in: its finder goes down as he takes the
        // role at the door, the other man once he's fighting in the room — his body is the one that must stay through Sal.
        var down = new HashSet<IntruderT>();
        IntruderT probe = null;
        Action<IntruderT, string> onReport = (o, r) =>
        {
            if (o == null || !o.IsHitman) return;
            if (r == "out of the car") outOfCar++;
            bool letIn = o.Wave == 3 && safeHouseAt.Count == 1;
            bool kill = letIn
                ? r.StartsWith("finder: takes the role") || r.StartsWith("finder: goes for") || r == "fighting"
                : r == "out of the car" || r == "in the room" || r == "fighting" || r.StartsWith("finder: ");
            if (letIn && r == "fighting") probe = o;
            if (kill) lock (down) down.Add(o);
        };
        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        bool visitorsWere = ScenarioDirector.MarshalVisitors;
        ScenarioDirector.MarshalVisitors = true;
        // The weapons check opens the first night (asked for: the Editor never reads the headset flag), each step done by its
        // test hook (MarshalBriefing.TestAutoComplete) — the steps' own events are RunMarshalBriefingHeadless's.
        MarshalBriefing.TestAutoComplete = true;
        MarshalBriefing.Request();
        bool roleMarshal = false, ySent = false, probeAtSalOk = false, probeAfterSalOk = false;
        string probeAtSal = "Sal never set off", probeAfterSal = "Sal's round never ended";
        int bloodAfterSal = -1, rickySpawnsNight1 = -1;
        var arrival = new ArrivalWatch();          // each wave heard and seen coming (Brett 0.1.56; ScenarioTests.MarshalArrival.cs)
        try
        {
            int nightsBefore = dir.NightsDone;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            if (dir.CurrentMode != ScenarioDirector.Mode.Marshal) { ExtraFail("marshal night: mode switch", $"mode {dir.CurrentMode}"); return; }
            var police = PoliceResponse.Instance;
            float until = Time.realtimeSinceStartup + 1100f;
            while (dir.NightsDone == nightsBefore && Time.realtimeSinceStartup < until && EditorApplication.isPlaying)
            {
                roleMarshal |= Judge.Role == Role.Marshal;
                rickyMissedCheck |= MarshalBriefing.Running && RickyCount() != 1;
                arrival.Poll();
                lock (down)
                    foreach (var o in down)
                        if (o != null && o.Current != IntruderT.State.Dead) o.TakeDamage(1000f);
                // Y in the quiet after the 1:12 card (the "Marshal: on now" skip): the first car must still give its warning.
                if (!ySent && safeHouseAt.Count == 1 && Time.time > safeHouseAt[0] + 3.4f && police != null && !police.Active)
                { ySent = true; dir.RequestSkip(); }
                // Wave 3's body in the room, as Sal sets off up the path and as his round ends.
                if (spawned.ContainsKey(SalName) && !probeAtSalOk && probeAtSal == "Sal never set off")
                { probeAtSalOk = InRoomBody(probe); probeAtSal = BodyAt(probe); }
                if (verdicts.ContainsKey(SalName) && probeAfterSal == "Sal's round never ended")
                { probeAfterSalOk = InRoomBody(probe); probeAfterSal = BodyAt(probe); bloodAfterSal = BloodCount(); }
                await Wait(0.1f);
            }
            rickySpawnsNight1 = rickySpawns;
            var v = dir.LastVerdict.GetValueOrDefault();
            string card = $"{police?.Ending}: {v.Headline} — {v.Reason} / {v.Sentence}";
            if (dir.NightsDone == nightsBefore) { ExtraFail("marshal night: the night reaches its card", $"still {dir.Status} after 1100 s; shows: {string.Join(" | ", shows)}"); return; }

            // The first car: a Y after the card cuts the quiet, never under the floor, and the car still warns before anyone is out.
            calls.TryGetValue(1, out var c1);
            float firstGap = safeHouseAt.Count > 0 && c1.At > 0f ? c1.At - safeHouseAt[0] : -1f;
            MarshalCheck(ySent && arrival.OnNow >= 1 && firstGap >= 3f + 1.5f - 0.1f && firstGap <= 10f,
                  "marshal night: Y cuts the quiet after the 1:12 card (never under the floor)",
                  $"Y sent {ySent}, 'Marshal: on now' {arrival.OnNow}, the first car {firstGap:F1} s after the card (card 3 s + floor 1.5 s at the least)");
            arrival.CheckBeforeOut("marshal night", 1, 2, 3, 4, 5);       // (each man is put down as he gets out; wave 3 just reports it)

            MarshalCheck(roleMarshal, "marshal night: the judge weighs a Marshal", $"Judge.Role {Judge.Role}");
            var missing = new List<int>(); for (int s = 0; s <= 10; s++) if (!stepsDone.Contains(s)) missing.Add(s);
            MarshalCheck(checksRun == 1 && checksDone == 1 && missing.Count == 0 && !rickyMissedCheck && rickyAt > 0f && rickyAt <= checkStartAt && checkDoneAt > 0f
                         && MarshalBriefing.Target == null && !MarshalBriefing.Running,
                  "marshal night: the weapons check first (test hook), Ricky in his spot from before it (the handover)",
                  $"{checksRun} started, {checksDone} done, steps 0–10 done {(missing.Count == 0 ? "all" : "missing " + string.Join(",", missing))}, " +
                  $"Ricky missing or doubled during it {rickyMissedCheck}, Ricky {(rickyAt > 0f && rickyAt <= checkStartAt ? "before" : "after")} it, target {(MarshalBriefing.Target != null ? "still up" : "gone")}");
            MarshalCheck(rickySpawnsNight1 == 1 && RickyCount() == 1 && IntruderT.Protectee != null && IntruderT.Protectee.Current != IntruderT.State.Dead,
                  "marshal night: one Ricky, alive all night", $"{rickySpawnsNight1} spawn(s), {RickyCount()} in the scene");
            MarshalCheck(ducks == 1, "marshal night: Ricky ducks at the first car", $"{ducks}");

            // Five waves, each harder and each its tier's size (Brett 0.1.57: 2 / 3 / 2 / 3 / 3 = 13).
            var sizes = new List<string>(); int men = 0; bool sizesOk = calls.Count == 5 && HitmanTier.Waves == 5;
            for (int w = 1; w <= 5; w++)
            {
                calls.TryGetValue(w, out var cw);
                HitmanTier.For(w).Split(false, out int wf, out int ws);
                sizes.Add($"{cw.Men}"); men += cw.Men; sizesOk &= cw.Men == wf + ws;
            }
            MarshalCheck(sizesOk && men == 13, "marshal night: five hitman waves, 2 / 3 / 2 / 3 / 3 = 13",
                  $"{string.Join(" / ", sizes)} = {men}; {outOfCar} reported out of the car");

            // The night in its order: wave 1, the courier, waves 2 and 3, Sal, waves 4 and 5, the cut, the relief — each visitor after
            // the street tidy, which comes after the wave before him was beaten.
            float T(Dictionary<int, (int Men, float At, string Clock, int CarsOut)> d, int w) => d.TryGetValue(w, out var x) ? x.At : -1f;
            float S(string who) => spawned.TryGetValue(who, out var x) ? x.At : -1f;
            float Td(string who) => tidied.TryGetValue(who, out var x) ? x.At : -1f;
            float B(int w) => beaten.TryGetValue(w, out var x) ? x : -1f;
            var order = new[] { T(calls, 1), S(CourierName), T(calls, 2), T(calls, 3), S(SalName), T(calls, 4), T(calls, 5), cutAt, S(ReliefName) };
            bool inOrder = true; for (int i = 0; i < order.Length; i++) inOrder &= order[i] > 0f && (i == 0 || order[i] > order[i - 1]);
            // (The tidy's line is its last word, logged once the bodies out front are gone — the frame he sets off at the latest:
            // the fixer, 0.1.58, waits for them to be out of your sight first.)
            bool tidyOk = B(1) > 0f && Td(CourierName) > B(1) && Td(CourierName) <= S(CourierName) && B(3) > 0f && Td(SalName) > B(3) && Td(SalName) <= S(SalName);
            string R(float t) => t < 0f ? "never" : c1.At > 0f ? $"+{t - c1.At:F0} s" : $"{t:F0}";
            MarshalCheck(inOrder && tidyOk, "marshal night: wave 1, the courier, waves 2–3, Sal, waves 4–5, the cut, the relief (each visitor after the street tidy)",
                  $"waves {R(T(calls, 1))} {R(T(calls, 2))} {R(T(calls, 3))} {R(T(calls, 4))} {R(T(calls, 5))}; courier {R(S(CourierName))} (tidy {R(Td(CourierName))}, wave 1 beaten {R(B(1))}); " +
                  $"Sal {R(S(SalName))} (tidy {R(Td(SalName))}, wave 3 beaten {R(B(3))}); cut {R(cutAt)}; relief {R(S(ReliefName))}; " +
                  $"tidies: {string.Join(" | ", System.Linq.Enumerable.Select(tidied, kv => $"{kv.Key}: {kv.Value.Text}"))}");

            // The clock: each wave as it's called, each visitor as he sets off (StoryClock never goes back).
            string[] wantWave = { "1:15", "1:24", "1:33", "1:42", "1:51" };
            bool clockOk = true; var clockInfo = new List<string>();
            for (int w = 1; w <= 5; w++) { calls.TryGetValue(w, out var cw); clockOk &= cw.Clock == wantWave[w - 1]; clockInfo.Add($"w{w} {cw.Clock ?? "-"}"); }
            spawned.TryGetValue(CourierName, out var sc); spawned.TryGetValue(SalName, out var ss);
            clockOk &= sc.Clock == "1:20" && ss.Clock == "1:38";
            MarshalCheck(clockOk, "marshal night: the clock — waves 1:15 / 1:24 / 1:33 / 1:42 / 1:51, the courier 1:20, Sal 1:38",
                  $"{string.Join(", ", clockInfo)}; courier {sc.Clock ?? "-"}, Sal {ss.Clock ?? "-"}");

            // The visitors between the waves: each walks up to a shut door (Sal's kicked in by wave 3, swung shut by the tidy),
            // each left alone is PERFECT with no show at all — no banner, nothing (Brett 0.1.58, replaying the intro: "when he
            // leaves it shouldnt give the good call banner the mob people should come"): the courier, Sal and the relief each log
            // "no show (left alone)", and night 1's only show is its WITNESS DELIVERED card. Sal comes in for his visit.
            verdicts.TryGetValue(CourierName, out var tc); verdicts.TryGetValue(SalName, out var ts);
            bool oneShow = shows.Count == 1 && shows[0].StartsWith("Perfect,") && quiet == 0;
            MarshalCheck(tc == "Perfect" && ts == "Perfect" && noShow == 3 && oneShow && visits.Contains(SalName) && sc.Door == "shut" && ss.Door == "shut",
                  "marshal night: the courier and Sal, left alone between the waves — each PERFECT, no banner or show (the night goes on), a shut door to walk up to",
                  $"courier {tc ?? "no verdict"} (the door {sc.Door ?? "-"}), Sal {ts ?? "no verdict"} (the door {ss.Door ?? "-"}, came in {visits.Contains(SalName)}); " +
                  $"{noShow} 'no show (left alone)' (want 3: the courier, Sal, the relief), {quiet} quiet banner(s); night 1's shows ({shows.Count}, want only its card): {string.Join(" | ", shows)}");
            MarshalCheck(checkIns == 0 && radioAt.Count == 1 && cutAt > 0f && radioAt[0] > cutAt,
                  "marshal night: no Ortiz check-in (no such round, his only radio call is the relief's after two)",
                  $"'{OrtizCheckInName}' seen {checkIns} time(s); Ortiz radio calls {radioAt.Count}{(radioAt.Count > 0 && cutAt > 0f ? $", first {radioAt[0] - cutAt:F0} s after the cut" : "")}");

            // The hitmen's cars stay parked through the visitors: each wave's call finds every earlier car still out.
            bool parked = carsCleared == 0; var carsInfo = new List<string>();
            for (int w = 1; w <= 5; w++) { calls.TryGetValue(w, out var cw); parked &= cw.CarsOut == w; carsInfo.Add($"w{w}: {cw.CarsOut} out"); }
            MarshalCheck(parked, "marshal night: the hitmen's cars stay parked through the visitors (each call finds the earlier ones out)",
                  $"{string.Join(", ", carsInfo)}; 'PoliceCars: cleared' before the cut {carsCleared}");

            // Wave 3's man killed fighting in the room: still lying there, with his blood, through Sal's whole visit (the tidy
            // takes the street's and the doorway's only; Sal's round doesn't clear the room).
            tidied.TryGetValue(SalName, out var salTidy);
            var keptRx = new Regex(@"(\d+) kept in the room");
            var km = salTidy.Text != null ? keptRx.Match(salTidy.Text) : Match.Empty;
            int keptByTidy = km.Success ? int.Parse(km.Groups[1].Value) : -1;
            // (Not `probe != null` here: the 2:00 AM cut destroyed his body since — InRoomBody needed him there at both looks.)
            MarshalCheck((object)probe != null && probeAtSalOk && probeAfterSalOk && keptByTidy >= 1 && bloodAfterSal > 0,
                  "marshal night: a body in the room stays through Sal's visit (with its blood)",
                  $"wave 3's man as Sal set off: {probeAtSal}; as his round ended: {probeAfterSal}; the tidy kept {keptByTidy} in the room; {bloodAfterSal} blood decal(s)");

            verdicts.TryGetValue(ReliefName, out var relief);
            MarshalCheck(cut == 1 && relieved >= 1 && relief == "Perfect", "marshal night: the 2:00 AM cut, then the relief", $"cut {cut}, relieved {relieved}, the relief's call {relief ?? "none"}");
            MarshalCheck(police != null && police.Ending == StandoffEnding.Delivered && v.Headline == Judge.HeadlineDelivered && v.Tier == Tier.Perfect
                  && v.Sentence != null && v.Sentence.Contains("of 13.") && v.Sentence.Contains(Judge.VisitorTally(3)),
                  "marshal night: WITNESS DELIVERED, of 13, 3 visitors", card);

            // The next night opens without the check (done once; in the Editor only a request brings it back), and with no Y
            // its first car comes about 30 s after its 1:12 card (Brett 0.1.57: "mostly the mob": no two minutes of visitors first).
            bool second = await WaitFor(() => safeHouseAt.Count >= 2, 60);
            MarshalCheck(second && checksRun == 1, "marshal night: the second night skips the weapons check",
                  $"second night {(second ? "began" : "never began")}, checks run {checksRun}");
            bool secondCar = second && await WaitFor(() => night2FirstCall > 0f, 45);
            float gap2 = secondCar ? night2FirstCall - safeHouseAt[1] : -1f;
            MarshalCheck(secondCar && gap2 >= 26f && gap2 <= 32f, "marshal night: the first car about 30 s after the 1:12 card (no Y)",
                  secondCar ? $"{gap2:F1} s (the card 3 s + the quiet 25 s)" : "the second night's first car never came");
            MarshalCheck(failures == 0, "marshal night: no brain failure or exception", failures == 0 ? "none" : $"{failures}, first: {firstFailure}");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            arrival.Dispose();
            ScenarioDirector.MarshalVisitors = visitorsWere;
            MarshalBriefing.TestAutoComplete = false;
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }
        // The safety goal (research/27; Brett "safely adding the story"): back in Scenarios, nothing of the night is left.
        await Wait(0.3f);
        MarshalCheck(IntruderT.Protectee == null && StoryClock.Now == null && Judge.Role == Role.Homeowner,
              "marshal night: back in Scenarios, no Ricky, the real clock, the homeowner judge",
              $"Ricky {(IntruderT.Protectee != null ? "still there" : "gone")}, clock {(StoryClock.Now.HasValue ? "story" : "real")}, role {Judge.Role}");
    }

    static async Task MarshalFinder()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("marshal finder: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }

        var finders = new HashSet<IntruderT>();
        var inRoom = new Dictionary<IntruderT, float>();
        var down = new HashSet<IntruderT>();
        float firstRound = -1f, finderInRoom = -1f;
        var finderWaves = new Dictionary<int, int>();
        int rickyHitByYou = 0, rickySquadKill = 0, failures = 0, badShows = 0;
        bool killAll = false;                           // the reset's night: finders go down too, so it reaches wave 2
        string firstFailure = null, lastFinder = "none", cardShow = "none";
        Action<IntruderT, string> onReport = (o, r) =>
        {
            if (o == null || !o.IsHitman) return;
            lock (down)
            {
                if (killAll) { if (r == "out of the car" || r == "in the room" || r == "fighting" || r.StartsWith("finder: ")) down.Add(o); return; }
                if (r.StartsWith("finder: takes the role")) { finders.Add(o); finderWaves[o.Wave] = finderWaves.TryGetValue(o.Wave, out var n) ? n + 1 : 1; }
                if (r == "in the room") inRoom[o] = Time.time;
                if (r.StartsWith("finder: ")) lastFinder = r;
                if (r.StartsWith("finder: fires at the witness") && firstRound < 0f)
                {
                    firstRound = Time.time;
                    finderInRoom = inRoom.TryGetValue(o, out var t) ? t : -1f;
                }
                // Everyone else goes down as he gets in; the finder too if he turns on you (then he's no finder).
                if ((r == "in the room" && !finders.Contains(o)) || r == "fighting") down.Add(o);
            }
        };
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            if (msg.StartsWith("[HIS] Marshal: you hit Ricky")) rickyHitByYou++;
            if (msg.StartsWith("[HIS] Ricky: dead (a squad round)")) rickySquadKill++;
            if (msg.StartsWith("[HIS] Bad-call show: ")) badShows++;           // the booking's mugshot (or any bad-call show)
            if (type == LogType.Exception || msg.Contains("brain failed") || msg.Contains("brain stopped")) { failures++; firstFailure ??= msg; }
        };
        // The card's show as it starts: its tier, and the booking override (OutcomeShow.ForceBadMode) left armed or not.
        Action<Tier, float> onShow = (tier, len) => { if (cardShow == "none") cardShow = $"{tier}, ForceBadMode {(HIS.Outcome.OutcomeShow.ForceBadMode?.ToString() ?? "none")}"; };
        HIS.Outcome.OutcomeShow.Started += onShow;
        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        bool visitorsWere = ScenarioDirector.MarshalVisitors;
        ScenarioDirector.MarshalVisitors = false;
        IntruderT.TestSpread = 0f;                      // straight down the line: the finder's job is the check, not his aim
        var cam = Camera.main;
        var camWas = cam != null ? cam.transform.position : Vector3.zero;
        try
        {
            int nightsBefore = dir.NightsDone;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            if (!await WaitFor(() => IntruderT.Protectee != null, 60)) { ExtraFail("marshal finder: Ricky spawns", dir.Status); return; }
            // The player out of the finder's reach (> 2 m from Ricky, the front door's way in and the line between them).
            var ricky = IntruderT.Protectee.transform.position;
            if (cam != null && spawner.TryGetDoorPoints(0, out _, out var inside, out _)) cam.transform.position = OutOfReach(ricky, inside, camWas.y);
            float camToRicky = cam != null ? Vector3.Distance(FlatXZ(cam.transform.position), FlatXZ(ricky)) : -1f;
            Debug.Log($"[HIS] Marshal finder test: you at {(cam != null ? cam.transform.position : Vector3.zero):F2}, {camToRicky:F1} m from Ricky at {ricky:F2}");

            var police = PoliceResponse.Instance;
            float until = Time.realtimeSinceStartup + 300f;
            while (dir.NightsDone == nightsBefore && Time.realtimeSinceStartup < until && EditorApplication.isPlaying)
            {
                lock (down)
                    foreach (var o in down)
                        if (o != null && o.Current != IntruderT.State.Dead) o.TakeDamage(1000f);
                await Wait(0.1f);
            }
            var v = dir.LastVerdict.GetValueOrDefault();
            string card = $"{police?.Ending}: {v.Headline} — {v.Reason} / {v.Sentence}";
            if (dir.NightsDone == nightsBefore) { ExtraFail("marshal finder: the night reaches its card", $"still {dir.Status}; last finder report: {lastFinder}"); return; }
            MarshalCheck(police != null && police.Ending == StandoffEnding.WitnessKilled && v.Headline == Judge.HeadlineTrialOff && v.Tier == Tier.YouDied
                  && rickySquadKill == 1 && rickyHitByYou == 0,
                  "marshal finder: he kills Ricky → THE TRIAL IS OFF, not the booking",
                  $"{card}; squad kill {rickySquadKill}, 'you hit Ricky' {rickyHitByYou}; you {camToRicky:F1} m from Ricky; finder: {lastFinder}");
            float gap = firstRound >= 0f && finderInRoom >= 0f ? firstRound - finderInRoom : -1f;
            MarshalCheck(gap >= 2.5f - 0.05f, "marshal finder: no round at Ricky sooner than 2.5 s after he's in the room", $"{gap:F2} s");
            // research/27 §6 step 11: no Mugshot mode — the dim YouDied show, no bad-call show, and no booking override left armed
            // for the next bad call.
            await Wait(1f);
            MarshalCheck(cardShow.StartsWith("YouDied") && cardShow.EndsWith("none") && badShows == 0 && HIS.Outcome.OutcomeShow.ForceBadMode == null,
                  "marshal finder: no Mugshot mode", $"the card's show: {cardShow}; bad-call shows {badShows}; ForceBadMode now {(HIS.Outcome.OutcomeShow.ForceBadMode?.ToString() ?? "none")}");
            string perWave; lock (down) perWave = string.Join(", ", System.Linq.Enumerable.Select(finderWaves, kv => $"wave {kv.Key}: {kv.Value}"));
            bool oneEach; lock (down) oneEach = finderWaves.Count > 0 && System.Linq.Enumerable.All(finderWaves.Values, n => n == 1);
            MarshalCheck(oneEach, "marshal finder: one finder per wave", perWave);

            // A reset mid-wave 2 on the next night (the hold-Menu path, without the door picker): called off, one Ricky. Every
            // man of that night goes down as he reports out of the car (the finders too), so it gets to wave 2.
            lock (down) killAll = true;
            bool wave2 = await WaitFor(() =>
            {
                lock (down)
                    foreach (var o in down)
                        if (o != null && o.Current != IntruderT.State.Dead) o.TakeDamage(1000f);
                return police.Active && police.Faction == SquadFaction.Hitmen && police.Wave >= 2;
            }, 180);
            if (!wave2) { ExtraFail("marshal finder: a reset mid-wave 2", $"the next night's wave 2 never came ({dir.Status}, wave {police.Wave})"); return; }
            await Wait(2f);
            int waveAtReset = police.Wave;
            var oldRicky = IntruderT.Protectee;
            UnityEngine.Object.FindAnyObjectByType<HIS.Core.GameSession>()?.ResetSession("menu hold (test: no door picker)");
            bool calledOff = !police.Active;
            // The new night's own Ricky (the old one is destroyed at the frame's end; the new one comes after the night's delay).
            await WaitFor(() => IntruderT.Protectee != null && IntruderT.Protectee != oldRicky, 40);
            await Wait(1f);
            MarshalCheck(waveAtReset == 2 && calledOff && !police.Active && RickyCount() == 1, "marshal finder: a reset mid-wave 2 calls the squad off and leaves one Ricky",
                  $"reset in wave {waveAtReset}; active after the reset {!calledOff}, now {police.Active}; {RickyCount()} Ricky in the scene");
            MarshalCheck(failures == 0, "marshal finder: no brain failure or exception", failures == 0 ? "none" : $"{failures}, first: {firstFailure}");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            HIS.Outcome.OutcomeShow.Started -= onShow;
            ScenarioDirector.MarshalVisitors = visitorsWere;
            IntruderT.TestSpread = null;
            if (cam != null) cam.transform.position = camWas;
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }
    }

    static Vector3 FlatXZ(Vector3 v) => new(v.x, 0f, v.z);

    /// <summary>
    /// A NavMesh point in the room (inside the scanned outline) far from Ricky, the door's way in and the finder's walk to him
    /// (the room NavMesh path from the door to Ricky's side): he drops the role for a player within 2 m of him anywhere on it.
    /// </summary>
    static Vector3 OutOfReach(Vector3 ricky, Vector3 doorInside, float eyeY)
    {
        var filter = UnityEngine.AI.NavMesh.AllAreas;
        var route = new List<Vector3> { doorInside };
        var toDoor = FlatXZ(doorInside - ricky).normalized;
        if (UnityEngine.AI.NavMesh.SamplePosition(ricky + toDoor * 1.5f, out var nearRicky, 0.6f, filter))
        {
            var path = new UnityEngine.AI.NavMeshPath();
            if (UnityEngine.AI.NavMesh.CalculatePath(doorInside, nearRicky.position, filter, path) && path.corners.Length > 0) route = new List<Vector3>(path.corners);
            else route.Add(nearRicky.position);
        }
        else route.Add(ricky);
        var geo = HIS.Room.RoomScanner.Instance != null ? HIS.Room.RoomScanner.Instance.Geometry : null;
        var mid = (FlatXZ(doorInside) + FlatXZ(ricky)) * 0.5f;
        Vector3 best = doorInside; float bestScore = -1f;
        for (float x = -8f; x <= 8f; x += 0.3f)
            for (float z = -8f; z <= 8f; z += 0.3f)
            {
                var p = new Vector3(mid.x + x, ricky.y, mid.z + z);
                if (!UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 0.2f, filter)) continue;
                if (HIS.Intruder.DoorwayNav.AnyBeyond(hit.position)) continue;          // in the room, not out on the porch
                // (the porch and street have NavMesh too: only points inside the scanned outline, clear of the walls)
                var q2 = new Vector2(hit.position.x, hit.position.z);
                if (geo != null && (!geo.Inside(q2) || geo.Clearance(q2) < 0.3f)) continue;
                var q = FlatXZ(hit.position);
                float score = Vector3.Distance(q, FlatXZ(ricky));
                for (int i = 0; i < route.Count; i++)
                {
                    var a = FlatXZ(route[i]); var b = FlatXZ(route[Mathf.Min(i + 1, route.Count - 1)]);
                    var ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(q - a, ab) / Mathf.Max(1e-4f, ab.sqrMagnitude));
                    score = Mathf.Min(score, Vector3.Distance(q, a + ab * t));
                }
                if (score > bestScore) { bestScore = score; best = hit.position; }
            }
        Debug.Log($"[HIS] Marshal finder test: the finder's way in has {route.Count} corner(s); the player's spot keeps {bestScore:F2} m off it and Ricky");
        return new Vector3(best.x, eyeY, best.z);
    }
}
