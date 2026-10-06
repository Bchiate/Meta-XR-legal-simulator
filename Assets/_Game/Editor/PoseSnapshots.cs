using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Eyes without Play Mode: renders the intruder (Intruder_Alex.prefab) posed by a clip, or by the real Animator controller
/// driven through a scripted sequence, into one contact-sheet PNG per clip/sequence under Logs/poses/.
/// Columns = sample times; rows = front (ortho, facing us), his right side (ortho, facing right) and a 3/4 view from above.
/// World-fixed grid backdrops (0.1 m lines, 0.5 m bold) with a red y = 0 line, so floating/sinking and root travel read off
/// the picture; each cell is labelled with the lowest skin vertex (SOLE, cm), hips height and hand-to-head distances.
/// Numbers also go to Logs/poses/poses.txt. KNIFE=1 shows his knife prop (red dot = blade tip) and adds two close-ups of the
/// right hand (from his right-front and from below-front) as rows 4–5 (once the prop leaves his hand, both close-ups follow it
/// to the floor). PROP=IntruderPistol: the gunman's Glock, its muzzle ray, and in the Ctl_Gun* sequences the game's aim pass
/// (IntruderAnimator.AimBones) onto a green target (the player's chest, in front of him), shot kicks + the flash quad, and the
/// gun dropped/tossed as the physics prop it becomes (IntruderWeapon.Drop/Toss). Needs a GPU: GRAPHICS=1 scripts/unity-batch.sh PoseSnapshots.Render
/// [-poses Name1,Name2] (a subset by name). HIS > Debug > Render Pose Snapshots in the Editor.
/// </summary>
public static class PoseSnapshots
{
    const string PrefabPath = "Assets/_Game/Prefabs/Intruder_Alex.prefab";
    const string ControllerPath = "Assets/_Game/Animation/Intruder_Alex.controller";
    const string AnimDir = "Assets/ThirdParty/Mixamo/Animations";
    const string OutDir = "Logs/poses";
    const int Cell = 300, Header = 22, Layer = 31;
    const float Step = 1f / 60f;
    /// <summary>TRACE=1: also log root/hips (and the base-layer state) every 0.05 s into poses.txt.</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("TRACE") == "1";

    static readonly float[] Norm = { 0f, 0.25f, 0.5f, 0.75f, 1f };

    // Every clip the Animator plays in the new scenarios (clip sheets: normalized 0, .25, .5, .75, 1).
    static readonly string[] Clips =
    {
        "DrunkIdle", "DrunkWalk", "DrunkRun", "DrunkWalkBack", "DrunkStumble", "DrunkHiccup", "DrunkTryDoor", "TripFall", "StumbleBackFall",
        "GetUpFromStomach", "GetUpFromBack", "PointThumb", "Shrug", "SneakForward", "CrouchedWalk", "Rummaging", "LookAroundNervous",
        "LookOverShoulder", "CaughtReact", "RunLookBack", "FallToKnees", "KneelIdle", "KneelHandsOnHead", "CrouchToProne", "ProneHold", "ProneDeathFlat",
        "SitHold", "OnBackHold", "KneelToStand", "ProneDeath", "KneelDeath", "KneelDeathFromKnees",
        "LowCrouchIdle", "DuckCover", "DuckHold",   // Ricky, the Marshal night's protectee (research/27 §3): never rendered before
    };

    // ── Controller sequences: the real Animator (blends, chained ways down/up, the Arms layer) driven like the brain does.
    const int Normal = 0, Calm = 3, Drunk = 4, Kneel = 6, Prone = 7, Sit = 8;

    sealed class Seq
    {
        public string Name;
        public (float t, Action<Driver> act)[] Events;
        public float[] Captures;
        /// <summary>Also measure every 1/60 s step (not just the captures): lowest skin / leg-skin point, the hips' fastest flat
        /// step, how far he rolls onto his back — the numbers a pop between two captures hides. Title + poses.txt "WATCH".</summary>
        public bool Watch;
    }

    static Action<Driver> Gait(int g) => d => d.Ctl.SetInteger("Gait", g);
    static Action<Driver> Arms(bool on) => d => d.ArmsOn = on;
    static Action<Driver> Speed(float s) => d => d.Ctl.SetFloat("Speed", s);
    static Action<Driver> FallKind(int k) => d => { d.Ctl.SetInteger("FallIndex", k); d.Ctl.SetTrigger("Fall"); d.Ctl.SetFloat("Speed", 0f); };
    static Action<Driver> Die(int k) => d => { d.Ctl.SetInteger("DeathIndex", k); d.Ctl.SetTrigger("Die"); };
    static Action<Driver> Attack(int k) => d => { d.Ctl.SetInteger("AttackIndex", k); d.Ctl.SetTrigger("Attack"); };
    static Action<Driver> Knife(bool on) => d => d.Knife = on;
    /// <summary>The gunman (render with PROP=IntruderPistol): pistol fist on; arms = the GunArms layer; aim 0 low ready … 1 aimed.</summary>
    static Action<Driver> Pistol(bool on, bool arms, float aim) => d => { d.Pistol = on; d.GunArms = arms; d.Aim = aim; };
    /// <summary>The player's chest <paramref name="dist"/> m in front of him (1.25 m up): the aim pass turns chest/arm onto it.</summary>
    static Action<Driver> AimAt(float dist, float height = 1.25f) => d => d.AimOffset = new Vector3(0f, height, dist);
    /// <summary>One round: the kick (IntruderAnimator.Recoil) and 50 ms of flash.</summary>
    static readonly Action<Driver> Fire = d => { d.Recoil = 1f; d.FiredAt = d.T; };
    /// <summary>The gun leaves his hand as the game lets it go: Drop (death: forward 0.8 + up 0.5) or Toss ("Drop it!").</summary>
    static Action<Driver> DropGun(Vector3 v) => d => d.Release = (v, false);
    static Action<Driver> TossGun(Vector3 v) => d => d.Release = (v, true);
    static Action<Driver> Act(int k) => d => { d.Ctl.SetInteger("ActIndex", k); d.Ctl.SetBool("Acting", true); d.Ctl.SetTrigger("Act"); };
    static Action<Driver> Hit(int part) => d => { d.Ctl.SetInteger("HitPart", part); d.Ctl.SetTrigger("Hit"); };
    /// <summary>0.1.15's death: the "Die" trigger pulsed like IntruderAnimator.Pulse did (cleared after two evaluations — the
    /// frame it's set and the next — whether or not a transition took it).</summary>
    static Action<Driver> DiePulse(int k) => d => { d.Ctl.SetInteger("DeathIndex", k); d.Ctl.SetTrigger("Die"); d.PulseLeft = 2; };
    /// <summary>The fixed death (IntruderAnimator.Die): CrossFade straight into the death state, whatever is blending.</summary>
    static Action<Driver> DieNow(int k, int gait = 0) => d =>
    {
        var kind = (HIS.Intruder.IntruderAnimator.DeathKind)k; var g = (HIS.Intruder.IntruderAnimator.Gait)gait;
        d.Ctl.SetInteger("DeathIndex", k);
        d.Ctl.CrossFadeInFixedTime(HIS.Intruder.IntruderAnimator.DeathState(kind, g), HIS.Intruder.IntruderAnimator.DeathBlend(kind, g), 0, 0f);
    };
    static readonly float[] DeathCaps = { 0.95f, 1.1f, 1.6f, 2.4f, 3.4f, 4.8f };
    static float[] Shift(float[] caps, float by) => caps.Select(c => c + by).ToArray();
    const int GunGait = 9;
    /// <summary>Ricky's postures (IntruderAnimator.Gait.Crouch / Cower).</summary>
    const int CrouchGait = 12, CowerGait = 13;
    /// <summary>Straight into a state, as the brain's SnapGait does (Ricky is placed already crouched: no way down).</summary>
    static Action<Driver> Snap(string state) => d => d.Ctl.Play(state, 0, 0f);
    /// <summary>IntruderAnimator drives GaitRate = speed / CrawlNatural in the crawl (0 = lying still).</summary>
    static Action<Driver> Rate(float r) => d => d.Ctl.SetFloat("GaitRate", r);
    /// <summary>The share of the fall's root travel the game lets through (IntruderAnimator.Fall travel: 0 = drops in place).</summary>
    static Action<Driver> Travel(float k) => d => d.Travel = k;
    const int Crawl = 2;
    static readonly float CrawlRate = HIS.Intruder.IntruderClips.CrawlSpeed / HIS.Intruder.IntruderClips.CrawlNatural;

