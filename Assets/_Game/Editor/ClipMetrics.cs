using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Measures the intruder's Mixamo clips on the real rig (HIS > Debug > Measure Intruder Clips → Logs/clip-metrics.txt):
/// natural ground speed of in-place locomotion (planted-contact slip), how far and which way each death falls, when it settles,
/// and the contact frame of attacks. The runtime constants in Intruder/IntruderAnimator/IntruderBuilder come from this file.
/// Root frame: x = his right, z = his forward, y = up, origin = the root at the floor.
/// </summary>
public static class ClipMetrics
{
    const string PrefabPath = "Assets/_Game/Prefabs/Intruder_Alex.prefab";
    const string AnimDir = "Assets/ThirdParty/Mixamo/Animations/";
    const float Hz = 120f;

    static readonly string[] Locomotion = { "Walking", "Running", "InjuredWalking", "ZombieCrawl", "FightIdle", "KnifeIdle", "Idle",
                                            "DrunkIdle", "DrunkWalk", "DrunkWalkBack", "DrunkRun", "SneakForward", "CrouchedWalk", "RunLookBack",
                                            "WalkNormal", "BackAwayFear", "PistolWalkAimed", "PistolRun" };
    static readonly string[] Deaths = { "DeathFromTheFront", "DeathFromFrontHeadshot", "StandingDeathBackward01", "DeathFromTheBack", "StandingDeathForward01",
                                        "ProneDeath", "KneelDeath", "TripFall", "StumbleBackFall", "CrouchToProne", "KnockedDownStomach", "FallToKnees" };
    static readonly string[] OneShots = { "HeadHit", "StandingReactLargeGut", "HitToBody", "HitOnLegs", "StomachHit", "ShoveReaction", "StandingReactSmallFromHeadshot",
                                          "CrossPunch", "Hook", "JabCross", "Stabbing", "SlashAdvance", "BayonetStab", "MmaKick", "StandingMeleeAttackKickVer1",
                                          "DrunkStumble", "DrunkHiccup", "DrunkTurn", "DrunkTryDoor", "PointThumb", "Shrug", "CaughtReact", "LookOverShoulder",
                                          "GetUpFromBack", "GetUpFromStomach", "KneelToStand", "PutDown", "Terrified", "DuckCover" };

