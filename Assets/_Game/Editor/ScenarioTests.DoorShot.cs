using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Shooting through the closed door (Brett 0.1.17: "when i shoot through door he'll kick it in anyway and wont react").
/// The player's real guns — the Glock, the M9 and the shotgun, instantiated from the loadout's own prefabs — are fired
/// through Firearm's own shot (TryFire → FireRay → VirtualDoor.RaycastLeaf → the round carries on behind the leaf →
/// Hitbox → Intruder), the muzzle posed where a standing player holds it and aimed at a point on the leaf:
///   phases — waiting outside (State.Outside), pounding, each kick's wind-up, between kicks, per kind of man (the fighters:
///            fists, knife, armed robber; the burglar; the quiet entries at the handle);
///   aim    — centre, latch edge, hinge edge, low, from straight on and from off to either side (while he pounds).
/// A round that reaches him must get a reaction (the scream through the leaf, then ShotAtTheDoor's thud and run / slide
/// down, or his death) and he must never come in (no Entering, the door never bursts for a live man) — every phase run and
/// every sweep round that reaches him counts. A sweep round that passes him by is a MISS row (listed, not counted: where
/// the zero-spread ray went and how far from his chest it passed); so is the same sweep with him pounding at the leaf
/// (proposed fix 3, emulated) and the varied-entries door (another workflow's, in progress).
/// The shot is the real one; only the trigger/ammo state is set by reflection (chambered, no fire-rate wait), and the
/// spread is seeded per shot so every run fires the same pellets.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunDoorShotHeadless  → [HIS] DOORSHOT lines + a table at the end.
/// </summary>
public static partial class ScenarioTests
{
    /// <summary>Batch entry: scripts/unity-batch.sh ScenarioTests.RunDoorShotHeadless.</summary>
    public static void RunDoorShotHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 1800f;
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
                // The game's door (varied entries), pinned to the classic pound-and-kick so every phase below comes round.
                ScenarioDirector.VariedEntries = true; ScenarioDirector.PinnedStyle = EntryStyle.Classic;
                HIS.Intruder.Intruder.StreetApproach = false; // the sweep is about the door itself: he starts at it
                HIS.Intruder.Hitbox.LethalRounds = false;     // and about graded hits through the leaf (grazes, give-ups)
                PoliceResponse.Enabled = false;               // (verdicts right away)
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await DoorShotChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally
            {
                ScenarioDirector.WaitForForced = false;
                ScenarioDirector.PinnedStyle = null;
                Application.logMessageReceived -= DsOnLog;
                _running = false;
                Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed");
            }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    // ───────────────────────────── the timeline under test (Intruder.ForcedEntry) ─────────────────────────────
    // Pounding: 3 blows 0.55 s apart, then 0.4 s; kick k starts at 2.05 + k·(KickLength + 0.25) s after BreakIn, lands
    // KickContact later. The kick in progress is read back (reflection: _kicksLanded) and printed with every shot.
    const float DsFirstKick = 3 * 0.55f + 0.4f;
    static float DsKickStart(int k) => DsFirstKick + k * (HIS.Intruder.IntruderClips.KickLength + 0.25f);
    static float DsWindUp(int k) => DsKickStart(k) + 0.35f;                                          // before the foot lands
    static float DsAfterKick(int k) => DsKickStart(k) + HIS.Intruder.IntruderClips.KickContact + 0.45f; // recovering, between kicks

    /// <summary>A phase: seconds after BreakIn (negative = seconds after he spawned, still in State.Outside).</summary>
    struct DsPhase { public string Name; public float At; public DsPhase(string n, float at) { Name = n; At = at; } }

    static readonly DsPhase[] DsForcedPhases =
    {
        new("waiting outside", -1.5f),
        new("pounding", 0.8f),
        new("kick 1 wind-up", DsWindUp(0)),
        new("after kick 1", DsAfterKick(0)),
        new("kick 2 wind-up", DsWindUp(1)),
        new("after kick 2", DsAfterKick(1)),
        new("kick 3 wind-up", DsWindUp(2)),
    };

    // ───────────────────────────── guns, aim points, where the player stands ─────────────────────────────
    sealed class DsGun { public string Name; public Firearm Gun; }
    struct DsAim { public string Name; public Vector3 Point; }
    struct DsFrom { public string Name; public Vector3 Muzzle; }

    static readonly BindingFlags DsNP = BindingFlags.Instance | BindingFlags.NonPublic;
    static MethodInfo s_dsTryFire;
    static FieldInfo s_dsNextShot, s_dsReloadUntil, s_dsKicksLanded, s_dsKicking;
    static PropertyInfo s_dsChambered, s_dsLocked;
    static int s_dsSeed = 1000;

