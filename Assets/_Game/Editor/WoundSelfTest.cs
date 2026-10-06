using System;
using HIS.Core;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Pure-logic test of the player's wounds (Brett 0.1.17: one round to the head or center mass kills, the second round to an arm
/// or a leg kills, one knife blow kills; fists cost hit points): <see cref="PlayerState.IsFatal"/>, the ray-vs-body zones of
/// <see cref="PlayerShape"/> and the gunman's spread calibration. No scene, no Play Mode. Runs inside JudgeSelfTest.Run (counted
/// in its JUDGE total), from HIS > Debug > Wound Self-Test, or alone: scripts/unity-batch.sh WoundSelfTest.Run. Grep "[HIS] WOUND".
/// </summary>
public static class WoundSelfTest
{
    static int s_pass, s_total;

    [MenuItem("HIS/Debug/Wound Self-Test")]
    public static void Run()
    {
        var (pass, total) = Checks();
        string summary = $"[HIS] WOUND {pass}/{total} passed";
        if (pass == total) Debug.Log(summary); else Debug.LogError(summary);
        if (Application.isBatchMode) EditorApplication.Exit(pass == total ? 0 : 1);
    }

    /// <summary>Every check once; returns (passed, total).</summary>
    public static (int pass, int total) Checks()
    {
        s_pass = s_total = 0;
        try
        {
            Rule();
            Zones();
            Primitives();
            Spread();
        }
        catch (Exception ex) { s_total++; Fail("harness", "no exception", ex.ToString()); }
        return (s_pass, s_total);
    }

    // ---------------------------------------------------------------- the 1 / 2 / 1 rule

    static void Rule()
    {
        ExpectFatal("gun, head, unhurt", HitBy.Gun, HitZone.Head, 0, true);
        ExpectFatal("gun, torso (center mass), unhurt", HitBy.Gun, HitZone.Torso, 0, true);
        ExpectFatal("gun, first arm round", HitBy.Gun, HitZone.Arm, 0, false);
        ExpectFatal("gun, first leg round", HitBy.Gun, HitZone.Leg, 0, false);
        ExpectFatal("gun, second limb round (arm after a leg)", HitBy.Gun, HitZone.Arm, 1, true);
        ExpectFatal("gun, second limb round (leg after an arm)", HitBy.Gun, HitZone.Leg, 1, true);
        ExpectFatal("gun, torso after a limb wound", HitBy.Gun, HitZone.Torso, 1, true);
        ExpectFatal("knife, one stab to the body", HitBy.Knife, HitZone.Torso, 0, true);
        ExpectFatal("knife, one slash at the legs (from the floor)", HitBy.Knife, HitZone.Leg, 0, true);
        ExpectFatal("knife, one blow to an arm", HitBy.Knife, HitZone.Arm, 0, true);
        ExpectFatal("fists, a punch to the face (hit points, not the rule)", HitBy.Fists, HitZone.Head, 0, false);
        ExpectFatal("fists, a punch after a limb wound", HitBy.Fists, HitZone.Torso, 1, false);
        ExpectFatal("gun, a miss", HitBy.Gun, HitZone.None, 1, false);
        ExpectFatal("knife, a miss", HitBy.Knife, HitZone.None, 0, false);
        Check("two limb wounds to die", PlayerState.LimbWoundsToDie == 2, "2", PlayerState.LimbWoundsToDie.ToString());
    }

    static void ExpectFatal(string name, HitBy by, HitZone zone, int limbsBefore, bool fatal)
    {
        bool got = PlayerState.IsFatal(by, zone, limbsBefore);
        Check("rule: " + name, got == fatal, fatal ? "fatal" : "survives", got ? "fatal" : "survives");
    }

    // ---------------------------------------------------------------- where a ray lands on the body

