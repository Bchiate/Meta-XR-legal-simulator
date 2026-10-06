using System.Collections.Generic;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// How the vehicles look (Brett 2026-10-05: "maybe we can make the police cars / mob cars and everything look a little
    /// better", then "just make sure it wont be too heavy on the quest"). They were slabs: a box body, a roof on thin invisible
    /// pillars, octagonal wheels. Every vehicle is now built here from its look (<see cref="CarLook"/>, <see cref="TruckLook"/>)
    /// by the same few builders, so a new look entry gets all of it with no code of its own:
    ///   • the body is lofted along its length (<see cref="Loft"/>): chamfered edges, the hood dropping to the nose and the trunk
    ///     lid to the tail, a crease under the belt line with the side above it leaning in, corners rounded in plan, the wheel
    ///     arches cut up into its lower edge over dark liners, a dark skirt along the bottom;
    ///   • the greenhouse gets solid pillars and frames over the glass's edges (<see cref="Pillar"/>: the A and C pillars in the
    ///     roof's colour, the B pillars and window frames black) and a roof with a dark drip rail. The panes are as before (each
    ///     its own part and collider, the same split), only the front doors' windows now lean in with the side glass;
    ///   • 16-sided tyres with a shoulder and a sidewall on spoked rims — silver, black on the SUVs (<see cref="Wheel"/>);
    ///   • shaped head and tail lamps wrapping round the corners (the hitmen's lit faces stay their own part), a grille,
    ///     licence plates, mirrors and handles on the doors, seams, an exhaust;
    ///   • the cruiser: black-and-white, POLICE on the front doors, its unit number on the roof and trunk, a push bar, a light bar
    ///     with a housing and segmented red / blue lenses (its strobe and breaking as before), a spotlight on the driver's A-pillar;
    ///     the SWAT truck: panel lines, POLICE and SWAT, a rear step, a roof hatch; the van: plain white, a sliding-door seam, no
    ///     side windows.
    /// The Quest budget (passthrough caps the GPU; 0.1.47 hit 90 % with two open doorways and three lit police vehicles): no new
    /// renderer, material, texture, light or transparency — all of it is vertex-coloured geometry in the meshes each vehicle
    /// already had (body, doors, the hitmen's lamp part, the lenses), built once with the vehicles and never per frame — but for
    /// the trucks' driver's cab door (one opaque part each, so it can swing open as he climbs out: the SWAT truck 16 renderers,
    /// the van 7; the integrator, 2026-10-05). Under
    /// 3,500 triangles a car (the cruiser with its light bar included) and 4,500 a truck: <see cref="VehicleBudget"/> counts them.
    /// The lettering is geometry too (block glyphs, <see cref="Lettering"/>), not a texture: no atlas, so no second material.
    /// </summary>
    public partial class PoliceCars
    {
        // ───────────────────────────── colours ─────────────────────────────

        const float Black = 0.06f, White = 0.9f, InsideGrey = 0.025f;
        static readonly Color32 DarkTrim = MeshKit.Grey(0.05f), SeamCol = MeshKit.Grey(0.015f), LinerCol = MeshKit.Grey(0.022f),
                                UnderCol = MeshKit.Grey(0.03f), CowlCol = MeshKit.Grey(0.035f), InsideCol = MeshKit.Grey(InsideGrey),
                                InteriorCol = MeshKit.Grey(0.07f), SeatCol = MeshKit.Grey(0.05f), MirrorCol = MeshKit.Grey(0.35f),
                                TyreCol = MeshKit.Grey(0.035f), SidewallCol = MeshKit.Grey(0.055f), Ink = MeshKit.Grey(0.04f),
                                PlateCol = MeshKit.Grey(0.82f), PlateInk = MeshKit.Rgb(0.05f, 0.07f, 0.3f), PlateRed = MeshKit.Rgb(0.55f, 0.06f, 0.06f),
                                HeadLensCol = MeshKit.Grey(0.45f), TailLensCol = MeshKit.Rgb(0.32f, 0.04f, 0.035f),
                                // Lit (emissive): the night feed shows their luma (the red tail ~0.2 × the gain), the headset their colour.
                                // Vertex colours go to the shader as they are and the project renders in linear space, where HIS/SecurityPorch
                                // also warms any emissive over a luma of 0.3 (× (1, 0.82, 0.55), × the gain of 2): a green of 0.1 came out
                                // ~0.16 linear, ~115 in sRGB — the red tails and lenses read salmon, the blue periwinkle (the cars fixer,
                                // 2026-10-05). So the reds and blue keep their other channels down to a percent or two.
                                LitHeadCol = MeshKit.Grey(1f, MeshKit.Emissive), LitTailCol = MeshKit.Rgb(0.9f, 0.015f, 0.01f, MeshKit.Emissive),
                                // The light bar: red and blue lenses, a clear one in the middle. Lit, the headset sees them red and blue; on
                                // the IR feed their luma is ~0.2 (red) and ~0.1 (blue) × the emissive gain of 2 — under the strobe's glow
                                // sprite, which is what blooms there (the old lenses were plain white boxes, and the glow covered them the same).
                                RedUnlit = MeshKit.Rgb(0.3f, 0.035f, 0.03f), BlueUnlit = MeshKit.Rgb(0.035f, 0.06f, 0.3f), ClearUnlit = MeshKit.Grey(0.4f),
                                RedLit = MeshKit.Rgb(1f, 0.012f, 0.008f, MeshKit.Emissive), BlueLit = MeshKit.Rgb(0.02f, 0.08f, 1f, MeshKit.Emissive);

        /// <summary>
        /// A look's paint at <paramref name="grey"/> as a vertex colour (every painted part of a car goes through here).
        /// MERGE (2026-10-05): the main tree's five-wave work gives <see cref="CarLook"/> a tint (the dark red sedan) and
        /// <c>PaintAt</c> — with both in, this is <c>look.PaintAt(grey)</c>, and every upgrade comes in the look's colour.
        /// </summary>
        static Color32 PaintOf(in CarLook look, float grey) => look.PaintAt(grey);   // (the look's tint: the red sedan red, Brett 0.1.57)

        // ───────────────────────────── the lofted body ─────────────────────────────

        /// <summary>
        /// A body lofted along z (car space: x right, y up, z forward) from piecewise-linear (z, value) profiles, each sorted by
        /// z, sliced at every profile point: its lower edge (the wheel arches cut up into it, <see cref="Arch"/>), its top (nose,
        /// hood, cowl, belt line, trunk lid) and its half width (corners rounded in plan). Each slice's cross-section, bottom to
        /// top: a chamfer off the bottom (the skirt), the side to the crease, the side above it leaning in by
        /// <see cref="Tumble"/>, a chamfer onto the top, the sill to <see cref="Sill"/> (where the glass sits), the top across.
        /// </summary>
        struct BodyShape
        {
            public Vector2[] Bottom, Top, Half;
            /// <summary>The crease's height; the sill's inner x (≤ 0: no sill, the top runs straight across).</summary>
            public float Crease, Sill;
            public float BevelLow, BevelTop, Tumble;
            /// <summary>Close the tail end (the truck's cab sits against its box: open there).</summary>
            public bool RearCap;
        }

        /// <summary>A lofted body's colours, slice by slice (each by its middle z).</summary>
        struct BodyPaint
        {
            public Color32 Side, Top, Cap, Skirt, Under, Floor;
            /// <summary>The sides and sills in <see cref="AltZ0"/>…<see cref="AltZ1"/> (the cruiser's white doors); off when Z0 ≥ Z1.</summary>
            public Color32 Alt; public float AltZ0, AltZ1;
            /// <summary>The sides behind a front door: its dark doorway, seen when the door is open.</summary>
            public Color32 Gap; public float GapZ0, GapZ1;
            /// <summary>
            /// The doorway is open instead (2026-10-05, the cars fixer: a flat dark panel flush with the side cut the rider getting out
            /// off at the hip while his forearm and shin floated in front of it): the side's faces there are left out, for a recess
            /// built into the body behind them (<see cref="DoorRecess"/>). <see cref="GapLeftOnly"/>: on the left (−x) side only
            /// (the trucks' cab: only the driver's door opens).
            /// </summary>
            public bool GapOpen, GapLeftOnly;
            /// <summary>The top is the cabin here (seen through the glass): sills in the side's paint, a dark floor between them.</summary>
            public float CabinZ0, CabinZ1;
            /// <summary>The top is the dark cowl under the windshield here.</summary>
            public float CowlZ0, CowlZ1;
        }

        /// <summary>How far the arches clear the tyres (radius − wheel radius).</summary>
        const float ArchGap = 0.06f;

        static readonly List<float> s_loftZ = new();
        static readonly Vector2[] s_secA = new Vector2[6], s_secB = new Vector2[6];
        static readonly Vector3[] s_ring = new Vector3[16];

        /// <summary>
        /// The body itself: ~24 triangles a slice, flat-shaded (a crash's Crumple recomputes the normals: the facets stay) —
        /// a car's ~32 slices come to ~790 triangles (the cruiser's old body, wheels and kit together were 438).
        /// </summary>
        static void Loft(MeshKit k, in BodyShape s, in BodyPaint p)
        {
            var zs = s_loftZ; zs.Clear();
            float zMin = s.Half[0].x, zMax = s.Half[s.Half.Length - 1].x;
            foreach (var q in s.Bottom) zs.Add(q.x);
            foreach (var q in s.Top) zs.Add(q.x);
            foreach (var q in s.Half) zs.Add(q.x);
            void Mark(float z) { if (z > zMin && z < zMax) zs.Add(z); }
            Mark(p.AltZ0); Mark(p.AltZ1); Mark(p.GapZ0); Mark(p.GapZ1); Mark(p.CabinZ0); Mark(p.CabinZ1); Mark(p.CowlZ0); Mark(p.CowlZ1);
            zs.Sort();
            // Slices closer than 6 mm merge (an arch's end on a paint boundary): one of no length is only waste. The ends stay.
            int w = 0;
            for (int i = 0; i < zs.Count; i++)
            {
                float z = Mathf.Clamp(zs[i], zMin, zMax);
                if (w > 0 && z - zs[w - 1] <= 0.006f) { if (z >= zMax - 1e-4f) zs[w - 1] = zMax; continue; }
                zs[w++] = z;
            }
            zs.RemoveRange(w, zs.Count - w);
            int n = Section(s, zs[0], s_secA);
            if (s.RearCap) Cap(k, s_secA, n, zs[0], p.Cap, Vector3.back);
            for (int i = 0; i + 1 < zs.Count; i++)
            {
                float z0 = zs[i], z1 = zs[i + 1], zm = (z0 + z1) * 0.5f;
                Section(s, z1, s_secB);
                bool alt = zm > p.AltZ0 && zm < p.AltZ1, gap = zm > p.GapZ0 && zm < p.GapZ1;
                bool cabin = zm > p.CabinZ0 && zm < p.CabinZ1, cowl = zm > p.CowlZ0 && zm < p.CowlZ1;
                var paint = alt ? p.Alt : p.Side;
                var side = gap ? p.Gap : paint;
                var sill = cowl ? CowlCol : cabin ? paint : p.Top;
                var floor = cowl ? CowlCol : cabin ? p.Floor : p.Top;
                var axis = new Vector3(0f, (s_secA[0].y + s_secB[0].y + s_secA[n - 1].y + s_secB[n - 1].y) * 0.25f, zm);
                for (int sd = -1; sd <= 1; sd += 2)
                    for (int j = 0; j + 1 < n; j++)
                    {
                        if (gap && p.GapOpen && (j == 1 || j == 2) && (sd < 0 || !p.GapLeftOnly)) continue;   // (the open doorway)
                        var col = j == 0 ? p.Skirt : j == 3 ? paint : j == 4 ? sill : side;
                        Vector3 a = P(s_secA[j], sd, z0), b = P(s_secB[j], sd, z1), c = P(s_secB[j + 1], sd, z1), d = P(s_secA[j + 1], sd, z0);
                        k.Quad4(a, b, c, d, col, (a + b + c + d) * 0.25f - axis);
                    }
                k.Quad4(P(s_secA[0], -1, z0), P(s_secA[0], 1, z0), P(s_secB[0], 1, z1), P(s_secB[0], -1, z1), p.Under, Vector3.down);
                k.Quad4(P(s_secA[n - 1], -1, z0), P(s_secA[n - 1], 1, z0), P(s_secB[n - 1], 1, z1), P(s_secB[n - 1], -1, z1), floor, Vector3.up);
                for (int j = 0; j < n; j++) s_secA[j] = s_secB[j];
            }
            Cap(k, s_secA, n, zs[zs.Count - 1], p.Cap, Vector3.forward);
        }

        static Vector3 P(Vector2 v, int side, float z) => new(v.x * side, v.y, z);

        /// <summary>The cars' front doors' openings along the car (z: the rear edge, the hinge line); their doorways' back wall, out from the middle.</summary>
        const float CarDoorRear = -0.04f, CarDoorHinge = 0.98f, CarRecessX = 0.3f;
        static readonly Color32 RecessCol = MeshKit.Grey(0.02f);

        /// <summary>
        /// A doorway set back into a body on <paramref name="s"/>'s side (−1 left): its sill, top, back wall (at
        /// |x| <paramref name="backX"/>) and two jambs, all dark — the opening in the side runs <paramref name="z0"/>–<paramref name="z1"/>,
        /// from <paramref name="y0"/> (at |x| <paramref name="xLow"/>) to <paramref name="y1"/> (at <paramref name="xTop"/>). A foot that
        /// isn't level (the truck's cab door over its wheel arch): <paramref name="foot"/>'s first <paramref name="nFoot"/> (z, y)
        /// points, rear to front, for the sill and the back wall's foot. 10 triangles (+4 a foot segment).
        /// </summary>
        static void DoorRecess(MeshKit k, float s, float xLow, float xTop, float z0, float z1, float y0, float y1, float backX, Vector2[] foot, int nFoot)
        {
            var o = new Vector3(s, 0f, 0f);
            float bx = s * backX, lx = s * xLow, tx = s * xTop;
            if (foot == null || nFoot < 2)
            {
                k.Quad4(new Vector3(lx, y0, z0), new Vector3(lx, y0, z1), new Vector3(bx, y0, z1), new Vector3(bx, y0, z0), RecessCol, Vector3.up);   // sill
                k.Quad4(new Vector3(bx, y0, z0), new Vector3(bx, y0, z1), new Vector3(bx, y1, z1), new Vector3(bx, y1, z0), RecessCol, o);            // back wall
            }
            else
                for (int i = 0; i + 1 < nFoot; i++)
                {
                    Vector2 a = foot[i], b = foot[i + 1];
                    k.Quad4(new Vector3(lx, a.y, a.x), new Vector3(lx, b.y, b.x), new Vector3(bx, b.y, b.x), new Vector3(bx, a.y, a.x), RecessCol, Vector3.up);
                    k.Quad4(new Vector3(bx, a.y, a.x), new Vector3(bx, b.y, b.x), new Vector3(bx, y1, b.x), new Vector3(bx, y1, a.x), RecessCol, o);
                }
            float yRear = foot != null && nFoot >= 2 ? foot[0].y : y0, yFront = foot != null && nFoot >= 2 ? foot[nFoot - 1].y : y0;
            k.Quad4(new Vector3(tx, y1, z0), new Vector3(tx, y1, z1), new Vector3(bx, y1, z1), new Vector3(bx, y1, z0), RecessCol, Vector3.down);      // top
            k.Quad4(new Vector3(lx, yRear, z0), new Vector3(bx, yRear, z0), new Vector3(bx, y1, z0), new Vector3(tx, y1, z0), RecessCol, Vector3.forward);   // rear jamb
            k.Quad4(new Vector3(lx, yFront, z1), new Vector3(bx, yFront, z1), new Vector3(bx, y1, z1), new Vector3(tx, y1, z1), RecessCol, Vector3.back);    // front jamb
        }

        /// <summary>An end of the loft: its cross-section closed (convex, so a fan), facing <paramref name="outward"/>.</summary>
        static void Cap(MeshKit k, Vector2[] sec, int n, float z, Color32 col, Vector3 outward)
        {
            for (int i = 0; i < n; i++) { s_ring[i] = P(sec[i], -1, z); s_ring[2 * n - 1 - i] = P(sec[i], 1, z); }
            k.Fan(s_ring, 2 * n, col, outward);
        }

        /// <summary>The loft's cross-section at <paramref name="z"/> (the right half; the left mirrors it): 6 points, 5 without a sill.</summary>
        static int Section(in BodyShape s, float z, Vector2[] sec)
        {
            float w = Profile(s.Half, z), yB = Profile(s.Bottom, z), yT = Profile(s.Top, z), h = yT - yB;
            float cb = Mathf.Min(s.BevelLow, h * 0.25f), cs = Mathf.Min(s.BevelTop, h * 0.25f);
            float yC = Mathf.Max(yB + cb + 0.01f, Mathf.Min(s.Crease, yT - cs - 0.01f));
            sec[0] = new Vector2(w - cb, yB);
            sec[1] = new Vector2(w, yB + cb);
            sec[2] = new Vector2(w, yC);
            sec[3] = new Vector2(w - s.Tumble, yT - cs);
            sec[4] = new Vector2(w - s.Tumble - cs, yT);
            if (s.Sill <= 0f) return 5;
            sec[5] = new Vector2(Mathf.Min(s.Sill, sec[4].x - 0.005f), yT);
            return 6;
        }

        /// <summary>A piecewise-linear profile's value at <paramref name="z"/> (held past its ends).</summary>
        static float Profile(Vector2[] p, float z)
        {
            if (z <= p[0].x) return p[0].y;
            for (int i = 1; i < p.Length; i++)
                if (z <= p[i].x)
                {
                    Vector2 a = p[i - 1], b = p[i];
                    return b.x - a.x < 1e-5f ? b.y : Mathf.Lerp(a.y, b.y, (z - a.x) / (b.x - a.x));
                }
            return p[p.Length - 1].y;
        }

        /// <summary>
        /// A wheel arch in a lower-edge profile: radius <paramref name="r"/> round (<paramref name="zc"/>, <paramref name="yc"/>)
        /// from where it leaves the body's lower edge <paramref name="bottom"/> to where it comes back, every 30° between.
        /// </summary>
        static void Arch(List<Vector2> profile, float zc, float yc, float r, float bottom)
        {
            float dy = bottom - yc;
            if (dy >= r) return;
            float half = Mathf.Sqrt(r * r - dy * dy);
            profile.Add(new Vector2(zc - half, bottom));
            for (int deg = 150; deg >= 30; deg -= 30)
            {
                float a = deg * Mathf.Deg2Rad, z = zc + Mathf.Cos(a) * r, y = yc + Mathf.Sin(a) * r;
                if (y > bottom + 0.005f && Mathf.Abs(z - zc) < half - 0.005f) profile.Add(new Vector2(z, y));
            }
            profile.Add(new Vector2(zc + half, bottom));
        }

        static Vector2[] Lifted(Vector2[] p, float lift)
        {
            var o = new Vector2[p.Length];
            for (int i = 0; i < p.Length; i++) o[i] = new Vector2(p[i].x, p[i].y + lift);
            return o;
        }

        // ───────────────────────────── cars: the cruiser, the hitmen's sedans and SUVs ─────────────────────────────

        // The car's ends in plan (the half width at the nose and tail and one slice in): the lamps wrap round these corners.
        const float NoseZ = 2.45f, NoseW = 0.87f, NoseZ2 = 2.38f, NoseW2 = 0.915f, TailZ = -2.45f, TailW = 0.885f, TailZ2 = -2.36f, TailW2 = 0.925f;
        const float CarHalf = 0.93f, CarFrontWheel = 1.5f, CarRearWheel = -1.52f, CarWheelX = 0.84f, CarWheelWidth = 0.24f;

        /// <summary>
        /// The car's body shape: 4.9 × 1.86 m as before, on the colliders' boxes (the nose's top 0.86 m, the doors' section and
        /// belt line 0.95, the trunk 0.9): the hood and trunk lid stay within ~1.5 cm of those tops, so a hole there still shows
        /// on the paint (Brett: every hit must land visibly). Now with a cowl under the windshield, the nose rounding down its last
        /// 25 cm (to 0.815 m) and the trunk lid falling to the tail, the arches round the look's wheels. The SUV
        /// (<see cref="CarLook.LongRoof"/>): no trunk — the belt runs back to a near-upright tailgate. Everything above the
        /// wheels rides <see cref="CarLook.Lift"/> higher.
        /// </summary>
        static BodyShape CarShape(in CarLook look)
        {
            float l = look.Lift, r = look.WheelR, bottom = 0.30f + l;
            var low = new List<Vector2>(18) { new(TailZ, bottom) };
            Arch(low, CarRearWheel, r, r + ArchGap, bottom);
            Arch(low, CarFrontWheel, r, r + ArchGap, bottom);
            low.Add(new Vector2(NoseZ, bottom));
            var hood = new[] { new Vector2(1.04f, 0.95f), new(1.12f, 0.875f), new(1.4f, 0.868f), new(1.9f, 0.862f), new(2.2f, 0.858f), new(2.34f, 0.848f), new(NoseZ, 0.815f) };
            var tail = look.LongRoof
                ? new[] { new Vector2(TailZ, 0.905f), new(LongBeltBack, 0.95f) }
                : new[] { new Vector2(TailZ, 0.845f), new(TailZ2, 0.885f), new(-2.2f, 0.902f), new(-1.42f, 0.91f), new(-1.37f, 0.93f), new(-1.15f, 0.95f) };
            var top = new Vector2[tail.Length + hood.Length];
            tail.CopyTo(top, 0); hood.CopyTo(top, tail.Length);
            return new BodyShape
            {
                Bottom = low.ToArray(),
                Top = Lifted(top, l),
                Half = new[] { new Vector2(TailZ, TailW), new(TailZ2, TailW2), new(-2.25f, CarHalf), new(2.3f, CarHalf), new(NoseZ2, NoseW2), new(NoseZ, NoseW) },
                Crease = 0.78f + l, Sill = 0.8f, BevelLow = 0.04f, BevelTop = 0.035f, Tumble = 0.015f, RearCap = true,
            };
        }

        static BodyPaint CarPaint(in CarLook look, Color32 paint, Color32 white)
        {
            bool cruiser = look.Paint < 0f;
            return new BodyPaint
            {
                Side = paint, Top = paint, Cap = paint, Skirt = DarkTrim, Under = UnderCol, Floor = InsideCol,
                // The cruiser's white doors section (front door, rear door; the black-and-white's pattern), as before.
                Alt = cruiser ? white : paint, AltZ0 = cruiser ? -1.12f : 1f, AltZ1 = cruiser ? 1f : 0f,
                Gap = MeshKit.Grey(0.02f), GapZ0 = CarDoorRear, GapZ1 = CarDoorHinge, GapOpen = true,
                CabinZ0 = look.LongRoof ? LongBeltBack : -1.36f, CabinZ1 = 1.04f,
                CowlZ0 = 1.04f, CowlZ1 = 1.12f,
            };
        }

        /// <summary>A cruiser's unit number from its index (Police A 12, Police B 27): on its roof and trunk lid.</summary>
        static string UnitNumber(int vehicle) => (12 + 15 * vehicle).ToString();

        /// <summary>
        /// Everything in a car's body mesh but its doors and glass: the loft, the underbody (seated riders' feet stay out of
        /// sight), the arch liners, the dash and front seats, the greenhouse's pillars and roof, lamps, grille, bumpers, plates,
        /// exhaust, wheels; the police kit (light bar, push bar, spotlight, unit numbers) on the cruiser. <paramref name="unit"/>:
        /// the cruiser's unit number.
        /// </summary>
        static void CarBody(MeshKit k, in CarLook look, string unit)
        {
            bool cruiser = look.Paint < 0f;
            float l = look.Lift, r = look.WheelR;
            var up = new Vector3(0f, l, 0f);
            var paint = cruiser ? MeshKit.Grey(Black) : PaintOf(look, look.Paint);
            var white = MeshKit.Grey(White);
            var roof = cruiser ? white : paint;                    // the roof and the A / C pillars: the cruiser's white top
            var trim = MeshKit.Grey(look.Trim);                     // bumpers (the SUV's light: they outline a black car on the feed)
            var chrome = cruiser || look.LongRoof ? MeshKit.Grey(0.08f) : MeshKit.Grey(0.5f);   // handles, the grille's surround
            var shape = CarShape(look);
            Loft(k, shape, CarPaint(look, paint, white));
            // Each front door's doorway, set back into the body (the loft leaves the side open there): seen when the door is open,
            // the rider's hips and legs show in it as he swings out.
            Section(shape, (CarDoorRear + CarDoorHinge) * 0.5f, s_secA);
            for (int s = -1; s <= 1; s += 2)
                DoorRecess(k, s, s_secA[1].x, s_secA[3].x, CarDoorRear, CarDoorHinge, s_secA[1].y, s_secA[3].y, CarRecessX, null, 0);

            // The underbody between the wheels, down to 4 cm off the road: seated riders' feet (0.1.48) rest below the body's
            // lower edge (a raised body: up to its own). The arches' inboard walls, from that edge up: no seeing into the body
            // past the tyres (below it the car is open underneath, as any car).
            k.Box(new Vector3(-0.7f, 0.04f, -1.18f), new Vector3(0.7f, 0.3f, 1.16f) + up, UnderCol, bottom: true);   // (shadow-dark: it's under the car)
            float archR = r + ArchGap, wallX = CarWheelX - CarWheelWidth * 0.5f - 0.035f, wallY = 0.3f + l;
            foreach (float zc in new[] { CarRearWheel, CarFrontWheel })
                for (int s = -1; s <= 1; s += 2)
                    k.Quad4(new Vector3(s * wallX, wallY, zc - archR), new Vector3(s * wallX, wallY, zc + archR),
                            new Vector3(s * wallX, r + archR, zc + archR), new Vector3(s * wallX, r + archR, zc - archR), LinerCol, new Vector3(s, 0f, 0f));

            // Inside, seen through the glass (0.1.34): the dash under the windshield, the front seat backs and headrests just
            // behind the front riders' hips (SeatZ), a steering wheel at the driver's hands. No rear bench: the back-seat rider sits
            // behind the seat backs. The seat backs stop 0.45 m out from the middle (they ran to 0.64): a back-seat man going out
            // of a front door goes by the outer side of one (PoliceCars.Exit.cs BackSeatOutboard), not through it.
            k.Box(new Vector3(-0.76f, 0.95f, 0.55f) + up, new Vector3(0.76f, 1.04f, 0.9f) + up, InsideGrey * 1.6f, MeshKit.Plain);
            for (int s = -1; s <= 1; s += 2)
            {
                BoxX(k, new Vector3(0.14f, 0.95f, SeatZ - 0.3f) + up, new Vector3(SeatBackOut, 1.26f, SeatZ - 0.18f) + up, s, SeatCol);
                BoxX(k, new Vector3(0.22f, 1.26f, SeatZ - 0.28f) + up, new Vector3(0.38f, 1.36f, SeatZ - 0.2f) + up, s, SeatCol);
            }
            SteeringWheel(k, new Vector3(-0.4f, 1.0f, SeatZ + 0.31f) + up);

            Greenhouse(k, look, roof, cruiser);

            // The face: lamps wrapping the corners in dark bezels, a grille, the bumpers wrapping theirs, plates, an exhaust.
            LampFaces(k, up, -0.002f, DarkTrim, DarkTrim, 0.012f);                                        // bezels
            if (look.Police) LampFaces(k, up, 0f, LitHeadCol, LitTailCol);                               // the police's: lit all call
            else LampFaces(k, up, 0f, HeadLensCol, TailLensCol);                                         // lit faces apart (MakeCar)
            Grille(k, new Vector3(0f, 0.6f, NoseZ) + up, Vector3.left, Vector3.forward, 0.94f, 0.22f,
                   cruiser ? MeshKit.Grey(0.08f) : look.LongRoof ? trim : chrome, MeshKit.Grey(0.02f), MeshKit.Grey(cruiser ? 0.06f : look.LongRoof ? 0.12f : 0.3f), 3);
            k.BevelBox(new Vector3(-0.945f, 0.24f, 2.2f) + up, new Vector3(0.945f, 0.46f, 2.56f) + up, 0.16f, 0.03f, trim, trim, bottom: true);
            k.BevelBox(new Vector3(-0.945f, 0.24f, -2.56f) + up, new Vector3(0.945f, 0.46f, -2.2f) + up, 0.16f, 0.03f, trim, trim, bottom: true);
            Plate(k, new Vector3(0f, 0.355f, 2.56f) + up, Vector3.left, Vector3.forward);
            Plate(k, new Vector3(0f, 0.57f, TailZ) + up, Vector3.right, Vector3.back);
            Tube(k, new Vector3(0.55f, 0.2f, -2.47f) + up, new Vector3(0.55f, 0.2f, -2.6f) + up, 0.032f, 6, MeshKit.Grey(0.28f), MeshKit.Grey(0.02f));

            // The rear doors (part of the body): a handle each and the seam at their back edge, on the side as it leans.
            Section(shape, -1.1f, s_secA);
            for (int s = -1; s <= 1; s += 2)
            {
                BoxX(k, new Vector3(CarHalf - 0.004f, 0.8f, -0.95f) + up, new Vector3(CarHalf + 0.014f, 0.83f, -0.81f) + up, s, chrome);
                SideSeam(k, s_secA, s, -1.1f);
            }

            // Wheels: tyres on spoked rims (silver; black on the SUVs).
            bool blackRims = look.LongRoof;
            var rim = MeshKit.Grey(blackRims ? 0.09f : 0.62f);
            var slot = MeshKit.Grey(blackRims ? 0.03f : 0.2f);
            var hub = MeshKit.Grey(blackRims ? 0.2f : 0.75f);
            foreach (float x in new[] { -CarWheelX, CarWheelX })
                foreach (float z in new[] { CarRearWheel, CarFrontWheel })
                    Wheel(k, new Vector3(x, r, z), r, CarWheelWidth, Mathf.Sign(x), rim, slot, hub);

            if (!look.Police) return;
            // The police kit. The push bar (uprights and bars as before, a dark guard between them).
            var bar = MeshKit.Grey(0.1f);
            foreach (float x in new[] { -0.36f, 0.3f })
                k.Box(new Vector3(x, 0.24f, 2.56f), new Vector3(x + 0.06f, 0.9f, 2.63f), bar, bottom: true);
            k.Box(new Vector3(-0.46f, 0.52f, 2.56f), new Vector3(0.46f, 0.58f, 2.63f), bar, bottom: true);
            k.Box(new Vector3(-0.46f, 0.82f, 2.56f), new Vector3(0.46f, 0.88f, 2.63f), bar, bottom: true);
            k.Quad4(new Vector3(-0.3f, 0.58f, 2.6f), new Vector3(0.3f, 0.58f, 2.6f), new Vector3(0.3f, 0.82f, 2.6f), new Vector3(-0.3f, 0.82f, 2.6f), MeshKit.Grey(0.04f), Vector3.forward);
            // The light bar: a housing, segmented red / blue lenses (unlit here; the lit halves are LensRed / LensBlue), a cap.
            LightBar(k, 1.474f, 1.535f, 1.64f, -0.3f, 0.06f);
            // The spotlight on the driver's A-pillar.
            k.Box(new Vector3(-0.875f, 1.07f, 0.8f), new Vector3(-0.8f, 1.13f, 0.93f), MeshKit.Grey(0.5f));
            RectQuad(k, new Vector3(-0.8375f, 1.1f, 0.93f), Vector3.left, Vector3.up, Vector3.forward, 0.06f, 0.045f, 0.002f, MeshKit.Grey(0.8f));
            k.Box(new Vector3(-0.835f, 0.955f, 0.86f), new Vector3(-0.82f, 1.07f, 0.875f), MeshKit.Grey(0.1f));
            // The unit number: big on the white roof (behind the bar, read from behind), small and white on the black trunk lid.
            if (string.IsNullOrEmpty(unit)) return;
            float wRoof = TextWidth(unit, 0.2f, 0.05f, 0.055f), wTrunk = TextWidth(unit, 0.08f, 0.025f, 0.022f);
            Lettering(k, unit, new Vector3(-wRoof * 0.5f, 1.4815f, -0.81f), Vector3.right, Vector3.forward, Vector3.up, 0.3f, 0.2f, 0.05f, 0.055f, Ink);
            Lettering(k, unit, new Vector3(-wTrunk * 0.5f, 0.907f, -2.14f), Vector3.right, Vector3.forward, Vector3.up, 0.12f, 0.08f, 0.025f, 0.022f, MeshKit.Grey(0.85f));
        }

        /// <summary>
        /// The greenhouse's frame over the glass (GreenhousePanes' corners): A pillars on the windshield's edges and C pillars
        /// on the rear window's (the SUV's D, and a C splitting its long side glass) in <paramref name="roof"/>'s colour, black
        /// B pillars just behind the front doors, a dark strip along each glass's foot, and the roof itself: a bevelled panel
        /// over the glass's top edge with a dark drip rail round it (the SUV's: roof rails on top). The roof no longer floats.
        /// </summary>
        static void Greenhouse(MeshKit k, in CarLook look, Color32 roof, bool cruiser)
        {
            float l = look.Lift;
            bool longRoof = look.LongRoof;
            float belt = 0.95f + l, top = 1.42f + l, back = longRoof ? LongBeltBack : -1.35f, roofBack = longRoof ? LongRoofBack : -0.95f;
            Vector3 b0 = new(-0.8f, belt, back), b1 = new(0.8f, belt, back), b2 = new(0.8f, belt, 1.05f), b3 = new(-0.8f, belt, 1.05f);
            Vector3 t0 = new(-0.72f, top, roofBack), t1 = new(0.72f, top, roofBack), t2 = new(0.72f, top, 0.42f), t3 = new(-0.72f, top, 0.42f);
            var nWs = PlaneNormal(b3, b2, t3, new Vector3(0f, 0.6f, 1f));
            var nRear = PlaneNormal(b0, b1, t0, new Vector3(0f, longRoof ? 0.15f : 0.6f, -1f));
            for (int s = -1; s <= 1; s += 2)
            {
                var nSide = new Vector3(s * 0.47f, 0.08f, 0f).normalized;
                Vector3 bf = s < 0 ? b3 : b2, tf = s < 0 ? t3 : t2, bb = s < 0 ? b0 : b1, tb = s < 0 ? t0 : t1;
                Vector3 bfOther = s < 0 ? b2 : b3, bbOther = s < 0 ? b1 : b0;
                Pillar(k, bf, tf, bfOther, nWs, 0.05f, bb, nSide, 0.045f, 0.015f, roof);                                // A
                Pillar(k, bb, tb, bf, nSide, longRoof ? 0.08f : 0.05f, bbOther, nRear, 0.07f, 0.015f, roof);             // C (the SUV's D)
                Vector3 e0 = OnSideGlass(s, belt, -0.05f, l, 0f), e1 = OnSideGlass(s, top, -0.05f, l, 0f);
                Pillar(k, e0, e1, e0 + Vector3.forward, nSide, 0.08f, e0 + Vector3.back, nSide, 0.08f, 0.018f, DarkTrim);  // B
                if (longRoof)
                {
                    e0 = OnSideGlass(s, belt, -1.42f, l, 0f); e1 = OnSideGlass(s, top, -1.42f, l, 0f);
                    Pillar(k, e0, e1, e0 + Vector3.forward, nSide, 0.055f, e0 + Vector3.back, nSide, 0.055f, 0.015f, roof); // C
                }
            }
            Pillar(k, b3, b2, t3, nWs, 0.03f, b3 + Vector3.forward * 0.2f, Vector3.up, 0.05f, 0.01f, CowlCol);    // the windshield's foot
            Pillar(k, b0, b1, t0, nRear, 0.03f, b0 + Vector3.back * 0.2f, Vector3.up, 0.04f, 0.01f, CowlCol);     // the rear glass's
            // (Closed underneath — a dark headliner: the doorbell, 1.38 m up, sees the roof's underside through the glass; the old
            // roof box had none, and from below the belt line the cabin read as open-topped.)
            k.BevelBox(new Vector3(-0.75f, top - 0.002f, roofBack - 0.035f), new Vector3(0.75f, top + 0.058f, 0.455f), 0.07f, 0.025f, roof, DarkTrim, bottom: true);
            if (!longRoof) return;
            // Roof rails on their feet: light trim that outlines a black SUV on the night feed with no light bar to light it.
            var rail = MeshKit.Grey(look.Trim);
            float rt = top + 0.058f;
            for (int s = -1; s <= 1; s += 2)
            {
                BoxX(k, new Vector3(0.56f, rt + 0.027f, -2.05f), new Vector3(0.62f, rt + 0.055f, 0.25f), s, rail);
                foreach (float z in new[] { -1.98f, 0.12f })
                    BoxX(k, new Vector3(0.57f, rt - 0.003f, z), new Vector3(0.61f, rt + 0.027f, z + 0.1f), s, rail);
            }
        }

        /// <summary>
        /// A point on a car's side glass (the plane from x 0.8 at the belt to 0.72 at the roof), <paramref name="off"/> out along
        /// its normal; <paramref name="s"/> the side (−1 left).
        /// </summary>
        static Vector3 OnSideGlass(float s, float y, float z, float lift, float off)
        {
            float x = 0.8f - 0.08f * (y - 0.95f - lift) / 0.47f;
            return new Vector3(s * x, y, z) + new Vector3(s * 0.47f, 0.08f, 0f).normalized * off;
        }

        /// <summary>The normal of the plane through a, b, c, turned to <paramref name="outward"/>'s side.</summary>
        static Vector3 PlaneNormal(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            var n = Vector3.Cross(b - a, c - a).normalized;
            return Vector3.Dot(n, outward) < 0f ? -n : n;
        }

        /// <summary>
        /// A front door's window, hinge space (the hinge at (±0.935, 0, 0.98)): in the side glass's plane 8 mm out from it (it
        /// leaned out vertical at x ±0.955 before, its top 23 cm outboard of the roof's edge), from its rear edge at car z 0.03 (where
        /// the side glass's own collider stops: GreenhousePanes) to the A pillar, its front edge raked with it. Corners as
        /// AddPane takes them: the bottom edge rear → front (<paramref name="a"/>, <paramref name="b"/>), the top front → rear.
        /// </summary>
        static void DoorGlass(float side, float lift, out Vector3 a, out Vector3 b, out Vector3 cTop, out Vector3 d)
        {
            var hinge = new Vector3(0.935f * side, 0f, 0.98f);
            a = OnSideGlass(side, 0.955f + lift, 0.03f, lift, 0.008f) - hinge;
            b = OnSideGlass(side, 0.955f + lift, 0.93f, lift, 0.008f) - hinge;
            cTop = OnSideGlass(side, 1.4f + lift, 0.46f, lift, 0.008f) - hinge;
            d = OnSideGlass(side, 1.4f + lift, 0.03f, lift, 0.008f) - hinge;
        }

        /// <summary>
        /// A front door's mesh in its hinge's space (built for <paramref name="side"/>: −1 the driver's): the skin with its
        /// edges chamfered (its leaf — DoorBoxes — as before: 5 cm thick, 0.3–0.95 m, car z −0.04…0.98), the dark skirt carried
        /// across its foot, a black belt moulding on top running in to the glass, the inner trim (seen when it opens), the
        /// window's black frame, a mirror, a handle — and on the cruiser POLICE across it.
        /// </summary>
        static void CarDoor(MeshKit k, in CarLook look, float side)
        {
            bool cruiser = look.Paint < 0f;
            float l = look.Lift, s = side;
            var skin = cruiser ? MeshKit.Grey(0.9f) : PaintOf(look, look.Paint);   // the cruiser's white doors; the others' own paint
            var chrome = cruiser || look.LongRoof ? MeshKit.Grey(0.08f) : MeshKit.Grey(0.5f);
            var mirror = cruiser || look.LongRoof ? DarkTrim : skin;
            var hinge = new Vector3(0.935f * s, 0f, 0.98f);
            // Car space, x as the distance out from the middle; into the hinge's space.
            Vector3 Pt(float x, float y, float z) => new Vector3(s * x, y + l, z) - hinge;
            Vector3 O(float x, float y, float z) => new(s * x, y, z);
            Vector3 f0 = Pt(0.985f, 0.325f, -0.015f), f1 = Pt(0.985f, 0.325f, 0.955f), f2 = Pt(0.985f, 0.956f, 0.955f), f3 = Pt(0.985f, 0.956f, -0.015f);
            Vector3 c0 = Pt(0.96f, 0.3f, -0.04f), c1 = Pt(0.96f, 0.3f, 0.98f), c2 = Pt(0.96f, 0.956f, 0.98f), c3 = Pt(0.96f, 0.956f, -0.04f);
            Vector3 i0 = Pt(0.935f, 0.3f, -0.04f), i1 = Pt(0.935f, 0.3f, 0.98f), i2 = Pt(0.935f, 0.956f, 0.98f), i3 = Pt(0.935f, 0.956f, -0.04f);
            k.Quad4(f0, f1, f2, f3, skin, O(1f, 0f, 0f));               // the skin
            k.Quad4(f0, f1, c1, c0, DarkTrim, O(1f, -1f, 0f));          // its foot: the body's skirt carried across
            k.Quad4(f1, f2, c2, c1, skin, O(1f, 0f, 1f));               // front and back edges, chamfered
            k.Quad4(f3, f0, c0, c3, skin, O(1f, 0f, -1f));
            k.Quad4(c0, c1, i1, i0, skin, O(0f, -1f, 0f));
            k.Quad4(c0, c3, i3, i0, skin, O(0f, 0f, -1f));
            k.Quad4(c1, c2, i2, i1, skin, O(0f, 0f, 1f));
            // Inside (when it's open): the trim panel and the shelf under the window, in to the glass.
            k.Quad4(i0, i1, Pt(0.935f, 0.93f, 0.98f), Pt(0.935f, 0.93f, -0.04f), InteriorCol, O(-1f, 0f, 0f));
            k.Quad4(Pt(0.935f, 0.93f, -0.04f), Pt(0.935f, 0.93f, 0.98f), Pt(0.8f, 0.93f, 0.98f), Pt(0.8f, 0.93f, -0.04f), InteriorCol, O(0f, -1f, 0f));
            k.Quad4(Pt(0.8f, 0.93f, -0.04f), Pt(0.8f, 0.93f, 0.98f), Pt(0.8f, 0.956f, 0.98f), Pt(0.8f, 0.956f, -0.04f), InteriorCol, O(-1f, 0f, 0f));
            // The belt moulding: the door's top, 6 mm over the body's sill (no z-fighting), out to the skin's chamfers.
            s_ring[0] = f3; s_ring[1] = f2; s_ring[2] = c2; s_ring[3] = Pt(0.8f, 0.956f, 0.98f); s_ring[4] = Pt(0.8f, 0.956f, -0.04f); s_ring[5] = c3;
            k.Fan(s_ring, 6, DarkTrim, Vector3.up);
            // The window's frame: black strips over the glass's rear, top and raked front edges (the glass is its own pane).
            DoorGlass(s, l, out var ga, out var gb, out var gc, out var gd);
            var nGlass = O(0.47f, 0.08f, 0f).normalized;
            var centre = (ga + gb + gc + gd) * 0.25f;
            FrameEdge(k, ga, gd, centre, nGlass);
            FrameEdge(k, gd, gc, centre, nGlass);
            FrameEdge(k, gc, gb, centre, nGlass);
            // A mirror on the window's front corner, a handle.
            BoxS(k, Pt(1.14f, 0.985f, 0.8f), Pt(1.02f, 1.075f, 0.9f), mirror);
            k.Quad4(Pt(1.13f, 0.995f, 0.798f), Pt(1.03f, 0.995f, 0.798f), Pt(1.03f, 1.065f, 0.798f), Pt(1.13f, 1.065f, 0.798f), MirrorCol, Vector3.back);
            BoxS(k, Pt(1.02f, 1.0f, 0.835f), Pt(0.984f, 1.03f, 0.865f), mirror);
            BoxS(k, Pt(1.0f, 0.8f, 0.12f), Pt(0.985f, 0.83f, 0.26f), chrome);   // (up at the belt: POLICE goes under it)
            if (!cruiser) return;
            // POLICE, black on the white door, centred on it below the window; read left to right from either side.
            const float h = 0.11f, gw = 0.075f, gap = 0.022f, stroke = 0.022f;
            float w = TextWidth("POLICE", gw, gap, stroke), z0 = 0.47f - s * w * 0.5f;
            Lettering(k, "POLICE", Pt(0.9865f, 0.58f, z0), new Vector3(0f, 0f, s), Vector3.up, O(1f, 0f, 0f), h, gw, gap, stroke, Ink);
        }

        /// <summary>A door window's frame strip on one edge (<paramref name="a"/> → <paramref name="b"/>): 2 cm over the glass, 2 cm past it.</summary>
        static void FrameEdge(MeshKit k, Vector3 a, Vector3 b, Vector3 inside, Vector3 n)
        {
            var mid = (a + b) * 0.5f;
            Pillar(k, a, b, inside, n, 0.02f, mid * 2f - inside, n, 0.022f, 0.006f, DarkTrim);
        }

        // ───────────────────────────── trucks: the SWAT truck, the hitmen's van ─────────────────────────────

        const float CabBack = 1.45f, CabFront = 3.2f, CabHalf = 1.1f, BoxBack = -3.0f, BoxHalf = 1.15f, BoxTop = 2.7f, TruckFloor = 0.45f;
        /// <summary>
        /// The trucks' wheels (car z of the axles, x of the wheels' middles, radius, width). The front axle is 15 cm further forward
        /// since the cab door (2026-10-05: at 2.3 the driver's first foot came down against the back of the tyre).
        /// </summary>
        const float TruckFrontWheel = 2.45f, TruckRearWheel = -2.0f, TruckWheelX = 1.0f, TruckWheelR = 0.45f, TruckWheelWidth = 0.3f;

        /// <summary>
        /// Everything in a truck's body mesh but its rear doors and glass (6.4 × 2.3 m, the colliders' boxes as before): the cab
        /// lofted with its front arch, a flat-faced front with a grille and lamps, the raked windshield framed by pillars and a
        /// cowl, a dark quarter light ahead of each side window (that corner was open before), the roof; the cargo box lofted
        /// with chamfered corners and its rear arch; mirrors on arms, door seams and handles, a bumper, a rear step with its tail
        /// lamps and plate (the tail lamps were behind the closed rear doors), marker lamps over the doors, wheels. The police
        /// kit on the SWAT truck (push bar, light bar, stripes, panel lines, POLICE, a roof hatch); a sliding-door seam on the
        /// van, and no lights lit on it ever.
        /// </summary>
        static void TruckBody(MeshKit k, in TruckLook look)
        {
            var cab = MeshKit.Grey(look.Paint);
            var box = MeshKit.Grey(look.Paint * look.BoxShade);
            var trim = MeshKit.Grey(0.12f);
            var chrome = look.Police ? MeshKit.Grey(0.1f) : MeshKit.Grey(0.45f);
            // The cab: lower body lofted round its front wheel; its top, under the glass, the cab floor (dark).
            var cabLow = new List<Vector2>(9) { new(CabBack, TruckFloor) };
            Arch(cabLow, TruckFrontWheel, TruckWheelR, TruckWheelR + ArchGap, TruckFloor);
            cabLow.Add(new Vector2(CabFront, TruckFloor));
            var cabShape = new BodyShape
            {
                Bottom = cabLow.ToArray(),
                Top = new[] { new Vector2(CabBack, 1.25f), new(3.14f, 1.25f), new(CabFront, 1.205f) },
                Half = new[] { new Vector2(CabBack, CabHalf), new(3.14f, CabHalf), new(CabFront, 1.06f) },
                Crease = 0.99f, Sill = 0f, BevelLow = 0.04f, BevelTop = 0.025f, Tumble = 0f, RearCap = false,
            };
            Loft(k, cabShape, new BodyPaint
            {
                Side = cab, Top = cab, Cap = cab, Skirt = DarkTrim, Under = UnderCol, Floor = InsideCol,
                AltZ0 = 1f, AltZ1 = 0f, CowlZ0 = 1f, CowlZ1 = 0f, CabinZ0 = CabBack - 0.01f, CabinZ1 = 3.14f,
                // The driver's cab doorway, open in the loft's left side (CabDoorway sets it back into the cab).
                GapZ0 = CabDoorRear, GapZ1 = CabDoorHingeZ, GapOpen = true, GapLeftOnly = true,
            });
            // The cargo box: chamfered upright corners and roof edges, its rear arch. Its back face is open behind the back doors
            // (BackDoorway: a shallow dark recess, framed), not capped.
            var boxLow = new List<Vector2>(9) { new(BoxBack, TruckFloor) };
            Arch(boxLow, TruckRearWheel, TruckWheelR, TruckWheelR + ArchGap, TruckFloor);
            boxLow.Add(new Vector2(CabBack, TruckFloor));
            var boxShape = new BodyShape
            {
                Bottom = boxLow.ToArray(),
                Top = new[] { new Vector2(BoxBack, 2.67f), new(-2.97f, BoxTop), new(1.42f, BoxTop), new(CabBack, 2.67f) },
                Half = new[] { new Vector2(BoxBack, 1.115f), new(-2.965f, BoxHalf), new(1.415f, BoxHalf), new(CabBack, 1.115f) },
                Crease = 0.99f, Sill = 0f, BevelLow = 0.04f, BevelTop = 0.05f, Tumble = 0f, RearCap = false,
            };
            Loft(k, boxShape, new BodyPaint
            {
                Side = box, Top = box, Cap = box, Skirt = DarkTrim, Under = UnderCol, Floor = box,
                AltZ0 = 1f, AltZ1 = 0f, GapZ0 = 1f, GapZ1 = 0f, CowlZ0 = 1f, CowlZ1 = 0f, CabinZ0 = 1f, CabinZ1 = 0f,
            });
            BackDoorway(k, boxShape, box);
            // Arch liners' inboard walls.
            float archR = TruckWheelR + ArchGap, wallX = TruckWheelX - TruckWheelWidth * 0.5f - 0.035f;
            foreach (float zc in new[] { TruckRearWheel, TruckFrontWheel })
                for (int s = -1; s <= 1; s += 2)
                    k.Quad4(new Vector3(s * wallX, TruckFloor, zc - archR), new Vector3(s * wallX, TruckFloor, zc + archR),
                            new Vector3(s * wallX, TruckWheelR + archR, zc + archR), new Vector3(s * wallX, TruckWheelR + archR, zc - archR), LinerCol, new Vector3(s, 0f, 0f));

            // Inside the cab (open glass round the driver): its back wall behind him, a dash under the windshield (its top 2 cm
            // under the raked glass where they meet).
            k.Box(new Vector3(-CabHalf, 1.25f, CabBack), new Vector3(CabHalf, 2.1f, 1.52f), InsideGrey, MeshKit.Plain);
            k.Box(new Vector3(-1.02f, 1.25f, 2.7f), new Vector3(1.02f, 1.42f, 3.0f), InsideGrey * 1.6f, MeshKit.Plain);
            // The greenhouse: the windshield (its pane: MakeTruck) framed by pillars and a cowl, the roof, quarter lights.
            Vector3 w0 = new(-1.05f, 1.25f, CabFront), w1 = new(1.05f, 1.25f, CabFront), w2 = new(1f, 2.1f, 2.3f), w3 = new(-1f, 2.1f, 2.3f);
            var nWs = PlaneNormal(w0, w1, w3, new Vector3(0f, 0.7f, 1f));
            for (int s = -1; s <= 1; s += 2)
            {
                var n = new Vector3(s, 0f, 0f);
                Vector3 wb = s < 0 ? w0 : w1, wt = s < 0 ? w3 : w2, wOther = s < 0 ? w1 : w0;
                Pillar(k, wb, wt, wOther, nWs, 0.05f, new Vector3(s * CabHalf, 1.6f, 2.4f), n, 0.06f, 0.015f, cab);           // A
                // The quarter light: the corner between the side window (z 1.52–2.25) and the raked windshield, dark.
                var q0 = new Vector3(s * 1.103f, 1.27f, 2.25f); var q1 = new Vector3(s * 1.103f, 1.25f, 3.17f); var q2 = new Vector3(s * 1.103f, 2.08f, 2.29f);
                s_ring[0] = q0; s_ring[1] = q1; s_ring[2] = q2;
                k.Fan(s_ring, 3, MeshKit.Grey(0.04f), n);
                var d0 = new Vector3(s * CabHalf, 1.26f, 2.25f); var d1 = new Vector3(s * CabHalf, 2.09f, 2.25f);
                Pillar(k, d0, d1, d0 + Vector3.forward, n, 0.022f, d0 + Vector3.back, n, 0.022f, 0.012f, DarkTrim);           // its divider
                var p0 = new Vector3(s * CabHalf, 1.25f, 1.5f); var p1 = new Vector3(s * CabHalf, 2.1f, 1.5f);
                Pillar(k, p0, p1, p0 + Vector3.forward, n, 0.03f, p0 + Vector3.back, n, 0.05f, 0.012f, cab);                 // B
                // Mirrors on arms (on the cab, ahead of the door's hinge line).
                BoxX(k, new Vector3(1.1f, 1.55f, 2.92f), new Vector3(1.3f, 1.58f, 2.96f), s, trim);
                BoxX(k, new Vector3(1.28f, 1.42f, 2.9f), new Vector3(1.36f, 1.8f, 2.98f), s, trim);
                k.Quad4(new Vector3(s * 1.29f, 1.44f, 2.898f), new Vector3(s * 1.35f, 1.44f, 2.898f), new Vector3(s * 1.35f, 1.78f, 2.898f), new Vector3(s * 1.29f, 1.78f, 2.898f), MirrorCol, Vector3.back);
                // The cab doors: the driver's is a part of its own that swings open as he gets out (MakeTruck: TruckCabDoor), its
                // doorway dark behind it; the passenger's (nobody rides there) is drawn on the cab — its seams on the same
                // outline, its handle.
                if (s < 0) CabDoorway(k, s);
                else
                {
                    int nf = CabFootline(0f, 0.03f);
                    SeamAt(k, s, CabHalf, CabDoorRear, CabDoorFoot, CabBelt - 0.03f);
                    SeamAt(k, s, CabHalf, CabDoorHingeZ, s_cabFoot[nf - 1].y, CabBelt - 0.03f);   // (its front edge comes down onto the arch)
                    BoxX(k, new Vector3(CabHalf, 1.1f, 1.64f), new Vector3(CabHalf + 0.018f, 1.13f, 1.76f), s, chrome);
                }
            }
            Pillar(k, w0, w1, w3, nWs, 0.03f, new Vector3(0f, 1.2f, 3.4f), new Vector3(0f, 0.3f, 1f).normalized, 0.04f, 0.01f, CowlCol);   // the windshield's foot
            k.BevelBox(new Vector3(-CabHalf, 2.1f, CabBack), new Vector3(CabHalf, 2.18f, 2.36f), 0.06f, 0.025f, cab, DarkTrim, bottom: true);   // (a headliner too)
            // The face: lamps in bezels, a big grille, the bumper, the plate.
            TruckLamps(k, -0.002f, DarkTrim, DarkTrim, DarkTrim, 0.012f);
            if (look.Police) TruckLamps(k, 0f, LitHeadCol, LitTailCol, LitTailCol);   // the SWAT truck's: lit all call
            else TruckLamps(k, 0f, HeadLensCol, TailLensCol, TailLensCol);            // the van's: never on (research/27)
            Grille(k, new Vector3(0f, 0.86f, CabFront), Vector3.left, Vector3.forward, 1.08f, 0.46f, look.Police ? MeshKit.Grey(0.1f) : chrome, MeshKit.Grey(0.02f), MeshKit.Grey(look.Police ? 0.08f : 0.3f), 4);
            k.BevelBox(new Vector3(-1.12f, 0.28f, 3.05f), new Vector3(1.12f, 0.56f, 3.36f), 0.1f, 0.025f, trim, trim, bottom: true);
            Plate(k, new Vector3(0f, 0.42f, 3.36f), Vector3.left, Vector3.forward);
            // The rear step (its tread grooved), its back face carrying the tail lamps and the plate; dark inside behind the doors.
            k.BevelBox(new Vector3(-BoxHalf, 0.28f, -3.17f), new Vector3(BoxHalf, 0.5f, BoxBack), 0.04f, 0.02f, MeshKit.Grey(0.3f), trim, bottom: true);
            foreach (float z in new[] { -3.14f, -3.1f, -3.06f })
                k.Quad4(new Vector3(-1.1f, 0.5015f, z), new Vector3(1.1f, 0.5015f, z), new Vector3(1.1f, 0.5015f, z + 0.008f), new Vector3(-1.1f, 0.5015f, z + 0.008f), SeamCol, Vector3.up);
            Plate(k, new Vector3(0f, 0.39f, -3.17f), Vector3.right, Vector3.back);
            // Wheels: steel rims (the SWAT truck's dark, the van's grey).
            var rim = MeshKit.Grey(look.Police ? 0.2f : 0.55f);
            var hub = MeshKit.Grey(look.Police ? 0.3f : 0.7f);
            foreach (float x in new[] { -TruckWheelX, TruckWheelX })
                foreach (float z in new[] { TruckRearWheel, TruckFrontWheel })
                    Wheel(k, new Vector3(x, TruckWheelR, z), TruckWheelR, TruckWheelWidth, Mathf.Sign(x), rim, MeshKit.Grey(0.05f), hub);
            // The van's riders sit as the truck's (0.1.50: the driver's root 5 cm and the box riders' 10 cm under the floor): an
            // underbody between the wheels, 6 cm off the road, keeps their feet out of sight under the cab and the box.
            if (look.Underbody) k.Box(new Vector3(-0.83f, 0.06f, -2.95f), new Vector3(0.83f, 0.45f, 3.15f), 0.04f, MeshKit.Plain, bottom: true);

            if (!look.Police)
            {
                // The van: plain, a sliding door on the passenger's side (its seams, its track back along the box, a handle).
                SeamAt(k, 1f, BoxHalf, 0.05f, 0.55f, 2.45f);
                SeamAt(k, 1f, BoxHalf, 1.3f, 0.55f, 2.45f);
                SeamAlong(k, 1f, BoxHalf, 2.45f, 0.05f, 1.3f, FarSeam);
                SeamAlong(k, 1f, BoxHalf, 0.55f, 0.05f, 1.3f, FarSeam);
                SeamAlong(k, 1f, BoxHalf, 1.94f, -0.95f, 0.05f, 0.03f);
                BoxX(k, new Vector3(BoxHalf, 1.33f, 0.12f), new Vector3(BoxHalf + 0.018f, 1.37f, 0.24f), 1f, chrome);
                return;
            }
            // The SWAT truck. Push bar (uprights, bars, a ram plate between them).
            var bar = MeshKit.Grey(0.1f);
            foreach (float x in new[] { -0.72f, 0.62f })
                k.Box(new Vector3(x, 0.3f, 3.36f), new Vector3(x + 0.1f, 1.15f, 3.46f), bar, bottom: true);
            k.Box(new Vector3(-0.8f, 0.95f, 3.36f), new Vector3(0.8f, 1.03f, 3.46f), bar, bottom: true);
            k.Box(new Vector3(-0.8f, 0.58f, 3.36f), new Vector3(0.8f, 0.64f, 3.46f), bar, bottom: true);
            k.Quad4(new Vector3(-0.62f, 0.64f, 3.42f), new Vector3(0.62f, 0.64f, 3.42f), new Vector3(0.62f, 0.95f, 3.42f), new Vector3(-0.62f, 0.95f, 3.42f), MeshKit.Grey(0.04f), Vector3.forward);
            // The light bar on the cab roof, as the cruiser's.
            LightBar(k, 2.175f, 2.215f, 2.318f, 1.76f, 2.14f);
            // The box: light stripes, armour panel lines, POLICE along each side (read left to right from either), a roof hatch.
            var stripe = MeshKit.Grey(0.75f);
            const float h = 0.28f, gw = 0.19f, gap = 0.06f, stroke = 0.055f;
            float w = TextWidth("POLICE", gw, gap, stroke);
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s * (BoxHalf + 0.0025f);   // (over the panel lines' 1.5 mm where they cross it)
                k.Quad4(new Vector3(x, 1.3f, -2.9f), new Vector3(x, 1.3f, 1.4f), new Vector3(x, 1.45f, 1.4f), new Vector3(x, 1.45f, -2.9f), stripe, new Vector3(s, 0f, 0f));
                foreach (float z in new[] { 1.3f, -0.4f, -1.7f })
                    SeamAt(k, s, BoxHalf, z, Mathf.Max(TruckFloor, BoxBottomAt(z)) + 0.04f, 2.62f);
                SeamAlong(k, s, BoxHalf, 2.22f, -2.95f, 1.4f, FarSeam);
                Lettering(k, "POLICE", new Vector3(s * (BoxHalf + 0.002f), 1.6f, 0.48f - s * w * 0.5f), new Vector3(0f, 0f, s), Vector3.up, new Vector3(s, 0f, 0f), h, gw, gap, stroke, MeshKit.Grey(0.8f));
            }
            k.BevelBox(new Vector3(-0.42f, BoxTop - 0.004f, 0.15f), new Vector3(0.42f, BoxTop + 0.05f, 0.85f), 0.05f, 0.015f, MeshKit.Grey(0.1f), MeshKit.Grey(0.07f));
            k.Box(new Vector3(-0.36f, BoxTop - 0.004f, 0.85f), new Vector3(0.36f, BoxTop + 0.032f, 0.885f), MeshKit.Grey(0.05f));
        }

        // ── The driver's cab door (2026-10-05, the cars integrator: the truck's and the van's drivers climbed out through the
        //    cab's solid side for ~0.5 s — there was no door). A cab-over's door: from just behind the B pillar to its hinge at
        //    the front edge, its foot wrapping the front wheel's arch 3 cm clear, up to the belt; above it the window's frame
        //    round the cab's side glass (the glass rides the door, as a car's door window does; the quarter light ahead of it
        //    stays on the cab). Its leaf is 5 cm thick from the hinge line 4 cm inside the cab's side to a skin 1 cm proud of it
        //    (it reads flush; a 1 cm dark seam round it — the doorway behind it, CabDoorway — shows the cut). Open: a dark
        //    doorway below the belt, the cab's dark inside through the window. Built for the driver's side (−x); MakeTruck hangs it.

        /// <summary>The cab door's rear edge, its hinge line (car z), its foot behind the arch, the belt and the window's top (y).</summary>
        const float CabDoorRear = 1.535f, CabDoorHingeZ = 2.88f, CabDoorFoot = 0.5f, CabBelt = 1.25f, CabDoorTop = 2.1f;
        /// <summary>Its leaf's inner face (the hinge line) and skin, out from the middle; the window's front edge (car z).</summary>
        const float CabDoorIn = 1.06f, CabDoorOut = 1.11f, CabWindowFront = 2.25f;
        /// <summary>How far it swings open (degrees).</summary>
        const float CabDoorAngle = 65f;

        static readonly Vector2[] s_cabFoot = new Vector2[12], s_cabRaw = new Vector2[12];

        /// <summary>
        /// The cab door's foot (z, y), rear to front: <see cref="CabDoorFoot"/> high behind the front wheel, then over its arch
        /// (<paramref name="clear"/> m clear of the tyre's arch) to its hinge line — the wheel sits under the door's front, so its
        /// front edge comes down onto the arch (where the arch ends short of the hinge, the foot runs on along the bottom);
        /// <paramref name="grow"/> bigger all round (the doorway behind it). Into <see cref="s_cabFoot"/>; returns the point count.
        /// </summary>
        static int CabFootline(float grow, float clear)
        {
            float y0 = CabDoorFoot - grow, r = TruckWheelR + ArchGap + clear - grow, dy = y0 - TruckWheelR, zEnd = CabDoorHingeZ + grow;
            float half = Mathf.Sqrt(Mathf.Max(0f, r * r - dy * dy));
            int m = 0;
            s_cabRaw[m++] = new Vector2(CabDoorRear - grow, y0);
            s_cabRaw[m++] = new Vector2(TruckFrontWheel - half, y0);
            for (int deg = 165; deg >= 15; deg -= 30)
            {
                float a = deg * Mathf.Deg2Rad;
                s_cabRaw[m++] = new Vector2(TruckFrontWheel + Mathf.Cos(a) * r, Mathf.Max(y0, TruckWheelR + Mathf.Sin(a) * r));
            }
            s_cabRaw[m++] = new Vector2(TruckFrontWheel + half, y0);
            int n = 0;
            for (int i = 0; i < m; i++)
            {
                var q = s_cabRaw[i];
                if (q.x <= zEnd) { s_cabFoot[n++] = q; continue; }
                var p = s_cabRaw[i - 1];   // (the first point is at the door's rear edge: never past the hinge)
                s_cabFoot[n++] = Vector2.Lerp(p, q, (zEnd - p.x) / (q.x - p.x));
                break;
            }
            if (s_cabFoot[n - 1].x < zEnd - 1e-4f) s_cabFoot[n++] = new Vector2(zEnd, y0);
            return n;
        }

        /// <summary>The cab door's hinge (car space) on <paramref name="side"/>'s side: on its leaf's inner face, at its front edge.</summary>
        static Vector3 CabDoorHinge(float side) => new(CabDoorIn * side, 0f, CabDoorHingeZ);

        /// <summary>
        /// The cab's doorway behind its door (<paramref name="s"/>'s side; the loft leaves the side open there): set back into the
        /// cab to a dark wall 0.3 m out from the middle (it was a flat dark panel on the side — the driver getting out was cut at
        /// the belt by it, his hand floating in front, 2026-10-05), its sill following the opening's foot over the front wheel's arch
        /// (the loft's lower edge there, 4 cm up its chamfer), up to the cab side's top. Seen when the door is open.
        /// </summary>
        static void CabDoorway(MeshKit k, float s)
        {
            int n = CabFootline(0.01f, 0.05f);   // (the loft's arch + its 4 cm chamfer: r + 0.04, the foot 0.49)
            DoorRecess(k, s, CabHalf, CabHalf, CabDoorRear, CabDoorHingeZ, s_cabFoot[0].y, CabBelt - 0.025f, CarRecessX, s_cabFoot, n);
        }

        /// <summary>The cargo box's floor behind its back doors (y): the doorway's sill, where the box team stands up before he goes.</summary>
        internal const float BoxDoorwayFloor = 0.55f;
        /// <summary>How deep the back doorway's dark recess is (m in from the back face): the box team stands ready in it (PoliceCars.Exit.cs).</summary>
        internal const float BoxDoorwayDepth = 1.0f;

        /// <summary>
        /// The box's back face (2026-10-05, the cars fixer: a flat dark quad over it — each of the box team came out of a black wall
        /// mid-hop): open behind the back doors (±1.1 m, <see cref="BoxDoorwayFloor"/>–2.6 m), framed by the face's strips round the
        /// opening in <paramref name="paint"/>, with a shallow dark recess behind it — floor, side walls, ceiling and a back wall
        /// <see cref="BoxDoorwayDepth"/> in (the box is otherwise closed: from the doorway nothing past it shows). 28 triangles.
        /// </summary>
        static void BackDoorway(MeshKit k, in BodyShape box, Color32 paint)
        {
            int n = Section(box, BoxBack, s_secA);   // the back face's outline: bottom chamfer, side, crease, top chamfer
            float z = BoxBack, w = s_secA[1].x, yB = s_secA[0].y, yT = s_secA[n - 1].y;
            const float ox = 1.1f, oy0 = BoxDoorwayFloor, oy1 = 2.6f;
            var back = Vector3.back;
            // The frame: below the opening (the bottom chamfer's corners), above it (the top chamfer's), and the thin strips beside it.
            s_ring[0] = new Vector3(-s_secA[0].x, yB, z); s_ring[1] = new Vector3(s_secA[0].x, yB, z); s_ring[2] = new Vector3(w, s_secA[1].y, z);
            s_ring[3] = new Vector3(w, oy0, z); s_ring[4] = new Vector3(-w, oy0, z); s_ring[5] = new Vector3(-w, s_secA[1].y, z);
            k.Fan(s_ring, 6, paint, back);
            s_ring[0] = new Vector3(-w, oy1, z); s_ring[1] = new Vector3(w, oy1, z); s_ring[2] = new Vector3(s_secA[n - 2].x, s_secA[n - 2].y, z);
            s_ring[3] = new Vector3(s_secA[n - 1].x, yT, z); s_ring[4] = new Vector3(-s_secA[n - 1].x, yT, z); s_ring[5] = new Vector3(-s_secA[n - 2].x, s_secA[n - 2].y, z);
            k.Fan(s_ring, 6, paint, back);
            for (int s = -1; s <= 1; s += 2)
                k.Quad4(new Vector3(s * ox, oy0, z), new Vector3(s * w, oy0, z), new Vector3(s * w, oy1, z), new Vector3(s * ox, oy1, z), paint, back);
            // The recess.
            float zi = z + BoxDoorwayDepth;
            k.Quad4(new Vector3(-ox, oy0, z), new Vector3(ox, oy0, z), new Vector3(ox, oy0, zi), new Vector3(-ox, oy0, zi), RecessCol, Vector3.up);        // floor
            k.Quad4(new Vector3(-ox, oy1, z), new Vector3(ox, oy1, z), new Vector3(ox, oy1, zi), new Vector3(-ox, oy1, zi), RecessCol, Vector3.down);      // ceiling
            for (int s = -1; s <= 1; s += 2)
                k.Quad4(new Vector3(s * ox, oy0, z), new Vector3(s * ox, oy0, zi), new Vector3(s * ox, oy1, zi), new Vector3(s * ox, oy1, z), RecessCol, new Vector3(-s, 0f, 0f));
            k.Quad4(new Vector3(-ox, oy0, zi), new Vector3(ox, oy0, zi), new Vector3(ox, oy1, zi), new Vector3(-ox, oy1, zi), RecessCol, back);           // its back wall
        }

        /// <summary>
        /// The cab door's mesh in its hinge's space (<see cref="CabDoorHinge"/>; built for <paramref name="side"/>, −1 the driver's):
        /// the skin over the arch-cut foot up to the belt, its edges and inner trim (seen when it's open), a dark belt moulding,
        /// the window's frame (the glass is its own pane: <see cref="CabDoorGlass"/>), a handle. ~110 triangles.
        /// </summary>
        static void TruckCabDoor(MeshKit k, in TruckLook look, float side)
        {
            float s = side;
            var paint = MeshKit.Grey(look.Paint);
            var chrome = look.Police ? MeshKit.Grey(0.1f) : MeshKit.Grey(0.45f);
            var hinge = CabDoorHinge(s);
            Vector3 Pt(float x, float y, float z) => new Vector3(s * x, y, z) - hinge;
            var outside = new Vector3(s, 0f, 0f);
            int n = CabFootline(0f, 0.03f);
            for (int i = 0; i + 1 < n; i++)
            {
                Vector2 a = s_cabFoot[i], b = s_cabFoot[i + 1];
                k.Quad4(Pt(CabDoorOut, a.y, a.x), Pt(CabDoorOut, b.y, b.x), Pt(CabDoorOut, CabBelt, b.x), Pt(CabDoorOut, CabBelt, a.x), paint, outside);   // the skin
                k.Quad4(Pt(CabDoorIn, a.y, a.x), Pt(CabDoorIn, b.y, b.x), Pt(CabDoorIn, CabBelt, b.x), Pt(CabDoorIn, CabBelt, a.x), InteriorCol, -outside); // the trim inside
                var down = new Vector3(0f, -(b.x - a.x), b.y - a.y);                                                                                       // (off the foot)
                k.Quad4(Pt(CabDoorOut, a.y, a.x), Pt(CabDoorOut, b.y, b.x), Pt(CabDoorIn, b.y, b.x), Pt(CabDoorIn, a.y, a.x), paint, down);
            }
            Vector2 rear = s_cabFoot[0], front = s_cabFoot[n - 1];
            k.Quad4(Pt(CabDoorOut, rear.y, rear.x), Pt(CabDoorOut, CabBelt, rear.x), Pt(CabDoorIn, CabBelt, rear.x), Pt(CabDoorIn, rear.y, rear.x), paint, Vector3.back);
            k.Quad4(Pt(CabDoorOut, front.y, front.x), Pt(CabDoorOut, CabBelt, front.x), Pt(CabDoorIn, CabBelt, front.x), Pt(CabDoorIn, front.y, front.x), paint, Vector3.forward);
            // The belt moulding along its top, a touch proud; the window's frame on it: the rear and front posts, the top rail.
            BoxS(k, Pt(CabDoorIn - 0.005f, CabBelt - 0.015f, CabDoorRear), Pt(CabDoorOut + 0.006f, CabBelt + 0.02f, CabDoorHingeZ), DarkTrim);
            BoxS(k, Pt(CabDoorIn, CabBelt + 0.02f, CabDoorRear), Pt(CabDoorOut, CabDoorTop, CabDoorRear + 0.05f), DarkTrim);
            BoxS(k, Pt(CabDoorIn, CabBelt + 0.02f, CabWindowFront - 0.05f), Pt(CabDoorOut, CabDoorTop, CabWindowFront), DarkTrim);
            BoxS(k, Pt(CabDoorIn, CabDoorTop - 0.045f, CabDoorRear + 0.05f), Pt(CabDoorOut, CabDoorTop, CabWindowFront - 0.05f), DarkTrim);
            BoxS(k, Pt(CabDoorOut, 1.1f, 1.64f), Pt(CabDoorOut + 0.018f, 1.13f, 1.76f), chrome);   // the handle (it was on the cab's side)
        }

        /// <summary>
        /// The cab door's window, hinge space (<see cref="CabDoorHinge"/>): the cab's side glass in the middle of the leaf, inside its
        /// frame. Corners as AddPane takes them: the bottom edge rear → front, the top front → rear.
        /// </summary>
        static void CabDoorGlass(float side, out Vector3 a, out Vector3 b, out Vector3 cTop, out Vector3 d)
        {
            var hinge = CabDoorHinge(side);
            float x = side * (CabDoorIn + CabDoorOut) * 0.5f, y0 = CabBelt + 0.02f, y1 = CabDoorTop - 0.045f, z0 = CabDoorRear + 0.05f, z1 = CabWindowFront - 0.05f;
            a = new Vector3(x, y0, z0) - hinge; b = new Vector3(x, y0, z1) - hinge;
            cTop = new Vector3(x, y1, z1) - hinge; d = new Vector3(x, y1, z0) - hinge;
        }

        /// <summary>The cargo box's lower edge at <paramref name="z"/> (its rear arch), for a seam to start above it.</summary>
        static float BoxBottomAt(float z)
        {
            float r = TruckWheelR + ArchGap, dz = z - TruckRearWheel;
            return Mathf.Abs(dz) >= r ? TruckFloor : TruckWheelR + Mathf.Sqrt(r * r - dz * dz);
        }

        /// <summary>
        /// A truck's rear door leaf in its hinge's space (the hinge at the box's back corner, ±1.12; <paramref name="side"/> −1 the
        /// left): the leaf as before (DoorBoxes: 1.1 × 2.07 m, 5 cm thick), an inset panel seam, its small window, three hinges, a
        /// handle at the meeting edge — on the SWAT truck SW | AT across the pair.
        /// </summary>
        static void TruckDoor(MeshKit k, in TruckLook look, float side)
        {
            float s = side;
            float X(float u) => -s * u;   // u: from the hinge in toward the middle
            var paint = MeshKit.Grey(look.Paint);
            BoxS(k, new Vector3(X(0f), 0.55f, -0.05f), new Vector3(X(1.1f), 2.62f, 0f), paint, true);
            const float z = -0.0515f;
            // The panel seam round the leaf, inset 7 cm (FarSeam wide).
            void Seam(float u0, float y0, float u1, float y1) => k.Quad4(new Vector3(X(u0), y0, z), new Vector3(X(u1), y0, z), new Vector3(X(u1), y1, z), new Vector3(X(u0), y1, z), SeamCol, Vector3.back);
            const float sw = FarSeam;
            Seam(0.07f, 0.62f, 1.03f, 0.62f + sw); Seam(0.07f, 2.55f - sw, 1.03f, 2.55f);
            Seam(0.07f, 0.62f + sw, 0.07f + sw, 2.55f - sw); Seam(1.03f - sw, 0.62f + sw, 1.03f, 2.55f - sw);
            BoxS(k, new Vector3(X(0.25f), 1.75f, -0.06f), new Vector3(X(0.85f), 2.25f, -0.05f), MeshKit.Grey(0.05f));   // its small window
            foreach (float y in new[] { 0.8f, 1.6f, 2.4f })
                BoxS(k, new Vector3(X(0f), y - 0.05f, -0.072f), new Vector3(X(0.07f), y + 0.05f, -0.05f), DarkTrim);
            var metal = look.Police ? MeshKit.Grey(0.12f) : MeshKit.Grey(0.45f);
            BoxS(k, new Vector3(X(0.96f), 1.15f, -0.075f), new Vector3(X(1.02f), 1.45f, -0.05f), metal);   // the handle
            if (!look.Police) return;
            // SW on the left leaf, AT on the right: SWAT across the closed pair, read from behind, under the handles. In the car's
            // frame the left leaf's letters end 12 cm left of the middle and the right's start 12 cm right of it (inside the seams).
            const float h = 0.3f, gw = 0.22f, gap = 0.07f, stroke = 0.06f;
            string t = s < 0f ? "SW" : "AT";
            float w = TextWidth(t, gw, gap, stroke);
            float carX0 = s < 0f ? -0.12f - w : 0.12f, hingeX = 1.12f * s;
            Lettering(k, t, new Vector3(carX0 - hingeX, 0.78f, z - 0.001f), Vector3.right, Vector3.up, Vector3.back, h, gw, gap, stroke, MeshKit.Grey(0.8f));
        }

        // ───────────────────────────── parts ─────────────────────────────

        static readonly Vector3[] s_pillar = new Vector3[5];

        /// <summary>
        /// A pillar or frame strip along a glass edge <paramref name="e0"/>→<paramref name="e1"/>: <paramref name="wA"/> m across one
        /// side (toward <paramref name="pA"/>, over a pane facing <paramref name="nA"/>) and <paramref name="wB"/> across the other
        /// (toward <paramref name="pB"/>: a second pane at a corner — the windshield and a side window — or past the glass's edge),
        /// <paramref name="t"/> proud of the glass and as far behind it, with a ridge on the edge: five faces, 10 triangles.
        /// </summary>
        static void Pillar(MeshKit k, Vector3 e0, Vector3 e1, Vector3 pA, Vector3 nA, float wA, Vector3 pB, Vector3 nB, float wB, float t, Color32 col)
        {
            var d = (e1 - e0).normalized;
            var mid = (e0 + e1) * 0.5f;
            var uA = Vector3.ProjectOnPlane(pA - mid, d).normalized;
            var uB = Vector3.ProjectOnPlane(pB - mid, d).normalized;
            var nC = (nA + nB).normalized;
            s_pillar[0] = uA * wA - nA * t; s_pillar[1] = uA * wA + nA * t; s_pillar[2] = nC * (t * 1.4f);
            s_pillar[3] = uB * wB + nB * t; s_pillar[4] = uB * wB - nB * t;
            var centre = (uA * wA + uB * wB) * 0.5f - nC * (t * 0.5f);
            for (int i = 0; i < 5; i++)
            {
                var p = s_pillar[i]; var q = s_pillar[(i + 1) % 5];
                k.Quad4(e0 + p, e1 + p, e1 + q, e0 + q, col, (p + q) * 0.5f - centre);
            }
        }

        static readonly Vector3[] s_wheelA = new Vector3[16], s_wheelB = new Vector3[16];

        /// <summary>
        /// A wheel along x at <paramref name="c"/> (its outer face toward <paramref name="side"/>): a 16-sided tyre (a flat at
        /// the bottom, sitting on the road) with a rounded shoulder and a sidewall, a dished rim with 8 spokes
        /// (<paramref name="rim"/>, the gaps between them <paramref name="slot"/>), a domed hub cap, the inner face closed. 142
        /// triangles (the old octagonal one: 50).
        /// </summary>
        static void Wheel(MeshKit k, Vector3 c, float r, float width, float side, Color32 rim, Color32 slot, Color32 hub)
        {
            const int n = 16;
            float xo = c.x + side * width * 0.5f, xi = c.x - side * width * 0.5f;
            float xs = xo - side * 0.035f, rs = r - 0.04f, rr = r * 0.64f, rh = r * 0.24f;
            var outward = new Vector3(side, 0f, 0f);
            Vector3 RingPt(float x, float rad, int i)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / n - Mathf.PI * 0.5f;   // (+ half a step: a flat at the bottom)
                return new Vector3(x, c.y + Mathf.Sin(a) * rad, c.z + Mathf.Cos(a) * rad);
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var rad = (RingPt(xi, 1f, i) + RingPt(xi, 1f, j)) * 0.5f - new Vector3(xi, c.y, c.z);   // out from the axis
                k.Quad4(RingPt(xi, r, i), RingPt(xi, r, j), RingPt(xs, r, j), RingPt(xs, r, i), TyreCol, rad);                                    // tread
                k.Quad4(RingPt(xs, r, i), RingPt(xs, r, j), RingPt(xo, rs, j), RingPt(xo, rs, i), TyreCol, rad + outward);                       // shoulder
                k.Quad4(RingPt(xo, rs, i), RingPt(xo, rs, j), RingPt(xo - side * 0.006f, rr, j), RingPt(xo - side * 0.006f, rr, i), SidewallCol, outward);
                k.Quad4(RingPt(xo - side * 0.006f, rr, i), RingPt(xo - side * 0.006f, rr, j), RingPt(xo - side * 0.03f, rh, j), RingPt(xo - side * 0.03f, rh, i),
                        (i & 1) == 0 ? rim : slot, outward);                                                                              // the dished rim
            }
            for (int i = 0; i < 8; i++) { s_wheelA[i] = RingPt(xo - side * 0.03f, rh, i * 2); s_wheelB[i] = RingPt(xi, r * 0.98f, i * 2); }
            var dome = new Vector3(xo - side * 0.012f, c.y, c.z);
            for (int i = 0; i < 8; i++)
            {
                Vector3 a = s_wheelA[i], b = s_wheelA[(i + 1) % 8];
                if (Vector3.Dot(Vector3.Cross(a - dome, b - dome), outward) < 0f) (a, b) = (b, a);
                k.Tri(dome, a, b, hub);                                                                                                   // hub cap
            }
            k.Fan(s_wheelB, 8, TyreCol, -outward);                                                                                        // inner face
        }

        /// <summary>
        /// A car's lamp faces, <paramref name="proud"/> out from 4 mm off the nose and tail (<paramref name="grow"/>: bigger all
        /// round — the dark bezels behind them): swept headlights wrapping round the nose's corners onto its first slice, tail
        /// lamps wrapping round the tail's. The same faces in a lit part over unlit lenses (the hitmen's: dark once parked).
        /// </summary>
        static void LampFaces(MeshKit k, Vector3 up, float proud, Color32 head, Color32 tail, float grow = 0f)
        {
            float o = 0.004f + proud, g = grow;
            for (int s = -1; s <= 1; s += 2)
            {
                float zh = NoseZ + o, zt = TailZ - o;
                // Headlight: lower and narrower at its inner end, sweeping up at the corner.
                k.Quad4(new Vector3(s * (0.53f - g), 0.64f - g, zh) + up, new Vector3(s * 0.866f, 0.62f - g, zh) + up,
                        new Vector3(s * 0.866f, 0.75f + g, zh) + up, new Vector3(s * (0.53f - g), 0.73f + g, zh) + up, head, Vector3.forward);
                Wrap(k, up, s, NoseW, NoseZ, NoseW2, NoseZ2, 0.62f - g, 0.75f + g, 0.635f - g, 0.735f + g, o, g, head, true);
                // Tail lamp.
                k.Quad4(new Vector3(s * (0.52f - g), 0.66f - g, zt) + up, new Vector3(s * 0.865f, 0.66f - g, zt) + up,
                        new Vector3(s * 0.865f, 0.79f + g, zt) + up, new Vector3(s * (0.52f - g), 0.79f + g, zt) + up, tail, Vector3.back);
                Wrap(k, up, s, TailW, TailZ, TailW2, TailZ2, 0.66f - g, 0.775f + g, 0.66f - g, 0.775f + g, o, g, tail, false);
            }
        }

        /// <summary>A lamp's piece round a corner: on the slanted side between (w0, z0) and (w1, z1), heights y at each end.</summary>
        static void Wrap(MeshKit k, Vector3 up, float s, float w0, float z0, float w1, float z1, float yLo0, float yHi0, float yLo1, float yHi1,
                         float off, float grow, Color32 col, bool front)
        {
            var a = new Vector3(s * w0, 0f, z0); var b = new Vector3(s * w1, 0f, z1);
            var along = (b - a).normalized;
            var n = Vector3.Cross(Vector3.up, along).normalized;
            if (Vector3.Dot(n, new Vector3(s, 0f, front ? 1f : -1f)) < 0f) n = -n;
            a += n * off; b += n * off + along * grow;
            k.Quad4(new Vector3(a.x, yLo0, a.z) + up, new Vector3(b.x, yLo1, b.z) + up, new Vector3(b.x, yHi1, b.z) + up, new Vector3(a.x, yHi0, a.z) + up, col, n);
        }

        /// <summary>
        /// A truck's lamp faces, <paramref name="proud"/> out from 4 mm off their faces (<paramref name="grow"/>: the bezels):
        /// headlights on the cab's face, tail lamps on the rear step's back (below the rear doors: they were behind them), three
        /// marker lamps and two corner ones over the doors.
        /// </summary>
        static void TruckLamps(MeshKit k, float proud, Color32 head, Color32 tail, Color32 marker, float grow = 0f)
        {
            float o = 0.004f + proud, g = grow;
            for (int s = -1; s <= 1; s += 2)
            {
                RectQuad(k, new Vector3(s * 0.785f, 0.83f, CabFront), Vector3.left, Vector3.up, Vector3.forward, 0.33f + 2f * g, 0.16f + 2f * g, o, head);
                RectQuad(k, new Vector3(s * 0.85f, 0.39f, -3.17f), Vector3.right, Vector3.up, Vector3.back, 0.3f + 2f * g, 0.1f + 2f * g, o, tail);
                RectQuad(k, new Vector3(s * 1.005f, 2.645f, BoxBack), Vector3.right, Vector3.up, Vector3.back, 0.09f + 2f * g, 0.028f + 2f * g, o, marker);
            }
            for (int i = -1; i <= 1; i++)
                RectQuad(k, new Vector3(i * 0.11f, 2.645f, BoxBack), Vector3.right, Vector3.up, Vector3.back, 0.07f + 2f * g, 0.028f + 2f * g, o, marker);
        }

        /// <summary>
        /// The light bar's housing, its unlit lens segments (three red on the left, three blue on the right, a clear one between)
        /// and a dark cap — the lit halves are parts of their own over them (<see cref="Lens"/>, toggled by the strobe).
        /// <paramref name="y0"/> the housing's foot, <paramref name="y1"/>–<paramref name="y2"/> the lenses, <paramref name="z0"/>–<paramref name="z1"/> their depth.
        /// </summary>
        static void LightBar(MeshKit k, float y0, float y1, float y2, float z0, float z1)
        {
            k.BevelBox(new Vector3(-0.68f, y0, z0 - 0.025f), new Vector3(0.68f, y1, z1 + 0.025f), 0.05f, 0.012f, MeshKit.Grey(0.1f), MeshKit.Grey(0.08f));
            for (int s = -1; s <= 1; s += 2)
                for (int i = 0; i < BarSegments; i++)
                {
                    BarSegment(s, i, out float xa, out float xb);
                    k.Box(new Vector3(xa, y1, z0), new Vector3(xb, y2, z1), s < 0 ? RedUnlit : BlueUnlit);
                }
            k.Box(new Vector3(-0.035f, y1, z0), new Vector3(0.035f, y2, z1), ClearUnlit);
            k.Box(new Vector3(-0.665f, y2, z0 - 0.012f), new Vector3(0.665f, y2 + 0.012f, z1 + 0.012f), MeshKit.Grey(0.05f));
        }

        const int BarSegments = 3;

        /// <summary>Segment <paramref name="i"/> of the light bar's red (<paramref name="s"/> −1) or blue (+1) half: x from <paramref name="xa"/> to <paramref name="xb"/>.</summary>
        static void BarSegment(float s, int i, out float xa, out float xb)
        {
            const float inner = 0.045f, outer = 0.635f, gap = 0.02f;
            float w = (outer - inner - gap * (BarSegments - 1)) / BarSegments;
            float a = inner + i * (w + gap), b = a + w;
            xa = s < 0f ? -b : a; xb = s < 0f ? -a : b;
        }

        /// <summary>A light bar half's lit segments (emissive, a little bigger than the unlit ones they cover): the strobe's part.</summary>
        static void LensSegments(MeshKit k, float s, float y1, float y2, float z0, float z1)
        {
            for (int i = 0; i < BarSegments; i++)
            {
                BarSegment(s, i, out float xa, out float xb);
                k.Box(new Vector3(xa - 0.004f, y1 - 0.005f, z0 - 0.01f), new Vector3(xb + 0.004f, y2 + 0.005f, z1 + 0.01f), s < 0f ? RedLit : BlueLit);
            }
        }

        /// <summary>A grille on a face: a surround, the dark mesh, <paramref name="slats"/> bars across.</summary>
        static void Grille(MeshKit k, Vector3 centre, Vector3 right, Vector3 outward, float w, float h, Color32 frame, Color32 mesh, Color32 slat, int slats)
        {
            RectQuad(k, centre, right, Vector3.up, outward, w, h, 0.002f, frame);
            RectQuad(k, centre, right, Vector3.up, outward, w - 0.04f, h - 0.04f, 0.004f, mesh);
            float step = (h - 0.04f) / (slats + 1);
            for (int i = 1; i <= slats; i++)
                RectQuad(k, centre + Vector3.up * (-(h - 0.04f) * 0.5f + step * i), right, Vector3.up, outward, w - 0.06f, Mathf.Min(0.025f, step * 0.4f), 0.006f, slat);
        }

        /// <summary>A licence plate on a face (30 × 14 cm in a dark surround): white, a red name across its top, six blue characters.</summary>
        static void Plate(MeshKit k, Vector3 centre, Vector3 right, Vector3 outward)
        {
            RectQuad(k, centre, right, Vector3.up, outward, 0.32f, 0.16f, 0.002f, DarkTrim);
            RectQuad(k, centre, right, Vector3.up, outward, 0.3f, 0.14f, 0.004f, PlateCol);
            RectQuad(k, centre + Vector3.up * 0.052f, right, Vector3.up, outward, 0.17f, 0.014f, 0.006f, PlateRed);
            for (int i = 0; i < 6; i++)
                RectQuad(k, centre + right * (-0.1f + i * 0.04f) - Vector3.up * 0.012f, right, Vector3.up, outward, 0.026f, 0.058f, 0.006f, PlateInk);
        }

        /// <summary>A rectangle <paramref name="w"/> × <paramref name="h"/> centred on <paramref name="centre"/>, <paramref name="lift"/> out along <paramref name="outward"/>.</summary>
        static void RectQuad(MeshKit k, Vector3 centre, Vector3 right, Vector3 up, Vector3 outward, float w, float h, float lift, Color32 col)
        {
            var c = centre + outward * lift; Vector3 x = right * (w * 0.5f), y = up * (h * 0.5f);
            k.Quad4(c - x - y, c + x - y, c + x + y, c - x + y, col, outward);
        }

        /// <summary>An exhaust: a <paramref name="sides"/>-sided tube from <paramref name="a"/> to <paramref name="b"/>, dark inside its open end.</summary>
        /// <summary>How far out from the middle the cars' front seat backs reach (m): a back-seat man goes by outside it.</summary>
        const float SeatBackOut = 0.45f;

        /// <summary>
        /// The driver's steering wheel (2026-10-05: the Driving pose's hands held nothing): a flat 10-sided rim round
        /// <paramref name="hub"/> on a column down into the dash, its top leaning away from him. ~56 triangles; its lower part
        /// is under the cabin's belt-line floor, out of sight.
        /// </summary>
        static void SteeringWheel(MeshKit k, Vector3 hub)
        {
            const int n = 10; const float r = 0.18f, rim = 0.028f;
            var normal = new Vector3(0f, 0.42f, -0.9f).normalized;          // toward him (the column's axis)
            var up = Vector3.Cross(normal, Vector3.right).normalized;          // up the wheel, leaning forward
            if (up.y < 0f) up = -up;
            var col = MeshKit.Grey(0.04f);
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                Vector3 d0 = Vector3.right * Mathf.Cos(a0) + up * Mathf.Sin(a0), d1 = Vector3.right * Mathf.Cos(a1) + up * Mathf.Sin(a1);
                Vector3 o0 = hub + d0 * r, o1 = hub + d1 * r, i0 = hub + d0 * (r - rim), i1 = hub + d1 * (r - rim);
                k.Quad4(i0, o0, o1, i1, col, normal);
                k.Quad4(i0, i1, o1, o0, col, -normal);
            }
            Tube(k, hub - normal * 0.005f, hub - normal * 0.17f, 0.028f, 6, col, col);
        }

        static void Tube(MeshKit k, Vector3 a, Vector3 b, float r, int sides, Color32 col, Color32 end)
        {
            var axis = (b - a).normalized;
            var u = Vector3.Cross(axis, Vector3.up).normalized; var v = Vector3.Cross(u, axis);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                var d0 = (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * r; var d1 = (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * r;
                k.Quad4(a + d0, b + d0, b + d1, a + d1, col, d0 + d1);
                s_ring[i] = b + d0;
            }
            k.Fan(s_ring, sides, end, axis);
        }

        /// <summary>A seam on a body's side at <paramref name="z"/> (from a loft section there): up the side, then up its leaning band.</summary>
        static void SideSeam(MeshKit k, Vector2[] sec, float s, float z)
        {
            const float out_ = 0.0015f, w = CarSeam * 0.5f;
            Vector3 P2(Vector2 v, float dx) => new(s * (v.x + dx), v.y, z);
            var n = new Vector3(s, 0f, 0f);
            var lo = P2(sec[1], out_) + Vector3.up * 0.01f; var mid = P2(sec[2], out_); var hi = P2(sec[3], out_) - Vector3.up * 0.01f;
            k.Quad4(lo + Vector3.back * w, lo + Vector3.forward * w, mid + Vector3.forward * w, mid + Vector3.back * w, SeamCol, n);
            k.Quad4(mid + Vector3.back * w, mid + Vector3.forward * w, hi + Vector3.forward * w, hi + Vector3.back * w, SeamCol, n + Vector3.up * 0.1f);
        }

        /// <summary>An upright seam on a flat side (x = ±<paramref name="half"/>) at <paramref name="z"/>, from <paramref name="y0"/> to <paramref name="y1"/>.</summary>
        static void SeamAt(MeshKit k, float s, float half, float z, float y0, float y1)
        {
            float x = s * (half + 0.0015f), w = FarSeam * 0.5f;
            k.Quad4(new Vector3(x, y0, z - w), new Vector3(x, y0, z + w), new Vector3(x, y1, z + w), new Vector3(x, y1, z - w), SeamCol, new Vector3(s, 0f, 0f));
        }

        /// <summary>
        /// The width of the seams seen from afar (the trucks' box, cab and back doors; 2026-10-05: at 1.2 cm they were under a pixel
        /// at 10–25 m and broke into shimmering dashes on the headset), and of the cars' rear-door seam.
        /// </summary>
        const float FarSeam = 0.025f, CarSeam = 0.016f;

        /// <summary>A level seam on a flat side at height <paramref name="y"/>, from <paramref name="z0"/> to <paramref name="z1"/>, <paramref name="h"/> tall.</summary>
        static void SeamAlong(MeshKit k, float s, float half, float y, float z0, float z1, float h)
        {
            float x = s * (half + 0.0015f);
            k.Quad4(new Vector3(x, y - h * 0.5f, z0), new Vector3(x, y - h * 0.5f, z1), new Vector3(x, y + h * 0.5f, z1), new Vector3(x, y + h * 0.5f, z0), SeamCol, new Vector3(s, 0f, 0f));
        }

        /// <summary>A box given for the right (x ≥ 0), mirrored to the left for <paramref name="s"/> −1.</summary>
        static void BoxX(MeshKit k, Vector3 min, Vector3 max, float s, Color32 col, bool bottom = false)
        {
            if (s < 0f) { float x0 = -max.x, x1 = -min.x; min.x = x0; max.x = x1; }
            k.Box(min, max, col, bottom);
        }

        /// <summary>A box between two opposite corners given in any order.</summary>
        static void BoxS(MeshKit k, Vector3 a, Vector3 b, Color32 col, bool bottom = false) => k.Box(Vector3.Min(a, b), Vector3.Max(a, b), col, bottom);

        // ───────────────────────────── lettering ─────────────────────────────

        static readonly List<Vector4> s_glyph = new();

        /// <summary>
        /// Block capitals and digits as rectangles on a face (POLICE, SWAT, unit numbers): from <paramref name="origin"/> (the
        /// first glyph's lower left) along <paramref name="right"/>, <paramref name="up"/> the glyphs' up, facing
        /// <paramref name="outward"/>. Glyphs <paramref name="gw"/> × <paramref name="h"/>, strokes <paramref name="stroke"/>
        /// thick. Geometry, not a texture: no atlas, no second material; POLICE is 18 quads.
        /// </summary>
        static void Lettering(MeshKit k, string text, Vector3 origin, Vector3 right, Vector3 up, Vector3 outward, float h, float gw, float gap, float stroke, Color32 col)
        {
            float x = 0f;
            foreach (char ch in text)
            {
                float adv = Glyph(ch, gw, stroke, s_glyph);
                foreach (var r in s_glyph)   // (x0, y0, x1, y1) in metres along right, up
                {
                    Vector3 p = origin + right * (x + r.x), q = origin + right * (x + r.z);
                    k.Quad4(p + up * r.y, q + up * r.y, q + up * r.w, p + up * r.w, col, outward);
                }
                x += adv + gap;
            }

            float Glyph(char c, float w, float t, List<Vector4> rects)
            {
                rects.Clear();
                float sy = t / h;   // the stroke in the glyph's height
                void R(float x0, float y0, float x1, float y1) => rects.Add(new Vector4(x0 * w, y0 * h, x1 * w, y1 * h));
                float sx = t / w, mid = 0.5f - sy * 0.5f;
                switch (c)
                {
                    case 'P': R(0, 0, sx, 1); R(sx, 1 - sy, 1, 1); R(sx, mid, 1, mid + sy); R(1 - sx, mid + sy, 1, 1 - sy); return w;
                    case 'O': case '0': R(0, 0, sx, 1); R(1 - sx, 0, 1, 1); R(sx, 1 - sy, 1 - sx, 1); R(sx, 0, 1 - sx, sy); return w;
                    case 'L': R(0, 0, sx, 1); R(sx, 0, 1, sy); return w;
                    case 'I': rects.Add(new Vector4(0f, 0f, t, h)); return t;
                    case 'C': R(0, 0, sx, 1); R(sx, 1 - sy, 1, 1); R(sx, 0, 1, sy); return w;
                    case 'E': R(0, 0, sx, 1); R(sx, 1 - sy, 1, 1); R(sx, mid, 0.85f, mid + sy); R(sx, 0, 1, sy); return w;
                    case 'S': case '5': R(0, 1 - sy, 1, 1); R(0, mid + sy, sx, 1 - sy); R(0, mid, 1, mid + sy); R(1 - sx, sy, 1, mid); R(0, 0, 1, sy); return w;
                    case 'W': R(0, sy, sx, 1); R(1 - sx, sy, 1, 1); R(0.5f - sx * 0.5f, sy, 0.5f + sx * 0.5f, 0.6f); R(0, 0, 1, sy); return w;
                    case 'A': R(0, 0, sx, 1 - sy); R(1 - sx, 0, 1, 1 - sy); R(0, 1 - sy, 1, 1); R(sx, mid, 1 - sx, mid + sy); return w;
                    case 'T': R(0, 1 - sy, 1, 1); R(0.5f - sx * 0.5f, 0, 0.5f + sx * 0.5f, 1 - sy); return w;
                    case '1': R(1 - sx, 0, 1, 1); return w;
                    case '2': R(0, 1 - sy, 1, 1); R(1 - sx, mid + sy, 1, 1 - sy); R(0, mid, 1, mid + sy); R(0, sy, sx, mid); R(0, 0, 1, sy); return w;
                    case '3': R(0, 1 - sy, 1, 1); R(1 - sx, sy, 1, 1 - sy); R(0, mid, 1 - sx, mid + sy); R(0, 0, 1, sy); return w;
                    case '4': R(0, mid, sx, 1); R(sx, mid, 1 - sx, mid + sy); R(1 - sx, 0, 1, 1); return w;
                    case '6': R(0, 0, sx, 1); R(sx, 1 - sy, 1, 1); R(sx, mid, 1, mid + sy); R(1 - sx, sy, 1, mid); R(sx, 0, 1, sy); return w;
                    case '7': R(0, 1 - sy, 1, 1); R(1 - sx, 0, 1, 1 - sy); return w;
                    case '8': R(0, 0, sx, 1); R(1 - sx, 0, 1, 1); R(sx, 1 - sy, 1 - sx, 1); R(sx, mid, 1 - sx, mid + sy); R(sx, 0, 1 - sx, sy); return w;
                    case '9': R(0, mid, sx, 1); R(sx, 1 - sy, 1, 1); R(sx, mid, 1 - sx, mid + sy); R(1 - sx, 0, 1, 1 - sy); R(0, 0, 1 - sx, sy); return w;
                    default: return w * 0.6f;   // (a space)
                }
            }
        }

        /// <summary>How long <see cref="Lettering"/> lays <paramref name="text"/> out (I is a stroke wide, the rest <paramref name="gw"/>).</summary>
        static float TextWidth(string text, float gw, float gap, float stroke)
        {
            float w = 0f;
            for (int i = 0; i < text.Length; i++) w += (text[i] == 'I' ? stroke : text[i] == ' ' ? gw * 0.6f : gw) + (i > 0 ? gap : 0f);
            return w;
        }
    }
}
