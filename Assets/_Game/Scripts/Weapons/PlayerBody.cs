using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// A body frame estimated from the head (no body tracking — IOBT drops to 3-point inference under passthrough anyway).
    /// What shipped VR shooters converge on (docs/research/15, Brett 2026-09-23 "still a little off"):
    ///  • hang the body from a NECK pivot, not the eyes — looking down no longer drags the belt forward and down;
    ///  • torso yaw is CLAMPED (VRIK style): the head can look ±30° freely, beyond that the body turns only by the excess —
    ///    the head's own motion, 1:1, so even a fast turn-around never leaves the body behind;
    ///    after a real turn it settles back under the head once the head is calm; nothing ever drifts;
    ///  • yaw FREEZES while you look down (at your holsters) or reach for a slot — the slot doesn't run from your hand —
    ///    but only briefly, only from a squared body, and never past <see cref="maxYawOff"/> (0.1.16: frozen locks left the
    ///    body up to 180° off after a turn: the slung shotgun on the chest, the hip slots mirrored);
    ///  • belt height from the player's own standing eye height (auto-captured, or set in the pause menu: PlayerHeight),
    ///    following crouches 1:1 past a deadband.
    /// Hip slots hang from <see cref="Waist"/>; the shotgun sling from <see cref="Chest"/> so belt height never moves it.
    /// </summary>
    public class PlayerBody : MonoBehaviour
    {
        [SerializeField] Transform head;
        [Tooltip("Head can look this far left/right before the body turns (by the excess only).")]
        [SerializeField] float yawClamp = 30f;
        [Tooltip("Hard limit, locks included: the body never faces further than this from the head. The sling sits ~139° round " +
                 "from the facing, so below ~49° it can never swing in front of the chest.")]
        [SerializeField] float maxYawOff = 45f;
        [Tooltip("How fast a lock's leftover offset (past the clamp) is taken back, so a release never pops. Head motion itself " +
                 "is followed 1:1 at the clamp, not at this rate.")]
        [SerializeField] float catchUpDegPerSec = 150f;
        [Tooltip("Reach lock: a hand this close to a docked weapon's grab zone (empty hand), or holding a weapon this close to a " +
                 "free slot that takes it, holds the body still.")]
        [SerializeField] float reachLockRadius = 0.2f;
        const float ReachLockMax = 1.5f, ReachRearmClear = 0.5f, LookDownEscapeAfter = 2.5f;
        [Tooltip("Eye → pistol-grip height as a fraction of standing eye height (0.31 ≈ 0.51 m at 1.64 m).")]
        [SerializeField] float gripBelowEyeRatio = 0.31f;

        public Transform Waist { get; private set; }
        public Transform Chest { get; private set; }
        public Transform Back { get; private set; }
        public Holster RightHip { get; private set; }
        public Holster LeftHip { get; private set; }
        public Holster BackSlot { get; private set; }

        /// <summary>Oculus default head-neck model: the eyes sit 7.5 cm above and 8 cm in front of the neck pivot.</summary>
        static readonly Vector3 NeckToEye = new(0f, 0.075f, 0.0805f);

        float _bodyYaw, _lastFaceYaw, _standingEye = 1.64f, _measuredEye = 1.64f, _captured, _captureUntil = -1f, _levelFor, _steady, _reachLockFor, _lookDownFor;
        float _reachClearFor = ReachRearmClear; // hands clear of every slot this long: a reach lock may start
        bool _init, _lookDown, _lookDownEscaped, _settling, _capturing;
        Quaternion _lastRot = Quaternion.identity;
        int _tickFrame = -1;
        OVRCameraRig _rig;
        HandController[] _hands;

        void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;
            Waist = new GameObject("Waist").transform; Waist.SetParent(transform, false);
            Chest = new GameObject("Chest").transform; Chest.SetParent(transform, false);
            // Slung behind the back: the grip sits behind the chest, the barrel runs down toward the left hip and the stock
            // ends at the top of the right shoulder — reach up to the shoulder (where the headset still sees the hand) and grab.
            Back = new GameObject("Back").transform; Back.SetParent(Chest, false);
            Back.localPosition = new Vector3(0.07f, -0.345f, -0.08f);
            Back.localRotation = Quaternion.identity;

            // Hip slots on the neck line (not 4 cm ahead of the eyes), at the grip line. Spacing is NOT scaled with height.
            RightHip = MakeHolster("Holster_RightHip", Waist, new Vector3(0.21f, 0f, 0f), Quaternion.identity, HolsterType.Hip);
            LeftHip = MakeHolster("Holster_LeftHip", Waist, new Vector3(-0.21f, 0f, 0f), Quaternion.identity, HolsterType.Hip);
            var muzzleDir = new Vector3(-0.3f, -0.9f, -0.2f).normalized;             // barrel: down, a little left and back
            var gunTop = Vector3.ProjectOnPlane(Vector3.back, muzzleDir).normalized; // top of the gun faces away from the body
            BackSlot = MakeHolster("Holster_Back", Back, Vector3.zero, Quaternion.LookRotation(muzzleDir, gunTop), HolsterType.Back);
            BackSlot.DockRadius = 0.45f; // releasing it anywhere over the right shoulder re-slings it
        }

        void Start()
        {
            _rig = GetComponent<OVRCameraRig>();
            if (_rig != null) _rig.UpdatedAnchors += OnAnchors;
            if (OVRManager.display != null) OVRManager.display.RecenteredPose += SnapYaw;
            OVRManager.HMDMounted += OnMounted;
            _hands = FindObjectsByType<HandController>(FindObjectsInactive.Exclude);
            ArmCapture();
        }

        void OnDestroy()
        {
            if (_rig != null) _rig.UpdatedAnchors -= OnAnchors;
            if (OVRManager.display != null) OVRManager.display.RecenteredPose -= SnapYaw;
            OVRManager.HMDMounted -= OnMounted;
        }

        // Both events fire from OVRManager.Update (order -100), before OVRCameraRig (-50) moves the anchors this frame:
        // head.rotation is still last frame's (pre-sleep) pose, so snap on the next Tick, which runs right after the anchor update.
        void SnapYaw() => _init = false;
        void OnMounted() { SnapYaw(); ArmCapture(); } // someone else may be wearing it
        void ArmCapture() { _capturing = true; _captured = 0f; _captureUntil = -1f; _levelFor = 0f; }

        /// <summary>
        /// Measure the standing eye height again, exactly as when the headset goes on (the Marshal weapons check's "Stand up
        /// straight.", research/26 §2: a tester who started seated gets the belt and holsters at his real hips).
        /// <see cref="Measuring"/> until it's in.
        /// </summary>
        public void Remeasure() => ArmCapture();
        /// <summary>The standing eye height is being measured (start-up, the headset put on, <see cref="Remeasure"/>).</summary>
        public bool Measuring => _capturing;

        Holster MakeHolster(string name, Transform parent, Vector3 pos, Quaternion rot, HolsterType type)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localRotation = rot;
            var h = go.AddComponent<Holster>(); h.Type = type; return h;
        }

        // UpdatedAnchors fires in Update AND onBeforeRender: advance state once per frame, re-place from the latest pose each time.
        void OnAnchors(OVRCameraRig _)
        {
            if (head == null) return;
            if (Time.frameCount != _tickFrame) { _tickFrame = Time.frameCount; Tick(Time.unscaledDeltaTime); }
            ApplyPose();
        }

        // Fallback when there's no OVRCameraRig, or XR isn't up (headless tests, Editor Play without XR): UpdateAnchors bails
        // before raising UpdatedAnchors then. The frame guard in OnAnchors keeps it to one Tick per frame.
        void LateUpdate() { if ((_rig == null || !OVRManager.OVRManagerinitialized) && head != null) OnAnchors(null); }

        static float HeadingYaw(Quaternion r)
        {
            // VRIK heading: stays stable when looking straight down (forward alone flips when pitch passes -90).
            var face = Vector3.Cross(r * Vector3.right, Vector3.up) + r * Vector3.forward;
            face.y = 0f;
            return face.sqrMagnitude < 1e-6f ? r.eulerAngles.y : Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
        }

        void Tick(float dt)
        {
            var r = head.rotation;
            var fwd = r * Vector3.forward;
            float faceYaw = HeadingYaw(r);
            float pitchDown = Mathf.Asin(Mathf.Clamp(-fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            float angSpeed = dt > 1e-4f ? Quaternion.Angle(_lastRot, r) / dt : 0f;
            _lastRot = r;
            // No head turns 45° in one frame (3000°/s at 72 Hz): that's the tracking space moving (a recenter whose event we
            // missed, tracking regained after a turn) — square the body under the head, as SnapYaw would.
            if (_init && Mathf.Abs(Mathf.DeltaAngle(_lastFaceYaw, faceYaw)) > 45f)
            {
                if (Debug.isDebugBuild) Debug.Log($"[HIS] Body head yaw jumped {Mathf.DeltaAngle(_lastFaceYaw, faceYaw):F0}° in one frame: re-squared");
                _init = false;
            }
            if (!_init)
            {
                _bodyYaw = _lastFaceYaw = faceYaw; _init = true; _settling = false; _steady = 0f;
                _reachLockFor = 0f; _reachClearFor = ReachRearmClear; _lookDownEscaped = false;
            }
            float headStep = Mathf.DeltaAngle(_lastFaceYaw, faceYaw); // how far the head turned this frame
            _lastFaceYaw = faceYaw;

            bool wasDown = _lookDown;
            _lookDown = _lookDown ? pitchDown > 20f : pitchDown > 30f;          // hysteresis: no flicker around -30°
            bool calm = angSpeed < 30f && Mathf.Abs(pitchDown) < 20f;
            float d = Mathf.DeltaAngle(_bodyYaw, faceYaw), absD = Mathf.Abs(d);
            bool squared = absD <= yawClamp + 0.5f; // the body is plausibly under the torso: a lock may start
            bool far = absD >= maxYawOff;           // the head has turned away: every lock gives way
            // Look-down lock, released for good (until the player looks up) when: it would start from a body that is already off,
            // the head turns to the hard limit, or — eyes on something low (a prone suspect) while turning around it isn't
            // looking at the belt — the head is past the clamp after 2.5 s of looking down.
            _lookDownFor = _lookDown ? _lookDownFor + dt : 0f;
            if (!_lookDown) _lookDownEscaped = false;
            else if ((!wasDown && !squared) || far || (_lookDownFor > LookDownEscapeAfter && absD > yawClamp)) _lookDownEscaped = true;
            bool lookLock = _lookDown && !_lookDownEscaped;
            bool reachLock = ReachLock(dt, squared, far); // every tick: its timers never go stale under the look-down lock
            if (!lookLock && !reachLock)
            {
                float excess = absD - yawClamp;
                // VRIK clamp: past it the body turns by the excess — whatever the head turned this frame, 1:1, so a fast turn-around
                // keeps it at the clamp. Only a lock's leftover (past the clamp) is rate-limited, so a release never pops.
                if (excess > 0f) _bodyYaw += Mathf.Sign(d) * Mathf.Min(excess, Mathf.Max(0f, Mathf.Sign(d) * headStep) + catchUpDegPerSec * dt);
                // After a real turn, settle back under the head — only once the head has been calm for a moment.
                _steady = calm && absD > 20f ? _steady + dt : 0f;
                if (_steady > 0.75f) _settling = true;
                if (_settling)
                {
                    _bodyYaw = Mathf.LerpAngle(_bodyYaw, faceYaw, 1f - Mathf.Exp(-dt / 0.5f));
                    if (Mathf.Abs(Mathf.DeltaAngle(_bodyYaw, faceYaw)) < 3f || !calm) _settling = false;
                }
            }
            // Hard limit, locks included: the body never faces away from the head (a tracking jump, a recenter with no event).
            d = Mathf.DeltaAngle(_bodyYaw, faceYaw);
            if (Mathf.Abs(d) > maxYawOff) _bodyYaw = faceYaw - Mathf.Sign(d) * maxYawOff;
            _bodyYaw = Mathf.Repeat(_bodyYaw, 360f);
            UpdateStandingEye(calm, pitchDown, angSpeed, dt);
        }

        /// <summary>
        /// Reach lock: a hand about to draw (empty, at a docked weapon's grab zone) or holster (holding a weapon at a free slot that
        /// takes it) holds the body still, so the slot doesn't run from the hand. Bounded: it only starts from a squared body, lasts
        /// at most 1.5 s, gives way when the head turns to the hard limit, and re-arms only after the hands have been clear of every
        /// slot for 0.5 s — a gun held low by the hip gets one short hold, not an endless chain of them.
        /// </summary>
        bool ReachLock(float dt, bool squared, bool far)
        {
            bool near = HandAtSlot();
            if (_reachLockFor > 0f)
            {
                _reachLockFor += dt;
                if (near && (far || _reachLockFor > ReachLockMax) && Debug.isDebugBuild) // gave way with a hand still at a slot
                    Debug.Log($"[HIS] Body reach lock gave way after {_reachLockFor:F1} s ({(far ? $"head {maxYawOff:F0}° off" : "time")})");
                if (!near || far || _reachLockFor > ReachLockMax) _reachLockFor = 0f;
            }
            else if (near && squared && !far && _reachClearFor >= ReachRearmClear) _reachLockFor = dt;
            _reachClearFor = near ? 0f : _reachClearFor + dt;
            return _reachLockFor > 0f;
        }

        bool HandAtSlot()
        {
            if (_hands == null) return false;
            foreach (var h in _hands)
            {
                if (h == null || h.Supporting != null) continue; // steadying the other hand's gun is not a reach
                var held = h.Held;
                if (held == null)
                {
                    var p = h.Anchor.position;
                    if (CanDraw(RightHip, p) || CanDraw(LeftHip, p) || CanDraw(BackSlot, p)) return true;
                }
                else if (CanHolster(RightHip, held) || CanHolster(LeftHip, held) || CanHolster(BackSlot, held)) return true;
            }
            return false;
        }

        // Measured to the weapon's grab zone (the shotgun's runs up to the right shoulder), not the slot origin.
        bool CanDraw(Holster s, Vector3 hand) => s.Occupant != null && !s.Occupant.IsHeld && s.Occupant.GrabDistance(hand) < reachLockRadius;
        bool CanHolster(Holster s, Grabbable g) =>
            s.Accepts(g) && (g.GripPoint.position - s.transform.position).sqrMagnitude < reachLockRadius * reachLockRadius;

        /// <summary>The standing eye height the body hangs from (m): the set height (<see cref="PlayerHeight"/>) or the measured one.</summary>
        public float StandingEye => PlayerHeight.Auto ? _measuredEye : PlayerHeight.EyeHeight;
        /// <summary>The measured standing eye height (m), kept up to date even while a height is set (the pause menu shows it).</summary>
        public float MeasuredEye => _measuredEye;

        void UpdateStandingEye(bool calm, float pitchDown, float angSpeed, float dt)
        {
            float eye = head.localPosition.y; // tracking origin is the floor (FloorLevel)
            if (eye >= 0.5f)                  // (below: no tracking yet)
            {
                bool level = Mathf.Abs(pitchDown) < 20f && angSpeed < 60f;
                if (_capturing)
                {
                    // The 1.5 s window opens on the first level sample (not at start/mount) and stays open until 0.5 s of level
                    // samples are in, so looking down at the controllers first can't leave the 1.64 default stuck (rise-only).
                    if (level)
                    {
                        if (_captureUntil < 0f) _captureUntil = Time.unscaledTime + 1.5f;
                        _captured = Mathf.Max(_captured, eye); _measuredEye = _captured; _levelFor += dt;
                    }
                    if (_captureUntil >= 0f && Time.unscaledTime >= _captureUntil && _levelFor >= 0.5f) _capturing = false;
                }
                else if (level && eye > _measuredEye) _measuredEye = Mathf.MoveTowards(_measuredEye, eye, 0.01f * dt); // rise only, 1 cm/s
                _measuredEye = Mathf.Clamp(_measuredEye, 1.2f, 2.0f);
            }
            // A height set in the pause menu wins (Brett: a shorter or taller player; a headset put on sitting down).
            _standingEye = StandingEye;
        }

        void ApplyPose()
        {
            var neck = head.position - head.rotation * NeckToEye;
            var origin = transform.position.y;                         // rig origin = the floor
            float standNeckY = origin + _standingEye - NeckToEye.y;
            float crouch = Mathf.Max(0f, standNeckY - neck.y - 0.08f); // 8 cm deadband for nods/slouch, then 1:1
            float beltY = origin + _standingEye * (1f - gripBelowEyeRatio) - crouch;
            var yaw = Quaternion.Euler(0f, _bodyYaw, 0f);
            Waist.SetPositionAndRotation(new Vector3(neck.x, Mathf.Max(origin + 0.4f, beltY), neck.z), yaw);
            Chest.SetPositionAndRotation(neck, yaw);
        }

        float _nextLog;
        void Update()
        {
            if (head == null) { if (Time.time > _nextLog) { _nextLog = Time.time + 3f; Debug.LogWarning("[HIS] PlayerBody: no head!"); } return; }
            if (Debug.isDebugBuild && Time.unscaledTime > _nextLog)
            {
                _nextLog = Time.unscaledTime + 10f;
                Debug.Log($"[HIS] Body head={head.position:F2} standEye={_standingEye:F2}{(PlayerHeight.Auto ? "" : $" (set {PlayerHeight.Cm} cm, measured {_measuredEye:F2})")} waist={Waist.position:F2} yawOff={Mathf.DeltaAngle(_bodyYaw, HeadingYaw(head.rotation)):F0}°" +
                          $"{(_lookDown ? (_lookDownEscaped ? " lookDown(escaped)" : " lookDown") : "")}{(_reachLockFor > 0f ? " reach" : "")} | R hip {Describe(RightHip)} | L hip {Describe(LeftHip)} | back {Describe(BackSlot)}");
            }
        }

        static string Describe(Holster h) =>
            h.Occupant != null ? $"{h.Occupant.name}@{h.Occupant.transform.position:F2}" : "empty";
    }

    public enum HolsterType { Hip, Back }

    /// <summary>A docking slot on the body. Weapons snap here on release when close enough; grabbing undocks them.</summary>
    public class Holster : MonoBehaviour
    {
        public HolsterType Type;
        public float DockRadius = 0.35f;
        public Grabbable Occupant { get; private set; }
        public bool IsFree => Occupant == null;

        static readonly System.Collections.Generic.List<Holster> _all = new();
        void OnEnable() => _all.Add(this);
        void OnDisable() => _all.Remove(this);

        public bool Accepts(Grabbable g)
        {
            var w = g.GetComponent<WeaponBase>();
            return IsFree && w != null && w.HolsterType == Type;
        }

        public void Dock(Grabbable g)
        {
            if (Occupant != null && Occupant != g) { Debug.LogWarning($"[HIS] {name} is taken by {Occupant.name}; {g.name} not docked"); return; }
            Occupant = g;
            if (g.HomeHolster == null) g.HomeHolster = this;
            var w = g.GetComponent<WeaponBase>();
            g.DockTo(transform, w != null ? w.HolsterOffsetPosition : Vector3.zero, w != null ? Quaternion.Euler(w.HolsterOffsetEuler) : Quaternion.identity);
        }

        public void Undock(Grabbable g) { if (Occupant == g) Occupant = null; }

        /// <summary>A free slot for this weapon — its own home slot first (so the pistol and knife never swap hips).</summary>
        public static Holster FindFree(Grabbable g)
        {
            if (g.HomeHolster != null && g.HomeHolster.Accepts(g)) return g.HomeHolster;
            foreach (var h in _all) if (h.Accepts(g)) return h;
            return null;
        }

        public static Holster FindNearest(Vector3 position, Grabbable g)
        {
            Holster best = null; float bestD = float.MaxValue;
            foreach (var h in _all)
            {
                if (!h.Accepts(g)) continue;
                float d = Vector3.Distance(position, h.transform.position);
                if (d > h.DockRadius) continue;
                if (h == g.HomeHolster) d -= 0.05f; // ties go home (ranking only; the capture radius stays DockRadius)
                if (d < bestD) { best = h; bestD = d; }
            }
            return best;
        }
    }
}
