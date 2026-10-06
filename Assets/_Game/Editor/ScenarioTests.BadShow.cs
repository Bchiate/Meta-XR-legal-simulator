using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Core;
using HIS.Outcome;
using HIS.Room;
using HIS.Scenario;
using UnityEngine;

/// <summary>
/// The funny bad-call show (PLAN M4; <see cref="BadCallShow"/>), driven the way Brett reaches it on the headset: pause
/// (Menu) → "Preview bad call". The picker row is in the pause panel and saves; every single pick (Bullhorn, Sirens,
/// Helicopter, Mugshot) plays that bit start to finish with no exception (lines, sirens joining, searchlight on a scanned
/// surface, mugshot + two flashes, the banner after the mugshot's hold), fits in 12.5 s, and afterwards leaves nothing
/// behind (no loops, no props, the passthrough restored); a B/Y skip ends one early and cleans up the same way.
/// Brett 0.1.16: the helicopter line comes from a pool (never twice running, clip-less lines skipped → the window line);
/// the mugshot board lands in the gaze cone with and without a wall ahead, holds ~3 s and never covers the banner.
/// Brett 0.1.17: "All" features ONE bit per bad call in rotation (helicopter first, saved across sessions; each preview
/// moves it on, single picks force their bit); the mugshot board is placed once and stays put when he turns left; "your
/// other left" is a rare treat (at most once per session, never twice running).
///   Quick run: NOQUIT=1 scripts/unity-batch.sh ScenarioTests.RunBadShowHeadless (also part of the full scenario suite).
/// </summary>
public static partial class ScenarioTests
{
    static bool s_badShowOnly;
    const string RotationPref = "his.badShow.next";

    /// <summary>Batch entry for just the bad-call show checks: NOQUIT=1 scripts/unity-batch.sh ScenarioTests.RunBadShowHeadless.</summary>
    public static void RunBadShowHeadless() { s_badShowOnly = true; RunHeadless(); }

