using System;
using HIS.Scenario;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless regression test for the lean judge (<see cref="Judge"/>): every rule of Brett's PLAN.md §2 summary, the
/// unarmed-standing timer boundaries, shots through the closed door and the scenario aggregation, then the Marshal's role switch
/// and night cards (research/27 §4). Plain asserts instead of the Test Framework so it
/// runs from the menu (HIS > Debug > Judge Self-Test) or headless while no GUI Editor owns the project:
///   Unity -batchmode -nographics -projectPath . -executeMethod JudgeSelfTest.Run -logFile Logs/judge.log
/// Batch mode exits 1 on any failure, 0 otherwise. Grep the log for "[HIS] JUDGE".
/// </summary>
public static class JudgeSelfTest
{
    static int s_pass, s_total;

    static readonly ScenarioDef s_def = new()
    {
        Name = "Self-test", Intent = Intent.Burglar, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Knife,
        Identity = Identity.Stranger, WhoItWas = "A burglar with a knife", VoicePrefix = "intruder",
    };

    [MenuItem("HIS/Debug/Judge Self-Test")]
    public static void Run()
    {
        s_pass = s_total = 0;
        // The cases assume the shipped tunables and the homeowner's role; put back whatever a live session tuned or played
        // afterwards (a Marshal night sets Judge.Role).
        float good = Judge.StandingGoodSeconds, bad = Judge.StandingBadSeconds, grace = Judge.ThreatGraceSeconds;
        float graceClose = Judge.GraceCloseSeconds, strike = Judge.StrikeDistance, downReach = Judge.DownReachExtra;
        var role = Judge.Role;
        Judge.StandingGoodSeconds = 3f; Judge.StandingBadSeconds = 8f; Judge.ThreatGraceSeconds = 1f;
        Judge.GraceCloseSeconds = 0.5f; Judge.StrikeDistance = 2.5f; Judge.DownReachExtra = 0.5f;
        Judge.Role = Role.Homeowner;
        try
        {
            PerHit();
            Gun();
            Scenarios();
            PlayerWounds();
            Dispatcher();
            FirearmYears();
            Marshal();
        }
        catch (Exception ex)
        {
            s_total++;
            Fail("harness", "no exception", ex.ToString());
        }
        finally
        {
            Judge.StandingGoodSeconds = good; Judge.StandingBadSeconds = bad; Judge.ThreatGraceSeconds = grace;
            Judge.GraceCloseSeconds = graceClose; Judge.StrikeDistance = strike; Judge.DownReachExtra = downReach;
            Judge.Role = role;
        }

        string summary = $"[HIS] JUDGE {s_pass}/{s_total} passed";
        if (s_pass == s_total) Debug.Log(summary); else Debug.LogError(summary);
        // Only headless runs quit; exiting the GUI Editor would kill the tooling loop (same rule as BuildScript).
        if (Application.isBatchMode) EditorApplication.Exit(s_pass == s_total ? 0 : 1);
    }

    // ---------------------------------------------------------------- one hit at a time (rules 0–8)

