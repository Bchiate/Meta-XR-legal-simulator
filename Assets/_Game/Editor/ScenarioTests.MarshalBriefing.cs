using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;
using BodyPart = HIS.Weapons.BodyPart;                               // (UnityEditor has one too)
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The Marshal night's HANDOVER, IN PERSON (Scenario/MarshalBriefing, Intruder/IntruderHandover; Brett 0.1.57: "I thought ortiz
/// was just apart of the story intro and tutorial which seemed to be missing"; before that 2026-10-04: "it should have more of a
/// story feel to it"; research/27 §6 step 11). What Brett would see, checked the way a player plays it:
///   1. Editor: no handover unless asked; the night opens on Ricky, no Ortiz.
///   2. The pause menu's "Weapons check" row: the night starts over with the drill (the door not asked again). Deputy Ortiz is
///      IN THE ROOM from its start: standing inside by the front door on the NavMesh, out of the leaf's sweep, never between the
///      player and the practice target nor behind it (≥ 25° off the eye→target line, ≥ 1 m from the board), ≥ 0.8 m from Ricky,
///      dressed as the deputy (the U.S. MARSHAL print), no weapon out.
///   3. Step 0: "Marshal. I'm Ortiz." / "That's Ricky. He testifies Monday." / "Keep him breathing till two." — Ricky's "So
///      you're the new guy." — "Weapons check before I go.", each of Ortiz's IN PERSON: a positional voice source at his head
///      (when the take is in), the caption "Ortiz", nothing on the radio; "Deputy Ortiz" over his head with his hello, then
///      "Ricky" over Ricky; the skip offer at the left hand only once the check is called.
///   4. A practice round, blade or fist on Ricky: "Hey! Watch it!", nothing more; on Ortiz: "Whoa! Watch it!" once, no damage,
///      no force reported, no verdict.
///   5. Steps 1–8 done through the game's own input code (two test hands squeeze grip through HandController.GripInput; the
///      held guns get trigger / B through Firearm.HandUpdate; the knife meets the cardboard at speed; A + a stick flick yell),
///      each waiting while it isn't done, each line in person.
///   6. Step 9: "Get the door for me." — he waits for it; opened by hand, he walks out through the doorway; through it, "Lock
///      it behind me." and "Swing it shut" at the handle; the step waits until it's latched shut behind him.
///   7. Step 10 ON THE RADIO (squelch + caption), the check done, the sign-off on the radio: "Good. See you at two."; after it
///      no target, label or Ortiz (he walked off the porch and was removed), both guns full, nothing judged, the same Ricky,
///      the 1:12 card.
///   8. Again from the row: a tap of Y doesn't skip; a 1 s hold does — "Alright. Lock it behind me.", he leaves, the 1:12 card
///      within the skip's 6 s (+ a beat), then he's gone; a hit on Ricky is real again.
///   9. The SHORT handover (no drill due): Ortiz's four beats and "Two o'clock. Lock it behind me.", only the two name labels,
///      no target; he opens the front door himself, walks out, the door shut behind him; the 1:12 card within ~20 s; nothing
///      left. And Y held during it: the skip line, he goes, the card within the skip's 6 s.
///  10. The session rule (MarshalBriefing.TestSessionRule: as on the headset): a switch into Marshal owes the handover; the
///      next night (a restart) has none; switching modes and back gets it again.
///   Just this: scripts/unity-batch.sh ScenarioTests.RunMarshalBriefingHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunMarshalBriefingHeadless() => RunMarshal(MarshalBriefingRun, 900f);

    const BindingFlags MbNP = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>A HandController input (Trigger, TriggerDown, SecondaryButtonDown, PrimaryButton, Thumbstick, Velocity …) as the hand's ReadInput would set it.</summary>
    static void SetHand<T>(HandController h, string prop, T value) => typeof(HandController).GetProperty(prop).GetSetMethod(true).Invoke(h, new object[] { value });

    /// <summary>One frame of a button held on a held weapon's hand: the weapon reads it in HandUpdate, as HandController.Update calls it.</summary>
    static void Press(HandController h, WeaponBase w, string button) { SetHand(h, button, true); w.HandUpdate(h); SetHand(h, button, false); }

    /// <summary>The M9's analog trigger, released (it arms), squeezed through the break, released (HandUpdate → SqueezeTrigger).</summary>
    static void Pull(HandController h, Firearm gun)
    {
        SetHand(h, "Trigger", 0f); gun.HandUpdate(h);
        SetHand(h, "Trigger", 1f); gun.HandUpdate(h);
        SetHand(h, "Trigger", 0f); gun.HandUpdate(h);
    }

    /// <summary>The hand turned and moved so <paramref name="gun"/>'s muzzle sits <paramref name="from"/> m in front of the point, aimed at it.</summary>
    static void AimAt(HandController h, Firearm gun, Vector3 point, Vector3 frontDir, float from = 2f)
    {
        var at = point + frontDir.normalized * from;
        for (int i = 0; i < 2; i++)
        {
            var m = gun.Muzzle;
            h.transform.rotation = Quaternion.FromToRotation(m.forward, (point - m.position).normalized) * h.transform.rotation;
            h.transform.position += at - gun.Muzzle.position;
        }
    }

    /// <summary>The squeeze: open, then closed (the grip's rising edge), with the physics scene up to date first.</summary>
    static void Grip(HandController h) { Physics.SyncTransforms(); Squeeze(h, 0f); Squeeze(h, 1f); }

    /// <summary>A hit straight onto one of a man's own hitboxes (the path every round, pellet, blade and fist takes: Hitbox.ApplyDamage).</summary>
    static float HitMan(IntruderT man, BodyPart part, DamageType type, float amount, GameObject source)
    {
        HIS.Intruder.Hitbox box = null;
        foreach (var hb in man.GetComponentsInChildren<HIS.Intruder.Hitbox>()) { if (hb.Part == part) { box = hb; break; } box ??= hb; }
        if (box == null) return -1f;
        var p = box.transform.position;
        var info = new DamageInfo { Amount = amount, Type = type, Point = p, Normal = Vector3.up, Direction = Vector3.down, Penetration = 1f, Source = source };
        return box.ApplyDamage(info);
    }

    static float HitRicky(IntruderT ricky, BodyPart part, DamageType type, float amount, GameObject source) => HitMan(ricky, part, type, amount, source);

    /// <summary>"[HIS] Briefing: step 3, Ortiz (in person): "Put three in the target."" (MarshalBriefing.OnAir's log line).</summary>
    static readonly Regex BriefingLineRe = new(@"^\[HIS\] Briefing: step (-?\d+), (\w+)(?: \((in person|radio)\))?: ""(.*)""$");

    /// <summary>Ortizes in the scene (the handover's deputy; a destroyed one reads as none).</summary>
    static int OrtizCount()
    {
        int n = 0;
        foreach (var i in UnityEngine.Object.FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude)) if (i != null && i.IsHandoverDeputy) n++;
        return n;
    }

    /// <summary>A playing 3D Sfx voice this close to <paramref name="at"/> (m): the source a line was said from. Its distance out.</summary>
    static bool VoiceNear(Vector3 at, float tol, out float dist)
    {
        dist = float.MaxValue;
        var sfx = HIS.Core.Sfx.Instance;
        if (sfx == null) return false;
        foreach (var src in sfx.GetComponentsInChildren<AudioSource>())
        {
            if (src == null || !src.isPlaying || src.clip == null || src.spatialBlend < 0.5f) continue;
            dist = Mathf.Min(dist, Vector3.Distance(src.transform.position, at));
        }
        return dist <= tol;
    }

    /// <summary>
    /// How far <paramref name="p"/> is (flat) from the floor the leaf sweeps (sampled here on its own: the leaf at every 2° from
    /// shut to its swing stop, the hinge's half-width × 2 long), so the test doesn't lean on IntruderHandover's own maths.
    /// </summary>
    static float LeafSweepGap(VirtualDoor door, Vector3 p)
    {
        var x = door.HingeToLatch; x.y = 0f; x.Normalize();
        var z = door.Inward; z.y = 0f; z.Normalize();
        var v = p - door.HingeWorld;
        float px = Vector3.Dot(v, x), pz = Vector3.Dot(v, z);
        var hw = door.HingeWorld - door.Threshold; hw.y = 0f;
        float w = hw.magnitude * 2f, stop = door.MaxOpenAngle * Mathf.Deg2Rad, best = float.MaxValue;
        for (float a = 0f; a <= stop + 1e-4f; a += 2f * Mathf.Deg2Rad)
            for (int i = 0; i <= 24; i++)
            {
                float r = w * i / 24f, dx = px - r * Mathf.Cos(a), dz = pz - r * Mathf.Sin(a);
                best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dz * dz));
            }
        return best;
    }

    /// <summary>
    /// Ortiz's stand spot, as Brett would see it (null: right, else what's wrong): in the room off the door's wall, on the
    /// NavMesh, ≥ 0.3 m clear of the leaf's sweep, ≥ 0.8 m from Ricky, and with a target ≥ 25° off the line from the eye to it
    /// and ≥ 1 m from the board. <paramref name="info"/>: how it measured.
    /// </summary>
    static string OrtizSpotWrong(IntruderT ortiz, VirtualDoor door, IntruderT ricky, PracticeTarget target, Vector3 eye, out string info)
    {
        info = "no Ortiz";
        if (ortiz == null) return "no Ortiz in the room";
        var p = ortiz.transform.position;
        float inRoom = Vector3.Dot(p - door.Threshold, door.Inward);
        bool onMesh = NavMesh.SamplePosition(p, out var hit, 0.15f, NavMesh.AllAreas);
        float leaf = LeafSweepGap(door, p);
        float fromRicky = ricky != null ? Flat(p - ricky.transform.position) : 99f;
        float angle = 180f, fromBoard = 99f;
        if (target != null)
        {
            var toBoard = target.transform.position - eye; toBoard.y = 0f;
            var toHim = p - eye; toHim.y = 0f;
            angle = Vector3.Angle(toBoard, toHim);
            fromBoard = Flat(p - target.transform.position);
        }
        var along = Vector3.Dot(p - door.Threshold, door.HingeToLatch);
        info = $"at {p:F2}: {inRoom:F2} m into the room, {along:+0.00;-0.00} m along the wall (+ latch side), leaf clear by {leaf:F2} m, " +
               $"{(ricky != null ? $"{fromRicky:F2} m from Ricky" : "no Ricky")}, {(target != null ? $"{angle:F0}° off the eye→target line, {fromBoard:F2} m from the board" : "no target")} — " +
               $"\"{MarshalBriefing.OrtizSpotWhy}\"";
        if (inRoom < 0.3f) return "not in the room (on or behind the door's wall)";
        if (!onMesh) return "not on the NavMesh";
        if (leaf < 0.3f) return "in the leaf's sweep";
        if (fromRicky < 0.8f) return "within 0.8 m of Ricky";
        if (target != null && (angle < 25f || fromBoard < 1f)) return "between the player and the target, or behind it";
        return null;
    }

    static async Task MarshalBriefingRun()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("weapons check: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        var cs = CommandSystem.Instance;
        var body = UnityEngine.Object.FindAnyObjectByType<PlayerBody>();
        var pause = UnityEngine.Object.FindAnyObjectByType<HIS.Core.GamePause>();
        var lo = Loadout.Instance;
        var cam = Camera.main;
        var door = VirtualDoor.Instance;
        if (cs == null || cs.Left == null || cs.Right == null || body == null || pause == null || lo == null || cam == null || door == null || s_odGrip == null)
        { ExtraFail("weapons check: setup", $"commands {cs != null}, hands {cs?.Left != null}/{cs?.Right != null}, body {body != null}, pause {pause != null}, loadout {lo != null}, camera {cam != null}, door {door != null}"); return; }
        var pistol = lo.SpawnedIn(lo.SlotIndex(LoadoutSlot.Sidearm)) as Firearm;
        var longGun = lo.SpawnedIn(lo.SlotIndex(LoadoutSlot.LongGun)) as Firearm;
        var knife = lo.SpawnedIn(lo.SlotIndex(LoadoutSlot.Knife)) as Knife;
        if (pistol == null || longGun == null || knife == null) { ExtraFail("weapons check: setup", $"pistol {pistol}, long gun {longGun}, knife {knife}"); return; }
        bool rifle = !longGun.ShellByShell;                                  // the AR-15 in the Long gun slot (else the pump shotgun)

        int checksRun = 0, shortsRun = 0, verdicts = 0, shows = 0, pickerOpened = 0, failures = 0, watchIts = 0, ortizWatchIts = 0, nights112 = 0;
        int ortizStuck = 0, walkedOff = 0, ortizOpened = 0, ortizPulled = 0;
        string firstFailure = null;
        var heard = new List<(int step, string who, string how, string text)>();   // every line the handover put on air, in order
        var radio = new List<(int step, string text)>();                          // every RadioVoice transmission (squelch + caption)
        var captions = new List<string>();                                        // "[HIS] Subtitle Ortiz: …"
        var fromHead = new List<(string text, bool near, float dist, bool take)>(); // each in-person line: a 3D source at his head?
        var nightAt = new List<float>();                                          // each 1:12 card (Time.time)
        IntruderT ortizNow = null;
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            if (msg.StartsWith("[HIS] Briefing: weapons check starts")) checksRun++;
            if (msg.StartsWith("[HIS] Briefing: short handover starts")) shortsRun++;
            if (msg.StartsWith("[HIS] VERDICT")) verdicts++;
            if (msg.StartsWith("[HIS] Outcome show: ")) shows++;
            if (msg.StartsWith("[HIS] Door picker open")) pickerOpened++;
            if (msg.StartsWith("[HIS] Ricky: a practice ")) watchIts++;
            if (msg.StartsWith("[HIS] Ortiz: a practice ")) ortizWatchIts++;
            if (msg.StartsWith("[HIS] Ortiz: no way out") || msg.StartsWith("[HIS] Ortiz: blocked") || msg.StartsWith("[HIS] Ortiz: his brain stopped")) ortizStuck++;
            if (msg.StartsWith("[HIS] Ortiz: opens the front door himself")) ortizOpened++;
            if (msg.StartsWith("[HIS] Ortiz: pulls the door to behind him")) ortizPulled++;
            if (ortizNow != null && msg.StartsWith($"[HIS] Intruder #{ortizNow.Id}: walks off the porch")) walkedOff++;
            if (msg.Contains(": safe house, 1:12 AM")) { nights112++; nightAt.Add(Time.time); }
            if (msg.StartsWith("[HIS] Radio Ortiz: ")) radio.Add((MarshalBriefing.Step, msg.Substring("[HIS] Radio Ortiz: ".Length)));
            if (msg.StartsWith("[HIS] Subtitle Ortiz: ")) captions.Add(msg.Substring("[HIS] Subtitle Ortiz: ".Length));
            var m = BriefingLineRe.Match(msg);
            if (m.Success)
            {
                string how = m.Groups[3].Success ? m.Groups[3].Value : "";
                heard.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value, how, m.Groups[4].Value));
                // Said in person: its source, this frame, at his head (Sfx.PlayFollow puts it there on the spot).
                var o = MarshalBriefing.Ortiz;
                if (how == "in person" && o != null)
                {
                    string key = MarshalBriefing.LineKey;
                    bool take = key != null && HIS.Core.Sfx.Has(key);
                    bool near = VoiceNear(o.HandoverHeadPoint, 0.06f, out float d);
                    fromHead.Add((m.Groups[4].Value, near, d, take));
                }
            }
            if (type == LogType.Exception || msg.Contains("brain failed") || msg.Contains("brain stopped")) { failures++; firstFailure ??= msg; }
        };
        Application.logMessageReceived += onLog;
        // A standing player: the batch camera sits on the floor, and step 1 measures a real eye height (PlayerBody ignores < 0.5 m).
        var camWas = cam.transform.localPosition;
        cam.transform.localPosition = new Vector3(camWas.x, 1.65f, camWas.z);
        // The rig's own hands stop polling OVRInput (all zero in batch): the test sets their A, stick and Y. Two test hands
        // (disabled HandControllers, OpenDoorHand) do the grabbing through the real GripInput.
        bool leftWas = cs.Left.enabled, rightWas = cs.Right.enabled;
        cs.Left.enabled = cs.Right.enabled = false;
        var right = OpenDoorHand(false); var left = OpenDoorHand(true);
        var mon = HIS.Security.SecurityMonitor.Instance;
        IntruderT ricky = null, ortiz = null;
        bool rickyGone = false, onMonitor = false;
        int mostLabels = 0, rickyForce = 0, ortizForce = 0;
        Action<IntruderT, ForceEvent> onRickyForce = (who, e) => rickyForce++;
        Action<IntruderT, ForceEvent> onOrtizForce = (who, e) => ortizForce++;
        // Every poll while the handover runs: Ricky in his spot (the one it found), nothing on the monitor, never more than two labels.
        void Poll()
        {
            if (!MarshalBriefing.Running) return;
            rickyGone |= RickyCount() != 1 || (ricky != null && IntruderT.Protectee != ricky);
            onMonitor |= mon != null && mon.BigText != null;
            mostLabels = Math.Max(mostLabels, MarshalBriefing.LabelsUp);
        }
        // Waits for the handover's step.
        async Task<bool> UntilStep(int step, float timeout) => await WaitFor(() =>
        {
            Poll();
            return MarshalBriefing.Step == step || (step == -2 && !MarshalBriefing.Running);
        }, timeout);
        // The step still waits for the player after a moment of doing nothing (or only part of it).
        async Task<bool> Waits(int step, float seconds) { await Wait(seconds); Poll(); return MarshalBriefing.Step == step && MarshalBriefing.Running; }
        // Ortiz (or Ricky) said that line on that step (a player listens first, then acts: the test waits for it).
        bool Said(int step, string who, string key) => heard.Exists(h => h.step == step && h.who == who && h.text == VoiceLines.Text(key));
        bool SaidHow(int step, string key, string how) => heard.Exists(h => h.step == step && h.who == "Ortiz" && h.how == how && h.text == VoiceLines.Text(key));
        async Task<bool> Hear(int step, string key, float timeout = 6f, string who = "Ortiz") => await WaitFor(() => { Poll(); return Said(step, who, key); }, timeout);
        string Where() => $"step {MarshalBriefing.Step}, line \"{MarshalBriefing.Line}\", label \"{MarshalBriefing.Label?.Replace("\n", " / ")}\"";
        // Every in-person line since index i came from his head (a 3D voice at it), or has no take in the library yet; text out.
        string HeadWrong(int from)
        {
            var bad = new List<string>();
            for (int i = from; i < fromHead.Count; i++)
            {
                if (!fromHead[i].take) continue;                                // (no take yet: the caption is checked instead)
                if (!fromHead[i].near) bad.Add($"\"{fromHead[i].text}\" {(fromHead[i].dist > 1e3f ? "no 3D voice playing" : $"its voice {fromHead[i].dist:F2} m from his head")}");
            }
            return bad.Count > 0 ? string.Join("; ", bad) : null;
        }
        string HeadInfo(int from)
        {
            int n = fromHead.Count - from, noTake = 0;
            for (int i = from; i < fromHead.Count; i++) if (!fromHead[i].take) noTake++;
            return $"{n} in person, {n - noTake} from a 3D voice at his head{(noTake > 0 ? $", {noTake} with no take yet (caption only: SfxLibraryBuilder.Build)" : "")}";
        }
        // The step's label: these words, its arrow at this thing (within tol m), facing the eye (yaw), sized by distance, and
        // (beside) its plate never over the thing. Null when right, else what's wrong.
        string LabelWrong(string words, Vector3 at, float tol, bool beside = true, int slot = 0)
        {
            var l = MarshalBriefing.LabelObject(slot);
            if (l == null || !l.Visible) return "no label up";
            if (l.Text != words) return $"says \"{l.Text.Replace("\n", " / ")}\" (want \"{words.Replace("\n", " / ")}\")";
            float off = Vector3.Distance(l.ThingPoint, at);
            if (off > tol) return $"its arrow's thing is {off:F2} m off";
            var head = cam.transform.position;
            // Turned to the eye about the vertical, leaning on the head's heading by how far below the eye it is (a hip label
            // reads left to right from above: WorldLabel.Place).
            var rel = l.PlateCentre - head;
            var heading = Vector3.Cross(cam.transform.right, Vector3.up) + cam.transform.forward; heading.y = 0f; heading.Normalize();
            var to = new Vector3(rel.x, 0f, rel.z) + heading * Mathf.Max(0f, -rel.y);
            var f = l.transform.forward; f.y = 0f;
            if (to.sqrMagnitude > 1e-4f && f.sqrMagnitude > 1e-4f && Vector3.Angle(to, f) > 10f) return $"turned {Vector3.Angle(to, f):F0}° off the eye";
            // Sized by distance: that of its working point, WorldLabel.Pull of the way from the thing toward the eye.
            float want = Mathf.Clamp(Vector3.Distance(head, l.ThingPoint) * (1f - HIS.UI.WorldLabel.Pull), 0.45f, 4f), scale = l.transform.lossyScale.x;
            if (Mathf.Abs(scale - want) > 0.1f * want) return $"scale {scale:F2}, want {want:F2} (its distance)";
            if (beside)
            {
                // Seen from the eye: the sight line to the thing must not pass through the plate on its way there.
                var sight = l.ThingPoint - head;
                var n = l.transform.forward;
                float denom = Vector3.Dot(n, sight);
                if (Mathf.Abs(denom) > 1e-5f)
                {
                    float t = Vector3.Dot(n, l.PlateCentre - head) / denom;   // the sight line meets the plate's plane at head + sight·t
                    if (t > 0f && t < 1f)
                    {
                        var hit = l.transform.InverseTransformPoint(head + sight * t);
                        var c = l.transform.InverseTransformPoint(l.PlateCentre);
                        var h = l.PlateHalfSize / Mathf.Max(scale, 1e-4f);
                        if (Mathf.Abs(hit.x - c.x) < h.x && Mathf.Abs(hit.y - c.y) < h.y) return "its plate covers the thing (from the eye)";
                    }
                }
            }
            return null;
        }
        string Ok(string wrong) => wrong ?? "ok";
        try
        {
            // ── 1. The Editor never reads the headset flag: the first night opens without the handover (only a request runs it).
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            bool firstRicky = await WaitFor(() => IntruderT.Protectee != null, 60);
            MarshalCheck(firstRicky && checksRun == 0 && shortsRun == 0 && OrtizCount() == 0,
                  "handover: not unless asked (Editor), the night opens on Ricky, no Ortiz",
                  $"Ricky {firstRicky}, drills {checksRun}, short handovers {shortsRun}, Ortiz {OrtizCount()}");

            // ── 2. The pause menu's row: the night starts over with the drill; the door isn't asked again; Ricky already in his
            // spot; Ortiz IN THE ROOM at a valid spot, dressed, unarmed; the target up, clear of him.
            int nights0 = nights112;
            pause.StartWeaponsCheck();
            bool started = await WaitFor(() => MarshalBriefing.Running, 20);
            ricky = IntruderT.Protectee;
            ortiz = MarshalBriefing.Ortiz;
            ortizNow = ortiz;
            var eye0 = MarshalBriefing.EyeAtStart;
            MarshalCheck(started && checksRun == 1 && MarshalBriefing.Drill && spawner.DoorConfirmed && pickerOpened == 0 && RickyCount() == 1 && ricky != null
                         && MarshalBriefing.Ricky == ricky && MarshalBriefing.Target != null && ortiz != null && OrtizCount() == 1,
                  "weapons check: the pause menu row starts it — the door kept, Ricky in his spot, Ortiz in the room, the target up",
                  $"running {started}, drill {MarshalBriefing.Drill}, door confirmed {spawner.DoorConfirmed}, picker {pickerOpened}, Ricky {RickyCount()} (the check's {(MarshalBriefing.Ricky != null && MarshalBriefing.Ricky == ricky ? "same" : "other / none")}), " +
                  $"Ortiz {OrtizCount()}, target {(MarshalBriefing.Target != null ? "up" : "none")}, monitor {(mon != null ? (mon.Placed ? "placed" : "unplaced") : "none")}");
            if (!started || ricky == null) return;
            ricky.ForceApplied += onRickyForce;
            if (ortiz != null) ortiz.ForceApplied += onOrtizForce;
            float rickyHealth0 = ricky.Health;
            int verdicts0 = verdicts, shows0 = shows;                        // (the night before the row: nothing judged during the check)
            var target = MarshalBriefing.Target;
            {
                string spotWrong = OrtizSpotWrong(ortiz, door, ricky, target, eye0, out var spotInfo);
                bool print = false, armed = false;
                if (ortiz != null)
                {
                    foreach (var r in ortiz.GetComponentsInChildren<Renderer>(true)) if (r.name.StartsWith("MarshalPrint")) print = true;
                    foreach (var w in ortiz.GetComponentsInChildren<HIS.Intruder.IntruderWeapon>(true)) if (w.InHand) armed = true;
                }
                MarshalCheck(spotWrong == null && print && !armed && ortiz != null && ortiz.Current == IntruderT.State.Idle,
                      "handover: Ortiz stands inside by the front door — off the leaf's sweep, clear of the target's line and Ricky; the deputy, unarmed",
                      $"{Ok(spotWrong)}; {spotInfo}; U.S. MARSHAL print {print}, a weapon out {armed}, {(ortiz != null ? ortiz.Current.ToString() : "none")}");
            }
            var chest = target != null ? target.ChestPoint : door.Threshold + door.Inward * 1.1f + Vector3.up * 1.15f;
            var face = target != null ? target.transform.forward : door.Inward;
            var steps = new List<string>();
            void Step(bool ok, string what, string info) { steps.Add($"{what}: {(ok ? "ok" : "FAIL")} ({info})"); MarshalCheck(ok, "weapons check: " + what, info); }

            // ── 3. Step 0, in person: hello ("Deputy Ortiz" over his head), Ricky named ("Ricky" over Ricky), keep him breathing,
            // Ricky's answer, "Weapons check before I go." (the skip offer at the left hand only from then, still up at step 1).
            int head0 = fromHead.Count, radio0 = radio.Count, caps0 = captions.Count;
            bool s0 = await UntilStep(0, 10);
            string skipLabel = !s0 ? "no step 0" : MarshalBriefing.Label != null ? $"\"{MarshalBriefing.Label}\" up before a word was said" : "never up";
            bool skipSeen = false;
            bool rickyLabel = false, ortizLabel = false; string rickyLabelInfo = "never up", ortizLabelInfo = "never up";
            bool s1 = await WaitFor(() =>
            {
                Poll();
                if (!ortizLabel && MarshalBriefing.SideLabel == MarshalBriefing.WOrtiz && ortiz != null)
                {
                    var wrong = LabelWrong(MarshalBriefing.WOrtiz, ortiz.HandoverHeadPoint + Vector3.up * 0.15f, 0.3f, slot: 1);
                    if (wrong == null && MarshalBriefing.LineKey != "vo.deputy.handover.hello") wrong = $"up on \"{MarshalBriefing.Line}\" (want: with his hello)";
                    ortizLabel = wrong == null; ortizLabelInfo = Ok(wrong);
                }
                if (!rickyLabel && MarshalBriefing.SideLabel == MarshalBriefing.WRicky)
                {
                    var wrong = LabelWrong(MarshalBriefing.WRicky, ricky.ProtecteeHeadPoint, 0.3f, slot: 1);
                    rickyLabel = wrong == null; rickyLabelInfo = Ok(wrong);
                }
                if (!skipSeen && s0 && skipLabel == "never up" && MarshalBriefing.Label == MarshalBriefing.WSkip)
                {
                    skipSeen = true;
                    skipLabel = !Said(0, "Ortiz", "vo.deputy.handover.check") ? "up before \"Weapons check before I go.\""
                              : LabelWrong(MarshalBriefing.WSkip, cs.Left.transform.position, 0.02f);
                }
                return MarshalBriefing.Step == 1;
            }, 30);
            if (s1 && skipLabel == null && MarshalBriefing.Label != MarshalBriefing.WSkip) skipLabel = "gone at step 1 (want it through step 1)";
            var order = new List<string>();
            foreach (var h in heard) if (h.step == 0) order.Add($"{h.who}{(h.how != "" ? $" ({h.how})" : "")}: {h.text}");
            string[] want0 = { "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.hello"), "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.ricky"),
                               "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.breathing"), "Ricky: " + VoiceLines.Text(IntruderT.RickyNewGuyKey),
                               "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.check") };
            bool inOrder = order.Count == want0.Length;
            for (int i = 0; inOrder && i < want0.Length; i++) inOrder = order[i] == want0[i];
            int onRadio0 = radio.Count - radio0;                           // (any transmission at all since the card: none wanted)
            int ortizCaps = 0;
            for (int i = caps0; i < captions.Count; i++) if (Array.Exists(want0, w => w.EndsWith(": " + captions[i]) && w.StartsWith("Ortiz"))) ortizCaps++;
            string head0Wrong = HeadWrong(head0);
            Step(s0 && s1 && inOrder && onRadio0 == 0 && ortizCaps >= 4 && head0Wrong == null && skipLabel == null && rickyLabel && ortizLabel,
                 "0 the handover IN PERSON: from his head, captioned Ortiz, nothing on the radio; his name, then Ricky's",
                 $"lines [{string.Join(" | ", order)}], radio transmissions {onRadio0}, Ortiz captions {ortizCaps}, voices: {Ok(head0Wrong)} ({HeadInfo(head0)}), " +
                 $"skip label {Ok(skipLabel)}, \"Deputy Ortiz\" label {ortizLabelInfo}, \"Ricky\" label {rickyLabelInfo}");

            // ── 4. Practice hits while it runs: on Ricky "Hey! Watch it!" once (nothing more); on Ortiz a round, a blade and a fist
            // do nothing but "Whoa! Watch it!" once — no damage, no force reported, no verdict.
            int practice0 = ricky.ProtecteePracticeHits, watch0 = watchIts;
            float shotDmg = HitRicky(ricky, BodyPart.Torso, DamageType.Bullet, 60f, pistol.gameObject);
            float stabDmg = HitRicky(ricky, BodyPart.Abdomen, DamageType.Stab, 40f, knife.gameObject);
            await Wait(0.4f);
            MarshalCheck(shotDmg == 0f && stabDmg == 0f && ricky.Health == rickyHealth0 && ricky.Current != IntruderT.State.Dead
                         && ricky.ProtecteePracticeHits == practice0 + 2 && watchIts == watch0 + 1 && rickyForce == 0,
                  "weapons check: a practice round or blade on Ricky does nothing but \"Hey! Watch it!\"",
                  $"damage {shotDmg}/{stabDmg}, health {ricky.Health:F0} (was {rickyHealth0:F0}), {ricky.Current}, practice hits {ricky.ProtecteePracticeHits - practice0}, " +
                  $"\"Watch it\" {watchIts - watch0}× (\"{VoiceLines.Text(IntruderT.RickyWatchItKey)}\"), force reported {rickyForce}");
            if (ortiz != null)
            {
                float oHealth0 = ortiz.Health;
                int oPractice0 = ortiz.HandoverPracticeHits, oWatch0 = ortizWatchIts, v0 = verdicts;
                float oShot = HitMan(ortiz, BodyPart.Torso, DamageType.Bullet, 60f, pistol.gameObject);
                float oStab = HitMan(ortiz, BodyPart.Abdomen, DamageType.Stab, 40f, knife.gameObject);
                float oFist = HitMan(ortiz, BodyPart.Head, DamageType.Blunt, 15f, right.gameObject);
                await Wait(0.4f);
                MarshalCheck(oShot == 0f && oStab == 0f && oFist == 0f && ortiz.Health == oHealth0 && ortiz.Current == IntruderT.State.Idle
                             && ortiz.HandoverPracticeHits == oPractice0 + 3 && ortizWatchIts == oWatch0 + 1 && ortizForce == 0 && verdicts == v0,
                      "handover: a practice round, blade or fist on Ortiz does nothing but \"Whoa! Watch it!\" (no damage, no verdict)",
                      $"damage {oShot}/{oStab}/{oFist}, health {ortiz.Health:F0} (was {oHealth0:F0}), {ortiz.Current}, practice hits {ortiz.HandoverPracticeHits - oPractice0}, " +
                      $"\"{VoiceLines.Text(IntruderT.HandoverWatchItKey)}\" {ortizWatchIts - oWatch0}×, force reported {ortizForce}, verdicts {verdicts - v0}");
            }
            else MarshalCheck(false, "handover: a practice round, blade or fist on Ortiz does nothing but \"Whoa! Watch it!\" (no damage, no verdict)", "no Ortiz");

            // ── 5. Steps 1–8, his lines in person.
            int head1 = fromHead.Count;
            // 1: "Stand up straight for me." — still and level for 1.5 s; the height measure runs again. No label.
            float t1 = Time.time;
            bool line1 = Said(1, "Ortiz", "vo.deputy.handover.stand");
            bool s2 = await UntilStep(2, 8);
            float stood = Time.time - t1;
            Step(line1 && s2 && stood >= 0.5f && !body.Measuring, "1 stand up straight (re-measured)",   // (1.5 s still; ~0.8 s went on the practice hits)
                 $"line {line1}, {stood:F1} s, measuring {body.Measuring}, eyes {body.MeasuredEye:F2} m; {Where()}");

            // 2: "Sidearm, right hip. Draw it." — "Squeeze grip" at the holstered pistol; drawn by an empty hand's squeeze.
            bool line2 = await Hear(2, "vo.deputy.handover.draw");
            string label2 = LabelWrong(MarshalBriefing.WGrip, pistol.Grabbable.GripPoint.position, 0.02f);
            bool waited = await Waits(2, 0.8f);
            right.transform.rotation = Quaternion.identity;
            HandAt(right, pistol.Grabbable.GripPoint.position);
            Grip(right);
            bool drawn = pistol.IsHeld && pistol.Hand == right;
            bool s3 = await UntilStep(3, 3);
            Step(line2 && label2 == null && waited && drawn && s3, "2 sidearm: right hip, squeeze grip",
                 $"line {line2}, label {Ok(label2)}, waited {waited}, drawn {drawn} ({(right.Held != null ? right.Held.name : "nothing")} in the hand); {Where()}");

            // 3: "Put three in the target." — "Trigger" over the target; three practice rounds: the slide locks back. Two aren't enough.
            bool line3 = await Hear(3, "vo.deputy.handover.three");
            string want3 = MarshalBriefing.Label == MarshalBriefing.WTriggerDa ? MarshalBriefing.WTriggerDa : MarshalBriefing.WTrigger;
            string label3 = target != null ? LabelWrong(want3, target.LabelPoint, 0.02f) : "no target";
            bool above3 = target != null && MarshalBriefing.LabelObject(0) != null && MarshalBriefing.LabelObject(0).PlateCentre.y > target.LabelPoint.y;
            AimAt(right, pistol, chest, face);
            int shots0 = pistol.ShotsFired, hits0 = target != null ? target.RoundHits : 0;
            Pull(right, pistol); await Wait(0.35f);
            bool singleAfter = MarshalBriefing.Label == MarshalBriefing.WTrigger;  // (decocked: "the first pull is long" only until the first shot)
            Pull(right, pistol); await Wait(0.35f);
            bool notYet = MarshalBriefing.Step == 3;
            Pull(right, pistol);
            bool s4 = await UntilStep(4, 3);
            Step(line3 && label3 == null && above3 && singleAfter && notYet && s4 && pistol.SlideLockedBack && pistol.ShotsFired - shots0 == 3,
                 "3 three in the target: \"Trigger\" over it, the slide locks",
                 $"line {line3}, label {Ok(label3)} (\"{want3.Replace("\n", " / ")}\", above the board {above3}, plain after the first shot {singleAfter}), " +
                 $"waited after two {notYet}, {pistol.ShotsFired - shots0} fired, {(target != null ? target.RoundHits - hits0 : 0)} on the target, locked {pistol.SlideLockedBack}; {Where()}");

            // 4: "You're dry. Reload." — "B: reload" at the gun (the right hand's B).
            bool line4 = await Hear(4, "vo.deputy.handover.dry");
            string label4 = LabelWrong(MarshalBriefing.WReloadB, pistol.transform.position, 0.02f);
            waited = await Waits(4, 0.6f);
            Press(right, pistol, "SecondaryButtonDown");
            bool s5 = await UntilStep(5, 4);
            Step(line4 && label4 == null && waited && s5 && pistol.RoundChambered && !pistol.SlideLockedBack, "4 dry: B reloads",
                 $"line {line4}, label {Ok(label4)}, waited {waited}, {pistol.Status}; {Where()}");

            // 5: "Holster it." — "Let go of grip here" at the right hip; back on the hip, let go.
            bool line5 = await Hear(5, "vo.deputy.handover.holster");
            string label5 = LabelWrong(MarshalBriefing.WHolster, body.RightHip.transform.position, 0.02f);
            waited = await Waits(5, 0.5f);
            HandAt(right, body.RightHip.transform.position);
            Squeeze(right, 0f);
            bool s6 = await UntilStep(6, 3);
            Step(line5 && label5 == null && waited && s6 && pistol.Grabbable.CurrentHolster == body.RightHip, "5 holster it: back on the hip, let go",
                 $"line {line5}, label {Ok(label5)}, waited {waited}, holstered in {(pistol.Grabbable.CurrentHolster != null ? pistol.Grabbable.CurrentHolster.name : "nothing")}; {Where()}");

            // 6: "Long gun's on your shoulder." — the label in view, its arrow over the right shoulder; drawn by its stock end:
            // "One round." — "Trigger" over the target; fired: the shotgun "Pump it." — "Tap B: pump" (one pump), the AR-15
            // "Reload it." — "B: reload" (one reload), each at the gun.
            bool line6a = await Hear(6, "vo.deputy.handover.longgun");
            string label6a = LabelWrong(MarshalBriefing.WShoulder, body.Chest.position, 0.3f, beside: false);
            var zoneEnd = typeof(Grabbable).GetField("grabZoneEnd", MbNP)?.GetValue(longGun.Grabbable) as Transform;
            var stock = zoneEnd != null ? Vector3.Lerp(longGun.Grabbable.GripPoint.position, zoneEnd.position, 0.85f) : longGun.Grabbable.GripPoint.position;
            right.transform.rotation = Quaternion.identity;
            HandAt(right, stock);
            Grip(right);
            bool slung = longGun.IsHeld && longGun.Hand == right;
            bool line6b = await Hear(6, "vo.deputy.handover.oneround");
            AimAt(right, longGun, chest, face);
            await Wait(0.15f);
            string label6b = target != null ? LabelWrong(MarshalBriefing.WTrigger, target.LabelPoint, 0.02f) : "no target";
            int lg0 = longGun.ShotsFired;
            Press(right, longGun, "TriggerDown");
            string afterKey = rifle ? "vo.deputy.handover.reloadit" : "vo.deputy.handover.pump";
            bool line6c = await Hear(6, afterKey);
            var after = MarshalBriefing.LongGunAfterShot(longGun, leftHand: false);
            string label6c = LabelWrong(after.label, longGun.Grabbable.GripPoint.position, 0.02f);
            waited = await Waits(6, 0.5f);                                   // one shot is not enough
            bool fired = longGun.ShotsFired == lg0 + 1;
            Press(right, longGun, "SecondaryButtonDown");                    // shotgun: the tap pumps; rifle: B reloads
            bool s7 = await UntilStep(7, rifle ? 8 : 3);
            Step(line6a && label6a == null && slung && line6b && label6b == null && fired && line6c && after.key == afterKey && label6c == null && waited && s7 && longGun.RoundChambered,
                 rifle ? "6 rifle: over the shoulder, one shot, B reloads" : "6 shotgun: over the shoulder, one shot, tap B to pump",
                 $"shoulder line {line6a} label {Ok(label6a)}, drawn {slung}, \"One round.\" {line6b} label {Ok(label6b)}, fired {fired}, " +
                 $"\"{VoiceLines.Text(afterKey)}\" {line6c} label {Ok(label6c)}, waited {waited}, {longGun.Status}; {Where()}");
            HandAt(right, body.BackSlot.transform.position);
            Squeeze(right, 0f);                                             // slung again (the door wants an empty hand)

            // 7: "Knife's on your left hip. Use it." — "Squeeze grip" at the sheathed knife, then "Stab or slash" over the target.
            bool line7 = await Hear(7, "vo.deputy.handover.knife");
            await Wait(0.15f);
            string label7a = LabelWrong(MarshalBriefing.WGrip, knife.Grabbable.GripPoint.position, 0.07f);   // (its arrow at the handle's end)
            left.transform.rotation = Quaternion.identity;
            HandAt(left, knife.Grabbable.GripPoint.position);
            Grip(left);
            bool knifeOut = knife.IsHeld && knife.Hand == left;
            waited = await Waits(7, 0.6f);
            string label7b = target != null ? LabelWrong(MarshalBriefing.WKnife, target.LabelPoint, 0.02f) : "no target";
            Collider blade = null;
            foreach (var c in knife.GetComponentsInChildren<Collider>()) if (c.isTrigger) { blade = c; break; }
            if (blade == null) blade = knife.GetComponentInChildren<Collider>();
            int stabs0 = target != null ? target.KnifeHits : 0;
            if (blade != null)
            {
                left.transform.rotation = Quaternion.LookRotation(-face, Vector3.up);   // the blade toward the board
                HandAt(left, left.Anchor.position + (target != null ? target.transform.position + Vector3.up * 1.13f : chest) - blade.bounds.center);
                SetHand(left, "Velocity", -face.normalized * 2.5f);                    // a 2.5 m/s thrust into its face
            }
            bool s8 = await UntilStep(8, 3);
            SetHand(left, "Velocity", Vector3.zero);
            Step(line7 && label7a == null && knifeOut && waited && label7b == null && s8 && target != null && target.KnifeHits > stabs0,
                 "7 knife: left hip, a blade lands on the target",
                 $"line {line7}, hip label {Ok(label7a)}, drawn {knifeOut}, waited {waited}, target label {Ok(label7b)}, blade {(blade != null ? blade.name : "none")}, " +
                 $"{(target != null ? target.KnifeHits - stabs0 : 0)} hit(s) ({(target != null && target.LastBladeWasStab ? "a stab" : "a slash")}); {Where()}");
            HandAt(left, body.LeftHip.transform.position);
            Squeeze(left, 0f);

            // 8: "Let me hear you call it." — "A / X: yell" at the right hand; A held, the stick flicked up: "U.S. Marshals! Show me your hands!"
            bool line8 = await Hear(8, "vo.deputy.handover.yell");
            string label8 = LabelWrong(MarshalBriefing.WYell, cs.Right.transform.position, 0.02f);
            waited = await Waits(8, 0.8f);
            SetHand(cs.Right, "Thumbstick", Vector2.zero);
            SetHand(cs.Right, "PrimaryButton", true);
            await Wait(0.45f);                                               // past the 0.3 s hold: the pick hint is up
            SetHand(cs.Right, "Thumbstick", new Vector2(0f, 1f));
            await Wait(0.2f);
            SetHand(cs.Right, "PrimaryButton", false);
            SetHand(cs.Right, "Thumbstick", Vector2.zero);
            bool s9 = await UntilStep(9, 3);
            string key = VoiceLines.KeyFor(cs.Last);
            Step(line8 && label8 == null && waited && s9 && cs.Last == Command.ShowHands && key == "vo.player.marshal.hands" && HIS.Core.Sfx.Has(key),
                 "8 call it: A held + a flick, the Marshal's yell",
                 $"line {line8}, label {Ok(label8)}, waited {waited}, yelled {cs.Last} → {key} (clip {HIS.Core.Sfx.Has(key)}); {Where()}");
            string head1Wrong = HeadWrong(head1);
            int radio18 = 0;
            foreach (var r in radio) if (r.step >= 1 && r.step <= 8) radio18++;
            MarshalCheck(head1Wrong == null && radio18 == 0 && ortiz != null && OrtizCount() == 1,
                  "handover: steps 1–8 in person (from his head, none on the radio), Ortiz still in the room",
                  $"voices: {Ok(head1Wrong)} ({HeadInfo(head1)}), radio transmissions {radio18}, Ortiz {OrtizCount()}");

            // ── 6. Step 9: "Get the door for me." in person — "Squeeze the handle, swing" at the handle; he waits for it; opened by
            // hand, he walks out through the doorway; through it, "Lock it behind me." in person and "Swing it shut" at the handle;
            // the step waits until it's latched shut behind him.
            int head9 = fromHead.Count;
            bool line9 = await Hear(9, "vo.deputy.handover.door");
            bool line9Person = SaidHow(9, "vo.deputy.handover.door", "in person");
            string label9a = LabelWrong(MarshalBriefing.WDoorOpen, door.HandleWorld + door.Inward * 0.04f, 0.02f);
            bool heWaits = await Waits(9, 0.6f) && ortiz != null && !ortiz.HandoverLeaving && ortiz.Current == IntruderT.State.Idle;
            right.transform.rotation = Quaternion.identity;
            HandAt(right, door.HandleWorld + door.Inward * 0.05f);
            Grip(right);
            bool took = door.IsHeldBy(right);
            // Swung wide (95°, the stop is ~100°: the integrator, 0.1.58 — at 80° the leaf and the old board's NavMesh holes once
            // boxed the doorway in and Ortiz was gone where he stood; PracticeTarget keeps a walkway now). He must still get out.
            SwingHand(right, door, 95f);
            bool opened = door.IsOpen && door.OpenedByPlayer;
            bool sent = await WaitFor(() => ortiz == null || ortiz.HandoverLeaving, 2);
            bool lockLine = await Hear(9, "vo.deputy.handover.lock", 15f);
            bool lockPerson = SaidHow(9, "vo.deputy.handover.lock", "in person");
            float outBy = ortiz != null ? Vector3.Dot(ortiz.transform.position - door.Threshold, door.Outward) : -9f;
            await Wait(0.1f);
            string label9b = LabelWrong(MarshalBriefing.WDoorShut, door.HandleWorld + door.Inward * 0.04f, 0.02f);
            bool waitsShut = await Waits(9, 0.4f);                           // him out, the door still open: not done
            if (!door.IsHeldBy(right)) { HandAt(right, door.HandleWorld + door.Inward * 0.05f); Grip(right); }
            SwingHand(right, door, -(door.OpenAngle + 5f));
            Squeeze(right, 0f);
            bool s10 = await UntilStep(10, 3);
            Step(line9 && line9Person && label9a == null && heWaits && took && opened && sent && lockLine && lockPerson && outBy > 0.3f && label9b == null
                 && waitsShut && s10 && !door.IsOpen && ortizStuck == 0,
                 "9 the door: he waits for it, opened by hand he walks out, \"Lock it behind me.\", shut behind him",
                 $"line {line9} in person {line9Person}, label {Ok(label9a)}, he waited {heWaits}, took the handle {took}, opened {opened}, he set off {sent}, " +
                 $"\"{VoiceLines.Text("vo.deputy.handover.lock")}\" {lockLine} in person {lockPerson} with him {outBy:F2} m out past the door, label {Ok(label9b)} then, " +
                 $"waited for the shut {waitsShut}, now {door.State}, stuck {ortizStuck}; {Where()}");

            // ── 7. Step 10 ON THE RADIO: "That screen's your porch camera." — "Porch camera" over the monitor (no monitor: on), then
            // "Menu pauses. Hold it to start over." — "Menu: pause" at the left hand; done; the sign-off on the radio.
            bool monPlaced = mon != null && mon.Placed;
            string label10a = null; bool line10a = true;
            if (monPlaced)
            {
                line10a = await Hear(10, "vo.deputy.radio.camera") && SaidHow(10, "vo.deputy.radio.camera", "radio");
                label10a = LabelWrong(MarshalBriefing.WMonitor, mon.ScreenCenter + Vector3.up * (mon.BezelSize.y * 0.5f) + mon.Facing * 0.01f, 0.02f);
            }
            bool line10b = await Hear(10, "vo.deputy.radio.menu", 10f) && SaidHow(10, "vo.deputy.radio.menu", "radio");
            await Wait(0.15f);
            string label10b = LabelWrong(MarshalBriefing.WMenu, cs.Left.transform.position, 0.02f);
            int radioBeforeDone = radio.Count;
            bool done = await UntilStep(-2, 10);
            const string SignOff = "Good. See you at two.";
            bool signOff = await WaitFor(() => radio.Count > radioBeforeDone && radio.Exists(r => r.text == SignOff), 3);
            Step(line10a && label10a == null && line10b && label10b == null && done && signOff && VoiceLines.Text("vo.deputy.radio.done") == SignOff
                 && MarshalBriefing.Completed == 1 && !MarshalBriefing.LastSkipped,
                 "10 on the radio: the porch camera, the Menu line, the check done, \"Good. See you at two.\"",
                 $"monitor {(monPlaced ? $"line {line10a} label {Ok(label10a)}" : "none (left out)")}, Menu line {line10b} label {Ok(label10b)}, done {done}, " +
                 $"sign-off \"{VoiceLines.Text("vo.deputy.radio.done")}\" on the radio {signOff}, completed {MarshalBriefing.Completed}");

            // After it: no target, no label, both guns full, nothing judged, nothing ever on the monitor, never more than two labels;
            // the same Ricky in his spot, unharmed; the 1:12 card; Ortiz walked off the porch (when there is one) and was removed.
            bool full = pistol.RoundChambered && pistol.RoundsInMag == pistol.FullLoad - 1 && longGun.RoundChambered && longGun.RoundsInMag == longGun.FullLoad - 1;
            bool card = await WaitFor(() => nights112 > nights0, 15);
            bool removed = await WaitFor(() => IntruderT.HandoverDeputy == null && OrtizCount() == 0, 15);
            bool porch = HIS.Security.PorchSet.For(door) != null;
            bool sameRicky = IntruderT.Protectee == ricky && ricky != null && ricky.Current != IntruderT.State.Dead;
            MarshalCheck(done && MarshalBriefing.Target == null && MarshalBriefing.LabelObject(0) == null && MarshalBriefing.LabelObject(1) == null && full
                         && !onMonitor && mostLabels <= 2 && !rickyGone && sameRicky && rickyForce == 0 && ricky.Health == rickyHealth0
                         && verdicts == verdicts0 && shows == shows0 && card && removed && (!porch || walkedOff > 0),
                  "weapons check: after it — no target, label or Ortiz (off the porch, removed), guns full, nothing judged, the same Ricky, the 1:12 card",
                  $"target {(MarshalBriefing.Target != null ? "up" : "gone")}, labels {(MarshalBriefing.LabelObject(0) != null || MarshalBriefing.LabelObject(1) != null ? "left behind" : "gone")}, " +
                  $"pistol {pistol.Status}, long gun {longGun.Status}, on the monitor {onMonitor}, most labels at once {mostLabels}, Ricky left his spot / swapped {rickyGone}, " +
                  $"same Ricky after {sameRicky}, force {rickyForce}, verdicts {verdicts - verdicts0}, shows {shows - shows0}, 1:12 card {card}, " +
                  $"Ortiz removed {removed} (walked off the porch {walkedOff}×{(porch ? "" : ", no porch here")})");

            // ── 8. Again from the row; Y on the rig's left hand (empty): a tap doesn't skip, a 1 s hold skips it all — "Alright. Lock
            // it behind me." in person, he leaves (the door pulled to behind him), the 1:12 card within the skip's 6 s, then he's gone.
            ricky.ForceApplied -= onRickyForce;
            if (ortiz != null) ortiz.ForceApplied -= onOrtizForce;
            int skips0 = MarshalBriefing.Skips, nightsBeforeSkip = nights112;
            pause.StartWeaponsCheck();
            bool again = await WaitFor(() => MarshalBriefing.Running, 20) && await UntilStep(0, 8);
            ricky = IntruderT.Protectee;
            ortiz = MarshalBriefing.Ortiz; ortizNow = ortiz;
            await Wait(0.5f);                                                // (into his hello)
            SetHand(cs.Left, "SecondaryButton", true); await Wait(0.4f); SetHand(cs.Left, "SecondaryButton", false);
            await Wait(0.3f);
            bool tapKept = MarshalBriefing.Running && MarshalBriefing.Skips == skips0;
            float held0 = Time.time;
            SetHand(cs.Left, "SecondaryButton", true);
            bool skipped = await WaitFor(() => MarshalBriefing.Skips == skips0 + 1, 4);
            float heldFor = Time.time - held0, skipAt = Time.time;
            SetHand(cs.Left, "SecondaryButton", false);
            bool skipLine = await Hear(MarshalBriefing.LeaveStep, "vo.deputy.handover.skip", 3f) && SaidHow(MarshalBriefing.LeaveStep, "vo.deputy.handover.skip", "in person");
            bool leaving = ortiz != null && ortiz.HandoverLeaving;
            bool skipCard = await WaitFor(() => nights112 > nightsBeforeSkip, MarshalBriefing.SkipLeaveSeconds + 3f);
            float cardAfter = nightAt.Count > 0 ? nightAt[nightAt.Count - 1] - skipAt : -1f;
            bool goneAfter = await WaitFor(() => IntruderT.HandoverDeputy == null && OrtizCount() == 0, 15);
            bool rickyAfterSkip = ricky != null && IntruderT.Protectee == ricky;
            MarshalCheck(again && tapKept && skipped && MarshalBriefing.LastSkipped && heldFor >= 0.9f && skipLine && leaving && skipCard
                         && cardAfter <= MarshalBriefing.SkipLeaveSeconds + 1.5f && goneAfter && !MarshalBriefing.Running
                         && MarshalBriefing.Target == null && MarshalBriefing.LabelObject(0) == null && rickyAfterSkip && door.State == VirtualDoor.DoorState.Closed,
                  "weapons check: a tap of Y doesn't skip; holding Y 1 s skips it — \"Alright. Lock it behind me.\", he leaves, the card within 6 s",
                  $"started again {again}, still on after the tap {tapKept}, skipped {skipped} after {heldFor:F1} s held, skip line in person {skipLine}, he leaves {leaving}, " +
                  $"1:12 card {skipCard} {cardAfter:F1} s after the skip, Ortiz removed {goneAfter}, target {(MarshalBriefing.Target != null ? "up" : "gone")}, " +
                  $"the same Ricky {rickyAfterSkip}, the door {door.State}");

            // ── After the handover Ricky is himself again: a hit on him is a real hit (his ForceApplied: the night's rule).
            if (ricky != null && !MarshalBriefing.Running)
            {
                rickyForce = 0;
                ricky.ForceApplied += onRickyForce;
                int practiceBefore = ricky.ProtecteePracticeHits;
                float hp0 = ricky.Health;
                float dmg = HitRicky(ricky, BodyPart.RightArm, DamageType.Blunt, 5f, right.gameObject);
                bool reported = await WaitFor(() => rickyForce > 0, 2);
                ricky.ForceApplied -= onRickyForce;
                MarshalCheck(dmg > 0f && reported && ricky.ProtecteePracticeHits == practiceBefore,
                      "weapons check: after it, a hit on Ricky is real again",
                      $"damage {dmg:F1} (health {hp0:F0} → {ricky.Health:F0}), force reported {reported}, practice hits {ricky.ProtecteePracticeHits - practiceBefore}");
            }
            else MarshalCheck(false, "weapons check: after it, a hit on Ricky is real again", $"Ricky {(ricky != null ? "there" : "none")}, check running {MarshalBriefing.Running}");

            // ── 9. The SHORT handover (no drill due): his four beats, then "Two o'clock. Lock it behind me."; only the two name
            // labels, no target; he opens the front door himself, walks out, the door shut behind him; the 1:12 card within ~20 s.
            {
                int short0 = MarshalBriefing.ShortDone, shorts0 = shortsRun, heardFrom = heard.Count, headFrom = fromHead.Count, opened0 = ortizOpened, pulled0 = ortizPulled;
                int nightsShort = nights112, stuck0 = ortizStuck, walked0 = walkedOff;
                MarshalBriefing.RequestHandover();
                dir.RestartMarshalNight();
                bool shortOn = await WaitFor(() => MarshalBriefing.Running && shortsRun > shorts0, 20);
                float shortFrom = Time.time;
                ortiz = MarshalBriefing.Ortiz; ortizNow = ortiz;
                ricky = IntruderT.Protectee;
                string spotWrong = OrtizSpotWrong(ortiz, door, ricky, null, MarshalBriefing.EyeAtStart, out var spotInfo);
                bool slot0Ever = false, ortizName = false, rickyName = false, targetEver = false;
                bool shortDone = await WaitFor(() =>
                {
                    if (MarshalBriefing.Running)
                    {
                        slot0Ever |= MarshalBriefing.Label != null;
                        targetEver |= MarshalBriefing.Target != null;
                        ortizName |= MarshalBriefing.SideLabel == MarshalBriefing.WOrtiz;
                        rickyName |= MarshalBriefing.SideLabel == MarshalBriefing.WRicky;
                        mostLabels = Math.Max(mostLabels, MarshalBriefing.LabelsUp);
                    }
                    return !MarshalBriefing.Running;
                }, 30);
                bool doorShutAtEnd = door.State == VirtualDoor.DoorState.Closed;
                bool cardS = await WaitFor(() => nights112 > nightsShort, 10);
                float cardIn = nightAt.Count > 0 ? nightAt[nightAt.Count - 1] - shortFrom : -1f;
                bool goneShort = await WaitFor(() => IntruderT.HandoverDeputy == null && OrtizCount() == 0, 15);
                var shortOrder = new List<string>();
                for (int i = heardFrom; i < heard.Count; i++) shortOrder.Add($"{heard[i].who}{(heard[i].how != "" ? $" ({heard[i].how})" : "")}: {heard[i].text}");
                string[] wantShort = { "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.hello"), "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.ricky"),
                                       "Ortiz (in person): " + VoiceLines.Text("vo.deputy.handover.breathing"), "Ricky: " + VoiceLines.Text(IntruderT.RickyNewGuyKey),
                                       "Ortiz (in person): " + VoiceLines.Text("vo.deputy.leave") };
                bool shortInOrder = shortOrder.Count == wantShort.Length;
                for (int i = 0; shortInOrder && i < wantShort.Length; i++) shortInOrder = shortOrder[i] == wantShort[i];
                string headShort = HeadWrong(headFrom);
                MarshalCheck(shortOn && !MarshalBriefing.Drill && spotWrong == null && shortInOrder && headShort == null && !slot0Ever && !targetEver && ortizName && rickyName
                             && shortDone && MarshalBriefing.ShortDone == short0 + 1 && ortizOpened > opened0 && ortizPulled > pulled0 && doorShutAtEnd
                             && cardS && cardIn <= 22f && goneShort && ortizStuck == stuck0 && (!porch || walkedOff > walked0)
                             && MarshalBriefing.LabelObject(0) == null && MarshalBriefing.LabelObject(1) == null,
                      "handover (short): in person, his name then Ricky's, no target; he opens the door himself, out, shut behind him; the card within ~20 s",
                      $"started {shortOn} (drill {MarshalBriefing.Drill}), spot {Ok(spotWrong)} ({spotInfo}), lines [{string.Join(" | ", shortOrder)}], voices {Ok(headShort)} ({HeadInfo(headFrom)}), " +
                      $"a step label {slot0Ever}, a target {targetEver}, \"Deputy Ortiz\" {ortizName}, \"Ricky\" {rickyName}, done {shortDone}, opened it himself {ortizOpened - opened0}, " +
                      $"pulled it to {ortizPulled - pulled0}, the door {(doorShutAtEnd ? "shut" : "open")} at the end, the 1:12 card {cardS} {cardIn:F1} s after it began, " +
                      $"Ortiz removed {goneShort}, stuck {ortizStuck - stuck0}, walked off {walkedOff - walked0}×");
            }

            // The short one skipped: Y held during his hello — "Alright. Lock it behind me.", he goes, the card within the skip's 6 s.
            {
                int skipsS = MarshalBriefing.Skips, shortsS = shortsRun, nightsS = nights112;
                MarshalBriefing.RequestHandover();
                dir.RestartMarshalNight();
                bool on = await WaitFor(() => MarshalBriefing.Running && shortsRun > shortsS && MarshalBriefing.Step == 0, 20);
                ortiz = MarshalBriefing.Ortiz; ortizNow = ortiz;
                await Wait(0.6f);
                SetHand(cs.Left, "SecondaryButton", true);
                bool sk = await WaitFor(() => MarshalBriefing.Skips == skipsS + 1, 4);
                float at = Time.time;
                SetHand(cs.Left, "SecondaryButton", false);
                bool line = await Hear(MarshalBriefing.LeaveStep, "vo.deputy.handover.skip", 3f);
                bool goes = ortiz != null && ortiz.HandoverLeaving;
                bool cardK = await WaitFor(() => nights112 > nightsS, MarshalBriefing.SkipLeaveSeconds + 3f);
                float cardAfterK = nightAt.Count > 0 ? nightAt[nightAt.Count - 1] - at : -1f;
                bool gone = await WaitFor(() => IntruderT.HandoverDeputy == null && OrtizCount() == 0, 15);
                MarshalCheck(on && sk && MarshalBriefing.LastSkipped && line && goes && cardK && cardAfterK <= MarshalBriefing.SkipLeaveSeconds + 1.5f && gone && !MarshalBriefing.Running,
                      "handover (short): held Y skips it too — the skip line, he goes, the card within 6 s",
                      $"started {on}, skipped {sk}, \"{VoiceLines.Text("vo.deputy.handover.skip")}\" {line}, he leaves {goes}, the card {cardK} {cardAfterK:F1} s after the skip, Ortiz removed {gone}");
            }

            // ── 10. The session rule, as on the headset: a switch into Marshal owes the handover (short: the drill isn't due in the
            // Editor); the next night — a restart — has none; switching modes and back gets it again.
            MarshalBriefing.TestSessionRule = true;
            {
                dir.SetMode(ScenarioDirector.Mode.Scenarios);
                bool notOwedOutside = !MarshalBriefing.SessionOwed;
                int sh0 = shortsRun, dr0 = checksRun;
                dir.SetMode(ScenarioDirector.Mode.Marshal);
                bool owed = MarshalBriefing.SessionOwed && MarshalBriefing.Due;
                bool first = await WaitFor(() => shortsRun > sh0 && MarshalBriefing.Running, 30);
                bool consumed = !MarshalBriefing.SessionOwed;
                if (first) MarshalBriefing.TestSkip();                         // (its own run is section 9's: on to the card)
                int nA = nights112;
                bool firstCard = await WaitFor(() => nights112 > nA, 15);
                int shAfter = shortsRun, drAfter = checksRun, nB = nights112;
                dir.RestartMarshalNight();                                     // the next night of the same session
                bool secondCard = await WaitFor(() => nights112 > nB, 30);
                bool noSecond = shortsRun == shAfter && checksRun == drAfter && OrtizCount() == 0;
                dir.SetMode(ScenarioDirector.Mode.Scenarios);
                int sh1 = shortsRun;
                dir.SetMode(ScenarioDirector.Mode.Marshal);
                bool backAgain = await WaitFor(() => shortsRun > sh1 && MarshalBriefing.Running, 30);
                if (backAgain) MarshalBriefing.TestSkip();
                await WaitFor(() => !MarshalBriefing.Running, MarshalBriefing.SkipLeaveSeconds + 3f);
                MarshalCheck(notOwedOutside && owed && first && consumed && firstCard && secondCard && noSecond && backAgain && checksRun == dr0,
                      "handover: the session rule — into Marshal it opens the night; the next night has none; modes and back, again",
                      $"owed in Scenarios {!notOwedOutside}, owed on the switch {owed}, the first night's {first} (then owed {!consumed}), its card {firstCard}, " +
                      $"the next night's card {secondCard} with no handover {noSecond}, back into Marshal: again {backAgain}, drills {checksRun - dr0}");
            }
            MarshalBriefing.TestSessionRule = false;

            MarshalCheck(failures == 0, "weapons check: no brain failure or exception", failures == 0 ? "none" : $"{failures}, first: {firstFailure}");
            Debug.Log("[HIS] SCENARIO weapons check steps: " + string.Join(" | ", steps));
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            MarshalBriefing.TestSessionRule = false;
            if (ricky != null) ricky.ForceApplied -= onRickyForce;
            if (ortiz != null) ortiz.ForceApplied -= onOrtizForce;
            SetHand(cs.Left, "SecondaryButton", false); SetHand(cs.Right, "PrimaryButton", false); SetHand(cs.Right, "Thumbstick", Vector2.zero);
            cs.Left.enabled = leftWas; cs.Right.enabled = rightWas;
            UnityEngine.Object.Destroy(right.gameObject); UnityEngine.Object.Destroy(left.gameObject);
            cam.transform.localPosition = camWas;
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }
    }
}
