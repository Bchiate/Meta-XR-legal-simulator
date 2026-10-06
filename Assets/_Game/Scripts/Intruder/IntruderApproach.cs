using System.Collections;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using Act = HIS.Intruder.IntruderAnimator.ActKind;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// Up the path on the door camera (Brett 0.1.20: "some investigation maybe you can see them in a security monitor at the
    /// door ... so that each situation doesnt end within 10 seconds"). On a varied round with a porch outside his door
    /// (<see cref="HIS.Security.PorchSet"/>), he starts at the street and walks up to the step in his own gait — the burglar
    /// creeping, Mike weaving, the gunman with the gun out — seen only on the monitor (outside his door he's drawn on the
    /// Outside layer). On the step, a beat that gives him away to a player who's watching:
    ///   burglar — looks both ways, and either rings to see if anyone's home or quietly tries the handle
    ///   Mike — fumbles his keys (hiccup) · home invader / knife man — a glance back, then straight at it
    ///   gunman — a look over his shoulder; now and then rings (the "Pizza!" liar) · surrenderer — hesitates, looks round
    /// Then, with a second door, he may give this one up and go round (burglar ~40%, the others ~15%, never Mike): he walks
    /// off the edge of the frame, and after the walk round the house comes up to the other door on its camera.
    /// A round through the leaf finds him anywhere on the path (Brett 0.1.21: "when i shoot through the door and hit them I
    /// should see that on the feed ... aiming low should hit him in the leg"): he reacts as he would at the door — runs off,
    /// goes down, or (a grazed fighter) comes on at a run. Out of earshot of a yell until the step. From the step on,
    /// everything is as before (<see cref="EntryByStyle"/> starts with a short wait instead of a long one).
    /// </summary>
    public partial class Intruder
    {
        /// <summary>Walk up from the street before a varied round's entry (tests of the door itself turn it off).</summary>
        public static bool StreetApproach = true;
        /// <summary>Tests: the chance he goes round to the other door (null = by who he is).</summary>
        public static float? TestSwitchDoor;
        /// <summary>Tests: ring the doorbell on the step (null = by who he is).</summary>
        public static bool? TestRingBell;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetApproachStatics() { StreetApproach = true; TestSwitchDoor = null; TestRingBell = null; } // domain reload off

        /// <summary>He came up the path on camera this round (the wait at the door is short after that).</summary>
        bool _cameUpStreet;
        /// <summary>Out on the path or going round the house: far from the leaf.</summary>
        public bool OnPorchPath { get; private set; }
        /// <summary>Went round to the other door this round.</summary>
        public bool SwitchedDoor { get; private set; }

        static HIS.Security.PorchSet PorchFor(HIS.Room.VirtualDoor door) => door != null ? HIS.Security.PorchSet.For(door) : null;

        /// <summary>His door has a path up from the street and the walk-up is on (<see cref="WalkUpFromStreet"/> will run it).</summary>
        bool WalksUpFromStreet
        {
            get
            {
                var porch = PorchFor(Door);
                var path = porch != null ? porch.Approach : null;
                return StreetApproach && path != null && path.Length >= 2;
            }
        }

        IEnumerator WalkUpFromStreet()
        {
            var porch = PorchFor(Door);
            var path = porch != null ? porch.Approach : null;
            if (!StreetApproach || path == null || path.Length < 2) yield break;
            _cameUpStreet = true;
            OnPorchPath = true;
            _doorHealth = Health;                            // (NewDoorHit: a hit since the walk-up began)
            SetHittable(true);                               // a round through the leaf reaches him out on the path too
            if (Style == EntryStyle.FalseAlarm && _gait == Gait.Drunk) SetGait(Gait.Calm); // the delivery man isn't Mike
            CarryProp();                                     // camera tells (research/21 idea 2): a crowbar, a parcel
            if (_agent.enabled) _agent.enabled = false;
            transform.SetPositionAndRotation(path[0], Quaternion.LookRotation(Flat(path[1] - path[0]), Vector3.up));
            ShowPlaced();                                    // (a Marshal visitor, hidden on his spawn frame at the step: placed now)
            Debug.Log($"[HIS] Intruder #{Id}: walks up from the street to the {(DoorIndex == 0 ? "front" : "second")} door");
            for (int k = 1; k < path.Length && Current is State.Outside; k++)
            {
                yield return WalkOutside(path[k], PathSpeed);
                if (NewDoorHit) yield return ReactToDoorShot();  // runs off / goes down (never returns), or a grazed fighter runs on
            }
            if (Current is not State.Outside) yield break;
            if (!_enraged) yield return OnTheStep(porch);
            if (NewDoorHit) yield return ReactToDoorShot();
            if (Current is not State.Outside) yield break;
            if (!_enraged && ShouldGoRound()) yield return GoRoundToOtherDoor();
            if (NewDoorHit) yield return ReactToDoorShot();
            OnPorchPath = false;
            SetHittable(true);
        }

        /// <summary>His pace up the path: his gait's, or a run once a round through the door has made him angry.</summary>
        float PathSpeed => _enraged ? IntruderClips.RunSpeed : GaitSpeed;

        /// <summary>Straight to <paramref name="to"/> on the path at <paramref name="speed"/> (outside: no NavMesh), held by a gesture.</summary>
        IEnumerator WalkOutside(Vector3 to, float speed)
        {
            while (Current is State.Outside && FlatDistance(transform.position, to) > 0.04f && !NewDoorHit)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                float s = MovementLocked ? 0f : speed;
                if (_hasAnim) _anim.SetSpeedOverride(s);
                FaceTowards(to, 8f);
                var next = Vector3.MoveTowards(transform.position, to, s * Time.deltaTime);
                next.y = to.y;
                transform.position = next;
                yield return null;
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
        }

        /// <summary>Turned to the door, a still moment (listening), then the beat that says who he is.</summary>
        IEnumerator OnTheStep(HIS.Security.PorchSet porch)
        {
            var door = Door;
            var at = door != null ? door.Center : _insidePoint;
            for (float t = 0f; t < 0.5f; t += Time.deltaTime) { FaceTowards(at, 6f); yield return null; }
            bool ring = TestRingBell ?? _intent switch
            {
                Intent.Burglar => Random.value < 0.4f,     // is anyone home?
                Intent.Gunman => Random.value < 0.3f,      // the liar at the door
                Intent.Surrenderer => Random.value < 0.25f,
                _ => false,
            };
            switch (_intent)
            {
                case Intent.Burglar:
                    Gesture(Act.LookAround);
                    yield return Hold(Random.Range(1.2f, 2f));
                    if (!ring)
                    {
                        Gesture(Act.TryDoor);
                        if (Sfx.Has("door.handle") && door != null) door.PlayThrough("door.handle", door.Center, 0.55f);
                        yield return Hold(Random.Range(1f, 1.6f));
                    }
                    break;
                case Intent.Innocent:
                    if (Style == EntryStyle.FalseAlarm) break;           // the delivery man rings (FalseAlarmVisit)
                    // A Marshal night's sober visitor (Sal, Ortiz: research/27 §6 step 8) knocks and says who he is before his key
                    // (KnockAndAnnounce, which jingles it then): no fumble and no hiccup on the step. Mike (drunk) as before.
                    if (!IsDrunk) { Gesture(Act.LookAround); yield return Hold(Random.Range(0.8f, 1.4f)); break; }
                    if (door != null) door.PlayThrough("door.keys.jingle", door.Center, 0.5f);
                    Gesture(Act.Hiccup);
                    yield return Hold(Random.Range(1f, 1.8f));
                    break;
                case Intent.Gunman:
                case Intent.MeleeAttacker:
                case Intent.KnifeAttacker:
                    Gesture(Act.LookOverShoulder);
                    yield return Hold(Random.Range(0.8f, 1.4f));
                    break;
                default:
                    Gesture(Act.LookAround);
                    yield return Hold(Random.Range(1.5f, 2.5f));
                    break;
            }
            if (ring && door != null)
            {
                door.RingDoorbell();
                Debug.Log($"[HIS] Intruder #{Id}: rings the doorbell");
                yield return Hold(Random.Range(3f, 5f));                   // waits to see if anyone comes
            }
        }

        /// <summary>A wait on the step or the path that still ends for death / leaving (a yell doesn't reach him out here).</summary>
        IEnumerator Hold(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until && Current is State.Outside && !NewDoorHit) yield return null;
        }

        bool ShouldGoRound()
        {
            var spawner = Spawner;
            if (spawner == null || spawner.DoorCount < 2 || _doorTalked || Style == EntryStyle.FalseAlarm) return false;
            if (PorchFor(OtherDoor) == null) return false;
            float p = TestSwitchDoor ?? _intent switch { Intent.Burglar => 0.4f, Intent.Innocent => 0f, _ => 0.15f };
            return Random.value < p;
        }

        HIS.Room.VirtualDoor OtherDoor
        {
            get { foreach (var d in HIS.Room.VirtualDoor.All) if (d != null && d.Index != DoorIndex) return d; return null; }
        }

        IntruderSpawner _spawner;
        IntruderSpawner Spawner => _spawner != null ? _spawner : _spawner = FindAnyObjectByType<IntruderSpawner>();

        /// <summary>Off this porch past the edge of its camera, round the house, and up to the other door on its camera.</summary>
        IEnumerator GoRoundToOtherDoor()
        {
            var here = PorchFor(Door);
            var other = OtherDoor;
            var there = PorchFor(other);
            if (here == null || there == null) yield break;
            Debug.Log($"[HIS] Intruder #{Id}: gives up on this door, goes round to the {(other.Index == 0 ? "front" : "second")} door");
            var off = Nearer(here.OffLeft, here.OffRight);
            yield return WalkOutside(off, GaitSpeed);
            if (Current is not State.Outside) yield break;
            // Round the house: out of every camera's view for the length of the walk — but heard along the real walls
            // (research/21 idea 7): a gate latch, then his steps on gravel moving from this door toward the other one.
            float walk = Mathf.Clamp(Vector3.Distance(Door.Center, other.Center) / Mathf.Max(0.5f, GaitSpeed) + Random.Range(2f, 4f), 5f, 14f);
            yield return FootstepsRound(Door, other, walk);
            if (Current is not State.Outside || !Retarget(other.Index)) yield break;
            SwitchedDoor = true;
            var from = Nearer(there.OffLeft, there.OffRight);
            transform.SetPositionAndRotation(from, Quaternion.LookRotation(Flat(there.Step - from), Vector3.up));
            yield return WalkOutside(there.Step, GaitSpeed);
            if (Current is not State.Outside) yield break;
            var at = Door.Center;
            for (float t = 0f; t < 0.4f; t += Time.deltaTime) { FaceTowards(at, 6f); yield return null; }
        }

        /// <summary>
        /// <paramref name="seconds"/> of him walking round the outside of the house from <paramref name="from"/> to <paramref name="to"/>:
        /// steps every ~0.6 s at points just outside the real walls (pushed out from the room's middle past the scanned
        /// outline), muffled as through a wall, so you can follow him round by ear. A gate latch where he sets off.
        /// </summary>
        IEnumerator FootstepsRound(HIS.Room.VirtualDoor from, HIS.Room.VirtualDoor to, float seconds)
        {
            var geo = HIS.Room.RoomScanner.Instance != null ? HIS.Room.RoomScanner.Instance.Geometry : null;
            Vector3 a = from.Center + from.Outward * 0.8f, b = to.Center + to.Outward * 0.8f;
            Vector3 mid = (from.Center + to.Center) * 0.5f;
            if (geo != null) { var e = geo.Extent(); mid = new Vector3(e.center.x, mid.y, e.center.y); }
            float y = _floorY + 0.05f;
            if (Sfx.Has("gate.latch")) from.PlayThrough("gate.latch", new Vector3(a.x, y + 0.9f, a.z), 0.5f, 1f, 1200f);
            float t0 = Time.time, next = Time.time + 0.8f;
            while (Time.time - t0 < seconds && Current is State.Outside)
            {
                if (Time.time >= next && Sfx.Has("steps.gravel"))
                {
                    next = Time.time + Random.Range(0.52f, 0.68f);
                    float k = Mathf.Clamp01((Time.time - t0) / seconds);
                    var p = OutsideWalls(Vector3.Lerp(a, b, k), mid, geo);
                    from.PlayThrough("steps.gravel", new Vector3(p.x, y, p.z), 0.55f, Random.Range(0.92f, 1.08f), 900f);
                }
                yield return null;
            }
        }

        /// <summary>A point pushed out from the room's middle until it's past the scanned outline (plus 0.6 m).</summary>
        static Vector3 OutsideWalls(Vector3 p, Vector3 mid, HIS.Room.RoomGeometry geo)
        {
            if (geo == null) return p;
            var d = new Vector2(p.x - mid.x, p.z - mid.z);
            if (d.sqrMagnitude < 1e-4f) d = Vector2.right;
            d.Normalize();
            var q = new Vector2(p.x, p.z);
            for (int i = 0; i < 30 && geo.Inside(q); i++) q += d * 0.2f;
            q += d * 0.6f;
            return new Vector3(q.x, p.y, q.y);
        }

        Vector3 Nearer(Vector3 a, Vector3 b) => FlatDistance(transform.position, a) <= FlatDistance(transform.position, b) ? a : b;

        /// <summary>Make door <paramref name="door"/> his: its way in (DoorwayNav), the points either side of it, the key light.</summary>
        bool Retarget(int door)
        {
            var spawner = Spawner;
            if (spawner == null || !spawner.PrepareDoor(door, out var outside, out var inside, out var floorY)) return false;
            DoorIndex = door;
            HIS.Room.VirtualDoor.Current = Door;
            _outsidePoint = outside; _insidePoint = inside; _floorY = floorY;
            // (Not a door the player opened himself: he walks up to it open, and the door opens on him — research/25.)
            if (Door != null && Door.IsOpen && !Door.OpenedByPlayer) Door.Close(0.8f);
            return true;
        }

        // ───────────────────────────── a door you can't see (research/21 §5) ─────────────────────────────

        /// <summary>He's coming in through a door the player wasn't facing: walks in, calls out, and can't strike until seen ≥ 1 s.</summary>
        bool _cameFromBehind;
        float _inViewSince = -1f;

        /// <summary>His door is more than 60° off where the player is looking (flat).</summary>
        bool DoorOutOfView(HIS.Room.VirtualDoor door)
        {
            var cam = Camera.main;
            if (cam == null || door == null) return false;
            var to = Flat(door.Center - cam.transform.position);
            var fwd = Flat(cam.transform.forward);
            return Vector3.Angle(fwd, to) > 60f;
        }

        /// <summary>
        /// Safety (research/21 §5.1: looming threats from behind make people dodge into real furniture): before a door the
        /// player isn't facing opens, the handle is heard at it, then at least 3 s.
        /// </summary>
        IEnumerator WarnIfUnseen(HIS.Room.VirtualDoor door)
        {
            if (!DoorOutOfView(door)) yield break;
            _cameFromBehind = true;
            string which = door != null && !door.IsFront ? "side" : "front";
            // No pop-up or announcement (Brett: "dont give a warning", "remove 1"): only what you'd really hear — the
            // handle tried at that door — and a few seconds before it opens.
            if (door != null) door.PlayThrough(Sfx.Has("door.handle") ? "door.handle" : "door.knock", door.Center, 0.8f);
            Debug.Log($"[HIS] Intruder #{Id}: at the {which} door, out of the player's view — the handle, 3 s");
            yield return DoorWait(3f);
        }

        /// <summary>Came in from behind: may he strike yet? Only once he's been in the player's view for a second.</summary>
        bool SeenLongEnough()
        {
            if (!_cameFromBehind) return true;
            if (InViewNow()) { if (_inViewSince < 0f) _inViewSince = Time.time; }
            else _inViewSince = -1f;
            return _inViewSince >= 0f && Time.time - _inViewSince >= 1f;
        }

        bool InViewNow() => InView();

        // ───────────────────────────── camera tells: props (research/21 idea 2) ─────────────────────────────

        GameObject _prop;
        bool _propIsParcel;

        /// <summary>
        /// What he carries up the path, big enough to read on the monitor: the burglar a crowbar, the delivery man a parcel.
        /// Held in the knife's grip (same hand, same fist), on the Outside layer with him; gone once he's in view in the room
        /// (<see cref="DropProp"/>) — a camera clue, not a weapon the judge would have to weigh.
        /// </summary>
        void CarryProp()
        {
            // A Marshal night's visitor says what he carries (ScenarioDef.Carries): the courier a pizza box in both hands and a
            // red cap (IntruderVisitors); None, empty hands. ByRole (every other def): as before.
            if (CarriesPizza) { DressAsCourierWithPizza(); return; }
            if (_def != null && _def.Carries == VisitorProp.None) return;
            bool crowbar = _intent == Intent.Burglar, parcel = Style == EntryStyle.FalseAlarm;
            if ((!crowbar && !parcel) || _prop != null || _weapons == null) return;
            IntruderWeapon grip = null;
            foreach (var w in _weapons) if (w != null && w.Kind == WeaponKind.Knife) grip = w;
            if (grip == null || grip.transform.parent == null) return;
            // The knife's long axis in its own space (its mesh's longest extent, blade side positive).
            var mf = grip.GetComponentInChildren<MeshFilter>(true);
            Vector3 axis = Vector3.forward;
            if (mf != null && mf.sharedMesh != null)
            {
                var b = mf.sharedMesh.bounds; var e = b.extents;
                int i = e.x >= e.y && e.x >= e.z ? 0 : e.y >= e.z ? 1 : 2;
                axis = Vector3.zero; axis[i] = b.center[i] >= 0f ? 1f : -1f;
                axis = grip.transform.InverseTransformDirection(mf.transform.TransformDirection(axis)).normalized;
            }
            var root = new GameObject(crowbar ? "Prop_Crowbar" : "Prop_Parcel").transform;
            root.SetParent(grip.transform.parent, false);
            root.SetLocalPositionAndRotation(grip.transform.localPosition, grip.transform.localRotation * Quaternion.FromToRotation(Vector3.forward, axis));
            int layer = LayerMask.NameToLayer("Outside");
            if (crowbar)
            {
                var steel = PropMaterial(new Color(0.16f, 0.16f, 0.17f), 0.8f);
                Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.18f), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.024f, 0.34f, 0.024f), steel, layer);
                Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.045f, 0.52f), Quaternion.Euler(35f, 0f, 0f), new Vector3(0.022f, 0.06f, 0.022f), steel, layer);
                Part(root, PrimitiveType.Cube, new Vector3(0f, 0f, -0.17f), Quaternion.identity, new Vector3(0.03f, 0.012f, 0.05f), steel, layer);
            }
            else
            {
                var card = PropMaterial(new Color(0.62f, 0.47f, 0.3f), 0f);
                Part(root, PrimitiveType.Cube, new Vector3(0f, -0.1f, 0.06f), Quaternion.identity, new Vector3(0.12f, 0.24f, 0.32f), card, layer);
                var tape = PropMaterial(new Color(0.86f, 0.84f, 0.78f), 0f);
                Part(root, PrimitiveType.Cube, new Vector3(0f, 0.021f, 0.06f), Quaternion.identity, new Vector3(0.124f, 0.004f, 0.05f), tape, layer);
            }
            _prop = root.gameObject;
            _propIsParcel = parcel;
            Debug.Log($"[HIS] Intruder #{Id}: carries a {(crowbar ? "crowbar" : "parcel")} (camera tell)");
        }

        static void Part(Transform parent, PrimitiveType type, Vector3 pos, Quaternion rot, Vector3 scale, Material mat, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(pos, rot);
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            if (layer >= 0) go.layer = layer;
        }

        static readonly System.Collections.Generic.Dictionary<Color, Material> s_propMats = new();
        static Material PropMaterial(Color c, float metal)
        {
            if (s_propMats.TryGetValue(c, out var m) && m != null) return m;
            var sh = Shader.Find("HIS/OcclusionLitPBR") ?? Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(sh) { color = c, name = "Prop (runtime)" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            // Meta's 0.06 (like the door and the men): a cap or helmet that falls on the real floor stays solid.
            if (m.HasProperty("_EnvironmentDepthBias")) m.SetFloat("_EnvironmentDepthBias", 0.06f);
            return s_propMats[c] = m;
        }

        /// <summary>
        /// The parcel goes down on the step ("I'll just leave it here") and stays there for the camera until the room is
        /// cleared; any other prop is simply gone (stashed) — call when he's about to be seen in the room or leaves.
        /// </summary>
        void DropProp(bool onStep)
        {
            if (_prop == null) return;
            if (onStep && _propIsParcel)
            {
                var t = _prop.transform;
                t.SetParent(null, true);
                var at = transform.position + transform.forward * 0.35f + transform.right * 0.2f;
                t.SetPositionAndRotation(new Vector3(at.x, _floorY, at.z), Quaternion.LookRotation(transform.right, Vector3.up));
                // Box on the floor, tape on its top (the parts were laid out for the hand).
                if (t.childCount > 0) t.GetChild(0).localPosition = new Vector3(0f, 0.12f, 0f);
                if (t.childCount > 1) t.GetChild(1).localPosition = new Vector3(0f, 0.242f, 0f);
                var dropped = _prop;
                System.Action clear = null;
                clear = () => { if (dropped != null) Destroy(dropped); GameSession.WorldCleared -= clear; };
                GameSession.WorldCleared += clear;
            }
            else Destroy(_prop);
            _prop = null;
        }

        /// <summary>
        /// Away from the door outside: off the side of the porch (past its camera's edge) when there's a porch, else 4 m
        /// straight back behind the door. Null when the legacy walk applies.
        /// </summary>
        Vector3? PorchExit()
        {
            var porch = PorchFor(Door);
            if (porch == null) return null;
            // Hit out on the path: away from the house, back to the street (never toward the door he was shot through). The door
            // opened on him: back down the path too, in view through the doorway (off the side of the porch he'd be drawn up
            // against the real wall beside the door).
            var path = porch.Approach;
            if (path != null && path.Length > 0 && (_doorOpenedOnHim || (OnPorchPath && FlatDistance(transform.position, porch.Step) > 2.5f))) return path[0];
            return Nearer(porch.OffLeft, porch.OffRight);
        }
    }
}
