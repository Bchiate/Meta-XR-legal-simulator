using UnityEngine;
using UnityEngine.AI;

namespace HIS.Intruder
{
    /// <summary>
    /// Drives the Mixamo Animator from the Intruder state machine. Parameters (created by IntruderBuilder):
    /// Speed(float) GaitRate(float) Gait(int) Kick/Attack/Hit/Fall/Die/Act(trig) AttackIndex/HitPart/DeathIndex/FallIndex/ActIndex(int)
    /// Knife/Acting(bool) Aim(float). Layer 1 "Arms" (arms-only mask) holds the hands-on-head pose over the kneel and the flat
    /// prone; layer 2 "Grip" (right fingers) closes his fist round the knife while it's in his hand; layer 3 "PistolGrip" (right
    /// fingers) closes it round the pistol grip; layer 4 "GunArms" (arms mask) holds the pistol two-handed over the Gun gait,
    /// low ready (Aim 0) or aimed (Aim 1). Aiming is finished procedurally (<see cref="LateUpdate"/>): chest then right arm
    /// turned so the prop's Muzzle ray really lands on the target — the judge's "pointing at you" reads that same ray.
    /// Postures are gaits too: setting Gait to Kneel/Prone from standing plays the way down (FallToKnees, then CrouchToProne), and
    /// setting a standing gait from Kneel/Prone/Sit plays the way up (KneelToStand / GetUpFromStomach / GetUpFromBack) — the
    /// state machine chains the clips, the brain only says where he should end up.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class IntruderAnimator : MonoBehaviour
    {
        public static readonly int SpeedId = Animator.StringToHash("Speed");
        public static readonly int GaitId = Animator.StringToHash("Gait");
        public static readonly int GaitRateId = Animator.StringToHash("GaitRate");
        public static readonly int KickId = Animator.StringToHash("Kick");
        public static readonly int AttackId = Animator.StringToHash("Attack");
        public static readonly int AttackIndexId = Animator.StringToHash("AttackIndex");
        public static readonly int HitId = Animator.StringToHash("Hit");
        public static readonly int HitPartId = Animator.StringToHash("HitPart");
        public static readonly int FallId = Animator.StringToHash("Fall");
        public static readonly int DieId = Animator.StringToHash("Die");
        public static readonly int DeathIndexId = Animator.StringToHash("DeathIndex");
        public static readonly int KnifeId = Animator.StringToHash("Knife");
        public static readonly int FallIndexId = Animator.StringToHash("FallIndex");
        public static readonly int ActId = Animator.StringToHash("Act");
        public static readonly int ActIndexId = Animator.StringToHash("ActIndex");
        public static readonly int ActingId = Animator.StringToHash("Acting");
        public static readonly int AimId = Animator.StringToHash("Aim");
        /// <summary>The car-exit clips' playback rate (IntruderBuilder: the ExitCar states' speed parameter).</summary>
        public static readonly int ExitRateId = Animator.StringToHash("ExitRate");

        public enum AttackKind { CrossPunch = 0, Hook = 1, JabCross = 2, Stab = 3, Slash = 4, BayonetStab = 5 }
        /// <summary>Pistol: the gunman's flinch with the gun still up (PistolHit), whatever was hit.</summary>
        public enum HitKind { Head = 0, Gut = 1, Body = 2, Legs = 3, Pistol = 4 }
        /// <summary>0–4 standing deaths; Kneeling / Prone / OnBack start from where he already is (no standing pose first).</summary>
        public enum DeathKind { Front = 0, FrontHeadshot = 1, Backward = 2, Back = 3, Forward = 4, Kneeling = 5, Prone = 6, OnBack = 7 }
        /// <summary>
        /// Normal = fight stance (attackers); Calm = relaxed idle (surrenderer); Limp/Crawl = hurt; Drunk = the neighbour's
        /// sway/stagger/jog; Sneak = the burglar's crouched creep (and his getaway run). Kneel/Prone/Sit are held postures
        /// (on his knees hands on head, face down, on his backside). Gun = the gunman on his feet: pistol low ready at a stand,
        /// the aimed pistol walk, the sprint (GunArms layer on top). Append only: the value is the Animator's Gait int.
        /// </summary>
        /// <summary>Seated / Driving: a rider in a police car's seat (Mixamo Sitting Idle / Driving; Brett 0.1.48: "theyre like kneeling instead of sitting").</summary>
        /// <summary>Crouch / Cower: Ricky, the protectee on a Marshal night (research/27 §3): crouched in his hiding spot
        /// (LowCrouchIdle), then ducked with his arms over his head (DuckHold). Held postures, no walking. (The plan's
        /// "Cower = 10" was taken by Seated: appended here.)</summary>
        public enum Gait { Normal = 0, Limp = 1, Crawl = 2, Calm = 3, Drunk = 4, Sneak = 5, Kneel = 6, Prone = 7, Sit = 8, Gun = 9, Seated = 10, Driving = 11,
                           Crouch = 12, Cower = 13 }
        /// <summary>Falls with their own travel (root motion through the NavMesh): legs shot out, a drunk trip, knocked onto his backside.</summary>
        public enum FallKind { LegsOut = 0, Trip = 1, StumbleBack = 2 }
        /// <summary>In-place gestures over the current gait (IntruderClips.ActLength order).</summary>
        public enum ActKind { TryDoor = 0, Stumble = 1, Hiccup = 2, PointThumb = 3, Shrug = 4, WalkBack = 5, CaughtReact = 6, Rummage = 7, LookAround = 8, LookOverShoulder = 9,
                              TossAway = 10, PistolDraw = 11, PistolLower = 12, PistolGrabFloor = 13 }
        /// <summary>How he holds the pistol: None = whatever the body clip does with his arms (the sprint); Ready = two hands,
        /// muzzle low; Aimed = on the <see cref="AimAt"/> target.</summary>
        public enum GunPose { None, Ready, Aimed }

        /// <summary>Animator state per gait (IntruderBuilder names them), for "has he got there yet".</summary>
        static readonly int[] GaitStates =
        {
            Animator.StringToHash("Base Layer.Locomotion"), Animator.StringToHash("Base Layer.Limp"), Animator.StringToHash("Base Layer.Crawl"),
            Animator.StringToHash("Base Layer.Calm"), Animator.StringToHash("Base Layer.Drunk"), Animator.StringToHash("Base Layer.Sneak"),
            Animator.StringToHash("Base Layer.Kneel"), Animator.StringToHash("Base Layer.Prone"), Animator.StringToHash("Base Layer.Sit"),
            Animator.StringToHash("Base Layer.Gun"), Animator.StringToHash("Base Layer.Seated"), Animator.StringToHash("Base Layer.Driving"),
            Animator.StringToHash("Base Layer.Crouch"), Animator.StringToHash("Base Layer.Cower"),
        };

        static readonly int[] Triggers = { KickId, AttackId, HitId, FallId, DieId, ActId };
        static readonly int LegsOutFallState = Animator.StringToHash("Base Layer.Fall_LegsOut");
        /// <summary>Getting out of a car (Brett 2026-10-05): Mixamo Exiting Car out to his left (the driver's side), and its mirror.</summary>
        static readonly int ExitCarState = Animator.StringToHash("Base Layer.ExitCar"), ExitCarMirrorState = Animator.StringToHash("Base Layer.ExitCarMirror");

        Animator _anim;
        NavMeshAgent _agent;
        float _speedSmooth;
        float? _speedOverride;
        Gait _gait;
        int _triggerFrame = -1;

        IntruderWeapon _knife, _pistol;
        float _grip, _pistolGrip, _gunArms;

        public Transform Hips { get; private set; }
        /// <summary>Where his voice comes from (follows him while he walks or runs).</summary>
        public Transform Head { get; private set; }

        /// <summary>Settled (not blending) in the crawl, face-down or sitting state: he's on the floor.</summary>
        public bool OnFloor => IsIn(Gait.Crawl) || IsIn(Gait.Prone) || IsIn(Gait.Sit);

        /// <summary>Settled in this gait's state (the way down/up is over).</summary>
        public bool IsIn(Gait gait) => _anim.enabled && !_anim.IsInTransition(0) && _anim.GetCurrentAnimatorStateInfo(0).fullPathHash == GaitStates[(int)gait];

        /// <summary>Blending into this gait's state right now (the last stretch of the way down).</summary>
        bool Entering(Gait gait) => _anim.IsInTransition(0) && _anim.GetNextAnimatorStateInfo(0).fullPathHash == GaitStates[(int)gait];

        /// <summary>Drive the locomotion blend while the agent is off (scripted walk through the doorway); null = use the agent.</summary>
        public void SetSpeedOverride(float? metresPerSecond) => _speedOverride = metresPerSecond;

        void Awake()
        {
            _anim = GetComponent<Animator>();
            _agent = GetComponentInParent<NavMeshAgent>();
            // Root motion is "handled by script" (OnAnimatorMove): every clip has its travel baked into the pose except the
            // falls and posture changes (MixamoImportSettings.RootMotionXZ), whose travel goes through the NavMesh.
            _anim.applyRootMotion = true;
            _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Hips = _anim.GetBoneTransform(HumanBodyBones.Hips);
            Head = _anim.GetBoneTransform(HumanBodyBones.Head);
            foreach (var w in GetComponentsInChildren<IntruderWeapon>(true))
                if (w.Kind == HIS.Scenario.WeaponKind.Gun) _pistol = w; else _knife = w;
            _aimRig = new AimRig(_anim);
        }

        void Update()
        {
            float target = _speedOverride ?? (_agent != null && _agent.enabled ? _agent.velocity.magnitude : 0f);
            _speedSmooth = Mathf.Lerp(_speedSmooth, target, 10f * Time.deltaTime);
            _anim.SetFloat(SpeedId, _speedSmooth);
            // The crawl is one cycle played at the rate that matches his speed (0 = lying still).
            _anim.SetFloat(GaitRateId, _gait == Gait.Crawl ? _speedSmooth / IntruderClips.CrawlNatural : 1f);
            // Hands on head only once he's actually kneeling, or lowering himself flat (not while going down or getting up). On
            // the way from his knees to the floor his hands go to the floor, then up behind his head as he lowers flat — straight
            // from the elbows-forward end of CrouchToProne; the flat hold's arms-at-sides would sweep them through the floor.
            if (_anim.layerCount > 1)
            {
                float arms = _handsOnHead && (IsIn(Gait.Kneel) || IsIn(Gait.Prone) || Entering(Gait.Prone)) ? 1f : 0f;
                if (_armsWeight != arms) { _armsWeight = Mathf.MoveTowards(_armsWeight, arms, 2.5f * Time.deltaTime); _anim.SetLayerWeight(1, _armsWeight); }
            }
            // Knife in hand: the Grip layer keeps his right fist closed round the handle (the prop is placed in that fist).
            if (_anim.layerCount > 2)
            {
                float grip = _knife != null && _knife.InHand ? 1f : 0f;
                if (_grip != grip) { _grip = Mathf.MoveTowards(_grip, grip, 8f * Time.deltaTime); _anim.SetLayerWeight(2, _grip); }
            }
            if (_anim.layerCount > 4) UpdateGun();
        }

        // A trigger that can't fire right away (e.g. the target state doesn't allow a transition to itself) stays latched and
        // fires a surprise reaction or kick seconds later. Triggers here are pulses: whatever the Animator didn't consume on
        // the next update is cleared.
        void LateUpdate()
        {
            AimCorrection();
            if (_triggerFrame < 0 || Time.frameCount <= _triggerFrame) return;
            foreach (var id in Triggers) _anim.ResetTrigger(id);
            _triggerFrame = -1;
        }

        void Pulse(int id)
        {
            if (_dead) return; // nothing plays over a death: a latched hit/fall/act would take him off the death clip
            _anim.ResetTrigger(id);
            _anim.SetTrigger(id);
            _triggerFrame = Time.frameCount;
        }

        void OnAnimatorMove()
        {
            // A rider getting out of a car (IntruderCarExit.cs): no NavMesh out on the street — the clip's travel and turn go to
            // his brain, which carries him out of the door along them.
            if (RootMotionSink != null) { RootMotionSink(_anim.deltaPosition, _anim.deltaRotation); return; }
            var d = _anim.deltaPosition; d.y = 0f;
            if (_fallTravel < 1f && InLegsOutFall()) d *= _fallTravel;
            if (d.sqrMagnitude < 1e-8f) return;
            // Not on the doorway link: the agent can't Move there (a fall on the threshold goes down where he stands).
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh && !_agent.isOnOffMeshLink) _agent.Move(d);
        }

