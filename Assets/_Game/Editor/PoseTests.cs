using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using Gait = HIS.Intruder.IntruderAnimator.Gait;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The headless stand-in for watching him: drives deck scenarios with yells and shots and checks which Animator state he
/// settles in, how high his hips are (standing ≈ 0.9 m, kneeling ≈ 0.5 m, face down / sitting ≈ 0.1–0.2 m), which death
/// he picks and what the judge would read (Down, posture). Not part of the 8 deck verdict tests.
///   In Play Mode: HIS > Debug > Run Pose Tests.   Headless: NOQUIT=1 scripts/unity-batch.sh PoseTests.RunHeadless → [HIS] POSE lines.
/// </summary>
public static class PoseTests
{
    static int _pass, _fail;
    static bool _running;
    static readonly List<string> _log = new();

    [MenuItem("HIS/Debug/Run Pose Tests")]
    public static async void RunInPlayMode()
    {
        if (!EditorApplication.isPlaying || _running) { Debug.LogWarning("[HIS] POSE tests need Play Mode"); return; }
        await RunAll();
    }

    public static void RunHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 1500f;
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] POSE watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += async s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            await RunAll();
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static void OnLog(string msg, string stack, LogType t) { if (msg.StartsWith("[HIS] Intruder")) lock (_log) _log.Add(msg); }

    static async Task RunAll()
    {
        _running = true; _pass = _fail = 0;
        Application.logMessageReceived += OnLog;
        try
        {
            HIS.Core.PlayerState.GodMode = true;
            ScenarioDirector.WaitForForced = true;
            ScenarioDirector.VariedEntries = false;   // the fixed door timing these poses are scripted against
            HIS.Intruder.Hitbox.LethalRounds = false; // the reaction poses need hits that don't kill
            HIS.Scenario.PoliceResponse.Enabled = false;
            if (!await WaitFor(() => ScenarioDirector.Instance != null, 30)) return;

            // 1. Surrender: hands up → "get down" (FallToKnees, kneel, hands on head) → again (face down) → "get out" (up, out).
            if (await Begin("Surrenders"))
            {
                Yell(Command.ShowHands); await Wait(1.5f);
                Yell(Command.GetDown);
                await ExpectPose("on his knees", Gait.Kneel, 0.35f, 0.7f, 5f);
                await Wait(1.2f);
                Check("hands on head (arms layer)", Anim().layerCount > 1 && Anim().GetLayerWeight(1) > 0.9f, $"weight {Anim().GetLayerWeight(1):F2}");
                Check("kneeling reads as hands up", Live().Facts.Posture == Posture.HandsUp, Live().Facts.Posture.ToString());
                Yell(Command.GetDown);
                await ExpectPose("face down", Gait.Prone, 0f, 0.3f, 5f);
                Check("prone reads as prone", Live().Facts.Posture == Posture.Prone, Live().Facts.Posture.ToString());
                Yell(Command.GetOut);
                await ExpectPose("up off the floor", Gait.Calm, 0.8f, 1.05f, 8f);
                await End();
            }

            // 2. Killed on his knees → KneelDeath; killed face down → ProneDeath.
            if (await Begin("Surrenders"))
            {
                Yell(Command.ShowHands); await Wait(1f); Yell(Command.GetDown);
                await ExpectPose("on his knees (to be shot)", Gait.Kneel, 0.35f, 0.7f, 5f);
                Shoot(3);
                await ExpectLog("kneeling death", "death Kneeling", 3f);
                await Wait(2.5f); Dump("kneel corpse");
                await End();
            }
            if (await Begin("Surrenders"))
            {
                Yell(Command.ShowHands); await Wait(1f); Yell(Command.GetDown); await Wait(2.5f); Yell(Command.GetDown);
                await ExpectPose("face down (to be shot)", Gait.Prone, 0f, 0.3f, 6f);
                Shoot(3);
                await ExpectLog("prone death", "death Prone", 3f);
                await Wait(2f); Dump("prone corpse");
                await End();
            }

            // 3. The drunk neighbour: at the handle, staggers in, fidgets; forced trip (if the room has 2.3 m for it); "who's there?" →
            //    a gesture; "get out" → he goes. Then a fresh round: "who's there?", a shot beside him (forced) → onto his backside
            //    (if there's 2.3 m behind him); "get out" → up and away. (One round for both ran past his clocks — 25 s wandering,
            //    12 s after he said who he is — and he walked home before the shot: 2 false fails.)
            IntruderT.ForceTrip = true;
            bool door = false;
            if (await Begin("Wrong house", untilIdle: false))
            {
                door = await WaitFor(() => InState("Act_DrunkTryDoor"), 8f);
                Check("rattles the handle at the door", door, State());
                await WaitFor(() => Live() != null && Live().Current == IntruderT.State.Idle, 20);
                Check("drunk gait", Live().CurrentGait == Gait.Drunk, Live().CurrentGait.ToString());
                bool tripped = await WaitForLog("trips", 6f);
                if (!tripped)
                {
                    Note("no natural trip in 6 s (needs ~2.3 m of clear floor ahead mid-stride) → forcing one to check the animation chain");
                    StartPrivate(Live(), "Trip");
                    tripped = await WaitForLog("trips", 1f);
                }
                if (tripped)
                {
                    await ExpectPose("face down after the trip", Gait.Prone, 0f, 0.3f, 4f);
                    Check("a tripped man is Down", Live().Facts.Down, "Down=false");
                    await ExpectPose("back on his feet", Gait.Drunk, 0.8f, 1.05f, 10f);
                }
                else Note("no trip (no 2.3 m run of clear floor, or never walking): not a failure");
                Yell(Command.WhoIsThere);
                await WaitFor(() => Live().Current == IntruderT.State.Complying, 5f);
                bool gesture = await WaitFor(() => InState("Act_PointThumb") || InState("Act_Shrug") || Anim().GetBool(IntruderAnimator.ActingId) && Live().Facts.Posture == Posture.HandsUp, 3f);
                Check("answers with a thumb at next door / a shrug", gesture, State());
                Yell(Command.GetOut);
                await End();
            }
            IntruderT.ForceTrip = false;
            if (await Begin("Wrong house"))
            {
                Yell(Command.WhoIsThere);
                await WaitFor(() => Live().Current == IntruderT.State.Complying, 5f);
                await Wait(2f);
                IntruderT.ForceStumbleBack = true;
                var i = Live();
                typeof(IntruderT).GetMethod("OnGunshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(i, new object[] { i.transform.position + i.transform.forward * 1.2f + Vector3.up * 1.3f });
                if (!await WaitForLog("backside", 2f))
                {
                    Note("no natural stumble back (needs 2.3 m clear behind him) → forcing one to check the animation chain");
                    StartPrivate(Live(), "StumbleBack");
                }
                IntruderT.ForceStumbleBack = false;
                await ExpectPose("sitting on his backside", Gait.Sit, 0f, 0.3f, 5f);
                Check("sitting man is Down", Live().Facts.Down, "Down=false");
                Yell(Command.GetOut);
                await ExpectPose("up and leaving (drunk jog)", Gait.Drunk, 0.75f, 1.05f, 8f);
                await End();
            }
            // Killed sitting on his backside → slumps flat (OnBack).
            if (await Begin("Wrong house"))
            {
                Yell(Command.WhoIsThere); await Wait(1.5f);
                StartPrivate(Live(), "StumbleBack");
                await ExpectPose("sitting (to be shot)", Gait.Sit, 0f, 0.3f, 5f);
                Shoot(3);
                await ExpectLog("slumps flat on his back", "death OnBack", 3f);
                await Wait(1.5f); Dump("sitting corpse");
                await End();
            }

            // 4. Burglar: creeps (Sneak), works a spot (rummage/look gestures), "get out" → startle → jogs off looking back.
            if (await Begin("Burglar who runs"))
            {
                Check("sneak gait", Live().CurrentGait == Gait.Sneak, Live().CurrentGait.ToString());
                bool working = await WaitFor(() => InState("Act_Rummaging") || InState("Act_LookAroundNervous") || InState("Act_LookOverShoulder"), 15f);
                Check("works the loot spot", working, State());
                Yell(Command.GetOut);
                Check("startled when caught", await WaitFor(() => InState("Act_CaughtReact"), 1.5f), State());
                await WaitFor(() => Agent().enabled && Agent().velocity.magnitude > 1f, 4f);
                Check("jogs off looking back (1.69 m/s)", Mathf.Abs(Agent().speed - IntruderClips.LookBackRunSpeed) < 0.05f, $"speed {Agent().speed:F2}");
                await End();
            }

            // 5. Legs shot out (Brett shoots the legs first nearly every round — the most-seen fall in the game), watched every
            //    frame: far off (the whole lunge onto his hands) and close (the player in the way: short / in place).
            foreach (var (at, what) in new[] { (4.5f, "far"), (1.4f, "close") })
            {
                if (!await Begin("Home invader", untilIdle: false)) continue;
                if (!await WaitFor(() => Live() != null && Live().Current == IntruderT.State.Chasing && Live().Facts.Distance < at, 40))
                    Fail($"legs out ({what})", $"never came within {at} m ({Live()?.Summary})");
                else await LegsOutWatched(what);
                if (Live() != null) for (int k = 0; k < 4 && Live().Current != IntruderT.State.Dead; k++) { Shoot(1); await Wait(0.1f); }
                await End();
            }

            await DeathsDuringTransitions();
        }
        catch (Exception e) { _fail++; Debug.LogError($"[HIS] POSE exception: {e}"); }
        finally
        {
            IntruderAnimator.LegacyDieTrigger = false;
            IntruderT.ForceTrip = IntruderT.ForceStumbleBack = false;
            ScenarioDirector.WaitForForced = false;
            Application.logMessageReceived -= OnLog;
            Debug.Log($"[HIS] POSE {_pass}/{_pass + _fail} passed");
            _running = false;
        }
    }

    // ── 6. Brett 0.1.15: "I killed a dude with a knife and he stayed standing, didn't fall." The device log's kills, replayed
    // through the real Intruder + controller: #7 a slash to the head, the killing stab 31 ms later (inside the hit reaction's
    // blend-in); #14 a limping knife man, the other leg shot for the kill; #9 the knife surrenderer, hands up, headshot 2.25 s
    // after "Show me your hands"; plus killed as a reaction blends back into the limp, legs out and killed in the same frame,
    // killed mid-fall. Every body must play a death clip and end on the floor (hips < 0.3 m) with no EnsureDown rescue.
    // The first round runs 0.1.15's pulsed "Die" trigger (IntruderAnimator.LegacyDieTrigger) to show the bug reproduces.
    static async Task DeathsDuringTransitions()
    {
        var face = HumanBodyBones.Head; var chest = HumanBodyBones.Chest;
        IntruderAnimator.LegacyDieTrigger = true;
        if (await Approach("Home invader", 3f))
        {
            Strike(face, 81f, DamageType.Slash);
            await KillWhen(a => a.IsInTransition(0), 1f, () => Strike(chest, 45f, DamageType.Stab), "0.1.15 pulse");
            var (asleep, hips, sawDeath) = await WatchCorpse();
            Note($"0.1.15 pulsed Die, killed inside the hit blend: {(hips > 0.5f ? "REPRODUCED — the corpse sleeps standing" : "fell anyway")} " +
                 $"(hips {hips:F2} m, death clip {(sawDeath ? "played" : "never played")}, rig {(asleep ? "asleep" : "awake")})");
            await End();
        }
        IntruderAnimator.LegacyDieTrigger = false;

        foreach (bool behind in new[] { false, true })
            if (await Approach("Home invader", 3f))
            {
                Strike(face, 81f, DamageType.Slash, behind);
                await KillWhen(a => a.IsInTransition(0), 1f, () => Strike(chest, 45f, DamageType.Stab, behind), "hit blend");
                await ExpectCorpse($"#7 stabbed dead inside the hit reaction's blend ({(behind ? "from behind" : "from the front")})");
                await End();
            }
        if (await Approach("Knife attacker", 3.5f))
        {
            Strike(HumanBodyBones.RightUpperLeg, 27f, DamageType.Bullet);
            await Wait(1.7f);
            Strike(HumanBodyBones.LeftUpperLeg, 73f, DamageType.Bullet, side: true);
            await ExpectCorpse("#14 limping knife man, other leg shot for the kill (legs-out threshold crossed by the kill)");
            await End();
        }
        if (await Approach("Knife attacker", 3.5f))
        {
            Strike(HumanBodyBones.RightUpperLeg, 27f, DamageType.Bullet);
            int limp = Animator.StringToHash("Base Layer.Limp");
            await KillWhen(a => a.IsInTransition(0) && a.GetNextAnimatorStateInfo(0).fullPathHash == limp, 2.5f,
                           () => Strike(chest, 110f, DamageType.Bullet), "blend back into the limp");
            await ExpectCorpse("killed as the leg reaction blends back into the limp");
            await End();
        }
        if (await Begin("Surrenders (knife)"))
        {
            Yell(Command.ShowHands);
            await Wait(2.25f);
            Strike(face, 1000f, DamageType.Bullet);
            await ExpectCorpse("#9 knife surrenderer, hands up, headshot");
            await End();
        }
        if (await Approach("Knife attacker", 3.5f))
        {
            Strike(HumanBodyBones.LeftUpperLeg, 40f, DamageType.Bullet); Strike(HumanBodyBones.RightUpperLeg, 40f, DamageType.Bullet);
            Strike(chest, 30f, DamageType.Bullet);
            await ExpectCorpse("legs out and killed in the same frame");
            await End();
        }
        if (await Approach("Knife attacker", 3.5f))
        {
            Strike(HumanBodyBones.LeftUpperLeg, 36f, DamageType.Bullet); Strike(HumanBodyBones.RightUpperLeg, 36f, DamageType.Bullet);
            int fall = Animator.StringToHash("Base Layer.Fall_LegsOut");
            await KillWhen(a => a.GetCurrentAnimatorStateInfo(0).fullPathHash == fall && a.GetCurrentAnimatorStateInfo(0).normalizedTime > 0.2f, 2f,
                           () => Strike(chest, 110f, DamageType.Bullet), "mid-fall");
            await ExpectCorpse("killed mid-fall (legs out)");
            await End();
        }
    }

    /// <summary>A fighter scenario, waited on until he's chasing within <paramref name="within"/> m.</summary>
    static async Task<bool> Approach(string name, float within)
    {
        if (!await Begin(name, untilIdle: false)) return false;
        if (await WaitFor(() => Live() != null && Live().Current == IntruderT.State.Chasing && Live().Facts.Distance < within, 40)) return true;
        Fail(name, $"never came within {within} m ({Live()?.Summary})");
        if (Live() != null) for (int k = 0; k < 4 && Live().Current != IntruderT.State.Dead; k++) { Shoot(1); await Wait(0.1f); }
        await End();
        return false;
    }

    /// <summary>A blow on a bone's hitbox, sized so it lands as <paramref name="applied"/> after the hitbox multiplier; from the
    /// player's head (the front), along his facing (behind) or across him (side).</summary>
    static void Strike(HumanBodyBones bone, float applied, DamageType type, bool behind = false, bool side = false)
    {
        var i = Live(); var b = Anim().GetBoneTransform(bone);
        var hb = b != null ? b.GetComponent<Hitbox>() : null;
        if (hb == null) { Fail("strike", $"no hitbox on {bone}"); return; }
        var cam = Camera.main;
        var dir = behind ? i.transform.forward : side ? i.transform.right : (b.position - cam.transform.position).normalized;
        hb.ApplyDamage(new DamageInfo { Amount = applied / hb.Multiplier, Type = type, Direction = dir, Penetration = 1f, Point = b.position, Normal = -dir, Source = cam.gameObject });
    }

    /// <summary>Frame-exact: the kill lands the first frame <paramref name="window"/> holds (or at the timeout).</summary>
    static async Task KillWhen(Func<Animator, bool> window, float timeout, Action kill, string what)
    {
        bool done = false, inWindow = false; string at = "";
        var i = Live(); var anim = Anim();
        i.StartCoroutine(Frames());
        await WaitFor(() => done, timeout + 1f);
        Note($"kill ({what}): {(inWindow ? "inside the window" : "window never came — killed at the timeout")} at {at}");

        System.Collections.IEnumerator Frames()
        {
            float until = Time.time + timeout;
            while (Time.time < until && !window(anim)) yield return null;
            inWindow = window(anim);
            at = State();
            kill();
            done = true;
        }
    }

    /// <summary>Until the corpse sleeps (≤ 6 s): did a death clip play, where are the hips.</summary>
    static async Task<(bool asleep, float hips, bool sawDeath)> WatchCorpse()
    {
        var i = Live(); var anim = Anim(); var ia = i.GetComponentInChildren<IntruderAnimator>();
        bool saw = false;
        for (float t = 0; t < 6f && anim.enabled; t += 0.05f) { if (ia.InDeathClip) saw = true; await Wait(0.05f); }
        return (!anim.enabled, Hips(), saw);
    }

    static async Task ExpectCorpse(string what)
    {
        if (Live() == null || Live().Current != IntruderT.State.Dead) { Fail(what, $"not dead: {Live()?.Summary}"); return; }
        var (asleep, hips, saw) = await WatchCorpse();
        bool rescued; lock (_log) rescued = _log.Exists(l => l.Contains("still on his feet"));
        string death; lock (_log) death = _log.FindLast(l => l.Contains("death ")) ?? "(no death line)";
        Check(what + ": death clip played, ends on the floor", saw && asleep && hips < 0.3f && !rescued,
              $"{death.Replace("[HIS] Intruder: ", "")}; hips {hips:F2} m, clip {(saw ? "played" : "NEVER")}, {(asleep ? "asleep" : "still animating")}{(rescued ? ", needed the EnsureDown rescue" : "")}");
    }

    // ── Legs out, frame by frame. Limits: leg skin ≤ 5 cm under the floor (the crawl clip itself rests its legs 3.5 cm under,
    // the landing 4 cm; HitOnLegs's blend went 76 cm), never on his back while down (belly·up < 0.5; HitOnLegs: 1.0), the hips
    // ≤ 0.45 m over the floor in the 0.6 s after they get down (the lunge's own landing slide 0.34 m; HitOnLegs jumped 0.7–0.8 m),
    // no frame step faster than 6 m/s (the lunge peaks ~5), in the crawl within 2.5 s.
    const float LegsUnderFloor = 0.05f, BackMax = 0.5f, SlideMax = 0.45f, StepSpeedMax = 6f;

    sealed class FallWatch
    {
        public float Legs = float.MaxValue, Body = float.MaxValue, Back = -1f, Slide, Speed, RootStep, DownAt = -1f, SettledAt = -1f;
        public bool SawFall, Stop;   // Stop: the probe is disposed (the watch outlived the checks)
        public string SpeedAt = "";
    }

    static async Task LegsOutWatched(string what)
    {
        var i = Live(); var anim = Anim(); var ia = i.GetComponentInChildren<IntruderAnimator>();
        float floor = (float)typeof(IntruderT).GetField("_floorY", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(i);
        using var probe = new SkinProbe(anim);   // leg skin picked while he's on his feet
        var w = new FallWatch();
        lock (_log) _log.Clear();
        IntruderDebugMenu.Shoot(2, 36f, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg);
        i.StartCoroutine(Watch(i, anim, ia, probe, floor, w));
        await WaitFor(() => w.SettledAt >= 0f || i.Current == IntruderT.State.Dead, 4f);
        await Wait(0.3f);
        string how; lock (_log) how = _log.Find(l => l.Contains("legs shot out")) ?? "(no legs-out line)";
        Note($"legs out ({what}): {how}");
        Check($"legs out ({what}): went down in Fall_LegsOut", w.SawFall, State());
        Check($"legs out ({what}): legs stay out of the floor", w.Legs >= -LegsUnderFloor, $"legs {w.Legs * 100f:F1} cm, body {w.Body * 100f:F1} cm");
        Check($"legs out ({what}): face down, never on his back", w.Back < BackMax, $"belly·up {w.Back:F2}");
        Check($"legs out ({what}): no slide or jump once down", w.Slide <= SlideMax && w.Speed <= StepSpeedMax,
              $"slid {w.Slide:F2} m in 0.6 s, fastest hips {w.Speed:F1} m/s{w.SpeedAt}, root step {w.RootStep * 100f:F1} cm");
        Check($"legs out ({what}): settles in the crawl", w.SettledAt >= 0f && w.SettledAt <= 2.5f && Hips() is > 0.05f and < 0.3f,
              $"crawl at {w.SettledAt:F2} s, hips {Hips():F2}, down {Live().Facts.Down}");
        Check($"legs out ({what}): reads as Down", Live().Facts.Down, Live().Summary);
        w.Stop = true;
    }

    /// <summary>Every frame from the shot until 0.4 s after he's settled in the crawl (≤ 3 s): <see cref="FallWatch"/>.</summary>
    static System.Collections.IEnumerator Watch(IntruderT i, Animator anim, IntruderAnimator ia, SkinProbe probe, float floor, FallWatch w)
    {
        var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        Vector3 prevHips = hips.position, prevRoot = i.transform.position, winHips = prevHips;
        float t = 0f, win = 0f;
        int fall = Animator.StringToHash("Base Layer.Fall_LegsOut");
        while (t < 3f && i != null && i.Current != IntruderT.State.Dead)
        {
            yield return null;
            if (w.Stop) yield break;
            float dt = Time.deltaTime; t += dt;
            if (dt <= 0f) continue;
            var st = anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (st == fall || anim.IsInTransition(0) && anim.GetNextAnimatorStateInfo(0).fullPathHash == fall) w.SawFall = true;
            probe.Lowest(out float body, out float legs);
            w.Body = Mathf.Min(w.Body, body - floor); w.Legs = Mathf.Min(w.Legs, legs - floor);
            var hp = hips.position; var rp = i.transform.position;
            float step = new Vector2(hp.x - prevHips.x, hp.z - prevHips.z).magnitude;
            // Speed over at least one Quest frame (1/72 s): headless runs unthrottled (~1 ms frames) and caught the pose's
            // instantaneous peaks (8–9 m/s, 2026-09-24) that no 72 Hz frame shows; overnight (.lowheat) frames were longer.
            win += dt;
            if (win >= 1f / 72f)
            {
                float v = new Vector2(hp.x - winHips.x, hp.z - winHips.z).magnitude / win;
                if (v > w.Speed) { w.Speed = v; w.SpeedAt = $" @{t:F2} s ({win * 1000f:F0} ms, {(anim.IsInTransition(0) ? "blend" : "state")})"; }
                win = 0f; winHips = hp;
            }
            w.RootStep = Mathf.Max(w.RootStep, new Vector2(rp.x - prevRoot.x, rp.z - prevRoot.z).magnitude);
            if (hp.y - floor < 0.45f) w.Back = Mathf.Max(w.Back, Vector3.Dot(SkinProbe.BellyDirection(anim), Vector3.up));
            if (w.DownAt < 0f && hp.y - floor < 0.3f) w.DownAt = t;
            if (w.DownAt >= 0f && t - w.DownAt <= 0.6f) w.Slide += step;
            if (w.SettledAt < 0f && ia.IsIn(Gait.Crawl)) w.SettledAt = t;
            if (w.SettledAt >= 0f && t - w.SettledAt > 0.4f) break;
            prevHips = hp; prevRoot = rp;
        }
    }

    static int _done;
    static async Task<bool> Begin(string name, bool untilIdle = true)
    {
        var dir = ScenarioDirector.Instance;
        _done = dir.Completed;
        var previous = dir.Intruder;
        lock (_log) _log.Clear();
        ScenarioDirector.ForcedNext = name;
        Debug.Log($"[HIS] POSE --- {name}");
        if (!await WaitFor(() => dir.Record != null && dir.Record.Def.Name == name && dir.Intruder != null && dir.Intruder != previous && dir.Completed == _done, 120))
        { Fail(name, "never started"); return false; }
        if (untilIdle && !await WaitFor(() => Live() != null && Live().Current == IntruderT.State.Idle, 40)) { Fail(name, "never came in"); return false; }
        if (untilIdle) await Wait(0.8f);
        return true;
    }

    static async Task End()
    {
        var dir = ScenarioDirector.Instance;
        if (!await WaitFor(() => dir.Completed > _done, 90)) Fail("scenario", "never ended");
    }

    static async Task ExpectPose(string what, Gait gait, float hipsMin, float hipsMax, float timeout)
    {
        bool got = await WaitFor(() => Live() != null && Live().GetComponentInChildren<IntruderAnimator>().IsIn(gait), timeout);
        float hips = Hips();
        Check(what, got && hips >= hipsMin && hips <= hipsMax, $"state {State()} hips {hips:F2} (want {gait} {hipsMin:F2}–{hipsMax:F2})");
    }

    static async Task ExpectLog(string what, string fragment, float timeout) => Check(what, await WaitForLog(fragment, timeout), string.Join(" | ", Tail()));

    static async Task<bool> WaitForLog(string fragment, float timeout) => await WaitFor(() => { lock (_log) return _log.Exists(l => l.Contains(fragment)); }, timeout);

    static string[] Tail() { lock (_log) return _log.GetRange(Math.Max(0, _log.Count - 4), Math.Min(4, _log.Count)).ToArray(); }

    static void Check(string what, bool ok, string info)
    {
        if (ok) { _pass++; Debug.Log($"[HIS] POSE PASS {what} ({info})"); }
        else { _fail++; Debug.LogError($"[HIS] POSE FAIL {what}: {info}"); }
    }
    static void Fail(string what, string why) { _fail++; Debug.LogError($"[HIS] POSE FAIL {what}: {why}"); }
    static void Note(string what) => Debug.Log($"[HIS] POSE NOTE {what}");

    static void Dump(string label) => Debug.Log($"[HIS] POSE dump {label}: {(Live() != null ? Live().Summary : "-")} | {State()} hips {Hips():F2}");

    static IntruderT Live() => ScenarioDirector.Instance != null ? ScenarioDirector.Instance.Intruder : null;
    static Animator Anim() => Live().GetComponentInChildren<Animator>();
    static UnityEngine.AI.NavMeshAgent Agent() => Live().GetComponent<UnityEngine.AI.NavMeshAgent>();
    static float Hips() { var i = Live(); if (i == null) return -1f; var h = Anim().GetBoneTransform(HumanBodyBones.Hips); return h.position.y - i.transform.position.y; }
    static bool InState(string name) => Live() != null && Anim().enabled && Anim().GetCurrentAnimatorStateInfo(0).IsName(name);
    static string State()
    {
        var i = Live(); if (i == null) return "no intruder";
        var a = Anim(); if (!a.enabled) return "ASLEEP";
        string clip = a.GetCurrentAnimatorClipInfoCount(0) > 0 ? a.GetCurrentAnimatorClipInfo(0)[0].clip.name : "?";
        if (a.IsInTransition(0) && a.GetNextAnimatorClipInfoCount(0) > 0) clip += "→" + a.GetNextAnimatorClipInfo(0)[0].clip.name;
        return $"{i.Current}/{i.CurrentGait} {clip} t={a.GetCurrentAnimatorStateInfo(0).normalizedTime:F2}";
    }

    static void Yell(Command c) => ScenarioDirector.Instance.Inject(c);
    static void StartPrivate(IntruderT i, string routine) =>
        i.StartCoroutine((System.Collections.IEnumerator)typeof(IntruderT).GetMethod(routine, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(i, null));
    static void Shoot(int n) { for (int k = 0; k < n; k++) IntruderDebugMenu.Shoot(1, 34f, HumanBodyBones.Head); }

    static async Task Wait(float s) => await Task.Delay(Mathf.RoundToInt(s * 1000));

    static async Task<bool> WaitFor(Func<bool> cond, float timeout)
    {
        for (float t = 0; t < timeout; t += 0.1f)
        {
            if (!EditorApplication.isPlaying) return false;
            if (cond()) return true;
            await Wait(0.1f);
        }
        return false;
    }
}