    static void PerHit()
    {
        const WeaponKind Knife = WeaponKind.Knife;

        // 1. Active threat
        ExpectHit("attacking with bare hands", F(attacking: true, distance: 1f), Tier.Good, false, "attacking you");
        ExpectHit("came at you with a knife", F(weapon: Knife, advancing: true, distance: 5f), Tier.Good, false, "came at you with a knife");
        ExpectHit("standing within reach with a knife", F(weapon: Knife, distance: 2f), Tier.Good, false, "close enough");

        // 2. Reaction grace
        ExpectHit("0.3 s after he turned to run", F(fleeing: true, distance: 3f, sinceThreat: 0.3f), Tier.Good, false, "turned to run");
        ExpectHit("0.8 s after he turned to run (close)", F(fleeing: true, distance: 3f, sinceThreat: 0.8f), Tier.Good, true, "turned to run");
        ExpectHit("0.4 s after he put his hands up", F(posture: Posture.HandsUp, sinceThreat: 0.4f), Tier.Good, false, "given up");
        ExpectHit("1.5 s after he turned to run", F(fleeing: true, distance: 3f, sinceThreat: 1.5f), Tier.Bad, false, "running away");

        // 3. Down
        ExpectHit("already down", F(down: true, distance: 2f), Tier.Bad, false, "already down");
        ExpectHit("down, knife still in hand, close", F(down: true, weapon: Knife, distance: 1.5f), Tier.Bad, false, "already down");
        // 3b. Down but still coming with the knife (Brett 0.1.10: "running at me with a knife and it said bad call")
        ExpectHit("down, knife, clawing at you from the floor", F(down: true, weapon: Knife, attacking: true, distance: 1.2f), Tier.Good, false, "down, but still coming at you with the knife");
        ExpectHit("down, knife, crawling at you from 2.8 m", F(down: true, weapon: Knife, advancing: true, distance: 2.8f), Tier.Good, false, "still coming at you");
        ExpectHit("down, knife, crawling at you from 3.0 m (edge)", F(down: true, weapon: Knife, advancing: true, distance: 3f), Tier.Good, false, "still coming at you");
        ExpectHit("down, knife, crawling toward you from 4 m", F(down: true, weapon: Knife, advancing: true, distance: 4f), Tier.Bad, false, "already down");
        ExpectHit("down, empty hands, crawling at you", F(down: true, advancing: true, distance: 1.2f), Tier.Bad, false, "already down");
        ExpectHit("down, dropped the knife, in reach", F(down: true, distance: 1.2f), Tier.Bad, false, "already down");
        ExpectHit("down, knife, crawling at you, only just went down", F(down: true, weapon: Knife, advancing: true, distance: 2f, sinceThreat: 0.2f), Tier.Good, false, "still coming at you");

        // 4. Surrender
        ExpectHit("hands up and empty", F(posture: Posture.HandsUp), Tier.Bad, false, "hands were up and empty");
        ExpectHit("prone and empty", F(posture: Posture.Prone), Tier.Bad, false, "face-down");
        ExpectHit("hands up, still holding the knife", F(posture: Posture.HandsUp, weapon: Knife), Tier.Good, true, "still holding the knife");
        ExpectHit("hands up with knife within 2.5 m", F(posture: Posture.HandsUp, weapon: Knife, distance: 1.5f), Tier.Good, true, "still holding the knife");
        ExpectHit("prone, still holding the knife", F(posture: Posture.Prone, weapon: Knife), Tier.Good, true, "on the ground");

        // 5. Fleeing
        ExpectHit("fleeing, empty hands", F(fleeing: true), Tier.Bad, false, "running away");
        ExpectHit("fleeing with the knife", F(fleeing: true, weapon: Knife), Tier.Bad, false, "knife in his hand");
        ExpectHit("fleeing with the knife past you at 2 m", F(fleeing: true, weapon: Knife, distance: 2f), Tier.Bad, false, "knife in his hand");

        // 6. Identified
        ExpectHit("identified neighbor, empty hands", F(forced: false, identity: true), Tier.Bad, false, "told you who he was");
        ExpectHit("identified after kicking the door in", F(identity: true, standing: 1f), Tier.Bad, false, "told you who he was");

        // 7. Walked in
        ExpectHit("walked-in innocent", F(forced: false), Tier.Bad, false, "unlocked door");
        ExpectHit("shot through the door while he only knocked", F(forced: false, inside: false), Tier.Bad, false, "still outside");
        ExpectHit("let in, unarmed, walking at you (research/25)", F(forced: false, advancing: true), Tier.Bad, false, "coming toward you");
        ExpectHit("walked in holding a knife, not advancing", F(forced: false, weapon: Knife), Tier.Good, true, "walked in holding a knife");

        // 8. Kicked-in door + unarmed-standing house rule
        ExpectHit("broke in and coming at you, unarmed", F(advancing: true), Tier.Good, false, "coming at you");
        ExpectHit("broke in holding a knife, standing back", F(weapon: Knife), Tier.Good, false, "holding a knife");
        ExpectHit("standing timer 0 s", F(standing: 0f), Tier.Good, false, "just kicked your door in");
        ExpectHit("standing timer 2.9 s", F(standing: 2.9f), Tier.Good, false, "just kicked your door in");
        ExpectHit("standing timer 3.0 s (edge)", F(standing: 3f), Tier.Good, false, "just kicked your door in");
        ExpectHit("standing timer 5 s (close)", F(standing: 5f), Tier.Good, true, "unarmed for 5 seconds");
        ExpectHit("standing timer 7.9 s (close)", F(standing: 7.9f), Tier.Good, true, "unarmed for 7 seconds");
        ExpectHit("standing timer 8.0 s (edge)", F(standing: 8f), Tier.Bad, false, "unarmed for 8 seconds");
        ExpectHit("standing timer 9 s", F(standing: 9f), Tier.Bad, false, "unarmed for 9 seconds");
        // 8b. The recorder refreshes the grace clock every frame the break-in alone makes him a threat, so a hit snapshots
        //     sinceThreat ≈ 0: the banner must still say why (not "a threat a moment ago").
        ExpectHit("broke in, coming at you unarmed (grace clock fresh)", F(advancing: true, distance: 2f, sinceThreat: 0f), Tier.Good, false, "kicked your door in and was coming at you");
        ExpectHit("broke in, standing 1 s unarmed (grace clock fresh)", F(standing: 1f, sinceThreat: 0f), Tier.Good, false, "just kicked your door in");
        ExpectHit("broke in with a knife, 4 m back, not advancing (grace clock fresh)", F(weapon: Knife, distance: 4f, sinceThreat: 0f), Tier.Good, false, "kicked your door in holding a knife");
        ExpectHit("broke in, standing 5 s, but a threat 0.3 s ago", F(standing: 5f, sinceThreat: 0.3f), Tier.Good, false, "a moment ago");
        ExpectHit("broke in, turned to run 0.3 s ago", F(fleeing: true, distance: 3f, sinceThreat: 0.3f), Tier.Good, false, "turned to run");

        // 0. Through the closed door (he's out of sight): only a man kicking it in is covered, and it stays a close call
        ExpectHit("through the door while he kicks it in", F(inside: false, behind: true, forcing: true), Tier.Good, true, "kicking your door in");
        ExpectHit("through the door, knife attacker kicking (unseen knife)",
            F(inside: false, weapon: Knife, distance: 2f, behind: true, forcing: true), Tier.Good, true, "fired blind");
        ExpectHit("through the door while he only pounded", F(inside: false, behind: true), Tier.Bad, false, "couldn't see");
        ExpectHit("through the door mid-break-in, pounding before the kicks (forced entry)",
            F(inside: false, weapon: Knife, behind: true, forcing: true, sinceThreat: Judge.NoThreatYet), Tier.Good, true, "kicking your door in");
        ExpectHit("through the door at a sneaking burglar picking the lock", F(inside: false, forced: false, behind: true), Tier.Bad, false, "closed door");
        // Brett 0.1.29: a man the door camera showed coming up with a gun in his hand was seen — a good call, not blind.
        { var armed = F(inside: false, forced: false, weapon: WeaponKind.Gun, behind: true); armed.SeenArmedOnCamera = true;
          ExpectHit("through the door at a gunman seen on the door camera", armed, Tier.Good, false, "door camera"); }
        ExpectHit("through the door at a gunman nobody saw on camera", F(inside: false, forced: false, weapon: WeaponKind.Gun, behind: true), Tier.Bad, false, "couldn't see");
        ExpectHit("through the door at the neighbour on the handle", F(forced: false, inside: false, behind: true), Tier.Bad, false, "couldn't see");
        ExpectHit("through the door at a man running off outside", F(inside: false, fleeing: true, sinceThreat: 0.2f, behind: true), Tier.Bad, false, "closed door");
    }

