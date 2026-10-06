using HIS.Core;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;

namespace HIS.Room
{
    /// <summary>
    /// The Marshal weapons check's practice target (research/26 §2 "a cardboard man stands 1 m inside your front door";
    /// research/27; driven by Scenario/MarshalBriefing): a flat cardboard man on a post, about a metre inside the front door,
    /// facing the player. Primitives only, built for the check and gone after it.
    /// It is never an Intruder and never judged: no ForceApplied, no Judge, no police, nothing the night counts. It isn't flesh
    /// (<see cref="IInanimate"/>, like a car's body): a round makes no flesh sound, and the knife's own flesh-only contact
    /// passes it by, so the target reads the blade itself (<see cref="KnifeHits"/>).
    /// Every hit shows (Brett: "every shot must visibly land", his Last Stand reference): a round punches a hole through the
    /// cardboard (the door leaf's bullet-hole mesh, <see cref="VirtualDoor.BulletHoleMesh"/>), a blade leaves a slit, a light
    /// thud plays and the board rocks back on its post and settles; a round also gives the gun hand the hit pulse a man gives.
    /// GPU: six small boxes and at most <see cref="MaxMarks"/> marks, only during the check (the room is empty then).
    /// Brett 0.1.57: Deputy Ortiz stands in the room for the check; the board is placed so he's never between the player and it
    /// nor behind it as the player sees it (<see cref="Crowds"/>), and it's carved out of the NavMesh so he walks out round it —
    /// and (0.1.58) it stands a man's width clear of the leaf's whole swing (<see cref="LeafWalkway"/>: deeper in when the door
    /// is wide), so the doorway is never boxed in between the two holes however far the door is swung.
    /// </summary>
    public class PracticeTarget : MonoBehaviour, IDamageable, IInanimate
    {
        /// <summary>How far inside the door's opening the target stands (m) — further where the leaf's swing needs it (PlanSpot).</summary>
        public const float InsideDoor = 1.1f;
        /// <summary>Holes and slits kept at once (a shotgun blast puts in nine); the oldest is reused.</summary>
        public const int MaxMarks = 16;

        // The cardboard man (m, in the target's frame: +z is its printed face, toward the player; y up from the floor).
        const float BoardThick = 0.012f, Face = BoardThick * 0.5f + 0.002f; // marks 2 mm off the face
        const float TorsoW = 0.46f, TorsoH = 0.70f, TorsoY = 1.13f, HeadW = 0.21f, HeadH = 0.28f, HeadY = 1.62f;
        const float PostZ = -(BoardThick * 0.5f + 0.0175f);
        // A blade counts from this hand speed (Knife's own floor is 1.2 m/s at the tip; the hand moves a little slower).
        const float KnifeMinSpeed = 1.0f, KnifeRehit = 0.3f;
        // The rock on the post (degrees, °/s): a damped spring, about 2 Hz, settled in half a second.
        const float Stiff = 160f, Damp = 9f, MaxTilt = 6f, RoundKick = 40f, PelletKick = 6f, BladeKick = 25f;

        /// <summary>Rounds (pellets each) that hit it.</summary>
        public int RoundHits { get; private set; }
        /// <summary>Knife strikes that landed on it (hand at ≥ 1 m/s), stabs and slashes alike.</summary>
        public int KnifeHits { get; private set; }
        /// <summary>The last knife strike was a stab (along the blade), not a slash.</summary>
        public bool LastBladeWasStab { get; private set; }
        /// <summary>Called first thing every frame (the briefing's watchdog: a check whose coroutine was stopped cleans up).</summary>
        public System.Action Watchdog;

        /// <summary>
        /// Just above the cardboard head, on the board (world; it rocks with it): what the weapons check's label points at from
        /// above (Scenario/MarshalBriefing, UI/WorldLabel), so the words never cover the printed centre or head.
        /// </summary>
        public Vector3 LabelPoint => (_board != null ? _board : transform).TransformPoint(new Vector3(0f, HeadY + HeadH * 0.5f + 0.03f, 0f));
        /// <summary>The printed centre-mass zone on the face (world): where a round should go (tests aim here).</summary>
        public Vector3 ChestPoint => (_board != null ? _board : transform).TransformPoint(new Vector3(0f, 1.20f, Face));

