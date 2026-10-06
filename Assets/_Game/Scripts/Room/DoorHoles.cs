using HIS.Intruder;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// Keeps the door holes in the room's wall meshes (Brett 0.1.34: "nothing was registering", a door looked see-through, the
    /// man who kicked it in was all grey). When the device re-sends the room (it does on resume) and a wall changed, MRUK's
    /// EffectMesh rebuilds that wall through its per-anchor path (ReceiveAnchorUpdatedCallback → CreateEffectMesh), which never
    /// cuts the door holes. The WallOccluders wall then lies across the doorway: its collider stops every round and blocks the
    /// way in, and its depth hides the door leaf, the outside and the man behind it.
    /// So after any change to the room (once the burst of anchor events has settled) every wall EffectMesh that cuts holes is
    /// rebuilt through its whole-room path (CreateMesh, which cuts them), each doorway is checked for a wall across it, and
    /// the ways in are reconnected. The rebuild also leaves those walls untracked by MRUK's per-anchor path (EffectMesh.
    /// DestroyMesh), so later updates move them with their anchors instead of rebuilding them uncut. A slow watchdog repeats
    /// the doorway check. Firearm passes a round through a wall left across a doorway anyway (<see cref="WallAcrossDoorway"/>).
    /// Only the game's room (<see cref="RoomScanner.Room"/>) is built: on a scene load MRUK builds the room the head is in,
    /// which may be the next room, and Brett's front door opens onto one whose wall stands 5 cm behind the door (no hole there:
    /// it has no door frame) — so any other room's effect meshes are cleared at each rebuild.
    /// </summary>
    public class DoorHoles : MonoBehaviour
    {
        public static DoorHoles Instance { get; private set; }
        /// <summary>Tests: false = MRUK's own handling only (the 0.1.34 repro). Room events still mark the walls for a rebuild.</summary>
        public static bool Enabled = true;
        /// <summary>How many times the wall meshes were rebuilt (tests).</summary>
        public int Rebuilds { get; private set; }
        /// <summary>
        /// A room change came in and its rebuild hasn't run yet (the burst of anchor events is still settling): the door rigs
        /// hold their hinge check until it has (VirtualDoor.RechooseHinge). False while disabled (nothing would clear it).
        /// </summary>
        public bool Pending => Enabled && isActiveAndEnabled && _dirtyAt >= 0f;

        const MRUKAnchor.SceneLabels Walls = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE;
        const float SettleSeconds = 0.25f, WatchSeconds = 2f;
        const int MaxTries = 3; // rebuilds in a row that leave a doorway blocked before giving up until the room changes again

        MRUKRoom _room;          // the room whose anchor events we hear
        float _dirtyAt = -1f;    // realtime of the last room event; -1 = nothing pending
        int _tries;
        bool _navRebaked; // the game's room NavMesh rebaked since the last room change
        float _nextWatch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; Enabled = true; } // domain reload off

        void Awake() => Instance = this;

        void Start()
        {
            var mruk = MRUK.Instance;
            if (mruk == null) return;
            mruk.RegisterSceneLoadedCallback(OnSceneLoaded); // (called at once if the scene is already loaded)
            mruk.RoomCreatedEvent.AddListener(OnRoomEvent);
            mruk.RoomUpdatedEvent.AddListener(OnRoomEvent);
            mruk.RoomRemovedEvent.AddListener(OnRoomEvent);
        }

        void OnDestroy()
        {
            Hook(null);
            var mruk = MRUK.Instance;
            if (mruk != null)
            {
                mruk.SceneLoadedEvent.RemoveListener(OnSceneLoaded);
                mruk.RoomCreatedEvent.RemoveListener(OnRoomEvent);
                mruk.RoomUpdatedEvent.RemoveListener(OnRoomEvent);
                mruk.RoomRemovedEvent.RemoveListener(OnRoomEvent);
            }
            if (Instance == this) Instance = null;
        }

        void OnSceneLoaded() { Hook(GameRoom()); MarkDirty(); }

        /// <summary>The room the game plays in (RoomScanner's; it survives a re-sent scene), else MRUK's current room.</summary>
        static MRUKRoom GameRoom()
        {
            var scanner = RoomScanner.Instance;
            if (scanner != null && scanner.Room != null) return scanner.Room;
            return MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        }
        void OnRoomEvent(MRUKRoom _) => MarkDirty();
        void OnAnchorEvent(MRUKAnchor _) => MarkDirty();
        void MarkDirty() { _dirtyAt = Time.realtimeSinceStartup; _tries = 0; _navRebaked = false; }

        void Hook(MRUKRoom room)
        {
            if (room == _room) return;
            if (_room != null)
            {
                _room.AnchorCreatedEvent.RemoveListener(OnAnchorEvent);
                _room.AnchorUpdatedEvent.RemoveListener(OnAnchorEvent);
                _room.AnchorRemovedEvent.RemoveListener(OnAnchorEvent);
            }
            _room = room;
            if (_room == null) return;
            _room.AnchorCreatedEvent.AddListener(OnAnchorEvent);
            _room.AnchorUpdatedEvent.AddListener(OnAnchorEvent);
            _room.AnchorRemovedEvent.AddListener(OnAnchorEvent);
        }

        void Update()
        {
            if (!Enabled || MRUK.Instance == null) return;
            float now = Time.realtimeSinceStartup;
            if (_dirtyAt >= 0f)
            {
                if (now - _dirtyAt >= SettleSeconds) { _dirtyAt = -1f; Rebuild("room update"); }
                return;
            }
            if (now < _nextWatch) return;
            _nextWatch = now + WatchSeconds;
            if (_tries < MaxTries && Blocked(out string what)) Rebuild($"a wall across the doorway ({what})");
        }

        /// <summary>Rebuild the wall meshes of every EffectMesh that cuts holes, check the doorways and reconnect the ways in.</summary>
        public void Rebuild(string why)
        {
            var room = GameRoom();
            if (room == null) return;
            Hook(room);
            var meshes = new System.Collections.Generic.List<EffectMesh>();
            var foreign = new System.Collections.Generic.List<MRUKAnchor>();
            int cleared = 0;
            foreach (var em in FindObjectsByType<EffectMesh>())
            {
                // Another room's meshes (MRUK built the room the head was in at a scene load): walls, furniture, floor — gone.
                foreign.Clear();
                foreach (var kv in em.EffectMeshObjects)
                    if ((kv.Key == null || kv.Key.Room != room) && kv.Value.effectMeshGO != null) foreign.Add(kv.Key);
                foreach (var a in foreign) em.DestroyMesh(a);
                cleared += foreign.Count;
                if (em.CutHoles != 0 && (em.Labels & Walls) != 0) meshes.Add(em);
            }
            foreach (var em in meshes)
            {
                em.DestroyMesh(new LabelFilter(Walls));
                em.CreateMesh(room); // the whole-room path: walls with their door holes (only what's missing is created)
            }
            if (meshes.Count == 0 && cleared == 0) return;
            Rebuilds++;
            Physics.SyncTransforms();
            foreach (var d in VirtualDoor.All)
            {
                if (d == null) continue;
                DoorwayNav.For(d.Index)?.Reconnect();
                // The room changed under the rig (0.1.56: a re-send dropped the wide frame beside the front door 0.36 s after
                // its hinge was picked, and the kept rig never looked again): measure its hinge side again on the settled room.
                d.RechooseHinge($"walls rebuilt: {why}");
            }
            // MRUK's SceneNavigation bakes the room that updated last (and the head's room on a scene load): with two rooms that
            // can be the other one, leaving no NavMesh in the game's room and every way in off. Rebake it, once per room change.
            if (!_navRebaked && MRUK.Instance.Rooms.Count > 1 && AnyLinkOff() && FindAnyObjectByType<SceneNavigation>() is { } nav)
            {
                _navRebaked = true;
                nav.BuildSceneNavMeshForRoom(room); // → OnNavMeshInitialized → IntruderSpawner rebuilds the ways in
                Debug.Log($"[HIS] DoorHoles: NavMesh rebaked for the game's room (a way in was off; MRUK had baked another room), links {(AnyLinkOff() ? "still off" : "on")}");
            }
            if (Blocked(out string what))
            {
                _tries++;
                Debug.LogWarning($"[HIS] Doorway still blocked by {what} after rebuilding the walls ({why}, try {_tries}/{MaxTries})");
            }
            else
            {
                _tries = 0;
                Debug.Log($"[HIS] DoorHoles: walls rebuilt ({why}; {room.WallAnchors.Count} walls, {meshes.Count} meshes{(cleared > 0 ? $", {cleared} from another room cleared" : "")}), doorways clear");
            }
        }

        static bool AnyLinkOff()
        {
            foreach (var d in VirtualDoor.All) if (d != null && DoorwayNav.For(d.Index) is { } n && !n.LinkOn) return true;
            return false;
        }

        /// <summary>A wall mesh lies across a door rig's opening: <paramref name="what"/> names the wall and the door.</summary>
        public static bool Blocked(out string what)
        {
            var w = WallsAcross();
            what = w.Count > 0 ? $"{w[0].wall.name} across the {w[0].door.Label}" : null;
            return w.Count > 0;
        }

        static readonly System.Collections.Generic.List<(Collider wall, VirtualDoor door)> s_across = new();
        static readonly float[] ProbeHeights = { 0.5f, 1.0f, 1.6f };
        static readonly RaycastHit[] s_hits = new RaycastHit[8];

        /// <summary>
        /// Every wall collider across a door rig's opening (the list is reused): probed through the middle of the opening at
        /// knee, waist and head height, both ways, since a mesh collider is only hit from its front face.
        /// </summary>
        public static System.Collections.Generic.List<(Collider wall, VirtualDoor door)> WallsAcross()
        {
            s_across.Clear();
            int mask = LayerMask.GetMask("Room");
            if (mask == 0) return s_across;
            foreach (var d in VirtualDoor.All)
            {
                if (d == null) continue;
                var r = d.Opening;
                foreach (float up in ProbeHeights)
                {
                    float y = Mathf.Min(r.yMin + up, r.yMax - 0.1f);
                    var a = d.transform.TransformPoint(new Vector3(r.center.x, y, -0.3f));
                    var b = d.transform.TransformPoint(new Vector3(r.center.x, y, 0.3f));
                    for (int way = 0; way < 2; way++)
                    {
                        var from = way == 0 ? a : b; var to = way == 0 ? b : a;
                        int n = Physics.RaycastNonAlloc(from, to - from, s_hits, Vector3.Distance(a, b), mask, QueryTriggerInteraction.Ignore);
                        for (int i = 0; i < n; i++)
                        {
                            var c = s_hits[i].collider;
                            if (IsWall(c) && !s_across.Exists(f => f.wall == c)) s_across.Add((c, d));
                        }
                    }
                }
            }
            return s_across;
        }

        static bool IsWall(Collider c)
        {
            var anchor = c != null ? c.GetComponentInParent<MRUKAnchor>() : null;
            return anchor != null && anchor.HasAnyLabel(Walls);
        }

        /// <summary>
        /// A wall collider hit within a door rig's opening (2 cm in from the frame, 25 cm either side of its plane): the wall
        /// mesh wasn't cut there, and there's no real wall in a doorway. Firearm passes the round on through it.
        /// </summary>
        public static bool WallAcrossDoorway(Collider c, Vector3 point)
        {
            if (!IsWall(c)) return false;
            foreach (var d in VirtualDoor.All)
            {
                if (d == null) continue;
                var p = d.transform.InverseTransformPoint(point);
                var r = d.Opening;
                if (Mathf.Abs(p.z) < 0.25f && p.x > r.xMin + 0.02f && p.x < r.xMax - 0.02f && p.y > r.yMin + 0.02f && p.y < r.yMax - 0.02f) return true;
            }
            return false;
        }
    }
}
