using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The door's hinge side (Brett 0.1.56: "the door start opening the wrong way again"; his real front door hinges on the left
/// seen from inside — the corner side — and opens into the room). His 0.1.55 re-scan put the frame 9 cm from the corner and a
/// 1.65 m DOOR_FRAME in the corner wall: the old Room-layer probe started 10 cm past the jamb, behind the corner wall, the old
/// leaf sweep looked through that frame's wall hole ("walls 5.0/5.0"), so the leaf hung from the far side; the boot re-send then
/// kept the rig and never looked again. On Brett's Editor room (BrettRoom.json), re-sent through MRUK.LoadSceneFromJsonString
/// (the device path, as RunRoomUpdateHeadless):
///   the scanned room hangs the front door at its corner · the re-scan room (frame 8.5 cm from the corner under a new anchor id,
///   the 1.65 m frame 0.25 m along the corner wall, its hole cut) fools the old probe, and the new rig hangs at the corner with
///   a swing stop ≤ 90° and the depth mask reaching the corner · the device re-sends it without the wide frame with the rig
///   hung wrong (0.1.56): the rig is kept and re-picks the corner · the pause menu's "Door hinge: flip" (listed after "Doors:
///   pick again") flips it at once, is saved for this room, survives a re-check and a fresh rig, waits for an open leaf to shut,
///   and another room's saved side is ignored (auto again) · the saved side survives a new launch (every anchor moved by one
///   rigid motion: it is kept in the front door anchor's own space, 0.1.57 review) · a side door whose jamb is beside the saved
///   spot never takes it · a shut door marks no doorway stencil (no black box in the leaf's gaps), open / kicked in it does, and
///   the depth mask reaches the corner only while the leaf is shut. PlayerPrefs (the room's door choice and hinge) are put back
///   afterwards. Room JSON ids: MRUK's saved JSON is matched in either byte order, else by label and position.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunDoorHingeHeadless
/// </summary>
public static partial class ScenarioTests
{
    const float HingeTestGap = 0.085f;                        // the re-scan's frame-to-corner gap (0.1.55: 9.4 cm, 0.1.56: 8.7 cm)
    const float WideFrameW = 1.65f, WideFrameH = 2.20f, WideFrameFromCorner = 0.25f;
    const string OtherRoomHingeKey = "HIS.door.test-another-room.hinge";

