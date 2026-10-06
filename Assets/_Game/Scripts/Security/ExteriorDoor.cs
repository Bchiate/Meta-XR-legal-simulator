using System.Collections.Generic;
using HIS.Core;
using HIS.Room;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// The outside of one door for its camera (Brett 0.1.22: "maybe you can see door splintering through camera … I want
    /// the continuity to be very good"). Layer 11 only, drawn only during this porch's camera render (<see cref="PorchSet.AddLive"/>):
    ///  • a panelled exterior leaf (off-white like the real one, knob + deadbolt on the latch side) that copies the real
    ///    <see cref="VirtualDoor"/> leaf's pose right before every feed render — closed, kicked, bursting open, opened gently;
    ///  • a dark interior box behind the opening and the jamb reveals, so an open door shows black, never sky;
    ///  • rounds through the door (<see cref="VirtualDoor.LeafShot"/>): a hole on this leaf at the same leaf-space point as on
    ///    the real one (a torn exit hole — lighter splintered wood round a dark core — or a small entry hole for a round from
    ///    outside; pellets small and many; <see cref="MaxHoles"/>, oldest reused), and splinters + dust blown out onto the
    ///    porch along the round; kicks puff dust at the latch, the breach bursts splinters out;
    ///  • holes clear with the real door's (ResetDoor, polled) and on <see cref="GameSession.WorldCleared"/>;
    ///  • keeps the camera rendering while the door moves or anything is in the air (<see cref="SecurityCamera.KeepLive"/>).
    /// Note: a doorbell/eave camera on the wall can't see a door recessed in that wall; what shows on today's feeds is the
    /// splinters and dust coming out into the picture. The leaf is there for any camera that can see it.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExteriorDoor : MonoBehaviour
    {
        public const int MaxHoles = 40;
        const float Thick = VirtualDoor.LeafDepth, PanelProud = 0.008f, HandleHeight = 0.95f;

        static readonly List<ExteriorDoor> s_all = new();
        public static IReadOnlyList<ExteriorDoor> All => s_all;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_all.Clear();

        public static ExteriorDoor For(VirtualDoor d)
        {
            if (d == null) return null;
            foreach (var x in s_all) if (x != null && x.Door == d) return x;
            return null;
        }

        public VirtualDoor Door { get; private set; }
        public PorchSet Porch { get; private set; }
        /// <summary>The exterior leaf: same world pose (and so the same leaf space) as the real leaf at the last sync.</summary>
        public Transform Leaf => _leaf;
        public Transform Interior => _interior;
        public int HoleCount => _count;
        public ParticleSystem Splinters => _splinters;
        public ParticleSystem Dust => _dust;
        /// <summary>Leaf-space centre of hole <paramref name="i"/> (0 = the oldest still showing), on the face it sits on.</summary>
        public Vector3 HoleLocal(int i) => _holes[(_next - _count + i + MaxHoles * 2) % MaxHoles].Pos;
        /// <summary>The door is moving or splinters/dust are in the air (the camera is kept live).</summary>
        public bool Busy => Time.unscaledTime < _busyUntil || Airborne;
        public bool Airborne => (_splinters != null && _splinters.particleCount > 0) || (_dust != null && _dust.particleCount > 0);
        /// <summary>The real leaf was found (exact pose copy) rather than rebuilt from the door's public numbers.</summary>
        public bool Tracking => _real != null;

        struct Hole { public Vector3 Pos, Normal; public float Core, Rim; public int Seed; }
        readonly Hole[] _holes = new Hole[MaxHoles];
        int _count, _next;

        Transform _leaf, _interior, _real;
        MeshFilter _leafFilter, _holeFilter;
        Mesh _leafMesh, _holeMesh, _interiorMesh, _frameMesh;
        ParticleSystem _splinters, _dust;
        SecurityCamera _cam;
        float _roomSign = 1f, _dir = 1f, _builtDir, _prevAngle, _busyUntil, _nextHolePoll, _lastKick = -10f;
        bool _holesDirty;
        Vector3 _prevRealLocal;
        VirtualDoor.DoorState _prevState;
        bool _burst;
        Vector2 _size;
        readonly List<Transform> _realHoles = new();
        int _realChildCount = -1;

        public static ExteriorDoor Build(VirtualDoor door, PorchSet porch)
        {
            var go = new GameObject($"[HIS] ExteriorDoor ({door.Label})") { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(porch.transform, false); // goes with the porch (door re-picked / room reloaded)
            var x = go.AddComponent<ExteriorDoor>();
            x.Init(door, porch);
            return x;
        }

        void Init(VirtualDoor door, PorchSet porch)
        {
            Door = door; Porch = porch;
            s_all.Add(this);
            var open = door.Opening;
            _size = new Vector2(open.width - 0.006f, open.height - 0.012f); // the real slab's size (VirtualDoor.Layout)
            _roomSign = Vector3.Dot(door.Inward, door.transform.forward) >= 0f ? 1f : -1f;
            _real = door.transform.Find("Hinge/Leaf");

            _leaf = Child("Leaf", transform, out _leafFilter, out var leafR);
            _interior = Child("Interior", transform, out var intF, out var intR);
            var holes = Child("Holes", _leaf, out _holeFilter, out var holeR);
            _interiorMesh = BuildInterior(door, porch, box: true);
            intF.sharedMesh = _interiorMesh;
            // The jamb reveals and sill: seen by the camera and, through the open doorway, by the headset (the wall's depth).
            var frame = Child("Frame", _interior, out var frameF, out var frameR);
            _frameMesh = BuildInterior(door, porch, box: false);
            frameF.sharedMesh = _frameMesh;
            _interior.SetPositionAndRotation(porch.transform.position, porch.transform.rotation);
            _dir = HingeDir();
            RebuildLeaf();
            RebuildHoles();

            _splinters = Particles("Splinters", porch.SetMaterial, stretch: true);
            _dust = Particles("Dust", porch.GlowMaterial, stretch: false);
            foreach (var r in new Renderer[] { frameR, _splinters.GetComponent<ParticleSystemRenderer>(), _dust.GetComponent<ParticleSystemRenderer>() })
                porch.AddLive(r);
            // The dark interior box stands on the room side of the door and the leaf copy sits on the real leaf: the camera's
            // only (seen at an angle through the open doorway, the box blacked out the view: Brett 0.1.32).
            foreach (var r in new Renderer[] { leafR, intR, holeR }) porch.AddFeedOnly(r);
            porch.BeforeRender += Sync;
            _prevAngle = door.OpenAngle; _prevState = door.State;
            Sync();
            Debug.Log($"[HIS] ExteriorDoor {door.Label}: leaf {_size.x:F2}x{_size.y:F2} m, {(Tracking ? "tracking the real leaf" : "from the door's numbers")}");
        }

        void OnEnable()
        {
            VirtualDoor.LeafShot += OnLeafShot;
            GameSession.WorldCleared += ClearHoles;
        }

        void OnDisable()
        {
            VirtualDoor.LeafShot -= OnLeafShot;
            GameSession.WorldCleared -= ClearHoles;
        }

        void OnDestroy()
        {
            s_all.Remove(this);
            if (Porch != null) Porch.BeforeRender -= Sync;
            if (_leafMesh != null) Destroy(_leafMesh);
            if (_holeMesh != null) Destroy(_holeMesh);
            if (_interiorMesh != null) Destroy(_interiorMesh);
            if (_frameMesh != null) Destroy(_frameMesh);
        }

        // ───────────────────────────── following the real door ─────────────────────────────

        /// <summary>Copies the real leaf's pose (called every LateUpdate and right before this porch's camera draws).</summary>
        public void Sync()
        {
            if (Door == null || _leaf == null) return;
            if (_holesDirty) { _holesDirty = false; RebuildHoles(); }
            if (_real == null) _real = Door.transform.Find("Hinge/Leaf");
            float dir = HingeDir();
            if (dir != _builtDir) { _dir = dir; RebuildLeaf(); }
            if (_real != null)
            {
                _leaf.SetPositionAndRotation(_real.position, _real.rotation);
                var s = _real.lossyScale; var p = _leaf.parent != null ? _leaf.parent.lossyScale : Vector3.one;
                _leaf.localScale = new Vector3(s.x / p.x, s.y / p.y, s.z / p.z);
                return;
            }
            // Fallback: rebuilt from the public numbers (VirtualDoor.Layout/ApplyAngle).
            var open = Door.Opening; float R = _roomSign;
            bool hingeAtMin = _dir > 0f;
            var hingeLocal = new Vector3(hingeAtMin ? open.xMin + 0.003f : open.xMax - 0.003f, open.yMin, 0f);
            var hingeRot = Quaternion.Euler(0f, (hingeAtMin ? -R : R) * Door.OpenAngle, 0f);
            var leafLocal = hingeLocal + hingeRot * new Vector3(_dir * _size.x * 0.5f, 0.004f + _size.y * 0.5f, -R * Thick * 0.5f);
            var t = Door.transform;
            _leaf.SetPositionAndRotation(t.TransformPoint(leafLocal), t.rotation * hingeRot);
        }

        /// <summary>+1 when the hinge is on the opening's min-x side (door space), as VirtualDoor.HingeToLatch says.</summary>
        float HingeDir() => Door != null && Vector3.Dot(Door.HingeToLatch, Door.transform.right) < 0f ? -1f : 1f;

        void LateUpdate()
        {
            if (Door == null || Porch == null) { Destroy(gameObject); return; }
            Sync();
            _interior.SetPositionAndRotation(Porch.transform.position, Porch.transform.rotation);
            float now = Time.unscaledTime, dt = Mathf.Max(Time.deltaTime, 1e-4f);
            float ang = Door.OpenAngle; var st = Door.State;
            var realLocal = _real != null ? _real.localPosition : Vector3.zero;
            bool moving = Mathf.Abs(ang - _prevAngle) > 0.01f || st == VirtualDoor.DoorState.Opening || (realLocal - _prevRealLocal).sqrMagnitude > 1e-8f;
            if (moving) _busyUntil = now + 0.4f;
            // A kick the latch survives: the leaf pops in a few degrees — dust puffs out at the latch.
            if (st == VirtualDoor.DoorState.Closed && _prevState == VirtualDoor.DoorState.Closed && ang > 0.8f && _prevAngle <= 0.8f && now - _lastKick > 0.35f)
            { _lastKick = now; KickPuff(false); }
            // The breach: the leaf flies open fast — the frame bursts, splinters and dust out onto the step.
            if (st == VirtualDoor.DoorState.Opening && !_burst && (ang - _prevAngle) / dt > 200f) { _burst = true; KickPuff(true); }
            if (st == VirtualDoor.DoorState.Closed) _burst = false;
            _prevAngle = ang; _prevState = st; _prevRealLocal = realLocal;

            if (_count > 0 && now >= _nextHolePoll) { _nextHolePoll = now + 0.25f; if (!RealHasHoles()) ClearHoles(); }

            if (Busy)
            {
                if (_cam == null) _cam = SecurityCamera.For(Door);
                if (_cam != null) _cam.KeepLive(0.3f);
            }
        }

        /// <summary>The real leaf still shows a hole (ResetDoor hides them all): ours clear with it.</summary>
        bool RealHasHoles()
        {
            if (_real == null) return true; // nothing to poll: WorldCleared clears
            if (_real.childCount != _realChildCount)
            {
                _realChildCount = _real.childCount;
                _realHoles.Clear();
                for (int i = 0; i < _real.childCount; i++) { var c = _real.GetChild(i); if (c.name == "Hole") _realHoles.Add(c); }
            }
            foreach (var h in _realHoles) if (h != null && h.gameObject.activeSelf) return true;
            return false;
        }

        // ───────────────────────────── rounds through the leaf ─────────────────────────────

        void OnLeafShot(VirtualDoor door, VirtualDoor.LeafHit h, Vector3 dir, bool pellet)
        {
            if (door != Door || _leaf == null) return;
            Sync();
            var outN = new Vector3(0f, 0f, -_roomSign); // the leaf's outside face normal, in leaf space
            bool exitOutside = h.LocalOutNormal != Vector3.zero && Vector3.Dot(h.LocalOutNormal, outN) > 0.5f;
            bool entryOutside = Vector3.Dot(h.LocalInNormal, outN) > 0.5f;
            var dirN = dir.sqrMagnitude > 1e-6f ? dir.normalized : Door.Outward;
            if (exitOutside)
            {
                AddHole(h.LocalOut, h.LocalOutNormal, pellet ? 0.006f : 0.014f, pellet ? 0.016f : 0.045f);
                BlowOut(h.ExitPoint, dirN, _leaf.TransformDirection(h.LocalOutNormal), pellet ? 4 : 24, pellet ? 2 : 9, pellet ? 0.7f : 1f);
            }
            else if (entryOutside)
            {
                // A round from outside: a neat entry hole, a few chips kicked back toward the shooter.
                AddHole(h.LocalIn, h.LocalInNormal, pellet ? 0.004f : 0.0055f, pellet ? 0.007f : 0.01f);
                BlowOut(h.Point, -dirN, _leaf.TransformDirection(h.LocalInNormal), pellet ? 1 : 4, 1, 0.5f);
            }
            else
            {
                // Through the edge of the slab: just the splinters where it came out.
                BlowOut(h.ExitPoint, dirN, dirN, pellet ? 2 : 8, pellet ? 1 : 3, 0.8f);
            }
        }

        void AddHole(Vector3 pos, Vector3 normal, float core, float rim)
        {
            _holes[_next] = new Hole { Pos = pos, Normal = normal, Core = core, Rim = rim, Seed = Random.Range(1, 1 << 20) };
            _next = (_next + 1) % MaxHoles;
            _count = Mathf.Min(_count + 1, MaxHoles);
            _holesDirty = true; // one rebuild for a whole shotgun blast (Sync, before the next render)
        }

        public void ClearHoles()
        {
            if (_count == 0) return;
            _count = 0; _next = 0;
            _holesDirty = true;
        }

        void BlowOut(Vector3 at, Vector3 dir, Vector3 faceN, int splinters, int dust, float power)
        {
            var main = (dir * 0.75f + faceN * 0.25f).normalized;
            for (int i = 0; i < splinters; i++)
            {
                var v = Spread(main, 28f) * Random.Range(2.5f, 6.5f) * power;
                _splinters.Emit(new ParticleSystem.EmitParams
                {
                    position = at + Random.insideUnitSphere * 0.015f, velocity = v, applyShapeToPosition = false,
                    startSize = Random.Range(0.015f, 0.045f), startLifetime = Random.Range(0.7f, 1.3f),
                    startColor = SplinterColour(), rotation = Random.Range(0f, 360f),
                }, 1);
            }
            for (int i = 0; i < dust; i++)
            {
                var v = Spread(main, 40f) * Random.Range(0.6f, 1.8f) * power;
                _dust.Emit(new ParticleSystem.EmitParams
                {
                    position = at + main * 0.05f + Random.insideUnitSphere * 0.03f, velocity = v, applyShapeToPosition = false,
                    startSize = Random.Range(0.18f, 0.42f), startLifetime = Random.Range(0.9f, 1.6f),
                    startColor = new Color(0.32f, 0.32f, 0.32f, 1f), rotation = Random.Range(0f, 360f), // IR backscatter: bright
                }, 1);
            }
            _busyUntil = Mathf.Max(_busyUntil, Time.unscaledTime + 0.3f);
            if (_cam == null) _cam = SecurityCamera.For(Door);
            if (_cam != null) _cam.KeepLive(0.5f);
        }

        /// <summary>A kick at the latch (small puff) or the breach (the frame bursts).</summary>
        void KickPuff(bool breach)
        {
            if (_leaf == null) return;
            float x = _dir * (_size.x * 0.5f - 0.02f), y = HandleHeight - (0.004f + _size.y * 0.5f), z = -_roomSign * Thick * 0.5f;
            var at = _leaf.TransformPoint(new Vector3(x, y, z));
            var outward = Door.Outward;
            BlowOut(at, (outward + Vector3.up * 0.15f).normalized, outward, breach ? 30 : 3, breach ? 16 : 4, breach ? 0.9f : 0.35f);
        }

        static Vector3 Spread(Vector3 dir, float degrees)
        {
            var r = Random.insideUnitCircle * Mathf.Tan(degrees * Mathf.Deg2Rad);
            var right = Vector3.Cross(Mathf.Abs(dir.y) > 0.95f ? Vector3.forward : Vector3.up, dir).normalized;
            var up = Vector3.Cross(dir, right);
            return (dir + right * r.x + up * r.y).normalized;
        }

        static Color SplinterColour()
        {
            float g = Random.Range(0.55f, 0.95f); // raw wood and paint chips; alpha 0 = HIS/SecurityPorch "plain" (IR-lit)
            return new Color(g, g, g, 0f);
        }

        // ───────────────────────────── meshes ─────────────────────────────

        Transform Child(string name, Transform parent, out MeshFilter filter, out MeshRenderer renderer)
        {
            var go = new GameObject(name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(parent, false);
            filter = go.AddComponent<MeshFilter>();
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Porch.SetMaterial;
            Quiet(renderer);
            return go.transform;
        }

        static void Quiet(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
        }

        /// <summary>The slab in leaf space (centred), raised panels, knob and deadbolt on the outside face (z = -roomSign · 2 cm).</summary>
        void RebuildLeaf()
        {
            _builtDir = _dir;
            float lw = _size.x, lh = _size.y, R = _roomSign, zo = -R * Thick * 0.5f; // the outside face
            var k = new MeshKit();
            const float paint = 0.78f;
            k.Box(new Vector3(-lw * 0.5f, -lh * 0.5f, -Thick * 0.5f), new Vector3(lw * 0.5f, lh * 0.5f, Thick * 0.5f), paint, MeshKit.Plain, bottom: true);
            // Raised panels (as on the real room side): a dark groove round each, the panel a touch brighter.
            Panel(k, new Vector2(0f, lh * 0.2f), new Vector2(lw * 0.62f, lh * 0.34f), zo, R);
            Panel(k, new Vector2(0f, -lh * 0.25f), new Vector2(lw * 0.62f, lh * 0.3f), zo, R);
            // Knob and deadbolt on the latch side, dark metal.
            float kx = _dir * (lw * 0.5f - 0.065f), ky = HandleHeight - (0.004f + lh * 0.5f);
            Slab(k, new Vector3(kx, ky, zo), new Vector3(0.06f, 0.06f, 0.01f), R, 0.22f);
            Slab(k, new Vector3(kx, ky, zo - R * 0.01f), new Vector3(0.018f, 0.018f, 0.03f), R, 0.3f);
            k.Blob(new Vector3(kx, ky, zo - R * 0.055f), new Vector3(0.03f, 0.03f, 0.022f), 0.42f, MeshKit.Plain, 11, 5, 8);
            Slab(k, new Vector3(kx, ky + 0.15f, zo), new Vector3(0.055f, 0.055f, 0.012f), R, 0.3f);
            var old = _leafMesh;
            _leafMesh = k.ToMesh($"ExteriorLeaf {Door.Label}");
            _leafFilter.sharedMesh = _leafMesh;
            if (old != null) Destroy(old);
        }

        static void Panel(MeshKit k, Vector2 c, Vector2 size, float zo, float R)
        {
            Slab(k, new Vector3(c.x, c.y, zo), new Vector3(size.x + 0.03f, size.y + 0.03f, 0.002f), R, 0.45f); // the groove
            Slab(k, new Vector3(c.x, c.y, zo), new Vector3(size.x, size.y, PanelProud), R, 0.84f);
        }

        /// <summary>A box standing <paramref name="size"/>.z proud of the outside face at <paramref name="face"/> (leaf space).</summary>
        static void Slab(MeshKit k, Vector3 face, Vector3 size, float R, float grey)
        {
            float z0 = face.z, z1 = face.z - R * size.z;
            k.Box(new Vector3(face.x - size.x * 0.5f, face.y - size.y * 0.5f, Mathf.Min(z0, z1)),
                  new Vector3(face.x + size.x * 0.5f, face.y + size.y * 0.5f, Mathf.Max(z0, z1)), grey, MeshKit.Plain, bottom: true);
        }

        bool OnPanel(Vector3 p)
        {
            float lw = _size.x, lh = _size.y;
            if (Mathf.Abs(p.x) > lw * 0.31f) return false;
            return Mathf.Abs(p.y - lh * 0.2f) < lh * 0.17f || Mathf.Abs(p.y + lh * 0.25f) < lh * 0.15f;
        }

        /// <summary>All holes in one mesh (leaf space): a jagged ring of light torn wood round a dark core, 2 mm off the face.</summary>
        void RebuildHoles()
        {
            var k = new MeshKit();
            var outN = new Vector3(0f, 0f, -_roomSign);
            var wood = MeshKit.Grey(0.93f); var core = MeshKit.Grey(0.012f); var edge = MeshKit.Grey(0.6f);
            for (int i = 0; i < _count; i++)
            {
                var h = _holes[(_next - _count + i + MaxHoles * 2) % MaxHoles];
                var n = h.Normal.sqrMagnitude > 0.5f ? h.Normal.normalized : outN;
                float lift = 0.002f + (Vector3.Dot(n, outN) > 0.9f && OnPanel(h.Pos) ? PanelProud : 0f);
                var c = h.Pos + n * lift;
                var u = Vector3.Cross(Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up, n).normalized;
                var w = Vector3.Cross(n, u);
                var rng = new System.Random(h.Seed);
                const int seg = 10;
                var inner = new Vector3[seg]; var outer = new Vector3[seg]; var mid = new Vector3[seg];
                float spin = (float)rng.NextDouble() * Mathf.PI * 2f;
                for (int s = 0; s < seg; s++)
                {
                    float a = spin + s * Mathf.PI * 2f / seg;
                    var d = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                    float ci = h.Core * (0.8f + 0.4f * (float)rng.NextDouble());
                    float spike = (s & 1) == 0 ? 0.75f + 0.5f * (float)rng.NextDouble() : 0.45f + 0.25f * (float)rng.NextDouble();
                    inner[s] = c + d * ci;
                    mid[s] = c + d * (ci + (h.Rim - h.Core) * 0.35f * spike);
                    outer[s] = c + d * (ci + (h.Rim - h.Core) * spike);
                }
                for (int s = 0; s < seg; s++)
                {
                    int t = (s + 1) % seg;
                    Tri(k, c, inner[s], inner[t], core, n);
                    Tri(k, inner[s], mid[s], mid[t], wood, n); Tri(k, inner[s], mid[t], inner[t], wood, n);
                    Tri(k, mid[s], outer[s], mid[t], edge, n); // the splinter tips, a touch darker (paint torn off)
                }
            }
            var old = _holeMesh;
            _holeMesh = k.ToMesh($"ExteriorHoles {Door?.Label}");
            _holeFilter.sharedMesh = _holeMesh;
            if (old != null) Destroy(old);
        }

        static void Tri(MeshKit k, Vector3 a, Vector3 b, Vector3 c, Color32 col, Vector3 n)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) < 0f) (b, c) = (c, b);
            k.Tri(a, b, c, col);
        }

        /// <summary>
        /// Porch space (origin the door centre on the floor, +z out): a black box behind the opening, the jamb reveals from the
        /// leaf's plane out to the siding, and a threshold.
        /// </summary>
        static Mesh BuildInterior(VirtualDoor door, PorchSet porch, bool box)
        {
            var k = new MeshKit();
            float w = door.Opening.width, hole = w * 0.5f + 0.02f;
            float top = door.transform.TransformPoint(new Vector3(door.Opening.center.x, door.Opening.yMax, 0f)).y - porch.FloorY;
            if (top < 1.7f || top > 2.8f) top = 2.03f;
            float holeTop = top + 0.02f, back = -1.6f, T = PorchSet.WallOut;
            const float dark = 0.012f, reveal = 0.62f;
            if (box)
            {
            // The dark room: five inward faces (Quad(o, u, w) faces Cross(u, w)).
            k.Quad(new Vector3(-hole, 0f, back), new Vector3(2f * hole, 0, 0), new Vector3(0, holeTop, 0), dark, MeshKit.Plain);           // back wall, faces +z
            k.Quad(new Vector3(-hole, 0f, back), new Vector3(0, holeTop, 0), new Vector3(0, 0, -back), dark, MeshKit.Plain);                // left, faces +x
            k.Quad(new Vector3(hole, 0f, back), new Vector3(0, 0, -back), new Vector3(0, holeTop, 0), dark, MeshKit.Plain);                 // right, faces -x
            k.Quad(new Vector3(-hole, 0.002f, back), new Vector3(0, 0, -back), new Vector3(2f * hole, 0, 0), dark, MeshKit.Plain);          // floor, faces +y
            k.Quad(new Vector3(-hole, holeTop, back), new Vector3(2f * hole, 0, 0), new Vector3(0, 0, -back), dark, MeshKit.Plain);         // ceiling, faces -y
            return k.ToMesh($"ExteriorInterior {door.Label}");
            }
            // Jamb reveals from the door plane to the siding (painted trim) against the leaf's edges, the head over it, and the
            // jamb's face between the reveal and the siding's cut (the casing starts there).
            float j = w * 0.5f + 0.004f, jt = top + 0.002f, d = T + 0.001f;
            k.Quad(new Vector3(-j, 0f, 0f), new Vector3(0, jt, 0), new Vector3(0, 0, d), reveal, MeshKit.Plain);                           // left jamb, faces +x
            k.Quad(new Vector3(j, 0f, 0f), new Vector3(0, 0, d), new Vector3(0, jt, 0), reveal, MeshKit.Plain);                            // right jamb, faces -x
            k.Quad(new Vector3(-j, jt, 0f), new Vector3(2f * j, 0, 0), new Vector3(0, 0, d), reveal * 0.8f, MeshKit.Plain);                // head, faces -y
            k.Quad(new Vector3(-hole - 0.001f, 0f, d), new Vector3(hole - j + 0.001f, 0, 0), new Vector3(0, holeTop, 0), 0.72f, MeshKit.Plain); // faces +z
            k.Quad(new Vector3(j, 0f, d), new Vector3(hole - j + 0.001f, 0, 0), new Vector3(0, holeTop, 0), 0.72f, MeshKit.Plain);
            k.Quad(new Vector3(-j, jt, d), new Vector3(2f * j, 0, 0), new Vector3(0, holeTop - jt, 0), 0.72f, MeshKit.Plain);
            // Threshold: a worn sill from the leaf out to the stoop (faces +y).
            k.Quad(new Vector3(-hole, 0.003f, 0f), new Vector3(0, 0, T + 0.02f), new Vector3(2f * hole, 0, 0), 0.5f, MeshKit.Concrete);
            return k.ToMesh($"ExteriorFrame {door.Label}");
        }

        ParticleSystem Particles(string name, Material mat, bool stretch)
        {
            var go = new GameObject(name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate; // the feed renders rarely: never pause on "not visible"
            main.gravityModifier = stretch ? 1f : -0.02f;
            main.maxParticles = stretch ? 200 : 80;
            main.startLifetime = 1f; main.startSpeed = 0f; main.startSize = 0.02f;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            if (stretch)
            {
                // Chips tumble onto the step and stop (a floor at the porch height), fading only at the very end.
                var col = ps.collision; col.enabled = true; col.type = ParticleSystemCollisionType.Planes; col.mode = ParticleSystemCollisionMode.Collision3D;
                col.SetPlane(0, Porch.transform); col.dampen = 0.75f; col.bounce = 0.2f; col.lifetimeLoss = 0f;
                main.startLifetime = 1.2f;
            }
            else
            {
                var vel = ps.limitVelocityOverLifetime; vel.enabled = true; vel.limit = 0.25f; vel.dampen = 0.08f;
                var size = ps.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.8f));
                var colour = ps.colorOverLifetime; colour.enabled = true;
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.5f, 0.5f, 0.5f), 0.4f), new GradientColorKey(Color.black, 1f) },
                          new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
                colour.color = g; // additive glow: fading to black = fading out
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = 2.2f; r.velocityScale = 0.012f; }
            else r.renderMode = ParticleSystemRenderMode.Billboard;
            Quiet(r);
            return ps;
        }
    }
}
