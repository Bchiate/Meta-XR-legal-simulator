using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Opening the door yourself (research/25; Brett 2026-10-01: "adding a way to open the door yourself before someone breaks
/// in"). Two test hands drive the real HandController.GripInput (as BodySlotTests does): a hand holding a weapon can't take
/// the handle; an empty hand opens the door and shuts it again while the knife man is still out on the path (he keeps
/// coming, unseen); a leaf in a scripted swing, and a squeeze begun away from the lever, don't take it; then, while he's knocking, the door opened on him: he reacts without a kick, he's in view, and it isn't
/// a forced entry. Stopped coming at you with the knife: GOOD.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunOpenDoorHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_openDoorOnly;

    public static void RunOpenDoorHeadless() { s_openDoorOnly = s_doorwayOnly = true; RunHeadless(); } // (the doorway run's short start)

    static IEnumerable<Case> WithOpenDoorCases(IEnumerable<Case> cases)
    {
        if (!s_openDoorOnly) foreach (var c in cases) yield return c;
        foreach (var c in OpenDoorCases()) yield return c;
    }

    static IEnumerable<Case> OpenDoorCases() => new[]
    {
        new Case { Name = "Knife attacker", Expect = Tier.Good, Doorway = s_openDoorOnly, Act = OpenTheDoorCase, Setup = () =>
            {
                ScenarioDirector.VariedEntries = true; ScenarioDirector.ForcedStyle = EntryStyle.KnockThenKick;
                IntruderT.StreetApproach = true; IntruderT.TestSwitchDoor = 0f; IntruderT.TestRingBell = false; // up the path, straight to this door
            } },
    };

    const BindingFlags OdNP = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly MethodInfo s_odGrip = typeof(HandController).GetMethod("GripInput", OdNP);
    static readonly MethodInfo s_odSetHeld = typeof(HandController).GetProperty("Held")?.GetSetMethod(true);

    static async Task OpenTheDoorCase()
    {
        var door = VirtualDoor.Instance;
        if (door == null || s_odGrip == null || s_odSetHeld == null) { ExtraFail("open door", $"door {door}, GripInput {s_odGrip}, Held setter {s_odSetHeld}"); return; }
        var right = OpenDoorHand(false); var left = OpenDoorHand(true);
        try
        {
            if (!await WaitFor(() => Live() != null && Live().OnPorchPath && Flat(Live().transform.position - door.Threshold) > 3.5f, 15))
            { ExtraFail("open door: he walks up the path", Live()?.Summary); return; }
            var lever = door.HandleWorld + door.Inward * 0.05f; // the lever stands ~5 cm proud of the leaf

            // A hand holding a weapon can't take the handle.
            var weapon = Object.FindAnyObjectByType<Grabbable>();
            if (weapon == null) ExtraFail("open door: a hand holding a weapon can't take the handle", "no Grabbable in the scene");
            else
            {
                HandAt(left, lever);
                s_odSetHeld.Invoke(left, new object[] { weapon });
                for (int k = 0; k < 15; k++) Squeeze(left, 1f);
                string info = $"door held {door.HeldByPlayer}, open {door.IsOpen}, hand on it {left.OnDoor}";
                s_odSetHeld.Invoke(left, new object[] { null }); // (cleared before the grip opens: nothing to drop)
                Squeeze(left, 0f);
                if (!door.HeldByPlayer && !door.IsOpen && !left.OnDoor) ExtraPass("open door: a hand holding a weapon can't take the handle", info);
                else ExtraFail("open door: a hand holding a weapon can't take the handle", info);
            }

            // An empty hand opens it (pulled 60° round the hinge) and shuts it again; he's still out on the path, unseen, coming.
            HandAt(right, lever);
            Squeeze(right, 0f); Squeeze(right, 1f);
            bool took = door.IsHeldBy(right) && right.OnDoor;
            SwingHand(right, door, 60f);
            string opened = $"took {took}, open {door.IsOpen} at {door.OpenAngle:F0}° (stop {door.MaxOpenAngle:F0}°), by you {door.OpenedByPlayer}";
            if (took && door.IsOpen && door.OpenAngle > 45f && door.OpenedByPlayer) ExtraPass("open door: an empty hand opens it", opened);
            else ExtraFail("open door: an empty hand opens it", opened);
            await Wait(0.3f);
            var man = Live();
            SwingHand(right, door, -60f);
            string shut = $"state {door.State}, by you {door.OpenedByPlayer}, still held {door.IsHeldBy(right)}";
            Squeeze(right, 0f);
            if (door.State == VirtualDoor.DoorState.Closed && !door.OpenedByPlayer) ExtraPass("open door: and shuts it again", shut);
            else ExtraFail("open door: and shuts it again", shut);
            string coming = man != null ? $"on the path {man.OnPorchPath}, hidden {man.BehindDoor}, {Flat(man.transform.position - door.Threshold):F1} m out" : "gone";
            if (man != null && man == Live() && man.OnPorchPath && man.BehindDoor) ExtraPass("open door: a man out on the path keeps coming", coming);
            else ExtraFail("open door: a man out on the path keeps coming", coming);

            // A leaf in a scripted swing (a key in the lock, the burglar's pry) can't be caught and pushed shut on him.
            door.OpenGently(0.5f, 40f);
            HandAt(right, lever);
            Squeeze(right, 0f); Squeeze(right, 1f);
            string swinging = $"held {door.HeldByPlayer}, hand on it {right.OnDoor}, state {door.State}";
            Squeeze(right, 0f);
            if (!door.HeldByPlayer && !right.OnDoor) ExtraPass("open door: not mid scripted swing", swinging);
            else ExtraFail("open door: not mid scripted swing", swinging);
            await Wait(0.7f);
            door.Close(0.05f);
            await Wait(0.2f);
            // A squeeze begun away from the lever (on the way to a holster) never ends on it: the grab buffer only takes weapons.
            var rot = right.transform.rotation;
            right.transform.rotation = Quaternion.LookRotation(Vector3.down, door.Inward); // (aimed at the floor: no distance pull)
            HandAt(right, lever + door.Inward * 0.5f);
            Squeeze(right, 0f); Squeeze(right, 1f);
            HandAt(right, lever);
            for (int k = 0; k < 5; k++) Squeeze(right, 1f);
            string buffered = $"held {door.HeldByPlayer}, hand on it {right.OnDoor}, open {door.IsOpen}, holding {right.Held}";
            Squeeze(right, 0f);
            right.transform.rotation = rot;
            if (!door.HeldByPlayer && !right.OnDoor && !door.IsOpen) ExtraPass("open door: the grab buffer never takes the handle", buffered);
            else ExtraFail("open door: the grab buffer never takes the handle", buffered);

            // He knocks (not forcing it yet): the door opened on him. He sees you and comes in — no kick — and it isn't forced.
            if (!await WaitFor(() => State() == IntruderT.State.BreakIn, 40)) { ExtraFail("open door: he knocks", Live()?.Summary); return; }
            await Wait(1.2f);                                    // the two knocks are done: the silence before the kicks
            ClearLog();
            man = Live();
            HandAt(right, lever);
            Squeeze(right, 0f); Squeeze(right, 1f);
            SwingHand(right, door, 60f);
            Squeeze(right, 0f);                                  // let go: it carries on open
            bool reacted = await WaitForLog("the door opened on him (let in)", 2f);
            await Wait(0.2f);
            var facts = man.Facts;
            string how = $"reacted {reacted}, door breached {door.Breached}, open {door.IsOpen}, he's {(man.BehindDoor ? "hidden" : "in view")}, " +
                         $"forced entry {facts.ForcedEntry}, {man.Summary}";
            if (reacted && !door.Breached && door.IsOpen && !man.BehindDoor) ExtraPass("open door: he reacts, no kick", how);
            else ExtraFail("open door: he reacts, no kick", how);
            if (!facts.ForcedEntry) ExtraPass("open door: not a forced entry", how); else ExtraFail("open door: not a forced entry", how);

            // In and coming at you with the knife: stopping him is a good call.
            await WaitFor(() => Dist() < 2.6f, 40);
            for (int k = 0; k < 4 && Alive(); k++) { Shoot(1, 34f, HumanBodyBones.Chest); await Wait(0.3f); }
        }
        finally
        {
            Object.Destroy(right.gameObject); Object.Destroy(left.gameObject);
        }
    }

    /// <summary>A HandController with the right handedness, disabled: no OVRInput polling, the test feeds GripInput itself.</summary>
    static HandController OpenDoorHand(bool isLeft)
    {
        var go = new GameObject(isLeft ? "[OpenDoorTest] Hand L" : "[OpenDoorTest] Hand R");
        go.SetActive(false);
        var h = go.AddComponent<HandController>();
        typeof(HandController).GetField("controller", OdNP).SetValue(h, isLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch);
        go.SetActive(true);  // Awake: the AimAnchor on the web point
        h.enabled = false;
        return h;
    }

    /// <summary>Moves the hand so its grab point (Anchor) is at <paramref name="p"/>.</summary>
    static void HandAt(HandController h, Vector3 p) => h.transform.position += p - h.Anchor.position;

    static void Squeeze(HandController h, float grip) => s_odGrip.Invoke(h, new object[] { grip, 0.02f });

    /// <summary>
    /// Squeezing, the hand swings <paramref name="degrees"/> round the door's hinge (+ = into the room): 1.5° per GripInput
    /// call at 0.02 s, so the sweep doesn't depend on the batch frame rate.
    /// </summary>
    static void SwingHand(HandController h, VirtualDoor door, float degrees)
    {
        var hinge = door.HingeWorld; var x = door.HingeToLatch; var z = door.Inward;
        var v = h.Anchor.position - hinge;
        float r = new Vector2(Vector3.Dot(v, x), Vector3.Dot(v, z)).magnitude;
        float a0 = Mathf.Atan2(Vector3.Dot(v, z), Vector3.Dot(v, x)) * Mathf.Rad2Deg;
        int steps = Mathf.CeilToInt(Mathf.Abs(degrees) / 1.5f);
        for (int i = 1; i <= steps; i++)
        {
            float a = (a0 + degrees * i / steps) * Mathf.Deg2Rad;
            HandAt(h, hinge + x * (Mathf.Cos(a) * r) + z * (Mathf.Sin(a) * r) + Vector3.up * v.y);
            Squeeze(h, 1f);
        }
    }
}