    static readonly HumanBodyBones[] Contacts = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
                                                  HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg };
    static readonly HumanBodyBones[] Extent = { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.Chest, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
                                                HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm };

    [MenuItem("HIS/Debug/Measure Intruder Clips")]
    public static void Measure()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { Debug.LogError("[HIS] ClipMetrics: prefab missing"); return; }
        var go = Object.Instantiate(prefab);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        var anim = go.GetComponentInChildren<Animator>();
        var root = go.transform;
        var sb = new StringBuilder($"# Intruder clip metrics ({System.DateTime.Now:yyyy-MM-dd HH:mm}) — root frame x=right z=forward, metres/seconds\n");
        var graph = PlayableGraph.Create("ClipMetrics");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        var output = AnimationPlayableOutput.Create(graph, "out", anim);
        try
        {
            sb.AppendLine("\n## Locomotion (in-place): slip = backward speed of the planted contact = natural ground speed");
            foreach (var n in Locomotion) Loco(n, graph, output, anim, root, sb);
            sb.AppendLine("\n## Deaths: final head/hips in root frame, fall = head xz, reach = max body extent along the fall, settle = hips stop moving");
            foreach (var n in Deaths) Death(n, graph, output, anim, root, sb);
            sb.AppendLine("\n## One-shots: hips drift (in-pose travel), peak reach of hands/feet (contact frame)");
            foreach (var n in OneShots) OneShot(n, graph, output, anim, root, sb);
        }
        finally
        {
            graph.Destroy();
            Object.DestroyImmediate(go);
        }
        System.IO.File.WriteAllText("Logs/clip-metrics.txt", sb.ToString());
        Debug.Log("[HIS] ClipMetrics written to Logs/clip-metrics.txt\n" + sb);
    }

    // Pose transitions and gestures whose trims, lock times and hand-offs are read off a trace (Logs/clip-trace.txt).
    static readonly string[] Traced = { "DrunkTryDoor", "DrunkStumble", "DrunkHiccup", "PointThumb", "Shrug", "TripFall", "StumbleBackFall",
                                        "GetUpFromBack", "GetUpFromStomach", "FallToKnees", "KneelIdle", "KneelHandsOnHead", "CrouchToProne",
                                        "KneelToStand", "KneelDeath", "ProneDeath", "CaughtReact", "Rummaging", "LookAroundNervous", "LookOverShoulder",
                                        "DrunkTurn", "DrunkWalkBack", "Terrified", "PutDown" };

    /// <summary>
    /// Every Mixamo clip's import facts (length, frame rate, loop, root travel) plus a 0.1 s trace of the <see cref="Traced"/>
    /// ones with root motion applied: root travel, hips height, head height, hand spacing and hand height vs the head
    /// (hands joined = small spacing; hands on head = hands near head height). → Logs/clip-trace.txt
    /// </summary>
    [MenuItem("HIS/Debug/Trace Intruder Clips")]
    public static void Trace()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) { Debug.LogError("[HIS] ClipMetrics: prefab missing"); return; }
        var go = Object.Instantiate(prefab);
        go.hideFlags = HideFlags.HideAndDontSave;
        var anim = go.GetComponentInChildren<Animator>();
        // Its OnAnimatorMove would swallow the root motion (script-handled); the trace wants the Animator to apply it.
        foreach (var ia in go.GetComponentsInChildren<HIS.Intruder.IntruderAnimator>(true)) Object.DestroyImmediate(ia);
        var root = anim.transform;
        var sb = new StringBuilder($"# Intruder clip traces ({System.DateTime.Now:yyyy-MM-dd HH:mm}) — world metres from the start root, x=right z=forward\n\n## All clips\n");
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { AnimDir.TrimEnd('/') }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => c.name == System.IO.Path.GetFileNameWithoutExtension(path));
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            var ca = imp != null && imp.clipAnimations.Length > 0 ? imp.clipAnimations[0] : null;
            if (clip == null) { sb.AppendLine($"- {System.IO.Path.GetFileNameWithoutExtension(path)}: NO CLIP"); continue; }
            var travel = clip.averageSpeed * clip.length;
            sb.AppendLine($"- {clip.name}: {clip.length:F2}s @{clip.frameRate:F0}fps human {clip.humanMotion} loop {clip.isLooping} | frames {(ca != null ? $"{ca.firstFrame:F0}-{ca.lastFrame:F0}" : "?")} bakeXZ {(ca != null && ca.lockRootPositionXZ)} | root travel ({travel.x:F2},{travel.z:F2})");
        }
        var graph = PlayableGraph.Create("ClipTrace");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        var output = AnimationPlayableOutput.Create(graph, "out", anim);
        try
        {
            anim.applyRootMotion = true;
            foreach (var n in Traced)
            {
                var clip = Load(n); if (clip == null) { sb.AppendLine($"\n### {n}: MISSING"); continue; }
                root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var p = Bind(graph, output, clip);
                p.SetTime(0); graph.Evaluate(0f);
                root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                sb.AppendLine($"\n### {n} ({clip.length:F2}s) t: root x,z | hips y | head y | hands apart | hands-head y | hips x,z");
                const float step = 1f / 60f;
                float next = 0f;
                for (float t = 0f; t <= clip.length + 1e-4f; t += step)
                {
                    if (t > 0f) graph.Evaluate(step);
                    if (t + 1e-4f < next) continue;
                    next += 0.1f;
                    var hips = anim.GetBoneTransform(HumanBodyBones.Hips).position;
                    var head = anim.GetBoneTransform(HumanBodyBones.Head).position;
                    var lh = anim.GetBoneTransform(HumanBodyBones.LeftHand).position;
                    var rh = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
                    sb.AppendLine($"  {t:F1}: {root.position.x:F2},{root.position.z:F2} | {hips.y:F2} | {head.y:F2} | {Vector3.Distance(lh, rh):F2} | {Mathf.Min(lh.y, rh.y) - head.y:F2} | {hips.x:F2},{hips.z:F2}");
                }
            }
        }
        finally
        {
            graph.Destroy();
            Object.DestroyImmediate(go);
        }
        System.IO.File.WriteAllText("Logs/clip-trace.txt", sb.ToString());
        Debug.Log("[HIS] ClipMetrics trace written to Logs/clip-trace.txt");
    }

    static AnimationClip Load(string name) =>
        AssetDatabase.LoadAllAssetsAtPath(AnimDir + name + ".fbx").OfType<AnimationClip>().FirstOrDefault(c => c.name == name);

    static AnimationClipPlayable Bind(PlayableGraph graph, AnimationPlayableOutput output, AnimationClip clip)
    {
        var p = AnimationClipPlayable.Create(graph, clip);
        p.SetApplyFootIK(false);
        output.SetSourcePlayable(p);
        return p;
    }

    static void Sample(PlayableGraph graph, AnimationClipPlayable p, float t) { p.SetTime(t); p.SetTime(t); graph.Evaluate(0f); }

    static Vector3 Local(Transform root, Animator a, HumanBodyBones b)
    {
        var t = a.GetBoneTransform(b);
        return t == null ? Vector3.zero : root.InverseTransformPoint(t.position);
    }

    static void Loco(string name, PlayableGraph graph, AnimationPlayableOutput output, Animator a, Transform root, StringBuilder sb)
    {
        var clip = Load(name); if (clip == null) { sb.AppendLine($"- {name}: MISSING"); return; }
        var p = Bind(graph, output, clip);
        int n = Mathf.Max(2, Mathf.CeilToInt(clip.length * Hz));
        float dt = clip.length / n;
        var prev = new Vector3[Contacts.Length];
        var slips = new System.Collections.Generic.List<float>();
        float minY = float.MaxValue;
        // Pass 1: lowest contact height over the clip.
        for (int i = 0; i <= n; i++)
        {
            Sample(graph, p, i * dt);
            foreach (var c in Contacts) { var v = Local(root, a, c); if (v != Vector3.zero) minY = Mathf.Min(minY, v.y); }
        }
        Sample(graph, p, 0); var hips0 = Local(root, a, HumanBodyBones.Hips);
        for (int k = 0; k < Contacts.Length; k++) prev[k] = Local(root, a, Contacts[k]);
        Vector3 hipsEnd = hips0;
        for (int i = 1; i <= n; i++)
        {
            Sample(graph, p, i * dt);
            int lowest = -1; float ly = float.MaxValue;
            var cur = new Vector3[Contacts.Length];
            for (int k = 0; k < Contacts.Length; k++) { cur[k] = Local(root, a, Contacts[k]); if (cur[k] != Vector3.zero && cur[k].y < ly) { ly = cur[k].y; lowest = k; } }
            if (lowest >= 0 && ly < minY + 0.04f)
            {
                var d = (cur[lowest] - prev[lowest]) / dt;
                slips.Add(-d.z);
            }
            prev = cur;
            if (i == n) hipsEnd = Local(root, a, HumanBodyBones.Hips);
        }
        slips.Sort();
        float median = slips.Count > 0 ? slips[slips.Count / 2] : 0f;
        // Per contact: backward speed while that bone is within 3 cm of its own lowest point (hands for crawls).
        var per = new StringBuilder();
        foreach (var c in Contacts)
        {
            float own = float.MaxValue;
            for (int i = 0; i <= n; i++) { Sample(graph, p, i * dt); var v = Local(root, a, c); if (v != Vector3.zero) own = Mathf.Min(own, v.y); }
            var list = new System.Collections.Generic.List<float>(); Vector3 pv = Vector3.zero;
            for (int i = 0; i <= n; i++)
            {
                Sample(graph, p, i * dt); var v = Local(root, a, c);
                if (i > 0 && v.y < own + 0.03f && pv.y < own + 0.03f) list.Add(-(v.z - pv.z) / dt);
                pv = v;
            }
            list.Sort();
            if (list.Count > 0) per.Append($" {c}:{list[list.Count / 2]:F2}@{own:F2}");
        }
        float drift = (hipsEnd - hips0).z;
        sb.AppendLine($"- {name}: length {clip.length:F2}s loop {clip.isLooping} | slip median {median:F2} m/s (n={slips.Count}) | hips drift z {drift:F2} m ({(clip.length > 0 ? drift / clip.length : 0):F2} m/s) | hips y {hips0.y:F2} | contact minY {minY:F2}\n    per-contact slip:{per}");
    }

    static void Death(string name, PlayableGraph graph, AnimationPlayableOutput output, Animator a, Transform root, StringBuilder sb)
    {
        var clip = Load(name); if (clip == null) { sb.AppendLine($"- {name}: MISSING"); return; }
        var p = Bind(graph, output, clip);
        int n = Mathf.Max(2, Mathf.CeilToInt(clip.length * Hz));
        float dt = clip.length / n;
        Sample(graph, p, clip.length);
        var headEnd = Local(root, a, HumanBodyBones.Head); var hipsEnd = Local(root, a, HumanBodyBones.Hips);
        var fall = new Vector3(headEnd.x, 0, headEnd.z);
        var dir = fall.sqrMagnitude > 1e-4f ? fall.normalized : Vector3.back;
        float reach = 0, side = 0, settle = clip.length;
        for (int i = 0; i <= n; i++)
        {
            Sample(graph, p, i * dt);
            foreach (var b in Extent)
            {
                var v = Local(root, a, b); v.y = 0;
                reach = Mathf.Max(reach, Vector3.Dot(v, dir));
                side = Mathf.Max(side, Mathf.Abs(Vector3.Dot(v, Vector3.Cross(Vector3.up, dir))));
            }
        }
        // Settle: last time the hips moved faster than 5 cm/s.
        Vector3 prevH = Vector3.zero;
        for (int i = 0; i <= n; i++)
        {
            Sample(graph, p, i * dt);
            var h = Local(root, a, HumanBodyBones.Hips);
            if (i > 0 && (h - prevH).magnitude / dt > 0.05f) settle = i * dt;
            prevH = h;
        }
        float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        sb.AppendLine($"- {name}: length {clip.length:F2}s settle {settle:F2}s | head end ({headEnd.x:F2},{headEnd.y:F2},{headEnd.z:F2}) hips end ({hipsEnd.x:F2},{hipsEnd.y:F2},{hipsEnd.z:F2}) | fall yaw {yaw:F0}° len {fall.magnitude:F2} reach {reach:F2} half-width {side:F2}");
    }

    static void OneShot(string name, PlayableGraph graph, AnimationPlayableOutput output, Animator a, Transform root, StringBuilder sb)
    {
        var clip = Load(name); if (clip == null) { sb.AppendLine($"- {name}: MISSING"); return; }
        var p = Bind(graph, output, clip);
        int n = Mathf.Max(2, Mathf.CeilToInt(clip.length * Hz));
        float dt = clip.length / n;
        Sample(graph, p, 0); var hips0 = Local(root, a, HumanBodyBones.Hips);
        float maxDrift = 0; Vector3 hipsEnd = hips0;
        float peak = float.MinValue, peakT = 0; string peakBone = "";
        var reachers = new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
        for (int i = 0; i <= n; i++)
        {
            float t = i * dt;
            Sample(graph, p, t);
            var h = Local(root, a, HumanBodyBones.Hips);
            var d = h - hips0; d.y = 0; maxDrift = Mathf.Max(maxDrift, d.magnitude);
            if (i == n) hipsEnd = h;
            foreach (var r in reachers)
            {
                var v = Local(root, a, r);
                if (v.z > peak) { peak = v.z; peakT = t; peakBone = r.ToString(); }
            }
        }
        var end = hipsEnd - hips0;
        if (maxDrift > 0.5f)
        {
            var tr = new StringBuilder("    hips trace (t: x,z,y):");
            for (float t = 0; t <= clip.length + 1e-4f; t += 0.1f) { Sample(graph, p, t); var h = Local(root, a, HumanBodyBones.Hips) - hips0; tr.Append($" {t:F1}:{h.x:F2},{h.z:F2},{h.y + hips0.y:F2}"); }
            sb.AppendLine(tr.ToString());
        }
        sb.AppendLine($"- {name}: length {clip.length:F2}s | hips drift max {maxDrift:F2} end ({end.x:F2},{end.z:F2}) | peak reach {peak:F2} m by {peakBone} at {peakT:F2}s ({(clip.length > 0 ? peakT / clip.length : 0):F2})");
    }
}
