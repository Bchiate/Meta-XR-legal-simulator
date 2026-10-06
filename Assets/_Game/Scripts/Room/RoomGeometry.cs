using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// A flat (top-down, world XZ) copy of the scanned room for cheap safety queries (M1a safety): wall segments, furniture
    /// footprints as oriented boxes, and the floor outline. Built once per room by <see cref="RoomScanner"/>; pure math
    /// afterwards (no physics, no native MRUK calls), so the proximity guard can sample it several times a second and the
    /// tests can build a fake room by hand.
    /// </summary>
    public sealed class RoomGeometry
    {
        public struct Segment { public Vector2 A, B; public bool Invisible; }
        /// <summary>Footprint: centre, unit axes, half extents (m); Bottom/Top are world heights.</summary>
        public struct Box { public Vector2 Center, AxisX, AxisZ, Half; public float Bottom, Top; public string Name; }
        public struct Hit { public float Distance; public Vector2 Point; public string Name; public bool Wall; }

        public readonly List<Segment> Walls = new();
        public readonly List<Box> Boxes = new();
        /// <summary>Floor outline; fewer than 3 points = unknown (everything counts as inside).</summary>
        public readonly List<Vector2> Floor = new();
        public float FloorY;

        /// <summary>Furniture whose underside is higher than this above the floor (wall cabinets, a shelf over the TV) can't be walked into.</summary>
        public const float OverheadClearance = 1.9f;

        public static Vector2 Flat(Vector3 v) => new(v.x, v.z);

        public void AddWall(Vector3 a, Vector3 b, bool invisible = false) =>
            Walls.Add(new Segment { A = Flat(a), B = Flat(b), Invisible = invisible });

        /// <summary>Test/fake rooms: a box standing on the floor, <paramref name="size"/> = footprint (x, z), yawed.</summary>
        public void AddBox(Vector3 center, Vector2 size, float yawDeg, float height, string name)
        {
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            Boxes.Add(new Box
            {
                Center = Flat(center), AxisX = Flat(rot * Vector3.right).normalized, AxisZ = Flat(rot * Vector3.forward).normalized,
                Half = size * 0.5f, Bottom = FloorY, Top = FloorY + height, Name = name,
            });
        }

        /// <summary>From an MRUK room: WALL_FACE / INVISIBLE_WALL_FACE planes become segments, every volume a footprint.</summary>
        public static RoomGeometry FromRoom(MRUKRoom room)
        {
            var g = new RoomGeometry();
            if (room == null) return g;
            var floor = room.FloorAnchors is { Count: > 0 } f ? f[0] : null;
            g.FloorY = floor != null ? floor.transform.position.y : 0f;
            if (floor != null && floor.PlaneBoundary2D is { Count: >= 3 } poly)
                foreach (var p in poly) g.Floor.Add(Flat(floor.transform.TransformPoint(new Vector3(p.x, p.y, 0f))));
            foreach (var a in room.Anchors)
            {
                if (a == null) continue;
                bool wall = a.Label == MRUKAnchor.SceneLabels.WALL_FACE, invisible = a.Label == MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE;
                if ((wall || invisible) && a.PlaneRect.HasValue)
                {
                    var r = a.PlaneRect.Value; // plane anchors: local XY spans the wall, X runs along it
                    g.AddWall(a.transform.TransformPoint(new Vector3(r.xMin, r.center.y, 0f)), a.transform.TransformPoint(new Vector3(r.xMax, r.center.y, 0f)), invisible);
                }
                else if (a.VolumeBounds.HasValue) g.AddVolume(a.transform, a.VolumeBounds.Value, NameOf(a.Label));
            }
            return g;
        }

        void AddVolume(Transform t, Bounds b, string name)
        {
            // Which local axis points up differs by anchor type: take the one closest to world up, the other two span the footprint.
            Vector3[] axes = { t.right, t.up, t.forward };
            int up = 0;
            for (int i = 1; i < 3; i++) if (Mathf.Abs(axes[i].y) > Mathf.Abs(axes[up].y)) up = i;
            int h1 = (up + 1) % 3, h2 = (up + 2) % 3;
            float bottom = float.MaxValue, top = float.MinValue;
            for (int k = 0; k < 8; k++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                float y = t.TransformPoint(c).y;
                bottom = Mathf.Min(bottom, y); top = Mathf.Max(top, y);
            }
            var e = b.extents;
            Vector3 Axis(int i) => i == 0 ? Vector3.right * e.x : i == 1 ? Vector3.up * e.y : Vector3.forward * e.z;
            var ax = Flat(t.TransformVector(Axis(h1))); var az = Flat(t.TransformVector(Axis(h2)));
            if (ax.sqrMagnitude < 1e-8f || az.sqrMagnitude < 1e-8f) return;
            Boxes.Add(new Box
            {
                Center = Flat(t.TransformPoint(b.center)), AxisX = ax.normalized, AxisZ = az.normalized,
                Half = new Vector2(ax.magnitude, az.magnitude), Bottom = bottom, Top = top, Name = name,
            });
        }

        static string NameOf(MRUKAnchor.SceneLabels l) =>
            l == MRUKAnchor.SceneLabels.COUCH ? "Couch" : l == MRUKAnchor.SceneLabels.TABLE ? "Table" : l == MRUKAnchor.SceneLabels.BED ? "Bed" :
            l == MRUKAnchor.SceneLabels.STORAGE ? "Cabinet" : l == MRUKAnchor.SceneLabels.SCREEN ? "TV" : l == MRUKAnchor.SceneLabels.LAMP ? "Lamp" :
            l == MRUKAnchor.SceneLabels.PLANT ? "Plant" : "Furniture";

        // ───────────────────────────── queries ─────────────────────────────

        /// <summary>Is this furniture something you can walk into (not an overhead cabinet)?</summary>
        public bool AtBodyHeight(in Box b) => b.Bottom - FloorY < OverheadClearance;

        /// <summary>Nearest wall or body-height furniture to <paramref name="p"/>, flat. Infinity when the room is empty.</summary>
        public float Nearest(Vector3 p, out Hit hit)
        {
            var q = Flat(p);
            hit = new Hit { Distance = float.PositiveInfinity };
            foreach (var w in Walls)
            {
                var c = ClosestOnSegment(q, w.A, w.B);
                float d = Vector2.Distance(q, c);
                if (d < hit.Distance) hit = new Hit { Distance = d, Point = c, Name = w.Invisible ? "Edge of your scan" : "Wall", Wall = true };
            }
            foreach (var b in Boxes)
            {
                if (!AtBodyHeight(b)) continue;
                var c = ClosestOnBox(q, b);
                float d = Vector2.Distance(q, c);
                if (d < hit.Distance) hit = new Hit { Distance = d, Point = c, Name = b.Name, Wall = false };
            }
            return hit.Distance;
        }

        public float Clearance(Vector2 q) => Nearest(new Vector3(q.x, 0f, q.y), out _);

        public bool Inside(Vector2 p)
        {
            if (Floor.Count < 3) return true;
            bool inside = false;
            for (int i = 0, j = Floor.Count - 1; i < Floor.Count; j = i++)
            {
                var a = Floor[i]; var b = Floor[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Line of sight a→b cut by a wall or by furniture taller than <paramref name="tallerThan"/> above the floor (a table
        /// doesn't block your view of the door; a wardrobe does).
        /// </summary>
        public bool Blocked(Vector2 a, Vector2 b, float tallerThan)
        {
            foreach (var w in Walls) if (SegmentsCross(a, b, w.A, w.B)) return true;
            foreach (var x in Boxes)
            {
                if (x.Top - FloorY < tallerThan || !AtBodyHeight(x)) continue;
                if (SegmentHitsBox(a, b, x)) return true;
            }
            return false;
        }

        /// <summary>
        /// Does a→b cross a wall (walls only, no furniture)? The outline's walls are whole segments: unlike the Room-layer wall
        /// colliders, they have no hole where a door frame sits (VirtualDoor's hinge check, 0.1.55: another door's wall hole
        /// read as clear space beside the front door). <paramref name="skip"/>: the index of a wall in <see cref="Walls"/> to leave out.
        /// </summary>
        public bool CrossesWall(Vector2 a, Vector2 b, int skip = -1)
        {
            for (int i = 0; i < Walls.Count; i++) if (i != skip && SegmentsCross(a, b, Walls[i].A, Walls[i].B)) return true;
            return false;
        }

        /// <summary>Where the ray <paramref name="o"/> + t·<paramref name="dir"/> (dir a unit vector) meets the segment a–b: t ≥ 0 (m), or false (parallel, or it misses).</summary>
        public static bool RaySegment(Vector2 o, Vector2 dir, Vector2 a, Vector2 b, out float t)
        {
            t = 0f;
            var s = b - a;
            float den = Cross(dir, s);
            if (Mathf.Abs(den) < 1e-9f) return false;
            var ao = a - o;
            t = Cross(ao, s) / den;
            float u = Cross(ao, dir) / den;
            return t >= 0f && u >= 0f && u <= 1f;
        }

        /// <summary>The XZ bounds of the floor outline, or of the walls when there's no outline.</summary>
        public Rect Extent()
        {
            var pts = new List<Vector2>(Floor);
            if (pts.Count < 3) foreach (var w in Walls) { pts.Add(w.A); pts.Add(w.B); }
            if (pts.Count == 0) return default;
            Vector2 min = pts[0], max = pts[0];
            foreach (var p in pts) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>Door on the floor and the flat direction into the room (the frame normal, flipped toward the room centre).</summary>
        public static void DoorOnFloor(Vector3 doorPos, Vector3 doorForward, Vector3 roomCenter, out Vector2 door, out Vector2 inward)
        {
            door = Flat(doorPos);
            var toRoom = Flat(roomCenter) - door;
            var n = Flat(doorForward);
            inward = n.sqrMagnitude > 1e-6f ? (Vector2.Dot(n, toRoom) >= 0f ? n : -n) : toRoom;
            inward = inward.sqrMagnitude > 1e-8f ? inward.normalized : Vector2.up;
        }

        // ───────────────────────────── 2D helpers ─────────────────────────────

        public static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-10f) return a;
            return a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        }

        public static Vector2 ClosestOnBox(Vector2 p, in Box b)
        {
            var d = p - b.Center;
            float x = Mathf.Clamp(Vector2.Dot(d, b.AxisX), -b.Half.x, b.Half.x);
            float z = Mathf.Clamp(Vector2.Dot(d, b.AxisZ), -b.Half.y, b.Half.y);
            return b.Center + b.AxisX * x + b.AxisZ * z;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool SegmentsCross(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2)
        {
            var r = p2 - p; var s = q2 - q;
            float den = Cross(r, s);
            if (Mathf.Abs(den) < 1e-9f) return false; // parallel: a sight line along a wall doesn't count
            float t = Cross(q - p, s) / den, u = Cross(q - p, r) / den;
            return t > 0f && t < 1f && u >= 0f && u <= 1f;
        }

        static bool SegmentHitsBox(Vector2 a, Vector2 b, in Box x)
        {
            // Slab test in the box's own frame.
            Vector2 la = new(Vector2.Dot(a - x.Center, x.AxisX), Vector2.Dot(a - x.Center, x.AxisZ));
            Vector2 lb = new(Vector2.Dot(b - x.Center, x.AxisX), Vector2.Dot(b - x.Center, x.AxisZ));
            var d = lb - la;
            float t0 = 0f, t1 = 1f;
            for (int i = 0; i < 2; i++)
            {
                float o = la[i], dir = d[i], h = x.Half[i];
                if (Mathf.Abs(dir) < 1e-9f) { if (o < -h || o > h) return false; continue; }
                float ta = (-h - o) / dir, tb = (h - o) / dir;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Mathf.Max(t0, ta); t1 = Mathf.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }
    }
}
