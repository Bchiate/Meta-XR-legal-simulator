using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using BodyPart = HIS.Weapons.BodyPart;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Play Mode test shots (HIS > Debug > Shoot Intruder): fires real rays from the player's head at the live intruder's bones
/// and routes them through the same Hitbox → WoundSystem → Intruder path as the guns. All pellets of a "shotgun" item land
/// in the same frame, like the real Firearm. Used to check reactions, limp/crawl, deaths and gore in the simulator.
/// </summary>
public static class IntruderDebugMenu
{
    const string Root = "HIS/Debug/Shoot Intruder/";

    [MenuItem(Root + "Torso (pistol)")] static void Torso() => Shoot(1, 34f, HumanBodyBones.Chest);
    [MenuItem(Root + "Gut (pistol)")] static void Gut() => Shoot(1, 34f, HumanBodyBones.Spine);
    [MenuItem(Root + "Leg (pistol)")] static void Leg() => Shoot(1, 34f, HumanBodyBones.LeftUpperLeg);
    [MenuItem(Root + "Head (pistol)")] static void Head() => Shoot(1, 34f, HumanBodyBones.Head);
    [MenuItem(Root + "Torso (shotgun)")] static void TorsoBlast() => Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine);
    [MenuItem(Root + "Legs (shotgun)")] static void LegsBlast() => Shoot(9, 13f, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg);
    [MenuItem(Root + "Arm (pistol, weak)")] static void Arm() => Shoot(1, 12f, HumanBodyBones.RightLowerArm);

    [MenuItem(Root + "Torso (pistol)", true)]
    [MenuItem(Root + "Gut (pistol)", true)]
    [MenuItem(Root + "Leg (pistol)", true)]
    [MenuItem(Root + "Head (pistol)", true)]
    [MenuItem(Root + "Torso (shotgun)", true)]
    [MenuItem(Root + "Legs (shotgun)", true)]
    [MenuItem(Root + "Arm (pistol, weak)", true)]
    static bool CanShoot() => EditorApplication.isPlaying;

    [MenuItem("HIS/Debug/God Mode")]
    static void ToggleGod()
    {
        HIS.Core.PlayerState.GodMode = !HIS.Core.PlayerState.GodMode;
        Debug.Log($"[HIS] God mode {(HIS.Core.PlayerState.GodMode ? "ON" : "OFF")}");
    }
    [MenuItem("HIS/Debug/God Mode", true)]
    static bool ToggleGodValidate() { Menu.SetChecked("HIS/Debug/God Mode", HIS.Core.PlayerState.GodMode); return true; }

    [MenuItem("HIS/Debug/Dump Intruders")]
    static void Dump()
    {
        foreach (var i in Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude))
        {
            var anim = i.GetComponentInChildren<Animator>();
            string clip = anim == null ? "no animator" : !anim.enabled ? "ASLEEP" :
                anim.GetCurrentAnimatorClipInfoCount(0) > 0 ? anim.GetCurrentAnimatorClipInfo(0)[0].clip.name + $" t={anim.GetCurrentAnimatorStateInfo(0).normalizedTime:F2}" : "?";
            var agent = i.GetComponent<UnityEngine.AI.NavMeshAgent>();
            string ag = agent == null || !agent.enabled ? "agent off" : $"agent stopped={agent.isStopped} v={agent.velocity.magnitude:F2} speed={agent.speed:F2} onMesh={agent.isOnNavMesh}";
            var hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : null;
            Debug.Log($"[HIS] Dump {i.name}: {i.Summary} root {i.transform.position:F2} yaw {i.transform.eulerAngles.y:F0} hips {(hips != null ? hips.position.ToString("F2") : "-")} | {clip} | {ag}");
        }
        Debug.Log($"[HIS] Dump blood: {HIS.Intruder.BloodDecals.DebugSummary} | corpses {HIS.Intruder.CorpseRegistry.Count}");
    }

    // ───────────── scripted feel test (HIS > Debug > Run Feel Test): one click, read the [HIS] TEST lines in the log ─────────────
    static bool _testing;

    [MenuItem("HIS/Debug/Run Feel Test")]
    static async void RunFeelTest()
    {
        if (!EditorApplication.isPlaying || _testing) { Debug.LogWarning("[HIS] TEST needs Play Mode (and not already running)"); return; }
        _testing = true;
        try
        {
            HIS.Core.PlayerState.GodMode = true;
            Debug.Log("[HIS] TEST 1: limp → legs out → crawl → kill on the floor");
            if (!await WaitFor(() => Live(IntruderT.State.Chasing), 60)) return;
            Shoot(1, 34f, HumanBodyBones.LeftUpperLeg); await Wait(1.2f); Dump("T1 limping");
            Shoot(1, 34f, HumanBodyBones.RightUpperLeg);
            for (int k = 0; k < 7; k++) { await Wait(0.3f); Dump($"T1 legs-out +{0.3f * (k + 1):F1}s"); }
            await Wait(1.0f); Dump("T1 crawling");
            Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine); await Wait(2.5f); Dump("T1 crawl corpse");

            Debug.Log("[HIS] TEST 2: one pistol hit = one planted reaction, then chase resumes; torso blast kill + clearance");
            if (!await WaitFor(() => Live(IntruderT.State.Chasing), 60)) return;
            Shoot(1, 34f, HumanBodyBones.Chest);
            foreach (var t in new[] { 0.1f, 0.6f, 0.6f, 0.6f }) { await Wait(t); Dump("T2 reaction"); }
            Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine); await Wait(4f); Dump("T2 corpse");

            Debug.Log("[HIS] TEST 3: kill right at the door (open leaf beside him) → third body retires the first");
            if (!await WaitFor(() => Live(IntruderT.State.Entering), 60)) return;
            await Wait(0.8f);
            Shoot(1, 34f, HumanBodyBones.Head); await Wait(4f); Dump("T3 door corpse");

            Debug.Log("[HIS] TEST 4: legs out then killed mid-stumble → must finish on the floor, not frozen mid-air");
            if (!await WaitFor(() => Live(IntruderT.State.Chasing), 60)) return;
            Shoot(4, 34f, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg); await Wait(0.3f);
            Shoot(1, 34f, HumanBodyBones.Head);
            for (int k = 0; k < 6; k++) { await Wait(0.5f); Dump($"T4 +{0.5f * (k + 1):F1}s"); }
            Debug.Log("[HIS] TEST done");
        }
        finally { _testing = false; }
    }

    [MenuItem("HIS/Debug/Run Stumble Test")]
    static async void RunStumbleTest()
    {
        if (!EditorApplication.isPlaying || _testing) return;
        _testing = true;
        try
        {
            HIS.Core.PlayerState.GodMode = true;
            Debug.Log("[HIS] TEST 5: legs shot out with open floor ahead and the player far away → stumble-and-fall into the crawl");
            if (!await WaitFor(() => Live(IntruderT.State.Chasing), 60)) return;
            await Wait(1.0f); // let him turn toward the player first
            Shoot(9, 13f, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg);
            for (int k = 0; k < 10; k++) { await Wait(0.25f); Dump($"T5 +{0.25f * (k + 1):F2}s"); }
            Debug.Log("[HIS] TEST done");
        }
        finally { _testing = false; }
    }

    static IntruderT Live(IntruderT.State state)
    {
        foreach (var i in Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude))
            if (i.Current == state) return i;
        return null;
    }

    static async System.Threading.Tasks.Task Wait(float seconds) => await System.Threading.Tasks.Task.Delay(Mathf.RoundToInt(seconds * 1000));

    static async System.Threading.Tasks.Task<bool> WaitFor(System.Func<Object> cond, float timeout)
    {
        float left = timeout;
        while (left > 0f)
        {
            if (!EditorApplication.isPlaying) { Debug.LogWarning("[HIS] TEST aborted: Play Mode ended"); return false; }
            if (cond() != null) return true;
            await Wait(0.1f); left -= 0.1f;
        }
        Debug.LogWarning("[HIS] TEST timeout");
        return false;
    }

    static void Dump(string label)
    {
        if (!EditorApplication.isPlaying) return;
        foreach (var i in Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude))
        {
            var ia = i.GetComponentInChildren<HIS.Intruder.IntruderAnimator>();
            var anim = i.GetComponentInChildren<Animator>();
            string clip = anim == null ? "-" : !anim.enabled ? "ASLEEP" :
                (anim.GetCurrentAnimatorClipInfoCount(0) > 0 ? anim.GetCurrentAnimatorClipInfo(0)[0].clip.name : "?") +
                (anim.IsInTransition(0) && anim.GetNextAnimatorClipInfoCount(0) > 0 ? "→" + anim.GetNextAnimatorClipInfo(0)[0].clip.name : "") +
                $" t={anim.GetCurrentAnimatorStateInfo(0).normalizedTime:F2}" + (ia != null && ia.OnFloor ? " onFloor" : "");
            var agent = i.GetComponent<UnityEngine.AI.NavMeshAgent>();
            string ag = agent == null || !agent.enabled ? "agent off" : $"stopped={(agent.isOnNavMesh ? agent.isStopped.ToString() : "offmesh")} v={agent.velocity.magnitude:F2} spd={agent.speed:F2}";
            var hips = anim != null ? anim.GetBoneTransform(HumanBodyBones.Hips) : null;
            Debug.Log($"[HIS] TEST {label}: {i.Summary} root {i.transform.position:F2} hipsY {(hips != null ? hips.position.y : 0):F2} | {clip} | {ag}");
        }
        Debug.Log($"[HIS] TEST {label}: blood {HIS.Intruder.BloodDecals.DebugSummary} | corpses {HIS.Intruder.CorpseRegistry.Count}");
    }

    static IntruderT Target()
    {
        foreach (var i in Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude))
            if (i.Current is not IntruderT.State.Dead and not IntruderT.State.Gone) return i;
        return null;
    }

    internal static void Shoot(int pellets, float damage, params HumanBodyBones[] bones)
    {
        var target = Target();
        var cam = Camera.main;
        if (target == null || cam == null) { Debug.LogWarning("[HIS] Debug shot: no live intruder / camera"); return; }
        var animator = target.GetComponentInChildren<Animator>();
        int landed = 0;
        for (int p = 0; p < pellets; p++)
        {
            var bone = animator != null ? animator.GetBoneTransform(bones[p % bones.Length]) : target.transform;
            var col = bone != null ? bone.GetComponent<Collider>() : null;
            var aim = col != null ? col.bounds.center : target.transform.position + Vector3.up * 1.2f;
            aim += Random.insideUnitSphere * 0.03f;
            var origin = cam.transform.position;
            var dir = (aim - origin).normalized;
            var info = new DamageInfo { Amount = damage, Type = DamageType.Bullet, Direction = dir, Penetration = 1f, Source = cam.gameObject };
            // A real ray first (so the hit point/normal are right); if furniture or a wall is in the way, hit the bone directly.
            if (Physics.Raycast(origin, dir, out var hit, 20f, ~0, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<IDamageable>() != null)
            {
                info.Point = hit.point; info.Normal = hit.normal;
                hit.collider.GetComponentInParent<IDamageable>().ApplyDamage(info);
            }
            else if (col != null && col.TryGetComponent<IDamageable>(out var direct))
            {
                info.Point = col.ClosestPoint(origin); info.Normal = -dir;
                direct.ApplyDamage(info);
            }
            else continue;
            landed++;
        }
        Debug.Log($"[HIS] Debug shot: {landed}/{pellets} × {damage} at {string.Join("+", bones)} → {target.Summary}");
    }
}
