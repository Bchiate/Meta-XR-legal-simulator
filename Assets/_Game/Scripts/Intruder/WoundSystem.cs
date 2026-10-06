using System.Collections;
using System.Collections.Generic;
using HIS.Room;
using HIS.Weapons;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// Gore: a wound decal on the body at the hit point, a blood spray burst along the exit direction, and splatter on
    /// whatever real surface is behind the hit (scanned floor/walls/ceiling/furniture, or the open virtual door leaf).
    /// Intensity is a global setting (Sandbox slider later). Body wounds ride the bones and go with the body (capped per
    /// intruder); world marks belong to <see cref="BloodDecals"/> (pooled, lifetimes, drying, fade-out, room-wide cap).
    /// Realism rules (Brett, 2026-09-23: blood floating in the doorway, a pool growing on empty floor):
    /// - Blood only lands on real surfaces: never on door/window frame planes, never on the wall plane across a doorway.
    /// - Impact marks land at their final size; grazing hits streak along the travel direction, with a few flecks beyond.
    /// - Heavy hits drip small drops under the wound; a wounded man on the move leaves a sparse drip trail.
    /// - Only the death pool grows: it starts under the torso once the body is still, spreads fast then slower, and its
    ///   size follows how badly he bled. Shot behind the closed door, his blood comes out under it instead (<see cref="SeepUnderDoor"/>).
    /// A shotgun blast lands 8–9 hits in one frame, so world marks and spray have a per-frame budget.
    /// </summary>
    public class WoundSystem : MonoBehaviour
    {
        [Range(0f, 2f)] public static float GoreIntensity = 1f;

        [SerializeField] Material bloodMaterial;
        [SerializeField] Material wallSplatterMaterial;
        [SerializeField] float bodyDecalSize = 0.07f;
        [SerializeField] float worldSplatterSize = 0.35f;
        [SerializeField] float splatterSearch = 2.5f;
        [SerializeField] int maxWounds = 40;

        // Atlas cells (BloodSplatAtlas): 0/2 = spatter with flecks (2 elongated), 1/3 = solid blobs.
        // Pools use 3 only: cell 1 has a detached lobe that would spread out across empty floor as the pool grows.
        const int StreakCell = 2, PoolCell = 3;
        static int SolidCell => Random.value < 0.5f ? 1 : 3;
        const float SplatReach = 0.35f, PoolReach = 0.27f; // visible radius of the atlas splat / pool blob, × quad width

        readonly List<GameObject> _bodyWounds = new();
        static MaterialPropertyBlock _mpb;
        Intruder _intruder;
        Transform _hips, _chest;
        float _bled;       // how much he has bled (wound damage, each capped): sizes the death pool
        float _bleeding;   // live bleed rate 0..1 for the drip trail; heals off over ~40 s
        Vector3 _lastDrip;
        bool _pooled;

        // Per-frame budget: one blast = a handful of splats and one set of drops, not 27 raycasts and 27 quads.
        int _budgetFrame = -1, _splatBudget, _dropBudget, _sprayBudget, _fleckBudget;
        void RefillBudget()
        {
            if (_budgetFrame == Time.frameCount) return;
            _budgetFrame = Time.frameCount; _splatBudget = 5; _dropBudget = 1; _sprayBudget = HIS.Core.PerfBudget.Spray(60); _fleckBudget = 6;
        }

        void Awake()
        {
            _intruder = GetComponent<Intruder>();
            var anim = GetComponentInChildren<Animator>(true);
            if (anim != null && anim.isHuman)
            {
                _hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                _chest = anim.GetBoneTransform(HumanBodyBones.Chest);
                if (_chest == null) _chest = anim.GetBoneTransform(HumanBodyBones.Spine);
            }
        }

        /// <summary>
        /// A hit's wound. <paramref name="worldFx"/> false (shot through the closed door, out of sight): only the body state
        /// (the wound on him, hidden with him until he shows, and his bleeding), no spray or marks on our side of the door.
        /// </summary>
        public void AddWound(Hitbox hitbox, in DamageInfo info, float applied, bool worldFx = true)
        {
            if (GoreIntensity <= 0f) return;
            RefillBudget();

            float scale = Mathf.Lerp(0.6f, 1.6f, Mathf.InverseLerp(10f, 100f, applied)) * GoreIntensity;
            bool bullet = info.Type == DamageType.Bullet;
            float bleed = Mathf.Min(applied, 100f); // a headshot's 1000 doesn't make a lake
            _bled += bleed;
            if (_bleeding <= 0.05f) _lastDrip = info.Point;
            _bleeding = Mathf.Min(1f, _bleeding + bleed / 60f);

            // 1. Wound on the body, parented so it rides the bone/part.
            var wound = MakeBodyDecal(info.Point + info.Normal * 0.003f, info.Normal, bodyDecalSize * scale,
                info.Type == DamageType.Slash ? Random.Range(0f, 360f) : 0f);
            // Ride the bone that was hit whenever its scale is uniform (any size: a rig imported in cm has 0.01/100 bones);
            // only a non-uniformly scaled placeholder part would skew the quad, so those ride the root. (Wounds left on the
            // root of a rig stayed hanging in the air where he was shot while the body fell away.)
            var ls = hitbox.transform.lossyScale;
            bool uniform = ls.x > 1e-4f && ls.y > 1e-4f && ls.z > 1e-4f && Mathf.Abs(ls.y / ls.x - 1f) < 0.05f && Mathf.Abs(ls.z / ls.x - 1f) < 0.05f;
            wound.transform.SetParent(uniform ? hitbox.transform : transform, true);
            if (info.Type == DamageType.Slash) wound.transform.localScale = new Vector3(wound.transform.localScale.x * 2.5f, wound.transform.localScale.y * 0.5f, wound.transform.localScale.z);
            if (!_shown) wound.GetComponent<MeshRenderer>().enabled = false;
            if (_woundLayer >= 0) wound.layer = _woundLayer;
            _bodyWounds.Add(wound);
            while (_bodyWounds.Count > maxWounds) { if (_bodyWounds[0] != null) Destroy(_bodyWounds[0]); _bodyWounds.RemoveAt(0); }
            var travel = info.Direction.sqrMagnitude > 1e-6f ? info.Direction.normalized : -info.Normal;
            // The spray's layer follows where he is: the room, or (behind his door) the door camera's Outside layer — so a
            // round through the door shows as blood on the feed (Brett 0.1.22), never on our side.
            int outsideLayer = LayerMask.NameToLayer("Outside");
            bool behind = _intruder != null && _intruder.BehindDoor;
            if (_spray == null) _spray = BuildSpray();
            _spray.gameObject.layer = behind && outsideLayer >= 0 ? outsideLayer : 0;
            if (!worldFx)
            {
                if (behind && outsideLayer >= 0) Spray(info.Point, bullet ? travel : info.Normal, bullet ? 18 : 10, scale);
                return;
            }

            // Spray burst: droplets leaving the wound along the exit direction.
            Spray(info.Point, bullet ? travel : info.Normal, bullet ? 18 : 10, scale);

            // 2. Exit-side spray onto real surfaces: bullets throw blood forward, blades sideways/down.
            var sprayDir = bullet ? travel : Vector3.Lerp(travel, Vector3.down, 0.5f).normalized;
            int splats = bullet ? 3 : 2;
            float reach = splatterSearch * scale;
            var splatMat = wallSplatterMaterial != null ? wallSplatterMaterial : bloodMaterial;
            for (int i = 0; i < splats && _splatBudget > 0; i++)
            {
                _splatBudget--;
                var dir = Quaternion.Euler(Random.Range(-25f, 25f), Random.Range(-25f, 25f), 0) * sprayDir;
                if (!RaycastRealWorld(info.Point, dir, reach, out var hit)) continue;
                // Close surfaces catch a dense mark; farther ones only the thinner end of the spray.
                float size = worldSplatterSize * scale * Random.Range(0.6f, 1.3f) * Mathf.Lerp(1.2f, 0.5f, hit.Dist / splatterSearch);
                if (Mathf.Abs(hit.Normal.y) < 0.5f) size *= 0.8f; // walls: tighter, directional marks
                Splat(splatMat, hit, dir, size, 150f, bullet ? Random.Range(0, 3) : Random.Range(0, 2));
                if (i == 0 && hit.Dist > 0.4f) HIS.Core.Sfx.Play("blood.splat", hit.Point, 0.7f);
            }

            // 3. A heavy hit drips: a few small drops on the floor under the wound (not a pool — pools are for bodies).
            if (applied >= 40f && _dropBudget > 0)
            {
                _dropBudget--;
                if (RaycastRealWorld(info.Point, Vector3.down, 2.5f, out var floor) && floor.Normal.y > 0.7f)
                    Drops(floor, Random.Range(2, 5), 0.12f, 0.06f * GoreIntensity, 150f);
            }
        }

        bool _shown = true;

        /// <summary>Show/hide the wounds on his body with it (<see cref="Intruder.SetVisible"/>).</summary>
        public void ShowBodyWounds(bool shown)
        {
            _shown = shown;
            foreach (var w in _bodyWounds) if (w != null && w.TryGetComponent<MeshRenderer>(out var r)) r.enabled = shown;
        }

        /// <summary>
        /// Behind his door: his wounds are drawn only for the door camera (layer <paramref name="layer"/>, the Outside layer),
        /// so a round through the door shows on the feed (Brett 0.1.21) and never on our side. -1: back on his own layer.
        /// </summary>
        public void WoundsOnLayer(int layer)
        {
            _woundLayer = layer;
            foreach (var w in _bodyWounds) if (w != null) w.layer = layer >= 0 ? layer : 0;
        }
        int _woundLayer = -1;

        /// <summary>Wounds on his body that the headset draws right now (ScenarioTests): enabled and not on the door camera's layer.</summary>
        public int ShownBodyWounds
        {
            get { int n = 0; foreach (var w in _bodyWounds) if (w != null && w.layer == 0 && w.TryGetComponent<MeshRenderer>(out var r) && r.enabled) n++; return n; }
        }

        // ───────────────────────────── drip trail ─────────────────────────────
        void Update()
        {
            if (_bleeding <= 0.05f) return;
            _bleeding -= Time.deltaTime / 40f;
            // (Out of sight behind the door, no trail: the next drop after he shows starts from where he is then.)
            if (!_shown) { _lastDrip = transform.position + Vector3.up; return; }
            if (GoreIntensity <= 0f || _intruder == null || !Walking(_intruder.Current)) return;
            // Drip from the newest wound (it rides the bone), every so many metres of travel; heavier bleeding = closer drops.
            var src = transform.position + Vector3.up;
            for (int i = _bodyWounds.Count - 1; i >= 0; i--) if (_bodyWounds[i] != null) { src = _bodyWounds[i].transform.position; break; }
            var moved = src - _lastDrip; moved.y = 0f;
            float spacing = Mathf.Lerp(1.1f, 0.35f, _bleeding);
            if (moved.sqrMagnitude < spacing * spacing) return;
            _lastDrip = src;
            if (RaycastRealWorld(src, Vector3.down, 2.5f, out var floor) && floor.Normal.y > 0.7f)
                Drops(floor, _bleeding > 0.6f ? 2 : 1, 0.05f, 0.045f * GoreIntensity, 60f);
        }

        static bool Walking(Intruder.State s) =>
            s is Intruder.State.Entering or Intruder.State.Chasing or Intruder.State.Attacking or Intruder.State.Idle
              or Intruder.State.Fleeing or Intruder.State.Complying;

        // ───────────────────────────── death pool ─────────────────────────────
        /// <summary>
        /// The pool that spreads from under the body; it fades when the body retires. Called once the body is still
        /// (<see cref="Intruder"/> FinishDeath); <paramref name="under"/> is the fallback centre for a rig without bones.
        /// </summary>
        bool _outside; float _outsideFloorY; int _outsideLayer;

        /// <summary>
        /// He died out on the porch/path, seen only by the door camera: the pool goes on the Outside layer at
        /// <paramref name="floorY"/> (no room surface out there).
        /// </summary>
        public void BleedOutOutside(Vector3 under, float floorY, int layer)
        {
            _outside = true; _outsideFloorY = floorY; _outsideLayer = layer;
            BleedOut(under);
        }

        public void BleedOut(Vector3 under)
        {
            if (GoreIntensity <= 0f || _pooled) return;
            _pooled = true;
            _bleeding = 0f; // the trail stops; the pool takes over
            StartCoroutine(PoolWhenStill(under));
        }

        IEnumerator PoolWhenStill(Vector3 under)
        {
            // Blood takes a moment to find the floor after the body settles.
            yield return new WaitForSeconds(Random.Range(0.6f, 1.2f));
            // Under the torso (between hips and chest), pulled toward where he was hit; wounds ride the bones, so they're
            // where the body lies now.
            var c = _hips != null ? (_chest != null ? Vector3.Lerp(_hips.position, _chest.position, 0.45f) : _hips.position) : under;
            Vector3 pull = Vector3.zero; int n = 0;
            foreach (var w in _bodyWounds)
            {
                if (w == null) continue;
                var d = w.transform.position - c; d.y = 0f;
                if (d.sqrMagnitude < 0.8f * 0.8f) { pull += d; n++; }
            }
            if (n > 0) c += Vector3.ClampMagnitude(pull / n * 0.6f, 0.3f);

            Vector3 p, normal;
            var room = RoomScanner.Instance != null ? RoomScanner.Instance.Room : null;
            if (_outside)
            {
                // Out on the porch/path (the door camera sees it, the headset never does): straight onto his floor height.
                float bloodyO = Mathf.InverseLerp(20f, 250f, _bled);
                float sizeO = Mathf.Min(Mathf.Lerp(0.6f, 1.25f, bloodyO) * GoreIntensity, 1.5f), startO = Mathf.Min(0.15f, sizeO * 0.5f);
                var at = new Vector3(c.x, _outsideFloorY + 0.006f, c.z);
                BloodDecals.Spawn(bloodMaterial, at, OnSurface(Vector3.up, AnyTangent(Vector3.up)) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)),
                    startO, startO * Random.Range(0.75f, 0.95f), PoolCell, 600f, owner: gameObject, growTo: sizeO,
                    growSeconds: Mathf.Lerp(9f, 16f, bloodyO), layer: _outsideLayer);
                yield break;
            }
            if (RaycastRealWorld(c + Vector3.up * 0.6f, Vector3.down, 1.6f, out var floor, floorOnly: true) && floor.Normal.y > 0.7f)
            { p = floor.Point; normal = floor.Normal; }
            else
            {
                // Lying across the threshold (outside the scanned floor polygon): the floor's height, if he's on it at all.
                var fa = room != null && room.FloorAnchors is { Count: > 0 } floors ? floors[0] : null;
                if (fa == null || Mathf.Abs(c.y - fa.transform.position.y) > 0.8f) yield break;
                p = c; p.y = fa.transform.position.y; normal = Vector3.up;
            }

            // Size follows how badly he bled (a single pistol round vs. a magazine), capped.
            float bloody = Mathf.InverseLerp(20f, 250f, _bled);
            float size = Mathf.Min(Mathf.Lerp(0.55f, 1.15f, bloody) * GoreIntensity, 1.5f);
            // Nothing real occludes the quad: a pool spreading past a wall or under a couch would draw over it. Nudge it
            // off the nearest one (up to 20 cm) and cap its spread there.
            if (room != null)
            {
                float r = size * PoolReach;
                float d = room.TryGetClosestSurfacePosition(p + Vector3.up * 0.02f, out var sp, out _, BlockFilter);
                if (d < r && !InDoorway(sp)) // the wall plane across a doorway is open floor, not a wall
                {
                    var away = p - sp; away.y = 0f;
                    if (away.sqrMagnitude > 1e-6f) { float push = Mathf.Min(r - d, 0.2f); p += away.normalized * push; d += push; }
                    size = Mathf.Max(0.3f, Mathf.Min(size, (d + 0.04f) / PoolReach));
                }
            }
            float start = Mathf.Min(0.15f, size * 0.5f); // mostly hidden under the torso
            float aspect = Random.Range(0.75f, 0.95f);
            BloodDecals.Spawn(bloodMaterial, p + normal * 0.004f, OnSurface(normal, AnyTangent(normal)) * Quaternion.Euler(0, 0, Random.Range(0f, 360f)),
                start, start * aspect, PoolCell, 600f, owner: gameObject, growTo: size, growSeconds: Mathf.Lerp(9f, 16f, bloody));
        }

        // ───────────────────────────── under the door ─────────────────────────────
        /// <summary>How badly he was hurt out of sight behind the closed door: how much comes under it (<see cref="SeepUnderDoor"/>).</summary>
        public enum DoorSeep { None, Hit, Legs, Kill }
        DoorSeep _doorSeep;

        /// <summary>
        /// Shot through the closed door (Brett 0.1.17: "maybe some blood pours from under the door after a second"): about a
        /// second later his blood finds the gap under the leaf and comes out onto the real floor on our side, pushing out of the
        /// gap and spreading along it (<see cref="BloodDecals.Seep"/>). A hit he runs off with: a small seep. Legs shot out,
        /// sliding down the leaf: more, for longer. Killed against it: the biggest and slowest, sized by how badly he bled, in
        /// three runs along the gap that flow together. Each level once (a kill after a hit adds its pool over the first); only
        /// while he's at the door, not halfway down the path. <paramref name="floorY"/> = the scanned floor (the door's sill can
        /// sit a little off it).
        /// </summary>
        public void SeepUnderDoor(DoorSeep kind, float floorY)
        {
            var door = _intruder != null ? _intruder.Door : VirtualDoor.Instance; // his door (front or second)
            if (GoreIntensity <= 0f || kind <= _doorSeep || door == null) return;
            // Decided once he's settled, from where his BODY ends up (Brett 0.1.26: "had a guy get shot and fall in the path
            // not at the door and blood still went under door" — it was decided at the shot, from his feet, 1.4 m out; a man
            // killed there who fell back down the path, or hit at the door who then crawled off to die, still bled under it).
            if (kind > _seepPending) _seepPending = kind;
            _seepFloorY = floorY;
            _seepAt = Mathf.Max(_seepAt, Time.time + (kind == DoorSeep.Kill ? 0.85f : 0.9f)); // the body's mostly down / he stays or moves off
            if (_seepCheck == null) _seepCheck = StartCoroutine(SeepWhenSettled(door));
        }

        DoorSeep _seepPending;
        float _seepAt, _seepFloorY;
        Coroutine _seepCheck;

        System.Collections.IEnumerator SeepWhenSettled(VirtualDoor door)
        {
            while (Time.time < _seepAt) yield return null;
            var kind = _seepPending;
            _seepPending = DoorSeep.None; _seepAt = 0f; _seepCheck = null;
            if (door != null && kind > _doorSeep) Seep(door, kind, _seepFloorY);
        }

        /// <summary>The body point nearest the door (hips, chest, head; else his feet): where his blood would reach the gap from.</summary>
        Vector3 NearestBodyPoint(VirtualDoor door, Vector3 inward)
        {
            var best = transform.position;
            float bestBehind = Vector3.Dot(door.transform.position - best, inward);
            var anim = _intruder != null ? _intruder.GetComponentInChildren<Animator>() : null;
            if (anim != null && anim.isHuman)
                foreach (var b in new[] { HumanBodyBones.Hips, HumanBodyBones.Chest, HumanBodyBones.Head })
                {
                    var t = anim.GetBoneTransform(b);
                    if (t == null) continue;
                    float behind = Vector3.Dot(door.transform.position - t.position, inward);
                    if (behind < bestBehind) { bestBehind = behind; best = t.position; }
                }
            return best;
        }

        void Seep(VirtualDoor door, DoorSeep kind, float floorY)
        {
            var dt = door.transform;
            var o = door.Opening;
            var inward = door.Inward; inward.y = 0f;
            if (inward.sqrMagnitude < 1e-4f) return;
            inward.Normalize();
            // A kill: where the body came to rest (the part nearest the door). A hit: where he's standing now, a second on.
            var at = kind == DoorSeep.Kill ? NearestBodyPoint(door, inward) : transform.position;
            var local = dt.InverseTransformPoint(at);
            float behind = Vector3.Dot(dt.position - at, inward); // metres out behind the door plane
            // Only a man right at the door bleeds under it (Brett 0.1.23: "the blood should only go on the door if they die close
            // to the door if they fall in the path that wouldnt make sense"): lying against it, or standing where he waits.
            float reach = kind == DoorSeep.Kill ? 1.35f : 1.4f, aside = kind == DoorSeep.Kill ? 0.5f : 0.6f; // (dropped on the step: yes; fallen back down the path: no)
            if (behind < -0.3f || behind > reach || local.x < o.xMin - aside || local.x > o.xMax + aside)
            {
                Debug.Log($"[HIS] Intruder #{(_intruder != null ? _intruder.Id : 0)}: no blood under the door ({kind}: {behind:F2} m out, {local.x:F2} across)");
                return;
            }
            _doorSeep = kind;

            bool kill = kind == DoorSeep.Kill, legs = kind == DoorSeep.Legs;
            float bloody = Mathf.InverseLerp(20f, 250f, _bled);
            // (Quad widths: the blob shows ~0.27 of it each way.) Brett 0.1.20: "I want more blood to pour under the door".
            // A kill: the whole door wide and ~0.9-1.2 m out into the room, in four runs that keep coming for ~15 s; legs
            // out ~0.6 m; a hit ~0.25 m in two runs. Longer than wide (aspect > 1): it pours out INTO the room, not along the wall.
            float size = (kill ? Mathf.Lerp(1.8f, 2.2f, bloody) : legs ? 1.4f : 0.75f) * GoreIntensity;
            float grow = kill ? Mathf.Lerp(12f, 16f, bloody) : legs ? 9f : 4f;
            int runs = kill ? 4 : legs ? 3 : 2;
            // On the real floor (not the scanned sill, unless the two disagree wildly), a leaf's depth back under the door: each
            // run starts hidden under the leaf and creeps out of the gap into the room (its local +Y), never wider than the
            // opening + 10 cm a side (past the jambs it would run up the wall line).
            float sillY = dt.TransformPoint(new Vector3(o.center.x, o.yMin, 0f)).y;
            float y = (Mathf.Abs(floorY - sillY) < 0.3f ? floorY : sillY) + 0.004f;
            float maxReach = o.width * 0.5f + 0.1f, side = Random.value < 0.5f ? -1f : 1f;
            for (int i = 0; i < runs; i++)
            {
                float s = Mathf.Min(Mathf.Min(i == 0 ? size : size * Random.Range(0.6f, 0.85f), maxReach / PoolReach), 2.2f);
                float r = s * PoolReach;
                float off = i == 0 ? 0f : (i % 2 == 1 ? side : -side) * Random.Range(0.12f, 0.3f);
                float x = Mathf.Clamp(local.x + off, o.xMin - 0.1f + r, o.xMax + 0.1f - r);
                var edge = dt.TransformPoint(new Vector3(x, o.yMin, 0f)) - inward * VirtualDoor.LeafDepth;
                edge.y = y;
                var rot = Quaternion.LookRotation(Vector3.down, inward) * Quaternion.Euler(0f, 0f, Random.Range(-12f, 12f));
                float aspect = kill ? Random.Range(1.25f, 1.6f) : legs ? Random.Range(1.05f, 1.3f) : Random.Range(0.9f, 1.1f);
                // The first run where he is, about a second after the shot; the others find the gap a moment later.
                float delay = i == 0 ? Random.Range(0.05f, 0.25f) : Random.Range(0.4f, 1.4f) + (i - 1) * 1.2f; // (the check waited ~1 s already)
                BloodDecals.Seep(bloodMaterial, edge, rot, 0.06f, s, aspect, PoolCell, PoolReach,
                    i == 0 ? grow : grow * Random.Range(0.6f, 0.85f), delay, 240f);
            }
            Debug.Log($"[HIS] Intruder #{(_intruder != null ? _intruder.Id : 0)}: blood under the door ({kind}, {runs} run(s), {size:F2} m, {behind:F2} m out)");
        }

        // ───────────────────────────── surfaces ─────────────────────────────
        struct SurfaceHit { public Vector3 Point, Normal; public float Dist; public Transform Parent; }

        // Real, solid surfaces only. Not DOOR_FRAME / WINDOW_FRAME (openings), INVISIBLE_WALL_FACE (a boundary, not a wall),
        // GLOBAL_MESH (no plane/volume here).
        const MRUKAnchor.SceneLabels Paintable =
            MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.WALL_FACE |
            MRUKAnchor.SceneLabels.INNER_WALL_FACE | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH |
            MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER |
            MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT |
            MRUKAnchor.SceneLabels.WALL_ART;
        static readonly LabelFilter PaintFilter = new(Paintable);
        static readonly LabelFilter FloorFilter = new(MRUKAnchor.SceneLabels.FLOOR);
        static readonly LabelFilter BlockFilter = new(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE |
            MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED);
        static int _roomMask = -1, _doorMask;

        /// <summary>
        /// Where does a spray land in the real room? MRUK's room raycast (scanned planes/volumes, available in the simulator
        /// and on device without colliders), minus anything inside a doorway: a WALL_FACE plane is a full rectangle that also
        /// spans its door opening, so a hit there is blood hanging in the open doorway. The open virtual door leaf (Door
        /// layer, collider only while it stands open) is a surface too: those marks are parented to it and swing with it.
        /// Room-layer physics (walls with the door hole cut, floor, furniture) is the fallback when there is no MRUK room.
        /// Never the intruder, the player, weapons or hitboxes (not on those layers).
        /// </summary>
        static bool RaycastRealWorld(Vector3 origin, Vector3 dir, float maxDist, out SurfaceHit hit, bool floorOnly = false)
        {
            hit = default;
            if (_roomMask < 0) { _roomMask = LayerMask.GetMask("Room"); _doorMask = LayerMask.GetMask("Door"); }
            var ray = new Ray(origin, dir);
            var scanner = RoomScanner.Instance;
            var room = scanner != null ? scanner.Room : null;
            bool found = false;
            if (room != null)
            {
                if (room.Raycast(ray, maxDist, floorOnly ? FloorFilter : PaintFilter, out var mh, out _))
                { hit = new SurfaceHit { Point = mh.point, Normal = mh.normal, Dist = mh.distance }; found = true; }
            }
            else if (Physics.Raycast(ray, out var ph, maxDist, _roomMask, QueryTriggerInteraction.Ignore))
            { hit = new SurfaceHit { Point = ph.point, Normal = ph.normal, Dist = ph.distance }; found = true; }

            if (!floorOnly && _doorMask != 0 &&
                Physics.Raycast(ray, out var dh, found ? hit.Dist : maxDist, _doorMask, QueryTriggerInteraction.Ignore))
            {
                hit = new SurfaceHit { Point = dh.point, Normal = dh.normal, Dist = dh.distance, Parent = dh.collider.transform };
                return true;
            }
            return found && !InDoorway(hit.Point);
        }

        /// <summary>In (or within 10 cm of) a scanned door opening, on its plane: no real surface there.</summary>
        static bool InDoorway(Vector3 p) => DoorRoom(p, Vector3.zero) <= 0f;

        const float DoorMargin = 0.1f, DoorDepth = 0.15f;

        /// <summary>
        /// How far a mark at <paramref name="p"/> can spread along its wall before it reaches a door opening (+margin):
        /// 0 = inside one, +inf = no door on this plane. <paramref name="normal"/> zero = any orientation (point test only).
        /// </summary>
        static float DoorRoom(Vector3 p, Vector3 normal)
        {
            float room = float.PositiveInfinity;
            var scanner = RoomScanner.Instance;
            if (scanner == null) return room;
            bool any = normal == Vector3.zero;
            foreach (var d in scanner.Doors)
            {
                if (d == null || !d.PlaneRect.HasValue) continue;
                var l = d.transform.InverseTransformPoint(p);
                if (Mathf.Abs(l.z) >= DoorDepth) continue;
                var r = d.PlaneRect.Value;
                float dx = Mathf.Max(Mathf.Max(r.xMin - DoorMargin - l.x, l.x - r.xMax - DoorMargin), 0f);
                float dy = Mathf.Max(Mathf.Max(r.yMin - DoorMargin - l.y, l.y - r.yMax - DoorMargin), 0f);
                if (dx <= 0f && dy <= 0f) return 0f;
                // Spread only matters on the wall itself (a floor mark near the threshold spreads away from the door plane).
                if (any || Mathf.Abs(Vector3.Dot(normal, d.transform.forward)) < 0.7f) continue;
                room = Mathf.Min(room, Mathf.Sqrt(dx * dx + dy * dy));
            }
            return room;
        }

        // ───────────────────────────── marks ─────────────────────────────
        /// <summary>
        /// An impact mark: the shallower the angle, the longer the streak along the travel direction (up to 3:1), pushed
        /// forward of the impact, with a few flecks flung beyond it.
        /// </summary>
        void Splat(Material mat, in SurfaceHit h, Vector3 dir, float size, float life, int flecks)
        {
            var along = Vector3.ProjectOnPlane(dir, h.Normal);
            float stretch = along.sqrMagnitude > 1e-4f ? Mathf.Clamp(1f / Mathf.Max(Mathf.Abs(Vector3.Dot(dir, h.Normal)), 0.33f), 1f, 3f) : 1f;
            if (h.Parent == null)
            {
                // A hit just outside the door margin must not streak (or spread) across the opening.
                float room = DoorRoom(h.Point, h.Normal);
                if (size * (SplatReach * stretch + (stretch - 1f) * 0.3f) > room) { stretch = 1f; size = Mathf.Min(size, room / SplatReach); }
                if (size < 0.03f) return;
            }
            bool streak = stretch > 1.1f;
            var rot = OnSurface(h.Normal, streak ? along.normalized : AnyTangent(h.Normal)) *
                      Quaternion.Euler(0, 0, streak ? Random.Range(-8f, 8f) : Random.Range(0f, 360f));
            var x = rot * Vector3.right; var y = rot * Vector3.up;
            float w = size * stretch, ht = size / Mathf.Sqrt(stretch);
            float lift = h.Parent != null ? 0.008f : 0.005f; // the leaf's raised panels stand 4 mm proud
            var centre = h.Point + h.Normal * lift + x * (size * (stretch - 1f) * 0.3f);
            BloodDecals.Spawn(mat, centre, rot, w, ht, stretch > 1.4f ? StreakCell : Random.Range(0, 4), life, h.Parent);

            for (int i = 0; i < flecks && _fleckBudget > 0; i++)
            {
                _fleckBudget--;
                float side = streak || Random.value < 0.5f ? 1f : -1f;
                var p = centre + x * (w * Random.Range(0.45f, 0.9f) * side) + y * (ht * Random.Range(-0.4f, 0.4f));
                if (h.Parent == null && InDoorway(p)) continue;
                float d = size * Random.Range(0.08f, 0.18f);
                BloodDecals.Spawn(mat, p, rot * Quaternion.Euler(0, 0, Random.Range(0f, 360f)), d, d, SolidCell, life, h.Parent);
            }
        }

        /// <summary>A few small round drops scattered around a floor point.</summary>
        void Drops(in SurfaceHit h, int count, float spread, float size, float life)
        {
            var t = AnyTangent(h.Normal); var b = Vector3.Cross(h.Normal, t);
            var rot = OnSurface(h.Normal, t);
            for (int i = 0; i < count; i++)
            {
                var p = h.Point + h.Normal * 0.004f + t * Random.Range(-spread, spread) + b * Random.Range(-spread, spread);
                if (InDoorway(p)) continue;
                float d = size * Random.Range(0.6f, 1.3f);
                BloodDecals.Spawn(bloodMaterial, p, rot * Quaternion.Euler(0, 0, Random.Range(0f, 360f)), d, d * Random.Range(0.8f, 1f), SolidCell, life);
            }
        }

        /// <summary>A quad lying on a surface (its visible face along <paramref name="normal"/>), local X along <paramref name="along"/>.</summary>
        static Quaternion OnSurface(Vector3 normal, Vector3 along)
        {
            var f = -normal;
            return Quaternion.LookRotation(f, Vector3.Cross(f, along));
        }

        static Vector3 AnyTangent(Vector3 n) => Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;

        // ───────────────────────────── spray + body decals ─────────────────────────────
        ParticleSystem _spray;
        void Spray(Vector3 point, Vector3 dir, int count, float scale)
        {
            if (_spray == null) _spray = BuildSpray();
            _spray.transform.position = point;
            _spray.transform.rotation = Quaternion.LookRotation(dir.sqrMagnitude > 0 ? dir : Vector3.up);
            int n = Mathf.Min(Mathf.RoundToInt(count * scale), _sprayBudget);
            if (n <= 0) return;
            _sprayBudget -= n;
            _spray.Emit(n);
        }

        ParticleSystem BuildSpray()
        {
            var go = new GameObject("BloodSpray");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop();
            var main = ps.main;
            main.loop = false; main.playOnAwake = false; main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.04f);
            main.startColor = new Color(0.45f, 0.02f, 0.02f, 1f);
            main.gravityModifier = 1.2f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;
            var emission = ps.emission; emission.enabled = false; // manual Emit()
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 22f; shape.radius = 0.01f;
            var col = ps.colorOverLifetime; col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(new Color(0.5f, 0.03f, 0.03f), 0), new GradientColorKey(new Color(0.3f, 0.01f, 0.01f), 1) },
                         new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 0.7f), new GradientAlphaKey(0, 1) });
            col.color = grad;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.08f; r.lengthScale = 1.6f;
            r.sharedMaterial = bloodMaterial; // atlas splat as the droplet sprite
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return ps;
        }

        /// <summary>A wound on the body (not pooled: it rides a bone and is destroyed with the body).</summary>
        GameObject MakeBodyDecal(Vector3 pos, Vector3 normal, float size, float rollDeg)
        {
            var go = new GameObject("Blood");
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, rollDeg);
            go.transform.localScale = new Vector3(size, size, 1f);
            var mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = BloodDecals.Quad;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = bloodMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
            // Pick one of the 4 atlas splats without breaking batching.
            _mpb ??= new MaterialPropertyBlock();
            _mpb.Clear();
            _mpb.SetVector("_BaseMap_ST", BloodDecals.AtlasCell(Random.Range(0, 4)));
            mr.SetPropertyBlock(_mpb);
            return go;
        }
    }
}
