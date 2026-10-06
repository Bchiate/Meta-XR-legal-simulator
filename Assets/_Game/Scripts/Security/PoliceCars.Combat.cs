using System.Collections.Generic;
using HIS.Core;
using HIS.Weapons;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Security
{
    /// <summary>
    /// Shootable police vehicles (Brett 0.1.33: "making it so i can shoot them as they pull up shoot out glass and hit driver
    /// maybe they crash if its not too much for the headset"). No physics engine: box colliders for rounds, scripted motion.
    ///  • The wave rides in: the driver (and his partner, a third in the back; the truck's team in its closed box) sit in their
    ///    seats (IntruderPolice.RideIn), seen through the glass on the feed and through the open doorway.
    ///  • Hits, Hollywood style (Brett 0.1.40: "the cars dont show like theyre getting hit or anything. Last Stand is very good
    ///    at this, look at how they do it"). The street is dark and 2–15 m away, so every hit is big, bright and lasting:
    ///    – Body (<see cref="VehicleBody"/>): an orange-white spark burst, a flash, a puff and paint chips, and a hole that stays
    ///      (a dark 1.5 cm hole in a bright 5 cm ring of bare metal); the round stops. ~8 body hits: the hood smokes.
    ///    – Glass (<see cref="VehicleGlass"/>, one collider per pane): a big bright spider crack and glass bits, and the round
    ///      goes on into whoever is behind it. The 3rd hit on the same pane (or a shotgun volley's pellets) shatters it: the
    ///      pane is gone, with its cracks and its collider (rounds pass through the opening), in a shower of glittering bits.
    ///    – The light bar: a hit breaks it (sparks, red and blue lens bits) and that car's bar goes dark for the rest of the call.
    ///    All of it is pooled: four shared particle systems (Emit only) and one mark mesh per thing marks stick to.
    ///  • The driver shot dead while it's still moving: it rolls on, veers toward the house, jolts over the curb onto the lawn
    ///    or crunches into the other car, and stops; the horn blares a few seconds (he's slumped on it). The others get out.
    ///  • Walkers go round the parked vehicles (<see cref="RouteAround"/>; 0.1.33: wave 2 walked through the first car).
    /// All of it is the same for a Marshal night's hitman vehicles (research/27; five since Brett 0.1.57: two sedans, two SUVs,
    /// the van): they ride in, a dead driver crashes the car, the glass cracks and goes; they have no light bar to break (the
    /// SUVs and the van an underbody that stops rounds).
    /// </summary>
    public partial class PoliceCars
    {
        // A seated rider's root (his feet, below the car's floor out of sight): hips on the cushion just in front of the seat back,
        // head under the roof (1.48 m) and above the belt line (0.95 m). 0.1.48 had them kneeling (root 0.22, z -0.3).
        // The front seats sit in their door's opening (2026-10-05, the cars fixer: at z −0.5 the driver sat behind the B pillar, in
        // the rear door's window, and slid 0.6 m forward on his way out): his head in the front door's window, his hands just
        // behind the dash, his legs out through the front of the opening. The back seat 0.8 m behind them, in the rear door's
        // window; the front seat backs and headrests (CarBody) go with SeatZ.
        const float SeatY = 0.02f, SeatZ = 0.2f, RearSeatY = -0.04f, RearSeatZ = -0.62f, HornSeconds = 5f;
        /// <summary>What a <see cref="VehicleBody"/> collider covers: the shell, or the light bar on the roof.</summary>
        internal const int BodyPart = 0, LightBarPart = 1;
        // Marks kept per thing they stick to (the oldest goes); hits on one pane to shatter it; body hits before the hood smokes.
        const int BodyCap = 28, DoorCap = 16, PaneCap = 8, ShatterAt = 3, SmokeAfter = 8;
        const float PaneThick = 0.04f, SmokeRate = 6f;
        float _combatT;

        /// <summary>One pane of glass: one part, or two (a cruiser's side glass + its front door's window), shattered together.</summary>
        class Pane
        {
            public readonly List<GameObject> Parts = new();
            public readonly List<BoxCollider> Boxes = new();
            public int Hits, LastFrame = -1;
            public Vector3 LastDir;
            public bool Shattered;
        }

        /// <summary>Bullet marks stuck to one thing (the car body, a door, a pane): one mesh rebuilt per hit, one draw call.</summary>
        class Decals
        {
            public Transform Parent, T;
            public Mesh Mesh;
            public int Cap;
            public readonly List<MarkRec> Marks = new();
        }

        /// <summary>A mark in its parent's space: +z faces the shooter; <see cref="Clip"/> (min x, max x, min y, max y) keeps a crack on its pane.</summary>
        struct MarkRec
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector4 Clip;
            public uint Seed;
            public bool Glass;
        }

        // ───────────────────────────── hits ─────────────────────────────

        /// <summary>A body collider (the round stops: a hole and sparks): the shell, or <see cref="LightBarPart"/>.</summary>
        void Surface(Car c, string name, Vector3 center, Vector3 size, int part = BodyPart)
        {
            var go = new GameObject("Hit" + name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(c.Root, false);
            go.transform.localPosition = center;
            go.AddComponent<BoxCollider>().size = size;
            var b = go.AddComponent<VehicleBody>(); b.Cars = this; b.Vehicle = _cars.Count; b.Part = part; // (being built: its index once added)
        }

        /// <summary>
        /// A pane of glass: its own see-through part under <paramref name="parent"/> (the car, or a door's hinge) and a thin box
        /// collider in its plane (<see cref="VehicleGlass"/>, pane <paramref name="pane"/>). Corners: the bottom edge
        /// <paramref name="a"/>→<paramref name="b"/>, the top edge <paramref name="d"/>→<paramref name="cTop"/> (above a and b).
        /// <paramref name="u0"/>–<paramref name="u1"/>: the stretch of the bottom edge the collider covers (a side glass
        /// behind a door's window leaves that part to the window's own collider: one round, one hit).
        /// </summary>
        void AddPane(Car c, Transform parent, string name, int pane, Vector3 a, Vector3 b, Vector3 cTop, Vector3 d, Vector3 outward,
                     float grey, float u0 = 0f, float u1 = 1f)
        {
            var k = new MeshKit();
            Face(k, a, b, cTop, d, MeshKit.Grey(grey), outward);
            var part = Part(parent, name, k, _glassMat);
            var u = b - a; float bottom = u.magnitude; var ud = u / bottom;
            var h = (d + cTop) * 0.5f - (a + b) * 0.5f; h -= ud * Vector3.Dot(h, ud);
            var n = Vector3.Cross(ud, h).normalized;
            if (Vector3.Dot(n, outward) < 0f) n = -n;
            bool partial = u1 - u0 < 0.999f;
            float width = partial ? bottom * (u1 - u0) : Mathf.Lerp(bottom, (cTop - d).magnitude, 0.35f);
            var centre = (a + b + cTop + d) * 0.25f + (partial ? ud * (bottom * ((u0 + u1) * 0.5f - 0.5f)) : Vector3.zero);
            var go = new GameObject("Hit" + name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(part, false);
            go.transform.localPosition = centre;
            go.transform.localRotation = Quaternion.LookRotation(n, h);   // z out of the pane, x along its bottom edge
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(width, h.magnitude, PaneThick);
            var g = go.AddComponent<VehicleGlass>(); g.Cars = this; g.Vehicle = _cars.Count; g.Pane = pane; g.Part = part;
            while (c.Panes.Count <= pane) c.Panes.Add(new Pane());
            c.Panes[pane].Parts.Add(part.gameObject);
            c.Panes[pane].Boxes.Add(box);
        }

        internal void OnGlassHit(VehicleGlass g, in DamageInfo info)
        {
            if (g == null || g.Vehicle < 0 || g.Vehicle >= _cars.Count) return;
            var c = _cars[g.Vehicle];
            if (c.Root == null || _porch == null || g.Pane < 0 || g.Pane >= c.Panes.Count) return;
            _stirredAt = Time.time;                    // (the feed shows the hit: HitmenStill)
            var p = c.Panes[g.Pane];
            if (p.Shattered) return;
            // The same round through both parts of one pane (a door's window, then the side glass behind it) is one hit;
            // a shotgun's pellets (other directions, same frame) each count: a volley shatters it.
            bool same = p.LastFrame == Time.frameCount && Vector3.Dot(p.LastDir, info.Direction) > 0.99999f;
            p.LastFrame = Time.frameCount; p.LastDir = info.Direction;
            if (!same) p.Hits++;
            EnsureFx();
            if (p.Hits >= ShatterAt) { Shatter(c, p, g.Pane, info); return; }
            Crack(c, g, info);
            GlassBits(info.Point, info.Normal, info.Direction, 18, 6);
            Flash(info.Point + info.Normal * 0.03f, 0.3f);
            SoundAt("glass.hit", info.Point, 0.85f);
            Log.V($"[HIS] PoliceCars: {c.Root.name} pane {g.Pane} hit ({p.Hits}/{ShatterAt})");
        }

        internal void OnBodyHit(int vehicle, int part, in DamageInfo info)
        {
            if (vehicle < 0 || vehicle >= _cars.Count) return;
            var c = _cars[vehicle];
            if (c.Root == null || _porch == null) return;
            _stirredAt = Time.time;                    // (the feed shows the hit: HitmenStill)
            EnsureFx();
            Hole(c, info.Point, info.Normal);
            Sparks(info.Point, info.Normal, info.Direction, Random.Range(8, 16), 5f);
            Flash(info.Point + info.Normal * 0.03f, Random.Range(0.35f, 0.45f));
            Puff(c, info.Point, info.Normal, info.Direction);
            SoundAt("metal.hit", info.Point, 0.75f);
            if (part == LightBarPart) { if (!c.BarBroken) BreakBar(c, info); return; }
            if (++c.BodyHits == SmokeAfter)
            {
                c.Smoking = true;
                Debug.Log($"[HIS] PoliceCars: {c.Root.name} took {SmokeAfter} body hits — its hood smokes");
            }
        }

        /// <summary>From outside, through the shut front door it's muffled.</summary>
        void SoundAt(string key, Vector3 p, float volume)
        {
            var door = _porch != null ? _porch.Door : null;
            if (door != null && !door.IsOpen) door.PlayThrough(key, p, volume, 1f, 900f);
            else Sfx.Play(key, p, volume);
        }

        /// <summary>The third hit (or a volley): the pane is gone — its parts, cracks and colliders — in a shower of glass.</summary>
        void Shatter(Car c, Pane p, int pane, in DamageInfo info)
        {
            p.Shattered = true;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            foreach (var box in p.Boxes)
            {
                if (box == null || !box.gameObject.activeInHierarchy) continue;
                var t = box.transform; var s = box.size;
                int n = Mathf.Clamp(Mathf.RoundToInt(s.x * s.y * 60f), 22, 60);
                for (int i = 0; i < n; i++)   // across the whole pane: mostly dropping, some on into the car, some out
                {
                    ep.position = t.TransformPoint(new Vector3((Random.value - 0.5f) * s.x, (Random.value - 0.5f) * s.y, 0f));
                    ep.velocity = info.Direction * Random.Range(0.2f, 1.4f) + t.forward * Random.Range(-0.3f, 0.6f) + Random.insideUnitSphere * 0.5f;
                    ep.startSize = Random.Range(0.012f, 0.035f); ep.startLifetime = Random.Range(1.0f, 1.8f);
                    ep.rotation = Random.Range(0f, 360f); ep.angularVelocity = Random.Range(-720f, 720f);
                    ep.startColor = Random.value < 0.25f ? Emissive(0.55f) : Glitter();
                    _bits.Emit(ep, 1);
                }
                ep.rotation = 0f; ep.angularVelocity = 0f;
                for (int i = 0; i < 10; i++)  // glints: tiny bright streaks falling with them
                {
                    ep.position = t.TransformPoint(new Vector3((Random.value - 0.5f) * s.x, (Random.value - 0.5f) * s.y, 0f));
                    ep.velocity = Random.insideUnitSphere * 0.6f + Vector3.down * Random.Range(0.3f, 1.2f);
                    ep.startSize = Random.Range(0.012f, 0.022f); ep.startLifetime = Random.Range(0.4f, 0.9f);
                    ep.startColor = GlintColour;
                    _sparks.Emit(ep, 1);
                }
            }
            GlassBits(info.Point, info.Normal, info.Direction, 14, 6);
            Flash(info.Point + info.Normal * 0.03f, 0.5f);
            foreach (var part in p.Parts) if (part != null) part.SetActive(false);   // its collider and cracks with it
            // Louder than a crack: a "glass.shatter" clip if the library has one, else two glass.hit takes at once (no cooldown on
            // that key), the second from the middle of the pane.
            if (Sfx.Has("glass.shatter")) SoundAt("glass.shatter", info.Point, 1f);
            else
            {
                SoundAt("glass.hit", info.Point, 1f);
                if (p.Boxes.Count > 0 && p.Boxes[0] != null) SoundAt("glass.hit", p.Boxes[0].transform.position, 0.9f);
            }
            Debug.Log($"[HIS] PoliceCars: {c.Root.name} pane {pane} shattered");
        }

        /// <summary>The light bar is hit: sparks, red and blue lens bits, and it goes dark (<see cref="Step"/> skips it).</summary>
        void BreakBar(Car c, in DamageInfo info)
        {
            c.BarBroken = true;
            foreach (var t in new[] { c.LensRed, c.LensBlue, c.GlowRed, c.GlowBlue }) if (t != null) t.gameObject.SetActive(false);
            Sparks(info.Point, info.Normal, info.Direction, 18, 5.5f);
            Flash(info.Point + info.Normal * 0.05f, 0.65f);
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < 24; i++)
            {
                bool red = (i & 1) == 0;
                ep.position = c.Root.TransformPoint(red ? c.RedLocal : c.BlueLocal) + Random.insideUnitSphere * 0.12f;
                ep.velocity = (Vector3.up * 1.2f + info.Direction * 0.8f + Random.insideUnitSphere * 1.4f) * Random.Range(0.8f, 1.6f);
                ep.startSize = Random.Range(0.015f, 0.035f); ep.startLifetime = Random.Range(0.9f, 1.5f);
                ep.rotation = Random.Range(0f, 360f); ep.angularVelocity = Random.Range(-720f, 720f);
                ep.startColor = red ? LensRedBit : LensBlueBit;
                _bits.Emit(ep, 1);
            }
            SoundAt("glass.hit", info.Point, 0.9f);
            Debug.Log($"[HIS] PoliceCars: {c.Root.name}'s light bar shot out");
        }

        // ── marks ──

        static readonly MeshKit s_kit = new();

        /// <summary>
        /// A bullet hole in the body: dark hole, bright bare-metal ring. On a door it sticks to the door (and rides it open), out
        /// on the door's skin: the body's collider is the closed shell, a few cm inside the skin (before 0.1.41 a hole in a
        /// cruiser's door was drawn inside it — never seen).
        /// </summary>
        void Hole(Car c, Vector3 point, Vector3 normal)
        {
            Transform parent = c.Root; int cap = BodyCap;
            var pos = c.Root.InverseTransformPoint(point + normal * 0.003f);
            for (int i = 0; i < c.Doors.Count && i < c.DoorBoxes.Count; i++)
            {
                var hinge = c.Doors[i].Hinge;
                if (hinge == null) continue;
                var leaf = c.DoorBoxes[i];
                var hp = hinge.InverseTransformPoint(point);
                if (leaf.SqrDistance(hp) > 0.08f * 0.08f) continue;
                var hn = hinge.InverseTransformDirection(normal);
                pos = hp + hn * (ExitDistance(leaf, hp, hn) + 0.003f);
                parent = hinge; cap = DoorCap;
                break;
            }
            var rot = Quaternion.LookRotation(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up);
            AddMark(c, parent, cap, new MarkRec
            {
                Pos = pos, Rot = Quaternion.Inverse(parent.rotation) * rot,
                Clip = new Vector4(-10f, 10f, -10f, 10f), Seed = NewSeed(), Glass = false,
            });
        }

        /// <summary>How far along <paramref name="n"/> from <paramref name="p"/> until it is out of <paramref name="b"/>'s far face (≥ 0).</summary>
        static float ExitDistance(Bounds b, Vector3 p, Vector3 n)
        {
            float t = float.MaxValue;
            for (int a = 0; a < 3; a++)
                if (Mathf.Abs(n[a]) > 1e-3f) t = Mathf.Min(t, ((n[a] > 0f ? b.max[a] : b.min[a]) - p[a]) / n[a]);
            return t == float.MaxValue ? 0f : Mathf.Max(0f, t);
        }

        /// <summary>A spider crack on the pane, on the side the round came from, kept inside the pane's box.</summary>
        void Crack(Car c, VehicleGlass g, in DamageInfo info)
        {
            var box = g.GetComponent<BoxCollider>();
            if (box == null || g.Part == null) return;
            var t = g.transform;
            var l = t.InverseTransformPoint(info.Point);
            float hx = Mathf.Max(0.02f, box.size.x * 0.5f - 0.01f), hy = Mathf.Max(0.02f, box.size.y * 0.5f - 0.01f);
            l.x = Mathf.Clamp(l.x, -hx, hx); l.y = Mathf.Clamp(l.y, -hy, hy);
            bool front = Vector3.Dot(t.forward, info.Direction) <= 0f;   // came from outside (else: the far pane, seen from inside)
            l.z = front ? 0.004f : -0.004f;
            var clip = front ? new Vector4(-hx - l.x, hx - l.x, -hy - l.y, hy - l.y) : new Vector4(l.x - hx, l.x + hx, -hy - l.y, hy - l.y);
            AddMark(c, g.Part, PaneCap, new MarkRec
            {
                Pos = t.localPosition + t.localRotation * l,
                Rot = t.localRotation * (front ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f)),
                Clip = clip, Seed = NewSeed(), Glass = true,
            });
        }

        void AddMark(Car c, Transform parent, int cap, in MarkRec m)
        {
            Decals d = null;
            foreach (var x in c.Decals) if (x.Parent == parent) { d = x; break; }
            if (d == null)
            {
                var go = new GameObject("Marks") { layer = PorchSet.OutsideLayer };
                go.transform.SetParent(parent, false);
                var mesh = new Mesh { name = $"{c.Root.name} {parent.name} marks" };
                mesh.MarkDynamic();
                _meshes.Add(mesh);
                LiveRenderer(go, mesh, _carMat);
                d = new Decals { Parent = parent, T = go.transform, Mesh = mesh, Cap = cap };
                c.Decals.Add(d);
            }
            if (d.Marks.Count >= d.Cap) d.Marks.RemoveAt(0);
            d.Marks.Add(m);
            var k = s_kit; k.Clear();
            foreach (var r in d.Marks) { if (r.Glass) CrackMesh(k, r); else HoleMesh(k, r); }
            k.Apply(d.Mesh);
            d.T.gameObject.SetActive(true);
        }

        static int MarkCount(Car c) { int n = 0; foreach (var d in c.Decals) n += d.Marks.Count; return n; }

        static uint NewSeed() => (uint)Random.Range(1, int.MaxValue);

        /// <summary>xorshift: the same mark rebuilds the same shape from its seed (no allocation).</summary>
        static float Rnd(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 16777216f; }
        static float Rnd(ref uint s, float a, float b) => a + (b - a) * Rnd(ref s);

        /// <summary>Vertex colour for HIS/SecurityPorch's emissive pattern (id 6 in alpha): marks and bits read at night, on the feed too.</summary>
        static Color32 Emissive(float grey) => MeshKit.Grey(grey, MeshKit.Emissive);
        static Color32 Emissive(float r, float g, float b) { var c = (Color32)new Color(r, g, b); c.a = MeshKit.Grey(0f, MeshKit.Emissive).a; return c; }

        /// <summary>How far from the mark's centre along <paramref name="d"/> it stays inside its clip rect.</summary>
        static float Reach(Vector2 d, Vector4 clip)
        {
            float t = 10f;
            if (d.x > 1e-4f) t = Mathf.Min(t, clip.y / d.x); else if (d.x < -1e-4f) t = Mathf.Min(t, clip.x / d.x);
            if (d.y > 1e-4f) t = Mathf.Min(t, clip.w / d.y); else if (d.y < -1e-4f) t = Mathf.Min(t, clip.z / d.y);
            return Mathf.Max(0f, t);
        }

        static Vector3 At(in MarkRec m, Vector2 p, float z) => m.Pos + m.Rot * new Vector3(p.x, p.y, z);

        /// <summary>A flat strip from <paramref name="a"/> to <paramref name="b"/> (mark space), facing the mark's +z.</summary>
        static void Strip(MeshKit k, in MarkRec m, Vector2 a, Vector2 b, float w, float z, Color32 col)
        {
            var d = b - a; float len = d.magnitude;
            if (len < 1e-4f) return;
            var p = new Vector2(-d.y, d.x) / len * w;   // Cross(d, p) = +z
            k.Quad(At(m, a - p * 0.5f, z), m.Rot * new Vector3(d.x, d.y, 0f), m.Rot * new Vector3(p.x, p.y, 0f), col);
        }

        /// <summary>A ragged disc/ring (inner 0: a disc), <paramref name="n"/> segments, the outer edge jittered and clipped.</summary>
        static void Ring(MeshKit k, in MarkRec m, ref uint s, float inner, float outer, float jitter, int n, float z, Color32 col)
        {
            float first = outer * (1f + Rnd(ref s, -jitter, jitter)), prev = first;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                var d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)); var d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                float next = i == n - 1 ? first : outer * (1f + Rnd(ref s, -jitter, jitter));
                float r0 = Mathf.Min(prev, Reach(d0, m.Clip)), r1 = Mathf.Min(next, Reach(d1, m.Clip));
                prev = next;
                var o0 = At(m, d0 * r0, z); var o1 = At(m, d1 * r1, z);
                if (inner <= 0f) { k.Tri(At(m, Vector2.zero, z), o0, o1, col); continue; }   // counter-clockwise in xy: faces +z
                var i0 = At(m, d0 * Mathf.Min(inner, r0), z); var i1 = At(m, d1 * Mathf.Min(inner, r1), z);
                k.Tri(i0, o0, o1, col); k.Tri(i0, o1, i1, col);
            }
        }

        /// <summary>
        /// A bullet hole in the body, big enough to read from the doorway at 10 m at night: a dark 1.5 cm hole, a bright torn lip,
        /// a ragged ~5 cm ring of bare metal where the paint came off, and a few scuffs off it (emissive: it catches the light).
        /// </summary>
        static void HoleMesh(MeshKit k, in MarkRec m)
        {
            uint s = m.Seed;
            Ring(k, m, ref s, 0.011f, 0.025f, 0.18f, 10, 0f, Emissive(0.19f));        // bare metal
            Ring(k, m, ref s, 0.0075f, 0.0115f, 0.1f, 10, 0.0003f, Emissive(0.32f));   // the torn lip
            Ring(k, m, ref s, 0f, 0.0078f, 0.08f, 8, 0.0006f, MeshKit.Grey(0.012f));   // the hole
            int scuffs = 2 + (int)(Rnd(ref s) * 3f);
            for (int i = 0; i < scuffs; i++)
            {
                float a = Rnd(ref s) * Mathf.PI * 2f;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Strip(k, m, d * 0.018f, d * Rnd(ref s, 0.035f, 0.055f), Rnd(ref s, 0.003f, 0.005f), -0.0002f, Emissive(0.14f));
            }
        }

        static readonly Vector2[] s_spoke = new Vector2[3 * 12];
        static readonly float[] s_spokeLen = new float[12];

        /// <summary>
        /// A spider crack, big and bright: a frosted star (10–13 cm) round a small dark hole, 7–11 kinked spokes 12–30 cm long
        /// tapering from 1 cm, and web arcs joining them at two radii (emissive silver-white: it catches the street's light).
        /// </summary>
        static void CrackMesh(MeshKit k, in MarkRec m)
        {
            uint s = m.Seed;
            Ring(k, m, ref s, 0f, Rnd(ref s, 0.05f, 0.065f), 0.3f, 12, 0f, Emissive(0.13f));        // the frosted star
            Ring(k, m, ref s, 0f, Rnd(ref s, 0.02f, 0.028f), 0.25f, 9, 0.0003f, Emissive(0.24f));   // its crushed core
            Ring(k, m, ref s, 0f, 0.008f, 0.1f, 8, 0.0008f, MeshKit.Grey(0.015f));                   // the hole
            int n = 7 + (int)(Rnd(ref s) * 5f);
            for (int i = 0; i < n; i++)
            {
                float a = (i + Rnd(ref s, -0.3f, 0.3f)) / n * Mathf.PI * 2f;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float len = Mathf.Min(Rnd(ref s, 0.12f, 0.3f), Reach(d, m.Clip) * 0.97f);
                float kink = Rnd(ref s, -0.2f, 0.2f), kink2 = kink + Rnd(ref s, -0.25f, 0.25f);
                var p0 = d * 0.018f;
                var p1 = Rot2(d, kink) * (len * 0.5f);
                var p2 = Rot2(d, kink2) * len;
                float r2 = Reach(p2.normalized, m.Clip);
                if (p2.magnitude > r2) p2 = p2.normalized * (r2 * 0.97f);
                s_spoke[i * 3] = p0; s_spoke[i * 3 + 1] = p1; s_spoke[i * 3 + 2] = p2; s_spokeLen[i] = len;
                if (len < 0.03f) continue;
                float w = Rnd(ref s, 0.009f, 0.012f);
                var col = Emissive(Rnd(ref s, 0.2f, 0.26f));
                Strip(k, m, p0, p1, w, 0.0004f, col);
                Strip(k, m, p1, p2, w * 0.55f, 0.0004f, col);
            }
            // The web: arcs between neighbouring spokes at ~8 and ~16 cm, where both reach that far.
            for (int ringNo = 0; ringNo < 2; ringNo++)
            {
                float r = ringNo == 0 ? Rnd(ref s, 0.07f, 0.1f) : Rnd(ref s, 0.14f, 0.18f);
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    bool draw = Rnd(ref s) < 0.65f;
                    float ri = r * Rnd(ref s, 0.9f, 1.1f), rj = r * Rnd(ref s, 0.9f, 1.1f);
                    if (!draw || s_spokeLen[i] < ri + 0.01f || s_spokeLen[j] < rj + 0.01f) continue;
                    Strip(k, m, SpokeAt(i, ri), SpokeAt(j, rj), 0.004f, 0.0002f, Emissive(0.17f));
                }
            }
        }

        static Vector2 Rot2(Vector2 v, float a) { float c = Mathf.Cos(a), s = Mathf.Sin(a); return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c); }

        /// <summary>The point <paramref name="r"/> from the centre along spoke <paramref name="i"/>'s two segments.</summary>
        static Vector2 SpokeAt(int i, float r)
        {
            Vector2 p0 = s_spoke[i * 3], p1 = s_spoke[i * 3 + 1], p2 = s_spoke[i * 3 + 2];
            float m1 = p1.magnitude;
            if (r <= m1) return Vector2.Lerp(p0, p1, Mathf.InverseLerp(p0.magnitude, m1, r));
            return Vector2.Lerp(p1, p2, Mathf.InverseLerp(m1, p2.magnitude, r));
        }

        // ── effects: four shared particle systems on the porch, fed with Emit() (nothing made per shot) ──

        ParticleSystem _sparks, _glows, _bits, _smoke;
        static readonly Color SparkHot = new(1f, 0.95f, 0.8f), SparkOrange = new(1f, 0.6f, 0.26f), GlintColour = new(0.8f, 0.88f, 1f);
        static readonly Color32 LensRedBit = Emissive(0.6f, 0.04f, 0.03f), LensBlueBit = Emissive(0.06f, 0.14f, 0.7f);

        void EnsureFx()
        {
            if (_sparks != null || _porch == null) return;
            // Sparks and glints: additive streaks (the glow material: a soft ellipse along the velocity), fading out fast.
            _sparks = Fx("Sparks", _porch.GlowMaterial, 200, 0.7f, true, Fade(0.45f), null, false);
            // The flash at the impact and the puff of paint and dust: additive, growing as they fade.
            _glows = Fx("ImpactGlow", _porch.GlowMaterial, 60, 0f, false, Fade(0.3f), AnimationCurve.Linear(0f, 0.7f, 1f, 1.9f), false);
            // Glass bits, lens bits and paint chips: solid, tumbling, landing on the road (a floor at the porch set's height).
            _bits = Fx("Bits", _carMat, 260, 1f, false, null, null, true);
            // The smoke from a shot-up hood: slow, growing, fading in and out.
            var smokeFade = new Gradient();
            smokeFade.SetKeys(new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.white, 0.15f), new GradientColorKey(new Color(0.55f, 0.55f, 0.55f), 0.6f), new GradientColorKey(Color.black, 1f) },
                              new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            _smoke = Fx("Smoke", _porch.GlowMaterial, 90, -0.02f, false, smokeFade, AnimationCurve.Linear(0f, 0.6f, 1f, 2.6f), false);
        }

        static Gradient Fade(float mid)
        {
            var g = new Gradient();   // additive: fading to black is fading out
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(mid, mid, mid), 0.5f), new GradientColorKey(Color.black, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        ParticleSystem Fx(string name, Material mat, int max, float gravity, bool stretch, Gradient fade, AnimationCurve grow, bool floor)
        {
            var go = new GameObject("Car" + name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(_porch.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;   // its renderer is off between the porch's renders
            main.gravityModifier = gravity; main.maxParticles = max;
            main.startLifetime = 1f; main.startSpeed = 0f; main.startSize = 0.02f;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            if (fade != null) { var col = ps.colorOverLifetime; col.enabled = true; col.color = fade; }
            if (grow != null) { var size = ps.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1f, grow); }
            if (floor)
            {
                var col = ps.collision; col.enabled = true; col.type = ParticleSystemCollisionType.Planes; col.mode = ParticleSystemCollisionMode.Collision3D;
                col.SetPlane(0, _porch.transform); col.dampen = 0.7f; col.bounce = 0.25f; col.lifetimeLoss = 0f;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = 1.5f; r.velocityScale = 0.045f; }
            else r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            _porch.AddLive(r);
            return ps;
        }

        void DropFx()
        {
            foreach (var ps in new[] { _sparks, _glows, _bits, _smoke }) if (ps != null) Destroy(ps.gameObject);
            _sparks = _glows = _bits = _smoke = null;
        }

        void ClearFx()
        {
            if (_sparks != null) _sparks.Clear();
            if (_glows != null) _glows.Clear();
            if (_bits != null) _bits.Clear();
            if (_smoke != null) _smoke.Clear();
        }

        static Color32 Glitter() => Emissive(Random.Range(0.1f, 0.4f));   // mixed: some bits catch the light, some don't

        /// <summary>An orange-white spark burst off the metal: <paramref name="n"/> streaks, 0.15–0.3 s, glancing off the surface.</summary>
        void Sparks(Vector3 at, Vector3 normal, Vector3 dir, int n, float speed)
        {
            var main = (Vector3.Reflect(dir, normal) * 0.6f + normal * 0.4f).normalized;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < n; i++)
            {
                ep.position = at + normal * 0.01f;
                ep.velocity = (main + Random.insideUnitSphere * 0.75f).normalized * (speed * Random.Range(0.5f, 1.3f));
                ep.startSize = Random.Range(0.028f, 0.045f); ep.startLifetime = Random.Range(0.15f, 0.3f);
                ep.startColor = Color.Lerp(SparkHot, SparkOrange, Random.value);
                _sparks.Emit(ep, 1);
            }
        }

        /// <summary>A bright pop at the impact, <paramref name="size"/> across, ~80 ms.</summary>
        void Flash(Vector3 at, float size)
        {
            _glows.Emit(new ParticleSystem.EmitParams
            {
                position = at, velocity = Vector3.zero, applyShapeToPosition = false,
                startSize = size, startLifetime = 0.08f, startColor = Color.white, rotation = Random.Range(0f, 360f),
            }, 1);
        }

        /// <summary>A puff of paint dust and a few chips of paint flying off the hole.</summary>
        void Puff(Car c, Vector3 at, Vector3 normal, Vector3 dir)
        {
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < 3; i++)
            {
                ep.position = at + normal * 0.04f + Random.insideUnitSphere * 0.02f;
                ep.velocity = normal * Random.Range(0.3f, 0.8f) + Random.insideUnitSphere * 0.2f;
                ep.startSize = Random.Range(0.12f, 0.22f); ep.startLifetime = Random.Range(0.4f, 0.7f);
                ep.startColor = new Color(0.22f, 0.22f, 0.22f); ep.rotation = Random.Range(0f, 360f);
                _glows.Emit(ep, 1);
            }
            var bounce = Vector3.Reflect(dir, normal);
            float paint = c.Chips;   // its own paint: the cruiser's black and white, the truck's dark, the hitmen's grey / black / white
            for (int i = 0; i < 5; i++)
            {
                ep.position = at + normal * 0.01f;
                ep.velocity = (normal * 0.7f + bounce * 0.3f + Random.insideUnitSphere * 0.5f) * Random.Range(1.5f, 3.5f);
                ep.startSize = Random.Range(0.008f, 0.018f); ep.startLifetime = Random.Range(0.5f, 0.9f);
                ep.startColor = Emissive(paint * Random.Range(0.6f, 1f)); ep.rotation = Random.Range(0f, 360f);
                ep.angularVelocity = Random.Range(-900f, 900f);
                _bits.Emit(ep, 1);
            }
        }

        /// <summary>Glass bits bursting from the pane, mostly into the car, some back out, falling; and a few glints.</summary>
        void GlassBits(Vector3 point, Vector3 normal, Vector3 dir, int n, int glints)
        {
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < n; i++)
            {
                ep.position = point + Random.insideUnitSphere * 0.02f;
                ep.velocity = (Random.value < 0.7f ? dir : normal) * Random.Range(0.8f, 2.4f) + Random.insideUnitSphere * 0.8f;
                ep.startSize = Random.Range(0.012f, 0.03f); ep.startLifetime = Random.Range(0.6f, 1.2f);
                ep.rotation = Random.Range(0f, 360f); ep.angularVelocity = Random.Range(-720f, 720f);
                ep.startColor = Glitter();
                _bits.Emit(ep, 1);
            }
            ep.rotation = 0f; ep.angularVelocity = 0f;
            for (int i = 0; i < glints; i++)
            {
                ep.position = point + normal * 0.01f;
                ep.velocity = (normal + Random.insideUnitSphere * 0.8f) * Random.Range(0.5f, 1.5f);
                ep.startSize = Random.Range(0.012f, 0.02f); ep.startLifetime = Random.Range(0.3f, 0.7f);
                ep.startColor = GlintColour;
                _sparks.Emit(ep, 1);
            }
        }

        /// <summary>A thin grey plume off a shot-up hood (<see cref="SmokeAfter"/> body hits), ~<see cref="SmokeRate"/> puffs a second.</summary>
        void StepSmoke(Car c, float dt)
        {
            if (!c.Smoking || _smoke == null) return;
            c.SmokeAcc += dt * SmokeRate;
            if (c.SmokeAcc < 1f) return;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            var at = c.Root.TransformPoint(c.Hood);
            for (; c.SmokeAcc >= 1f; c.SmokeAcc -= 1f)
            {
                ep.position = at + Random.insideUnitSphere * 0.08f;
                ep.velocity = new Vector3(0.1f, Random.Range(0.35f, 0.55f), 0.06f) + Random.insideUnitSphere * 0.05f;
                ep.startSize = Random.Range(0.16f, 0.26f); ep.startLifetime = Random.Range(2.4f, 3.4f);
                ep.startColor = new Color(0.11f, 0.11f, 0.11f) * Random.Range(0.8f, 1.2f); ep.rotation = Random.Range(0f, 360f);
                _smoke.Emit(ep, 1);
            }
        }

        // ───────────────────────────── riders ─────────────────────────────

        /// <summary>
        /// <paramref name="man"/> rides wave <paramref name="wave"/>'s vehicle in <paramref name="seat"/> (0 = the driver): where to
        /// sit (car space). False: no vehicle, no such seat, or it has already stopped — he comes on foot.
        /// </summary>
        public static bool Board(IntruderT man, int wave, int seat, out Transform car, out Vector3 local)
        {
            car = null; local = default;
            var pc = Instance;
            if (pc == null || !pc.Active || man == null) return false;
            int v = VehicleFor(wave, man.Faction);   // his own faction's vehicle, not whatever the last run left PoliceResponse.Faction at
            if (v >= pc._cars.Count) return false;
            var c = pc._cars[v];
            if (!c.Sent || c.Root == null || c.Seats == null || seat < 0 || seat >= c.Seats.Length || pc.Stopped(c)) return false;
            while (c.Riders.Count <= seat) c.Riders.Add(null);
            if (c.Riders[seat] != null && c.Riders[seat] != man) return false;
            c.Riders[seat] = man;
            car = c.Root; local = c.Seats[seat];
            return true;
        }

        /// <summary>
        /// The ride is over for this seat: the vehicle has stopped (or crashed to a stop) and it's his turn to get out (Brett
        /// 2026-10-05: the driver first, the passenger a beat later, the back seat once the door is free, the box team in turn —
        /// PoliceCars.Exit.cs TurnAt; his door swings when his hand pushes it). <paramref name="exit"/>: where he ends up standing
        /// (world, on the porch set's floor: by his open door, behind the truck). The show over (cars gone): out where he is (zero).
        /// </summary>
        /// <param name="faction">The rider's own (<see cref="VehicleOf"/>); null: the run's now.</param>
        public static bool GetOut(int wave, int seat, out Vector3 exit, HIS.Scenario.SquadFaction? faction = null)
        {
            exit = Vector3.zero;
            var pc = Instance;
            if (pc == null || !pc.Active) return true;
            int v = VehicleOf(wave, faction);
            if (v >= pc._cars.Count) return true;
            var c = pc._cars[v];
            if (!c.Sent || c.Root == null) return true;
            if (!pc.Stopped(c) || pc._t < pc.TurnAt(c, seat)) return false;
            var e = pc.Plan(c, seat, out var plan) ? plan.Stand
                  : c.Truck ? seat == 0 ? new Vector3(-1.7f, 0f, 2.2f) : c.Exits[(seat - 1) % c.Exits.Length] : c.Exits[seat % c.Exits.Length];
            exit = c.Root.TransformPoint(e);
            exit.y = pc._porch != null ? pc._porch.FloorY : exit.y;
            return true;
        }

        /// <summary>Tests: vehicle <paramref name="v"/> crashed (its driver shot while it moved), and how many cracks/holes it carries (shattered panes' included).</summary>
        /// <summary>Tests: vehicle <paramref name="v"/>'s damage — body hits, shattered panes, the light bar shot out, smoking.</summary>
        public bool Damage(int v, out int bodyHits, out int shattered, out bool barBroken, out bool smoking)
        {
            bodyHits = shattered = 0; barBroken = smoking = false;
            if (v < 0 || v >= _cars.Count) return false;
            var c = _cars[v];
            bodyHits = c.BodyHits; barBroken = c.BarBroken; smoking = c.Smoking;
            foreach (var p in c.Panes) if (p.Shattered) shattered++;
            return true;
        }

        public bool Crashed(int v, out int marks, out Transform root)
        {
            marks = 0; root = null;
            if (v < 0 || v >= _cars.Count) return false;
            marks = MarkCount(_cars[v]); root = _cars[v].Root;
            return _cars[v].Crashed;
        }

        /// <summary>Tests: vehicle <paramref name="v"/>'s driver "shot" now, if it's on its way in with at least <paramref name="along"/> of the drive done.</summary>
        public bool CrashNow(int v, float along)
        {
            if (v < 0 || v >= _cars.Count) return false;
            var c = _cars[v];
            if (!c.Sent || c.Root == null || c.Crashed || Stopped(c) || _t - c.StartAt - c.Delay < along * c.Duration) return false;
            BeginCrash(c);
            return true;
        }

        /// <summary>Tests: vehicle <paramref name="v"/>'s crash has come to a stop; seconds it took, where (porch space).</summary>
        public bool CrashOver(int v, out float took, out Vector3 at)
        {
            took = -1f; at = default;
            if (v < 0 || v >= _cars.Count || !_cars[v].Crashed) return false;
            var c = _cars[v];
            at = c.CrashPos;
            if (c.CrashStopped) took = c.CrashStoppedAt - c.CrashAt;
            return c.CrashStopped;
        }

        /// <summary>Tests: seconds on vehicle <paramref name="v"/>'s drive-in clock (−1: not sent).</summary>
        public float DriveClock(int v) => v >= 0 && v < _cars.Count && _cars[v].Sent ? _t - _cars[v].StartAt : -1f;

        bool Stopped(Car c) => c.Sent && (c.Crashed ? c.CrashStopped : _t >= c.StartAt + c.Delay + c.Duration);
        float StoppedAt(Car c) => c.Crashed ? c.CrashStoppedAt : c.StartAt + c.Delay + c.Duration;

        /// <summary>A car's hit's sparks, glass and puff are on the feed this long (s) after it (<see cref="_stirredAt"/>).</summary>
        const float StirLive = 2.5f;
        /// <summary>A hitman vehicle is still this long after its stop (s): its lights died at 0.7 s, its riders are out and
        /// clear of it by then (each walking man keeps the feed live himself: SecurityCamera.CheckMotion).</summary>
        const float StillAfter = 6f;
        /// <summary>When a round last hit a car (its body or glass): Time.time.</summary>
        float _stirredAt = -100f;

        /// <summary>
        /// Every vehicle out is a hitman's, parked, and still (the fixer, 0.1.58 review: the hitmen's cars stay out from the
        /// first car to the 2:00 AM cut since Brett 0.1.57 — five waves, two visitors, every lull and gap — and Update kept the
        /// doorbell feed rendering at 12 Hz all that time for parked, unlit cars that never change): stopped
        /// <see cref="StillAfter"/> s ago, lamps off, no smoke, no rock from a hit or a shove still dying away, no hit on any car
        /// in the last <see cref="StirLive"/> s. Then the feed holds its frame like any quiet porch (anyone walking in view
        /// keeps it live by himself). Any police car out (its light bar): false — the default game exactly as before.
        /// </summary>
        bool HitmenStill()
        {
            bool any = false;
            foreach (var c in _cars)
            {
                if (!c.Sent || c.Root == null) continue;
                if (!c.Hitman) return false;
                any = true;
                if (!Stopped(c) || _t < StoppedAt(c) + StillAfter || (c.Lamps != null && c.LampsOn) || c.Smoking
                    || c.CrashJolt > 0.01f || c.ShoveVel.sqrMagnitude > 1e-6f || Mathf.Abs(c.ShoveYawVel) > 1e-3f) return false;
            }
            return any && Time.time - _stirredAt > StirLive;
        }

        // ───────────────────────────── crash ─────────────────────────────

        void StepCombat()
        {
            float dt = Mathf.Clamp(_t - _combatT, 0f, 0.25f);
            _combatT = _t;
            foreach (var c in _cars)
            {
                if (!c.Sent || c.Root == null) continue;
                var d = c.Riders.Count > 0 ? c.Riders[0] : null;
                bool driverDead = d != null && d.Riding && d.Current == IntruderT.State.Dead;
                if (driverDead && !c.Crashed && !Stopped(c)) BeginCrash(c);
                if (driverDead && c.HornUntil < 0f)
                {
                    c.HornUntil = _t + HornSeconds;   // slumped on the wheel
                    if (Sfx.Has("car.horn")) c.Horn = Sfx.PlayLoop("car.horn", c.Root.position + Vector3.up, 0.5f, 0.05f, c.Root);
                }
                if (c.Horn != 0 && _t > c.HornUntil) { Sfx.Stop(c.Horn, 0.8f); c.Horn = 0; }
                if (c.Crashed) for (float left = dt; left > 1e-4f; left -= 0.02f) CrashStep(c, Mathf.Min(0.02f, left));
                else c.CrashJolt = Mathf.MoveTowards(c.CrashJolt, 0f, dt * 2.5f);   // (a parked car rocked by a hit)
                StepShove(c, dt);
                StepSmoke(c, dt);
            }
        }

        /// <summary>Where it would be on its drive-in curve at its clock <paramref name="t"/> (porch space).</summary>
        Vector3 CurveAt(Car c, float t)
        {
            float u = Mathf.Clamp01((t - c.Delay) / c.Duration);
            return Bezier(c.P0, c.P1, c.P2, c.P3, 1f - (1f - u) * (1f - u));
        }

        void BeginCrash(Car c)
        {
            float t = _t - c.StartAt;
            var p1 = CurveAt(c, t); var p0 = CurveAt(c, t - 0.05f);
            var v = (p1 - p0) / 0.05f; v.y = 0f;
            c.Crashed = true; c.CrashStopped = false; c.OverCurb = false; c.Wrecked = false; c.CrashAt = _t; c.CrashTurned = 0f; c.CrashJolt = 0f;
            c.CrashPos = p1;
            // His foot's still on the gas: it doesn't brake to the stop it was making, it keeps coming (at least ~22 km/h).
            c.CrashSpeed = Mathf.Max(v.magnitude, CrashFloorSpeed);
            c.Hop = c.HopVel = c.ReboundLeft = c.SpinLeft = 0f;
            c.CrashDir = v.magnitude > 0.1f ? v / v.magnitude : c.Root.localRotation * Vector3.forward;
            // A dead man's hands drag the wheel: it veers toward the house side (−z in porch space), at most ~55°.
            float yaw = Mathf.Atan2(c.CrashDir.x, c.CrashDir.z) * Mathf.Rad2Deg;
            c.CrashYawRate = Mathf.Sign(Mathf.DeltaAngle(yaw, 180f)) * Random.Range(25f, 45f);
            Debug.Log($"[HIS] PoliceCars: {c.Root.name}'s driver shot dead at {c.CrashSpeed:F1} m/s — it rolls on and crashes");
        }

        const float CrashFloorSpeed = 7f, CrashTopSpeed = 11f;
        /// <summary>Seconds after the driver's shot: no wall or car met by then, it coasts to a stop; and the hard limit.</summary>
        const float CrashCoastAfter = 5f, CrashMaxSeconds = 10f;

        /// <summary>Would <paramref name="c"/> at <paramref name="pos"/> heading <paramref name="dir"/> hit one of the set's posts (the lawn tree)?</summary>
        bool HitsPost(Car c, Vector3 pos, Vector3 dir)
        {
            var inv = Quaternion.Inverse(Quaternion.LookRotation(dir, Vector3.up));
            foreach (var p in _porch.Posts)
            {
                var l = inv * (new Vector3(p.x, 0f, p.y) - new Vector3(pos.x, 0f, pos.z));
                if (Mathf.Abs(l.x) < c.Half.x + p.z && Mathf.Abs(l.z) < c.Half.y + p.z) return true;
            }
            return false;
        }

        void CrashStep(Car c, float h)
        {
            c.CrashJolt = Mathf.MoveTowards(c.CrashJolt, 0f, h * 2.5f);
            // Never a crash without an end (0.1.44: rammed into a parked cruiser on the road, the "foot on the gas" sped it up
            // again after every bounce — it rammed forever, never stopped, and its riders never got out: the wave never
            // ended). Out of road after a few seconds, it coasts to a stop; at the limit it simply is stopped.
            if (!c.CrashStopped)
            {
                if (!c.Wrecked && _t - c.CrashAt > CrashCoastAfter) c.Wrecked = true;
                if (_t - c.CrashAt > CrashMaxSeconds) { c.CrashSpeed = 0f; c.ReboundLeft = 0f; c.SpinLeft = 0f; c.Hop = c.HopVel = 0f; }
            }
            // The hop over the curb: up, down, one small bounce.
            if (c.Hop > 0f || c.HopVel > 0f)
            {
                c.HopVel -= 9.81f * h; c.Hop += c.HopVel * h;
                if (c.Hop <= 0f)
                {
                    c.Hop = 0f;
                    if (c.HopVel < -1.2f) { c.HopVel *= -0.3f; c.CrashJolt = Mathf.Max(c.CrashJolt, 0.7f); SoundAt("car.crash", c.Root.position, 0.3f); }
                    else c.HopVel = 0f;
                }
            }
            // After a hit: rolls back off it and slews round.
            if (c.ReboundLeft > 0f)
            {
                float step = Mathf.Min(c.ReboundLeft, h * 2.2f);
                c.CrashPos -= c.CrashDir * step; c.ReboundLeft -= step;
            }
            if (Mathf.Abs(c.SpinLeft) > 0.01f)
            {
                float turn = Mathf.Sign(c.SpinLeft) * Mathf.Min(Mathf.Abs(c.SpinLeft), c.SpinRate * h);
                c.CrashDir = Quaternion.Euler(0f, turn, 0f) * c.CrashDir; c.SpinLeft -= turn;
            }
            if (c.CrashStopped) return;
            float dYaw = c.CrashYawRate * h;
            if (Mathf.Abs(c.CrashTurned + dYaw) <= 55f) { c.CrashTurned += dYaw; c.CrashDir = Quaternion.Euler(0f, dYaw, 0f) * c.CrashDir; }
            // A dead foot on the gas: it picks up speed on the road (to ~40 km/h); the lawn drags it back down.
            // Once it has hit something (Wrecked) it only slows: no second run at what it just hit.
            c.CrashSpeed = c.Wrecked ? Mathf.Max(0f, c.CrashSpeed - 8f * h)
                         : c.OverCurb ? Mathf.Max(0f, c.CrashSpeed - 1.5f * h) : Mathf.Min(CrashTopSpeed, c.CrashSpeed + 1.0f * h);
            var next = c.CrashPos + c.CrashDir * (c.CrashSpeed * h);
            // Over the curb (the road's near edge, z ≈ 9.35 in porch space): it hops, sparks off the concrete, loses speed.
            if (!c.OverCurb && next.z < 9.5f && c.CrashPos.z >= 9.5f)
            {
                c.OverCurb = true;
                float e = c.CrashSpeed;
                c.CrashSpeed *= 0.8f; c.CrashJolt = Mathf.Clamp(e / 5f, 0.6f, 1.6f);
                c.HopVel = Mathf.Clamp(e * 0.28f, 0.8f, 2.6f);
                CurbHit(c, e);
            }
            // The house front, or another vehicle: it slams to a stop, crumples, bounces back and slews.
            bool hit = next.z < 4.2f;
            Car into = null;
            foreach (var o in _cars)
                if (o != c && o.Sent && o.Root != null && Overlaps(c, next, c.CrashDir, o)) { hit = true; into = o; break; }
            if (!hit && _porch != null && HitsPost(c, next, c.CrashDir)) hit = true;
            if (hit)
            {
                if (c.CrashSpeed > 0.6f) Impact(c, into, c.CrashSpeed);
                c.CrashSpeed = 0f;
                c.Wrecked = true;
            }
            else c.CrashPos = next;
            if (c.CrashSpeed < 0.12f && c.ReboundLeft <= 0f && Mathf.Abs(c.SpinLeft) <= 0.01f && c.Hop <= 0f && c.HopVel <= 0f)
            {
                c.CrashStopped = true; c.CrashStoppedAt = _t;
                Debug.Log($"[HIS] PoliceCars: {c.Root.name} crashed to a stop at {c.CrashPos:F1} (porch space){(c.OverCurb ? ", on the lawn" : "")}");
            }
        }

        /// <summary>The front wheels hitting the curb at <paramref name="e"/> m/s: sparks under the bumper, dirt, a thump.</summary>
        void CurbHit(Car c, float e)
        {
            EnsureFx();
            var fwd = c.Root.TransformDirection(Vector3.forward);
            var bumper = c.Root.TransformPoint(new Vector3(0f, 0.12f, c.Half.y - 0.2f));
            Sparks(bumper, Vector3.up, fwd, Mathf.Clamp(Mathf.RoundToInt(e * 3f), 8, 26), 4.5f);
            Dust(bumper + Vector3.down * 0.1f, fwd, Mathf.Clamp(Mathf.RoundToInt(e), 3, 8));
            SoundAt("car.crash", c.Root.position, Mathf.Clamp(0.25f + e * 0.05f, 0.3f, 0.7f));
        }

        /// <summary>
        /// Slammed into the house front or another vehicle at <paramref name="e"/> m/s: a burst of sparks, glass and dust off the
        /// front, the windshield gone (a side window too, harder), the light bar flying apart, the hood smoking, the front
        /// crumpled, a bounce back and a slew; the car it hit is shoved and rocks. Every effect is from the capped pools
        /// already used for gunfire (sparks 200, bits 260, flashes 60, smoke 90): a crash can't cost more than those.
        /// </summary>
        void Impact(Car c, Car into, float e)
        {
            EnsureFx();
            var fwd = c.Root.TransformDirection(Vector3.forward);
            var front = c.Root.TransformPoint(new Vector3(0f, 0.55f, c.Half.y));
            var info = new DamageInfo { Point = front, Normal = -fwd, Direction = fwd, Type = DamageType.Bullet, Amount = 0f, Penetration = 0f };
            Sparks(front, -fwd, fwd, Mathf.Clamp(Mathf.RoundToInt(e * 4f), 12, 40), 5.5f);
            Flash(front, Mathf.Clamp(0.5f + e * 0.06f, 0.5f, 1.2f));
            GlassBits(front + Vector3.up * 0.35f, -fwd, fwd, Mathf.Clamp(Mathf.RoundToInt(e * 3f), 10, 30), 8);
            Dust(front + Vector3.down * 0.3f, fwd, Mathf.Clamp(Mathf.RoundToInt(e), 3, 9));
            if (e > 2.5f && c.Panes.Count > 0 && !c.Panes[0].Shattered) Shatter(c, c.Panes[0], 0, info);          // the windshield
            if (e > 6f)
            {
                int side = c.Truck ? 1 + Random.Range(0, 2) : 2 + Random.Range(0, 2);
                if (side < c.Panes.Count && !c.Panes[side].Shattered) Shatter(c, c.Panes[side], side, info);
            }
            if (e > 4.5f && !c.BarBroken)
            {
                var bar = c.Root.TransformPoint((c.RedLocal + c.BlueLocal) * 0.5f);
                BreakBar(c, new DamageInfo { Point = bar, Normal = Vector3.up, Direction = fwd, Type = DamageType.Bullet });
            }
            c.Smoking = true;
            Crumple(c, e);
            SoundAt("car.crash", front, 1f);
            SoundAt("metal.hit", front, 0.9f);
            c.CrashJolt = Mathf.Clamp(0.8f + e * 0.15f, 1f, 2.4f);
            c.ReboundLeft = Mathf.Clamp(e * 0.05f, 0.1f, 0.5f);
            c.SpinLeft = (c.CrashYawRate >= 0f ? 1f : -1f) * Mathf.Clamp(e * 3.5f, 6f, 35f);
            c.SpinRate = 90f;
            if (into != null)
            {
                into.ShoveVel += c.CrashDir * Mathf.Clamp(e * 0.12f, 0.2f, 1.4f);
                into.ShoveYawVel += (Random.value < 0.5f ? -1f : 1f) * Mathf.Clamp(e * 3f, 4f, 25f);
                into.CrashJolt = Mathf.Max(into.CrashJolt, Mathf.Clamp(e / 6f, 0.4f, 1.4f));
            }
            Debug.Log($"[HIS] PoliceCars: {c.Root.name} slammed into {(into != null ? into.Root.name : c.CrashPos.z < 4.6f ? "the front of the house" : "the tree")} at {e:F1} m/s");
        }

        /// <summary>A car shoved by a crash slides and turns a little, then stops (friction), in porch space.</summary>
        void StepShove(Car c, float dt)
        {
            if (c.ShoveVel.sqrMagnitude < 1e-6f && Mathf.Abs(c.ShoveYawVel) < 1e-3f) return;
            c.Shove += c.ShoveVel * dt; c.ShoveYaw += c.ShoveYawVel * dt;
            float sp = c.ShoveVel.magnitude;
            c.ShoveVel = sp > 1e-4f ? c.ShoveVel * (Mathf.Max(0f, sp - 3f * dt) / sp) : Vector3.zero;
            c.ShoveYawVel = Mathf.MoveTowards(c.ShoveYawVel, 0f, 60f * dt);
        }

        /// <summary>
        /// The front caves in: the body's vertices within ~0.7 m of the nose pushed back (up to ~0.35 m), the hood buckled
        /// down, a little ragged. Once per crash on the car's own 504-tri mesh (CPU, a fraction of a millisecond); undone
        /// when the car is reset.
        /// </summary>
        void Crumple(Car c, float e)
        {
            if (c.BodyMesh == null)
            {
                var body = c.Root.Find("Body");
                var mf = body != null ? body.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null) return;
                c.BodyMesh = mf.sharedMesh;
            }
            // An upload-only body (the police cars': Part(readable: false)) can't be bent on the headset — the Editor reads any
            // mesh, the player doesn't, so every crash logged "Not allowed to access vertices" with a stack trace on the crash
            // frame and again at the reset (0.1.54 log). No crumple there, and no error.
            if (!c.BodyMesh.isReadable) return;
            c.BodyRest ??= c.BodyMesh.vertices;
            var v = (Vector3[])c.BodyRest.Clone();
            float nose = float.MinValue;
            foreach (var p in v) nose = Mathf.Max(nose, p.z);
            const float zone = 0.7f;
            float depth = Mathf.Clamp(e * 0.035f, 0.08f, 0.35f);
            for (int i = 0; i < v.Length; i++)
            {
                float k = (v[i].z - (nose - zone)) / zone;
                if (k <= 0f) continue;
                k *= k;
                float rag = Mathf.Sin(i * 12.9898f) * 0.5f + 0.5f;           // uneven: not a clean slice
                v[i].z -= depth * k * (0.75f + 0.5f * rag);
                v[i].y -= depth * 0.4f * k * (v[i].y > 0.7f ? 1f : 0.25f);
                v[i].x *= 1f + 0.05f * k * (depth / 0.35f);
            }
            c.BodyMesh.vertices = v;
            c.BodyMesh.RecalculateNormals();
            c.BodyMesh.RecalculateBounds();
        }

        /// <summary>A few dull brown-grey puffs kicked up off the ground (dirt, road dust).</summary>
        void Dust(Vector3 at, Vector3 dir, int n)
        {
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < n; i++)
            {
                ep.position = at + Random.insideUnitSphere * 0.25f;
                ep.velocity = (dir * Random.Range(0.3f, 1.2f) + Vector3.up * Random.Range(0.3f, 0.9f) + Random.insideUnitSphere * 0.3f);
                ep.startSize = Random.Range(0.3f, 0.6f); ep.startLifetime = Random.Range(0.6f, 1.1f);
                ep.startColor = new Color(0.2f, 0.18f, 0.15f); ep.rotation = Random.Range(0f, 360f);
                _glows.Emit(ep, 1);
            }
        }

        void PoseCrashed(Car c)
        {
            c.Root.localPosition = c.CrashPos + c.Shove + Vector3.up * c.Hop;
            float j = c.CrashJolt, wob = Mathf.Sin(_t * 31f) * j;
            // Nose up on the way over the curb (the hop), nose down into a hit (the jolt), rocking as it settles.
            float pitch = -2.5f * j + 1.2f * wob - Mathf.Clamp(c.HopVel * 3f, -6f, 8f);
            c.Root.localRotation = Quaternion.Euler(0f, c.ShoveYaw, 0f) * Quaternion.LookRotation(c.CrashDir, Vector3.up) * Quaternion.Euler(pitch, 0f, 1.5f * wob);
            OpenDoors(c, c.CrashStopped ? _t - c.CrashStoppedAt : -1f);
        }

        /// <summary>Would <paramref name="c"/> at <paramref name="pos"/> heading <paramref name="dir"/> overlap <paramref name="o"/> (porch space, footprints)?</summary>
        bool Overlaps(Car c, Vector3 pos, Vector3 dir, Car o)
        {
            var right = new Vector3(dir.z, 0f, -dir.x);
            for (int sx = -1; sx <= 1; sx++)
                for (int sz = -1; sz <= 1; sz++)
                {
                    if (sx == 0 && sz == 0) continue;
                    var p = pos + right * (sx * c.Half.x) + dir * (sz * c.Half.y);
                    var l = Quaternion.Inverse(o.Root.localRotation) * (p - o.Root.localPosition);
                    if (Mathf.Abs(l.x) < o.Half.x && Mathf.Abs(l.z) < o.Half.y) return true;
                }
            return false;
        }

        void ResetCombat(Car c)
        {
            // Marks off, every pane back in (collider and all), the light bar lit, no smoke; the bits in the air go.
            foreach (var d in c.Decals)
            {
                d.Marks.Clear();
                if (d.Mesh != null) d.Mesh.Clear();
                if (d.T != null) d.T.gameObject.SetActive(false);
            }
            foreach (var p in c.Panes)
            {
                p.Hits = 0; p.LastFrame = -1; p.Shattered = false;
                foreach (var g in p.Parts) if (g != null) g.SetActive(true);
            }
            c.BarBroken = !c.HasBar; c.Smoking = false; c.BodyHits = 0; c.SmokeAcc = 0f;   // (no bar at all: stays "broken", dark)
            ClearFx();
            if (c.Horn != 0) { Sfx.Stop(c.Horn, 0.2f); c.Horn = 0; }
            // A dead rider goes with the car (never back in the seat next call); a live one steps out of it.
            foreach (var r in c.Riders)
            {
                if (r == null || r.transform.parent != c.Root) continue;
                if (r.Current == IntruderT.State.Dead) Destroy(r.gameObject); else r.transform.SetParent(null, true);
            }
            c.Riders.Clear();
            c.Crashed = c.CrashStopped = c.OverCurb = c.Wrecked = false;
            c.HornUntil = -1f; c.CrashJolt = 0f;
            c.Hop = c.HopVel = c.ReboundLeft = c.SpinLeft = 0f;
            c.Shove = c.ShoveVel = Vector3.zero; c.ShoveYaw = c.ShoveYawVel = 0f;
            if (c.BodyMesh != null && c.BodyRest != null && c.BodyMesh.isReadable) { c.BodyMesh.vertices = c.BodyRest; c.BodyMesh.RecalculateNormals(); c.BodyMesh.RecalculateBounds(); }
            _combatT = _t;
        }

        // ───────────────────────────── walking round them ─────────────────────────────

        static readonly List<Car> s_passed = new();
        const float WalkPad = 0.5f, CornerPad = 0.55f;

        /// <summary>
        /// Waypoints (world, not including the ends) for a walker going straight from <paramref name="from"/> to
        /// <paramref name="to"/> round every vehicle out on the street (its footprint + 0.5 m): around the nearer side.
        /// Brett 0.1.38: "the cops ran in zig zags outside for a few laps before coming down the path" — a rider starts at his
        /// own car's door, inside that car's padded footprint, and every leg of his path was routed round his own car, and
        /// round each car again after grazing a corner (up to 4 detours a leg: 13–15 legs where 5–7 were needed). Now: starting
        /// inside a footprint he first steps straight out of it (the door side), each car is gone round at most once per leg,
        /// and a car whose footprint holds the leg's end is left alone (he's going there anyway).
        /// A new list every call, his to keep: a walker iterates it across frames (one leg per yield) while his mates ask for
        /// their own routes. It was one shared list (0.1.44): the second SWAT officer bound for the side door refilled it
        /// under the first one's feet, his brain died on "Collection was modified", he stood hidden behind the house forever
        /// and wave 3 never ended (Brett: "after third wave of cops nothing happens"; every wave 3 on the headset).
        /// </summary>
        public static List<Vector3> RouteAround(Vector3 from, Vector3 to)
        {
            var detour = new List<Vector3>(4);
            s_passed.Clear();
            var pc = Instance;
            if (pc == null || !pc.Active) return detour;
            var a = from;
            bool steppedOut = false;
            foreach (var c in pc._cars)   // out of the car he's standing in the footprint of: straight out its nearer side
            {
                if (!Live(c) || !Inside(c, a, WalkPad)) continue;
                var l = c.Root.InverseTransformPoint(a);
                var exit = c.Root.TransformPoint(new Vector3(l.x >= 0f ? c.Half.x + CornerPad : -(c.Half.x + CornerPad), l.y, l.z));
                exit.y = from.y;
                detour.Add(exit); a = exit;   // (still gone round below if the leg's end is on its far side)
                steppedOut = true;
                break;
            }
            for (int guard = 0; guard < 4; guard++)
            {
                Car hit = null; float first = float.MaxValue;
                foreach (var c in pc._cars)
                {
                    if (!Live(c) || s_passed.Contains(c) || Inside(c, to, WalkPad)) continue;
                    if (SegmentHitsBox(c, a, to, WalkPad, out float at) && at < first) { first = at; hit = c; }
                }
                if (hit == null) break;
                s_passed.Add(hit);
                var corners = Corners(hit, CornerPad);
                // The two ways round: via the corners on either side of the line, whichever is shorter.
                List<Vector3> best = null; float bestLen = float.MaxValue;
                for (int side = 0; side < 2; side++)
                {
                    var way = new List<Vector3>();
                    var n = Vector3.Cross(Vector3.up, (to - a).normalized);
                    foreach (var k in corners) if ((Vector3.Dot(k - a, n) >= 0f) == (side == 0)) way.Add(k);
                    if (way.Count == 0) continue;
                    way.Sort((p, q) => Vector3.Dot(p - a, to - a).CompareTo(Vector3.Dot(q - a, to - a)));
                    float len = 0f; var prev = a;
                    foreach (var w in way) { len += Vector3.Distance(prev, w); prev = w; }
                    len += Vector3.Distance(prev, to);
                    if (len < bestLen) { bestLen = len; best = way; }
                }
                if (best == null) break;
                foreach (var w in best) { var p = w; p.y = from.y; detour.Add(p); }
                a = detour[^1];
            }
            // Going round one car's corners can run into another: a crash pile-up (Brett 0.1.54: the SWAT truck slammed into
            // the cruiser and the team walked out through it). Every leg is checked; if one still crosses a vehicle, the way
            // round is found on a grid instead (after the step out of his own car, kept).
            var prevPt = from;
            for (int i = 0; i <= detour.Count; i++)
            {
                var next = i < detour.Count ? detour[i] : to;
                if (SegmentBlocked(prevPt, next))
                {
                    int keep = steppedOut ? 1 : 0;
                    var start = keep == 1 ? detour[0] : from;
                    var grid = GridRoute(start, to);
                    if (grid == null) break;                              // no way found: the corners, as before
                    detour.RemoveRange(keep, detour.Count - keep);
                    foreach (var w in grid) { var p = w; p.y = from.y; detour.Add(p); }
                    break;
                }
                prevPt = next;
            }
            // Round the end of an open door, not through it (2026-10-05, the car exit: a rider gets out into the pocket behind his
            // open door — the police use it as cover — and his walk off to the front ran straight through its leaf).
            prevPt = from;
            for (int i = 0, added = 0; i <= detour.Count && added < 2; i++)
            {
                var next = i < detour.Count ? detour[i] : to;
                // (Only when going by it is clear: never into another vehicle parked or crashed up against this one.)
                if (PastOpenDoor(prevPt, next, out var via) && !InFootprint(via, StandPad) && !SegmentBlocked(prevPt, via) && !SegmentBlocked(via, next))
                { via.y = from.y; detour.Insert(i, via); added++; next = via; }
                prevPt = next;
            }
            return detour;
        }

        /// <summary>How far past an open door's free end (m) a walker goes by it.</summary>
        const float DoorPass = 0.4f;

        /// <summary>
        /// The walk from <paramref name="a"/> to <paramref name="b"/> crosses an open side door's leaf (hinge to free end, as it
        /// stands swung now, on the porch's floor): <paramref name="via"/>, the point just past its free end to go by first.
        /// Transform math only (a few doors per vehicle, a walker's leg at a time: no allocation).
        /// </summary>
        static bool PastOpenDoor(Vector3 a, Vector3 b, out Vector3 via)
        {
            via = default;
            var pc = Instance;
            if (pc == null) return false;
            foreach (var c in pc._cars)
            {
                if (!Live(c)) continue;
                for (int i = 0; i < c.Doors.Count && i < c.DoorBoxes.Count; i++)
                {
                    var h = c.Doors[i].Hinge;
                    if (h == null || !IsSideDoor(c, i) || Quaternion.Angle(h.localRotation, Quaternion.identity) < 10f) continue;
                    var leaf = c.DoorBoxes[i];
                    Vector3 h0 = h.TransformPoint(new Vector3(leaf.center.x, 0f, leaf.max.z)), h1 = h.TransformPoint(new Vector3(leaf.center.x, 0f, leaf.min.z));
                    if (!SegmentsCrossXZ(a, b, h0, h1)) continue;
                    var along = h1 - h0; along.y = 0f;
                    via = h1 + along.normalized * DoorPass;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Do the segments <paramref name="a"/>–<paramref name="b"/> and <paramref name="c"/>–<paramref name="d"/> cross on the floor (x, z)?</summary>
        static bool SegmentsCrossXZ(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            static float Side(Vector3 p, Vector3 q, Vector3 r) => (q.x - p.x) * (r.z - p.z) - (q.z - p.z) * (r.x - p.x);
            float d1 = Side(a, b, c), d2 = Side(a, b, d), d3 = Side(c, d, a), d4 = Side(c, d, b);
            return d1 * d2 < 0f && d3 * d4 < 0f;
        }

        /// <summary>
        /// Does the straight walk from <paramref name="a"/> to <paramref name="b"/> cross a vehicle out on the street (its footprint
        /// + 0.5 m)? A car he's standing in (getting out of it) or whose footprint holds the end (going to it) doesn't count.
        /// Brett 0.1.54: "the swat especially after the car crashes walk through the swat car" — a walker checks each leg again
        /// as he goes (a car can crash onto his way after he planned it).
        /// </summary>
        public static bool SegmentBlocked(Vector3 a, Vector3 b)
        {
            var pc = Instance;
            if (pc == null || !pc.Active) return false;
            foreach (var c in pc._cars)
                if (Live(c) && !Inside(c, a, StandPad) && !Inside(c, b, WalkPad) && SegmentHitsBox(c, a, b, WalkPad, out _)) return true;
            return false;
        }

        /// <summary>Within this of a footprint he counts as standing in that car (stepping out of it), not walking into it.</summary>
        const float StandPad = 0.15f;
        const float GridCell = 0.25f;
        const int GridMax = 160;
        static readonly List<Car> s_gridCars = new();

        /// <summary>
        /// A walk from <paramref name="from"/> to <paramref name="to"/> round every vehicle (footprint + 0.5 m) on a 0.25 m grid
        /// (A*, 8 ways), cut down to the points where the way turns: each kept only if the straight walk on from the last one
        /// would cross a vehicle. Null when there is no way (the caller keeps its corners). Rare and once per leg: a crash pile-up.
        /// </summary>
        static List<Vector3> GridRoute(Vector3 from, Vector3 to)
        {
            var pc = Instance;
            if (pc == null) return null;
            s_gridCars.Clear();
            foreach (var c in pc._cars) if (Live(c) && !Inside(c, from, StandPad) && !Inside(c, to, WalkPad)) s_gridCars.Add(c);
            if (s_gridCars.Count == 0) return new List<Vector3>();
            float x0 = Mathf.Min(from.x, to.x), x1 = Mathf.Max(from.x, to.x), z0 = Mathf.Min(from.z, to.z), z1 = Mathf.Max(from.z, to.z);
            foreach (var c in s_gridCars)
                foreach (var k in Corners(c, CornerPad + GridCell)) { x0 = Mathf.Min(x0, k.x); x1 = Mathf.Max(x1, k.x); z0 = Mathf.Min(z0, k.z); z1 = Mathf.Max(z1, k.z); }
            x0 -= 1f; z0 -= 1f; x1 += 1f; z1 += 1f;
            int nx = Mathf.Min(GridMax, Mathf.CeilToInt((x1 - x0) / GridCell) + 1), nz = Mathf.Min(GridMax, Mathf.CeilToInt((z1 - z0) / GridCell) + 1);
            Vector3 At(int i, int k) => new(x0 + i * GridCell, from.y, z0 + k * GridCell);
            bool Free(int i, int k) { var p = At(i, k); foreach (var c in s_gridCars) if (Inside(c, p, WalkPad)) return false; return true; }
            // Two free cells beside a vehicle's corner (it isn't square to the grid) can have a step between them that cuts across
            // its padded footprint (2026-10-05: the pile-up's way round kept a 0.35 m diagonal step through the crashed truck's pad):
            // a step near one is checked against it. Out in the open (most cells) it costs nothing.
            bool Near(int i, int k) { var p = At(i, k); foreach (var c in s_gridCars) if (Inside(c, p, WalkPad + GridCell)) return true; return false; }
            bool Cuts(Vector3 a, Vector3 b) { foreach (var c in s_gridCars) if (SegmentHitsBox(c, a, b, WalkPad, out _)) return true; return false; }
            int Cell(Vector3 p, out int ci, out int ck) { ci = Mathf.Clamp(Mathf.RoundToInt((p.x - x0) / GridCell), 0, nx - 1); ck = Mathf.Clamp(Mathf.RoundToInt((p.z - z0) / GridCell), 0, nz - 1); return ck * nx + ci; }
            int start = Cell(from, out int si, out int sk), goal = Cell(to, out int gi, out int gk);
            var cost = new float[nx * nz]; var came = new int[nx * nz]; var closed = new bool[nx * nz]; var blocked = new sbyte[nx * nz];
            for (int i = 0; i < cost.Length; i++) { cost[i] = float.MaxValue; came[i] = -1; }
            cost[start] = 0f;
            var open = new List<int> { start };
            while (open.Count > 0)
            {
                int bi = 0; float bf = float.MaxValue;
                for (int i = 0; i < open.Count; i++)
                {
                    int n = open[i], ni = n % nx, nk = n / nx;
                    float f = cost[n] + GridCell * Mathf.Sqrt((ni - gi) * (ni - gi) + (nk - gk) * (nk - gk));
                    if (f < bf) { bf = f; bi = i; }
                }
                int cur = open[bi]; open.RemoveAt(bi);
                if (cur == goal) break;
                if (closed[cur]) continue;
                closed[cur] = true;
                int cx = cur % nx, cz = cur / nx;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int x = cx + dx, z = cz + dz;
                        if (x < 0 || z < 0 || x >= nx || z >= nz) continue;
                        int n = z * nx + x;
                        if (closed[n]) continue;
                        if (blocked[n] == 0) blocked[n] = (sbyte)(Free(x, z) || n == goal ? 1 : 2);
                        if (blocked[n] == 2) continue;
                        if (cur != start && n != goal && (Near(x, z) || Near(cx, cz)) && Cuts(At(cx, cz), At(x, z))) continue;
                        float g = cost[cur] + GridCell * (dx != 0 && dz != 0 ? 1.4142f : 1f);
                        if (g < cost[n]) { cost[n] = g; came[n] = cur; open.Add(n); }
                    }
            }
            if (came[goal] < 0 && goal != start) return null;
            var cells = new List<Vector3>();
            for (int n = goal; n >= 0 && n != start; n = came[n]) cells.Add(At(n % nx, n / nx));
            cells.Reverse();
            // Only the turns: from where he is, the farthest grid point he can walk to straight.
            var route = new List<Vector3>();
            var here = from;
            int j = 0;
            while (j < cells.Count)
            {
                int far = j;
                for (int m = cells.Count - 1; m > j; m--) if (!SegmentBlocked(here, cells[m])) { far = m; break; }
                if (far == cells.Count - 1 && !SegmentBlocked(here, to)) break;
                route.Add(cells[far]); here = cells[far]; j = far + 1;
            }
            return route;
        }

        /// <summary>Is <paramref name="p"/> inside a parked vehicle's footprint (+ <paramref name="pad"/>)? (Walk-up paths skip such points.)</summary>
        public static bool InFootprint(Vector3 p, float pad = WalkPad)
        {
            var pc = Instance;
            if (pc == null || !pc.Active) return false;
            foreach (var c in pc._cars) if (Live(c) && Inside(c, p, pad)) return true;
            return false;
        }

        /// <summary>
        /// Clear floor from <paramref name="from"/> along <paramref name="dir"/> (flat), up to <paramref name="maxDist"/>, against the
        /// vehicles out on the street: their footprints (one he's standing in — getting out of it — doesn't count) and the leaves of
        /// their open doors, as they stand swung now (the leaves have no colliders). For a fall or a stumble out there
        /// (Intruder.FreeDistance: the room's raycasts don't see the Outside layer). Transform math only, no allocation.
        /// </summary>
        public static float ClearOfVehicles(Vector3 from, Vector3 dir, float maxDist)
        {
            var pc = Instance;
            dir.y = 0f;
            if (pc == null || !pc.Active || maxDist <= 0f || dir.sqrMagnitude < 1e-6f) return maxDist;
            dir.Normalize();
            var to = from + dir * maxDist;
            float free = maxDist;
            foreach (var c in pc._cars)
            {
                if (!Live(c)) continue;
                var a = from;
                if (Inside(c, a, 0f))
                {
                    // In its footprint by a side (getting out: his root still over the sill): from just outside that side — into the
                    // car is blocked at once, out of it is clear. Deep inside (in a seat): the car's own business, not counted.
                    var l = c.Root.InverseTransformPoint(a);
                    if (c.Half.x - Mathf.Abs(l.x) > NearSide) continue;
                    a = c.Root.TransformPoint(new Vector3((l.x < 0f ? -1f : 1f) * (c.Half.x + 0.01f), l.y, l.z));
                }
                if (SegmentHitsBox(c, a, a + dir * maxDist, 0f, out float at)) free = Mathf.Min(free, at * maxDist);
                for (int i = 0; i < c.Doors.Count && i < c.DoorBoxes.Count; i++)
                {
                    var h = c.Doors[i].Hinge;
                    if (h == null || Quaternion.Angle(h.localRotation, Quaternion.identity) < 5f) continue;
                    var leaf = c.DoorBoxes[i];
                    bool alongZ = leaf.size.z >= leaf.size.x;   // a side door's leaf runs along the car; a back one across it
                    Vector3 h0 = h.TransformPoint(alongZ ? new Vector3(leaf.center.x, 0f, leaf.max.z) : new Vector3(leaf.min.x, 0f, leaf.center.z));
                    Vector3 h1 = h.TransformPoint(alongZ ? new Vector3(leaf.center.x, 0f, leaf.min.z) : new Vector3(leaf.max.x, 0f, leaf.center.z));
                    if (CrossAtXZ(from, to, h0, h1, out float u)) free = Mathf.Min(free, u * maxDist);
                }
            }
            return free;
        }

        /// <summary>Within this of a vehicle's side (inside its footprint) a man counts as at that side (ClearOfVehicles).</summary>
        const float NearSide = 0.45f;

        /// <summary>Where along <paramref name="a"/>–<paramref name="b"/> (0–1) it crosses <paramref name="c"/>–<paramref name="d"/> on the floor (x, z); false: it doesn't.</summary>
        static bool CrossAtXZ(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float u)
        {
            u = 0f;
            float rx = b.x - a.x, rz = b.z - a.z, sx = d.x - c.x, sz = d.z - c.z;
            float den = rx * sz - rz * sx;
            if (Mathf.Abs(den) < 1e-8f) return false;
            float qx = c.x - a.x, qz = c.z - a.z;
            u = (qx * sz - qz * sx) / den;
            float v = (qx * rz - qz * rx) / den;
            return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
        }

        static bool Live(Car c) => c.Sent && c.Root != null && c.Root.gameObject.activeSelf;

        static bool Inside(Car c, Vector3 p, float pad)
        {
            var l = c.Root.InverseTransformPoint(p);
            return Mathf.Abs(l.x) <= c.Half.x + pad && Mathf.Abs(l.z) <= c.Half.y + pad;
        }

        static Vector3[] Corners(Car c, float pad)
        {
            float hx = c.Half.x + pad, hz = c.Half.y + pad;
            var r = c.Root;
            return new[] { r.TransformPoint(new Vector3(-hx, 0f, -hz)), r.TransformPoint(new Vector3(hx, 0f, -hz)),
                           r.TransformPoint(new Vector3(hx, 0f, hz)), r.TransformPoint(new Vector3(-hx, 0f, hz)) };
        }

        /// <summary>Does the segment cross the vehicle's footprint (padded)? <paramref name="at"/>: how far along (0–1) it enters.</summary>
        static bool SegmentHitsBox(Car c, Vector3 a, Vector3 b, float pad, out float at)
        {
            at = 0f;
            var la = c.Root.InverseTransformPoint(a); var lb = c.Root.InverseTransformPoint(b);
            float hx = c.Half.x + pad, hz = c.Half.y + pad, t0 = 0f, t1 = 1f;
            var d = lb - la;
            if (!Clip(-d.x, la.x + hx, ref t0, ref t1) || !Clip(d.x, hx - la.x, ref t0, ref t1) ||
                !Clip(-d.z, la.z + hz, ref t0, ref t1) || !Clip(d.z, hz - la.z, ref t0, ref t1)) return false;
            at = t0;
            return t1 > t0 + 1e-4f;

            static bool Clip(float p, float q, ref float lo, ref float hi)
            {
                if (Mathf.Abs(p) < 1e-6f) return q >= 0f;
                float r = q / p;
                if (p < 0f) { if (r > hi) return false; if (r > lo) lo = r; }
                else { if (r < lo) return false; if (r < hi) hi = r; }
                return true;
            }
        }
    }
}