    static List<DsGun> DsMakeGuns()
    {
        var guns = new List<DsGun>();
        var lo = Loadout.Instance;
        if (lo == null) return guns;
        foreach (var (slot, id, label) in new[] { (LoadoutSlot.Sidearm, "glock19", "Glock"), (LoadoutSlot.Sidearm, "m9", "M9"), (LoadoutSlot.LongGun, "r870", "Shotgun") })
        {
            int s = lo.SlotIndex(slot);
            var opt = s >= 0 ? Array.Find(lo.Slots[s].Options, o => o.Id == id) : null;
            if (opt == null || opt.Prefab == null) { Debug.LogWarning($"[HIS] DOORSHOT no {label} in the loadout"); continue; }
            var w = UnityEngine.Object.Instantiate(opt.Prefab);
            w.name = "DoorShot_" + label;
            if (w.TryGetComponent<Rigidbody>(out var rb)) { rb.isKinematic = true; rb.detectCollisions = false; } // held still, not in a holster
            var f = w.GetComponent<Firearm>();
            if (f != null) guns.Add(new DsGun { Name = label, Gun = f });
        }
        return guns;
    }

    /// <summary>The leaf point at door-local (x, height above the sill) on its room face.</summary>
    static Vector3 DsLeafPoint(VirtualDoor door, float x, float height) =>
        door.transform.TransformPoint(new Vector3(x, door.Opening.yMin + height, 0f));

    static DsAim[] DsAims(VirtualDoor door)
    {
        var o = door.Opening;
        float s = Vector3.Dot(door.HingeToLatch, door.transform.right) >= 0f ? 1f : -1f; // +1: the latch is at xMax
        float latch = s > 0f ? o.xMax : o.xMin, hinge = s > 0f ? o.xMin : o.xMax;
        return new[]
        {
            new DsAim { Name = "centre", Point = DsLeafPoint(door, o.center.x, o.height * 0.5f) },
            new DsAim { Name = "latch edge", Point = DsLeafPoint(door, latch - s * 0.1f, 1.1f) },
            new DsAim { Name = "hinge edge", Point = DsLeafPoint(door, hinge + s * 0.1f, 1.1f) },
            new DsAim { Name = "low", Point = DsLeafPoint(door, o.center.x, 0.45f) },
        };
    }

    static DsAim DsChestAim(VirtualDoor door) => new() { Name = "chest high", Point = DsLeafPoint(door, door.Opening.center.x, 1.35f) };

    /// <summary>A standing player's muzzle (1.4 m up) <paramref name="degrees"/> off the door's axis (+ toward the latch), at the
    /// first of 2.4 / 1.8 / 1.3 m with a clear line to the door's centre and not inside furniture.</summary>
    static DsFrom? DsFromAngle(VirtualDoor door, float degrees, string name)
    {
        var o = door.Opening;
        var sill = DsLeafPoint(door, o.center.x, 0f);
        var latchDir = door.HingeToLatch; latchDir.y = 0f; latchDir.Normalize();
        var inward = door.Inward; inward.y = 0f; inward.Normalize();
        var dir = (inward * Mathf.Cos(degrees * Mathf.Deg2Rad) + latchDir * Mathf.Sin(degrees * Mathf.Deg2Rad)).normalized;
        int room = LayerMask.GetMask("Room");
        var target = DsLeafPoint(door, o.center.x, o.height * 0.5f);
        foreach (float d in new[] { 2.4f, 1.8f, 1.3f })
        {
            var m = sill + dir * d + Vector3.up * 1.4f;
            var to = target - m;
            if (Physics.CheckSphere(m, 0.1f, room, QueryTriggerInteraction.Ignore)) continue;
            if (Physics.Raycast(m, to.normalized, to.magnitude - 0.03f, room, QueryTriggerInteraction.Ignore)) continue;
            return new DsFrom { Name = $"{name} {d:F1} m", Muzzle = m };
        }
        Debug.LogWarning($"[HIS] DOORSHOT no clear spot {name}");
        return null;
    }

