using System;
using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// The man breaking in. State machine: Outside → BreakIn → Entering (agent path over the DoorwayNav link) →
    /// Chasing (NavMesh) → Attacking → Dead.
    /// Feel rules (Brett's 0.1.x playtest notes):
    /// - One owner decides when his feet are planted (<see cref="LockMovement"/>): swings, hit reactions and the legs-out
    ///   fall each take the lock for exactly as long as their clip plays, so he never slides mid-animation.
    /// - Everything that lands in one frame (8–9 shotgun pellets) is one hit: one pain sound, one reaction, one lock.
    /// - Leg damage accumulates: limp, then legs shot out → he falls and crawls. Gait speeds match the clips (IntruderClips).
    /// - He dies falling toward open floor (clearance raycasts against the scanned room), then goes still as a corpse.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public partial class Intruder : MonoBehaviour
    {
        // Appended only (logs stay readable). Idle = standing/sneaking inside; Complying = hands up / prone;
        // Fleeing = running for the door; Gone = left through the door (hidden, about to be despawned).
        public enum State { Outside, BreakIn, Entering, Chasing, Attacking, Dead, Idle, Fleeing, Complying, Gone }

        [Header("Timing")]
        [SerializeField] float knockDelay = 3f;

        [Header("Combat")]
        [SerializeField] float attackRange = 0.9f;
        [SerializeField] float attackInterval = 1.6f;
        [SerializeField] float attackDamage = 15f;
        [SerializeField] float lungeDistance = 0.35f;
        [SerializeField] Transform strikingArm;   // placeholder body only; ignored when an IntruderAnimator exists
        [SerializeField] float repathInterval = 0.25f;

        [Header("Wounds")]
        [Tooltip("Accumulated leg damage that makes him limp (one pistol round to a leg is ~20–24).")]
        [SerializeField] float limpAtLegDamage = 15f;
        [Tooltip("Accumulated leg damage that takes his legs out: he falls and crawls (two pistol rounds, or a shotgun blast to the legs).")]
        [SerializeField] float downAtLegDamage = 45f;
        [Tooltip("Smallest hit (after multipliers, summed per frame) that plays a hit-reaction clip.")]
        [SerializeField] float reactMinDamage = 8f;
        [Tooltip("A hit this big restarts a reaction that is already playing; smaller ones don't (no stun-lock).")]
        [SerializeField] float interruptDamage = 30f;

        public State Current { get; private set; } = State.Outside;
        public float Health { get; private set; } = 100f;
        public Gait CurrentGait => _gait;
        public string Summary => $"{Current} {_gait} hp {Health:F0} legs {_legDamage:F0}";

        /// <summary>Raised on every state change (state, this).</summary>
        public event Action<State, Intruder> StateChanged;
        public event Action<Intruder> Died;

        NavMeshAgent _agent;
        IntruderAnimator _anim;
        bool _hasAnim;
        WoundSystem _wounds;
        Transform _player;
        Vector3 _outsidePoint, _insidePoint;
        float _floorY;

        static readonly List<Intruder> s_all = new();
        /// <summary>Every intruder/officer in the scene (alive or not): the shot log's nearest-man check.</summary>
        public static IReadOnlyList<Intruder> All => s_all;

        void Awake()
        {
            s_all.Add(this);
            _agent = GetComponent<NavMeshAgent>();
            _agent.enabled = false;
            _agent.autoTraverseOffMeshLink = false; // the doorway link is crossed at his gait speed (CrossLink)
            _anim = GetComponentInChildren<IntruderAnimator>();
            _hasAnim = _anim != null;
            _wounds = GetComponent<WoundSystem>();
            _weapons = GetComponentsInChildren<IntruderWeapon>(true);
            foreach (var w in _weapons) if (w.Kind == HIS.Scenario.WeaponKind.Knife) _weapon = w; // the scenario picks (Configure)
            Id = ++_ids;
        }

        /// <summary>Called by the spawner once the room + NavMesh exist.</summary>
        /// <summary>Which door he came to (0 = the front door, 1 = the second door): VirtualDoor.Index / DoorwayNav.Index.</summary>
        public int DoorIndex { get; private set; }
        /// <summary>His door's rig (the leaf he pounds, kicks and talks through); the front door's if his is missing.</summary>
        public HIS.Room.VirtualDoor Door
        {
            get
            {
                foreach (var d in HIS.Room.VirtualDoor.All) if (d != null && d.Index == DoorIndex) return d;
                return HIS.Room.VirtualDoor.Instance;
            }
        }
        /// <summary>His door's way in and out; the front door's if his is missing.</summary>
        DoorwayNav Nav => DoorwayNav.For(DoorIndex) ?? DoorwayNav.Instance;

        public void Initialize(Vector3 outsidePoint, Vector3 insidePoint, float floorY, int agentTypeId, Transform player, int doorIndex = 0)
        {
            DoorIndex = doorIndex;
            HIS.Room.VirtualDoor.Current = Door;
            _outsidePoint = outsidePoint;
            _insidePoint = insidePoint;
            _floorY = floorY;
            _player = player;
            _agent.agentTypeID = agentTypeId;
            _agent.speed = GaitSpeed;

            transform.position = outsidePoint;
            _prevPos = outsidePoint;
            transform.rotation = Quaternion.LookRotation(Flat(insidePoint - outsidePoint), Vector3.up);
            StopAllCoroutines();
            StartCoroutine(Run());
        }

        Renderer[] _renderers;
        int[] _rendererLayers; // each renderer's own layer, restored when he comes into view
        Collider[] _hitboxes;
        bool _visible = true;
        float _visibleSince = -100f; // the door opened on him: the gunman's perception gate runs from here

        /// <summary>Out of sight: outside behind the closed door (or gone). A hit now is a blind shot through the door.</summary>
        public bool BehindDoor => !_visible;

        /// <summary>
        /// Hit through the closed door before he came in (Outside/BreakIn). The quiet entries read it: the neighbour and the
        /// burglar never come in after that. A kicker who's only wounded comes on angrier (<see cref="ForcedEntry"/>).
        /// </summary>
        public bool ShotThroughDoor { get; private set; }

        /// <summary>
        /// Hide/show his body. He's invisible outside until the door opens, so no foot ever pokes through the door. His
        /// hitboxes follow, except while he's at the door (<see cref="State.BreakIn"/>: kicking it, on the handle, about to
        /// walk in): then rounds through the closed leaf can hit him (Brett 0.1.8: shooting through doors).
        /// </summary>
        public void SetVisible(bool visible)
        {
            // Gone is gone (VISITORS, Brett 0.1.56: a man who left stood frozen in the doorway and on the door camera until the
            // next round, Scenarios mode too): never drawn again, whoever asks. Police withdraw and back out as before.
            if (Current == State.Gone && _intent != HIS.Scenario.Intent.Police) { Vanish(); return; }
            _renderers ??= GetComponentsInChildren<Renderer>(true);
            if (visible && !_visible) _visibleSince = Time.time;
            // The crowbar he carried up the path is stashed once he's in the room; the delivery man the door was opened on
            // keeps his parcel until he sets it on the step (FalseAlarmVisit).
            if (visible && !_propIsParcel) DropProp(false);
            _visible = visible;
            // A knife he dropped is no longer his (it stays on the floor when he leaves or the police take him).
            // Out of sight behind his door he's still drawn for the door camera (security monitor, Brett 0.1.20): his
            // renderers move to the "Outside" layer, which the headset camera never draws, instead of switching off.
            _rendererLayers ??= System.Array.ConvertAll(_renderers, r => r != null ? r.gameObject.layer : 0);
            int outside = LayerMask.NameToLayer("Outside");
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null || !r.transform.IsChildOf(transform)) continue;
                if (visible || outside < 0) { r.enabled = visible; r.gameObject.layer = _rendererLayers[i]; }
                else { r.enabled = true; r.gameObject.layer = outside; }
            }
            // His wound decals came after the cache: hidden with him (no blood patches floating where he was taken away or
            // walked out), shown with him (rounds taken through the door are on him when it bursts).
            if (_wounds != null)
            {
                // Behind his door the wounds stay on him for the door camera (Outside layer), never for the headset.
                _wounds.ShowBodyWounds(visible || outside >= 0);
                _wounds.WoundsOnLayer(visible ? -1 : outside);
            }
            // At the door he's hittable through the leaf; with varied entries that includes the wait before the first sound
            // (Brett 0.1.17: shots then went through him unnoticed and he kicked it in anyway).
            SetHittable(visible || Current == State.BreakIn || (Current == State.Outside && (_styled || OnPorchPath)));
        }

        bool _hittable = true;

        /// <summary>In a police car's seat (IntruderPolice.RideIn): parented to it, slumps there if shot.</summary>
        public bool Riding { get; private set; }

        void SetHittable(bool on)
        {
            _hittable = on;
            _hitboxes ??= GetComponentsInChildren<Collider>(true);
            foreach (var c in _hitboxes) if (c != null && c.transform.IsChildOf(transform)) c.enabled = on;
        }

        /// <summary>
        /// A scream / groan from his mouth. Behind the closed door it goes on the door's own voice source, muffled (the wood
        /// cracks of the next rounds can't cut it off) and replaces whatever he was saying; in view, a plain Sfx at his head.
        /// </summary>
        const float DoorCryLowpass = 1000f;   // a real door passes more of a scream than 650 Hz did (Brett: louder groans)

        void Cry(string key, float volume, float lowpassHz = 650f)
        {
            var at = transform.position + Vector3.up * 1.5f;
            var door = BehindDoor ? Door : null;
            if (door == null || door.IsOpen) { Sfx.Play(key, at, volume); return; }
            // Refused (the key's 0.35 s cooldown: a double tap through the door): the first scream plays on, uncut.
            if (!door.PlayVoiceThrough(key, at, volume, lowpassHz)) return;
            Sfx.Stop(_voice, 0.05f); _voice = 0; // a line on an Sfx handle; one on the door's voice source was just replaced
            _voiceThroughDoor = true;
        }

        /// <summary>A sound from his body. Hidden behind the closed door it's muffled and comes through the leaf.</summary>
        void BodySound(string key, Vector3 at, float volume)
        {
            var door = BehindDoor ? Door : null;
            if (door != null && !door.IsOpen) door.PlayThrough(key, at, volume, 1f, 650f);
            else Sfx.Play(key, at, volume);
        }

        // ───────────────────────────── movement lock ─────────────────────────────
        // Priorities: a gesture (hiccup, rummaging) gives way to anything; a hit reaction cuts a swing short; a fall or a
        // posture change (down to his knees, up off the floor) beats all of them. Only a lock that starts a new clip may cut an
        // older one short (the new clip replaced the old one); a clip-less flinch only ever extends.
        enum Lock { None, Gesture, Strike, React, Fall }
        Lock _lock;
        float _lockedUntil;
        bool MovementLocked => Time.time < _lockedUntil;
        bool Reacting => MovementLocked && _lock >= Lock.React;
        /// <summary>A fall or posture change (down to his knees, up off the floor) has his feet: its root motion carries him.</summary>
        bool Falling => MovementLocked && _lock == Lock.Fall;

        void LockMovement(Lock kind, float seconds, bool startsClip = true)
        {
            float until = Time.time + seconds;
            if (!MovementLocked) { _lock = kind; _lockedUntil = until; }
            else if (!startsClip || kind == _lock) _lockedUntil = Mathf.Max(_lockedUntil, until);
            else if (kind > _lock) { _lock = kind; _lockedUntil = until; }
            else return;
            if (_agent.enabled && _agent.isOnNavMesh) { _agent.isStopped = true; _agent.velocity = Vector3.zero; }
        }

