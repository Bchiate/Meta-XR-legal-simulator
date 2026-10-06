using System;
using System.Collections.Generic;
using System.Reflection;
using HIS.Weapons;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Brett 0.1.16: "holster height and stuff is good for first few seconds as game starts, sometimes you can't grab stuff tho and
/// it starts getting messed up, like shotgun on the chest if you turn around". A headless repro, no headset: the real
/// <see cref="PlayerBody"/> (its private Tick + ApplyPose, called by reflection at a fixed 72 Hz step) on a bare rig in an empty
/// scene, fed a scripted head (neck pivot + yaw/pitch, the same head-neck model PlayerBody inverts) and scripted hands (web
/// points in the real torso's frame), with the real weapon prefabs docked in its slots.
/// Each scenario checks, once the turning is over and the body has had 2.5 s to settle, that the back slot is still behind the
/// head's heading (> 120° around the neck; 139° when squared) and both hip slots are within 15 cm of the real hips; a
/// [transient] line reports how long the back slot sat in front of the neck plane ("on the chest") during the move, and a
/// [hard limit] / [clamp] line the worst head-body yaw offset (locks give way at 45°; with no lock the body follows at 30°).
/// (h) puts a hand where a slot is (live, and where the real hip/back is) and asks the real HandController.FindGrabbable —
/// the grip's own target pick — what it would take; the grab-buffer cases drive the real HandController.GripInput.
/// Deterministic (no XR, no frame timing, no scenario director).
/// Root causes found with it (0.1.16) and their regression cases:
///  1. catch-up capped at 150°/s for every turn → a 0.5 s turn-around left the body 126° behind: (b) 0.3/0.5/1 s [clamp];
///  2. both locks skipped the whole clamp → a body frozen up to 180° off: (c) hand resting on the grip, (d) fast look-down turn [hard limit];
///  3. reach lock: any hand near any slot, re-armed after one frame out → (c) jitter at the lock edge [lock budget], wrong slot [no lock];
///  4. look-down lock short-circuited the reach lock (stale timer, then a fresh freeze mid-turn): (d) hand on the hip [hard limit];
///  5. grab only on the grip's rising edge → a squeeze begun on the way in never grabbed: (h) grab buffer.
///   Headless: NOQUIT=1 scripts/unity-batch.sh BodySlotTests.RunHeadless → [HIS] BODY lines (exit 1 while anything FAILs).
///   In Play Mode (any scene; the rig is built 40 m from the origin): HIS > Debug > Run Body Slot Tests.
/// </summary>
public static class BodySlotTests
{
    const float Dt = 1f / 72f;
    const float Eye = 1.66f;          // standing eye height (Brett's capture in 0.1.16: 1.67 m)
    const float BackMin = 120f;       // settled: back slot bearing around the neck, from the head's facing
    const float HipTol = 0.15f;       // settled: each hip slot within this (horizontal) of the real hip
    const float ChestMax = 0.10f;     // transient: seconds the back slot may spend in front of the neck plane
    const float HardLimit = 45.5f;    // PlayerBody.maxYawOff (45°): no lock may hold the body further off the head than this
    const float ClampMax = 30.5f;     // PlayerBody.yawClamp (30°): with no lock the body follows the head at the clamp, 1:1
    const float SettleWait = 2.5f, SettleWatch = 2.5f;
    static readonly Vector3 Origin = new(40f, 0f, 40f);

    // Hand web points in the real torso's frame, from the neck pivot (neck at 1.585 m; hip slots ±0.21 m, 0.44 m below it; back
    // slot 0.07 m right, 0.345 m below, 0.08 m behind it). PlayerBody's reach lock: an empty hand within 0.2 m of a docked weapon's
    // grab zone, or a weapon held within 0.2 m of a free slot that takes it.
    static readonly Vector3 FrontR = new(0.20f, -0.15f, 0.50f), FrontL = new(-0.20f, -0.15f, 0.50f); // arms out front: never near a slot
    static readonly Vector3 HangR = new(0.24f, -0.70f, 0.05f), HangL = new(-0.24f, -0.70f, 0.05f);   // relaxed at the sides (0.27 m under a hip slot)
    static readonly Vector3 LowReadyR = new(0.22f, -0.40f, 0.20f);                                    // pistol at low ready by the right hip
    static readonly Vector3 CompR = new(0.03f, -0.28f, 0.30f), CompL = new(-0.03f, -0.30f, 0.28f);   // two hands, compressed ready at the chest
    static readonly Vector3 OnHipR = new(0.21f, -0.40f, 0.02f);                                       // right hand on the real right hip
    static readonly Vector3 HipEdgeR = new(0.21f, -0.44f, 0.20f);                                     // 20 cm in front of the right-hip slot: the lock's edge
    static readonly Vector3 KnifeSlotL = new(-0.21f, -0.44f, 0.02f);                                  // on the (occupied) left-hip slot

    const string M9Path = "Assets/_Game/Prefabs/Weapon_M9.prefab", GlockPath = "Assets/_Game/Prefabs/Weapon_Pistol.prefab";
    const string KnifePath = "Assets/_Game/Prefabs/Weapon_Knife.prefab", ShotgunPath = "Assets/_Game/Prefabs/Weapon_Shotgun.prefab";

    const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic, SP = BindingFlags.Static | BindingFlags.NonPublic;
    static MethodInfo s_tick, s_apply, s_snap, s_arm, s_heading, s_find, s_tryGrab, s_grip;
    static FieldInfo s_head, s_hands, s_bodyYaw, s_lookDown, s_escaped, s_reachFor, s_controller, s_zoneEnd;
    static Vector3 s_neckToEye;
    static int s_pass, s_fail;
    static readonly List<string> s_fails = new();

    // ───────────── entry points ─────────────

    public static void RunHeadless()
    {
        // An empty scene: no scenario director, no other rig, nothing else near the test rig's slots.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Application.runInBackground = true;
        double deadline = EditorApplication.timeSinceStartup + 600;
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] BODY watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            RunAll();
            EditorApplication.Exit(s_fail == 0 && s_pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    [MenuItem("HIS/Debug/Run Body Slot Tests")]
    public static void RunInPlayMode()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[HIS] BODY tests need Play Mode"); return; }
        RunAll();
    }