        public void Kick() => Pulse(KickId);
        public void ResetKick() => _anim.ResetTrigger(KickId);
        public void Attack(AttackKind kind) { _anim.SetInteger(AttackIndexId, (int)kind); Pulse(AttackId); }
        public void Hit(HitKind kind) { _anim.SetInteger(HitPartId, (int)kind); Pulse(HitId); }
        /// <summary>
        /// Off his feet (root motion through the NavMesh). <paramref name="travel"/>: the share of the legs-out lunge let through —
        /// less with a wall or the player close ahead, 0 = he goes down where he stands (the pose still pitches him forward).
        /// </summary>
        public void Fall(FallKind kind = FallKind.LegsOut, float travel = 1f) { _fallTravel = Mathf.Clamp01(travel); _anim.SetInteger(FallIndexId, (int)kind); Pulse(FallId); }
        float _fallTravel = 1f;
        bool InLegsOutFall() => _anim.GetCurrentAnimatorStateInfo(0).fullPathHash == LegsOutFallState
                                || _anim.IsInTransition(0) && _anim.GetNextAnimatorStateInfo(0).fullPathHash == LegsOutFallState;
        /// <summary>A gesture over the gait; it ends by itself (clip end) or at <see cref="EndAct"/>.</summary>
        public void Act(ActKind kind) { _anim.SetInteger(ActIndexId, (int)kind); _anim.SetBool(ActingId, true); Pulse(ActId); }
        public void EndAct() => _anim.SetBool(ActingId, false);
        /// <summary>
        /// Straight into the death clip (CrossFade), whatever the base layer is doing. 0.1.15 pulsed a "Die" trigger for its
        /// Any State transitions instead — but a transition already running (a hit reaction's 0.08 s blend-in, a one-shot's
        /// blend back to the gait, a gait change) can't be interrupted by Any State (interruption source None), the pulse
        /// was cleared a frame later, and the body slept standing (Brett 0.1.15: "I killed a dude with a knife and he stayed
        /// standing"; renders Ctl_HitThenDie_Pulse vs Ctl_HitThenDie).
        /// </summary>
        public void Die(DeathKind kind)
        {
            _anim.SetInteger(DeathIndexId, (int)kind);
            if (LegacyDieTrigger) { Pulse(DieId); return; }
            _dead = true;
            foreach (var id in Triggers) _anim.ResetTrigger(id);
            _triggerFrame = -1;
            _anim.CrossFadeInFixedTime(DeathState(kind, _gait), DeathBlend(kind, _gait), 0, 0f);
        }

