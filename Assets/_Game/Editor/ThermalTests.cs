using System;
using System.Threading.Tasks;
using HIS.Core;
using HIS.UI;
using UnityEditor;
using UnityEngine;
using Perf = OVRManager.ProcessorPerformanceLevel;

/// <summary>
/// Headless tests for the heat governor: the tier logic with a fake clock and fake temperatures (enter/exit thresholds,
/// hysteresis, step-down dwell, hard signals, low frame rate, a Brett-like 30-minute warm-up with sensor noise), the
/// budget/hint/notice tables, then Play Mode with a fake source (Hot → budget + break notice → cools → all restored).
///   NOQUIT=1 scripts/unity-batch.sh ThermalTests.RunHeadless   (exits 0 / 1; without NOQUIT=1 -quit ends the run after the
///   logic checks, before Play Mode, and no summary line prints). Grep "[HIS] THERMAL".
/// </summary>
public static class ThermalTests
{
    static int s_pass, s_fail;

    sealed class FakeSource : IThermalSource
    {
        public ThermalSample Next = ThermalSample.Unknown;
        public string Describe => "fake";
        public void Read(ref ThermalSample s, float now)
        {
            s.BatteryC = Next.BatteryC; s.Status = Next.Status; s.Headroom = Next.Headroom; s.Plugged = Next.Plugged;
        }
    }

