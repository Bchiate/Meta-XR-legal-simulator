using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.UI;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Eyes on the Marshal night's looks (the integrator's renders; needs a GPU): what the player sees, as PNGs under
/// Logs/poses/marshal_*.png, to LOOK at before Brett does.
///   0. The night's five hitman vehicles (Brett 0.1.57: five waves, one vehicle each) parked as wave 5 leaves them (shot
///      once the room has settled: after the courier, before wave 2 — the five sent in place of the night's own sedan): the
///      doorbell feed as the monitor shows it (marshal_cars_feed), and the street from above in the headset's colours
///      (marshal_cars_top: the red sedan reads red there) — where each parked against the path and the doorway's line.
///   1. The handover's labels (MarshalBriefing + UI/WorldLabel) from the safe spot, at the eye's height, with the eye's own
///      field of view (…_wide: ~11 px/°) and a close crop at about the headset's sharpness (…_zoom: ~25 px/°): "Deputy Ortiz"
///      over him and "Ricky" over his head (the handover, IN PERSON since 0.1.58), "Hold Y" at the left hand, "Squeeze grip" at
///      the holstered sidearm (looking down at the hip), "Trigger" over the cardboard target with the pistol drawn, "B: reload"
///      at the gun, the shoulder, the knife's hip, the yell at the right hand, the door handle, the monitor and the Menu tip.
///      The background is a flat "passthrough" grey: only the game's own things are drawn, as on the headset.
///   2. Deputy Ortiz in person: in the room beside the front door (the eye's view, a close look at his U.S. MARSHAL print),
///      then on the porch camera walking off after the handover, and his back.
///   3. The pizza courier with the box in his hands (feed + close look), and the box set down on the step — since 0.1.58 he
///      comes BETWEEN the waves (after wave 1: its men are put down as they get out of the car, as the full night's test).
/// The check's steps after the draw complete on their own (MarshalBriefing.TestAutoComplete): this is about looks, the steps
/// themselves are ScenarioTests.MarshalBriefing.
///   GRAPHICS=1 scripts/unity-batch.sh ScenarioTests.RunMarshalLooksHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunMarshalLooksHeadless() => RunMarshal(MarshalLooksRun, 900f);

    const string LooksDir = "Logs/poses";
    /// <summary>The headset-like eye: vertical field of view (°) and the wide shot's size; the zoom's field of view and size.</summary>
    const float EyeFov = 90f, ZoomFov = 24f;
    const int WideW = 1000, WideH = 1000, ZoomW = 600, ZoomH = 600;

    static Camera s_looksCam, s_looksOutCam;

    /// <summary>
    /// One frame from <paramref name="pos"/> along <paramref name="rot"/> into Logs/poses/<paramref name="file"/>. Room things with
    /// the eye's culling mask; <paramref name="outside"/>: the porch side (the Outside layer, with the door feed's renderer and
    /// the porch set live, as ScenarioTests.Police's car shots).
    /// </summary>
    static void LooksRender(Vector3 pos, Quaternion rot, float fov, int w, int h, int mask, string file, bool outside = false)
    {
        ref Camera slot = ref outside ? ref s_looksOutCam : ref s_looksCam;
        if (slot == null)
        {
            slot = new GameObject(outside ? "[Looks] Porch camera" : "[Looks] Camera").AddComponent<Camera>();
            slot.enabled = false;
            slot.clearFlags = CameraClearFlags.SolidColor;
            slot.backgroundColor = outside ? new Color(0.2f, 0.22f, 0.25f) : new Color(0.34f, 0.32f, 0.30f);   // (a dim room through passthrough)
            slot.nearClipPlane = 0.02f; slot.farClipPlane = 80f;
            if (outside) HIS.Security.DoorwayView.UseFeedRenderer(slot);
        }
        var c = slot;
        c.cullingMask = outside ? 1 << HIS.Security.PorchSet.OutsideLayer : mask;
        var porch = outside ? HIS.Security.PorchSet.For(VirtualDoor.Instance) : null;
        if (outside) Shader.SetGlobalFloat("_HIS_FeedView", 0f);
        c.transform.SetPositionAndRotation(pos, rot);
        c.fieldOfView = fov; c.aspect = (float)w / h;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        try
        {
            if (porch != null) porch.SetLive(true);
            c.targetTexture = rt; c.Render(); c.targetTexture = null;
            if (porch != null) porch.SetLive(false);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply(false);
            RenderTexture.active = prev;
            Directory.CreateDirectory(LooksDir);
            File.WriteAllBytes(Path.Combine(LooksDir, file), tex.EncodeToPNG());
        }
        finally { c.targetTexture = null; UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex); }
    }

    static async Task MarshalLooksRun()
    {
        if (!HIS.Security.SecurityCamera.CanRender) { ExtraFail("marshal looks: a GPU", "no graphics device: run with GRAPHICS=1"); return; }
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("marshal looks: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        var cs = CommandSystem.Instance;
        var body = UnityEngine.Object.FindAnyObjectByType<PlayerBody>();
        var lo = Loadout.Instance;
        var cam = Camera.main;
        var door = VirtualDoor.Instance;
        if (cs == null || cs.Left == null || cs.Right == null || body == null || lo == null || cam == null || door == null || s_odGrip == null)
        { ExtraFail("marshal looks: setup", $"commands {cs != null}, body {body != null}, loadout {lo != null}, camera {cam != null}, door {door != null}"); return; }
        var pistol = lo.SpawnedIn(lo.SlotIndex(LoadoutSlot.Sidearm)) as Firearm;
        var longGun = lo.SpawnedIn(lo.SlotIndex(LoadoutSlot.LongGun)) as Firearm;
        if (pistol == null) { ExtraFail("marshal looks: setup", "no sidearm"); return; }
        var scam = HIS.Security.SecurityCamera.For(door);
        if (scam == null) { ExtraFail("marshal looks: the porch camera", "none at the front door"); return; }
        int eyeMask = cam.cullingMask;
        int saved = 0;
        var files = new List<string>();
        const int All = ~0;
        void Feed(string file) { scam.RenderNow(); SaveFeed(scam, file); files.Add(file); saved++; }

        // ── 0. The five hitman vehicles, parked: shot after the courier (section 3) — the integrator, 0.1.58: shot here, before
        //       the night, the room's late update ("walls rebuilt: room update") re-hung the door and re-laid the porch AFTER the
        //       render, so the feed showed a street the night never has (the cars on the doorbell's narrow side, four of five
        //       out of the picture). By the courier the room has long settled, and wave 2 is still 10+ s off.
        bool carsShot = false;

        // The safe spot (else 2.2 m inside the front door), eyes at 1.65 m, facing the door.
        var spot = SafeSpot.Instance != null && SafeSpot.Instance.Spot.HasValue ? SafeSpot.Instance.Spot.Value : door.Threshold + door.Inward * 2.2f;
        var eye = new Vector3(spot.x, door.Threshold.y + 1.65f, spot.z);
        void Look(Vector3 at) => cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye, Vector3.up));
        Look(door.Threshold + Vector3.up * 1.2f);
        // The rig's hands where a standing player's are (they stop polling OVRInput: nothing moves them back).
        bool leftWas = cs.Left.enabled, rightWas = cs.Right.enabled;
        cs.Left.enabled = cs.Right.enabled = false;
        var flatFwd = door.Threshold - eye; flatFwd.y = 0f; flatFwd.Normalize();
        var flatRight = Vector3.Cross(Vector3.up, flatFwd);
        cs.Left.transform.SetPositionAndRotation(eye + flatFwd * 0.32f - flatRight * 0.2f - Vector3.up * 0.5f, Quaternion.LookRotation(flatFwd, Vector3.up));
        cs.Right.transform.SetPositionAndRotation(eye + flatFwd * 0.32f + flatRight * 0.2f - Vector3.up * 0.5f, Quaternion.LookRotation(flatFwd, Vector3.up));
        var right = OpenDoorHand(false);
        right.transform.position = eye + flatFwd * 0.3f + flatRight * 0.25f - Vector3.up * 0.6f;

        // The eye's view of a label (wide, as the headset frames it) and a crop at about the headset's sharpness.
        async Task Snap(string name, Vector3 lookAt, int slot = 0)
        {
            Look(lookAt);
            await Wait(0.12f);                                    // LateUpdate re-places the label for the eye
            var l = MarshalBriefing.LabelObject(slot);
            string says = l != null && l.Visible ? l.Text.Replace("\n", " / ") : "no label";
            LooksRender(cam.transform.position, cam.transform.rotation, EyeFov, WideW, WideH, eyeMask, $"marshal_label_{name}_wide.png");
            files.Add($"marshal_label_{name}_wide.png");
            if (l != null && l.Visible)
            {
                var mid = Vector3.Lerp(l.PlateCentre, l.ThingPoint, 0.35f);
                LooksRender(eye, Quaternion.LookRotation(mid - eye, Vector3.up), ZoomFov, ZoomW, ZoomH, eyeMask, $"marshal_label_{name}_zoom.png");
                files.Add($"marshal_label_{name}_zoom.png");
                float dist = Vector3.Distance(eye, l.ThingPoint);
                var half = l.PlateHalfSize;
                float tall = 2f * Mathf.Atan2(half.y, Vector3.Distance(eye, l.PlateCentre)) * Mathf.Rad2Deg;
                Debug.Log($"[HIS] Looks: {name}: \"{says}\" {dist:F2} m from the eye, plate {half.x * 200f:F1} × {half.y * 200f:F1} cm ({tall:F1}° tall), " +
                          $"plate centre {l.PlateCentre:F2}, thing {l.ThingPoint:F2}");
            }
            else Debug.Log($"[HIS] Looks: {name}: {says} (step {MarshalBriefing.Step})");
            saved++;
        }

        void Close(IntruderT m, string file, float yaw, float dist, float height, Vector3 at)
        {
            var f = m.transform.forward; f.y = 0f; f.Normalize();
            var dirTo = Quaternion.AngleAxis(yaw, Vector3.up) * f;
            var p = at + dirTo * dist; p.y = m.transform.position.y + height;
            LooksRender(p, Quaternion.LookRotation(at - p, Vector3.up), 40f, 640, 640, All, file, outside: m.BehindDoor);
            files.Add(file); saved++;
        }
        Vector3 Chest(IntruderT m)
        {
            var a = m.GetComponentInChildren<Animator>();
            var b = a != null && a.isHuman ? a.GetBoneTransform(HumanBodyBones.UpperChest) ?? a.GetBoneTransform(HumanBodyBones.Chest) : null;
            return b != null ? b.position : m.transform.position + Vector3.up * 1.35f;
        }

        // Who's who, from the log: Ortiz by his jacket (the handover's deputy: "dressed as Deputy Ortiz"), the courier by name.
        var courierRx = new Regex(@"^\[HIS\] Intruder #(\d+): Pizza courier \(");
        var ortizRx = new Regex(@"^\[HIS\] Intruder #(\d+): dressed as Deputy Ortiz");
        int ortizId = -1, courierId = -1;
        float setDownAt = -1f;
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            var m = ortizRx.Match(msg);
            if (m.Success && ortizId < 0) ortizId = int.Parse(m.Groups[1].Value);
            m = courierRx.Match(msg);
            if (m.Success) courierId = int.Parse(m.Groups[1].Value);
            if (msg.Contains("sets the pizza down on the step")) setDownAt = Time.time;
        };
        IntruderT ById(int id)
        {
            if (id < 0) return null;
            foreach (var i in IntruderT.All) if (i != null && i.Id == id) return i;
            return null;
        }
        // Ortiz on the porch camera walking off after the handover (WatchOrtiz: polled through the check's last steps and after).
        bool oInRoom = false, oLeave = false;
        void WatchOrtiz()
        {
            if (oLeave) return;
            var m = ById(ortizId);
            if (m == null || m.Current == IntruderT.State.Gone || !m.BehindDoor) return;
            float far = FlatOf(m.transform.position, door.Threshold);
            if (far <= 0.9f || !scam.Sees(Chest(m))) return;
            oLeave = true;
            Feed("marshal_ortiz_feed_leaving.png");
            Close(m, "marshal_ortiz_back.png", 180f, 1.6f, 1.45f, Chest(m));
            Debug.Log($"[HIS] Looks: Ortiz leaving on the porch camera, {far:F1} m out");
        }
        // Wave 1 goes down as it gets out of the car (the courier comes after it: Brett 0.1.57's visitors between the waves).
        var down = new HashSet<IntruderT>();
        Action<IntruderT, string> onReport = (o, r) => { if (o != null && o.IsHitman && (r == "out of the car" || r == "in the room" || r == "fighting")) lock (down) down.Add(o); };
        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        bool visitorsWere = ScenarioDirector.MarshalVisitors;
        ScenarioDirector.MarshalVisitors = true;
        try
        {
            // ── 1. The handover's labels; 2. Ortiz in the room.
            MarshalBriefing.Request();
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            if (!await WaitFor(() => MarshalBriefing.Running && MarshalBriefing.Step == 0, 40)) { ExtraFail("marshal looks: the check", $"never started ({dir.Status})"); return; }
            var ricky = MarshalBriefing.Ricky;
            var target = MarshalBriefing.Target;
            if (await WaitFor(() => MarshalBriefing.SideLabel == MarshalBriefing.WOrtiz, 20))
            {
                var l = MarshalBriefing.LabelObject(1);
                await Snap("ortiz", l != null ? l.ThingPoint : door.Threshold + Vector3.up * 1.6f, slot: 1);
                // What the player sees as the handover opens (the integrator, 0.1.58): Ortiz by the front door with his name over
                // him, and the cardboard target, in one eye-wide frame aimed between the two.
                var o = ById(ortizId);
                if (o != null && target != null)
                {
                    var mid = Vector3.Lerp(Chest(o), target.ChestPoint, 0.5f);
                    LooksRender(eye, Quaternion.LookRotation(mid - eye, Vector3.up), EyeFov, WideW, WideH, eyeMask, "marshal_handover_eye_wide.png");
                    files.Add("marshal_handover_eye_wide.png"); saved++;
                    Debug.Log($"[HIS] Looks: the handover from the eye — Ortiz {FlatOf(o.transform.position, eye):F2} m, the target {FlatOf(target.transform.position, eye):F2} m, " +
                              $"{Vector3.Angle(FlatV(o.transform.position - eye), FlatV(target.transform.position - eye)):F0}° apart; label \"{(l != null && l.Visible ? l.Text : "none")}\"");
                }
            }
            var ortiz = ById(ortizId);
            if (ortiz != null && !ortiz.BehindDoor && ortiz.Current != IntruderT.State.Gone)
            {
                // In person, as the eye sees him beside the front door, and his jacket's print up close.
                var chest = Chest(ortiz);
                LooksRender(eye, Quaternion.LookRotation(chest - eye, Vector3.up), EyeFov, WideW, WideH, eyeMask, "marshal_ortiz_inroom_wide.png");
                files.Add("marshal_ortiz_inroom_wide.png"); saved++;
                Close(ortiz, "marshal_ortiz_front.png", 0f, 1.6f, 1.45f, chest);
                oInRoom = true;
                Debug.Log($"[HIS] Looks: Ortiz in the room, {FlatOf(ortiz.transform.position, door.Threshold):F2} m from the door, {FlatOf(ortiz.transform.position, eye):F2} m from the eye");
            }
            if (ricky != null && await WaitFor(() => MarshalBriefing.SideLabel == MarshalBriefing.WRicky, 25))
                await Snap("ricky", ricky.ProtecteeHeadPoint, slot: 1);
            // (The skip offer goes up with "Weapons check before I go.", after Ricky's line.)
            if (await WaitFor(() => MarshalBriefing.Label == MarshalBriefing.WSkip, 40)) await Snap("skip_lefthand", cs.Left.transform.position);
            // Step 1 wants the head still and level: back to the door.
            Look(door.Threshold + Vector3.up * 1.6f);
            if (!await WaitFor(() => MarshalBriefing.Step == 2, 40)) { ExtraFail("marshal looks: step 2", $"step {MarshalBriefing.Step}"); return; }
            MarshalBriefing.TestStepSeconds = 6f;
            MarshalBriefing.TestAutoComplete = true;                 // from here each step lasts 6 s: time to look
            await Wait(0.3f);
            await Snap("hip_draw", pistol.Grabbable.GripPoint.position);
            right.transform.rotation = Quaternion.identity;
            HandAt(right, pistol.Grabbable.GripPoint.position);
            Grip(right);
            if (!await WaitFor(() => MarshalBriefing.Step == 3, 10)) { ExtraFail("marshal looks: step 3", $"step {MarshalBriefing.Step}"); return; }
            if (target != null)
            {
                // The gun up, two hands' length out, at the cardboard's chest.
                right.transform.position = eye + flatFwd * 0.45f + flatRight * 0.08f - Vector3.up * 0.25f;
                AimAt(right, pistol, target.ChestPoint, -flatFwd, Vector3.Distance(eye, target.ChestPoint) - 0.5f);
                await Wait(0.2f);
                await Snap("target_trigger", target.ChestPoint + Vector3.up * 0.2f);
            }
            if (await WaitFor(() => MarshalBriefing.Step == 4, 10)) { await Wait(0.3f); await Snap("gun_reload", pistol.transform.position); }
            if (await WaitFor(() => MarshalBriefing.Step == 5, 10))
            {
                await Wait(0.3f);
                await Snap("hip_holster", body.RightHip.transform.position);
                HandAt(right, body.RightHip.transform.position);
                Squeeze(right, 0f);
            }
            if (longGun != null && await WaitFor(() => MarshalBriefing.Step == 6, 10))
            { await Wait(0.3f); Look(door.Threshold + Vector3.up * 1.6f); await Wait(0.12f); await Snap("shoulder", cam.transform.position + cam.transform.forward); }
            if (await WaitFor(() => MarshalBriefing.Step == 7, 12)) { await Wait(0.3f); await Snap("hip_knife", body.LeftHip.transform.position); }
            if (await WaitFor(() => MarshalBriefing.Step == 8, 10)) { await Wait(0.3f); await Snap("yell_righthand", cs.Right.transform.position); }
            // (Under the test hook Ortiz sets off at once — he opens the door himself, no "Squeeze the handle" wait — so the handle's
            // label to shoot is "Swing it shut", up from his "Lock it behind me." until the leaf is to: the integrator, 0.1.58.)
            if (await WaitFor(() => MarshalBriefing.Step == 9, 10)
                && await WaitFor(() => { WatchOrtiz(); return MarshalBriefing.Label == MarshalBriefing.WDoorShut || MarshalBriefing.Step != 9; }, 25)
                && MarshalBriefing.Step == 9)
                await Snap("door_handle", door.HandleWorld);
            // Step 9: Ortiz goes out through the door you opened; step 10 is the porch camera (he's on it, walking off).
            if (await WaitFor(() => { WatchOrtiz(); return MarshalBriefing.Step == 10; }, 30))
            {
                await Wait(0.3f);
                var mon = HIS.Security.SecurityMonitor.Instance;
                if (mon != null && mon.Placed) await Snap("monitor", mon.ScreenCenter);
                if (await WaitFor(() => { WatchOrtiz(); return MarshalBriefing.Label == MarshalBriefing.WMenu; }, 10)) await Snap("menu_lefthand", cs.Left.transform.position);
            }
            MarshalBriefing.TestAutoComplete = false;
            await WaitFor(() => { WatchOrtiz(); return !MarshalBriefing.Running; }, 15);
            await WaitFor(() => { WatchOrtiz(); var m = ById(ortizId); return oLeave || m == null || m.Current == IntruderT.State.Gone; }, 20);
            Look(door.Threshold + Vector3.up * 1.6f);

            // ── 3. The courier's box, between the waves (wave 1 put down as it gets out of the car).
            bool cPath = false, cStep = false;
            float until = Time.realtimeSinceStartup + 420f;
            while (EditorApplication.isPlaying && Time.realtimeSinceStartup < until && !(cPath && cStep))
            {
                lock (down) foreach (var o in down) if (o != null && o.Current != IntruderT.State.Dead) o.TakeDamage(1000f);
                var m = ById(courierId);
                if (m != null && !cPath)
                {
                    float far = FlatOf(m.transform.position, door.Threshold);
                    var box = m.transform.Find("Prop_PizzaBox");
                    if (m.OnPorchPath && scam.Sees(Chest(m)) && far < 4.2f && far > 2.2f && box != null)
                    {
                        cPath = true; Feed("marshal_courier_feed_path.png");
                        Close(m, "marshal_courier_box_front.png", 0f, 1.7f, 1.5f, box.position);
                        Close(m, "marshal_courier_box_side.png", 70f, 1.5f, 1.4f, box.position);
                        Debug.Log($"[HIS] Looks: the courier on the path, {far:F1} m out (hitman wave {PoliceResponse.Instance?.Wave})");
                    }
                }
                if (!cStep && setDownAt > 0f && Time.time - setDownAt > 0.6f)
                {
                    cStep = true;
                    Feed("marshal_courier_feed_step.png");
                    Transform box = null;
                    foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude)) if (t.name == "Prop_PizzaBox") { box = t; break; }
                    if (box != null)
                    {
                        var outward = -door.Inward; outward.y = 0f; outward.Normalize();
                        var p = box.position + outward * 0.9f + Vector3.up * 1.1f;
                        LooksRender(p, Quaternion.LookRotation(box.position - p, Vector3.up), 45f, 640, 640, All, "marshal_courier_box_step.png", outside: true);
                        files.Add("marshal_courier_box_step.png"); saved++;
                    }
                }
                await Wait(0.05f);
            }
            // ── 0, shot now (see the top): the courier off the porch first, then the five parked, then the street as it was.
            await WaitFor(() => { var c = ById(courierId); return c == null || c.Current == IntruderT.State.Gone; }, 8);
            carsShot = await HitmanCarsLooks(door, scam, files, () => saved++);
            MarshalCheck(carsShot, "marshal looks: the five hitman vehicles parked, on the porch camera and from above", carsShot ? "marshal_cars_feed / marshal_cars_top" : "not rendered");
            MarshalCheck(oInRoom && oLeave, "marshal looks: Ortiz in person — in the room, then on the porch camera walking off",
                         $"in the room {oInRoom}, leaving on the camera {oLeave} (Ortiz #{ortizId})");
            MarshalCheck(cPath && cStep, "marshal looks: the courier between the waves — the box in his hands, then on the step",
                         $"on the path {cPath}, the box on the step {cStep} (courier #{courierId})");
            MarshalCheck(saved > 0, "marshal looks: rendered", $"{saved} shot(s) → {LooksDir}: {string.Join(", ", files)}");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            MarshalBriefing.TestAutoComplete = false; MarshalBriefing.TestStepSeconds = 0.3f;
            ScenarioDirector.MarshalVisitors = visitorsWere;
            if (right != null) UnityEngine.Object.Destroy(right.gameObject);
            cs.Left.enabled = leftWas; cs.Right.enabled = rightWas;
            if (s_looksCam != null) UnityEngine.Object.Destroy(s_looksCam.gameObject);
            if (s_looksOutCam != null) UnityEngine.Object.Destroy(s_looksOutCam.gameObject);
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }
    }

    /// <summary>
    /// Brett 0.1.57's five vehicles, sent in wave order and parked as a night leaves them by wave 5 (no riders): the doorbell's
    /// feed (marshal_cars_feed.png, the IR picture the monitor shows) and the street from high above in the headset's colours
    /// (marshal_cars_top.png). Each one's spot is logged. Then the street is cleared and the hitmen's vehicles dropped again
    /// (the night builds them on its first wave).
    /// </summary>
    static async Task<bool> HitmanCarsLooks(VirtualDoor door, HIS.Security.SecurityCamera scam, List<string> files, Action counted)
    {
        var pc = HIS.Security.PoliceCars.Instance;
        var porch = HIS.Security.PorchSet.For(door);
        if (pc == null || porch == null) return false;
        try
        {
            pc.End();
            var roots = new List<Vector3>();
            for (int w = 1; w <= HitmanTier.Waves; w++)
            {
                int v = HIS.Security.PoliceCars.VehicleFor(w, SquadFaction.Hitmen);
                if (!pc.Send(v)) return false;
                pc.Advance(7f);                                                  // parked, its lights out
                if (pc.VehicleNow(v, out var i))
                {
                    roots.Add(i.Root);
                    var l = porch.transform.InverseTransformPoint(i.Root);
                    Debug.Log($"[HIS] Looks: wave {w}'s {i.Name} parked at porch x {l.x:F1}, z {l.z:F1} ({Mathf.Atan2(Mathf.Abs(l.x), l.z) * Mathf.Rad2Deg:F0}° off the door's axis)");
                }
            }
            await Wait(0.3f);
            scam.RenderNow(); SaveFeed(scam, "marshal_cars_feed.png"); files.Add("marshal_cars_feed.png"); counted();
            // From high over the street, looking down at them all: the path, the doorway's line, the five spots.
            var mid = Vector3.zero; foreach (var r in roots) mid += r; mid /= Mathf.Max(1, roots.Count);
            var centre = Vector3.Lerp(porch.transform.position, mid, 0.6f);
            var over = centre + Vector3.up * 32f - porch.transform.forward * 4f;
            LooksRender(over, Quaternion.LookRotation(centre - over, porch.transform.forward), 60f, 1000, 700, ~0, "marshal_cars_top.png", outside: true);
            files.Add("marshal_cars_top.png"); counted();
            return roots.Count == HitmanTier.Waves;
        }
        finally { pc.End(); pc.DropHitmanCars(); }
    }
}