    /// <summary>Legs shot out mid-walk at 1 s (Fall_LegsOut: the lunge onto his hands into the crawl), crawling at his gameplay
    /// speed from 2.1 s (the game's Fall lock ends 1.0 s after the shot).</summary>
    static (float, Action<Driver>)[] LegsOut(params (float, Action<Driver>)[] then) => LegsOutWith(1f, 1.25f, then);
    /// <summary>The same with <paramref name="travel"/> of the lunge let through (a wall / the player close ahead: 0 = in
    /// place), from walking at <paramref name="speed"/> (0 = the fight idle).</summary>
    static (float, Action<Driver>)[] LegsOutWith(float travel, float speed, params (float, Action<Driver>)[] then) => new (float, Action<Driver>)[]
        { (0f, Speed(speed)), (1f, Travel(travel)), (1f, FallKind(0)), (1f, Gait(Crawl)), (1f, Rate(0f)), (2.1f, Rate(CrawlRate)) }.Concat(then).ToArray();
    /// <summary>Killed mid-fall: Intruder.Collapse waits until he's settled in the crawl (IntruderAnimator.OnFloor), then Die.</summary>
    static Action<Driver> DieOnFloor(int k) => d => d.PendingDie = k;

    /// <summary>A standing death from the fight idle (Die at 1 s).</summary>
    static Seq StandingDeath(int k, string name) => new Seq { Name = "Ctl_Death" + k + "_" + name, Events = new[] { (0f, Speed(0f)), (1f, Die(k)) },
        Captures = new[] { 0.95f, 1.4f, 1.9f, 2.5f, 3.2f, 4.6f } };