    // ---------------------------------------------------------------- the gunman (M3a): rule 0b + the gun through rules 1–7

    static void Gun()
    {
        const WeaponKind G = WeaponKind.Gun;
        // 0b. His gun on you: pointed, being raised (the tell), firing — good whatever else he's doing.
        ExpectHit("gun pointed at you", F(weapon: G, pointing: true, distance: 4f), Tier.Good, false, "pointing a gun at you");
        ExpectHit("gun being raised at you (the tell)", F(weapon: G, raising: true, distance: 4f), Tier.Good, false, "raising his gun");
        ExpectHit("gun firing at you", F(weapon: G, pointing: true, firing: true, distance: 5f), Tier.Good, false, "shooting at you");
        ExpectHit("firing, walked in quietly", F(forced: false, weapon: G, firing: true, distance: 5f), Tier.Good, false, "shooting at you");
        ExpectHit("running, turned and pointed the gun", F(weapon: G, fleeing: true, pointing: true, distance: 4f), Tier.Good, false, "turned and pointed");
        ExpectHit("running, raising the gun as he turns", F(weapon: G, fleeing: true, raising: true, distance: 4f), Tier.Good, false, "raising his gun");
        ExpectHit("down (legs out), gun pointed at you", F(weapon: G, down: true, pointing: true, distance: 3f), Tier.Good, false, "down, but still pointing");
        ExpectHit("hands up but pointing the gun", F(weapon: G, posture: Posture.HandsUp, pointing: true), Tier.Good, false, "pointing a gun");
        // Lowered gun: the break-in / advancing rules with the gun noun.
        ExpectHit("gun lowered, advancing on you", F(weapon: G, advancing: true, distance: 5f), Tier.Good, false, "came at you with a gun");
        ExpectHit("gun lowered, broke in, standing back", F(weapon: G, distance: 5f), Tier.Good, false, "kicked your door in holding a gun");
        ExpectHit("gun lowered, walked in quietly, not advancing", F(forced: false, weapon: G, distance: 5f), Tier.Good, true, "walked in holding a gun");
        // Not pointing: the usual bad calls, even with the gun still in his hand.
        ExpectHit("fleeing with the gun, not pointing", F(weapon: G, fleeing: true, distance: 4f), Tier.Bad, false, "gun in his hand");
        ExpectHit("fleeing with the gun, turned 0.4 s ago", F(weapon: G, fleeing: true, sinceThreat: 0.4f), Tier.Good, false, "turned to run");
        ExpectHit("dropped the gun, hands up", F(posture: Posture.HandsUp), Tier.Bad, false, "hands were up and empty");
        ExpectHit("hands up, gun still in hand, not pointing", F(weapon: G, posture: Posture.HandsUp), Tier.Good, true, "still holding the gun");
        ExpectHit("down (legs out), gun in hand, not pointing, lying still", F(weapon: G, down: true, distance: 4f), Tier.Bad, false, "already down");
        ExpectHit("down (legs out), dropped the gun", F(down: true, distance: 3f), Tier.Bad, false, "already down");
        ExpectHit("pointing through the closed door doesn't count (unseen)", F(inside: false, weapon: G, pointing: true, behind: true), Tier.Bad, false, "couldn't see");

        ExpectVerdict("robber pointed the gun, shot dead",
            Rec(EndReason.Killed, Shot(F(weapon: G, pointing: true), killed: true)), Tier.Good, false, null, "pointing a gun at you");
        ExpectVerdict("robber dropped the gun, shot with his hands up",
            Rec(EndReason.Killed, Shot(F(forced: false, posture: Posture.HandsUp), killed: true)), Tier.Bad, false, Judge.ChargeKilled, "hands were up and empty");
        ExpectVerdict("robber ran with the gun, shot in the back",
            Rec(EndReason.Killed, Shot(F(weapon: G, fleeing: true), killed: true)), Tier.Bad, false, Judge.ChargeKilled, "gun in his hand");
        ExpectVerdict("robber ran, turned and pointed, shot",
            Rec(EndReason.Killed, Shot(F(weapon: G, fleeing: true, pointing: true), killed: true)), Tier.Good, false, null, "turned and pointed");
        ExpectVerdict("legs first, then killed pointing from the floor",
            Rec(EndReason.Killed, Shot(F(weapon: G, distance: 4f)), Shot(F(weapon: G, down: true, pointing: true), killed: true)), Tier.Good, false, null, "kicked your door in holding a gun");
    }

