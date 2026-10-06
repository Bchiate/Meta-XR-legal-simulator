using HIS.Intruder;
using UnityEngine;
using UnityEngine.AI;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The way in and out (PLAN M1a "Entry sequences"): every frame of the walk in (Entering) and the walk out (Fleeing) his feet
/// are sampled against the NavMesh (the room's, the DoorwayNav apron, or the doorway link itself), the Room layer (walls,
/// furniture) and the Door layer (the open leaf); the door plane may only be crossed on the link, with the door open.
/// </summary>
public static partial class ScenarioTests
{
    sealed class DoorwayWatch
    {
        IntruderT _who;
        NavMeshAgent _agent;
        readonly int _room = LayerMask.GetMask("Room"), _door = LayerMask.GetMask("Door");
        // Per phase (0 = in, 1 = out): frames watched, frames off the NavMesh, frames in a wall/furniture, in the leaf,
        // door-plane crossings (on the link / off it / with the door shut).
        readonly int[] _frames = new int[2], _offNav = new int[2], _roomHits = new int[2], _leafHits = new int[2],
                       _linkCross = new int[2], _straightCross = new int[2], _shutCross = new int[2];
        readonly string[] _first = new string[2];
        float _side = float.NaN;
        bool _wasEntering, _gone;
        public bool EndedInside, EndedOutside;

        /// <param name="who">This case's intruder. A case right after one of the same name can start while the director still
        /// points at the last one's body: then the first live intruder the probe sees is the one watched.</param>
        public DoorwayWatch(IntruderT who)
        {
            if (who != null && who.Current is not (IntruderT.State.Dead or IntruderT.State.Gone)) Bind(who);
            IntruderT.FrameProbe += Probe;
        }

        void Bind(IntruderT who) { _who = who; _agent = who.GetComponent<NavMeshAgent>(); }

        public void Stop() => IntruderT.FrameProbe -= Probe;

        void Probe(IntruderT i)
        {
            var nav = DoorwayNav.Instance;
            if (_who == null && i.Current is IntruderT.State.Outside or IntruderT.State.BreakIn) Bind(i);
            if (i != _who || nav == null || _gone) return;
            var p = i.transform.position;
            float side = Vector3.Dot(p - nav.DoorPoint, nav.Outward); // > 0: behind the door (outside)
            var st = i.Current;
            if (_wasEntering && st != IntruderT.State.Entering)
                EndedInside = side < -0.3f && _agent.enabled && _agent.isOnNavMesh && !_agent.isOnOffMeshLink;
            _wasEntering = st == IntruderT.State.Entering;
            if (st == IntruderT.State.Gone) { _gone = true; EndedOutside = side > 0.9f; return; }
            int ph = st == IntruderT.State.Entering ? 0 : st == IntruderT.State.Fleeing ? 1 : -1;
            if (ph < 0 || i.BehindDoor) { _side = side; return; }
            _frames[ph]++;
            void Note(int[] counter, string what) { counter[ph]++; _first[ph] ??= $"{what} at {p} (side {side:F2})"; }
            bool onLink = _agent.enabled && _agent.isOnOffMeshLink;
            bool onNav = onLink || (_agent.enabled && NavMesh.SamplePosition(p, out var hit, 0.15f, nav.Filter)
                                    && new Vector2(hit.position.x - p.x, hit.position.z - p.z).magnitude < 0.05f);
            if (!onNav) Note(_offNav, "off the NavMesh");
            if (Physics.CheckSphere(p + Vector3.up * 0.35f, 0.15f, _room, QueryTriggerInteraction.Ignore)
                || Physics.CheckSphere(p + Vector3.up * 1.0f, 0.15f, _room, QueryTriggerInteraction.Ignore)) Note(_roomHits, "in a wall/furniture");
            if (Physics.CheckSphere(p + Vector3.up * 0.35f, 0.15f, _door, QueryTriggerInteraction.Ignore)
                || Physics.CheckSphere(p + Vector3.up * 1.0f, 0.15f, _door, QueryTriggerInteraction.Ignore)) Note(_leafHits, "in the door leaf" + LeafInfo(p, nav));
            if (!float.IsNaN(_side) && (_side > 0f) != (side > 0f))
            {
                var door = HIS.Room.VirtualDoor.Instance;
                if (door != null && !door.IsOpen) Note(_shutCross, "through the shut door");
                if (onLink) _linkCross[ph]++; else Note(_straightCross, "across the door plane off the link");
            }
            _side = side;
        }

