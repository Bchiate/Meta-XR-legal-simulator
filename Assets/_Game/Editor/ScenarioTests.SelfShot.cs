using System;
using System.Threading.Tasks;
using HIS.Core;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Brett 2026-10-03: "shooting yourself in the head should automatically restart at any point". The player's own gun, its
/// muzzle at his head (the temple, under the chin, looking down the barrel — the shotgun too), restarts the session the way
/// hold-Menu does (GameSession.SelfShotReason); held up beside the face or aimed normally, it's an ordinary shot. Real guns
/// from the loadout fired through Firearm.TryFire (the trigger's own call), the camera standing in for the player's head.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunSelfShotHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunSelfShotHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 240f;
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
                PlayerState.GodMode = true;
                ScenarioDirector.WaitForForced = true;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await SelfShotChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task SelfShotChecks()
    {
        var session = UnityEngine.Object.FindAnyObjectByType<GameSession>();
        var cam = Camera.main;
        if (session == null || cam == null || !DsInit()) { ExtraFail("self-shot", "no GameSession / camera, or Firearm internals renamed"); return; }
        var guns = DsMakeGuns();
        var pistol = guns.Find(g => g.Name == "M9")?.Gun ?? (guns.Count > 0 ? guns[0].Gun : null);
        var shotgun = guns.Find(g => g.Name == "Shotgun")?.Gun;
        if (pistol == null) { ExtraFail("self-shot", "no pistol in the loadout"); return; }
        try
        {
            var head = cam.transform;
            Vector3 eye = head.position, fwd = head.forward, right = head.right, up = head.up;
            Vector3 centre = eye - fwd * 0.09f;

            async Task Case(Firearm gun, string label, Vector3 muzzle, Vector3 aim, bool restarts)
            {
                await Wait(0.3f);
                int before = session.Resets;
                DsFire(gun, muzzle, aim);
                await Wait(0.2f);
                bool did = session.Resets > before && GameSession.LastResetReason == GameSession.SelfShotReason;
                string got = did ? "restarted" : session.Resets > before ? $"reset for '{GameSession.LastResetReason}'" : "an ordinary shot";
                if (did == restarts) ExtraPass($"self-shot: {label}", got);
                else ExtraFail($"self-shot: {label}", $"{got} (expected {(restarts ? "a restart" : "an ordinary shot")})");
            }

            await Case(pistol, "aimed normally, muzzle 0.5 m out", eye + fwd * 0.5f - up * 0.05f, eye + fwd * 5f, false);
            await Case(pistol, "held up beside the face, pointing ahead", eye + right * 0.2f, eye + right * 0.2f + fwd * 5f, false);
            await Case(pistol, "at the temple", eye + right * 0.12f - fwd * 0.05f, centre, true);
            await Case(pistol, "under the chin", eye - up * 0.2f + fwd * 0.04f, centre, true);
            await Case(pistol, "looking down the barrel, 0.3 m", eye + fwd * 0.3f, centre, true);
            if (shotgun != null) await Case(shotgun, "shotgun turned round at the face", eye + fwd * 0.25f, centre, true);
            // At any point: in Marshal mode too (the same restart path, whatever the director is doing).
            var dir = ScenarioDirector.Instance;
            if (dir != null)
            {
                var was = dir.CurrentMode;
                dir.SetMode(ScenarioDirector.Mode.Marshal);
                await Wait(1.5f);
                await Case(pistol, "at the temple, Marshal mode", eye + right * 0.12f - fwd * 0.05f, centre, true);
                dir.SetMode(was);
            }
        }
        finally { foreach (var g in guns) if (g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject); }
    }
}