    // ---------------------------------------------------------------- whole scenarios (Evaluate)

    static void Scenarios()
    {
        var knifeCharge = F(weapon: WeaponKind.Knife, advancing: true, distance: 3f);

        ExpectVerdict("perfect: held for police", Rec(EndReason.Custody), Tier.Perfect, false, null, "police arrived");
        ExpectVerdict("perfect: he left", Rec(EndReason.Left), Tier.Perfect, false, null, "without firing a shot");
        ExpectVerdict("perfect: clock ran out", Rec(EndReason.TimedOut), Tier.Perfect, false, null, "without firing a shot");
        ExpectVerdict("player died, no force", Rec(EndReason.PlayerDied), Tier.YouDied, false, null, "before you could stop him");
        ExpectVerdict("player died after a good shot", Rec(EndReason.PlayerDied, Shot(knifeCharge)), Tier.YouDied, false, null, "fought back");

        ExpectVerdict("good kill, then shots into the body are ignored",
            Rec(EndReason.Killed, Shot(knifeCharge, killed: true),
                Shot(F(down: true, dead: true)), Shot(F(fleeing: true, dead: true))),
            Tier.Good, false, null, "came at you with a knife");
        ExpectVerdict("close call carries into the verdict",
            Rec(EndReason.Killed, Shot(knifeCharge), Shot(F(fleeing: true, sinceThreat: 0.8f), killed: true)),
            Tier.Good, true, null, "turned to run");
        ExpectVerdict("custody after a hands-up-with-knife shot",
            Rec(EndReason.Custody, Shot(F(posture: Posture.HandsUp, weapon: WeaponKind.Knife))),
            Tier.Good, true, null, "still holding the knife");

        ExpectVerdict("fleeing burglar shot dead", Rec(EndReason.Killed, Shot(F(fleeing: true), killed: true)),
            Tier.Bad, false, Judge.ChargeKilled, "running away");
        ExpectVerdict("fleeing burglar wounded, got away", Rec(EndReason.Left, Shot(F(fleeing: true))),
            Tier.Bad, false, Judge.ChargeInjured, "running away");
        ExpectVerdict("good shot, then a finishing shot on him down",
            Rec(EndReason.Killed, Shot(knifeCharge), Shot(F(down: true), killed: true)),
            Tier.Bad, false, Judge.ChargeKilled, "already down");
        ExpectVerdict("bad wound first, then a justified kill",
            Rec(EndReason.Killed, Shot(F(posture: Posture.HandsUp)), Shot(F(attacking: true, distance: 1f), killed: true)),
            Tier.Bad, false, Judge.ChargeInjured, "hands were up and empty");
        ExpectVerdict("bled out after a bad hit", Rec(EndReason.Killed, Shot(F(fleeing: true))),
            Tier.Bad, false, Judge.ChargeKilled, "running away");
        ExpectVerdict("punch-only bad call is battery",
            Rec(EndReason.Left, Shot(F(forced: false, identity: true), kind: ForceKind.Punch)),
            Tier.Bad, false, Judge.ChargeBattery, "told you who he was");
        ExpectVerdict("punch plus a bad stab is not just battery",
            Rec(EndReason.Left, Shot(F(forced: false), kind: ForceKind.Punch), Shot(F(forced: false), kind: ForceKind.Stab)),
            Tier.Bad, false, Judge.ChargeInjured, "unlocked door");
        ExpectVerdict("killed through the door mid-kick",
            Rec(EndReason.Killed, Shot(F(inside: false, behind: true, forcing: true), killed: true)),
            Tier.Good, true, null, "kicking your door in");
        ExpectVerdict("wounded through the door mid-kick, then shot charging in",
            Rec(EndReason.Killed, Shot(F(inside: false, behind: true, forcing: true)), Shot(F(advancing: true, distance: 2f), killed: true)),
            Tier.Good, true, null, "fired blind");
        ExpectVerdict("knife attacker: shot through the door mid-break-in, killed charging in (Brett 0.1.10 #4)",
            Rec(EndReason.Killed, Shot(F(inside: false, weapon: WeaponKind.Knife, behind: true, forcing: true)), Shot(knifeCharge, killed: true)),
            Tier.Good, true, null, "kicking your door in");
        ExpectVerdict("knife attacker: legs shot out, killed clawing at you from the floor (Brett 0.1.10 #2)",
            Rec(EndReason.Killed, Shot(knifeCharge), Shot(F(down: true, weapon: WeaponKind.Knife, sinceThreat: 0.3f)),
                Shot(F(down: true, weapon: WeaponKind.Knife, attacking: true, distance: 1.2f), killed: true)),
            Tier.Good, false, null, "came at you with a knife");
        ExpectVerdict("home invader: legs shot out, lying there empty-handed, finished off (Brett 0.1.10 #1)",
            Rec(EndReason.Killed, Shot(F(advancing: true, distance: 2f)), Shot(F(down: true, distance: 2f), killed: true)),
            Tier.Bad, false, Judge.ChargeKilled, "already down");
        ExpectVerdict("home invader: legs then head as he comes at you (the banner says why)",
            Rec(EndReason.Killed, Shot(F(advancing: true, distance: 2.2f, sinceThreat: 0f)), Shot(F(advancing: true, distance: 1.8f, sinceThreat: 0f), killed: true)),
            Tier.Good, false, null, "kicked your door in and was coming at you");
        ExpectVerdict("neighbour shot through the door, ran off",
            Rec(EndReason.Left, Shot(F(forced: false, inside: false, behind: true))),
            Tier.Bad, false, Judge.ChargeInjured, "couldn't see");
        ExpectVerdict("null record doesn't throw", null, Tier.Perfect, false, null, null);
    }