    static async Task BadShowChecks()
    {
        var show = OutcomeShow.Instance; var dir = ScenarioDirector.Instance; var pause = GamePause.Instance;
        if (show == null || dir == null || pause == null || show.BadCall == null) { ExtraFail("bad-call show", "no OutcomeShow / director / pause menu"); return; }
        var saved = BadCallShow.Mode;
        bool hadRotation = PlayerPrefs.HasKey(RotationPref);
        int savedRotation = BadCallShow.RotationIndex;
        int savedOtherLeft = PlayerPrefs.GetInt(BadCallShow.OtherLeftPrefKey, 0);
        try
        {
            // The picker: both rows in the pause panel, cycling wraps and is saved; All names the bit that comes next.
            string err = null, menu = pause.DebugMenuText();
            if (!menu.Contains("Bad-call show") || !menu.Contains("Preview bad call")) err = "rows missing from the pause panel";
            BadCallShow.Mode = BadShowMode.Mugshot; BadCallShow.Cycle(1);
            if (err == null && (BadCallShow.Mode != BadShowMode.All || PlayerPrefs.GetInt("his.badShow", -1) != 0)) err = $"cycle past Mugshot → {BadCallShow.Mode}, saved {PlayerPrefs.GetInt("his.badShow", -1)}";
            BadCallShow.RotationIndex = 0;
            if (err == null && !pause.DebugMenuText().Contains($"Next: {BadCallShow.Rotation[0]}")) err = "All doesn't name the next bit";
            BadCallShow.Cycle(-1);
            if (err == null && BadCallShow.Mode != BadShowMode.Mugshot) err = "cycle back from All should wrap to Mugshot";
            BadCallShow.Mode = BadShowMode.Helicopter;
            menu = pause.DebugMenuText();
            if (err == null && !menu.Contains("<u>Helicopter</u>")) err = "the pick isn't marked in the panel";
            if (err == null && menu.Contains("Next:")) err = "a single pick still shows the rotation line";
            Check("bad-call picker", err, "rows in the pause panel, wraps both ways, saved to PlayerPrefs, All names the next bit");

            HeliLineChecks();
            OtherLeftRuleChecks();
            RotationRuleChecks();
            // Every preview below looks at a scanned wall 1.8 m ahead (when the room has one), like standing in the room.
            OutcomeShow.TestHead = MugshotPlacementChecks();

            // A pause mid-flash: the flash runs out on real time, so the room isn't held blown white behind the menu.
            // (Left paused: the first preview below starts from the pause menu anyway.)
            if (show.Lights != null)
            {
                show.Lights.Flash(0.85f, 0.35f);
                pause.AddReason("menu");
                await Wait(0.8f);
                Check("camera flash vs pause", show.Lights.FlashActive || !show.Lights.Idle ? "still flashing 0.8 s into a pause" : null,
                      "a flash runs out on real time behind the pause menu");
            }

            // Everything below is one "session" of consecutive previews for the other-left rule, counted at the end.
            BadCallShow.NewOtherLeftSession();
            PlayerPrefs.SetInt(BadCallShow.OtherLeftPrefKey, 0);
            int turns0 = BadCallShow.TurnLinesSpoken, others0 = BadCallShow.OtherLeftsSpoken;

            bool? wall = OutcomeShow.TestHead != null ? (bool?)true : null;
            foreach (var m in new[] { BadShowMode.Bullhorn, BadShowMode.Sirens, BadShowMode.Helicopter, BadShowMode.Mugshot })
                await PreviewAndWatch(m, -1f, wall);
            // Looking 45° down at the floor: no wall inside the cone, so the board floats — and must be gone before the banner.
            if (OutcomeShow.TestHead is Pose wp)
            {
                OutcomeShow.TestHead = new Pose(wp.position, wp.rotation * Quaternion.Euler(45f, 0f, 0f));
                await PreviewAndWatch(BadShowMode.Mugshot, -1f, false, " (looking down: floating)");
                OutcomeShow.TestHead = wp;
            }
            await RotationPreviews(wall);
            await PreviewAndWatch(BadShowMode.Mugshot, 6.5f); // skipped mid-mugshot: props and the flash must go too
            await MugshotStaysPutCheck();

            int turns = BadCallShow.TurnLinesSpoken - turns0, others = BadCallShow.OtherLeftsSpoken - others0;
            Check("bad-call other left (live)",
                  turns < 2 ? $"only {turns} turn line(s) heard" : others > 1 ? $"'your other left' {others}× in {turns} consecutive mugshots" : null,
                  $"{turns} consecutive mugshot previews with a turn line: 'your other left' {others}× (≤ 1)");
        }
        finally
        {
            BadCallShow.Mode = saved;
            if (hadRotation) BadCallShow.RotationIndex = savedRotation;
            else { PlayerPrefs.DeleteKey(RotationPref); BadCallShow.ForgetCachedPrefs(); }
            PlayerPrefs.SetInt(BadCallShow.OtherLeftPrefKey, savedOtherLeft);
            OutcomeShow.TestHead = null;
        }
    }

    // ───────────────────────────── helicopter lines ─────────────────────────────

    /// <summary>The helicopter line pool: several lines, random, never twice running; clip-less lines fall back to the window line.</summary>
    static void HeliLineChecks()
    {
        const string fallback = VoiceLines.PoliceHeliFallback;
        var pool = VoiceLines.PoliceHeliPool;
        string err = null;
        bool hasFallback = false;
        foreach (var k in pool)
        {
            if (k == fallback) hasFallback = true;
            var text = VoiceLines.Text(k);
            if (text == null) { err ??= $"{k} has no VoiceLines text"; continue; }
            int words = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 9) err ??= $"{k} is {words} words (≤ 9 for the 2.5 s cap): '{text}'";
        }
        if (err == null && pool.Count < 2) err = $"only {pool.Count} line(s) in the pool";
        if (err == null && !hasFallback) err = $"{fallback} missing from the pool";
        var seen = new HashSet<string>();
        string last = null;
        for (int i = 0; i < 300 && err == null; i++)
        {
            var k = VoiceLines.PoliceHeli(null);
            if (k == last) err = $"{k} twice running (pick {i})";
            last = k; seen.Add(k);
        }
        if (err == null && seen.Count < pool.Count) err = $"300 picks only ever chose {seen.Count} of {pool.Count} lines";
        Check("bad-call heli lines", err, $"{pool.Count} lines, 300 picks: {seen.Count} distinct, never twice running");

