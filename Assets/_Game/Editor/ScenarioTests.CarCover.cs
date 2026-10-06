using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Scenario;
using HIS.Security;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Brett 0.1.54: "the swat especially after the car crashes walk through the swat car and also shoot you through it". The
/// vehicles out front are cover both ways and nobody walks through one:
///   • a pile-up (the SWAT truck driven into the parked cruiser) — the way from the truck's door to the step goes round both;
///   • an officer behind the parked truck, the player in line with it through the open doorway: holds fire (the round would
///     stop in the truck); the same officer off to the side, a clear line through the doorway: fires.
/// Brett 0.1.57's five hitman waves ("it should be mostly the mob coming after him"; HitmanWaveChecks below):
///   • the tiers get harder every wave and never ridiculous; 13 men; the looks street / street / pro / pro / operator;
///   • each wave's man dressed by his look (wave 2 a street man, wave 4 a pro, wave 5 plated with the SMG);
///   • the five vehicles, sent in wave order: each its own, every arrival with its engine and VEHICLE, a clean drive in
///     (clear of the ones parked, no crash, no shove), parked with no overlap, off the doorway's line on the doorbell's wide
///     side, in the camera's picture, lights out; each one cover (a round stops in it, the way round it is clear).
///   Just these: scripts/unity-batch.sh ScenarioTests.RunCarCoverHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunCarCoverHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 420f;   // (+ the five waves' checks, 0.1.58)
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
                HIS.Core.PlayerState.GodMode = true;
                ScenarioDirector.WaitForForced = true;
                PoliceResponse.Enabled = false;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun())
                {
                    await CarCoverChecks();
                    await HitmanWaveChecks();   // Brett 0.1.57's five waves (the tiers, the looks, the five vehicles)
                }
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task CarCoverChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30)) { ExtraFail("car cover: room", spawner?.Status); return; }
        if (PoliceResponse.Instance == null) new GameObject("[HIS] PoliceResponse (test)").AddComponent<PoliceResponse>();
        var pc = PoliceCars.Instance;
        var door = HIS.Room.VirtualDoor.Instance;
        if (pc == null || door == null) { ExtraFail("car cover", "no cars / door"); return; }
        door.Breach();
        await WaitFor(() => door.IsOpen && door.OpenAngle > 60f, 3);
        float floorY = door.Threshold.y;
        int truck = PoliceCars.VehicleFor(3, SquadFaction.Police);

        // ── 1. The pile-up: the cruiser parked, the SWAT truck crashed into it; the way from the truck's door to the step.
        {
            const string what = "car cover: the way from the crashed SWAT truck to the step goes round both vehicles";
            pc.End();
            pc.Send(0); pc.Advance(30f);                                       // the cruiser parked in front
            pc.Send(truck);
            bool crashed = false;
            for (float along = 0.95f; along >= 0.6f && !crashed; along -= 0.05f)
                for (int i = 0; i < 400 && !crashed; i++) { pc.Advance(0.05f); crashed = pc.CrashNow(truck, along); }
            bool over = false;
            for (int i = 0; i < 300 && crashed && !over; i++) { pc.Advance(0.05f); over = pc.CrashOver(truck, out _, out _); }
            if (!crashed || !over) ExtraFail(what, $"the truck didn't crash to a stop (crashed {crashed}, over {over})");
            else
            {
                pc.Advance(3f);                                                // the doors open a beat after it stops (GetOut)
                var step = door.Center + FlatV(door.Outward).normalized * 1.2f; step.y = floorY;
                var worst = new List<string>(); var stepped = new List<string>(); int legs = 0, blocked = 0;
                for (int seat = 0; seat < 4; seat++)
                {
                    if (!PoliceCars.GetOut(3, seat, out var exit, SquadFaction.Police)) continue;
                    exit.y = floorY;
                    var route = PoliceCars.RouteAround(exit, step);
                    var prev = exit;
                    route.Add(step);
                    foreach (var p in route)
                    {
                        legs++;
                        if (PoliceCars.SegmentBlocked(prev, p))
                        {
                            // A leg from inside another vehicle's padded footprint (0.15–0.5 m off it: the crash shoves the vehicles up
                            // against each other's exits) is "blocked" whichever way it goes — stepping away from it isn't walking through
                            // it: that counts only if it comes within 0.15 m of a footprint on the way (2026-10-05).
                            string where = $"seat {seat}: {prev:F1}→{p:F1} (leg {legs}, from {(PoliceCars.InFootprint(prev, 0.15f) ? "IN a footprint" : PoliceCars.InFootprint(prev) ? "inside a padded footprint" : "clear")}, " +
                                           $"to {(PoliceCars.InFootprint(p) ? "inside a padded footprint" : "clear")}, exit {exit:F1})";
                            if (SteppingOut(prev, p)) stepped.Add(where);
                            else { blocked++; worst.Add(where); }
                        }
                        prev = p;
                    }
                }
                string info = $"{legs} legs from the truck's doors, {blocked} through a vehicle{(worst.Count > 0 ? ": " + string.Join("; ", worst) : "")}" +
                              $"{(stepped.Count > 0 ? $"; {stepped.Count} stepping away from one it starts beside: " + string.Join("; ", stepped) : "")}";
                if (legs > 0 && blocked == 0) ExtraPass(what, info); else ExtraFail(what, legs == 0 ? "no exits (GetOut)" : info);
            }
        }

        // ── 2. The parked truck between an officer and the player: no round through it.
        pc.End();
        pc.Send(truck); pc.Advance(30f);
        pc.Crashed(truck, out _, out var truckRoot);
        var def = new ScenarioDef { Name = "Car-cover officer", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
        var cop = spawner.SpawnScenario(def, 0);
        if (truckRoot == null || cop == null) { ExtraFail("car cover", "no truck / officer"); return; }
        await Wait(1.5f);
        var fire = typeof(IntruderT).GetMethod("FireShot", WsNP);
        var tellDone = typeof(IntruderT).GetField("_tellDoneAt", WsNP);
        var player = typeof(IntruderT).GetField("_player", WsNP);
        var gunUp = typeof(IntruderT).GetMethod("GunUp", WsNP, null, Type.EmptyTypes, null);
        var anim = cop.GetComponentInChildren<IntruderAnimator>();
        if (fire == null || tellDone == null || player == null || anim == null || anim.Muzzle == null) { ExtraFail("car cover", "internals renamed, or no gun in hand"); return; }
        cop.StopAllCoroutines();
        var agent = cop.GetComponent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
        gunUp?.Invoke(cop, null);
        var head = new GameObject("[test] player head").transform;
        player.SetValue(cop, head);
        try
        {
            var d = door.Center; d.y = floorY;
            var toTruck = FlatV(truckRoot.position - d); float truckDist = toTruck.magnitude; toTruck /= Mathf.Max(truckDist, 1e-3f);
            async Task<bool> Fires(Vector3 dirOut, string label)
            {
                // The player 1.5 m inside on the line, the officer 3.5 m past the truck's middle on it: the line runs through the doorway's middle.
                head.position = d - dirOut * 1.5f + Vector3.up * 1.6f;
                var at = d + dirOut * (truckDist + 3.5f);
                cop.SetVisible(true);
                cop.transform.position = new Vector3(at.x, floorY, at.z);
                cop.transform.rotation = Quaternion.LookRotation(FlatV(head.position - cop.transform.position).normalized, Vector3.up);
                var chest = head.position + Vector3.down * 0.35f;
                float until = Time.time + 0.6f;
                await WaitFor(() => { anim.AimAt(chest); return Time.time > until; }, 5);
                Physics.SyncTransforms();
                tellDone.SetValue(cop, 0f);
                bool fired = (bool)fire.Invoke(cop, new object[] { 1f });
                Debug.Log($"[HIS] SCENARIO car cover {label}: officer {Vector3.Distance(cop.transform.position, d):F1} m out → {(fired ? "fired" : "held")}");
                return fired;
            }
            bool behind = await Fires(toTruck, "behind the truck");
            if (!behind) ExtraPass("car cover: behind the parked truck, in line with the player — holds fire", "the truck is between them");
            else ExtraFail("car cover: behind the parked truck", "he fired through the truck (0.1.54 bug)");
            var aside = Quaternion.Euler(0f, 30f, 0f) * toTruck;
            if (Vector3.Dot(aside, FlatV(door.Outward)) < 0.2f) aside = Quaternion.Euler(0f, -30f, 0f) * toTruck;
            bool clear = await Fires(aside, "off to the side");
            if (clear) ExtraPass("car cover: off to the side, a clear line through the doorway — fires", "nothing in the way");
            else ExtraFail("car cover: off to the side", "he held fire with a clear line");
        }
        finally { UnityEngine.Object.Destroy(head.gameObject); pc.End(); }
    }

    // ───────────────────────────── the five hitman waves (Brett 0.1.57) ─────────────────────────────

    /// <summary>The least angle (°) any corner of a parked hitman vehicle may sit off the front door's axis: today's grey sedan's nearest is ~36°.</summary>
    const float HitmanCarMinAngle = 34f;

    static void WaveCheck(bool ok, string what, string info) { if (ok) ExtraPass(what, info); else ExtraFail(what, info); }

    /// <summary>
    /// Brett 0.1.57 ("it should be mostly the mob coming after him"): five waves, each harder (B1), five vehicles (B2), Ricky's
    /// line for each car (B3). The tiers as data, a man of each wave dressed, then the vehicles on the porch set.
    /// </summary>
    static async Task HitmanWaveChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        var pc = PoliceCars.Instance;
        var door = HIS.Room.VirtualDoor.Instance;
        if (spawner == null || pc == null || door == null) { ExtraFail("five waves: setup", $"spawner {spawner != null}, cars {pc != null}, door {door != null}"); return; }
        HitmanTierChecks();
        await HitmanLookChecks(spawner, door);
        try { await HitmanVehicleChecks(pc, door); }
        finally { pc.End(); pc.DropHitmanCars(); }
    }

    /// <summary>
    /// The tier table (B1): five waves; react (both ends) and raise down every wave, accuracy up every wave and never over 0.9,
    /// aim lag down, the tell never under MinWindow; the plate on wave 5 alone; looks street, street, pro, pro, operator; 2 + 3
    /// + 2 + 3 + 3 = 13 men with one door or two; five different vehicles; Ricky's five lines, each with its text.
    /// </summary>
    static void HitmanTierChecks()
    {
        int n = HitmanTier.Waves;
        bool harder = n == 5, fair = true;
        var info = new List<string>();
        for (int w = 1; w <= n; w++)
        {
            var t = HitmanTier.For(w);
            fair &= t.Accuracy <= 0.9f && t.PathAccuracy <= 0.9f && t.React.x + t.Raise >= HitmanTier.MinWindow - 1e-4f && t.Wave == w;
            if (w > 1)
            {
                var p = HitmanTier.For(w - 1);
                harder &= t.React.x < p.React.x && t.React.y < p.React.y && t.Raise < p.Raise && t.Accuracy > p.Accuracy && t.AimLag < p.AimLag
                          && t.Magazine > p.Magazine && t.Reload < p.Reload;
            }
            info.Add($"w{w} {t.Name}: react {t.React.x:F2}–{t.React.y:F2}, raise {t.Raise:F2}, accuracy {t.Accuracy:F2}, lag {t.AimLag:F2}, mag {t.Magazine}");
        }
        WaveCheck(harder && fair, "five waves: harder every wave (react and raise down, accuracy up), never ridiculous (≤ 0.9, the tell ≥ MinWindow)",
                     $"{n} waves: {string.Join("; ", info)}");

        var looks = new List<int>(); var plated = new List<int>(); var cars = new HashSet<string>(); var men = new List<string>();
        for (int w = 1; w <= n; w++)
        {
            var t = HitmanTier.For(w);
            looks.Add(t.Look);
            if (t.Plated) plated.Add(w);
            if (!string.IsNullOrEmpty(t.Vehicle)) cars.Add(t.Vehicle);
            t.Split(true, out int f2, out int s2); t.Split(false, out int f1, out int s1);
            men.Add($"{f2}+{s2}/{f1 + s1}");
        }
        string lookList = string.Join(",", looks);
        bool menOk = HitmanTier.Total(true) == 13 && HitmanTier.Total(false) == 13 && string.Join(" ", men) == "2+0/2 2+1/3 1+1/2 2+1/3 2+1/3";
        WaveCheck(lookList == "1,1,2,2,3" && plated.Count == 1 && plated[0] == 5 && cars.Count == 5 && menOk,
                     "five waves: street, street, pro, pro, operator; the plate on wave 5 only; five vehicles; 13 men",
                     $"looks {lookList}, plated wave(s) {string.Join(",", plated)}, {cars.Count} vehicle names ({string.Join(", ", cars)}), " +
                     $"men {string.Join(" ", men)} = {HitmanTier.Total(true)} (two doors) / {HitmanTier.Total(false)} (one)");

        string[] want = { "That's not the relief.", "Another car.", "They're back.", "More of them.", "That's a van. Oh no." };
        var said = new List<string>(); bool linesOk = n == want.Length;
        for (int w = 1; w <= n && w <= want.Length; w++)
        {
            var text = VoiceLines.Text(HitmanTier.For(w).RickyLine);
            said.Add($"w{w} \"{text ?? HitmanTier.For(w).RickyLine + ": no text"}\"");
            linesOk &= text == want[w - 1];
        }
        WaveCheck(linesOk, "five waves: Ricky calls every car with its own line", string.Join(", ", said));
    }

    /// <summary>
    /// One man of each wave, dressed (DressAsHitman by the tier's Look): waves 1–2 in the street men's beanie, waves 3–4 in the
    /// pros' suits, wave 5 the operators' kit with the SMG and the plate (and its deeper report); nobody else plated or with an SMG.
    /// </summary>
    static async Task HitmanLookChecks(IntruderSpawner spawner, HIS.Room.VirtualDoor door)
    {
        string[] kit = { "beanie", "beanie", "suit", "suit", "operator" };
        var got = new List<string>(); bool ok = true;
        for (int w = 1; w <= HitmanTier.Waves; w++)
        {
            var def = new ScenarioDef { Name = $"Hitman w{w} (looks)", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
            var o = spawner.SpawnScenario(def, 0);
            if (o == null) { ok = false; got.Add($"w{w}: no spawn"); continue; }
            var start = door.Center + FlatV(door.Outward).normalized * 6f;
            o.ConfigureOfficer(OfficerRole.Cover, w, start, -1, -1, 0, SquadFaction.Hitmen);
            float pitch = 1f;                                                    // (the lowest report pitch of his guns: the operators' is 0.85)
            foreach (var fx in o.GetComponentsInChildren<IntruderGunFx>(true)) pitch = Mathf.Min(pitch, fx.Pitch);
            bool five = w == HitmanTier.Waves;
            bool right = o.IsHitman && o.HitmanKit == kit[Mathf.Min(w, kit.Length) - 1] && o.HasSmg == five && o.Armored == five && (pitch < 1f) == five;
            ok &= right;
            got.Add($"w{w}: {o.HitmanKit ?? "undressed"}{(o.HasSmg ? " + SMG" : "")}{(o.Armored ? " + plate" : "")}, report ×{pitch:F2}{(right ? "" : " ✗")}");
            o.StopAllCoroutines();
            UnityEngine.Object.Destroy(o.gameObject);
            await Wait(0.1f);
        }
        WaveCheck(ok, "five waves: wave 2 dressed as street, wave 4 as pros, wave 5 plated with the SMG", string.Join("; ", got));
    }

    /// <summary>
    /// The five vehicles (B2), sent one after another in wave order as a night sends them: each its tier's own (distinct names);
    /// each drive in clear of the ones already parked at every 0.05 s step and never crashed or shoved; its engine heard and the
    /// doorbell's VEHICLE raised; parked with no two footprints overlapping, every corner ≥ <see cref="HitmanCarMinAngle"/>° off
    /// the door's axis on the doorbell's wide side, all of it in the camera's picture, its lights out. Then each alone: cover —
    /// a round from its house side stops in its body, the straight walk across it is blocked and the way round it is clear.
    /// </summary>
    static async Task HitmanVehicleChecks(PoliceCars pc, HIS.Room.VirtualDoor door)
    {
        var porch = PorchSet.For(door);
        var cam = SecurityCamera.For(door);
        if (porch == null || cam == null) { ExtraFail("five waves: the vehicles", $"porch {porch != null}, camera {cam != null} at the front door"); return; }
        var engines = new HashSet<string>();
        var engineRx = new System.Text.RegularExpressions.Regex(@"^\[HIS\] PoliceCars: (\S+) engine \(wave (\d)\)");
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            var m = engineRx.Match(msg);
            if (m.Success && !msg.Contains("not heard")) lock (engines) engines.Add($"{m.Groups[1].Value} {m.Groups[2].Value}");
        };
        Application.logMessageReceived += onLog;
        try
        {
            pc.End();
            int n = HitmanTier.Waves;
            var v = new int[n + 1];
            var clashes = new List<string>();
            var arrivals = new List<string>();
            bool sentAll = true;
            for (int w = 1; w <= n; w++)
            {
                v[w] = PoliceCars.VehicleFor(w, SquadFaction.Hitmen);
                // (The drive-ins below take a few frames, not the night's minute between cars: the last two engines would still
                // be on air, and car.arrive plays at most two at once — the third "not heard" is this test's, never a night's.)
                HIS.Core.Sfx.StopAll();
                if (!pc.Send(v[w])) { sentAll = false; arrivals.Add($"w{w}: not sent"); continue; }
                // The drive in, as the night runs it: in 0.05 s steps, clear of every vehicle out at each.
                float drove = 0f; bool parked = false;
                for (; drove < 15f && !parked; drove += 0.05f)
                {
                    pc.Advance(0.05f);
                    var hit = pc.ClashWith(v[w]);
                    if (hit != null && clashes.Count < 6) clashes.Add($"w{w} into {hit} at +{drove:F2} s");
                    parked = pc.VehicleNow(v[w], out var now) && now.Parked;
                }
                pc.Advance(1.6f);                                                // its lights die 0.7 s after the stop; the doors open
                pc.VehicleNow(v[w], out var i);
                arrivals.Add($"w{w} {i.Name}: parked after {drove:F1} s{(i.Crashed ? ", CRASHED" : "")}{(i.Shoved > 1e-4f ? $", shoved {i.Shoved:F3}" : "")}");
                await Wait(0.05f);
            }
            Physics.SyncTransforms();
            float camSide = porch.transform.InverseTransformPoint(porch.CameraPosition).x;
            var names = new HashSet<string>();
            var each = new List<string>();
            bool own = sentAll, clean = sentAll && clashes.Count == 0, apart = sentAll, offLine = sentAll, seen = sentAll, dark = sentAll, heard = sentAll;
            for (int w = 1; w <= n; w++)
            {
                if (!pc.VehicleNow(v[w], out var i)) { own = false; continue; }
                var tier = HitmanTier.For(w);
                names.Add(i.Name);
                own &= i.Hitman && i.Out && i.Name == tier.Vehicle;
                clean &= i.Parked && !i.Crashed && i.Shoved <= 1e-4f;
                var clash = pc.ClashWith(v[w]);
                if (clash != null) { apart = false; clashes.Add($"{i.Name} parked on {clash}"); }
                float least = 180f; bool wide = true, inPicture = cam.Sees(i.Root + Vector3.up * 0.8f);
                foreach (var k in i.Corners)
                {
                    var l = porch.transform.InverseTransformPoint(k);
                    least = Mathf.Min(least, l.z > 0f ? Mathf.Atan2(Mathf.Abs(l.x), l.z) * Mathf.Rad2Deg : 0f);
                    wide &= l.x * camSide < 0f;                                  // (the doorbell hangs on the latch side: the wide side is the other)
                    inPicture &= cam.Sees(k + Vector3.up * 0.8f);
                }
                offLine &= least >= HitmanCarMinAngle && wide;
                seen &= inPicture && i.Spotted;
                dark &= !i.LampsLit;
                bool engine; lock (engines) engine = engines.Contains($"{i.Name} {w}");
                heard &= engine || !HIS.Core.Sfx.Has(PoliceCars.EngineKey);
                each.Add($"w{w} {i.Name} at {porch.transform.InverseTransformPoint(i.Root).x:F1}, {porch.transform.InverseTransformPoint(i.Root).z:F1} " +
                         $"(nearest corner {least:F0}° off the door's axis{(wide ? "" : ", WRONG SIDE")}, {(inPicture ? "in the picture" : "OUT OF THE PICTURE")}, " +
                         $"VEHICLE {(i.Spotted ? "yes" : "no")}, lights {(i.LampsLit ? "ON" : "out")}, engine {(engine ? "heard" : "not heard")})");
            }
            own &= names.Count == n;
            string all = string.Join("; ", each);
            WaveCheck(own, "five waves: five distinct vehicles, one per wave, each its tier's", $"{names.Count} names: {string.Join(", ", names)}");
            WaveCheck(clean, "five waves: every drive in is clear of the parked ones — no crash, no shove on a clean arrival",
                         $"{string.Join("; ", arrivals)}{(clashes.Count > 0 ? " — " + string.Join("; ", clashes) : "")}");
            WaveCheck(apart, "five waves: parked, no two vehicles overlap", clashes.Count > 0 ? string.Join("; ", clashes) : all);
            WaveCheck(offLine, $"five waves: none across the doorway's line of sight (every corner ≥ {HitmanCarMinAngle:F0}° off the door's axis, the doorbell's wide side)", all);
            WaveCheck(seen, "five waves: each in the doorbell's picture, its VEHICLE alert raised", all);
            WaveCheck(dark && heard, "five waves: each one's engine heard pulling up, its lights out once parked",
                         HIS.Core.Sfx.Has(PoliceCars.EngineKey) ? all : all + $" (no '{PoliceCars.EngineKey}' clip: the engine not checked)");

            // Cover, each alone (no neighbour in the way): a round stops in it, nobody walks through it.
            var cover = new List<string>(); bool covers = true;
            for (int w = 1; w <= n; w++)
            {
                pc.End();
                if (!pc.Send(v[w])) { covers = false; continue; }
                pc.Advance(8f);
                Physics.SyncTransforms();
                if (!pc.VehicleNow(v[w], out var i) || i.Corners == null || i.Corners.Length < 4) { covers = false; continue; }
                var c0 = i.Corners[0]; var c1 = i.Corners[1];
                var mid = (i.Corners[0] + i.Corners[2]) * 0.5f;
                var across = FlatV(c1 - c0); float halfW = across.magnitude * 0.5f; across.Normalize();
                if (Vector3.Dot(across, FlatV(-door.Outward)) < 0f) across = -across;   // toward the house: the side a round from the doorway meets
                var a = mid + across * (halfW + 1.2f); a.y = porch.FloorY;
                var b = mid - across * (halfW + 0.6f); b.y = porch.FloorY;
                // A round at chest height from its house side, toward its middle: stops in its own body.
                bool stops = Physics.Raycast(a + Vector3.up * 0.9f, (mid - a).normalized, out var hit, Vector3.Distance(a, mid) + 2f, 1 << PorchSet.OutsideLayer, QueryTriggerInteraction.Ignore)
                             && hit.collider.transform.IsChildOf(i.RootTransform) && hit.collider.GetComponent<VehicleBody>() != null;
                bool walkBlocked = PoliceCars.SegmentBlocked(a, b) && PoliceCars.InFootprint(mid, 0f);
                var route = PoliceCars.RouteAround(a, b);
                route.Add(b);
                var prev = a; int legs = 0, through = 0;
                foreach (var p in route) { legs++; if (PoliceCars.SegmentBlocked(prev, p)) through++; prev = p; }
                bool ok = stops && walkBlocked && route.Count > 1 && through == 0;
                covers &= ok;
                cover.Add($"{i.Name}: round {(stops ? "stops in it" : $"NOT stopped ({(hit.collider != null ? hit.collider.name : "nothing")})")}, " +
                          $"straight walk {(walkBlocked ? "blocked" : "OPEN")}, the way round {legs} leg(s), {through} through it");
            }
            WaveCheck(covers, "five waves: every vehicle is cover — a round stops in it, the way round it is clear", string.Join("; ", cover));
        }
        finally { Application.logMessageReceived -= onLog; }
    }


    /// <summary>
    /// A leg from <paramref name="a"/> inside a vehicle's padded footprint (but clear of the vehicle itself, ≥ 0.15 m) that never
    /// comes within 0.15 m of any footprint on its way to <paramref name="b"/> (sampled every 5 cm): stepping away, not through.
    /// </summary>
    static bool SteppingOut(Vector3 a, Vector3 b)
    {
        if (!PoliceCars.InFootprint(a) || PoliceCars.InFootprint(a, 0.15f)) return false;
        int n = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(a, b) / 0.05f));
        for (int i = 1; i <= n; i++) if (PoliceCars.InFootprint(Vector3.Lerp(a, b, i / (float)n), 0.15f)) return false;
        return true;
    }

    /// <summary>The vector on the floor plane (y dropped). (ScenarioTests.TwoDoors' Flat returns a flat distance.)</summary>
    static Vector3 FlatV(Vector3 v) { v.y = 0f; return v; }
}