#if UNITY_EDITOR
        /// <summary>Test hook (ScenarioTests' doorway checks): called every frame with this intruder.</summary>
        public static Action<Intruder> FrameProbe;
#endif

        void Update()
        {
            TrackFacts();
            TrackCameraSighting();
            WatchDoor();         // the player opened his door on him (IntruderDoorOpened.cs)
            // Out of the room but in plain sight through his open door (the headset draws the outside there, DoorwayView):
            // rounds through the doorway find him — a wounded officer backing out, a man off to the side of the step
            // (Brett 0.1.31: hits at a distance with the door open).
            if (!_hittable && !_visible && Current != State.Dead && Current != State.Gone && Door != null && Door.IsOpen) SetHittable(true);
#if UNITY_EDITOR
            FrameProbe?.Invoke(this);
#endif
            if (Current == State.Dead || !_agent.enabled || !_agent.isOnNavMesh) return;
            bool locked = MovementLocked;
            if (_agent.isStopped != locked)
            {
                _agent.isStopped = locked;
                if (locked) _agent.velocity = Vector3.zero;
            }
            if (_agent.isOnOffMeshLink) CrossLink(); else _linkLeg2 = false;
        }

        /// <summary>On the doorway link (DoorwayNav) or just off it: the agent has no velocity there.</summary>
        public bool OnDoorwayLink => _agent.enabled && _agent.isOnOffMeshLink;

        /// <summary>
        /// Through the doorway on the runtime link (<see cref="DoorwayNav"/>), one owner for every path that uses it (entry,
        /// leaving): at the speed the walk set on the agent, planted while a clip has his feet, the walk blend driven directly
        /// (an agent on a link reports no velocity), paused with the game (timeScale 0).
        /// </summary>
        void CrossLink()
        {
            if (Time.deltaTime <= 0f) return;
            var link = _agent.currentOffMeshLinkData;
            // The agent can go onto the link short of its start: walk to the start first, so the crossing is the line
            // DoorwayNav checked clear of the jambs, furniture and leaf (not a diagonal from wherever he triggered it).
            if (!_linkLeg2 && FlatDistance(transform.position, link.startPos) > 0.03f) _linkTarget = link.startPos;
            else { _linkLeg2 = true; _linkTarget = link.endPos; }
            float s = MovementLocked ? 0f : _agent.speed;
            if (_hasAnim) _anim.SetSpeedOverride(s);
            FaceTowards(_linkTarget, 10f);
            transform.position = Vector3.MoveTowards(transform.position, _linkTarget, s * Time.deltaTime);
            if (!_linkLeg2 || (transform.position - link.endPos).sqrMagnitude > 1e-4f) return;
            _linkLeg2 = false;
            _agent.CompleteOffMeshLink();
            // On the way in, carry his stride off the link (the agent comes off it at rest and would ramp up at 4 m/s²: a hitch
            // as he steps into the room). Not on the way out: that ramp keeps a runner in the doorway the beat the "shot in the
            // back" call needs (ScenarioTests: Robber runs with the gun), and he's walking into the dark box there anyway.
            var dir = Flat(link.endPos - link.startPos);
            var nav = Nav;
            if (s > 0f && dir.sqrMagnitude > 1e-4f && nav != null && Vector3.Dot(dir, nav.Outward) < 0f) _agent.velocity = dir.normalized * s;
            if (_hasAnim) _anim.SetSpeedOverride(null);
        }

        bool _linkLeg2;      // CrossLink: at the link's start, now on the way to its end
        Vector3 _linkTarget;

        // ───────────────────────────── gait ─────────────────────────────
        Gait _gait = Gait.Normal;
        float _legDamage;

        float GaitSpeed => _gait switch
        {
            Gait.Limp => IntruderClips.LimpSpeed,
            Gait.Crawl => IntruderClips.CrawlSpeed,
            Gait.Drunk => IntruderClips.DrunkWalkSpeed,
            Gait.Sneak => IntruderClips.SneakSpeed,
            Gait.Gun => _legDamage >= limpAtLegDamage ? IntruderClips.LimpSpeed : IntruderClips.GunWalkSpeed, // no limp gait: the gun hold is the Gun gait's
            Gait.Kneel or Gait.Prone or Gait.Sit or Gait.Seated or Gait.Driving or Gait.Crouch or Gait.Cower => 0f, // (Crouch/Cower: Ricky never walks)
            _ => IntruderClips.WalkSpeed,
        };

        /// <summary>On his feet (any walking gait), as opposed to crawling, kneeling, face down or sitting.</summary>
        static bool Upright(Gait g) => g is Gait.Normal or Gait.Limp or Gait.Calm or Gait.Drunk or Gait.Sneak or Gait.Gun;

        void SetGait(Gait gait)
        {
            if (_gait == gait) return;
            _gait = gait;
            if (_hasAnim) _anim.SetGait(gait);
            if (_agent.enabled) _agent.speed = GaitSpeed;
            Debug.Log($"[HIS] Intruder: {gait} (leg damage {_legDamage:F0})");
        }

        // ───────────────────────────── behaviour ─────────────────────────────
        IEnumerator Run()
        {
            // Ricky (research/27 §3): already in his hiding spot in the room. Never at a door: none of the visitor's start below
            // (hidden outside, the last man's door swung shut) may touch him.
            if (_protectee) { yield return Guarded(ProtecteeBrain(), ProtecteeBrainFailed, "Ricky"); yield break; }
            SetState(State.Outside);
            SetVisible(false);
            // A Marshal night's visitor (his own voice: only the Marshal defs set one) comes up the path from the street: not
            // drawn on his spawn frame at the step, where the door camera could catch him before he's placed (the police's and
            // hitmen's HideUntilPlaced; the camera's motion check skips him meanwhile). Shown once placed (WalkUpFromStreet), or
            // just below if he doesn't walk up.
            // Every mode's street walk-up has the same spawn frame at the step (the T-pose flash on the door camera), so everyone
            // but police (their own placement) starts hidden; the lines below show him after one frame when he doesn't walk up.
            if (_intent != HIS.Scenario.Intent.Police) HideUntilPlaced();
            var door = Door;
            // The last intruder left it hanging open. (Not for police: a later wave comes through the door the first one broke.
            // Not for a Marshal night's visitor between the hitman waves either — the fixer, 0.1.58: the street tidy just shut
            // every door it could (PoliceResponse.TidyForVisitor → ShutBrokenDoors), and one it left open is open for the reason
            // it logged — you hold it, you opened it by hand, you or someone stand in it. This Close drops your hold and eases the
            // leaf shut through whoever is in its sweep: it would tear the handle out of your hand, or latch the door with you in
            // the doorway, a second after the tidy chose not to. The hitman run is out (and idle) only between its waves.)
            var squad = HIS.Scenario.PoliceResponse.Instance;
            bool betweenWaves = squad != null && squad.Active && squad.Faction == HIS.Scenario.SquadFaction.Hitmen;
            if (door != null && door.IsOpen && _intent != HIS.Scenario.Intent.Police && !betweenWaves) door.Close(0.8f);
            yield return null;                       // the director sets this round's Style right after spawning (varied entries)
            if (_intent == HIS.Scenario.Intent.Police) { yield return Guarded(PoliceBrain(), OfficerBrainFailed, $"Intruder #{Id} ({name})"); yield break; } // an officer runs his whole call (IntruderPolice.cs)
            if (!_styled || !WalksUpFromStreet) ShowPlaced(); // (no walk-up: he's at his door, drawn there as before)
            if (_styled)
            {
                yield return WalkUpFromStreet();     // IntruderApproach.cs: up the path on the door camera, maybe round to the other door
                ShowPlaced();                        // (never left hidden, however the walk ended)
                if (Current is State.Gone or State.Dead) yield break;
                SetHittable(true);                   // at the door from the first moment: a round through the leaf finds him
                yield return EntryByStyle(Door);     // IntruderEntries.cs: a random wait, then the rolled style (his door now)
            }
            else
            {
                yield return new WaitForSeconds(knockDelay);
                if (_entry == HIS.Scenario.EntryType.ForcedKick) yield return ForcedEntry(door);
                else yield return QuietEntry(door);
            }
            if (Current is State.Gone or State.Dead || (ShotThroughDoor && BehindDoor)) yield break; // shot through the door and still outside: he never comes in
            yield return WalkIn();
            if (Current is State.Gone or State.Dead) yield break;
            yield return Brain();
        }

        /// <summary>
        /// Runs <paramref name="brain"/> — its nested steps (yielded IEnumerators) included, one stack — the way Unity would,
        /// but an exception at any depth is caught: logged with its stack trace, then <paramref name="failed"/>. Unity on its
        /// own just drops a coroutine that throws, with its whole chain: the man freezes wherever he is, still "alive"
        /// (0.1.44: an officer's walk round the house threw, he stood hidden behind it, and the wave never ended).
        /// Yield instructions (null, WaitForSeconds, a Coroutine) pass straight through to Unity.
        /// </summary>
        public static IEnumerator Guarded(IEnumerator brain, Action<Exception> failed, string who)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(brain);
            while (stack.Count > 0)
            {
                var step = stack.Peek();
                bool more;
                try { more = step.MoveNext(); }
                catch (Exception e)
                {
                    Debug.LogError($"[HIS] {who}: brain failed in {step.GetType().Name} — {e.GetType().Name}: {e.Message}");
                    Debug.LogException(e);
                    failed?.Invoke(e);
                    yield break;
                }
                if (!more) { stack.Pop(); continue; }
                if (step.Current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return step.Current;
            }
        }

        /// <summary>
        /// Breaking the door in (the judge's PC 198.5 "is unlawfully and forcibly entering"): true for the WHOLE forced entry,
        /// from the first blow on the door (pounding, then the kicks, and the pauses between them) until it bursts or he gives
        /// up and runs. 0.1.10 only set it per kick from the kick's start, so a shot during the pounding read as blind fire at
        /// a knocker (Bad) — Brett's knife attacker #4.
        /// </summary>
        bool _forcingDoor;
        float _forcingEndedAt = -100f; // shot, he gave up forcing it (the judge's reaction grace runs from here)
        bool _kicking;       // in the kicks and still at them (not turning away shot, not on the floor)
        int _kicksLanded;    // kicks that hit the door: its latch is weakened, so a man shot dead against it bursts it (Kill)
        bool _slumpIn;       // shot dead mid-kick: the door bursts and he falls forward into the doorway (Collapse)

        IEnumerator ForcedEntry(HIS.Room.VirtualDoor door)
        {
            SetState(State.BreakIn);
            Debug.Log("[HIS] Intruder: forcing the door");
            // Shot through the door (Brett 0.1.8): a burglar, or a man who'd give up anyway, runs for it (so does a fighter
            // whose legs are gone); a fighter who's only wounded comes on angrier: his next kick goes through.
            bool fighter = _intent is HIS.Scenario.Intent.MeleeAttacker or HIS.Scenario.Intent.KnifeAttacker
                           || (_intent == HIS.Scenario.Intent.Gunman && Aggressive);
            _forcingDoor = true;
            // Pounding: three fist blows building up — the door judders in its frame.
            for (int i = 0; i < 3 && !ShotThroughDoor; i++)
            {
                Sfx.Play("door.pound", _outsidePoint + Vector3.up * 1.1f, 0.7f + 0.1f * i);
                if (HIS.Room.RoomScanner.Instance != null) HIS.Room.RoomScanner.Instance.ShakeDoor(0.6f + 0.25f * i);
                yield return new WaitForSeconds(0.55f);
            }
            yield return new WaitForSeconds(0.4f);
            // Kicks: the latch holds twice (the door pops in and springs back), the third kick breaks it. Every impact is
            // timed to the kick clip's contact frame, and each kick waits for the previous clip to finish (no latched triggers).
            const float kickRecover = IntruderClips.KickLength * IntruderClips.OneShotExit - IntruderClips.KickContact;
            for (int k = 0; k < 3; k++)
            {
                if (ShotThroughDoor && (!fighter || _gait == Gait.Crawl)) { StopForcing(); yield return ShotAtTheDoor(); }
                bool final = k == 2 || ShotThroughDoor; // (only a fighter gets here shot)
                _kicking = true;
                if (_hasAnim) _anim.Kick();
                yield return new WaitForSeconds(IntruderClips.KickContact);
                if (ShotThroughDoor && (!fighter || _gait == Gait.Crawl)) { StopForcing(); yield return ShotAtTheDoor(); } // mid-kick: it never lands
                if (door == null) Sfx.Play(final ? "door.burst" : "door.kick", _outsidePoint + Vector3.up * 1.0f, 1f);
                _kicksLanded++;
                if (final) { _forcingDoor = _kicking = false; if (door != null) door.Breach(); SetVisible(true); }
                else if (door != null) door.KickImpact(0.7f + 0.3f * k);
                // The final kick's clip exits at 0.92 — walking in before that glides the kick pose through the doorway.
                yield return new WaitForSeconds(final ? kickRecover : IntruderClips.KickLength - IntruderClips.KickContact + 0.25f);
                if (final) break;
            }
            if (_hasAnim) _anim.ResetKick();
        }

        /// <summary>Shot through the door, he stops forcing it and turns away (<see cref="ShotAtTheDoor"/>).</summary>
        void StopForcing() { _forcingDoor = _kicking = false; _forcingEndedAt = Time.time; }

        /// <summary>
        /// The judge's "is forcibly entering" (IntruderFacts.ForcingDoor): breaking the door in, or stopped only a split
        /// second ago. The first round through the leaf makes a non-fighter give up, but the player can't see that: the
        /// next rounds of the same burst get the same reaction grace as everywhere else (Judge.ThreatGraceSeconds).
        /// </summary>
        bool ForcingDoorNow => BehindDoor && (_forcingDoor || Time.time - _forcingEndedAt < HIS.Scenario.Judge.ThreatGraceSeconds);

        Vector3 _navEntry; // where he stepped onto the room NavMesh: the doorway link's room end (a leaver paths back out through it)

        /// <summary>
        /// Walk in through the open doorway: an agent path from where he stands behind the door (the DoorwayNav apron), over the
        /// doorway link, to its room end — the room's NavMesh from there on (<see cref="Brain"/>). The agent's velocity drives the
        /// walk blend (CrossLink drives it on the link); a reaction plants him mid-doorway, a leg hit slows him right there,
        /// legs shot out he crawls on in. Blocked (no path, or stuck), he stops where he is and the brain takes over.
        /// </summary>
        IEnumerator WalkIn()
        {
            SetState(State.Entering);
            CancelAct(); // the drunk lets go of the handle
            var nav = Nav;
            if (nav == null || !nav.Ready || !nav.OnApron(transform.position, out var start))
            {
                Debug.LogWarning($"[HIS] Intruder: no doorway link ({(nav == null ? "no DoorwayNav" : !nav.Ready ? "not built" : "not on the apron")}); straight walk-in");
                yield return WalkInStraight();
                HandOverToNavMesh();
                yield break;
            }
            // A door the player opened (research/25): he's standing right at it, holding it. A leaf left ajar swings wide first
            // (out of his hand), the link's room end is picked clear of him, and the walk ends once he's in and within reach —
            // never through the player to the link's far end.
            var door = Door;
            bool letIn = door != null && door.OpenedByPlayer;
            if (door != null && door.MakeWay()) yield return new WaitForSeconds(0.4f);
            nav.Reconnect(letIn ? PlayerOnFloor() : (Vector3?)null); // the leaf has swung open since the link was placed (it carves the room NavMesh)
            _navEntry = nav.InsideEnd;
            transform.position = start;
            if (_hasAnim) _anim.SetSpeedOverride(null); // the agent's velocity drives the walk from here
            _agent.enabled = true;
            _agent.Warp(start);
            Func<bool> nearYou = letIn
                ? () => door != null && Vector3.Dot(transform.position - door.Threshold, door.Inward) > 0.1f && FlatDistance(PlayerOnFloor(), transform.position) <= attackRange
                : null;
            // No stop at the link's room end: the brain's first path picks up his stride (a fighter keeps coming, still Advancing).
            yield return PathTo(_navEntry, () => _posture == HIS.Scenario.Posture.HandsUp ? 0f : GaitSpeed, null, stop: false, until: nearYou);
            if (Current == State.Dead) yield break;
            if (_hasAnim) _anim.SetSpeedOverride(null);
            if (_walkBlocked) Debug.LogWarning($"[HIS] Intruder #{Id}: the way in is blocked; stops at {transform.position}");
        }

        bool _walkBlocked; // PathTo's result: gave up short of the destination (no path, or no progress)

        /// <summary>
        /// Walk the agent to <paramref name="dest"/> (0 stopping distance) at <paramref name="speed"/>, over the doorway link if
        /// the path uses it. <paramref name="each"/> runs every live frame first and may take over (return a routine to run, e.g.
        /// the gunman's turn-and-point). No progress for 1.5 s with nothing holding him → the link's room end is re-picked and the
        /// path retried; still stuck 4 s on (or no path at all) → <see cref="_walkBlocked"/>, he stops where he is. <paramref name="stop"/>:
        /// plant him at the end (off: he walks on at speed into whatever the caller paths next). <paramref name="until"/> (checked
        /// off the link only): true ends the walk early, planted where he is (a man let in, already within reach of the player).
        /// </summary>
        IEnumerator PathTo(Vector3 dest, Func<float> speed, Func<IEnumerator> each, bool stop = true, Func<bool> until = null)
        {
            _walkBlocked = false;
            bool early = false;
            float stopping = _agent.stoppingDistance;
            _agent.stoppingDistance = 0f; // the prefab's 0.6 m would park him short of the doorway for good
            var lastPos = transform.position;
            float lastProgress = Time.time, nextPath = 0f;
            bool retried = false, asked = false;
            // An officer hit on his way (into the room, re-angling, to cuff) stops here: his speed is 0 from then on, so the
            // walk could never end and he never got to OfficerWounded — frozen, never counted down, the wave hung (audit 0.1.46;
            // headset 0.1.44: a lead hit in the arm stood still until headshot). Officers only: an intruder's hits don't take him out.
            while (Current != State.Dead && _agent.enabled && _agent.isOnNavMesh && !(_intent == HIS.Scenario.Intent.Police && OfficerHit))
            {
                if (FlatDistance(transform.position, dest) < 0.12f && !_agent.isOnOffMeshLink) break;
                if (until != null && !_agent.isOnOffMeshLink && until()) { early = true; break; }
                if (Time.deltaTime <= 0f) { yield return null; continue; } // paused
                var over = _agent.isOnOffMeshLink ? null : each?.Invoke(); // never a stand-off on the threshold (CrossLink keeps going)
                if (over != null)
                {
                    yield return over;
                    // A hitman's shot (FireOnce: he stops, fires, holds) isn't progress — the stuck clock runs on through it, and
                    // only his feet reset it (the fixer, 0.1.58: the integrator's MarshalSmoke had a wave-4 lead and cover firing
                    // from just inside the doorway for 150 s, every round resetting this clock, so their walk in never gave up
                    // and the wave only ended on its timeout — no cover to reload, never "fighting"). Police and intruders: as before.
                    if (!IsHitman) { lastProgress = Time.time; lastPos = transform.position; }
                    nextPath = 0f;
                    continue;
                }
                float s = speed();
                _agent.speed = s;
                if (s <= 0f && !_agent.isOnOffMeshLink) _agent.velocity = Vector3.zero;
                if (MovementLocked || s <= 0f || FlatDistance(transform.position, lastPos) > 0.1f) { lastProgress = Time.time; lastPos = transform.position; }
                else if (Time.time - lastProgress > (retried ? 4f : 1.5f) || (asked && !_agent.pathPending && _agent.pathStatus == NavMeshPathStatus.PathInvalid))
                {
                    if (retried) { _walkBlocked = true; break; }
                    retried = true; lastProgress = Time.time;
                    if (Nav != null) Nav.Reconnect();
                    nextPath = 0f;
                }
                if (!_agent.isOnOffMeshLink && Time.time >= nextPath) { _agent.SetDestination(dest); asked = true; nextPath = Time.time + 0.25f; }
                yield return null;
            }
            if (_agent.enabled)
            {
                _agent.stoppingDistance = stopping;
                if ((stop || _walkBlocked || early) && _agent.isOnNavMesh && !_agent.isOnOffMeshLink) { _agent.ResetPath(); _agent.velocity = Vector3.zero; }
            }
        }

        /// <summary>No doorway link (no room NavMesh to link to): the old straight walk to the step-in point, but only if that line
        /// clears the walls, furniture and the open leaf — otherwise he stays at the door.</summary>
        IEnumerator WalkInStraight()
        {
            if (!DoorwayNav.ClearLine(transform.position, _insidePoint)) { Debug.LogWarning("[HIS] Intruder: doorway blocked; holding position"); yield break; }
            while (FlatDistance(transform.position, _insidePoint) > 0.01f)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; } // paused
                float speed = MovementLocked || _posture != HIS.Scenario.Posture.Standing ? 0f : GaitSpeed;
                if (_hasAnim) _anim.SetSpeedOverride(speed);
                transform.position = Vector3.MoveTowards(transform.position, _insidePoint, speed * Time.deltaTime);
                yield return null;
            }
            if (_hasAnim) _anim.SetSpeedOverride(null);
        }

        /// <summary>Hand over to the NavMesh (the no-link fallback). Off the NavMesh he holds position rather than walking through real furniture.</summary>
        void HandOverToNavMesh()
        {
            if (!NavMesh.SamplePosition(transform.position, out var hit, 1.0f, NavMesh.AllAreas))
            { Debug.LogWarning("[HIS] Intruder: no NavMesh near the door; holding position"); return; }
            _agent.enabled = true;
            _agent.speed = GaitSpeed;
            _navEntry = hit.position;
            _agent.Warp(_navEntry);
            if (MovementLocked && _agent.isOnNavMesh) _agent.isStopped = true;
        }

        /// <summary>The fighter (fists or knife): comes straight at the player and attacks. Knife: charges at a run when close.</summary>
        IEnumerator MeleeBrain()
        {
            SetState(State.Chasing);
            float nextRepath = 0, nextAttack = 0;
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; } // paused (timeScale 0): hold still
                // Rarely a command gets through to him. Mid-swing/reaction it waits (still queued) until his feet are free,
                // so a man who just gave up never lands the blow he was already throwing.
                if (!MovementLocked && TryComply()) yield break;
                var target = PlayerOnFloor();
                float dist = FlatDistance(target, transform.position);
                bool crawling = _gait == Gait.Crawl;
                bool armed = _weapon != null && _weapon.InHand;
                // Knife: closes the last few metres at a run.
                // (In from a door the player wasn't facing: a walk, never a run at his back — research/21 §5.3.)
                bool seen = SeenLongEnough();
                if (_agent.enabled && !crawling && _gait != Gait.Limp)
                    _agent.speed = armed && seen && dist < 3.5f && dist > attackRange ? IntruderClips.RunSpeed : GaitSpeed;
                if (_cameFromBehind && !_calledOut && dist < 3f) { _calledOut = true; Say(TauntKey); }
                // A man on the floor stops short of you and can't throw a punch: he claws at your legs instead (FloorLunge) — the
                // knife at your shins, or empty hands grabbing at your ankles (Brett 0.1.19: "when they get to you they just look
                // up at you").
                float reach = crawling ? attackRange + 0.4f : attackRange;

                if (dist <= reach)
                {
                    if (Current != State.Attacking) SetState(State.Attacking);
                    if (_agent.enabled && _agent.isOnNavMesh && _agent.hasPath) _agent.ResetPath();
                    if (!Reacting && !(MovementLocked && _lock == Lock.Strike))
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(Flat(target - transform.position), Vector3.up), (crawling ? 3f : 10f) * Time.deltaTime);
                    if (!MovementLocked && Time.time >= nextAttack)
                    {
                        if (!crawling && seen) nextAttack = Time.time + StartStrike(target);
                        else if (_hasAnim) { StartCoroutine(FloorLunge()); nextAttack = Time.time + attackInterval + FloorLungeTime; }
                    }
                }
                else
                {
                    if (Current != State.Chasing) SetState(State.Chasing);
                    if (Time.time >= nextRepath)
                    {
                        nextRepath = Time.time + repathInterval;
                        if (_agent.enabled && _agent.isOnNavMesh) _agent.SetDestination(target);
                    }
                }
                yield return null;
            }
        }

        /// <summary>Starts a swing; returns the cooldown until the next one may start.</summary>
        float StartStrike(Vector3 target)
        {
            if (!_hasAnim) { StartCoroutine(Lunge(target)); return attackInterval; }
            // Fists: cross / hook / jab-cross. Knife in hand: slash / lunging stab. (Not Stabbing: its thrust turns the fist
            // pinky-forward, an ice-pick grip, so the forward-held knife points back at him — see Logs/poses/Stabbing.png.)
            var kind = (IntruderAnimator.AttackKind)(_weapon != null && _weapon.InHand ? UnityEngine.Random.Range(4, 6) : UnityEngine.Random.Range(0, 3));
            StartCoroutine(AnimatedStrike(kind));
            return Mathf.Max(attackInterval, IntruderClips.AttackLength[(int)kind] * IntruderClips.OneShotExit + 0.15f);
        }

        int _reactions; // bumped by every reaction/fall: a swing in its wind-up checks it and never lands if he got hit

        IEnumerator AnimatedStrike(IntruderAnimator.AttackKind kind)
        {
            int k = (int)kind;
            int reactionsAtStart = _reactions;
            _anim.Attack(kind);
            LockMovement(Lock.Strike, IntruderClips.AttackLength[k] * IntruderClips.OneShotExit);
            float contact = IntruderClips.AttackContact[k], t = 0;
            while (t < contact)
            {
                t += Time.deltaTime;
                if (_reactions != reactionsAtStart) yield break; // shot mid-swing: the blow never lands
                if (t < contact * 0.8f)
                {
                    var to = Flat(PlayerOnFloor() - transform.position);
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to, Vector3.up), 8f * Time.deltaTime);
                }
                yield return null;
            }
            if (_reactions != reactionsAtStart) yield break; // hit on the contact frame itself
            if (Current is not (State.Chasing or State.Attacking)) yield break; // gave up / ran mid-swing
            Debug.Log($"[HIS] Intruder: ATTACK {kind}");
            Sfx.Play("intruder.swing", transform.position + Vector3.up * 1.3f, 0.8f);
            var toPlayer = PlayerOnFloor() - transform.position; toPlayer.y = 0;
            bool lands = toPlayer.magnitude <= attackRange + 0.35f && Vector3.Angle(transform.forward, toPlayer) < 60f
                         && ReachesPlayerFromHere();   // (outside: never round the jamb through the wall, 0.1.53)
            // The knife clips (slash / lunging stab, at your body) kill with one blow (Brett 0.1.17); punches (at your face) cost hit points.
            if (lands && PlayerState.Instance != null)
            {
                if (kind >= IntruderAnimator.AttackKind.Stab) PlayerState.Instance.TakeHit(HitZone.Torso, HitBy.Knife, transform.position);
                else PlayerState.Instance.TakeHit(HitZone.Head, HitBy.Fists, transform.position, attackDamage);
            }
        }

        /// <summary>A blow from his hand right now: the knife if he's holding it, else his fist.</summary>
        HitBy MeleeHitBy => _weapon != null && _weapon.InHand && _weapon.Kind == HIS.Scenario.WeaponKind.Knife ? HitBy.Knife : HitBy.Fists;

        const float FloorLungeTime = 0.7f, FloorLungeSpeed = 1.2f, FloorLungeReach = 0.4f;
        bool _floorLunged, _calledOut;
        /// <summary>His taunt in his own voice (knife man / home invader), or null (no take: he stays quiet).</summary>
        string TauntKey => _intent == HIS.Scenario.Intent.KnifeAttacker ? "vo.knife.taunt" : _intent == HIS.Scenario.Intent.MeleeAttacker ? "vo.attacker.taunt" : null;

        /// <summary>
        /// Legs out, you within reach: he claws forward at your legs — a burst of the crawl's reach (the cycle at ~3× for 0.7 s,
        /// both arms swiping forward, the knife hand among them) carrying him up to 0.4 m closer, then the blade at your shins,
        /// or, empty-handed, a grab and a yank at your ankle (fist damage). The first one comes with a snarl. No standing attack clip from the floor (they'd stand his torso up). Takes the Strike
        /// lock, so the judge sees an attack in progress (IntruderFacts.Attacking); a shot mid-lunge ends it.
        /// </summary>
        IEnumerator FloorLunge()
        {
            int reactionsAtStart = _reactions;
            LockMovement(Lock.Strike, FloorLungeTime);
            bool knife = MeleeHitBy == HitBy.Knife;
            Debug.Log($"[HIS] Intruder: floor lunge ({(knife ? "knife" : "grab")}, legs out)");
            if (!_floorLunged) // in his own voice only (a cornered burglar has no taunt take: he stays quiet)
            {
                _floorLunged = true;
                Say(TauntKey);
            }
            float t = 0f, moved = 0f;
            while (t < FloorLungeTime)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                t += Time.deltaTime;
                if (_reactions != reactionsAtStart || Current != State.Attacking) break;
                _anim.SetSpeedOverride(FloorLungeSpeed); // GaitRate = speed / CrawlNatural: the arms swipe forward fast
                var to = Flat(PlayerOnFloor() - transform.position);
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to, Vector3.up), 6f * Time.deltaTime);
                float step = Mathf.Min(FloorLungeReach / FloorLungeTime * Time.deltaTime, FloorLungeReach - moved);
                if (step > 0f && to.magnitude > 0.55f && _agent.enabled && _agent.isOnNavMesh) { _agent.Move(to.normalized * step); moved += step; }
                yield return null;
            }
            _anim.SetSpeedOverride(null);
            if (_reactions != reactionsAtStart || Current != State.Attacking) yield break;
            var toPlayer = PlayerOnFloor() - transform.position; toPlayer.y = 0;
            bool lands = toPlayer.magnitude <= attackRange + 0.6f && Vector3.Angle(transform.forward, toPlayer) < 60f && ReachesPlayerFromHere();
            Debug.Log($"[HIS] Intruder: ATTACK {(knife ? "FloorSlash" : "FloorGrab")} ({(lands ? "lands" : "short")})");
            Sfx.Play("intruder.swing", transform.position + Vector3.up * 0.3f, 0.7f);
            if (lands && PlayerState.Instance != null) PlayerState.Instance.TakeHit(HitZone.Leg, MeleeHitBy, transform.position, attackDamage * 0.7f);
        }

        IEnumerator Lunge(Vector3 target)
        {
            // Placeholder capsule body only: wind-up, fast swing + lunge, damage at contact, recover.
            var start = transform.position;
            var dir = Flat(target - start).normalized;
            var arm = strikingArm != null ? strikingArm : transform.Find("RightArm");
            Quaternion armRest = arm != null ? arm.localRotation : Quaternion.identity;
            Vector3 armRestPos = arm != null ? arm.localPosition : Vector3.zero;

            float t = 0, windUp = 0.28f, strike = 0.11f, recover = 0.35f;
            while (t < windUp)
            {
                t += Time.deltaTime; float k = t / windUp;
                if (arm != null) { arm.localRotation = armRest * Quaternion.Euler(70f * k, 0, 0); arm.localPosition = armRestPos + new Vector3(0, 0.12f * k, -0.15f * k); }
                transform.position = start - dir * (0.08f * k);
                yield return null;
            }
            t = 0;
            while (t < strike)
            {
                t += Time.deltaTime; float k = t / strike;
                if (arm != null) { arm.localRotation = armRest * Quaternion.Euler(Mathf.Lerp(70f, -95f, k), 0, 0); arm.localPosition = armRestPos + new Vector3(0, Mathf.Lerp(0.12f, 0.05f, k), Mathf.Lerp(-0.15f, 0.35f, k)); }
                transform.position = start + dir * (lungeDistance * k);
                yield return null;
            }
            Debug.Log("[HIS] Intruder: ATTACK");
            Sfx.Play("intruder.swing", transform.position + Vector3.up * 1.3f, 0.8f);
            if (PlayerState.Instance != null && FlatDistance(PlayerOnFloor(), transform.position) <= attackRange + 0.35f && ReachesPlayerFromHere())
                PlayerState.Instance.TakeHit(HitZone.Torso, MeleeHitBy, transform.position, attackDamage);
            t = 0;
            while (t < recover)
            {
                t += Time.deltaTime; float k = t / recover;
                if (arm != null) { arm.localRotation = Quaternion.Slerp(armRest * Quaternion.Euler(-95f, 0, 0), armRest, k); arm.localPosition = Vector3.Lerp(armRestPos + new Vector3(0, 0.05f, 0.35f), armRestPos, k); }
                transform.position = Vector3.Lerp(start + dir * lungeDistance, start, k);
                yield return null;
            }
            if (arm != null) { arm.localRotation = armRest; arm.localPosition = armRestPos; }
            transform.position = start;
        }

        // ───────────────────────────── damage ─────────────────────────────
        // Health drops per hit immediately (so the killing pellet kills); the reaction is resolved once per frame in LateUpdate.
        struct PendingHit
        {
            public bool Any;
            public float Total, Legs, Biggest;
            public BodyPart Part;
            public DamageInfo Info;
        }
        PendingHit _pending;
        Vector3 _lastHitDir = Vector3.forward;
        BodyPart _lastPart = BodyPart.Torso;

        public void TakeDamage(float amount) => TakeDamage(amount, BodyPart.Torso, default);

        int _limbRounds, _limbRoundFrame = -1;

        /// <summary>
        /// Lethal rounds (Hitbox.LethalRounds): what a bullet in an arm or leg does. The first shot that hits a limb (all the
        /// pellets of one blast count as one shot) is a real wound — in a leg, enough to take his legs out; the second kills.
        /// </summary>
        public float LimbRound(BodyPart part, float applied)
        {
            if (Time.frameCount != _limbRoundFrame) { _limbRoundFrame = Time.frameCount; _limbRounds++; }
            if (_limbRounds >= 2) return Mathf.Max(applied, 1000f);
            bool leg = part is BodyPart.LeftLeg or BodyPart.RightLeg;
            return leg ? Mathf.Max(applied, downAtLegDamage) : Mathf.Max(applied, 35f);
        }

        public void TakeDamage(float amount, BodyPart part, DamageInfo info)
        {
            if (Current == State.Dead) return;
            // Ricky: his rig wakes before anything reads or plays on it (research/27 §3), and whose round it was is kept for
            // his death (a squad's kill loses the night; the player's is the night's "you shot Ricky" card).
            if (_protectee) ProtecteeHit(info);
            // Evidence: what he was doing just BEFORE this burst of force landed (all pellets of one frame = one event).
            // Behind the closed door the player saw a door, not him (Judge rule 0): only whether he was kicking it in counts.
            if (!_pending.Any)
            {
                _forceFacts = BuildFacts(); _forceKind = KindOf(info.Type);
                _forceFacts.BehindDoor = BehindDoor;
                _forceFacts.ForcingDoor = ForcingDoorNow;
                // Still out on the path, but seen: a round at full energy came through the doorway the player opened himself
                // (one through the leaf has less), where the open door shows the outside. Judged on what he saw — armed and
                // coming, still outside, running away — not as "fired through a closed door" (research/25).
                var door = BehindDoor && info.Type == DamageType.Bullet && info.Penetration >= 0.99f ? Door : null;
                if (door != null && door.OpenedByPlayer && door.IsOpen)
                { _forceFacts.BehindDoor = false; _forceFacts.ForcingDoor = false; _forceFacts.ForcedEntry = false; }
            }
            if (BehindDoor && Current is State.Outside or State.BreakIn) ShotThroughDoor = true;
            Health -= amount;
            if (!_pending.Any || amount > _pending.Biggest) { _pending.Biggest = amount; _pending.Part = part; _pending.Info = info; }
            _pending.Any = true;
            _pending.Total += amount;
            if (part is BodyPart.LeftLeg or BodyPart.RightLeg) _pending.Legs += amount;
            _lastPart = _pending.Part;
            if (_pending.Info.Direction.sqrMagnitude > 0.01f) _lastHitDir = _pending.Info.Direction;
            else if (info.Direction.sqrMagnitude > 0.01f) _lastHitDir = info.Direction;
            if (Health <= 0) { EmitForce(killed: true); _pending = default; Kill(); }
        }

        void LateUpdate()
        {
            if (!_pending.Any) return;
            var hit = _pending;
            EmitForce(killed: false);
            _pending = default;
            if (Current != State.Dead) ResolveHit(hit);
        }

        void ResolveHit(in PendingHit hit)
        {
            if (hit.Total >= 5f) _woundCount++; // the gunman's morale (IntruderGunman)
            bool officer = _intent == HIS.Scenario.Intent.Police;
            // SWAT (Brett 0.1.26: "improve their reactions to getting hit"): a round in the vest knocks him back, he stays in it.
            // A wave 3 hitman's plate covers the chest only (research/27 §2): a belly round is a real hit.
            bool vestStop = officer && Armored && hit.Total >= 5f && (hit.Part == BodyPart.Torso || (hit.Part == BodyPart.Abdomen && !IsHitman));
            if (vestStop) _vestStops++;
            // Shot through the closed door: a real scream (the Dia takes), muffled through the leaf (ShotAtTheDoor); in view, a pain grunt.
            // Brett 0.1.17: "the groaning a little louder" — full volume and a lighter muffle (1 kHz) through the door.
            // (Ricky's first wound is his own line, "Agh! I'm hit!", instead of the grunt: research/27 §3.)
            if (hit.Total >= 5f && !(_protectee && ProtecteeHitLine()))
                Cry(BehindDoor && Sfx.Has("intruder.pain.scream") ? "intruder.pain.scream" : "intruder.pain", BehindDoor ? 1.15f : 0.9f, DoorCryLowpass);
            // Out on the path (walk-up) he reacts in full too: the door camera shows it (Brett 0.1.22: "the characters arent
            // reacting well to being shot ... especially when theyre farther down the path").
            // (Not in a car seat: every hit reaction is a standing clip — it would stand him up through the roof. He takes it sitting.)
            bool canReact = !Riding && (Current is State.Entering or State.Chasing or State.Attacking or State.Idle or State.Fleeing or State.Complying
                            || (Current == State.Outside && OnPorchPath)
                            || (Current == State.BreakIn && _intent == HIS.Scenario.Intent.Police) // stacked: the camera shows it
                            || (Current == State.BreakIn && !BehindDoor)); // in view at the door (it burst, or the player opened it on him)
            bool blunt = hit.Info.Type == DamageType.Blunt;

            _legDamage += hit.Legs;
            // Hit out of sight behind the closed door: a second later his blood comes under it (a small seep; legs out, sliding
            // down the leaf: more). Brett 0.1.17.
            // (Not off a hitman's plate: it stopped the round, no blood — research/27 §2.)
            if (BehindDoor && _wounds != null && hit.Total >= 5f && !(vestStop && IsHitman))
                _wounds.SeepUnderDoor(_legDamage >= downAtLegDamage ? WoundSystem.DoorSeep.Legs : WoundSystem.DoorSeep.Hit, _floorY);
            // Legs out: he goes down and crawls. Already lying face down or sitting, he stays there (he just can't get up again).
            if (_gait is not (Gait.Crawl or Gait.Prone or Gait.Sit) && !Riding && _legDamage >= downAtLegDamage) { LegsOut(canReact); return; }
            if (_gait is Gait.Normal or Gait.Calm or Gait.Drunk or Gait.Sneak && _legDamage >= limpAtLegDamage) SetGait(Gait.Limp);

            if (!canReact) return;
            // On the floor or his knees (or mid-way down/up): a flinch, no standing reaction.
            if (!Upright(_gait) || Falling) { _reactions++; LockMovement(Lock.React, 0.5f, startsClip: false); return; } // (ends a floor lunge)
            CancelAct(); // a hit ends any gesture
            if (!_hasAnim)
            {
                if (blunt) LockMovement(Lock.React, 0.6f, startsClip: false);
                return;
            }
            if (hit.Total < reactMinDamage)
            {
                if (blunt) { LockMovement(Lock.React, 0.35f, startsClip: false); StartCoroutine(Knockback(hit.Info.Direction, 0.08f)); }
                return;
            }
            if (Reacting && _lock == Lock.React && hit.Total < interruptDamage) return; // one reaction at a time: no stun-lock

            // The gunman with his pistol up flinches round it (PistolHit), whatever was hit; running, the plain reactions. An
            // officer really hit (a wound, or the vest taking a round) reels with the hit where it landed instead: the small
            // flinch read as "he didn't feel it" (Brett 0.1.26).
            bool officerReels = officer && hit.Total >= 20f;
            var kind = _gait == Gait.Gun && _hasAnim && _anim.HasPistol && !_fleeingNow && !officerReels ? IntruderAnimator.HitKind.Pistol : hit.Part switch
            {
                BodyPart.Head or BodyPart.Neck => IntruderAnimator.HitKind.Head,
                BodyPart.Abdomen => IntruderAnimator.HitKind.Gut,
                BodyPart.LeftLeg or BodyPart.RightLeg => IntruderAnimator.HitKind.Legs,
                _ => IntruderAnimator.HitKind.Body,
            };
            _anim.Hit(kind);
            _reactions++;
            LockMovement(Lock.React, IntruderClips.HitLength[(int)kind] * IntruderClips.OneShotExit);
            float push = blunt ? 0.12f : hit.Info.Type == DamageType.Bullet ? (officerReels ? 0.2f : 0.06f) * Mathf.Clamp01(hit.Total / 40f) : 0f;
            if (push > 0f) StartCoroutine(Knockback(hit.Info.Direction, push));
        }

        /// <summary>Legs shot out: he pitches forward onto his hands, face down (as far as there's floor for), then crawls.</summary>
        void LegsOut(bool canReact)
        {
            _reactions++;
            bool falling = Falling, upright = Upright(_gait) && !falling;
            var was = _gait;
            CancelAct();
            SetGait(Gait.Crawl);
            // Hands up and now on the floor: face down is his surrender (RaiseHands' own rule) — else Surrender's custody check
            // (prone / kneeling / hands up on his feet) could never pass and the round waited out its timeout (audit 0.1.46).
            if (_posture == HIS.Scenario.Posture.HandsUp) { _posture = HIS.Scenario.Posture.Prone; _proneAt = Time.time; }
            if (_hasAnim) _anim.SetHandsOnHead(false);
            _anim_HandsDown(); // no hands-up IK on a body going face-down
            // On his feet: the fall onto his hands (Fall_LegsOut), its lunge along the clip's travel (8° right of his facing) cut
            // to the floor there is: walls/furniture (FreeDistance), the player standing in it (he's usually coming at you: he goes
            // down short of your feet), on the doorway link or no agent: in place. The NavMesh edge clamps the rest (agent.Move).
            var travel = transform.TransformDirection(new Vector3(IntruderClips.FallTravel.x, 0f, IntruderClips.FallTravel.y));
            var dir = travel.normalized;
            float full = travel.magnitude, stumble = full + IntruderClips.FallBodyAhead;
            float clear = FreeDistance(dir, 0.3f, stumble), room = clear;
            var toPlayer = PlayerOnFloor() - transform.position; toPlayer.y = 0f;
            float ahead = Vector3.Dot(toPlayer, dir);
            bool playerInPath = ahead > -0.3f && ahead < stumble + 0.3f && (toPlayer - dir * ahead).magnitude < 0.6f;
            if (playerInPath) room = Mathf.Min(room, ahead - 0.3f);
            if (_hasAnim && canReact && upright)
            {
                float share = _agent.enabled && _agent.isOnNavMesh && !_agent.isOnOffMeshLink ? Mathf.Clamp01((room - IntruderClips.FallBodyAhead) / full) : 0f;
                _anim.Fall(IntruderAnimator.FallKind.LegsOut, share);
                StartCoroutine(FloorThud(0.8f));
                LockMovement(Lock.Fall, IntruderClips.FallOnFloor + 0.1f);
                Debug.Log(share > 0.99f ? "[HIS] Intruder: legs shot out — stumbles and falls"
                    : $"[HIS] Intruder: legs shot out — falls {(share < 0.01f ? "in place" : $"short ({share * 100f:F0}% of the stumble)")} (clear {clear:F2}/{stumble:F2} m{(clear < stumble ? " blocked by " + _lastBlocker : "")}, player {(playerInPath ? "in the way" : "clear")}{(_agent.enabled ? "" : ", no agent")})");
            }
            else
            {
                // Already down (kneeling, mid-fall), at the door or no rig: he drops where he is (Gait 2 cuts any one-shot → Crawl).
                LockMovement(Lock.Fall, 0.8f);
                StartCoroutine(FloorThud(0.7f));
                Debug.Log($"[HIS] Intruder: legs shot out — drops in place ({(upright ? Current.ToString() : falling ? "mid-fall" : was.ToString())})");
            }
        }

        IEnumerator Knockback(Vector3 dir, float metres)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) yield break;
            dir.Normalize();
            float t = 0, done = 0;
            const float duration = 0.12f;
            while (t < duration && Current != State.Dead)
            {
                t += Time.deltaTime;
                float target = metres * Mathf.Clamp01(t / duration);
                if (_agent.enabled && _agent.isOnNavMesh && !_agent.isOnOffMeshLink) _agent.Move(dir * (target - done)); // NavMesh-clamped: never into a wall
                done = target;
                yield return null;
            }
        }

        /// <summary>Re-target the agent when the room NavMesh is rebuilt with a new agent type.</summary>
        public void SetAgentType(int agentTypeId)
        {
            if (_agent == null) return;
            bool wasEnabled = _agent.enabled;
            if (wasEnabled) _agent.enabled = false;
            _agent.agentTypeID = agentTypeId;
            if (wasEnabled && Current != State.Dead)
            {
                _agent.enabled = true;
                if (NavMesh.SamplePosition(transform.position, out var hit, 1f, NavMesh.AllAreas)) _agent.Warp(hit.position);
                _agent.speed = GaitSpeed;
                if (_agent.isOnNavMesh) _agent.isStopped = MovementLocked;
            }
        }

        // ───────────────────────────── death ─────────────────────────────
        public void Kill()
        {
            if (Current == State.Dead) return;
            if (_protectee) WakeProtectee(); // (killed without a hit, e.g. a dev kill: the death clip needs his rig awake)
            bool atDoor = BehindDoor && Current is State.Outside or State.BreakIn && !OnPorchPath; // (out on the path: dies where he is)
            // Bursts the door only if he's still at the kicks on his feet: a latch already weakened by a kick, a body falling
            // into it. On the floor (legs shot out) or turning away shot, he drops behind it (a thump) and stays out of sight.
            bool kicking = atDoor && _kicking && _kicksLanded > 0 && Upright(_gait) && !Falling && !_tripping; // before StopAllCoroutines ends the kicks
            // Shot getting out of a car once he's coming up off the seat (a leg out of the door, his head through it — clip
            // ≥ ExitRiseFrom), not in his seat: no longer a rider. He dies on the street from where he is (Collapse's deaths by his
            // hips' height, clear of the car and its open door: FreeDistance), no horn for him, his door not blocked for the back
            // seat, his body a street one (2026-10-05: shot as he stood up beside his door, the seat death froze him leaning there,
            // head 1.2 m up, with the horn blaring).
            if (Riding && _exiting && _exitClipNow >= IntruderClips.ExitRiseFrom)
            { Riding = false; _floorY = _exitGround; _settleOnFloor = true; }
            StopAllCoroutines();
            _pending = default;
            // Mid-stumble/trip the agent stays on (stopped) until he's on the floor, so the fall's root motion still carries him.
            _dyingMidFall = (_gait == Gait.Crawl || _tripping) && Falling;
            if (_tripping && _gait != _fallingTo) SetGait(_fallingTo); // killed the very frame he tripped: still land (no get-up)
            if (_agent.enabled && !_dyingMidFall) _agent.enabled = false;
            if (_hasAnim) _anim.SetHandsOnHead(false);
            _raisingGun = false;
            SetState(State.Dead);
            _anim_HandsDown();
            StopVoice(0.1f); // no finishing his sentence over his own death (also a line muffled through the door)
            if (atDoor) DieAtTheDoor(kicking); // before the drop: a burst door shows him (and the knife he held) coming down
            // Outside there's no floor collider (the room's floor ends at the door): a dropped knife would fall through the
            // void box, so at the door or behind it — or in view out on the step, the door open — he goes down with it still in his fist.
            if (_weapon != null && _weapon.InHand && !atDoor && !BehindDoor && !OnTheStepInView) _weapon.Drop(transform.forward * 0.8f + Vector3.up * 0.5f);
            // Dead out of sight behind the closed door: no stray round may hit (and confirm) an unseen body; no pool out there
            // (FinishDeath) — his blood comes under the door to us instead, a second later.
            if (BehindDoor) SetHittable(false);
            if (BehindDoor && _wounds != null) _wounds.SeepUnderDoor(WoundSystem.DoorSeep.Kill, _floorY);
            Cry("intruder.death", 1f, DoorCryLowpass);
            Died?.Invoke(this);
            StartCoroutine(Collapse());
        }

        bool _dyingMidFall, _tripping; // _tripping: a drunk trip / stagger onto his backside is carrying him to the floor
        bool _settleOnFloor;           // shot getting out of a car, out of its body: his root settles onto the road as he falls

        /// <summary>
        /// Shot dead through the closed door. Mid-kick (the latch already took a kick): his weight bursts it and he pitches
        /// forward into the doorway, in view. Otherwise a heavy thud against the leaf and he stays out of sight.
        /// </summary>
        void DieAtTheDoor(bool kicking)
        {
            _forcingDoor = _kicking = false;
            var door = Door;
            if (door == null) return;
            if (door.IsOpen) { SetVisible(true); return; } // shot as the leaf swung open (quiet entry): nothing hides him now
            if (kicking && _hasAnim)
            {
                door.Breach(320f); // slower than a kick: a falling body, not a foot
                SetVisible(true);
                _slumpIn = true;
                Debug.Log("[HIS] Intruder: shot dead mid-kick, the door bursts and he falls in");
            }
            else door.Thump(1f, 1100f); // his weight against the leaf, dull through it; the death cry, the floor thud, then silence
        }

        IEnumerator Collapse()
        {
            if (!_hasAnim) { Debug.Log("[HIS] Intruder: death Placeholder (capsule topples)"); yield return PlaceholderCollapse(); yield break; }
            if (Riding && Upright(_gait))
            {
                // On his feet in a truck's box (the team walking out of the back, IntruderCarExit.HopOutTheBack): back down onto its
                // floor where he stands, into the box — not a seated slump (it would leave him standing, leaning), nor a fall out over
                // the back. Moves with the truck from here on.
                _anim.Die(IntruderAnimator.DeathKind.Backward);
                StartCoroutine(FloorThud(1f));
                Debug.Log("[HIS] Intruder: death Backward (on his feet in the truck's box)");
                yield return new WaitForSeconds(IntruderClips.DeathLength[(int)IntruderAnimator.DeathKind.Backward] + 0.1f);
                yield return EnsureDown(IntruderAnimator.DeathKind.Backward);
                FinishDeath();
                yield break;
            }
            if (Riding)
            {
                // Shot in a police car's seat: he slumps forward onto the wheel/dash where he sits (a death clip would carry
                // him through the car's body). Moves with the car from here on.
                var r0 = transform.localRotation;
                var r1 = r0 * Quaternion.Euler(30f, UnityEngine.Random.Range(-12f, 12f), UnityEngine.Random.Range(-10f, 10f));
                for (float s = 0f; s < 0.45f; s += Time.deltaTime) { transform.localRotation = Quaternion.Slerp(r0, r1, Mathf.SmoothStep(0f, 1f, s / 0.45f)); yield return null; }
                transform.localRotation = r1;
                Debug.Log("[HIS] Intruder: death in his seat (slumped)");
                FinishDeath();
                yield break;
            }
            if (_gait == Gait.Crawl || _dyingMidFall)
            {
                // Legs already gone / going down anyway: let the stumble/drop reach the floor, then he dies where he lies.
                // Crawling (or face down after a trip): ProneDeath, which starts propped on the elbows like the crawl and slumps
                // flat (Logs/poses/Ctl_CrawlDeath.png); sitting after a stagger: slumps onto his back. 0.1.10 had no death clip
                // here: the crawl cycle just stopped and the rig slept in it, so every legs-first kill left a corpse frozen in
                // a push-up (Ctl_CrawlFreeze.png) — Brett: "how they're falling after dying has completely changed and broke".
                bool midFall = !_anim.OnFloor;
                _anim.SetSpeedOverride(0f);
                if (midFall) StartCoroutine(FloorThud(0.8f)); // killed mid-fall: the thud still comes when he lands
                float giveUp = Time.time + 2.5f;
                while (!_anim.OnFloor && Time.time < giveUp) yield return null;
                if (_agent.enabled) _agent.enabled = false;   // the fall's travel is done: nothing may slide the body now
                var lying = _gait == Gait.Sit ? IntruderAnimator.DeathKind.OnBack : IntruderAnimator.DeathKind.Prone;
                _anim.Die(lying);
                Debug.Log($"[HIS] Intruder: death {lying} (from the {_gait}{(midFall ? ", after the fall" : "")})");
                yield return new WaitForSeconds(IntruderClips.DeathLength[(int)lying] + 0.1f);
                yield return EnsureDown(lying);
                FinishDeath();
                yield break;
            }

            IntruderAnimator.DeathKind kind; float turn; Vector3 shift;
            // Shot dead against the door he was kicking: face down forward into the doorway, if the floor just inside is clear
            // (a narrow probe: the jambs are closer than the clip's full width).
            const int slump = (int)IntruderAnimator.DeathKind.Front;
            if (_slumpIn && FreeDistance(FallDirection(slump, 0f), 0.3f, IntruderClips.DeathReach[slump] + 0.1f) >= IntruderClips.DeathReach[slump] + 0.1f)
            { kind = IntruderAnimator.DeathKind.Front; turn = 0f; shift = Vector3.zero; Debug.Log("[HIS] Intruder: death Front (into the doorway)"); }
            else kind = ChooseDeath(out turn, out shift);
            _anim.Die(kind);
            if (kind is not (IntruderAnimator.DeathKind.Prone or IntruderAnimator.DeathKind.OnBack)) StartCoroutine(FloorThud(1f)); // already down
            // Turn / slide toward open floor during the first part of the fall so the body lands clear of walls and furniture.
            // (Riding in a police car: he slumps in his seat, moving with the car — no world-space turn or slide.)
            var rot0 = transform.rotation; var rot1 = Quaternion.AngleAxis(turn, Vector3.up) * rot0;
            var pos0 = transform.position;
            float t = 0;
            const float turnTime = 0.3f, shiftTime = 0.8f;
            // (Shot getting out of a car, out of its body: his root, still on the exit's height path, settles onto the road too.)
            float settle = _settleOnFloor ? _floorY - pos0.y : 0f;
            _settleOnFloor = false;
            while (t < shiftTime && !Riding)
            {
                t += Time.deltaTime;
                transform.rotation = Quaternion.Slerp(rot0, rot1, Mathf.SmoothStep(0, 1, t / turnTime));
                transform.position = pos0 + shift * Mathf.SmoothStep(0, 1, t / shiftTime) + Vector3.up * (settle * Mathf.SmoothStep(0, 1, t / turnTime));
                yield return null;
            }
            yield return new WaitForSeconds(Mathf.Max(0f, IntruderClips.DeathLength[(int)kind] + 0.1f - t));
            yield return EnsureDown(kind);
            FinishDeath();
        }

        /// <summary>
        /// Belt and braces before the rig sleeps: the death clip must have put him on the floor (a corpse frozen standing is
        /// the worst thing the game can show — Brett 0.1.15). Still up: the death again, once, then sleep wherever he is.
        /// </summary>
        IEnumerator EnsureDown(IntruderAnimator.DeathKind kind)
        {
            if (!_hasAnim || _anim.Hips == null || IntruderAnimator.LegacyDieTrigger) yield break;
            float hips = _anim.Hips.position.y - _floorY;
            if (hips < 0.5f) yield break;
            Debug.LogWarning($"[HIS] Intruder: still on his feet after death {kind} (hips {hips:F2} m, {(_anim.InDeathClip ? "in" : "NOT in")} the death clip) — drops again");
            _anim.Die(kind);
            yield return new WaitForSeconds(IntruderClips.DeathLength[(int)kind] + 0.1f);
        }

        /// <summary>The thud of a body hitting the floor, when the hips actually get there (whatever clip is playing).</summary>
        IEnumerator FloorThud(float volume)
        {
            if (!_hasAnim || _anim.Hips == null) yield break;
            float prev = _anim.Hips.position.y, giveUp = Time.time + 4f;
            while (Time.time < giveUp)
            {
                yield return null;
                float y = _anim.Hips.position.y;
                if (y - _floorY < 0.38f && y < prev) { BodySound("body.fall", _anim.Hips.position, volume); yield break; }
                prev = y;
            }
        }

        void FinishDeath()
        {
            if (_agent.enabled) _agent.enabled = false;
            var hips = _hasAnim && _anim.Hips != null ? _anim.Hips.position : transform.position;
            // Behind his door (at it or out on the path) the pool is on the porch for the door camera only; in the room, ours.
            // In view out on the step (the door open), on the porch too: the headset sees it there through the doorway.
            int outsideLayer = LayerMask.NameToLayer("Outside");
            if (_wounds != null && !BehindDoor && !OnTheStepInView) _wounds.BleedOut(hips);
            else if (_wounds != null && outsideLayer >= 0 && !Riding) _wounds.BleedOutOutside(hips, _floorY, outsideLayer); // (not in a car seat)
            if (_hasAnim) _anim.Sleep();
            // Not Ricky (research/27 §3): with MaxCorpses 1 on a Marshal night, a hitman's body would evict his. His body stays
            // until the night clears the room.
            if (!_protectee) CorpseRegistry.Register(this);
        }

        IEnumerator PlaceholderCollapse()
        {
            var dir = Flat(_lastHitDir).normalized;
            var axis = Vector3.Cross(Vector3.up, dir).normalized;
            var startRot = transform.rotation; var startPos = transform.position;
            float t = 0, dur = 0.55f;
            while (t < dur)
            {
                t += Time.deltaTime; float k = Mathf.SmoothStep(0, 1, t / dur);
                transform.rotation = Quaternion.AngleAxis(88f * k, axis) * startRot;
                transform.position = startPos + dir * (0.45f * k);
                yield return null;
            }
            if (_wounds != null && !BehindDoor) _wounds.BleedOut(transform.position + dir * 0.3f);
            if (!_protectee) CorpseRegistry.Register(this); // (as FinishDeath)
        }

        static readonly float[] DeathTurns = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };
        static readonly int[] KneelDeathOrder = { (int)IntruderAnimator.DeathKind.Kneeling };

        /// <summary>
        /// Pick the death clip by where the blow came from (shot from the front → over backwards, from behind → face down),
        /// then keep the first one whose fall has clear floor. Tight spot: turn him up to 90° toward open floor; nothing fits:
        /// the roomiest fall plus a short slide away from the obstacle.
        /// </summary>
        IntruderAnimator.DeathKind ChooseDeath(out float turn, out Vector3 shift)
        {
            // Already low (by the hips, so a man half-way down or up gets the death that matches his body): face down or
            // sitting dies where he lies; on his knees he pitches forward from the knees (KneelDeath, turned toward open floor).
            float hips = _anim.Hips != null ? _anim.Hips.position.y - _floorY : 1f;
            if (hips < 0.3f)
            {
                turn = 0f; shift = Vector3.zero;
                var lying = _gait == Gait.Sit ? IntruderAnimator.DeathKind.OnBack : IntruderAnimator.DeathKind.Prone;
                Debug.Log($"[HIS] Intruder: death {lying} (on the floor)");
                return lying;
            }
            var dir = Flat(_lastHitDir).normalized;
            bool fromFront = Vector3.Dot(dir, transform.forward) < 0f; // blow travelling against his facing = hit in the front
            bool headshot = _lastPart is BodyPart.Head or BodyPart.Neck;
            bool coin = UnityEngine.Random.value < 0.5f;
            int[] order = hips < 0.72f ? KneelDeathOrder : fromFront
                ? (headshot ? new[] { 1, 2, 0, 4, 3 } : coin ? new[] { 2, 0, 1, 4, 3 } : new[] { 0, 2, 1, 4, 3 })
                : (coin ? new[] { 3, 4, 0, 2, 1 } : new[] { 4, 3, 0, 2, 1 });

            float bestScore = float.MinValue, bestTurn = 0, bestFree = 0; int bestK = order[0];
            foreach (var tr in DeathTurns)
            {
                foreach (var k in order)
                {
                    float need = IntruderClips.DeathReach[k] + 0.1f;
                    float free = FreeDistance(FallDirection(k, tr), IntruderClips.DeathHalfWidth[k], need);
                    if (free >= need)
                    {
                        turn = tr; shift = Vector3.zero;
                        Debug.Log($"[HIS] Intruder: death {(IntruderAnimator.DeathKind)k} turn {tr:F0}° (clear {free:F2} m)");
                        return (IntruderAnimator.DeathKind)k;
                    }
                    float score = free / need - Mathf.Abs(tr) / 900f;
                    if (score > bestScore) { bestScore = score; bestK = k; bestTurn = tr; bestFree = free; }
                }
            }
            turn = bestTurn;
            var fall = FallDirection(bestK, bestTurn);
            float deficit = IntruderClips.DeathReach[bestK] + 0.1f - bestFree;
            float behind = FreeDistance(-fall, 0.25f, deficit + 0.3f);
            shift = -fall * Mathf.Clamp(Mathf.Min(deficit, behind - 0.3f), 0f, 0.6f);
            Debug.Log($"[HIS] Intruder: death {(IntruderAnimator.DeathKind)bestK} turn {turn:F0}° tight (clear {bestFree:F2} m, slide {shift.magnitude:F2} m)");
            return (IntruderAnimator.DeathKind)bestK;
        }

        Vector3 FallDirection(int kind, float turnDegrees)
        {
            var h = IntruderClips.DeathHead[kind];
            return Quaternion.AngleAxis(turnDegrees, Vector3.up) * (transform.rotation * new Vector3(h.x, 0f, h.y).normalized);
        }

        static readonly float[] ProbeHeights = { 0.25f, 0.8f };
        static string _lastBlocker;
        static int _roomMask = -1;

        /// <summary>
        /// Clear floor along <paramref name="dir"/> from his feet, up to <paramref name="maxDist"/>: rays at shin and hip height,
        /// down the middle and at each shoulder, against the scanned room (walls with their door holes, furniture volumes)
        /// and the virtual door leaf.
        /// </summary>
        float FreeDistance(Vector3 dir, float halfWidth, float maxDist)
        {
            if (_roomMask < 0) _roomMask = LayerMask.GetMask("Room", "Door");
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return maxDist;
            dir.Normalize();
            var side = Vector3.Cross(Vector3.up, dir) * Mathf.Min(halfWidth * 0.75f, 0.6f);
            float free = maxDist;
            // Behind his closed door, falling toward the room: the door is a wall. (Neither a shut leaf nor the doorway —
            // the walls are cut there — has a collider, so the probe saw open floor and he fell through the closed door into
            // the room: Brett 0.1.30.)
            var door = Door;
            if (door != null && !door.IsOpen && BehindDoor)
            {
                var inward = door.Inward; inward.y = 0f;
                if (inward.sqrMagnitude > 1e-6f)
                {
                    inward.Normalize();
                    float toward = Vector3.Dot(dir, inward);
                    float gap = Vector3.Dot(door.Center - transform.position, inward); // metres to the door plane (he's outside: > 0)
                    if (toward > 0.05f && gap > -0.2f) free = Mathf.Min(free, Mathf.Max(0f, gap - 0.08f) / toward);
                }
            }
            _lastBlocker = null;
            foreach (var h in ProbeHeights)
            {
                for (int s = -1; s <= 1; s++)
                {
                    var origin = transform.position + Vector3.up * h + side * s;
                    if (Physics.Raycast(origin, dir, out var hit, maxDist, _roomMask, QueryTriggerInteraction.Ignore) && hit.distance < free)
                    {
                        free = hit.distance;
                        _lastBlocker = hit.collider.name;
                    }
                }
            }
            // Out on the street: the vehicles (their colliders are on the Outside layer, not the room's) and the leaves of their open
            // doors (no colliders at all). 2026-10-05: a man shot in the pocket between his open door and his car saw open floor all
            // round and could fall through the door or into the car's side. Plain math (PoliceCars.ClearOfVehicles), nothing near a
            // vehicle: no change.
            for (int s = -1; s <= 1; s++)
            {
                float v = HIS.Security.PoliceCars.ClearOfVehicles(transform.position + side * s, dir, free);
                if (v < free) { free = v; _lastBlocker = "a vehicle"; }
            }
            return free;
        }

        // ───────────────────────────── corpse ─────────────────────────────
        /// <summary>
        /// Evicted by <see cref="CorpseRegistry"/>: the body stays until the player isn't looking at it (or 25 s), then it and
        /// its blood pool go — no popping out of existence in front of him.
        /// </summary>
        public void Retire()
        {
            if (!isActiveAndEnabled) { Destroy(gameObject); return; }
            StartCoroutine(VanishWhenUnseen());
        }

        IEnumerator VanishWhenUnseen()
        {
            float giveUp = Time.time + 25f;
            while (Time.time < giveUp && InView()) yield return new WaitForSeconds(0.25f);
            BloodDecals.Release(gameObject);
            Destroy(gameObject);
        }

        static readonly Plane[] _frustum = new Plane[6];

        /// <summary>Any part of the body (renderer bounds + 0.3 m) inside the view frustum, not just one bone.</summary>
        bool InView()
        {
            var cam = Camera.main;
            if (cam == null || !_visible) return false; // behind his door: drawn only for the door camera
            _renderers ??= GetComponentsInChildren<Renderer>(true);
            bool any = false; Bounds b = default;
            foreach (var r in _renderers)
            {
                if (r == null || !r.enabled || !r.transform.IsChildOf(transform)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return false;
            b.Expand(0.6f);
            GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
            return GeometryUtility.TestPlanesAABB(_frustum, b);
        }

        // ───────────────────────────── helpers ─────────────────────────────
        void SetState(State s)
        {
            Current = s;
            if (s == State.BreakIn && !_visible) SetHittable(true); // at the door: the closed leaf doesn't stop a round
            Debug.Log($"[HIS] Intruder: {s}");
            StateChanged?.Invoke(s, this);
        }

        Vector3 PlayerOnFloor()
        {
            var p = _player != null ? _player.position : Vector3.zero;
            p.y = _floorY;
            return p;
        }

        static float FlatDistance(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Distance(a, b); }
        static Vector3 Flat(Vector3 v) { v.y = 0; return v.sqrMagnitude < 1e-6f ? Vector3.forward : v; }
    }
}
