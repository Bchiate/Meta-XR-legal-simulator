using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Security;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;
using Object = UnityEngine.Object;

/// <summary>
/// The security cameras and the wall monitor (Brett 0.1.20: "see them in a security monitor at the door"):
///   the monitor hangs beside the front door (≤ 1.3 m from the frame edge, 1.3–1.6 m high, facing the room, out of the leaf's
///   swing, not in furniture, on the wall) · every door has a porch set + camera · each camera renders a non-black picture ·
///   the Depth API occlusion keywords are off during a security render and exactly as before afterwards · the headset camera
///   culls layer 11 · a man on the porch is in view and raises Motion · the approach points are on camera, the off points
///   are not · the doorbell rings and brings the feed up · with two doors: the inset, ShowFeed swaps, motion swaps ·
///   the exterior door mirrors the real leaf (closed / open / burst), rounds put holes on it at the same spot + splinters out,
///   holes clear with the door · a bad call brings two police cars into the front feed, gone on Cleared · render gating.
/// PNGs (look at them): Logs/security/feed_*.png (raw feeds), monitor*.png (the screen as the player sees it), door_*.png
///   (the exterior door seen from the step, layer 11).
///   GRAPHICS=1 scripts/unity-batch.sh SecurityTests.RunHeadless           (the Editor room: one door)
///   GRAPHICS=1 scripts/unity-batch.sh SecurityTests.RunTwoDoorsHeadless   (Editor/TestRooms/BrettRoomTwoDoors.json)
///   scripts/unity-batch.sh SecurityTests.MakeAssets                       (Resources/Security materials; RunHeadless does it too)
/// </summary>
public static class SecurityTests
{
    const string AssetDir = "Assets/_Game/Resources/Security";
    const string TwoDoorRoom = "Assets/_Game/Editor/TestRooms/BrettRoomTwoDoors.json";
    const string OutDir = "Logs/security";

    static int _pass, _fail;
    static bool s_twoDoors;

    // ───────────────────────────── assets ─────────────────────────────

