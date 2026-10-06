using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HIS.Core;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The AR-15 (Brett 2026-10-03: "a semi automatic ar 15 we can swap out for the shotgun", red dot, "do good animations"): the
/// loadout's own Weapon_AR15 fired through Firearm.TryFire (the trigger's call) and reloaded through Firearm.Reload (B).
/// Checks: one round per pull and no faster than 10 a second; the bolt carrier snaps back and is home again within ~0.1 s;
/// the dust cover pops open on the first shot; the last round locks the bolt back; the reload's phases come in order (mag out
/// and dropped, fresh mag in, seated, bolt release) and end chambered with a full magazine; a racked charging handle chambers an
/// empty chamber; the pause menu's Long gun swap puts it on the back sling; the Marshal weapons check's step 6 names the rifle.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunRifleHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunRifleHeadless()
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
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await RifleChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task RifleChecks()
    {
        var lo = Loadout.Instance;
        var cam = Camera.main;
        if (lo == null || cam == null || !DsInit()) { ExtraFail("rifle: setup", "no loadout / camera, or Firearm internals renamed"); return; }
        int slot = lo.SlotIndex(LoadoutSlot.LongGun);
        var opt = slot >= 0 ? Array.Find(lo.Slots[slot].Options, o => o.Id == "ar15") : null;
        if (opt == null || opt.Prefab == null) { ExtraFail("rifle: setup", "no AR-15 in the Long gun slot (run HIS > Build AR-15, then MainSceneBuilder.Build)"); return; }
        var gun = UnityEngine.Object.Instantiate(opt.Prefab) as Firearm;
        if (gun == null) { ExtraFail("rifle: setup", "the AR-15 prefab has no Firearm"); return; }
        gun.name = "Rifle_Test";
        if (gun.TryGetComponent<Rigidbody>(out var rb)) { rb.isKinematic = true; rb.detectCollisions = false; }
        var rack = typeof(Firearm).GetMethod("Rack", DsNP);
        var phases = new List<string>();
        Application.LogCallback onLog = (msg, trace, type) => { if (msg.StartsWith("[HIS] AR-15 reload: ")) phases.Add(msg.Substring("[HIS] AR-15 reload: ".Length)); };
        Application.logMessageReceived += onLog;
        try
        {
            await Wait(0.3f);
            // The parts the builder made, and the sight.
            var model = gun.transform.GetChild(0);
            var bolt = gun.Slide; var cover = gun.DustCover; var handle = gun.ChargingHandle; var mag = model.Find("Parts/Magazine");
            var glass = model.Find("Parts/Frame/RedDot/Glass");
            var mats = gun.GetComponentsInChildren<Renderer>().Where(r => !(r is ParticleSystemRenderer)).SelectMany(r => r.sharedMaterials).Where(m => m != null && m.shader.name != "Universal Render Pipeline/Particles/Unlit").Distinct().ToList();
            string partsErr = bolt == null ? "no bolt" : cover == null ? "no dust cover" : handle == null ? "no charging handle" : mag == null ? "no magazine"
                            : glass == null ? "no red-dot glass" : glass.GetComponent<Renderer>().sharedMaterial.shader.name != "HIS/RedDot" ? "the glass isn't HIS/RedDot"
                            : gun.HolsterType != HolsterType.Back ? "not slung on the back" : null;
            if (partsErr == null) ExtraPass("rifle: parts", $"bolt, dust cover, charging handle, magazine, red dot on HIS/RedDot; {mats.Count} materials ({string.Join(", ", mats.Select(m => m.name))}), {gun.Status}");
            else ExtraFail("rifle: parts", partsErr);
            if (partsErr != null) return;

            // Aimed at the floor ahead (well clear of the head: no self-shot restart), held at a standing player's chest (the batch
            // camera sits on the floor: a magazine dropped from there would fall out of the room and be cleaned up at once).
            var head = cam.transform;
            var chest = head.position; chest.y = Mathf.Max(chest.y, 1.3f);
            var muzzleAt = chest + RifleFlat(head.forward) * 0.6f;
            var aim = muzzleAt + RifleFlat(head.forward) * 1.5f - Vector3.up * 1.2f;
            void Point() { var m = gun.Muzzle; var t = gun.transform; t.rotation = Quaternion.FromToRotation(m.forward, (aim - muzzleAt).normalized) * t.rotation; t.position += muzzleAt - m.position; }
            void Pull() => s_dsTryFire.Invoke(gun, new object[] { null });
            Point();
            gun.LoadForPractice(gun.FullLoad);
            float boltRest = bolt.localPosition.z;
            var coverShut = cover.localRotation;

            // 1. Semi-auto: one round per pull; a second pull in the same frame is refused (≤ 10 a second); 0.3 s later it fires.
            int shots0 = gun.ShotsFired;
            // The bolt is timed per drawn frame (batch Play Mode runs ~3 frames a second, so it can't be sampled on the fly):
            // the frame after the shot draws it fully back, then its stroke curve (BoltStroke) is read along the clock.
            var update = typeof(Firearm).GetMethod("Update", DsNP);
            var stroke = typeof(Firearm).GetMethod("BoltStroke", DsNP);
            var boltAt = typeof(Firearm).GetField("_boltAt", DsNP);
            var pending = typeof(Firearm).GetField("_boltPending", DsNP);
            Pull();
            update.Invoke(gun, null);                                   // the next frame
            float back = boltRest - bolt.localPosition.z;
            Pull();
            bool oneOnly = gun.ShotsFired == shots0 + 1 && gun.RoundsInMag == gun.FullLoad - 2 && gun.RoundChambered;
            if (oneOnly) ExtraPass("rifle: semi-auto", $"one round per pull, the second pull refused; {gun.Status}");
            else ExtraFail("rifle: semi-auto", $"shots {gun.ShotsFired - shots0} after two quick pulls, {gun.Status}");
            var curve = new List<string>(); float homeAt = -1f;
            if (stroke != null && boltAt != null && pending != null)
            {
                pending.SetValue(gun, false);
                for (int ms = 0; ms <= 60; ms += 5)
                {
                    boltAt.SetValue(gun, Time.time - ms / 1000f);
                    float st = (float)stroke.Invoke(gun, null);
                    curve.Add($"{ms}:{st:F2}");
                    if (st <= 0f && homeAt < 0f) homeAt = ms / 1000f;
                }
                boltAt.SetValue(gun, -10f);
            }
            await Wait(0.3f);
            Pull();
            if (gun.ShotsFired == shots0 + 2) ExtraPass("rifle: rate", "fires again 0.3 s later");
            else ExtraFail("rifle: rate", $"no second round 0.3 s later ({gun.Status})");

            // 2. The bolt snaps back the full 20 mm and is home within ~45 ms; the dust cover has popped open.
            float homeBy = gun.Motion.BoltBackSeconds + gun.Motion.BoltHomeSeconds + 0.0051f;
            if (back >= gun.SlideTravel * 0.95f && homeAt > 0f && homeAt <= homeBy)
                ExtraPass("rifle: bolt cycle", $"back {back * 1000f:F0} mm on the frame after the shot, home {homeAt * 1000f:F0} ms later (stroke ms:back {string.Join(" ", curve)})");
            else ExtraFail("rifle: bolt cycle", $"back {back * 1000f:F1} mm, home {(homeAt < 0f ? "never" : $"after {homeAt * 1000f:F0} ms")} (stroke {string.Join(" ", curve)})");
            await Wait(0.15f);
            float coverTurn = Quaternion.Angle(coverShut, cover.localRotation);
            if (gun.DustCoverOpen && coverTurn > 150f) ExtraPass("rifle: dust cover", $"open {coverTurn:F0}° after the first shot");
            else ExtraFail("rifle: dust cover", $"open {gun.DustCoverOpen}, turned {coverTurn:F0}°");

            // 3. The last round locks the bolt back.
            gun.LoadForPractice(1);
            await Wait(0.15f);
            Pull();
            await Wait(0.2f);
            float held = boltRest - bolt.localPosition.z;
            if (gun.SlideLockedBack && !gun.RoundChambered && held >= gun.SlideTravel * 0.95f) ExtraPass("rifle: locks back empty", $"bolt held {held * 1000f:F0} mm back, {gun.Status}");
            else ExtraFail("rifle: locks back empty", $"locked {gun.SlideLockedBack}, bolt {held * 1000f:F1} mm back, {gun.Status}");

            // 3b. Released after the lock-back (the bolt release's state, its 35 ms slam just over): the bolt stays home — the shot
            //     that locked it back left no stroke pending to fire now (a second slam on every empty reload). One frame, by hand.
            var releaseAt = typeof(Firearm).GetField("_releaseAt", DsNP);
            if (releaseAt != null && update != null)
            {
                s_dsLocked.SetValue(gun, false); s_dsChambered.SetValue(gun, true);
                releaseAt.SetValue(gun, Time.time - gun.Motion.SlideSlamSeconds - 0.005f);
                update.Invoke(gun, null);
                float after = boltRest - bolt.localPosition.z;
                if (after < 0.0005f) ExtraPass("rifle: one slam on release", $"the bolt home ({after * 1000f:F1} mm) the frame after the release's slam");
                else ExtraFail("rifle: one slam on release", $"the bolt {after * 1000f:F1} mm back again after the release's slam (a stale stroke)");
                s_dsLocked.SetValue(gun, true); s_dsChambered.SetValue(gun, false); releaseAt.SetValue(gun, -10f);   // (step 4 reloads from locked back)
                update.Invoke(gun, null);
            }
            else ExtraFail("rifle: one slam on release", "Firearm._releaseAt / Update renamed");

            // 4. B: the reload's phases in order, a magazine on the floor, chambered by the bolt release, full again.
            phases.Clear();
            int dropped0 = UnityEngine.Object.FindObjectsByType<DroppedMag>(FindObjectsSortMode.None).Length;
            float magRest = mag.localPosition.y, lowest = 0f, highest = 0f; bool magHidden = false;
            // Game time in 5 ms frames while it's watched, every frame read (as the charging handle below): the seat's overshoot
            // past home lasts a few tens of ms, and a slow batch frame (GRAPHICS=1, a busy Mac) stepped right over it once.
            Time.captureDeltaTime = 0.005f;
            gun.Reload(null);
            float r0 = Time.time;
            try
            {
                while (Time.time - r0 < 2.2f && EditorApplication.isPlaying)
                {
                    float dy = mag.localPosition.y - magRest;
                    if (mag.gameObject.activeSelf) { lowest = Mathf.Min(lowest, dy); highest = Mathf.Max(highest, dy); } else magHidden = true;
                    int frame = Time.frameCount;
                    while (Time.frameCount == frame && EditorApplication.isPlaying) await Task.Yield();
                }
            }
            finally { Time.captureDeltaTime = 0f; }
            int dropped = UnityEngine.Object.FindObjectsByType<DroppedMag>(FindObjectsSortMode.None).Length - dropped0;
            var want = new[] { "mag out (dropped)", "fresh mag in", "seated", "bolt release: chambered" };
            bool inOrder = phases.Count == want.Length && phases.SequenceEqual(want);
            bool loaded = gun.RoundChambered && !gun.SlideLockedBack && gun.RoundsInMag == gun.FullLoad - 2 && !gun.IsReloading
                          && Mathf.Abs(bolt.localPosition.z - boltRest) < 0.0005f && Mathf.Abs(mag.localPosition.y - magRest) < 0.0005f;
            if (inOrder && loaded && dropped == 1 && magHidden && highest > 0.0002f)
                ExtraPass("rifle: reload", $"{string.Join(" → ", phases)}; the mag dropped to the floor, the fresh one rose from {-lowest * 1000f:F0} mm and seated {highest * 1000f:F1} mm past home; {gun.Status}");
            else ExtraFail("rifle: reload", $"phases [{string.Join(" | ", phases)}], dropped {dropped}, hidden {magHidden}, mag {lowest * 1000f:F0}…+{highest * 1000f:F1} mm, {gun.Status}");

            // 5. The charging handle on an empty chamber (stick click): back, released, chambered.
            float handleRest = handle.localPosition.z, pulled = 0f;
            s_dsChambered.SetValue(gun, false);
            s_dsLocked.SetValue(gun, false);
            int inMag = gun.RoundsInMag;
            // Game time in 5 ms frames while it's watched, read on every frame (the integrator, 2026-10-04: on a busy Mac one long
            // batch frame stepped over the 50 ms hold at the back, twice running: "pulled 37 / 33 mm" of a rack that chambered).
            Time.captureDeltaTime = 0.005f;
            rack.Invoke(gun, new object[] { null });
            float c0 = Time.time;
            try
            {
                while (Time.time - c0 < 0.5f && EditorApplication.isPlaying)
                {
                    pulled = Mathf.Max(pulled, handleRest - handle.localPosition.z);
                    int frame = Time.frameCount;
                    while (Time.frameCount == frame && EditorApplication.isPlaying) await Task.Yield();
                }
            }
            finally { Time.captureDeltaTime = 0f; }
            if (gun.RoundChambered && gun.RoundsInMag == inMag - 1 && pulled >= gun.ChargingHandleTravel * 0.9f && Mathf.Abs(handle.localPosition.z - handleRest) < 0.0005f)
                ExtraPass("rifle: charging handle", $"pulled {pulled * 1000f:F0} mm and let go: chambered, {gun.Status}");
            else ExtraFail("rifle: charging handle", $"pulled {pulled * 1000f:F1} mm, {gun.Status}");

            // 6. The Marshal weapons check's step 6 (the handover) fits the long gun actually slung: after its one shot the
            //    AR-15 is reloaded ("Reload it." / "B: reload", Y in the left hand), never pumped. (Brett 0.1.57: Ortiz says it in
            //    person now — the vo.deputy.handover.* take, not the radio's.)
            var right = MarshalBriefing.LongGunAfterShot(gun, leftHand: false);
            var left = MarshalBriefing.LongGunAfterShot(gun, leftHand: true);
            if (right.key == "vo.deputy.handover.reloadit" && right.label == MarshalBriefing.WReloadB && left.label == MarshalBriefing.WReloadY
                && VoiceLines.Text(right.key) != null)
                ExtraPass("rifle: weapons check line", $"\"{VoiceLines.Text(right.key)}\" — \"{right.label}\" / \"{left.label}\"");
            else ExtraFail("rifle: weapons check line", $"{right.key} \"{right.label}\" / \"{left.label}\"");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            if (gun != null) UnityEngine.Object.Destroy(gun.gameObject);
        }

        // 7. The pause menu's swap: the rifle replaces the shotgun on the back sling at the next room clear, and back again.
        //    Slung, nothing of the AR sticks forward of where the shotgun's parts reach below the chest (its grip and magazine
        //    used to point into the torso: drawn over passthrough, they showed over the belly when you looked down at the hip).
        string saved = PlayerPrefs.GetString("his.loadout.longgun", "");
        // A standing player for the looks down (the batch camera sits on the floor: the body hangs its belt from the eye height).
        var camWas = cam.transform.localPosition;
        cam.transform.localPosition = new Vector3(camWas.x, 1.65f, camWas.z);
        try
        {
            var body = UnityEngine.Object.FindAnyObjectByType<PlayerBody>();
            lo.Pick(slot, "r870");
            lo.ApplyPending();
            await Wait(0.2f);
            var sg = lo.SpawnedIn(slot);
            float shotgunZ = sg != null && body != null ? SlungForwardMost(sg, body.Chest, BelowChest) : float.NaN;
            SlingShots("shotgun", cam, body);
            lo.Pick(slot, "ar15");
            lo.ApplyPending();
            await Wait(0.2f);
            var w = lo.SpawnedIn(slot);
            SlingShots("ar15", cam, body);
            bool slung = w != null && w.name == "Weapon_AR15" && body != null && w.Grabbable.CurrentHolster == body.BackSlot;
            if (slung) ExtraPass("rifle: loadout swap", $"{w.name} slung in {w.Grabbable.CurrentHolster.name}");
            else ExtraFail("rifle: loadout swap", $"spawned {(w != null ? w.name : "none")} in {(w != null && w.Grabbable.CurrentHolster != null ? w.Grabbable.CurrentHolster.name : "no holster")}");
            float arZ = slung ? SlungForwardMost(w, body.Chest, BelowChest) : float.NaN;
            var mag = slung ? w.transform.Find("Model_AR15/Parts/Magazine") ?? FindDeep(w.transform, "Magazine") : null;
            float magZ = mag != null ? SlungForwardMost(mag, body.Chest, 0f) : float.NaN;
            if (slung && !float.IsNaN(shotgunZ) && arZ <= shotgunZ + 0.01f && magZ < -0.03f)
                ExtraPass("rifle: slung clear of the belly", $"forward-most below the chest: AR {arZ * 100f:F1} cm, shotgun {shotgunZ * 100f:F1} cm (chest frame, + = ahead of the neck line); the magazine's front {magZ * 100f:F1} cm (behind the back)");
            else ExtraFail("rifle: slung clear of the belly", $"forward-most below the chest: AR {arZ * 100f:F1} cm vs the shotgun's {shotgunZ * 100f:F1} cm; the magazine's front {magZ * 100f:F1} cm (want < -3)");
        }
        finally
        {
            lo.Pick(slot, "r870");
            lo.ApplyPending();
            if (saved == "") PlayerPrefs.DeleteKey("his.loadout.longgun"); else PlayerPrefs.SetString("his.loadout.longgun", saved);
            PlayerPrefs.Save();
            if (s_looksCam != null) UnityEngine.Object.Destroy(s_looksCam.gameObject);
            cam.transform.localPosition = camWas;
        }
    }

    /// <summary>
    /// With a GPU (GRAPHICS=1 only; nothing otherwise): what the eye sees of the slung long gun, as Logs/poses/sling_*.png —
    /// looking down at the right hip (the draw) and pitched 60° down, the eye's field of view. Weapons draw over passthrough,
    /// so any part in view shows; the background is the flat "passthrough" grey (ScenarioTests.MarshalLooks.LooksRender).
    /// </summary>
    static void SlingShots(string which, Camera cam, PlayerBody body)
    {
        if (!HIS.Security.SecurityCamera.CanRender || cam == null || body == null) return;
        // The eyes on the body's own neck model (PlayerBody.NeckToEye: 7.5 cm above and 8 cm in front of the neck pivot), not
        // the rig camera (the batch rig's head isn't where the body was last placed).
        var eye = body.Chest.TransformPoint(new Vector3(0f, 0.075f, 0.0805f));
        var fwd = RifleFlat(body.Chest.forward);
        var hip = body.RightHip.transform.position;
        int weapons = 1 << QuestProjectSetup.WeaponLayer;                  // (only what draws over passthrough: the guns)
        LooksRender(eye, Quaternion.LookRotation(hip - eye, fwd), 90f, 800, 800, weapons, $"sling_{which}_hip.png");
        var pitched = Quaternion.AngleAxis(60f, Vector3.Cross(Vector3.up, fwd)) * fwd;
        LooksRender(eye, Quaternion.LookRotation(pitched, Vector3.up), 90f, 800, 800, weapons, $"sling_{which}_down60.png");
        Debug.Log($"[HIS] Rifle: rendered Logs/poses/sling_{which}_hip.png and sling_{which}_down60.png (eye {eye:F2}, hip {hip:F2})");
    }

    static Vector3 RifleFlat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

    /// <summary>Below the shoulder blades in the chest frame (m below the neck pivot): what a look down at the hip can take in.</summary>
    const float BelowChest = -0.25f;

    /// <summary>
    /// The forward-most point (chest-frame z, m; + = ahead of the neck line) of every drawn mesh under <paramref name="root"/>
    /// lower than <paramref name="below"/> (chest-frame y) — from the vertices (the Editor reads any mesh), else the mesh bounds.
    /// </summary>
    static float SlungForwardMost(Component root, Transform chest, float below)
    {
        float best = float.NegativeInfinity;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null || !mf.TryGetComponent<MeshRenderer>(out var mr) || !mr.enabled) continue;
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0)
            {
                var b = mesh.bounds; verts = new Vector3[8];
                for (int i = 0; i < 8; i++) verts[i] = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            }
            var m = chest.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            foreach (var v in verts)
            {
                var p = m.MultiplyPoint3x4(v);
                if (p.y < below && p.z > best) best = p.z;
            }
        }
        return best;
    }

    static Transform FindDeep(Transform t, string name)
    {
        foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (c.name == name) return c;
        return null;
    }
}
