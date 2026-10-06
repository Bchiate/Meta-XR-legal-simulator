using System.Collections.Generic;
using HIS.Room;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// A small virtual night exterior just outside one door rig, seen only through that door's grainy B&amp;W security camera
    /// (Brett 0.1.20: "maybe you can see them in a security monitor at the door"). Layer "Outside" (11): the headset camera
    /// culls it, and its renderers are switched on only for the owning camera's render (<see cref="SetLive"/>), so two
    /// porches built on neighbouring walls never show in each other's feed or z-fight.
    /// Front door: siding with the door hole, trim, a porch lamp, a concrete stoop and doormat, a path ~9 m out to the
    /// sidewalk, lawn and mulch beds with bushes, a mailbox, the street with a far streetlight, houses across the road, sky.
    /// Side door (index ≥ 1): a backyard — stoop, path to a fence gate, bins, an AC unit, a neighbour's floodlight.
    /// Local frame (the GameObject's): origin = door centre on the floor, +z out of the house, +x to the right looking out.
    /// Everything is one opaque mesh + one additive glow mesh (2 draw calls), lit in HIS/SecurityPorch (no URP lights).
    /// Rounds land on it (0.1.41): box colliders on this layer for the ground, stoop, houses, roofs, posts, trunks, fence
    /// (<see cref="OutsideSurface"/>, placed with <see cref="Solid"/>/<see cref="Hit"/> from the same numbers as the meshes),
    /// and <see cref="Impacts"/>, the pooled dust, bits and scars they throw up.
    /// Walkers (IntruderApproach) use <see cref="Approach"/> (street → step, all at the floor height; the walking surfaces are flush),
    /// <see cref="Step"/>, and <see cref="OffLeft"/>/<see cref="OffRight"/> (just out of the camera's view; walk to
    /// Approach[^2], the porch front, first so the line clears the bushes).
    /// </summary>
    public class PorchSet : MonoBehaviour
    {
        public const int OutsideLayer = 11;
        /// <summary>The exterior siding sits this far out from the door plane (the scanned frame is the inner wall face).</summary>
        public const float WallOut = 0.16f;
        public const float Aspect = 16f / 9f, NearClip = 0.05f, FarClip = 80f;
        /// <summary>
        /// Front door: a doorbell cam (research/21 §3.1) — 1.38 m up beside the latch jamb, turned 15° toward the step, 120°
        /// wide, strong fisheye on the monitor: the face at the step fills it, the path and street sit small at the horizon.
        /// </summary>
        public const float BellHeight = 1.38f, BellPitch = 10f, BellYaw = 15f, BellFov = 120f, BellBarrel = 0.3f;
        /// <summary>
        /// Side door: an eave cam over the door (≥ 2.1 m, 0.26 m out), looking out and down over the yard: 116° (≈83° vertical)
        /// keeps a man on the step in frame down to his waist and still shows the fence and the houses beyond.
        /// </summary>
        public const float EavePitch = 37f, EaveFov = 116f, EaveBarrel = 0.2f;
        // The front set's lines out from the door (porch z): the path ends at the sidewalk, the curb, the road's near edge,
        // its far edge; the backyard's fence. Shared by the meshes and by where a round lands (GroundKindAt).
        const float Walk = 8f, Curb = 9.2f, Road = 9.35f, FarSide = 16f, Fence = 7.5f;
        /// <summary>
        /// The colliders by the door start this far out from the siding (0.41 m past the door plane): a punch or a blade
        /// reaching out through the doorway at floor height (HandController.FistStrike, Knife: any IDamageable) never meets them.
        /// </summary>
        const float NearGap = 0.25f;

        static readonly List<PorchSet> s_all = new();
        public static IReadOnlyList<PorchSet> All => s_all;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_all.Clear();

        /// <summary>The porch outside <paramref name="d"/>, or null.</summary>
        public static PorchSet For(VirtualDoor d)
        {
            if (d == null) return null;
            foreach (var p in s_all) if (p != null && p.Door == d) return p;
            return null;
        }

        public VirtualDoor Door { get; private set; }
        public bool Backyard { get; private set; }
        /// <summary>Floor height the set was built at (the room's scanned floor).</summary>
        public float FloorY { get; private set; }
        public int Triangles { get; private set; }

        /// <summary>World points at the room's floor height from the street ~10 m out (or beyond the yard gate) to the step. A fresh array.</summary>
        public Vector3[] Approach
        {
            get
            {
                var a = new Vector3[_approach.Length];
                for (int i = 0; i < a.Length; i++) a[i] = transform.TransformPoint(_approach[i]);
                return a;
            }
        }
        /// <summary>On the doormat, ~0.6 m outside the door centre: where a visitor stands to knock or ring.</summary>
        public Vector3 Step => transform.TransformPoint(_step);
        /// <summary>Just out of the camera's view on the left of its picture (on the lawn, in front of the house).</summary>
        public Vector3 OffLeft => transform.TransformPoint(_offLeft);
        /// <summary>Just out of the camera's view on the right of its picture.</summary>
        public Vector3 OffRight => transform.TransformPoint(_offRight);

        /// <summary>A doorbell cam beside the door (front) rather than an eave cam above it (side door).</summary>
        public bool DoorbellCam => !Backyard;
        /// <summary>Where the security camera hangs and how it looks. The doorbell follows the latch side once the door picks it.</summary>
        public Vector3 CameraPosition => transform.TransformPoint(CamLocal(_latchSign));
        public Quaternion CameraRotation => transform.rotation * CamRotLocal(_latchSign);
        public float HorizontalFov => DoorbellCam ? BellFov : EaveFov;
        public float VerticalFov => Camera.HorizontalToVerticalFieldOfView(HorizontalFov, Aspect);
        /// <summary>The monitor's fisheye squeeze for this camera's picture.</summary>
        public float Barrel => DoorbellCam ? BellBarrel : EaveBarrel;

        Vector3 CamLocal(float latch) => DoorbellCam ? new Vector3(latch * (_hole + 0.16f), BellHeight, WallOut + 0.045f) : _camLocal;
        Quaternion CamRotLocal(float latch) => DoorbellCam ? Quaternion.Euler(BellPitch, -latch * BellYaw, 0f) : Quaternion.Euler(EavePitch, 0f, 0f);

        Vector3[] _approach = System.Array.Empty<Vector3>();
        Vector3 _step, _offLeft, _offRight, _camLocal, _lampLocal, _streetLocal, _fillLocal;
        float _lampPower, _streetPower, _streetRange, _fillPower, _fillRange = 4f, _lampRange = LampRange, _moon = 1f;
        MeshRenderer _renderer, _glowRenderer;
        Mesh _mesh, _glowMesh;
        Material _mat, _glowMat;
        Vector3 _doorCenter, _doorOut;
        float _hole = 0.47f, _latchSign = 1f;
        Transform _hitRoot;

        /// <summary>This porch's pooled bullet impacts (dust, bits, scars): drawn by its camera and through its open doorway only.</summary>
        public OutsideImpacts Impacts { get; private set; }

        /// <summary>Builds the set for <paramref name="door"/> under <paramref name="parent"/> (a plain root, not the door).</summary>
        public static PorchSet Build(VirtualDoor door, Transform parent)
        {
            var go = new GameObject($"[HIS] Porch ({door.Label})") { layer = OutsideLayer };
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<PorchSet>();
            p.Init(door);
            return p;
        }

        void Init(VirtualDoor door)
        {
            Door = door;
            Backyard = !door.IsFront;
            s_all.Add(this);
            var geo = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
            FloorY = geo != null && geo.Walls.Count > 0 ? geo.FloorY : door.Threshold.y;
            Follow(force: true);

            var open = door.Opening;
            float width = Mathf.Max(0.6f, open.width);
            float top = door.transform.TransformPoint(new Vector3(open.center.x, open.yMax, 0f)).y - FloorY;
            if (top < 1.7f || top > 2.8f) top = 2.03f;

            var k = new MeshKit(); var g = new MeshKit();
            float holeTop = top + 0.02f;
            _hole = width * 0.5f + 0.02f;
            _camLocal = new Vector3(0f, Mathf.Max(2.1f, holeTop + 0.2f), WallOut + 0.1f); // the eave cam (and the glows face it)
            _hitRoot = new GameObject("Hits") { layer = OutsideLayer }.transform;   // where rounds land (OutsideSurface)
            _hitRoot.SetParent(transform, false);
            House(k, g, width, holeTop);
            if (Backyard) BuildBackyard(k, g); else BuildFront(k, g);
            Sky(k);

            _mesh = k.ToMesh($"Porch {door.Label}");
            _glowMesh = g.ToMesh($"Porch glow {door.Label}");
            Triangles = k.Triangles + g.Triangles;
            _mat = MakeMaterial("Security/SecurityPorch", false);
            _glowMat = MakeMaterial("Security/SecurityPorchGlow", true);
            _renderer = AddRenderer("Set", _mesh, _mat);
            _glowRenderer = AddRenderer("Glow", _glowMesh, _glowMat);
            Impacts = new OutsideImpacts(this);
            HIS.Core.GameSession.WorldCleared += ClearShotMarks;   // a fresh night: the last round's scars go with the bodies
            FindOffPoints();
            PushLighting();
            SetLive(false);
            Debug.Log($"[HIS] PorchSet {door.Label}: {(Backyard ? "backyard" : "street")} set, {Triangles} tris, {(DoorbellCam ? "doorbell" : "eave")} camera at {CamLocal(_latchSign).y:F2} m, " +
                      $"step {Step:F2}, off-view L {_offLeft.x:F1} / R {_offRight.x:F1} m, {_hitRoot.childCount} hit surfaces");
        }

        void OnDestroy()
        {
            s_all.Remove(this);
            HIS.Core.GameSession.WorldCleared -= ClearShotMarks;
            Impacts?.Dispose();
            if (_mesh != null) Destroy(_mesh);
            if (_glowMesh != null) Destroy(_glowMesh);
            if (_mat != null) Destroy(_mat);
            if (_glowMat != null) Destroy(_glowMat);
        }

        void LateUpdate()
        {
            if (Door == null) { Destroy(gameObject); return; }   // room reload / door re-picked: the security system rebuilds
            Follow(force: false);
        }

        /// <summary>Keeps the set on its door (a recenter can move the anchor); lighting follows.</summary>
        void Follow(bool force)
        {
            var c = Door.Center; var o = Door.Outward; o.y = 0f;
            if (!force && (c - _doorCenter).sqrMagnitude < 1e-6f && (o - _doorOut).sqrMagnitude < 1e-6f) return;
            _doorCenter = c; _doorOut = o;
            transform.SetPositionAndRotation(new Vector3(c.x, FloorY, c.z), Quaternion.LookRotation(o.sqrMagnitude > 1e-6f ? o.normalized : Vector3.forward, Vector3.up));
            if (!force) PushLighting();
        }

        void Update()
        {
            // The doorbell sits on the latch side, which the door settles ~1.5 s after it is built (and on a re-pick).
            if (Door != null) _latchSign = Vector3.Dot(Door.HingeToLatch, transform.right) >= 0f ? 1f : -1f;
            // The door open (a crack is enough): the headset sees this set through the doorway (DoorwayView), so it stays on.
            bool portal = Door != null && Door.OpenAngle > 0.5f && DoorwayView.Available;
            if (portal != ThroughDoorway)
            {
                ThroughDoorway = portal;
                if (portal) { PushLighting(); BeforeRender?.Invoke(); }
                SetLive(false);
                Debug.Log($"[HIS] PorchSet {Door.Label}: {(portal ? "seen through the open doorway" : "door shut, feed only")}");
            }
        }

        /// <summary>Its door is open and the headset draws the set through the doorway (DoorwayView): renderers stay on.</summary>
        public bool ThroughDoorway { get; private set; }

        /// <summary>
        /// Renderers on only while the owning camera draws (SecurityCamera's begin/end camera callbacks) — or all the time
        /// while the headset sees the set through the open doorway (<see cref="ThroughDoorway"/>).
        /// </summary>
        public void SetLive(bool on)
        {
            if (on) BeforeRender?.Invoke();
            for (int i = _feedOnly.Count - 1; i >= 0; i--)
            {
                var r = _feedOnly[i];
                if (r == null) { _feedOnly.RemoveAt(i); continue; }
                r.enabled = on;                 // the camera's render only, never the headset's doorway view
            }
            on |= ThroughDoorway;
            _liveNow = on;
            if (_renderer != null) _renderer.enabled = on;
            if (_glowRenderer != null) _glowRenderer.enabled = on;
            for (int i = _extras.Count - 1; i >= 0; i--)
            {
                var r = _extras[i];
                if (r == null) { _extras.RemoveAt(i); continue; }
                r.enabled = on;
            }
        }

        readonly List<Renderer> _extras = new(), _feedOnly = new();
        /// <summary>The set's renderers are on right now (the last <see cref="SetLive"/>, the open doorway included).</summary>
        bool _liveNow;

        /// <summary>
        /// A layer-11 renderer that only this door's camera may ever draw, not the headset through the open doorway: the
        /// exterior door's dark "inside of the house" box (it stands on the room side of the door — seen at an angle through
        /// the doorway it blacked the view out: Brett 0.1.32) and its copy of the leaf (the headset has the real one).
        /// </summary>
        public void AddFeedOnly(Renderer r)
        {
            if (r == null || _feedOnly.Contains(r)) return;
            _extras.Remove(r);
            _feedOnly.Add(r);
            r.enabled = false;
        }

        /// <summary>
        /// Another layer-11 renderer that belongs to this porch (the exterior door, its holes and splinters, the police cars):
        /// switched on and off with the set, so only this door's camera ever draws it. It starts as the set is now: on while
        /// the doorway shows it (0.1.47: the first police car was built with the front door already open by hand — "off until
        /// the next render" left it invisible behind its kneeling riders for the whole wave, the monitor unwatched), else off
        /// until the next render. Hide one for good with its GameObject (SetActive), never with <c>enabled</c>: that is this switch's.
        /// </summary>
        public void AddLive(Renderer r)
        {
            if (r == null || _extras.Contains(r)) return;
            _extras.Add(r);
            r.enabled = _liveNow || ThroughDoorway;
        }

        /// <summary>Right before this porch's camera draws (inside its begin-camera callback): things outside sync to the room.</summary>
        public event System.Action BeforeRender;

        /// <summary>The set's lit material (HIS/SecurityPorch) and its additive glow one: things added outside share them.</summary>
        public Material SetMaterial => _mat;
        public Material GlowMaterial => _glowMat;

        // The night look (HIS/SecurityPorch): the IR illuminator lights the porch and the near lawn, fading out by the street;
        // beyond it only the streetlight pool, lit windows and the town glow in the sky.
        const float IR = 1.8f, IRRange = 3f, Ambient = 0.01f, Emission = 2f, LampRange = 1.8f;

        /// <param name="irPower">The camera's smart-IR LED power (1 = full; it dims for a face close to the lens).</param>
        public void PushLighting(float irPower = 1f)
        {
            if (_mat == null) return;
            _mat.SetFloat("_IR", IR * irPower); _mat.SetFloat("_IRRange", IRRange); _mat.SetFloat("_Ambient", Ambient);
            _mat.SetFloat("_Emission", Emission); _mat.SetFloat("_LampRange", _lampRange);
            var lamp = transform.TransformPoint(_lampLocal); var street = transform.TransformPoint(_streetLocal);
            _mat.SetVector("_Lamp", new Vector4(lamp.x, lamp.y, lamp.z, _lampPower));
            _mat.SetVector("_Street", new Vector4(street.x, street.y, street.z, _streetPower));
            _mat.SetFloat("_StreetRange", _streetRange);
            var fill = transform.TransformPoint(_fillLocal);
            _mat.SetVector("_Fill", new Vector4(fill.x, fill.y, fill.z, _fillPower));
            _mat.SetFloat("_FillRange", _fillRange); _mat.SetFloat("_Moon", _moon);
        }

        Material MakeMaterial(string path, bool glow)
        {
            var src = Resources.Load<Material>(path);
            if (src != null) return new Material(src);
            var shader = Shader.Find("HIS/SecurityPorch");
            if (shader == null) { Debug.LogError($"[HIS] PorchSet: no {path} material and no HIS/SecurityPorch shader"); return null; }
            var m = new Material(shader);
            if (glow)
            {
                m.SetFloat("_Glow", 1f); m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 1f); m.SetFloat("_ZWrite", 0f);
                m.renderQueue = 3000;
            }
            return m;
        }

        MeshRenderer AddRenderer(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name) { layer = OutsideLayer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            return r;
        }

        // ───────────────────────────── where rounds land ─────────────────────────────

        /// <summary>What the ground is where a round hit it (world point): the path, sidewalks and curb are concrete, the street asphalt, the rest dirt.</summary>
        internal OutsideKind GroundKindAt(Vector3 world)
        {
            var l = transform.InverseTransformPoint(world);
            float ax = Mathf.Abs(l.x);
            if (Backyard) return ax <= 0.45f && l.z <= Fence ? OutsideKind.Concrete : OutsideKind.Dirt;
            if (l.z < Walk) return ax <= 0.55f ? OutsideKind.Concrete : OutsideKind.Dirt;   // the path, or the lawn and mulch beds
            if (l.z < Road) return OutsideKind.Concrete;                                     // the sidewalk and the curb
            if (l.z < FarSide) return OutsideKind.Asphalt;                                   // the street
            return l.z < FarSide + 1.5f ? OutsideKind.Concrete : OutsideKind.Dirt;           // the far sidewalk, their lawns
        }

        /// <summary>
        /// The porch whose effects should show a hit at <paramref name="world"/>: this one, unless its door is shut and another
        /// porch seen through its open doorway covers that spot (porches on neighbouring walls overlap outside).
        /// </summary>
        internal PorchSet ShownAt(Vector3 world)
        {
            if (ThroughDoorway) return this;
            foreach (var p in s_all)
                if (p != null && p != this && p.ThroughDoorway && p.transform.InverseTransformPoint(world).z > WallOut) return p;
            return this;
        }

        /// <summary>Clears the bullet scars and dust (a fresh night).</summary>
        public void ClearShotMarks() => Impacts?.Clear();

        /// <summary>A solid box in the set: its mesh, and a collider of the same size that rounds land on.</summary>
        void Solid(MeshKit k, Vector3 min, Vector3 max, float grey, byte pattern, OutsideKind kind, string name)
        {
            k.Box(min, max, grey, pattern);
            Hit(name, min, max, kind);
        }

        /// <summary>A collider only (porch space), for surfaces built as quads (ground, fence) or trimmed near the door.</summary>
        void Hit(string name, Vector3 min, Vector3 max, OutsideKind kind, bool ground = false) =>
            OutsideSurface.Box(this, _hitRoot, name, min, max, kind, ground);

        /// <summary>A gable roof (MeshKit.GableRoof) and a collider on its street-side slope, the one a round can reach.</summary>
        void Roof(MeshKit k, Vector3 min, Vector3 max, float ridge, float grey)
        {
            k.GableRoof(min, max, ridge, grey);
            float x = (min.x + max.x) * 0.5f;
            OutsideSurface.Slope(this, _hitRoot, "Roof", new Vector3(x, max.y, min.z), new Vector3(x, max.y + ridge, (min.z + max.z) * 0.5f), max.x - min.x, OutsideKind.Wood);
        }

        // ───────────────────────────── the set ─────────────────────────────

        const float T = WallOut;

        /// <summary>The house front: lap siding with the door hole, trim, foundation, the porch lamp; the camera's view point.</summary>
        void House(MeshKit k, MeshKit g, float width, float holeTop)
        {
            float hole = width * 0.5f + 0.02f;
            const float span = 6.5f, height = 3.4f, found = 0.22f;
            // Siding (faces +z = out): left, right, over the door.
            k.Quad(new Vector3(-span, found, T), new Vector3(span - hole, 0, 0), new Vector3(0, height - found, 0), 0.5f, MeshKit.Siding, new Vector2(-span, found));
            k.Quad(new Vector3(hole, found, T), new Vector3(span - hole, 0, 0), new Vector3(0, height - found, 0), 0.5f, MeshKit.Siding, new Vector2(hole, found));
            k.Quad(new Vector3(-hole, holeTop, T), new Vector3(2f * hole, 0, 0), new Vector3(0, height - holeTop, 0), 0.5f, MeshKit.Siding, new Vector2(-hole, holeTop));
            // Foundation strip.
            k.Box(new Vector3(-span, -0.08f, T - 0.02f), new Vector3(-hole, found, T + 0.015f), 0.36f, MeshKit.Concrete);
            k.Box(new Vector3(hole, -0.08f, T - 0.02f), new Vector3(span, found, T + 0.015f), 0.36f, MeshKit.Concrete);
            // Door casing, 2.5 cm proud.
            k.Box(new Vector3(-hole - 0.1f, 0f, T), new Vector3(-hole, holeTop, T + 0.025f), 0.72f, MeshKit.Plain);
            k.Box(new Vector3(hole, 0f, T), new Vector3(hole + 0.1f, holeTop, T + 0.025f), 0.72f, MeshKit.Plain);
            k.Box(new Vector3(-hole - 0.1f, holeTop, T), new Vector3(hole + 0.1f, holeTop + 0.12f, T + 0.03f), 0.72f, MeshKit.Plain);
            // Porch lamp on the right of the door (looking out): a dark lantern with a lit glass front, and its halo.
            float lx = hole + 0.3f;
            k.Box(new Vector3(lx - 0.075f, 1.6f, T), new Vector3(lx + 0.075f, 1.9f, T + 0.14f), 0.1f, MeshKit.Plain);
            k.Box(new Vector3(lx - 0.1f, 1.9f, T), new Vector3(lx + 0.1f, 1.94f, T + 0.17f), 0.1f, MeshKit.Plain);
            k.Quad(new Vector3(lx - 0.055f, 1.64f, T + 0.142f), new Vector3(0.11f, 0, 0), new Vector3(0, 0.22f, 0), 0.9f, MeshKit.Emissive);
            _lampLocal = new Vector3(lx, 1.74f, T + 0.35f);
            _lampPower = 0.55f;
            g.Billboard(new Vector3(lx, 1.76f, T + 0.16f), 0.34f, _camLocal, new Color(0.42f, 0.42f, 0.42f));
        }

        void BuildFront(MeshKit k, MeshKit g)
        {
            // Walking surfaces sit at the room's floor height (the walk-up keeps his feet at FloorY): stoop top 0, path and
            // sidewalks 5 mm under, the street 12 mm, the lawn 3 cm.
            k.Box(new Vector3(-1.1f, -0.1f, T), new Vector3(1.1f, 0f, T + 1.5f), 0.44f, MeshKit.Concrete);
            Hit("Stoop", new Vector3(-1.1f, -0.1f, T + NearGap), new Vector3(1.1f, 0f, T + 1.5f), OutsideKind.Concrete);
            k.Quad(new Vector3(-0.42f, 0.004f, T + 0.06f), new Vector3(0, 0, 0.55f), new Vector3(0.84f, 0, 0), 0.13f, MeshKit.Asphalt);
            const float walk = Walk, curb = Curb, road = Road, farSide = FarSide;
            // The ground under everything out to the far lawns (its top at the path's height; the lawn is 2.5 cm under it):
            // what it is where a round lands comes from GroundKindAt.
            Hit("Ground", new Vector3(-65f, -0.105f, T + NearGap), new Vector3(65f, -0.005f, 70f), OutsideKind.Dirt, ground: true);
            k.Quad(new Vector3(-0.55f, -0.005f, T + 1.5f), new Vector3(0, 0, walk - T - 1.5f), new Vector3(1.1f, 0, 0), 0.42f, MeshKit.Concrete, new Vector2(T + 1.5f, -0.55f));
            // Lawn under everything, mulch beds along the house, bushes.
            k.Quad(new Vector3(-24f, -0.03f, T), new Vector3(0, 0, walk - T), new Vector3(48f, 0, 0), 0.44f, MeshKit.Grass, new Vector2(T, -24f));
            k.Quad(new Vector3(-5.5f, -0.02f, T), new Vector3(0, 0, 1.0f), new Vector3(4.4f, 0, 0), 0.2f, MeshKit.Asphalt);
            k.Quad(new Vector3(1.1f, -0.02f, T), new Vector3(0, 0, 1.0f), new Vector3(4.4f, 0, 0), 0.2f, MeshKit.Asphalt);
            k.Blob(new Vector3(-1.75f, 0.28f, T + 0.5f), new Vector3(0.5f, 0.5f, 0.42f), 0.55f, MeshKit.Foliage, 1);
            k.Blob(new Vector3(-3.15f, 0.34f, T + 0.52f), new Vector3(0.62f, 0.6f, 0.46f), 0.5f, MeshKit.Foliage, 2);
            k.Blob(new Vector3(1.8f, 0.26f, T + 0.5f), new Vector3(0.48f, 0.46f, 0.4f), 0.55f, MeshKit.Foliage, 3);
            k.Blob(new Vector3(3.3f, 0.36f, T + 0.55f), new Vector3(0.64f, 0.62f, 0.48f), 0.5f, MeshKit.Foliage, 4);
            // A tree on the left of the lawn, a mailbox by the sidewalk.
            Solid(k, new Vector3(-6.7f, -0.03f, 5.3f), new Vector3(-6.4f, 2.4f, 5.6f), 0.2f, MeshKit.Plain, OutsideKind.Wood, "Tree trunk");
            _posts.Add(new Vector3(-6.55f, 5.45f, 0.18f));   // a runaway police car wraps round it (PoliceCars crash)
            k.Blob(new Vector3(-6.55f, 3.6f, 5.45f), new Vector3(1.9f, 1.7f, 1.9f), 0.45f, MeshKit.Foliage, 5, 8, 12);
            Solid(k, new Vector3(1.35f, -0.03f, 7.55f), new Vector3(1.44f, 1.02f, 7.64f), 0.2f, MeshKit.Plain, OutsideKind.Wood, "Mailbox post");
            Solid(k, new Vector3(1.22f, 1.02f, 7.35f), new Vector3(1.57f, 1.22f, 7.85f), 0.24f, MeshKit.Plain, OutsideKind.Metal, "Mailbox");
            // Sidewalk (joints along the street), curb, street with a dashed centre line.
            k.Quad(new Vector3(-40f, -0.005f, curb), new Vector3(80f, 0, 0), new Vector3(0, 0, -(curb - walk)), 0.4f, MeshKit.Concrete, new Vector2(-40f, 0f));
            k.Quad(new Vector3(-40f, -0.004f, road), new Vector3(80f, 0, 0), new Vector3(0, 0, -(road - curb)), 0.55f, MeshKit.Plain);
            k.Quad(new Vector3(-45f, -0.012f, farSide), new Vector3(90f, 0, 0), new Vector3(0, 0, -(farSide - road)), 0.14f, MeshKit.Asphalt, new Vector2(-45f, 0f));
            float mid = (road + farSide) * 0.5f;
            for (float x = -40f; x < 40f; x += 4.6f)
                k.Quad(new Vector3(x, -0.008f, mid + 0.06f), new Vector3(2.8f, 0, 0), new Vector3(0, 0, -0.12f), 0.6f, MeshKit.Plain);
            // Across the street: sidewalk, lawns, two houses (one lit window each), a tree.
            k.Quad(new Vector3(-45f, -0.005f, farSide + 1.5f), new Vector3(90f, 0, 0), new Vector3(0, 0, -1.5f), 0.38f, MeshKit.Concrete, new Vector2(-45f, 0f));
            k.Quad(new Vector3(-65f, -0.03f, 70f), new Vector3(130f, 0, 0), new Vector3(0, 0, -(70f - farSide - 1.5f)), 0.34f, MeshKit.Grass, new Vector2(-65f, 0f));
            NeighbourHouse(k, g, new Vector3(-15f, -0.03f, 22f), new Vector3(-4.5f, 3.2f, 30f), true);
            NeighbourHouse(k, g, new Vector3(3.5f, -0.03f, 21f), new Vector3(14f, 3.0f, 29f), false);
            // More of the block for the doorbell's wide view: a house either side further along.
            NeighbourHouse(k, g, new Vector3(-30f, -0.03f, 21.5f), new Vector3(-19f, 3.1f, 29.5f), false);
            NeighbourHouse(k, g, new Vector3(19f, -0.03f, 22f), new Vector3(30f, 3.3f, 30f), true);
            Solid(k, new Vector3(0.8f, -0.03f, 19f), new Vector3(1.15f, 2.6f, 19.35f), 0.18f, MeshKit.Plain, OutsideKind.Wood, "Tree trunk (across)");
            k.Blob(new Vector3(0.95f, 4.3f, 19.2f), new Vector3(2.2f, 2.0f, 2.2f), 0.4f, MeshKit.Foliage, 6, 8, 12);
            // Streetlight on the far side, left: pole, arm over the street, head with a lit lens, halo; its pool on the road.
            Solid(k, new Vector3(-6.6f, -0.005f, 16.4f), new Vector3(-6.45f, 6.0f, 16.55f), 0.25f, MeshKit.Plain, OutsideKind.Metal, "Lamp post");
            Solid(k, new Vector3(-6.58f, 5.84f, 15.0f), new Vector3(-6.47f, 5.94f, 16.45f), 0.25f, MeshKit.Plain, OutsideKind.Metal, "Lamp arm");
            Solid(k, new Vector3(-6.7f, 5.7f, 14.7f), new Vector3(-6.35f, 5.88f, 15.2f), 0.3f, MeshKit.Plain, OutsideKind.Metal, "Lamp head");
            k.Quad(new Vector3(-6.66f, 5.695f, 14.75f), new Vector3(0.28f, 0, 0), new Vector3(0, 0, 0.4f), 1f, MeshKit.Emissive);
            g.Billboard(new Vector3(-6.52f, 5.55f, 14.95f), 2.6f, _camLocal, new Color(0.34f, 0.34f, 0.34f)); // its halo reaches into the picture
            _streetLocal = new Vector3(-6.5f, 5.5f, 14.9f);
            _streetPower = 1.3f; _streetRange = 4f;
            // A second one far down the street on the right (glow only).
            Solid(k, new Vector3(13.4f, -0.005f, 16.4f), new Vector3(13.55f, 5.0f, 16.55f), 0.22f, MeshKit.Plain, OutsideKind.Metal, "Lamp post (far)");
            Solid(k, new Vector3(13.3f, 4.8f, 15.4f), new Vector3(13.65f, 4.95f, 15.9f), 0.25f, MeshKit.Plain, OutsideKind.Metal, "Lamp head (far)");
            g.Billboard(new Vector3(13.47f, 4.7f, 15.65f), 2.0f, _camLocal, new Color(0.26f, 0.26f, 0.26f));

            // All at the room's floor height (y = 0 here): the street just past the curb (~10 m out), the sidewalk, the path.
            _approach = new[]
            {
                new Vector3(-1.1f, 0f, 9.9f), new Vector3(0f, 0f, 8.6f), new Vector3(0f, 0f, 5.8f), new Vector3(0f, 0f, 3.2f),
                new Vector3(0f, 0f, T + 1.2f), new Vector3(0f, 0f, 0.6f),
            };
            _step = _approach[^1];
            _offZ = 1.95f; _offY = 0f;
        }

        void NeighbourHouse(MeshKit k, MeshKit g, Vector3 min, Vector3 max, bool leftLit)
        {
            Solid(k, min, max, 0.3f, MeshKit.Siding, OutsideKind.Wood, "House across");
            Roof(k, new Vector3(min.x - 0.3f, min.y, min.z - 0.4f), new Vector3(max.x + 0.3f, max.y, max.z + 0.4f), 2.2f, 0.12f);
            float z = min.z - 0.01f;
            // Windows on the street face (face -z: Cross(y, x) = -z).
            float w1 = min.x + (max.x - min.x) * 0.2f, w2 = min.x + (max.x - min.x) * 0.62f;
            k.Quad(new Vector3(w1, 1.0f, z), new Vector3(0, 1.1f, 0), new Vector3(1.3f, 0, 0), leftLit ? 0.55f : 0.05f, leftLit ? MeshKit.Emissive : MeshKit.Plain);
            k.Quad(new Vector3(w2, 1.0f, z), new Vector3(0, 1.1f, 0), new Vector3(1.3f, 0, 0), leftLit ? 0.05f : 0.45f, leftLit ? MeshKit.Plain : MeshKit.Emissive);
            // Their front door and porch light (a small glow at door height, where it shows at the top of the picture).
            float dx = min.x + (max.x - min.x) * 0.45f;
            k.Quad(new Vector3(dx, 0f, z), new Vector3(0, 2.0f, 0), new Vector3(0.9f, 0, 0), 0.08f, MeshKit.Plain);
            k.Quad(new Vector3(dx + 1.05f, 1.85f, z - 0.005f), new Vector3(0, 0.22f, 0), new Vector3(0.12f, 0, 0), 0.9f, MeshKit.Emissive);
            g.Billboard(new Vector3(dx + 1.11f, 1.96f, z - 0.1f), 0.55f, _camLocal, new Color(0.26f, 0.26f, 0.26f));
        }

        void BuildBackyard(MeshKit k, MeshKit g)
        {
            k.Box(new Vector3(-0.8f, -0.1f, T), new Vector3(0.8f, 0f, T + 1.1f), 0.44f, MeshKit.Concrete);
            Hit("Stoop", new Vector3(-0.8f, -0.1f, T + NearGap), new Vector3(0.8f, 0f, T + 1.1f), OutsideKind.Concrete);
            k.Quad(new Vector3(-0.36f, 0.004f, T + 0.06f), new Vector3(0, 0, 0.5f), new Vector3(0.72f, 0, 0), 0.13f, MeshKit.Asphalt);
            const float fence = Fence;
            Hit("Ground", new Vector3(-40f, -0.105f, T + NearGap), new Vector3(40f, -0.005f, 70f), OutsideKind.Dirt, ground: true);
            k.Quad(new Vector3(-0.45f, -0.005f, T + 1.1f), new Vector3(0, 0, fence - T - 1.1f), new Vector3(0.9f, 0, 0), 0.4f, MeshKit.Concrete, new Vector2(T + 1.1f, -0.45f));
            k.Quad(new Vector3(-24f, -0.03f, T), new Vector3(0, 0, fence - T), new Vector3(48f, 0, 0), 0.44f, MeshKit.Grass, new Vector2(T, -24f));
            k.Quad(new Vector3(-3f, -0.03f, 12f), new Vector3(6f, 0, 0), new Vector3(0, 0, -(12f - fence)), 0.42f, MeshKit.Grass, new Vector2(-3f, 0f));
            // Board fence with an open gate gap on the path (faces the camera: Cross(y, x) = -z), gate posts.
            k.Quad(new Vector3(-12f, -0.05f, fence), new Vector3(0, 1.85f, 0), new Vector3(11.45f, 0, 0), 0.34f, MeshKit.Siding, new Vector2(0f, -12f));
            k.Quad(new Vector3(0.55f, -0.05f, fence), new Vector3(0, 1.85f, 0), new Vector3(11.45f, 0, 0), 0.34f, MeshKit.Siding, new Vector2(0f, 0.55f));
            Hit("Fence", new Vector3(-12f, -0.05f, fence - 0.02f), new Vector3(-0.55f, 1.8f, fence + 0.02f), OutsideKind.Wood);
            Hit("Fence", new Vector3(0.55f, -0.05f, fence - 0.02f), new Vector3(12f, 1.8f, fence + 0.02f), OutsideKind.Wood);
            Solid(k, new Vector3(-0.65f, -0.05f, fence - 0.05f), new Vector3(-0.55f, 1.95f, fence + 0.05f), 0.28f, MeshKit.Plain, OutsideKind.Wood, "Gate post");
            Solid(k, new Vector3(0.55f, -0.05f, fence - 0.05f), new Vector3(0.65f, 1.95f, fence + 0.05f), 0.28f, MeshKit.Plain, OutsideKind.Wood, "Gate post");
            // Bins and an AC unit against the house, a shrub, the neighbours' roofs and trees over the fence.
            Solid(k, new Vector3(1.5f, -0.05f, T + 0.08f), new Vector3(2.1f, 1.05f, T + 0.7f), 0.2f, MeshKit.Plain, OutsideKind.Wood, "Bin");     // plastic: a dull thunk
            Solid(k, new Vector3(2.2f, -0.05f, T + 0.08f), new Vector3(2.8f, 1.05f, T + 0.7f), 0.26f, MeshKit.Plain, OutsideKind.Wood, "Bin");
            Solid(k, new Vector3(-2.5f, -0.05f, T + 0.05f), new Vector3(-1.6f, 0.8f, T + 0.85f), 0.36f, MeshKit.Siding, OutsideKind.Metal, "AC unit");
            k.Blob(new Vector3(-3.6f, 0.4f, T + 0.6f), new Vector3(0.7f, 0.65f, 0.5f), 0.5f, MeshKit.Foliage, 7);
            Solid(k, new Vector3(-12f, -0.05f, 14f), new Vector3(-2f, 3.4f, 22f), 0.28f, MeshKit.Siding, OutsideKind.Wood, "House behind");
            Roof(k, new Vector3(-12.3f, -0.05f, 13.6f), new Vector3(-1.7f, 3.4f, 22.4f), 2.3f, 0.12f);
            Solid(k, new Vector3(3f, -0.05f, 13f), new Vector3(13f, 3.1f, 20f), 0.3f, MeshKit.Siding, OutsideKind.Wood, "House behind");
            Roof(k, new Vector3(2.7f, -0.05f, 12.6f), new Vector3(13.3f, 3.1f, 20.4f), 2.1f, 0.12f);
            k.Quad(new Vector3(5f, 1.0f, 12.99f), new Vector3(0, 1.0f, 0), new Vector3(1.2f, 0, 0), 0.5f, MeshKit.Emissive);
            k.Blob(new Vector3(-7f, 3.8f, 10f), new Vector3(2.4f, 2.2f, 2.4f), 0.42f, MeshKit.Foliage, 8, 8, 12);
            k.Blob(new Vector3(8.5f, 3.4f, 10.5f), new Vector3(2.0f, 2.0f, 2.0f), 0.42f, MeshKit.Foliage, 9, 8, 12);
            k.Quad(new Vector3(-40f, -0.05f, 70f), new Vector3(80f, 0, 0), new Vector3(0, 0, -58f), 0.3f, MeshKit.Grass, new Vector2(-40f, 0f));
            // The neighbour's floodlight on the left house.
            k.Box(new Vector3(-3.3f, 2.8f, 13.75f), new Vector3(-2.9f, 3.05f, 14f), 0.2f, MeshKit.Plain);
            k.Quad(new Vector3(-3.25f, 2.83f, 13.74f), new Vector3(0, 0.18f, 0), new Vector3(0.3f, 0, 0), 1f, MeshKit.Emissive);
            g.Billboard(new Vector3(-3.1f, 2.92f, 13.6f), 0.9f, _camLocal, new Color(0.3f, 0.3f, 0.3f));
            _streetLocal = new Vector3(-3.1f, 2.9f, 13.3f);
            _streetPower = 1.0f; _streetRange = 3.5f;
            // Brett 0.1.43: "the backyard when looking at the right side of it at an angle through the door is so dark it just
            // looks black". No streetlight back here: a floodlight on the house over the bins lights the right side (bins,
            // lawn, the fence's near end), the stoop lamp reaches further, and the moon is brighter. (Three extra terms in the
            // set's shader, and the set is drawn only through the doorway: next to nothing for the headset.)
            k.Box(new Vector3(2.0f, 2.3f, T), new Vector3(2.3f, 2.45f, T + 0.12f), 0.15f, MeshKit.Plain);
            k.Quad(new Vector3(2.03f, 2.3f, T + 0.121f), new Vector3(0.24f, 0, 0), new Vector3(0, 0.12f, 0), 1f, MeshKit.Emissive);
            g.Billboard(new Vector3(2.15f, 2.36f, T + 0.2f), 0.7f, _camLocal, new Color(0.34f, 0.34f, 0.34f));
            _fillLocal = new Vector3(2.15f, 2.2f, T + 0.5f);
            _fillPower = 1.1f; _fillRange = 5f;
            _lampRange = 2.8f;
            _moon = 2.2f;

            _approach = new[]
            {
                new Vector3(0f, 0f, 9.4f), new Vector3(0f, 0f, fence), new Vector3(0f, 0f, 4.6f), new Vector3(0f, 0f, 2.2f),
                new Vector3(0f, 0f, T + 0.85f), new Vector3(0f, 0f, 0.6f),
            };
            _step = _approach[^1];
            _offZ = 1.8f; _offY = 0f;
        }

        /// <summary>The night sky behind everything: brighter at the horizon (town glow), dark overhead.</summary>
        static void Sky(MeshKit k)
        {
            // A ring of quads 60 m out (inside the far clip) from 110° left to 110° right: the wide doorbell view never
            // runs off its edge. Brighter at the horizon (town glow), dark overhead; its foot is under the ground.
            const float R = 60f, from = -110f, to = 110f; const int segs = 12;
            for (int i = 0; i < segs; i++)
            {
                float a0 = Mathf.Deg2Rad * Mathf.Lerp(from, to, i / (float)segs), a1 = Mathf.Deg2Rad * Mathf.Lerp(from, to, (i + 1) / (float)segs);
                var p0 = new Vector3(Mathf.Sin(a0) * R, -2f, Mathf.Cos(a0) * R); var p1 = new Vector3(Mathf.Sin(a1) * R, -2f, Mathf.Cos(a1) * R);
                k.GradientQuad(p0, new Vector3(0f, 45f, 0f), p1 - p0, 0.032f, 0.006f); // Cross(up, along) faces the centre
            }
        }

        float _offZ, _offY;

        readonly List<Vector3> _posts = new();
        /// <summary>Things a crashing car stops dead against on this set (porch space x, z, and radius): the lawn tree.</summary>
        public IReadOnlyList<Vector3> Posts => _posts;

        /// <summary>
        /// OffLeft/OffRight: along a line across the lawn in front of the house, the first spot where a standing man (feet,
        /// hips, head) is wholly outside the camera's frustum, plus 0.6 m.
        /// </summary>
        void FindOffPoints()
        {
            // Both latch sides for the doorbell (the door may still flip its hinge after this): out of view from either.
            var views = new List<Plane[]>();
            foreach (float latch in DoorbellCam ? new[] { 1f, -1f } : new[] { 1f })
            {
                var planes = new Plane[6];
                var view = Matrix4x4.Inverse(Matrix4x4.TRS(CamLocal(latch), CamRotLocal(latch), new Vector3(1f, 1f, -1f)));
                var proj = Matrix4x4.Perspective(VerticalFov, Aspect, NearClip, FarClip);
                GeometryUtility.CalculateFrustumPlanes(proj * view, planes);
                views.Add(planes);
            }
            float Edge(float sign)
            {
                for (float x = 0.3f; x < 16f; x += 0.05f)
                {
                    bool seen = false;
                    foreach (var planes in views)
                        foreach (float h in new[] { 0.1f, 0.9f, 1.75f })
                            if (InFrustum(planes, new Vector3(sign * x, _offY + h, _offZ), 0.2f)) { seen = true; break; }
                    if (!seen) return sign * (x + 0.6f);
                }
                return sign * 16f;
            }
            _offLeft = new Vector3(Edge(-1f), _offY, _offZ);
            _offRight = new Vector3(Edge(1f), _offY, _offZ);
        }

        /// <summary>Is <paramref name="p"/> inside the frustum, with <paramref name="radius"/> of slack (a body's width)?</summary>
        public static bool InFrustum(Plane[] planes, Vector3 p, float radius)
        {
            foreach (var pl in planes) if (pl.GetDistanceToPoint(p) < -radius) return false;
            return true;
        }
    }
}
