using System.Globalization;
using HIS.Core;
using HIS.Room;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// Move the door-camera screen yourself (Brett 0.1.21: "make it like the menu in guardians of realms white bar on the
    /// bottom point at it and you can move the screen wherever you want it" — the auto spot had put it in front of a lamp).
    /// A white bar under the screen: point an EMPTY hand at it (a thin line shows you're on it), hold the trigger and the
    /// screen follows your pointing, turned to face you; the stick pushes it away or pulls it closer; let go and it stays.
    /// The spot is saved per room, relative to the front door, and wins over the automatic placement from then on.
    /// A hand holding a weapon never grabs it (its trigger fires), and nothing happens while the game is paused.
    /// </summary>
    [RequireComponent(typeof(SecurityMonitor))]
    public class MonitorMover : MonoBehaviour
    {
        const float BarW = 0.24f, BarH = 0.016f, BarGap = 0.035f, HitHalfW = 0.2f, HitHalfH = 0.05f;
        const float MinDist = 0.4f, MaxDist = 10f, PushSpeed = 2f;
        const string PrefSuffix = ".monitor";

        SecurityMonitor _monitor;
        Transform _bar;
        Material _barMat;
        LineRenderer _line;
        HandController _dragHand;
        float _grabDist;
        Vector3 _grabOffset;

        /// <summary>The player has placed it by hand in this room (the automatic placement leaves it alone).</summary>
        public bool UserPlaced { get; private set; }
        public bool Dragging => _dragHand != null;

        void Awake() => _monitor = GetComponent<SecurityMonitor>();

        /// <summary>The bar sits under the bezel of the current size (the monitor rebuilds its visual when the size changes).</summary>
        public void Attach(Transform visual, Vector2 bezel)
        {
            if (_barMat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                _barMat = sh != null ? new Material(sh) : null;
                SetBarColor(Idle);
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "MoveBar";
            Destroy(go.GetComponent<Collider>());
            _bar = go.transform;
            _bar.SetParent(visual, false);
            // The screen faces -Z (its front is toward the room): the bar hangs under the bezel, a little proud of it.
            _bar.localPosition = new Vector3(0f, -(bezel.y * 0.5f + BarGap), -0.02f);
            _bar.localScale = new Vector3(BarW, BarH, 0.008f);
            var r = go.GetComponent<Renderer>();
            if (_barMat != null) r.sharedMaterial = _barMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        }

        static readonly Color Idle = new(0.92f, 0.93f, 0.95f), Hover = Color.white, Held = new(0.55f, 0.8f, 1f);
        void SetBarColor(Color c)
        {
            if (_barMat == null) return;
            _barMat.color = c;
            if (_barMat.HasProperty("_BaseColor")) _barMat.SetColor("_BaseColor", c);
        }

        void OnDestroy() { if (_barMat != null) Destroy(_barMat); }

        void Update()
        {
            if (_bar == null || !_bar.gameObject.activeInHierarchy) { ShowLine(false); return; }
            if (!InputGate.GameplayActive || Time.deltaTime <= 0f) { if (Dragging) Drop(); ShowLine(false); return; }
            var cs = CommandSystem.Instance;
            if (cs == null) return;
            if (Dragging) { Drag(); return; }

            HandController hover = null; Vector3 hit = default;
            foreach (var h in new[] { cs.Right, cs.Left })
                if (h != null && h.Held == null && PointsAtBar(h, out hit)) { hover = h; break; }
            SetBarColor(hover != null ? Hover : Idle);
            _bar.localScale = new Vector3(BarW * (hover != null ? 1.15f : 1f), BarH * (hover != null ? 1.4f : 1f), 0.008f);
            if (hover == null) { ShowLine(false); return; }
            ShowLine(true, hover.transform.position, hit);
            if (hover.TriggerDown)
            {
                _dragHand = hover;
                _grabDist = Vector3.Distance(hover.transform.position, hit);
                _grabOffset = transform.position - hit;
                hover.HapticLight();
                SetBarColor(Held);
            }
        }

        /// <summary>
        /// Pointing at the monitor from anywhere in the room (Brett 0.1.22: "use the line to grab the screen from farther
        /// away"): the whole screen and its bar are the target, up to 10 m, with a margin that grows with distance (~3°),
        /// so a far screen is as easy to hit as a near one. The line then runs to where you're pointing.
        /// </summary>
        bool PointsAtBar(HandController h, out Vector3 hit)
        {
            hit = default;
            var o = h.transform.position; var d = h.transform.forward;
            var lo = _bar.parent.InverseTransformPoint(o); var ld = _bar.parent.InverseTransformDirection(d);
            float z = _bar.localPosition.z;
            if (Mathf.Abs(ld.z) < 1e-4f) return false;
            float t = (z - lo.z) / ld.z;
            if (t <= 0f || t > MaxReach) return false;
            var p = lo + ld * t;
            float margin = Mathf.Max(0.04f, t * 0.05f);                 // ~3° either way
            var size = _monitor != null ? _monitor.BezelSize : new Vector2(0.74f, 0.44f);
            float top = size.y * 0.5f, bottom = _bar.localPosition.y - HitHalfH;
            if (Mathf.Abs(p.x) > size.x * 0.5f + margin || p.y > top + margin || p.y < bottom - margin) return false;
            // Clamp the end of the line onto the monitor so it visibly lands on it.
            p.x = Mathf.Clamp(p.x, -size.x * 0.5f, size.x * 0.5f); p.y = Mathf.Clamp(p.y, bottom, top);
            hit = _bar.parent.TransformPoint(p);
            return true;
        }

        const float MaxReach = 10f;

        void Drag()
        {
            var h = _dragHand;
            if (h == null || h.Held != null || h.Trigger < 0.35f) { Drop(); return; }
            float stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, h.Controller).y;
            if (Mathf.Abs(stick) > 0.3f) _grabDist = Mathf.Clamp(_grabDist + stick * PushSpeed * Time.deltaTime, MinDist, MaxDist);
            var point = h.transform.position + h.transform.forward * _grabDist;
            var want = point + _grabOffset;
            var head = Camera.main != null ? Camera.main.transform.position : want - Vector3.forward;
            var away = want - head; away.y = 0f;                               // upright, facing you (its forward points away)
            var rot = away.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(away.normalized, Vector3.up) : transform.rotation;
            float k = 1f - Mathf.Exp(-18f * Time.deltaTime);
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, want, k), Quaternion.Slerp(transform.rotation, rot, k));
            ShowLine(true, h.transform.position, _bar.position);
        }

        void Drop()
        {
            if (_dragHand != null) _dragHand.HapticLight();
            _dragHand = null;
            SetBarColor(Idle);
            ShowLine(false);
            UserPlaced = true;
            Save();
            Debug.Log($"[HIS] Security monitor moved by hand to {transform.position:F2}");
        }

        void ShowLine(bool on, Vector3 a = default, Vector3 b = default)
        {
            if (!on) { if (_line != null && _line.enabled) _line.enabled = false; return; }
            if (_line == null)
            {
                var go = new GameObject("MoveBarLine");
                go.transform.SetParent(transform, false);
                _line = go.AddComponent<LineRenderer>();
                _line.useWorldSpace = true; _line.positionCount = 2; _line.widthMultiplier = 0.004f;
                _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; _line.receiveShadows = false;
                _line.sharedMaterial = _barMat;
            }
            _line.enabled = true;
            _line.SetPosition(0, a); _line.SetPosition(1, b);
        }

        // ───────────────────────────── saved per room, relative to the front door ─────────────────────────────

        static string Key()
        {
            var room = RoomScanner.Instance != null ? RoomScanner.Instance.Room : null;
            return room != null ? DoorPicker.PrefKey(room) + PrefSuffix : null;
        }

        /// <summary>
        /// The frame the spot is kept in: the front door's scanned anchor (the rig's parent). VirtualDoor.FitToRoom moves the rig
        /// itself flush with the wall's face, which would bring a saved spot back that far off the wall; the anchor stays put, and
        /// spots saved before (the rig then sat on it) land where they were.
        /// </summary>
        static Transform Frame(VirtualDoor door) => door.transform.parent != null ? door.transform.parent : door.transform;

        void Save()
        {
            var door = _monitor != null ? _monitor.FrontDoor : null;
            string key = Key();
            if (door == null || key == null) return;
            var frame = Frame(door);
            var lp = frame.InverseTransformPoint(transform.position);
            var lf = frame.InverseTransformDirection(transform.forward);
            string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
            PlayerPrefs.SetString(key, string.Join(",", F(lp.x), F(lp.y), F(lp.z), F(lf.x), F(lf.y), F(lf.z)));
            PlayerPrefs.Save();
        }

        /// <summary>The player's own spot for this room, if they've placed it before (SecurityMonitor.Place asks first).</summary>
        public bool TryRestore(VirtualDoor front)
        {
            string key = Key();
            if (front == null || key == null) return false;
            var parts = PlayerPrefs.GetString(key, "").Split(',');
            if (parts.Length != 6) return false;
            var v = new float[6];
            for (int i = 0; i < 6; i++) if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            var frame = Frame(front);
            var pos = frame.TransformPoint(new Vector3(v[0], v[1], v[2]));
            var fwd = frame.TransformDirection(new Vector3(v[3], v[4], v[5])); fwd.y = 0f;
            transform.SetPositionAndRotation(pos, fwd.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : transform.rotation);
            UserPlaced = true;
            return true;
        }

        /// <summary>Tests: forget the saved spot for this room.</summary>
        public static void ForgetForRoom() { string key = Key(); if (key != null) { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); } }
    }
}
