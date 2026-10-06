using System.Collections.Generic;
using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Lethal rounds (Brett 0.1.22: "most often if they got shot they should just die"; HIS.Intruder.Hitbox.LethalRounds): one
/// round in the body kills, through the door too; the first limb round wounds (a leg takes his legs out), the second kills.
/// The other scenario cases run with graded hit points (they switch it off); these opt in.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunLethalHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_lethalOnly;

    public static void RunLethalHeadless() { s_lethalOnly = s_doorwayOnly = true; RunHeadless(); }

    static IEnumerable<Case> WithLethalCases(IEnumerable<Case> cases)
    {
        if (!s_lethalOnly) foreach (var c in cases) yield return c;
        foreach (var c in LethalCases()) yield return c;
    }

    static System.Action Lethal() => () => HIS.Intruder.Hitbox.LethalRounds = true;

    static IEnumerable<Case> LethalCases() => new[]
    {
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = true, Setup = Lethal(), Act = async () =>
            { await WaitFor(() => Alive() && Dist() < 3f, 40);
              Shoot(1, 25f, HumanBodyBones.Chest); await Wait(0.3f);
              var i = Live();
              if (i != null && i.Current == IntruderT.State.Dead) ExtraPass("lethal: one round in the chest", "dead");
              else ExtraFail("lethal: one round in the chest", i?.Summary); } },
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = true, Setup = Lethal(), Act = async () =>
            { await WaitFor(() => Alive() && Dist() < 3.2f, 40);
              Shoot(1, 20f, HumanBodyBones.LeftUpperLeg); await Wait(0.6f);
              var i = Live();
              bool down = i != null && i.Current != IntruderT.State.Dead && i.CurrentGait == HIS.Intruder.IntruderAnimator.Gait.Crawl;
              if (down) ExtraPass("lethal: first leg round", "legs out, alive"); else ExtraFail("lethal: first leg round", i?.Summary);
              Shoot(1, 20f, HumanBodyBones.RightUpperArm); await Wait(0.3f);
              if (i != null && i.Current == IntruderT.State.Dead) ExtraPass("lethal: second limb round", "dead");
              else ExtraFail("lethal: second limb round", i?.Summary); } },
        new Case { Name = "Knife attacker", Expect = Tier.Good, Doorway = true, Setup = Lethal(), Act = async () =>
            { await WaitFor(() => Alive() && State() == IntruderT.State.BreakIn, 40); await Wait(0.8f);
              Shoot(1, 18f, HumanBodyBones.Hips, HumanBodyBones.Spine); await Wait(0.5f); // (the leaf takes some of it: still a round in his body; his arm guards the chest mid-kick)
              var i = Live();
              if (i != null && i.Current == IntruderT.State.Dead && i.BehindDoor) ExtraPass("lethal: through the door", "dies at the door");
              else ExtraFail("lethal: through the door", i?.Summary); } },
    };
}