    [MenuItem("HIS/Debug/Thermal Tests")]
    public static void RunHeadless()
    {
        s_pass = s_fail = 0;
        try { Logic(); Tables(); }
        catch (Exception e) { Fail("harness", e.ToString()); }
        finally { PerfBudget.Apply(ThermalTier.Normal); }
        if (!Application.isBatchMode) { Summary(); return; }
        // Play Mode part: the governor itself, fed by a fake source.
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        double deadline = EditorApplication.timeSinceStartup + 600;
        EditorApplication.update += () => { if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] THERMAL watchdog: timeout"); EditorApplication.Exit(3); } };
        EditorApplication.playModeStateChanged += async st =>
        {
            if (st != PlayModeStateChange.EnteredPlayMode) return;
            try { await Governor(); }
            catch (Exception e) { Fail("play harness", e.ToString()); }
            finally { ThermalGovernor.OverrideSource = null; PerfBudget.Apply(ThermalTier.Normal); }
            Summary();
            EditorApplication.Exit(s_fail == 0 && s_pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static void Summary()
    {
        string line = $"[HIS] THERMAL {s_pass}/{s_pass + s_fail} passed";
        if (s_fail == 0) Debug.Log(line); else Debug.LogError(line);
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { s_pass++; Debug.Log($"[HIS] THERMAL ok   {name}"); }
        else Fail(name, detail);
    }
    static void Fail(string name, string detail) { s_fail++; Debug.LogError($"[HIS] THERMAL FAIL {name} {detail}"); }

    static ThermalSample Batt(float c) { var s = ThermalSample.Unknown; s.BatteryC = c; return s; }
    static ThermalSample Head(float h) { var s = ThermalSample.Unknown; s.Headroom = h; return s; }

    // Feed the same sample every `step` seconds for `seconds`; returns the number of tier changes.
    static int Feed(ThermalTiers t, ref float now, ThermalSample s, float seconds, float step = 5f)
    {
        int changes = 0;
        for (float end = now + seconds; now < end - 1e-3f; now += step) if (t.Update(s, now)) changes++;
        return changes;
    }

    // ───────────────────────────── pure tier logic ─────────────────────────────

    static void Logic()
    {
        float now = 0f;
        var t = new ThermalTiers();
        Feed(t, ref now, ThermalSample.Unknown, 600f);
        Check("unknown signals stay Normal (Editor / no API)", t.Tier == ThermalTier.Normal);

        Feed(t, ref now, Batt(53.9f), 60f);
        Check("battery 53.9 C stays Normal", t.Tier == ThermalTier.Normal);
        t.Update(Batt(54.2f), now += 5f);
        Check("one battery reading over 54 C isn't enough (noise)", t.Tier == ThermalTier.Normal);
        t.Update(Batt(54.2f), now += 5f);
        Check("two in a row → Warm", t.Tier == ThermalTier.Warm, t.Reason);
        Check("reason names the battery", t.Reason.Contains("battery"), t.Reason);

        int flips = Feed(t, ref now, Batt(53f), 600f);
        Check("hysteresis: 53 C (between exit 52 and enter 54) holds Warm for 10 min", t.Tier == ThermalTier.Warm && flips == 0);

        Feed(t, ref now, Batt(51f), 55f);
        Check("cooling: still Warm 55 s under the exit", t.Tier == ThermalTier.Warm);
        Feed(t, ref now, Batt(53f), 5f);
        Feed(t, ref now, Batt(51f), 55f);
        Check("a warm blip restarts the 60 s cool-down", t.Tier == ThermalTier.Warm);
        Feed(t, ref now, Batt(51f), 10f);
        Check("60 s under the exit → Normal", t.Tier == ThermalTier.Normal, t.Reason);

        t.Reset(); now = 0f;
        t.Update(Head(0.91f), now += 5f); t.Update(Head(0.91f), now += 5f);
        Check("headroom 0.91 twice → straight to Hot", t.Tier == ThermalTier.Hot, t.Reason);
        Feed(t, ref now, Head(0.80f), 55f);
        Check("Hot holds for the dwell", t.Tier == ThermalTier.Hot);
        Feed(t, ref now, Head(0.80f), 10f);
        Check("headroom 0.80 for 60 s → Warm (one step)", t.Tier == ThermalTier.Warm, t.Reason);
        Feed(t, ref now, Head(0.80f), 300f);
        Check("0.80 keeps Warm (above its 0.72 exit)", t.Tier == ThermalTier.Warm);
        Feed(t, ref now, Head(0.50f), 65f);
        Check("0.50 for 60 s → Normal", t.Tier == ThermalTier.Normal);

        t.Reset(); now = 0f;
        var st = ThermalSample.Unknown; st.Status = 2;
        t.Update(st, now += 5f);
        Check("OS thermal status MODERATE → Hot at once", t.Tier == ThermalTier.Hot, t.Reason);
        t.Reset();
        var xr = ThermalSample.Unknown; xr.XrLevel = 25;
        t.Update(xr, now += 5f);
        Check("XR thermal warning → Warm at once", t.Tier == ThermalTier.Warm, t.Reason);
        xr.XrLevel = 0;
        Feed(t, ref now, xr, 65f);
        Check("XR back to normal → Normal after the dwell", t.Tier == ThermalTier.Normal);

        t.Reset();
        var fps = ThermalSample.Unknown; fps.Fps = 58f;
        t.Update(fps, now += 5f); t.Update(fps, now += 5f);
        Check("two low-fps polls aren't enough", t.Tier == ThermalTier.Normal);
        fps.Fps = float.NaN; t.Update(fps, now += 5f); // a loading/paused poll breaks the run
        fps.Fps = 58f; t.Update(fps, now += 5f); t.Update(fps, now += 5f);
        Check("a NaN (loading) poll resets the low-fps run", t.Tier == ThermalTier.Normal);
        t.Update(fps, now += 5f);
        Check("three low-fps polls → Warm", t.Tier == ThermalTier.Warm, t.Reason);

        // Brett's 0.1.15 curve (31 → 50 °C battery over 29 min, ±0.3 °C noise): the headset was nowhere near throttling
        // (battery throttles at 58 °C), so the battery alone must not cut blood/effects for the rest of the session.
        t.Reset(); now = 0f;
        var rng = new System.Random(7);
        int changes = 0;
        for (; now < 29f * 60f; now += 5f)
            if (t.Update(Batt(31f + 19f * Mathf.Sqrt(now / (29f * 60f)) + (float)(rng.NextDouble() - 0.5) * 0.6f), now)) changes++;
        Check("0.1.15 curve (peak 50 C): stays Normal the whole session", t.Tier == ThermalTier.Normal && changes == 0, $"changes {changes}");

        // A hotter session (31 → 56 °C, e.g. charging while playing), then 20 min of cooling to 46 °C.
        t.Reset(); now = 0f;
        float warmAt = -1f; bool hot = false;
        for (; now < 29f * 60f; now += 5f)
        {
            float c = 31f + 25f * Mathf.Sqrt(now / (29f * 60f)) + (float)(rng.NextDouble() - 0.5) * 0.6f;
            if (t.Update(Batt(c), now)) { changes++; if (t.Tier == ThermalTier.Warm && warmAt < 0) warmAt = now; }
            hot |= t.Tier == ThermalTier.Hot;
        }
        Check("hot curve (peak 56 C): Warm reached near the top, never Hot, no flapping", warmAt > 0 && !hot && changes == 1, $"warmAt {warmAt / 60f:0.0} min, changes {changes}");
        for (float end = now + 20f * 60f; now < end; now += 5f)
        {
            float c = 56f - 10f * Mathf.Clamp01((now - 29f * 60f) / 600f) + (float)(rng.NextDouble() - 0.5) * 0.6f;
            if (t.Update(Batt(c), now)) changes++;
        }
        Check("cools back to Normal with exactly one step down", t.Tier == ThermalTier.Normal && changes == 2, $"changes {changes}");
    }

    // ───────────────────────────── budget / hints / notice tables ─────────────────────────────

    static void Tables()
    {
        PerfBudget.Apply(ThermalTier.Normal);
        Check("Normal budget = shipped values", PerfBudget.BloodDecalCap == 120 && PerfBudget.Spray(60) == 60 && PerfBudget.Confetti(150) == 150 && PerfBudget.MaxCorpses >= 2);
        PerfBudget.Apply(ThermalTier.Warm);
        Check("Warm trims blood/spray/confetti/bodies", PerfBudget.BloodDecalCap == 60 && PerfBudget.Spray(60) == 36 && PerfBudget.Confetti(150) == 90 && PerfBudget.MaxCorpses == 1);
        PerfBudget.Apply(ThermalTier.Hot);
        Check("Hot trims further", PerfBudget.BloodDecalCap == 30 && PerfBudget.Spray(60) == 21 && PerfBudget.Confetti(150) == 60 && PerfBudget.MaxCorpses == 1);
        PerfBudget.Apply(ThermalTier.Normal);
        Check("back to Normal restores everything", PerfBudget.BloodDecalCap == 120 && PerfBudget.Spray(60) == 60 && PerfBudget.Confetti(150) == 150);
        Check("foveation steps up when warm", PerfBudget.Normal.Foveation < PerfBudget.Warm.Foveation && PerfBudget.Normal.DynamicFoveation && !PerfBudget.Hot.DynamicFoveation);

        bool boost = false;
        foreach (ThermalGovernor.Phase p in Enum.GetValues(typeof(ThermalGovernor.Phase)))
            foreach (ThermalTier tier in Enum.GetValues(typeof(ThermalTier)))
                boost |= ThermalGovernor.HintFor(p, tier) == Perf.Boost;
        Check("never Boost", !boost);
        Check("live fight = SustainedLow at every tier (0.1.15 sent no hint and ran fights at levels 2/2, inside Low's range)",
            ThermalGovernor.HintFor(ThermalGovernor.Phase.Live, ThermalTier.Normal) == Perf.SustainedLow &&
            ThermalGovernor.HintFor(ThermalGovernor.Phase.Live, ThermalTier.Warm) == Perf.SustainedLow);
        Check("room loading = SustainedHigh unless Hot", ThermalGovernor.HintFor(ThermalGovernor.Phase.Loading, ThermalTier.Normal) == Perf.SustainedHigh &&
            ThermalGovernor.HintFor(ThermalGovernor.Phase.Loading, ThermalTier.Hot) == Perf.SustainedLow);
        Check("between rounds / menus / show = SustainedLow", ThermalGovernor.HintFor(ThermalGovernor.Phase.Idle, ThermalTier.Normal) == Perf.SustainedLow);
        Check("Hot = SustainedLow even live", ThermalGovernor.HintFor(ThermalGovernor.Phase.Live, ThermalTier.Hot) == Perf.SustainedLow);

        Check("break notice: Hot, between rounds", ThermalGovernor.WantsBreakNotice(ThermalTier.Hot, ThermalGovernor.Phase.Idle, false));
        Check("break notice: never mid-fight", !ThermalGovernor.WantsBreakNotice(ThermalTier.Hot, ThermalGovernor.Phase.Live, false));
        Check("break notice: not over the verdict show", !ThermalGovernor.WantsBreakNotice(ThermalTier.Hot, ThermalGovernor.Phase.Idle, true));
        Check("break notice: not when only Warm", !ThermalGovernor.WantsBreakNotice(ThermalTier.Warm, ThermalGovernor.Phase.Idle, false));
    }

    // ───────────────────────────── Play Mode: the governor with a fake source ─────────────────────────────

    static async Task Governor()
    {
        if (!await WaitFor(() => ThermalGovernor.Instance != null, 30f)) { Fail("governor self-starts", "no instance"); return; }
        var gov = ThermalGovernor.Instance;
        Check("governor self-starts at Normal", gov.Tier == ThermalTier.Normal && PerfBudget.BloodDecalCap == 120);
        var fake = new FakeSource();
        ThermalGovernor.OverrideSource = fake;
        gov.PollSeconds = 0.2f;
        gov.Rebind();
        gov.Tiers.StepDownSeconds = 1f;

        fake.Next = Head(0.95f);
        if (!await WaitFor(() => gov.Tier == ThermalTier.Hot, 10f)) { Fail("fake headroom 0.95 → Hot", gov.Tier.ToString()); return; }
        Check("fake headroom 0.95 → Hot", true);
        Check("Hot budget applied", PerfBudget.BloodDecalCap == 30 && PerfBudget.MaxCorpses == 1);
        bool live = gov.CurrentPhase == ThermalGovernor.Phase.Live;
        Check($"break notice posted only between rounds ({gov.CurrentPhase})",
            await WaitFor(() => (Notice.Posted(ThermalGovernor.NoticeKey) != null) == !live, 3f));
        Check("HUD shows the tier", ThermalGovernor.HudLine.StartsWith("HOT"), ThermalGovernor.HudLine);

        fake.Next = Head(0.40f);
        if (!await WaitFor(() => gov.Tier == ThermalTier.Normal, 15f)) { Fail("cools → Normal", gov.Tier.ToString()); return; }
        Check("cools Hot → Warm → Normal", true);
        Check("budget restored", PerfBudget.BloodDecalCap == 120 && PerfBudget.Spray(60) == 60);
        Check("break notice gone", await WaitFor(() => Notice.Posted(ThermalGovernor.NoticeKey) == null, 3f));
    }

    static async Task<bool> WaitFor(Func<bool> cond, float timeout)
    {
        for (float t = 0; t < timeout; t += 0.1f)
        {
            if (!EditorApplication.isPlaying) return false;
            if (cond()) return true;
            await Task.Delay(100);
        }
        return cond();
    }
}