        /// <summary>PoseTests only: the 0.1.15 trigger pulse, to show the lost death still reproduces without the fix.</summary>
        public static bool LegacyDieTrigger;
        bool _dead;

        /// <summary>The base layer is in (or blending into) a death clip.</summary>
        public bool InDeathClip => _anim.enabled && (IsDeath(_anim.GetCurrentAnimatorStateInfo(0).fullPathHash)
                                                     || _anim.IsInTransition(0) && IsDeath(_anim.GetNextAnimatorStateInfo(0).fullPathHash));

        // IntruderBuilder's death states ("Death_<kind>_<clip>"); face down in the Prone gait: only ProneDeath's end (flat).
        static readonly int[] DeathStates =
        {
            Animator.StringToHash("Base Layer.Death_0_DeathFromTheFront"), Animator.StringToHash("Base Layer.Death_1_DeathFromFrontHeadshot"),
            Animator.StringToHash("Base Layer.Death_2_StandingDeathBackward01"), Animator.StringToHash("Base Layer.Death_3_DeathFromTheBack"),
            Animator.StringToHash("Base Layer.Death_4_StandingDeathForward01"), Animator.StringToHash("Base Layer.Death_5_KneelDeathFromKnees"),
            Animator.StringToHash("Base Layer.Death_6_ProneDeath"), Animator.StringToHash("Base Layer.Death_7_OnBackHold"),
        };
        static readonly int ProneDeathFlatState = Animator.StringToHash("Base Layer.Death_6_ProneDeathFlat");

