using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace HIS.Intruder
{
    /// <summary>
    /// The way in and out (PLAN M1a "Entry sequences"): the room NavMesh only covers the scanned floor, so a small runtime
    /// NavMesh "apron" is baked behind the door (in the dark box, where he waits) and a bidirectional <see cref="NavMeshLink"/>
    /// joins it to the room floor straight through the doorway. Entry and exit are then ordinary agent paths (the intruder
    /// crosses the link himself, at his gait speed: <see cref="Intruder"/>.CrossLink) — never a straight line through furniture.
    /// The link's room end is re-picked when the door opens (the open leaf is carved out of the room NavMesh).
    /// </summary>
    public class DoorwayNav : MonoBehaviour
    {
        /// <summary>The front door's way in (index 0). Each door has its own (<see cref="For"/>); an intruder uses his door's.</summary>
        public static DoorwayNav Instance => For(0);
        /// <summary>The way in for door <paramref name="index"/> (VirtualDoor.Index / RoomScanner.ActiveDoors), or null.</summary>
        public static DoorwayNav For(int index) { foreach (var n in s_all) if (n != null && n.Index == index) return n; return null; }
        static readonly List<DoorwayNav> s_all = new();
        /// <summary>Which door this is (0 = the front door).</summary>
        public int Index { get; set; }

        const float ApronWidth = 1.3f;   // walkable 0.7 m once the agent radius (0.3) is eroded off both sides
        const float ApronDepth = 2.0f;   // behind the door plane (walkable from 0.3 m to 1.7 m: he waits at 1.25 m)
        const float LinkOut = 0.4f;      // the link's outside end, behind the door plane
        const float ClearRadius = 0.15f; // the straight crossing must clear walls, furniture and the open leaf by this much
        const float AvoidRadius = 0.7f;  // Reconnect(avoid): the room end this far from the player standing at the door he opened

        NavMeshDataInstance _apron;
        NavMeshLink _link;
        int _agentType = int.MinValue;
        Vector3 _door, _outward;

        public bool Ready => _apron.valid && _link != null && _link.isActiveAndEnabled;
        /// <summary>The link is on: a clear way through the doorway was found (<see cref="Reconnect"/>).</summary>
        public bool LinkOn => Ready && _link.activated;
        /// <summary>The link's ends in world space (outside: on the apron; inside: on the room floor).</summary>
        public Vector3 OutsideEnd { get; private set; }
        public Vector3 InsideEnd { get; private set; }
        /// <summary>The door on the floor and its outward axis (for tests and callers that measure the crossing).</summary>
        public Vector3 DoorPoint => _door;
        public Vector3 Outward => _outward;

        void Awake() => s_all.Add(this);
        void OnDestroy() { s_all.Remove(this); if (_apron.valid) _apron.Remove(); }

        public NavMeshQueryFilter Filter => new() { agentTypeID = _agentType, areaMask = NavMesh.AllAreas };

        /// <summary>
        /// Bake the apron and place the link for this door (no-op if nothing changed but the room NavMesh). <paramref name="agent"/>:
        /// the room NavMesh's agent (MRUK registers its custom agent type with Unity's default dimensions and bakes with its own,
        /// so the dimensions come from its SceneNavigation, not from NavMesh.GetSettingsByID).
        /// </summary>
        public void Build(Vector3 doorFloor, Vector3 outward, int agentTypeId, float radius, float height, float climb, float slope)
        {
            outward.y = 0f; outward.Normalize();
            bool same = _apron.valid && agentTypeId == _agentType && (doorFloor - _door).sqrMagnitude < 1e-4f && Vector3.Dot(outward, _outward) > 0.9999f;
            _door = doorFloor; _outward = outward; _agentType = agentTypeId;
            if (!same)
            {
                if (_apron.valid) _apron.Remove();
                var settings = NavMesh.GetSettingsByID(agentTypeId);
                if (settings.agentTypeID != agentTypeId) { Debug.LogWarning($"[HIS] DoorwayNav: no NavMesh settings for agent type {agentTypeId}"); return; }
                settings.agentRadius = radius; settings.agentHeight = height; settings.agentClimb = climb; settings.agentSlope = slope;
                settings.minRegionArea = 0.01f; // the default 2 m² culls the whole apron (0.7 × 1.4 m walkable), as MRUK's own surface does
                var rot = Quaternion.LookRotation(outward, Vector3.up);
                var centre = doorFloor + outward * (ApronDepth * 0.5f);
                var src = new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box, size = new Vector3(ApronWidth, 0.02f, ApronDepth),
                    transform = Matrix4x4.TRS(centre - Vector3.up * 0.01f, rot, Vector3.one), area = 0,
                };
                var bounds = new Bounds(Vector3.zero, new Vector3(ApronWidth + 1f, 4f, ApronDepth + 1f));
                var data = NavMeshBuilder.BuildNavMeshData(settings, new List<NavMeshBuildSource> { src }, bounds, centre, rot);
                _apron = data != null ? NavMesh.AddNavMeshData(data) : default;
                if (!_apron.valid) { Debug.LogWarning("[HIS] DoorwayNav: apron bake failed"); return; }
                if (!NavMesh.SamplePosition(doorFloor + outward * 1.0f, out _, 0.3f, Filter))
                    Debug.LogWarning($"[HIS] DoorwayNav: apron baked empty (bounds {data.sourceBounds}, r {settings.agentRadius} h {settings.agentHeight} climb {settings.agentClimb} " +
                                     $"slope {settings.agentSlope} vox {settings.overrideVoxelSize}/{settings.voxelSize} tile {settings.overrideTileSize}/{settings.tileSize}, " +
                                     $"tris {NavMesh.CalculateTriangulation().indices.Length / 3}, door {doorFloor} out {outward}, errors: {string.Join("; ", settings.ValidationReport(bounds))})");
            }
            if (_link == null)
            {
                var go = new GameObject("[HIS] DoorwayLink");
                go.SetActive(false); // configure before OnEnable adds the link
                go.transform.SetParent(transform, false);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                _link = go.AddComponent<NavMeshLink>();
                _link.width = 0f;
                _link.bidirectional = true;
                _link.autoUpdate = false;
                _link.area = 0;
            }
            _link.agentTypeID = agentTypeId;
            var outside = doorFloor + outward * LinkOut;
            OutsideEnd = NavMesh.SamplePosition(outside, out var hit, 0.3f, Filter) ? hit.position : outside;
            bool linked = Reconnect();
            _link.gameObject.SetActive(true);
            Debug.Log(linked ? $"[HIS] DoorwayNav: apron + link {OutsideEnd} → {InsideEnd} (agent type {agentTypeId})"
                             : $"[HIS] DoorwayNav: apron, link off: no clear way through the doorway (door {_door}, agent type {agentTypeId})");
        }

        /// <summary>
        /// Re-pick the link's room end: on the room NavMesh (not carved away by the open leaf or furniture), and a straight
        /// crossing from the outside end to it that clears the walls, furniture and the leaf. Called on every room re-bake and
        /// by the intruder as he sets off through the doorway (the leaf may have swung open since). Returns false when no end
        /// was found: then the link is switched off (no path through the doorway: he stops, never crosses a wall, furniture or
        /// the leaf) until a later call finds one. Never moves the link under someone crossing it. <paramref name="avoid"/>: a room
        /// end at least 0.7 m (flat) from there if one clears (the player at the door he opened himself: the man steps in
        /// beside him, not through him — research/25); else the usual pick.
        /// </summary>
        public bool Reconnect(Vector3? avoid = null)
        {
            if (_link == null) return false;
            if (_link.isActiveAndEnabled && _link.occupied) return _link.activated; // re-adding it would pull the link from under his feet
            var right = Vector3.Cross(Vector3.up, _outward);
            bool found = false;
            Vector3 best = InsideEnd;
            for (int pass = avoid.HasValue ? 0 : 1; pass < 2 && !found; pass++)
                foreach (var (depth, lateral) in Candidates)
                {
                    var p = _door - _outward * depth + right * lateral;
                    if (!NavMesh.SamplePosition(p, out var hit, 0.12f, Filter) || Vector3.Dot(hit.position - _door, _outward) > -0.3f) continue;
                    if (pass == 0 && FlatDistance(hit.position, avoid.Value) < AvoidRadius) continue;
                    if (!ClearLine(OutsideEnd, hit.position)) continue;
                    best = hit.position; found = true; break;
                }
            if (!found)
            {
                if (_link.activated) Debug.LogWarning($"[HIS] DoorwayNav: no clear way through the doorway (door {_door}); link off");
                _link.activated = false;
                return false;
            }
            if ((best - InsideEnd).sqrMagnitude > 1e-6f || _link.startPoint != OutsideEnd || !_link.isActiveAndEnabled)
            {
                InsideEnd = best;
                _link.startPoint = OutsideEnd;
                _link.endPoint = InsideEnd;
                _link.UpdateLink();
            }
            _link.activated = true;
            return true;
        }

        /// <summary>Room-end candidates (depth into the room, sideways), best first: near the doorway's middle line, then out
        /// to 0.6 m sideways (a leaf stopped short by furniture, 60–80° plus its carve, can leave only the far side open).</summary>
        static readonly (float depth, float lateral)[] Candidates = BuildCandidates();
        static (float, float)[] BuildCandidates()
        {
            var list = new List<(float, float)>();
            foreach (var lats in new[] { new[] { 0f, 0.15f, -0.15f, 0.3f, -0.3f }, new[] { 0.45f, -0.45f, 0.6f, -0.6f } })
                foreach (float d in new[] { 0.7f, 0.55f, 0.9f, 1.1f, 1.3f })
                    foreach (float l in lats) list.Add((d, l));
            return list.ToArray();
        }

        /// <summary>Behind the door plane (the apron side): brains picking somewhere to go in the room must skip it.</summary>
        public bool Beyond(Vector3 p) => _apron.valid && Vector3.Dot(p - _door, _outward) > 0.05f;

        /// <summary>Beyond any door's plane (on some door's apron side).</summary>
        public static bool AnyBeyond(Vector3 p) { foreach (var n in s_all) if (n != null && n.Beyond(p)) return true; return false; }

        static float FlatDistance(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        /// <summary>A body-width line at knee and chest height hits nothing on the Room or Door layers (walls, furniture, open leaf).</summary>
        public static bool ClearLine(Vector3 a, Vector3 b)
        {
            int mask = LayerMask.GetMask("Room", "Door");
            if (mask == 0) return true;
            foreach (float y in new[] { 0.35f, 1.0f })
                if (Physics.CheckCapsule(a + Vector3.up * y, b + Vector3.up * y, ClearRadius, mask, QueryTriggerInteraction.Ignore)) return false;
            return true;
        }

        /// <summary>The nearest apron point to <paramref name="p"/> (where he waits, or where a leaver disappears).</summary>
        public bool OnApron(Vector3 p, out Vector3 point)
        {
            point = p;
            if (!_apron.valid || !NavMesh.SamplePosition(p, out var hit, 0.8f, Filter) || Vector3.Dot(hit.position - _door, _outward) < 0.2f) return false;
            point = hit.position;
            return true;
        }
    }
}