    // ---------------------------------------------------------------- the player's wounds (Brett 0.1.17)

    /// <summary>
    /// The judge doesn't care how badly you're hurt: the same record gives the same verdict with a limb wound on you (PlayerHurt),
    /// and one round center mass is simply "you died". Then the wound rule, zones and spread themselves (WoundSelfTest, counted here).
    /// </summary>
    static void PlayerWounds()
    {
        var knifeCharge = F(weapon: WeaponKind.Knife, advancing: true, distance: 3f);
        ExpectVerdict("wounded (limb), then a good kill: still good",
            Hurt(Rec(EndReason.Killed, Shot(knifeCharge, killed: true))), Tier.Good, false, null, "came at you with a knife");
        ExpectVerdict("wounded (limb), shot him running: still bad",
            Hurt(Rec(EndReason.Killed, Shot(F(fleeing: true), killed: true))), Tier.Bad, false, Judge.ChargeKilled, "running away");
        ExpectVerdict("wounded (limb), he left: still perfect", Hurt(Rec(EndReason.Left)), Tier.Perfect, false, null, "without firing a shot");
        ExpectVerdict("one round center mass from the gunman: you died",
            Hurt(Rec(EndReason.PlayerDied, Shot(F(weapon: WeaponKind.Gun, pointing: true)))), Tier.YouDied, false, null, "fought back");

        var (pass, total) = WoundSelfTest.Checks();
        s_pass += pass; s_total += total;
        Debug.Log($"[HIS] JUDGE wounds: {pass}/{total} WOUND checks passed");
    }

