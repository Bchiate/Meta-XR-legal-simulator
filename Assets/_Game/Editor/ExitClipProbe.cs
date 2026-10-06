using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Measures the car exit (Brett 2026-10-05; Mixamo "Exiting Car" → ExitCar / ExitCarMirror, MixamoImportSettings.Sided) on the
/// real Alex rig with its root motion applied, for the constants IntruderCarExit runs on (IntruderClips' TODO-MEASURE block,
/// read off the raw FBX when no Unity was at hand): when his hand is on the door and pushes it wide, when each foot leaves the
/// take's car floor and comes down on the road outside, when he's standing, when his hips are past the door's line by a body's
/// width (the hand-off), whether (and when) the take shuts the door behind him, the root's travel and turn up to then, his
/// hips over the root sitting (vs SittingIdle and Driving: the drop and the start offset), how far a planted foot slides (≈ 0
/// with the root motion right), and that the mirror is the mirror.
///   scripts/unity-batch.sh ExitClipProbe.Run  →  Logs/exit-clip-probe.txt (a trace, the events, and a block to paste into
///   IntruderClips). Also HIS > Debug > Probe Car Exit Clip.
/// Frame: the root at the start (x = his right: −x out of the driver's door; z = his forward), metres / seconds at 1×.
/// </summary>
public static class ExitClipProbe
{
    const string PrefabPath = "Assets/_Game/Prefabs/Intruder_Alex.prefab";
    const string AnimDir = "Assets/ThirdParty/Mixamo/Animations/";
    const float Step = 1f / 60f;

    struct Sample
    {
        public float T, Yaw;
        public Vector3 Root, Hips, Head, LToe, RToe, LFoot, RFoot, LHand, RHand;
    }

