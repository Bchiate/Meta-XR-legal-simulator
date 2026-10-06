using System.Collections.Generic;
using HIS.Core;
using HIS.Weapons;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>What an outside surface is made of: what a round kicks up from it, how it sounds, whether it keeps a mark.</summary>
    public enum OutsideKind { Dirt, Concrete, Asphalt, Wood, Metal }

    /// <summary>
    /// Where a missed round lands outside (Brett 0.1.40: "im feeling like a lot of the shots at the cops werent hitting" — the
    /// porch set had no colliders, so a round that missed a man on the path vanished without a trace and he couldn't walk his
    /// aim in). A few cheap box colliders on layer 11 (<see cref="PorchSet"/> places them from the same numbers it builds its
    /// meshes from): the ground (one thin slab under lawn, path, sidewalk and street; the kind comes from where it's hit,
    /// <see cref="PorchSet.GroundKindAt"/>), the stoop, the houses and roofs across the street, lamp posts, the mailbox, tree
    /// trunks, the backyard's fence, bins and AC unit. Bushes and canopies have none (a round goes through leaves).
    /// Like <see cref="VehicleBody"/> the round stops here (IInanimate: no flesh sound, no hit pulse): a burst of dust and bits
    /// sized to read at 10 m at night through the doorway (<see cref="OutsideImpacts"/>), a scar on hard surfaces, one sound.
    /// Only rounds count: a punch or a blade reaching out through the doorway does nothing here.
    /// </summary>
    public class OutsideSurface : MonoBehaviour, IDamageable, IInanimate
    {
        public OutsideKind Kind;
        internal PorchSet Porch;
        /// <summary>The ground slab: the kind is looked up where the round landed (path, lawn, sidewalk, street).</summary>
        internal bool Ground;

        public float ApplyDamage(in DamageInfo info)
        {
            if (info.Type != DamageType.Bullet || Porch == null) return 0f;
            var kind = Ground ? Porch.GroundKindAt(info.Point) : Kind;
            // Two porches on neighbouring walls overlap outside: the burst goes on the one the headset is looking at.
            var shown = Porch.ShownAt(info.Point);
            if (shown.Impacts != null) shown.Impacts.Hit(kind, info, name);
            return 0f;
        }

        /// <summary>An axis-aligned box collider from <paramref name="min"/> to <paramref name="max"/> in <paramref name="parent"/>'s space (the porch's).</summary>
        internal static OutsideSurface Box(PorchSet porch, Transform parent, string name, Vector3 min, Vector3 max, OutsideKind kind, bool ground = false)
        {
            var go = new GameObject(name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (min + max) * 0.5f;
            var s = max - min;
            go.AddComponent<BoxCollider>().size = new Vector3(Mathf.Max(0.02f, Mathf.Abs(s.x)), Mathf.Max(0.02f, Mathf.Abs(s.y)), Mathf.Max(0.02f, Mathf.Abs(s.z)));
            var o = go.AddComponent<OutsideSurface>();
            o.Kind = kind; o.Porch = porch; o.Ground = ground;
            return o;
        }

        /// <summary>
        /// A thin slab whose top face runs from the line through <paramref name="eave"/> up to the line through
        /// <paramref name="ridge"/> (both along x, <paramref name="width"/> wide): a gable roof's street-side slope.
        /// </summary>
        internal static OutsideSurface Slope(PorchSet porch, Transform parent, string name, Vector3 eave, Vector3 ridge, float width, OutsideKind kind)
        {
            const float thick = 0.12f;
            var along = ridge - eave; along.x = 0f;
            float len = along.magnitude;
            if (len < 1e-3f) return null;
            along /= len;
            var n = new Vector3(0f, along.z, -along.y);   // the slope's face: up and toward the street
            var go = new GameObject(name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (eave + ridge) * 0.5f - n * (thick * 0.5f);
            go.transform.localRotation = Quaternion.LookRotation(along, n);
            go.AddComponent<BoxCollider>().size = new Vector3(Mathf.Max(0.02f, width), thick, len);
            var o = go.AddComponent<OutsideSurface>();
            o.Kind = kind; o.Porch = porch;
            return o;
        }
    }

    /// <summary>
    /// One porch's bullet impacts, pooled: two particle systems fed with Emit() (a soft puff + flash, and flying bits: clods,
    /// chips, splinters, sparks, as velocity-stretched streaks) on the porch's additive glow material, and one mesh of up to
    /// <see cref="MaxMarks"/> scars on its lit material (the oldest goes first). Last Stand-style (Brett 0.1.40: "the cars dont
    /// show like theyre getting hit or anything. Last Stand is very good at this"): big and bright enough to read at 10 m at
    /// night through the doorway — a 0.3–0.6 m puff growing to ~1 m, a 0.25–0.45 m pop of light, 10–16 streaks at 2–9 m/s,
    /// all gone within 0.6 s. Every renderer is the porch's (<see cref="PorchSet.AddLive"/>): only its camera's feed and the
    /// headset through its open doorway ever draw them. The glow shader tints every sprite warm in the headset look
    /// (1, 0.8, 0.55): the colours below are what the headset should show, with that tint divided back out; the IR feed takes
    /// their luma. Per shot: ≤ 1 flash + 6 puffs + 16 bits (Emit calls), ≤ 1 scar (a 448-vertex upload), ≤ 1 sound; a
    /// shotgun's pellets past the third in a frame get a small burst and no scar, past the twelfth nothing.
    /// </summary>
    public sealed class OutsideImpacts
    {
        public const int MaxMarks = 32;
        const int MarkVerts = 14, MarkTris = 12;   // a ragged pale hexagon (the chipped scar) round a dark one (the hole)

        readonly PorchSet _porch;
        readonly ParticleSystem _puff, _bits;
        readonly Mesh _markMesh;
        readonly GameObject _markGo;
        readonly Vector3[] _mv = new Vector3[MaxMarks * MarkVerts], _mn = new Vector3[MaxMarks * MarkVerts];
        readonly Color32[] _mc = new Color32[MaxMarks * MarkVerts];
        int _nextMark;

        /// <summary>Rounds that landed on this porch's set (tests), and the scars it carries now.</summary>
        public int Count { get; private set; }
        public int Marks { get; private set; }

        static int s_frame = -1, s_hits;
        static bool s_sound;

        struct Look
        {
            public Color Dust, Bits, Flash;
            public int DustN, BitsN;
            public float DustMin, DustMax, BitMin, BitMax, SpeedMin, SpeedMax, LifeMin, LifeMax, FlashSize, Up;
            public Look(Color dust, int dustN, float dustMin, float dustMax, Color bits, int bitsN, float bitMin, float bitMax,
                        float speedMin, float speedMax, float lifeMin, float lifeMax, Color flash, float flashSize, float up)
            {
                Dust = Untint(dust); DustN = dustN; DustMin = dustMin; DustMax = dustMax;
                Bits = Untint(bits); BitsN = bitsN; BitMin = bitMin; BitMax = bitMax;
                SpeedMin = speedMin; SpeedMax = speedMax; LifeMin = lifeMin; LifeMax = lifeMax;
                Flash = Untint(flash); FlashSize = flashSize; Up = up;
            }
            // The glow shader multiplies every sprite by (1, 0.8, 0.55) in the headset look: divide it back out.
            static Color Untint(Color c) => new Color(Mathf.Min(1f, c.r), Mathf.Min(1f, c.g / 0.8f), Mathf.Min(1f, c.b / 0.55f), 1f);
        }

        // By OutsideKind. Dust: a brown-grey cloud off the lawn, pale off concrete, grey off the street, tan off wood, a wisp
        // off metal. Bits: clods (thrown up, heavy), chips (fast, flat), splinters (light), sparks (hot, fastest, shortest).
        static readonly Look[] s_looks =
        {
            // Dirt / grass
            new Look(new Color(0.30f, 0.23f, 0.15f), 6, 0.35f, 0.60f, new Color(0.50f, 0.40f, 0.27f), 12, 0.035f, 0.07f, 2.0f, 4.5f, 0.35f, 0.60f,
                     new Color(0.45f, 0.38f, 0.28f), 0.30f, 0.75f),
            // Concrete (stoop, path, sidewalks, curb)
            new Look(new Color(0.36f, 0.35f, 0.33f), 5, 0.30f, 0.50f, new Color(0.85f, 0.82f, 0.76f), 12, 0.030f, 0.055f, 3.0f, 6.5f, 0.20f, 0.45f,
                     new Color(0.95f, 0.85f, 0.62f), 0.30f, 0.50f),
            // Asphalt (the street)
            new Look(new Color(0.28f, 0.28f, 0.27f), 5, 0.30f, 0.50f, new Color(0.60f, 0.58f, 0.55f), 10, 0.030f, 0.050f, 3.0f, 6.0f, 0.20f, 0.45f,
                     new Color(0.90f, 0.78f, 0.55f), 0.30f, 0.50f),
            // Wood (siding, roofs, fence, trunks, posts)
            new Look(new Color(0.32f, 0.26f, 0.17f), 4, 0.25f, 0.42f, new Color(0.90f, 0.74f, 0.48f), 12, 0.030f, 0.050f, 2.5f, 5.0f, 0.30f, 0.55f,
                     new Color(0.60f, 0.50f, 0.34f), 0.24f, 0.60f),
            // Metal (lamp posts, mailbox, AC unit): sparks
            new Look(new Color(0.22f, 0.22f, 0.22f), 2, 0.20f, 0.30f, new Color(1.00f, 0.78f, 0.42f), 16, 0.025f, 0.045f, 4.0f, 9.0f, 0.15f, 0.35f,
                     new Color(1.00f, 0.88f, 0.55f), 0.45f, 0.35f),
        };

        // Fresh scars (lit like the set, so they show under the lamps, the light bars and the camera's IR): pale chipped concrete,
        // lighter asphalt, bare wood, bright bare metal; the hole itself near black. Alpha 0 = the plain pattern.
        static readonly Color32[] s_scar =
        {
            new Color32(120, 100, 80, 0), new Color32(220, 214, 204, 0), new Color32(150, 150, 148, 0), new Color32(235, 200, 140, 0), new Color32(240, 240, 245, 0),
        };
        static readonly Color32 s_hole = new Color32(10, 9, 8, 0);

        public OutsideImpacts(PorchSet porch)
        {
            _porch = porch;
            // The puff: soft sprites that grow ×1.8 and slow down (drag), fading out; the flash rides in it (0.07 s).
            _puff = MakeSystem("ShotDust", 96, 0f, false, 3.5f);
            var sol = _puff.sizeOverLifetime; sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(0.25f, 1.15f), new Keyframe(1f, 1.8f)));
            // The bits: streaks stretched along their velocity, falling.
            _bits = MakeSystem("ShotBits", 256, 1f, true, 0.8f);

            _markGo = new GameObject("ShotMarks") { layer = PorchSet.OutsideLayer };
            _markGo.transform.SetParent(porch.transform, false);
            _markMesh = new Mesh { name = $"Shot marks {porch.name}" };
            _markMesh.MarkDynamic();
            var tris = new int[MaxMarks * MarkTris * 3];
            for (int m = 0; m < MaxMarks; m++)
                for (int fan = 0; fan < 2; fan++)
                {
                    int c = m * MarkVerts + fan * 7, t = (m * MarkTris + fan * 6) * 3;
                    for (int i = 0; i < 6; i++) { tris[t + i * 3] = c; tris[t + i * 3 + 1] = c + 1 + i; tris[t + i * 3 + 2] = c + 1 + (i + 1) % 6; }
                }
            _markMesh.SetVertices(_mv); _markMesh.SetNormals(_mn); _markMesh.SetColors(_mc);
            _markMesh.SetUVs(0, new Vector2[MaxMarks * MarkVerts]);
            _markMesh.SetTriangles(tris, 0);
            _markGo.AddComponent<MeshFilter>().sharedMesh = _markMesh;
            var r = _markGo.AddComponent<MeshRenderer>();
            r.sharedMaterial = porch.SetMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            porch.AddLive(r);
            _markGo.SetActive(false);   // no draw call until the first scar
        }

        ParticleSystem MakeSystem(string name, int max, float gravity, bool stretched, float drag)
        {
            var go = new GameObject(name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(_porch.transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false; main.playOnAwake = false; main.maxParticles = max;
            main.startSpeed = 0f;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var lv = ps.limitVelocityOverLifetime; lv.enabled = true;
            lv.limit = 100f; lv.dampen = 0f; lv.drag = drag;
            lv.multiplyDragByParticleSize = false; lv.multiplyDragByParticleVelocity = false;
            // Additive: fading the colour to black is the fade (the glow shader ignores alpha).
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.55f, 0.55f, 0.55f), stretched ? 0.5f : 0.35f), new GradientColorKey(Color.black, 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            var col = ps.colorOverLifetime; col.enabled = true; col.color = new ParticleSystem.MinMaxGradient(fade);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _porch.GlowMaterial;
            r.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretched) { r.velocityScale = 0.045f; r.lengthScale = 1.5f; }
            r.SetActiveVertexStreams(new List<ParticleSystemVertexStream>
                { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV });
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            _porch.AddLive(r);
            return ps;
        }

        /// <summary>A round landed on <paramref name="kind"/> (the surface <paramref name="what"/>) at info.Point.</summary>
        public void Hit(OutsideKind kind, in DamageInfo info, string what)
        {
            if (_puff == null || _bits == null) return;
            if (Time.frameCount != s_frame) { s_frame = Time.frameCount; s_hits = 0; s_sound = false; }
            s_hits++; Count++;
            if (s_hits > 12) return;                              // a shotgun's 13th+ pellet this frame: enough already
            float scale = s_hits <= 3 ? 1f : 0.35f;               // pellets past the third: a small burst each
            var look = s_looks[(int)kind];
            var p = info.Point;
            var n = info.Normal.sqrMagnitude > 0.5f ? info.Normal.normalized : Vector3.up;
            var dir = info.Direction.sqrMagnitude > 0.5f ? info.Direction.normalized : -n;
            var bounce = Vector3.Reflect(dir, n);                 // a grazing round throws the dirt on ahead and up

            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            // The pop of light where it struck: the one thing that says exactly where the round went.
            ep.position = p + n * 0.05f; ep.velocity = Vector3.zero;
            ep.startSize = look.FlashSize * (scale < 1f ? 0.6f : 1f); ep.startLifetime = 0.07f; ep.startColor = look.Flash;
            _puff.Emit(ep, 1);
            int dust = Mathf.Max(1, Mathf.RoundToInt(look.DustN * scale));
            for (int i = 0; i < dust; i++)
            {
                ep.position = p + n * 0.08f + Random.insideUnitSphere * 0.05f;
                ep.velocity = n * Random.Range(0.4f, 1.3f) + bounce * Random.Range(0.3f, 1.2f) + Random.insideUnitSphere * 0.35f;
                ep.startSize = Random.Range(look.DustMin, look.DustMax);
                ep.startLifetime = Random.Range(0.4f, 0.6f);
                ep.startColor = look.Dust;
                _puff.Emit(ep, 1);
            }
            int bits = Mathf.Max(2, Mathf.RoundToInt(look.BitsN * scale));
            for (int i = 0; i < bits; i++)
            {
                var v = n * look.Up + bounce * (1f - look.Up) + Random.insideUnitSphere * 0.55f;
                if (Vector3.Dot(v, n) < 0.05f) v += n * 0.3f;     // never into the surface
                ep.position = p + n * 0.02f;
                ep.velocity = v.normalized * Random.Range(look.SpeedMin, look.SpeedMax);
                ep.startSize = Random.Range(look.BitMin, look.BitMax);
                ep.startLifetime = Random.Range(look.LifeMin, look.LifeMax);
                ep.startColor = look.Bits;
                _bits.Emit(ep, 1);
            }
            if (kind != OutsideKind.Dirt && s_hits <= 3) Mark(kind, p, n);
            if (!s_sound) { s_sound = true; Sound(kind, p); }
            Log.V($"[HIS] Round landed outside: {kind} ({what}) at {_porch.transform.InverseTransformPoint(p):F1} (porch space)");
        }

        /// <summary>One sound per shot: a thud in the dirt, a crack off concrete, wood, a ping off metal (muffled through the shut door).</summary>
        void Sound(OutsideKind kind, Vector3 p)
        {
            string key = kind == OutsideKind.Metal ? "metal.hit" : "impact.wall";
            if (!Sfx.Has(key)) return;
            float volume = kind == OutsideKind.Dirt ? 0.5f : kind == OutsideKind.Metal ? 0.7f : 0.65f;
            float pitch = kind switch { OutsideKind.Dirt => 0.6f, OutsideKind.Wood => 0.78f, OutsideKind.Asphalt => 0.95f, OutsideKind.Concrete => 1.08f, _ => 1f };
            var door = _porch.Door;
            if (door != null && !door.IsOpen) { door.PlayThrough(key, p, volume, pitch, 900f); return; }
            int h = Sfx.Play(key, p, volume, 0.05f);
            if (h != 0 && pitch != 1f) Sfx.ScalePitch(h, pitch);
        }

        /// <summary>A scar on a hard surface: a ragged pale hexagon 4.5–7 cm across (metal 2.5–3.5 cm) round a dark 1.5–2 cm hole.</summary>
        void Mark(OutsideKind kind, Vector3 world, Vector3 normal)
        {
            var t = _porch.transform;
            var p = t.InverseTransformPoint(world);
            var n = t.InverseTransformDirection(normal).normalized;
            var u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            u = Quaternion.AngleAxis(Random.Range(0f, 360f), n) * u;
            var w = Vector3.Cross(n, u);                          // Cross(u, w) = n: the fans face out of the surface
            bool metal = kind == OutsideKind.Metal;
            int b = _nextMark * MarkVerts;
            Fan(b, p + n * 0.006f, u, w, n, metal ? Random.Range(0.025f, 0.035f) : Random.Range(0.045f, 0.07f), 0.35f, s_scar[(int)kind]);
            Fan(b + 7, p + n * 0.008f, u, w, n, metal ? 0.011f : Random.Range(0.015f, 0.02f), 0.2f, s_hole);
            _nextMark = (_nextMark + 1) % MaxMarks;
            Marks = Mathf.Min(Marks + 1, MaxMarks);
            _markMesh.SetVertices(_mv); _markMesh.SetNormals(_mn); _markMesh.SetColors(_mc);
            _markMesh.RecalculateBounds();
            if (!_markGo.activeSelf) _markGo.SetActive(true);
        }

        void Fan(int b, Vector3 c, Vector3 u, Vector3 w, Vector3 n, float radius, float ragged, Color32 col)
        {
            _mv[b] = c; _mn[b] = n; _mc[b] = col;
            for (int i = 0; i < 6; i++)
            {
                float a = (i + Random.Range(-0.2f, 0.2f)) * (Mathf.PI / 3f), r = radius * Random.Range(1f - ragged, 1f + ragged * 0.6f);
                _mv[b + 1 + i] = c + (u * Mathf.Cos(a) + w * Mathf.Sin(a)) * r;
                _mn[b + 1 + i] = n; _mc[b + 1 + i] = col;
            }
        }

        /// <summary>Clears the scars and any dust still in the air (a new night; the lead's call when).</summary>
        public void Clear()
        {
            System.Array.Clear(_mv, 0, _mv.Length);
            if (_markMesh != null) { _markMesh.SetVertices(_mv); _markMesh.RecalculateBounds(); }
            if (_markGo != null) _markGo.SetActive(false);
            _nextMark = 0; Marks = 0;
            if (_puff != null) _puff.Clear();
            if (_bits != null) _bits.Clear();
        }

        public void Dispose()
        {
            if (_markMesh != null) Object.Destroy(_markMesh);
        }
    }
}