        // No audio for a line → never picked; no audio for any → the window line. The live library: every pick plays.
        err = null;
        if (VoiceLines.PoliceHeli(_ => false) != fallback) err = "nothing has audio but the pick isn't the window line";
        for (int i = 0; i < 20 && err == null; i++)
            if (VoiceLines.PoliceHeli(k => k == fallback) != fallback) err = "picked a line without audio while only the window line has some";
        int withAudio = 0;
        foreach (var k in pool) if (Sfx.Has(k)) withAudio++;
        if (err == null && !Sfx.Has(fallback)) err = $"{fallback} has no clips in the library";
        for (int i = 0; i < 50 && err == null; i++)
        {
            var k = VoiceLines.PoliceHeli(Sfx.Has);
            if (!Sfx.Has(k)) err = $"picked {k}, which has no clips";
        }
        Check("bad-call heli fallback", err, $"{withAudio} of {pool.Count} lines have audio now; clip-less lines are skipped, none → {fallback}");
    }

    // ───────────────────────────── "your other left" ─────────────────────────────

    /// <summary>
    /// Brett 0.1.17: "your other left is also funny the first time annoying on repeat". The pick rule as a string of
    /// mugshots (<see cref="BadCallShow.PickTurnLine"/> then <see cref="BadCallShow.TurnLineSpoken"/>, as the show does):
    /// at most once in N consecutive mugshots of a session with fair rolls, exactly once (the first) when every roll hits;
    /// never on two mugshots running even across restarts; ≤ 1 in 6 overall; never without its audio. And the audio: the
    /// plain line is the short cut, the rare one the full take.
    /// </summary>
    static void OtherLeftRuleChecks()
    {
        const int N = 40;
        string err = null;
        var rng = new System.Random(20260924);
        string Mug(float roll, bool audio = true) { var k = BadCallShow.PickTurnLine(roll, audio); BadCallShow.TurnLineSpoken(k); return k; }
        static bool Other(string k) => k == BadCallShow.OtherLeftLine;
        void NewSession(bool clearLast) { BadCallShow.NewOtherLeftSession(); if (clearLast) PlayerPrefs.SetInt(BadCallShow.OtherLeftPrefKey, 0); }

        NewSession(true);
        int fair = 0;
        for (int i = 0; i < N; i++) if (Other(Mug((float)rng.NextDouble()))) fair++;
        if (fair > 1) err = $"{fair}× in {N} consecutive mugshots of one session";

        NewSession(true);
        int hits = 0, firstAt = -1;
        for (int i = 0; i < N; i++) if (Other(Mug(0f))) { hits++; if (firstAt < 0) firstAt = i; }
        if (err == null && (hits != 1 || firstAt != 0)) err = $"every roll a hit: {hits}× (first at mugshot {firstAt}); expected once, on the first";

        // A restart before every mugshot, every roll a hit: it alternates at most.
        string last = null; int restarts = 0;
        for (int i = 0; i < 20 && err == null; i++)
        {
            NewSession(false);
            var k = Mug(0f);
            if (Other(k)) { restarts++; if (last != null && Other(last)) err = $"twice running across a restart (mugshot {i})"; }
            last = k;
        }

        // Fair rolls, a restart after every 2 mugshots: how often overall.
        int total = 0, others = 0; last = null;
        for (int s = 0; s < 1500 && err == null; s++)
        {
            NewSession(false);
            for (int i = 0; i < 2 && err == null; i++)
            {
                var k = Mug((float)rng.NextDouble()); total++;
                if (Other(k)) { others++; if (last != null && Other(last)) err = $"twice running (fair rolls, session {s})"; }
                last = k;
            }
        }
        float rate = total > 0 ? (float)others / total : 0f;
        if (err == null && (others == 0 || rate > BadCallShow.OtherLeftOdds + 0.01f)) err = $"{others}/{total} = {rate:P1} of mugshots (> 0, ≤ 1 in 6)";

        NewSession(true);
        if (err == null && Other(BadCallShow.PickTurnLine(0f, false))) err = "picked without audio";

        // The clips: plain = "Now turn to your left." (the cut), rare = the full take, both in the library.
        string plainText = VoiceLines.Text(BadCallShow.TurnLine), otherText = VoiceLines.Text(BadCallShow.OtherLeftLine);
        float plainLen = Sfx.Length(BadCallShow.TurnLine), otherLen = Sfx.Length(BadCallShow.OtherLeftLine);
        if (err == null && (plainText == null || plainText.Contains("other"))) err = $"plain turn line text '{plainText}'";
        else if (err == null && (otherText == null || !otherText.Contains("other left"))) err = $"rare turn line text '{otherText}'";
        else if (err == null && (plainLen <= 0f || otherLen <= 0f)) err = $"no clips (plain {plainLen:F2} s, other left {otherLen:F2} s) — HIS > Audio > Build Sfx Library";
        else if (err == null && plainLen > otherLen - 0.8f) err = $"the plain take ({plainLen:F2} s) isn't the short cut of the full one ({otherLen:F2} s)";
        Check("bad-call other left (rule)", err,
              $"{N} mugshots in a session: {fair}× (fair rolls), {hits}× first (every roll a hit); 20 restarts, all hits: {restarts}×, never twice running; " +
              $"fair rolls {others}/{total} = {rate:P1}; clips {plainLen:F2} s / {otherLen:F2} s");
    }

    // ───────────────────────────── the "All" rotation ─────────────────────────────

    /// <summary>The rotation itself: each bit once, the helicopter in the first two, single picks force their bit, saved and wrapping.</summary>
    static void RotationRuleChecks()
    {
        string err = null;
        var rot = BadCallShow.Rotation;
        var bits = new HashSet<BadShowMode>(rot);
        if (rot.Length != 4 || bits.Count != 4 || bits.Contains(BadShowMode.All)) err = $"rotation {string.Join(", ", rot)}: each of the four bits once";
        else if (rot[0] != BadShowMode.Helicopter && rot[1] != BadShowMode.Helicopter) err = "the helicopter isn't in the first two";
        foreach (var m in rot) if (err == null && BadCallShow.Featured(m) != m) err = $"picking {m} features {BadCallShow.Featured(m)}";
        BadCallShow.RotationIndex = 2; BadCallShow.ForgetCachedPrefs();   // a restart: read back from PlayerPrefs
        if (err == null && (BadCallShow.RotationIndex != 2 || BadCallShow.Featured(BadShowMode.All) != rot[2])) err = $"after a restart the rotation is at {BadCallShow.RotationIndex} (2)";
        BadCallShow.RotationIndex = rot.Length + 1;
        if (err == null && BadCallShow.RotationIndex != 1) err = $"index {rot.Length + 1} → {BadCallShow.RotationIndex} (wraps to 1)";
        PlayerPrefs.DeleteKey(RotationPref); BadCallShow.ForgetCachedPrefs(); // a fresh install
        if (err == null && BadCallShow.Featured(BadShowMode.All) != rot[0]) err = $"fresh install starts on {BadCallShow.Featured(BadShowMode.All)}";
        Check("bad-call rotation (rule)", err, $"All → {string.Join(" → ", rot)} → …, saved in PlayerPrefs (survives a restart), single picks force their bit");
    }

    /// <summary>
    /// Live, "All" as Brett has it, from a fresh install: five previews in a row feature every bit once and then wrap, the
    /// helicopter within the first two, each moving the rotation on by one. The first plays in full (its bit checked like
    /// its own pick); the rest are skipped 1.5 s in.
    /// </summary>
    static async Task RotationPreviews(bool? wall)
    {
        PlayerPrefs.DeleteKey(RotationPref); BadCallShow.ForgetCachedPrefs();
        var featured = new List<BadShowMode>();
        await PreviewAndWatch(BadShowMode.All, -1f, wall, " (rotation 1)", featured);
        for (int i = 2; i <= 5; i++) await PreviewAndWatch(BadShowMode.All, 1.5f, null, $" (rotation {i})", featured);
        string err = null;
        var firstFour = featured.Count >= 4 ? new HashSet<BadShowMode>(featured.GetRange(0, 4)) : new HashSet<BadShowMode>();
        if (featured.Count != 5) err = $"{featured.Count} of 5 previews started";
        else if (firstFour.Count != 4 || firstFour.Contains(BadShowMode.All)) err = $"the first four featured {string.Join(", ", featured.GetRange(0, 4))}";
        else if (featured[0] != BadShowMode.Helicopter && featured[1] != BadShowMode.Helicopter) err = "no helicopter in the first two";
        else if (featured[4] != featured[0]) err = $"the fifth featured {featured[4]}, not the wrap to {featured[0]}";
        else if (BadCallShow.RotationIndex != 5 % BadCallShow.Rotation.Length) err = $"rotation at {BadCallShow.RotationIndex} after 5 previews";
        Check("bad-call rotation (All, live)", err, $"5 previews from a fresh install: {string.Join(" → ", featured)}");
    }

    // ───────────────────────────── mugshot placement ─────────────────────────────

    /// <summary>
    /// <see cref="MugshotProps.Plan"/> from many heads: no scan (floats along the gaze, level/down/up) and 1.8 m in front
    /// of every visible wall looking at it (level and 15° down): always inside the gaze cone, on a wall at least once.
    /// Returns a head pose that puts the board on a wall (for the live previews), or null.
    /// </summary>
    static Pose? MugshotPlacementChecks()
    {
        string err = null;
        int floated = 0;
        var eye0 = new Vector3(0.3f, 1.6f, -0.2f);
        foreach (float pitch in new[] { 0f, -20f, -35f, 15f })
        foreach (float yaw in new[] { 0f, 90f, 200f })
        {
            var gaze = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
            var p = MugshotProps.Plan(eye0, gaze, null, 0f, 2.4f);
            float off = Vector3.Angle(gaze, p.Center - eye0);
            if (p.OnWall) err ??= "on a wall without a scan";
            else if (off > MugshotProps.PlaceCone) err ??= $"pitch {pitch}, yaw {yaw}: {off:F0}° off the gaze";
            else if (p.Distance < 0.49f || p.Distance > MugshotProps.FloatDistance + 0.01f) err ??= $"pitch {pitch}: floats {p.Distance:F2} m out";
            else if (p.Center.y - 0.68f * p.Scale < -0.01f || p.Center.y + 0.68f * p.Scale > 2.41f) err ??= $"pitch {pitch}: through the floor/ceiling (centre {p.Center.y:F2}, scale {p.Scale:F2})";
            else floated++;
        }
        Check("mugshot: no wall ahead", err, $"{floated}/12 gazes (level, 20° and 35° down, 15° up): floats ≤ {MugshotProps.FloatDistance} m along the gaze, inside the {MugshotProps.PlaceCone}° cone");

        err = null;
        var rs = RoomScanner.Instance; var g = rs != null ? rs.Geometry : null;
        if (rs == null || rs.Room == null || g == null) { ExtraFail("mugshot: wall ahead", "no scanned room"); return null; }
        int tried = 0, onWall = 0, inCone = 0;
        Pose? wallPose = null;
        float bestOff = float.MaxValue;
        foreach (var w in g.Walls)
        {
            if (w.Invisible) continue;
            var mid = Vector2.Lerp(w.A, w.B, 0.5f);
            var along = (w.B - w.A).normalized; var perp = new Vector2(-along.y, along.x);
            var n = g.Inside(mid + perp * 0.5f) ? perp : -perp;        // into the room
            var spot = mid + n * 1.8f;
            if (!g.Inside(spot) || g.Clearance(spot) < 0.35f) continue;
            var eye = new Vector3(spot.x, g.FloorY + 1.6f, spot.y);
            foreach (float pitch in new[] { 0f, -15f })
            {
                var rot = Quaternion.LookRotation(new Vector3(-n.x, 0f, -n.y)) * Quaternion.Euler(-pitch, 0f, 0f);
                var gaze = rot * Vector3.forward;
                var p = MugshotProps.Plan(eye, gaze, rs.Room, g.FloorY, g.FloorY + 2.4f);
                float off = Vector3.Angle(gaze, p.Center - eye);
                tried++;
                if (off <= MugshotProps.PlaceCone) inCone++;
                else err ??= $"{(p.OnWall ? "on a wall" : "floating")} {off:F0}° off the gaze (wall {w.A}→{w.B}, pitch {pitch})";
                if (!p.OnWall) continue;
                onWall++;
                if (p.Distance > MugshotProps.MaxWallDistance + 0.01f) err ??= $"on a wall {p.Distance:F2} m away (≤ {MugshotProps.MaxWallDistance})";
                if (pitch == 0f && off < bestOff) { bestOff = off; wallPose = new Pose(eye, rot); }
            }
        }
        if (err == null && tried == 0) err = "no spot 1.8 m in front of a wall to test from";
        else if (err == null && onWall == 0) err = $"never on a wall from {tried} heads facing one";
        Check("mugshot: wall ahead", err, $"{tried} heads 1.8 m from a wall: {onWall} on the wall, {tried - onWall} floating, {inCone}/{tried} inside the {MugshotProps.PlaceCone}° cone");
        return wallPose;
    }

    /// <summary>
    /// Live, Brett 0.1.17 ("he asks you to turn left and then it goes left"): the board goes up once where the (test) head
    /// looks and stays put. The head turns 90° to its left at once (before the board has even been seen) and stays turned:
    /// through the turn line and the profile flash the board must not move or turn, the profile silhouette goes up on it,
    /// both shutters click at the same camera spot, and the board ends up off to his right.
    /// </summary>
    static async Task MugshotStaysPutCheck()
    {
        const string what = "mugshot stays put (turn to your left)";
        var show = OutcomeShow.Instance; var dir = ScenarioDirector.Instance; var pause = GamePause.Instance;
        var bad = show.BadCall; var mug = bad.Mugshot;
        var start = OutcomeShow.TestHead ?? new Pose(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
        string err = null, info = "";
        try
        {
            OutcomeShow.TestHead = start;
            BadCallShow.Mode = BadShowMode.Mugshot;
            int done = dir.PreviewsDone;
            pause.AddReason("menu");
            await Wait(0.3f);
            pause.PreviewBadCall();
            if (!await WaitFor(() => mug.Visible, 10f)) err = "the board never went up";
            Vector3 c0 = mug.Center; Quaternion r0 = mug.Facing;
            float off0 = err == null ? Vector3.Angle(start.forward, c0 - start.position) : -1f;
            if (err == null && off0 > MugshotProps.PlaceCone) err = $"went up {off0:F0}° off the gaze";
            bool wall0 = mug.OnWall;
            var left = new Pose(start.position, start.rotation * Quaternion.Euler(0f, -90f, 0f));
            OutcomeShow.TestHead = left;
            float maxMove = 0f, maxTurn = 0f, t0 = Time.realtimeSinceStartup;
            Vector3 shutter0 = default; bool shot0 = false;
            void Sample()
            {
                maxMove = Mathf.Max(maxMove, Vector3.Distance(mug.Center, c0));
                maxTurn = Mathf.Max(maxTurn, Quaternion.Angle(mug.Facing, r0));
                if (!shot0 && bad.FlashesFired >= 1) { shot0 = true; shutter0 = bad.LastShutter; }
            }
            while (err == null && bad.FlashesFired < 2 && dir.PreviewsDone == done && Time.realtimeSinceStartup - t0 < 14f)
            {
                if (!mug.Visible) { err = "the board went away before the profile shot"; break; }
                Sample();
                await Task.Delay(40);
            }
            if (err == null && bad.FlashesFired < 2) err = "no profile shot";
            if (err == null) { await Wait(0.3f); Sample(); }
            float shutterGap = shot0 ? Vector3.Distance(bad.LastShutter, shutter0) : -1f;
            float toRight = Vector3.SignedAngle(new Vector3(left.forward.x, 0f, left.forward.z), new Vector3(c0.x - left.position.x, 0f, c0.z - left.position.z), Vector3.up);
            if (err == null && (maxMove > 0.001f || maxTurn > 0.1f)) err = $"the board moved {maxMove * 100f:F1} cm / turned {maxTurn:F1}° after he turned left";
            else if (err == null && mug.Placements != 1) err = $"placed {mug.Placements}× in one show";
            else if (err == null && !mug.Profile) err = "no profile silhouette";
            else if (err == null && (!shot0 || shutterGap > 0.001f)) err = $"the profile shutter clicked {shutterGap:F2} m from the front one";
            else if (err == null && toRight < 45f) err = $"after turning left the board is at {toRight:F0}° (expected off to his right)";
            info = $"up {off0:F0}° off the gaze ({(wall0 ? "on the wall" : "floating")}); head turned 90° left at once: board moved {maxMove * 1000f:F1} mm, " +
                   $"turned {maxTurn:F2}°, placed {mug.Placements}×, now {toRight:F0}° to his right, profile {mug.Profile}, shutters {shutterGap * 1000f:F1} mm apart";
            dir.RequestSkip();
            if (!await WaitFor(() => dir.PreviewsDone > done, 5f)) err ??= "preview didn't end on skip";
            await Wait(0.4f);
            if (err == null && mug.Visible) err = "board still up after the skip";
        }
        finally
        {
            OutcomeShow.TestHead = start;
            if (pause.IsPaused) pause.RemoveReason("menu");
        }
        Check(what, err, info);
    }

    // ───────────────────────────── one preview, start to finish ─────────────────────────────

    /// <summary>
    /// Pause → Preview bad call in <paramref name="mode"/>; <paramref name="skipAt"/> ≥ 0 = press skip that far in;
    /// <paramref name="expectWall"/> = where the mugshot board must end up (null = either); <paramref name="featured"/>
    /// collects the bit each preview featured. The bit must be the pick (All: the rotation's next, which moves on by one;
    /// a single pick leaves the rotation alone).
    /// </summary>
    static async Task PreviewAndWatch(BadShowMode mode, float skipAt, bool? expectWall = null, string tag = "", List<BadShowMode> featured = null)
    {
        string what = (skipAt >= 0f ? $"bad-call skip ({mode} at {skipAt:F1} s)" : $"bad-call show: {mode}") + tag;
        var show = OutcomeShow.Instance; var dir = ScenarioDirector.Instance; var pause = GamePause.Instance;
        var bad = show.BadCall;
        int exceptions = 0; string firstException = null;
        void OnLog(string msg, string stack, LogType type)
        {
            if (type != LogType.Exception && !(type == LogType.Error && msg.Contains("[HIS]"))) return;
            exceptions++; firstException ??= msg;
        }
        Application.logMessageReceived += OnLog;
        string err = null, info = "";
        try
        {
            BadCallShow.Mode = mode;
            int done = dir.PreviewsDone;
            int rot0 = BadCallShow.RotationIndex;
            var expectBit = BadCallShow.Featured(mode);
            pause.AddReason("menu");                 // Menu: paused, the settings panel is up
            await Wait(0.3f);
            if (!pause.IsPaused) err = "Menu didn't pause";
            pause.PreviewBadCall();                  // the "Preview bad call" row
            if (err == null && pause.IsPaused) err = "still paused after Preview";
            if (err == null && (!dir.Previewing || !show.Playing)) err = $"preview not playing (previewing {dir.Previewing}, show {show.Playing})";
            var bit = bad.Playing;
            featured?.Add(bit);
            int wantRot = mode == BadShowMode.All ? (rot0 + 1) % BadCallShow.Rotation.Length : rot0;
            if (err == null && bit != expectBit) err = $"featured {bit}, expected {expectBit}";
            if (err == null && BadCallShow.RotationIndex != wantRot) err = $"rotation {rot0} → {BadCallShow.RotationIndex}, expected {wantRot}";
            float length = show.Length, linger = show.Linger, t0 = Time.realtimeSinceStartup, bannerAt = -1f, firstFlashAt = -1f, lastFlashAt = -1f, skipSentAt = -1f;
            bool spot = false, mug = false, flashed = false, mugSeen = false, covered = false;
            int lightsFlashes0 = show.Lights != null ? show.Lights.Flashes : 0;
            VerdictBanner banner = null;
            while (err == null && dir.PreviewsDone == done && Time.realtimeSinceStartup - t0 < 35f)
            {
                if (bad.Spotlight.Visible) spot = true;
                if (bad.Mugshot.Visible) mug = true;
                if (bad.Mugshot.Seen) mugSeen = true;
                if (!flashed && bad.FlashesFired > 0) { flashed = true; firstFlashAt = show.Elapsed; }
                if (lastFlashAt < 0f && bad.FlashesFired > 1) lastFlashAt = show.Elapsed;
                if (bannerAt < 0f && show.BannerUp)
                {
                    bannerAt = show.Elapsed;
                    banner ??= Object.FindAnyObjectByType<VerdictBanner>();
                    OutcomeShow.Head(out var eye, out _);
                    if (banner != null && bad.Mugshot.Covers(eye, banner.transform.position)) covered = true;
                }
                if (skipAt >= 0f && skipSentAt < 0f && show.Elapsed >= skipAt) { dir.RequestSkip(); skipSentAt = Time.realtimeSinceStartup; }
                await Task.Delay(40);
            }
            float took = Time.realtimeSinceStartup - t0, skipLag = skipSentAt >= 0f ? t0 + took - skipSentAt : -1f;
            if (err == null && dir.PreviewsDone == done) err = $"never ended ({took:F0} s)";
            await Wait(0.6f); // the stop fades (0.25 s) run out
            int loops = Sfx.CountPlaying("outcome.") + Sfx.CountPlaying("vo.police");
            string left = bad.DebugLeftovers();
            float hold = lastFlashAt >= 0f && bannerAt >= 0f ? bannerAt - lastFlashAt : -1f;
            info = $"{(mode == BadShowMode.All ? $"featured {bit}, " : "")}{length:F1} s show (+{linger:F1} s banner), ended after {took:F1} s, {bad.LinesSpoken} line(s), {bad.SirensJoined} extra siren(s), " +
                   $"spot {(spot ? $"yes ({bad.Spotlight.SurfaceHits} surface frames)" : "no")}, mugshot {(mug ? (bad.Mugshot.OnWall ? "on the wall" : "floating") + (mugSeen ? ", seen" : ", not seen") : "no")}, " +
                   $"{bad.FlashesFired} flash(es), banner at {bannerAt:F1} s{(hold >= 0f ? $" after a {hold:F1} s hold" : "")}";
            if (err == null && exceptions > 0) err = $"{exceptions} exception/error(s): {firstException}";
            if (err == null && left != null) err = "left over after the show: " + left;
            if (err == null && loops > 0) err = $"{loops} outcome/police sound(s) still playing: {Sfx.PlayingKeys("outcome.")} {Sfx.PlayingKeys("vo.police")}";
            if (err == null && (show.Playing || show.BannerUp)) err = "show/banner still up";
            if (err == null && show.Lights != null && !show.Lights.Idle) err = "passthrough tint/flash not restored";
            if (err == null && dir.LastPreviewSkipped != skipAt >= 0f) err = $"skipped {dir.LastPreviewSkipped}, expected {skipAt >= 0f}";
            if (err == null && skipAt >= 0f && (skipLag < 0f || skipLag > 0.5f)) err = $"skip took {skipLag:F2} s";
            if (err == null && covered) err = "the mugshot board hung in front of the banner";
            if (err == null && skipAt < 0f)
            {
                if (length > 12.5f) err = $"show is {length:F1} s (≤ 12.5)";
                else if (took > length + linger + 1.5f) err = $"ran {took:F1} s for a {length:F1} s show (+{linger:F1} s banner)";
                else if (bannerAt < 0f) err = "banner never shown";
                else if (length + linger - bannerAt < OutcomeShow.MinBannerSeconds - 0.05f) err = $"banner only up {length + linger - bannerAt:F1} s";
                else switch (bit)
                {
                    case BadShowMode.Bullhorn:
                        if (bad.LinesSpoken < 5) err = $"only {bad.LinesSpoken} bullhorn lines";
                        else if (spot || mug || bad.SirensJoined > 0) err = "other bits played";
                        break;
                    case BadShowMode.Sirens:
                        if (bad.SirensJoined != 6) err = $"{bad.SirensJoined} sirens joined (6)";
                        else if (spot || mug) err = "other bits played";
                        break;
                    case BadShowMode.Helicopter:
                        if (!spot || bad.Spotlight.SurfaceHits == 0) err = "searchlight never on a scanned surface";
                        else if (bad.LinesSpoken < 3) err = $"only {bad.LinesSpoken} line(s)";
                        else if (mug || bad.SirensJoined > 0) err = "other bits played";
                        break;
                    case BadShowMode.Mugshot:
                        if (!mug || bad.FlashesFired != 2) err = $"mugshot {mug}, {bad.FlashesFired} flash(es)";
                        else if (show.Lights != null && show.Lights.Flashes - lightsFlashes0 != 2) err = "flashes didn't reach the passthrough";
                        else if (bannerAt < firstFlashAt) err = "banner before the mugshot";
                        else if (hold < BadCallShow.MugshotHold - 0.3f) err = $"mugshot held only {hold:F1} s before the banner";
                        else if (!mugSeen) err = "the board never counted as seen";
                        else if (!bad.Mugshot.Profile) err = "no profile shot";
                        else if (bad.Mugshot.Placements != 1) err = $"the board was placed {bad.Mugshot.Placements}× (once, then it stays)";
                        else if (expectWall.HasValue && bad.Mugshot.OnWall != expectWall.Value) err = expectWall.Value ? "facing a wall 1.8 m away, but the board floated" : "looking at the floor, but the board went on a wall";
                        else if (bad.Mugshot.PlacardText == null || !bad.Mugshot.PlacardText.Contains("VOLUNTARY MANSLAUGHTER")) err = "placard without the charge";
                        else if (spot || bad.SirensJoined > 0) err = "other bits played";
                        break;
                    default:
                        err = $"featured {bit}";
                        break;
                }
            }
        }
        finally
        {
            Application.logMessageReceived -= OnLog;
            if (pause.IsPaused) pause.RemoveReason("menu");
        }
        Check(what, err, info);
    }
}
