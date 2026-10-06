using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The player's wounds end to end (Brett 0.1.17: "my health should only allow me to get shot center mass or head once and
/// twice for legs and arms and stabbed once before I die"). God mode is off for these cases only: a punch costs hit points,
/// the first limb round leaves you standing and the second kills; one knife blow kills; the gunman's round straight down his
/// barrel lands center mass and kills. Each death ends the round as YOU DIED, and the next round starts whole. The rule, the
/// zones and the spread on their own: WoundSelfTest (inside JudgeSelfTest.Run).
///   Just these: scripts/unity-batch.sh ScenarioTests.RunWoundsHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_woundsOnly;

    /// <summary>Batch entry for just the wound cases: scripts/unity-batch.sh ScenarioTests.RunWoundsHeadless.</summary>
    public static void RunWoundsHeadless() { s_woundsOnly = s_doorwayOnly = true; RunHeadless(); } // (the doorway run's short start)

    static IEnumerable<Case> WithWoundCases(IEnumerable<Case> cases)
    {
        if (!s_woundsOnly) foreach (var c in cases) yield return c;
        foreach (var c in WoundCases()) yield return c;
    }

    static Case[] WoundCases() => new[]
    {
        // His fists aren't in the rule: a punch costs hit points, never your life. Then two gun wounds to the limbs straight into
        // PlayerState (where the gunman's rounds land is the robber case below): the first leaves you standing, the second kills.
        new Case { Name = "Home invader", Expect = Tier.YouDied, Doorway = s_woundsOnly, Act = async () =>
            {
                var ps = PlayerState.Instance;
                using var hits = new PlayerHitWatch();
                PlayerState.GodMode = false;
                try
                {
                    string punch = await WaitFor(() => hits.Count > 0, 60) ? hits.Last : $"never punched ({Live()?.Summary})";
                    if (!punch.Contains("by fists -> ") || ps.IsDead || ps.Health >= ps.MaxHealth || ps.LimbWounds != 0)
                        ExtraFail("punch is not a stab", $"{punch} — {ps.Health:F0} hp, dead {ps.IsDead}, limb wounds {ps.LimbWounds}");
                    else ExtraPass("punch is not a stab", punch);
                    var from = Live() != null ? Live().transform.position : Vector3.zero;
                    ps.TakeHit(HitZone.Arm, HitBy.Gun, from);
                    string first = hits.Last;
                    if (ps.IsDead || ps.LimbWounds != 1 || !first.Contains("arm by gun -> limb wound 1/2"))
                        ExtraFail("first limb wound survives", $"{first} — dead {ps.IsDead}, limb wounds {ps.LimbWounds}");
                    else ExtraPass("first limb wound survives", first);
                    ps.TakeHit(HitZone.Leg, HitBy.Gun, from);
                    string second = hits.Last;
                    if (!ps.IsDead || !second.Contains("leg by gun -> dead")) ExtraFail("second limb wound kills", $"{second} — dead {ps.IsDead}");
                    else ExtraPass("second limb wound kills", second);
                }
                finally { PlayerState.GodMode = true; }
                if (!ps.IsDead) await EndRound();
            } },
        // The round after starts whole (GameSession.ClearWorld → ResetHealth: no limb wound carried over). Then the knife man
        // reaches you: one blow and you're dead.
        new Case { Name = "Knife attacker", Expect = Tier.YouDied, Doorway = s_woundsOnly, Act = async () =>
            {
                var ps = PlayerState.Instance;
                string whole = $"dead {ps.IsDead}, limb wounds {ps.LimbWounds}, {ps.Health:F0}/{ps.MaxHealth:F0} hp";
                if (ps.IsDead || ps.LimbWounds != 0 || ps.Health < ps.MaxHealth) ExtraFail("wounds reset with the round", whole);
                else ExtraPass("wounds reset with the round", whole);
                using var hits = new PlayerHitWatch();
                PlayerState.GodMode = false;
                try
                {
                    if (!await WaitFor(() => ps.IsDead || hits.Count > 0, 60)) ExtraFail("one stab kills", $"never reached you ({Live()?.Summary})");
                    else if (hits.Count == 1 && ps.IsDead && hits.Last.Contains("by knife -> dead")) ExtraPass("one stab kills", hits.Last);
                    else ExtraFail("one stab kills", $"{hits.All} — dead {ps.IsDead}");
                }
                finally { PlayerState.GodMode = true; }
                if (!ps.IsDead) await EndRound();
            } },
        // The gunman with no spread: the round flies straight down his barrel, which the aim pass holds on your chest, so it
        // must land center mass (the ray through the player's body shapes) — and that one round kills.
        new Case { Name = "Armed robber", Expect = Tier.YouDied, Doorway = s_woundsOnly, Act = async () =>
            {
                ResetGunHooks(); IntruderT.TestSpread = 0f;
                var ps = PlayerState.Instance;
                using var hits = new PlayerHitWatch();
                PlayerState.GodMode = false;
                try
                {
                    if (!await WaitFor(() => ps.IsDead || hits.Fires >= 3, 60)) // (three rounds and still alive: fail now, not in a minute)
                        ExtraFail("one round center mass kills", $"never fired ({Live()?.Summary})");
                    else if (ps.IsDead && hits.Count == 1 && hits.Fires == 1 && hits.FirstFire.Contains("HIT torso") && hits.Last.Contains("torso by gun -> dead"))
                        ExtraPass("one round center mass kills", $"{hits.FirstFire} | {hits.Last}");
                    else ExtraFail("one round center mass kills", $"{hits.Fires} round(s): {hits.FirstFire} | {hits.All} — dead {ps.IsDead}");
                }
                finally { PlayerState.GodMode = true; IntruderT.TestSpread = null; }
                if (!ps.IsDead) await EndRound();
            } },
    };

    /// <summary>A failed wound case still has to end: shotgun blasts until he's down.</summary>
    static async Task EndRound()
    {
        for (int i = 0; i < 4 && Alive(); i++) { Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine); await Wait(0.3f); }
    }

    /// <summary>This case's "[HIS] Player hit:" lines, and the gunman's "FIRES at the player" lines.</summary>
    sealed class PlayerHitWatch : System.IDisposable
    {
        readonly List<string> _hits = new(), _fires = new();

        public PlayerHitWatch() => Application.logMessageReceived += OnLog;

        void OnLog(string msg, string stack, LogType t)
        {
            if (msg.StartsWith("[HIS] Player hit:")) _hits.Add(msg);
            else if (msg.StartsWith("[HIS] Intruder") && msg.Contains("FIRES at the player")) _fires.Add(msg);
        }

        public int Count => _hits.Count;
        public int Fires => _fires.Count;
        public string Last => _hits.Count > 0 ? _hits[^1] : "(no hit)";
        public string FirstFire => _fires.Count > 0 ? _fires[0] : "(no round fired)";
        public string All => _hits.Count > 0 ? string.Join(" | ", _hits) : "(no hit)";

        public void Dispose() => Application.logMessageReceived -= OnLog;
    }
}