    /// <summary>The real shot: the gun posed with its muzzle at <paramref name="muzzle"/> looking at <paramref name="aim"/>, ready to
    /// fire (a round chambered, no fire-rate wait), then Firearm.TryFire — the same call the trigger makes.</summary>
    static void DsFire(Firearm gun, Vector3 muzzle, Vector3 aim)
    {
        var m = gun.Muzzle;
        var t = gun.transform;
        t.rotation = Quaternion.FromToRotation(m.forward, (aim - muzzle).normalized) * t.rotation;
        t.position += muzzle - m.position;
        s_dsChambered.SetValue(gun, true);
        s_dsLocked.SetValue(gun, false);
        s_dsNextShot.SetValue(gun, 0f);
        s_dsReloadUntil.SetValue(gun, 0f);
        var saved = UnityEngine.Random.state;
        UnityEngine.Random.InitState(s_dsSeed++);
        s_dsTryFire.Invoke(gun, new object[] { null });
        UnityEngine.Random.state = saved;
    }

    static Transform DsBone(IntruderT i, HumanBodyBones b)
    {
        var a = i != null ? i.GetComponentInChildren<Animator>() : null;
        return a != null ? a.GetBoneTransform(b) : null;
    }

    /// <summary>The zero-spread ray, traced the way FireRay traces it: what it meets first, the leaf, what it meets behind the
    /// leaf; how far it passes from his chest; how deep his chest is behind the leaf's face.</summary>
    static string DsTrace(Vector3 o, Vector3 aim, IntruderT i)
    {
        var dir = (aim - o).normalized;
        int mask = ~0, wl = LayerMask.NameToLayer("Weapon"), dl = LayerMask.NameToLayer("Door");
        if (wl >= 0) mask &= ~(1 << wl);
        if (dl >= 0) mask &= ~(1 << dl);
        var door = VirtualDoor.Instance;
        bool struck = Physics.Raycast(o, dir, out var h, 60f, mask, QueryTriggerInteraction.Ignore);
        string path;
        if (door != null && door.RaycastLeaf(o, dir, struck ? h.distance : 60f, out var leaf))
        {
            bool s2 = Physics.Raycast(leaf.ExitPoint + dir * 0.002f, dir, out var h2, 60f - leaf.ExitDistance, mask, QueryTriggerInteraction.Ignore);
            path = s2 ? $"leaf→{h2.collider.name}" : "leaf→nothing";
        }
        else path = struck ? $"stopped by '{h.collider.name}' before the leaf" : "missed the leaf";
        var chest = DsBone(i, HumanBodyBones.Chest);
        if (chest == null || door == null) return path;
        var v = chest.position - o;
        float miss = (v - dir * Vector3.Dot(v, dir)).magnitude;
        float depth = Vector3.Dot(chest.position - door.transform.TransformPoint(Vector3.zero), -door.Inward);
        var root = i.transform.position;
        float rootDepth = Vector3.Dot(root - door.transform.TransformPoint(Vector3.zero), -door.Inward);
        return $"{path}; passes {miss:F2} m from his chest; chest {depth:F2} m / feet {rootDepth:F2} m behind the leaf";
    }

    static string DsKicks(IntruderT i)
    {
        if (i == null || s_dsKicksLanded == null) return "?";
        return $"kicks landed {(int)s_dsKicksLanded.GetValue(i)}{((bool)s_dsKicking.GetValue(i) ? ", kicking" : "")}";
    }

    // ───────────────────────────── what he does after it ─────────────────────────────
    static readonly List<(float t, string msg)> s_dsLog = new();
    static void DsOnLog(string msg, string stack, LogType type)
    {
        if (msg.StartsWith("[HIS] Intruder") || msg.StartsWith("[HIS] Door") || msg.StartsWith("[HIS] Outcome") || msg.StartsWith("[HIS] Force"))
            lock (s_dsLog) s_dsLog.Add((Time.time, msg));
    }
    static bool DsLogged(string fragment, float since) { lock (s_dsLog) return s_dsLog.Exists(l => l.t >= since && l.msg.Contains(fragment)); }
    /// <summary>Game seconds from <paramref name="since"/> to the first log line with <paramref name="fragment"/> (−1: none).</summary>
    static float DsLoggedAfter(string fragment, float since)
    {
        lock (s_dsLog) { int k = s_dsLog.FindIndex(l => l.t >= since && l.msg.Contains(fragment)); return k < 0 ? -1f : s_dsLog[k].t - since; }
    }