        public static int DeathState(DeathKind kind, Gait gait) => kind == DeathKind.Prone && gait == Gait.Prone ? ProneDeathFlatState : DeathStates[(int)kind];
        /// <summary>The builder's blend-ins: standing 0.1 s, from the knees / the crawl 0.3 s, flat 0.4 s, the slump onto his back 0.8 s.</summary>
        public static float DeathBlend(DeathKind kind, Gait gait) =>
            kind == DeathKind.OnBack ? 0.8f : kind == DeathKind.Prone && gait == Gait.Prone ? 0.4f : kind >= DeathKind.Kneeling ? 0.3f : 0.1f;
        static bool IsDeath(int hash) => hash == ProneDeathFlatState || System.Array.IndexOf(DeathStates, hash) >= 0;
        public void SetGait(Gait gait) { _gait = gait; _anim.SetInteger(GaitId, (int)gait); }
        /// <summary>The gait at once, no blend (a rider placed in his seat: no stand-to-sit on the way in).</summary>
        public void SnapGait(Gait gait) { SetGait(gait); _anim.Play(GaitStates[(int)gait], 0, 0f); }
        public void SetKnife(bool knife) => _anim.SetBool(KnifeId, knife);

        // ── Getting out of a car (Brett 2026-10-05: "have the flow of cops exiting look better"; IntruderCarExit.cs).

        /// <summary>The controller has the car-exit states (IntruderBuilder 2026-10-05). An older one has none: riders pop out as before.</summary>
        public bool HasExitClips => _anim.HasState(0, ExitCarState) && _anim.HasState(0, ExitCarMirrorState);

