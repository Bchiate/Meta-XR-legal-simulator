using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Room;
using HIS.Scenario;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The 0.1.34 regression (HANDOFF-NEXT): the headset re-sent the room on resume (each wall's outline a few mm off, one STORAGE gone),
/// MRUK's EffectMesh rebuilt each changed wall through its per-anchor path without the door holes, and the invisible wall
/// occluder across the doorway stopped every round and hid the door and the man. The same room update is fed through
/// MRUK.LoadSceneFromJsonString (the device path: native discovery → anchor updated/removed events → scene loaded):
///   the loaded room's doorways are clear · with DoorHoles off, a fresh wall EffectMesh (the 0.1.34 setup) is rebuilt uncut
///   across the doorway (the repro), while the game's own occluders, already rebuilt by DoorHoles, stay cut · a real M9 round
///   through the shut door still reaches a man on the path (Firearm passes the uncut wall) · DoorHoles on: rebuilt, clear,
///   the ways in back on · a second update never leaves a wall across · the door rig is kept through the reloads · a real
///   M9 round through the open doorway hits.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunRoomUpdateHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunRoomUpdateHeadless()
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
            try
            {
                HIS.Core.PlayerState.GodMode = true;
                ScenarioDirector.WaitForForced = true;   // no rounds: only the room updates and the shots
                PoliceResponse.Enabled = false;
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await RoomUpdateChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally
            {
                DoorHoles.Enabled = true;
                _running = false;
                Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed");
            }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task RoomUpdateChecks()
    {
        var spawner = UnityEngine.Object.FindAnyObjectByType<IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("room update: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        var holes = DoorHoles.Instance;
        var mruk = MRUK.Instance;
        if (holes == null || mruk == null) { ExtraFail("room update: DoorHoles", $"holes {holes}, mruk {mruk}"); return; }
        var sanity = GameObject.Find("[HIS] SanityCube"); // (builds drop it; in the Editor its collider stops rounds)
        if (sanity != null) sanity.SetActive(false);
        Hitbox.LethalRounds = true;

        // 0a. Two rooms (BrettRoom.json, like Brett's scan): the game keeps its room, and only its room is built. MRUK builds the
        //     room the head is in on a scene load, and on the headset that can be the next room, whose wall stands behind the
        //     front door. Build the other room into every effect mesh by hand, then rebuild: it must be cleared. Then a
        //     two-room update: the game's room and its rig stay (MRUK's current room flips here in the Editor).
        var game = RoomScanner.Instance.Room;
        var other = mruk.Rooms.Find(r => r != null && r != game);
        if (game != null && other != null)
        {
            foreach (var em in UnityEngine.Object.FindObjectsByType<EffectMesh>()) em.CreateMesh(other);
            int before = OtherRoomMeshes(game);
            holes.Rebuild("test: the other room built");
            int after = OtherRoomMeshes(game);
            string info = $"{before} mesh(es) of the other room → {after}, across [{WallsAcross()}], links {LinksInfo()}";
            if (before > 0 && after == 0 && WallsAcross().Length == 0 && LinksOn()) ExtraPass("room update: only the game's room is built", info);
            else ExtraFail("room update: only the game's room is built", info);

            var rig0 = VirtualDoor.Instance; int rebuilt = holes.Rebuilds;
            var res2 = await mruk.LoadSceneFromJsonString(Nudged(mruk.SaveSceneToJsonString(true), -0.008f, out _, dropStorage: false));
            await WaitFor(() => holes.Rebuilds > rebuilt && LinksOn(), 4);
            info = $"load {res2}, MRUK current {(mruk.GetCurrentRoom() == game ? "the game's room" : "the other room")}, game room kept {RoomScanner.Instance.Room == game}, " +
                   $"rig kept {VirtualDoor.Instance == rig0}, other-room meshes {OtherRoomMeshes(game)}, across [{WallsAcross()}], links {LinksInfo()}";
            if (RoomScanner.Instance.Room == game && VirtualDoor.Instance == rig0 && OtherRoomMeshes(game) == 0 && WallsAcross().Length == 0 && LinksOn())
                ExtraPass("room update: two-room update keeps the game's room", info);
            else ExtraFail("room update: two-room update keeps the game's room", info);
        }
        else ExtraFail("room update: two rooms", $"game {game?.name}, other {other?.name} ({mruk.Rooms.Count} rooms)");

        // 0b. Just the game's room from here: the repro below builds a fresh EffectMesh for MRUK's current room, which flips
        //     between the two rooms on a reload in the Editor (no head inside either).
        var current = game != null ? game : mruk.GetCurrentRoom();
        var only = await mruk.LoadSceneFromJsonString(mruk.SaveSceneToJsonString(true, new List<MRUKRoom> { current }));
        if (!await WaitFor(() => mruk.Rooms.Count == 1 && mruk.GetCurrentRoom() == current && spawner.NavMeshReady && spawner.DoorConfirmed, 10))
        { ExtraFail("room update: one room", $"load {only}, {mruk.Rooms.Count} rooms, current {mruk.GetCurrentRoom()?.name}"); return; }

        // 1. The room as loaded: rebuilt, doorways clear, the ways in on.
        await WaitFor(() => holes.Rebuilds > 0 && LinksOn() && WallsAcross().Length == 0, 5);
        string across = WallsAcross();
        if (holes.Rebuilds > 0 && across.Length == 0 && LinksOn()) ExtraPass("room update: loaded room", $"{holes.Rebuilds} rebuild(s), {VirtualDoor.All.Count} doorway(s) clear, ways in on");
        else ExtraFail("room update: loaded room", $"rebuilds {holes.Rebuilds}, across [{across}], links {LinksInfo()}");
        var rig = VirtualDoor.Instance;

        // 2. The repro. DoorHoles off; a wall EffectMesh set up the way 0.1.34's WallOccluders was (colliders on the Room layer,
        //    holes cut at the door frames), freshly built so MRUK still tracks its walls. The device's update: each wall's outline a few mm
        //    off, a STORAGE gone.
        DoorHoles.Enabled = false;
        var testMesh = new GameObject("[HIS] TestOccluders (0.1.34 setup)").AddComponent<EffectMesh>();
        testMesh.Labels = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE;
        testMesh.CutHoles = MRUKAnchor.SceneLabels.DOOR_FRAME;
        testMesh.Colliders = true;
        testMesh.Layer = LayerMask.NameToLayer("Room");
        testMesh.SpawnOnStart = MRUK.RoomFilter.CurrentRoomOnly;
        try
        {
            await WaitFor(() => testMesh.EffectMeshObjects.Count > 0, 3);
            across = WallsAcross();
            if (testMesh.EffectMeshObjects.Count == 0 || across.Length > 0) { ExtraFail("room update: test walls built cut", $"{testMesh.EffectMeshObjects.Count} objects, across [{across}]"); return; }
            int rebuilds = holes.Rebuilds;
            string json = mruk.SaveSceneToJsonString(true);
            var res = await mruk.LoadSceneFromJsonString(Nudged(json, -0.008f, out string dropped));
            await Wait(1f);
            across = WallsAcross();
            string repro = $"load {res}, dropped {dropped}; across [{across}]";
            if (across.Contains(testMesh.name) && !across.Contains("WallOccluders")) ExtraPass("room update: repro — MRUK rebuilds the walls uncut", repro);
            else ExtraFail("room update: repro — MRUK rebuilds the walls uncut", repro);
            if (VirtualDoor.Instance == rig && rig != null) ExtraPass("room update: the door rig is kept", $"{VirtualDoor.All.Count} rig(s)");
            else ExtraFail("room update: the door rig is kept", $"rig rebuilt ({VirtualDoor.All.Count} rigs)");

            // 2b. Even with a wall across the doorway, a real M9 round through the shut door reaches a man on the path.
            await FarShot(spawner, "room update: shot through the shut door past an uncut wall");

            // 3. DoorHoles on: the pending update is rebuilt with its holes, doorways clear, the ways in back on.
            DoorHoles.Enabled = true;
            bool fixedUp = await WaitFor(() => holes.Rebuilds > rebuilds && WallsAcross().Length == 0 && LinksOn(), 4);
            string info = $"rebuilds {rebuilds} → {holes.Rebuilds}, across [{WallsAcross()}], links {LinksInfo()}";
            if (fixedUp) ExtraPass("room update: DoorHoles cuts them again", info); else ExtraFail("room update: DoorHoles cuts them again", info);

            // 4. Another update with DoorHoles on: its walls move with their anchors, never uncut, and are rebuilt again.
            rebuilds = holes.Rebuilds;
            res = await mruk.LoadSceneFromJsonString(Nudged(mruk.SaveSceneToJsonString(true), 0.008f, out _, dropStorage: false));
            string straight = WallsAcross();
            bool again = await WaitFor(() => holes.Rebuilds > rebuilds && LinksOn(), 4);
            info = $"load {res}, right after [{straight}], rebuilds {rebuilds} → {holes.Rebuilds}, after [{WallsAcross()}], links {LinksInfo()}";
            if (straight.Length == 0 && again && WallsAcross().Length == 0) ExtraPass("room update: a second update stays cut", info);
            else ExtraFail("room update: a second update stays cut", info);
            if (VirtualDoor.Instance == rig && rig != null) ExtraPass("room update: the door rig is kept (2)", $"{VirtualDoor.All.Count} rig(s)");
            else ExtraFail("room update: the door rig is kept (2)", $"rig rebuilt ({VirtualDoor.All.Count} rigs)");
        }
        finally
        {
            if (testMesh != null) { testMesh.DestroyMesh(); UnityEngine.Object.Destroy(testMesh.gameObject); }
            DoorHoles.Enabled = true;
        }

        // 5. The door kicked in: a real M9 round through the open doorway hits a man out on the path.
        var door = VirtualDoor.Instance;
        door.Breach();
        await WaitFor(() => door.IsOpen && door.OpenAngle > 60f, 3);
        await FarShot(spawner, "room update: shot through the open doorway");
    }

    /// <summary>Live effect-mesh objects (any EffectMesh) whose anchor isn't in <paramref name="game"/>.</summary>
    static int OtherRoomMeshes(MRUKRoom game)
    {
        int n = 0;
        foreach (var em in UnityEngine.Object.FindObjectsByType<EffectMesh>())
            foreach (var kv in em.EffectMeshObjects) if (kv.Value.effectMeshGO != null && (kv.Key == null || kv.Key.Room != game)) n++;
        return n;
    }

    static bool LinksOn()
    {
        foreach (var d in VirtualDoor.All) if (d != null && DoorwayNav.For(d.Index) is { } n && !n.LinkOn) return false;
        return true;
    }

    static string LinksInfo()
    {
        var parts = new List<string>();
        foreach (var d in VirtualDoor.All) if (d != null) parts.Add($"{d.Label}: {(DoorwayNav.For(d.Index) is { } n ? (n.LinkOn ? "on" : "off") : "none")}");
        return string.Join(", ", parts);
    }

    /// <summary>Every wall collider across a door rig's opening (DoorHoles.WallsAcross), as "EffectMesh:collider@door", or "".</summary>
    static string WallsAcross()
    {
        var owners = new Dictionary<GameObject, string>();
        foreach (var em in UnityEngine.Object.FindObjectsByType<EffectMesh>())
            foreach (var kv in em.EffectMeshObjects) if (kv.Value.effectMeshGO != null) owners[kv.Value.effectMeshGO] = em.name;
        var found = new List<string>();
        foreach (var (wall, door) in DoorHoles.WallsAcross())
            found.Add($"{(owners.TryGetValue(wall.gameObject, out var o) ? o : "?")}:{wall.name}@{door.Label}");
        return string.Join(", ", found);
    }

    /// <summary>
    /// The room JSON with every wall's outline moved <paramref name="dy"/> m up its own plane (the anchors stay put, so the door
    /// frames stay in their walls) and, with <paramref name="dropStorage"/>, one STORAGE volume removed: what the headset
    /// re-sent in 0.1.34 (same UUIDs, each wall's boundary a few mm off, one STORAGE gone). MRUK takes a boundary change as
    /// significant (a pose change of 2 cm alone is not) and rebuilds the wall.
    /// </summary>
    static string Nudged(string json, float dy, out string dropped, bool dropStorage = true)
    {
        dropped = "nothing";
        var root = JObject.Parse(json);
        var ents = (JArray)root["spatialEntities"];
        foreach (var e in ents)
        {
            string label = (string)e["semanticLabelMETA"] ?? "";
            if (!label.Contains("WALL_FACE")) continue;
            if (e["bounded2DMETA"]?["offset"] is JObject off) off["y"] = (float)off["y"] + dy;
            if (e["boundary2DMETA"] is JObject bd && (string)bd["vertices"] is { } b64)
            {
                var bytes = Convert.FromBase64String(b64);
                var v = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
                for (int i = 1; i < v.Length; i += 2) v[i] += dy; // (x, y) pairs
                Buffer.BlockCopy(v, 0, bytes, 0, v.Length * 4);
                bd["vertices"] = Convert.ToBase64String(bytes);
            }
        }
        if (dropStorage)
        {
            JToken victim = null;
            foreach (var e in ents) if (((string)e["semanticLabelMETA"] ?? "") == "STORAGE") { victim = e; break; }
            if (victim != null)
            {
                string id = (string)victim["uuid"];
                foreach (var e in ents)
                    if (e["spaceContainerMETA"]?["uuids"] is JArray ids)
                        for (int i = ids.Count - 1; i >= 0; i--) if ((string)ids[i] == id) ids.RemoveAt(i);
                victim.Remove();
                dropped = $"STORAGE {id.Substring(0, 8)}";
            }
        }
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }
}