    sealed class DsWatch : IDisposable
    {
        readonly IntruderT _i;
        public readonly List<ForceEvent> Force = new();
        public readonly List<string> States = new();
        public float BreakInAt = -1f, SpawnedAt;
        public readonly int StartCompleted;
        /// <summary>Alive and in view (the door burst or opened on him and he came through): he came in.</summary>
        public bool Entered;
        public DsWatch(IntruderT i)
        {
            _i = i; SpawnedAt = Time.time; StartCompleted = ScenarioDirector.Instance.Completed;
            i.ForceApplied += OnForce; i.StateChanged += OnState;
        }
        void OnForce(IntruderT who, ForceEvent e) => Force.Add(e);
        void OnState(IntruderT.State s, IntruderT who)
        {
            States.Add(s.ToString());
            if (s == IntruderT.State.BreakIn && BreakInAt < 0f) BreakInAt = Time.time;
            if (s == IntruderT.State.Entering) Entered = true;
        }
        /// <summary>Sampled every poll: alive and in view = he came through (a man shot dead mid-kick who falls in doesn't count).</summary>
        public void Sample()
        {
            if (_i == null) return;
            if (_i.Current is not (IntruderT.State.Dead or IntruderT.State.Gone) && !_i.BehindDoor) Entered = true;
        }
        public void Dispose() { if (_i != null) { _i.ForceApplied -= OnForce; _i.StateChanged -= OnState; } }
    }

    /// <summary>The door's through-the-leaf sources (his voice, the thud/wood sounds): the clip keys heard on them.</summary>
    static void DsListen(HashSet<string> heard)
    {
        var door = VirtualDoor.Instance;
        if (door == null) return;
        foreach (var src in door.GetComponentsInChildren<AudioSource>())
        {
            if (src == null || !src.isPlaying || src.clip == null) continue;
            if (!src.name.StartsWith("Through")) continue;
            var n = src.clip.name; int u = n.LastIndexOf('_');
            heard.Add((src.name == "ThroughVoice" ? "voice:" : "sfx:") + (u > 0 ? n.Substring(0, u) : n));
        }
    }

