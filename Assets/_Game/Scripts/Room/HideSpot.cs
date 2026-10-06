using System.Collections.Generic;
using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// Ricky's hiding spot on a US Marshal night (research/27 §3 "Place"): where the protectee crouches all night. Picked on
    /// the safe spot's 0.2 m grid, once per confirmed door: <see cref="SafeSpot"/> hands over the room and its own spot when
    /// the door is chosen (<see cref="DoorChosen"/>), and the first <see cref="Pick"/> after that works the list out — so
    /// Scenarios and Sandbox, which never spawn him, do nothing new. Kept as a short ranked list so the spawn can skip a
    /// spot the player is standing on. Pure math on <see cref="RoomGeometry"/>, like <see cref="SafeSpot.Find"/>.
    /// Required: on the floor; clearance ≥ 0.6 m (0.45 fallback); ≥ 1.2 m from the safe spot and ≥ 0.9 m off the line from it
    /// to each door (the ring is off and you stand where you like, so this only makes a crossfire through him less likely —
    /// the squad's spot rules do the rest); ≥ 1.5 m from every door (1.0 fallback: never in a doorway's way, the entry's
    /// link or a leaf's swing).
    /// Score: the distance to the nearer door, + 0.5 × min(clearance, 1.2), + 0.8 when more than 50° off every door's axis
    /// (out of the view through an open doorway), + 0.5 when tall furniture (or a wall) stands between him and the door(s).
    /// A room too small for both passes gets a last resort (0.3 m clear, 0.6 m from the doors, no safe-spot rules): a Ricky
    /// somewhere beats no night.
    /// </summary>
    public static class HideSpot
    {
        public const float MinClearance = 0.6f, FallbackClearance = 0.45f, LastClearance = 0.3f;
        public const float MinDoorDistance = 1.5f, FallbackDoorDistance = 1.0f, LastDoorDistance = 0.6f;
        /// <summary>Kept off the safe spot, and off the line from it to each door (m).</summary>
        public const float SafeSpotKeepOff = 1.2f, LineKeepOff = 0.9f;
        /// <summary>The player standing this close (flat) to a spot at the spawn: the next-best spot (§3).</summary>
        public const float PlayerKeepOff = 0.8f;
        /// <summary>"Out of view through an open doorway": more than this off a door's axis.</summary>
        const float OffAxisDegrees = 50f;
        /// <summary>Furniture taller than this hides him from the door (SafeSpot's sight-line height).</summary>
        const float TallerThan = 1.2f;
        /// <summary>Ranked spots kept, each this far from the ones above it: "next-best" is somewhere else, not the next tile.</summary>
        const int MaxRanked = 8;
        const float MinSpread = 0.5f;

        /// <summary>One grid point that passed the rules, with its score and what went into it (for the log).</summary>
        public struct Candidate
        {
            public Vector2 Point;
            public float Score, NearDoor, Clearance;
            public bool OffAxis;
            public int Blocked;
        }

        static readonly List<Candidate> s_ranked = new(), s_scratch = new();
        static float s_floorY;
        static Vector3 s_lookAt;
        static string s_pass = "none";
        static RoomScanner s_room;
        static Vector3? s_safe;
        static bool s_dirty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_ranked.Clear(); s_scratch.Clear(); s_pass = "none"; s_room = null; s_safe = null; s_dirty = false; } // domain reload off

        /// <summary>
        /// The door was (re)confirmed: the room and the safe spot (null: none) to work from. Nothing is worked out yet (the
        /// next <see cref="Pick"/> does it); the last door's spots are forgotten.
        /// </summary>
        public static void DoorChosen(RoomScanner room, Vector3? safeSpot)
        {
            s_room = room; s_safe = safeSpot; s_dirty = true;
            s_ranked.Clear(); s_pass = "none";
        }

        /// <summary>The ranked spots for the confirmed door(s), best first (after the first <see cref="Pick"/> or a
        /// <see cref="Compute"/>); empty before, or in a room with none.</summary>
        public static IReadOnlyList<Candidate> Ranked => s_ranked;
        /// <summary>Which rule set found them: "strict", "relaxed", "last resort" or "none".</summary>
        public static string Pass => s_pass;

        /// <summary>
        /// Work out the spots for this room and its confirmed door(s) (<see cref="RoomScanner.ActiveDoors"/>) now. Ricky faces
        /// <paramref name="safeSpot"/> from his (the room's floor centre without one). <see cref="Pick"/> calls it after a
        /// door confirm; tests may call it directly.
        /// </summary>
        public static void Compute(RoomScanner room, Vector3? safeSpot)
        {
            s_ranked.Clear(); s_pass = "none"; s_dirty = false;
            var g = room != null ? room.Geometry : null;
            if (g == null || room.PrimaryDoor == null) return;
            var centre = room.Room != null && room.Room.FloorAnchors is { Count: > 0 } f ? f[0].GetAnchorCenter() : room.PrimaryDoor.transform.position + room.PrimaryDoor.transform.forward;
            var doors = new List<Vector2>(2); var inwards = new List<Vector2>(2);
            foreach (var d in room.ActiveDoors)
            {
                if (d == null) continue;
                RoomGeometry.DoorOnFloor(d.transform.position, d.transform.forward, centre, out var p, out var n);
                doors.Add(p); inwards.Add(n);
            }
            Vector2? safe = safeSpot.HasValue ? RoomGeometry.Flat(safeSpot.Value) : (Vector2?)null;
            s_pass = Find(g, doors, inwards, safe, MinClearance, MinDoorDistance, true, s_ranked) ? "strict"
                   : Find(g, doors, inwards, safe, FallbackClearance, FallbackDoorDistance, true, s_ranked) ? "relaxed"
                   : Find(g, doors, inwards, safe, LastClearance, LastDoorDistance, false, s_ranked) ? "last resort" : "none";
            s_floorY = g.FloorY;
            s_lookAt = safeSpot ?? new Vector3(centre.x, g.FloorY, centre.z);
            if (s_ranked.Count == 0) { Debug.LogWarning("[HIS] Hide spot (Ricky): no floor spot in this room — no protectee here"); return; }
            var b = s_ranked[0];
            Debug.Log($"[HIS] Hide spot (Ricky) at ({b.Point.x:F2}, {g.FloorY:F2}, {b.Point.y:F2}): {b.NearDoor:F1} m from the nearer door, " +
                      $"{b.Clearance:F2} m clear, {(b.OffAxis ? "off every door's axis" : "in line with a doorway")}, " +
                      $"{(b.Blocked > 0 ? $"hidden from {b.Blocked} door(s)" : "in sight of the door")}; {s_ranked.Count} ranked ({s_pass})");
        }

        /// <summary>
        /// The ranked spots that pass on this floor plan (<paramref name="ranked"/>, best first, at most <see cref="MaxRanked"/>,
        /// each ≥ <see cref="MinSpread"/> from the ones above it). <paramref name="doors"/>: each door on the floor with its
        /// inward direction. <paramref name="keepOffs"/>: the safe-spot rules (off for the last resort). Tests can call it on
        /// a fake room.
        /// </summary>
        public static bool Find(RoomGeometry g, IReadOnlyList<Vector2> doors, IReadOnlyList<Vector2> inwards, Vector2? safe,
                                float minClear, float minDoor, bool keepOffs, List<Candidate> ranked)
        {
            ranked.Clear();
            if (g == null || doors == null || doors.Count == 0) return false;
            var ext = g.Extent();
            if (ext.width <= 0f || ext.height <= 0f) return false;
            float cosOff = Mathf.Cos(OffAxisDegrees * Mathf.Deg2Rad);
            s_scratch.Clear();
            const float step = 0.2f;
            for (float x = ext.xMin + step * 0.5f; x < ext.xMax; x += step)
            for (float z = ext.yMin + step * 0.5f; z < ext.yMax; z += step)
            {
                var p = new Vector2(x, z);
                if (!g.Inside(p)) continue;
                float near = float.PositiveInfinity;
                bool tooClose = false;
                for (int i = 0; i < doors.Count && !tooClose; i++)
                {
                    float dd = Vector2.Distance(p, doors[i]);
                    tooClose = dd < minDoor;
                    near = Mathf.Min(near, dd);
                }
                if (tooClose) continue;
                if (keepOffs && safe.HasValue)
                {
                    var s = safe.Value;
                    if (Vector2.Distance(p, s) < SafeSpotKeepOff) continue;
                    bool onLine = false;
                    for (int i = 0; i < doors.Count && !onLine; i++)
                        onLine = Vector2.Distance(p, RoomGeometry.ClosestOnSegment(p, s, doors[i])) < LineKeepOff;
                    if (onLine) continue;
                }
                float clear = g.Clearance(p);   // (the costly checks last)
                if (clear < minClear) continue;
                bool offAxis = true; int blocked = 0;
                for (int i = 0; i < doors.Count; i++)
                {
                    var to = p - doors[i];
                    if (Vector2.Dot(to.normalized, inwards[i]) > cosOff) offAxis = false;   // within 50° of its axis: seen through it open
                    if (g.Blocked(p, doors[i] + inwards[i] * 0.3f, TallerThan)) blocked++; // (just inside: not the door's own wall)
                }
                float score = near + 0.5f * Mathf.Min(clear, 1.2f) + (offAxis ? 0.8f : 0f) + 0.5f * blocked / doors.Count;
                s_scratch.Add(new Candidate { Point = p, Score = score, NearDoor = near, Clearance = clear, OffAxis = offAxis, Blocked = blocked });
            }
            s_scratch.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var c in s_scratch)
            {
                if (ranked.Count >= MaxRanked) break;
                bool crowded = false;
                foreach (var r in ranked) if (Vector2.Distance(r.Point, c.Point) < MinSpread) { crowded = true; break; }
                if (!crowded) ranked.Add(c);
            }
            s_scratch.Clear();
            return ranked.Count > 0;
        }

        /// <summary>
        /// Where Ricky goes now: the best ranked spot more than <see cref="PlayerKeepOff"/> from the player (flat), facing the
        /// safe spot. Every spot that close: the one farthest from him (never on top of him). False before <see cref="Compute"/>
        /// found any (no door confirmed yet, or no spot in this room).
        /// </summary>
        public static bool Pick(Vector3 player, out Vector3 spot, out Vector3 facing)
        {
            spot = facing = default;
            // No SafeSpot ever reported a door (a scene without one): the confirmed room as it is, with the safe spot if any.
            if (s_room == null && !s_dirty && s_ranked.Count == 0 && RoomScanner.Instance != null && RoomScanner.Instance.DoorConfirmed)
                DoorChosen(RoomScanner.Instance, SafeSpot.Instance != null ? SafeSpot.Instance.Spot : null);
            if (s_dirty) Compute(s_room, s_safe);   // the first spawn since the door confirm (a few ms of grid math, once)
            if (s_ranked.Count == 0) return false;
            var me = RoomGeometry.Flat(player);
            int pick = -1, far = 0; float farD = -1f;
            for (int i = 0; i < s_ranked.Count; i++)
            {
                float d = Vector2.Distance(s_ranked[i].Point, me);
                if (d >= PlayerKeepOff) { pick = i; break; }
                if (d > farD) { farD = d; far = i; }
            }
            if (pick < 0) pick = far;
            var p = s_ranked[pick].Point;
            spot = new Vector3(p.x, s_floorY, p.y);
            facing = s_lookAt - spot; facing.y = 0f;
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.forward;
            if (pick > 0) Debug.Log($"[HIS] Hide spot (Ricky): ranked #{pick + 1} ({(farD >= 0f && pick == far ? "every spot is near you" : "you're standing on a better one")})");
            return true;
        }
    }
}