        Transform _board;
        BoxCollider _torsoBox, _headBox;
        readonly Transform[] _marks = new Transform[MaxMarks];
        int _nextMark, _soundFrame = -1, _hapticFrame = -1;
        float _tilt, _tiltVel, _lastBlade = -10f;
        Material _card, _dark, _torn;

        /// <summary>Ricky (the handover: he's in his spot during the check) closer than this (flat, m): the target steps away from him.</summary>
        const float AvoidRadius = 1.0f;
        /// <summary>
        /// Deputy Ortiz (Brett 0.1.57: he stands inside by the front door through the check) within this angle (°, flat, at the
        /// player's eye) of the line to the board — in front of it or behind it, as the player sees it — or within
        /// <see cref="AvoidRadius"/> of it: the board steps away from him (<see cref="Crowds"/>).
        /// </summary>
        public const float KeepClearAngle = 25f;

        /// <summary>
        /// Stand a target <see cref="InsideDoor"/> m inside <paramref name="door"/>, on the floor, its face toward
        /// <paramref name="face"/> (the player's head) but never more than 60° off straight into the room; stepped 0.6 m
        /// along the wall if the player is standing on the spot, 0.7 m away from <paramref name="avoid"/> (Ricky, crouched
        /// in his spot through the check) if it would stand within 1 m of him, and 0.7 m along the wall away from
        /// <paramref name="keepClear"/> (Ortiz, standing by the door: Brett 0.1.57) if he'd crowd it (<see cref="Crowds"/>).
        /// Null when there's no door. Carved out of the room NavMesh (Ortiz walks out round it at step 9, never through it).
        /// </summary>
        public static PracticeTarget Spawn(VirtualDoor door, Vector3 face, Vector3? avoid = null, Vector3? keepClear = null)
        {
            var planned = PlanSpot(door, face, avoid, keepClear, out var note);
            if (!planned.HasValue) return null;
            var spot = planned.Value;
            if (note != null) Debug.Log($"[HIS] Practice target: {note}");
            var dir = FaceDir(door, spot, face);
            var go = new GameObject("[HIS] PracticeTarget");
            go.transform.SetPositionAndRotation(new Vector3(spot.x, FloorY(door), spot.z), Quaternion.LookRotation(dir, Vector3.up));
            var t = go.AddComponent<PracticeTarget>();
            t.Build();
            float inside = Vector3.Dot(spot - Flat(door.Threshold), Flat(door.Inward).normalized);
            Debug.Log($"[HIS] Practice target up {inside:F1} m inside the door at {go.transform.position:F2}, facing {dir:F2}, " +
                      $"{LeafGap(door, spot, face):F2} m clear of the door's swing");
            return t;
        }

