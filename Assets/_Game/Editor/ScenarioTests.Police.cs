using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The officers themselves (Scripts/Police/IntruderPolice.cs, research/22), without a whole round: two officers spawned by
/// hand (the way PoliceResponse spawns them) and driven through PoliceResponse's test hooks:
///   up the path and stacked at the front door · entry cleared → the door is kicked in, the lead steps into the room, the
///   cover takes the frame · the player "fires" → both fire back (reported) · the player surrenders → the lead cuffs
///   (reported) · a head shot kills the cover → reported down.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunPoliceOfficersHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunPoliceOfficersHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 600f;
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
                ScenarioDirector.WaitForForced = true;   // no rounds: only the officers
                PoliceResponse.Enabled = false;          // (no standoff of the director's own)
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await PoliceOfficerChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally
            {
                PoliceResponse.TestStance = null; PoliceResponse.TestFired = false; PoliceResponse.TestEntryCleared = false;
                _running = false;
                Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed");
            }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task PoliceOfficerChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("police: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        if (PoliceResponse.Instance == null) new GameObject("[HIS] PoliceResponse (test)").AddComponent<PoliceResponse>();
        Hitbox.LethalRounds = true;
        PoliceResponse.TestStance = PlayerStance.Armed; PoliceResponse.TestFired = false; PoliceResponse.TestEntryCleared = false;

        var reports = new List<string>();
        Action<IntruderT, string> onReport = (o, what) => { lock (reports) reports.Add($"{o.Role}:{what}"); };
        bool Reported(OfficerRole role, string what) { lock (reports) return reports.Exists(r => r.StartsWith($"{role}:{what}")); }
        IntruderT.OfficerReported += onReport;
        IntruderT lead = null, cover = null;
        var sanity = GameObject.Find("[HIS] SanityCube"); // (builds drop it; in the Editor its collider stops rounds)
        if (sanity != null) sanity.SetActive(false);
        try
        {
            // 0. Brett 0.1.30: "if im shooting when theyre far away it doesnt work". A real M9 round through the closed door
            //    at an officer ~8 m down the path (a lone target in another wave, so the squad below is untouched).
            await FarShot(spawner, "police: far shot through the closed door");

            var def = new ScenarioDef { Name = "Police officer", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
            lead = spawner.SpawnScenario(def, 0);
            if (lead != null) { lead.ConfigureOfficer(OfficerRole.Lead, 1, Vector3.zero); lead.name = "Officer (test lead)"; }  // zero: his own start (cars / street)
            cover = spawner.SpawnScenario(def, 0);
            if (cover != null) { cover.ConfigureOfficer(OfficerRole.Cover, 1, Vector3.zero); cover.name = "Officer (test cover)"; }
            if (lead == null || cover == null) { ExtraFail("police: spawn", "SpawnScenario returned null"); return; }
            var door = HIS.Room.VirtualDoor.Instance;

            // 1. Up the path, stacked beside the front door, still out of sight.
            bool stacked = await WaitFor(() => Reported(OfficerRole.Lead, "at the door") && Reported(OfficerRole.Cover, "at the door"), 60);
            string where = $"lead {lead.transform.position:F2} ({lead.Current}), cover {cover.transform.position:F2} ({cover.Current}), door {(door != null ? door.Center.ToString("F2") : "none")}";
            if (stacked && lead.BehindDoor && cover.BehindDoor && door != null && !door.IsOpen) ExtraPass("police: up the path and stacked", where);
            else { ExtraFail("police: up the path and stacked", where); return; }

            // 1b. Brett 0.1.26: rounds through the closed door must reach the stack (they used to stand behind the wall).
            string reach = $"lead {ThroughTheDoor(door, lead)}, cover {ThroughTheDoor(door, cover)}";
            if (!reach.Contains("no")) ExtraPass("police: stack hittable through the door", reach); else ExtraFail("police: stack hittable through the door", reach);

            // 2. Entry: the lead kicks it in and steps into the room; the cover takes the frame.
            PoliceResponse.TestEntryCleared = true;
            bool opened = await WaitFor(() => door.IsOpen, 10);
            bool inside = await WaitFor(() => lead.Current == IntruderT.State.Idle && cover.Current == IntruderT.State.Idle, 20);
            float leadIn = Vector3.Dot(lead.transform.position - door.Center, door.Inward);
            float coverIn = Vector3.Dot(cover.transform.position - door.Center, door.Inward);
            var you = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            string entry = $"door open {opened}, lead {leadIn:F2} m in, {Flat(lead.transform.position - you):F2} m from you ({lead.Current}, hidden {lead.BehindDoor}), " +
                           $"cover {coverIn:F2} m in, {Flat(cover.transform.position - you):F2} m from you ({cover.Current}, hidden {cover.BehindDoor}), apart {Flat(lead.transform.position - cover.transform.position):F2} m";
            if (opened && inside && leadIn > 0.6f && coverIn > 0.6f && !lead.BehindDoor && !cover.BehindDoor) ExtraPass("police: breach and entry", entry);
            else { ExtraFail("police: breach and entry", entry); return; }

            // (With a GPU — GRAPHICS=1 — a look at them: Logs/poses/Police_Officers.png.)
            await Wait(1f);
            RenderOfficers(lead, cover);

            // 3. The player opens fire: both fire back (fair: after the reaction time).
            PoliceResponse.TestFired = true;
            bool fired = await WaitFor(() => Reported(OfficerRole.Lead, "fired") && Reported(OfficerRole.Cover, "fired"), 8);
            PoliceResponse.TestFired = false;
            string shots; lock (reports) shots = string.Join(", ", reports.FindAll(r => r.Contains("fired")));
            if (fired) ExtraPass("police: return fire", shots); else ExtraFail("police: return fire", $"reports: {shots}");

            // 4. Weapons down, hands up: the lead holsters, comes round behind the player and cuffs him.
            PoliceResponse.TestStance = PlayerStance.Surrendered;
            bool cuffed = await WaitFor(() => Reported(OfficerRole.Lead, "cuffed"), 35);
            var cam = Camera.main;
            float gap = cam != null ? Flat(lead.transform.position - cam.transform.position) : -1f;
            if (cuffed) ExtraPass("police: surrender → cuffed", $"lead {gap:F2} m from the player");
            else { string all; lock (reports) all = string.Join(", ", reports); ExtraFail("police: surrender → cuffed", $"{lead.Summary}; reports: {all}"); }

            // 5. A head shot kills the cover: reported down (killed).
            ShootOfficer(cover, HumanBodyBones.Head);
            bool down = await WaitFor(() => Reported(OfficerRole.Cover, "down killed"), 3);
            if (down && cover.Current == IntruderT.State.Dead) ExtraPass("police: officer killed → reported", cover.Summary);
            else ExtraFail("police: officer killed → reported", cover.Summary);

            // 6. The door hangs open now: the same far shot, through the open doorway.
            await FarShot(spawner, "police: far shot through the open doorway");

            // 7. Brett 0.1.33: a cruiser pulls up with two riders; a real M9 round through the windshield kills the driver
            //    while it's still moving — the glass cracks, it crashes to a stop, the partner gets out.
            await CarCrash(spawner);

            // 8. Brett 0.1.40: "the cars dont show like theyre getting hit or anything". The crashed cruiser, up close: three
            //    rounds into its side, three into its left window, one into the light bar — the window shatters, the bar goes
            //    dark, the side is holed. (With a GPU: Logs/poses/car_damage.png.)
            await CarDamage();

            // 9. 0.1.44 → 0.1.45 (Brett: "after third wave of cops nothing happens"): a crash that met a parked car on the
            //    road sped up again after every bounce (the dead foot on the gas) and rammed it forever — never stopped, its
            //    riders never got out, the wave never ended. Every crash must come to a stop.
            CrashesEnd();

            // 10. 0.1.48 → 0.1.50 (Brett: "theyre like kneeling instead of sitting"): the SWAT truck's driver at the wheel in its
            //     glass cab and a man in the box, seated (Logs/poses/truck_riders.png, _side; with a GPU).
            await TruckRiders(spawner);
        }
        finally
        {
            IntruderT.OfficerReported -= onReport;
            PoliceResponse.TestStance = null; PoliceResponse.TestFired = false; PoliceResponse.TestEntryCleared = false;
        }
    }

    /// <summary>
    /// GRAPHICS=1 only: the lead from the front, back and side and the cover from the front, on a plain backdrop (only the
    /// officers' layer is drawn), into Logs/poses/Police_Officers.png.
    /// </summary>
    static void RenderOfficers(IntruderT lead, IntruderT cover)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || lead == null || cover == null) return;
        const int w = 360, h = 560;
        var body = lead.GetComponentInChildren<SkinnedMeshRenderer>();
        var go = new GameObject("PoliceShotCam");
        var cam = go.AddComponent<Camera>();
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.32f, 0.34f, 0.38f);
        cam.cullingMask = 1 << (body != null ? body.gameObject.layer : 0);
        cam.fieldOfView = 38f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 20f;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        (IntruderT who, float yaw, bool head)[] views = { (lead, 0f, false), (lead, 180f, false), (lead, 90f, false), (cover, 0f, false), (lead, 20f, true), (lead, 90f, true), (cover, 20f, true) };
        var sheet = new Texture2D(w * views.Length, h, TextureFormat.RGB24, false);
        for (int i = 0; i < views.Length; i++)
        {
            var o = views[i].who.transform;
            var dir = Quaternion.AngleAxis(views[i].yaw, Vector3.up) * o.forward;
            var headBone = views[i].head ? views[i].who.GetComponentInChildren<Animator>()?.GetBoneTransform(HumanBodyBones.Head) : null;
            var target = headBone != null ? headBone.position + Vector3.up * 0.08f : o.position + Vector3.up * 1.0f;
            float dist = headBone != null ? 0.75f : 2.6f;
            var eye = target + dir * dist + Vector3.up * (headBone != null ? 0.05f : 0.25f);
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye));
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            RenderTexture.active = rt;
            sheet.ReadPixels(new Rect(0, 0, w, h), i * w, 0);
            RenderTexture.active = null;
        }
        sheet.Apply(false);
        System.IO.Directory.CreateDirectory("Logs/poses");
        System.IO.File.WriteAllBytes("Logs/poses/Police_Officers.png", sheet.EncodeToPNG());
        Debug.Log("[HIS] SCENARIO police: rendered Logs/poses/Police_Officers.png");
        UnityEngine.Object.Destroy(go); UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(sheet);
    }

    static async Task CarCrash(IntruderSpawner spawner)
    {
        const string what = "police: driver shot as the car pulls up → it crashes";
        var pc = HIS.Security.PoliceCars.Instance;
        var door = HIS.Room.VirtualDoor.Instance;
        if (pc == null || door == null || !DsInit()) { ExtraFail(what, "no cars / door"); return; }
        pc.End();
        if (!pc.Send(0)) { ExtraFail(what, "car didn't go"); return; }
        var def = new ScenarioDef { Name = "Rider", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
        var driver = spawner.SpawnScenario(def, 0); var partner = spawner.SpawnScenario(def, 0);
        if (driver == null || partner == null) { ExtraFail(what, "no riders"); return; }
        driver.ConfigureOfficer(OfficerRole.Lead, 1, Vector3.zero, 0); driver.name = "Officer (driver)";
        partner.ConfigureOfficer(OfficerRole.Cover, 1, Vector3.zero, 1); partner.name = "Officer (partner)";
        var rear = spawner.SpawnScenario(def, 0);                            // the back seat too (0.1.48: seated riders, all three seats)
        if (rear != null) { rear.ConfigureOfficer(OfficerRole.Backup, 1, Vector3.zero, 2); rear.name = "Officer (back seat)"; }
        await WaitFor(() => driver.Riding && partner.Riding, 3);
        await WaitFor(() => pc.DriveClock(0) > 2.75f, 8);
        var cam = HIS.Security.SecurityCamera.For(door);
        if (cam != null && HIS.Security.SecurityCamera.CanRender)
        { cam.RenderNow(); await Wait(0.05f); SaveFeed(cam, "feed_front_riders.png"); RenderCar(pc, "car_riders.png"); }
        // At his chest (the M9's 0.6° spread is ±12 cm at 12 m: a head through a windshield is a coin flip — Brett's too).
        var head = driver.GetComponentInChildren<Animator>()?.GetBoneTransform(HumanBodyBones.Neck);
        var guns = DsMakeGuns();
        var gun = guns.Find(g => g.Name == "M9") ?? (guns.Count > 0 ? guns[0] : null);
        try
        {
            if (head == null || gun == null) { ExtraFail(what, "no head / gun"); return; }
            var through = door.Center; // the line from his head through the doorway's middle, 1 m on into the room
            var muzzle = through + (through - head.position).normalized * 1.0f;
            var hits = Physics.RaycastAll(muzzle, (head.position - muzzle).normalized, 40f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (var h in hits) Debug.Log($"[HIS] TRACE {h.distance:F2} m: {h.collider.name} (layer {LayerMask.LayerToName(h.collider.gameObject.layer)}, {h.collider.GetComponentInParent<IntruderT>()?.name})");
            Debug.Log($"[HIS] TRACE driver head at {head.position:F2}, muzzle {muzzle:F2}");
            DsFire(gun.Gun, muzzle, head.position);
            await Wait(0.3f);
            bool dead = driver.Current == IntruderT.State.Dead;
            bool crashed = await WaitFor(() => pc.Crashed(0, out _, out _), 2);
            bool partnerOut = await WaitFor(() => !partner.Riding && partner.Current != IntruderT.State.Dead, 12);
            pc.Crashed(0, out int marks, out var root);
            // 0.1.44 (Brett: "more dramatic crashes"): it keeps its speed, runs into something and it shows — the windshield
            // gone, the hood smoking.
            pc.Damage(0, out _, out int shattered, out _, out bool smoking);
            string info = $"driver {driver.Current}, crashed {crashed}, cracks {marks}, partner out {partnerOut}, car at {(root != null ? root.localPosition.ToString("F1") : "?")}, panes shattered {shattered}, smoking {smoking}";
            if (dead && crashed && marks > 0 && partnerOut && shattered >= 1 && smoking) ExtraPass(what, info); else ExtraFail(what, info);
            if (cam != null && HIS.Security.SecurityCamera.CanRender) { cam.RenderNow(); await Wait(0.05f); SaveFeed(cam, "feed_front_crash.png"); RenderCar(pc, "car_crash.png"); }
        }
        finally { foreach (var g in guns) if (g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject); }
    }

    /// <summary>The second cruiser's driver "shot" at points along its drive in, the first parked in front: each crash stops within 10 s.</summary>
    static void CrashesEnd()
    {
        const string what = "police: every crash comes to a stop (no ramming a parked car forever)";
        var pc = HIS.Security.PoliceCars.Instance;
        if (pc == null) { ExtraFail(what, "no cars"); return; }
        var info = new List<string>();
        bool ok = true;
        foreach (float along in new[] { 0.5f, 0.7f, 0.85f, 0.95f })
        {
            pc.End();
            if (!pc.Send(0)) { ExtraFail(what, "car didn't go"); return; }
            pc.Advance(30f);                                            // the first cruiser parked
            pc.Send(1);
            bool crashed = false;
            for (int i = 0; i < 400 && !crashed; i++) { pc.Advance(0.05f); crashed = pc.CrashNow(1, along); }
            if (!crashed) { info.Add($"{along:P0}: never crashed"); ok = false; continue; }
            bool over = false; float took = -1f; Vector3 at = default;
            for (int i = 0; i < 300 && !over; i++) { pc.Advance(0.05f); over = pc.CrashOver(1, out took, out at); }
            info.Add($"{along:P0}: {(over ? $"stopped after {took:F1} s at {at:F1}" : $"STILL GOING at {at:F1}")}");
            ok &= over && took <= 10.5f;
        }
        pc.End();
        if (ok) ExtraPass(what, string.Join("; ", info)); else ExtraFail(what, string.Join("; ", info));
    }

    static async Task TruckRiders(IntruderSpawner spawner)
    {
        const string what = "police: the SWAT truck's riders sit (driver at the wheel, one in the box)";
        var pc = HIS.Security.PoliceCars.Instance;
        if (pc == null) { ExtraFail(what, "no cars"); return; }
        pc.End();
        int v = HIS.Security.PoliceCars.VehicleFor(3, SquadFaction.Police);   // (the SWAT truck, whatever a Marshal night left Faction at)
        if (!pc.Send(v)) { ExtraFail(what, "truck didn't go"); return; }
        var def = new ScenarioDef { Name = "Rider", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
        var driver = spawner.SpawnScenario(def, 0); var back = spawner.SpawnScenario(def, 0);
        if (driver == null || back == null) { ExtraFail(what, "no riders"); return; }
        driver.ConfigureOfficer(OfficerRole.Lead, 3, Vector3.zero, 0); driver.name = "SWAT (driver)";
        back.ConfigureOfficer(OfficerRole.Cover, 3, Vector3.zero, 1); back.name = "SWAT (box)";
        bool riding = await WaitFor(() => driver.Riding && back.Riding, 3);
        await WaitFor(() => pc.DriveClock(v) > 2.75f, 8);
        await Wait(0.5f);   // (the seated pose settled)
        RenderVehicle(pc, v, "truck_riders.png", (new Vector3(-3.2f, 2.3f, 5.2f), new Vector3(0f, 1.6f, 1.8f)), (new Vector3(-4.2f, 1.7f, 1.4f), new Vector3(0f, 1.5f, 1.4f)));
        string info = $"riding {riding}; driver {(driver.Riding ? "seated" : "out")}, box {(back.Riding ? "seated" : "out")}";
        if (riding) ExtraPass(what, info); else ExtraFail(what, info);
        pc.End();
        foreach (var o in new[] { driver, back }) if (o != null) UnityEngine.Object.Destroy(o.gameObject);
    }

    static async Task CarDamage()
    {
        const string what = "police: a cruiser shows its hits";
        var pc = HIS.Security.PoliceCars.Instance;
        if (pc == null || !pc.Crashed(0, out _, out var root) || root == null) { ExtraFail(what, "no crashed cruiser"); return; }
        Collider Find(string name) { foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (c.name == name) return c; return null; }
        var body = Find("HitBody"); var glass = Find("HitDoorGlassL") ?? Find("HitSideGlassL"); var bar = Find("HitLightBar");
        if (body == null || glass == null || bar == null) { ExtraFail(what, $"colliders: body {body != null}, glass {glass != null}, bar {bar != null}"); return; }
        var guns = DsMakeGuns();
        var gun = guns.Find(g => g.Name == "M9");
        if (gun == null) { ExtraFail(what, "no M9"); return; }
        try
        {
            var left = -root.right;                                            // the driver's side
            for (int k = 0; k < 3; k++)
            {
                var at = body.bounds.center + root.forward * (0.35f * k - 0.35f) + Vector3.up * 0.05f;
                DsFire(gun.Gun, at + left * 2.5f + Vector3.up * 0.3f, at); await Wait(0.15f);
            }
            for (int k = 0; k < 3; k++)
            {
                var at = glass.bounds.center + root.forward * (0.12f * k - 0.12f);
                DsFire(gun.Gun, at + left * 2.5f + Vector3.up * 0.2f, at); await Wait(0.15f);
            }
            var top = bar.bounds.center;
            DsFire(gun.Gun, top + left * 2.5f + Vector3.up * 0.4f, top); await Wait(0.3f);
            pc.Damage(0, out int hits, out int shattered, out bool broken, out _);
            pc.Crashed(0, out int marks, out _);
            RenderCar(pc, "car_damage.png");
            string info = $"body hits {hits}, marks {marks}, panes shattered {shattered}, light bar out {broken}";
            if (hits >= 3 && shattered >= 1 && broken && marks >= 3) ExtraPass(what, info); else ExtraFail(what, info);
        }
        finally { foreach (var g in guns) if (g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject); }
    }

    static void SaveFeed(HIS.Security.SecurityCamera cam, string file)
    {
        var rt = cam.Feed; var prev = RenderTexture.active; RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply(false);
        RenderTexture.active = prev;
        System.IO.Directory.CreateDirectory("Logs/poses");
        System.IO.File.WriteAllBytes("Logs/poses/" + file, tex.EncodeToPNG());
        UnityEngine.Object.Destroy(tex);
    }

    /// <summary>A close look at cruiser A (its riders through the glass), with the feed's renderer: Logs/poses/<paramref name="file"/>.</summary>
    static void RenderCar(HIS.Security.PoliceCars pc, string file) =>
        RenderVehicle(pc, 0, file, (new Vector3(-2.6f, 1.7f, 3.2f), new Vector3(0f, 1.0f, -0.2f)), (new Vector3(-3.6f, 1.25f, -0.45f), new Vector3(0f, 1.0f, -0.45f)));

    /// <summary>Vehicle <paramref name="v"/> from each (eye, look-at) pair in its own space: Logs/poses/<paramref name="file"/>, then _side, _2…</summary>
    static void RenderVehicle(HIS.Security.PoliceCars pc, int v, string file, params (Vector3 eye, Vector3 look)[] views)
    {
        pc.Crashed(v, out _, out var car);
        if (car == null || !HIS.Security.SecurityCamera.CanRender) return;   // (-nographics: rendering its particles crashed Unity natively)
        const int W = 640, H = 400;
        var go = new GameObject("CarShot");
        var cam = go.AddComponent<Camera>();
        var rt = new RenderTexture(W, H, 24);
        try
        {
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.2f, 0.22f, 0.25f);
            cam.cullingMask = 1 << HIS.Security.PorchSet.OutsideLayer; cam.fieldOfView = 45f; cam.nearClipPlane = 0.05f;
            HIS.Security.DoorwayView.UseFeedRenderer(cam);
            var porch = HIS.Security.PorchSet.For(HIS.Room.VirtualDoor.Instance);
            Shader.SetGlobalFloat("_HIS_FeedView", 0f);
            // Front-left (the driver through the windshield), then straight from the left (0.1.48 seated riders: heads under the
            // roof, above the belt line, the back-seat man inside the greenhouse).
            for (int i = 0; i < views.Length; i++)
            {
                var (eyeLocal, lookLocal) = views[i];
                string name = i == 0 ? file : file.Replace(".png", i == 1 ? "_side.png" : $"_{i}.png");
                var eye = car.TransformPoint(eyeLocal);
                go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(car.TransformPoint(lookLocal) - eye));
                if (porch != null) porch.SetLive(true);
                cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
                if (porch != null) porch.SetLive(false);
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(false);
                RenderTexture.active = null;
                System.IO.File.WriteAllBytes("Logs/poses/" + name, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
        }
        finally { UnityEngine.Object.Destroy(go); rt.Release(); UnityEngine.Object.Destroy(rt); }
    }

    /// <summary>
    /// A target officer (wave 2: not a mate of the squad under test) walks up the path; when he is 6–10 m from the door, a
    /// real M9 round (Firearm.TryFire, the trigger's call) from 1.4 m inside the door at his chest must hit him.
    /// </summary>
    static async Task FarShot(IntruderSpawner spawner, string what)
    {
        var door = HIS.Room.VirtualDoor.Instance;
        if (!DsInit() || door == null) { ExtraFail(what, "no door / Firearm internals renamed"); return; }
        var def = new ScenarioDef { Name = "Target officer", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
        var t = spawner.SpawnScenario(def, 0);
        if (t == null) { ExtraFail(what, "no target"); return; }
        var porch = HIS.Security.PorchSet.For(door);
        var path = porch != null ? porch.Approach : null;
        var start = path != null && path.Length > 1 ? path[0] + Vector3.Cross(Vector3.up, (path[1] - path[0]).normalized) * 1.5f : door.Center + door.Outward * 9f;
        t.ConfigureOfficer(OfficerRole.Lead, 2, start); t.name = "Officer (far target)";
        float Out() => Vector3.Dot(t.transform.position - door.Center, door.Outward);
        if (!await WaitFor(() => t.Current == IntruderT.State.Outside && Out() > 5f && Out() < 10f && t.BehindDoor, 20)) { ExtraFail(what, $"target never on the path ({Out():F1} m out, {t.Current})"); return; }
        await WaitFor(() => Out() < 8f, 6);
        var guns = DsMakeGuns();
        var gun = guns.Find(g => g.Name == "M9") ?? (guns.Count > 0 ? guns[0] : null);
        if (gun == null) { ExtraFail(what, "no gun"); return; }
        try
        {
            var chest = t.GetComponentInChildren<Animator>()?.GetBoneTransform(HumanBodyBones.Neck);
            var inward = door.Inward; inward.y = 0f; inward.Normalize();
            var muzzle = door.Center + inward * 1.4f; muzzle.y = chest != null ? chest.position.y + 0.1f : door.Center.y;
            float hp = t.Health, dist = Out();
            DsFire(gun.Gun, muzzle, chest != null ? chest.position : t.transform.position + Vector3.up * 1.3f);
            await Wait(0.3f);
            bool hit = t.Health < hp || t.Current == IntruderT.State.Dead;
            string info = $"{dist:F1} m out, door {(door.IsOpen ? "open" : "shut")}, hp {hp:F0} → {t.Health:F0} ({t.Current})";
            if (hit) ExtraPass(what, info); else ExtraFail(what, info);
        }
        finally { foreach (var g in guns) if (g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject); }
    }

    /// <summary>
    /// Traced the way Firearm.FireRay goes (the leaf, then on behind it): from a few spots in the room 1.6 m from the closed
    /// door, is there a line through the leaf into <paramref name="o"/>'s hitboxes? "yes (angle)" or "no".
    /// </summary>
    static string ThroughTheDoor(HIS.Room.VirtualDoor door, IntruderT o)
    {
        var anim = o.GetComponentInChildren<Animator>();
        var chest = anim != null ? anim.GetBoneTransform(HumanBodyBones.Chest) : null;
        if (door == null || chest == null) return "no (no door/chest)";
        int mask = ~0, wl = LayerMask.NameToLayer("Weapon"), dl = LayerMask.NameToLayer("Door");
        if (wl >= 0) mask &= ~(1 << wl);
        if (dl >= 0) mask &= ~(1 << dl);
        foreach (float deg in new[] { 0f, -25f, 25f, -40f, 40f })
        {
            var inward = Vector3.ProjectOnPlane(door.Inward, Vector3.up).normalized;
            var from = door.Center + Quaternion.AngleAxis(deg, Vector3.up) * inward * 1.6f; from.y = 1.45f;
            var dir = (chest.position - from).normalized;
            if (!door.RaycastLeaf(from, dir, 20f, out var leaf)) continue;
            if (!Physics.Raycast(leaf.ExitPoint + dir * 0.002f, dir, out var hit, 20f, mask, QueryTriggerInteraction.Ignore)) continue;
            if (hit.collider.GetComponentInParent<IntruderT>() == o) return $"yes ({deg:F0}°)";
        }
        return "no";
    }

    /// <summary>One pistol round straight into <paramref name="o"/>'s bone (its Hitbox), from the player's head.</summary>
    static void ShootOfficer(IntruderT o, HumanBodyBones bone)
    {
        var anim = o != null ? o.GetComponentInChildren<Animator>() : null;
        var b = anim != null ? anim.GetBoneTransform(bone) : null;
        var col = b != null ? b.GetComponent<Collider>() : null;
        if (col == null || !col.TryGetComponent<IDamageable>(out var target)) { Debug.LogWarning("[HIS] SCENARIO police: no hitbox to shoot"); return; }
        var origin = Camera.main != null ? Camera.main.transform.position : Vector3.up * 1.6f;
        var dir = (col.bounds.center - origin).normalized;
        target.ApplyDamage(new DamageInfo { Amount = 40f, Type = DamageType.Bullet, Direction = dir, Point = col.ClosestPoint(origin), Normal = -dir, Penetration = 1f, Source = Camera.main != null ? Camera.main.gameObject : null });
    }
}
