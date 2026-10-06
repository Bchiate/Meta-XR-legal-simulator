using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Brett 0.1.40: "a lot of the shots at the cops werent hitting". His log: ~25 M9 rounds at two officers walking up the path
/// to the side door (him ~1.25 m inside the open doorway), every one a miss "by 0.36–0.55 m from his chest" at 3–13 m, nothing
/// stopping them. The same scene with real M9 rounds (Firearm.TryFire): the door open, an officer walking up from ~10 m,
/// the muzzle where Brett stood (1.25 m in, 0.5 m aside, at hand height), rounds aimed at his head, chest, hips and upper arm
/// as he comes. Damage is ~0 so he keeps walking; the shot log says what each round hit.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunPathShotsHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunPathShotsHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 400f;
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
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await PathShotChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task PathShotChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30)) { ExtraFail("path shots: room", spawner?.Status); return; }
        if (PoliceResponse.Instance == null) new GameObject("[HIS] PoliceResponse (test)").AddComponent<PoliceResponse>();
        var sanity = GameObject.Find("[HIS] SanityCube"); if (sanity != null) sanity.SetActive(false);
        var door = HIS.Room.VirtualDoor.Instance;
        if (!DsInit() || door == null) { ExtraFail("path shots", "no door / Firearm internals renamed"); return; }
        door.Breach();
        await WaitFor(() => door.IsOpen && door.OpenAngle > 60f, 3);
        var guns = DsMakeGuns();
        var gun = guns.Find(g => g.Name == "M9");
        if (gun == null) { ExtraFail("path shots", "no M9"); return; }
        typeof(Firearm).GetField("damage", DsNP)?.SetValue(gun.Gun, 0.001f);   // he keeps walking (the head goes last: it kills)
        bool lethal = Hitbox.LethalRounds; Hitbox.LethalRounds = false;
        var lines = new List<string>();
        Application.LogCallback grab = (m, _, _) => { if (m.StartsWith("[HIS] Shot M9")) lock (lines) lines.Add(m); };
        Application.logMessageReceived += grab;
        var inward = door.Inward; inward.y = 0f; inward.Normalize();
        var side = Vector3.Cross(Vector3.up, inward);
        var muzzle = door.Center + inward * 1.25f + side * 0.5f; muzzle.y = door.Threshold.y + 1.35f;   // where Brett stood
        int roomMask = LayerMask.GetMask("Room");
        try
        {
            foreach (bool swat in new[] { false, true })
                foreach (float band in new[] { 8f, 5f })
                {
                    var def = new ScenarioDef { Name = "Target officer", Intent = Intent.Police, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun, Identity = Identity.Stranger };
                    var t = spawner.SpawnScenario(def, 0);
                    if (t == null) { ExtraFail("path shots", "no target"); return; }
                    var porch = HIS.Security.PorchSet.For(door);
                    var path = porch != null ? porch.Approach : null;
                    t.ConfigureOfficer(OfficerRole.Lead, swat ? 3 : 2, path != null && path.Length > 1 ? path[0] : door.Center + door.Outward * 10f);
                    t.name = $"Officer ({(swat ? "SWAT" : "patrol")} target)";
                    float Out() => Vector3.Dot(t.transform.position - door.Center, door.Outward);
                    string what = $"path shots: {(swat ? "SWAT" : "patrol")} at ~{band:F0} m";
                    if (!await WaitFor(() => t.OnPorchPath && Out() > 1f && Out() <= band, 30)) { ExtraFail(what, $"never came within {band} m ({Out():F1} m, {t.Current})"); t.TakeDamage(1000f); continue; }
                    var anim = t.GetComponentInChildren<Animator>();
                    Vector3 B(HumanBodyBones b) => anim.GetBoneTransform(b).position;
                    // Gear first, the head last (a head-zone hit kills: the cap/helmet top counts as the head). Each aim point is
                    // taken right before its shot: he keeps coming (a jog: ~0.3 m in 0.1 s).
                    var aims = new List<(string name, System.Func<Vector3> at)>
                    {
                        ("arm", () => (B(HumanBodyBones.LeftUpperArm) + B(HumanBodyBones.LeftLowerArm)) * 0.5f),
                        ("hips", () => B(HumanBodyBones.Hips)),
                        ("chest", () => B(HumanBodyBones.UpperChest)),
                        (swat ? "vest edge" : "chest side", () => B(HumanBodyBones.UpperChest) + Vector3.Cross(Vector3.up, (B(HumanBodyBones.Head) - muzzle).normalized) * 0.12f),   // (inside the torso's edge: the M9's 0.6° spread is ±6 cm here)
                        (swat ? "helmet top" : "cap top", () => B(HumanBodyBones.Head) + Vector3.up * (swat ? 0.2f : 0.22f)),
                        ("head", () => B(HumanBodyBones.Head) + Vector3.up * 0.08f),
                    };
                    int hit = 0, shots = 0, walled = 0; string misses = "";
                    foreach (var (name, aimAt) in aims)
                    {
                        if (t.Current == IntruderT.State.Dead) break;               // (the cap/helmet top already killed him)
                        var at = aimAt();
                        if (Physics.Linecast(muzzle, at, roomMask, QueryTriggerInteraction.Ignore)) { walled++; continue; } // not in view from here
                        lock (lines) lines.Clear();
                        DsFire(gun.Gun, muzzle, at);
                        await Wait(0.1f);
                        string line; lock (lines) line = lines.Count > 0 ? lines[0] : "(no shot line)";
                        shots++;
                        if (line.Contains("1/1 hit")) hit++; else misses += $" {name}: {line.Replace("[HIS] Shot M9: ", "")};";
                    }
                    string info = $"{hit}/{shots} hit at {Out():F1} m out ({walled} aim point(s) behind the wall from there){(misses.Length > 0 ? " —" + misses : "")}";
                    if (shots > 0 && hit == shots) ExtraPass(what, info); else ExtraFail(what, info);
                    if (t.Current != IntruderT.State.Dead) t.TakeDamage(1000f);
                    await Wait(0.5f);
                }
        }
        finally
        {
            Application.logMessageReceived -= grab;
            Hitbox.LethalRounds = lethal;
            foreach (var g in guns) if (g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject);
        }
    }
}
