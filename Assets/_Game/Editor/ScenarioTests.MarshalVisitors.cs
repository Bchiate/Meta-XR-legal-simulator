using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// A Marshal night's visitors after 0.1.56 (Brett: "the ortiz dude could be anybody walking up if youre a us marshall he also
/// just stands there for like a minute beofre he leaves when he levaes he like goes in and out then in then back out ... pizza
/// delivery guy carrying a mail package just got stuck there even into a new round"), BETWEEN the hitman waves since 0.1.57
/// ("it should be mostly the mob coming after him"; pick 3: the courier and Sal come between the waves). One run, one door,
/// two nights; every hitman goes down as he reports out of the car unless said otherwise:
///   1. Everyone left alone, the door shut. Before each visitor the street is tidied: he sets off with no body on the porch,
///      the path or in the doorway, to a shut door. The courier (after wave 1): a red cap and a pizza box (never the parcel)
///      carried flat at his waist in both hands, his line, the box set down on the step outside the door, off the porch: gone
///      out of the door camera's view, beyond the door, never drawn again, one switch at the threshold. The box stays on the
///      step through waves 2 and 3 (no room clear between the waves) and goes with the 2:00 AM cut. Sal (after wave 3): a short
///      visit, his leaving line ("Fine. Tell him Sal came by.": Mike's in-room "leave" slot), out, the door pulled to behind
///      him. Neither gets a banner or any show for being left alone (Brett 0.1.58): the night just goes on. At the next car
///      nobody from either visit is left in the scene. The relief at two (Ortiz's only visit now): he
///      radios once before he walks up, knocks and says "It's Ortiz." through the shut door before it opens (Ricky never asks
///      who), is known as a deputy from his call, wears the U.S. MARSHAL print chest and back, and is PERFECT. No Ortiz
///      check-in on the night.
///   2. Waves 1 and 3 let in (each man put down once he's in the room), so each kicks the front door in: the broken door is
///      swung shut by the tidy and works again — the courier finds it shut, it opens by hand on him as he comes up the path,
///      and he turns, holds the box out to you, says his line and sets it down on the step (you shut it again after him); Sal
///      finds it shut and lets himself in with his key through it. Then Sal is stabbed once in the room: his hurt line, never his leaving line, and the night
///      ends a beat later on its booking (assault, no firearm years, the federal count, the OPR referral; no police) with the
///      hitman run called off there: no wave 4.
///   Just this: scripts/unity-batch.sh ScenarioTests.RunMarshalVisitorsHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunMarshalVisitorsHeadless() => RunMarshal(MarshalVisitorsRun, 1600f);   // (between five waves since Brett 0.1.57: was 900)

    const string CourierName = "Pizza courier", SalName = "Sal, Ricky's brother";

    /// <summary>One visitor's round as the test saw it: its log lines, and a poll every couple of frames.</summary>
    sealed class VisitSeen
    {
        public string Name; public int Id; public int Night; public IntruderT Man; public bool Bound;
        public float Radio = -1f, Spawn = -1f, Knock = -1f, KnockLine = -1f, Face = -1f, OpenedOnHim = -1f, DoorOpen = -1f, Idle = -1f,
                     Hello = -1f, Leaves = -1f, LeaveLine = -1f, HurtLine = -1f, Hurt = -1f, Flee = -1f, Gone = -1f, Verdict = -1f;
        public int Radios, HiddenFlips, DrawnFlips, Prints;
        /// <summary>Outcome shows that started while his was the current visit, and his round's "no show (left alone)" line.</summary>
        public int Shows; public bool NoShow;
        public bool? WasHidden, WasDrawn;
        public bool DrawnAfterGone, RickyHeard, KnownDeputy, GoneOk;
        public string Tier, GoneInfo = "never gone";
        /// <summary>As he set off: the front door ("shut", "open", "shut but broken") and the street ("clear" or what lay on it).</summary>
        public string DoorAtSpawn, StreetAtSpawn;
        // The courier's box.
        public Transform Box;
        public string WalkInfo, HandsInfo, HeldInfo, DownInfo;
        public bool WalkOk, HandsOk, HeldOk, DownOk;

        public override string ToString() => $"{Name} (#{Id}, night {Night})";
    }

    static bool AnyDrawn(IntruderT m)
    {
        if (m == null) return false;
        foreach (var r in m.GetComponentsInChildren<Renderer>(true)) if (r != null && r.enabled && r.gameObject.activeInHierarchy) return true;
        return false;
    }

    /// <summary>The test hand takes the front door's lever, swings it 60° open and lets go (as Brett did on 0.1.56).</summary>
    static void OpenByHand(HandController h, VirtualDoor door)
    {
        HandAt(h, door.HandleWorld + door.Inward * 0.05f);
        Grip(h);
        SwingHand(h, door, 60f);
        Squeeze(h, 0f);
    }

    /// <summary>The test hand takes the open leaf's lever and swings it back shut (it latches under 2°), then lets go.</summary>
    static void ShutByHand(HandController h, VirtualDoor door)
    {
        HandAt(h, door.HandleWorld);
        Grip(h);
        SwingHand(h, door, -(door.OpenAngle + 5f));
        Squeeze(h, 0f);
    }

    /// <summary>
    /// The street as a visitor sets off: "clear", or the squad bodies on it — out of the room (on the porch side, past a door's
    /// plane) or with their root within 0.8 m of the front door's threshold (a man dead in his seat is in his car: not counted).
    /// </summary>
    static string StreetNow(VirtualDoor door)
    {
        var lying = new List<string>();
        foreach (var o in IntruderT.All)
        {
            if (o == null || !o.IsHitman || o.Current != IntruderT.State.Dead || o.Riding) continue;
            bool outside = o.BehindDoor || HIS.Intruder.DoorwayNav.AnyBeyond(o.transform.position);
            bool doorway = door != null && FlatOf(o.transform.position, door.Threshold) < 0.8f;
            if (outside || doorway) lying.Add($"#{o.Id} {(doorway ? "in the doorway" : "outside")} at {o.transform.position:F1}");
        }
        return lying.Count == 0 ? "clear" : string.Join(", ", lying);
    }

    static async Task MarshalVisitorsRun()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("marshal visitors: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        var door = VirtualDoor.Instance;
        if (door == null || s_odGrip == null) { ExtraFail("marshal visitors: the front door", $"door {door}, GripInput {s_odGrip}"); return; }

        var seen = new List<VisitSeen>();
        VisitSeen cur = null;
        int night = 1, parcels = 0, pizzas = 0, capped = 0, pulls = 0, failures = 0, radiosPending = 0, checkIns = 0, breaches = 0, cancels = 0, policeRuns = 0;
        float radioPending = -1f, heldOutAt = -1f, setDownAt = -1f, cardAt = -1f;
        string firstFailure = null;
        var waveCalls = new Dictionary<int, (string Leftover, string Door)>();   // the current night's wave calls: who's left from the visits, the door
        var spawnRx = new Regex(@"^\[HIS\] Intruder #(\d+): (Pizza courier|Sal, Ricky's brother|Deputy Ortiz, the relief) \(");
        var verdictRx = new Regex(@"^\[HIS\] VERDICT #\d+ (.+?): (Perfect|Good|Bad|YouDied)");
        var waveRx = new Regex(@"^\[HIS\] Hitmen: wave (\d) \(.*\) called");
        string LeftoverNow()
        {
            var who = new List<string>();
            foreach (var i in UnityEngine.Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude))
                if (i != null && !i.IsProtectee && !i.IsHitman && i.Intent != Intent.Police) who.Add($"#{i.Id} {i.Current}");
            return who.Count == 0 ? "none" : string.Join(", ", who);
        }
        // The moment he's gone (hidden for good): where he is, drawn or not, on the door camera or not. Taken at his "gone —
        // hidden for good" line too (after Vanish, in the same frame): since Brett 0.1.58 a visitor left alone gets no show, so
        // the director's safety net (ClearLeftoverVisitors) destroys him the frame his round ends — before the next poll.
        void MarkGone(VisitSeen v)
        {
            var m = v.Man;
            if (m == null || v.Gone >= 0f) return;
            v.Gone = Time.time;
            bool drawn = AnyDrawn(m);
            var p = m.transform.position;
            var cam = HIS.Security.SecurityCamera.For(door);
            bool onCam = cam != null && (cam.Sees(p + Vector3.up * 0.9f) || cam.Sees(p + Vector3.up * 1.7f));
            var nav = HIS.Intruder.DoorwayNav.Instance;
            bool beyond = nav != null && nav.Beyond(p);
            v.GoneOk = !drawn && !onCam && beyond && cam != null;
            v.GoneInfo = $"drawn {drawn}, on the door camera {(cam == null ? "(no camera)" : onCam.ToString())}, beyond the door {beyond}, at {p:F2}";
        }
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            float now = Time.time;
            if (type == LogType.Exception || msg.Contains("brain failed") || msg.Contains("brain stopped")) { failures++; firstFailure ??= msg; }
            if (msg.Contains(": safe house, 1:12 AM")) cardAt = now;
            if (msg.Contains(OrtizCheckInName)) checkIns++;
            if (msg.StartsWith("[HIS] Door: breached")) breaches++;
            if (msg.StartsWith("[HIS] Hitmen cancelled")) cancels++;
            if (msg.StartsWith("[HIS] Police responding")) policeRuns++;
            if (msg.StartsWith("[HIS] Marshal: Ortiz radios ahead")) { radioPending = now; radiosPending++; }
            var wm = waveRx.Match(msg);
            if (wm.Success) waveCalls[int.Parse(wm.Groups[1].Value)] = (LeftoverNow(), door.IsOpen ? "open" : "shut");
            var sm = spawnRx.Match(msg);
            if (sm.Success)
            {
                cur = new VisitSeen { Id = int.Parse(sm.Groups[1].Value), Name = sm.Groups[2].Value, Night = night, Spawn = now,
                                      Radio = radioPending, Radios = radiosPending,
                                      DoorAtSpawn = door.IsOpen ? "open" : door.Breached ? "shut but broken" : "shut", StreetAtSpawn = StreetNow(door) };
                seen.Add(cur);
                radioPending = -1f; radiosPending = 0;
                return;
            }
            if (msg.Contains("carries a parcel")) parcels++;
            if (msg.Contains("carries a pizza box (camera tell)")) pizzas++;
            if (msg.Contains("dressed as the courier (red cap")) capped++;
            if (msg.Contains("pulls the door to behind him")) pulls++;
            if (cur == null) return;
            if (msg.StartsWith("[HIS] Outcome show: ")) cur.Shows++;
            if (msg.StartsWith("[HIS] Scenario #") && msg.Contains($" {cur.Name}: no show (left alone)")) cur.NoShow = true;
            string me = $"#{cur.Id}: ";
            if (msg.Contains(me + "gone — hidden for good")) MarkGone(cur);
            if (msg.Contains(me + "knocks and says who he is")) cur.Knock = now;
            if (msg.Contains(me + "says who he is to your face")) cur.Face = now;
            if (msg.Contains(me + "the door opened on him")) cur.OpenedOnHim = now;
            if (msg.Contains(me + "leaves (")) cur.Leaves = now;
            if (msg.Contains(me + "hurt by you")) cur.Hurt = now;
            if (msg.Contains(me + "holds the pizza out to you")) heldOutAt = now;
            if (msg.Contains(me + "sets the pizza down on the step")) setDownAt = now;
            if (msg == "[HIS] Subtitle Ortiz: It's Ortiz." && cur.KnockLine < 0f) cur.KnockLine = now;
            if ((msg == "[HIS] Subtitle Ortiz: " + VoiceLines.Text("vo.relief.hello") || msg == "[HIS] Subtitle Sal: Ricky? It's Sal. You up?"
                 || msg == "[HIS] Subtitle Courier: Pizza. Large pepperoni. It's paid for.") && cur.Hello < 0f) cur.Hello = now;
            if (msg == "[HIS] Subtitle Sal: " + VoiceLines.Text("vo.brother.leave") && cur.LeaveLine < 0f) cur.LeaveLine = now;
            if (msg == "[HIS] Subtitle Sal: Ow! What the hell, man?!" && cur.HurtLine < 0f) cur.HurtLine = now;
            if (msg.StartsWith("[HIS] Intruder: Fleeing") && cur.Flee < 0f) cur.Flee = now;
            if (msg.StartsWith("[HIS] Ricky: hears the door") && cur.Verdict < 0f) cur.RickyHeard = true;
            var vm = verdictRx.Match(msg);
            if (vm.Success && vm.Groups[1].Value == cur.Name && cur.Verdict < 0f) { cur.Verdict = now; cur.Tier = vm.Groups[2].Value; }
        };

        // Every couple of frames: bind the director's man, his switches at the threshold, the moment he's gone, the courier's box.
        int outside = LayerMask.NameToLayer("Outside");
        void Watch(VisitSeen v)
        {
            if (v == null) return;
            float now = Time.time;
            if (!v.Bound && dir.Intruder != null && dir.Intruder.Id == v.Id)
            {
                v.Man = dir.Intruder; v.Bound = true;
                v.KnownDeputy = v.Man.Facts.KnownDeputy;
                foreach (var r in v.Man.GetComponentsInChildren<Renderer>(true))
                    if (r.name.StartsWith("MarshalPrint") && r.sharedMaterial != null && r.sharedMaterial.mainTexture != null) v.Prints++;
            }
            var m = v.Man;
            if (m == null) return;                                       // (destroyed: the night's clear)
            if (v.DoorOpen < 0f && door.IsOpen && m.Current != IntruderT.State.Gone) v.DoorOpen = now;
            if (v.Idle < 0f && m.Current == IntruderT.State.Idle) v.Idle = now;
            bool hidden = m.BehindDoor, drawn = AnyDrawn(m);
            if (v.WasHidden.HasValue && v.WasHidden.Value != hidden) v.HiddenFlips++;
            if (v.WasDrawn.HasValue && v.WasDrawn.Value != drawn) v.DrawnFlips++;
            v.WasHidden = hidden; v.WasDrawn = drawn;
            if (m.Current == IntruderT.State.Gone)
            {
                if (v.Gone < 0f) MarkGone(v);
                else if (drawn) v.DrawnAfterGone = true;
            }
            if (v.Name != CourierName) return;
            // The box while he walks up: flat, level, at his waist, both hands on its sides.
            if (v.Box == null && !v.WalkOk && v.WalkInfo == null && m.OnPorchPath && now - v.Spawn > 1.5f)
            {
                v.Box = m.transform.Find("Prop_PizzaBox");
                if (v.Box == null) { v.WalkInfo = "no Prop_PizzaBox under him"; return; }
                var b = RenderBounds(v.Box);
                float level = Vector3.Dot(v.Box.up, Vector3.up), height = v.Box.position.y - m.transform.position.y;
                v.WalkOk = b.size.y <= 0.075f && Mathf.Max(b.size.x, b.size.z) >= 0.38f && level >= 0.97f && height >= 0.8f && height <= 1.25f
                           && v.Box.gameObject.layer == outside;
                v.WalkInfo = $"size {b.size:F3}, level {level:F3}, {height:F2} m up, layer {LayerMask.LayerToName(v.Box.gameObject.layer)}";
                var anim = m.GetComponentInChildren<Animator>();
                var lh = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : null;
                var rh = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
                if (lh != null && rh != null)
                {
                    float half = 0.2f;
                    float dl = Vector3.Distance(lh.position, v.Box.position - v.Box.right * half);
                    float dr = Vector3.Distance(rh.position, v.Box.position + v.Box.right * half);
                    v.HandsOk = dl <= 0.18f && dr <= 0.18f;
                    v.HandsInfo = $"left hand {dl:F2} m, right hand {dr:F2} m from the box's sides";
                }
                else v.HandsInfo = "no hand bones";
            }
            // Held out to you (the door opened on him), a beat into it.
            if (heldOutAt > 0f && v.HeldInfo == null && now - heldOutAt > 0.6f && v.Box != null && v.Box.parent == m.transform)
            {
                float ahead = Vector3.Dot(v.Box.position - m.transform.position, FlatV(m.transform.forward).normalized);
                var eye = Camera.main != null ? Camera.main.transform.position : Vector3.up * 1.6f;
                float off = Vector3.Angle(FlatV(m.transform.forward), FlatV(eye - m.transform.position));
                v.HeldOk = ahead >= 0.38f && off < 30f;
                v.HeldInfo = $"box {ahead:F2} m out in front of him, he's {off:F0}° off facing you";
            }
            // Down on the step: his no longer, level, on the floor, outside the door, on the outside's layer.
            if (setDownAt > 0f && v.DownInfo == null && now - setDownAt > 0.1f && v.Box != null)
            {
                var b = RenderBounds(v.Box);
                float level = Vector3.Dot(v.Box.up, Vector3.up), floorGap = b.min.y - m.transform.position.y;
                float outBy = -Vector3.Dot(v.Box.position - door.Threshold, door.Inward);
                v.DownOk = v.Box.parent == null && Mathf.Abs(floorGap) <= 0.03f && level >= 0.99f && outBy > 0.2f && v.Box.gameObject.layer == outside;
                v.DownInfo = $"parent {(v.Box.parent == null ? "none" : v.Box.parent.name)}, {floorGap:F3} m off his floor, level {level:F3}, " +
                             $"{outBy:F2} m outside the door, layer {LayerMask.LayerToName(v.Box.gameObject.layer)}";
            }
        }

        // The hitmen: down as each reports out of the car — or, on the waves let in (night 2's 1 and 3), once he's in the room
        // (after his kick: the broken door the next visitor must still get through), the finder too (he only fires at Ricky
        // 2.5 s after his "in the room").
        var down = new HashSet<IntruderT>();
        var letIn = new HashSet<int>();
        Action<IntruderT, string> onReport = (o, r) =>
        {
            if (o == null || !o.IsHitman) return;
            bool kill = letIn.Contains(o.Wave)
                ? r == "in the room" || r == "fighting" || r.StartsWith("finder: goes for") || r.StartsWith("finder: raises")
                : r == "out of the car" || r == "in the room" || r == "fighting" || r.StartsWith("finder: ");
            if (kill) lock (down) down.Add(o);
        };
        void PutDown() { lock (down) foreach (var o in down) if (o != null && o.Current != IntruderT.State.Dead) o.TakeDamage(1000f); }

        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        bool visitorsWere = ScenarioDirector.MarshalVisitors;
        ScenarioDirector.MarshalVisitors = true;
        var hand = OpenDoorHand(false);
        try
        {
            // ── Night 1: everyone left alone, the door shut.
            int nightsBefore = dir.NightsDone;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            float until = Time.realtimeSinceStartup + 700f;
            VisitSeen courier1 = null; bool ySent = false;
            string boxAtSal = "Sal never came", boxAtRelief = "the relief never came";
            bool boxKeptAtSal = false, boxGoneAtRelief = false;
            while (EditorApplication.isPlaying && Time.realtimeSinceStartup < until && dir.NightsDone == nightsBefore)
            {
                Watch(cur);
                PutDown();
                if (cur != null && cur.Name == CourierName) courier1 = cur;
                // (A Y in the quiet after the 1:12 card: this run is about the visitors, not the quiet.)
                if (!ySent && cardAt > 0f && Time.time > cardAt + 3.4f) { ySent = true; dir.RequestSkip(); }
                if (courier1 != null && cur != null && cur.Name == SalName && boxAtSal == "Sal never came" && Time.time - cur.Spawn > 0.3f)
                {
                    boxKeptAtSal = courier1.Box != null && courier1.Box.parent == null;
                    boxAtSal = courier1.Box == null ? "gone" : $"on the step at {courier1.Box.position:F2}";
                }
                if (courier1 != null && cur != null && cur.Name == ReliefName && boxAtRelief == "the relief never came" && Time.time - cur.Spawn > 0.3f)
                {
                    boxGoneAtRelief = courier1.Box == null;
                    boxAtRelief = courier1.Box == null ? "gone with the 2:00 AM clear" : $"still at {courier1.Box.position:F2}";
                }
                await Wait(0.02f);
            }
            var n1 = seen.FindAll(s => s.Night == 1);
            var c1 = n1.Find(s => s.Name == CourierName); var s1 = n1.Find(s => s.Name == SalName); var r1 = n1.Find(s => s.Name == ReliefName);
            if (c1 == null || s1 == null || r1 == null) { ExtraFail("marshal visitors: night 1's visitors", $"seen: {string.Join(", ", seen)}; {dir.Status}"); return; }
            var calls1 = new Dictionary<int, (string Leftover, string Door)>(waveCalls);

            MarshalCheck(c1.DoorAtSpawn == "shut" && c1.StreetAtSpawn == "clear" && s1.DoorAtSpawn == "shut" && s1.StreetAtSpawn == "clear",
                  "marshal visitors: each sets off up a tidied street to a shut door",
                  $"the courier: door {c1.DoorAtSpawn}, street {c1.StreetAtSpawn}; Sal: door {s1.DoorAtSpawn}, street {s1.StreetAtSpawn}");
            MarshalCheck(parcels == 0 && pizzas >= 1 && capped >= 1, "marshal visitors: the courier carries a pizza box and wears a red cap, never the parcel",
                  $"pizza boxes {pizzas}, caps {capped}, parcels {parcels}");
            MarshalCheck(c1.WalkOk, "marshal visitors: the box is carried flat and level at his waist", c1.WalkInfo ?? "never checked");
            MarshalCheck(c1.HandsOk, "marshal visitors: both his hands are on the box (IK)", c1.HandsInfo ?? "never checked");
            MarshalCheck(c1.DownOk && c1.Hello > 0f && c1.Flee > 0f && c1.Flee - c1.Hello >= 2f && c1.Flee - c1.Hello <= 7f,
                  "marshal visitors: the courier says his line, sets the box on the step outside the door, and goes",
                  $"{c1.DownInfo ?? "never set down"}; his line to leaving {(c1.Hello > 0f && c1.Flee > 0f ? $"{c1.Flee - c1.Hello:F1} s" : "never")}");
            CheckGone(c1, "the courier");
            // Brett 0.1.58, replaying the intro: "when he leaves it shouldnt give the good call banner the mob people should come" —
            // left alone, a visitor between the waves gets no show at all (no banner, no sound): his round logs "no show (left
            // alone)" and no Outcome show starts from his set-off until the next visitor sets off (the night's own card comes
            // after the relief's).
            MarshalCheck(c1.NoShow && s1.NoShow && c1.Shows == 0 && s1.Shows == 0,
                  "marshal visitors: the courier and Sal left alone get no banner or show — the night just goes on",
                  $"the courier: 'no show' {c1.NoShow}, shows {c1.Shows}; Sal: 'no show' {s1.NoShow}, shows {s1.Shows} (want true, 0 each)");
            MarshalCheck(boxKeptAtSal && boxGoneAtRelief, "marshal visitors: the box stays on the step through the next waves and goes with the 2:00 AM cut",
                  $"as Sal set off: {boxAtSal}; as the relief set off: {boxAtRelief}");
            MarshalCheck(s1.Hello > 0f && s1.Leaves > 0f && s1.Leaves - s1.Hello >= 7.5f && s1.Leaves - s1.Hello <= 10.5f && s1.LeaveLine > 0f,
                  $"marshal visitors: Sal's visit is a short beat, then his leaving line (\"{VoiceLines.Text("vo.brother.leave")}\")",
                  $"hello to his leaving {(s1.Hello > 0f && s1.Leaves > 0f ? $"{s1.Leaves - s1.Hello:F1} s" : "never")}, his line {(s1.LeaveLine > 0f ? "said" : "missing")}");
            CheckGone(s1, "Sal");
            calls1.TryGetValue(2, out var w2); calls1.TryGetValue(4, out var w4);
            MarshalCheck(pulls >= 1 && w4.Door == "shut", "marshal visitors: Sal pulls the door to behind him (he opened it)",
                  $"{pulls} pull(s), the door at wave 4's call {w4.Door ?? "never called"}");
            MarshalCheck(w2.Leftover == "none" && w4.Leftover == "none", "marshal visitors: at the next car nobody from the visit is left in the scene",
                  $"at wave 2: {w2.Leftover ?? "never called"}; at wave 4: {w4.Leftover ?? "never called"}");
            // The relief at two: Ortiz's only visit of the night now.
            MarshalCheck(r1.Radios == 1 && r1.Radio > 0f && r1.Spawn - r1.Radio >= 2f,
                  "marshal visitors: the relief radios once before he walks up",
                  $"{r1.Radios} call(s), {(r1.Radio > 0f ? $"{r1.Spawn - r1.Radio:F1} s before he set off" : "none")}");
            MarshalCheck(r1.Knock > 0f && r1.KnockLine > 0f && r1.DoorOpen > 0f && r1.KnockLine < r1.DoorOpen && !r1.RickyHeard,
                  "marshal visitors: the relief knocks and says \"It's Ortiz.\" through the shut door before it opens; Ricky doesn't ask who",
                  $"knock {(r1.Knock > 0f ? "yes" : "no")}, his line {(r1.KnockLine > 0f && r1.DoorOpen > 0f ? $"{r1.DoorOpen - r1.KnockLine:F1} s before the door opened" : "missing")}, " +
                  $"Ricky asked {r1.RickyHeard}");
            MarshalCheck(r1.KnownDeputy && r1.Prints == 2 && r1.Tier == "Perfect",
                  "marshal visitors: the relief is a known deputy from his call, U.S. MARSHAL on his chest and back, PERFECT",
                  $"known deputy {r1.KnownDeputy}, {r1.Prints} printed panel(s), {r1.Tier ?? "no verdict"}");
            MarshalCheck(checkIns == 0, "marshal visitors: no Ortiz check-in on the night", $"'{OrtizCheckInName}' seen {checkIns} time(s)");

            // ── Night 2: waves 1 and 3 let in (each kicks the front door in); the door opened by hand on the courier; Sal stabbed.
            dir.SetMode(ScenarioDirector.Mode.Scenarios);
            await Wait(0.5f);
            night = 2; cur = null; heldOutAt = setDownAt = cardAt = -1f; radioPending = -1f; radiosPending = 0;
            int breachesBefore = breaches, cancelsBefore = cancels;
            waveCalls.Clear();
            lock (down) { letIn.Add(1); letIn.Add(3); }
            nightsBefore = dir.NightsDone;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            bool openedCourier = false, stabbed = false, shutAfterCourier = false; ySent = false;
            float stabAt = -1f;
            int breachesAtCourier = -1, breachesAtSal = -1;
            string handleAtCourier = "the courier never came";
            until = Time.realtimeSinceStartup + 700f;
            while (EditorApplication.isPlaying && Time.realtimeSinceStartup < until && dir.NightsDone == nightsBefore)
            {
                Watch(cur);
                PutDown();
                if (!ySent && cardAt > 0f && Time.time > cardAt + 3.4f) { ySent = true; dir.RequestSkip(); }
                var m = cur?.Man;
                if (m != null)
                {
                    float far = FlatOf(m.transform.position, door.Threshold);
                    bool atTheDoorStill = m.Current is IntruderT.State.Outside or IntruderT.State.BreakIn;
                    if (cur.Name == CourierName && breachesAtCourier < 0)
                    { breachesAtCourier = breaches - breachesBefore; handleAtCourier = $"{(door.IsOpen ? "open" : "shut")}, broken {door.Breached}, handle {(door.HandleFree ? "free" : "not free")}"; }
                    if (cur.Name == SalName && breachesAtSal < 0) breachesAtSal = breaches - breachesBefore;
                    if (cur.Name == CourierName && !openedCourier && atTheDoorStill && far < 2.6f && !door.IsOpen && door.HandleFree) { openedCourier = true; OpenByHand(hand, door); }
                    if (cur.Name == SalName && !stabbed && m.Current == IntruderT.State.Idle && !m.BehindDoor && cur.Idle > 0f && Time.time - cur.Idle > 1f)
                    { stabbed = true; stabAt = Time.time; HitWith(m, HumanBodyBones.Hips, DamageType.Stab, 40f); }
                }
                // The door you opened stays as you left it (Question 5: the hitmen never shut one you opened by hand): you shut it
                // again once the courier's round is over, so wave 3 has a shut door to kick in before Sal.
                if (cur != null && cur.Name == CourierName && openedCourier && !shutAfterCourier && (cur.Verdict > 0f || cur.Gone > 0f)
                    && door.IsOpen && door.HandleFree)
                { shutAfterCourier = true; ShutByHand(hand, door); }
                await Wait(0.02f);
            }
            var n2 = seen.FindAll(s => s.Night == 2);
            var c2 = n2.Find(s => s.Name == CourierName); var s2 = n2.Find(s => s.Name == SalName);
            if (c2 == null || s2 == null) { ExtraFail("marshal visitors: night 2's visitors", $"seen: {string.Join(", ", n2)}; {dir.Status}"); return; }

            MarshalCheck(breachesAtCourier >= 1 && c2.DoorAtSpawn == "shut" && c2.StreetAtSpawn == "clear" && openedCourier && c2.OpenedOnHim > 0f,
                  "marshal visitors: wave 1 kicked the door in — the tidy swings it shut, and it opens by hand on the courier",
                  $"kicks in before him {breachesAtCourier}; as he set off the door {c2.DoorAtSpawn} ({handleAtCourier}), the street {c2.StreetAtSpawn}; " +
                  $"opened by hand {openedCourier}, the door opened on him {(c2.OpenedOnHim > 0f ? "yes" : "no")}");
            MarshalCheck(c2.HeldOk, "marshal visitors: the door opened on the courier — he turns and holds the box out to you", c2.HeldInfo ?? "never held out");
            MarshalCheck(c2.DownOk && c2.Hello > 0f && c2.Flee > 0f && c2.Flee - c2.Hello >= 2f && c2.Flee - c2.Hello <= 7.5f,
                  "marshal visitors: face to face, his line, the box on the step, and he goes",
                  $"{c2.DownInfo ?? "never set down"}; his line to leaving {(c2.Hello > 0f && c2.Flee > 0f ? $"{c2.Flee - c2.Hello:F1} s" : "never")}");
            CheckGone(c2, "the courier (door opened on him)");
            MarshalCheck(breachesAtSal >= 2 && s2.DoorAtSpawn == "shut" && s2.DoorOpen > 0f && s2.Idle > 0f,
                  "marshal visitors: wave 3 kicked it in again — shut for Sal, and he lets himself in through it",
                  $"kicks in before him {breachesAtSal}; as he set off the door {s2.DoorAtSpawn}; it opened {(s2.DoorOpen > 0f ? "yes" : "no")}, he was in the room {(s2.Idle > 0f ? "yes" : "no")}");
            await Wait(0.5f);                                            // (the booking's show has begun)
            var card = dir.LastVerdict.GetValueOrDefault();
            waveCalls.TryGetValue(4, out var n2w4);
            MarshalCheck(stabbed && s2.HurtLine > 0f && s2.HurtLine - stabAt <= 1.5f && s2.Hurt > 0f && s2.LeaveLine < 0f && dir.NightsDone > nightsBefore,
                  "marshal visitors: Sal stabbed — his hurt line, never his leaving line, and the night ends",
                  $"stabbed {stabbed}, hurt line {(s2.HurtLine > 0f ? $"{s2.HurtLine - stabAt:F1} s after" : "missing")}, leaving line {(s2.LeaveLine > 0f ? "SAID" : "never")}, " +
                  $"night over {dir.NightsDone > nightsBefore}");
            MarshalCheck(card.Tier == Tier.Bad && card.Headline == Judge.HeadlineBad && card.Charge == Judge.ChargeInjured && card.WhoItWas == Judge.WhoSal
                         && Has(card.Sentence, "8 years in prison") && Has(card.Sentence, "18 U.S.C. 242") && Has(card.Reason, Sentencing.OprReferral)
                         && policeRuns == 0 && cancels == cancelsBefore + 1 && n2w4.Door == null && !PoliceResponse.Instance.Active,
                  "marshal visitors: the stab between waves is the booking (assault, no firearm years), the run called off — no wave 4, no police",
                  $"card {card.Tier} '{card.Headline}' charge '{card.Charge}' who '{card.WhoItWas}' — {OneLine(card.Reason)} / {OneLine(card.Sentence)}; " +
                  $"police {policeRuns}, hitman runs cancelled +{cancels - cancelsBefore}, wave 4 {(n2w4.Door == null ? "never called" : "CALLED")}");
            MarshalCheck(failures == 0, "marshal visitors: no brain failure or exception", failures == 0 ? "none" : $"{failures}, first: {firstFailure}");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            if (hand != null) UnityEngine.Object.Destroy(hand.gameObject);
            ScenarioDirector.MarshalVisitors = visitorsWere;
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }

        // Left alone, he's PERFECT; once gone, out of sight and never drawn again; one switch at the threshold at most.
        void CheckGone(VisitSeen v, string who)
        {
            MarshalCheck(v.Tier == "Perfect" && v.GoneOk && !v.DrawnAfterGone && v.HiddenFlips <= 2 && v.DrawnFlips <= 1,
                  $"marshal visitors: {who} leaves out of sight — gone off the porch, out of the camera's view, never drawn again",
                  $"{v.Tier ?? "no verdict"}; at gone: {v.GoneInfo}; drawn after {v.DrawnAfterGone}; switches in/out of view {v.HiddenFlips}, drawn on/off {v.DrawnFlips}");
        }
    }

    static Bounds RenderBounds(Transform root)
    {
        var rs = root.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Bounds(root.position, Vector3.zero);
        var b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    static float FlatOf(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }
}