    // The reference man: eyes 1.6 m up at the origin, facing +z, belt at PlayerBody's height, floor at 0.
    static readonly Vector3 Eye = new(0f, 1.6f, 0f);
    static readonly Vector3 Neck = Eye - new Vector3(0f, 0.075f, 0.0805f);
    static readonly Vector3 Waist = new(0f, 1.6f * 0.69f, Neck.z);

    static PlayerShape Standing(Vector3? left = null, Vector3? right = null, Vector3? waist = null) =>
        PlayerShape.Build(Eye, Quaternion.identity, Neck, waist ?? Waist, Quaternion.identity, 0f, left, right);

    static void Zones()
    {
        var man = Standing();
        var front = new Vector3(0f, 0f, 3f); // a shooter 3 m in front of him (x, y set per ray)
        ExpectZone("head from the front", man, At(front, 0f, 1.61f), Vector3.back, HitZone.Head);
        ExpectZone("chest from the front", man, At(front, 0f, 1.25f), Vector3.back, HitZone.Torso);
        ExpectZone("belly from the front", man, At(front, 0f, 1.0f), Vector3.back, HitZone.Torso);
        ExpectZone("neck from the front (center mass)", man, At(front, 0f, 1.49f), Vector3.back, HitZone.Torso);
        ExpectZone("thigh from the front", man, At(front, 0.1f, 0.6f), Vector3.back, HitZone.Leg);
        ExpectZone("shin from the front", man, At(front, -0.1f, 0.25f), Vector3.back, HitZone.Leg);
        ExpectZone("hanging right arm from the front", man, At(front, 0.18f, 1.1f), Vector3.back, HitZone.Arm);
        ExpectZone("0.5 m to his side", man, At(front, 0.5f, 1.25f), Vector3.back, HitZone.None);
        ExpectZone("over his head", man, At(front, 0f, 1.85f), Vector3.back, HitZone.None);
        ExpectZone("between his feet, low", man, At(front, 0f, 0.3f), Vector3.back, HitZone.None);
        ExpectZone("chest from behind", man, new Vector3(0f, 1.25f, -3f), Vector3.forward, HitZone.Torso);
        ExpectZone("from his right side: the arm before the chest", man, new Vector3(3f, 1.3f, Neck.z), Vector3.left, HitZone.Arm);
        ExpectZone("round travelling away from him", man, At(front, 0f, 1.25f), Vector3.forward, HitZone.None);

        // Gun up two-handed at chest height: the arms in front of the chest take a round aimed at the chest.
        var aiming = Standing(left: new Vector3(-0.03f, 1.25f, 0.37f), right: new Vector3(0.03f, 1.25f, 0.37f));
        ExpectZone("gun up: chest shot hits the arms first", aiming, At(front, 0f, 1.25f), Vector3.back, HitZone.Arm);
        ExpectZone("gun up: belly still torso", aiming, At(front, 0f, 1.0f), Vector3.back, HitZone.Torso);
        // A controller out of reach (untracked, at the rig origin) is not a hand: the arm hangs.
        var untrackedHands = Standing(left: Vector3.zero, right: Vector3.zero);
        ExpectZone("untracked controllers: arms hang", untrackedHands, At(front, 0.18f, 1.1f), Vector3.back, HitZone.Arm);

        // Crouched (belt at 0.6 m): the legs are short, the torso comes down with the belt.
        var low = Standing(waist: new Vector3(0f, 0.6f, Neck.z));
        ExpectZone("crouched: shin", low, At(front, 0.1f, 0.3f), Vector3.back, HitZone.Leg);
        ExpectZone("crouched: low belly", low, At(front, 0f, 0.6f), Vector3.back, HitZone.Torso);

        // The first surface: the chest's front, 0.14 m in front of the torso axis.
        man.Raycast(At(front, 0f, 1.25f), Vector3.back, 30f, out float at);
        float want = 3f - Neck.z - PlayerShape.TorsoRadius;
        Check("distance to the chest's front", Mathf.Abs(at - want) < 0.01f, $"{want:F3} m", $"{at:F3} m");

        // The live path with no tracked head (headless tests): built from the eye alone, standing, hands hanging.
        var fallback = PlayerShape.Current(null, Eye, 0f);
        ExpectZone("no head transform: chest", fallback, At(front, 0f, 1.25f), Vector3.back, HitZone.Torso);
        ExpectZone("no head transform: thigh", fallback, At(front, 0.1f, 0.6f), Vector3.back, HitZone.Leg);
    }