    // ───────────── scenarios ─────────────

    static void RunAll()
    {
        s_pass = s_fail = 0; s_fails.Clear();
        var stack = Application.GetStackTraceLogType(LogType.Log);
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        try
        {
            Bind();
            Scenarios();
        }
        catch (Exception e) { s_fail++; s_fails.Add("exception: " + e.Message); Debug.LogError($"[HIS] BODY exception: {e}"); }
        finally
        {
            Debug.Log("[HIS] BODY ───── failures ─────");
            foreach (var f in s_fails) Debug.Log("[HIS] BODY ✗ " + f);
            Debug.Log($"[HIS] BODY {s_pass}/{s_pass + s_fail} passed");
            Application.SetStackTraceLogType(LogType.Log, stack);
        }
    }

    static void Scenarios()
    {
        // (a) stand still 10 s: arms out front, then arms relaxed at the sides (a hand hanging near a hip slot).
        {
            var r = Build("a", "(a) stand still 10 s", FrontR, FrontL);
            Run(r, SettleWait, null); r.Watch = true; Run(r, 10f - SettleWait, null); r.Watch = false;
            float squared = Bearing(r.Body.Chest.position, r.Body.BackSlot.transform.position, Face(r));
            Note($"geometry: squared under the head the back slot is {squared:F0}° from facing " +
                 $"(0.07 m right, 0.08 m behind the neck, {r.Body.Chest.position.y - r.Body.BackSlot.transform.position.y:F3} m below); the ±30° clamp alone takes it to " +
                 $"{squared - 30f:F0}° (head 30° right of the body) — the 120° line allows ~{squared - BackMin:F0}° of body lag to that side. Standing eye {(float)Field(r, "_standingEye"):F2} m, " +
                 $"belt {r.Body.Waist.position.y:F3} m, sidearm {r.Pistol.name}");
            Report(r);
            GrabChecks(r, "after 10 s standing");
            Kill(r);
        }
        {
            var r = Build("a-hang", "(a) stand still 10 s, arms relaxed at the sides", HangR, HangL);
            Run(r, SettleWait, null); r.Watch = true; Run(r, 10f - SettleWait, null); r.Watch = false;
            Report(r);
            Kill(r);
        }

        // (b) turn around (180° to the right, smoothstep: peak head speed 1.5 × the mean) over 0.3 / 0.5 / 1 / 2 / 4 s, arms out
        // front. No lock: the body must follow at the ±30° clamp however fast the turn (root cause 1: a 150°/s catch-up cap).
        foreach (float secs in new[] { 0.3f, 0.5f, 1f, 2f, 4f })
        {
            var r = Build($"b{secs}", $"(b) turn around (180° right) in {secs} s", FrontR, FrontL);
            Turn(r, 180f, secs);
            if (secs <= 1f) GrabChecks(r, $"the frame the {secs} s turn ends");
            Settle(r, ClampMax);
            Kill(r);
        }
        {
            var r = Build("b1-left", "(b) turn around (180° left) in 1 s", FrontR, FrontL);
            Turn(r, -180f, 1f); Settle(r, ClampMax); Kill(r);
        }
        foreach (float secs in new[] { 1f, 2f })
        {
            var r = Build($"b{secs}-hang", $"(b) turn around in {secs} s, arms relaxed at the sides", HangR, HangL);
            Turn(r, 180f, secs); Settle(r); Kill(r);
        }

        // (c) turn around holding the pistol: at low ready by the right hip (a held gun by its own free slot), and two-handed at
        // the chest (0.1.16: hands in front re-armed the old any-hand-any-slot lock on the mirrored slots mid-turn, 180° off).
        foreach (float secs in new[] { 1f, 2f, 4f })
        {
            var r = Build($"c{secs}", $"(c) turn around in {secs} s, pistol at low ready by the right hip", FrontR, HangL);
            if (!DrawPistol(r, LowReadyR)) { Kill(r); continue; }
            Run(r, 2f, null); // the reach lock engages at the hip and runs out (1.5 s)
            Turn(r, 180f, secs);
            Run(r, 0.5f, null);
            GrabChecks(r, $"0.5 s after the {secs} s turn, pistol at low ready");
            Settle(r);
            Kill(r);
        }
        {
            var r = Build("c-comp", "(c) turn around in 1 s, pistol two-handed at the chest (compressed ready)", FrontR, CompL);
            if (DrawPistol(r, CompR))
            {
                Run(r, 2f, null);
                Turn(r, 180f, 1f);
                Run(r, 0.5f, null);
                GrabChecks(r, "0.5 s after the 1 s turn, pistol at the chest");
                Settle(r);
            }
            Kill(r);
        }

        // (c) root causes 2 and 3 — the reach lock. An empty hand resting on the M9's grip (about to draw) while the player turns
        // round: the lock may hold the slot under the hand, but gives way at 45° (it used to hold the body 180° off).
        {
            var r = Build("c-rest", "(c) empty right hand resting on the M9's grip, turn around in 1 s", FrontR, FrontL);
            Hands(r, Local(r, r.Pistol.GripPoint.position), FrontL, 0.3f); r.ResetStats();
            Turn(r, 180f, 1f); Run(r, 0.5f, null);
            GrabChecks(r, "0.5 s after turning round with a hand on the grip");
            Settle(r); Kill(r);
        }
        // A held gun bobbing in and out of the lock's edge by its free slot (±3 cm at 4 Hz) for 8 s while the head glances ±20°:
        // one lock of at most 1.5 s, not a chain (it used to re-arm on every re-entry).
        {
            var r = Build("c-jitter", "(c) pistol held at the right hip's lock edge, bobbing in and out of it for 8 s", FrontR, FrontL);
            if (DrawPistol(r, HipEdgeR + new Vector3(0f, 0f, 0.1f)))
            {
                Run(r, 0.6f, null); r.ResetStats(); // 30 cm out for 0.6 s: clear, so the first re-entry may arm a lock
                float t0 = r.T;
                Run(r, 8f, k =>
                {
                    float t = r.T - t0;
                    r.RHand = HipEdgeR + new Vector3(0f, 0f, 0.03f * Mathf.Sin(2f * Mathf.PI * 4f * t));
                    r.Yaw = r.Torso + 20f * Mathf.Sin(2f * Mathf.PI * 0.5f * t);
                });
                Result(r.Reach <= 1.55f, r.Name + " [lock budget]", $"reach lock held {r.Reach:F2} s of 8 s (want ≤ 1.5: one hold, and no re-arm until the hands are clear for 0.5 s)");
                r.Yaw = r.Torso; Settle(r);
            }
            Kill(r);
        }
        // A pistol held on the knife's (occupied) slot has nowhere to holster: no lock at all.
        {
            var r = Build("c-wrong", "(c) pistol held on the knife's occupied left-hip slot for 3 s", FrontR, FrontL);
            if (DrawPistol(r, KnifeSlotL))
            {
                Run(r, 0.3f, null); r.ResetStats();
                Run(r, 3f, null);
                Result(r.Reach == 0f, r.Name + " [no lock]", $"reach lock held {r.Reach:F2} s (want 0: that slot can't take it, the free right hip is 42 cm away)");
            }
            Kill(r);
        }

        // (d) look down at the belt (60°) for 3 s, then turn around.
        {
            var r = Build("d-down", "(d) look down at the belt 3 s, then turn around in 1 s still looking down", FrontR, FrontL);
            Look(r, 60f, 0.3f); Run(r, 3f, null); Turn(r, 180f, 1f); Run(r, 1f, null); Look(r, 5f, 0.3f);
            Settle(r); Kill(r);
        }
        {
            var r = Build("d-up", "(d) look down at the belt 3 s, look up, then turn around in 1 s", FrontR, FrontL);
            Look(r, 60f, 0.3f); Run(r, 3f, null); Look(r, 5f, 0.3f); Turn(r, 180f, 1f);
            Settle(r); Kill(r);
        }
        {
            var r = Build("d-glance", "(d) glance down 1 s, then turn around in 2 s looking down", FrontR, FrontL);
            Look(r, 60f, 0.3f); Run(r, 1f, null); Turn(r, 180f, 2f); Look(r, 5f, 0.3f);
            Settle(r); Kill(r);
        }
        {
            var r = Build("d-fast", "(d) look down at the belt 1 s, then turn around in 0.6 s still looking down", FrontR, FrontL);
            Look(r, 60f, 0.3f); Run(r, 1f, null); Turn(r, 180f, 0.6f); Run(r, 0.5f, null);
            GrabChecks(r, "0.5 s after a fast turn looking down");
            Look(r, 5f, 0.3f); Settle(r); Kill(r);
        }
        {
            var r = Build("d-hand", "(d) look down at the belt with the right hand on the hip 3 s, then turn around in 1 s looking down", FrontR, FrontL);
            Look(r, 60f, 0.3f); Hands(r, OnHipR, FrontL, 0.3f); Run(r, 3f, null); Turn(r, 180f, 1f); Run(r, 0.5f, null);
            GrabChecks(r, "0.5 s after turning round looking down, hand on the hip");
            Look(r, 5f, 0.3f); Hands(r, FrontR, FrontL, 0.3f);
            Settle(r); Kill(r);
        }

        // (e) 360° in steps, and in one go.
        {
            var r = Build("e4", "(e) turn 360° in 4 steps of 90° (0.4 s each, 1 s apart)", FrontR, FrontL);
            for (int i = 0; i < 4; i++) { Turn(r, 90f, 0.4f); Run(r, 1f, null); }
            Settle(r, ClampMax); Kill(r);
        }
        {
            var r = Build("e8", "(e) turn 360° in 8 steps of 45° (0.3 s each, 0.6 s apart), arms relaxed", HangR, HangL);
            for (int i = 0; i < 8; i++) { Turn(r, 45f, 0.3f); Run(r, 0.6f, null); }
            Settle(r); Kill(r);
        }
        {
            var r = Build("e-spin", "(e) spin 360° in 2 s", FrontR, FrontL);
            Turn(r, 360f, 2f); Settle(r, ClampMax); Kill(r);
        }
        {
            var r = Build("e-spin2", "(e) spin 720° in 2 s", FrontR, FrontL);
            Turn(r, 720f, 2f); Settle(r, ClampMax); Kill(r);
        }

        // (f) walk while turning (1 m/s, head bob 2 cm at 1.8 Hz, head sway ±4° and arm swing ±12 cm at 0.9 Hz).
        {
            var r = Build("f-arc", "(f) walk a half circle (1 m/s, 180° over 3 s), arms swinging", HangR, HangL);
            Walk(r, 3f, 1f, 180f);
            Settle(r);
            GrabChecks(r, "after walking a half circle");
            Kill(r);
        }
        {
            var r = Build("f-pivot", "(f) walk 1.5 s, turn around in 1 s mid-stride, walk back 1.5 s, arms swinging", HangR, HangL);
            Walk(r, 1.5f, 1f, 0f); Walk(r, 1f, 0.6f, 180f); Walk(r, 1.5f, 1f, 0f);
            Settle(r); Kill(r);
        }

        // (g) recenter: the world yaw jumps 180° in one frame (tracking space re-aligned; the player didn't move).
        foreach (var (id, desc, evt, hang) in new[]
                 {
                     ("g-event", "(g) recenter, world yaw jumps 180°, RecenteredPose fires (SnapYaw)", true, false),
                     ("g-noevent", "(g) recenter, world yaw jumps 180°, no RecenteredPose event", false, false),
                     ("g-hang", "(g) recenter with the event, arms relaxed at the sides", true, true),
                     ("g-hang-noevent", "(g) recenter without the event, arms relaxed at the sides", false, true),
                 })
        {
            var r = Build(id, desc, hang ? HangR : FrontR, hang ? HangL : FrontL);
            if (evt) s_snap.Invoke(r.Body, null); // OVRManager raises it before the anchors move; PlayerBody snaps on the next Tick
            r.Yaw += 180f; r.Torso += 180f;
            Step(r);
            GrabChecks(r, evt ? "the frame after a recenter" : "the frame after a recenter with no event");
            Settle(r);
            Kill(r);
        }

        // (h) root cause 5: the grab fired only on the grip's rising edge.
        GrabBuffer();

        // (i) "starts getting messed up over time": seeded random play, each move followed by 5 s standing calm.
        Soak(60, 1234);

        // (j) Brett 2026-09-25: "adjust height in settings so if a shorter person plays or taller holster and stuff doesnt get
        //     messed up". A height set in the pause menu (PlayerHeight) hangs the belt from it at once, not from the measured
        //     eyes; Auto goes back to the measured height. (The rig's head stands at 1.66 m, so a 160 cm setting is below it:
        //     no crouch is read, the belt drops by the whole difference.)
        {
            int saved = PlayerHeight.Cm;
            var r = Build("j", "(j) height setting", FrontR, FrontL);
            try
            {
                Run(r, SettleWait, null);
                float auto = r.Body.Waist.position.y - Origin.y;
                PlayerHeight.Set(160); Run(r, 0.1f, null);
                float set = r.Body.Waist.position.y - Origin.y, want = 1.60f * PlayerHeight.EyeRatio * (1f - 0.31f);
                PlayerHeight.Set(0); Run(r, 0.1f, null);
                float back = r.Body.Waist.position.y - Origin.y;
                Result(Mathf.Abs(set - want) < 0.02f && Mathf.Abs(back - auto) < 0.01f && auto - set > 0.08f, "(j) height setting",
                       $"belt {auto:F3} m on Auto (eyes {Eye} m) → {set:F3} m at 160 cm (want {want:F3}) → {back:F3} m back on Auto");
            }
            finally { PlayerHeight.Set(saved); Kill(r); }
        }
    }