    [MenuItem("HIS/Debug/Probe Car Exit Clip")]
    public static void Run()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        var exitL = Load("ExitingCar", "ExitCar");
        var exitR = Load("ExitingCar", "ExitCarMirror");
        if (prefab == null || exitL == null || exitR == null)
        {
            Debug.LogError($"[HIS] ExitClipProbe: missing {(prefab == null ? "the Alex prefab " : "")}{(exitL == null ? "ExitCar " : "")}{(exitR == null ? "ExitCarMirror" : "")}" +
                           " — import Assets/ThirdParty/Mixamo/Animations/ExitingCar.fbx with MixamoImportSettings' Sided rules first");
            return;
        }
        var go = Object.Instantiate(prefab);
        go.hideFlags = HideFlags.HideAndDontSave;
        var anim = go.GetComponentInChildren<Animator>();
        // Its OnAnimatorMove would take the root motion (script-handled): the probe wants the Animator to apply it.
        foreach (var ia in go.GetComponentsInChildren<HIS.Intruder.IntruderAnimator>(true)) Object.DestroyImmediate(ia);
        var root = anim.transform;
        var graph = PlayableGraph.Create("ExitClipProbe");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        var output = AnimationPlayableOutput.Create(graph, "out", anim);
        var sb = new StringBuilder($"# Car exit clip probe ({System.DateTime.Now:yyyy-MM-dd HH:mm}) — root frame at the start: x = his right, z = his forward; m, s at 1×\n");
        try
        {
            anim.applyRootMotion = true;
            var sit = Pose0(graph, output, anim, root, Load("SittingIdle", "SittingIdle"));
            var drive = Pose0(graph, output, anim, root, Load("Driving", "Driving"));
            var left = Trace(graph, output, anim, root, exitL);
            var right = Trace(graph, output, anim, root, exitR);
            sb.AppendLine($"\n## Clips\n- ExitCar {exitL.length:F2} s @{exitL.frameRate:F0} fps, human {exitL.humanMotion}, loop {exitL.isLooping}; ExitCarMirror {exitR.length:F2} s");
            sb.AppendLine($"- seated hips over the root at t 0: ExitCar {Fmt(left[0].Hips)}, SittingIdle {Fmt(sit)}, Driving {Fmt(drive)}");
            // Mixamo calls it the driver's side (out to his left, −x). If the import says otherwise, the events are read off the
            // mirrored trace and the raw travel pasted below (its x > 0) flips which state plays which side (IntruderAnimator.ExitCar).
            bool goesLeft = left[^1].Root.x <= 0f;
            var outL = goesLeft ? left : Mirror(left);
            var ev = Events(outL, sit, out string notes);
            sb.AppendLine($"\n## Events (ExitCar: out to his {(goesLeft ? "left, as Mixamo says" : "RIGHT — the events below are its mirror's")})");
            sb.Append(notes);
            var evR = Events(goesLeft ? Mirror(right) : right, sit, out _);
            sb.AppendLine($"\n## Mirror check: ExitCarMirror's travel at its clear {Fmt(At(right, evR.Clear).Root)} vs ExitCar's {Fmt(At(left, ev.Clear).Root)} " +
                          $"(want x negated), turn {At(right, evR.Clear).Yaw:F0}° vs {At(left, ev.Clear).Yaw:F0}°");
            sb.AppendLine("\n## Trace (every 0.1 s): t | root x,z yaw | hips x,y,z | head y | L toe x,y,z | R toe x,y,z | L hand x,y,z | R hand x,y,z");
            float next = 0f;
            foreach (var s in left)
            {
                if (s.T + 1e-4f < next) continue;
                next += 0.1f;
                sb.AppendLine($"  {s.T:F2} | {s.Root.x:F2},{s.Root.z:F2} {s.Yaw:F0} | {Fmt(s.Hips)} | {s.Head.y:F2} | {Fmt(s.LToe)} | {Fmt(s.RToe)} | {Fmt(s.LHand)} | {Fmt(s.RHand)}");
            }
            // The block for IntruderClips (the TODO-MEASURE constants).
            var c = At(left, ev.Clear);
            var ahead = new Vector2(sit.x - left[0].Hips.x, sit.z - left[0].Hips.z);
            sb.AppendLine("\n## Paste into IntruderClips (over the TODO-MEASURE block's values)");
            sb.AppendLine($"        public const float ExitLength = {exitL.length:F2}f;");
            sb.AppendLine($"        public const float ExitDoorTouch = {ev.DoorTouch:F2}f, ExitDoorOpen = {ev.DoorOpen:F2}f;");
            sb.AppendLine($"        public const float ExitFootFrom = {ev.FootFrom:F2}f, ExitFootOut = {ev.FootOut:F2}f;");
            sb.AppendLine($"        public const float ExitStand = {ev.Stand:F2}f, ExitGunAt = {Mathf.Min(ev.Clear, ev.Stand + 0.1f):F2}f;");
            sb.AppendLine($"        public const float ExitClear = {ev.Clear:F2}f;");
            sb.AppendLine($"        public const float ExitShutsDoor = {(ev.Shuts >= 0f ? ev.Shuts : exitL.length):F2}f;{(ev.Shuts >= 0f ? "" : "   // (it never does)")}");
            sb.AppendLine($"        public const float ExitSeatHips = {left[0].Hips.y:F2}f, SitHips = {sit.y:F2}f, DriveHips = {drive.y:F2}f;");
            sb.AppendLine($"        public static readonly Vector2 ExitRootAhead = new({ahead.x:F2}f, {ahead.y:F2}f);");
            sb.AppendLine($"        public static readonly Vector2 ExitTravel = new({c.Root.x:F2}f, {c.Root.z:F2}f);");
            sb.AppendLine($"        public const float ExitTurn = {c.Yaw:F0}f;");
        }
        finally
        {
            graph.Destroy();
            Object.DestroyImmediate(go);
        }
        System.IO.Directory.CreateDirectory("Logs");
        System.IO.File.WriteAllText("Logs/exit-clip-probe.txt", sb.ToString());
        Debug.Log("[HIS] ExitClipProbe written to Logs/exit-clip-probe.txt\n" + sb);
    }

    struct Ev { public float DoorTouch, DoorOpen, FootFrom, FootOut, SecondFoot, Stand, Clear, Shuts, Plane; }

    /// <summary>The take's moments, out to his left (a mirrored trace: x negated first).</summary>
    static Ev Events(List<Sample> s, Vector3 sit, out string notes)
    {
        var e = new Ev { Shuts = -1f };
        var n = new StringBuilder();
        var h0 = s[0].Hips;
        float len = s[^1].T;
        float floorL = s[0].LToe.y, groundL = s.Min(q => q.LToe.y), groundR = s.Min(q => q.RToe.y);
        float First(System.Func<Sample, bool> f, float from = 0f, float none = -1f) { foreach (var q in s) if (q.T >= from && f(q)) return q.T; return none; }
        e.DoorTouch = First(q => q.LHand.x < h0.x - 0.35f && q.LHand.y > 0.75f && q.LHand.y < 1.25f, 0f, 0.5f);
        float most = float.MaxValue;
        foreach (var q in s) if (q.T >= e.DoorTouch && q.T <= Mathf.Min(2.2f, len) && q.LHand.x < most) { most = q.LHand.x; e.DoorOpen = q.T; }
        float out1 = First(q => q.LToe.x < h0.x - 0.40f, e.DoorTouch, 1.6f);
        e.FootFrom = Mathf.Max(e.DoorTouch + 0.2f, out1 - 0.1f);
        e.FootOut = First(q => q.LToe.y < groundL + 0.03f && q.LToe.x < h0.x - 0.4f, e.FootFrom, e.FootFrom + 0.75f);
        e.SecondFoot = First(q => q.RToe.y < groundR + 0.03f && q.RToe.x < h0.x - 0.5f, e.FootOut, e.FootOut + 0.6f);
        float headTop = s.Max(q => q.Head.y);
        e.Stand = First(q => q.Head.y >= headTop - 0.04f, e.FootOut, e.FootOut + 0.4f);
        // The door's line, 0.72 m out from his seat (the take's car; the hand-off is timed off it). Does he shut it? After he's up
        // he turns back: his left hand comes back toward the car from where it reached farthest out, by more than a forearm's
        // length (2026-10-05: compared with his hips at standing, this missed the take's push-to at ~4.1 s, which ends 0.6 m
        // past them — he has walked on by then).
        e.Plane = h0.x - 0.72f;
        e.Clear = First(q => q.Hips.x < e.Plane - 0.2f, e.Stand, e.Stand + 0.3f);
        float far = float.MaxValue, farAt = -1f;
        foreach (var q in s) if (q.T >= e.Stand && q.T <= e.Stand + 1.2f && q.LHand.x < far) { far = q.LHand.x; farAt = q.T; }
        float back = float.MinValue, backAt = -1f;
        foreach (var q in s) if (farAt >= 0f && q.T > farAt && q.LHand.x > back) { back = q.LHand.x; backAt = q.T; }
        bool shuts = farAt >= 0f && back > far + 0.3f;
        if (shuts) e.Shuts = First(q => q.LHand.x > e.Plane - 0.15f, farAt, backAt);
        // Planted feet: how far each slides while it's down (with the root motion applied it should stay put).
        float Slide(System.Func<Sample, Vector3> foot, float from, float to)
        {
            Vector3 a = default; bool have = false; float m = 0f;
            foreach (var q in s) { if (q.T < from || q.T > to) continue; var p = foot(q); p.y = 0f; if (!have) { a = p; have = true; } m = Mathf.Max(m, (p - a).magnitude); }
            return m;
        }
        float slideL = Slide(q => q.LToe, e.FootOut, e.SecondFoot), slideR = Slide(q => q.RToe, e.SecondFoot, e.Clear);
        var c = At(s, e.Clear);
        n.AppendLine($"- the take's car floor (his left toe sitting) {floorL:F2} m; the road (lowest toes) L {groundL:F2} / R {groundR:F2}");
        n.AppendLine($"- hand on the door {e.DoorTouch:F2} s (left hand {Fmt(At(s, e.DoorTouch).LHand)}), pushed widest {e.DoorOpen:F2} s (x {most:F2})");
        n.AppendLine($"- left foot out over the sill from {e.FootFrom:F2} s, down on the road {e.FootOut:F2} s at {Fmt(At(s, e.FootOut).LToe)}; right foot down {e.SecondFoot:F2} s at {Fmt(At(s, e.SecondFoot).RToe)}");
        n.AppendLine($"- standing (head within 4 cm of its top {headTop:F2}) {e.Stand:F2} s");
        n.AppendLine($"- the door's line x {e.Plane:F2} (0.72 m out from his seat); hips past it by 0.2 m {e.Clear:F2} s");
        n.AppendLine($"- at the hand-off ({e.Clear:F2} s): root travel {c.Root.x:F2},{c.Root.z:F2}, turn {c.Yaw:F0}°, hips over the root {Fmt(c.Hips - c.Root)}");
        n.AppendLine($"- hips over the root sitting: ExitCar {Fmt(s[0].Hips - s[0].Root)}, SittingIdle {Fmt(sit)} → drop {s[0].Hips.y - sit.y:F2} m, start offset (ExitRootAhead) {sit.x - s[0].Hips.x:F2},{sit.z - s[0].Hips.z:F2}");
        n.AppendLine($"- planted feet slide: left {slideL * 100f:F1} cm ({e.FootOut:F2}–{e.SecondFoot:F2} s), right {slideR * 100f:F1} cm ({e.SecondFoot:F2}–{e.Clear:F2} s) — want ≲ 3 cm");
        n.AppendLine($"- the take shuts the door: {(shuts ? $"yes, his left hand back from x {far:F2} ({farAt:F2} s) to the door's line {e.Shuts:F2} s, {back:F2} at {backAt:F2} s (skipped: the hand-off is at {e.Clear:F2} s)" : "no")}");
        notes = n.ToString();
        return e;
    }

    /// <summary>The clip played from 0 with root motion applied, every <see cref="Step"/>: the root and the bones in the start's frame.</summary>
    static List<Sample> Trace(PlayableGraph graph, AnimationPlayableOutput output, Animator a, Transform root, AnimationClip clip)
    {
        var list = new List<Sample>();
        root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        var p = Bind(graph, output, clip);
        p.SetTime(0); graph.Evaluate(0f);
        root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        graph.Evaluate(0f);
        for (float t = 0f; t <= clip.length + 1e-4f; t += Step)
        {
            if (t > 0f) graph.Evaluate(Step);
            list.Add(new Sample
            {
                T = t, Root = root.position, Yaw = Mathf.DeltaAngle(0f, root.eulerAngles.y),
                Hips = Bone(a, HumanBodyBones.Hips), Head = Bone(a, HumanBodyBones.Head),
                LToe = Bone(a, HumanBodyBones.LeftToes), RToe = Bone(a, HumanBodyBones.RightToes),
                LFoot = Bone(a, HumanBodyBones.LeftFoot), RFoot = Bone(a, HumanBodyBones.RightFoot),
                LHand = Bone(a, HumanBodyBones.LeftHand), RHand = Bone(a, HumanBodyBones.RightHand),
            });
        }
        return list;
    }

    /// <summary>A seated clip's hips over its root at t 0 (in place: the root stays put).</summary>
    static Vector3 Pose0(PlayableGraph graph, AnimationPlayableOutput output, Animator a, Transform root, AnimationClip clip)
    {
        if (clip == null) return new Vector3(0f, -1f, 0f);
        root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        var p = Bind(graph, output, clip);
        p.SetTime(0); graph.Evaluate(0f);
        root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        graph.Evaluate(0f);
        return root.InverseTransformPoint(Bone(a, HumanBodyBones.Hips));
    }

    /// <summary>A mirrored trace read as out to his left (x negated, turns negated): the same event rules apply.</summary>
    static List<Sample> Mirror(List<Sample> s)
    {
        static Vector3 M(Vector3 v) => new(-v.x, v.y, v.z);
        return s.Select(q => new Sample
        {
            T = q.T, Yaw = -q.Yaw, Root = M(q.Root), Hips = M(q.Hips), Head = M(q.Head),
            LToe = M(q.RToe), RToe = M(q.LToe), LFoot = M(q.RFoot), RFoot = M(q.LFoot), LHand = M(q.RHand), RHand = M(q.LHand),
        }).ToList();
    }

    static Sample At(List<Sample> s, float t)
    {
        foreach (var q in s) if (q.T >= t - 1e-4f) return q;
        return s[^1];
    }

    static AnimationClipPlayable Bind(PlayableGraph graph, AnimationPlayableOutput output, AnimationClip clip)
    {
        var p = AnimationClipPlayable.Create(graph, clip);
        p.SetApplyFootIK(false);
        output.SetSourcePlayable(p);
        return p;
    }

    static Vector3 Bone(Animator a, HumanBodyBones b)
    {
        var t = a.GetBoneTransform(b);
        return t != null ? t.position : Vector3.zero;
    }

    static AnimationClip Load(string fbx, string clip) =>
        AssetDatabase.LoadAllAssetsAtPath(AnimDir + fbx + ".fbx").OfType<AnimationClip>().FirstOrDefault(c => c.name == clip);

    static string Fmt(Vector3 v) => $"{v.x:F2},{v.y:F2},{v.z:F2}";
}
