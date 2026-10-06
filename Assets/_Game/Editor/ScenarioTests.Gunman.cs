using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The gunman (M3a) end to end: one case per deck entry, the tell-before-fire rule, and Brett's legs-first habit on a man
/// with a gun. Compliance and the runner's turn are pinned with the test hooks (Intruder.TestRoll / TestTurnAndPoint).
/// </summary>
public static partial class ScenarioTests
{
    static IEnumerable<Case> WithGunmanCases(Case[] cases)
    {
        foreach (var c in cases) yield return c;
        foreach (var c in GunmanCases()) yield return c;
    }

    static Case[] GunmanCases() => new[]
    {
        // He raises the gun on you (tell), holds you at gunpoint and fires; shot while it's on you: GOOD. No round before the
        // tell is over, and no tell before he's been in view for the 1 s perception gate.
        new Case { Name = "Armed robber", Expect = Tier.Good, Act = async () =>
            {
                ResetGunHooks();
                var watch = new TellWatch();
                try
                {
                    if (!await WaitFor(() => watch.FirstFire > 0f, 40)) { ExtraFail("tell before fire", "he never fired (" + Live()?.Summary + ")"); }
                    else watch.Judge();
                }
                finally { watch.Dispose(); }
                if (!await WaitFor(() => Live() != null && Live().Facts.PointingAtPlayer, 8)) { ExtraFail("robber pointing", "gun never on the player"); return; }
                // Until he's down (three chest rounds can leave him on 1 hp: 34 + 31 + 34 — he then fired on for the full 120 s).
                for (int i = 0; i < 6 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.25f); }
            } },
        // "Drop it!" → he tosses the gun away and puts his hands up; "Get down!" → held for the police: PERFECT.
        new Case { Name = "Robber drops the gun", Expect = Tier.Perfect, Act = async () =>
            {
                ResetGunHooks(); IntruderT.TestRoll = 0f;
                await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f);
                Yell(Command.DropWeapon);
                await CheckTossed();
                await Wait(1.5f); Yell(Command.GetDown);
            } },
        // Same, then shot with his hands up and empty: BAD.
        new Case { Name = "Robber drops the gun", Expect = Tier.Bad, Reason = "hands were up and empty", Act = async () =>
            {
                ResetGunHooks(); IntruderT.TestRoll = 0f;
                await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f);
                Yell(Command.DropWeapon);
                await WaitFor(() => Live() != null && Live().Facts.WeaponInHand == WeaponKind.None && Live().Facts.Posture == Posture.HandsUp, 8);
                await Wait(1.5f);
                for (int i = 0; i < 3 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.25f); }
            } },
        // Challenged, he runs with the gun, never turning: shot in the back, BAD.
        new Case { Name = "Robber runs with the gun", Expect = Tier.Bad, Reason = "gun in his hand", Act = async () =>
            {
                ResetGunHooks(); IntruderT.TestTurnAndPoint = false;
                await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f);
                Yell(Command.DropWeapon);
                await WaitFor(() => State() == IntruderT.State.Fleeing, 10); await Wait(1.3f); // past the 1 s grace from the turn
                var f = Live().Facts;
                Debug.Log($"[HIS] SCENARIO runner: weapon {f.WeaponInHand} fleeing {f.Fleeing} pointing {f.PointingAtPlayer}");
                for (int i = 0; i < 3 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.25f); }
            } },
        // He runs, then turns and points it: shot while it's on you, GOOD.
        new Case { Name = "Robber runs with the gun", Expect = Tier.Good, Act = async () =>
            {
                ResetGunHooks(); IntruderT.TestTurnAndPoint = true;
                await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f);
                Yell(Command.GetOut);
                await WaitFor(() => State() == IntruderT.State.Fleeing, 10);
                if (!await WaitFor(() => Live() != null && Live().Facts.PointingAtPlayer, 12))
                { ExtraFail("runner turns and points", $"never pointed ({Live()?.Summary}, dist {Dist():F1})"); return; }
                ExtraPass("runner turns and points", $"at {Dist():F1} m");
                for (int i = 0; i < 2 && Alive(); i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.2f); }
            } },
        // Brett's habit, on a gunman: legs out, and from the floor he brings the gun onto you — killing him there is GOOD.
        new Case { Name = "Armed robber", Expect = Tier.Good, Act = async () =>
            {
                ResetGunHooks();
                await WaitFor(() => Alive() && State() == IntruderT.State.Idle && Dist() < 8f, 40);
                if (!await LegsOut()) return;
                if (!await WaitFor(() => Live() != null && Live().Facts.PointingAtPlayer, 10))
                { var ff = Live().Facts; ExtraFail("gun from the floor", $"never pointed from the floor (down {ff.Down}, weapon {ff.WeaponInHand}, {Live().Summary})"); }
                else ExtraPass("gun from the floor", "legs out, gun back on the player");
                await KillAndCheckBody("crawl death (gunman)");
            } },
        // Legs out, then "Drop it!": he lets go of it on the floor and gives up — finishing him is BAD.
        new Case { Name = "Robber drops the gun", Expect = Tier.Bad, Act = async () =>
            {
                ResetGunHooks(); IntruderT.TestRoll = 0f;
                await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f);
                if (!await LegsOut()) return;
                await Wait(0.5f); Yell(Command.DropWeapon);
                if (!await WaitFor(() => Live() != null && Live().Facts.WeaponInHand == WeaponKind.None, 6)) { ExtraFail("drops it from the floor", Live()?.Summary); return; }
                await Wait(1.5f);
                await KillAndCheckBody("crawl death (disarmed gunman)");
            } },
    };

    static void ResetGunHooks() { IntruderT.TestRoll = null; IntruderT.TestTurnAndPoint = null; IntruderT.TestSpread = null; }

    /// <summary>The tossed pistol leaves his hand and ends up lying on the floor, not in him or in the air.</summary>
    static async Task CheckTossed()
    {
        var i = Live();
        if (!await WaitFor(() => i != null && i.Facts.WeaponInHand == WeaponKind.None, 8)) { ExtraFail("gun tossed", "still in his hand: " + i?.Summary); return; }
        await Wait(2f);
        HIS.Intruder.IntruderWeapon gun = null;
        foreach (var w in Object.FindObjectsByType<HIS.Intruder.IntruderWeapon>(FindObjectsInactive.Exclude))
            if (w.Kind == WeaponKind.Gun && w.transform.parent == null) gun = w;
        if (gun == null) { ExtraFail("gun tossed", "no loose gun found"); return; }
        float above = gun.transform.position.y - i.transform.position.y, away = Vector3.Distance(Flat(gun.transform.position), Flat(i.transform.position));
        string info = $"{above:F2} m above his feet, {away:F2} m from him";
        if (above < 0.25f && above > -0.1f && away > 0.3f) ExtraPass("gun tossed", info); else ExtraFail("gun tossed", info);
        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    }

    /// <summary>Watches one gunman: when the tell started (and how long he'd been in view), when it finished, his first round, the first hit on you.</summary>
    sealed class TellWatch : System.IDisposable
    {
        public float TellStart = -1f, SeenAtTell = -1f, TellDone = -1f, FirstFire = -1f, FirstHit = -1f;
        static readonly Regex Seen = new(@"seen for ([0-9.]+) s");

        public TellWatch()
        {
            Application.logMessageReceived += OnLog;
            if (HIS.Core.PlayerState.Instance != null) HIS.Core.PlayerState.Instance.Hit += OnHit;
        }

        void OnLog(string msg, string stack, LogType t)
        {
            if (!msg.StartsWith("[HIS] Intruder")) return;
            if (TellStart < 0f && msg.Contains(": TELL — raising"))
            {
                TellStart = Time.time;
                var m = Seen.Match(msg);
                if (m.Success) float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out SeenAtTell);
            }
            else if (TellDone < 0f && msg.Contains(": TELL done")) TellDone = Time.time;
            else if (FirstFire < 0f && msg.Contains(": FIRES at the player")) FirstFire = Time.time;
        }

        void OnHit(float damage) { if (FirstHit < 0f) FirstHit = Time.time; }

        public void Judge()
        {
            string info = $"gate {SeenAtTell:F2} s in view, tell {TellStart:F2}→{TellDone:F2}, first round {FirstFire:F2}, first hit {(FirstHit < 0f ? "none" : FirstHit.ToString("F2"))}";
            bool ok = TellStart > 0f && TellDone >= TellStart + 0.6f && FirstFire >= TellDone && (FirstHit < 0f || FirstHit >= TellDone)
                      && SeenAtTell >= 1f - 0.05f;
            if (ok) ExtraPass("tell before fire", info); else ExtraFail("tell before fire", info);
        }

        public void Dispose()
        {
            Application.logMessageReceived -= OnLog;
            if (HIS.Core.PlayerState.Instance != null) HIS.Core.PlayerState.Instance.Hit -= OnHit;
        }
    }
}