    // ───────────── rig ─────────────

    sealed class Rig
    {
        public string Id, Name;
        public GameObject Root; public PlayerBody Body; public Transform Head; public HandController R, L;
        public Grabbable Pistol, Knife, Shotgun;
        public Vector3 Neck; public float Yaw, Pitch = 5f, Torso; // scripted: neck pivot, head yaw (+ right) / pitch (+ down), real torso heading
        public Vector3 RHand, LHand;                              // hand web points in the torso frame, from the neck
        public float T, Mark;
        public float WorstBack, WorstAt, Chest, MaxOff, Reach, Look;
        public float ChestRun, ChestLongest; public string Label = "", ChestLongestLabel = ""; // longest unbroken stretch on the chest
        public bool Watch, Tracing = true; public float WBack, WHip, WOff, NextTrace;
        public void ResetStats()
        {
            Mark = T; WorstBack = 999f; WorstAt = 0f; Chest = MaxOff = Reach = Look = 0f; WBack = 999f; WHip = WOff = 0f; NextTrace = T;
            ChestRun = ChestLongest = 0f; ChestLongestLabel = "";
        }
    }

    static Rig Build(string id, string name, Vector3 rHand, Vector3 lHand)
    {
        var r = new Rig { Id = id, Name = name, RHand = rHand, LHand = lHand };
        r.Root = new GameObject("[BodyTest] Rig " + id);
        r.Root.transform.position = Origin;                                     // rig origin = the floor
        r.Head = new GameObject("[BodyTest] Head").transform;
        r.Head.SetParent(r.Root.transform, false);                              // local y = eye height over the floor
        r.Body = r.Root.AddComponent<PlayerBody>();                             // Awake: Waist, Chest, Back, the three Holsters
        r.Body.enabled = false;                                                 // no Update/LateUpdate of its own: the test ticks it
        s_head.SetValue(r.Body, r.Head);
        r.R = MakeHand(r.Root.transform, false); r.L = MakeHand(r.Root.transform, true);
        s_hands.SetValue(r.Body, new[] { r.R, r.L });
        r.Pistol = Spawn(AssetDatabase.LoadAssetAtPath<GameObject>(M9Path) != null ? M9Path : GlockPath, r.Body.RightHip);
        r.Knife = Spawn(KnifePath, r.Body.LeftHip);
        r.Shotgun = Spawn(ShotgunPath, r.Body.BackSlot);
        s_arm.Invoke(r.Body, null);                                             // as Start: capture the standing eye height
        r.Neck = Origin + new Vector3(0f, Eye - s_neckToEye.y, 0f);
        r.Tracing = false;
        Run(r, 1.5f, null);                                                     // the game's first frames: yaw init, eye capture
        r.Tracing = true;
        r.ResetStats();
        return r;
    }

