using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIS.UI;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// Waits for MRUK to load the player's scanned room, catalogs the anchors we care about
    /// (doors, windows, walls, floor, furniture) and draws debug outlines on the entry points.
    /// Everything that needs "where is the door" goes through this.
    /// </summary>
    public class RoomScanner : MonoBehaviour
    {
        public static RoomScanner Instance { get; private set; }

        [Header("Door/window outlines (development builds only)")]
        [SerializeField] bool drawOutlines = true;
        [Tooltip("Debug only. The door outline lies exactly in the virtual leaf's front plane (z = 0 of the door anchor): it " +
                 "z-fights with the leaf (a flickering band just inside the frame; the eye buffer's depth is 16-bit).")]
        [SerializeField] bool drawDoorOutlines;
        [Header("Door rig materials (depth-occluded; assigned by MainSceneBuilder)")]
        [SerializeField] Material doorLeafMaterial;
        [SerializeField] Material doorTrimMaterial;
        [Tooltip("HIS/DoorPortalMask over the opening: the void (HIS/DoorVoid) draws only through it.")]
        [SerializeField] Material doorPortalMaterial;
        [SerializeField] Material doorVoidMaterial;
        [SerializeField] Material doorMetalMaterial;
        [Tooltip("How long to wait for a room scan before telling the player how to make one.")]
        [SerializeField] float roomTimeoutSeconds = 15f;
        [SerializeField] Color doorColor = new(1f, 0.25f, 0.1f);
        [SerializeField] Color windowColor = new(0.2f, 0.7f, 1f);
        [SerializeField] float lineWidth = 0.05f;

        public MRUKRoom Room { get; private set; }
        public List<MRUKAnchor> Doors { get; } = new();
        public List<MRUKAnchor> Windows { get; } = new();
        public bool IsReady => Room != null;

        /// <summary>Fired once the room is loaded and catalogued.</summary>
        public event Action<RoomScanner> RoomReady;

        /// <summary>Flat copy of walls/furniture/floor for the safety checks (<see cref="RoomGeometry"/>); null before the room.</summary>
        public RoomGeometry Geometry { get; private set; }

        /// <summary>
        /// The player has confirmed which door the intruders use (<see cref="DoorPicker"/>; restored from PlayerPrefs for a known
        /// room, auto-confirmed in headless tests). Scenarios and the Sandbox spawner wait for it.
        /// </summary>
        public bool DoorConfirmed { get; private set; }
        /// <summary>A second door intruders may use (Brett 0.1.20: "a second door"), or null: the player confirms it after
        /// the front door in the <see cref="DoorPicker"/>. Never the same anchor as <see cref="PrimaryDoor"/>.</summary>
        public MRUKAnchor SecondDoor { get; private set; }
        /// <summary>The doors intruders use: [0] the front door, [1] the second door if confirmed. Index = VirtualDoor.Index.</summary>
        public IReadOnlyList<MRUKAnchor> ActiveDoors
        {
            get
            {
                _active.Clear();
                if (PrimaryDoor != null) _active.Add(PrimaryDoor);
                if (SecondDoor != null && SecondDoor != PrimaryDoor) _active.Add(SecondDoor);
                return _active;
            }
        }
        readonly List<MRUKAnchor> _active = new();
        /// <summary>After <see cref="ConfirmDoor"/>: the primary door is final (the safe spot is picked from it).</summary>
        public event Action<RoomScanner> DoorChosen;

        readonly List<GameObject> _markers = new();

        void Awake()
        {
            Instance = this;
            if (!TryGetComponent<DoorHoles>(out _)) gameObject.AddComponent<DoorHoles>(); // door holes kept cut after room updates
        }

        void Start()
        {
            if (MRUK.Instance == null)
            {
                Debug.LogError("[HIS] RoomScanner: no MRUK instance in scene");
                return;
            }
            MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
            StartCoroutine(WatchForRoom());
        }

        const string NoticeKey = "room";

        // MRUK queries the scene once, at startup. Launched while the headset is off the head (adb deploy, a remote
        // launch, waking from sleep) tracking isn't up yet, so that query finds 0 anchors and never tries again — Brett
        // 0.1.10: "no spatial data found". So on device: while there's no room and the headset is on, re-query every few
        // seconds. Only after roomTimeoutSeconds of WORN time with no room do we tell the player to make a scan.
        IEnumerator WatchForRoom()
        {
            float wornFor = 0f, nextTry = 0f;
            int tries = 0;
            bool told = false;
            while (Room == null)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (Room != null) break;
                bool worn = Application.isEditor || OVRManager.instance == null || OVRManager.instance.isUserPresent;
                if (!worn) continue;
                wornFor += 1f;
                if (!Application.isEditor && wornFor >= nextTry && tries < 30)
                {
                    tries++;
                    nextTry = wornFor + 3f;
                    Debug.Log($"[HIS] RoomScanner: no room yet, re-querying the scene (try {tries})");
                    _ = MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false);
                }
                if (!told && wornFor >= roomTimeoutSeconds)
                {
                    told = true;
                    Debug.LogWarning("[HIS] RoomScanner: no room after timeout");
                    Notice.Show(NoticeKey,
                        "<b>No room scan found</b>\n<size=70%>Allow spatial data when asked, then open Quest Settings > Physical Space >\nSpace Setup, scan this room and mark at least one door. Then reopen the game.</size>", 50);
                }
            }
            if (told) Notice.Hide(NoticeKey); // a retry found it after all
        }

        void OnSceneLoaded()
        {
            // A re-sent scene (the headset re-sends it on resume) keeps the game's room while it still exists: by then the head
            // may be in the next room (Brett's front door opens onto another scanned room, whose wall stands 5 cm behind it).
            Room = Room != null && MRUK.Instance.Rooms.Contains(Room) ? Room : MRUK.Instance.GetCurrentRoom();
            if (Room == null)
            {
                Debug.LogWarning("[HIS] RoomScanner: scene loaded but no current room");
                return;
            }

            Doors.Clear();
            Windows.Clear();
            foreach (var m in _markers) Destroy(m);
            _markers.Clear();

            foreach (var a in Room.Anchors)
            {
                string size = a.PlaneRect.HasValue ? $"plane {a.PlaneRect.Value.size}" :
                              a.VolumeBounds.HasValue ? $"volume {a.VolumeBounds.Value.size}" : "no-geom";
                Debug.Log($"[HIS] anchor {a.Label} @ {a.transform.position} ({size})");

                if (a.Label == MRUKAnchor.SceneLabels.DOOR_FRAME) Doors.Add(a);
                else if (a.Label == MRUKAnchor.SceneLabels.WINDOW_FRAME) Windows.Add(a);
            }

            // Default "front door" (the first candidate the door picker offers): the nearest door at least 2 m from where the
            // player stands, else the farthest one — never the door right behind the player if there's a choice (M1a safety).
            var head = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var ranked = RankDoors(Doors.ConvertAll(d => d.transform.position), head);
            var sorted = new List<MRUKAnchor>(Doors.Count);
            foreach (int i in ranked) sorted.Add(Doors[i]);
            Doors.Clear(); Doors.AddRange(sorted);
            // A known room: its saved doors first, so the rigs are built once, on the right frames, and a room the device
            // re-sends (it does on resume) keeps its rigs, door state and all (0.1.34 built the front rig twice per load).
            // The door picker still confirms them on RoomReady.
            var saved = DoorPicker.FindSaved(this);
            if (saved != null) { Doors.Remove(saved); Doors.Insert(0, saved); }
            DoorConfirmed = false;
            SecondDoor = saved != null && DoorPicker.FindSavedSecond(this, out var second) ? second : null;
            Geometry = RoomGeometry.FromRoom(Room);

            Debug.Log($"[HIS] Room ready: {Room.Anchors.Count} anchors, {Doors.Count} door(s), {Windows.Count} window(s), " +
                      $"{Room.WallAnchors.Count} wall(s), floors={Room.FloorAnchors.Count}");

            Notice.Hide(NoticeKey);
            if (Doors.Count == 0)
                Notice.Show(NoticeKey,
                    "<b>No door in your room scan</b>\n<size=70%>Intruders come in through a scanned door. Re-run Space Setup\n(Quest Settings > Physical Space), add your door, then reopen the game.</size>", 50);

            SaveRoomForEditor();

            if (drawOutlines && Debug.isDebugBuild) // the door rig is the real visual now; outlines are a dev aid
            {
                if (drawDoorOutlines) foreach (var d in Doors) _markers.Add(MakeOutline(d, doorColor)); // the door rig shows the fit
                foreach (var w in Windows) _markers.Add(MakeOutline(w, windowColor));
            }

            BuildDoorRig(roomChanged: true);

            RoomReady?.Invoke(this);
        }

        /// <summary>
        /// Candidate order for the intruder door: doors at least <paramref name="preferMin"/> m (flat) from the player first,
        /// nearest of those first; then the closer ones, farthest first. Returns indices into <paramref name="doors"/>.
        /// </summary>
        public static List<int> RankDoors(IList<Vector3> doors, Vector3 head, float preferMin = 2f)
        {
            float Flat(int i) { var d = doors[i] - head; d.y = 0; return d.magnitude; }
            var idx = new List<int>();
            for (int i = 0; i < doors.Count; i++) idx.Add(i);
            idx.Sort((a, b) =>
            {
                float da = Flat(a), db = Flat(b);
                bool fa = da >= preferMin, fb = db >= preferMin;
                if (fa != fb) return fa ? -1 : 1;
                return fa ? da.CompareTo(db) : db.CompareTo(da);
            });
            return idx;
        }

        /// <summary>Make <paramref name="door"/> the primary door (first in <see cref="Doors"/>) and move the door rig onto it.</summary>
        public void SelectDoor(MRUKAnchor door)
        {
            if (door == null || !Doors.Contains(door)) return;
            if (Doors[0] != door) { Doors.Remove(door); Doors.Insert(0, door); }
            if (SecondDoor == door) SecondDoor = null;
            BuildDoorRig();
        }

        /// <summary>Set (or clear, null) the second door and build/remove its door rig.</summary>
        public void SetSecondDoor(MRUKAnchor door)
        {
            if (door != null && (!Doors.Contains(door) || door == PrimaryDoor)) door = null;
            SecondDoor = door;
            BuildDoorRig();
            if (door != null) Debug.Log($"[HIS] Second door: {door.transform.position}");
        }

        public void ConfirmDoor()
        {
            if (PrimaryDoor == null) return;
            DoorConfirmed = true;
            Debug.Log($"[HIS] Intruder door confirmed: {PrimaryDoor.transform.position} (of {Doors.Count})" +
                      (SecondDoor != null ? $", second door {SecondDoor.transform.position}" : ""));
            DoorChosen?.Invoke(this);
        }

        /// <summary>Hold scenarios/spawns until the door is confirmed again (the picker is open).</summary>
        public void UnconfirmDoor() => DoorConfirmed = false;

        /// <summary>
        /// The virtual doors (dark box, leaf): index 0 on the primary door frame, index 1 on the second door if there is one.
        /// A rig already on the right frame stays (its state, holes and blood with it); one on the wrong frame is rebuilt.
        /// <paramref name="roomChanged"/>: a (re-)loaded room, not a door pick — a rig kept through it measures its hinge again.
        /// </summary>
        void BuildDoorRig(bool roomChanged = false)
        {
            var want = ActiveDoors;
            for (int i = VirtualDoor.All.Count - 1; i >= 0; i--)
            {
                var rig = VirtualDoor.All[i];
                if (rig == null) { VirtualDoor.All.RemoveAt(i); continue; }
                bool keep = rig.Index < want.Count && want[rig.Index] != null && rig.transform.parent == want[rig.Index].transform &&
                            SameOpening(rig.Opening, want[rig.Index].PlaneRect);
                if (!keep) { VirtualDoor.All.RemoveAt(i); Destroy(rig.gameObject); }
                // Kept through a re-sent room: the walls around it may have changed (0.1.56: the wide frame beside the front
                // door was gone 0.36 s after its hinge was picked), so it measures its hinge side again once the room settles.
                else if (roomChanged) rig.RechooseHinge("room re-sent");
            }
            for (int i = 0; i < want.Count; i++)
            {
                var anchor = want[i];
                if (anchor == null || !anchor.PlaneRect.HasValue) continue;
                if (VirtualDoor.All.Exists(r => r != null && r.Index == i)) continue;
                var existing = anchor.transform.Find("VirtualDoor"); if (existing != null) Destroy(existing.gameObject);
                // Inward = from the door toward the room centre.
                var head = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                var floorC = Room.FloorAnchors is { Count: > 0 } f ? f[0].GetAnchorCenter() : head;
                var inward = floorC - anchor.transform.position; inward.y = 0; inward.Normalize();
                VirtualDoor.Build(anchor.transform, anchor.PlaneRect.Value, inward, DoorMaterials(), i);
                Debug.Log($"[HIS] Door rig built on the {(i == 0 ? "primary" : "second")} door frame ({anchor.PlaneRect.Value.width:F2} x {anchor.PlaneRect.Value.height:F2} m)");
            }
        }

        /// <summary>The frame's rect still matches the rig's opening to 2 cm (a re-sent room nudges it; a re-scan may not).</summary>
        static bool SameOpening(Rect rig, Rect? frame) =>
            frame.HasValue && Vector2.Distance(rig.min, frame.Value.min) < 0.02f && Vector2.Distance(rig.max, frame.Value.max) < 0.02f;

        /// <summary>
        /// Development builds on the headset write the real room to persistentDataPath/room.json so it can be pulled with adb
        /// (scripts/pull-room.sh) and used as the Editor's MRUK room — door fitting then runs against Brett's actual door.
        /// </summary>
        void SaveRoomForEditor()
        {
            if (!Debug.isDebugBuild || Application.isEditor) return;
            try
            {
                var json = MRUK.Instance.SaveSceneToJsonString(false);
                var path = Path.Combine(Application.persistentDataPath, "room.json");
                File.WriteAllText(path, json);
                Debug.Log($"[HIS] Room saved to {path} ({json.Length} chars)");
            }
            catch (Exception e) { Debug.LogWarning($"[HIS] Could not save room JSON: {e.Message}"); }
        }

        VirtualDoor.Materials DoorMaterials()
        {
            Material Make(Material m, Color c, float metal, float gloss, float bias)
            {
                if (m != null) return m;
                var shader = Shader.Find("HIS/OcclusionLitPBR") ?? Shader.Find("Universal Render Pipeline/Lit");
                var mat = new Material(shader) { color = c };
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metal);
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", gloss);
                if (mat.HasProperty("_EnvironmentDepthBias")) mat.SetFloat("_EnvironmentDepthBias", bias);
                return mat;
            }
            return new VirtualDoor.Materials
            {
                Leaf = Make(doorLeafMaterial, new Color(0.86f, 0.84f, 0.8f), 0f, 0.35f, 0.06f),
                Trim = Make(doorTrimMaterial, new Color(0.9f, 0.89f, 0.86f), 0f, 0.35f, 0.06f),
                Void = doorVoidMaterial != null ? doorVoidMaterial
                     : Shader.Find("HIS/DoorVoid") is { } voidShader ? new Material(voidShader) { color = new Color(0.012f, 0.012f, 0.016f) }
                     : Make(null, new Color(0.012f, 0.012f, 0.016f), 0f, 0f, 0f),
                // The void's stencil mask; without it a stencil-tested void would never draw (no mask → keep the old look).
                Portal = doorPortalMaterial != null ? doorPortalMaterial
                       : Shader.Find("HIS/DoorPortalMask") is { } maskShader ? new Material(maskShader) : null,
                Metal = Make(doorMetalMaterial, new Color(0.72f, 0.7f, 0.66f), 0.9f, 0.6f, 0.06f),
            };
        }

        /// <summary>Pounding/kicking on the door (the virtual door rig judders; the debug outline too, if drawn).</summary>
        public void ShakeDoor(float strength)
        {
            if (PrimaryDoor == null) return;
            VirtualDoor.Instance?.Pound(strength);
            var outline = PrimaryDoor.transform.Find("Outline_DOOR_FRAME");
            if (outline != null) StartCoroutine(Judder(outline, strength));
        }

        System.Collections.IEnumerator Judder(Transform t, float strength)
        {
            var rest = t.localPosition; float time = 0, dur = 0.35f;
            while (time < dur)
            {
                time += Time.deltaTime; float k = 1f - time / dur;
                t.localPosition = rest + new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f), 0) * (0.02f * strength * k);
                yield return null;
            }
            t.localPosition = rest;
        }

        /// <summary>Primary entry point for the intruder, or null if the room has no scanned door.</summary>
        public MRUKAnchor PrimaryDoor => Doors.FirstOrDefault();

        GameObject MakeOutline(MRUKAnchor anchor, Color color)
        {
            var go = new GameObject($"Outline_{anchor.Label}");
            go.transform.SetParent(anchor.transform, false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.widthMultiplier = lineWidth;
            lr.material = LineMaterial(color);
            lr.startColor = lr.endColor = color;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            if (anchor.PlaneRect.HasValue)
            {
                var r = anchor.PlaneRect.Value;
                lr.positionCount = 4;
                lr.SetPositions(new[]
                {
                    new Vector3(r.xMin, r.yMin, 0), new Vector3(r.xMax, r.yMin, 0),
                    new Vector3(r.xMax, r.yMax, 0), new Vector3(r.xMin, r.yMax, 0),
                });
            }
            else
            {
                lr.positionCount = 0;
            }
            return go;
        }

        readonly Dictionary<Color, Material> _lineMats = new();
        Material LineMaterial(Color color)
        {
            if (_lineMats.TryGetValue(color, out var m)) return m;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            m = new Material(shader);
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            _lineMats[color] = m;
            return m;
        }

        /// <summary>Distance from the player's head to the primary door, or -1.</summary>
        public float DoorDistance => PrimaryDoor != null && Camera.main != null ? Vector3.Distance(Camera.main.transform.position, PrimaryDoor.transform.position) : -1f;
    }
}
