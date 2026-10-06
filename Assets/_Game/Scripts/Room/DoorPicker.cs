using System.Collections.Generic;
using System.Globalization;
using HIS.Core;
using HIS.UI;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// M1a safety, step 1: the player confirms which real door the intruders come through, so nobody is lured toward a door
    /// right behind them. First load of a room: the candidate door (<see cref="RoomScanner.RankDoors"/>: prefers doors ≥2 m
    /// away) gets the door rig and a pulsing outline; stick left/right cycles when there are several doors, trigger or A/X
    /// confirms. The choice is saved per room (PlayerPrefs, room + door anchor UUIDs, door position as a fallback), so it's
    /// asked once; a hold-Menu restart (not a Sandbox death reset) asks again when the room has 2+ doors (the saved one
    /// comes first). Gameplay input stays off until the confirming trigger/button is let go (no shot on the confirm). Scenarios and the
    /// spawner wait for <see cref="RoomScanner.DoorConfirmed"/>. Headless runs (batch mode) auto-confirm the first candidate.
    /// Two doors (Brett 0.1.20: "a second door"): with 2+ doors, once the front door is confirmed a second step asks "Can
    /// intruders also come in through this door?" over the other doors — A/trigger: yes (<see cref="RoomScanner.SecondDoor"/>),
    /// B/Y: no, the front door only. Both answers are saved; a saved room that gained a door since asks just that step again.
    /// </summary>
    public class DoorPicker : MonoBehaviour
    {
        public static DoorPicker Instance { get; private set; }
        /// <summary>Tools/tests: confirm the first candidate without asking (batch mode always does).</summary>
        public static bool AutoConfirm;
        /// <summary>Tests: when auto-confirming, also take the next door as the second door (default: front door only).</summary>
        public static bool AutoSecondDoor;

        [SerializeField] Material highlightMaterial; // FX_DoorHighlight.mat (URP Unlit, amber), assigned by MainSceneBuilder
        [SerializeField] float confirmDelay = 0.4f;  // a trigger still held from something else doesn't confirm at once

        public bool IsPicking { get; private set; }
        /// <summary>Picking the second door (step 2), not the front door.</summary>
        public bool PickingSecond => IsPicking && _second;
        public MRUKAnchor Candidate => IsPicking && _order.Count > 0 ? _order[_index] : null;

        const string NoticeKey = "door", PrefPrefix = "HIS.door.";
        readonly List<MRUKAnchor> _order = new();
        int _index;
        float _openedAt;
        bool _stickLatched;
        bool _gateHeld; // picker closed, gameplay input still off until the confirm trigger/button is released
        bool _second;   // step 2: the other doors, "can intruders come in here too?"
        LineRenderer _outline;
        RoomScanner _room;

        void Awake() => Instance = this;

        void Start()
        {
            _room = RoomScanner.Instance;
            if (_room == null) return;
            _room.RoomReady += OnRoomReady;
            if (_room.IsReady) OnRoomReady(_room);
            GameSession.SessionReset += OnSessionReset;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { AutoConfirm = false; AutoSecondDoor = false; } // domain reload off

        void OnDestroy()
        {
            if (_gateHeld && InputGate.Current == InputGate.Mode.UI) InputGate.Set(InputGate.Mode.Gameplay);
            if (_room != null) _room.RoomReady -= OnRoomReady;
            GameSession.SessionReset -= OnSessionReset;
            if (Instance == this) Instance = null;
        }

        void OnRoomReady(RoomScanner room)
        {
            End();
            if (room.Doors.Count == 0) return; // RoomScanner already tells the player to scan a door
            var saved = FindSaved(room);
            if (saved != null)
            {
                room.SelectDoor(saved);
                bool decided = FindSavedSecond(room, out var second);
                if (room.Doors.Count >= 2 && !decided)
                {
                    Debug.Log("[HIS] Door picker: saved front door; asking about a second door");
                    BeginSecond();
                    return;
                }
                room.SetSecondDoor(second);
                room.ConfirmDoor();
                Debug.Log($"[HIS] Door picker: using the saved door{(second != null ? "s" : "")} for this room");
                return;
            }
            Begin();
        }

        void OnSessionReset()
        {
            // Hold-Menu restart: a chance to change the door, only when there's a choice to make. A Sandbox death reset
            // (also a SessionReset) must not ask again every time the player dies.
            if (_room != null && _room.IsReady && ReopensAfterReset(GameSession.LastResetReason, _room.Doors.Count)) Begin();
        }

        /// <summary>
        /// Does a session reset for <paramref name="reason"/> reopen the picker? A hold-Menu restart with 2+ doors, or the pause
        /// menu's "Doors: pick again" row (any door count: it was asked for).
        /// </summary>
        public static bool ReopensAfterReset(string reason, int doors) =>
            (reason == GameSession.MenuHoldReason && doors >= 2) || (reason == GameSession.PickDoorsReason && doors >= 1);

        /// <summary>Open the picker on the current primary door (auto-confirmed in batch mode).</summary>
        public void Begin()
        {
            if (_room == null || _room.Doors.Count == 0) return;
            _room.UnconfirmDoor();
            _second = false;
            _order.Clear(); _order.AddRange(_room.Doors); // primary first: the ranked default or the saved door
            _index = 0;
            _gateHeld = false;
            if (Application.isBatchMode || AutoConfirm) { Confirm(); return; }
            IsPicking = true;
            _openedAt = Time.unscaledTime;
            _stickLatched = true; // wait for the stick to be seen centred first
            ShowCandidate();
            Debug.Log($"[HIS] Door picker open: {_order.Count} door(s)");
        }

        /// <summary>Step 2 only: the front door stands; ask whether one of the other doors is a way in too.</summary>
        void BeginSecond()
        {
            _room.UnconfirmDoor();
            _second = true;
            _order.Clear();
            foreach (var d in _room.Doors) if (d != _room.PrimaryDoor) _order.Add(d);
            if (_order.Count == 0) { FinishSecond(null); return; }
            var saved = _room.SecondDoor; // back through step 2 after a restart: the current second door first
            _index = saved != null && _order.Contains(saved) ? _order.IndexOf(saved) : 0;
            if (Application.isBatchMode || AutoConfirm) { FinishSecond(AutoSecondDoor ? _order[_index] : null); return; }
            IsPicking = true;
            _openedAt = Time.unscaledTime;
            _stickLatched = true;
            ShowCandidate();
            Debug.Log($"[HIS] Door picker: second door? {_order.Count} candidate(s)");
        }

        void FinishSecond(MRUKAnchor door)
        {
            _room.SetSecondDoor(door);
            SaveSecond(_room, door);
            End();
            _room.ConfirmDoor();
        }

        void Update()
        {
            if (!IsPicking)
            {
                // The confirm press must not also fire the held gun or count as a punch/grab: hand the controllers back
                // to gameplay only once the trigger and A/X are up (HandController reads GetDown on the frame the gate opens).
                if (_gateHeld && !ConfirmHeld())
                {
                    _gateHeld = false;
                    if (InputGate.Current == InputGate.Mode.UI) InputGate.Set(InputGate.Mode.Gameplay);
                }
                return;
            }
            if (_outline != null) _outline.widthMultiplier = 0.025f + 0.012f * Mathf.Sin(Time.unscaledTime * 5f);
            if (Time.timeScale <= 0f || (GamePause.Instance != null && GamePause.Instance.IsPaused)) return; // the pause menu owns the sticks
            if (InputGate.Current == InputGate.Mode.Gameplay) InputGate.Set(InputGate.Mode.UI); // no firing/yelling while choosing

            var l = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            var r = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            float x = Mathf.Abs(l.x) > Mathf.Abs(r.x) ? l.x : r.x;
            if (Mathf.Abs(x) < 0.3f) _stickLatched = false;
            else if (!_stickLatched && Mathf.Abs(x) > 0.6f && _order.Count > 1)
            {
                _stickLatched = true;
                _index = (_index + (x > 0f ? 1 : _order.Count - 1)) % _order.Count;
                ShowCandidate();
            }
            if (Time.unscaledTime - _openedAt >= confirmDelay)
            {
                if (ConfirmPressed()) { if (_second) FinishSecond(_order[_index]); else Confirm(); }
                else if (_second && DeclinePressed()) FinishSecond(null);
            }
        }

        static bool DeclinePressed() =>
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) || // B
            OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch);   // Y

        static bool ConfirmPressed() =>
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch) ||
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) || // A
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch);   // X

        static bool ConfirmHeld() =>
            OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch) ||
            OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch) ||
            OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
            OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);

        void ShowCandidate()
        {
            var door = _order[_index];
            // The door rig moves with the choice: you see where he'll come from.
            if (_second) _room.SetSecondDoor(door); else _room.SelectDoor(door);
            DrawOutline(door);
            var cam = Camera.main;
            float dist = 0f;
            if (cam != null) { var d = door.transform.position - cam.transform.position; d.y = 0; dist = d.magnitude; }
            string close = dist < 2f ? "\n<color=#FFB23F>Close to you: a door 2 m+ away is safer</color>" : "";
            string msg = _second
                ? $"<b>Can intruders also come in through this door?</b>\n<size=70%>{(_order.Count > 1 ? $"Door {_index + 1} of {_order.Count}, " : "")}outlined, {dist:0.0} m away{close}" +
                  $"\n{(_order.Count > 1 ? "Stick left/right: other door   " : "")}A or trigger: yes   B: no, front door only</size>"
                : _order.Count == 1
                ? "<b>Intruders come through this door</b>\n<size=70%>A or trigger to confirm</size>"
                : $"<b>Which is your front door?</b>\n<size=70%>Door {_index + 1} of {_order.Count} (outlined, {dist:0.0} m away){close}" +
                  "\nThe door camera's screen goes beside it. Next you can add a second door." +
                  "\nStick left/right: other door   A or trigger: confirm</size>";
            Notice.Show(NoticeKey, msg, 60);
        }

        void Confirm()
        {
            var door = _order.Count > 0 ? _order[_index] : _room.PrimaryDoor;
            _room.SelectDoor(door);
            Save(_room, door);
            if (_room.Doors.Count >= 2) { End(); BeginSecond(); return; }
            End();
            _room.ConfirmDoor();
        }

        void End()
        {
            bool was = IsPicking;
            IsPicking = false;
            if (_outline != null) _outline.gameObject.SetActive(false);
            Notice.Hide(NoticeKey);
            if (was && InputGate.Current == InputGate.Mode.UI) _gateHeld = true; // Update gives it back once the buttons are up
        }

        void DrawOutline(MRUKAnchor door)
        {
            if (!door.PlaneRect.HasValue) return;
            if (_outline == null)
            {
                var go = new GameObject("[HIS] DoorHighlight");
                go.transform.SetParent(transform, false);
                _outline = go.AddComponent<LineRenderer>();
                _outline.useWorldSpace = true;
                _outline.loop = true;
                _outline.positionCount = 4;
                _outline.numCornerVertices = 2;
                _outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _outline.receiveShadows = false;
                _outline.sharedMaterial = highlightMaterial != null ? highlightMaterial
                    : new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.82f, 0.2f) };
            }
            _outline.gameObject.SetActive(true);
            // Just outside the casing and 3 cm into the room, so it rings the virtual door instead of cutting through it.
            var r = door.PlaneRect.Value;
            var head = Camera.main != null ? Camera.main.transform.position : door.transform.position + door.transform.forward;
            float side = Vector3.Dot(head - door.transform.position, door.transform.forward) >= 0f ? 1f : -1f;
            const float m = 0.1f;
            var t = door.transform;
            _outline.SetPositions(new[]
            {
                t.TransformPoint(new Vector3(r.xMin - m, r.yMin + 0.02f, side * 0.03f)), t.TransformPoint(new Vector3(r.xMax + m, r.yMin + 0.02f, side * 0.03f)),
                t.TransformPoint(new Vector3(r.xMax + m, r.yMax + m, side * 0.03f)), t.TransformPoint(new Vector3(r.xMin - m, r.yMax + m, side * 0.03f)),
            });
        }

        // ───────────────────────────── persistence ─────────────────────────────

        /// <summary>PlayerPrefs key for this room (its anchor UUID; name + size for a room without one).</summary>
        public static string PrefKey(MRUKRoom room)
        {
            var id = room.Anchor.Uuid;
            if (id != System.Guid.Empty) return PrefPrefix + id.ToString("N");
            var b = room.GetRoomBounds();
            return PrefPrefix + room.name + "_" + b.size.x.ToString("F1", CultureInfo.InvariantCulture) + "x" + b.size.z.ToString("F1", CultureInfo.InvariantCulture);
        }

        static string DoorId(MRUKAnchor door)
        {
            var p = door.transform.position;
            return door.Anchor.Uuid.ToString("N") + "|" + string.Join(",", p.x.ToString("F2", CultureInfo.InvariantCulture),
                p.y.ToString("F2", CultureInfo.InvariantCulture), p.z.ToString("F2", CultureInfo.InvariantCulture));
        }

        static void Save(RoomScanner room, MRUKAnchor door)
        {
            if (room.Room == null || door == null) return;
            // Another front door than the saved one: the hinge side flipped for the old one (VirtualDoor.FlipHinge) isn't this one's.
            // (The same door confirmed again keeps it.)
            if (FindSaved(room) != door) VirtualDoor.ForgetHinge(room.Room);
            PlayerPrefs.SetString(PrefKey(room.Room), DoorId(door));
            PlayerPrefs.DeleteKey(PrefKey(room.Room) + SecondSuffix); // a new front door: ask about the second one again
            PlayerPrefs.Save();
            Debug.Log($"[HIS] Door choice saved for this room ({PrefKey(room.Room)})");
        }

        const string SecondSuffix = ".second", NoSecond = "none";

        /// <summary>Save the step-2 answer: that door, or "none" (front door only).</summary>
        static void SaveSecond(RoomScanner room, MRUKAnchor door)
        {
            if (room.Room == null) return;
            PlayerPrefs.SetString(PrefKey(room.Room) + SecondSuffix, door != null ? DoorId(door) : NoSecond);
            PlayerPrefs.Save();
            Debug.Log($"[HIS] Second door {(door != null ? "saved" : "declined")} for this room");
        }

        /// <summary>
        /// The saved step-2 answer: true with <paramref name="second"/> = that door (null = "front door only"); false if never
        /// asked, or the saved door is gone and there are other doors to offer (asks again).
        /// </summary>
        public static bool FindSavedSecond(RoomScanner room, out MRUKAnchor second)
        {
            second = null;
            if (room == null || room.Room == null) return false;
            string v = PlayerPrefs.GetString(PrefKey(room.Room) + SecondSuffix, "");
            if (string.IsNullOrEmpty(v)) return false;
            if (v == NoSecond) return true;
            second = Match(room, v);
            if (second == room.PrimaryDoor) second = null;
            return second != null;
        }

        /// <summary>The saved door for this room: same anchor UUID, else the door within 0.4 m of the saved position; null if none.</summary>
        public static MRUKAnchor FindSaved(RoomScanner room)
        {
            if (room == null || room.Room == null) return null;
            string v = PlayerPrefs.GetString(PrefKey(room.Room), "");
            return string.IsNullOrEmpty(v) ? null : Match(room, v);
        }

        /// <summary>The door a saved id names: same anchor UUID, else within 0.4 m of the saved position; null if none.</summary>
        static MRUKAnchor Match(RoomScanner room, string v)
        {
            var parts = v.Split('|');
            if (System.Guid.TryParse(parts[0], out var id) && id != System.Guid.Empty)
                foreach (var d in room.Doors) if (d.Anchor.Uuid == id) return d;
            if (parts.Length > 1)
            {
                var c = parts[1].Split(',');
                if (c.Length == 3 && float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    var p = new Vector3(x, y, z);
                    foreach (var d in room.Doors) if (Vector3.Distance(d.transform.position, p) < 0.4f) return d;
                }
            }
            return null;
        }
    }
}
