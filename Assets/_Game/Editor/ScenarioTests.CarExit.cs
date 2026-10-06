using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Scenario;
using HIS.Security;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Brett 2026-10-05: "have the flow of cops exiting look better" (Police/IntruderCarExit.cs, Security/PoliceCars.Exit.cs). The
/// riders get out with Mixamo's Exiting Car instead of popping out standing beside the car:
///   1. a cruiser with three: the doors swing one after another (the driver's ~0.5 s after the stop, the passenger's later),
///      each man plays his side's exit (driver left, passenger right, the back seat out of the driver's door once he's clear),
///      each ends up standing at his exit (GetOut's) within a few seconds of his turn, and none of anyone's hips, head or feet
///      goes back inside the car's shell once it's out past the door's line; (GRAPHICS=1) the driver's exit at a few moments:
///      Logs/poses/car_exit_&lt;clip s&gt;.png and _side, and his first steps off (car_exit_walk_*); walking off, nobody goes
///      through an open door of his car (round its free end: PoliceCars.RouteAround);
///   2. the SWAT truck: its driver out of his cab door (it swings as he pushes it), the box team out of the back one after another (the back doors once),
///      each at his exit, none inside the shell once out; the driver's cab door swings as he pushes it and he walks off round
///      its end (GRAPHICS=1: truck_exit_*.png);
///   3. shot getting out: the passenger before he's up dies there (still in the car's space, where he was hit; his door open),
///      the back-seat man once he's up dies a standing death on the street, the driver goes on out (GRAPHICS=1:
///      car_exit_shot_seat / _up.png); 3b. the driver shot as he comes up beside his door (clip 2.35 s): down on the street, not a
///      rider, no horn, the back seat still out of the driver's door (car_exit_shot_rising.png);
///   4. shot dead in his seat before his turn (the passenger, while it's still driving in): his door stays shut, the driver's opens;
///   5. a Marshal night's sedan (when the hitmen's vehicles can be built here): its two doors' clunks on their own swings, both
///      standing at their exits; 6. the charcoal SUV (its raised body): both out of their sides, at their exits, none back inside
///      its shell (GRAPHICS=1: suv_exit_*.png). Also rendered: the box team out of the truck's back doorway (truck_back_*.png),
///      the cruiser's back-seat man by the driver's seat back (car_exit_back_*.png).
///   Just these: scripts/unity-batch.sh ScenarioTests.RunCarExitHeadless   (renders: GRAPHICS=1)
/// </summary>
public static partial class ScenarioTests
{
    public static void RunCarExitHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 420f;
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
                ScenarioDirector.WaitForForced = true;   // no rounds: only the riders
                PoliceResponse.Enabled = false;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await CarExitChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    /// <summary>One rider followed frame by frame (IntruderT.FrameProbe): his exit clip, his reports, the shell after the door line.</summary>
    class ExitWatch
    {
        public IntruderT Man;
        public int Vehicle, Seat;
        public Transform Car;
        public IntruderAnimator Anim;
        public Transform Hips, Head, LFoot, RFoot;
        /// <summary>Past the door's line: |car x| beyond this (a side door), or car z behind <see cref="BackZ"/> (the box team).</summary>
        public float SideX = 0.935f, BackZ = float.NegativeInfinity;
        public bool Back, Past, SawClip;
        /// <summary>Hips, head, left foot, right foot: out past the door's line yet.</summary>
        public readonly bool[] OutBone = new bool[4];
        public int Side, InsideFrames;
        public string InsideWhere;
        public float GettingOut = -1f, Out = -1f, MaxClip = -1f;
        public Vector3 OutAt, Exit;
        public readonly List<float> Marks = new();
        /// <summary>GRAPHICS=1: the renders' name (Logs/poses/&lt;Shot&gt;_&lt;clip s&gt;.png) and their views (his door's side, x −1: mirrored).</summary>
        public string Shot = "car_exit";
        public (Vector3 eye, Vector3 look) ViewA = (new Vector3(3.4f, 1.5f, 2.6f), new Vector3(0.9f, 0.9f, 0.1f)), ViewB = (new Vector3(4.4f, 1.25f, 0.1f), new Vector3(0.9f, 0.95f, 0.1f));
        /// <summary>GRAPHICS=1: seconds after he's out (his first steps from his door) to render, as &lt;Shot&gt;_walk_&lt;s&gt;.png.</summary>
        public readonly List<float> WalkMarks = new();
        /// <summary>His last place (car space) in his first seconds on foot, and the first open door he walked through (null: none).</summary>
        public Vector3 PrevAt; public bool HavePrev; public string ThroughDoor;
        /// <summary>GRAPHICS=1: seconds after his turn came (the box team: no exit clip) to render, as &lt;Shot&gt;_&lt;s&gt;.png.</summary>
        public readonly List<float> TurnMarks = new();
        /// <summary>Shoot him dead (head) the frame his exit clip reaches this (s; −1 never); the clip time he was shot at.</summary>
        public float ShootAtClip = -1f, ShotAtClip = -1f;
    }

