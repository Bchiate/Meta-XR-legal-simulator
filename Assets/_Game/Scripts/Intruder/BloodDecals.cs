using System.Collections.Generic;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// World blood (wall/floor splatters, drops and death pools) for the whole room: pooled quads, lifetimes, a soft fade-out
    /// instead of popping, drying (darker with age), and a cap so a long session can't pile up draw calls (each decal is its
    /// own draw). Only a body's death pool (it's fed by the body) and blood coming out under the closed door (<see cref="Seep"/>)
    /// grow; impact marks land at their final size. Body wounds stay on the bones (<see cref="WoundSystem"/>) and go with the body.
    /// </summary>
    public class BloodDecals : MonoBehaviour
    {
        const int SpareMax = 64; // live cap: HIS.Core.PerfBudget.BloodDecalCap (120; fewer when the headset runs warm)
        const float FadeSeconds = 2.5f, DrySeconds = 90f;

        class Entry
        {
            public Transform T;
            public Renderer R;
            public Object Owner;
            public bool Owned;
            public float Born, DieAt;
            public float GrowFrom, GrowTo, GrowSeconds, Aspect;
            public Vector4 St;
            public Color Base;
            // A seep: its back edge stays at Edge (the gap) while it spreads along its local +Y; Reach = the blob's visible
            // radius × quad width (0 = not a seep). Waiting: placed but hidden until Born (it starts a moment after the shot).
            public float Reach;
            public Vector3 Edge;
            public bool Waiting;
        }

        static BloodDecals _instance;
        static MaterialPropertyBlock _mpb;
        static Mesh _quad;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"), BaseMapStId = Shader.PropertyToID("_BaseMap_ST");
        readonly List<Entry> _entries = new();
        readonly Stack<Entry> _spare = new(); // recycled quads (inactive, under this object)

        static BloodDecals Instance
        {
            get
            {
                if (_instance == null) _instance = new GameObject("[HIS] BloodDecals").AddComponent<BloodDecals>();
                return _instance;
            }
        }

        /// <summary>One cell of the 2x2 splat atlas: 0/2 = spatter with flecks (2 is the elongated one), 1/3 = solid blobs.</summary>
        public static Vector4 AtlasCell(int cell) => new(0.5f, 0.5f, (cell & 1) * 0.5f, (cell >> 1) * 0.5f);

        /// <summary>
        /// Place a world decal (a pooled quad): <paramref name="width"/> x <paramref name="height"/> metres, atlas
        /// <paramref name="cell"/>. <paramref name="parent"/> pins it to a moving surface (the open door leaf). <paramref name="owner"/>
        /// ties it to a body: it stays while the body does and fades on <see cref="Release"/>. Only an owned pool may grow
        /// (<paramref name="growTo"/> &gt; 0 = spreads to that width over <paramref name="growSeconds"/>, fast then slower).
        /// </summary>
        public static void Spawn(Material mat, Vector3 pos, Quaternion rot, float width, float height, int cell, float lifetime,
            Transform parent = null, Object owner = null, float growTo = 0f, float growSeconds = 0f, int layer = 0)
        {
            if (mat == null) return;
            var inst = Instance;
            var e = inst.Place(mat, pos, rot, width, height, cell, lifetime, parent, owner);
            e.T.gameObject.layer = layer; // 0: the room; the door camera's Outside layer for blood out on the porch (Brett 0.1.22)
            e.GrowTo = e.Owned && growSeconds > 0f ? growTo : 0f; e.GrowSeconds = growSeconds;
            e.T.gameObject.SetActive(true);
            inst.Trim();
        }

        /// <summary>
        /// Blood coming out of a gap (under the closed door, Brett 0.1.17: "some blood pours from under the door after a
        /// second"): after <paramref name="delay"/> s a blob (atlas <paramref name="cell"/>) pushes out from
        /// <paramref name="edge"/> along <paramref name="rot"/>'s local +Y and spreads from <paramref name="growFrom"/> to
        /// <paramref name="growTo"/> wide over <paramref name="growSeconds"/>, quick at first, then slower — its back edge held
        /// at the gap the whole time (a blob growing about its centre reads as a sticker scaling up). <paramref name="reach"/> =
        /// the blob's visible radius × quad width. Not owned: a lifetime, the cap (it counts from now, hidden until it starts)
        /// and <see cref="ClearAll"/>, like any mark.
        /// </summary>
        public static void Seep(Material mat, Vector3 edge, Quaternion rot, float growFrom, float growTo, float aspect, int cell,
            float reach, float growSeconds, float delay, float lifetime)
        {
            if (mat == null || growTo <= growFrom) return;
            var inst = Instance;
            var e = inst.Place(mat, edge + rot * Vector3.up * (growFrom * aspect * reach), rot, growFrom, growFrom * aspect, cell, lifetime, null, null);
            e.Born += delay; e.DieAt += delay;
            e.GrowTo = growTo; e.GrowSeconds = Mathf.Max(0.1f, growSeconds);
            e.Reach = reach; e.Edge = edge; e.Waiting = delay > 0f;
            e.T.gameObject.SetActive(!e.Waiting);
            inst.Trim();
        }

        Entry Place(Material mat, Vector3 pos, Quaternion rot, float width, float height, int cell, float lifetime, Transform parent, Object owner)
        {
            var e = Take();
            e.T.SetParent(null, false);
            e.T.SetPositionAndRotation(pos, rot);
            e.T.localScale = new Vector3(width, height, 1f);
            if (parent != null) e.T.SetParent(parent, true);
            e.R.sharedMaterial = mat;
            bool owned = owner != null;
            e.Owner = owner; e.Owned = owned; e.Born = Time.time;
            e.DieAt = owned ? float.PositiveInfinity : Time.time + lifetime;
            e.GrowFrom = width; e.Aspect = width > 1e-5f ? height / width : 1f;
            e.St = AtlasCell(cell);
            e.Base = mat.HasProperty(BaseColorId) ? mat.GetColor(BaseColorId) : Color.white;
            Tint(e, 1f, 0f);
            _entries.Add(e);
            return e;
        }

        /// <summary>Over the cap: the oldest one that isn't already going starts fading now.</summary>
        void Trim()
        {
            float now = Time.time;
            int live = 0;
            foreach (var x in _entries) if (!x.Owned && x.DieAt - now > FadeSeconds) live++;
            // At most 2 per new mark: when the heat budget lowers the cap mid-fight, the extra old blood drains away over
            // the next splats instead of half the room fading at once (at a steady cap only 1 is ever over).
            int cap = HIS.Core.PerfBudget.BloodDecalCap, trimmed = 0;
            for (int i = 0; live > cap && trimmed < 2 && i < _entries.Count; i++)
            {
                var x = _entries[i];
                if (!x.Owned && x.DieAt - now > FadeSeconds) { x.DieAt = now + FadeSeconds; live--; trimmed++; }
            }
        }

        /// <summary>Fade out everything tied to this owner (a retired corpse's pool).</summary>
        public static void Release(Object owner)
        {
            if (_instance == null || owner == null) return;
            foreach (var e in _instance._entries)
                if (e.Owner == owner && e.DieAt - Time.time > FadeSeconds) e.DieAt = Time.time + FadeSeconds;
        }

        /// <summary>Session reset: remove all world blood at once (including marks pinned to the door leaf).</summary>
        public static void ClearAll()
        {
            if (_instance == null) return;
            for (int i = _instance._entries.Count - 1; i >= 0; i--) _instance.Recycle(i);
        }

        public static string DebugSummary
        {
            get
            {
                if (_instance == null) return "none";
                int fading = 0, growing = 0, seeps = 0; float biggest = 0;
                foreach (var e in _instance._entries)
                {
                    if (e.T == null) continue;
                    if (e.DieAt - Time.time < FadeSeconds) fading++;
                    if (e.GrowTo > 0f) growing++;
                    if (e.Reach > 0f) seeps++;
                    biggest = Mathf.Max(biggest, e.T.lossyScale.x);
                }
                return $"{_instance._entries.Count} world decals ({fading} fading, {growing} pooling, {seeps} under the door, largest {biggest:F2} m, {_instance._spare.Count} spare)";
            }
        }

        /// <summary>A seep as drawn (ScenarioTests): its back edge (at the gap), the tip of its visible front, its visible width.</summary>
        public struct SeepView { public Vector3 Edge, Front; public float Width; }

        /// <summary>Seeps drawn right now (each added to <paramref name="into"/> when given); <paramref name="pending"/> = placed, not started yet.</summary>
        public static int ShownSeeps(List<SeepView> into, out int pending)
        {
            pending = 0;
            if (_instance == null) return 0;
            int shown = 0;
            foreach (var e in _instance._entries)
            {
                if (e.T == null || e.Reach <= 0f) continue;
                if (e.Waiting) { pending++; continue; }
                shown++;
                var s = e.T.lossyScale;
                into?.Add(new SeepView { Edge = e.Edge, Front = e.T.position + e.T.up * (s.y * e.Reach), Width = 2f * s.x * e.Reach });
            }
            return shown;
        }

        public static Mesh Quad
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "BloodQuad" };
                _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
                _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                _quad.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
                _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                return _quad;
            }
        }

        Entry Take()
        {
            while (_spare.Count > 0)
            {
                var e = _spare.Pop();
                if (e.T != null) return e;
            }
            var go = new GameObject("Blood");
            go.SetActive(false);
            go.AddComponent<MeshFilter>().sharedMesh = Quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            return new Entry { T = go.transform, R = mr };
        }

        void Recycle(int i)
        {
            var e = _entries[i];
            _entries.RemoveAt(i);
            e.Owner = null; e.GrowTo = 0f; e.Reach = 0f; e.Waiting = false;
            if (e.T == null) return; // destroyed with its parent (door rebuilt)
            e.T.gameObject.layer = 0;
            if (_spare.Count >= SpareMax) { Destroy(e.T.gameObject); return; }
            e.T.gameObject.SetActive(false);
            e.T.SetParent(transform, false);
            _spare.Push(e);
        }

        /// <summary>Drying (darker, browner with age) and the fade-out, through a property block so batching/materials stay shared.</summary>
        static void Tint(Entry e, float alpha, float age)
        {
            _mpb ??= new MaterialPropertyBlock();
            float dry = Mathf.Clamp01(age / DrySeconds);
            var c = e.Base;
            c.r *= 1f - 0.35f * dry; c.g *= 1f - 0.5f * dry; c.b *= 1f - 0.5f * dry;
            c.a *= alpha;
            _mpb.Clear();
            _mpb.SetVector(BaseMapStId, e.St);
            _mpb.SetColor(BaseColorId, c);
            e.R.SetPropertyBlock(_mpb);
        }

        void Update()
        {
            float now = Time.time;
            int frame = Time.frameCount;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.T == null) { _entries.RemoveAt(i); continue; }
                // Owner gone without a Release (destroyed directly): fade its pool too.
                if (e.Owned && e.Owner == null && e.DieAt > now + FadeSeconds) e.DieAt = now + FadeSeconds;
                if (now >= e.DieAt) { Recycle(i); continue; }
                if (e.Waiting) { if (now < e.Born) continue; e.Waiting = false; e.T.gameObject.SetActive(true); }
                if (e.GrowTo > 0f)
                {
                    // Fed by a failing heart: spreads fast at first, then slower and slower (radius ~ sqrt(t)). A seep has to
                    // squeeze out of its gap: it creeps out at a finite speed and slows (ease-out), its back edge at the gap.
                    float k = Mathf.Clamp01((now - e.Born) / e.GrowSeconds);
                    float s = Mathf.Lerp(e.GrowFrom, e.GrowTo, e.Reach > 0f ? 1f - (1f - k) * (1f - k) : Mathf.Sqrt(k));
                    e.T.localScale = new Vector3(s, s * e.Aspect, 1f);
                    if (e.Reach > 0f) e.T.position = e.Edge + e.T.up * (s * e.Aspect * e.Reach);
                    if (k >= 1f) e.GrowTo = 0f;
                }
                float left = e.DieAt - now;
                // Fading: every frame. Drying is slow: each decal is re-tinted every 16th frame (staggered).
                if (left < FadeSeconds || ((i + frame) & 15) == 0) Tint(e, Mathf.Clamp01(left / FadeSeconds), now - e.Born);
            }
        }
    }
}
