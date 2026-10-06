using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.UI;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Safety + credits checks run at the start of the scenario suite (same Play Mode session, Brett's room):
///   credits  — the shipped TextAsset matches CREDITS.md, every heading/bullet and link is in the pages, paging works;
///   door     — auto-confirmed and saved per room, spawns blocked while unconfirmed, re-pick re-saves, candidate ranking;
///   ring     — the spot is ≥2 m from the door and 0.6 m clear, a fake room's couch/wardrobe/small-room rules, ring shows;
///   guard    — a fake head 0.2 m from a wall warns ("Wall behind you") and fades, 1 m clears, the ring spot is quiet,
///              backing up fast warns; every HIS/OcclusionLitPBR material keeps _Fade = 1.
/// </summary>
public static partial class ScenarioTests
{
    static async Task SafetyChecks()
    {
        CreditsCheck();
        await DoorConfirmCheck();
        await DoorLookChecks(); // key light + nothing z-fighting on the leaf (ScenarioTests.DoorLook.cs)
        SafeSpotCheck();
        await ProximityCheck();
    }

    static void Check(string what, string err, string info)
    {
        if (err == null) ExtraPass(what, info); else ExtraFail(what, err);
    }

    // ───────────────────────────── (1) credits ─────────────────────────────

    static void CreditsCheck()
    {
        const string what = "credits";
        var cm = CreditsMenu.Instance;
        if (cm == null) { ExtraFail(what, "no CreditsMenu in the scene"); return; }
        string md = File.Exists(CreditsSync.SourcePath) ? File.ReadAllText(CreditsSync.SourcePath) : null;
        if (md == null) { ExtraFail(what, "CREDITS.md not found"); return; }
        string err = null;
        if (cm.SourceText.Replace("\r", "") != md.Replace("\r", "")) err = "shipped credits differ from CREDITS.md (run HISAutomation.RebuildAll / CreditsSync)";

        // Every heading and bullet from the first "## " on is an entry; every link is on a page.
        var lines = md.Replace("\r", "").Split('\n');
        int start = System.Array.FindIndex(lines, l => l.StartsWith("## "));
        var missing = new List<string>();
        int items = 0;
        for (int i = Mathf.Max(0, start); i < lines.Length; i++)
        {
            var l = lines[i];
            if (!(l.StartsWith("## ") || l.TrimStart().StartsWith("- "))) continue;
            items++;
            var want = CreditsMenu.PlainOf(l);
            if (!cm.Entries.Contains(want)) missing.Add(want.Length > 60 ? want.Substring(0, 60) + "..." : want);
        }
        string pages = Regex.Replace(string.Join(" ", cm.Lines), "<[^>]+>", "");
        pages = Regex.Replace(pages, @"\s+", " ");
        var urls = Regex.Matches(string.Join("\n", lines.Skip(Mathf.Max(0, start))), @"https?://[^\s)]+").Cast<Match>().Select(m => m.Value).Distinct().ToList();
        var lostUrls = urls.Where(u => !pages.Contains(u)).ToList();
        string[] names = { "KaL-ABIZZARE", "WPthai", "Rotuma", "Ka-Bar", "Remington 870", "tris.blend", "vitvitskyi", "SpatialNeglect", "Mixamo", "Meta",
                           "Free Firearm Sound Library", "Freesound", "creativecommons.org/licenses/by/4.0", "CC0" };
        var lostNames = names.Where(n => !pages.Contains(n)).ToList();
        int modified = Regex.Matches(pages, "Modified").Count;
        if (err == null && missing.Count > 0) err = $"{missing.Count} entr(ies) not in the credits: {string.Join(" | ", missing.Take(3))}";
        if (err == null && lostUrls.Count > 0) err = $"{lostUrls.Count} link(s) lost in wrapping: {string.Join(" ", lostUrls.Take(3))}";
        if (err == null && lostNames.Count > 0) err = "missing: " + string.Join(", ", lostNames);
        if (err == null && modified < 5) err = $"only {modified} 'Modified' notes (the five Sketchfab models each have one)";
        if (err == null && cm.Lines.Any(x => Regex.Replace(x, "<[^>]+>", "").Length > 70 && !Regex.IsMatch(x, "https?://")))
            err = "a wrapped line is wider than the page";

        // The pause menu lists it; the page opens over everything, scrolls to the end with a constant height, closes.
        if (err == null && (HIS.Core.GamePause.Instance == null || !HIS.Core.GamePause.Instance.DebugMenuText().Contains("Credits")))
            err = "no Credits row in the pause menu";
        if (err == null)
        {
            cm.Open();
            int h0 = cm.Page().Count(ch => ch == '\n');
            bool top = Notice.ShownKey == CreditsMenu.NoticeKey;
            cm.Scroll(10000);
            int h1 = cm.Page().Count(ch => ch == '\n');
            bool scrolled = cm.MaxOffset > 0 && cm.Offset == cm.MaxOffset && cm.Page().Contains(StripTags(cm.Lines[cm.Lines.Count - 1]).Trim().Split(' ').Last());
            cm.Close();
            bool closed = Notice.ShownKey != CreditsMenu.NoticeKey && !cm.IsOpen;
            if (!top) err = $"credits page not on top (shown: {Notice.ShownKey})";
            else if (h0 != h1) err = $"page height changes while scrolling ({h0} vs {h1} lines)";
            else if (!scrolled) err = $"scrolling to the end failed (offset {cm.Offset}/{cm.MaxOffset})";
            else if (!closed) err = "credits didn't close";
        }
        Check(what, err, $"{items} entries, {cm.Lines.Count} lines, {urls.Count} links, {modified} 'Modified' notes, pause row ok");
    }

    static string StripTags(string s) => Regex.Replace(s, "<[^>]+>", "");

    // ───────────────────────────── (2a) door confirm ─────────────────────────────

    static async Task DoorConfirmCheck()
    {
        const string what = "door confirm";
        var rs = RoomScanner.Instance;
        if (rs == null || !await WaitFor(() => rs.IsReady && rs.PrimaryDoor != null, 60)) { ExtraFail(what, "no room / no door"); return; }
        string err = null;
        // Menu-run tests (not batch mode) with no saved door: answer the open picker the way batch mode does.
        DoorPicker.AutoConfirm = true;
        if (!rs.DoorConfirmed && DoorPicker.Instance != null && DoorPicker.Instance.IsPicking) DoorPicker.Instance.Begin();
        if (!await WaitFor(() => rs.DoorConfirmed, 10)) err = "not auto-confirmed in batch mode";
        string key = DoorPicker.PrefKey(rs.Room);
        if (err == null && (!PlayerPrefs.HasKey(key) || DoorPicker.FindSaved(rs) != rs.PrimaryDoor)) err = $"choice not saved for this room ({key})";

        // Nothing spawns while the door is unconfirmed (the picker is open).
        var spawner = Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (err == null && spawner != null && !await WaitFor(() => spawner.NavMeshReady, 60)) err = "NavMesh never ready";
        if (err == null && spawner != null)
        {
            rs.UnconfirmDoor();
            var early = spawner.SpawnScenario(ScenarioDirector.AllScenarios.First());
            if (early != null) { Object.Destroy(early.gameObject); err = "an intruder spawned before the door was confirmed"; }
            if (DoorPicker.Instance == null) err ??= "no DoorPicker in the scene";
            else DoorPicker.Instance.Begin(); // batch mode: auto-confirms again
            if (err == null && !rs.DoorConfirmed) err = "re-open didn't auto-confirm";
        }
        // A forgotten choice is saved again on the next confirm.
        if (err == null)
        {
            PlayerPrefs.DeleteKey(key);
            DoorPicker.Instance.Begin();
            if (!PlayerPrefs.HasKey(key) || DoorPicker.FindSaved(rs) != rs.PrimaryDoor) err = "re-confirm didn't save the choice";
        }
        // Candidate order: doors ≥2 m away first (nearest first), then the close ones (farthest first).
        var order = RoomScanner.RankDoors(new List<Vector3> { new(1f, 1f, 0f), new(3f, 1f, 0f), new(0f, 1f, 5f), new(0.5f, 1f, 0.5f) }, new Vector3(0f, 1.6f, 0f));
        if (err == null && !order.SequenceEqual(new[] { 1, 2, 0, 3 })) err = "door ranking wrong: " + string.Join(",", order);
        // Only a hold-Menu restart in a 2+ door room asks again; a Sandbox death reset never does.
        if (err == null && (!DoorPicker.ReopensAfterReset(HIS.Core.GameSession.MenuHoldReason, 2) || DoorPicker.ReopensAfterReset(HIS.Core.GameSession.MenuHoldReason, 1) ||
                            DoorPicker.ReopensAfterReset("player died", 3) || DoorPicker.ReopensAfterReset(null, 3)))
            err = "door picker reopens on the wrong session resets";
        Check(what, err, $"{rs.Doors.Count} door(s), saved as {PlayerPrefs.GetString(key)}, spawns wait, ranking ok, reopens only on a hold-Menu restart");
    }

    // ───────────────────────────── (2b) safe-spot ring ─────────────────────────────

    static void SafeSpotCheck()
    {
        const string what = "safe spot";
        var rs = RoomScanner.Instance; var ss = SafeSpot.Instance;
        if (rs == null || ss == null || rs.Geometry == null) { ExtraFail(what, "no room / SafeSpot"); return; }
        string err = null, info = "";
        if (ss.Spot == null) err = "no spot found in Brett's room";
        else
        {
            var g = rs.Geometry; var door = rs.PrimaryDoor;
            var centre = rs.Room.FloorAnchors[0].GetAnchorCenter();
            RoomGeometry.DoorOnFloor(door.transform.position, door.transform.forward, centre, out var d, out var inward);
            var p = RoomGeometry.Flat(ss.Spot.Value);
            float dd = Vector2.Distance(p, d);
            // Clearance recomputed here the long way: every wall and every body-height box.
            float clr = float.PositiveInfinity;
            foreach (var w in g.Walls) clr = Mathf.Min(clr, Vector2.Distance(p, RoomGeometry.ClosestOnSegment(p, w.A, w.B)));
            foreach (var b in g.Boxes) if (g.AtBodyHeight(b)) clr = Mathf.Min(clr, Vector2.Distance(p, RoomGeometry.ClosestOnBox(p, b)));
            bool view = !g.Blocked(p, d + inward * 0.3f, 1.2f);
            info = $"Brett's room: {dd:F2} m from the door, {clr:F2} m clear, view {(view ? "clear" : "blocked")}, {g.Walls.Count} walls, {g.Boxes.Count} volumes";
            if (dd < SafeSpot.MinDoorDistance) err = $"spot only {dd:F2} m from the door";
            else if (clr < SafeSpot.MinClearance) err = $"spot only {clr:F2} m from a wall/furniture";
            else if (!g.Inside(p)) err = "spot outside the floor outline";
        }

        // A fake 6 x 5 m room, door in the middle of the west wall: a couch right in front of the door at 3 m, and a
        // wardrobe (2 m tall) that hides the door from the north-east corner.
        if (err == null)
        {
            var f = new RoomGeometry { FloorY = 0f };
            Vector3[] c = { new(0, 0, 0), new(6, 0, 0), new(6, 0, 5), new(0, 0, 5) };
            for (int i = 0; i < 4; i++) { f.AddWall(c[i], c[(i + 1) % 4]); f.Floor.Add(RoomGeometry.Flat(c[i])); }
            f.AddBox(new Vector3(3f, 0f, 2.5f), new Vector2(1f, 2f), 0f, 0.8f, "Couch");
            f.AddBox(new Vector3(1.5f, 0f, 3.8f), new Vector2(0.6f, 1.2f), 0f, 2f, "Wardrobe");
            var door = new Vector2(0f, 2.5f); var inward = Vector2.right;
            if (!SafeSpot.Find(f, door, inward, SafeSpot.MinDoorDistance, SafeSpot.MinClearance, out var s, out var why)) err = "fake room: no spot";
            else
            {
                float dd = Vector2.Distance(s, door), clr = f.Clearance(s);
                bool view = !f.Blocked(s, door + inward * 0.3f, 1.2f);
                info += $"; fake room: ({s.x:F1}, {s.y:F1}) {why}";
                if (dd < 2f || clr < 0.6f || !f.Inside(s) || !view) err = $"fake room spot breaks the rules: {why}";
            }
            // A 2.4 m square room has no spot 2 m from the door and 0.6 m clear: the strict rules must say so.
            var tiny = new RoomGeometry();
            Vector3[] t = { new(0, 0, 0), new(2.4f, 0, 0), new(2.4f, 0, 2.4f), new(0, 0, 2.4f) };
            for (int i = 0; i < 4; i++) { tiny.AddWall(t[i], t[(i + 1) % 4]); tiny.Floor.Add(RoomGeometry.Flat(t[i])); }
            if (err == null && SafeSpot.Find(tiny, new Vector2(0f, 1.2f), Vector2.right, SafeSpot.MinDoorDistance, SafeSpot.MinClearance, out var ts, out _))
                err = $"tiny room: strict rules accepted ({ts.x:F1}, {ts.y:F1})";
        }

        // The ring itself (off by default since Brett 0.1.15 — switched on for this check only): shown on the spot, hidden
        // again; with the switch off (the shipped default) a scenario start shows nothing.
        if (err == null)
        {
            bool was = SafeSpot.ShowRing;
            try
            {
                SafeSpot.ShowRing = true;
                ss.ShowForScenario();
                bool shown = ss.RingVisible && Notice.Posted("ring") != null;
                ss.Hide();
                SafeSpot.ShowRing = false;
                ss.ShowForScenario();
                bool quiet = !ss.RingVisible && Notice.Posted("ring") == null;
                ss.Hide();
                if (!shown) err = "ring / 'Stand in the ring' not shown";
                else if (ss.RingVisible) err = "ring didn't hide";
                else if (!quiet) err = "ring shown with SafeSpot.ShowRing off";
            }
            finally { SafeSpot.ShowRing = was; }
        }
        Check(what, err, info);
    }

    /// <summary>A scenario start follows the ring switch (checked on the first case): off by default (Brett 0.1.15: "remove the
    /// stand in circle"), so no ring and no "Stand in the ring"; switched on, the director's ShowForScenario puts it up.</summary>
    static void RingAtScenarioStart()
    {
        var ss = SafeSpot.Instance;
        if (ss == null || ss.Spot == null) { ExtraFail("ring at scenario start", "no spot"); return; }
        if (SafeSpot.ShowRing) { if (ss.RingVisible) ExtraPass("ring at scenario start", $"ring at {ss.Spot.Value:F2}"); else ExtraFail("ring at scenario start", "ring not shown when the scenario started"); }
        else if (ss.RingVisible || Notice.Posted("ring") != null) ExtraFail("ring at scenario start", "ring shown though SafeSpot.ShowRing is off");
        else ExtraPass("ring at scenario start", "ring off (SafeSpot.ShowRing default), none shown");
    }

    // ───────────────────────────── (2c) near-surface warning ─────────────────────────────

    static async Task ProximityCheck()
    {
        const string what = "near-surface warning";
        var rs = RoomScanner.Instance; var pg = ProximityGuard.Instance;
        if (rs == null || pg == null || rs.Geometry == null) { ExtraFail(what, "no room / ProximityGuard"); return; }
        var g = rs.Geometry;
        string err = null, info = "";

        // Every material on the fade shader starts opaque (a missing default would hide the intruder).
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/_Game" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m != null && m.shader != null && m.shader.name == "HIS/OcclusionLitPBR" && (!m.HasProperty("_Fade") || m.GetFloat("_Fade") < 0.999f))
            { err = $"{m.name}: _Fade {(m.HasProperty("_Fade") ? m.GetFloat("_Fade").ToString("F2") : "missing")}"; break; }
        }

        // A real (visible) wall with open floor 1 m in front of it.
        bool found = false; Vector2 a = default, n = default;
        foreach (var w in g.Walls)
        {
            if (w.Invisible || found) continue;
            var along = (w.B - w.A).normalized; var perp = new Vector2(-along.y, along.x);
            for (float t = 0.2f; t <= 0.8f && !found; t += 0.1f)
            {
                var pt = Vector2.Lerp(w.A, w.B, t);
                var nn = g.Inside(pt + perp * 0.5f) ? perp : -perp;
                var p02 = pt + nn * 0.2f; var p1 = pt + nn * 1f;
                if (!g.Inside(p1) || g.Clearance(p1) < 0.65f) continue;
                g.Nearest(new Vector3(p02.x, 0f, p02.y), out var h);
                if (!h.Wall || h.Name != "Wall" || h.Distance > 0.25f) continue;
                found = true; a = pt; n = nn;
            }
        }
        if (err == null && !found) err = "no wall with 1 m of open floor in front of it";
        if (err == null)
        {
            // Facing into the room, the wall 0.2 m behind the head.
            Pose At(Vector2 p) => new(new Vector3(p.x, g.FloorY + 1.6f, p.y), Quaternion.LookRotation(new Vector3(n.x, 0f, n.y)));
            bool was = ProximityGuard.Enabled;
            try
            {
                // Off by default since Brett 0.1.15 ("the table in front of you" warnings): on for this check only.
                ProximityGuard.Enabled = true;
                pg.ResetMotion(); ProximityGuard.TestHead = At(a + n * 0.2f);
                await Wait(0.6f);
                string near = pg.Warning; float fade = pg.Fade; bool posted = Notice.Posted("safety") != null;
                pg.ResetMotion(); ProximityGuard.TestHead = At(a + n * 1f);
                await Wait(1.6f);
                string far = pg.Warning;
                info = $"0.2 m: '{near}' (fade {fade:F2}), 1 m: {(far ?? "clear")}";
                if (near == null || !near.Contains("Wall")) err = $"no wall warning 0.2 m from the wall (got '{near}', nearest {pg.LastDistance:F2} m)";
                else if (!near.Contains("behind")) err = $"wall is behind the head, warning says '{near}'";
                else if (!posted) err = "warning not posted to the Notice panel";
                else if (fade > 0.95f) err = $"intruder fade not applied ({fade:F2})";
                else if (far != null) err = $"still warning 1 m from the wall: '{far}'";

                // Standing in the ring never warns.
                var ss = SafeSpot.Instance;
                if (err == null && ss != null && ss.Spot != null)
                {
                    var sp = RoomGeometry.Flat(ss.Spot.Value);
                    pg.ResetMotion(); ProximityGuard.TestHead = At(sp);
                    await Wait(1.2f);
                    if (pg.Warning != null) err = $"warning while standing in the ring: '{pg.Warning}'";
                    else
                    {
                        // Backing away from the door fast (1.8 m/s for 0.4 s) warns before anything is near.
                        var door = RoomGeometry.Flat(rs.PrimaryDoor.transform.position);
                        var back = (sp - door).normalized;
                        var face = Quaternion.LookRotation(new Vector3(-back.x, 0f, -back.y));
                        pg.ResetMotion();
                        float t0 = Time.realtimeSinceStartup;
                        string seen = null;
                        while (Time.realtimeSinceStartup - t0 < 0.45f && EditorApplication.isPlaying)
                        {
                            float k = (Time.realtimeSinceStartup - t0) * 1.8f;
                            var p = sp + back * k;
                            ProximityGuard.TestHead = new Pose(new Vector3(p.x, g.FloorY + 1.6f, p.y), face);
                            if (pg.Warning != null) seen ??= pg.Warning;
                            await Task.Delay(15);
                        }
                        info += $", ring quiet, backing {pg.BackingSpeed:F1} m/s → '{seen ?? pg.Warning}'";
                        if ((seen ?? pg.Warning) == null) err = "backing up at 1.8 m/s didn't warn";
                    }
                }
            }
            finally { ProximityGuard.TestHead = null; pg.ResetMotion(); ProximityGuard.Enabled = was; }
            await Wait(1.2f); // back to the real head; the warning (if any) clears on its own
            if (err == null && !ProximityGuard.Enabled)
            {
                // Switched off (the shipped default): a head 0.2 m from the wall stays quiet and nothing fades.
                try { pg.ResetMotion(); ProximityGuard.TestHead = At(a + n * 0.2f); await Wait(0.6f); if (pg.Warning != null || pg.Fade < 0.99f) err = $"warning with ProximityGuard.Enabled off: '{pg.Warning}' fade {pg.Fade:F2}"; }
                finally { ProximityGuard.TestHead = null; pg.ResetMotion(); }
                info += ", quiet when off";
            }
        }
        Check(what, err, info);
    }
}