    /// <summary>
    /// His first seconds off his vehicle: does his path cross the leaf of one of its open side doors (hinge to free end, as it
    /// stands)? The riders get out into the pocket behind their open door; walking off they must go round its end.
    /// </summary>
    static void TrackDoorPass(ExitWatch w)
    {
        if (w.Out < 0f || Time.time - w.Out > 3f || w.Car == null || w.Man == null) return;
        var at = w.Car.InverseTransformPoint(w.Man.transform.position); at.y = 0f;
        if (w.HavePrev && w.ThroughDoor == null)
            foreach (Transform h in w.Car)
            {
                if (h.name != "DoorHinge" && h.name != "DoorHingeR" && h.name != "CabDoor") continue;
                if (Quaternion.Angle(h.localRotation, Quaternion.identity) < 10f) continue;
                var leaf = h.Find("Door");
                var mf = leaf != null ? leaf.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null) continue;
                var h0 = w.Car.InverseTransformPoint(h.position); h0.y = 0f;
                var h1 = w.Car.InverseTransformPoint(h.TransformPoint(new Vector3(0f, 0f, mf.sharedMesh.bounds.min.z))); h1.y = 0f;
                static float Side(Vector3 p, Vector3 q, Vector3 r) => (q.x - p.x) * (r.z - p.z) - (q.z - p.z) * (r.x - p.x);
                if (Side(w.PrevAt, at, h0) * Side(w.PrevAt, at, h1) < 0f && Side(h0, h1, w.PrevAt) * Side(h0, h1, at) < 0f)
                    w.ThroughDoor = $"{h.name} at {at:F2} (car space), {Time.time - w.Out:F1} s after he was out";
            }
        w.PrevAt = at; w.HavePrev = true;
    }

    static async Task CarExitChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30)) { ExtraFail("car exit: room", spawner?.Status); return; }
        if (PoliceResponse.Instance == null) new GameObject("[HIS] PoliceResponse (test)").AddComponent<PoliceResponse>();
        var pc = PoliceCars.Instance;
        if (pc == null || HIS.Room.VirtualDoor.Instance == null) { ExtraFail("car exit", "no cars / door"); return; }
        Hitbox.LethalRounds = true;
        PoliceResponse.TestStance = PlayerStance.Armed; PoliceResponse.TestFired = false; PoliceResponse.TestEntryCleared = false;
        var watches = new List<ExitWatch>();
        void OnReport(IntruderT o, string what)
        {
            foreach (var w in watches)
            {
                if (w.Man != o) continue;
                if (what.StartsWith("getting out") && w.GettingOut < 0f) w.GettingOut = Time.time;
                if (what == "out of the car" && w.Out < 0f) { w.Out = Time.time; w.OutAt = o.transform.position; }
            }
        }
        var prevProbe = IntruderT.FrameProbe;
        IntruderT.OfficerReported += OnReport;
        IntruderT.FrameProbe = o => { prevProbe?.Invoke(o); foreach (var w in watches) if (w.Man == o) TrackExit(pc, w); };
        try
        {
            await CruiserExit(spawner, pc, watches);
            await TruckExit(spawner, pc, watches);
            await ShotGettingOut(spawner, pc, watches);
            await ShotStandingUp(spawner, pc, watches);
            await DeadInSeatDoorShut(spawner, pc, watches);
            await HitmanDoors(spawner, pc, watches);
            await HitmanSuvExit(spawner, pc, watches);
        }
        finally
        {
            IntruderT.OfficerReported -= OnReport;
            IntruderT.FrameProbe = prevProbe;
            ClearRiders(watches);
            pc.End();
            pc.DropHitmanCars();
            PoliceResponse.TestStance = null;
        }
    }

    /// <summary>A rider in <paramref name="seat"/> of <paramref name="wave"/>'s vehicle (the way PoliceResponse seats them), watched.</summary>
    static ExitWatch SeatRider(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches, OfficerRole role, int wave, int seat, string name,
                          SquadFaction faction = SquadFaction.Police)
    {
        var def = new ScenarioDef { Name = "Rider", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
        var o = spawner.SpawnScenario(def, 0);
        if (o == null) return null;
        o.ConfigureOfficer(role, wave, Vector3.zero, seat, faction: faction);
        o.name = name;
        int v = PoliceCars.VehicleFor(wave, faction);
        pc.Crashed(v, out _, out var car);
        var a = o.GetComponentInChildren<Animator>();
        var w = new ExitWatch
        {
            Man = o, Vehicle = v, Seat = seat, Car = car, Anim = o.GetComponentInChildren<IntruderAnimator>(),
            Hips = a != null ? a.GetBoneTransform(HumanBodyBones.Hips) : null, Head = a != null ? a.GetBoneTransform(HumanBodyBones.Head) : null,
            LFoot = a != null ? a.GetBoneTransform(HumanBodyBones.LeftFoot) : null, RFoot = a != null ? a.GetBoneTransform(HumanBodyBones.RightFoot) : null,
        };
        watches.Add(w);
        return w;
    }

    /// <summary>Each frame: his exit clip (side, how far), and once he's past the door's line, any of him inside the shell.</summary>
    static void TrackExit(PoliceCars pc, ExitWatch w)
    {
        if (w.Man == null || w.Anim == null || w.Car == null || w.Hips == null) return;
        if (w.Man.Current == IntruderT.State.Dead) return;
        float ct = w.Anim.ExitClipTime;
        if (w.GettingOut >= 0f)
            foreach (float m in w.TurnMarks.ToArray())
                if (Time.time - w.GettingOut >= m) { w.TurnMarks.Remove(m); RenderExitPose(pc, w, m); }
        if (ct >= 0f && w.ShootAtClip >= 0f && w.ShotAtClip < 0f && ct >= w.ShootAtClip)
        {
            w.ShotAtClip = ct;
            ShootOfficer(w.Man, HumanBodyBones.Head);
            if (w.Man.Current == IntruderT.State.Dead) return;
        }
        if (ct >= 0f)
        {
            w.SawClip = true; w.MaxClip = Mathf.Max(w.MaxClip, ct);
            int side = w.Anim.ExitSide; if (side != 0) w.Side = side;
            foreach (float m in w.Marks.ToArray())
                if (ct >= m) { w.Marks.Remove(m); RenderExitPose(pc, w, m); }
        }
        if (w.Out >= 0f)
        {
            TrackDoorPass(w);
            foreach (float m in w.WalkMarks.ToArray())
                if (Time.time - w.Out >= m) { w.WalkMarks.Remove(m); RenderExitPose(pc, w, m, "walk_"); }
            return;   // (the walk-up from here goes round the vehicles: ScenarioTests.CarCover)
        }
        // Only from his turn on: before he boards he stands wherever he was spawned — "out past the door's line" in the car's
        // space — and every frame of the ride after that read as inside the shell (2026-10-05, the first run).
        if (w.GettingOut < 0f) return;
        // Each of hips, head and feet, once it's out past the door's line (the body's side; the box's back face), never back inside
        // the shell's colliders (no sliding back through the body at the hand-off or the step to his place). A trailing leg still
        // crossing the sill is inside the door section's (solid) collider by design, so each part counts only once it's out.
        for (int i = 0; i < 4; i++)
        {
            var bone = i == 0 ? w.Hips : i == 1 ? w.Head : i == 2 ? w.LFoot : w.RFoot;
            if (bone == null) continue;
            var l = w.Car.InverseTransformPoint(bone.position);
            if (!w.OutBone[i]) w.OutBone[i] = w.Back ? l.z < w.BackZ - 0.3f : Mathf.Abs(l.x) > w.SideX + 0.05f;
            if (i == 0) w.Past = w.OutBone[0];
            if (!w.OutBone[i] || !pc.InsideBody(w.Vehicle, bone.position)) continue;
            w.InsideFrames++;
            w.InsideWhere ??= $"{(i == 0 ? "hips" : i == 1 ? "head" : i == 2 ? "left foot" : "right foot")} at {l:F2} (car space), clip {ct:F2} s";
        }
    }

    /// <summary>GRAPHICS=1: the car and the man getting out, at clip time <paramref name="mark"/> — from ahead of his door, and side on.</summary>
    static void RenderExitPose(PoliceCars pc, ExitWatch w, float mark, string what = "")
    {
        if (!SecurityCamera.CanRender) return;
        var m = new Vector3(w.Side > 0 ? 1f : -1f, 1f, 1f);   // his door's side
        RenderVehicle(pc, w.Vehicle, $"{w.Shot}_{what}{mark:F1}.png",
                      (Vector3.Scale(w.ViewA.eye, m), Vector3.Scale(w.ViewA.look, m)), (Vector3.Scale(w.ViewB.eye, m), Vector3.Scale(w.ViewB.look, m)));
    }

    static void ClearRiders(List<ExitWatch> watches)
    {
        foreach (var w in watches) if (w.Man != null) UnityEngine.Object.Destroy(w.Man.gameObject);
        watches.Clear();
    }

    /// <summary>A watched man's exit, for the logs and failures.</summary>
    static string ExitSummary(ExitWatch w) =>
        $"{(w.Man != null ? w.Man.name : "?")}: side {(w.Side < 0 ? "left" : w.Side > 0 ? "right" : "none")}, clip {(w.SawClip ? $"to {w.MaxClip:F2} s" : "never")}, " +
        $"out {(w.Out >= 0f && w.GettingOut >= 0f ? $"{w.Out - w.GettingOut:F1} s after his turn" : "never")}, " +
        $"{(w.Out >= 0f ? $"{FlatV(w.OutAt - w.Exit).magnitude:F2} m from his exit" : "")}{(w.InsideFrames > 0 ? $", INSIDE the shell {w.InsideFrames} frame(s): {w.InsideWhere}" : "")}";

    // ── 1. The cruiser: three riders out one after another.
    static async Task CruiserExit(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: a cruiser's three get out one after another, each out of his side, standing at his exit";
        ClearRiders(watches);
        pc.End();
        if (!pc.Send(0)) { ExtraFail(what, "the cruiser didn't go"); return; }
        var driver = SeatRider(spawner, pc, watches, OfficerRole.Lead, 1, 0, "Officer (driver)");
        var partner = SeatRider(spawner, pc, watches, OfficerRole.Cover, 1, 1, "Officer (passenger)");
        var back = SeatRider(spawner, pc, watches, OfficerRole.Backup, 1, 2, "Officer (back seat)");
        if (driver == null || partner == null || back == null) { ExtraFail(what, "no riders"); return; }
        driver.Marks.AddRange(new[] { 0.6f, 1.2f, 1.8f, 2.3f, 2.8f });   // (GRAPHICS=1: Logs/poses/car_exit_*.png)
        driver.WalkMarks.AddRange(new[] { 0.5f, 1.2f });                 // (his first steps from his door: car_exit_walk_*.png)
        back.Shot = "car_exit_back";                                      // (the back seat by the driver's seat back: car_exit_back_*.png)
        back.ViewA = (new Vector3(2.6f, 1.75f, 2.4f), new Vector3(0.5f, 1.1f, -0.3f));
        back.Marks.AddRange(new[] { 0.5f, 0.9f, 1.3f });
        await WaitFor(() => driver.Man.Riding && partner.Man.Riding && back.Man.Riding, 3);
        bool stopped = await WaitFor(() => pc.StopClock(0) >= 0f, 10);
        bool allOut = await WaitFor(() => driver.Out >= 0f && partner.Out >= 0f && back.Out >= 0f, 15);
        float stop = pc.StopClock(0), dl = pc.DoorSwungAt(0, 0, out _), dr = pc.DoorSwungAt(0, 1, out _);
        foreach (var w in new[] { driver, partner, back }) PoliceCars.GetOut(1, w.Seat, out w.Exit, SquadFaction.Police);
        string doors = $"stop {stop:F2}, driver's door +{dl - stop:F2} s, passenger's +{dr - stop:F2} s";
        bool inTurn = stopped && dl >= stop && dr >= dl + 0.2f && dl - stop <= 1f;
        if (inTurn) ExtraPass("car exit: the cruiser's doors swing one after another", doors); else ExtraFail("car exit: the cruiser's doors swing one after another", doors);
        bool sides = driver.Side < 0 && partner.Side > 0 && back.Side < 0;
        bool backAfter = back.GettingOut >= 0f && driver.GettingOut >= 0f && back.GettingOut - driver.GettingOut >= 1f;
        bool quick = true, atExit = true, clean = true;
        foreach (var w in new[] { driver, partner, back })
        {
            quick &= w.Out >= 0f && w.GettingOut >= 0f && w.Out - w.GettingOut <= 4.5f;
            atExit &= w.Out >= 0f && FlatV(w.OutAt - w.Exit).magnitude <= 0.5f;
            clean &= w.InsideFrames == 0 && w.Past;
        }
        string info = $"{doors}; {ExitSummary(driver)}; {ExitSummary(partner)}; {ExitSummary(back)}; back seat {(backAfter ? "after" : "NOT after")} the driver";
        if (allOut && sides && backAfter && quick && atExit) ExtraPass(what, info); else ExtraFail(what, info);
        const string shell = "car exit: nobody inside the cruiser's shell once past the door's line";
        if (allOut && clean) ExtraPass(shell, info); else ExtraFail(shell, info);
        await Wait(2.5f);   // (their first steps off: round the end of their open door, not through it)
        string through = string.Join("; ", new[] { driver, partner, back }.Where(w => w.ThroughDoor != null).Select(w => $"{w.Man.name} through {w.ThroughDoor}"));
        const string pass = "car exit: walking off, nobody goes through an open door of his car";
        if (allOut && through.Length == 0) ExtraPass(pass, "each went round the end of the open door (or never near it)"); else ExtraFail(pass, allOut ? through : "not all out");
        ClearRiders(watches);
        pc.End();
    }

    // ── 2. The SWAT truck: its driver out of his cab door, the box team out of the back in turn.
    static async Task TruckExit(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: the SWAT truck's driver out of the cab, the box team out of the back in turn";
        ClearRiders(watches);
        pc.End();
        int v = PoliceCars.VehicleFor(3, SquadFaction.Police);
        if (!pc.Send(v)) { ExtraFail(what, "the truck didn't go"); return; }
        var team = new List<ExitWatch>();
        for (int seat = 0; seat < 4; seat++)
        {
            var w = SeatRider(spawner, pc, watches, seat == 0 ? OfficerRole.Lead : seat == 1 ? OfficerRole.Cover : OfficerRole.Backup, 3, seat, $"SWAT (seat {seat})");
            if (w == null) { ExtraFail(what, "no riders"); return; }
            if (seat == 0)
            {
                w.SideX = 1.12f;
                // (GRAPHICS=1: his cab door as he climbs out, Logs/poses/truck_exit_*.png — ahead of the door, and side on.)
                w.Shot = "truck_exit";
                w.ViewA = (new Vector3(3.9f, 1.9f, 4.4f), new Vector3(1.0f, 1.2f, 1.8f)); w.ViewB = (new Vector3(4.2f, 1.5f, 1.7f), new Vector3(1.0f, 1.2f, 1.7f));
                w.Marks.AddRange(new[] { 0.6f, 1.2f, 1.8f, 2.3f, 2.8f });
                w.WalkMarks.AddRange(new[] { 0.5f, 1.2f });
            }
            else
            {
                w.Back = true; w.BackZ = -3.0f;
                if (seat == 1)
                {
                    // (GRAPHICS=1: the first of the box team out of the back doorway, Logs/poses/truck_back_*.png — from behind, and
                    // from his side; seconds after his turn.)
                    w.Shot = "truck_back";
                    w.ViewA = (new Vector3(-1.6f, 1.7f, -7.2f), new Vector3(-0.3f, 1.0f, -3.0f)); w.ViewB = (new Vector3(-4.2f, 1.3f, -3.9f), new Vector3(-0.4f, 0.9f, -3.2f));
                    w.TurnMarks.AddRange(new[] { 0.4f, 0.8f, 1.15f, 1.5f });
                }
            }
            team.Add(w);
        }
        await WaitFor(() => team.TrueForAll(w => w.Man.Riding), 3);
        bool stopped = await WaitFor(() => pc.StopClock(v) >= 0f, 12);
        bool allOut = await WaitFor(() => team.TrueForAll(w => w.Out >= 0f), 20);
        foreach (var w in team) PoliceCars.GetOut(3, w.Seat, out w.Exit, SquadFaction.Police);
        float stop = pc.StopClock(v), b0 = pc.DoorSwungAt(v, 0, out _), b1 = pc.DoorSwungAt(v, 1, out _);
        bool backDoors = b0 >= stop && b1 >= b0 && b1 - b0 <= 0.5f;
        // The driver's cab door (door 2, 2026-10-05): it swings as his hand pushes it, after the stop, out of his left side.
        float cab = pc.DoorSwungAt(v, 2, out float cabOpen);
        bool cabDoor = pc.DoorCount(v) >= 3 && cab >= stop && cab - stop <= 1.5f && team[0].Side < 0;
        bool inTurn = true, atExit = true, clean = true;
        for (int i = 2; i < team.Count; i++) inTurn &= team[i].GettingOut >= 0f && team[i - 1].GettingOut >= 0f && team[i].GettingOut - team[i - 1].GettingOut >= 0.3f;
        foreach (var w in team)
        {
            atExit &= w.Out >= 0f && FlatV(w.OutAt - w.Exit).magnitude <= 0.5f;
            clean &= w.InsideFrames == 0;
        }
        string info = $"stop {stop:F2}, back doors +{b0 - stop:F2} / +{b1 - stop:F2} s, cab door {(cab >= 0f ? $"+{cab - stop:F2} s ({cabOpen:P0} open)" : "SHUT")}; " +
                      string.Join("; ", team.ConvertAll(w => ExitSummary(w)));
        if (stopped && allOut && backDoors && cabDoor && inTurn && atExit && clean && team[0].SawClip) ExtraPass(what, info); else ExtraFail(what, info);
        await Wait(2.5f);
        const string pass = "car exit: the SWAT truck's driver walks off round the end of his open cab door";
        if (allOut && team[0].ThroughDoor == null) ExtraPass(pass, "not through it"); else ExtraFail(pass, allOut ? $"through {team[0].ThroughDoor}" : "not all out");
        ClearRiders(watches);
        pc.End();
    }

    // ── 3. Shot getting out: before he's up he dies there; once up, a standing death; the others go on.
    static async Task ShotGettingOut(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: shot getting out — dies there (in his seat's space before he's up, on the street after), the rest go on";
        ClearRiders(watches);
        pc.End();
        if (!pc.Send(0)) { ExtraFail(what, "the cruiser didn't go"); return; }
        var driver = SeatRider(spawner, pc, watches, OfficerRole.Lead, 1, 0, "Officer (driver)");
        var partner = SeatRider(spawner, pc, watches, OfficerRole.Cover, 1, 1, "Officer (passenger, shot getting out)");
        var back = SeatRider(spawner, pc, watches, OfficerRole.Backup, 1, 2, "Officer (back seat, shot once up)");
        if (driver == null || partner == null || back == null) { ExtraFail(what, "no riders"); return; }
        // The passenger: hand on the door, legs still inside (clip 1.0–2.2 s).
        bool caught = await WaitFor(() => partner.Anim.ExitClipTime >= 1.0f, 12);
        var car = partner.Car;
        var hitLocal = car != null ? car.InverseTransformPoint(partner.Man.transform.position) : Vector3.zero;
        float hitClip = partner.Anim.ExitClipTime;
        ShootOfficer(partner.Man, HumanBodyBones.Head);
        await Wait(1.5f);
        var nowLocal = car != null ? car.InverseTransformPoint(partner.Man.transform.position) : Vector3.zero;
        float moved = FlatV(nowLocal - hitLocal).magnitude, swung = pc.DoorSwungAt(0, 1, out float open);
        bool diedThere = caught && partner.Man.Current == IntruderT.State.Dead && partner.Man.transform.parent == car && moved <= 0.35f && swung >= 0f;
        // (Read now: the second body can evict this one — CorpseRegistry.MaxCorpses — and he's gone once he's out of view.)
        string partnerInfo = $"passenger shot at clip {hitClip:F2} s: {partner.Man.Current}, {(partner.Man.transform.parent == car ? "in the car's space" : "OFF the car")}, moved {moved:F2} m, " +
                             $"his door {(swung >= 0f ? $"open {open:P0}" : "SHUT")}";
        RenderDeath(pc, partner, "car_exit_shot_seat.png");   // (GRAPHICS=1: dead in his seat, his legs on the way out)
        // The back-seat man: once he's on his feet outside.
        bool upCaught = await WaitFor(() => back.Anim.ExitClipTime >= IntruderClips.ExitStand + 0.05f || back.Out >= 0f, 15);
        float upClip = back.Anim.ExitClipTime;
        ShootOfficer(back.Man, HumanBodyBones.Head);
        await Wait(3.5f);
        bool backThere = back.Man != null;
        float hipsUp = backThere && back.Hips != null ? back.Hips.position.y - back.Man.transform.position.y : 9f;
        bool standingDeath = upCaught && backThere && back.Man.Current == IntruderT.State.Dead && !back.Man.Riding && hipsUp < 0.5f;
        string backInfo = backThere ? $"back seat shot at clip {upClip:F2} s: {back.Man.Current}, riding {back.Man.Riding}, hips {hipsUp:F2} m up" : "back seat GONE (despawned)";
        if (backThere) RenderDeath(pc, back, "car_exit_shot_up.png");   // (GRAPHICS=1: shot once up, down on the street by his door)
        bool driverOut = await WaitFor(() => driver.Out >= 0f, 8);
        string info = $"{partnerInfo}; {backInfo}; driver {(driverOut ? "out" : "NOT out")}";
        if (diedThere && standingDeath && driverOut) ExtraPass(what, info); else ExtraFail(what, info);
        ClearRiders(watches);
        pc.End();
    }

    /// <summary>GRAPHICS=1: a man shot getting out, after his death — from ahead of his door, and side on (his door's side).</summary>
    static void RenderDeath(PoliceCars pc, ExitWatch w, string file)
    {
        if (!SecurityCamera.CanRender || w == null) return;
        var m = new Vector3(w.Side > 0 ? 1f : -1f, 1f, 1f);
        var a = (eye: new Vector3(3.4f, 1.6f, 2.8f), look: new Vector3(1.2f, 0.4f, 0.1f));
        var b = (eye: new Vector3(4.6f, 1.5f, -0.2f), look: new Vector3(1.2f, 0.4f, 0.1f));
        RenderVehicle(pc, w.Vehicle, file, (Vector3.Scale(a.eye, m), Vector3.Scale(a.look, m)), (Vector3.Scale(b.eye, m), Vector3.Scale(b.look, m)));
    }

    // ── 3b. Shot as he comes up out of the car (legs out, past its side, not yet up: clip 2.2–2.63 s): no longer a rider — down on
    //        the street (not frozen leaning in the seat death), no horn, and the back seat still goes out of that door.
    static async Task ShotStandingUp(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: shot as he stands up beside his door — down on the street, no horn, the back seat still out of that door";
        ClearRiders(watches);
        pc.End();
        if (!pc.Send(0)) { ExtraFail(what, "the cruiser didn't go"); return; }
        var driver = SeatRider(spawner, pc, watches, OfficerRole.Lead, 1, 0, "Officer (driver, shot standing up)");
        var back = SeatRider(spawner, pc, watches, OfficerRole.Backup, 1, 2, "Officer (back seat)");
        if (driver == null || back == null) { ExtraFail(what, "no riders"); return; }
        driver.ShootAtClip = 2.35f;   // (ExitFootOut 2.20 – ExitStand 2.63: his root on the road, coming up)
        bool shot = await WaitFor(() => driver.ShotAtClip >= 0f, 15);
        bool dead = await WaitFor(() => driver.Man.Current == IntruderT.State.Dead, 2);
        await Wait(3.2f);
        var car = driver.Car;
        float hips = driver.Hips != null && car != null ? driver.Hips.position.y - car.position.y : 9f;
        bool horn = pc.HornBlown(0);
        RenderDeath(pc, driver, "car_exit_shot_rising.png");
        bool backGoes = await WaitFor(() => back.Side != 0 || back.Out >= 0f, 10);
        string info = $"driver shot at clip {driver.ShotAtClip:F2} s: {driver.Man.Current}, riding {driver.Man.Riding}, hips {hips:F2} m over the road; " +
                      $"horn {(horn ? "BLARED" : "quiet")}; back seat {(back.Side < 0 ? "out of the driver's door" : back.Side > 0 ? "out of the PASSENGER's door" : "not out")}";
        bool ok = shot && dead && driver.ShotAtClip < IntruderClips.ExitStand && !driver.Man.Riding && hips < 0.5f && !horn && backGoes && back.Side < 0;
        if (ok) ExtraPass(what, info); else ExtraFail(what, info);
        ClearRiders(watches);
        pc.End();
    }

    // ── 4. Shot dead in his seat before his turn: his door stays shut.
    static async Task DeadInSeatDoorShut(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: a rider shot dead in his seat keeps his door shut, the driver's opens";
        ClearRiders(watches);
        pc.End();
        if (!pc.Send(0)) { ExtraFail(what, "the cruiser didn't go"); return; }
        var driver = SeatRider(spawner, pc, watches, OfficerRole.Lead, 1, 0, "Officer (driver)");
        var partner = SeatRider(spawner, pc, watches, OfficerRole.Cover, 1, 1, "Officer (passenger, shot seated)");
        if (driver == null || partner == null) { ExtraFail(what, "no riders"); return; }
        await WaitFor(() => partner.Man.Riding, 3);
        await WaitFor(() => pc.DriveClock(0) > 2.0f, 6);   // still pulling up (the passenger: no crash)
        ShootOfficer(partner.Man, HumanBodyBones.Head);
        bool stopped = await WaitFor(() => pc.StopClock(0) >= 0f, 10);
        await WaitFor(() => driver.Out >= 0f, 8);
        float dl = pc.DoorSwungAt(0, 0, out _), dr = pc.DoorSwungAt(0, 1, out _);
        string info = $"passenger {partner.Man.Current} (riding {partner.Man.Riding}); driver's door {(dl >= 0f ? "open" : "shut")}, passenger's {(dr >= 0f ? "OPEN" : "shut")}, driver {(driver.Out >= 0f ? "out" : "NOT out")}";
        if (stopped && partner.Man.Current == IntruderT.State.Dead && partner.Man.Riding && dl >= 0f && dr < 0f && driver.Out >= 0f) ExtraPass(what, info); else ExtraFail(what, info);
        ClearRiders(watches);
        pc.End();
    }

    // ── 6. A Marshal night's charcoal SUV (its body rides 0.25 m higher: the drop to the take's seat is from higher up): both out
    //       of their sides, standing at their exits, nobody back inside its shell; (GRAPHICS=1) the driver's exit, suv_exit_*.png.
    static async Task HitmanSuvExit(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: the hitmen's charcoal SUV — both out of their sides, at their exits, none inside the shell";
        ClearRiders(watches);
        pc.End();
        int v = PoliceCars.VehicleFor(4, SquadFaction.Hitmen);
        if (!pc.Send(v)) { Debug.Log($"[HIS] SCENARIO {what}: skipped (no hitman vehicle {v} here)"); pc.DropHitmanCars(); return; }
        try
        {
            var a = SeatRider(spawner, pc, watches, OfficerRole.Lead, 4, 0, "Hitman (SUV driver)", SquadFaction.Hitmen);
            var b = SeatRider(spawner, pc, watches, OfficerRole.Cover, 4, 1, "Hitman (SUV passenger)", SquadFaction.Hitmen);
            if (a == null || b == null) { ExtraFail(what, "no riders"); return; }
            a.Shot = "suv_exit";
            a.ViewA = (new Vector3(3.6f, 1.7f, 2.8f), new Vector3(0.9f, 1.1f, 0.2f)); a.ViewB = (new Vector3(4.6f, 1.45f, 0.2f), new Vector3(0.9f, 1.15f, 0.2f));
            a.Marks.AddRange(new[] { 0.6f, 1.2f, 1.8f, 2.3f, 2.8f });
            bool out2 = await WaitFor(() => a.Out >= 0f && b.Out >= 0f, 25);
            foreach (var w in new[] { a, b }) PoliceCars.GetOut(4, w.Seat, out w.Exit, SquadFaction.Hitmen);
            bool atExit = out2 && FlatV(a.OutAt - a.Exit).magnitude <= 0.5f && FlatV(b.OutAt - b.Exit).magnitude <= 0.5f;
            bool clean = a.InsideFrames == 0 && b.InsideFrames == 0 && a.Past && b.Past;
            string info = $"{ExitSummary(a)}; {ExitSummary(b)}";
            if (out2 && atExit && clean && a.Side < 0 && b.Side > 0) ExtraPass(what, info); else ExtraFail(what, info);
        }
        finally
        {
            ClearRiders(watches);
            pc.End();
            pc.DropHitmanCars();
        }
    }

    // ── 5. A Marshal night's sedan: its doors' clunks on their own swings (skipped where its vehicles can't be built).
    static async Task HitmanDoors(IntruderSpawner spawner, PoliceCars pc, List<ExitWatch> watches)
    {
        const string what = "car exit: the hitmen's sedan — each door's clunk on its own swing, both out";
        ClearRiders(watches);
        pc.End();
        int v = PoliceCars.VehicleFor(1, SquadFaction.Hitmen);
        if (!pc.Send(v)) { Debug.Log($"[HIS] SCENARIO {what}: skipped (no hitman vehicle {v} here)"); pc.DropHitmanCars(); return; }
        var clunks = new List<(string msg, float at)>();
        void OnLog(string msg, string trace, LogType type)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(msg, @"^\[HIS\] PoliceCars: .+ door \d+$")) lock (clunks) clunks.Add((msg, Time.time));   // (DoorHeard's line)
        }
        Application.logMessageReceived += OnLog;
        try
        {
            var a = SeatRider(spawner, pc, watches, OfficerRole.Lead, 1, 0, "Hitman (driver)", SquadFaction.Hitmen);
            var b = SeatRider(spawner, pc, watches, OfficerRole.Cover, 1, 1, "Hitman (passenger)", SquadFaction.Hitmen);
            if (a == null || b == null) { ExtraFail(what, "no riders"); return; }
            bool out2 = await WaitFor(() => a.Out >= 0f && b.Out >= 0f, 20);
            foreach (var w in new[] { a, b }) PoliceCars.GetOut(1, w.Seat, out w.Exit, SquadFaction.Hitmen);
            bool atExit = out2 && FlatV(a.OutAt - a.Exit).magnitude <= 0.5f && FlatV(b.OutAt - b.Exit).magnitude <= 0.5f;
            float d0 = pc.DoorSwungAt(v, 0, out _), d1 = pc.DoorSwungAt(v, 1, out _);
            List<(string msg, float at)> heard; lock (clunks) heard = new List<(string, float)>(clunks);
            bool apart = heard.Count >= 2 && heard[1].at - heard[0].at >= 0.15f;
            string info = $"doors swung at {d0:F2} / {d1:F2} (clock), clunks {heard.Count}{(heard.Count >= 2 ? $" {heard[1].at - heard[0].at:F2} s apart" : "")}; {ExitSummary(a)}; {ExitSummary(b)}";
            if (out2 && atExit && d0 >= 0f && d1 > d0 && apart && a.Side < 0 && b.Side > 0) ExtraPass(what, info); else ExtraFail(what, info);
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
            ClearRiders(watches);
            pc.End();
            pc.DropHitmanCars();
        }
    }
}