    static Seq[] Sequences => new[]
    {
        // "Get down!": FallToKnees → Kneel, hands-on-head layer fades in once settled.
        new Seq { Name = "Ctl_KneelDown", Events = new[] { (0f, Gait(Calm)), (0.5f, Gait(Kneel)), (0.5f, Arms(true)) },
                  Captures = new[] { 0.45f, 0.8f, 1.1f, 1.5f, 2.0f, 3.5f } },
        // Arms layer close-up (settled): legs must match plain KneelIdle, hands on the head.
        new Seq { Name = "Ctl_KneelArms", Events = new[] { (0f, Gait(Calm)), (0.3f, Gait(Kneel)), (0.3f, Arms(true)) },
                  Captures = new[] { 2.0f, 2.4f, 3.0f, 4.0f, 5.5f } },
        // "Get down!" again: kneel → CrouchToProne (hands to the floor) → lowered flat → hands behind his head.
        new Seq { Name = "Ctl_ProneDown", Events = new[] { (0f, Gait(Calm)), (0.3f, Gait(Kneel)), (0.3f, Arms(true)), (4f, Gait(Prone)) },
                  Captures = new[] { 3.9f, 4.4f, 4.9f, 5.4f, 5.9f, 6.4f, 7.0f, 8.0f } },
        // "Get out!" from face down: GetUpFromStomach → Calm.
        new Seq { Name = "Ctl_ProneUp", Events = new[] { (0f, Gait(Calm)), (0.3f, Gait(Prone)), (0.3f, Arms(true)), (6f, Arms(false)), (6f, Gait(Calm)) },
                  Captures = new[] { 5.9f, 6.6f, 7.4f, 8.2f, 9.0f, 9.8f, 11.0f } },
        // "Get out!" from his knees: KneelToStand → Calm.
        new Seq { Name = "Ctl_KneelUp", Events = new[] { (0f, Gait(Calm)), (0.3f, Gait(Kneel)), (0.3f, Arms(true)), (4f, Arms(false)), (4f, Gait(Calm)) },
                  Captures = new[] { 3.9f, 4.4f, 4.9f, 5.4f, 6.0f, 7.0f } },
        // The drunk trips mid-stride (TripFall → Prone), lies there, gets up from his stomach.
        new Seq { Name = "Ctl_Trip", Events = new[] { (0f, Gait(Drunk)), (0f, Speed(0.75f)), (1f, FallKind(1)), (1.02f, Gait(Prone)), (4f, Gait(Drunk)) },
                  Captures = new[] { 0.95f, 1.3f, 1.7f, 2.2f, 3.0f, 4.8f, 6.0f, 7.2f, 8.8f } },
        // Knocked onto his backside (StumbleBackFall → Sit), then gets up (GetUpFromBack).
        new Seq { Name = "Ctl_StumbleBack", Events = new[] { (0f, Gait(Drunk)), (1f, FallKind(2)), (1.02f, Gait(Sit)), (5.5f, Gait(Drunk)) },
                  Captures = new[] { 0.95f, 1.8f, 2.6f, 3.5f, 5.4f, 5.9f, 6.5f, 7.2f, 8.2f } },
        // Deaths from the held postures.
        new Seq { Name = "Ctl_KneelDeath", Events = new[] { (0f, Gait(Calm)), (0.3f, Gait(Kneel)), (0.3f, Arms(true)), (4f, Arms(false)), (4f, Die(5)) },
                  Captures = new[] { 3.95f, 4.3f, 4.7f, 5.2f, 5.8f, 6.8f } },
        new Seq { Name = "Ctl_ProneDeath", Events = new[] { (0f, Gait(Calm)), (0.3f, Gait(Prone)), (0.3f, Arms(true)), (6f, Die(6)) },
                  Captures = new[] { 5.95f, 6.3f, 6.7f, 7.1f, 7.6f, 8.5f } },
        // Knife in hand (the Grip layer closes his fist over any clip; render with KNIFE=1): fight idle → walk → run, then the
        // lunging stab and the slash (his two knife attacks).
        new Seq { Name = "Ctl_KnifeWalk", Events = new[] { (0f, Knife(true)), (1f, Speed(1.25f)), (2.5f, Speed(3.5f)) },
                  Captures = new[] { 0.8f, 1.6f, 2.0f, 3.2f, 3.5f } },
        new Seq { Name = "Ctl_KnifeAttack", Events = new[] { (0f, Knife(true)), (0.5f, Attack(5)), (3.8f, Attack(4)) },
                  Captures = new[] { 0.45f, 1.2f, 1.76f, 2.3f, 4.3f, 4.58f, 5.0f } },
        // The gunman (M3a): low ready → the raise (0.7 s) → aimed; the aimed pistol walk; the sprint with the gun (arms free);
        // TossAway ("Drop it!"); the flinch with the gun up (PistolHit). Render with PROP=IntruderPistol.
        new Seq { Name = "Ctl_GunRaise", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 0f)), (0f, AimAt(3f)), (1f, Pistol(true, true, 1f)) },
                  Captures = new[] { 0.9f, 1.25f, 1.5f, 1.75f, 2.2f } },
        // The tell then the burst: raise at 1 s (0.7 s), three rounds (kick + flash at +1 frame, then settled).
        new Seq { Name = "Ctl_GunTellFire", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 0f)), (0f, AimAt(3f)), (1f, Pistol(true, true, 1f)),
                                                             (1.9f, Fire), (2.35f, Fire), (2.8f, Fire) },
                  Captures = new[] { 0.95f, 1.35f, 1.7f, 1.917f, 2.05f, 2.367f, 2.817f, 3.3f } },
        new Seq { Name = "Ctl_GunWalkRun", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 0f)), (0f, AimAt(4f)), (0.5f, Speed(1.5f)), (2f, Pistol(true, true, 1f)),
                                                            (3.2f, Pistol(true, false, 0f)), (3.2f, Speed(3.6f)) },
                  Captures = new[] { 1.2f, 1.5f, 2.8f, 3.1f, 4.0f, 4.2f } },
        // "Drop it!": TossAway, the gun let go at IntruderClips.TossRelease with the game's lob (right + forward + up, 1.4 m/s each).
        // AIM ERR here = the muzzle's angle off the player's chest 3 m ahead (the judge's pointing cone is 10°; no aim pass in the act).
        new Seq { Name = "Ctl_GunToss", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 0f)), (0f, AimAt(3f)), (0.5f, Pistol(true, false, 0f)), (0.5f, Act(10)),
                                                         (0.5f + HIS.Intruder.IntruderClips.TossRelease, TossGun(new Vector3(1.4f, 1.4f, 1.4f))) },
                  Captures = new[] { 0.45f, 0.9f, 1.15f, 1.2f, 1.25f, 1.3f, 1.35f, 1.4f, 1.6f, 2.0f, 2.6f, 3.5f } },
        new Seq { Name = "Ctl_GunHit", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 1f)), (0f, AimAt(3f)),
                                                        (0.8f, Hit(4)) },
                  Captures = new[] { 0.75f, 1.1f, 1.4f, 1.8f, 2.3f } },
        // Shot dead with the gun on you: DeathFromTheFront, the gun dropped at the moment of death (Intruder.Die).
        new Seq { Name = "Ctl_GunDeath", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 1f)), (0f, AimAt(3f)),
                                                          (1.2f, Die(0)), (1.2f, DropGun(new Vector3(0f, 0.5f, 0.8f))) },
                  Captures = new[] { 1.15f, 1.35f, 1.6f, 2.0f, 2.6f, 3.4f, 4.5f } },
        // Legs shot out before the tell (gun low), he raises it from the floor, fires, is killed there (crawl death, gun dropped).
        new Seq { Name = "Ctl_GunCrawl", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 0f)), (0f, AimAt(3.5f)), (0f, Speed(1.5f)),
                                                          (1f, FallKind(0)), (1f, Gait(Crawl)), (1f, Rate(0f)), (1f, Speed(0f)),
                                                          (3f, Pistol(true, false, 1f)), (4.2f, Fire), (5f, Die(6)), (5f, DropGun(new Vector3(0f, 0.5f, 0.8f))) },
                  Captures = new[] { 0.95f, 1.5f, 2.2f, 2.9f, 3.3f, 3.8f, 4.217f, 5.2f, 5.8f, 7.0f }, Watch = true },
        // Legs shot out while he's already aiming: the fall (no aim pass), then the crawl's aim (70° arm) — watch for a snap.
        new Seq { Name = "Ctl_GunCrawlSnap", Events = new[] { (0f, Gait(GunGait)), (0f, Pistol(true, true, 1f)), (0f, AimAt(3.5f)), (0f, Speed(1.5f)),
                                                              (1f, FallKind(0)), (1f, Gait(Crawl)), (1f, Rate(0f)), (1f, Speed(0f)) },
                  Captures = new[] { 0.95f, 1.3f, 1.6f, 1.9f, 2.1f, 2.3f, 2.5f, 3.2f }, Watch = true },
        new Seq { Name = "Ctl_SitDeath", Events = new[] { (0f, Gait(Drunk)), (1f, FallKind(2)), (1.02f, Gait(Sit)), (5.5f, Die(7)) },
                  Captures = new[] { 5.45f, 5.7f, 6.0f, 6.3f, 7.0f } },
        // Legs shot out → the crawl (Brett 0.1.10: "how they're falling after dying has completely changed and broke").
        // 2026-09-24: the lunge onto his hands (Fall_LegsOut) replaced HitOnLegs, which ended on his back and swung upright with
        // his legs 76 cm through the floor blending into the crawl.
        new Seq { Name = "Ctl_LegsOutCrawl", Events = LegsOut(), Captures = new[] { 0.95f, 1.3f, 1.6f, 1.9f, 2.6f, 3.4f, 4.2f, 5.0f }, Watch = true },
        // The same, sampled through the handover from the lunge into the face-down crawl.
        new Seq { Name = "Ctl_LegsOutBlend", Events = LegsOut(), Captures = new[] { 1.3f, 1.45f, 1.55f, 1.65f, 1.75f, 1.85f, 2.0f }, Watch = true },
        // The player (or a wall) right ahead: none of the lunge's travel (he goes down where he stands), and 40 % of it.
        new Seq { Name = "Ctl_LegsOutInPlace", Events = LegsOutWith(0f, 1.25f), Captures = new[] { 0.95f, 1.2f, 1.4f, 1.55f, 1.7f, 1.85f, 2.0f, 2.8f }, Watch = true },
        new Seq { Name = "Ctl_LegsOutShort", Events = LegsOutWith(0.4f, 1.25f), Captures = new[] { 0.95f, 1.2f, 1.4f, 1.55f, 1.7f, 1.85f, 2.0f, 2.8f }, Watch = true },
        // Knife in hand (render with KNIFE=1): the Grip layer keeps his fist round it through the fall.
        new Seq { Name = "Ctl_LegsOutKnife", Events = LegsOut((0f, Knife(true))), Captures = new[] { 0.95f, 1.3f, 1.5f, 1.7f, 1.9f, 2.6f }, Watch = true },
        // 0.1.10's crawl death: no death clip, the crawl cycle just stops (GaitRate → 0) and the rig sleeps in that pose.
        new Seq { Name = "Ctl_CrawlFreeze", Events = LegsOut((4.2f, Rate(0f))), Captures = new[] { 4.15f, 4.6f, 5.5f } },
        // Killed crawling: Die(Prone) from the Crawl gait (the CrawlDeath route), several crawl phases.
        new Seq { Name = "Ctl_CrawlDeath", Events = LegsOut((4.2f, Die(6))), Captures = new[] { 4.15f, 4.35f, 4.6f, 4.9f, 5.3f, 5.8f, 6.6f }, Watch = true },
        new Seq { Name = "Ctl_CrawlDeathB", Events = LegsOut((5.6f, Die(6))), Captures = new[] { 5.55f, 5.75f, 6.0f, 6.3f, 6.7f, 7.2f, 8.0f } },
        // Legs out standing still (fight idle) with the player right in front: the fall in place, then shot dead lying there.
        new Seq { Name = "Ctl_DropDeath", Events = LegsOutWith(0f, 0f, (2.5f, Die(6))),
                  Captures = new[] { 0.95f, 1.3f, 1.6f, 1.9f, 2.45f, 2.7f, 3.1f, 3.8f, 4.8f }, Watch = true },
        // Killed mid-fall (legs out, still going down at 1.3 s): Collapse waits for the crawl (OnFloor), then Die(Prone).
        new Seq { Name = "Ctl_FallDeath", Events = LegsOut((1.3f, DieOnFloor(6))), Captures = new[] { 0.95f, 1.3f, 1.6f, 1.9f, 2.2f, 2.6f, 3.2f, 4.2f }, Watch = true },
        // Killed during a transition (Brett 0.1.15 "I killed a dude with a knife and he stayed standing" — #7: a slash to the
        // head, the stab that killed him 31 ms later, still in the hit reaction's 0.08 s blend-in). _Pulse = the 0.1.15 trigger
        // (lost: Any State can't interrupt a running transition), no suffix = the fix (CrossFade into the death).
        new Seq { Name = "Ctl_HitThenDie_Pulse", Events = new[] { (0f, Speed(0f)), (1f, Hit(0)), (1.03f, DiePulse(0)) }, Captures = DeathCaps },
        new Seq { Name = "Ctl_HitThenDie", Events = new[] { (0f, Speed(0f)), (1f, Hit(0)), (1.03f, DieNow(0)) }, Captures = DeathCaps },
        // Limping (#14: a leg hit, the kill later): shot dead as the leg reaction blends back into the limp (1.33 s × 0.92 exit, 0.15 s blend).
        new Seq { Name = "Ctl_LimpBlendDie_Pulse", Events = new[] { (0f, Gait(1)), (0f, Speed(0f)), (1f, Hit(3)), (2.3f, DiePulse(0)) }, Captures = Shift(DeathCaps, 1.3f) },
        new Seq { Name = "Ctl_LimpBlendDie", Events = new[] { (0f, Gait(1)), (0f, Speed(0f)), (1f, Hit(3)), (2.3f, DieNow(0)) }, Captures = Shift(DeathCaps, 1.3f) },
        // Hands up (calm), told to get down: headshot 0.1 s into the drop to his knees (a gait-change blend).
        new Seq { Name = "Ctl_KneelingDie_Pulse", Events = new[] { (0f, Gait(Calm)), (1f, Gait(Kneel)), (1.1f, DiePulse(1)) }, Captures = DeathCaps },
        new Seq { Name = "Ctl_KneelingDie", Events = new[] { (0f, Gait(Calm)), (1f, Gait(Kneel)), (1.1f, DieNow(1, Kneel)) }, Captures = DeathCaps },
        // Ricky (research/27 §3): placed crouched (the brain snaps him into it), the duck at 2 s (a 0.6 s blend into DuckHold).
        // Hips and SOLE say whether DuckHold really is low (not DuckCover's standing start) and whether he stays where he was
        // placed (root x,z: the hold is centred on his mass, MixamoImportSettings.CentredHolds).
        new Seq { Name = "Ctl_Cower", Events = new[] { (0f, Gait(CrouchGait)), (0f, Snap("Crouch")), (2f, Gait(CowerGait)) },
                  Captures = new[] { 1.9f, 2.2f, 2.4f, 2.6f, 3.0f, 4.0f }, Watch = true },
        // Shot dead ducked: ChooseDeath's pick for hips under 0.72 m (the kneel death), and legs out from the crouch (the crawl).
        new Seq { Name = "Ctl_CowerDeath", Events = new[] { (0f, Gait(CrouchGait)), (0f, Snap("Crouch")), (0.5f, Gait(CowerGait)), (2.5f, DieNow(5, CowerGait)) },
                  Captures = new[] { 2.45f, 2.8f, 3.2f, 3.8f, 4.6f, 6.0f }, Watch = true },
        new Seq { Name = "Ctl_CrouchLegsOut", Events = new[] { (0f, Gait(CrouchGait)), (0f, Snap("Crouch")), (1f, Gait(Crawl)), (1f, Rate(0f)) },
                  Captures = new[] { 0.95f, 1.2f, 1.5f, 2.0f, 3.0f }, Watch = true },
        StandingDeath(0, "Front"), StandingDeath(1, "FrontHeadshot"), StandingDeath(2, "Backward"), StandingDeath(3, "Back"), StandingDeath(4, "Forward"),
    };

    /// <summary>What a capture step drives: a single clip or the controller (plus the IntruderAnimator arms-layer fade).</summary>
    sealed class Driver
    {
        public AnimatorControllerPlayable Ctl;
        public bool ArmsOn, Knife, Pistol, GunArms;
        public float Aim;
        /// <summary>Share of the root travel let through (IntruderAnimator's fall travel), 1 = all of it.</summary>
        public float Travel = 1f;
        /// <summary>A death waiting for him to be settled in the crawl (<see cref="DieOnFloor"/>), -1 = none.</summary>
        public int PendingDie = -1;
        /// <summary>Evaluations left before a pulsed trigger is cleared (<see cref="DiePulse"/>), -1 = none.</summary>
        public int PulseLeft = -1;
        // The gun (PROP=IntruderPistol): the aim target (relative to his root), a shot's kick, the prop letting go.
        public Vector3? AimOffset;
        public float Recoil, FiredAt = -1f, T;
        public (Vector3 v, bool toss)? Release;
        /// <summary>IntruderAnimator's aim-pass weight this step: SmoothStep(aim) over the Gun state (GunArms), 1 in the crawl.</summary>
        public float AimWeight;
        public bool Floor;
        float _aim, _floorAim;
        float _arms;
        static readonly int KneelState = Animator.StringToHash("Kneel"), ProneState = Animator.StringToHash("Prone"), GunState = Animator.StringToHash("Gun"),
                            CrawlState = Animator.StringToHash("Crawl");

        // IntruderAnimator.Update: the Arms layer fades in (2.5/s) only while settled in Kneel or Prone, or blending into Prone.
        public void Tick(float dt)
        {
            if (!Ctl.IsValid()) return;
            if (PulseLeft > 0 && --PulseLeft == 0) { Ctl.ResetTrigger("Die"); PulseLeft = -1; }
            int st = Ctl.GetCurrentAnimatorStateInfo(0).shortNameHash;
            if (PendingDie >= 0 && st == CrawlState && !Ctl.IsInTransition(0))
            { Ctl.SetInteger("DeathIndex", PendingDie); Ctl.SetTrigger("Die"); PendingDie = -1; }
            bool settled = !Ctl.IsInTransition(0) ? st == KneelState || st == ProneState
                                                  : Ctl.GetNextAnimatorStateInfo(0).shortNameHash == ProneState;
            _arms = Mathf.MoveTowards(_arms, ArmsOn && settled ? 1f : 0f, 2.5f * dt);
            Ctl.SetLayerWeight(1, _arms);
            if (Ctl.GetLayerCount() > 2) Ctl.SetLayerWeight(2, Knife ? 1f : 0f);   // IntruderAnimator: the Grip layer while armed
            if (Ctl.GetLayerCount() > 4)
            {
                // IntruderAnimator.UpdateGun: pistol fist; the two-hand hold only in the Gun gait's state; Aim ramps over the 0.7 s tell.
                bool gunState = Ctl.GetCurrentAnimatorStateInfo(0).shortNameHash == GunState && !Ctl.IsInTransition(0);
                Ctl.SetLayerWeight(3, Pistol ? 1f : 0f);
                Ctl.SetLayerWeight(4, Pistol && GunArms && gunState ? 1f : 0f);
                _aim = Mathf.MoveTowards(_aim, Aim, dt / (Aim > _aim ? 0.7f : 0.35f));
                Ctl.SetFloat("Aim", Mathf.SmoothStep(0f, 1f, _aim));
                Floor = !Ctl.IsInTransition(0) && Ctl.GetCurrentAnimatorStateInfo(0).shortNameHash == CrawlState;
                _floorAim = Mathf.MoveTowards(_floorAim, Floor ? 1f : 0f, dt / HIS.Intruder.IntruderAnimator.FloorAimSeconds);
                AimWeight = !Pistol || AimOffset == null ? 0f : Mathf.SmoothStep(0f, 1f, _aim) * (gunState ? (GunArms ? 1f : 0f) : _floorAim);
            }
        }
    }

    [MenuItem("HIS/Debug/Render Pose Snapshots")]
    public static void Render()
    {
        var args = Environment.GetCommandLineArgs();
        int ai = Array.IndexOf(args, "-poses");
        // -poses A,B:9 → only these clips/sequences (any clip by name, not just the list); ":9" = 9 evenly spaced samples.
        Dictionary<string, int> only = null;
        if (ai >= 0 && ai + 1 < args.Length)
        {
            only = new Dictionary<string, int>();
            foreach (var item in args[ai + 1].Split(','))
            {
                var parts = item.Split(':');
                only[parts[0]] = parts.Length > 1 && int.TryParse(parts[1], out var k) ? Mathf.Max(2, k) : Norm.Length;
            }
        }
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        { Debug.LogError("[HIS] POSES: no GPU (-nographics) — run with GRAPHICS=1 scripts/unity-batch.sh PoseSnapshots.Render"); return; }
        Directory.CreateDirectory(OutDir);
        bool async = ShaderUtil.allowAsyncCompilation;
        ShaderUtil.allowAsyncCompilation = false;   // else the first renders use the placeholder shader

        var clips = new Dictionary<string, AnimationClip>();
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { AnimDir }))
            foreach (var c in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<AnimationClip>())
                if (!c.name.StartsWith("__preview__")) clips[c.name] = c;

        // Headless: a throwaway single scene (nothing is saved). In the Editor: an additive one, closed afterwards.
        bool single = Application.isBatchMode;
        var prevActive = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        using var stage = new Stage();
        var report = new StringBuilder($"# Pose snapshots ({DateTime.Now:yyyy-MM-dd HH:mm}) — SOLE = lowest skin vertex y (cm), hips/head y (m), hand→head (m), root x,z (m)\n");
        int n = 0;
        try
        {
            var seqs = Sequences;
            foreach (var name in only != null ? only.Keys.Where(k => seqs.All(q => q.Name != k)) : Clips)
            {
                if (!clips.TryGetValue(name, out var clip)) { report.AppendLine($"\n## {name}: MISSING"); continue; }
                int cols = only != null ? only[name] : Norm.Length;
                stage.RenderClip(clip, Enumerable.Range(0, cols).Select(i => i / (cols - 1f)).ToArray(), report); n++;
            }
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            foreach (var seq in seqs)
            {
                if (only != null && !only.ContainsKey(seq.Name)) continue;
                stage.RenderSequence(seq, ctrl, report); n++;
            }
        }
        finally
        {
            stage.Dispose();
            ShaderUtil.allowAsyncCompilation = async;
            if (!single)
            {
                if (prevActive.IsValid() && prevActive.isLoaded) SceneManager.SetActiveScene(prevActive);
                EditorSceneManager.CloseScene(scene, true);
            }
        }
        File.WriteAllText(Path.Combine(OutDir, "poses.txt"), report.ToString());
        Debug.Log($"[HIS] POSES rendered {n} sheets → {OutDir}/");
    }

    /// <summary>The posed character, backdrops, light and cameras (all on layer 31, in a throwaway scene).</summary>
    sealed class Stage : IDisposable
    {
        readonly List<Object> _trash = new();
        readonly GameObject _go;
        readonly Animator _anim;
        readonly Transform _root;
        readonly SkinnedMeshRenderer[] _skins;
        readonly Mesh[] _baked;
        readonly Transform[] _bakedT;
        readonly Camera _front, _side, _persp, _closeA, _closeB;
        readonly Transform _rHand;
        readonly bool _knife;
        // The prop as a game object that can leave his hand (IntruderWeapon), put back in his fist before every sheet.
        readonly HIS.Intruder.IntruderWeapon _prop;
        readonly Transform _propSocket, _flash, _target;
        readonly HIS.Intruder.IntruderAnimator.AimRig _aimRig;
        readonly Vector3 _propPos; readonly Quaternion _propRot;
        readonly List<Renderer> _propRenderers = new();
        readonly SimulationMode _simMode;
        Driver _d;
        int Rows => _knife ? 5 : 3;
        readonly RenderTexture _rt;
        readonly Texture2D _read;
        readonly List<Vector3> _verts = new();
        readonly SkinProbe _probe;

        public Stage()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            _go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(_go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            _trash.Add(_go);
            // The game components would steer/swallow root motion; here the Animator applies it (like ClipMetrics.Trace).
            foreach (var ia in _go.GetComponentsInChildren<HIS.Intruder.IntruderAnimator>(true)) Object.DestroyImmediate(ia);
            _anim = _go.GetComponentInChildren<Animator>();
            _anim.applyRootMotion = true;
            _root = _anim.transform;
            _probe = new SkinProbe(_anim);   // leg skin picked in the prefab's own pose

            // Clay: the game's lit shader (URP, minus the Depth-API occlusion) with a flat light albedo — his black clothes
            // hide the limbs; shading alone shows twists and contacts. CLAY=0 keeps his textures.
            var src = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Materials/Intruder_Alex.mat");
            var skinMat = new Material(Shader.Find("HIS/LitPBR")); _trash.Add(skinMat);
            if (Environment.GetEnvironmentVariable("CLAY") == "0") { if (src != null) skinMat.CopyMatchingPropertiesFromMaterial(src); }
            else skinMat.SetColor("_Color", new Color(0.78f, 0.66f, 0.55f));
            skinMat.SetFloat("_Metallic", 0f);
            skinMat.SetFloat("_Glossiness", 0.2f);
            foreach (var r in _go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            _skins = _go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s.sharedMesh != null).ToArray();
            _baked = new Mesh[_skins.Length]; _bakedT = new Transform[_skins.Length];
            for (int i = 0; i < _skins.Length; i++)
            {
                _baked[i] = new Mesh { name = "Baked" }; _trash.Add(_baked[i]);
                var b = New("Baked_" + _skins[i].name);
                b.AddComponent<MeshFilter>().sharedMesh = _baked[i];
                b.AddComponent<MeshRenderer>().sharedMaterials = Enumerable.Repeat(skinMat, _skins[i].sharedMaterials.Length).ToArray();
                _bakedT[i] = b.transform;
            }

            // Grid backdrops: behind him for the front view (z = -6) and for the side view (x = -6), and the floor.
            var grid = GridTexture(); _trash.Add(grid);
            var gridMat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = grid }; _trash.Add(gridMat);
            gridMat.mainTextureScale = new Vector2(40f, 40f);
            var red = new Material(Shader.Find("Unlit/Color")); red.color = new Color(0.9f, 0.1f, 0.1f); _trash.Add(red);
            Quad(new Vector3(0f, 0f, -6f), Vector3.forward, new Vector3(40f, 40f, 1f), gridMat);     // faces the front camera
            Quad(new Vector3(-6f, 0f, 0f), Vector3.right, new Vector3(40f, 40f, 1f), gridMat);       // faces the side camera
            Quad(new Vector3(0f, -0.001f, 0f), Vector3.up, new Vector3(40f, 40f, 1f), gridMat);      // the floor
            Quad(new Vector3(0f, 0f, -5.98f), Vector3.forward, new Vector3(40f, 0.008f, 1f), red);   // y = 0
            Quad(new Vector3(-5.98f, 0f, 0f), Vector3.right, new Vector3(40f, 0.008f, 1f), red);
            // The floor a dropped prop lands on (physics stepped by hand in Run).
            var floorCol = New("FloorCollider").AddComponent<BoxCollider>();
            floorCol.center = new Vector3(0f, -0.5f, 0f); floorCol.size = new Vector3(40f, 1f, 40f);
            _simMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;

            var light = New("Light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.1f; light.cullingMask = 1 << Layer; light.shadows = LightShadows.None;
            light.transform.rotation = Quaternion.LookRotation(new Vector3(-0.45f, -0.65f, -0.6f));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f);
            RenderSettings.skybox = null;

            _front = Cam("Front", true); _side = Cam("Side", true); _persp = Cam("Persp", false);

            // KNIFE=1: the knife prop in his right hand (IntruderBuilder.AddKnife), in its own textures, plus hand close-ups.
            // PROP=IntruderPistol: the gunman's Glock instead (red dot on its Muzzle).
            var propName = Environment.GetEnvironmentVariable("PROP") ?? (Environment.GetEnvironmentVariable("KNIFE") == "1" ? "IntruderKnife" : null);
            var knife = propName != null ? _go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == propName) : null;
            if (knife != null)
            {
                _knife = true;
                _rHand = _anim.GetBoneTransform(HumanBodyBones.RightHand);
                knife.gameObject.SetActive(true);
                _prop = knife.GetComponent<HIS.Intruder.IntruderWeapon>();
                _propSocket = knife.parent; _propPos = knife.localPosition; _propRot = knife.localRotation;
                _trash.Add(knife.gameObject);   // once dropped it's no longer under him
                foreach (var r in knife.GetComponentsInChildren<Renderer>(true))
                {
                    var m = new Material(Shader.Find("HIS/LitPBR")); _trash.Add(m);
                    if (r.sharedMaterial != null) m.CopyMatchingPropertiesFromMaterial(r.sharedMaterial);
                    r.sharedMaterial = m; r.enabled = true;
                }
                var tip = GameObject.CreatePrimitive(PrimitiveType.Sphere); _trash.Add(tip);
                Object.DestroyImmediate(tip.GetComponent<Collider>());
                var muzzle = knife.Find("Muzzle");
                tip.transform.SetParent(muzzle != null ? muzzle : knife, false); tip.transform.localPosition = muzzle != null ? Vector3.zero : new Vector3(0f, 0f, 0.15f);
                tip.transform.localScale = Vector3.one * 0.018f;
                if (muzzle != null)
                {
                    // The muzzle ray (what the judge reads), 2 m of it.
                    var ray = GameObject.CreatePrimitive(PrimitiveType.Cube); _trash.Add(ray);
                    Object.DestroyImmediate(ray.GetComponent<Collider>());
                    ray.transform.SetParent(muzzle, false); ray.transform.localPosition = new Vector3(0f, 0f, 2f); ray.transform.localScale = new Vector3(0.006f, 0.006f, 4f);
                    ray.GetComponent<MeshRenderer>().sharedMaterial = red;
                }
                tip.GetComponent<MeshRenderer>().sharedMaterial = red;
                if (muzzle != null)
                {
                    _flash = HIS.Intruder.IntruderGunFx.MakeFlash(muzzle); _trash.Add(_flash.gameObject);
                    _flash.localScale = Vector3.one * 0.14f;
                    _aimRig = new HIS.Intruder.IntruderAnimator.AimRig(_anim);
                    var green = new Material(Shader.Find("Unlit/Color")); green.color = new Color(0.1f, 0.8f, 0.2f); _trash.Add(green);
                    var tg = GameObject.CreatePrimitive(PrimitiveType.Sphere); _trash.Add(tg);
                    Object.DestroyImmediate(tg.GetComponent<Collider>());
                    tg.layer = Layer; tg.transform.localScale = Vector3.one * 0.12f; tg.GetComponent<MeshRenderer>().sharedMaterial = green;
                    _target = tg.transform; tg.SetActive(false);
                }
                foreach (var t in knife.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
                _propRenderers.AddRange(knife.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponent<MeshFilter>() is { sharedMesh: { } mesh } && mesh.name is not ("Sphere" or "Cube" or "Quad")));
                _closeA = Cam("CloseA", false); _closeB = Cam("CloseB", false);
                _closeA.fieldOfView = _closeB.fieldOfView = 34f; _closeA.nearClipPlane = _closeB.nearClipPlane = 0.02f;
            }
            _rt = new RenderTexture(Cell, Cell, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 }; _trash.Add(_rt);
            _read = new Texture2D(Cell, Cell, TextureFormat.RGBA32, false); _trash.Add(_read);
        }

        GameObject New(string name)
        {
            var g = new GameObject(name) { layer = Layer };
            _trash.Add(g);
            return g;
        }

        /// <summary>A quad whose visible face points along <paramref name="normal"/> (Unity's quad shows its -Z side). The 40 m
        /// grid quads get one texture tile per metre, aligned to whole metres (they span -20..20).</summary>
        void Quad(Vector3 pos, Vector3 normal, Vector3 scale, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); q.layer = Layer; _trash.Add(q);
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-normal, normal == Vector3.up ? Vector3.forward : Vector3.up));
            q.transform.localScale = scale;
            q.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        Camera Cam(string name, bool ortho)
        {
            var c = New("Cam" + name).AddComponent<Camera>();
            c.enabled = false; c.cullingMask = 1 << Layer;
            c.clearFlags = CameraClearFlags.SolidColor; c.backgroundColor = new Color(0.25f, 0.25f, 0.28f);
            c.orthographic = ortho; c.orthographicSize = 1.15f; c.fieldOfView = 42f;
            c.nearClipPlane = 0.05f; c.farClipPlane = 40f;
            return c;
        }

        static Texture2D GridTexture()
        {
            const int px = 200; // one metre
            var t = new Texture2D(px, px, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var c = new Color32[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    bool bold = x < 2 || y < 2 || Mathf.Abs(x - px / 2) < 1 || Mathf.Abs(y - px / 2) < 1;
                    bool thin = x % (px / 10) == 0 || y % (px / 10) == 0;
                    byte v = bold ? (byte)70 : thin ? (byte)150 : (byte)205;
                    c[y * px + x] = new Color32(v, v, (byte)Mathf.Min(255, v + 8), 255);
                }
            t.SetPixels32(c); t.Apply(true);
            return t;
        }

        // ── Sampling
        public void RenderClip(AnimationClip clip, float[] norm, StringBuilder report)
        {
            var graph = PlayableGraph.Create("PoseSnapshots");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var output = AnimationPlayableOutput.Create(graph, "out", _anim);
                var p = AnimationClipPlayable.Create(graph, clip);
                p.SetApplyFootIK(false);
                output.SetSourcePlayable(p);
                var times = norm.Select(x => x * clip.length).ToArray();
                var title = $"{clip.name}  {clip.length:F2}S  {(clip.isLooping ? "LOOP" : "ONCE")}";
                Run(graph, null, times, Array.Empty<(float, Action<Driver>)>(), clip.name, title, report, norm);
            }
            finally { graph.Destroy(); }
        }

        public void RenderSequence(Seq seq, RuntimeAnimatorController ctrl, StringBuilder report)
        {
            var graph = PlayableGraph.Create("PoseSnapshots");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                var output = AnimationPlayableOutput.Create(graph, "out", _anim);
                var d = new Driver { Ctl = AnimatorControllerPlayable.Create(graph, ctrl) };
                output.SetSourcePlayable(d.Ctl);
                Run(graph, d, seq.Captures, seq.Events, seq.Name, seq.Name + "  (CONTROLLER)", report, null, seq.Watch);
            }
            finally { graph.Destroy(); }
        }

        void Run(PlayableGraph graph, Driver d, float[] captures, (float t, Action<Driver> act)[] events, string file, string title,
                 StringBuilder report, float[] norm, bool watch = false)
        {
            _root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            ResetProp();
            _d = d;
            graph.Evaluate(0f);
            _root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var ev = events.OrderBy(e => e.t).ToList();
            int W = captures.Length * Cell, H = Rows * Cell + Header;
            var sheet = new Color32[W * H];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(20, 20, 24, 255);
            Text(sheet, W, H, 4, 5, title, new Color32(255, 255, 255, 255));
            report.AppendLine($"\n## {title}");
            float t = 0f; int ci = 0, ei = 0;
            // Watch: the worst of every step (lowest skin, lowest leg skin, fastest flat hips step, most on his back while down).
            int spikes = 0;
            float downAt = -1f, slide = 0f;   // SLIDE: how far the hips move over the floor in the 0.6 s after they first get down (< 0.3 m)
            float wBody = float.MaxValue, wLegs = float.MaxValue, wSpeed = 0f, wBack = -1f, tBody = 0f, tLegs = 0f, tSpeed = 0f, tBack = 0f;
            var prevHips = _anim.GetBoneTransform(HumanBodyBones.Hips).position;
            while (ci < captures.Length)
            {
                while (ei < ev.Count && ev[ei].t <= t + 1e-4f) { ev[ei].act(d); ei++; }
                if (t >= captures[ci] - 1e-4f)
                {
                    string label = norm != null ? $"N{norm[ci]:F2} T{t:F2}" : $"T{t:F2}";
                    Capture(sheet, W, H, ci, label, report);
                    ci++; continue;
                }
                float next = captures[ci];
                if (ei < ev.Count) next = Mathf.Min(next, ev[ei].t);
                float dt = Mathf.Min(Step, Mathf.Max(next - t, 1e-4f));
                var before = _root.position;
                graph.Evaluate(dt); t += dt;
                _root.rotation = Quaternion.identity;   // the game applies root travel only (IntruderAnimator.OnAnimatorMove)
                if (d != null && d.Travel < 1f)
                {
                    var moved = _root.position - before;
                    _root.position = before + new Vector3(moved.x * d.Travel, moved.y, moved.z * d.Travel);
                }
                if (watch)
                {
                    _probe.Lowest(out float body, out float legs);
                    var hp = _anim.GetBoneTransform(HumanBodyBones.Hips).position;
                    float speed = new Vector2(hp.x - prevHips.x, hp.z - prevHips.z).magnitude / dt;
                    float back = hp.y < 0.45f ? Vector3.Dot(SkinProbe.BellyDirection(_anim), Vector3.up) : -1f;
                    if (body < wBody) { wBody = body; tBody = t; }
                    if (legs < wLegs) { wLegs = legs; tLegs = t; }
                    if (speed > wSpeed) { wSpeed = speed; tSpeed = t; }
                    if (speed > 3.5f && spikes++ < 6) report.AppendLine($"    ! t{t:F3} hips step {speed * dt * 100f:F1} cm ({speed:F1} m/s)");
                    if (back > wBack) { wBack = back; tBack = t; }
                    if (downAt < 0f && hp.y < 0.3f) downAt = t;
                    if (downAt >= 0f && t - downAt <= 0.6f) slide += new Vector2(hp.x - prevHips.x, hp.z - prevHips.z).magnitude;
                    prevHips = hp;
                }
                if (_prop != null && !_prop.InHand) Physics.Simulate(dt);
                if (Trace && Mathf.Repeat(t + 1e-4f, 0.05f) < dt)
                {
                    var hp = _anim.GetBoneTransform(HumanBodyBones.Hips).position;
                    var st = d != null ? d.Ctl.GetCurrentAnimatorStateInfo(0) : default;
                    report.AppendLine($"    ~{t:F2} root {_root.position.x:F2},{_root.position.z:F2} hips {hp.x:F2},{hp.y:F2},{hp.z:F2}{(d != null ? $" state {st.shortNameHash} n{st.normalizedTime:F2}{(d.Ctl.IsInTransition(0) ? " (blend)" : "")}" : "")}");
                }
                d?.Tick(dt);
                if (d != null) GunStep(d, dt, t);
            }
            if (watch)
            {
                string w = $"WATCH SOLE{wBody * 100f:+0;-0}@{tBody:F2} LEGS{wLegs * 100f:+0;-0}@{tLegs:F2} HIPS{wSpeed:F1}M/S@{tSpeed:F2} BACK{wBack:F2}@{tBack:F2} SLIDE{slide:F2}";
                Text(sheet, W, H, 4 + (title.Length + 3) * 8, 5, w, wLegs < -0.05f || slide > 0.45f || wBack > 0.5f ? new Color32(255, 90, 90, 255) : new Color32(140, 255, 140, 255));
                report.AppendLine($"  {w}");
            }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.SetPixels32(sheet); tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, file + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        /// <summary>The prop back in his fist (as built), visible, InHand — whatever the last sheet did with it.</summary>
        void ResetProp()
        {
            if (_prop == null) return;
            if (_prop.TryGetComponent<Rigidbody>(out var rb)) Object.DestroyImmediate(rb);
            if (_prop.TryGetComponent<BoxCollider>(out var col)) Object.DestroyImmediate(col);
            _prop.transform.SetParent(_propSocket, false);
            _prop.transform.SetLocalPositionAndRotation(_propPos, _propRot);
            _prop.Show(true);
            if (_flash != null) _flash.gameObject.SetActive(false);
        }

        /// <summary>After the pose: the prop let go (the game's Drop/Toss), then IntruderAnimator's aim pass + kick, the flash.</summary>
        void GunStep(Driver d, float dt, float t)
        {
            d.T = t;
            if (_prop == null) return;
            if (d.Release is var (v, toss))
            {
                d.Release = null; d.Pistol = false;   // the grip layer lets go with it (IntruderAnimator: HasPistol false)
                if (toss) _prop.Toss(v); else _prop.Drop(v);
            }
            var m = _prop.Muzzle;
            if (m == null) return;
            if (d.AimOffset is { } off && _prop.InHand)
            {
                _target.gameObject.SetActive(true);
                _target.position = _root.position + off;
                _aimRig.Apply(m, _target.position, d.AimWeight, d.Floor, d.Recoil);
            }
            else if (_target != null) _target.gameObject.SetActive(false);
            d.Recoil = Mathf.MoveTowards(d.Recoil, 0f, dt / 0.15f);
            _flash.gameObject.SetActive(d.FiredAt >= 0f && t - d.FiredAt < 0.05f && _prop.InHand);
        }

        void Capture(Color32[] sheet, int W, int H, int col, string label, StringBuilder report)
        {
            // Bake the skinned pose (the renderers don't skin outside the player loop) and measure the lowest skin vertex.
            float sole = float.MaxValue;
            for (int i = 0; i < _skins.Length; i++)
            {
                _skins[i].BakeMesh(_baked[i], true);
                _bakedT[i].SetPositionAndRotation(_skins[i].transform.position, _skins[i].transform.rotation);
                _baked[i].RecalculateBounds();
                _baked[i].GetVertices(_verts);
                foreach (var v in _verts) sole = Mathf.Min(sole, _bakedT[i].TransformPoint(v).y);
            }
            _probe.Lowest(out _, out float legs);
            var hips = _anim.GetBoneTransform(HumanBodyBones.Hips).position;
            var head = _anim.GetBoneTransform(HumanBodyBones.Head).position;
            float lh = Vector3.Distance(_anim.GetBoneTransform(HumanBodyBones.LeftHand).position, head);
            float rh = Vector3.Distance(_anim.GetBoneTransform(HumanBodyBones.RightHand).position, head);
            // The gun: aim error to the target (the judge's cone is 10°), or where the dropped prop lies (lowest point, cm; from him).
            string gun = ""; float aimErr = -1f;
            if (_prop != null && _prop.Muzzle != null && _prop.InHand && _d?.AimOffset != null && _target.gameObject.activeSelf)
            {
                aimErr = Vector3.Angle(_prop.Muzzle.forward, _target.position - _prop.Muzzle.position);
                gun = $"AIM ERR {aimErr:F1} W{_d.AimWeight:F2}{(_flash.gameObject.activeSelf ? " FIRE" : "")}";
            }
            else if (_prop != null && !_prop.InHand && _propRenderers.Count > 0)
            {
                var b = _propRenderers[0].bounds; foreach (var r in _propRenderers) b.Encapsulate(r.bounds);
                gun = $"PROP Y{b.min.y * 100f:+0.0;-0.0} D{Vector3.Distance(new Vector3(b.center.x, 0f, b.center.z), new Vector3(_root.position.x, 0f, _root.position.z)):F2}";
            }
            report.AppendLine($"  {label}: {(gun != "" ? gun + " | " : "")}SOLE {sole * 100f:+0.0;-0.0} cm legs {legs * 100f:+0.0;-0.0} | hips {hips.y:F2} head {head.y:F2} | hand→head L {lh:F2} R {rh:F2} | root {_root.position.x:F2},{_root.position.z:F2} | hips xz {hips.x:F2},{hips.z:F2} | head xz {head.x - _root.position.x:F2},{head.z - _root.position.z:F2} | hips yaw {Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(_anim.GetBoneTransform(HumanBodyBones.Hips).forward, Vector3.up), Vector3.up):F0}");

            // Ortho cameras centred on the hips (the grid is world-fixed, so travel shows); 3/4 view from front-right above.
            _front.transform.SetPositionAndRotation(new Vector3(hips.x, 0.95f, 10f), Quaternion.LookRotation(Vector3.back));
            _side.transform.SetPositionAndRotation(new Vector3(10f, 0.95f, hips.z), Quaternion.LookRotation(Vector3.left));
            var focus = new Vector3(hips.x, 0.55f, hips.z);
            _persp.transform.position = focus + new Vector3(2.1f, 1.25f, 2.5f);
            _persp.transform.LookAt(focus);
            var cams = new[] { _front, _side, _persp, _closeA, _closeB };
            if (_knife && _prop != null && !_prop.InHand && _propRenderers.Count > 0)
            {
                // Let go: both close-ups on the prop (from above-front-right, and low from the side to read floor contact).
                var b = _propRenderers[0].bounds; foreach (var r in _propRenderers) b.Encapsulate(r.bounds);
                var at = b.center;
                _closeA.transform.position = at + new Vector3(0.3f, 0.35f, 0.3f); _closeA.transform.LookAt(at);
                _closeB.transform.position = at + new Vector3(0.45f, 0.03f, 0.1f); _closeB.transform.LookAt(at);
            }
            else if (_knife)
            {
                var at = Vector3.Lerp(_rHand.position, _anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position, 0.6f);
                _closeA.transform.position = at + new Vector3(0.3f, 0.12f, 0.3f); _closeA.transform.LookAt(at);
                _closeB.transform.position = at + new Vector3(0.05f, -0.3f, 0.3f); _closeB.transform.LookAt(at);
            }
            for (int row = 0; row < Rows; row++)
            {
                var c = cams[row];
                c.targetTexture = _rt; c.Render(); c.targetTexture = null;
                var prev = RenderTexture.active; RenderTexture.active = _rt;
                _read.ReadPixels(new Rect(0, 0, Cell, Cell), 0, 0); _read.Apply(false);
                RenderTexture.active = prev;
                var px = _read.GetPixels32();
                int x0 = col * Cell, yTop = Header + row * Cell;           // top-left in sheet (top-down)
                for (int y = 0; y < Cell; y++)
                {
                    int dst = (H - 1 - (yTop + (Cell - 1 - y))) * W + x0;  // texture rows are bottom-up
                    Array.Copy(px, y * Cell, sheet, dst, Cell);
                }
                // Cell border + label band.
                for (int y = 0; y < Cell; y++) { Put(sheet, W, H, x0, yTop + y, Border); }
                for (int x = 0; x < Cell; x++) { Put(sheet, W, H, x0 + x, yTop, Border); }
                if (row == 0)
                {
                    Band(sheet, W, H, x0 + 1, yTop + 1, Cell - 2, 34);
                    Text(sheet, W, H, x0 + 4, yTop + 4, label, Ink);
                    Text(sheet, W, H, x0 + 4, yTop + 16, $"SOLE{sole * 100f:+0;-0} LEG{legs * 100f:+0;-0} HIP{hips.y:F2} H{head.y:F2}", sole < -0.02f || sole > 0.03f || legs < -0.03f ? Warn : Ink);
                }
                if (row == 1)
                {
                    Band(sheet, W, H, x0 + 1, yTop + 1, Cell - 2, gun != "" ? 34 : 22);
                    Text(sheet, W, H, x0 + 4, yTop + 4, $"HANDS-HEAD L{lh:F2} R{rh:F2}", Ink);
                    if (gun != "") Text(sheet, W, H, x0 + 4, yTop + 16, gun, aimErr > 5f && _d.AimWeight > 0.99f ? Warn : Ink);   // red: fully up yet off the chest
                }
            }
        }

        public void Dispose()
        {
            _probe?.Dispose();
            Physics.simulationMode = _simMode;
            foreach (var o in _trash) if (o != null) Object.DestroyImmediate(o);
            _trash.Clear();
        }
    }

    // ── Sheet drawing: a 3×5 pixel font (×2) so labels need no font rendering.
    internal static readonly Color32 Ink = new(0, 0, 0, 255), Warn = new(200, 0, 0, 255), Border = new(40, 40, 40, 255);

    internal static void Put(Color32[] buf, int W, int H, int x, int y, Color32 c)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        buf[(H - 1 - y) * W + x] = c;
    }

    internal static void Band(Color32[] buf, int W, int H, int x, int y, int w, int h)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int xx = x + i, yy = y + j; if (xx >= W || yy >= H) continue;
                ref var p = ref buf[(H - 1 - yy) * W + xx];
                p = new Color32((byte)((p.r + 255 * 3) / 4), (byte)((p.g + 255 * 3) / 4), (byte)((p.b + 255 * 3) / 4), 255);
            }
    }

    internal static void Text(Color32[] buf, int W, int H, int x, int y, string s, Color32 c)
    {
        const int scale = 2;
        foreach (var ch in s.ToUpperInvariant())
        {
            if (Glyphs.TryGetValue(ch, out var g))
                for (int r = 0; r < 5; r++)
                    for (int q = 0; q < 3; q++)
                        if (g[r * 3 + q] == '1')
                            for (int sy = 0; sy < scale; sy++)
                                for (int sx = 0; sx < scale; sx++)
                                    Put(buf, W, H, x + q * scale + sx, y + r * scale + sy, c);
            x += 4 * scale;
        }
    }

    static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "111001111100111", ['3'] = "111001111001111",
        ['4'] = "101101111001001", ['5'] = "111100111001111", ['6'] = "111100111101111", ['7'] = "111001001001001",
        ['8'] = "111101111101111", ['9'] = "111101111001111", ['.'] = "000000000000010", ['-'] = "000000111000000",
        ['+'] = "000010111010000", [':'] = "000010000010000", ['/'] = "001001010100100", ['('] = "010100100100010",
        [')'] = "010001001001010", ['_'] = "000000000000111",
        ['A'] = "010101111101101", ['B'] = "110101110101110", ['C'] = "011100100100011", ['D'] = "110101101101110",
        ['E'] = "111100110100111", ['F'] = "111100110100100", ['G'] = "011100101101011", ['H'] = "101101111101101",
        ['I'] = "111010010010111", ['J'] = "001001001101010", ['K'] = "101101110101101", ['L'] = "100100100100111",
        ['M'] = "101111111101101", ['N'] = "110101101101101", ['O'] = "010101101101010", ['P'] = "110101110100100",
        ['Q'] = "010101101110011", ['R'] = "110101110101101", ['S'] = "011100010001110", ['T'] = "111010010010010",
        ['U'] = "101101101101111", ['V'] = "101101101101010", ['W'] = "101101111111101", ['X'] = "101101010101101",
        ['Y'] = "101101010010010", ['Z'] = "111001010100111",
    };
}