        /// <summary>While set, root motion goes here instead of the agent (a rider getting out of a car: his brain moves him).</summary>
        public System.Action<Vector3, Quaternion> RootMotionSink { get; set; }

        /// <summary>
        /// Out of his seat: ExitCar (out to his left: the driver's side) or ExitCarMirror (to his right), at <paramref name="rate"/>×,
        /// cross-faded in from the seated pose over <paramref name="blend"/> s. The Gait stays the seat's until the brain sets the
        /// standing one as he stands; the state then hands him to it once he's clear of the door (IntruderClips.ExitClear: the
        /// take's slow tail, turning back to shut the door, is skipped).
        /// </summary>
        public void ExitCar(bool right, float rate, float blend)
        {
            _anim.SetFloat(ExitRateId, rate);
            _anim.CrossFadeInFixedTime(right == ExitCarGoesLeft ? ExitCarMirrorState : ExitCarState, blend, 0, 0f);
        }

        /// <summary>The take (unmirrored ExitCar) gets out to his left, as Mixamo's "driver's side" says — by the probe's measured travel.</summary>
        static bool ExitCarGoesLeft => IntruderClips.ExitTravel.x <= 0f;

        /// <summary>Still in the exit clip past its hand-off (no transition under way): into the gait now.</summary>
        public void EndExit(float blend)
        {
            if (!InExitClip || _anim.IsInTransition(0)) return;
            _anim.CrossFadeInFixedTime(GaitStates[(int)_gait], blend, 0, 0f);
        }

        /// <summary>Shot dead getting out: the exit clip holds the pose it's at (he dies from it, where he is).</summary>
        public void FreezeExit() { if (InExitClip) _anim.SetFloat(ExitRateId, 0f); }

        /// <summary>The base layer is in (or blending into, or out of) an exit clip.</summary>
        public bool InExitClip => ExitClipTime >= 0f;

        /// <summary>How far into the exit clip he is (seconds of the take at 1×; IntruderClips.ExitLength long); −1: not in one.</summary>
        public float ExitClipTime
        {
            get
            {
                if (!_anim.enabled) return -1f;
                var s = _anim.GetCurrentAnimatorStateInfo(0);
                if (IsExitState(s.fullPathHash)) return s.normalizedTime * IntruderClips.ExitLength;
                if (!_anim.IsInTransition(0)) return -1f;
                var n = _anim.GetNextAnimatorStateInfo(0);
                return IsExitState(n.fullPathHash) ? n.normalizedTime * IntruderClips.ExitLength : -1f;
            }
        }

        /// <summary>Tests: −1 getting out to his left (ExitCar: the driver's side), +1 to his right (ExitCarMirror), 0 neither.</summary>
        public int ExitSide
        {
            get
            {
                if (!_anim.enabled) return 0;
                int h = _anim.GetCurrentAnimatorStateInfo(0).fullPathHash;
                if (!IsExitState(h) && _anim.IsInTransition(0)) h = _anim.GetNextAnimatorStateInfo(0).fullPathHash;
                int left = ExitCarGoesLeft ? -1 : 1;
                return h == ExitCarState ? left : h == ExitCarMirrorState ? -left : 0;
            }
        }

        static bool IsExitState(int hash) => hash == ExitCarState || hash == ExitCarMirrorState;

        // ── Hands on head (kneeling surrender): the female-authored KneelHandsOnHead pose on an arms-only layer.
        bool _handsOnHead;
        float _armsWeight;
        public void SetHandsOnHead(bool on) => _handsOnHead = on;

        // ── Hands up (surrender) without a clip: IK pulls both hands above the head over whatever the body plays.
        float _handsUp, _handsUpTarget;
        public void SetHandsUp(bool up) => _handsUpTarget = up ? 1f : 0f;
        public bool HandsUpShown => _handsUp > 0.8f;

        // ── Carrying something flat in both hands (VISITORS: a Marshal night's pizza courier, Brett 0.1.56): IK puts each hand on
        //    a side edge of the box, a little under its middle, elbows in at his sides. Hands up always wins.
        Transform _carry;
        float _carryHalfWidth, _carryWeight;
        /// <summary>Both hands on <paramref name="box"/>'s left/right edges (<paramref name="halfWidth"/> m from its centre along its right).</summary>
        public void SetCarry(Transform box, float halfWidth) { _carry = box; _carryHalfWidth = halfWidth; }
        /// <summary>Let go (the box was set down or dropped): the hands ease back to the clip's.</summary>
        public void ClearCarry() => _carry = null;