    static HandController MakeHand(Transform parent, bool left)
    {
        var go = new GameObject(left ? "[BodyTest] Hand L" : "[BodyTest] Hand R");
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        var h = go.AddComponent<HandController>();
        s_controller.SetValue(h, left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch);
        go.SetActive(true); // Awake with the right handedness: the AimAnchor on the web point
        h.enabled = false;  // no OVRInput polling; the test calls FindGrabbable / TryGrab itself
        return h;
    }

    static Grabbable Spawn(string path, Holster slot)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new System.IO.FileNotFoundException("[HIS] BODY: missing prefab " + path);
        var go = Object.Instantiate(prefab);
        go.name = prefab.name;
        var g = go.GetComponent<Grabbable>();
        g.HomeHolster = slot; // as Loadout.Spawn
        slot.Dock(g);
        return g;
    }

    static void Kill(Rig r)
    {
        foreach (var g in new[] { r.Pistol, r.Knife, r.Shotgun }) if (g != null) Object.DestroyImmediate(g.gameObject);
        if (r.Root != null) Object.DestroyImmediate(r.Root);
        Physics.SyncTransforms();
    }

    /// <summary>Right hand onto the pistol's grip, the real grab (TryGrab), then out to <paramref name="to"/> over 0.3 s.</summary>
    static bool DrawPistol(Rig r, Vector3 to)
    {
        var torso = Quaternion.Euler(0f, r.Torso, 0f);
        r.RHand = Quaternion.Inverse(torso) * (r.Pistol.GripPoint.position - r.Neck);
        Pose(r);
        Physics.SyncTransforms();
        try { s_tryGrab.Invoke(r.R, new object[] { true }); }
        catch (TargetInvocationException e) { Note($"{r.Id}: TryGrab threw after the grab ({e.InnerException?.GetType().Name}: {e.InnerException?.Message})"); }
        bool ok = r.R.Held == r.Pistol;
        Result(ok, $"(h) {r.Id}: draw the pistol with the right hand on its grip", ok ? $"TryGrab took {r.Pistol.name}" : $"TryGrab took {Name(r.R.Held)}");
        if (ok) Hands(r, to, r.LHand, 0.3f);
        return ok;
    }

    // ───────────── driving ─────────────

    static void Pose(Rig r)
    {
        var rot = Quaternion.Euler(r.Pitch, r.Yaw, 0f);
        r.Head.SetPositionAndRotation(r.Neck + rot * s_neckToEye, rot); // the head pivots on the neck, as PlayerBody assumes
        var torso = Quaternion.Euler(0f, r.Torso, 0f);
        PlaceHand(r.R, r.Neck + torso * r.RHand, torso);
        PlaceHand(r.L, r.Neck + torso * r.LHand, torso);
    }

    static void PlaceHand(HandController h, Vector3 web, Quaternion torso)
    {
        h.transform.rotation = torso * Quaternion.Euler(20f, 0f, 0f);
        h.transform.position += web - h.Anchor.position; // the Anchor (web point) is what the reach lock and the grab measure from
    }

    static void Step(Rig r)
    {
        r.T += Dt;
        Pose(r);
        s_tick.Invoke(r.Body, new object[] { Dt });
        s_apply.Invoke(r.Body, null);
        Measure(r);
    }

    /// <summary><paramref name="secs"/> of 72 Hz frames; <paramref name="drive"/> gets 0→1 across them.</summary>
    static void Run(Rig r, float secs, Action<float> drive)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(secs / Dt));
        for (int i = 1; i <= n; i++) { drive?.Invoke((float)i / n); Step(r); }
    }

    static float Smooth(float k) => k * k * (3f - 2f * k);

    static void Turn(Rig r, float deg, float secs, bool torso = true)
    {
        float y0 = r.Yaw, t0 = r.Torso;
        Run(r, secs, k => { r.Yaw = y0 + deg * Smooth(k); if (torso) r.Torso = t0 + deg * Smooth(k); });
    }

    static void Look(Rig r, float pitch, float secs)
    {
        float p0 = r.Pitch;
        Run(r, secs, k => r.Pitch = Mathf.Lerp(p0, pitch, Smooth(k)));
    }

    static void Hands(Rig r, Vector3 toR, Vector3 toL, float secs)
    {
        Vector3 r0 = r.RHand, l0 = r.LHand;
        Run(r, secs, k => { r.RHand = Vector3.Lerp(r0, toR, Smooth(k)); r.LHand = Vector3.Lerp(l0, toL, Smooth(k)); });
    }

    /// <summary>Walk forward at <paramref name="speed"/> while turning <paramref name="deg"/> evenly; bob, sway and arm swing.</summary>
    static void Walk(Rig r, float secs, float speed, float deg)
    {
        float y0 = r.Torso, baseY = r.Neck.y, t0 = r.T;
        Vector3 hr = r.RHand, hl = r.LHand;
        Run(r, secs, k =>
        {
            float t = r.T - t0;
            r.Torso = y0 + deg * k;
            r.Yaw = r.Torso + 4f * Mathf.Sin(2f * Mathf.PI * 0.9f * t);
            var fwd = Quaternion.Euler(0f, r.Torso, 0f) * Vector3.forward;
            r.Neck += fwd * speed * Dt;
            r.Neck.y = baseY + 0.02f * Mathf.Sin(2f * Mathf.PI * 1.8f * t);
            float swing = 0.12f * Mathf.Sin(2f * Mathf.PI * 0.9f * t);
            r.RHand = hr + new Vector3(0f, 0f, swing); r.LHand = hl - new Vector3(0f, 0f, swing);
        });
        r.Neck.y = baseY; r.Yaw = r.Torso; r.RHand = hr; r.LHand = hl;
    }

    // ───────────── measuring ─────────────

    static float Face(Rig r) => (float)s_heading.Invoke(null, new object[] { r.Head.rotation });
    static object Field(Rig r, string name) => typeof(PlayerBody).GetField(name, NP).GetValue(r.Body);
    static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    /// <summary>A world point in the real torso's frame, from the neck (how the script places hands).</summary>
    static Vector3 Local(Rig r, Vector3 world) => Quaternion.Inverse(Quaternion.Euler(0f, r.Torso, 0f)) * (world - r.Neck);

    /// <summary>0–180°: how far round from the head's facing a point sits, seen from above around <paramref name="from"/>.</summary>
    static float Bearing(Vector3 from, Vector3 p, float face)
    {
        var v = p - from;
        return Mathf.Abs(Mathf.DeltaAngle(face, Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg));
    }

    /// <summary>Where a slot would be with the body squared under the real torso (the live pivot and height).</summary>
    static Vector3 Ideal(Rig r, Holster h)
    {
        var pivot = h.Type == HolsterType.Back ? r.Body.Chest : r.Body.Waist;
        var local = Quaternion.Inverse(pivot.rotation) * (h.transform.position - pivot.position);
        return pivot.position + Quaternion.Euler(0f, r.Torso, 0f) * local;
    }

    static float HipError(Rig r, Holster h) => Flat(h.transform.position - Ideal(r, h)).magnitude;

    static void Measure(Rig r)
    {
        float face = Face(r), body = (float)s_bodyYaw.GetValue(r.Body), off = Mathf.DeltaAngle(body, face);
        float back = Bearing(r.Body.Chest.position, r.Body.BackSlot.transform.position, face);
        float hip = Mathf.Max(HipError(r, r.Body.RightHip), HipError(r, r.Body.LeftHip));
        bool lookLock = (bool)s_lookDown.GetValue(r.Body) && !(bool)s_escaped.GetValue(r.Body);
        float reachFor = (float)s_reachFor.GetValue(r.Body);
        bool reachLock = reachFor > 0f; // > 0 exactly while the reach lock holds (it runs under the look-down lock too)
        float t = r.T - r.Mark;
        if (back < r.WorstBack) { r.WorstBack = back; r.WorstAt = t; }
        if (back < 90f)
        {
            r.Chest += Dt; r.ChestRun += Dt;
            if (r.ChestRun > r.ChestLongest) { r.ChestLongest = r.ChestRun; r.ChestLongestLabel = r.Label; }
        }
        else r.ChestRun = 0f;
        r.MaxOff = Mathf.Max(r.MaxOff, Mathf.Abs(off));
        if (reachLock) r.Reach += Dt;
        if (lookLock) r.Look += Dt;
        if (r.Watch) { r.WBack = Mathf.Min(r.WBack, back); r.WHip = Mathf.Max(r.WHip, hip); r.WOff = Mathf.Max(r.WOff, Mathf.Abs(off)); }
        else if (r.Tracing && r.T >= r.NextTrace - 1e-4f && t > 0f)
        {
            r.NextTrace = r.T + 0.1f;
            Debug.Log($"[HIS] BODY trace {r.Id} t={t:F1} face={Mathf.Repeat(face, 360f):F0} body={Mathf.Repeat(body, 360f):F0} off={off:+0;-0;0} back={back:F0}° hips={hip * 100f:F0}cm" +
                      $"{(reachLock ? $" REACH({reachFor:F1}s)" : "")}{(lookLock ? " LOOKDOWN" : "")}{(r.R.Held != null ? " holding" : "")}");
        }
    }

    static void Settle(Rig r, float maxOff = HardLimit)
    {
        Run(r, SettleWait, null);
        r.Watch = true; Run(r, SettleWatch, null); r.Watch = false;
        Report(r, maxOff);
    }

    static void Report(Rig r, float maxOff = HardLimit)
    {
        bool clamp = maxOff < HardLimit;
        Result(r.MaxOff <= maxOff, r.Name + (clamp ? " [clamp]" : " [hard limit]"),
               $"max yawOff {r.MaxOff:F1}° over the whole run (want ≤ {maxOff:F1}: " +
               (clamp ? "no lock here, so the body follows the head at the ±30° clamp however fast it turns)" : "a lock gives way before the body faces 45° off the head)") +
               $", reach lock {r.Reach:F2} s, look-down lock {r.Look:F2} s");
        bool ok = r.WBack > BackMin && r.WHip < HipTol;
        Result(ok, r.Name,
               $"settled back slot {r.WBack:F0}° from facing (want > {BackMin:F0}), hips ≤ {r.WHip * 100f:F0} cm off (want < {HipTol * 100f:F0}), yawOff ≤ {r.WOff:F0}° " +
               $"| whole run: worst back {r.WorstBack:F0}° at t={r.WorstAt:F2} s, max yawOff {r.MaxOff:F0}°, reach lock {r.Reach:F2} s, look-down lock {r.Look:F2} s");
        Result(r.Chest <= ChestMax, r.Name + " [transient]",
               $"back slot in front of the neck plane (< 90° from facing, i.e. on the chest) for {r.Chest:F2} s (want ≤ {ChestMax:F2}, longest stretch {r.ChestLongest:F2} s); " +
               $"worst {r.WorstBack:F0}° at t={r.WorstAt:F2} s, max yawOff {r.MaxOff:F0}°");
    }

    // ───────────── (h) grabs ─────────────

    /// <summary>
    /// For each occupied slot: a free hand at the slot's live position, on the weapon's grip (and, for the shotgun, on the stock
    /// end of its grab zone) — FindGrabbable must pick that slot's weapon. Then a blind reach: the hand where the slot would be
    /// with the body squared under the real torso, the way a player draws without looking.
    /// </summary>
    static void GrabChecks(Rig r, string when)
    {
        foreach (var slot in new[] { r.Body.RightHip, r.Body.LeftHip, r.Body.BackSlot })
        {
            var w = slot.Occupant;
            if (w == null) continue;
            var hand = slot == r.Body.LeftHip ? r.L : r.R;
            if (hand.Held != null) hand = hand == r.R ? r.L : r.R;
            string label = slot == r.Body.RightHip ? "R hip" : slot == r.Body.LeftHip ? "L hip" : "back";
            var spots = new List<(string what, Vector3 at)> { ("at the slot", slot.transform.position), ("on its grip", w.GripPoint.position) };
            var end = (Transform)s_zoneEnd.GetValue(w);
            if (end != null) spots.Add(("on the stock", end.position));
            var misses = new List<string>();
            foreach (var (what, at) in spots) { var got = Pick(hand, at); if (got != w) misses.Add($"{what} → {Name(got)}"); }
            // Anywhere within 10 cm of where the weapon is seen and reached (26 directions × 5 and 10 cm) must grab that weapon: a
            // hip weapon's grip; the slung shotgun's stock end over the right shoulder (its grip hangs out of sight behind the hip).
            var seen = end != null && slot.Type == HolsterType.Back ? end.position : w.GripPoint.position;
            int ball = 0, ballMiss = 0; string firstMiss = null;
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                var dir = new Vector3(x, y, z).normalized;
                foreach (float rad in new[] { 0.05f, 0.10f })
                {
                    ball++;
                    var got = Pick(hand, seen + dir * rad);
                    if (got != w) { ballMiss++; firstMiss ??= $"{rad * 100f:F0} cm toward {dir:F1} → {Name(got)}"; }
                }
            }
            if (ballMiss > 0) misses.Add($"{ballMiss}/{ball} points within 10 cm of its {(seen == w.GripPoint.position ? "grip" : "stock end")} miss (first: {firstMiss})");
            string side = hand.IsLeft ? "left" : "right";
            Result(misses.Count == 0, $"(h) {r.Id} {when}: {label} ({w.name}), {side} hand on the live slot",
                   misses.Count == 0 ? $"FindGrabbable picks {w.name} at the slot, on its grip{(end != null ? ", on the stock" : "")} and anywhere within 10 cm of the {(end != null && slot.Type == HolsterType.Back ? "stock end" : "grip")}" : string.Join("; ", misses));
            var ideal = Ideal(r, slot);
            float gap = Flat(ideal - slot.transform.position).magnitude;
            var blind = Pick(hand, ideal);
            Result(blind == w, $"(h) {r.Id} {when}: {label} ({w.name}), {side} hand blind reach",
                   $"hand where the real {label} slot is, {gap * 100f:F0} cm from the live one → {Name(blind)} " +
                   $"(grab distances: {Dists(r, ideal)})");
        }
        Pose(r); // hands back where the script has them
    }

    static string Dists(Rig r, Vector3 at)
    {
        string D(Grabbable g) => g == null || g.IsHeld ? "" : $"{g.name.Replace("Weapon_", "")} {g.GrabDistance(at) * 100f:F0} cm ";
        return (D(r.Pistol) + D(r.Knife) + D(r.Shotgun)).Trim();
    }

    static Grabbable Pick(HandController h, Vector3 web)
    {
        h.transform.position += web - h.Anchor.position;
        Physics.SyncTransforms();
        return (Grabbable)s_find.Invoke(h, null);
    }

    static string Name(Grabbable g) => g == null ? "nothing" : g.name;

    // ───────────── (h) grab buffer ─────────────

    /// <summary>
    /// Root cause 5: the grab only fired on the grip's rising edge. A squeeze that begins before the hand is within reach — closing
    /// the hand on the way to the holster — found nothing and did nothing until the hand opened and squeezed again. Drives the
    /// real HandController.GripInput (grip value + dt) frame by frame while the hand moves in.
    /// </summary>
    static void GrabBuffer()
    {
        var r = Build("h-buffer", "(h) grab buffer", FrontR, FrontL);
        var fwd = new Vector3(0f, 0.05f, 0.45f); // start 45 cm in front of (and a little above) the target: out of reach of everything
        // Draw: squeeze 45 cm short of the M9's grip, keep squeezing while the hand arrives 0.25 s later.
        var m9 = Local(r, r.Pistol.GripPoint.position);
        var got = Reach(r, r.R, m9 + fwd, m9, 0f);
        Result(got == r.Pistol, "(h) h-buffer: squeeze 45 cm short of the M9, the hand arrives 0.25 s later", $"the right hand holds {Name(got)} (want {r.Pistol.name})");
        Grip(r, r.R, 0f, Dt, null); // let go right there: it holsters
        Result(r.Body.RightHip.Occupant == r.Pistol, "(h) h-buffer: let go at the right hip", $"right-hip slot holds {Name(r.Body.RightHip.Occupant)}");
        // Shotgun: squeeze on the way up to the right shoulder (its stock end), arriving 0.25 s later.
        var stock = Local(r, ((Transform)s_zoneEnd.GetValue(r.Shotgun)).position);
        got = Reach(r, r.R, stock + fwd, stock, 0f);
        Result(got == r.Shotgun, "(h) h-buffer: squeeze on the way up to the shoulder, the hand arrives 0.25 s later", $"the right hand holds {Name(got)} (want {r.Shotgun.name})");
        Grip(r, r.R, 0f, Dt, null);
        Result(r.Body.BackSlot.Occupant == r.Shotgun, "(h) h-buffer: let go over the shoulder", $"back slot holds {Name(r.Body.BackSlot.Occupant)}");
        // A fist is not a grab: squeezed 0.6 s before the hand gets there, the buffer has run out → nothing; open, squeeze → the M9.
        m9 = Local(r, r.Pistol.GripPoint.position);
        got = Reach(r, r.R, m9 + fwd, m9, 0.6f);
        Result(got == null, "(h) h-buffer: a fist closed 0.6 s before reaching the M9 doesn't grab", $"the right hand holds {Name(got)} (want nothing)");
        Grip(r, r.R, 0f, Dt, null); Grip(r, r.R, 1f, Dt, null);
        Result(r.R.Held == r.Pistol, "(h) h-buffer: open and squeeze again on the M9", $"the right hand holds {Name(r.R.Held)}");
        Grip(r, r.R, 0f, Dt, null);
        Kill(r);
    }

    /// <summary>Squeeze at <paramref name="from"/>, hold still <paramref name="wait"/> s, move to <paramref name="to"/> over 0.25 s squeezing.</summary>
    static Grabbable Reach(Rig r, HandController h, Vector3 from, Vector3 to, float wait)
    {
        bool right = h == r.R;
        void At(Vector3 p) { if (right) r.RHand = p; else r.LHand = p; }
        Grip(r, h, 0f, Dt, _ => At(from));
        Grip(r, h, 1f, Dt, null);                                    // the rising edge, out of reach
        if (wait > 0f) Grip(r, h, 1f, wait, null);
        Grip(r, h, 1f, 0.25f, k => At(Vector3.Lerp(from, to, Smooth(k))));
        Grip(r, h, 1f, 0.1f, null);
        return h.Held;
    }

    /// <summary>Body frames as <see cref="Run"/>, then the hand's real grip logic with this grip value.</summary>
    static void Grip(Rig r, HandController h, float grip, float secs, Action<float> drive)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(secs / Dt));
        for (int i = 1; i <= n; i++)
        {
            drive?.Invoke((float)i / n); Step(r);
            Physics.SyncTransforms();
            s_grip.Invoke(h, new object[] { grip, Dt });
        }
    }

    // ───────────── (i) soak ─────────────

    static void Soak(int moves, int seed)
    {
        var r = Build("i", $"(i) seeded play (seed {seed}): {moves} random moves, each followed by 5 s standing calm", HangR, HangL);
        r.Tracing = false;
        var rnd = new System.Random(seed);
        float U(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
        var poses = new (Vector3 R, Vector3 L, string Name)[]
        {
            (FrontR, FrontL, "arms out"), (HangR, HangL, "arms relaxed"), (LowReadyR, HangL, "right hand low by the hip"), (CompR, CompL, "hands at the chest"),
        };
        int bad = 0; float worst = 999f; var fails = new List<string>();
        for (int m = 0; m < moves; m++)
        {
            var p = poses[rnd.Next(poses.Length)];
            float sign = rnd.Next(2) == 0 ? -1f : 1f;
            string what = "";
            string Go(string w) { what = w; r.Label = $"#{m + 1} {p.Name}, {w}"; return w; }
            Go("hands");
            Hands(r, p.R, p.L, 0.3f);
            switch (rnd.Next(5))
            {
                case 0:
                {
                    float a = sign * U(60f, 200f), s = U(0.4f, 2.5f);
                    Go($"turn {a:F0}° in {s:F1} s"); Turn(r, a, s); break;
                }
                case 1:
                {
                    float a = sign * U(20f, 70f), h = U(0.5f, 2f);
                    Go($"head-only glance {a:F0}° for {h:F1} s"); Turn(r, a, 0.3f, torso: false); Run(r, h, null); Turn(r, -a, 0.3f, torso: false); break;
                }
                case 2:
                {
                    float pitch = U(40f, 65f), h = U(0.5f, 4f), a = sign * U(60f, 180f), s = U(0.6f, 2f);
                    bool turn = rnd.Next(2) == 0;
                    Go($"look down {pitch:F0}° for {h:F1} s{(turn ? $", turning {a:F0}° in {s:F1} s" : "")}");
                    Look(r, pitch, 0.3f); Run(r, h, null); if (turn) Turn(r, a, s); Look(r, 5f, 0.3f); break;
                }
                case 3:
                {
                    float a = sign * U(20f, 60f);
                    Go($"right hand to the hip while glancing {a:F0}°");
                    Vector3 kr = r.RHand;
                    Hands(r, OnHipR, r.LHand, 0.3f); Turn(r, a, 0.4f, torso: false); Run(r, 0.6f, null); Turn(r, -a, 0.4f, torso: false); Hands(r, kr, r.LHand, 0.3f);
                    break;
                }
                default:
                {
                    float a = sign * U(45f, 180f), s = U(1.5f, 4f);
                    Go($"walk {s:F1} s turning {a:F0}°"); Walk(r, s, 1f, a); break;
                }
            }
            r.Label += " (then standing)";
            r.WBack = 999f; r.WHip = 0f; r.WOff = 0f;
            Run(r, 4f, null); r.Watch = true; Run(r, 1f, null); r.Watch = false;
            worst = Mathf.Min(worst, r.WBack);
            if (r.WBack <= BackMin || r.WHip >= HipTol)
            {
                bad++;
                if (fails.Count < 8) fails.Add($"#{m + 1} {p.Name}, {what} → back {r.WBack:F0}°, hips {r.WHip * 100f:F0} cm, yawOff {r.WOff:F0}°");
            }
        }
        Result(bad == 0, r.Name,
               $"{bad}/{moves} calm checks off after 4 s standing still (worst back {worst:F0}°); on the chest {r.Chest:F1} s of {r.T - r.Mark:F0} s played, " +
               $"reach lock {r.Reach:F1} s, look-down lock {r.Look:F1} s{(fails.Count > 0 ? " | " + string.Join(" | ", fails) : "")}");
        Result(r.MaxOff <= HardLimit, r.Name + " [hard limit]", $"max yawOff {r.MaxOff:F1}° over {moves} moves (want ≤ {HardLimit:F1})");
        Result(r.Chest <= ChestMax * moves, r.Name + " [transient]",
               $"back slot on the chest {r.Chest:F1} s over {moves} moves (want ≤ {ChestMax * moves:F1} s, {ChestMax:F2} s a move); worst {r.WorstBack:F0}°, max yawOff {r.MaxOff:F0}°; " +
               $"longest stretch {r.ChestLongest:F2} s in {r.ChestLongestLabel}");
        Kill(r);
    }

    // ───────────── plumbing ─────────────

    static void Bind()
    {
        var pb = typeof(PlayerBody); var hc = typeof(HandController);
        s_tick = Need(pb.GetMethod("Tick", NP), "PlayerBody.Tick");
        s_apply = Need(pb.GetMethod("ApplyPose", NP), "PlayerBody.ApplyPose");
        s_snap = Need(pb.GetMethod("SnapYaw", NP), "PlayerBody.SnapYaw");
        s_arm = Need(pb.GetMethod("ArmCapture", NP), "PlayerBody.ArmCapture");
        s_heading = Need(pb.GetMethod("HeadingYaw", SP), "PlayerBody.HeadingYaw");
        s_head = Need(pb.GetField("head", NP), "PlayerBody.head");
        s_hands = Need(pb.GetField("_hands", NP), "PlayerBody._hands");
        s_bodyYaw = Need(pb.GetField("_bodyYaw", NP), "PlayerBody._bodyYaw");
        s_lookDown = Need(pb.GetField("_lookDown", NP), "PlayerBody._lookDown");
        s_escaped = Need(pb.GetField("_lookDownEscaped", NP), "PlayerBody._lookDownEscaped");
        s_reachFor = Need(pb.GetField("_reachLockFor", NP), "PlayerBody._reachLockFor");
        s_neckToEye = (Vector3)Need(pb.GetField("NeckToEye", SP), "PlayerBody.NeckToEye").GetValue(null);
        s_find = Need(hc.GetMethod("FindGrabbable", NP), "HandController.FindGrabbable");
        s_tryGrab = Need(hc.GetMethod("TryGrab", NP), "HandController.TryGrab");
        s_grip = Need(hc.GetMethod("GripInput", NP), "HandController.GripInput");
        s_controller = Need(hc.GetField("controller", NP), "HandController.controller");
        s_zoneEnd = Need(typeof(Grabbable).GetField("grabZoneEnd", NP), "Grabbable.grabZoneEnd");
    }

    static T Need<T>(T member, string what) where T : MemberInfo =>
        member ?? throw new MissingMemberException($"[HIS] BODY: {what} not found — PlayerBody/HandController changed, update BodySlotTests");

    static void Result(bool ok, string what, string info)
    {
        string line = $"[HIS] BODY {(ok ? "PASS" : "FAIL")} {what}: {info}";
        if (ok) { s_pass++; Debug.Log(line); }
        else { s_fail++; s_fails.Add($"{what}: {info}"); Debug.LogError(line); }
    }

    static void Note(string what) => Debug.Log("[HIS] BODY NOTE " + what);
}
