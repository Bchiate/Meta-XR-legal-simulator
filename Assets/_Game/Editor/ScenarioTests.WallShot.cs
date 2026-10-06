using System;
using System.Reflection;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Scenario;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Brett 0.1.53: "bros shooting me through the wall". His log: wave 2's lead officer, in view at the open front door and
/// starting his entry from the stack beside it, fired 5 rounds from ~4.3 m (2 hits: dead) while all of Brett's M9 rounds at
/// him stopped on the wall beside the doorway. Gunman.FireShot only made hidden men (BehindDoor = not visible) shoot through
/// the opening; a visible man still outside his door could shoot through the wall (the scanned walls only stop rays from the
/// room side). The same scene, frozen: an officer with his gun out and his tell done, the player 3 m in front of the open
/// door, and FireShot called directly — beside the doorway outside (behind the wall), hidden and in view: never; in the
/// doorway outside, and inside the room: he fires.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunWallShotHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunWallShotHeadless()
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
                HIS.Core.PlayerState.GodMode = true;
                ScenarioDirector.WaitForForced = true;
                PoliceResponse.Enabled = false;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await WallShotChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    const BindingFlags WsNP = BindingFlags.NonPublic | BindingFlags.Instance;

    static async Task WallShotChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30)) { ExtraFail("wall shot: room", spawner?.Status); return; }
        if (PoliceResponse.Instance == null) new GameObject("[HIS] PoliceResponse (test)").AddComponent<PoliceResponse>();
        var door = HIS.Room.VirtualDoor.Instance;
        if (door == null) { ExtraFail("wall shot", "no door"); return; }
        door.Breach();
        await WaitFor(() => door.IsOpen && door.OpenAngle > 60f, 3);

        var def = new ScenarioDef { Name = "Wall-shot officer", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
        var cop = spawner.SpawnScenario(def, 0);
        if (cop == null) { ExtraFail("wall shot", "no officer"); return; }
        await Wait(1.5f); // dressed, gun in hand

        var fire = typeof(IntruderT).GetMethod("FireShot", WsNP);
        var tellDone = typeof(IntruderT).GetField("_tellDoneAt", WsNP);
        var player = typeof(IntruderT).GetField("_player", WsNP);
        var anim = cop.GetComponentInChildren<IntruderAnimator>();
        if (fire == null || tellDone == null || player == null || anim == null || anim.Muzzle == null)
        { ExtraFail("wall shot", "IntruderGunman internals renamed, or no gun in hand"); return; }

        // Freeze him: no agent, no brain moving him between our teleport and the shot.
        cop.StopAllCoroutines();
        var agent = cop.GetComponent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
        tellDone.SetValue(cop, 0f);

        var inward = door.Inward; inward.y = 0f; inward.Normalize();
        var side = Vector3.Cross(Vector3.up, inward);
        float floorY = door.Threshold.y, half = door.Opening.width * 0.5f;
        var head = new GameObject("[test] player head").transform;
        head.position = door.Center + inward * 3f; head.position = new Vector3(head.position.x, floorY + 1.6f, head.position.z);
        player.SetValue(cop, head);

        // The officer's real raised pistol (his GunUp, then ~0.6 s of aiming at the player's chest), so the muzzle sits where it
        // does in a fight — at the stack it already reaches past the door plane (the review of the first fix).
        // The police GunUp() (no arguments): the hitman's tell has its own GunUp(float raise), so by name alone it's ambiguous.
        var gunUp = typeof(IntruderT).GetMethod("GunUp", WsNP, null, Type.EmptyTypes, null);
        gunUp?.Invoke(cop, null);
        async Task<bool> Fires(Vector3 standAt, bool visible, string label)
        {
            cop.SetVisible(visible);
            cop.transform.position = new Vector3(standAt.x, floorY, standAt.z);
            var to = head.position - cop.transform.position; to.y = 0f;
            cop.transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
            var chest = head.position + Vector3.down * 0.35f;
            float until = Time.time + 0.6f;
            await WaitFor(() => { anim.AimAt(chest); return Time.time > until; }, 5);
            Physics.SyncTransforms();
            tellDone.SetValue(cop, 0f);
            var m = anim.Muzzle.position;
            float muzzleOut = Vector3.Dot(m - door.Center, door.Outward);
            float barrel = Vector3.Angle(anim.Muzzle.forward, chest - m);
            bool fired = (bool)fire.Invoke(cop, new object[] { 1f });
            Debug.Log($"[HIS] SCENARIO wall shot {label}: muzzle {(muzzleOut > 0f ? "outside" : "in the room")} ({muzzleOut:+0.00;-0.00} m), barrel {barrel:F0}° off the chest → {(fired ? "fired" : "held")}");
            return fired;
        }

        var besideOutside = door.Center + door.Outward * 0.35f + side * (half + 0.6f);   // behind the wall beside the doorway
        var stack = door.Center + door.Outward * 0.45f + side * 0.35f;                  // StackSpot depth, inside the opening's width
        var doorwayOutside = door.Center + door.Outward * 0.4f;                         // in the opening, outside (the link's outside end)
        var inside = door.Center + inward * 1.2f;                                       // in the room
        try
        {
            bool a = await Fires(besideOutside, true, "beside, in view");
            if (!a) ExtraPass("wall shot: beside the doorway outside, in view — holds fire", "no round through the wall");
            else ExtraFail("wall shot: beside the doorway outside, in view", "he fired through the wall (0.1.53 bug)");
            bool b = await Fires(besideOutside, false, "beside, hidden");
            if (!b) ExtraPass("wall shot: beside the doorway outside, hidden — holds fire", "no round through the wall");
            else ExtraFail("wall shot: beside the doorway outside, hidden", "he fired through the wall");
            bool s2 = await Fires(stack, true, "at the stack, gun reaching in");
            if (s2) ExtraPass("wall shot: at the stack with the gun raised — fires", "the arm reaches in through the opening");
            else ExtraFail("wall shot: at the stack with the gun raised", "he held fire with his gun through the opening (review: muzzle past the plane)");
            bool c = await Fires(doorwayOutside, true, "in the doorway outside");
            if (c) ExtraPass("wall shot: in the doorway outside — fires", "line through the opening");
            else ExtraFail("wall shot: in the doorway outside", "he held fire with a clear line through the opening");
            bool d = await Fires(inside, true, "inside");
            if (d) ExtraPass("wall shot: inside the room — fires", "clear line");
            else ExtraFail("wall shot: inside the room", "he held fire with a clear line");
        }
        finally { UnityEngine.Object.Destroy(head.gameObject); }
    }
}