        /// <summary>
        /// Where <see cref="Spawn"/> would stand the board (flat: y = 0), and why it stepped (null: it didn't). The handover's
        /// stand-spot finder (Intruder.HandoverStandSpot) asks it for each spot it tries, so Ortiz is never placed where the
        /// board would then step onto Ricky or back into his line. Null when there's no door.
        /// </summary>
        public static Vector3? PlanSpot(VirtualDoor door, Vector3 face, Vector3? avoid, Vector3? keepClear, out string note)
        {
            note = null;
            if (door == null) return null;
            var inward = Flat(door.Inward);
            if (inward.sqrMagnitude < 1e-4f) return null;
            inward.Normalize();
            var along = Flat(door.HingeToLatch).normalized;
            var spot = Flat(door.Threshold) + inward * InsideDoor;
            var head = Flat(face);
            if ((head - spot).sqrMagnitude < 0.5f * 0.5f) spot += along * (Vector3.Dot(head - spot, along) > 0f ? -0.6f : 0.6f);
            if (avoid.HasValue && (Flat(avoid.Value) - spot).sqrMagnitude < AvoidRadius * AvoidRadius)
            {
                spot += along * (Vector3.Dot(Flat(avoid.Value) - spot, along) > 0f ? -0.7f : 0.7f);
                note = "stepped along the wall, away from Ricky";
            }
            if (keepClear.HasValue && Crowds(spot, face, keepClear.Value))
            {
                spot += along * (Vector3.Dot(Flat(keepClear.Value) - spot, along) > 0f ? -0.7f : 0.7f);
                note = (note != null ? note + "; " : "") + "stepped along the wall, clear of Ortiz";
            }
            // Clear of the leaf's whole swing by a walkway (the integrator, 0.1.58: the board 1.1 m in stood ~0.2 m off a 0.94 m
            // leaf's sweep in Brett's room; with the leaf at 80° their two NavMesh holes, each the thing plus the agent's 0.3 m,
            // met and boxed the doorway link's room end in — Ortiz, opening the door himself at step 9, stood stuck 5 s and was
            // gone where he stood). Deeper into the room, a few cm at a time, until the board's face is LeafWalkway clear of it.
            // (Never out through the room's far side: a small room keeps it where the outline ends — Ortiz's walk out has its own
            // way past it then, Intruder.HandoverWalkOut.)
            var geo = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
            float deeper = 0f;
            // Deeper in can bring it back within AvoidRadius of Ricky, or onto Ortiz's line from the eye (the fixer, 0.1.58: a
            // hiding spot 1.9 m in, 0.2 m off the door's centre, put the board 0.9 m from him after the 0.7 m step away and the
            // walk deeper). So the two steps along the wall are checked again after it, and the swing again after a step — a
            // few passes at most — and Spawn stands it where HandoverStandSpot's Fits planned it (no spot thrown out for it).
            bool stepped = false;
            for (int pass = 0; pass < 3; pass++)
            {
                Deepen();
                stepped = false;
                if (deeper <= 0f) break;                               // (never moved deeper: the steps above stand as they were)
                if (avoid.HasValue && (Flat(avoid.Value) - spot).sqrMagnitude < AvoidRadius * AvoidRadius)
                {
                    spot += along * (Vector3.Dot(Flat(avoid.Value) - spot, along) > 0f ? -0.7f : 0.7f);
                    note = (note != null ? note + "; " : "") + "along the wall again, away from Ricky";
                    stepped = true;
                }
                else if (keepClear.HasValue && Crowds(spot, face, keepClear.Value))
                {
                    spot += along * (Vector3.Dot(Flat(keepClear.Value) - spot, along) > 0f ? -0.7f : 0.7f);
                    note = (note != null ? note + "; " : "") + "along the wall again, clear of Ortiz";
                    stepped = true;
                }
                else break;
            }
            if (stepped) Deepen();                                     // (the last pass stepped: clear of the swing once more)
            if (deeper > 0f)
            {
                if ((head - spot).sqrMagnitude < 0.5f * 0.5f) spot += along * (Vector3.Dot(head - spot, along) > 0f ? -0.6f : 0.6f);
                note = (note != null ? note + "; " : "") + $"{deeper:F2} m deeper into the room, clear of the door's swing by {LeafGap(door, spot, face):F2} m";
            }
            return spot;

            // Into the room a few cm at a time until the board's face is LeafWalkway clear of the swing (MaxDeeper in all).
            void Deepen()
            {
                while (deeper < MaxDeeper && LeafGap(door, spot, face) < LeafWalkway)
                {
                    var next = spot + inward * 0.05f;
                    if (geo != null && geo.Walls.Count > 0 && !geo.Inside(new Vector2(next.x, next.z))) break;
                    spot = next; deeper += 0.05f;
                }
            }
        }

        /// <summary>The board's face this clear (flat, m) of the floor the leaf sweeps up to its stop: a man's width between the
        /// two NavMesh holes (each the thing plus the agent's 0.3 m), so the doorway stays walkable with the door swung wide.</summary>
        public const float LeafWalkway = 0.75f;
        /// <summary>The most <see cref="PlanSpot"/> moves the board deeper than <see cref="InsideDoor"/> for it (m).</summary>
        const float MaxDeeper = 1.0f;

