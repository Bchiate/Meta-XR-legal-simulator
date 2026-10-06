using System;
using System.Collections;
using HIS.Core;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// Ricky, the protectee on a US Marshal night (research/27 §3): the informant the player guards at the safe house. He is
    /// one more Intruder (Alex in a light-grey sweatsuit, unarmed, no props), so the bone hitboxes, Brett's damage rule,
    /// wounds, deaths and captions all come for free; his brain is his own:
    ///   - placed crouched on his hiding spot (<see cref="HIS.Room.HideSpot"/>) facing the safe spot, his Animator asleep most
    ///     of the night (a CPU saving). He never walks, never follows you and takes no orders, so he can't spawn in you, walk
    ///     through you or get stuck;
    ///   - the first hitman car (the wave 1 call + 1.2 s: its engine, the car on the monitor): "That's not the relief." → he
    ///     ducks where he is → "Okay, okay. I'm down." → asleep again; each later car the same beat after its call, its wave's
    ///     own line (HitmanTier.RickyLine; Brett 0.1.57's five waves): "Another car." (wave 2), "They're back." (wave 3), "More
    ///     of them." (wave 4), "That's a van. Oh no." (wave 5);
    ///   - a bell or a knock (the night calls <see cref="HearTheDoor"/>): about half the time "Who's that? Nobody's supposed
    ///     to know.";
    ///   - the wave's finder in the room within 3 m of him: "No, no, please!" (he wakes for it: the finder's FinderPlea
    ///     calls <see cref="WakeProtectee"/> and his voice, IntruderHitman.cs);
    ///   - hit: the player's own rule (Hitbox: head or body kills, the first limb round wounds — "Agh! I'm hit!" — the
    ///     second kills).
    /// Dead: no CorpseRegistry (a hitman's body must never evict his); a squad's round (Source under an IntruderWeapon, the
    /// finder's) loses the night at once (<see cref="PoliceResponse.ProtecteeDied"/>); the player's own hits reach the night
    /// through <see cref="ForceApplied"/> (the "you shot Ricky" card). He is never the spawner's Active, never judged as a
    /// visitor, and he speaks only after a bell, a knock or a hitman car. Nothing he says names a door. Every line of this
    /// file is behind <see cref="IsProtectee"/>: Scenarios and Sandbox never spawn him.
    /// </summary>
    public partial class Intruder
    {
        // Voice keys (research/27 §5; the text lives in VoiceLines' table, the clips come from scripts/make_tts.py).
        const string RickyKnockKey = "vo.ricky.knock", RickyCarKey = "vo.ricky.car", RickyDownKey = "vo.ricky.down",
                     RickyHitKey = "vo.ricky.hit";   // (vo.ricky.beg: the finder says it for him, IntruderHitman.FinderPlea)

        /// <summary>After a hitman wave's call: its engine is heard and the car is on the monitor, then he sees it (research/27 §1,
        /// 4:30: "1.5 s later"; 1.2 s since 0.1.56 so his line sits on the engine swell, well before the men are out).</summary>
        const float CarDuckDelay = 1.2f;
        /// <summary>Ricky calls each later car too (Brett 0.1.56: "the bad guys seemingly come from no where"), in his own voice:
        /// the wave's own line (<see cref="HitmanTier.RickyLine"/>); a tier without one, this.</summary>
        const string RickyCarLaterKey = "vo.ricky.car2";
        /// <summary>Into "That's not the relief." he's already going down; "I'm down." comes this long after the first line ends.</summary>
        const float DuckIntoLine = 0.4f, DownPause = 0.9f;
        /// <summary>A bell or knock: the line about half the time (§1), never twice within this many seconds.</summary>
        const float KnockLineChance = 0.5f, KnockLineGap = 20f;
        /// <summary>Settled this long in his pose with nothing going on: the rig sleeps again.</summary>
        const float SleepAfter = 1f;
        /// <summary>His carve in the room NavMesh (he never moves): visitors and the squad path round him, not through him.</summary>
        const float CarveRadius = 0.35f;

        static Intruder s_protectee;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetProtecteeStatics() => s_protectee = null; // domain reload off

        /// <summary>Tonight's Ricky (alive or his body), or null when there's none (Scenarios, Sandbox, or he was cleared away).</summary>
        public static Intruder Protectee => s_protectee != null ? s_protectee : null; // (Unity's null: a destroyed one reads as none)

        bool _protectee;
        /// <summary>This is Ricky (research/27 §3), not a visitor or a squad member.</summary>
        public bool IsProtectee => _protectee;
        /// <summary>He has ducked (the first hitman car).</summary>
        public bool ProtecteeHidden => _protectee && _protecteeHidden;
        /// <summary>His rig is asleep (the Perf line: research/27 §6 step 12).</summary>
        public bool ProtecteeAsleep => _protectee && _hasAnim && _anim.Asleep;

        bool _protecteeHidden, _rickyHitLineSaid;
        float _protecteeStillSince, _nextKnockLine = -100f;
        /// <summary>Whose round hit him last: decides what his death means (see <see cref="OnProtecteeDied"/>).</summary>
        enum RickyHitBy { None, Player, Squad }   // (not "HitBy": that would hide HIS.Core.HitBy in every part of Intruder)
        RickyHitBy _rickyLastHit;

        /// <summary>
        /// Make this freshly spawned intruder Ricky (IntruderSpawner.SpawnProtectee): on his hiding spot <paramref name="spot"/>
        /// facing <paramref name="facing"/>, crouched and still. Not <see cref="Initialize"/>, which puts a man outside the door.
        /// <paramref name="outsidePoint"/>/<paramref name="insidePoint"/> are the front door's (his facts read "inside").
        /// </summary>
        public void InitializeProtectee(Vector3 spot, Vector3 facing, float floorY, Vector3 outsidePoint, Vector3 insidePoint, int agentTypeId, Transform player)
        {
            _protectee = true;
            s_protectee = this;
            DoorIndex = 0;                                   // (his voice is never behind a door: he's in the room)
            _outsidePoint = outsidePoint; _insidePoint = insidePoint;
            _floorY = floorY;
            _player = player;
            _agent.agentTypeID = agentTypeId;                // the agent stays off: he never walks
            // A man who never forced anything and never fights: no taunt, no door routine, no knife (Configure is not used).
            _intent = Intent.Surrenderer;
            _entry = EntryType.OpenedUnlocked;
            _speaker = "Ricky";
            _standGait = Gait.Calm;
            foreach (var w in _weapons) if (w != null) w.Show(false);
            _weapon = null;                                  // unarmed: his facts never show a weapon, his death drops nothing
            spot.y = floorY;
            transform.SetPositionAndRotation(spot, Quaternion.LookRotation(Flat(facing), Vector3.up));
            _prevPos = spot;
            if (_hasAnim) _anim.KeepStateWhenAsleep();        // before the first Sleep: woken, he stays crouched (IntruderAnimator)
            DressAsProtectee();                              // the grey sweats (IntruderPoliceLook)
            var carve = new GameObject("RickyCarve");
            carve.transform.SetParent(transform, false);
            var ob = carve.AddComponent<NavMeshObstacle>();  // (a child: never beside the NavMeshAgent on his root)
            ob.shape = NavMeshObstacleShape.Capsule; ob.radius = CarveRadius; ob.height = 1.2f; ob.center = new Vector3(0f, 0.6f, 0f);
            ob.carving = true; ob.carveOnlyStationary = true;
            Died -= OnProtecteeDied; Died += OnProtecteeDied;
            // In the room, drawn and hittable, crouched — all before this frame renders (the brain's first step may come a frame
            // later): no stand-to-crouch and no default standing pose, he was already hiding when the night began.
            SetState(State.Idle);
            SetVisible(true);
            var pose = ProtecteeGait(hidden: false);
            SetGait(pose);
            if (_hasAnim) { _anim.SnapGait(pose); _anim.Evaluate(); }
            _protecteeStillSince = Time.time;
            if (pose == Gait.Kneel) Debug.LogWarning("[HIS] Ricky: this Animator controller has no Crouch state (rebuild it: IntruderBuilder.Build) — kneeling instead");
            Debug.Log($"[HIS] Ricky (#{Id}): the protectee, at {spot:F2} facing {Flat(facing).normalized:F2}, {pose}");
            StopAllCoroutines();
            StartCoroutine(Run());                           // Run branches to ProtecteeBrain at its top
        }

        /// <summary>His rig evaluates again (asleep most of the night). Hits, his death, the duck and the finder's plea call it;
        /// he sleeps again once he has settled (<see cref="SleepWhenStill"/>).</summary>
        public void WakeProtectee()
        {
            if (!_protectee || !_hasAnim || !_anim.Asleep) return;   // (a body stays asleep: IntruderAnimator.Wake)
            _anim.Wake();
            _protecteeStillSince = Time.time;
            Debug.Log($"[HIS] Ricky: awake ({_gait})");
        }

        /// <summary>
        /// A bell or a knock at a door (research/27 §1, the courier's bell): about half the time "Who's that? Nobody's supposed
        /// to know." Not once he's ducked (the waves) and not twice within <see cref="KnockLineGap"/> s. He stays where he is.
        /// </summary>
        public void HearTheDoor()
        {
            if (!_protectee || Current == State.Dead || _protecteeHidden || Time.time < _nextKnockLine) return;
            _nextKnockLine = Time.time + KnockLineGap;
            if (UnityEngine.Random.value >= KnockLineChance) { Debug.Log("[HIS] Ricky: hears the door, stays quiet"); return; }
            Debug.Log("[HIS] Ricky: hears the door — \"Who's that?\"");
            Say(RickyKnockKey);
        }

        /// <summary>A bell or knock reached the safe house: tonight's Ricky reacts (<see cref="HearTheDoor"/>); no Ricky, nothing.</summary>
        public static void ProtecteeHearsTheDoor()
        {
            var ricky = Protectee;
            if (ricky != null) ricky.HearTheDoor();
        }

        // ───────────────────────────── the weapons check (the handover) ─────────────────────────────
        // Brett 2026-10-04: Ricky is in his spot from the start of the night, so he is there through the weapons check
        // (Scenario/MarshalBriefing): Ortiz introduces him over the radio, he answers once, and no practice round, blade or fist
        // can hurt him while it runs. After it he is himself again (research/27 §3: the player's rule, the night's card).

        /// <summary>His answer to Ortiz's handover ("So you're the new guy.") and his complaint at a practice hit ("Hey! Watch it!").</summary>
        public const string RickyNewGuyKey = "vo.ricky.newguy", RickyWatchItKey = "vo.ricky.watchit";
        /// <summary>"Hey! Watch it!" at most this often (a shotgun's nine pellets, a slash's re-hits: one complaint).</summary>
        const float WatchItGap = 2.5f;
        float _nextWatchIt = -100f;

        /// <summary>Practice hits on him that did nothing (the weapons check: tests, the log).</summary>
        public int ProtecteePracticeHits { get; private set; }

        /// <summary>His head (world), for a label naming him; his root + 1 m (crouched) without a rig.</summary>
        public Vector3 ProtecteeHeadPoint => _hasAnim && _anim.Head != null ? _anim.Head.position : transform.position + Vector3.up * 1.0f;

        /// <summary>He says <paramref name="key"/> from where he is (his voice, captioned "Ricky"); the handover's one line.
        /// Returns the line's length in seconds (0: no take yet — the caption still shows).</summary>
        public float ProtecteeSay(string key)
        {
            if (!_protectee || Current == State.Dead || key == null) return 0f;
            Debug.Log($"[HIS] Ricky: \"{VoiceLines.Text(key)}\"");
            WakeProtectee();                                 // (his rig for the line; asleep again once still: SleepWhenStill)
            Say(key);
            return Sfx.Length(key);
        }

        /// <summary>
        /// Every hit on any of his hitboxes asks first (Hitbox.ApplyDamage): while the weapons check runs
        /// (<see cref="MarshalBriefing.Running"/>) a round, a pellet, the knife or a fist does nothing to him — no wound, no blood,
        /// no damage, no force reported (so nothing is judged and the night never hears of it) — and he says so ("Hey! Watch
        /// it!", at most every <see cref="WatchItGap"/> s). True: the hit is used up here. False (any other time, any other
        /// man): a real hit.
        /// </summary>
        public bool PracticeHit(in DamageInfo info)
        {
            // Deputy Ortiz in the room for the handover (Brett 0.1.57): practice-safe for as long as he's there (IntruderHandover).
            if (_handover) return HandoverPracticeHit(info);
            if (!_protectee || Current == State.Dead || !MarshalBriefing.Running) return false;
            ProtecteePracticeHits++;
            if (Time.time < _nextWatchIt) return true;
            _nextWatchIt = Time.time + WatchItGap;
            Debug.Log($"[HIS] Ricky: a practice {info.Type} hit him during the weapons check — no harm, nothing judged (\"Hey! Watch it!\")");
            WakeProtectee();
            Say(RickyWatchItKey);
            return true;
        }

        // ───────────────────────────── his brain ─────────────────────────────

        /// <summary>
        /// Crouched and still on his spot until the first hitman car; then the duck. The rig sleeps whenever he has settled in
        /// his pose for a second (a hit, the plea or his death wake it).
        /// </summary>
        IEnumerator ProtecteeBrain()
        {
            float carAt = -1f;                               // (placed and posed already: InitializeProtectee)
            int calledWave = 1, laterWave = 0;               // wave 1's car is the duck's; each later one gets its own line
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; } // paused
                if (!_protecteeHidden)
                {
                    if (carAt < 0f && FirstCarCalled()) carAt = Time.time;
                    if (carAt >= 0f && Time.time >= carAt + CarDuckDelay) { yield return DuckDown(); continue; }
                }
                else
                {
                    // Waves 2–5: he's already down; he calls each car once, the same beat after its call as the first.
                    int w = HitmanWaveNow();
                    if (w > calledWave && laterWave == 0) { laterWave = w; carAt = Time.time; }
                    if (laterWave > 0 && Time.time >= carAt + CarDuckDelay) { AnotherCar(laterWave); calledWave = laterWave; laterWave = 0; }
                }
                SleepWhenStill();
                yield return null;
            }
        }

        /// <summary>The hitman wave out now (0: none): a hitman run that is active.</summary>
        static int HitmanWaveNow()
        {
            var pr = PoliceResponse.Instance;
            return pr != null && pr.Active && pr.Faction == SquadFaction.Hitmen ? pr.Wave : 0;
        }

        /// <summary>
        /// A later hitman car, its wave's line (<see cref="HitmanTier.RickyLine"/>: "Another car.", "They're back.", "More of
        /// them.", "That's a van. Oh no."): he says it where he lies, no move.
        /// </summary>
        void AnotherCar(int wave)
        {
            var key = HitmanTier.For(wave).RickyLine;
            if (string.IsNullOrEmpty(key) || key == RickyCarKey) key = RickyCarLaterKey;   // (the duck's line is wave 1's alone)
            Debug.Log($"[HIS] Ricky: another car (wave {wave}) — \"{VoiceLines.Text(key)}\"");
            WakeProtectee();                                 // (his rig for the line; he sleeps again once still: SleepWhenStill)
            Say(key);
        }

        void ProtecteeBrainFailed(Exception e) =>
            Debug.LogWarning($"[HIS] Ricky: his brain stopped ({e.GetType().Name}) — he stays where he is (hits and death still work)");

        /// <summary>A hitman wave has been called (the police after a bad call aren't "not the relief": he stays still for them).</summary>
        static bool FirstCarCalled()
        {
            var pr = PoliceResponse.Instance;
            return pr != null && pr.Active && pr.Faction == SquadFaction.Hitmen && pr.Wave >= 1;
        }

        /// <summary>"That's not the relief." — down where he is (no walk, no order from you) — "Okay, okay. I'm down."</summary>
        IEnumerator DuckDown()
        {
            _protecteeHidden = true;
            WakeProtectee();
            Debug.Log($"[HIS] Ricky: the first car — \"{VoiceLines.Text(RickyCarKey)}\", ducks where he is");
            Say(RickyCarKey);
            float line = Sfx.Length(RickyCarKey);
            if (line <= 0f) line = 1.4f;                      // no clip yet: about the line's length
            yield return new WaitForSeconds(DuckIntoLine);
            if (Current == State.Dead) yield break;
            SetGait(ProtecteeGait(hidden: true));
            yield return new WaitForSeconds(Mathf.Max(0f, line - DuckIntoLine) + DownPause);
            if (Current == State.Dead) yield break;
            Say(RickyDownKey);
            _protecteeStillSince = Time.time;
        }

        /// <summary>Settled in his pose for <see cref="SleepAfter"/> with nothing locking him: the rig sleeps (the pose stays).</summary>
        void SleepWhenStill()
        {
            if (!_hasAnim || _anim.Asleep) return;
            if (MovementLocked || !_anim.IsIn(_gait)) { _protecteeStillSince = Time.time; return; }
            if (Time.time - _protecteeStillSince < SleepAfter) return;
            _anim.Sleep();
            Debug.Log($"[HIS] Ricky: asleep ({_gait})");
        }

        /// <summary>
        /// Crouched (waiting) or ducked (hidden). A controller built before the Crouch / Cower states has neither: he kneels
        /// (research/27 §3's fallback), never the default standing state.
        /// </summary>
        Gait ProtecteeGait(bool hidden)
        {
            var g = hidden ? Gait.Cower : Gait.Crouch;
            return !_hasAnim || _anim.HasGait(g) ? g : Gait.Kneel;
        }

        // ───────────────────────────── hits and death ─────────────────────────────

        /// <summary>
        /// Every hit on him (TakeDamage, before anything else): the rig wakes (a reaction, a fall or the death clip plays on it),
        /// and whose it was is kept — a squad's (DamageInfo.Source under an IntruderWeapon: the finder's rounds) or the
        /// player's (everything else: the same rule as the night's EmitForce filter, research/27 §3).
        /// </summary>
        void ProtecteeHit(in DamageInfo info)
        {
            WakeProtectee();
            _rickyLastHit = info.Source != null && info.Source.GetComponentInParent<IntruderWeapon>() != null ? RickyHitBy.Squad : RickyHitBy.Player;
        }

        /// <summary>His first wound: "Agh! I'm hit!" in his own voice instead of the pain grunt. False: the grunt (a later wound,
        /// or no clip yet — a missing take is silence, and a wound must be heard).</summary>
        bool ProtecteeHitLine()
        {
            if (_rickyHitLineSaid || !Sfx.Has(RickyHitKey)) return false;
            _rickyHitLineSaid = true;
            Say(RickyHitKey);
            return true;
        }

        /// <summary>
        /// Ricky is dead. Killed by a squad's round (or with no hit at all): the night is lost at once (PoliceResponse ends a
        /// hitman run as WitnessKilled; outside one it does nothing). Killed by the player: nothing here — the night's
        /// ForceApplied handler ("you shot Ricky") owns that card, and must not race a WitnessKilled ending.
        /// </summary>
        void OnProtecteeDied(Intruder who)
        {
            Debug.Log($"[HIS] Ricky: dead ({(_rickyLastHit == RickyHitBy.Squad ? "a squad round" : _rickyLastHit == RickyHitBy.Player ? "the player's hit" : "no hit")})");
            if (_rickyLastHit == RickyHitBy.Player) return;
            var pr = PoliceResponse.Instance;
            if (pr != null) pr.ProtecteeDied();
        }
    }
}