    static ScenarioRecord Hurt(ScenarioRecord r) { r.PlayerHurt = true; return r; }

    // ---------------------------------------------------------------- the Marshal (research/27 §4, §6 step 11)

    /// <summary>
    /// The Marshal's role switch: no home presumption at a safe house (a forced door alone justifies nothing), the deputy by
    /// name, and the night's hand-built cards. Runs as Role.Marshal; Run() puts the role back. The homeowner's own cases above
    /// ran first, unchanged.
    /// </summary>
    static void Marshal()
    {
        Judge.Role = Role.Marshal;

        // The two cases research/27 §6 step 11 asks for.
        ExpectHit("marshal: kicked in, unarmed, standing 1 s (no home presumption)", F(standing: 1f), Tier.Bad, false, "isn't your home");
        ExpectCard("marshal: you shot Ricky, he died", Judge.ProtecteeShot(killed: true), Tier.Bad, "THE TRIAL IS OFF", Judge.ChargeKilled,
            "You shot Ricky", "18 U.S.C. 242", "25 years in prison", "Don't drop the soap.");

        // A little more, cheap: the other gates, the deputy, and the cards' exact tallies.
        ExpectHit("marshal: through the door while he kicks it in (no presumption to cover it)", F(inside: false, behind: true, forcing: true), Tier.Bad, false, "couldn't see");
        ExpectHit("marshal: kicked in, coming at you with a knife: still good", F(weapon: WeaponKind.Knife, advancing: true), Tier.Good, false, "came at you with a knife");
        { var d = F(forced: false, inside: false, behind: true); d.KnownDeputy = true;
          ExpectHit("marshal: Ortiz shot through the shut door", d, Tier.Bad, false, "Deputy Ortiz. He radioed ahead"); }
        { var d = F(forced: false); d.KnownDeputy = true;
          ExpectHit("marshal: Ortiz shot inside, before he spoke again", d, Tier.Bad, false, "Deputy Ortiz"); }
        ExpectCard("marshal: you shot Ricky, he lived", Judge.ProtecteeShot(killed: false), Tier.Bad, "BAD CALL", Judge.ChargeInjured,
            "You shot Ricky", "18 U.S.C. 242", "12 years in prison");
        // Ricky hit in the same round as a bad call on the visitor (the round is dropped for Ricky's card): both charges, the
        // federal count ×2. Ricky shot, lived: ADW with a gun 7 + Ortiz stabbed: ADW 3 + 18 U.S.C. 242 hurt 5 ×2 = 20 years.
        ExpectCard("marshal: Ricky shot and the visitor stabbed in the same round → both charges",
            Judge.ProtecteeShot(killed: false, ForceKind.Shot, new Verdict { Tier = Tier.Bad, Charge = Judge.ChargeInjured }, visitorGun: false),
            Tier.Bad, "BAD CALL", Judge.ChargeInjured, "You shot Ricky", "color of law ×2", "20 years in prison");
        ExpectCard("marshal: WITNESS DELIVERED", Judge.WitnessDelivered(7, 7, 4), Tier.Perfect, "WITNESS DELIVERED", null,
            "Ricky testifies Monday", "Hitmen stopped: 7 of 7. Visitors: 4, no bad calls.");
        ExpectCard("marshal: THE TRIAL IS OFF (Ricky)", Judge.TrialIsOff(true, 3, 7, 2, 3), Tier.YouDied, "THE TRIAL IS OFF", null,
            "They got to Ricky.", "Hitmen stopped: 3 of 7. Wave 2 of 3.");
        // A visitor's bad call: the judge's charge + the federal count says "in prison"; without it, "in state prison" as before.
        Check("marshal: manslaughter + 18 U.S.C. 242 = 25 years in prison",
            Sentencing.Estimate(new[] { Sentencing.FromJudgeCharge(Judge.ChargeKilled, true), Sentencing.ColorOfLaw(Judge.ChargeKilled) }), "25 years in prison");
        Check("homeowner: manslaughter alone still in state prison",
            Sentencing.Estimate(new[] { Sentencing.FromJudgeCharge(Judge.ChargeKilled, true) }), "10 years in state prison");

        // Brett 2026-10-04 (a): every bad call on a visitor is the arrest (no police standoff) — the booking card from the
        // judge's call. A knife is no firearm: a stabbed Ortiz is assault 3 + the federal count 5 = 8 years.
        var ortizStabbed = Judge.VisitorBooked(new Verdict
        {
            Tier = Tier.Bad, Headline = Judge.HeadlineBad, Reason = "Deputy Ortiz. He said so at the door, and he had a key.",
            Charge = Judge.ChargeInjured, WhoItWas = Judge.WhoOrtizCheckIn,
        }, gun: false);
        ExpectCard("marshal: Ortiz stabbed, lived → the booking", ortizStabbed, Tier.Bad, Judge.HeadlineBad, Judge.ChargeInjured,
            "Deputy Ortiz", Sentencing.OprReferral, "18 U.S.C. 242", "8 years in prison", "Don't drop the soap.");
        Check("marshal: the booking keeps who it was", ortizStabbed.WhoItWas, Judge.WhoOrtizCheckIn);
        ExpectCard("marshal: Sal shot dead → the booking", Judge.VisitorBooked(new Verdict
            {
                Tier = Tier.Bad, Headline = Judge.HeadlineBad, Reason = "He told you who he was, and his hands were empty.",
                Charge = Judge.ChargeKilled, WhoItWas = Judge.WhoSal,
            }, gun: true), Tier.Bad, Judge.HeadlineBad, Judge.ChargeKilled,
            "who he was", Sentencing.OprReferral, "18 U.S.C. 242", "25 years in prison", "Don't drop the soap.");
    }