        void OnAnimatorIK(int layerIndex)
        {
            _handsUp = Mathf.MoveTowards(_handsUp, _handsUpTarget, Time.deltaTime * 2.5f);
            _carryWeight = Mathf.MoveTowards(_carryWeight, _carry != null && _handsUpTarget <= 0f ? 1f : 0f, Time.deltaTime / 0.25f);
            if (_handsUp <= 0.001f && _carryWeight > 0.001f) { CarryIK(); return; }
            if (_handsUp <= 0.001f)
            {
                _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f); _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
                _anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 0f); _anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
                return;
            }
            var head = _anim.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;
            var root = transform;
            Vector3 up = Vector3.up, right = root.right, fwd = root.forward;
            float w = Mathf.SmoothStep(0f, 1f, _handsUp);
            var top = head.position + up * 0.24f + fwd * 0.06f;
            _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, w); _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, w);
            _anim.SetIKPosition(AvatarIKGoal.LeftHand, top - right * 0.24f);
            _anim.SetIKPosition(AvatarIKGoal.RightHand, top + right * 0.24f);
            // Elbows out to the sides, a little forward: the classic "don't shoot" pose.
            _anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, w); _anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, w);
            _anim.SetIKHintPosition(AvatarIKHint.LeftElbow, head.position - right * 0.45f + fwd * 0.1f);
            _anim.SetIKHintPosition(AvatarIKHint.RightElbow, head.position + right * 0.45f + fwd * 0.1f);
        }

        Vector3 _carryL, _carryR;

        /// <summary>The carry pass (<see cref="SetCarry"/>): wrists just outside the box's side edges, a little behind and under its
        /// middle (the hands round its sides); after <see cref="ClearCarry"/> the last grip eases out over 0.25 s.</summary>
        void CarryIK()
        {
            if (_carry != null)
            {
                Vector3 c = _carry.position, side = _carry.right * (_carryHalfWidth + 0.01f), under = -_carry.up * 0.02f - _carry.forward * 0.05f;
                _carryL = c - side + under;
                _carryR = c + side + under;
            }
            float w = Mathf.SmoothStep(0f, 1f, _carryWeight);
            Vector3 right = transform.right, fwd = transform.forward, up = Vector3.up;
            var hips = Hips != null ? Hips.position : transform.position + up;
            _anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, w); _anim.SetIKPositionWeight(AvatarIKGoal.RightHand, w);
            _anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f); _anim.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
            _anim.SetIKPosition(AvatarIKGoal.LeftHand, _carryL);
            _anim.SetIKPosition(AvatarIKGoal.RightHand, _carryR);
            // Elbows in at his sides, a little back: forearms forward under the box.
            _anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, w); _anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, w);
            _anim.SetIKHintPosition(AvatarIKHint.LeftElbow, hips - right * 0.3f - fwd * 0.12f + up * 0.12f);
            _anim.SetIKHintPosition(AvatarIKHint.RightElbow, hips + right * 0.3f - fwd * 0.12f + up * 0.12f);
        }

        // ── The pistol (M3a gunman).
        AimRig _aimRig;
        GunPose _gunPose;
        float _aim, _raiseSeconds = 0.35f, _aimWeight, _recoil, _floorAim;
        Vector3 _aimTarget;
        bool _hasTarget;

        public bool HasPistol => _pistol != null && _pistol.InHand;
        public Transform Muzzle => _pistol != null ? _pistol.Muzzle : null;
        public GunPose CurrentGunPose => _gunPose;
        /// <summary>0 = low ready … 1 = fully up on the target (the raise has finished: the tell is over).</summary>
        public float AimAmount => _aim;

        /// <summary>
        /// Low ready / aimed / arms free. A raise to Aimed takes <paramref name="raiseSeconds"/> (the gunman's 0.6–0.8 s tell);
        /// lowering is quicker (0.35 s).
        /// </summary>
        public void SetGunPose(GunPose pose, float raiseSeconds = 0.35f) { _gunPose = pose; _raiseSeconds = Mathf.Max(0.05f, raiseSeconds); }

        /// <summary>Where the aimed muzzle should point this frame (the player's chest). Call every frame while aiming.</summary>
        public void AimAt(Vector3 worldPoint) { _aimTarget = worldPoint; _hasTarget = true; }

        /// <summary>One shot's kick: the muzzle flips up ~7° and settles in 0.15 s.</summary>
        public void Recoil() => _recoil = 1f;

        void UpdateGun()
        {
            float dt = Time.deltaTime;
            bool pistol = HasPistol;
            float pg = pistol ? 1f : 0f;
            if (_pistolGrip != pg) { _pistolGrip = Mathf.MoveTowards(_pistolGrip, pg, 8f * dt); _anim.SetLayerWeight(3, _pistolGrip); }
            // The two-handed hold only over the Gun gait's own state: a hit reaction, a toss, a fall or a death gets his arms.
            bool gunGait = IsIn(Gait.Gun) || Entering(Gait.Gun);
            float arms = pistol && _gunPose != GunPose.None && gunGait ? 1f : 0f;
            if (_gunArms != arms) { _gunArms = Mathf.MoveTowards(_gunArms, arms, 6f * dt); _anim.SetLayerWeight(4, _gunArms); }
            float aimTo = pistol && _gunPose == GunPose.Aimed ? 1f : 0f;
            _aim = Mathf.MoveTowards(_aim, aimTo, dt / (aimTo > _aim ? _raiseSeconds : 0.35f));
            _anim.SetFloat(AimId, Mathf.SmoothStep(0f, 1f, _aim));
            // Aim correction: over the Gun gait with the layer's weight; legs shot out, from the crawl pose (arms free).
            bool floor = IsIn(Gait.Crawl);
            // Down in the crawl the aim pass eases in (0.4 s): it takes the arm up to 70° off the crawl pose, which in one frame
            // was a visible snap the moment the stumble settled (render Ctl_GunCrawlSnap).
            _floorAim = Mathf.MoveTowards(_floorAim, floor ? 1f : 0f, dt / FloorAimSeconds);
            _aimWeight = !pistol || !_hasTarget ? 0f : Mathf.SmoothStep(0f, 1f, _aim) * (gunGait ? _gunArms : _floorAim);
            _recoil = Mathf.MoveTowards(_recoil, 0f, dt / 0.15f);
        }

        /// <summary>
        /// After the Animator has posed him: turn the chest (≤ 35°), then the right upper arm (standing ≤ 15°, on the floor ≤ 70°:
        /// the crawl's arm is nowhere near an aim) so the muzzle ray lands on the target. The pose is his until the next frame's
        /// evaluation, so the facts (next Update) read exactly the ray the player sees.
        /// </summary>
        void AimCorrection()
        {
            var m = Muzzle;
            if (_aimWeight <= 0.01f || m == null || !_anim.enabled) return;
            _aimRig.Apply(m, _aimTarget, _aimWeight, IsIn(Gait.Crawl), _recoil);
        }

        /// <summary>How long the aim pass takes to take over once he's settled in the crawl (s).</summary>
        public const float FloorAimSeconds = 0.4f;

        /// <summary>
        /// The aim pass on an already-posed rig (also PoseSnapshots, so the renders show the game's aim): chest ≤ 35°, then the
        /// right upper arm (≤ 15° standing, ≤ 70° on the floor) turned so the muzzle ray lands on the target, scaled by the
        /// weight; then a shot's kick (recoil 1 → 0: the muzzle flips up ~7°). Standing, the left (support) hand then follows the
        /// gun: it keeps the pose it had relative to the right hand after the chest turn (two-bone IK on the left arm) — without
        /// it the arm turn left his left hand hanging ~10 cm under the gun (render Ctl_GunTellFire).
        /// </summary>
        public sealed class AimRig
        {
            readonly Transform _chest, _rUpperArm, _rHand, _lUpper, _lLower, _lHand;

            public AimRig(Animator a)
            {
                _chest = a.GetBoneTransform(HumanBodyBones.UpperChest);
                if (_chest == null) _chest = a.GetBoneTransform(HumanBodyBones.Chest);
                _rUpperArm = a.GetBoneTransform(HumanBodyBones.RightUpperArm);
                _rHand = a.GetBoneTransform(HumanBodyBones.RightHand);
                _lUpper = a.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _lLower = a.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _lHand = a.GetBoneTransform(HumanBodyBones.LeftHand);
            }

            public void Apply(Transform muzzle, Vector3 target, float weight, bool floor, float recoil)
            {
                if (muzzle == null || weight <= 0.01f) return;
                TurnBoneToAim(_chest, muzzle, target, 35f * weight);
                // The support hand's grip on the gun, as the clip has it (the chest turn moved both arms together).
                bool support = !floor && _rHand != null && _lUpper != null && _lLower != null && _lHand != null;
                Vector3 lPos = default; Quaternion lRot = default;
                if (support) { lPos = _rHand.InverseTransformPoint(_lHand.position); lRot = Quaternion.Inverse(_rHand.rotation) * _lHand.rotation; }
                // The arm swings the muzzle about the shoulder, so one turn falls short by the parallax (the barrel ended ~4° off
                // the chest at 2 m — and his rounds fly down the barrel: PlayerShape). A second pass from where the muzzle ended up
                // takes the rest, within the same limit.
                float armMax = (floor ? 70f : 15f) * weight;
                float armUsed = TurnBoneToAim(_rUpperArm, muzzle, target, armMax);
                TurnBoneToAim(_rUpperArm, muzzle, target, armMax - armUsed);
                if (recoil > 0f && _rUpperArm != null)
                {
                    var axis = Vector3.Cross(muzzle.forward, Vector3.up);
                    if (axis.sqrMagnitude > 1e-4f) _rUpperArm.rotation = Quaternion.AngleAxis(7f * recoil * recoil, axis.normalized) * _rUpperArm.rotation;
                }
                if (support) TwoBoneIK(_lUpper, _lLower, _lHand, _rHand.TransformPoint(lPos), _rHand.rotation * lRot);
            }
        }

        /// <summary>Turns the bone (≤ maxDegrees) so the muzzle points more at the target; returns how far it turned (°).</summary>
        static float TurnBoneToAim(Transform bone, Transform muzzle, Vector3 target, float maxDegrees)
        {
            if (bone == null || maxDegrees <= 0f) return 0f;
            var to = target - muzzle.position;
            if (to.sqrMagnitude < 0.04f) return 0f;
            Quaternion.FromToRotation(muzzle.forward, to).ToAngleAxis(out float angle, out var axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || Mathf.Abs(angle) < 0.05f) return 0f;
            float turn = Mathf.Clamp(angle, -maxDegrees, maxDegrees);
            bone.rotation = Quaternion.AngleAxis(turn, axis) * bone.rotation;
            return Mathf.Abs(turn);
        }

        /// <summary>Upper/lower/end so the end reaches <paramref name="target"/>: the elbow opens or closes in its current bend
        /// plane (law of cosines), then the upper bone swings the chain onto the target; the end takes <paramref name="endRot"/>.</summary>
        static void TwoBoneIK(Transform upper, Transform lower, Transform end, Vector3 target, Quaternion endRot)
        {
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float l1 = (b - a).magnitude, l2 = (c - b).magnitude;
            if (l1 < 1e-4f || l2 < 1e-4f) return;
            float d = Mathf.Clamp((target - a).magnitude, Mathf.Abs(l1 - l2) + 1e-3f, (l1 + l2) * 0.999f);
            var bend = Vector3.Cross(a - b, c - b);
            if (bend.sqrMagnitude > 1e-8f)
            {
                float want = Mathf.Acos(Mathf.Clamp((l1 * l1 + l2 * l2 - d * d) / (2f * l1 * l2), -1f, 1f)) * Mathf.Rad2Deg;
                lower.rotation = Quaternion.AngleAxis(want - Vector3.Angle(a - b, c - b), bend.normalized) * lower.rotation;
            }
            upper.rotation = Quaternion.FromToRotation(end.position - a, target - a) * upper.rotation;
            end.rotation = endRot;
        }

        /// <summary>A corpse: stop evaluating the rig. The last pose stays; hitboxes and wounds stay on the bones.</summary>
        public void Sleep()
        {
            _anim.enabled = false;
            enabled = false;
        }

        // ── Asleep alive (Ricky, research/27 §3: his Animator sleeps most of the night, a CPU saving) ──

        /// <summary>The rig isn't being evaluated (<see cref="Sleep"/>): the last pose stays.</summary>
        public bool Asleep => !_anim.enabled;

        /// <summary>
        /// Keep the state machine where it was across a sleep. Call before the first <see cref="Sleep"/> of a man who will be
        /// woken again: nothing set it before (a corpse never wakes), and without it the Animator restarts in its default state
        /// (Locomotion, standing) when re-enabled — a crouched man would pop up to his feet for a frame (research/27 §3).
        /// </summary>
        public void KeepStateWhenAsleep() => _anim.keepAnimatorStateOnDisable = true;

        /// <summary>Evaluate the rig again after <see cref="Sleep"/> (a corpse stays asleep).</summary>
        public void Wake()
        {
            if (_dead || _anim.enabled) return;
            _anim.enabled = true;
            enabled = true;
        }

        /// <summary>
        /// The controller has this gait's state. Crouch / Cower come with IntruderBuilder's 2026-10-03 states: an older
        /// controller has neither, and setting the Gait int to one would leave him standing in the default state.
        /// </summary>
        public bool HasGait(Gait gait) => (int)gait >= 0 && (int)gait < GaitStates.Length && _anim.HasState(0, GaitStates[(int)gait]);

        /// <summary>Pose the rig now (a zero-time update), so a <see cref="SnapGait"/> shows this frame, not the bind pose.</summary>
        public void Evaluate() { if (_anim.enabled) _anim.Update(0f); }
    }
}
