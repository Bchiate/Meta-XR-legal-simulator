using System.Collections;
using HIS.Room;
using Meta.XR.MRUtilityKit;
using Unity.AI.Navigation;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// Waits for the room scan and the NavMesh bake, then spawns intruders outside a door: the front door, or the second door
    /// the player confirmed (<see cref="RoomScanner.ActiveDoors"/>; each door has its own <see cref="DoorwayNav"/>).
    /// Phase 1: one intruder at a time, respawns a few seconds after death.
    /// </summary>
    public class IntruderSpawner : MonoBehaviour
    {
        [SerializeField] Intruder intruderPrefab;
        [SerializeField] SceneNavigation sceneNavigation;
        [SerializeField] float outsideDistance = 1.25f;  // behind the door plane: his kick (toe reaches ~1.27 m) just meets the door
        [SerializeField] float insideDistance = 0.7f;    // how far into the room he steps before pathing
        [SerializeField] float respawnDelay = 3f;
        [SerializeField] bool autoSpawn = true;

        public Intruder Active { get; private set; }
        /// <summary>Sandbox: the role for the next spawn (null = the plain melee attacker). Set by the ScenarioDirector.</summary>
        public System.Func<HIS.Scenario.ScenarioDef> SandboxRole;
        /// <summary>Sandbox respawns forever; the scenario director turns this off and spawns one per scenario.</summary>
        public bool AutoSpawn { get => autoSpawn; set { autoSpawn = value; if (!value && _loop != null) { StopCoroutine(_loop); _loop = null; } } }
        public int Kills { get; private set; }
        public bool NavMeshReady { get; private set; }
        public string Status { get; private set; } = "waiting for room";

        RoomScanner _room;
        DoorwayNav _doorway;                       // door 0's (on this object); door 1's lives on a child
        readonly System.Collections.Generic.List<DoorwayNav> _navs = new();
        int _agentTypeId;
        bool _paused;
        Coroutine _loop;

        void Start()
        {
            _room = RoomScanner.Instance;
            if (!TryGetComponent(out _doorway)) _doorway = gameObject.AddComponent<DoorwayNav>();
            _doorway.Index = 0;
            _navs.Add(_doorway);
            if (_room == null) { Debug.LogError("[HIS] IntruderSpawner: no RoomScanner"); return; }
            if (sceneNavigation == null) sceneNavigation = FindAnyObjectByType<SceneNavigation>();
            if (sceneNavigation != null) sceneNavigation.OnNavMeshInitialized.AddListener(OnNavMeshReady);

            if (_room.IsReady) OnRoomReady(_room); else _room.RoomReady += OnRoomReady;
        }

        void OnRoomReady(RoomScanner room)
        {
            Status = room.PrimaryDoor != null ? "room ok, baking navmesh" : "room ok, NO DOOR scanned";
            if (sceneNavigation == null)
            {
                // No navigation helper: spawn anyway, the intruder falls back to straight-line chase.
                NavMeshReady = true;
                if (autoSpawn && _loop == null) _loop = StartCoroutine(SpawnLoop());
            }
        }

        public void Pause(bool paused) { _paused = paused; }

        /// <summary>Forget the current intruder and start a fresh spawn cycle.</summary>
        public void ResetSpawner()
        {
            if (_loop != null) StopCoroutine(_loop);
            _loop = null; // OnNavMeshReady only starts a loop when none is running
            Active = null; Kills = 0; _paused = false;
            Status = "reset";
            if (NavMeshReady && autoSpawn) _loop = StartCoroutine(SpawnLoop());
        }

        void OnNavMeshReady()
        {
            var surface = sceneNavigation.GetComponent<NavMeshSurface>();
            _agentTypeId = surface != null ? surface.agentTypeID : 0;
            NavMeshReady = true;
            Status = "navmesh ready";
            Debug.Log($"[HIS] NavMesh ready (agentTypeID {_agentTypeId})");
            if (Active != null) Active.SetAgentType(_agentTypeId); // MRUK re-bakes as the room refines
            for (int d = 0; d < DoorCount; d++) // the link's room end follows the new NavMesh
                if (TryGetDoorPoints(d, out var o, out var i, out _)) BuildDoorway(d, o, i);
            if (autoSpawn && _loop == null) _loop = StartCoroutine(SpawnLoop());
        }

        IEnumerator SpawnLoop()
        {
            while (true)
            {
                while (_paused) yield return null;
                int door = DoorCount > 1 ? Random.Range(0, DoorCount) : 0;
                if (Active == null && DoorConfirmed && TryGetDoorPoints(door, out var outside, out var inside, out var floorY))
                {
                    Spawn(outside, inside, floorY, door);
                    Status = $"intruder active (kills {Kills})";
                }
                // A yell can get through to him in Sandbox too: he walks out, or gives up until "the police take him".
                // Either one is done with, same as a kill (Died never fires for them).
                while (Active != null && Active.Current != Intruder.State.Dead && !Active.HasLeft && !Active.InCustody) yield return null;
                if (Active != null && Active.Current != Intruder.State.Dead)
                {
                    var done = Active;
                    bool left = done.HasLeft;
                    Active = null;
                    if (left) Destroy(done.gameObject);
                    else done.Retire(); // in custody: he goes once the player isn't looking (no pop in front of him)
                    Status = left ? "intruder left" : "intruder in custody";
                }
                yield return new WaitForSeconds(respawnDelay);
            }
        }

        /// <summary>The player has confirmed the intruder door (M1a safety: <see cref="HIS.Room.DoorPicker"/>). Nothing spawns before.</summary>
        public bool DoorConfirmed => _room != null && _room.DoorConfirmed;

        /// <summary>How many doors intruders use now: 1, or 2 with a confirmed second door.</summary>
        public int DoorCount => _room != null ? _room.ActiveDoors.Count : 0;

        /// <summary>
        /// One scenario's intruder, configured for his role, outside door <paramref name="door"/> (0 = the front door; out of
        /// range = the front door); null until the room, a confirmed door and the NavMesh exist.
        /// </summary>
        public Intruder SpawnScenario(HIS.Scenario.ScenarioDef def, int door = 0)
        {
            if (door < 0 || door >= DoorCount) door = 0;
            if (!NavMeshReady || !DoorConfirmed || !TryGetDoorPoints(door, out var outside, out var inside, out var floorY)) return null;
            BuildDoorway(door, outside, inside);
            var player = Camera.main != null ? Camera.main.transform : transform;
            Active = Instantiate(intruderPrefab, outside, Quaternion.identity);
            Active.name = "Intruder";
            Active.Configure(def);
            Active.Initialize(outside, inside, floorY, _agentTypeId, player, door);
            Status = $"scenario: {def.Name}";
            Debug.Log($"[HIS] Scenario intruder spawned outside {(door == 0 ? "the front door" : "the second door")} at {outside}");
            return Active;
        }

        /// <summary>
        /// Ricky, the protectee of a US Marshal night (research/27 §3): crouched on his hiding spot in the room
        /// (<see cref="HideSpot.Pick"/>: the best spot you aren't standing on, facing the safe spot), not at a door. One Ricky
        /// at a time: one still there is replaced. Never <see cref="Active"/> (he's no visitor: the round code, the Sandbox loop
        /// and the proximity guard never see him). Null without a confirmed door or a spot in this room.
        /// </summary>
        public Intruder SpawnProtectee()
        {
            if (!DoorConfirmed) { Debug.LogWarning("[HIS] Ricky: no confirmed door yet — not spawned"); return null; }
            var player = Camera.main != null ? Camera.main.transform : transform;
            if (!HideSpot.Pick(player.position, out var spot, out var facing)) { Debug.LogWarning("[HIS] Ricky: no hiding spot in this room — not spawned"); return null; }
            // The front door's points: his facts read "inside" off them (he's never at it).
            if (!TryGetDoorPoints(0, out var outside, out var inside, out var floorY)) { outside = inside = spot; floorY = spot.y; }
            var old = Intruder.Protectee;
            if (old != null) { Debug.Log("[HIS] Ricky: the last one is cleared first"); Destroy(old.gameObject); }
            spot.y = floorY;
            var ricky = Instantiate(intruderPrefab, spot, Quaternion.LookRotation(facing, Vector3.up));
            ricky.name = "Ricky";
            ricky.InitializeProtectee(spot, facing, floorY, outside, inside, _agentTypeId, player);
            return ricky;
        }

        /// <summary>
        /// Deputy Ortiz IN THE ROOM for a Marshal night's handover (Brett 0.1.57; Intruder.InitializeHandover): standing on
        /// <paramref name="spot"/> (Intruder.HandoverStandSpot picked it: by the front door, clear of its leaf) facing
        /// <paramref name="facing"/>, the front door's way out built for him (he leaves through it). One at a time: one still
        /// there is replaced. Never <see cref="Active"/> (no round, no judge, no command ever sees him). Null without the room's
        /// NavMesh, a confirmed door or the front door's points.
        /// </summary>
        public Intruder SpawnHandoverDeputy(HIS.Scenario.ScenarioDef def, Vector3 spot, Vector3 facing)
        {
            if (def == null || !NavMeshReady || !DoorConfirmed) { Debug.LogWarning("[HIS] Ortiz: no room NavMesh or confirmed door yet — not spawned"); return null; }
            if (!PrepareDoor(0, out var outside, out var inside, out var floorY)) { Debug.LogWarning("[HIS] Ortiz: no front door points — not spawned"); return null; }
            var player = Camera.main != null ? Camera.main.transform : transform;
            var old = Intruder.HandoverDeputy;
            if (old != null) { Debug.Log("[HIS] Ortiz: the last one is cleared first"); Destroy(old.gameObject); }
            spot.y = floorY;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-4f) facing = inside - outside;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.forward;
            var ortiz = Instantiate(intruderPrefab, spot, Quaternion.LookRotation(facing.normalized, Vector3.up));
            ortiz.name = "Ortiz";
            ortiz.InitializeHandover(def, spot, facing.normalized, floorY, outside, inside, _agentTypeId, player);
            return ortiz;
        }

        /// <summary>
        /// The door on the floor and its outward direction (the outcome show's siren/bullhorn): the current intruder's door,
        /// else the front door.
        /// </summary>
        public bool TryGetDoor(out Vector3 doorPoint, out Vector3 outward) =>
            TryGetDoor(Active != null ? Active.DoorIndex : 0, out doorPoint, out outward);

        /// <summary>Door <paramref name="door"/> on the floor and its outward direction.</summary>
        public bool TryGetDoor(int door, out Vector3 doorPoint, out Vector3 outward)
        {
            doorPoint = outward = default;
            if (!TryGetDoorPoints(door, out var outside, out var inside, out _)) return false;
            outward = (outside - inside); outward.y = 0; outward.Normalize();
            doorPoint = inside + outward * insideDistance;
            return true;
        }

        public Intruder Spawn(Vector3 outside, Vector3 inside, float floorY, int door = 0)
        {
            BuildDoorway(door, outside, inside);
            var player = Camera.main != null ? Camera.main.transform : transform;
            Active = Instantiate(intruderPrefab, outside, Quaternion.identity);
            Active.name = "Intruder";
            // Only clear Active if it's still him (a man left in custody can still be shot after the next one spawned).
            Active.Died += who => { Kills++; if (Active == who) Active = null; Status = $"intruder killed (kills {Kills})"; };
            var role = SandboxRole?.Invoke();
            if (role != null) Active.Configure(role);
            Active.Initialize(outside, inside, floorY, _agentTypeId, player, door);
            Debug.Log($"[HIS] Intruder spawned outside door {door} at {outside}, will enter to {inside}");
            return Active;
        }

        /// <summary>The way in and out for door <paramref name="door"/>: the apron behind it and the NavMeshLink through it.</summary>
        void BuildDoorway(int door, Vector3 outside, Vector3 inside)
        {
            if (_doorway == null || sceneNavigation == null) return; // no room NavMesh: nothing to link to
            var outward = outside - inside; outward.y = 0f; outward.Normalize();
            var n = sceneNavigation;
            NavFor(door).Build(inside + outward * insideDistance, outward, _agentTypeId, n.AgentRadius, n.AgentHeight, n.AgentClimb, n.AgentMaxSlope);
        }

        DoorwayNav NavFor(int door)
        {
            while (_navs.Count <= door)
            {
                var go = new GameObject($"[HIS] Doorway {_navs.Count}");
                go.transform.SetParent(transform, false);
                var nav = go.AddComponent<DoorwayNav>();
                nav.Index = _navs.Count;
                _navs.Add(nav);
            }
            return _navs[door];
        }

        public bool TryGetDoorPoints(out Vector3 outside, out Vector3 inside, out float floorY) => TryGetDoorPoints(0, out outside, out inside, out floorY);

        /// <summary>Door <paramref name="door"/>'s points and its way in, ready for an intruder who went round to it.</summary>
        public bool PrepareDoor(int door, out Vector3 outside, out Vector3 inside, out float floorY)
        {
            if (!TryGetDoorPoints(door, out outside, out inside, out floorY)) return false;
            BuildDoorway(door, outside, inside);
            return true;
        }

        /// <summary>
        /// Outside/inside points straddling door <paramref name="index"/> (0 = the front door) on the floor. "Outside" is the
        /// side facing away from the room's floor centre, so it doesn't matter which way the anchor's normal points.
        /// </summary>
        public bool TryGetDoorPoints(int index, out Vector3 outside, out Vector3 inside, out float floorY)
        {
            outside = inside = default; floorY = 0;
            var doors = _room != null ? _room.ActiveDoors : null;
            var door = doors != null && index >= 0 && index < doors.Count ? doors[index] : null;
            var floor = _room?.Room?.FloorAnchors is { Count: > 0 } f ? f[0] : null;
            if (door == null || floor == null) return false;

            floorY = floor.transform.position.y;
            var roomCenter = floor.GetAnchorCenter(); roomCenter.y = floorY;
            var doorFloor = door.transform.position; doorFloor.y = floorY;

            var outward = doorFloor - roomCenter; outward.y = 0;
            // A door frame's plane normal is a better "through the wall" axis than the centre direction when available.
            var n = door.transform.forward; n.y = 0;
            if (n.sqrMagnitude > 1e-4f) outward = Vector3.Dot(n, outward) >= 0 ? n : -n;
            outward.Normalize();

            outside = doorFloor + outward * outsideDistance;
            inside = doorFloor - outward * insideDistance;
            return true;
        }
    }
}