    public static void RunDoorHingeHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 600f;
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] SCENARIO watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += async s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            _running = true; _pass = _fail = 0;
            var prefs = default(HingeTestPrefs);
            try
            {
                HIS.Core.PlayerState.GodMode = true;
                ScenarioDirector.WaitForForced = true;   // no rounds: only the room loads and the door
                PoliceResponse.Enabled = false;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun())
                {
                    prefs = HingeTestPrefs.Take(RoomScanner.Instance.Room);
                    await DoorHingeChecks();
                }
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally
            {
                prefs.Restore();
                _running = false;
                Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed");
            }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task DoorHingeChecks()
    {
        var rs = RoomScanner.Instance;
        var mruk = MRUK.Instance;
        var pause = HIS.Core.GamePause.Instance;
        if (mruk == null || rs.Room == null || pause == null) { ExtraFail("door hinge: setup", $"MRUK {mruk}, room {rs.Room}, pause {pause}"); return; }
        string key = VirtualDoor.HingePrefKey(rs.Room);
        string Save() => mruk.SaveSceneToJsonString(true, new List<MRUKRoom> { rs.Room }); // just the game's room (as RoomUpdate 0b)

        // 0. Brett's room as the Editor has it (his older scan: the frame 16.5 cm from the corner): the automatic pick (a side saved
        //    by an earlier run was cleared) hangs it at the corner, as on every Scenarios night before the re-scan.
        var door = VirtualDoor.Instance;
        if (!await Recheck(door, "test: the automatic pick") || !DoorCorner(door, out var corner, out var toCorner, out float gap))
        { ExtraFail("door hinge: the front door in the room outline", $"rig {door}, checks {door?.HingeChecks}"); return; }
        CornerCheck("door hinge: scanned room (frame 16.5 cm from the corner) hangs at the corner", door, corner, gap, 100f);

        // 1. The 0.1.55 re-scan: the frame slid along its wall to 8.5 cm from the corner under a new anchor id (a fresh rig, as a
        //    re-scan builds) and a 1.65 × 2.20 m DOOR_FRAME in the corner wall 0.25 m from the corner (its hole cut in the walls).
        if (!RescanJson(Save(), rs, gap, corner, toCorner, out string rescan, out var wideId, out var wideAt, out string how))
        { ExtraFail("door hinge: the re-scan room", how); return; }
        var before = door;
        var load = await mruk.LoadSceneFromJsonString(rescan);
        bool built = await WaitFor(() => VirtualDoor.Instance != null && VirtualDoor.Instance != before && rs.Doors.Count == 2, 10);
        door = VirtualDoor.Instance;
        if (!built || door == null || !await Settled(door, 0) || !DoorCorner(door, out corner, out toCorner, out gap))
        { ExtraFail("door hinge: the re-scan room loads", $"load {load}, new rig {built}, {rs.Doors.Count} door(s), checks {door?.HingeChecks} ({how})"); return; }
        bool cornerAtMin = CornerAtMin(door, corner);

        // The test room is the bug's: the wide frame's hole is in the corner wall, and the old probe misses the corner.
        bool hole = !WallHit(wideAt + Vec3(-toCorner) * 0.4f, Vec3(toCorner), 0.6f);
        var along = (RoomGeometry.Flat(wideAt) - corner).normalized;                         // (along the corner wall, from the corner)
        var solidAt = new Vector3(corner.x + along.x * 0.1f, wideAt.y, corner.y + along.y * 0.1f); // 10 cm from the corner: solid wall
        bool solid = WallHit(solidAt + Vec3(-toCorner) * 0.4f, Vec3(toCorner), 0.6f);
        OldHingeProbe(door, out float oldWallMin, out float oldWallMax, out float oldClearMin, out float oldClearMax);
        bool oldAtMin = oldClearMin >= oldClearMax;
        if (Mathf.Abs(oldClearMin - oldClearMax) < 15f && Mathf.Min(oldClearMin, oldClearMax) >= 80f && Mathf.Abs(oldWallMin - oldWallMax) > 0.2f) oldAtMin = oldWallMin < oldWallMax;
        float oldCornerWall = cornerAtMin ? oldWallMin : oldWallMax, oldCornerClear = cornerAtMin ? oldClearMin : oldClearMax;
        string repro = $"gap {gap * 100f:F1} cm, wide frame's hole {(hole ? "cut" : "MISSING")}, wall beside it {(solid ? "solid" : "MISSING")}; " +
                       $"old probe: walls {oldWallMin:F1}/{oldWallMax:F1}, clearance {oldClearMin:F0}°/{oldClearMax:F0}° → {(oldAtMin == cornerAtMin ? "the corner" : "the far side")} " +
                       $"(corner side: wall {oldCornerWall:F1} m, clear {oldCornerClear:F0}°)";
        if (hole && solid && oldCornerWall >= 4.99f && oldAtMin != cornerAtMin) ExtraPass("door hinge: the re-scan room fools the old probe (0.1.55 repro)", repro);
        else ExtraFail("door hinge: the re-scan room fools the old probe (0.1.55 repro)", repro);

        CornerCheck("door hinge: re-scan (8.5 cm from the corner, a 1.65 m frame's hole in the corner wall) hangs at the corner", door, corner, gap, 90f);
        var margins = door.DepthMaskMargins;
        float cornerMargin = cornerAtMin ? margins.x : margins.y, farMargin = cornerAtMin ? margins.y : margins.x;
        string mask = $"depth mask {cornerMargin * 100f:F1} cm past the corner-side jamb (gap {gap * 100f:F1} cm), {farMargin * 100f:F1} cm past the other; rig moved {door.WallShift * 100f:F1} cm";
        if (cornerMargin >= gap + 0.015f && Mathf.Abs(farMargin - 0.07f) < 0.001f) ExtraPass("door hinge: the depth mask reaches the corner", mask);
        else ExtraFail("door hinge: the depth mask reaches the corner", mask);

        // 2. 0.1.56: the hinge was picked, then the device re-sent the room without the wide frame (deleted in Space Setup) and the
        //    kept rig was never measured again. Hang it from the far side first (that state), then re-send: kept, re-picked.
        var rig = door;
        rig.SetHingeSide(!cornerAtMin);
        bool wrongFirst = !HingedAtCorner(rig, corner);
        int checks = rig.HingeChecks;
        load = await mruk.LoadSceneFromJsonString(WithoutEntity(Save(), wideId));
        bool resent = await WaitFor(() => rs.Doors.Count == 1, 5) && await Settled(rig, checks);
        DoorCorner(rig, out corner, out toCorner, out gap);
        string kept = $"load {load}, {rs.Doors.Count} door(s), rig kept {VirtualDoor.Instance == rig}, hung wrong first {wrongFirst}, checks {checks} → {rig.HingeChecks}, " +
                      $"{(HingedAtCorner(rig, corner) ? "at the corner" : "far side")}, stop {rig.MaxOpenAngle:F0}°";
        if (resent && wrongFirst && VirtualDoor.Instance == rig && HingedAtCorner(rig, corner) && rig.MaxOpenAngle <= 90.01f)
            ExtraPass("door hinge: a re-sent room keeps the rig and re-picks the hinge", kept);
        else ExtraFail("door hinge: a re-sent room keeps the rig and re-picks the hinge", kept);

        // 3. The pause menu's "Door hinge: flip", after "Doors: pick again" (Brett: yes, remembered for his room).
        string menu = pause.DebugMenuText();
        int doorsAt = menu.IndexOf("Doors: pick again", StringComparison.Ordinal), hingeAt = menu.IndexOf("Door hinge: flip", StringComparison.Ordinal);
        bool listed = doorsAt >= 0 && hingeAt > doorsAt && menu.LastIndexOf("Credits", StringComparison.Ordinal) > hingeAt;
        pause.FlipDoorHinge();
        bool flipped = !HingedAtCorner(rig, corner) && !rig.HingeChangePending;
        bool savedKey = PlayerPrefs.HasKey(key) && rig.HingeSaved && pause.DebugMenuText().Contains("your side, saved");
        string flip = $"listed {listed}, flipped at once {flipped}, saved {savedKey} ({key} = {PlayerPrefs.GetString(key, "-")})";
        if (listed && flipped && savedKey) ExtraPass("door hinge: the flip row flips the shut door at once and saves it", flip);
        else ExtraFail("door hinge: the flip row flips the shut door at once and saves it", flip);

        //    Saved: it beats the automatic pick on a re-check, and on a fresh rig on the same frame (a new anchor id).
        bool recheck = await Recheck(rig, "test: a re-check after the flip") && !HingedAtCorner(rig, corner) && rig.HingeSaved;
        before = rig;
        load = await mruk.LoadSceneFromJsonString(Renamed(Save(), rs.PrimaryDoor, Guid.NewGuid()));
        built = await WaitFor(() => VirtualDoor.Instance != null && VirtualDoor.Instance != before, 10);
        door = VirtualDoor.Instance;
        bool fresh = built && await Settled(door, 0) && DoorCorner(door, out corner, out toCorner, out gap) && !HingedAtCorner(door, corner) && door.HingeSaved;
        string persist = $"re-check: {(recheck ? "kept the flip" : "lost it")}; fresh rig (load {load}): {(fresh ? "flipped, saved" : $"built {built}, at the corner {door != null && HingedAtCorner(door, corner)}, saved {door?.HingeSaved}")}";
        if (recheck && fresh) ExtraPass("door hinge: the saved side beats the automatic pick (re-check, fresh rig)", persist);
        else ExtraFail("door hinge: the saved side beats the automatic pick (re-check, fresh rig)", persist);
        if (door == null) return;

        //    Flipped while open: it waits for the leaf to shut, then hangs from the other jamb (the corner: saved now).
        door.OpenGently(0.3f, 60f);
        await WaitFor(() => door.State == VirtualDoor.DoorState.Open, 3);
        pause.FlipDoorHinge();
        bool waits = door.HingeChangePending && !HingedAtCorner(door, corner) && door.IsOpen;
        door.Close(0.2f);
        bool shut = await WaitFor(() => door.State == VirtualDoor.DoorState.Closed && !door.HingeChangePending, 3);
        await Wait(0.1f);
        string open = $"waited while open {waits}, shut {shut}, then {(HingedAtCorner(door, corner) ? "at the corner" : "still the far side")}, saved {door.HingeSaved}";
        if (waits && shut && HingedAtCorner(door, corner) && door.HingeSaved) ExtraPass("door hinge: flipped while open, it changes once shut", open);
        else ExtraFail("door hinge: flipped while open, it changes once shut", open);

        //    Per room: the far side saved, then filed under another room's key: this room is automatic again (the corner); back
        //    under this room's key: the saved side wins again.
        pause.FlipDoorHinge();
        string savedSide = PlayerPrefs.GetString(key, "");
        PlayerPrefs.SetString(OtherRoomHingeKey, savedSide); PlayerPrefs.DeleteKey(key);
        bool autoAgain = await Recheck(door, "test: the saved side is another room's") && HingedAtCorner(door, corner) && !door.HingeSaved;
        PlayerPrefs.SetString(key, savedSide); PlayerPrefs.DeleteKey(OtherRoomHingeKey);
        bool overrides = await Recheck(door, "test: this room's saved side again") && !HingedAtCorner(door, corner) && door.HingeSaved;
        string perRoom = $"far side saved {savedSide.Length > 0}; another room's: {(autoAgain ? "auto, at the corner" : "not auto")}; this room's: {(overrides ? "far side, saved" : "ignored")}";
        if (savedSide.Length > 0 && autoAgain && overrides) ExtraPass("door hinge: the saved side is per room", perRoom);
        else ExtraFail("door hinge: the saved side is per room", perRoom);

        //    A new launch: the same scan under another world origin — every anchor elsewhere in world space by one rigid motion
        //    (turned 25° about the saved jamb, then 1 m along the floor), the room's own transform still the identity (its entity
        //    has no pose). The saved side (the far one) holds: 0.1.57's was kept in "room space", i.e. world space, and lost on
        //    every launch. (The anchors are moved in place: MRUK's JSON load leaves an anchor it already has where it was.)
        var savedJamb = door.HingeWorld;
        bool savedAtMin = door.HingeAtMin;
        var poses = MoveAnchors(rs.Room, 25f, savedJamb, new Vector3(0.6f, 0f, 0.8f));
        bool roomIdentity = rs.Room.transform.position == Vector3.zero && rs.Room.transform.rotation == Quaternion.identity;
        float drift = Mathf.Min(Vector3.Distance(savedJamb, Jamb(door, true)), Vector3.Distance(savedJamb, Jamb(door, false)));
        bool relaunched = await Recheck(door, "test: a new launch (every anchor moved)");
        bool sameSide = door.HingeAtMin == savedAtMin, launchSaved = door.HingeSaved;
        string launch = $"{poses.Count} anchors moved, the jambs {drift:F2} m from the old world spot, room transform {(roomIdentity ? "the identity" : "MOVED")}; " +
                        $"re-checked {relaunched}: {(sameSide ? "the saved side" : "the OTHER side")}, {(launchSaved ? "saved" : "auto")}";
        if (relaunched && drift > 0.5f && sameSide && launchSaved) ExtraPass("door hinge: the saved side survives a new launch (every anchor moved)", launch);
        else ExtraFail("door hinge: the saved side survives a new launch (every anchor moved)", launch);
        PutBack(poses);
        await Recheck(door, "test: the anchors back where they were");

        //    A side door beside the saved spot (0.1.57 review): the front door's corner side saved, then a 0.9 m frame in the corner
        //    wall 0.25 m from the corner taken as the side door — its near jamb inside the 0.35 m a saved spot reaches. Only the
        //    front door reads the saved side: the side door is picked automatically, and the front door keeps its own.
        if (!HingedAtCorner(door, corner)) pause.FlipDoorHinge();
        var cornerJamb = door.HingeWorld;
        bool cornerSaved = HingedAtCorner(door, corner) && door.HingeSaved;
        var withSide = JObject.Parse(Save());
        if (!AddFrame((JArray)withSide["spatialEntities"], rs, corner, toCorner, 0.9f, out _, out _, out string sideHow))
        { ExtraFail("door hinge: a side door beside the saved spot never takes it", sideHow); return; }
        int sideChecks = door.HingeChecks;
        load = await mruk.LoadSceneFromJsonString(withSide.ToString(Newtonsoft.Json.Formatting.None));
        bool twoDoors = await WaitFor(() => rs.Doors.Count == 2, 10) && await Settled(door, sideChecks);
        await Wait(0.5f); // (the door picker's own step 2 answers itself first)
        var sideAnchor = twoDoors ? rs.Doors.Find(a => a != null && a != rs.PrimaryDoor) : null;
        rs.SetSecondDoor(sideAnchor);
        VirtualDoor side = null;
        bool sideBuilt = sideAnchor != null && await WaitFor(() => (side = VirtualDoor.All.Find(r => r != null && r.Index == 1)) != null, 5) && await Settled(side, 0);
        door = VirtualDoor.Instance;
        float reach = side != null ? Mathf.Min(Vector3.Distance(cornerJamb, Jamb(side, true)), Vector3.Distance(cornerJamb, Jamb(side, false))) : 9f;
        bool frontKept = door != null && HingedAtCorner(door, corner) && door.HingeSaved;
        string sideInfo = $"load {load}, corner side saved {cornerSaved}, {rs.Doors.Count} door(s) ({sideHow}); side rig " +
                          (side != null ? $"built, its jamb {reach:F2} m from the saved spot, {(side.HingeSaved ? "TOOK the saved side" : "auto")}" : "MISSING") +
                          $"; front door {(frontKept ? "keeps its saved side" : "LOST it")}";
        if (cornerSaved && sideBuilt && reach < 0.35f && !side.HingeSaved && frontKept) ExtraPass("door hinge: a side door beside the saved spot never takes it", sideInfo);
        else ExtraFail("door hinge: a side door beside the saved spot never takes it", sideInfo);
        rs.SetSecondDoor(null);
        await Wait(0.2f);
        if (door == null) return;

        PlayerPrefs.DeleteKey(key);
        await Recheck(door, "test: back to the automatic pick");

        // 4. A shut door marks no doorway stencil, so the black box can't draw in the leaf's 3–8 mm gaps (Brett: "the edges
        //    flicker black"); open, kicked in or easing, it does (the box and the porch through the doorway, 0.1.17).
        var portal = PortalRenderer(door);
        if (portal == null) { ExtraFail("door hinge: the doorway stencil only while the leaf is off its seat", "no PortalMask quad"); return; }
        bool shutOff = door.State == VirtualDoor.DoorState.Closed && !portal.enabled;
        var maskShut = door.DepthMaskMargins;
        door.OpenGently(0.3f, 50f);
        bool easing = await WaitFor(() => door.OpenAngle > 5f, 2) && portal.enabled;
        await WaitFor(() => door.State == VirtualDoor.DoorState.Open, 3);
        bool openOn = portal.enabled;
        var maskOpen = door.DepthMaskMargins;
        door.Close(0.2f);
        await WaitFor(() => door.State == VirtualDoor.DoorState.Closed, 3);
        await Wait(0.1f);
        bool shutAgain = !portal.enabled;
        var maskShutAgain = door.DepthMaskMargins;
        //    The depth mask past the corner-side jamb (0.1.57 review): to just past the corner while shut (Brett's clean edges);
        //    open, the casing margin only, so a man beside the doorway stays behind the real wall.
        bool widenedShut = Mathf.Max(maskShut.x, maskShut.y) >= gap + 0.015f && Vector2.Distance(maskShut, maskShutAgain) < 0.001f;
        bool casingOpen = Mathf.Abs(maskOpen.x - 0.07f) < 0.001f && Mathf.Abs(maskOpen.y - 0.07f) < 0.001f;
        string maskInfo = $"gap {gap * 100f:F1} cm; shut {maskShut.x * 100f:F1}/{maskShut.y * 100f:F1} cm, open {maskOpen.x * 100f:F1}/{maskOpen.y * 100f:F1} cm, " +
                          $"shut again {maskShutAgain.x * 100f:F1}/{maskShutAgain.y * 100f:F1} cm past the jambs";
        if (widenedShut && casingOpen) ExtraPass("door hinge: the depth mask reaches the corner only while the leaf is shut", maskInfo);
        else ExtraFail("door hinge: the depth mask reaches the corner only while the leaf is shut", maskInfo);
        door.Breach();
        bool kicked = await WaitFor(() => door.IsOpen && door.OpenAngle > 30f, 3) && portal.enabled;
        door.ResetDoor();
        await Wait(0.1f);
        bool reset = !portal.enabled;
        string stencil = $"shut {Off(shutOff)}, easing open {On(easing)}, open {On(openOn)}, shut again {Off(shutAgain)}, kicked in {On(kicked)}, reset {Off(reset)}";
        if (shutOff && easing && openOn && shutAgain && kicked && reset) ExtraPass("door hinge: the doorway stencil only while the leaf is off its seat", stencil);
        else ExtraFail("door hinge: the doorway stencil only while the leaf is off its seat", stencil);
    }

    static string On(bool ok) => ok ? "marked" : "NOT marked";
    static string Off(bool ok) => ok ? "unmarked" : "MARKED";

    /// <summary>A fresh hinge check on <paramref name="d"/>, done.</summary>
    static async Task<bool> Recheck(VirtualDoor d, string why)
    {
        if (d == null) return false;
        int n = d.HingeChecks;
        d.RechooseHinge(why);
        return await Settled(d, n);
    }

    /// <summary>The rig has done a hinge check after its first <paramref name="after"/>, none is queued and no change waits.</summary>
    static async Task<bool> Settled(VirtualDoor d, int after, float timeout = 10f)
    {
        bool ok = await WaitFor(() => d != null && d.HingeChecks > after && !d.HingeCheckPending && !d.HingeChangePending, timeout);
        await Wait(0.2f); // (a frame or two: LateUpdate, a rebuild's re-check just queued)
        if (ok && d != null && d.HingeCheckPending) ok = await WaitFor(() => !d.HingeCheckPending, timeout);
        return ok;
    }

    static void CornerCheck(string what, VirtualDoor d, Vector2 corner, float gap, float maxStop)
    {
        bool at = HingedAtCorner(d, corner);
        string info = $"frame {gap * 100f:F1} cm from the corner, hinged at {(at ? "the corner" : "the FAR side")} ({(d.HingeAtMin ? "min" : "max")}), " +
                      $"stop {d.MaxOpenAngle:F0}°, {(d.HingeSaved ? "saved" : "auto")}";
        if (at && d.MaxOpenAngle <= maxStop + 0.01f) ExtraPass(what, info); else ExtraFail(what, info);
    }

    /// <summary>
    /// The door's wall in the room outline (RoomGeometry, independent of VirtualDoor's own search) and the end of it the door is
    /// nearer: its corner (flat world XZ), the way to it from the door, and the jamb-to-corner gap.
    /// </summary>
    static bool DoorCorner(VirtualDoor d, out Vector2 corner, out Vector2 toCorner, out float gap)
    {
        corner = toCorner = default; gap = 0f;
        var geo = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
        if (geo == null || d == null) return false;
        var c = RoomGeometry.Flat(d.Center);
        var along = RoomGeometry.Flat(d.HingeToLatch).normalized;
        float best = 0.15f; bool found = false;
        foreach (var s in geo.Walls)
        {
            var u = s.B - s.A; float len = u.magnitude;
            if (len < 0.1f) continue;
            u /= len;
            if (Mathf.Abs(Vector2.Dot(u, along)) < 0.985f) continue;
            float t = Vector2.Dot(c - s.A, u);
            if (t < 0f || t > len) continue;
            float off = Mathf.Abs(u.x * (c.y - s.A.y) - u.y * (c.x - s.A.x));
            if (off >= best) continue;
            best = off; found = true;
            bool nearA = t < len - t;
            corner = nearA ? s.A : s.B;
            toCorner = (corner - c).normalized;
            gap = Mathf.Min(t, len - t) - d.Opening.width * 0.5f;
        }
        return found;
    }

    /// <summary>The hinge jamb is the one nearer the corner.</summary>
    static bool HingedAtCorner(VirtualDoor d, Vector2 corner)
    {
        var hinge = RoomGeometry.Flat(d.HingeWorld);
        var latch = RoomGeometry.Flat(d.HingeWorld + d.HingeToLatch * d.Opening.width);
        return Vector2.Distance(hinge, corner) < Vector2.Distance(latch, corner);
    }

    /// <summary>The opening's min-x jamb (door space) is the one nearer the corner.</summary>
    static bool CornerAtMin(VirtualDoor d, Vector2 corner)
    {
        var r = d.Opening;
        var jMin = RoomGeometry.Flat(d.transform.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        var jMax = RoomGeometry.Flat(d.transform.TransformPoint(new Vector3(r.xMax, r.yMin, 0f)));
        return Vector2.Distance(jMin, corner) < Vector2.Distance(jMax, corner);
    }

    static Vector3 Vec3(Vector2 v) => new(v.x, 0f, v.y);

    /// <summary>A Room-layer wall collider (a WALL_FACE / INVISIBLE_WALL_FACE effect mesh) on the ray within <paramref name="length"/>.</summary>
    static bool WallHit(Vector3 from, Vector3 dir, float length)
    {
        int mask = LayerMask.GetMask("Room");
        foreach (var hit in Physics.RaycastAll(from, dir.normalized, length, mask, QueryTriggerInteraction.Ignore))
        {
            var a = hit.collider.GetComponentInParent<MRUKAnchor>();
            if (a != null && a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE)) return true;
        }
        return false;
    }

    /// <summary>
    /// 0.1.55's hinge check as it was (VirtualDoor before the fix): Room-layer rays along the wall from 10 cm past each jamb,
    /// 0.4 m into the room, hits under 5 cm ignored ("walls"), and the leaf swept in 10° steps by rays at three heights
    /// ("clearance") — wall colliders with a hole cut for every DOOR_FRAME.
    /// </summary>
    static void OldHingeProbe(VirtualDoor d, out float wallMin, out float wallMax, out float clearMin, out float clearMax)
    {
        int mask = LayerMask.GetMask("Room");
        var t = d.transform; var r = d.Opening;
        float R = Vector3.Dot(t.forward, d.Inward) >= 0f ? 1f : -1f;
        float Along(bool fromMin)
        {
            float x = fromMin ? r.xMin - 0.1f : r.xMax + 0.1f, best = 5f;
            var dir = t.TransformDirection(new Vector3(fromMin ? -1f : 1f, 0f, 0f)).normalized;
            foreach (float y in new[] { 0.5f, 1.2f })
                foreach (var hit in Physics.RaycastAll(t.TransformPoint(new Vector3(x, r.yMin + y, R * 0.4f)), dir, 5f, mask, QueryTriggerInteraction.Ignore))
                    if (hit.distance > 0.05f && hit.distance < best) best = hit.distance;
            return best;
        }
        float Clear(bool atMin)
        {
            float hx = atMin ? r.xMin : r.xMax, dir = atMin ? 1f : -1f;
            for (float a = 10f; a <= 100f; a += 10f)
            {
                var leaf = t.TransformDirection(Quaternion.Euler(0f, (atMin ? -R : R) * a, 0f) * new Vector3(dir, 0f, 0f)).normalized;
                foreach (float y in new[] { 0.4f, 1.0f, 1.7f })
                    if (Physics.Raycast(t.TransformPoint(new Vector3(hx, r.yMin + y, R * 0.03f)), leaf, r.width * 0.95f, mask, QueryTriggerInteraction.Ignore)) return a - 10f;
            }
            return 100f;
        }
        wallMin = Along(true); wallMax = Along(false); clearMin = Clear(true); clearMax = Clear(false);
    }

    static MeshRenderer PortalRenderer(VirtualDoor d)
    {
        foreach (var r in d.GetComponentsInChildren<MeshRenderer>(true)) if (r.name == "PortalMask") return r;
        return null;
    }

    // ───────────────────────────── the room JSON (MRUK's own save: OpenXR axes, Unity (x, y, z) = (x, y, −z)) ─────────────────────────────

    /// <summary>
    /// The game room's JSON as Brett's 0.1.55 re-scan made it: the front door slid along its wall until the corner-side jamb is
    /// <see cref="HingeTestGap"/> from the corner, under a new anchor id (a fresh rig), and a <see cref="WideFrameW"/> ×
    /// <see cref="WideFrameH"/> m DOOR_FRAME in the corner wall <see cref="WideFrameFromCorner"/> from the corner, oriented as
    /// that wall (MRUK makes it the wall's child, so the wall meshes get its hole). <paramref name="wideAt"/>: its centre
    /// (Unity, 1 m up).
    /// </summary>
    static bool RescanJson(string json, RoomScanner rs, float gap, Vector2 corner, Vector2 toCorner, out string result, out Guid wideId, out Vector3 wideAt, out string info)
    {
        result = null; wideId = Guid.Empty; wideAt = default;
        var root = JObject.Parse(json);
        var ents = (JArray)root["spatialEntities"];
        var front = rs.PrimaryDoor;
        var frontE = Entity(ents, front);
        if (frontE == null) { info = $"front door not in the saved JSON ({IdsInfo(ents, front)})"; return false; }
        // Slide the front door toward the corner (the anchor's plane rect is unchanged: same opening).
        float slide = gap - HingeTestGap;
        var pos = (JObject)frontE["locatableMETA"]["position"];
        pos["x"] = (float)pos["x"] + toCorner.x * slide;
        pos["z"] = (float)pos["z"] - toCorner.y * slide;    // (Unity z is −z there)
        Renamed(ents, (string)frontE["uuid"], Guid.NewGuid());
        if (!AddFrame(ents, rs, corner, toCorner, WideFrameW, out wideId, out wideAt, out string how)) { info = how; return false; }
        result = root.ToString(Newtonsoft.Json.Formatting.None);
        info = $"front door slid {slide * 100f:F1} cm to the corner, {how}";
        return true;
    }

    /// <summary>
    /// A DOOR_FRAME up to <paramref name="wantW"/> × <see cref="WideFrameH"/> m in the corner wall, <see cref="WideFrameFromCorner"/>
    /// from the corner, oriented as that wall (MRUK makes it the wall's child, so the wall meshes get its hole) and listed in the
    /// room. <paramref name="at"/>: its centre (Unity, 1 m up).
    /// </summary>
    static bool AddFrame(JArray ents, RoomScanner rs, Vector2 corner, Vector2 toCorner, float wantW, out Guid frameId, out Vector3 at, out string info)
    {
        frameId = Guid.NewGuid(); at = default;
        // The corner wall: a wall anchor whose segment ends within 20 cm of the corner, across the door's wall.
        MRUKAnchor cornerWall = null; Vector2 near = default, away = default; float wallLen = 0f;
        foreach (var a in rs.Room.WallAnchors)
        {
            if (a == null || !a.PlaneRect.HasValue) continue;
            var r = a.PlaneRect.Value;
            var p0 = RoomGeometry.Flat(a.transform.TransformPoint(new Vector3(r.xMin, r.center.y, 0f)));
            var p1 = RoomGeometry.Flat(a.transform.TransformPoint(new Vector3(r.xMax, r.center.y, 0f)));
            if (Mathf.Abs(Vector2.Dot((p1 - p0).normalized, toCorner)) > 0.34f) continue; // (not across the door's wall: within 70° of it)
            bool p0Near = (p0 - corner).sqrMagnitude <= (p1 - corner).sqrMagnitude;
            var n = p0Near ? p0 : p1;
            if ((n - corner).magnitude > 0.2f) continue;
            cornerWall = a; near = n; away = ((p0Near ? p1 : p0) - n).normalized; wallLen = (p1 - p0).magnitude;
            break;
        }
        var wallE = cornerWall != null ? Entity(ents, cornerWall) : null;
        if (wallE == null) { info = $"no corner wall at {corner} ({rs.Room.WallAnchors.Count} walls{(cornerWall != null ? $"; {IdsInfo(ents, cornerWall)}" : "")})"; return false; }
        float w = Mathf.Min(wantW, wallLen - WideFrameFromCorner - 0.1f), h = WideFrameH;
        if (w < 0.9f) { info = $"the corner wall is {wallLen:F2} m: no room for a {wantW:F2} m frame"; return false; }
        float sillY = VirtualDoor.Instance != null ? VirtualDoor.Instance.Threshold.y : rs.Geometry != null ? rs.Geometry.FloorY : 0f; // (as the scan sits its doors: a few mm above the wall's foot)
        var c = near + away * (WideFrameFromCorner + w * 0.5f);
        at = new Vector3(c.x, sillY + 1f, c.y);
        var verts = new[] { -w * 0.5f, -h * 0.5f, w * 0.5f, -h * 0.5f, w * 0.5f, h * 0.5f, -w * 0.5f, h * 0.5f };
        var bytes = new byte[verts.Length * 4];
        Buffer.BlockCopy(verts, 0, bytes, 0, bytes.Length);
        var wide = new JObject
        {
            ["uuid"] = frameId.ToString("D"),
            ["semanticLabelMETA"] = "DOOR_FRAME",
            ["locatableMETA"] = new JObject
            {
                ["orientation"] = wallE["locatableMETA"]["orientation"].DeepClone(),
                ["position"] = new JObject { ["x"] = c.x, ["y"] = sillY + h * 0.5f, ["z"] = -c.y },
            },
            ["boundary2DMETA"] = new JObject { ["vertices"] = Convert.ToBase64String(bytes) },
            ["bounded2DMETA"] = new JObject
            {
                ["offset"] = new JObject { ["x"] = -w * 0.5f, ["y"] = -h * 0.5f },
                ["extent"] = new JObject { ["width"] = w, ["height"] = h },
            },
        };
        ents.Add(wide);
        var roomE = RoomEntity(ents, rs.Room);
        if (roomE?["spaceContainerMETA"]?["uuids"] is not JArray ids) { info = "no room container in the JSON"; return false; }
        ids.Add(frameId.ToString("D"));
        info = $"a {w:F2} m frame on the {wallLen:F2} m corner wall";
        return true;
    }

    static JObject Entity(JArray ents, Guid id)
    {
        foreach (var e in ents) if (e is JObject o && SameId(o["uuid"], id)) return o;
        return null;
    }

    /// <summary>
    /// <paramref name="a"/>'s entity in MRUK's saved JSON: by its UUID (in either byte order, <see cref="SameId"/>), else the
    /// entity with its label at its position (OpenXR axes: Unity (x, y, z) = (x, y, −z)) to 2 cm.
    /// </summary>
    static JObject Entity(JArray ents, MRUKAnchor a)
    {
        if (a == null) return null;
        var byId = Entity(ents, a.Anchor.Uuid);
        if (byId != null) return byId;
        var p = a.transform.position; string label = a.Label.ToString();
        JObject best = null; float bestD = 0.02f;
        foreach (var e in ents)
        {
            if (e is not JObject o || o["locatableMETA"]?["position"] is not JObject q) continue;
            if (((string)o["semanticLabelMETA"] ?? "").IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0) continue;
            float d = Vector3.Distance(new Vector3((float)q["x"], (float)q["y"], -(float)q["z"]), p);
            if (d < bestD) { best = o; bestD = d; }
        }
        return best;
    }

    /// <summary>The room's entity (its anchor UUID, else the JSON's only room container).</summary>
    static JObject RoomEntity(JArray ents, MRUKRoom room)
    {
        var byId = Entity(ents, room.Anchor.Uuid);
        if (byId != null) return byId;
        JObject only = null; int n = 0;
        foreach (var e in ents) if (e is JObject o && o["spaceContainerMETA"] != null) { only = o; n++; }
        return n == 1 ? only : null;
    }

    /// <summary>What a failed lookup saw: the anchor's id (both byte orders) and place, and the JSON's door ids.</summary>
    static string IdsInfo(JArray ents, MRUKAnchor a)
    {
        var doors = new List<string>();
        foreach (var e in ents) if (e is JObject o && ((string)o["semanticLabelMETA"] ?? "").Contains("DOOR_FRAME")) doors.Add((string)o["uuid"]);
        return $"{a.Label} {a.Anchor.Uuid:D} (other byte order {Swapped(a.Anchor.Uuid):D}) at {a.transform.position:F2}; the JSON's door frames: {string.Join(", ", doors)}";
    }

    /// <summary>
    /// The JSON's uuid is <paramref name="id"/>, in either byte order: MRUK's native save writes an anchor's 16 bytes as they are,
    /// while .NET reads and prints a Guid's first three groups little-endian, so the same anchor can show as either string
    /// (0.1.57: "front door not in the saved JSON").
    /// </summary>
    static bool SameId(JToken t, Guid id) => t != null && t.Type == JTokenType.String && Guid.TryParse((string)t, out var g) && (g == id || g == Swapped(id));

    static Guid Swapped(Guid id)
    {
        var b = id.ToByteArray();
        Array.Reverse(b, 0, 4); Array.Reverse(b, 4, 2); Array.Reverse(b, 6, 2);
        return new Guid(b);
    }

    /// <summary>The entity whose uuid string is <paramref name="from"/> under a new id <paramref name="to"/> (its entity and every room's list of it).</summary>
    static void Renamed(JArray ents, string from, Guid to)
    {
        string id = to.ToString("D");
        bool Is(JToken k) => k != null && k.Type == JTokenType.String && string.Equals((string)k, from, StringComparison.OrdinalIgnoreCase);
        foreach (var e in ents)
        {
            if (Is(e["uuid"])) e["uuid"] = id;
            if (e["spaceContainerMETA"]?["uuids"] is JArray ids)
                for (int i = 0; i < ids.Count; i++) if (Is(ids[i])) ids[i] = id;
        }
    }

    /// <summary>The JSON with <paramref name="from"/>'s entity under a new id <paramref name="to"/> (a fresh anchor on the same frame).</summary>
    static string Renamed(string json, MRUKAnchor from, Guid to)
    {
        var root = JObject.Parse(json);
        var ents = (JArray)root["spatialEntities"];
        var e = Entity(ents, from);
        if (e != null) Renamed(ents, (string)e["uuid"], to);
        else Debug.LogWarning($"[HIS] door hinge: {IdsInfo(ents, from)} not in the saved JSON: not renamed");
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>
    /// Every anchor of <paramref name="room"/> moved by one rigid motion, as a new launch has them: the same scan under another
    /// world origin. Turned <paramref name="yaw"/>° about the vertical through <paramref name="pivot"/>, then shifted by
    /// <paramref name="shift"/>; each anchor's own world pose, parents first (a nested anchor isn't moved twice); the room's
    /// transform untouched (the identity, as on device). Returns the poses to put back (<see cref="PutBack"/>).
    /// </summary>
    static List<(Transform t, Vector3 p, Quaternion q)> MoveAnchors(MRUKRoom room, float yaw, Vector3 pivot, Vector3 shift)
    {
        var r = Quaternion.Euler(0f, yaw, 0f);
        var before = new List<(Transform t, Vector3 p, Quaternion q)>();
        foreach (var a in room.Anchors) if (a != null) before.Add((a.transform, a.transform.position, a.transform.rotation));
        before.Sort((x, y) => Depth(x.t).CompareTo(Depth(y.t)));
        foreach (var (t, p, q) in before) t.SetPositionAndRotation(r * (p - pivot) + pivot + shift, r * q);
        Physics.SyncTransforms();
        return before;
    }

    static void PutBack(List<(Transform t, Vector3 p, Quaternion q)> poses)
    {
        foreach (var (t, p, q) in poses) if (t != null) t.SetPositionAndRotation(p, q);
        Physics.SyncTransforms();
    }

    static int Depth(Transform t) { int n = 0; for (var x = t.parent; x != null; x = x.parent) n++; return n; }

    /// <summary>The opening's min- or max-x jamb at the sill (world, on the rig's plane).</summary>
    static Vector3 Jamb(VirtualDoor d, bool atMin)
    {
        var r = d.Opening;
        return d.transform.TransformPoint(new Vector3(atMin ? r.xMin : r.xMax, r.yMin, 0f));
    }

    /// <summary>The JSON without the anchor <paramref name="id"/> (the 0.1.56 re-send: the wide frame deleted in Space Setup).</summary>
    static string WithoutEntity(string json, Guid id)
    {
        var root = JObject.Parse(json);
        var ents = (JArray)root["spatialEntities"];
        JToken victim = null;
        foreach (var e in ents)
        {
            if (SameId(e["uuid"], id)) victim = e;
            if (e["spaceContainerMETA"]?["uuids"] is JArray ids)
                for (int i = ids.Count - 1; i >= 0; i--) if (SameId(ids[i], id)) ids.RemoveAt(i);
        }
        victim?.Remove();
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>The Editor room's saved door choices and hinge side (PlayerPrefs outlive Play Mode): taken at the start, put back after.</summary>
    struct HingeTestPrefs
    {
        string[] _keys, _values;

        public static HingeTestPrefs Take(MRUKRoom room)
        {
            var p = new HingeTestPrefs();
            if (room == null) return p;
            string door = DoorPicker.PrefKey(room);
            p._keys = new[] { door, door + ".second", VirtualDoor.HingePrefKey(room), OtherRoomHingeKey };
            p._values = Array.ConvertAll(p._keys, k => PlayerPrefs.HasKey(k) ? PlayerPrefs.GetString(k) : null);
            PlayerPrefs.DeleteKey(VirtualDoor.HingePrefKey(room)); // the run starts from the automatic pick
            return p;
        }

        public void Restore()
        {
            if (_keys == null) return;
            for (int i = 0; i < _keys.Length; i++)
                if (_values[i] != null) PlayerPrefs.SetString(_keys[i], _values[i]); else PlayerPrefs.DeleteKey(_keys[i]);
            PlayerPrefs.Save();
        }
    }
}
