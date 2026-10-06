using System;
using System.Collections.Generic;
using System.Globalization;
using HIS.Core;
using HIS.Room;
using HIS.UI;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// The wall screen beside the front door showing the door cameras at night (Brett 0.1.20: "some investigation maybe you
    /// can see them in a security monitor at the door … so that each situation doesn't end within 10 seconds").
    /// A 32″ screen (0.70 × 0.39 m picture, research/21 §2.1) — or a 17″ one where the wall has no room — centre 1.45 m above
    /// the floor, 2 cm off the wall, on the LATCH side of the front door (the leaf swings open on the hinge side), checked
    /// against the scanned walls, furniture, windows and every door's swing; else the hinge side past the swing, else the
    /// nearest free wall spot within 2 m.
    /// Picture: HIS/SecurityScreen (grayscale IR look, per-feed fisheye, smart-zoom crop). Overlay (TextMeshPro, XR-safe
    /// material, ≥ 14 mm on the big screen): the feed's label, REC with a blinking red dot, the date and a live 12-hour clock.
    /// With two doors the door with the latest motion is big and the other is a
    /// labelled picture-in-picture (40% wide); motion flashes the label and border (2 Hz) and plays "cam.motion" (the label
    /// reads MOTION, or VEHICLE for a Marshal night's hitman car pulling into the picture: SecurityCamera.VehicleInView).
    /// A ring at either door (<see cref="VirtualDoor.DoorbellRung"/>) plays the doorbell chime inside by the front door and
    /// brings that door's feed up big. The clock reads a Marshal night's story time while one runs (HIS.Scenario.StoryClock).
    /// A big line over the picture (<see cref="ShowBigText"/>) is unused since the handover (2026-10-04: the weapons check
    /// speaks over Ortiz's radio and labels the things in the room instead); its object stays inactive, the tests read
    /// <see cref="BigText"/> to see the screen stays clear.
    /// </summary>
    public class SecurityMonitor : MonoBehaviour
    {
        public static SecurityMonitor Instance { get; private set; }

        public const float ScreenCentreHeight = 1.45f;
        /// <summary>Gap between the door opening's edge and the monitor's near edge.</summary>
        public const float EdgeGap = 0.15f;
        const float BackZ = -0.002f, FrontZ = -0.022f, ScreenZ = -0.0205f, TextZ = -0.026f;
        const float FlashSeconds = 3f, FlashHz = 2f; // our flashing rule is under 3 Hz

        /// <summary>A bezel and its picture (m).</summary>
        public readonly struct Size
        {
            public readonly float W, H, ScreenW, ScreenH;
            public Size(float w, float h, float sw, float sh) { W = w; H = h; ScreenW = sw; ScreenH = sh; }
        }
        public static readonly Size Big = new(0.74f, 0.44f, 0.70f, 0.394f);     // 32″
        public static readonly Size Small = new(0.40f, 0.26f, 0.372f, 0.21f);   // 17″, where the wall is short

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; TestSeen = null; }

        /// <summary>Tests: force <see cref="Seen"/> (batch mode draws no headset view).</summary>
        public static bool? TestSeen;

        /// <summary>The front door the monitor hangs by.</summary>
        public VirtualDoor FrontDoor { get; private set; }
        public VirtualDoor MainDoor => _main != null ? _main.Door : null;
        public VirtualDoor InsetDoor => _inset != null ? _inset.Door : null;
        public bool Placed { get; private set; }
        /// <summary>How the spot was found: "latch side", "hinge side", "nearest free wall", "unchecked".</summary>
        public string Placement { get; private set; } = "none";
        /// <summary>The bezel's outer size (m) as placed: <see cref="Big"/> or <see cref="Small"/>.</summary>
        public Vector2 BezelSize => new(_size.W, _size.H);
        public Vector3 ScreenCenter => _screen != null ? _screen.position : transform.position;
        /// <summary>Unit vector out of the screen, into the room.</summary>
        public Vector3 Facing => -transform.forward;
        public string MainLabelText => _mainLabel != null ? _mainLabel.text : null;
        public string InsetLabelText => _insetLabel != null && _insetLabel.gameObject.activeSelf ? _insetLabel.text : null;
        public string ClockText => _clock != null ? _clock.text : null;
        public Material ScreenMaterial => _screenMat;
        /// <summary>The player (any on-screen camera) saw the screen last frame.</summary>
        public bool Seen => TestSeen ?? (_screenRenderer != null && _screenRenderer.isVisible);

        Size _size = Big;
        Transform _visual, _screen;
        MeshRenderer _screenRenderer;
        Material _screenMat, _bezelMat;
        Mesh _screenMesh, _bezelMesh;
        TextMeshPro _mainLabel, _insetLabel, _rec, _clock, _date;
        SecurityCamera _main, _inset;
        Rect _insetUv;
        readonly Dictionary<SecurityCamera, float> _flashUntil = new();
        float _nextCheck, _frontSeenAt = -1f;
        Vector3 _placedCenter, _placedHinge;
        int _lastSecond = -1;
        string _mainText, _insetText;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            var src = Resources.Load<Material>("Security/SecurityScreen");
            _screenMat = src != null ? new Material(src) : Shader.Find("HIS/SecurityScreen") is { } s ? new Material(s) : null;
            if (_screenMat == null) Debug.LogError("[HIS] SecurityMonitor: no HIS/SecurityScreen material");
            else
            {
                // The look (also the shader's defaults; set here so an old material asset can't drift from the code).
                _screenMat.SetFloat("_Exposure", 1.4f); _screenMat.SetFloat("_Grain", 0.08f);
                _screenMat.SetFloat("_Vignette", 0.9f); _screenMat.SetFloat("_Scan", 0.05f); _screenMat.SetFloat("_Bloom", 0.9f);
                _screenMat.SetColor("_Tint", new Color(0.94f, 1f, 0.97f));
                // Drawn like a menu, never cut out by real things in front of it (Brett 0.1.21: a lamp "shines through"):
                // a 10 m bias puts the real room always behind it for the Depth API.
                _screenMat.SetFloat("_EnvironmentDepthBias", 10f);
            }
            _bezelMat = Resources.Load<Material>("Security/SecurityBezel");
            if (_bezelMat == null)
            {
                var sh = Shader.Find("HIS/OcclusionLitPBR") ?? Shader.Find("Universal Render Pipeline/Lit");
                _bezelMat = new Material(sh) { color = new Color(0.035f, 0.036f, 0.04f) };
            }
            else _bezelMat = new Material(_bezelMat);
            if (_bezelMat.HasProperty("_EnvironmentDepthBias")) _bezelMat.SetFloat("_EnvironmentDepthBias", 10f);
            _mover = gameObject.AddComponent<MonitorMover>();   // the white bar: point, hold the trigger, move it (Brett 0.1.21)
            BuildVisual(Big);
            _visual.gameObject.SetActive(false);
            SecurityCamera.AnyMotion += OnMotion;
            VirtualDoor.DoorbellRung += RingDoorbell; // the intruder's door.RingDoorbell() lands here
        }

        void OnDestroy()
        {
            SecurityCamera.AnyMotion -= OnMotion;
            VirtualDoor.DoorbellRung -= RingDoorbell;
            if (Instance == this) Instance = null;
            if (_screenMat != null) Destroy(_screenMat);
            if (_screenMesh != null) Destroy(_screenMesh);
            if (_bezelMesh != null) Destroy(_bezelMesh);
        }

        // ───────────────────────────── public API ─────────────────────────────

        /// <summary>
        /// Someone rang at <paramref name="door"/> (also reached through <see cref="VirtualDoor.RingDoorbell"/> →
        /// <see cref="VirtualDoor.DoorbellRung"/>): the two-tone chime ("door.bell") inside by the front door, whichever door
        /// it was, and that door's feed comes up big.
        /// </summary>
        public void RingDoorbell(VirtualDoor door)
        {
            var front = FrontOrFirst();
            if (front != null && Sfx.Has("door.bell"))
            {
                var at = front.Center + Flat(front.Inward) * 0.4f;
                at.y = FloorY(front) + 2.1f; // the chime box high on the wall inside
                Sfx.Play("door.bell", at, 1f, 0f);
            }
            Debug.Log($"[HIS] SecurityMonitor: doorbell at {(door != null ? door.Label : "?")}");
            if (door != null) ShowFeed(door);
        }

        /// <summary>Show <paramref name="door"/>'s camera big (the other one, if any, goes to the inset).</summary>
        public void ShowFeed(VirtualDoor door)
        {
            var cam = SecurityCamera.For(door);
            if (cam == null || cam == _main) return;
            _main = cam;
            Rebind(force: true);
        }

        /// <summary>The big line on the screen now (<see cref="ShowBigText"/>), or null.</summary>
        public string BigText => _bigString;

        /// <summary>
        /// One line of big text over the lower middle of the picture, on a dark plate fitted to it (the weapons check's until
        /// 0.1.56; retired by the handover, nothing calls it now: the steps are Ortiz on the radio and labels at the things,
        /// Scenario/MarshalBriefing + UI/WorldLabel). Auto-sized to the screen (a long line wraps to two or three); kept across a re-placement or a
        /// 32″/17″ rebuild, and shown once the screen is up if it isn't yet. Null or empty takes it down. Built only on a change.
        /// </summary>
        public void ShowBigText(string text)
        {
            if (string.IsNullOrEmpty(text)) text = null;
            if (text == _bigString) return;
            _bigString = text;
            ApplyBigText();
        }

        /// <summary>Take the big line down: the whole picture again.</summary>
        public void ClearBigText() => ShowBigText(null);

        // ───────────────────────────── per frame ─────────────────────────────

        void Update()
        {
            float now = Time.unscaledTime;
            if (now >= _nextCheck)
            {
                _nextCheck = now + 0.5f;
                CheckPlacement(now);
                Rebind(force: false);
            }
            if (!Placed || _screenMat == null) return;

            // Clock / date: rebuilt once a second (TMP regenerates its mesh on every text change). A Marshal night's story time
            // (1:12 AM → the 2:00 AM relief, research/27 §1 / §6 step 8) while one runs; else the real time, as before.
            var t = HIS.Scenario.StoryClock.Now ?? DateTime.Now;
            if (t.Second != _lastSecond)
            {
                _lastSecond = t.Second;
                _clock.text = t.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture);
                _date.text = t.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            }
            bool recOn = (now % 1f) < 0.6f;
            _screenMat.SetVector("_Rec", new Vector4(0.962f, 0.918f, 0.011f, recOn ? 1f : 0f));

            bool flashMain = Flashing(_main, now), flashInset = Flashing(_inset, now);
            float pulse = (now * FlashHz % 1f) < 0.5f ? 1f : 0f;
            _screenMat.SetVector("_Flash", new Vector4(flashMain ? pulse : 0f, flashInset ? pulse : 0f, 0f, 0f));
            SetLabel(_mainLabel, _mainText, flashMain, pulse > 0.5f, VehicleAlert(_main));
            SetLabel(_insetLabel, _insetText, flashInset, pulse > 0.5f, VehicleAlert(_inset));
            // Smart zoom crops and each camera's fisheye.
            _screenMat.SetVector("_MainCrop", _main != null ? _main.ZoomRect : new Vector4(0f, 0f, 1f, 1f));
            _screenMat.SetVector("_InsetCrop", _inset != null ? _inset.ZoomRect : new Vector4(0f, 0f, 1f, 1f));
            _screenMat.SetVector("_Barrels", new Vector4(_main != null && _main.Porch != null ? _main.Porch.Barrel : 0.2f,
                                                         _inset != null && _inset.Porch != null ? _inset.Porch.Barrel : 0.2f, 0f, 0f));

            // Frame rates: the big feed at full rate and the inset at 8 fps while the player can see the screen; nothing
            // renders while nobody looks (turning back to it, the old picture shows for one frame, then it's live again).
            // Each camera also holds its last frame while nobody's in its view (SecurityCamera.Live).
            bool seen = Seen;
            foreach (var c in SecurityCamera.All)
            {
                if (c == null) continue;
                c.TargetFps = !seen ? 0f : c == _main ? SecurityCamera.Fps : c == _inset ? 8f : 0f;
            }
        }

        bool Flashing(SecurityCamera c, float now) => c != null && _flashUntil.TryGetValue(c, out var until) && now < until;

        static readonly Color LabelWhite = new(0.93f, 0.95f, 0.93f), LabelRed = new(1f, 0.28f, 0.22f);

        /// <summary>This camera's flashing alert is a vehicle's (VEHICLE), not a man's (MOTION).</summary>
        bool VehicleAlert(SecurityCamera c) => c != null && _flashVehicle.TryGetValue(c, out var v) && v;
        readonly Dictionary<SecurityCamera, bool> _flashVehicle = new();

        static void SetLabel(TextMeshPro tmp, string text, bool flashing, bool on, bool vehicle = false)
        {
            if (tmp == null || text == null) return;
            string want = flashing ? text + (vehicle ? "  <size=80%>VEHICLE</size>" : "  <size=80%>MOTION</size>") : text;
            if (tmp.text != want) tmp.text = want;
            var col = flashing && on ? LabelRed : LabelWhite;
            if (tmp.color != col) tmp.color = col;
        }

        void OnMotion(SecurityCamera cam)
        {
            if (cam == null) return;
            _flashUntil[cam] = Time.unscaledTime + FlashSeconds;
            _flashVehicle[cam] = cam.LastAlert == SecurityCamera.Alert.Vehicle;   // (a hitman car pulling in: PoliceCars.Arrival)
            if (Placed && Sfx.Has("cam.motion")) Sfx.Play("cam.motion", ScreenCenter, 0.7f, 0f);
            int live = 0; foreach (var c in SecurityCamera.All) if (c != null) live++;
            if (live >= 2 && cam != _main) ShowFeed(cam.Door); // the door with the latest motion goes big
        }

        /// <summary>Which camera is big and which is the inset; labels and textures follow.</summary>
        void Rebind(bool force)
        {
            var cams = SecurityCamera.All;
            var front = FrontOrFirst();
            bool changed = force;
            if (_main == null || _main.Door == null)
            {
                _main = SecurityCamera.For(front);
                if (_main == null) foreach (var c in cams) if (c != null) { _main = c; break; }
                changed = true;
            }
            SecurityCamera other = null;
            foreach (var c in cams) if (c != null && c != _main && c.Door != null) { if (other == null || c.LastMotionTime > other.LastMotionTime) other = c; }
            if (_inset != other) { _inset = other; changed = true; }
            if (!changed || _screenMat == null) return;

            _screenMat.SetTexture("_MainTex", _main != null ? _main.Feed : null);
            _screenMat.SetTexture("_InsetTex", _inset != null ? _inset.Feed : null);
            _screenMat.SetVector("_InsetRect", _inset != null ? new Vector4(_insetUv.xMin, _insetUv.yMin, _insetUv.xMax, _insetUv.yMax) : Vector4.zero);
            _mainText = _main != null ? _main.Label : "NO SIGNAL";
            _insetText = _inset != null ? _inset.Label : null;
            _mainLabel.text = _mainText;
            _insetLabel.gameObject.SetActive(_inset != null);
            if (_inset != null) _insetLabel.text = _insetText;
            _screenMat.SetFloat("_Power", _main != null ? 1f : 0.35f);
            foreach (var c in cams) if (c != null) c.Refresh(); // a feed that just came on screen shows its porch now
        }

        // ───────────────────────────── placement ─────────────────────────────

        static VirtualDoor FrontOrFirst()
        {
            if (VirtualDoor.Instance != null) return VirtualDoor.Instance;
            foreach (var d in VirtualDoor.All) if (d != null) return d;
            return null;
        }

        void CheckPlacement(float now)
        {
            var front = FrontOrFirst();
            if (front == null)
            {
                foreach (var c in SecurityCamera.All) if (c != null) c.TargetFps = 0f;
                if (_visual.gameObject.activeSelf) _visual.gameObject.SetActive(false);
                Placed = false; FrontDoor = null; _frontSeenAt = -1f;
                return;
            }
            if (front != FrontDoor) { FrontDoor = front; _frontSeenAt = now; Placed = false; }
            // The door picks its hinge side ~1.5 s after it is built (clearance raycasts): place after that.
            if (now - _frontSeenAt < 1.8f) return;
            bool moved = Placed && ((Flat(front.Center) - Flat(_placedCenter)).sqrMagnitude > 0.0004f || Vector3.Dot(front.HingeToLatch, _placedHinge) < 0.5f);
            if (Placed && !moved) return;
            Place(front);
            _visual.gameObject.SetActive(true);
            Placed = true;
            _lastSecond = -1;
            Rebind(force: true);
            ApplyBigText();                                  // a line posted before the screen was up (TMP needs it active)
        }

        static float FloorY(VirtualDoor d)
        {
            var geo = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
            return geo != null && geo.Walls.Count > 0 ? geo.FloorY : d.Threshold.y;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        static Vector2 F2(Vector3 v) => new(v.x, v.z);

        void Place(VirtualDoor front)
        {
            _placedCenter = front.Center; _placedHinge = front.HingeToLatch;
            // The player's own spot for this room (the white bar) beats any automatic choice.
            if (_mover != null && _mover.TryRestore(front))
            {
                if (_size.W != Big.W) BuildVisual(Big);
                Placement = "where you put it";
                Debug.Log($"[HIS] SecurityMonitor placed where you put it: {ScreenCenter:F2}");
                return;
            }
            var geo = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
            float floorY = FloorY(front), w = front.Opening.width;
            var inward = Flat(front.Inward).normalized; var along = Flat(front.HingeToLatch).normalized;
            var c = Flat(front.Center);
            string why = null; var notes = new List<string>();
            Vector3 pos = default, normal = inward; Size size = Big;
            foreach (var sz in new[] { Big, Small })
            {
                string tag = sz.W > 0.5f ? "32\"" : "17\"";
                var latch = c + along * (w * 0.5f + EdgeGap + sz.W * 0.5f);
                var hinge = c - along * (w * 0.5f + w + EdgeGap + sz.W * 0.5f);
                string no;
                if ((no = Candidate(geo, front, latch, inward, floorY, sz, out var p1)) == null) { pos = p1; why = $"latch side, {tag}"; size = sz; break; }
                notes.Add($"{tag} latch: {no}");
                if ((no = Candidate(geo, front, hinge, inward, floorY, sz, out var p2)) == null) { pos = p2; why = $"hinge side past the swing, {tag}"; size = sz; break; }
                notes.Add($"{tag} hinge: {no}");
                if (NearestFreeWall(geo, c, latch, floorY, sz, out var p3, out var n3)) { pos = p3; normal = n3; why = $"nearest free wall spot, {tag}"; size = sz; break; }
                notes.Add($"{tag}: no free wall within 2 m");
            }
            if (why == null)
            {
                size = Small; pos = c + along * (w * 0.5f + EdgeGap + Small.W * 0.5f); why = "unchecked (no free wall spot within 2 m)";
                Debug.LogWarning("[HIS] SecurityMonitor: no free wall spot by the front door; using the latch side");
            }
            Placement = why;
            if (size.W != _size.W) BuildVisual(size);
            transform.SetPositionAndRotation(new Vector3(pos.x, floorY + ScreenCentreHeight, pos.z), Quaternion.LookRotation(-normal, Vector3.up));
            float edge = Vector3.Distance(Flat(pos), c) - w * 0.5f - size.W * 0.5f;
            Debug.Log($"[HIS] SecurityMonitor placed {why}: {ScreenCenter:F2}, facing {Facing:F2}, {edge:F2} m from the opening edge" +
                      (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""));
        }

        /// <summary>Null if a monitor of <paramref name="size"/> fits at <paramref name="spot"/> on the door's wall, else why not.</summary>
        static string Candidate(RoomGeometry geo, VirtualDoor door, Vector3 spot, Vector3 normal, float floorY, Size size, out Vector3 onWall)
        {
            if (!OnDoorWall(geo, door, spot, size.W, out onWall)) return "past the end of the wall";
            return Blocker(geo, onWall, normal, floorY, size);
        }

        /// <summary>The door's own wall segment (parallel, through the door): the spot snapped onto it, if the monitor fits there.</summary>
        static bool OnDoorWall(RoomGeometry geo, VirtualDoor door, Vector3 spot, float width, out Vector3 onWall)
        {
            onWall = spot;
            if (geo == null || geo.Walls.Count == 0) return true; // no scan data: the door plane is the wall
            var along = F2(door.HingeToLatch).normalized; var c = F2(door.Center);
            float best = 0.35f; bool found = false; RoomGeometry.Segment wall = default;
            foreach (var s in geo.Walls)
            {
                if (s.Invisible) continue;
                var d = s.B - s.A; float len = d.magnitude; if (len < 0.5f) continue;
                d /= len;
                if (Mathf.Abs(Vector2.Dot(d, along)) < 0.9f) continue;
                float dist = Vector2.Distance(c, RoomGeometry.ClosestOnSegment(c, s.A, s.B));
                if (dist < best) { best = dist; wall = s; found = true; }
            }
            if (!found) return true;
            var dir = (wall.B - wall.A).normalized; float L = Vector2.Distance(wall.A, wall.B);
            float t = Vector2.Dot(F2(spot) - wall.A, dir);
            if (t < width * 0.5f + 0.02f || t > L - width * 0.5f - 0.02f) return false; // off the end of the wall (a corner)
            var q = wall.A + dir * t;
            onWall = new Vector3(q.x, spot.y, q.y);
            return true;
        }

        /// <summary>Free spot on a wall: clear of every door's swing and opening, windows, and furniture at screen height.</summary>
        public static bool Free(RoomGeometry geo, Vector3 wallPoint, Vector3 normal, float floorY, Size size) => Blocker(geo, wallPoint, normal, floorY, size) == null;

        /// <summary>What's in the way of a monitor of <paramref name="size"/> at <paramref name="wallPoint"/> (null = nothing).</summary>
        public static string Blocker(RoomGeometry geo, Vector3 wallPoint, Vector3 normal, float floorY, Size size)
        {
            normal = Flat(normal).normalized;
            var tangent = Vector3.Cross(Vector3.up, normal);
            var face = Flat(wallPoint) + normal * 0.025f;
            var a = face - tangent * size.W * 0.5f; var b = face + tangent * size.W * 0.5f;
            float yLo = floorY + ScreenCentreHeight - size.H * 0.5f, yHi = floorY + ScreenCentreHeight + size.H * 0.5f;
            // Door leaves: nowhere within a leaf's width of any hinge (the swing, whichever way it opens), nor over an opening.
            foreach (var d in VirtualDoor.All)
            {
                if (d == null) continue;
                float dw = d.Opening.width;
                var hingePt = F2(d.Center) - F2(d.HingeToLatch).normalized * dw * 0.5f;
                if (Vector2.Distance(hingePt, RoomGeometry.ClosestOnSegment(hingePt, F2(a), F2(b))) < dw + 0.03f) return $"{d.Label} swing";
            }
            var rs = RoomScanner.Instance;
            if (rs != null)
            {
                foreach (var list in new[] { rs.Doors, rs.Windows })
                    foreach (var anchor in list)
                    {
                        if (anchor == null || !anchor.PlaneRect.HasValue) continue;
                        var r = anchor.PlaneRect.Value;
                        var centre = new Vector3(wallPoint.x, (yLo + yHi) * 0.5f, wallPoint.z);
                        var l = anchor.transform.InverseTransformPoint(centre);
                        if (Mathf.Abs(l.z) > 0.3f) continue;
                        if (l.x > r.xMin - size.W * 0.5f - 0.08f && l.x < r.xMax + size.W * 0.5f + 0.08f &&
                            l.y > r.yMin - size.H * 0.5f - 0.05f && l.y < r.yMax + size.H * 0.5f + 0.05f)
                            return anchor.Label == MRUKAnchor.SceneLabels.WINDOW_FRAME ? "window" : "door frame";
                    }
            }
            if (geo != null)
                foreach (var box in geo.Boxes)
                {
                    if (box.Top < yLo - 0.03f || box.Bottom > yHi + 0.03f) continue;
                    for (int i = 0; i <= 6; i++)
                    {
                        var q = F2(Vector3.Lerp(a, b, i / 6f));
                        if (Vector2.Distance(q, RoomGeometry.ClosestOnBox(q, box)) < 0.08f) return box.Name;
                    }
                }
            return null;
        }

        /// <summary>Scan every scanned wall within 2 m of the door for the free spot nearest the ideal latch-side one.</summary>
        static bool NearestFreeWall(RoomGeometry geo, Vector3 doorCentre, Vector3 ideal, float floorY, Size size, out Vector3 best, out Vector3 bestNormal)
        {
            best = default; bestNormal = default;
            if (geo == null) return false;
            float bestScore = float.MaxValue;
            var centre2 = F2(doorCentre);
            foreach (var s in geo.Walls)
            {
                if (s.Invisible) continue;
                var d = s.B - s.A; float len = d.magnitude; if (len < size.W + 0.1f) continue;
                d /= len;
                var perp = new Vector2(-d.y, d.x);
                for (float t = size.W * 0.5f + 0.03f; t <= len - size.W * 0.5f - 0.03f; t += 0.05f)
                {
                    var q = s.A + d * t;
                    if (Vector2.Distance(q, centre2) > 2f) continue;
                    var n = geo.Inside(q + perp * 0.3f) ? perp : -perp;
                    var p = new Vector3(q.x, floorY, q.y); var n3 = new Vector3(n.x, 0f, n.y);
                    float score = Vector2.Distance(q, F2(ideal));
                    if (score >= bestScore || !Free(geo, p, n3, floorY, size)) continue;
                    bestScore = score; best = p; bestNormal = n3;
                }
            }
            return bestScore < float.MaxValue;
        }

        // ───────────────────────────── the visual ─────────────────────────────

        void BuildVisual(Size size)
        {
            bool wasActive = _visual == null || _visual.gameObject.activeSelf;
            if (_visual != null) Destroy(_visual.gameObject);
            if (_bezelMesh != null) Destroy(_bezelMesh);
            if (_screenMesh != null) Destroy(_screenMesh);
            _size = size;
            _visual = new GameObject("Monitor").transform;
            _visual.SetParent(transform, false);
            _visual.gameObject.SetActive(wasActive);

            // Bezel: a dark plastic frame round the screen hole, with sides (no back: it's against the wall).
            var k = new MeshKit();
            float W = size.W, H = size.H, hw = W * 0.5f, hh = H * 0.5f, sw = size.ScreenW * 0.5f, sh = size.ScreenH * 0.5f, depth = BackZ - FrontZ;
            // Front frame (faces -z: Cross(y, x) = -z).
            k.Quad(new Vector3(-hw, sh, FrontZ), new Vector3(0, hh - sh, 0), new Vector3(W, 0, 0), 0.05f, MeshKit.Plain);
            k.Quad(new Vector3(-hw, -hh, FrontZ), new Vector3(0, hh - sh, 0), new Vector3(W, 0, 0), 0.05f, MeshKit.Plain);
            k.Quad(new Vector3(-hw, -sh, FrontZ), new Vector3(0, size.ScreenH, 0), new Vector3(hw - sw, 0, 0), 0.05f, MeshKit.Plain);
            k.Quad(new Vector3(sw, -sh, FrontZ), new Vector3(0, size.ScreenH, 0), new Vector3(hw - sw, 0, 0), 0.05f, MeshKit.Plain);
            // Recess lips between the frame and the screen.
            float lip = ScreenZ - FrontZ;
            k.Quad(new Vector3(-sw, sh, FrontZ), new Vector3(size.ScreenW, 0, 0), new Vector3(0, 0, lip), 0.05f, MeshKit.Plain);   // top lip faces -y
            k.Quad(new Vector3(-sw, -sh, FrontZ), new Vector3(0, 0, lip), new Vector3(size.ScreenW, 0, 0), 0.05f, MeshKit.Plain);  // bottom lip faces +y
            // Sides.
            k.Quad(new Vector3(-hw, hh, FrontZ), new Vector3(0, 0, depth), new Vector3(W, 0, 0), 0.05f, MeshKit.Plain);    // top (+y)
            k.Quad(new Vector3(-hw, -hh, FrontZ), new Vector3(W, 0, 0), new Vector3(0, 0, depth), 0.05f, MeshKit.Plain);   // bottom (-y)
            k.Quad(new Vector3(-hw, -hh, FrontZ), new Vector3(0, 0, depth), new Vector3(0, H, 0), 0.05f, MeshKit.Plain);   // left (-x)
            k.Quad(new Vector3(hw, -hh, FrontZ), new Vector3(0, H, 0), new Vector3(0, 0, depth), 0.05f, MeshKit.Plain);    // right (+x)
            _bezelMesh = k.ToMesh("SecurityMonitor bezel", tangents: true);
            var bezel = new GameObject("Bezel");
            bezel.transform.SetParent(_visual, false);
            bezel.AddComponent<MeshFilter>().sharedMesh = _bezelMesh;
            var bezelRenderer = bezel.AddComponent<MeshRenderer>();
            bezelRenderer.sharedMaterial = _bezelMat;
            NoShadows(bezelRenderer);

            // Screen quad (faces -z, uv 0..1).
            _screenMesh = new Mesh { name = "SecurityMonitor screen" };
            _screenMesh.SetVertices(new List<Vector3> { new(-sw, -sh, 0f), new(sw, -sh, 0f), new(-sw, sh, 0f), new(sw, sh, 0f) });
            _screenMesh.SetUVs(0, new List<Vector2> { new(0f, 0f), new(1f, 0f), new(0f, 1f), new(1f, 1f) });
            _screenMesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            _screenMesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            _screenMesh.RecalculateBounds();
            var screen = new GameObject("Screen");
            _screen = screen.transform;
            _screen.SetParent(_visual, false);
            _screen.localPosition = new Vector3(0f, 0f, ScreenZ);
            screen.AddComponent<MeshFilter>().sharedMesh = _screenMesh;
            _screenRenderer = screen.AddComponent<MeshRenderer>();
            _screenRenderer.sharedMaterial = _screenMat;
            NoShadows(_screenRenderer);
            float aspect = size.ScreenW / size.ScreenH;
            if (_screenMat != null) _screenMat.SetFloat("_Aspect", aspect);
            // Picture-in-picture: 40% of the width, 16:9, top right under the REC row.
            float iw = 0.4f, ih = iw * aspect / (16f / 9f);
            _insetUv = Rect.MinMaxRect(0.975f - iw, 0.84f - ih, 0.975f, 0.84f);

            // Overlay text (TMP units: the text objects are scaled 0.1, so size 1.4 ≈ a 14 mm em — research/21 §2.5).
            float kS = size.ScreenW / Big.ScreenW;
            float big = Mathf.Max(1.1f, 1.6f * kS), mid = Mathf.Max(0.95f, 1.4f * kS), small = Mathf.Max(0.8f, 1.4f * kS);
            _mainLabel = Text("Label", big, TextAlignmentOptions.TopLeft, new Vector2(0f, 1f), Uv(0.022f, 0.965f));
            _rec = Text("Rec", mid, TextAlignmentOptions.Right, new Vector2(1f, 0.5f), Uv(0.94f, 0.918f));
            _rec.text = "<b>REC</b>"; _rec.color = new Color(1f, 0.3f, 0.25f);
            _clock = Text("Clock", mid, TextAlignmentOptions.BottomRight, new Vector2(1f, 0f), Uv(0.978f, 0.03f));
            _date = Text("Date", mid, TextAlignmentOptions.BottomLeft, new Vector2(0f, 0f), Uv(0.022f, 0.03f));
            _insetLabel = Text("InsetLabel", small, TextAlignmentOptions.TopLeft, new Vector2(0f, 1f), Uv(_insetUv.xMin + 0.01f, _insetUv.yMax - 0.015f));
            _insetLabel.gameObject.SetActive(false);
            BuildBigText(kS);
            _mainText ??= "NO SIGNAL";
            _mainLabel.text = _mainText;
            _lastSecond = -1;
            if (_mover != null) _mover.Attach(_visual, new Vector2(size.W, size.H));
        }

        MonitorMover _mover;
        /// <summary>The white move bar (MonitorMover): the player can drag the screen anywhere; their spot is kept per room.</summary>
        public MonitorMover Mover => _mover;

        // The big line (ShowBigText). Centred a little below the middle (screen uv), so the feed's label, REC and the clock
        // stay clear; 1 cm proud of the screen, with its plate 3 mm behind it, so neither z-fights the picture. The box and
        // the auto-size range are for the 32″ screen (TMP units: 0.1 m each, size 1 ≈ a 10 mm em) and scale down for the
        // 17″. Max ≈ a 40 mm em: readable from across a room (≈ 0.75° at 3 m), where the 14 mm labels aren't.
        const float BigV = 0.36f, BigZ = -0.032f, BigW = 6.4f, BigH = 1.5f, BigMin = 2.2f, BigMax = 4f;
        Transform _bigRoot, _bigPlate;
        TextMeshPro _big;
        string _bigString;
        Vector2 _bigPad;

        /// <summary>The big line's text and plate, rebuilt with the rest of the overlay (hidden until there's a line).</summary>
        void BuildBigText(float kS)
        {
            _bigRoot = new GameObject("BigText").transform;
            _bigRoot.SetParent(_visual, false);
            var at = Uv(0.5f, BigV);
            _bigRoot.localPosition = new Vector3(at.x, at.y, BigZ);
            _big = Subtitles.CreateWorldText("Text", _bigRoot, BigMax * kS, BigW * kS, TextAlignmentOptions.Center);
            _big.textWrappingMode = TextWrappingModes.Normal;
            _big.fontStyle = FontStyles.Bold;
            _big.enableAutoSizing = true;                    // (sized on a text change only: ApplyBigText)
            _big.fontSizeMin = BigMin * kS; _big.fontSizeMax = BigMax * kS;
            _big.rectTransform.sizeDelta = new Vector2(BigW * kS, BigH * kS);
            _big.color = LabelWhite;
            var tr = _big.GetComponent<MeshRenderer>();
            if (tr != null) NoShadows(tr);
            _bigPlate = Subtitles.CreatePlate("Plate", _bigRoot);
            var pr = _bigPlate.GetComponent<MeshRenderer>();
            if (pr != null) NoShadows(pr);
            _bigPad = new Vector2(0.025f, 0.014f) * kS;
            _bigRoot.gameObject.SetActive(false);
            ApplyBigText();
        }

        /// <summary>Show / hide the big line; its mesh and plate are measured only while the screen is up (TMP needs it active).</summary>
        void ApplyBigText()
        {
            if (_bigRoot == null || _big == null) return;
            bool on = _bigString != null;
            if (_bigRoot.gameObject.activeSelf != on) _bigRoot.gameObject.SetActive(on);
            if (!on) return;
            if (_big.text != _bigString) _big.text = _bigString;
            if (!_bigRoot.gameObject.activeInHierarchy) return; // CheckPlacement re-applies once the screen is placed
            _big.ForceMeshUpdate(true);
            Subtitles.FitBacking(_big, _bigPlate, _bigPad);
        }

        Vector2 Uv(float u, float v) => new((u - 0.5f) * _size.ScreenW, (v - 0.5f) * _size.ScreenH);

        TextMeshPro Text(string name, float size, TextAlignmentOptions align, Vector2 pivot, Vector2 at)
        {
            var tmp = Subtitles.CreateWorldText(name, _visual, size, 5f, align);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.fontStyle = FontStyles.Bold;
            tmp.rectTransform.pivot = pivot;
            tmp.rectTransform.sizeDelta = new Vector2(5f, 0.5f);
            tmp.transform.localPosition = new Vector3(at.x, at.y, TextZ);
            tmp.color = LabelWhite;
            var r = tmp.GetComponent<MeshRenderer>();
            if (r != null) NoShadows(r);
            return tmp;
        }

        static void NoShadows(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }
    }
}
