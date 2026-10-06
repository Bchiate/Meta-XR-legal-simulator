using System.Collections.Generic;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// Tiny procedural mesh builder for the porch sets: quads, boxes, blobs and billboards with vertex colour (albedo, grey)
    /// and a surface pattern id in vertex alpha (read by HIS/SecurityPorch: 0 plain, 1 grass, 2 concrete, 3 siding/boards,
    /// 4 asphalt, 5 foliage, 6 emissive). uv is in metres along each face, so patterns keep their real-world scale.
    /// Winding: a quad (origin, u, w) faces Cross(u, w) (Unity's clockwise front face).
    /// </summary>
    internal sealed class MeshKit
    {
        public const byte Plain = 0, Grass = 1, Concrete = 2, Siding = 3, Asphalt = 4, Foliage = 5, Emissive = 6;

        readonly List<Vector3> _v = new();
        readonly List<Vector3> _n = new();
        readonly List<Vector2> _uv = new();
        readonly List<Color32> _c = new();
        readonly List<int> _t = new();

        public int Triangles => _t.Count / 3;

        /// <summary>A vertex colour for <see cref="Tri"/>/<see cref="Quad(Vector3, Vector3, Vector3, Color32, Vector2)"/>: grey albedo + pattern id.</summary>
        public static Color32 Grey(float grey, byte pattern = Plain) => Col(grey, pattern);

        /// <summary>
        /// A colour albedo + pattern id (Brett 2026-10-05, the vehicles: red tail lights, red / blue light-bar lenses, blue plate
        /// lettering). The headset's colour look shows it; the night feed shows its luma (the monitor draws Rec.709 luma).
        /// </summary>
        public static Color32 Rgb(float r, float g, float b, byte pattern = Plain) => Col(new Color(r, g, b), pattern);

        static Color32 Col(float grey, byte pattern) => Col(new Color(grey, grey, grey), pattern);
        static Color32 Col(Color c, byte pattern)
        {
            var c32 = (Color32)c;
            c32.a = (byte)Mathf.RoundToInt(pattern * 255f / 8f);
            return c32;
        }

        /// <summary>A quad from <paramref name="o"/> spanning <paramref name="u"/> and <paramref name="w"/>; uv starts at <paramref name="uv0"/> (metres).</summary>
        public void Quad(Vector3 o, Vector3 u, Vector3 w, float grey, byte pattern, Vector2 uv0 = default) =>
            Quad(o, u, w, Col(grey, pattern), uv0);

        public void Quad(Vector3 o, Vector3 u, Vector3 w, Color32 col, Vector2 uv0 = default)
        {
            var n = Vector3.Cross(u, w).normalized;
            int b = _v.Count;
            float lu = u.magnitude, lw = w.magnitude;
            _v.Add(o); _v.Add(o + u); _v.Add(o + u + w); _v.Add(o + w);
            _uv.Add(uv0); _uv.Add(uv0 + new Vector2(lu, 0f)); _uv.Add(uv0 + new Vector2(lu, lw)); _uv.Add(uv0 + new Vector2(0f, lw));
            for (int i = 0; i < 4; i++) { _n.Add(n); _c.Add(col); }
            _t.Add(b); _t.Add(b + 1); _t.Add(b + 2);
            _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
        }

        /// <summary>An emissive quad shaded by height (the night sky): <paramref name="bottom"/> at its lowest corner → <paramref name="top"/>.</summary>
        public void GradientQuad(Vector3 o, Vector3 u, Vector3 w, float bottom, float top)
        {
            int b = _v.Count;
            Quad(o, u, w, Col(bottom, Emissive));
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = b; i < b + 4; i++) { lo = Mathf.Min(lo, _v[i].y); hi = Mathf.Max(hi, _v[i].y); }
            for (int i = b; i < b + 4; i++)
                _c[i] = Col(Mathf.Lerp(bottom, top, Mathf.InverseLerp(lo, hi, _v[i].y)), Emissive);
        }

        /// <summary>Axis-aligned box (in the kit's space) from min to max; the bottom face is left out unless asked.</summary>
        public void Box(Vector3 min, Vector3 max, float grey, byte pattern, bool bottom = false) =>
            Box(min, max, Col(grey, pattern), bottom);

        public void Box(Vector3 min, Vector3 max, Color32 col, bool bottom = false)
        {
            var s = max - min;
            Vector3 X = new(s.x, 0, 0), Y = new(0, s.y, 0), Z = new(0, 0, s.z);
            var uvA = new Vector2(min.x, min.y);
            Quad(min, X, Y, col, uvA); FlipLastQuad();                             // -Z face (Cross(X,Y) = +z points in: flipped)
            Quad(new Vector3(min.x, min.y, max.z), X, Y, col, uvA);                 // +Z face
            Quad(min, Y, Z, col, new Vector2(min.z, min.y)); FlipLastQuad();       // -X face (flipped)
            Quad(new Vector3(max.x, min.y, min.z), Y, Z, col, new Vector2(min.z, min.y)); // +X: Cross(Y,Z) = +x
            Quad(new Vector3(min.x, max.y, min.z), Z, X, col, new Vector2(min.x, min.z)); // top: Cross(Z,X) = +y
            if (bottom) { Quad(min, Z, X, col, new Vector2(min.x, min.z)); FlipLastQuad(); }
        }

        void FlipLastQuad()
        {
            int t = _t.Count - 6;
            (_t[t + 1], _t[t + 2]) = (_t[t + 2], _t[t + 1]);
            (_t[t + 4], _t[t + 5]) = (_t[t + 5], _t[t + 4]);
            int v = _v.Count - 4;
            for (int i = v; i < v + 4; i++) _n[i] = -_n[i];
        }

        /// <summary>A lumpy ellipsoid (bush, tree canopy) with a flattened underside: lat-long rings with noise.</summary>
        public void Blob(Vector3 centre, Vector3 radii, float grey, byte pattern, int seed, int rings = 7, int segs = 10)
        {
            var col = Col(grey, pattern);
            var rng = new System.Random(seed);
            int b = _v.Count;
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings; // 0 = top
                for (int s = 0; s <= segs; s++)
                {
                    float th = 2f * Mathf.PI * s / segs;
                    var dir = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                    float bump = (r == 0 || r == rings || s == segs) ? 1f : 0.82f + 0.3f * (float)rng.NextDouble();
                    if (s == segs) bump = 1f;
                    var p = Vector3.Scale(dir, radii) * bump;
                    if (p.y < -radii.y * 0.55f) p.y = -radii.y * 0.55f; // flat underside, sits on the ground
                    _v.Add(centre + p);
                    _n.Add(dir);
                    _uv.Add(new Vector2(th * radii.x, phi * radii.y));
                    _c.Add(col);
                }
            }
            // Seam fix: the last column repeats the first one's position.
            for (int r = 0; r <= rings; r++) _v[b + r * (segs + 1) + segs] = _v[b + r * (segs + 1)];
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segs; s++)
                {
                    int a = b + r * (segs + 1) + s, c = a + segs + 1;
                    _t.Add(a); _t.Add(a + 1); _t.Add(c + 1);
                    _t.Add(a); _t.Add(c + 1); _t.Add(c);
                }
        }

        /// <summary>A gable roof (triangular prism) over a box footprint along X.</summary>
        public void GableRoof(Vector3 min, Vector3 max, float ridge, float grey)
        {
            var col = Col(grey, Plain);
            float midZ = (min.z + max.z) * 0.5f;
            var a0 = new Vector3(min.x, max.y, min.z); var a1 = new Vector3(max.x, max.y, min.z);
            var r0 = new Vector3(min.x, max.y + ridge, midZ); var r1 = new Vector3(max.x, max.y + ridge, midZ);
            var b0 = new Vector3(min.x, max.y, max.z); var b1 = new Vector3(max.x, max.y, max.z);
            Quad(a0, r0 - a0, a1 - a0, col);             // front slope (faces -z/up)
            Quad(b0, b1 - b0, r0 - b0, col);             // back slope
            Tri(a0, b0, r0, col); Tri(a1, r1, b1, col);  // gable ends
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color32 col)
        {
            var n = Vector3.Cross(b - a, c - a).normalized;
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            _uv.Add(new Vector2(a.x, a.y)); _uv.Add(new Vector2(b.x, b.y)); _uv.Add(new Vector2(c.x, c.y));
            for (int k = 0; k < 3; k++) { _n.Add(n); _c.Add(col); }
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        // ── shaped parts (Brett 2026-10-05: "make the police cars / mob cars and everything look a little better"; the vehicles
        //    were slabs). Each face is told which way is out, so a part built for one side mirrors to the other unchanged. ──

        /// <summary>
        /// A quad from four corners in order round its edge (a → b → c → d: any flat or nearly flat four-sided face, not only a
        /// parallelogram), facing <paramref name="outward"/>'s side; flat-shaded, uv in metres along a→b and a→d. A face of no
        /// area (two corners on one spot of a profile) is left out: no triangles, no zero normal for the shader to normalise.
        /// </summary>
        public void Quad4(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 col, Vector3 outward)
        {
            var n = Vector3.Cross(c - a, d - b);   // twice the quad's area vector for the winding a, b, c, d
            if (n.sqrMagnitude < 1e-12f) return;
            if (Vector3.Dot(n, outward) < 0f) { (b, d) = (d, b); n = -n; }
            n = n.normalized;
            int i = _v.Count;
            float lu = (b - a).magnitude, lw = (d - a).magnitude;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            _uv.Add(Vector2.zero); _uv.Add(new Vector2(lu, 0f)); _uv.Add(new Vector2(lu, lw)); _uv.Add(new Vector2(0f, lw));
            for (int k = 0; k < 4; k++) { _n.Add(n); _c.Add(col); }
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        /// <summary>
        /// A convex polygon (the first <paramref name="count"/> points of <paramref name="ring"/>, in order round its edge) as a
        /// fan from its first point, facing <paramref name="outward"/>'s side: count − 2 triangles, none of no area.
        /// </summary>
        public void Fan(Vector3[] ring, int count, Color32 col, Vector3 outward)
        {
            for (int i = 1; i + 1 < count; i++)
            {
                Vector3 a = ring[0], b = ring[i], c = ring[i + 1];
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-12f) continue;
                if (Vector3.Dot(n, outward) < 0f) (b, c) = (c, b);
                Tri(a, b, c, col);
            }
        }

        static readonly Vector3[] s_foot = new Vector3[8], s_inset = new Vector3[8], s_cap = new Vector3[8];

        /// <summary>
        /// A box with its four upright edges cut back by <paramref name="corner"/> (an eight-sided footprint; 0: square) and its
        /// top edge — and the bottom's too, with <paramref name="bottom"/> — bevelled by <paramref name="edge"/>: bumpers that wrap
        /// their corners, a roof, a light bar's housing, a hatch. <paramref name="top"/> colours the top and its bevel,
        /// <paramref name="side"/> the upright faces and the underside. 38 triangles (60 with the bottom; square: 18 / 28).
        /// </summary>
        public void BevelBox(Vector3 min, Vector3 max, float corner, float edge, Color32 top, Color32 side, bool bottom = false)
        {
            var s = max - min;
            float flat = Mathf.Min(s.x, s.z) * 0.45f;
            edge = Mathf.Clamp(edge, 0f, Mathf.Min(flat, s.y * (bottom ? 0.45f : 0.9f)));
            corner = Mathf.Clamp(corner, 0f, flat);
            int n = corner > 0f ? 8 : 4;
            Footprint(s_foot, n, min.x, max.x, min.z, max.z, corner);
            // The inset outline the bevels run to: every edge moved in by `edge` (a 45° corner cut shrinks by edge·(2 − √2)).
            Footprint(s_inset, n, min.x + edge, max.x - edge, min.z + edge, max.z - edge, Mathf.Max(0f, corner - edge * 0.586f));
            var mid = new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
            float yLo = bottom ? min.y + edge : min.y, yHi = max.y - edge;
            for (int i = 0; i < n; i++)
            {
                var f0 = s_foot[i]; var f1 = s_foot[(i + 1) % n];
                var i0 = s_inset[i]; var i1 = s_inset[(i + 1) % n];
                var outward = (f0 + f1) * 0.5f - mid;
                Quad4(At(f0, yLo), At(f1, yLo), At(f1, yHi), At(f0, yHi), side, outward);                           // upright face
                if (edge > 0f) Quad4(At(f0, yHi), At(f1, yHi), At(i1, max.y), At(i0, max.y), top, outward + Vector3.up * outward.magnitude);
                if (bottom && edge > 0f) Quad4(At(i0, min.y), At(i1, min.y), At(f1, yLo), At(f0, yLo), side, outward - Vector3.up * outward.magnitude);
            }
            var capRing = edge > 0f ? s_inset : s_foot;
            for (int i = 0; i < n; i++) s_cap[i] = At(capRing[i], max.y);
            Fan(s_cap, n, top, Vector3.up);
            if (!bottom) return;
            for (int i = 0; i < n; i++) s_cap[i] = At(capRing[i], min.y);
            Fan(s_cap, n, side, Vector3.down);
        }

        static Vector3 At(Vector3 xz, float y) => new(xz.x, y, xz.z);

        /// <summary>
        /// A rectangle's outline in xz, in order round it: its 4 corners (<paramref name="n"/> 4), or 8 points with the corners
        /// cut back by <paramref name="c"/> (a cut of 0 leaves pairs on the corners: the faces between them have no area).
        /// </summary>
        static void Footprint(Vector3[] p, int n, float x0, float x1, float z0, float z1, float c)
        {
            if (n == 4)
            {
                p[0] = new Vector3(x0, 0f, z0); p[1] = new Vector3(x1, 0f, z0); p[2] = new Vector3(x1, 0f, z1); p[3] = new Vector3(x0, 0f, z1);
                return;
            }
            p[0] = new Vector3(x0 + c, 0f, z0); p[1] = new Vector3(x1 - c, 0f, z0); p[2] = new Vector3(x1, 0f, z0 + c); p[3] = new Vector3(x1, 0f, z1 - c);
            p[4] = new Vector3(x1 - c, 0f, z1); p[5] = new Vector3(x0 + c, 0f, z1); p[6] = new Vector3(x0, 0f, z1 - c); p[7] = new Vector3(x0, 0f, z0 + c);
        }

        /// <summary>A glow sprite facing <paramref name="toward"/> (uv 0..1 across; HIS/SecurityPorch _Glow mode).</summary>
        public void Billboard(Vector3 centre, float radius, Vector3 toward, Color col)
        {
            var f = (toward - centre).normalized;
            var right = Vector3.Cross(Vector3.up, f).normalized; if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            var up = Vector3.Cross(f, right).normalized;
            var o = centre - (right + up) * radius;   // Cross(right, up) = f: the sprite faces the viewer
            var c32 = (Color32)col;
            int b = _v.Count;
            Quad(o, right * 2f * radius, up * 2f * radius, c32);
            if (Vector3.Dot(_n[b], f) < 0f) FlipLastQuad();
            _uv[b] = new Vector2(0, 0); _uv[b + 1] = new Vector2(1, 0); _uv[b + 2] = new Vector2(1, 1); _uv[b + 3] = new Vector2(0, 1);
        }

        /// <summary>A glow quad with uv 0..1 across (HIS/SecurityPorch _Glow mode: a soft ellipse filling it), facing Cross(u, w).</summary>
        public void GlowQuad(Vector3 o, Vector3 u, Vector3 w, Color col)
        {
            int b = _v.Count;
            Quad(o, u, w, (Color32)col);
            _uv[b] = new Vector2(0, 0); _uv[b + 1] = new Vector2(1, 0); _uv[b + 2] = new Vector2(1, 1); _uv[b + 3] = new Vector2(0, 1);
        }

        /// <summary>Empties the kit for reuse; its lists keep their capacity, so a rebuild per hit makes no garbage.</summary>
        public void Clear() { _v.Clear(); _n.Clear(); _uv.Clear(); _c.Clear(); _t.Clear(); }

        /// <summary>
        /// Writes the kit into an existing mesh, rebuilt in place and kept readable (the police cars' bullet marks: one mesh
        /// per thing they stick to, redone on each hit instead of a new object per mark).
        /// </summary>
        public void Apply(Mesh m)
        {
            m.Clear();
            if (_v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(_v); m.SetNormals(_n); m.SetUVs(0, _uv); m.SetColors(_c); m.SetTriangles(_t, 0);
            m.RecalculateBounds();
        }

        /// <param name="tangents">For URP-lit materials (HIS/OcclusionLitPBR normalises the tangent: a zero one is NaN).</param>
        public Mesh ToMesh(string name, bool tangents = false)
        {
            var m = new Mesh { name = name };
            if (_v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(_v); m.SetNormals(_n); m.SetUVs(0, _uv); m.SetColors(_c); m.SetTriangles(_t, 0);
            if (tangents) m.RecalculateTangents();
            m.RecalculateBounds();
            m.UploadMeshData(true);
            return m;
        }
    }
}
