using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Marshal milestone A smoke run (research/27 §6; the full Marshal suite, step 11, is milestone B): the pause-menu path into
/// Marshal mode (<see cref="ScenarioDirector.SetMode"/>), one bare night end to end, every hitman left to come in, see the
/// player and fire before he's killed. Checks (Brett 0.1.57: five waves, each harder): five waves, each in its tier's own
/// vehicle (Sedan / RedSedan / SUV / CharcoalSUV / Van), 2 / 3 / 2 / 3 / 3 men (13), no light-bar flash on the room all night,
/// the tell (no normal round before the man's own window, never under MinWindow; the gun low until his raise), the night's
/// card WITNESS DELIVERED with "Hitmen stopped: N of 13.", and no brain failure or exception on the way. Probes on
/// the way, so the code paths run at least once: a round of yours through the shut front door at the first stack whose tier
/// answers it (the blind volley: wave 2's street crew on), the first wave whose tier takes cover to reload kept alive through
/// a magazine (the reload, and the step out of your line for it), a chest round on a plated man (wave 5's operators: a
/// clank, no wound, he fights on).
///   Just this: scripts/unity-batch.sh ScenarioTests.RunMarshalSmokeHeadless (two doors: RunMarshalSmokeTwoDoorsHeadless)
/// </summary>
public static partial class ScenarioTests
{
    public static void RunMarshalSmokeHeadless()
    {
        HISEditorMenu.OpenMain();
        s_beforePlay?.Invoke();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 1500f;   // (five waves since Brett 0.1.57: was 900 for three)
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
                PoliceResponse.Enabled = false;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun())
                {
                    // Two doors: confirm the second (the batch picker answers step 2 "no" unless told, and an earlier run may
                    // have saved "front door only"), as the standoff's two-door run does — else the night runs at one door.
                    if (s_marshalTwoDoors) await TwoDoorChecks();
                    await MarshalNightSmoke();
                }
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

    /// <summary>The two-door run (<see cref="RunMarshalSmokeTwoDoorsHeadless"/>): the second door must be confirmed and used.</summary>
    static bool s_marshalTwoDoors;

    /// <summary>
    /// The same night in the two-door room: wave 2's pincer (one man round to the side door), wave 3's side man. The second
    /// door is confirmed first (TwoDoorChecks), and the night fails unless the spawner has both doors and each wave splits
    /// front / side as HitmanTier says (1 + 1, then 2 + 1).
    /// </summary>
    public static void RunMarshalSmokeTwoDoorsHeadless() { s_marshalTwoDoors = true; s_beforePlay = LoadTwoDoorRoom; RunMarshalSmokeHeadless(); }

    /// <summary>One man's night as his reports gave it (Time.time).</summary>
    class HitLog
    {
        public float SeenAt = -1f, Window, React, Raise, RaisedAt = -1f, FiredAt = -1f, InRoomAt = -1f, StackedAt = -1f, PlateShotAt = -1f;
        public bool Behind, Plated, Reloaded, ProbedDoor, Finder; public int Wave, Reloads, Covers, NoCover, Volleys, BlindRounds;
    }

