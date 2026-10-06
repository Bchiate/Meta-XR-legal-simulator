using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Pick #1, "the door never plays the same way twice" (Brett 2026-09-24), and his 0.1.17 door-shot bug ("when i shoot
/// through door he'll kick it in anyway and wont react"). Each case forces one <see cref="EntryStyle"/> with varied entries
/// on (Case.Setup), then checks how the door goes and how a round through the leaf lands:
///   Rush bursts on the first kick · a knocker shot in the silence = BAD, and he never comes in · a man shot hard while
///   kicking gives up and never comes in (GOOD: he was forcing it) · a grazed fighter reels, then bursts it · a burglar
///   pries it and eases it open · the false alarm: nobody comes in (PERFECT), or you shot the delivery guy (BAD).
///   Just these: scripts/unity-batch.sh ScenarioTests.RunEntriesHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_entriesOnly;

    /// <summary>Batch entry for just the entry-style cases: scripts/unity-batch.sh ScenarioTests.RunEntriesHeadless.</summary>
    public static void RunEntriesHeadless() { s_entriesOnly = s_doorwayOnly = true; RunHeadless(); } // (the doorway run's short start)

    static IEnumerable<Case> WithEntryCases(IEnumerable<Case> cases)
    {
        if (!s_entriesOnly) foreach (var c in cases) yield return c;
        foreach (var c in EntryCases()) yield return c;
    }

    static System.Action Styled(EntryStyle s) => () => { ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = s; };

    static bool DoorOpen => HIS.Room.VirtualDoor.Instance != null && HIS.Room.VirtualDoor.Instance.IsOpen;

    static System.Action Talked(EntryStyle s, float? roll = null) => () =>
    {
        ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = s; IntruderT.TestDoorRoll = roll;
    };

    /// <summary>Yell through the closed door while he waits outside it (pick #2), after the round has really started.</summary>
    static async Task<bool> YellThroughDoor(Command c)
    {
        if (!await WaitFor(() => Live() != null && Live().BehindDoor && State() is IntruderT.State.Outside or IntruderT.State.BreakIn, 20)) return false;
        await Wait(0.8f);
        ClearLog();
        Yell(c);
        return await WaitForLog("through the door", 4f);
    }

    static System.Action Chooses(IntruderT.BurglarChoice choice) => () =>
    {
        ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = EntryStyle.Classic; IntruderT.TestBurglarChoice = choice;
    };

    /// <summary>Pick #3: once in, yelled at — he freezes first (not fleeing 1.2 s on), then the forced choice is logged.</summary>
    static async Task<bool> CatchBurglar(Command c, string choice)
    {
        if (!await WaitFor(() => State() == IntruderT.State.Idle, 60)) { ExtraFail("burglar caught: " + choice, "never in: " + Live()?.Summary); return false; }
        await Wait(1f);
        ClearLog();
        Yell(c);
        await Wait(1.2f);
        bool frozen = State() is not IntruderT.State.Fleeing and not IntruderT.State.Gone;
        bool chose = await WaitForLog("froze — " + choice, 4f);
        if (frozen && chose) ExtraPass("burglar freezes, then: " + choice, "still there 1.2 s after the yell");
        else ExtraFail("burglar freezes, then: " + choice, $"frozen {frozen}, chose {chose}: {Live()?.Summary}");
        return chose;
    }

    static Case[] EntryCases() => new[]
    {
        // Pick #3: caught in the act, he freezes, then backs off to the door ("I'm leaving, I'm leaving!"). PERFECT.
        new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Chooses(IntruderT.BurglarChoice.BackAway), Act = async () =>
            {
                if (!await CatchBurglar(Command.GetOut, "BackAway")) return;
                // Gone is gone (Brett 0.1.56, a shared fix: Scenarios too): once he's left, nothing of him is drawn — no statue
                // in the doorway or on the door camera until the next round.
                var man = Live();
                if (await WaitFor(() => State() == IntruderT.State.Gone, 40))
                {
                    if (man != null && !AnyDrawn(man)) ExtraPass("gone is gone (Scenarios)", "the burglar who left: no renderer of his drawn");
                    else ExtraFail("gone is gone (Scenarios)", man == null ? "no man to look at" : "a renderer of his still drawn after he left");
                }
            } },
        // He freezes, then ducks behind real furniture (or, with none near, runs); either way he's no threat. PERFECT.
        new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Chooses(IntruderT.BurglarChoice.Hide), Act = async () =>
            {
                if (await CatchBurglar(Command.WhoIsThere, "Hide")) await WaitFor(() => State() is IntruderT.State.Gone or IntruderT.State.Complying, 40);
                if (State() == IntruderT.State.Complying) { await Wait(1f); Yell(Command.GetDown); }
            } },
        // Cornered with his knife, he turns on you: now he's coming with a knife — stopping him is a GOOD call.
        new Case { Name = "Burglar with a knife", Expect = Tier.Good, Doorway = s_entriesOnly, Setup = Chooses(IntruderT.BurglarChoice.PullKnife), Act = async () =>
            {
                if (!await CatchBurglar(Command.GetOut, "PullKnife")) return;
                await WaitFor(() => Dist() < 2.2f, 30);
                for (int i = 0; i < 4 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); }
            } },
        // Pick #2: "Get out of here!" through the door — the burglar's steps hurry off; the door never opens. PERFECT.
        new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Talked(EntryStyle.KnockThenKick, 0f), Act = async () =>
            {
                bool heard = await YellThroughDoor(Command.GetOut);
                await WaitFor(() => DoorOpen || State() == IntruderT.State.Gone, 25);
                if (heard && !DoorOpen) ExtraPass("door talk: burglar talked off", "left without the door opening");
                else ExtraFail("door talk: burglar talked off", $"heard {heard}, door open {DoorOpen}, {Live()?.Summary}");
            } },
        // "Who's there?" — the knife man: "Open the door. Now." and it goes at once; then he's stopped. GOOD.
        new Case { Name = "Knife attacker", Expect = Tier.Good, Doorway = s_entriesOnly, Setup = Talked(EntryStyle.KnockThenKick), Act = async () =>
            {
                bool heard = await YellThroughDoor(Command.WhoIsThere);
                float t0 = Time.time;
                bool burst = await WaitFor(() => DoorOpen, 8);
                if (heard && burst && Time.time - t0 < 6f) ExtraPass("door talk: knife man kicks it in at once", $"{Time.time - t0:F1} s after his answer");
                else ExtraFail("door talk: knife man kicks it in at once", $"heard {heard}, burst {burst} after {Time.time - t0:F1} s");
                await WaitFor(() => Dist() < 3f, 40);
                for (int i = 0; i < 4 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); }
            } },
        // "Who's there?" — "Sorry! Sorry, wrong house!" and he goes. PERFECT, door never opened.
        new Case { Name = "Surrenders", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Talked(EntryStyle.Classic), Act = async () =>
            {
                bool heard = await YellThroughDoor(Command.WhoIsThere);
                await WaitFor(() => DoorOpen || State() == IntruderT.State.Gone, 25);
                if (heard && !DoorOpen) ExtraPass("door talk: wrong-house apology, he leaves", "never came in");
                else ExtraFail("door talk: wrong-house apology, he leaves", $"heard {heard}, door open {DoorOpen}");
            } },
        // Mike told to go home through the door: "Alright, alright... jeez." PERFECT, door never opened.
        new Case { Name = "Wrong house", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Talked(EntryStyle.Classic), Act = async () =>
            {
                bool heard = await YellThroughDoor(Command.GetOut);
                await WaitFor(() => DoorOpen || State() == IntruderT.State.Gone, 25);
                if (heard && !DoorOpen) ExtraPass("door talk: Mike goes home", "never came in");
                else ExtraFail("door talk: Mike goes home", $"heard {heard}, door open {DoorOpen}");
            } },
        // Rush: no pounding, the first kick bursts it; then he comes at you and is stopped: GOOD.
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.Rush), Act = async () =>
            {
                if (!await WaitFor(() => State() == IntruderT.State.BreakIn, 20)) { ExtraFail("rush", "never at the door: " + Live()?.Summary); return; }
                float t0 = Time.time;
                bool open = await WaitFor(() => DoorOpen, 6);
                float took = Time.time - t0;
                if (open && took < 2.2f) ExtraPass("rush: the first kick bursts it", $"{took:F1} s after BreakIn");
                else ExtraFail("rush: the first kick bursts it", open ? $"took {took:F1} s" : "door never opened");
                await WaitFor(() => Dist() < 2.6f, 40);
                for (int i = 0; i < 4 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); }
            } },
        // A polite knock, then silence: shot through the door now he only knocked — BAD — and he gives up, never comes in.
        new Case { Name = "Knife attacker", Expect = Tier.Bad, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.KnockThenKick), Act = async () =>
            {
                if (!await WaitFor(() => State() == IntruderT.State.BreakIn, 20)) { ExtraFail("knock then kick", "never at the door"); return; }
                await Wait(1.5f);                                  // the two knocks are done: the silence
                Shoot(1, 34f, HumanBodyBones.Chest);
                bool entered = await WaitFor(() => DoorOpen || (Live() != null && !Live().BehindDoor), 10);
                if (!entered) ExtraPass("knocker shot in the silence never comes in", Live()?.Summary);
                else ExtraFail("knocker shot in the silence never comes in", "he came in: " + Live()?.Summary);
            } },
        // Brett's bug: shot hard through the door while kicking it, he reacts and gives up — never kicks it in. GOOD (forcing).
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.Classic), Act = async () =>
            {
                if (!await WaitFor(() => State() == IntruderT.State.BreakIn, 20)) { ExtraFail("shot while kicking", "never at the door"); return; }
                await Wait(2.2f);                                  // pounding done, into the kicks
                Shoot(1, 34f, HumanBodyBones.Chest);
                bool entered = await WaitFor(() => DoorOpen, 8);
                if (!entered) ExtraPass("shot hard while kicking: gives up, never kicks it in", Live()?.Summary);
                else ExtraFail("shot hard while kicking: gives up, never kicks it in", "the door went: " + Live()?.Summary);
            } },
        // A grazed fighter reels into the leaf, a beat, then comes through angrier: the next kick bursts it. Then stopped: GOOD.
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.WeakGuy), Act = async () =>
            {
                if (!await WaitFor(() => State() == IntruderT.State.BreakIn, 20)) { ExtraFail("grazed fighter", "never at the door"); return; }
                await Wait(1.6f);
                ClearLog();
                Shoot(1, 12f, HumanBodyBones.LeftUpperArm);
                bool reeled = await WaitForLog("grazed through the door", 4f);
                bool burst = await WaitFor(() => DoorOpen, 8);
                if (reeled && burst) ExtraPass("grazed fighter reels, then bursts it", Live()?.Summary);
                else ExtraFail("grazed fighter reels, then bursts it", $"reeled {reeled}, burst {burst}: {Live()?.Summary}");
                await WaitFor(() => Dist() < 2.6f, 40);
                for (int i = 0; i < 4 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); }
            } },
        // A burglar pries it: the door eases open (not burst) and he slips in; told to get out, he goes: PERFECT.
        new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.QuietPry), Act = async () =>
            {
                if (!await WaitFor(() => DoorOpen, 25)) { ExtraFail("quiet pry", "door never opened: " + Live()?.Summary); return; }
                // Eased open over ~3 s (OpenGently), not burst (Breach swings it in a fraction of a second).
                float t0 = Time.time;
                await WaitFor(() => HIS.Room.VirtualDoor.Instance == null || HIS.Room.VirtualDoor.Instance.State == HIS.Room.VirtualDoor.DoorState.Open, 6);
                float swing = Time.time - t0;
                if (swing > 1.5f) ExtraPass("quiet pry: eased open, not kicked in", $"swing {swing:F1} s");
                else ExtraFail("quiet pry: eased open, not kicked in", $"swing {swing:F1} s");
                await WaitFor(() => State() == IntruderT.State.Idle, 40);
                await Wait(1f); Yell(Command.GetOut);
                await WaitFor(() => State() == IntruderT.State.Gone, 30);
            } },
        // False alarm, fire held: a knock, "Delivery!", his steps leave — the door never opens. PERFECT.
        new Case { Name = "False alarm (delivery)", Expect = Tier.Perfect, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.FalseAlarm), Act = async () =>
            {
                await WaitFor(() => DoorOpen || State() == IntruderT.State.Gone, 25);
                bool opened = DoorOpen;
                if (!opened) ExtraPass("false alarm: nobody comes in", "gone without opening the door");
                else ExtraFail("false alarm: nobody comes in", $"the door opened: {Live()?.Summary}");
            } },
        // False alarm, shot through the door: you shot the delivery guy. BAD.
        new Case { Name = "False alarm (delivery)", Expect = Tier.Bad, Doorway = s_entriesOnly, Setup = Styled(EntryStyle.FalseAlarm), Act = async () =>
            {
                if (!await WaitFor(() => State() == IntruderT.State.BreakIn, 20)) { ExtraFail("delivery shot", "never knocked"); return; }
                await Wait(1.2f);
                Shoot(1, 34f, HumanBodyBones.Chest);
                await WaitFor(() => State() is IntruderT.State.Gone or IntruderT.State.Dead, 15);
            } },
    };
}