        /// <summary>
        /// How far the board standing at <paramref name="spot"/> (facing <paramref name="face"/> as <see cref="Spawn"/> turns it)
        /// is from the floor <paramref name="door"/>'s leaf sweeps (Intruder.SweepGap: its centre and both edges, the nearest).
        /// </summary>
        static float LeafGap(VirtualDoor door, Vector3 spot, Vector3 face)
        {
            var dir = FaceDir(door, spot, face);
            var side = Vector3.Cross(Vector3.up, dir).normalized * (TorsoW * 0.5f + 0.02f);
            float gap = HIS.Intruder.Intruder.SweepGap(door, spot);
            gap = Mathf.Min(gap, HIS.Intruder.Intruder.SweepGap(door, spot + side));
            return Mathf.Min(gap, HIS.Intruder.Intruder.SweepGap(door, spot - side));
        }

        /// <summary>The board's facing at <paramref name="spot"/>: toward <paramref name="face"/>, never more than 60° off straight into the room.</summary>
        static Vector3 FaceDir(VirtualDoor door, Vector3 spot, Vector3 face)
        {
            var inward = Flat(door.Inward).normalized;
            var dir = Flat(face) - Flat(spot);
            return dir.sqrMagnitude < 1e-3f ? inward : Vector3.RotateTowards(inward, dir.normalized, 60f * Mathf.Deg2Rad, 0f);
        }

        /// <summary>
        /// A man at <paramref name="man"/> crowds a board at <paramref name="spot"/> for a player whose eye is at
        /// <paramref name="eye"/>: within <see cref="AvoidRadius"/> of it, or within <see cref="KeepClearAngle"/> of the line from
        /// the eye to it (flat) — between the player and the board, or behind it as the player sees it.
        /// </summary>
        public static bool Crowds(Vector3 spot, Vector3 eye, Vector3 man)
        {
            spot = Flat(spot); eye = Flat(eye); man = Flat(man);
            if ((man - spot).sqrMagnitude < AvoidRadius * AvoidRadius) return true;
            var toBoard = spot - eye; var toMan = man - eye;
            if (toBoard.sqrMagnitude < 1e-4f || toMan.sqrMagnitude < 1e-4f) return true;
            return Vector3.Angle(toBoard, toMan) < KeepClearAngle;
        }

        /// <summary>Take it down (the check is over).</summary>
        public void Remove() { if (this != null) Destroy(gameObject); }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        static float FloorY(VirtualDoor d)
        {
            var geo = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
            return geo != null && geo.Walls.Count > 0 ? geo.FloorY : d.Threshold.y;
        }

        // ───────────────────────────── the build ─────────────────────────────

        void Build()
        {
            _card = Mat(new Color(0.66f, 0.52f, 0.36f), "Practice cardboard");
            _dark = Mat(new Color(0.08f, 0.075f, 0.07f), "Practice print");
            _torn = Mat(new Color(0.80f, 0.70f, 0.54f), "Practice torn");
            // The board (and its post) rock about the floor line; the foot stays put.
            _board = new GameObject("Board").transform;
            _board.SetParent(transform, false);
            Part("Foot", transform, new Vector3(0f, 0.0125f, PostZ), new Vector3(0.40f, 0.025f, 0.30f), _dark);
            float postTop = TorsoY - TorsoH * 0.5f;
            Part("Post", _board, new Vector3(0f, (0.025f + postTop) * 0.5f, PostZ), new Vector3(0.035f, postTop - 0.025f, 0.035f), _dark);
            Part("Torso", _board, new Vector3(0f, TorsoY, 0f), new Vector3(TorsoW, TorsoH, BoardThick), _card);
            Part("Head", _board, new Vector3(0f, HeadY, 0f), new Vector3(HeadW, HeadH, BoardThick), _card);
            // The printed zones on the face (centre mass, the head), a millimetre proud of it.
            Part("ChestPrint", _board, new Vector3(0f, 1.20f, BoardThick * 0.5f + 0.001f), new Vector3(0.16f, 0.22f, 0.002f), _dark);
            Part("HeadPrint", _board, new Vector3(0f, HeadY + 0.01f, BoardThick * 0.5f + 0.001f), new Vector3(0.09f, 0.09f, 0.002f), _dark);
            // What rounds and the blade meet: two boxes on the root (the visuals carry none), thicker than the board so a
            // blade's trigger can't slip through it between physics steps. Default layer: the player's rounds hit it.
            _torsoBox = Box(new Vector3(0f, TorsoY, 0f), new Vector3(TorsoW, TorsoH, 0.03f));
            _headBox = Box(new Vector3(0f, HeadY, 0f), new Vector3(HeadW, HeadH, 0.03f));
            // Brett 0.1.57: Deputy Ortiz stands in the room through the check and walks out at step 9 — round the board, never
            // through it (as Ricky's carve). Thin (the board itself, a few cm proud): the hole the NavMesh carves is this plus the
            // agent's 0.3 m all round, so the doorway link's nearest room ends (0.55 m in, DoorwayNav.Reconnect) stay open
            // whichever way the board faces. On a child: the root's colliders stay as they are. Gone with the board.
            var carve = new GameObject("Carve");
            carve.transform.SetParent(transform, false);
            var ob = carve.AddComponent<NavMeshObstacle>();
            ob.shape = NavMeshObstacleShape.Box; ob.center = new Vector3(0f, 0.9f, 0f); ob.size = new Vector3(TorsoW + 0.04f, 1.8f, 0.06f);
            ob.carving = true; ob.carveOnlyStationary = true;
            _carve = ob;
        }