    static async Task MarshalNightSmoke()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("marshal: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        bool twoDoors = spawner.DoorCount > 1;
        if (s_marshalTwoDoors && !twoDoors) { ExtraFail("marshal: two doors", $"the spawner has {spawner.DoorCount} door(s): no pincer to run"); return; }
        HIS.Intruder.Hitbox.LethalRounds = true;   // (as the game plays: the plate's rule)
        var guns = DsInit() ? DsMakeGuns() : new List<DsGun>();
        var m9 = guns.Find(g => g.Name == "M9") ?? (guns.Count > 0 ? guns[0] : null);

        var cars = new List<string>();
        var called = new Dictionary<int, (int Front, int Side)>();
        int failures = 0, shutDoors = 0, stoodDown = 0, timedOut = 0, capped = 0;
        string firstTimeout = null;
        string firstFailure = null, doorProbe = "not tried";
        var carRx = new Regex(@"^\[HIS\] PoliceCars: (\S+) pulling up");
        // The probes' waves, from the tier table (Brett 0.1.57's five): the first whose men take cover to reload, the plated one.
        int reloadWave = 0, plateWave = 0;
        for (int w = HitmanTier.Waves; w >= 1; w--) { if (HitmanTier.For(w).CoverOnReload) reloadWave = w; if (HitmanTier.For(w).Plated) plateWave = w; }
        var waveRx = new Regex(@"^\[HIS\] Hitmen: wave (\d) \(.*\) called: (\d) front \+ (\d) side");
        var beatenRx = new Regex(@"^\[HIS\] Hitmen: wave (\d) beaten");
        // The doorbell feed (the fixer, 0.1.58): kept live while a car drives in, left to hold its frame in a lull once every car
        // out is parked and still (PoliceCars.HitmenStill) — per wave: kept live 1–3 s after its call, held in the lull after it.
        var calledAt = new Dictionary<int, float>(); var beatenAt = new Dictionary<int, float>();
        var feedLiveOnDrive = new HashSet<int>(); var feedIdleInLull = new HashSet<int>();
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            var m = carRx.Match(msg); if (m.Success) cars.Add(m.Groups[1].Value);
            m = waveRx.Match(msg); if (m.Success) { called[int.Parse(m.Groups[1].Value)] = (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); calledAt[int.Parse(m.Groups[1].Value)] = Time.time; }
            m = beatenRx.Match(msg); if (m.Success) beatenAt[int.Parse(m.Groups[1].Value)] = Time.time;
            if (msg.Contains("swings shut")) shutDoors++;
            if (msg.Contains("stood down")) stoodDown++;
            if (msg.StartsWith("[HIS] Hitmen: wave ") && msg.Contains(" timed out after ")) { timedOut++; firstTimeout ??= msg; }
            if (msg.StartsWith("[HIS] Hitman #") && msg.Contains(" s walking in — fights from ")) capped++;
            if (type == LogType.Exception || msg.Contains("brain failed")) { failures++; firstFailure ??= msg; }
        };
        var men = new Dictionary<IntruderT, HitLog>();
        var seenRx = new Regex(@"gun low ([\d.]+) s, raise ([\d.]+) s, no round before ([\d.]+) s");
        Action<IntruderT, string> onReport = (o, r) =>
        {
            if (o == null || !o.IsHitman) return;
            if (!men.TryGetValue(o, out var t)) men[o] = t = new HitLog { Wave = o.Wave };
            if (r.StartsWith("sees the player") && t.FiredAt < 0f)
            {
                var m = seenRx.Match(r);
                t.SeenAt = Time.time; t.RaisedAt = -1f; t.Behind = r.Contains("from behind");
                if (m.Success)
                {
                    t.React = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    t.Raise = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    t.Window = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                }
            }
            else if (r.StartsWith("raises the gun") && t.RaisedAt < 0f) t.RaisedAt = Time.time;
            else if ((r == "fired hit" || r == "fired miss") && t.FiredAt < 0f) t.FiredAt = Time.time;
            else if (r == "fighting" && t.InRoomAt < 0f) t.InRoomAt = Time.time;
            else if (r == "at the door" && t.StackedAt < 0f) t.StackedAt = Time.time;
            else if (r.StartsWith("reloading")) t.Reloads++;
            else if (r == "reloaded") t.Reloaded = true;
            else if (r.StartsWith("steps out of your line")) t.Covers++;
            else if (r.StartsWith("no cover within")) t.NoCover++;
            else if (r == "the plate stopped a round") t.Plated = true;
            else if (r.StartsWith("answers through the door")) t.Volleys++;
            else if (r.StartsWith("fired blind")) t.BlindRounds++;
            else if (r.StartsWith("finder: goes for the witness")) t.Finder = true;
        };
        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        bool flashed = false, volleyProbed = false;
        // Milestone A's bare night (Ricky, then the waves straight to the card), inside its budget: the full night with the
        // visitors, the cut and the relief is RunMarshalNightHeadless (ScenarioTests.MarshalNight.cs).
        bool visitorsWere = ScenarioDirector.MarshalVisitors;
        ScenarioDirector.MarshalVisitors = false;
        int finders = 0;
        var arrival = new ArrivalWatch();          // each wave heard and seen coming (Brett 0.1.56; ScenarioTests.MarshalArrival.cs)
        try
        {
            // The pause menu's Mode row (GamePause.ApplyModePick) lands here: the room starts over in Marshal mode.
            int nightsBefore = dir.NightsDone;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            if (dir.CurrentMode == ScenarioDirector.Mode.Marshal) ExtraPass("marshal: mode switch", "SetMode(Marshal) → MarshalNight");
            else { ExtraFail("marshal: mode switch", $"mode {dir.CurrentMode}"); return; }

            var police = PoliceResponse.Instance;
            var front = HIS.Room.VirtualDoor.Instance;
            float until = Time.realtimeSinceStartup + 1100f;
            while (dir.NightsDone == nightsBefore && Time.realtimeSinceStartup < until && EditorApplication.isPlaying)
            {
                if (Shader.GetGlobalVector("_HIS_FlashColA") != Vector4.zero || Shader.GetGlobalVector("_HIS_FlashColB") != Vector4.zero) flashed = true;
                arrival.Poll();                                            // the monitor's VEHICLE label
                // (PoliceCars' own call, not SecurityCamera.Live: batch mode never renders, so a Refresh there never clears.)
                var pcs = HIS.Security.PoliceCars.Instance;
                if (pcs != null && police != null && police.Active)
                {
                    int w = police.Wave;
                    if (calledAt.TryGetValue(w, out var ca) && Time.time - ca >= 1f && Time.time - ca <= 3f && pcs.Active && !pcs.FeedHeldForCars) feedLiveOnDrive.Add(w);
                    if (beatenAt.TryGetValue(w, out var ba) && Time.time - ba >= 1f && pcs.FeedHeldForCars) feedIdleInLull.Add(w);
                }
                if (police != null && police.Active)
                    foreach (var o in new List<IntruderT>(police.Officers))
                    {
                        if (o == null || o.Current is IntruderT.State.Dead or IntruderT.State.Gone || !men.TryGetValue(o, out var t)) continue;
                        // Probe: the front breacher of the first stack whose tier answers through a door (BlindVolley: wave 2's
                        // street crew on) at a SHUT front door (a body in the doorway keeps it open: Question 5) — a round of yours
                        // through the leaf (a line past it that meets nobody), and he answers along it.
                        if (HitmanTier.For(t.Wave).BlindVolley > 0 && !volleyProbed && !t.ProbedDoor && t.StackedAt >= 0f && Time.time - t.StackedAt > 0.3f && o.Role == OfficerRole.Lead
                            && o.DoorIndex == 0 && front != null && !front.IsOpen && police.EntryNow == false)
                        { t.ProbedDoor = volleyProbed = true; doorProbe = $"wave {t.Wave}: " + ShootThroughShutDoor(front, m9); }
                        // Each wave's finder goes for Ricky and would end the night (THE TRIAL IS OFF: RunMarshalFinderHeadless
                        // checks that): this run is about the waves, so he goes down as he sets off for him.
                        if (t.Finder) { finders++; o.TakeDamage(1000f); continue; }
                        if (t.InRoomAt < 0f) continue;
                        // Probe: a chest round on a plated man (wave 5's operators) once he's in the room (his plate).
                        if (t.Wave == plateWave && t.PlateShotAt < 0f && !o.BehindDoor) { t.PlateShotAt = Time.time; ShootOfficer(o, HumanBodyBones.Chest); continue; }
                        float since = Time.time - t.InRoomAt;
                        bool done = t.Wave == reloadWave
                            ? t.Reloaded || since > 16f                                                  // through a magazine (the reload, the cover)
                            : (t.FiredAt >= 0f && Time.time - t.FiredAt > 0.3f && (t.PlateShotAt < 0f || Time.time - t.PlateShotAt > 0.8f)) || since > 8f;
                        if (done) o.TakeDamage(1000f);
                    }
                await Wait(0.1f);
            }
            if (dir.NightsDone == nightsBefore) { ExtraFail("marshal: the night reaches its card", $"still {dir.Status} after 1100 s"); return; }

            // 1. The cars and the wave sizes (Brett 0.1.57: five waves, each in its own vehicle; 2 / 3 / 2 / 3 / 3 = 13 men with one
            //    door or two).
            string carInfo = string.Join(", ", cars);
            var wantCars = new List<string>();
            for (int w = 1; w <= HitmanTier.Waves; w++) wantCars.Add(HitmanTier.For(w).Vehicle);
            bool carsOk = HitmanTier.Waves == 5 && cars.Count == wantCars.Count;
            for (int i = 0; carsOk && i < cars.Count; i++) carsOk = cars[i] == wantCars[i];
            MarshalCheck(carsOk, "marshal: five waves, each in its own vehicle (the tier table's)", $"{carInfo} (want {string.Join(", ", wantCars)})");
            var sizeList = new List<string>(); int menCalled = 0; bool sizesOk = called.Count == HitmanTier.Waves;
            int[] wantSizes = { 2, 3, 2, 3, 3 };
            for (int w = 1; w <= HitmanTier.Waves; w++)
            {
                called.TryGetValue(w, out var got);
                sizeList.Add($"{got.Front + got.Side}");
                menCalled += got.Front + got.Side;
                sizesOk &= w - 1 < wantSizes.Length && got.Front + got.Side == wantSizes[w - 1];
            }
            string sizes = $"{string.Join(" / ", sizeList)} = {menCalled} ({(twoDoors ? "two doors" : "one door")})";
            MarshalCheck(sizesOk && menCalled == 13, "marshal: wave sizes 2 / 3 / 2 / 3 / 3 (13 men)", sizes);
            // Front / side per wave, against the tier table for this room's doors (two doors: 2 + 0, 1 + 1, 2 + 1; one: all front).
            bool splitOk = true; var splitInfo = new List<string>();
            for (int w = 1; w <= HitmanTier.Waves; w++)
            {
                called.TryGetValue(w, out var got);
                HitmanTier.For(w).Split(twoDoors, out int wantFront, out int wantSide);
                splitOk &= got.Front == wantFront && got.Side == wantSide;
                splitInfo.Add($"w{w} {got.Front} front + {got.Side} side (want {wantFront} + {wantSide})");
            }
            string splitText = $"{string.Join("; ", splitInfo)} ({(twoDoors ? "two doors" : "one door")})";
            if (splitOk) ExtraPass("marshal: each wave's front / side split", splitText); else ExtraFail("marshal: each wave's front / side split", splitText);
            if (!flashed) ExtraPass("marshal: no light-bar flash on the room", "_HIS_FlashColA/B zero all night");
            else ExtraFail("marshal: no light-bar flash on the room", "a flash colour was set");
            // Each wave warns you before the door (Brett 0.1.56: "the bad guys seemingly come from no where"): the engine at the
            // call, VEHICLE on the monitor, Ricky's line, a car door per rider, steps up the path, ≥ 6 s from engine to stack.
            arrival.Check("marshal", 1, 2, 3, 4, 5);
            arrival.CheckLabel("marshal");

            // 2. The tell: no normal round before the man's window (react + raise, ≥ MinWindow, +0.5 s from behind), and his gun
            //    low until his raise (no raise before `react`).
            int fired = 0, fair = 0; var unfair = new List<string>(); var perWave = new int[HitmanTier.Waves + 1];
            foreach (var kv in men)
            {
                var t = kv.Value;
                if (t.FiredAt < 0f || t.SeenAt < 0f) continue;
                fired++;
                float gap = t.FiredAt - t.SeenAt, raiseGap = t.RaisedAt >= 0f ? t.RaisedAt - t.SeenAt : -1f;
                // The window he reported, against the tier table (not just his own word for it): his react within the tier's
                // range, the raise the tier's, and window = max(MinWindow, react + raise) + BehindExtra from behind (research/27
                // §2). The report prints two decimals: ±0.005 each.
                var tier = HitmanTier.For(t.Wave);
                float expected = Mathf.Max(HitmanTier.MinWindow, t.React + tier.Raise) + (t.Behind ? HitmanTier.BehindExtra : 0f);
                bool table = t.React >= tier.React.x - 0.006f && t.React <= tier.React.y + 0.006f && Mathf.Abs(t.Raise - tier.Raise) < 0.006f
                             && Mathf.Abs(t.Window - expected) < 0.015f;
                bool ok = table && t.Window >= HitmanTier.MinWindow - 1e-3f && gap >= t.Window - 0.03f && raiseGap >= t.React - 0.03f;
                if (ok) { fair++; if (t.Wave >= 1 && t.Wave <= HitmanTier.Waves) perWave[t.Wave]++; }
                else unfair.Add($"#{kv.Key.Id} w{t.Wave}: first round {gap:F2} s after he saw you, window {t.Window:F2} (tier says {expected:F2}" +
                                $"{(t.Behind ? ", from behind" : "")}), raise at {raiseGap:F2} (react {t.React:F2} of {tier.React.x:F2}–{tier.React.y:F2}, raise {t.Raise:F2} of {tier.Raise:F2})");
            }
            var perWaveText = new List<string>(); for (int w = 1; w <= HitmanTier.Waves; w++) perWaveText.Add($"w{w} {perWave[w]}");
            string tellInfo = $"{fair}/{fired} men's first rounds after their window ({string.Join(", ", perWaveText)}); {men.Count} hitmen reported";
            if (fired > 0 && unfair.Count == 0) ExtraPass("marshal: the tell (no round before the window)", tellInfo);
            else ExtraFail("marshal: the tell (no round before the window)", fired == 0 ? $"no hitman fired a round ({tellInfo})" : $"{tellInfo}; {string.Join("; ", unfair)}");

            // 3. The probes: the blind volley, the reload (and cover), the plate.
            int volleys = 0, blind = 0, reloads = 0, reloaded = 0, covers = 0, noCover = 0, plateShots = 0, plated = 0;
            foreach (var t in men.Values)
            {
                volleys += t.Volleys; blind += t.BlindRounds; reloads += t.Reloads; if (t.Reloaded) reloaded++;
                if (t.Wave == reloadWave) { covers += t.Covers; noCover += t.NoCover; }
                if (t.PlateShotAt >= 0f) { plateShots++; if (t.Plated) plated++; }
            }
            string volleyInfo = $"your round: {doorProbe}; {volleys} volley(s), {blind} blind round(s)";
            if (volleys > 0 && blind > 0) ExtraPass("marshal: a round through his shut door is answered (blind volley)", volleyInfo);
            else ExtraFail("marshal: a round through his shut door is answered (blind volley)", volleyInfo);
            string reloadInfo = $"{reloads} reload(s) started, {reloaded} man/men reloaded; wave {reloadWave} cover: {covers} step(s) out of line, {noCover} with no cover near";
            if (reloads > 0 && reloaded > 0) ExtraPass($"marshal: wave {reloadWave} reloads (and takes cover for it)", reloadInfo);
            else ExtraFail($"marshal: wave {reloadWave} reloads (and takes cover for it)", reloadInfo);
            string plateInfo = $"{plated}/{plateShots} chest round(s) stopped by the plate";
            if (plateShots > 0 && plated == plateShots) ExtraPass($"marshal: wave {plateWave}'s plate stops the first chest round", plateInfo);
            else ExtraFail($"marshal: wave {plateWave}'s plate stops the first chest round", plateInfo);

            // 4. The card.
            var v = dir.LastVerdict.GetValueOrDefault();
            string card = $"{police?.Ending}: {v.Headline} — {v.Reason} / {v.Sentence}";
            // (The bare night's card: "Hitmen stopped: N of 13." — every man of the five tiers counted, whatever was stopped.)
            MarshalCheck(police != null && police.Ending == StandoffEnding.Delivered && v.Headline == "WITNESS DELIVERED" && v.Tier == Tier.Perfect
                         && v.Sentence != null && v.Sentence.Contains($"of {HitmanTier.Total(twoDoors)}.") && HitmanTier.Total(twoDoors) == 13,
                  "marshal: WITNESS DELIVERED, of 13", card);

            // 5. Every wave beaten, none on its timeout (the fixer, 0.1.58: a wave-4 lead and cover fired from just inside the
            //    doorway for 150 s and never finished walking in — every shot reset their walk's stuck clock — so the wave ended
            //    on its timeout: "Hitmen stopped: 11 of 13"). Each man shooting from the doorway must still come in and fight
            //    ("fighting": this run puts him down then), so every wave ends beaten.
            MarshalCheck(timedOut == 0 && v.Sentence != null && v.Sentence.Contains($"Hitmen stopped: {HitmanTier.Total(twoDoors)} of {HitmanTier.Total(twoDoors)}."),
                  "marshal: every wave is beaten, none ends on its timeout (a man firing from the doorway still comes in and fights)",
                  $"{timedOut} wave(s) timed out{(firstTimeout != null ? $" (first: {firstTimeout.Substring(6)})" : "")}; {capped} walk(s) in ended by the cap; card: {v.Sentence}");

            // 6. The doorbell feed (the fixer, 0.1.58: the hitmen's parked cars kept it rendering at 12 Hz from the first car to
            //    the 2:00 AM cut): the cars keep it live as each drives in, and let it hold its frame in the lull after each of
            //    waves 1–4 (every car out parked and still) — the GPU's quiet stretches stay quiet.
            var driveInfo = new List<string>(); bool feedOk = true;
            for (int w = 1; w <= HitmanTier.Waves; w++)
            {
                bool live = feedLiveOnDrive.Contains(w), idle = w == HitmanTier.Waves || feedIdleInLull.Contains(w);
                feedOk &= live && idle;
                driveInfo.Add($"w{w} kept live on the drive-in {live}, held in the lull {(w == HitmanTier.Waves ? "(last wave)" : idle.ToString())}");
            }
            MarshalCheck(feedOk, "marshal: the cars keep the feed live while one drives in, and let it hold once they're parked and still", string.Join("; ", driveInfo));

            // 7. Nothing broke on the way.
            if (failures == 0) ExtraPass("marshal: no brain failure or exception", $"{shutDoors} broken door(s) swung shut between waves, {stoodDown} stand-down(s), {finders} finder(s) stopped");
            else ExtraFail("marshal: no brain failure or exception", $"{failures}, first: {firstFailure}");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            arrival.Dispose();
            ScenarioDirector.MarshalVisitors = visitorsWere;
            foreach (var g in guns) if (g != null && g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject);
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }
    }

    /// <summary>
    /// One real round of the player's (the M9) from 1.4 m inside the shut <paramref name="door"/> out through its leaf, along a
    /// line that meets nobody past it (the stacked men stand beside the door): the leaf records it (VirtualDoor.LastInbound*)
    /// and the man who'd kick that door answers along it. Returns what happened, for the report.
    /// </summary>
    static string ShootThroughShutDoor(HIS.Room.VirtualDoor door, DsGun gun)
    {
        if (gun == null || gun.Gun == null) return "no gun";
        var inward = Vector3.ProjectOnPlane(door.Inward, Vector3.up).normalized;
        var muzzle = door.Threshold + inward * 1.4f + Vector3.up * 1.3f;
        int mask = ~0, wl = LayerMask.NameToLayer("Weapon"), dl = LayerMask.NameToLayer("Door");
        if (wl >= 0) mask &= ~(1 << wl);
        if (dl >= 0) mask &= ~(1 << dl);
        foreach (float deg in new[] { 0f, 20f, -20f, 35f, -35f })
            foreach (float drop in new[] { 0.9f, 0.4f })
            {
                // Out through the leaf's middle, down into the porch a few metres out.
                var aim = door.Threshold + Quaternion.AngleAxis(deg, Vector3.up) * -inward * 3f + Vector3.up * drop;
                var dir = (aim - muzzle).normalized;
                if (!door.RaycastLeaf(muzzle, dir, 6f, out var leaf)) continue;
                if (Physics.Raycast(leaf.ExitPoint + dir * 0.002f, dir, out var hit, 8f, mask, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<IntruderT>() != null) continue;   // someone's on that line
                float before = door.LastInboundAt;
                DsFire(gun.Gun, muzzle, aim);
                return door.LastInboundAt > before ? $"through the leaf at {deg:F0}° (recorded from {door.LastInboundFrom:F2})" : $"fired at {deg:F0}° but the leaf didn't record it";
            }
        return "no clear line through the leaf";
    }
}