        /// <summary>Why he's at the leaf: how close, and whether the NavMesh under its edge was carved away (NavMeshObstacle).</summary>
        string LeafInfo(Vector3 p, DoorwayNav nav)
        {
            if (_leafHits[0] + _leafHits[1] > 1) return "";
            var hits = Physics.OverlapSphere(p + Vector3.up * 0.35f, 0.3f, _door, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) hits = Physics.OverlapSphere(p + Vector3.up * 1.0f, 0.3f, _door, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return "";
            var c = hits[0];
            var q = c.ClosestPoint(p + Vector3.up * 0.35f); var onFloor = new Vector3(q.x, p.y, q.z);
            var ob = c.GetComponent<NavMeshObstacle>();
            bool navAtLeaf = NavMesh.SamplePosition(onFloor, out var h, 0.1f, nav.Filter);
            return $" ({c.name} {c.bounds.size}, {new Vector2(q.x - p.x, q.z - p.z).magnitude:F2} m off, obstacle {(ob != null ? $"{ob.enabled}/{ob.carving}/{ob.size}" : "none")}, " +
                   $"NavMesh under the leaf {navAtLeaf}, door {HIS.Room.VirtualDoor.Instance?.State} {HIS.Room.VirtualDoor.Instance?.OpenAngle:F0}°, agent {_agent.velocity.magnitude:F2} m/s path {_agent.pathStatus})";
        }

        /// <summary>One PASS/FAIL line for the walk in (phase 0) or out (1).</summary>
        public void Report(string what, int ph)
        {
            bool end = ph == 0 ? EndedInside : EndedOutside;
            string info = $"{_frames[ph]} frames, off-NavMesh {_offNav[ph]}, wall/furniture {_roomHits[ph]}, leaf {_leafHits[ph]}, " +
                          $"crossings on the link {_linkCross[ph]} / off it {_straightCross[ph]} / door shut {_shutCross[ph]}, " +
                          $"{(ph == 0 ? "on the room NavMesh inside" : "gone from behind the door")} {end}";
            bool ok = end && _frames[ph] > 10 && _offNav[ph] == 0 && _roomHits[ph] == 0 && _leafHits[ph] == 0 && _linkCross[ph] == 1
                      && _straightCross[ph] == 0 && _shutCross[ph] == 0;
            if (ok) ExtraPass(what, info); else ExtraFail(what, info + (_first[ph] != null ? " — first: " + _first[ph] : ""));
        }
    }

    static DoorwayWatch WatchDoorway() => new(Live());
}

public static partial class ScenarioTests
{
    static bool s_doorwayOnly;

    /// <summary>Batch entry for just the doorway cases (a quick check of the way in/out): scripts/unity-batch.sh ScenarioTests.RunDoorwayHeadless.</summary>
    public static void RunDoorwayHeadless() { s_doorwayOnly = true; RunHeadless(); }

    static async System.Threading.Tasks.Task<bool> ConfirmDoorForDoorwayRun()
    {
        if (!await WaitFor(() => HIS.Room.RoomScanner.Instance != null && HIS.Room.RoomScanner.Instance.IsReady && HIS.Room.RoomScanner.Instance.PrimaryDoor != null, 30))
        { ExtraFail("doorway run", "no room/door"); return false; }
        if (!HIS.Room.RoomScanner.Instance.DoorConfirmed) HIS.Room.RoomScanner.Instance.ConfirmDoor();
        return true;
    }
}