    static Vector3 At(Vector3 p, float x, float y) => new(x, y, p.z);

    static void ExpectZone(string name, PlayerShape s, Vector3 from, Vector3 dir, HitZone want)
    {
        var got = s.Raycast(from, dir, 30f, out _);
        Check("zone: " + name, got == want, PlayerShape.Name(want), PlayerShape.Name(got));
    }

    // ---------------------------------------------------------------- the ray primitives' edge cases

    static void Primitives()
    {
        Vector3 a = Vector3.zero, b = Vector3.up;
        Near("capsule side", PlayerShape.RayCapsule(new Vector3(0f, 0.5f, 2f), Vector3.back, a, b, 0.1f), 1.9f);
        Near("capsule down its axis (parallel: the end cap)", PlayerShape.RayCapsule(new Vector3(0f, 3f, 0f), Vector3.down, a, b, 0.1f), 1.9f);
        Near("capsule down, off the axis (end cap)", PlayerShape.RayCapsule(new Vector3(0.05f, 3f, 0f), Vector3.down, a, b, 0.1f), 2f - Mathf.Sqrt(0.0075f));
        Near("capsule past its end, grazing the cap", PlayerShape.RayCapsule(new Vector3(0f, 1.05f, 2f), Vector3.back, a, b, 0.1f), 2f - Mathf.Sqrt(0.0075f));
        Near("capsule missed above the cap", PlayerShape.RayCapsule(new Vector3(0f, 1.2f, 2f), Vector3.back, a, b, 0.1f), -1f);
        Near("capsule from inside (a contact shot)", PlayerShape.RayCapsule(new Vector3(0f, 0.5f, 0.05f), Vector3.back, a, b, 0.1f), 0f);
        Near("capsule behind the ray", PlayerShape.RayCapsule(new Vector3(0f, 0.5f, 2f), Vector3.forward, a, b, 0.1f), -1f);
        Near("capsule, degenerate (a sphere)", PlayerShape.RayCapsule(new Vector3(0f, 0f, 2f), Vector3.back, a, a, 0.1f), 1.9f);
        Near("sphere", PlayerShape.RaySphere(new Vector3(0f, 0f, 2f), Vector3.back, a, 0.1f), 1.9f);
        Near("sphere behind the ray", PlayerShape.RaySphere(new Vector3(0f, 0f, 2f), Vector3.forward, a, 0.1f), -1f);
    }

    static void Near(string name, float got, float want) =>
        Check("ray: " + name, Mathf.Abs(got - want) < 1e-3f, want.ToString("F3"), got.ToString("F3"));

    // ---------------------------------------------------------------- the gunman's spread