    /// <summary>
    /// The police dispatcher's first line after a bad call (the default game's standoff; Marshal mode never calls the police):
    /// a stabbing with no round fired is "Stabbing at the residence", never "Shots fired"; a blow is an assault; any round fired
    /// is a shooting (ScenarioDirector.ReportedForce → PoliceResponse.RespondingLine, private: read by reflection).
    /// </summary>
    static void Dispatcher()
    {
        const System.Reflection.BindingFlags SNP = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var reported = typeof(ScenarioDirector).GetMethod("ReportedForce", SNP);
        var line = typeof(PoliceResponse).GetMethod("RespondingLine", SNP);
        if (reported == null || line == null) { s_total++; Fail("dispatcher: the line by what happened", "ReportedForce + RespondingLine", "renamed"); return; }
        string Said(bool fired, params ForceKind[] kinds)
        {
            var rec = new ScenarioRecord();
            foreach (var k in kinds) rec.Force.Add(new ForceEvent { Kind = k, Damage = 10f });
            var key = (string)line.Invoke(null, new object[] { (ForceKind)reported.Invoke(null, new object[] { rec, fired }) });
            return VoiceLines.Text(key);
        }
        Check("dispatcher: a stab, nothing fired → a stabbing", Said(false, ForceKind.Stab), "Units responding. Stabbing at the residence.");
        Check("dispatcher: a slash, nothing fired → a stabbing", Said(false, ForceKind.Slash), "Units responding. Stabbing at the residence.");
        Check("dispatcher: a stab, and a round fired (a miss) → shots fired", Said(true, ForceKind.Stab), VoiceLines.Text("vo.cop.radio.responding"));
        Check("dispatcher: a blow, nothing fired → an assault", Said(false, ForceKind.Punch), VoiceLines.Text("vo.cop.radio.assault"));
    }

    /// <summary>
    /// The arrest card's firearm years (PoliceResponse.ComposeCard ← ScenarioDirector: Judge.BadShot): only a bad shooting
    /// carries them — a knife killing does not because a round was fired that night (a justified earlier shot, or one into the
    /// body afterwards). The dispatcher still says "Shots fired" for those (Dispatcher above).
    /// </summary>
    static void FirearmYears()
    {
        string Card(ScenarioRecord r) { var v = Judge.Evaluate(r); return Sentencing.Estimate(new[] { Sentencing.FromJudgeCharge(v.Charge, Judge.BadShot(r)) }); }
        var handsUp = F(posture: Posture.HandsUp);
        Check("firearm years: shot him dead with his hands up → manslaughter + the gun",
            Card(Rec(EndReason.Killed, Shot(handsUp, killed: true))), "10 years in state prison");
        Check("firearm years: a justified shot, then stabbed him dead with his hands up → manslaughter, no gun",
            Card(Rec(EndReason.Killed, Shot(F(weapon: WeaponKind.Knife, advancing: true)), Shot(handsUp, killed: true, kind: ForceKind.Stab))),
            "6 years in state prison");
        Check("firearm years: stabbed him dead with his hands up, then a round into the body → no gun",
            Card(Rec(EndReason.Killed, Shot(handsUp, killed: true, kind: ForceKind.Stab), Shot(F(posture: Posture.HandsUp, dead: true)))),
            "6 years in state prison");
        Check("firearm years: shot him with his hands up, he lived → assault with a deadly weapon + the gun",
            Card(Rec(EndReason.Custody, Shot(handsUp))), "7 years in state prison");
    }