    static async Task<bool> DsUntil(Func<bool> cond, float timeout)
    {
        float end = Time.realtimeSinceStartup + timeout;
        while (Time.realtimeSinceStartup < end)
        {
            if (!EditorApplication.isPlaying) return false;
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }

    // ───────────────────────────── one incident ─────────────────────────────
    sealed class DsRow
    {
        public string Who, Phase, Gun, Aim, From, Reached = "no", Cry = "-", Reaction = "-", CameIn = "no", Result, Why, Trace, Kicks;
        public bool Pass, Emulated;
    }
    static readonly List<DsRow> s_dsRows = new();
    static IntruderT s_dsLast;

    static async Task<(IntruderT, DsWatch)> DsStart(string scenario)
    {
        var dir = ScenarioDirector.Instance;
        ScenarioDirector.ForcedNext = scenario;
        bool ok = await WaitFor(() => dir.Record != null && dir.Record.Def.Name == scenario && dir.Intruder != null && dir.Intruder != s_dsLast
                                      && dir.Intruder.Current == IntruderT.State.Outside, 120);
        if (!ok) { ExtraFail("door shot: " + scenario, "never started"); return (null, null); }
        s_dsLast = dir.Intruder;
        return (dir.Intruder, new DsWatch(dir.Intruder));
    }

    /// <summary>Until the phase's moment (false: he's no longer at the door, or the moment never came).</summary>
    static async Task<bool> DsReach(IntruderT i, DsWatch w, DsPhase p)
    {
        if (p.At < 0f) return await DsUntil(() => i.Current != IntruderT.State.Outside || Time.time >= w.SpawnedAt - p.At, 10f) && i.Current == IntruderT.State.Outside;
        if (!await DsUntil(() => w.BreakInAt >= 0f, 15f)) return false;
        return await DsUntil(() => Time.time >= w.BreakInAt + p.At || !i.BehindDoor || i.Current is not IntruderT.State.BreakIn, 15f)
               && i.BehindDoor && i.Current == IntruderT.State.BreakIn;
    }

    /// <summary>Fire (then, if nothing reached him, the fallbacks), then watch what he does (<see cref="DsAfter"/>).</summary>
    static async Task<DsRow> DsShootAndWatch(IntruderT i, DsWatch w, string who, string phase, DsGun gun, DsFrom from, params DsAim[] aims)
    {
        var row = new DsRow { Who = who, Phase = phase, Gun = gun.Name, From = from.Name };
        int forceBefore = w.Force.Count;
        float shotAt = 0f;
        foreach (var aim in aims)
        {
            row.Aim = row.Aim == null ? aim.Name : row.Aim + " → " + aim.Name;
            row.Trace = DsTrace(from.Muzzle, aim.Point, i);
            row.Kicks = DsKicks(i);
            shotAt = Time.time;
            DsFire(gun.Gun, from.Muzzle, aim.Point);
            await DsUntil(() => Time.time > shotAt + 0.05f, 1f); // his LateUpdate resolves the frame's hits
            if (w.Force.Count > forceBefore || i.Current == IntruderT.State.Dead) break;
        }
        await DsAfter(row, i, w, forceBefore, shotAt);
        return row;
    }

    /// <summary>
    /// Watch him after the shot until he's gone, dead (+2 s), in, or 10 s went by, and fill the row: did the round reach him
    /// (a force event), did he cry out through the leaf (the door's voice source), thud / run (ShotAtTheDoor) or die, and did
    /// he come in. Pass = reached, reacted, never came in (and a cry for any hit of 5+ damage, ResolveHit's threshold).
    /// </summary>
    static async Task DsAfter(DsRow row, IntruderT i, DsWatch w, int forceBefore, float shotAt)
    {
        var heard = new HashSet<string>();
        float watchEnd = Time.time + 10f;
        await DsUntil(() =>
        {
            w.Sample();
            if (Time.time < shotAt + 2f) DsListen(heard);
            return w.Entered || i.Current == IntruderT.State.Gone || (i.Current == IntruderT.State.Dead && Time.time > shotAt + 2f) || Time.time > watchEnd;
        }, 30f);
        bool reached = w.Force.Count > forceBefore;
        float dmg = 0f; string part = null;
        for (int k = forceBefore; k < w.Force.Count; k++) { dmg += w.Force[k].Damage; part ??= w.Force[k].BodyPart; }
        bool dead = i.Current == IntruderT.State.Dead;
        bool ran = DsLogged("shot through the door", shotAt);
        var voices = new List<string>();
        foreach (var h in heard) if (h.StartsWith("voice:")) voices.Add(h.Substring(6));
        bool cried = voices.Exists(v => v.StartsWith("intruder."));
        bool thud = heard.Contains("sfx:door.pound") || heard.Contains("sfx:body.fall");
        row.Reached = reached ? $"{part} {dmg:F0}" : "no";
        row.Cry = cried ? string.Join("+", voices) : !reached ? "-" : dmg >= 5f ? "NONE" : "none (<5 dmg)";
        float after = DsLoggedAfter("shot through the door", shotAt);
        bool reeled = DsLogged("grazed through the door", shotAt); // the varied-entries path (IntruderEntries.ReactToDoorShot)
        row.Reaction = dead ? "dies" : ran ? (thud ? "thud + runs/slides" : "runs/slides, no thud heard") + $" {after:F2} s after the hit"
                     : reeled ? "reels into the leaf (\"grazed\"), then kicks on" : "none";
        row.CameIn = w.Entered ? "CAME IN" : "no";
        row.Pass = reached && !w.Entered && (dead || ran) && (cried || dmg < 5f);
        row.Why = !reached ? "the round never reached him" + (w.Entered ? ", and he came in anyway" : "")
                : w.Entered ? "hit, and came in anyway"
                : !(dead || ran) ? "hit, no reaction"
                : !(cried || dmg < 5f) ? "no scream through the door" : "ok";
        row.Result = row.Pass ? "PASS" : "FAIL";
    }

    /// <summary>End the incident fast: a live man is killed (the director ends it), the verdict show is skipped.</summary>
    static async Task DsEnd(IntruderT i, DsWatch w)
    {
        var dir = ScenarioDirector.Instance;
        w?.Dispose();
        if (i != null && i.Current is not (IntruderT.State.Dead or IntruderT.State.Gone)) i.Kill();
        if (w != null && !await WaitFor(() => dir.Completed > w.StartCompleted, 30)) Debug.LogWarning("[HIS] DOORSHOT incident never ended");
        float t0 = Time.time;
        for (int k = 0; k < 25; k++)
        {
            dir.RequestSkip();
            await Wait(0.2f);
            if (DsLogged("Outcome show skipped", t0)) break;
        }
    }

    static void DsReport(DsRow r, bool counted = true)
    {
        r.Emulated = !counted;
        s_dsRows.Add(r);
        string line = $"{r.Who} | {r.Phase} | {r.Gun} | {r.Aim} | {r.From} | reached {r.Reached} | cry {r.Cry} | {r.Reaction} | came in {r.CameIn} | {r.Kicks} | {r.Trace}";
        if (!counted) Debug.Log($"[HIS] DOORSHOT info {r.Result}: {(r.Pass ? "" : r.Why + " — ")}{line}");
        else if (r.Pass) ExtraPass("door shot", line); else ExtraFail("door shot", r.Why + " — " + line);
    }

    // ───────────────────────────── the checks ─────────────────────────────
    /// <summary>The Firearm internals the real-shot helper (DsFire) needs; false if they were renamed.</summary>
    static bool DsInit()
    {
        s_dsTryFire = typeof(Firearm).GetMethod("TryFire", DsNP);
        s_dsNextShot = typeof(Firearm).GetField("_nextShotTime", DsNP);
        s_dsReloadUntil = typeof(Firearm).GetField("_reloadUntil", DsNP);
        s_dsChambered = typeof(Firearm).GetProperty("RoundChambered");
        s_dsLocked = typeof(Firearm).GetProperty("SlideLockedBack");
        return s_dsTryFire != null && s_dsNextShot != null && s_dsReloadUntil != null && s_dsChambered != null && s_dsLocked != null;
    }

    static async Task DoorShotChecks()
    {
        s_dsTryFire = typeof(Firearm).GetMethod("TryFire", DsNP);
        s_dsNextShot = typeof(Firearm).GetField("_nextShotTime", DsNP);
        s_dsReloadUntil = typeof(Firearm).GetField("_reloadUntil", DsNP);
        s_dsChambered = typeof(Firearm).GetProperty("RoundChambered");
        s_dsLocked = typeof(Firearm).GetProperty("SlideLockedBack");
        s_dsKicksLanded = typeof(IntruderT).GetField("_kicksLanded", DsNP);
        s_dsKicking = typeof(IntruderT).GetField("_kicking", DsNP);
        if (s_dsTryFire == null || s_dsNextShot == null || s_dsReloadUntil == null || s_dsChambered == null || s_dsLocked == null)
        { ExtraFail("door shot setup", "Firearm internals renamed (TryFire/_nextShotTime/_reloadUntil/RoundChambered/SlideLockedBack)"); return; }
        Application.logMessageReceived -= DsOnLog; Application.logMessageReceived += DsOnLog;

        if (!await WaitFor(() => VirtualDoor.Instance != null && HIS.Intruder.DoorwayNav.Instance != null, 30)) { ExtraFail("door shot setup", "no door"); return; }
        await Wait(2f); // the hinge side is picked 1.5 s after the room loads
        // The red debug cube (1.5 m in front of the origin, with a collider) stops rounds in the Editor; a release APK has it
        // switched off (HISBuildProcessor), so the sweep runs without it, as on the headset.
        var sanity = GameObject.Find("[HIS] SanityCube");
        if (sanity != null) sanity.SetActive(false);
        var door = VirtualDoor.Instance;
        var guns = DsMakeGuns();
        if (guns.Count < 3) { ExtraFail("door shot setup", $"only {guns.Count} of the 3 guns"); }
        DsGun Gun(string n) => guns.Find(g => g.Name == n) ?? guns[0];
        var o = door.Opening;
        Debug.Log($"[HIS] DOORSHOT door {o.width:F2} x {o.height:F2} m, inward {door.Inward:F2}, latch toward {door.HingeToLatch:F2}");
        var straight = DsFromAngle(door, 0f, "straight on");
        var fromLatch = DsFromAngle(door, 25f, "25° latch side");
        var fromHinge = DsFromAngle(door, -45f, "45° hinge side");
        if (straight == null) { ExtraFail("door shot setup", "no clear spot straight in front of the door"); return; }
        var aims = DsAims(door);
        var centre = aims[0]; var low = aims[3]; var chest = DsChestAim(door);

        try
        {
            // ── 1) Every phase of a kicked-in entry, straight on at the centre (low, then chest high if that passes him by).
            var plan = new List<(string who, DsPhase phase, string gun)>();
            foreach (var p in DsForcedPhases) plan.Add(("Home invader", p, "M9"));       // a fighter (fists)
            foreach (var p in DsForcedPhases) plan.Add(("Burglar who runs", p, "Glock")); // gives up when shot
            plan.Add(("Knife attacker", DsForcedPhases[1], "Shotgun"));
            plan.Add(("Knife attacker", DsForcedPhases[4], "Shotgun"));
            plan.Add(("Armed robber", DsForcedPhases[1], "M9"));
            plan.Add(("Armed robber", DsForcedPhases[3], "M9"));
            plan.Add(("Wrong house", new DsPhase("at the handle", 0.8f), "Glock"));        // quiet entries (AtTheDoor)
            plan.Add(("Robber drops the gun", new DsPhase("keys at the door", 0.8f), "Shotgun"));
            foreach (var (who, phase, gunName) in plan)
            {
                if (!EditorApplication.isPlaying) return;
                var (i, w) = await DsStart(who);
                if (i == null) continue;
                try
                {
                    if (!await DsReach(i, w, phase)) { ExtraFail("door shot", $"{who} | {phase.Name}: never at that moment ({i.Summary}, states {string.Join(">", w.States)})"); continue; }
                    DsReport(await DsShootAndWatch(i, w, who, phase.Name, Gun(gunName), straight.Value, centre, low, chest));
                }
                finally { await DsEnd(i, w); }
            }

            // ── 1b) The varied-entries door (another workflow, in progress: ScenarioDirector.VariedEntries +
            // IntruderEntries), forced to its Classic style so the timeline matches. Informational rows (not counted);
            // set by reflection so this file compiles whatever that API ends up as.
            if (DsStyled(true))
            {
                try
                {
                    var styled = new List<(string who, DsPhase phase, string gun)>
                    {
                        ("Home invader", DsForcedPhases[0], "M9"), ("Home invader", DsForcedPhases[1], "M9"),
                        ("Home invader", DsForcedPhases[4], "M9"), ("Knife attacker", DsForcedPhases[1], "Shotgun"),
                        ("Armed robber", DsForcedPhases[3], "Glock"),
                    };
                    foreach (var (who, phase, gunName) in styled)
                    {
                        if (!EditorApplication.isPlaying) return;
                        DsStyled(true);
                        var (i, w) = await DsStart(who);
                        if (i == null) continue;
                        try
                        {
                            if (!await DsReach(i, w, phase)) { Debug.Log($"[HIS] DOORSHOT styled {who} | {phase.Name}: never at that moment ({i.Summary})"); continue; }
                            var row = await DsShootAndWatch(i, w, who, phase.Name + " [varied-entries Classic]", Gun(gunName), straight.Value, centre, low, chest);
                            DsReport(row, counted: false);
                        }
                        finally { await DsEnd(i, w); }
                    }
                }
                finally { DsStyled(false); }
            }

            // ── 2) Where on the door, from where, with what: every gun × aim point × spot while the burglar pounds (he stands
            // still then). Rounds that pass him by change nothing, so the next one goes in the same pounding; a hit ends it.
            var froms = new List<DsFrom> { straight.Value };
            if (fromLatch != null) froms.Add(fromLatch.Value);
            if (fromHinge != null) froms.Add(fromHinge.Value);
            var combos = new List<(DsGun gun, DsAim aim, DsFrom from)>();
            foreach (var f in froms) foreach (var a in aims) foreach (var g in guns) combos.Add((g, a, f));
            await DsSweep(combos, atLeaf: false);
            // ── 3) The same sweep with him pounding right at the leaf (0.5 m back, as a man pounding with his fist stands) —
            // what proposed fix 3 (Intruder.ForcedEntry: pound at the leaf, step back onto the kick mark for the kicks) would
            // give. Emulated here by moving the hidden man there at BreakIn; informational rows (not counted).
            await DsSweep(combos, atLeaf: true);
        }
        finally
        {
            foreach (var g in guns) if (g.Gun != null) UnityEngine.Object.Destroy(g.Gun.gameObject);
            DsTable();
        }
    }

    /// <summary>
    /// Every gun × aim point × spot while the burglar pounds (he stands still then). Rounds that pass him by change nothing,
    /// so the next one goes in the same pounding; a hit ends that incident. <paramref name="atLeaf"/>: he's moved to 0.5 m
    /// behind the leaf first (emulating proposed fix 3); those rows are informational.
    /// </summary>
    static async Task DsSweep(List<(DsGun gun, DsAim aim, DsFrom from)> combos, bool atLeaf)
    {
        int next = 0, runs = 0;
        while (next < combos.Count && runs++ < 40 && EditorApplication.isPlaying)
        {
            var (i, w) = await DsStart("Burglar who runs");
            if (i == null) break;
            try
            {
                if (!await DsReach(i, w, new DsPhase("pounding", 0.05f))) { ExtraFail("door shot", "burglar never pounding: " + i.Summary); continue; }
                var door = VirtualDoor.Instance;
                if (atLeaf && door != null)
                {
                    var inward = door.Inward; inward.y = 0f; inward.Normalize();
                    float behind = Vector3.Dot(door.transform.position - i.transform.position, inward); // 1.25 on the kick mark
                    i.transform.position += inward * Mathf.Max(0f, behind - 0.5f);
                }
                await DsUntil(() => Time.time >= w.BreakInAt + 0.3f, 3f); // (a physics step: the moved hitboxes are where he is)
                while (next < combos.Count && Time.time < w.BreakInAt + DsFirstKick - 0.2f && i.Current == IntruderT.State.BreakIn)
                {
                    var (g, a, f) = combos[next++];
                    var row = await DsShootAndWatchQuick(i, w, g, a, f, atLeaf);
                    if (row != null) { DsReport(row, counted: !atLeaf); break; } // it reached him: that incident is over
                    await Wait(0.1f);
                }
            }
            finally { await DsEnd(i, w); }
        }
    }

    /// <summary>
    /// The sweep's shot: a round that passes him by is logged as a miss row (FAIL: nothing reached him — on the headset he'd
    /// kick the door in as if nothing happened) and null comes back (the incident goes on); one that reaches him is watched.
    /// </summary>
    static async Task<DsRow> DsShootAndWatchQuick(IntruderT i, DsWatch w, DsGun gun, DsAim aim, DsFrom from, bool atLeaf)
    {
        var row = new DsRow { Who = "Burglar who runs", Phase = atLeaf ? "pounding AT THE LEAF (fix 3, emulated)" : "pounding (sweep)", Gun = gun.Name, Aim = aim.Name, From = from.Name,
                              Trace = DsTrace(from.Muzzle, aim.Point, i), Kicks = DsKicks(i) };
        int before = w.Force.Count;
        float shotAt = Time.time;
        DsFire(gun.Gun, from.Muzzle, aim.Point);
        await DsUntil(() => Time.time > shotAt + 0.05f, 1f);
        if (w.Force.Count > before || i.Current == IntruderT.State.Dead) { await DsAfter(row, i, w, before, shotAt); return row; }
        // Passed him by: nothing happens (on the headset he goes on pounding and kicks the door in); the sweep goes on. A
        // clean miss is geometry (where he stands, where the round went: the trace), not his reaction: listed, not counted.
        row.Result = "MISS"; row.Why = "passed him by — he goes on as if nothing happened";
        DsReport(row, counted: false);
        return null;
    }

    /// <summary>ScenarioDirector.VariedEntries on/off with the next round forced to EntryStyle.Classic (reflection; false = not there).</summary>
    static bool DsStyled(bool on)
    {
        var t = typeof(ScenarioDirector);
        var varied = t.GetField("VariedEntries", BindingFlags.Static | BindingFlags.Public);
        var forced = t.GetField("ForcedStyle", BindingFlags.Static | BindingFlags.Public);
        var style = t.Assembly.GetType("HIS.Scenario.EntryStyle");
        if (varied == null || forced == null || style == null || !Enum.IsDefined(style, "Classic")) { if (on) Debug.Log("[HIS] DOORSHOT no varied entries in this build: styled block skipped"); return false; }
        varied.SetValue(null, on);
        forced.SetValue(null, on ? Enum.Parse(style, "Classic") : null);
        return true;
    }

    static void DsTable()
    {
        Debug.Log("[HIS] DOORSHOT TABLE (who | phase | gun | aim | from | reached | cry | reaction | came in | result)");
        foreach (var r in s_dsRows)
            Debug.Log($"[HIS] DOORSHOT {r.Result} | {r.Who} | {r.Phase} | {r.Gun} | {r.Aim} | {r.From} | {r.Reached} | {r.Cry} | {r.Reaction} | {r.CameIn} | {r.Why}");
        bool AtLeaf(DsRow r) => r.Phase.Contains("AT THE LEAF");
        bool Styled(DsRow r) => r.Phase.Contains("varied-entries");
        var real = s_dsRows.FindAll(r => !r.Emulated);
        var sweep = s_dsRows.FindAll(r => r.Phase.StartsWith("pounding (sweep)"));
        var leaf = s_dsRows.FindAll(AtLeaf);
        var styled = s_dsRows.FindAll(Styled);
        Debug.Log($"[HIS] DOORSHOT {real.FindAll(r => r.Pass).Count}/{real.Count} counted door shots handled right (every phase run + every sweep round that reached him)");
        Debug.Log($"[HIS] DOORSHOT sweep: {sweep.FindAll(r => r.Result != "MISS").Count}/{sweep.Count} rounds reached him, {sweep.FindAll(r => r.Pass).Count} handled right" +
                  (leaf.Count > 0 ? $"; pounding at the leaf (fix 3, emulated): {leaf.FindAll(r => r.Result != "MISS").Count}/{leaf.Count} reached him, {leaf.FindAll(r => r.Pass).Count} handled right" : "") +
                  (styled.Count > 0 ? $"; varied-entries Classic: {styled.FindAll(r => r.Pass).Count}/{styled.Count} handled right" : ""));
    }
}