    /// <summary>The runtime materials, in Resources so the build carries them and their shaders. Idempotent.</summary>
    [MenuItem("HIS/Security/Make Assets")]
    public static void MakeAssets()
    {
        Mat("SecurityScreen", "HIS/SecurityScreen", _ => { });
        Mat("SecurityPorch", "HIS/SecurityPorch", _ => { });
        Mat("SecurityPorchGlow", "HIS/SecurityPorch", m =>
        {
            m.SetFloat("_Glow", 1f); m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f); m.SetFloat("_ZWrite", 0f);
            m.renderQueue = 3000;
        });
        Mat("SecurityBezel", "HIS/OcclusionLitPBR", m =>
        {
            m.SetColor("_Color", new Color(0.035f, 0.036f, 0.04f));
            m.SetFloat("_Glossiness", 0.6f);
            m.SetFloat("_EnvironmentDepthBias", 0.06f); // 2 cm off a real wall: Meta's bias for things flush with surfaces
        });
        AssetDatabase.SaveAssets();
        Debug.Log($"[HIS] Security assets ready in {AssetDir}");
    }

    static void Mat(string name, string shaderName, Action<Material> setup)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) { Debug.LogError($"[HIS] Security assets: shader {shaderName} missing"); return; }
        string path = $"{AssetDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(shader) { name = name };
            setup(m);
            AssetDatabase.CreateAsset(m, path);
        }
        else { m.shader = shader; setup(m); EditorUtility.SetDirty(m); }
    }

    // ───────────────────────────── batch entries ─────────────────────────────

    public static void RunHeadless() => Run(false);
    public static void RunTwoDoorsHeadless() => Run(true);

    static void Run(bool twoDoors)
    {
        s_twoDoors = twoDoors;
        MakeAssets();
        HISEditorMenu.OpenMain();
        if (twoDoors)
        {
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(TwoDoorRoom);
            var mruk = Object.FindAnyObjectByType<MRUK>();
            if (json == null || mruk == null) Debug.LogError($"[HIS] SECURITY two doors: no room json ({json}) or MRUK ({mruk})");
            else { mruk.SceneSettings.SceneJsons = new[] { json }; Debug.Log("[HIS] SECURITY: the two-door Editor room"); }
        }
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 600f;
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] SECURITY watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += async s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            await RunAll();
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    [MenuItem("HIS/Debug/Run Security Tests")]
    static async void RunInPlayMode()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[HIS] SECURITY tests need Play Mode"); return; }
        await RunAll();
    }

    static async Task RunAll()
    {
        _pass = _fail = 0;
        Directory.CreateDirectory(OutDir);
        try
        {
            HIS.Core.PlayerState.GodMode = true;
            ScenarioDirector.WaitForForced = true;
            ScenarioDirector.VariedEntries = false;
            if (!await WaitFor(() => RoomScanner.Instance != null && RoomScanner.Instance.IsReady && RoomScanner.Instance.PrimaryDoor != null, 40))
            { Fail("room", "no room/door"); return; }
            var rs = RoomScanner.Instance;
            if (!rs.DoorConfirmed) rs.ConfirmDoor();
            if (s_twoDoors)
            {
                if (rs.SecondDoor == null && DoorPicker.Instance != null) { DoorPicker.AutoSecondDoor = true; DoorPicker.Instance.Begin(); }
                await WaitFor(() => VirtualDoor.All.Count == 2, 15);
            }
            int doors = VirtualDoor.All.Count;
            await WaitFor(() => { foreach (var d in VirtualDoor.All) if (d == null || SecurityCamera.For(d) == null || PorchSet.For(d) == null) return false; return true; }, 10);
            int rigs = 0; foreach (var d in VirtualDoor.All) if (d != null && SecurityCamera.For(d) != null && PorchSet.For(d) != null) rigs++;
            Check("a porch + camera per door", rigs == doors && doors == (s_twoDoors ? 2 : doors) && doors > 0, $"{rigs} rig(s) for {doors} door(s)");
            if (!await WaitFor(() => SecurityMonitor.Instance != null && SecurityMonitor.Instance.Placed, 10)) { Fail("monitor", "never placed"); return; }
            await Wait(0.6f); // the monitor binds its feeds on its 0.5 s tick

            LayerChecks();
            MonitorPlacement();
            var front = VirtualDoor.Instance;
            var frontCam = SecurityCamera.For(front);
            foreach (var c in SecurityCamera.All) if (c != null) await FeedRenders(c);
            await PlayerLoopRenders(frontCam);
            await KeywordChecks(frontCam);
            PorchPoints();
            await DoorFeed(front, frontCam);
            await Police(front, frontCam);
            await ThroughTheDoorway(front);
            if (s_twoDoors) await SideDoorway();
            await IntruderOnPorch(front, frontCam);
            await Doorbell(front);
            if (s_twoDoors) await TwoFeeds();
            await WalkUp(front, frontCam);
            await Cost(frontCam);
        }
        catch (Exception e) { _fail++; Debug.LogError($"[HIS] SECURITY exception: {e}"); }
        finally
        {
            SecurityMonitor.TestSeen = null;
            ScenarioDirector.WaitForForced = false;
            ScenarioDirector.VariedEntries = true;
            Debug.Log($"[HIS] SECURITY {_pass}/{_pass + _fail} passed");
        }
    }

    // ───────────────────────────── checks ─────────────────────────────

    static void LayerChecks()
    {
        int named = LayerMask.NameToLayer("Outside");
        Check("layer 11 is 'Outside'", named == PorchSet.OutsideLayer, $"NameToLayer(\"Outside\") = {named}");
        var main = Camera.main;
        // The headset draws layer 11 only through open doorways (DoorwayView): its renderer keeps it out of the normal
        // passes; without that set-up it must cull it outright.
        bool viaDoorway = DoorwayView.Available;
        Check("headset sees layer 11 only through the doorway", main != null && (viaDoorway || (main.cullingMask & (1 << PorchSet.OutsideLayer)) == 0),
              main != null ? $"{main.name} mask 0x{main.cullingMask:X}, doorway passes {viaDoorway}" : "no Camera.main");
        foreach (var c in SecurityCamera.All)
            if (c != null) Check($"{c.Label} camera renders only layer 11", c.Camera.cullingMask == 1 << 11 && c.Camera.stereoTargetEye == StereoTargetEyeMask.None
                                 && c.Feed.width == SecurityCamera.FeedWidth && c.Feed.antiAliasing == 1, $"mask 0x{c.Camera.cullingMask:X}, {c.Feed.width}x{c.Feed.height} msaa {c.Feed.antiAliasing}");
    }

    static void MonitorPlacement()
    {
        var m = SecurityMonitor.Instance; var door = VirtualDoor.Instance; var geo = RoomScanner.Instance.Geometry;
        float floorY = geo != null && geo.Walls.Count > 0 ? geo.FloorY : door.Threshold.y;
        var s = m.ScreenCenter; var face = m.Facing;
        float w = door.Opening.width;
        var along = Flat(door.HingeToLatch).normalized; var c = Flat(door.Center);
        float mw = m.BezelSize.x;
        float fromEdge = Vector3.Distance(Flat(s), c) - w * 0.5f - mw * 0.5f; // the monitor's near edge from the opening's
        float height = s.y - floorY;
        string where = $"{m.Placement}; {m.BezelSize.x:F2}x{m.BezelSize.y:F2} m, centre {s:F2}, near edge {fromEdge:F2} m from the frame edge, {height:F2} m high, facing {face:F2}";
        Check("monitor beside the front door", fromEdge > 0.05f && fromEdge <= 1.3f, where);
        Check("monitor at eye-ish height", height >= 1.3f && height <= 1.6f, $"{height:F2} m");
        bool intoRoom = geo != null && geo.Floor.Count >= 3 ? geo.Inside(F2(s + face * 0.4f)) : Vector3.Dot(face, door.Inward) > 0.9f;
        Check("monitor faces into the room", intoRoom && Mathf.Abs(face.y) < 0.01f, $"facing {face:F2}, door inward {door.Inward:F2}");
        // Leaf swing: the leaf (≈ the opening's width) sweeps a disc round the hinge; the monitor must stay out of it.
        var hinge = c - along * w * 0.5f;
        var tangent = Vector3.Cross(Vector3.up, Flat(face).normalized);
        var a = Flat(s) - tangent * mw * 0.5f; var b = Flat(s) + tangent * mw * 0.5f;
        float swing = Vector2.Distance(F2(hinge), RoomGeometry.ClosestOnSegment(F2(hinge), F2(a), F2(b)));
        bool latchSide = Vector3.Dot(Flat(s) - c, along) > 0f;
        Check("monitor out of the leaf's swing", swing > w, $"{swing:F2} m from the hinge (leaf {w:F2} m), {(latchSide ? "latch" : "hinge")} side");
        // Furniture (independent of the placement code): nothing at screen height within 5 cm of the monitor's footprint.
        string inFurniture = null;
        if (geo != null)
            foreach (var box in geo.Boxes)
            {
                if (box.Top < height + floorY - m.BezelSize.y * 0.5f || box.Bottom > height + floorY + m.BezelSize.y * 0.5f) continue;
                for (int i = 0; i <= 4; i++)
                {
                    var q = F2(Vector3.Lerp(a, b, i / 4f));
                    if (Vector2.Distance(q, RoomGeometry.ClosestOnBox(q, box)) < 0.05f) inFurniture = $"{box.Name} at {box.Center}";
                }
            }
        Check("monitor not in furniture", inFurniture == null, inFurniture ?? $"{geo?.Boxes.Count ?? 0} furniture boxes clear");
        // On the real wall: the screen face ~2 cm off the nearest scanned wall.
        if (geo != null && geo.Walls.Count > 0)
        {
            float best = float.MaxValue;
            foreach (var wall in geo.Walls) best = Mathf.Min(best, Vector2.Distance(F2(s), RoomGeometry.ClosestOnSegment(F2(s), wall.A, wall.B)));
            Check("monitor on the wall (~2 cm off)", best > 0.01f && best < 0.05f, $"screen {best * 100f:F1} cm from the nearest wall");
        }
    }

    static async Task FeedRenders(SecurityCamera cam)
    {
        cam.RenderNow();
        await NextFrame();
        var px = Read(cam.Feed);
        Stats(px, out float mean, out float sd);
        Save(px, cam.Feed.width, cam.Feed.height, $"feed_{Slug(cam.Label)}_empty.png");
        Check($"{cam.Label} feed renders (not black)", mean > 0.03f && sd > 0.02f, $"mean luma {mean:F3}, sd {sd:F3}, {cam.FramesRendered} frames");
    }

    static bool s_loopRenders;

    /// <summary>
    /// Render gating (research/21 §1.1): with the screen "seen", a camera renders once (the first picture), then holds while
    /// nobody's in its view. Batch mode may not render offscreen cameras from the player loop at all: then this is info only.
    /// </summary>
    static async Task PlayerLoopRenders(SecurityCamera cam)
    {
        SecurityMonitor.TestSeen = true;
        cam.Refresh();
        int n0 = cam.FramesRendered;
        await Wait(0.6f);
        int n1 = cam.FramesRendered;
        s_loopRenders = n1 > n0;
        await Wait(1.2f);
        int n2 = cam.FramesRendered;
        if (!s_loopRenders) Debug.Log($"[HIS] SECURITY info: no player-loop renders in batch mode (0 in 0.6 s after a refresh); gating checked by state only");
        Check("holds the last frame while nobody's in view", n2 == n1 && !cam.Live && !cam.IntruderInView,
              $"refresh rendered {n1 - n0}, then {n2 - n1} renders in 1.2 s; Live {cam.Live}, target {cam.TargetFps} fps");
    }

    static async Task KeywordChecks(SecurityCamera cam)
    {
        const string H = "HARD_OCCLUSION", S = "SOFT_OCCLUSION";
        bool h0 = Shader.IsKeywordEnabled(H), s0 = Shader.IsKeywordEnabled(S);
        foreach (var (hard, soft) in new[] { (true, false), (false, true), (false, false) })
        {
            Set(H, hard); Set(S, soft);
            cam.RenderNow();
            bool hAfter = Shader.IsKeywordEnabled(H), sAfter = Shader.IsKeywordEnabled(S);
            Check($"occlusion keywords off in the render, restored after (hard {hard}, soft {soft})",
                  cam.LastRenderOcclusionOff && hAfter == hard && sAfter == soft, $"off during render {cam.LastRenderOcclusionOff}, after: hard {hAfter} soft {sAfter}");
        }
        // The IR illuminator borrows the one directional light only during the render: exactly as it was afterwards.
        var sun = RenderSettings.sun;
        if (sun == null) foreach (var l in Object.FindObjectsByType<Light>()) if (l.type == LightType.Directional && l.isActiveAndEnabled) { sun = l; break; }
        if (sun != null)
        {
            var rot = sun.transform.rotation; float inten = sun.intensity; var col = sun.color;
            cam.RenderNow();
            Check("key light restored after a security render", sun.transform.rotation == rot && sun.intensity == inten && sun.color == col,
                  $"{sun.name}: rotation {Quaternion.Angle(rot, sun.transform.rotation):F3}° off, intensity {inten:F3}→{sun.intensity:F3}");
        }
        Check("no extra lights on the security cameras", cam.GetComponentsInChildren<Light>(true).Length == 0, "IR = the key light, borrowed during the render");
        // With HARD_OCCLUSION on (the headset's state) the porch must still render (no environment depth to occlude it).
        Set(H, true); Set(S, false);
        cam.RenderNow();
        await NextFrame();
        Stats(Read(cam.Feed), out float mean, out _);
        Check("feed renders with the Depth API keywords on", mean > 0.03f, $"mean luma {mean:F3}");
        Set(H, h0); Set(S, s0);
    }

    static void Set(string kw, bool on) { if (on) Shader.EnableKeyword(kw); else Shader.DisableKeyword(kw); }

    static void PorchPoints()
    {
        foreach (var cam in SecurityCamera.All)
        {
            if (cam == null) continue;
            var p = cam.Porch;
            var app = p.Approach;
            int seen = 0; string missed = "";
            for (int i = 0; i < app.Length; i++)
                if (cam.Sees(app[i] + Vector3.up * 0.9f) || cam.Sees(app[i] + Vector3.up * 1.6f)) seen++; else missed += $" #{i}";
            Check($"{cam.Label}: approach on camera", seen >= app.Length - 1 && cam.Sees(p.Step + Vector3.up * 1.4f),
                  $"{seen}/{app.Length} points in view{(missed.Length > 0 ? ", not:" + missed : "")}; step {p.Step:F2}");
            bool offSeen = false;
            foreach (var o in new[] { p.OffLeft, p.OffRight })
                foreach (float h in new[] { 0.1f, 0.9f, 1.75f }) offSeen |= cam.Sees(o + Vector3.up * h);
            Check($"{cam.Label}: off-view points out of the picture", !offSeen, $"left {p.OffLeft:F2} right {p.OffRight:F2}");
            float stepToDoor = Vector3.Dot(p.Step - cam.Door.Center, cam.Door.Outward);
            Check($"{cam.Label}: step ~0.6 m outside the door", Mathf.Abs(stepToDoor - 0.6f) < 0.1f, $"{stepToDoor:F2} m");
        }
    }

    // ───────────────────────────── the door on the feed (Brett 0.1.22) ─────────────────────────────

    /// <summary>
    /// The exterior leaf copies the real one (closed / opened / burst), rounds through the door put holes on it at the same
    /// leaf-space spot and blow splinters out onto the porch (layer 11), holes follow the swing and clear with the door, and the
    /// camera stays live while any of it moves — and idles after.
    /// </summary>
    static async Task DoorFeed(VirtualDoor door, SecurityCamera cam)
    {
        var ext = ExteriorDoor.For(door);
        var real = door.transform.Find("Hinge/Leaf");
        Check("exterior door built for the front door", ext != null && ext.Leaf != null && real != null,
              $"exterior {(ext != null ? ext.name : "none")}, real leaf {(real != null ? "found" : "missing")}, tracking {ext?.Tracking}");
        if (ext == null || real == null) return;
        SecurityMonitor.TestSeen = true;
        door.ResetDoor();
        await NextFrame();
        string wrong = null;
        foreach (var r in ext.GetComponentsInChildren<Renderer>(true))
            if (r.gameObject.layer != PorchSet.OutsideLayer || r.enabled) wrong = $"{r.name} layer {r.gameObject.layer} enabled {r.enabled}";
        Check("exterior door: layer 11 only, drawn only in the feed's render", wrong == null, wrong ?? "all on layer 11, off between feed renders");
        Check("exterior leaf closed with the door", Mirrors(ext, real, door, out var info) && door.OpenAngle < 0.1f, info);

        // Opened normally.
        door.OpenGently(0.4f, 60f);
        await Wait(0.7f);
        ext.Sync();
        var latchEdge = ext.Leaf.TransformPoint(new Vector3((Vector3.Dot(door.HingeToLatch, ext.Leaf.right) >= 0f ? 1f : -1f) * door.Opening.width * 0.45f, 0f, 0f));
        float into = Vector3.Dot(latchEdge - door.Center, door.Inward);
        Check("exterior leaf opens with the door, into the room", Mirrors(ext, real, door, out info) && Mathf.Abs(door.OpenAngle - 60f) < 1f && into > 0.3f,
              $"{info}; latch edge {into:F2} m into the room");
        door.ResetDoor(); await NextFrame();

        // Burst open: follows the swing frame by frame, dust and splinters out, the camera kept live.
        cam.Refresh(); await NextFrame();
        int f0 = cam.FramesRendered;
        door.Breach();
        await NextFrame(); await NextFrame();
        bool swingOk = Mirrors(ext, real, door, out var swingInfo);
        bool liveInSwing = cam.Live;
        await Wait(0.08f);
        cam.RenderNow(); await NextFrame();
        Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, "feed_front_burst.png");
        await WaitFor(() => door.State == VirtualDoor.DoorState.Open, 4f);
        await Wait(0.3f);
        ext.Sync();
        Check("exterior leaf follows the burst (mid-swing and at rest)", swingOk && Mirrors(ext, real, door, out info) && door.OpenAngle > 45f, $"swing: {swingInfo}; rest: {info}");
        Check("burst: splinters/dust out on the porch, camera live", liveInSwing && (ext.Airborne || ext.Splinters.particleCount + ext.Dust.particleCount > 0 || cam.FramesRendered > f0),
              $"live in the swing {liveInSwing}, particles {ext.Splinters.particleCount}+{ext.Dust.particleCount}, renders {cam.FramesRendered - f0}");
        DoorView(door, "door_front_burst.png");
        door.ResetDoor(); await Wait(0.2f);

        // Rounds through the closed door from inside: exit holes at the real leaf's exit points, splinters blown out.
        var up = Vector3.up;
        var muzzle = door.Center + door.Inward * 2.2f - up * 0.35f;
        var aim = (door.Center + door.HingeToLatch * 0.12f - up * 0.25f - muzzle).normalized;
        if (!door.RaycastLeaf(muzzle, aim, 5f, out var h)) { Fail("a round through the door", "RaycastLeaf missed the closed leaf"); return; }
        int sp0 = ext.Splinters.particleCount;
        door.BulletHole(h, aim, false, false);
        await NextFrame();
        ext.Sync();
        var holeWorld = ext.Leaf.TransformPoint(ext.HoleLocal(ext.HoleCount - 1));
        Check("a round puts an exit hole on the exterior leaf at the same spot", ext.HoleCount == 1 && Vector3.Distance(ext.HoleLocal(0), h.LocalOut) < 0.001f
              && Vector3.Distance(holeWorld, h.ExitPoint) < 0.003f, $"{ext.HoleCount} hole(s), leaf-space {ext.HoleLocal(0):F3} vs {h.LocalOut:F3}, world {Vector3.Distance(holeWorld, h.ExitPoint) * 1000f:F1} mm off");
        Check("splinters blown out onto the porch (layer 11)", ext.Splinters.particleCount > sp0 && ext.Splinters.gameObject.layer == PorchSet.OutsideLayer
              && ext.Dust.gameObject.layer == PorchSet.OutsideLayer, $"{ext.Splinters.particleCount} splinters, {ext.Dust.particleCount} dust");
        // A shotgun blast: pellets, small and many.
        for (int i = 0; i < 9; i++)
        {
            var pd = (aim + UnityEngine.Random.insideUnitSphere * 0.06f).normalized;
            if (door.RaycastLeaf(muzzle, pd, 5f, out var ph)) door.BulletHole(ph, pd, true, false);
        }
        await Wait(0.07f); // the chips are out in front of the lens
        cam.RenderNow(); await NextFrame();
        Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, "feed_front_shot.png");
        await SaveMonitor("monitor_shot.png");
        DoorView(door, "door_front_shot.png");
        Check("shotgun: a hole per pellet", ext.HoleCount == 10, $"{ext.HoleCount} holes");
        // Holes ride the leaf when it swings.
        door.OpenGently(0.3f, 70f);
        await Wait(0.6f);
        ext.Sync();
        float drift = Vector3.Distance(ext.Leaf.TransformPoint(ext.HoleLocal(0)), real.TransformPoint(h.LocalOut));
        Check("holes swing with the leaf", drift < 0.003f && door.OpenAngle > 60f, $"first hole {drift * 1000f:F1} mm from the real leaf's exit point at {door.OpenAngle:F0}°");
        DoorView(door, "door_front_open_holes.png");
        // Cap: the oldest are reused.
        for (int i = 0; i < 40; i++)
        {
            var pd = (aim + UnityEngine.Random.insideUnitSphere * 0.1f).normalized;
            door.ResetDoor(); // (closed: every pellet meets the leaf)
            if (door.RaycastLeaf(muzzle, pd, 5f, out var ph)) door.BulletHole(ph, pd, true, false);
        }
        Check("holes capped at 40 (oldest reused)", ext.HoleCount == ExteriorDoor.MaxHoles, $"{ext.HoleCount} holes after 50 rounds");
        // ResetDoor alone clears them (polled), and WorldCleared does.
        door.ResetDoor();
        await Wait(0.45f);
        Check("holes clear with the door (ResetDoor)", ext.HoleCount == 0, $"{ext.HoleCount} holes after ResetDoor");
        if (door.RaycastLeaf(muzzle, aim, 5f, out h)) door.BulletHole(h, aim, false, false);
        int before = ext.HoleCount;
        HIS.Core.GameSession.ClearWorld(true);
        await NextFrame();
        Check("holes clear on WorldCleared", before == 1 && ext.HoleCount == 0, $"{before} → {ext.HoleCount}");

        // Gating: live while anything moves, then idle (the last frame held).
        if (!await WaitFor(() => !ext.Busy && !cam.Live, 5f)) Debug.Log($"[HIS] SECURITY info: still busy after 5 s (airborne {ext.Airborne})");
        int n0 = cam.FramesRendered; await Wait(1f); int n1 = cam.FramesRendered;
        Check("feed idles once the door and splinters are still", n1 == n0 && !cam.Live, $"{n1 - n0} renders in 1 s, Live {cam.Live}");
        if (s_loopRenders)
        {
            door.OpenGently(1.2f, 50f);
            int m0 = cam.FramesRendered; await Wait(0.8f); int m1 = cam.FramesRendered;
            Check("feed renders while the door moves", m1 - m0 >= 4 && cam.Live, $"{m1 - m0} renders in 0.8 s of the door opening");
            door.ResetDoor();
        }
        await Wait(0.5f);
    }

    /// <summary>Exterior leaf pose vs the real leaf (and the swing angle vs the door's).</summary>
    static bool Mirrors(ExteriorDoor ext, Transform real, VirtualDoor door, out string info)
    {
        ext.Sync();
        float dp = Vector3.Distance(ext.Leaf.position, real.position);
        float da = Quaternion.Angle(ext.Leaf.rotation, real.rotation);
        float swing = Quaternion.Angle(ext.Leaf.rotation, door.transform.rotation);
        info = $"{dp * 1000f:F2} mm / {da:F2}° off the real leaf, swung {swing:F1}° (door {door.OpenAngle:F1}°, {door.State})";
        return dp < 0.001f && da < 0.1f && Mathf.Abs(swing - door.OpenAngle) < 0.5f;
    }

    /// <summary>A look at the door from the porch (layer 11, like the feed but from the step): the exterior leaf, holes, interior.</summary>
    static void DoorView(VirtualDoor door, string file)
    {
        var porch = PorchSet.For(door);
        if (porch == null || !SecurityCamera.CanRender) return;
        const int W = 768, H = 432;
        var go = new GameObject("DoorViewShot");
        var cam = go.AddComponent<Camera>();
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.cullingMask = 1 << PorchSet.OutsideLayer; cam.fieldOfView = 60f; cam.nearClipPlane = 0.05f;
            DoorwayView.UseFeedRenderer(cam);
            go.transform.position = porch.transform.TransformPoint(new Vector3(0.7f, 1.45f, 2.3f));
            go.transform.LookAt(porch.transform.TransformPoint(new Vector3(0f, 1.0f, 0f)));
            porch.PushLighting(1f); porch.SetLive(true);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            porch.SetLive(false);
            Save(Read(rt), W, H, file);
        }
        finally { Object.Destroy(go); rt.Release(); Object.Destroy(rt); }
    }

    /// <summary>
    /// Brett 0.1.28 (research/23): the door open, the headset sees the outside through it — the porch set in colour, the
    /// path and street, continuous with the feed — and nothing of it beside the frame; shut, the leaf. Renders from 2.3 m
    /// into the room at eye height with the headset's renderer: Logs/security/doorway_open.png, doorway_shut.png.
    /// </summary>
    static async Task ThroughTheDoorway(VirtualDoor front)
    {
        if (!SecurityCamera.CanRender) { Debug.Log("[HIS] SECURITY info: no GPU, doorway view not rendered"); return; }
        var porch = PorchSet.For(front);
        Check("doorway view set up (renderer)", DoorwayView.Available, "main renderer: layer 11 only through the doorway, a feed renderer");
        if (!DoorwayView.Available || porch == null) return;
        const int W = 640, H = 640;
        var go = new GameObject("DoorwayShot");
        var cam = go.AddComponent<Camera>();
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f); // "passthrough" grey
            cam.cullingMask = ~0; cam.fieldOfView = 70f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200f;
            var inward = front.Inward; inward.y = 0f; inward.Normalize();
            var eye = front.Center + inward * 2.3f; eye.y = porch.FloorY + 1.6f;
            go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(front.Center + Vector3.down * 0.1f - eye, Vector3.up));
            cam.targetTexture = rt;
            // Three rounds through the shut leaf first (Brett 0.1.40 had put three through it): holes on the leaf and on the
            // camera's copy of its outside.
            for (int k = 0; k < 3; k++)
            {
                var from = front.Center + inward * 1.5f + Vector3.up * (0.1f * k - 0.1f);
                var to = front.Center + Vector3.up * (0.25f * k - 0.2f) + front.HingeToLatch.normalized * (0.15f * k - 0.1f);
                var dir = (to - from).normalized;
                if (front.RaycastLeaf(from, dir, 5f, out var hit)) front.BulletHole(in hit, dir, false, false);
            }
            front.Breach();
            await Wait(2.5f);                                   // swung open and settled; the porch goes live for the headset
            cam.Render();
            var open = Read(rt);
            Save(open, W, H, "doorway_open.png");
            // Brett 0.1.40: "when id get close to the open door transclucent objects like a rectangle with three gray holes
            // started appearing tethered to my vision". The view from close in: 1.2, 0.7 and 0.4 m from the opening, straight
            // on and 35° off (the hinge side: the open leaf's side), at eye height.
            foreach (float d in new[] { 1.2f, 0.7f, 0.4f })
                foreach (float yaw in new[] { 0f, 35f })
                {
                    var hingeSide = -front.HingeToLatch; hingeSide.y = 0f; hingeSide.Normalize();
                    var at = front.Center + inward * d + hingeSide * (yaw > 0f ? 0.3f : 0f); at.y = porch.FloorY + 1.6f;
                    var look = Quaternion.AngleAxis(yaw, Vector3.up) * -inward;
                    go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look, Vector3.up));
                    cam.Render();
                    Save(Read(rt), W, H, $"doorway_near_{d:F1}m_{yaw:F0}.png");
                }
            // Brett 0.1.41, same complaint: the caption plate follows the head ~1.2 m out, 14° down, and only stayed off walls
            // on the "Room" layer, which has a hole at the door frame: faced at the open doorway from ~1 m it landed outside,
            // where the frame and the real wall's eraser cut it up. Where it lands now, from 1.2 / 0.7 / 0.4 m, straight on and
            // turned 35° toward the open leaf: always on the room side of the frame plane and in front of the leaf.
            {
                int bad = 0; string where = "";
                var hingeSide = -front.HingeToLatch; hingeSide.y = 0f; hingeSide.Normalize();
                foreach (float d in new[] { 1.2f, 0.7f, 0.4f })
                    foreach (float yaw in new[] { 0f, 35f })
                    {
                        var head = front.Center + inward * d; head.y = porch.FloorY + 1.6f;
                        var fwd = Quaternion.AngleAxis(yaw, Vector3.up) * -inward;
                        if (Vector3.Dot(fwd, hingeSide) < 0f) fwd = Quaternion.AngleAxis(-yaw, Vector3.up) * -inward;   // (toward the leaf)
                        var dir = (fwd - Vector3.up * Mathf.Tan(14f * Mathf.Deg2Rad)).normalized;
                        float dist = HIS.UI.Subtitles.RoomSideDistance(head, dir, 1.15f / Mathf.Cos(14f * Mathf.Deg2Rad), 0.5f, HIS.UI.Subtitles.PlacementMask());
                        var plate = head + dir * dist;
                        float inRoom = Vector3.Dot(plate - front.Center, inward);
                        bool blocked = Physics.Linecast(head, plate, HIS.UI.Subtitles.PlacementMask(), QueryTriggerInteraction.Ignore);
                        where += $" {d:F1}m/{yaw:F0}°: {dist:F2} m out, {inRoom:F2} m inside{(blocked ? " BEHIND A WALL/LEAF" : "")};";
                        if (inRoom < 0.02f || blocked) bad++;
                    }
                Check("captions near the open door stay on the room side (not out in the doorway, not behind the leaf)", bad == 0, where);
            }
            // Brett 0.1.40: missed rounds outside must show (OutsideSurface): one landing on the path ~5 m out, one on a house
            // across the street, seen through the open doorway a frame later: Logs/security/doorway_impact.png.
            go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(front.Center + Vector3.down * 0.1f - eye, Vector3.up));
            int landed = 0;
            OutsideSurface house = null;
            foreach (var o in porch.GetComponentsInChildren<OutsideSurface>(true)) if (o.name.StartsWith("House") && (house == null || Vector3.Distance(o.transform.position, front.Center) < Vector3.Distance(house.transform.position, front.Center))) house = o;
            foreach (var target in new[] { front.Center + front.Outward * 5f + Vector3.down * 1.0f, house != null ? house.GetComponent<Collider>().bounds.center : front.Center + front.Outward * 30f })
            {
                var from = eye;
                if (Physics.Raycast(from, (target - from).normalized, out var ph, 80f, 1 << PorchSet.OutsideLayer, QueryTriggerInteraction.Ignore)
                    && ph.collider.TryGetComponent<OutsideSurface>(out var surf))
                {
                    var dir = (target - from).normalized;
                    surf.ApplyDamage(new HIS.Weapons.DamageInfo { Amount = 34f, Type = HIS.Weapons.DamageType.Bullet, Point = ph.point, Normal = ph.normal, Direction = dir, Penetration = 1f });
                    landed++;
                }
            }
            await NextFrame(); await NextFrame();
            cam.Render();
            Save(Read(rt), W, H, "doorway_impact.png");
            Check("missed rounds land outside (dust / chips through the doorway)", landed == 2, $"{landed}/2 rounds landed on a surface outside");
            // Inside the opening (the middle, a little below the head): the set (detail, not a flat box); the wall beside the
            // frame: exactly as with the door shut (nothing of the set leaks past the doorway).
            var mid = cam.WorldToViewportPoint(front.Center + Vector3.down * 0.3f);
            var beside = cam.WorldToViewportPoint(front.Center + front.HingeToLatch.normalized * -(front.Opening.width * 0.5f + 0.4f)); // hinge side: clear of the open leaf
            float inOpen = Patch(open, W, H, mid, out float spread), sideOpen = Patch(open, W, H, beside, out _);
            bool live = porch.ThroughDoorway;
            // From a sharp angle (55° off the door's axis, like looking at a side door from across the room): still the
            // outside, not the camera's dark interior box standing in front of the opening (Brett 0.1.32).
            var latchDir = front.HingeToLatch; latchDir.y = 0f; latchDir.Normalize();   // (from the latch side: the open leaf is on the hinge side)
            var side55 = (inward * Mathf.Cos(55f * Mathf.Deg2Rad) + latchDir * Mathf.Sin(55f * Mathf.Deg2Rad)).normalized;
            var eye55 = front.Center + side55 * 2.3f; eye55.y = porch.FloorY + 1.6f;
            go.transform.SetPositionAndRotation(eye55, Quaternion.LookRotation(front.Center + Vector3.down * 0.1f - eye55, Vector3.up));
            cam.Render();
            var angled = Read(rt);
            Save(angled, W, H, "doorway_open_angle.png");
            float inAngle = Patch(angled, W, H, cam.WorldToViewportPoint(front.Center + Vector3.down * 0.3f), out float spreadAngle);
            Check("door open, seen at 55°: the outside, not a black box", inAngle > 0.03f && spreadAngle > 0.004f,
                  $"opening luma {inAngle:F3} (spread {spreadAngle:F3})");
            go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(front.Center + Vector3.down * 0.1f - eye, Vector3.up));
            // The police outside, seen through the open door (continuity with the feed): the cruisers and the truck, light bars.
            var pc = PoliceCars.Instance;
            if (pc != null && pc.Begin())
            {
                pc.Send(PoliceCars.VehicleFor(3, HIS.Scenario.SquadFaction.Police));   // (the SWAT truck, whatever a Marshal night left Faction at)
                pc.Advance(8f);
                foreach (float phase in new[] { 0.12f, 0.62f })
                {
                    pc.Advance(Mathf.Repeat(phase - Mathf.Repeat(pc.Clock * PoliceCars.BarHz, 1f), 1f) / PoliceCars.BarHz);
                    await NextFrame();
                    cam.Render();
                    Save(Read(rt), W, H, phase < 0.5f ? "doorway_police_red.png" : "doorway_police_blue.png");
                }
                pc.End();
            }
            front.Close(0.01f);
            await Wait(0.6f);
            cam.Render();
            var shut = Read(rt);
            Save(shut, W, H, "doorway_shut.png");
            float inShut = Patch(shut, W, H, mid, out _), sideShut = Patch(shut, W, H, beside, out _);
            Check("door open: the outside through the doorway, nothing beside it", live && spread > 0.01f && Mathf.Abs(inOpen - inShut) > 0.05f && Mathf.Abs(sideOpen - sideShut) < 0.02f,
                  $"live {live}; opening luma {inOpen:F3} open (spread {spread:F3}) vs {inShut:F3} shut; wall beside {sideOpen:F3} vs {sideShut:F3}");
            Check("door shut: the set is feed-only again", !porch.ThroughDoorway, $"live {porch.ThroughDoorway}");
        }
        finally { cam.targetTexture = null; Object.Destroy(go); rt.Release(); Object.Destroy(rt); }
    }

    /// <summary>
    /// Brett 0.1.43: "the backyard when looking at the right side of it at an angle through the door is so dark it just looks
    /// black". The side door breached, seen from 1.4 m in: straight out, and turned 40° to each side from 0.6 m off the
    /// door's axis (looking across the opening at the far side of the yard). Logs/security/side_doorway_*.png; the mean luma
    /// of the yard seen through the opening, per view.
    /// </summary>
    static async Task SideDoorway()
    {
        if (!SecurityCamera.CanRender || !DoorwayView.Available) return;
        VirtualDoor side = null;
        foreach (var d in VirtualDoor.All) if (d != null && d != VirtualDoor.Instance) side = d;
        var porch = side != null ? PorchSet.For(side) : null;
        if (porch == null) { Fail("side doorway", "no second door / porch"); return; }
        const int W = 640, H = 640;
        var go = new GameObject("SideDoorwayShot");
        var cam = go.AddComponent<Camera>();
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            cam.cullingMask = ~0; cam.fieldOfView = 70f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200f;
            cam.targetTexture = rt;
            side.Breach();
            await Wait(2.5f);
            var inward = side.Inward; inward.y = 0f; inward.Normalize();
            var across = Vector3.Cross(Vector3.up, inward);                   // facing out (-inward): this points to the viewer's LEFT
            string info = ""; int dark = 0;
            foreach (float yaw in new[] { 0f, 40f, -40f })
            {
                // Turning right (+yaw) from left of the axis: the right side of the yard, across the opening (and vice versa).
                var at = side.Center + inward * 1.4f + across * (yaw == 0f ? 0f : Mathf.Sign(yaw) * 0.6f); at.y = porch.FloorY + 1.6f;
                var look = Quaternion.AngleAxis(yaw, Vector3.up) * -inward;
                go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look, Vector3.up));
                cam.Render();
                var px = Read(rt);
                string name = yaw == 0f ? "side_doorway_straight.png" : yaw > 0f ? "side_doorway_right.png" : "side_doorway_left.png";
                Save(px, W, H, name);
                // The yard through the opening: the part of the view past the frame plane (sampled over the opening's middle band).
                float sum = 0f; int n = 0;
                for (int k = 0; k < 9; k++)
                {
                    var p = side.Center + side.Outward * 4f + Vector3.down * 0.4f + (k - 4) * 0.8f * across;
                    var v = cam.WorldToViewportPoint(p);
                    if (v.z <= 0f || v.x < 0.05f || v.x > 0.95f || v.y < 0.05f || v.y > 0.95f) continue;
                    // only what's seen through the opening (the ray to it crosses the doorway)
                    var ray = p - at; float t = Vector3.Dot(side.Center - at, -inward) / Vector3.Dot(ray, -inward);
                    if (t <= 0f || t >= 1f) continue;
                    var cross = side.transform.InverseTransformPoint(at + ray * t);
                    if (cross.x < side.Opening.xMin || cross.x > side.Opening.xMax || cross.y < side.Opening.yMin || cross.y > side.Opening.yMax) continue;
                    sum += Patch(px, W, H, v, out _); n++;
                }
                float luma = n > 0 ? sum / n : -1f;
                if (n > 0 && luma < 0.15f) dark++;
                info += $" {name.Replace("side_doorway_", "").Replace(".png", "")}: yard luma {luma:F3} ({n} pts);";
            }
            // (Before the backyard floodlight, 0.1.43: 0.14 straight on; the sides read black on the Quest.)
            Check("backyard through the side door: lit, not black, straight on and at an angle either way", dark == 0, info);
            side.Close(0.01f);
            await Wait(0.6f);
        }
        finally { cam.targetTexture = null; Object.Destroy(go); rt.Release(); Object.Destroy(rt); }
    }

    /// <summary>Mean luma (and its spread) of a 24 px patch round viewport point <paramref name="v"/>.</summary>
    static float Patch(Color32[] px, int w, int h, Vector3 v, out float spread)
    {
        int cx = Mathf.Clamp((int)(v.x * w), 12, w - 13), cy = Mathf.Clamp((int)(v.y * h), 12, h - 13);
        float sum = 0f, sq = 0f; int n = 0;
        for (int y = cy - 12; y < cy + 12; y++)
            for (int x = cx - 12; x < cx + 12; x++)
            {
                var c = px[y * w + x];
                float l = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                sum += l; sq += l * l; n++;
            }
        float m = sum / n;
        spread = Mathf.Sqrt(Mathf.Max(0f, sq / n - m * m));
        return m;
    }

    /// <summary>Bad call: two police cars drive in on the front feed, stop in view with the light bars going, leave on Cleared.</summary>
    static async Task Police(VirtualDoor front, SecurityCamera cam)
    {
        var pc = PoliceCars.Instance;
        var show = HIS.Outcome.OutcomeShow.Instance;
        if (pc == null) { Fail("police cars", "no PoliceCars component"); return; }
        SecurityMonitor.TestSeen = true;
        if (show != null) show.PlayPreview(); else pc.Begin();
        await NextFrame();
        Check("bad call: two police cars on the front porch", pc.Active && pc.ActiveCars == 2 && pc.Porch == PorchSet.For(front),
              $"active {pc.Active}, {pc.ActiveCars} car(s), via {(show != null ? "OutcomeShow.PlayPreview" : "Begin")}");
        bool outside = AllOutside(pc, out var why);
        // Brett 2026-10-05 ("make sure it wont be too heavy on the quest"; the looks pass, PoliceCars.Looks.cs): each vehicle in
        // its own budget — ≤ 3,500 triangles a car (the cruiser with its light bar), ≤ 4,500 a truck — with no more renderers
        // than before it (cruiser 18, truck 15 + 1: the driver's cab door, a part of its own so it can swing open as he gets out,
        // 2026-10-05) and no new material (car fill, glass, porch, glow).
        bool inBudget = true; var budget = new List<string>();
        for (int v = 0; v < pc.VehicleCount; v++)
            if (pc.VehicleBudget(v, out var vName, out int vTris, out int vRenderers, out int vMats, out bool vTruck, out _))
            {
                inBudget &= vTris <= (vTruck ? 4500 : 3500) && vRenderers <= (vTruck ? 16 : 18) && vMats <= 4;
                budget.Add($"{vName} {vTris} tris, {vRenderers} renderers, {vMats} materials");
            }
        Check("police cars: in budget (≤ 3.5k triangles a car, ≤ 4.5k the truck), layer 11, no lights", inBudget && outside && budget.Count > 0,
              $"{string.Join("; ", budget)} (mean {pc.Triangles}); {why}");
        Check("police: the front feed goes big", SecurityMonitor.Instance == null || SecurityMonitor.Instance.MainDoor == front, $"main {SecurityMonitor.Instance?.MainDoor?.Label}");
        // Driving in: off the frame at first, the camera live.
        bool offAtStart = true;
        foreach (var car in pc.CarRoots) offAtStart &= !cam.Sees(car.position + Vector3.up * 0.8f);
        int f0 = cam.FramesRendered;
        await Wait(1.2f);
        Check("police drive in from out of frame, feed live", offAtStart && cam.Live && (!s_loopRenders || cam.FramesRendered - f0 >= 6),
              $"out of frame at the start {offAtStart}, live {cam.Live}, {cam.FramesRendered - f0} renders in 1.2 s");
        cam.RenderNow(); await NextFrame();
        Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, "feed_front_police_arriving.png");
        pc.Advance(Mathf.Max(0f, pc.ArriveTime - 1.2f) + 0.1f);
        await NextFrame();
        int inView = 0, angled = 0;
        var outward = front.Outward;
        foreach (var car in pc.CarRoots)
        {
            if (cam.Sees(car.position + Vector3.up * 0.8f)) inView++;
            if (Vector3.Dot(car.forward, -outward) > 0.3f) angled++;
        }
        Check("police cars pulled up in the camera's view, angled toward the house", pc.Arrived && inView == 2 && angled == 2, $"arrived {pc.Arrived}, {inView}/2 in view, {angled}/2 nose toward the house");
        // Light bars: the porch pulses with them (the porch shader's flash terms), strobe ≤ 2.5 Hz.
        var mat = pc.Porch.SetMaterial;
        float maxW = 0f, minW = 99f;
        for (int i = 0; i < 12; i++) { pc.Advance(0.05f); var a = mat.GetVector("_FlashA"); maxW = Mathf.Max(maxW, a.w); minW = Mathf.Min(minW, a.w); }
        Check("light bars flash and light the porch (≤ 2.5 Hz)", maxW > 1f && minW < maxW * 0.5f && PoliceCars.BarHz <= 2.5f, $"flash strength {minW:F2}–{maxW:F2}, {PoliceCars.BarHz} Hz cycle");
        foreach (float phase in new[] { 0.12f, 0.62f }) // car A's red half, then its blue half
        {
            pc.Advance(Mathf.Repeat(phase - Mathf.Repeat(pc.Clock * PoliceCars.BarHz, 1f), 1f) / PoliceCars.BarHz);
            cam.RenderNow(); await NextFrame();
            Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, phase < 0.5f ? "feed_front_police.png" : "feed_front_police_blue.png");
        }
        await SaveMonitor("monitor_police.png");
        // Standoff wave 3: the SWAT truck pulls up beside them (Brett 0.1.26), in view, rear doors open.
        pc.Send(PoliceCars.VehicleFor(3, HIS.Scenario.SquadFaction.Police));   // (the SWAT truck, whatever a Marshal night left Faction at)
        await NextFrame();
        pc.Advance(6f);
        await NextFrame();
        int trucks = 0; Transform truck = null;
        foreach (var car in pc.CarRoots) if (car.name.Contains("SWAT")) { trucks++; truck = car; }
        bool truckSeen = truck != null && cam.Sees(truck.position + Vector3.up * 1.2f);
        Check("wave 3: the SWAT truck pulls up in view", pc.ActiveCars == 3 && trucks == 1 && truckSeen && PoliceCars.TryGetOfficerStart(3, 0, out _, HIS.Scenario.SquadFaction.Police),
              $"{pc.ActiveCars} vehicle(s), truck {(truck != null ? truck.localPosition.ToString("F1") : "none")}, in view {truckSeen}");
        cam.RenderNow(); await NextFrame();
        Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, "feed_front_police_swat.png");
        // Skip / next round: gone.
        if (show != null) show.Clear(); else pc.End();
        await NextFrame();
        var flash = mat.GetVector("_FlashA");
        Check("police gone after the show is cleared", !pc.Active && pc.ActiveCars == 0 && flash.w == 0f, $"active {pc.Active}, {pc.ActiveCars} car(s), flash {flash.w:F2}");
        await Wait(0.5f);
    }

    static bool AllOutside(PoliceCars pc, out string why)
    {
        why = "all renderers on layer 11, none enabled between feed renders";
        foreach (var car in pc.CarRoots)
        {
            if (car.GetComponentsInChildren<Light>(true).Length > 0) { why = $"{car.name} has a Light"; return false; }
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
                if (r.gameObject.layer != PorchSet.OutsideLayer || r.enabled) { why = $"{car.name}/{r.name}: layer {r.gameObject.layer}, enabled {r.enabled}"; return false; }
        }
        return true;
    }

    static async Task IntruderOnPorch(VirtualDoor front, SecurityCamera cam)
    {
        int motions = 0;
        void OnMotion(SecurityCamera c) { if (c == cam) motions++; }
        cam.Motion += OnMotion;
        try
        {
            ScenarioDirector.ForcedDoor = front.Index;
            ScenarioDirector.ForcedNext = "Home invader";
            float t0 = Time.unscaledTime;
            if (!await WaitFor(() => Live() != null && Live().Current is IntruderT.State.Outside or IntruderT.State.BreakIn, 60))
            { Fail("intruder on the porch", "no intruder spawned"); return; }
            var man = Live();
            bool motion = await WaitFor(() => motions > 0, 3f);
            int r0 = cam.FramesRendered; await Wait(1f); int r1 = cam.FramesRendered;
            if (s_loopRenders) Check("renders while a man is in view", r1 - r0 >= 6 && cam.Live, $"{r1 - r0} renders in 1 s (≤ {SecurityCamera.Fps} fps)");
            Debug.Log($"[HIS] SECURITY info: smart IR power {cam.IrPower:F2} with him {Vector3.Distance(man.transform.position + Vector3.up * 1.1f, cam.transform.position):F2} m from the lens, zoom {cam.ZoomRect}");
            Check("man on the porch raises Motion", motion && cam.IntruderInView, $"{motions} motion event(s) in {Time.unscaledTime - t0:F1} s, in view {cam.IntruderInView}, at {man.transform.position:F2} ({man.Current})");
            // His picture: rendered on layer 11 while behind the door (lead, Intruder.SetVisible); if that isn't in yet,
            // switch his renderers on for the capture only.
            var forced = ForceVisible(man);
            cam.RenderNow();
            await NextFrame();
            var px = Read(cam.Feed);
            Save(px, cam.Feed.width, cam.Feed.height, "feed_front_man.png");
            var empty = LoadPng($"feed_{Slug(cam.Label)}_empty.png");
            float changed = empty != null ? Changed(px, empty) : -1f;
            Check("man shows on the feed", changed > 0.004f, $"{changed * 100f:F1}% of the picture changed vs the empty porch{(forced > 0 ? $" ({forced} renderers forced on)" : "")}");
            await SaveMonitor("monitor_motion.png");
            Restore();
            // At the door (kicking / pounding): right under the camera.
            if (await WaitFor(() => man != null && man.Current == IntruderT.State.BreakIn, 15))
            {
                await Wait(1.2f);
                forced = ForceVisible(man);
                cam.RenderNow(); await NextFrame();
                Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, "feed_front_at_door.png");
                await WaitFor(() => Time.unscaledTime - cam.LastMotionTime > 3.2f, 5f); // the motion flash is over
                await SaveMonitor("monitor.png");
                Restore();
                Debug.Log($"[HIS] SECURITY info: at the door {man.transform.position:F2}, in view {cam.IntruderInView}, forced {forced}");
            }
        }
        finally { cam.Motion -= OnMotion; ScenarioDirector.ForcedDoor = null; }
    }

    /// <summary>
    /// The lead's walk-up (IntruderApproach, varied rounds): the man from the street to the step on this porch. End the
    /// current round, deal a burglar with varied entries on, and grab the feed while he's out on the path.
    /// </summary>
    static async Task WalkUp(VirtualDoor front, SecurityCamera cam)
    {
        var dir = ScenarioDirector.Instance;
        var old = Live();
        if (old != null && old.Current != IntruderT.State.Dead) old.Kill();
        int done = dir.Completed;
        if (await WaitFor(() => HIS.Outcome.OutcomeShow.Instance != null && HIS.Outcome.OutcomeShow.Instance.BannerUp, 20)) dir.RequestSkip();
        if (!await WaitFor(() => dir.Completed > done, 40)) { Debug.Log("[HIS] SECURITY info: walk-up skipped (the round didn't end)"); return; }
        ScenarioDirector.VariedEntries = true;
        ScenarioDirector.ForcedDoor = front.Index;
        ScenarioDirector.ForcedNext = "Burglar who runs";
        try
        {
            if (!await WaitFor(() => Live() != null && Live() != old && Live().OnPorchPath, 60))
            { Debug.Log($"[HIS] SECURITY info: no walk-up seen ({Live()?.Summary})"); return; }
            var man = Live();
            int inView = 0, samples = 0; float minCrop = 1f;
            for (int i = 0; i < 3 && man != null && man.OnPorchPath; i++)
            {
                await Wait(i == 0 ? 0.6f : 1.6f);
                if (man == null || !man.OnPorchPath) break;
                samples++; if (cam.IntruderInView) inView++;
                minCrop = Mathf.Min(minCrop, cam.ZoomRect.z);
                cam.RenderNow(); await NextFrame();
                Save(Read(cam.Feed), cam.Feed.width, cam.Feed.height, $"feed_front_walkup_{i}.png");
                Debug.Log($"[HIS] SECURITY info: walk-up {i}: {man.transform.position:F2}, in view {cam.IntruderInView}");
                if (i == 1) await SaveMonitor("monitor_walkup.png");
            }
            Check("walk-up on camera", samples == 0 || inView == samples, $"in view {inView}/{samples} samples on the path");
            if (samples > 0) Check("smart zoom on the man on the path", minCrop < 0.85f, $"tightest crop {minCrop:F2} of the picture ({1f / minCrop:F1}x)");
        }
        finally { ScenarioDirector.VariedEntries = false; ScenarioDirector.ForcedDoor = null; }
    }

    static readonly List<(Renderer r, bool on, int layer)> s_forced = new();

    static int ForceVisible(IntruderT man)
    {
        s_forced.Clear();
        if (man == null) return 0;
        foreach (var r in man.GetComponentsInChildren<Renderer>(false))
            if (!r.enabled) { s_forced.Add((r, r.enabled, r.gameObject.layer)); r.enabled = true; r.gameObject.layer = PorchSet.OutsideLayer; }
        return s_forced.Count;
    }

    static void Restore()
    {
        foreach (var (r, on, layer) in s_forced) if (r != null) { r.enabled = on; r.gameObject.layer = layer; }
        s_forced.Clear();
    }

    static async Task Doorbell(VirtualDoor front)
    {
        var m = SecurityMonitor.Instance;
        m.RingDoorbell(front);
        await NextFrame();
        int bells = HIS.Core.Sfx.CountPlaying("door.bell");
        Check("doorbell rings and shows its feed", m.MainDoor == front && (bells > 0 || !HIS.Core.Sfx.Has("door.bell")),
              $"main feed {m.MainDoor?.Label}, door.bell voices {bells}{(HIS.Core.Sfx.Has("door.bell") ? "" : " (no door.bell in the Sfx library yet)")}");
    }

    static async Task TwoFeeds()
    {
        var m = SecurityMonitor.Instance;
        VirtualDoor side = null; foreach (var d in VirtualDoor.All) if (d != null && !d.IsFront) side = d;
        Check("two doors: inset shows the other door", m.InsetDoor == side && m.InsetLabelText == side?.Label,
              $"main {m.MainDoor?.Label}, inset {m.InsetDoor?.Label} '{m.InsetLabelText}'");
        await SaveMonitor("monitor_two.png");
        m.ShowFeed(side);
        Check("ShowFeed swaps big and inset", m.MainDoor == side && m.InsetDoor == VirtualDoor.Instance, $"main {m.MainDoor?.Label}, inset {m.InsetDoor?.Label}");
        m.ShowFeed(VirtualDoor.Instance);
        var sideCam = SecurityCamera.For(side);
        sideCam.SimulateMotion();
        await NextFrame(); await NextFrame();
        int chimes = HIS.Core.Sfx.CountPlaying("cam.motion");
        Check("motion on the inset door brings it up, flashes, chimes", m.MainDoor == side && (m.MainLabelText?.Contains("MOTION") ?? false) && (chimes > 0 || !HIS.Core.Sfx.Has("cam.motion")),
              $"main {m.MainDoor?.Label}, label '{m.MainLabelText}', cam.motion voices {chimes}");
        foreach (var c in SecurityCamera.All) if (c != null) c.RenderNow();
        await NextFrame();
        await SaveMonitor("monitor_two_swapped.png");
    }

    /// <summary>
    /// What one security render draws (URP Forward, no depth prepass/shadows/post): every enabled layer-11 renderer whose
    /// bounds meet the frustum, one draw per material, plus its triangles. (ProfilerRecorder render counters read 0 in batch.)
    /// </summary>
    static async Task Cost(SecurityCamera cam)
    {
        var planes = GeometryUtility.CalculateFrustumPlanes(cam.Camera);
        int draws = 0, tris = 0, porchTris = 0, manDraws = 0;
        cam.Porch.SetLive(true);
        try
        {
            foreach (var r in Object.FindObjectsByType<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r.gameObject.layer != PorchSet.OutsideLayer) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, r.bounds)) continue;
                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                int n = 0;
                if (mesh != null) for (int i = 0; i < mesh.subMeshCount; i++) n += (int)(mesh.GetIndexCount(i) / 3);
                draws += r.sharedMaterials.Length; tris += n;
                if (r.GetComponentInParent<PorchSet>() != null) porchTris += n; else manDraws += r.sharedMaterials.Length;
            }
        }
        finally { cam.Porch.SetLive(false); }
        await NextFrame();
        Debug.Log($"[HIS] SECURITY cost: one {cam.Label} render ≈ {draws} draw calls ({draws - manDraws} porch, {manDraws} intruder), {tris} tris " +
                  $"({porchTris} porch), {SecurityCamera.FeedWidth}x{SecurityCamera.FeedHeight}; at {SecurityCamera.Fps} fps ≈ every {72f / SecurityCamera.Fps:F0}th " +
                  $"frame at 72 Hz, and only while the player can see the monitor");
    }

    // ───────────────────────────── captures ─────────────────────────────

    /// <summary>The monitor as the player sees it: a plain camera 0.75 m in front, everything but layer 11.</summary>
    static async Task SaveMonitor(string file)
    {
        var m = SecurityMonitor.Instance;
        foreach (var c in SecurityCamera.All) if (c != null) c.RenderNow();
        await NextFrame();
        const int W = 1024, H = 640;
        var go = new GameObject("MonitorShot");
        var cam = go.AddComponent<Camera>();
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
        try
        {
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.62f, 0.6f, 0.56f, 1f);
            cam.cullingMask = ~(1 << PorchSet.OutsideLayer); cam.fieldOfView = 30f; cam.nearClipPlane = 0.05f;
            float halfH = Mathf.Atan(Mathf.Tan(Mathf.Deg2Rad * 15f) * W / H);                    // horizontal half-FOV
            float back = m.BezelSize.x * 0.6f / Mathf.Tan(halfH);                                   // the whole bezel in frame
            go.transform.position = m.ScreenCenter + m.Facing * back + Vector3.up * 0.03f;
            go.transform.LookAt(m.ScreenCenter);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            var px = Read(rt);
            Save(px, W, H, file);
        }
        finally { Object.Destroy(go); rt.Release(); Object.Destroy(rt); }
    }

    static Color32[] Read(RenderTexture rt)
    {
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply(false);
        RenderTexture.active = prev;
        var px = tex.GetPixels32();
        Object.Destroy(tex);
        return px;
    }

    static void Save(Color32[] px, int w, int h, string file)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var opaque = (Color32[])px.Clone();
        for (int i = 0; i < opaque.Length; i++) opaque[i].a = 255;
        tex.SetPixels32(opaque); tex.Apply(false);
        File.WriteAllBytes(Path.Combine(OutDir, file), tex.EncodeToPNG());
        Object.Destroy(tex);
        Debug.Log($"[HIS] SECURITY saved {OutDir}/{file}");
    }

    static Color32[] LoadPng(string file)
    {
        string path = Path.Combine(OutDir, file);
        if (!File.Exists(path)) return null;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(File.ReadAllBytes(path));
        var px = tex.GetPixels32();
        Object.Destroy(tex);
        return px;
    }

    static void Stats(Color32[] px, out float mean, out float sd)
    {
        double s = 0, s2 = 0;
        foreach (var p in px) { double l = (0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b) / 255.0; s += l; s2 += l * l; }
        mean = (float)(s / px.Length); sd = (float)Math.Sqrt(Math.Max(0, s2 / px.Length - mean * mean));
    }

    /// <summary>Share of pixels whose luma moved by more than 6% (grain-free raw feeds: only real changes count).</summary>
    static float Changed(Color32[] a, Color32[] b)
    {
        if (a.Length != b.Length) return -1f;
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int la = (a[i].r * 54 + a[i].g * 183 + a[i].b * 19) >> 8, lb = (b[i].r * 54 + b[i].g * 183 + b[i].b * 19) >> 8;
            if (Math.Abs(la - lb) > 15) n++;
        }
        return n / (float)a.Length;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    static IntruderT Live() => ScenarioDirector.Instance != null ? ScenarioDirector.Instance.Intruder : null;
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static Vector2 F2(Vector3 v) => new(v.x, v.z);
    static string Slug(string s) => s.ToLowerInvariant().Replace(' ', '_');

    static void Check(string what, bool ok, string info)
    {
        if (ok) { _pass++; Debug.Log($"[HIS] SECURITY PASS {what} ({info})"); }
        else Fail(what, info);
    }

    static void Fail(string what, string why) { _fail++; Debug.LogError($"[HIS] SECURITY FAIL {what}: {why}"); }

    static async Task Wait(float s) => await Task.Delay(Mathf.RoundToInt(s * 1000));

    static async Task NextFrame()
    {
        int f = Time.frameCount;
        await WaitFor(() => Time.frameCount > f, 2f);
    }

    static async Task<bool> WaitFor(Func<bool> cond, float timeout)
    {
        float end = Time.realtimeSinceStartup + timeout;
        while (Time.realtimeSinceStartup < end)
        {
            if (!EditorApplication.isPlaying) return false;
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }
}