    /// <summary>A hand-built card: tier, headline, charge (null = none), and each of <paramref name="has"/> in its reason or sentence.</summary>
    static void ExpectCard(string name, Verdict v, Tier tier, string headline, string charge, params string[] has)
    {
        s_total++;
        string text = v.Reason + "\n" + v.Sentence;
        bool ok = v.Tier == tier && v.Headline == headline && (charge == null ? string.IsNullOrEmpty(v.Charge) : v.Charge == charge);
        foreach (var h in has) ok &= Has(text, h);
        if (ok) Pass(name);
        else Fail(name, $"{tier} '{headline}' charge='{charge}' with '{string.Join("', '", has)}'",
            $"{v.Tier} '{v.Headline}' charge='{v.Charge}' text='{text}'");
    }

    static void Check(string name, string got, string expected)
    {
        s_total++;
        if (got == expected) Pass(name); else Fail(name, $"'{expected}'", $"'{got}'");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A stranger inside after kicking the door in, 4 m away, standing still with empty hands, never threatened yet.</summary>
    static IntruderFacts F(bool forced = true, bool inside = true, WeaponKind weapon = WeaponKind.None, bool advancing = false,
        bool attacking = false, bool fleeing = false, Posture posture = Posture.Standing, bool down = false,
        bool identity = false, float distance = 4f, float sinceThreat = Judge.NoThreatYet, float standing = 0f, bool dead = false,
        bool behind = false, bool forcing = false, bool pointing = false, bool raising = false, bool firing = false)
        => new()
        {
            IntruderId = 1, WeaponInHand = weapon, Advancing = advancing, Attacking = attacking, Fleeing = fleeing,
            Posture = posture, Down = down, IdentityStated = identity, ForcedEntry = forced, Inside = inside,
            Distance = distance, SecondsSinceThreat = sinceThreat, UnarmedStandingSeconds = standing, Dead = dead,
            BehindDoor = behind, ForcingDoor = forcing, PointingAtPlayer = pointing, RaisingGun = raising, Firing = firing,
        };

    static ForceEvent Shot(IntruderFacts before, bool killed = false, ForceKind kind = ForceKind.Shot)
        => new() { Kind = kind, Before = before, Damage = killed ? 120f : 34f, BodyPart = "Torso", Killed = killed };

    static ScenarioRecord Rec(EndReason end, params ForceEvent[] force)
    {
        var r = new ScenarioRecord { Def = s_def, End = end };
        r.Force.AddRange(force);
        if (end == EndReason.Killed) r.LastFacts.Dead = true;
        return r;
    }

    static void ExpectHit(string name, IntruderFacts facts, Tier tier, bool close, string reasonHas)
    {
        s_total++;
        try
        {
            var e = Shot(facts);
            var (t, c, reason) = Judge.JudgeForce(in e);
            if (t == tier && c == close && Has(reason, reasonHas)) Pass(name);
            else Fail(name, Describe(tier, close, reasonHas), Describe(t, c, reason));
        }
        catch (Exception ex) { Fail(name, Describe(tier, close, reasonHas), ex.GetType().Name + ": " + ex.Message); }
    }

    static void ExpectVerdict(string name, ScenarioRecord r, Tier tier, bool close, string charge, string reasonHas)
    {
        s_total++;
        try
        {
            var v = Judge.Evaluate(r);
            string headline = tier switch
            {
                Tier.Perfect => "PERFECT", Tier.Good => "GOOD CALL", Tier.Bad => "BAD CALL", _ => "YOU DIDN'T SURVIVE",
            };
            string who = r == null ? null : r.Def == null ? null : r.Def.WhoItWas;
            bool ok = v.Tier == tier && v.CloseCall == close && v.Headline == headline && v.WhoItWas == who
                && (charge == null ? string.IsNullOrEmpty(v.Charge) : v.Charge == charge)
                && !string.IsNullOrEmpty(v.Reason) && Has(v.Reason, reasonHas);
            if (ok) Pass(name);
            else Fail(name, $"{headline} {Describe(tier, close, reasonHas)} charge='{charge}' who='{who}'",
                $"{v.Headline} {Describe(v.Tier, v.CloseCall, v.Reason)} charge='{v.Charge}' who='{v.WhoItWas}'");
        }
        catch (Exception ex) { Fail(name, $"{tier} charge='{charge}'", ex.GetType().Name + ": " + ex.Message); }
    }

    static bool Has(string reason, string part) =>
        !string.IsNullOrEmpty(reason) && (part == null || reason.IndexOf(part, StringComparison.Ordinal) >= 0);

    static string Describe(Tier tier, bool close, string reason) => $"{tier}{(close ? " CLOSE" : "")} '{reason}'";

    static void Pass(string name) { s_pass++; Debug.Log($"[HIS] JUDGE PASS {name}"); }

    static void Fail(string name, string expected, string got) =>
        Debug.LogError($"[HIS] JUDGE FAIL {name}: expected {expected} got {got}");
}