    static void Spread()
    {
        var aim = new Vector3(0.2f, -0.1f, 1f).normalized;
        var straight = PlayerShape.Spread(aim, 0f, 0.3f, 0.7f);
        Check("spread 0 = straight down the barrel", (straight - aim).sqrMagnitude < 1e-10f, aim.ToString("F4"), straight.ToString("F4"));
        // Rayleigh: the mean angular error of a 2D Gaussian of σ is σ·√(π/2).
        var rng = new System.Random(17);
        float sigma = 3f * Mathf.Deg2Rad, sum = 0f; const int n = 4000;
        for (int i = 0; i < n; i++) sum += Vector3.Angle(PlayerShape.Spread(aim, sigma, (float)rng.NextDouble(), (float)rng.NextDouble()), aim);
        float mean = sum / n, want = 3f * Mathf.Sqrt(Mathf.PI / 2f);
        Check("spread: mean error of a 3° σ", Mathf.Abs(mean - want) < 0.15f, $"{want:F2}°", $"{mean:F2}°");
        float s2 = PlayerShape.SpreadSigma(0.6f, 2f) * Mathf.Rad2Deg, s8 = PlayerShape.SpreadSigma(0.6f, 8f) * Mathf.Rad2Deg;
        float s4 = PlayerShape.SpreadSigma(0.6f, 4f) * Mathf.Rad2Deg, s4low = PlayerShape.SpreadSigma(0.3f, 4f) * Mathf.Rad2Deg;
        Check("spread: same P = same size at the target (a smaller angle far away); lower P = wider", s8 < s2 && s4low > s4,
              "σ(8 m) < σ(2 m), σ(P 0.3) > σ(P 0.6)", $"P 0.6: {s2:F1}° at 2 m, {s8:F1}° at 8 m; at 4 m P 0.6 {s4:F1}°, P 0.3 {s4low:F1}°");

        // Calibration: rounds aimed at the reference man's chest from his front hit him about as often as the gunman's P(hit)
        // (the tuning it replaced), and a round straight down the barrel is center mass.
        HitRate("2 m, P 0.60 (close, first round)", 2f, 0.6f, 0.45f, 0.85f);
        HitRate("2 m, P 0.75 (close, steady)", 2f, 0.75f, 0.6f, 0.92f);
        HitRate("8 m, P 0.30 (far)", 8f, 0.3f, 0.2f, 0.55f);
        HitRate("4 m, P 0.15 (hurried, the runner's turn)", 4f, 0.15f, 0.08f, 0.35f);
        var man = Standing();
        var muzzle = new Vector3(0.1f, 1.45f, 3f);
        var chest = new Vector3(0f, 1.25f, Neck.z);
        var dead = man.Raycast(muzzle, PlayerShape.Spread(chest - muzzle, 0f, 0.5f, 0.5f), 30f, out _);
        Check("spread 0 at the chest: center mass", dead == HitZone.Torso, "torso", PlayerShape.Name(dead));
    }

    static void HitRate(string name, float range, float p, float lo, float hi)
    {
        var man = Standing();
        var chest = new Vector3(0f, 1.25f, Neck.z);
        var muzzle = chest + new Vector3(0.1f, 0.2f, range);
        var aim = (chest - muzzle).normalized;
        float sigma = PlayerShape.SpreadSigma(p, Vector3.Distance(muzzle, Eye));
        var rng = new System.Random(5);
        int hits = 0, head = 0, torso = 0, arm = 0, leg = 0; const int n = 4000;
        for (int i = 0; i < n; i++)
        {
            var z = man.Raycast(muzzle, PlayerShape.Spread(aim, sigma, (float)rng.NextDouble(), (float)rng.NextDouble()), 30f, out _);
            if (z == HitZone.None) continue;
            hits++;
            if (z == HitZone.Head) head++; else if (z == HitZone.Torso) torso++; else if (z == HitZone.Arm) arm++; else leg++;
        }
        float rate = hits / (float)n;
        Check($"calibration {name}", rate >= lo && rate <= hi, $"{lo:F2}–{hi:F2}",
              $"{rate:F2} (head {head / (float)n:F2} torso {torso / (float)n:F2} arm {arm / (float)n:F2} leg {leg / (float)n:F2}, σ {sigma * Mathf.Rad2Deg:F1}°)");
    }

    // ---------------------------------------------------------------- helpers

    static void Check(string name, bool ok, string expected, string got)
    {
        s_total++;
        if (ok) { s_pass++; Debug.Log($"[HIS] WOUND PASS {name} ({got})"); }
        else Fail(name, expected, got);
    }

    static void Fail(string name, string expected, string got) => Debug.LogError($"[HIS] WOUND FAIL {name}: expected {expected} got {got}");
}
