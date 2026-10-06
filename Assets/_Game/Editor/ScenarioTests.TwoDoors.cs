using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using HIS.Intruder;
using HIS.Room;
using HIS.Scenario;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// A second door (Brett 0.1.20: "I want to introduce ... a second door"; he marks a real one in Space Setup). The Editor room
/// gets one on another wall (Editor/TestRooms/BrettRoomTwoDoors.json = BrettRoom.json + a DOOR_FRAME on the living room's
/// far wall, where the floor inside is clear), the door picker's step 2 is auto-answered "yes" (DoorPicker.AutoSecondDoor), then:
///   both rigs and both ways in exist · the safe spot keeps 2 m from both · a man sent to the second door kicks IT in (the
///   front door stays shut) and is shot in the room (GOOD) · a burglar in through the second door is told to get out and
///   leaves the way he came (PERFECT) · shot hard while kicking the second door he gives up, bleeds under THAT door · the
///   front door still works · with no forced door, rounds use both doors.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunTwoDoorsHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_twoDoorsOnly;
    const string TwoDoorRoom = "Assets/_Game/Editor/TestRooms/BrettRoomTwoDoors.json";

    /// <summary>Batch entry: the two-door room and its cases.</summary>
    public static void RunTwoDoorsHeadless()
    {
        s_twoDoorsOnly = s_doorwayOnly = true;
        s_beforePlay = LoadTwoDoorRoom;
        RunHeadless();
    }

    static void LoadTwoDoorRoom()
    {
        var json = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(TwoDoorRoom);
        var mruk = Object.FindAnyObjectByType<MRUK>();
        if (json == null || mruk == null) { Debug.LogError($"[HIS] SCENARIO two doors: no room json ({json}) or MRUK ({mruk})"); return; }
        mruk.SceneSettings.SceneJsons = new[] { json };
        Debug.Log("[HIS] SCENARIO two doors: Editor room with a second door");
    }

    static IEnumerable<Case> WithTwoDoorCases(IEnumerable<Case> cases)
    {
        if (!s_twoDoorsOnly) { foreach (var c in cases) yield return c; yield break; }
        foreach (var c in TwoDoorCases()) yield return c;
    }

    static VirtualDoor Rig(int i) { foreach (var d in VirtualDoor.All) if (d != null && d.Index == i) return d; return null; }

    /// <summary>The room, rigs and ways in, before any case: 2 doors, 2 rigs, 2 ready doorways, a safe spot 2 m from both.</summary>
    static async Task TwoDoorChecks()
    {
        var rs = RoomScanner.Instance;
        // Play Mode entry resets the picker's statics, and an earlier run may have saved "front door only": pick again,
        // answering step 2 "yes" (the batch picker auto-confirms both steps).
        if (rs.SecondDoor == null && DoorPicker.Instance != null) { DoorPicker.AutoSecondDoor = true; DoorPicker.Instance.Begin(); }
        await WaitFor(() => rs.SecondDoor != null && VirtualDoor.All.Count == 2, 10);
        if (rs.SecondDoor == null) { ExtraFail("second door", $"{rs.Doors.Count} door(s), no second door confirmed"); return; }
        ExtraPass("second door", $"{rs.Doors.Count} doors, second at {rs.SecondDoor.transform.position}");
        var r0 = Rig(0); var r1 = Rig(1);
        if (r0 == null || r1 == null || r0.transform.parent != rs.PrimaryDoor.transform || r1.transform.parent != rs.SecondDoor.transform)
            ExtraFail("door rigs", $"rig0 {r0?.transform.parent?.name} rig1 {r1?.transform.parent?.name}");
        else ExtraPass("door rigs", $"front {r0.Center:F2}, second {r1.Center:F2}");
        var spawner = Object.FindAnyObjectByType<IntruderSpawner>();
        if (spawner == null || spawner.DoorCount != 2) ExtraFail("spawner doors", $"{spawner?.DoorCount}");
        var spot = SafeSpot.Instance != null ? SafeSpot.Instance.Spot : null;
        if (spot.HasValue)
        {
            float d0 = Flat(spot.Value - rs.PrimaryDoor.transform.position), d1 = Flat(spot.Value - rs.SecondDoor.transform.position);
            if (d0 < SafeSpot.FallbackDoorDistance - 0.01f || d1 < SafeSpot.FallbackDoorDistance - 0.01f) ExtraFail("safe spot, two doors", $"{d0:F2} / {d1:F2} m");
            else ExtraPass("safe spot, two doors", $"{d0:F2} m from the front door, {d1:F2} m from the second");
        }
        await DoorLightChecks();
    }

    /// <summary>
    /// Brett 0.1.38: "the second door turned grey after" (the police came to the front door, the one key light swung to it, and
    /// the side door on the facing wall was lit from behind its face: ambient only). With either door the aimed-at one, both
    /// closed leaves' room faces must get light: the key light's on one, its own fill (VirtualDoor.SetFill) on the other.
    /// </summary>
    static async Task DoorLightChecks()
    {
        var key = DoorKeyLight.Instance; var light = key != null ? key.Light : null;
        if (light == null) { ExtraFail("door light, two doors", "no key light"); return; }
        var saved = VirtualDoor.Current;
        try
        {
            for (int aimed = 0; aimed < 2; aimed++)
            {
                VirtualDoor.Current = Rig(aimed);
                await Wait(DoorKeyLight.CheckSeconds + 0.3f);
                string info = ""; bool ok = true;
                for (int i = 0; i < 2; i++)
                {
                    var d = Rig(i); if (d == null) { ok = false; continue; }
                    float main = Mathf.Max(0f, Vector3.Dot(d.Inward, -light.transform.forward));
                    var slab = d.GetComponentsInChildren<MeshRenderer>(true);
                    float fill = 0f;
                    foreach (var r in slab)
                    {
                        if (r.name != "Slab" || r.sharedMaterial == null || !r.sharedMaterial.HasProperty("_FillColor")) continue;
                        var c = r.sharedMaterial.GetColor("_FillColor"); Vector3 dir = r.sharedMaterial.GetVector("_FillDir");
                        float strength = (c.r + c.g + c.b) / 3f / Mathf.Max(0.01f, light.intensity);
                        fill = Mathf.Max(0f, Vector3.Dot(d.Inward, dir.normalized)) * strength;
                        break;
                    }
                    float lit = main + fill;
                    ok &= lit >= 0.3f;
                    info += $"{d.Label}: key {main:F2} + fill {fill:F2}{(i == aimed ? " (aimed)" : "")}; ";
                }
                if (ok) ExtraPass($"door light, aimed at door {aimed}", info); else ExtraFail($"door light, aimed at door {aimed}", info);
            }
        }
        finally { VirtualDoor.Current = saved; }
    }

    static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

    static System.Action AtDoor(int door, EntryStyle? style = null) => () =>
    {
        ScenarioDirector.ForcedDoor = door;
        if (style.HasValue) { ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = style.Value; }
    };

    /// <summary>Up the path on camera (IntruderApproach.cs): at door <paramref name="door"/>, ringing or not, going round or not.</summary>
    static System.Action WalksUp(int door, EntryStyle style, bool ring, float goRound) => () =>
    {
        ScenarioDirector.ForcedDoor = door; ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = style;
        IntruderT.StreetApproach = true; IntruderT.TestRingBell = ring; IntruderT.TestSwitchDoor = goRound;
        IntruderT.TestBurglarChoice = IntruderT.BurglarChoice.BackAway; // told to get out, he goes (the choice isn't under test)
    };

    /// <summary>He came to door <paramref name="i"/>, it's the one opening, and the other door stays shut.</summary>
    static async Task<bool> CameThrough(int i, string what, float timeout = 20f)
    {
        // (the last round's man stays Live() until the next one spawns: wait for one at the expected door)
        if (!await WaitFor(() => Live() != null && Live().DoorIndex == i && Alive(), timeout)) { ExtraFail(what, $"no intruder at door {i} (at {Live()?.DoorIndex})"); return false; }
        if (Live().DoorIndex != i) { ExtraFail(what, $"at door {Live().DoorIndex}, expected {i}"); return false; }
        bool opened = await WaitFor(() => Rig(i) != null && Rig(i).IsOpen, 60);
        var other = Rig(1 - i);
        if (!opened) { ExtraFail(what, $"door {i} never opened ({Live()?.Summary})"); return false; }
        if (other != null && other.IsOpen) { ExtraFail(what, $"door {1 - i} opened too"); return false; }
        ExtraPass(what, $"door {i} open, door {1 - i} shut");
        return true;
    }

    static IEnumerable<Case> TwoDoorCases() => new[]
    {
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = true, Setup = AtDoor(1), Act = async () =>
            { if (!await CameThrough(1, "in through the second door")) return;
              if (!await WaitFor(() => Dist() < 2.6f, 40)) { ExtraFail("second door: reaches you", Live()?.Summary); return; }
              for (int i = 0; i < 3; i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); } } },
        new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = true, Setup = AtDoor(1), Act = async () =>
            { if (!await CameThrough(1, "burglar in through the second door")) return;
              await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f); Yell(Command.GetOut);
              bool gone = await WaitFor(() => State() == IntruderT.State.Gone, 40);
              var nav = DoorwayNav.For(1); var i = Live();
              if (!gone || i == null) { ExtraFail("leaves by the second door", i?.Summary); return; }
              if (nav != null && nav.Beyond(i.transform.position)) ExtraPass("leaves by the second door", $"{i.transform.position:F2}");
              else ExtraFail("leaves by the second door", $"ended at {i.transform.position:F2}, not beyond door 1"); } },
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = true, Setup = AtDoor(1, EntryStyle.Classic), Act = async () =>
            { if (!await WaitFor(() => Live() != null && Live().Facts.ForcingDoor, 40)) { ExtraFail("second door shot", "never kicked"); return; }
              ClearLog();
              Shoot(1, 40f, HumanBodyBones.Chest);
              bool seep = await WaitForLog("blood under the door", 6f);
              if (!seep) ExtraFail("blood under the second door", "no seep");
              else ExtraPass("blood under the second door", "seeps under the leaf he was kicking");
              var r1 = Rig(1); var r0 = Rig(0);
              if ((r1 != null && r1.IsOpen) || (r0 != null && r0.IsOpen)) ExtraFail("second door shot: stays shut", $"door1 open {r1?.IsOpen}, door0 open {r0?.IsOpen}"); } },
        // Brett 0.1.20: the investigation — up the path on the door camera, the doorbell, round to the other door, and in there.
        new Case { Name = "Burglar who runs", Expect = Tier.Perfect, Doorway = true, Setup = WalksUp(0, EntryStyle.QuietPry, true, 1f), Act = async () =>
            { float t0 = Time.realtimeSinceStartup;
              if (!await WaitForLog("walks up from the street", 10f)) { ExtraFail("walk-up", "no walk up the path (no porch?)"); return; }
              if (!await WaitForLog("rings the doorbell", 40f)) ExtraFail("walk-up: doorbell", "never rang");
              else ExtraPass("walk-up: doorbell", $"rang {Time.realtimeSinceStartup - t0:F1} s in");
              if (!await WaitForLog("goes round to the second door", 20f)) { ExtraFail("walk-up: goes round", Live()?.Summary); return; }
              if (!await CameThrough(1, "walk-up: in through the other door", 40f)) return; // (round the house: up to ~20 s)
              float took = Time.realtimeSinceStartup - t0;
              if (took < 15f) ExtraFail("walk-up: a longer round", $"in after {took:F1} s"); else ExtraPass("walk-up: a longer round", $"in after {took:F1} s");
              await WaitFor(() => State() == IntruderT.State.Idle, 40); await Wait(1f); Yell(Command.GetOut);
              await WaitFor(() => State() == IntruderT.State.Gone, 40); } },
        // Brett 0.1.21: "when i shoot through the door and hit them I should see that on the feed". Hit out on the path: he
        // reacts (a burglar runs off), the wound is on him for the door camera (Outside layer) and never for the headset,
        // and it's a blind shot at a man who wasn't breaking in (bad).
        new Case { Name = "Burglar who runs", Expect = Tier.Bad, Doorway = true, Setup = WalksUp(0, EntryStyle.QuietPry, false, 0f), Act = async () =>
            { if (!await WaitForLog("walks up from the street", 10f)) { ExtraFail("path hit", "no walk-up"); return; }
              if (!await WaitFor(() => Live() != null && Live().OnPorchPath && Live().Facts.Distance < 7f, 20)) { ExtraFail("path hit", "never on the path"); return; }
              ClearLog();
              Shoot(1, 30f, HumanBodyBones.LeftUpperLeg);
              bool reacted = await WaitForLog("shot through the door", 3f);
              var i = Live(); var w = i != null ? i.GetComponent<HIS.Intruder.WoundSystem>() : null;
              int outsideLayer = LayerMask.NameToLayer("Outside"), onFeed = 0;
              if (i != null) foreach (var r in i.GetComponentsInChildren<MeshRenderer>(true)) if (r.name == "Blood" && r.enabled && r.gameObject.layer == outsideLayer) onFeed++;
              if (!reacted) ExtraFail("path hit: reacts", i?.Summary); else ExtraPass("path hit: reacts", "runs off after a round through the door on the path");
              if (w != null && w.ShownBodyWounds > 0) ExtraFail("path hit: wound hidden from you", $"{w.ShownBodyWounds} wound(s) drawn for the headset");
              if (onFeed == 0) ExtraFail("path hit: wound on the feed", "no wound on the Outside layer"); else ExtraPass("path hit: wound on the feed", $"{onFeed} wound(s) for the door camera");
              // He runs AWAY from the house (back down the path), not toward the door he was shot through.
              var door = Rig(0); float d0 = i != null && door != null ? Flat(i.transform.position - door.Center) : 0f;
              await Wait(3f); // the flinch and the cry first, then the run
              float d1 = i != null && door != null ? Flat(i.transform.position - door.Center) : 0f;
              if (d1 < d0 + 1f) ExtraFail("path hit: runs away from the house", $"{d0:F2} → {d1:F2} m from the door");
              else ExtraPass("path hit: runs away from the house", $"{d0:F2} → {d1:F2} m from the door"); } },
        // Killed out on the path: he dies where he is (not "at the door": no burst, no seep under it), and his blood pools on
        // the path for the door camera only.
        new Case { Name = "Home invader", Expect = Tier.Bad, Doorway = true, Setup = WalksUp(0, EntryStyle.Classic, false, 0f), Act = async () =>
            { if (!await WaitFor(() => Live() != null && Live().OnPorchPath && Live().Facts.Distance < 8f, 30)) { ExtraFail("path kill", "never on the path"); return; }
              var i = Live(); var door = Rig(0); float away = door != null ? Flat(i.transform.position - door.Center) : 0f;
              ClearLog();
              Shoot(1, 200f, HumanBodyBones.Head);
              await Wait(4f);
              bool dead = i != null && i.Current == IntruderT.State.Dead;
              int outsideLayer = LayerMask.NameToLayer("Outside"), pools = 0;
              foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                  if (r.name == "Blood" && r.enabled && r.gameObject.layer == outsideLayer && !r.transform.IsChildOf(i.transform)) pools++;
              bool doorOpen = door != null && door.IsOpen;
              if (!dead || doorOpen) ExtraFail("path kill: dies where he is", $"dead {dead}, door open {doorOpen}, {away:F2} m out");
              else ExtraPass("path kill: dies where he is", $"{away:F2} m out on the path, door shut");
              if (pools == 0) ExtraFail("path kill: pool on the feed", "no blood pool on the Outside layer"); else ExtraPass("path kill: pool on the feed", $"{pools} pool decal(s) for the door camera"); } },
        new Case { Name = "Home invader", Expect = Tier.Good, Doorway = true, Setup = AtDoor(0), Act = async () =>
            { if (!await CameThrough(0, "front door still works")) return;
              if (!await WaitFor(() => Dist() < 2.6f, 40)) { ExtraFail("front door: reaches you", Live()?.Summary); return; }
              for (int i = 0; i < 3; i++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); } } },
    };
}