        NavMeshObstacle _carve;

        /// <summary>Its hole in the room NavMesh is in (<see cref="StopCarving"/> takes it out).</summary>
        public bool Carving => _carve != null && _carve.enabled;

        /// <summary>
        /// Its hole out of the room NavMesh (the last resort: Ortiz boxed in between it and the swung leaf, Intruder.HandoverWalkOut —
        /// better past its edge than gone where he stands). The board stays up.
        /// </summary>
        public void StopCarving() { if (_carve != null) _carve.enabled = false; }

        static Material Mat(Color c, string name)
        {
            // The props' look (IntruderApproach.PropMaterial): lit, depth-occluded, Meta's 0.06 bias.
            var sh = Shader.Find("HIS/OcclusionLitPBR") ?? Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(sh) { color = c, name = name + " (runtime)" };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.1f);
            if (m.HasProperty("_EnvironmentDepthBias")) m.SetFloat("_EnvironmentDepthBias", 0.06f);
            return m;
        }

        static void Part(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            NoShadows(go.GetComponent<Renderer>(), mat);
        }

        BoxCollider Box(Vector3 centre, Vector3 size)
        {
            var b = gameObject.AddComponent<BoxCollider>();
            b.center = centre; b.size = size;
            return b;
        }

        static void NoShadows(Renderer r, Material mat)
        {
            if (r == null) return;
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        void OnDestroy()
        {
            if (_card != null) Destroy(_card);
            if (_dark != null) Destroy(_dark);
            if (_torn != null) Destroy(_torn);
        }

        // ───────────────────────────── hits ─────────────────────────────

        /// <summary>A round (Firearm: <see cref="IInanimate"/>, so no flesh sound): a hole, a thud, the hit pulse, a rock back.</summary>
        public float ApplyDamage(in DamageInfo info)
        {
            if (info.Type != DamageType.Bullet) return 0f;   // (fists pass IInanimate by; the blade is read in OnTrigger*)
            RoundHits++;
            bool pellet = info.Amount < 20f;                 // a 13-point shotgun pellet, not a 34-point pistol round
            var l = transform.InverseTransformPoint(info.Point);
            bool front = Vector3.Dot(info.Direction, transform.forward) < 0f; // from the printed side (the player's)
            Mark(l, front, pellet ? new Vector3(0.006f, 0.006f, 1f) : new Vector3(0.009f, 0.009f, 1f), Random.Range(0f, 360f));
            Kick((front ? -1f : 1f) * (pellet ? PelletKick : RoundKick));
            if (_soundFrame != Time.frameCount)              // one thud per shot, not per pellet
            {
                _soundFrame = Time.frameCount;
                int h = Sfx.Play("impact.wall", info.Point, pellet ? 0.5f : 0.6f);
                if (h != 0) Sfx.ScalePitch(h, 1.35f);       // lighter than plaster: cardboard
            }
            // The same confirm a man gives (Firearm's IInanimate branch skips it): a second, delayed pulse, once per shot.
            if (_hapticFrame != Time.frameCount && info.Source != null && info.Source.TryGetComponent<Firearm>(out var gun) && gun.Hand != null)
            {
                _hapticFrame = Time.frameCount;
                gun.Hand.Invoke(nameof(HandController.HapticLight), 0.06f);
            }
            return 0f;
        }

        void OnTriggerEnter(Collider other) => Blade(other);
        void OnTriggerStay(Collider other) => Blade(other);

        /// <summary>A held knife's blade (a trigger) meeting the board at speed: a slit, a softer thud, a pulse, a rock back.</summary>
        void Blade(Collider other)
        {
            if (Time.time - _lastBlade < KnifeRehit || other == null) return;
            var knife = other.GetComponentInParent<Knife>();
            if (knife == null || !knife.IsHeld) return;
            var hand = knife.Hand;
            var v = hand != null ? hand.Velocity : Vector3.zero;
            float speed = v.magnitude;
            if (speed < KnifeMinSpeed) return;
            _lastBlade = Time.time;
            KnifeHits++;
            var dir = v / speed;
            // Knife's own rule: along the hand's thrust line = a stab, across it = a slash.
            LastBladeWasStab = Vector3.Dot(dir, knife.Grabbable.GripPoint.forward) > 0.6f;
            var c = other.bounds.center;
            var p = _torsoBox.ClosestPoint(c); var q = _headBox.ClosestPoint(c);
            if ((q - c).sqrMagnitude < (p - c).sqrMagnitude) p = q;
            var l = transform.InverseTransformPoint(p);
            bool front = Vector3.Dot(dir, transform.forward) < 0f;
            // The slit runs along the blade's edge for a stab, along the swing for a slash (on the board's plane).
            var along = transform.InverseTransformDirection(LastBladeWasStab ? knife.transform.up : dir);
            float roll = Mathf.Atan2(along.y, along.x) * Mathf.Rad2Deg;
            Mark(l, front, new Vector3(0.024f, 0.004f, 1f), front ? -roll : roll);
            Kick((front ? -1f : 1f) * BladeKick);
            int h = Sfx.Play("impact.wall", p, 0.45f);
            if (h != 0) Sfx.ScalePitch(h, 1.6f);
            if (hand != null) hand.Haptic(LastBladeWasStab ? 0.7f : 0.45f, 0.08f);
            Debug.Log($"[HIS] Practice target: knife {(LastBladeWasStab ? "stab" : "slash")} at {speed:F1} m/s");
        }

        /// <summary>
        /// A hole (or a slit: a squashed hole) on the face the hit came from, at <paramref name="local"/> in the target's frame.
        /// It rides the board, so it rocks with it.
        /// </summary>
        void Mark(Vector3 local, bool front, Vector3 scale, float roll)
        {
            var mesh = VirtualDoor.BulletHoleMesh;
            if (mesh == null || _board == null) return;
            var t = _marks[_nextMark];
            if (t == null)
            {
                var go = new GameObject("Mark");
                go.transform.SetParent(_board, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = new[] { _torn, _dark };  // the torn ring round a dark core
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                t = _marks[_nextMark] = go.transform;
            }
            _nextMark = (_nextMark + 1) % MaxMarks;
            local.z = front ? Face : -Face;
            var n = front ? Vector3.forward : Vector3.back;
            t.localPosition = local;
            t.localRotation = Quaternion.LookRotation(-n, Vector3.up) * Quaternion.Euler(0f, 0f, roll); // the mesh's front (−Z) out of the face
            t.localScale = scale;
            t.gameObject.SetActive(true);
        }

        void Kick(float degreesPerSecond) => _tiltVel = Mathf.Clamp(_tiltVel + degreesPerSecond, -120f, 120f);

        void Update()
        {
            Watchdog?.Invoke();
            if (_board == null || (_tilt == 0f && _tiltVel == 0f)) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;                            // paused
            _tiltVel += (-Stiff * _tilt - Damp * _tiltVel) * dt;
            _tilt = Mathf.Clamp(_tilt + _tiltVel * dt, -MaxTilt, MaxTilt);
            if (Mathf.Abs(_tilt) < 0.02f && Mathf.Abs(_tiltVel) < 0.3f) _tilt = _tiltVel = 0f;
            _board.localRotation = Quaternion.Euler(_tilt, 0f, 0f); // + leans the top toward the face side, − away from it
        }
    }
}
