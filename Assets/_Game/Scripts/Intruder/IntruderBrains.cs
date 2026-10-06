using System;
using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;
using Act = HIS.Intruder.IntruderAnimator.ActKind;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// The intruder's intents (research/13): what he does once he's through the door, and what he does when the player
    /// yells, aims or fires. The hidden intent never reaches the judge — only <see cref="Facts"/>, what the player could
    /// see and hear, does. Body (health, locks, gaits, death) lives in Intruder.cs; this half is behaviour + evidence.
    /// </summary>
    public partial class Intruder
    {
        static int _ids;
        public int Id { get; private set; }

        IntruderWeapon _weapon;          // the one this scenario arms him with (Configure); the knife by default
        IntruderWeapon[] _weapons;       // every prop on the rig (knife, pistol)
        ScenarioDef _def;
        Intent _intent = Intent.MeleeAttacker;
        EntryType _entry = EntryType.ForcedKick;
        Posture _posture = Posture.Standing;
        bool _identityStated, _fleeingNow;
        string _speaker = "Intruder";
        Gait _standGait = Gait.Calm;   // what he gets up into off his knees / the floor (the neighbour stays drunk)
        bool _floored;                 // knocked off his feet (a drunk trip, onto his backside): Down, not a surrender
        bool _tripPlanned, _sitDone;   // the neighbour's one rare trip this run; the stagger onto his backside happens once

        /// <summary>Tests (PoseTests): the next neighbour trips when he can / a close shot always puts him on his backside (room permitting).</summary>
        public static bool ForceTrip, ForceStumbleBack;

        /// <summary>He surrendered and stayed down long enough for the police to take him (scenario ends: custody).</summary>
        public bool InCustody { get; private set; }
        /// <summary>He left through the door (scenario ends: left).</summary>
        public bool HasLeft => Current == State.Gone;
        public Intent Intent => _intent;

        /// <summary>
        /// Mike, drunk: the Intent.Innocent man's sway, trip, stumbles, hiccups and the rattle at the handle. A def marked
        /// <see cref="ScenarioDef.Sober"/> (a Marshal night's visitors) is an Innocent without them (research/27 §6 step 8).
        /// </summary>
        bool IsDrunk => _intent == Intent.Innocent && (_def == null || !_def.Sober);
        /// <summary>His own voice's key prefix (ScenarioDef.VoicePrefix: "deputy", "brother", "courier"…); null = Mike's lines.</summary>
        string VisitorVoice => _def != null && !string.IsNullOrEmpty(_def.VoicePrefix) ? _def.VoicePrefix : null;
        /// <summary>
        /// A sober Innocent visitor with his own lines (Ortiz, Sal): he knocks, says who he is through the door and lets himself
        /// in with a key (<see cref="KnockAndAnnounce"/>), and his lines go through <see cref="VisitorVoice"/>.
        /// </summary>
        bool KeyedVisitor => _intent == Intent.Innocent && !IsDrunk && VisitorVoice != null && _entry == EntryType.OpenedUnlocked;
        /// <summary>
        /// A keyed visitor has said who he is (his line through the door, his hello, or "it's me" to your face): the judge's
        /// IdentityStated, and for a deputy KnownDeputy (research/27 §4). Mike's hello never set it, and still doesn't.
        /// </summary>
        bool _introduced;

        /// <summary>
        /// When he first spoke to the room once in (his hello, or "it's me" when challenged first); −1 before. The 2 AM relief's
        /// round ends <see cref="ScenarioDef.RelievedAfter"/> seconds after it (ScenarioDirector, research/27 §1).
        /// </summary>
        public float GreetedAt { get; private set; } = -1f;

        /// <summary>One use of force by the player (a shotgun blast = one event), with the facts from just before it.</summary>
        public event Action<Intruder, ForceEvent> ForceApplied;

        /// <summary>Set up the scenario role. Call before Initialize (the spawner does).</summary>
        public void Configure(ScenarioDef def)
        {
            _def = def;
            _intent = def.Intent;
            _entry = def.Entry;
            // A Marshal night's visitors name themselves on the caption (Ortiz, Sal, the courier: research/27 §5); the rest as before.
            _speaker = !string.IsNullOrEmpty(def.Speaker) ? def.Speaker
                     : def.Identity == Identity.Neighbor ? "Mike" : def.Identity == Identity.Family ? "Family" : "Intruder";
            foreach (var w in _weapons) if (w.Kind == def.Weapon) _weapon = w;
            foreach (var w in _weapons) w.Show(w == _weapon && def.Weapon != WeaponKind.None && w.Kind == def.Weapon);
            _morale = def.Morale >= 0f ? def.Morale : 0.5f; // m0 (research/08 §2c); the gunman reads it (IntruderGunman)
            switch (_intent)
            {
                case Intent.Burglar: SetGait(Gait.Sneak); break;       // creeps in crouched
                case Intent.Surrenderer: SetGait(Gait.Calm); break;
                case Intent.Gunman: SetGait(_weapon != null && _weapon.Kind == WeaponKind.Gun && _weapon.InHand ? Gait.Gun : Gait.Normal); break;
                // Drunk sway and stagger. A trip is funny once, not every time: at most one run in three. A sober visitor (a
                // Marshal night's Sal and Ortiz, research/27 §6 step 8) walks in calm and never trips.
                case Intent.Innocent when IsDrunk: SetGait(Gait.Drunk); _tripPlanned = ForceTrip || UnityEngine.Random.value < 0.33f; break;
                case Intent.Innocent: SetGait(Gait.Calm); break;
            }
            _standGait = IsDrunk ? Gait.Drunk : Gait.Calm;
            Debug.Log($"[HIS] Intruder #{Id}: {def.Name} ({_intent}, {_entry}, {def.Weapon})");
        }

        void OnEnable() => Firearm.Fired += OnGunshot;
        void OnDisable() => Firearm.Fired -= OnGunshot;
        void OnDestroy()
        {
            s_all.Remove(this);
            if (_weapon != null && _weapon.transform.parent == null) Destroy(_weapon.gameObject); // a dropped knife goes with him
            StopVoice(0f);
        }

        // ───────────────────────────── perception ─────────────────────────────
        readonly Queue<Command> _heard = new();
        float _gunshotAt = -100f, _aimedSince = -1f;
        Vector3 _gunshotFrom;

        /// <summary>The player yelled something (CommandSystem → ScenarioDirector → here).</summary>
        public void Hear(Command c)
        {
            if (Current is State.Dead or State.Gone) return;
            if (HearsThroughDoor) { HearThroughDoor(c); return; }   // pick #2: he answers through the closed door (IntruderDoorTalk)
            if (Current == State.Outside) return;
            if (Current is State.BreakIn or State.Entering) _heard.Clear(); // before his brain starts: only the latest yell counts
            _heard.Enqueue(c);
        }

        void OnGunshot(Vector3 from)
        {
            if (Current is State.Dead or State.Gone) return;
            if ((from - transform.position).sqrMagnitude < 15f * 15f) { _gunshotAt = Time.time; _gunshotFrom = from; }
        }

        Vector3 Chest => _hasAnim && _anim.Hips != null ? _anim.Hips.position + Vector3.up * 0.35f : transform.position + Vector3.up * 1.3f;
        bool AimedAt => PlayerAim.IsAimedAt(Chest);
        float AimedFor => _aimedSince < 0f ? 0f : Time.time - _aimedSince;
        bool GunshotRecently(float seconds) => Time.time - _gunshotAt < seconds;
        /// <summary>What the player has in hand, for his lines; someone who just fired has a gun whatever he's holstered since.</summary>
        PlayerWeapon PlayerHolds => GunshotRecently(3f) ? PlayerWeapon.Gun : PlayerAim.HeldWeapon;

        bool TryHear(out Command c) { if (_heard.Count > 0) { c = _heard.Dequeue(); return true; } c = Command.None; return false; }

        // ───────────────────────────── facts (evidence) ─────────────────────────────
        Vector3 _prevPos, _vel;
        float _lastThreat = -100f, _standingSince = -1f, _nextStep;
        IntruderFacts _forceFacts;
        ForceKind _forceKind;

        public IntruderFacts Facts => BuildFacts();

        void TrackFacts()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var p = transform.position;
            _vel = Vector3.Lerp(_vel, (p - _prevPos) / dt, 0.3f);
            _prevPos = p;
            if (Current is State.Dead or State.Gone) return;
            _aimedSince = AimedAt ? (_aimedSince < 0f ? Time.time : _aimedSince) : -1f;
            var f = BuildFacts();
            // The reaction grace runs from the last moment the judge would have called force a plain GOOD on its own (an
            // attack, a charge, a knife in reach, the break-in presumption), so recorder and judge can't drift apart. Close
            // calls don't refresh it, or the grace would erase them.
            var probe = f; probe.SecondsSinceThreat = Judge.NoThreatYet;
            var (tier, close, _) = Judge.JudgeFacts(in probe);
            if (tier == Tier.Good && !close) _lastThreat = Time.time;
            // Not while he's in punching range: a man hitting you isn't "standing there unarmed".
            bool standingUnarmed = f.Inside && f.WeaponInHand == WeaponKind.None && !f.Advancing && !f.Fleeing && !f.Down
                                   && !f.Attacking && Current != State.Attacking
                                   && _posture == Posture.Standing && Current != State.Entering;
            _standingSince = standingUnarmed ? (_standingSince < 0f ? Time.time : _standingSince) : -1f;
            // Footsteps for every brain (walk / run), not on the floor or while a fall / get-up carries him.
            float speed = new Vector3(_vel.x, 0, _vel.z).magnitude;
            if (Upright(_gait) && !Falling && speed > 0.25f && Time.time >= _nextStep && Current != State.Outside)
            {
                _nextStep = Time.time + Mathf.Clamp(0.55f / Mathf.Max(speed, 0.3f), 0.25f, 0.9f);
                var stepKey = speed > 2f ? "intruder.step.run" : "intruder.step";
                var shut = BehindDoor ? Door : null;
                // Running off behind the closed door (shot through it): muffled through the leaf, fading as he gets away.
                if (shut != null && !shut.IsOpen) shut.PlayThrough(stepKey, p, 0.55f * Mathf.Clamp01(1.15f - FlatDistance(p, _outsidePoint) / 4.5f), 1f, 700f);
                else Sfx.Play(stepKey, p, 0.5f);
            }
            // A Marshal night's hitmen on the walk up (Brett 0.1.56: "the bad guys seemingly come from no where"): their steps
            // up the path, heard through the front of the house (IntruderHitman.PathStep). Everyone else outside: silent, as before.
            else if (Current == State.Outside && IsHitman && !Riding && Upright(_gait) && !Falling && speed > 0.25f && Time.time >= _nextStep)
            {
                _nextStep = Time.time + Mathf.Clamp(0.55f / Mathf.Max(speed, 0.3f), 0.25f, 0.9f);
                PathStep(p);
            }
        }

        float _armedOnCameraAt = -99f, _nextSighting;

        /// <summary>
        /// Behind the door with a gun in his hand, on his door's camera (and there's a monitor to see it on): the player could
        /// see he was armed (Brett 0.1.29). Checked 5× a second; remembered 3 s (a glance at the screen, then the shot).
        /// </summary>
        void TrackCameraSighting()
        {
            if (Time.time < _nextSighting || Current == State.Dead) return;
            _nextSighting = Time.time + 0.2f;
            if (!BehindDoor || _weapon == null || !_weapon.InHand || _weapon.Kind != WeaponKind.Gun) return;
            var mon = HIS.Security.SecurityMonitor.Instance;
            var cam = HIS.Security.SecurityCamera.For(Door);
            if (mon == null || !mon.Placed || cam == null) return;
            if (cam.Sees(_weapon.transform.position) && cam.Sees(transform.position + Vector3.up * 1.2f)) _armedOnCameraAt = Time.time;
        }

        IntruderFacts BuildFacts()
        {
            var toPlayer = PlayerOnFloor() - transform.position; toPlayer.y = 0f;
            float dist = toPlayer.magnitude;
            var dir = dist > 0.01f ? toPlayer / dist : transform.forward;
            var flatVel = new Vector3(_vel.x, 0f, _vel.z);
            float closing = Vector3.Dot(flatVel, dir);
            var inward = Flat(_insidePoint - _outsidePoint).normalized;
            bool crawling = _gait == Gait.Crawl;
            var held = _weapon != null && _weapon.InHand ? _weapon.Kind : WeaponKind.None;
            bool knife = held == WeaponKind.Knife, gun = held == WeaponKind.Gun;
            // A man on the floor or his knees isn't charging anyone, nor is one whose fall / get-up (root motion) happens to
            // carry him your way. And a "fleeing" man whose way out runs straight at the player with the knife out is judged on
            // what the player sees (a knife coming at him), not on where he means to go.
            bool closingIn = closing > 0.3f && Upright(_gait) && !_floored && !Falling;
            // Legs out but still coming: crawling your way (the crawl is 0.35 m/s) or clawing at you from the floor (in reach:
            // State.Attacking, lunging every ~2 s — FloorLunge), with the knife or, empty-handed, grabbing at your ankles. Down
            // stays true; the judge weighs the knife and the distance (Brett 0.1.10: a knife man shot down, still coming, was
            // called "already down"). An empty-handed man clawing at you is attacking you; one only crawling your way isn't.
            bool crawlingIn = crawling && closing > 0.12f && !Falling;
            bool floorAttack = crawling && !gun && Current == State.Attacking;
            bool fleeing = _fleeingNow && !(knife && closingIn && dist < Judge.StrikeDistance);
            return new IntruderFacts
            {
                IntruderId = Id,
                Time = Time.time,
                WeaponInHand = held,
                Advancing = (closingIn || crawlingIn) && !fleeing,
                Attacking = (MovementLocked && _lock == Lock.Strike) || (knife && closingIn && dist < 3f && !fleeing) || floorAttack,
                Fleeing = fleeing,
                Posture = _posture,
                // Legs out, or knocked off his feet (the drunk's trip): down. Kneeling / face down by choice is a surrender posture.
                Down = (crawling && _posture != Posture.Prone) || _floored,
                IdentityStated = _identityStated || _introduced,
                // Let in through a door the player opened before he forced it: no PC 198.5 presumption (research/25).
                ForcedEntry = _entry == EntryType.ForcedKick && !_letIn,
                Inside = Vector3.Dot(transform.position - _insidePoint, inward) > -0.5f,
                Distance = dist,
                SecondsSinceThreat = Time.time - _lastThreat,
                UnarmedStandingSeconds = _standingSince < 0f ? 0f : Time.time - _standingSince,
                Dead = Current == State.Dead,
                BehindDoor = BehindDoor,
                ForcingDoor = ForcingDoorNow,
                SeenArmedOnCamera = BehindDoor && gun && Time.time - _armedOnCameraAt < 3f,
                PointingAtPlayer = gun && !BehindDoor && GunOnPlayer(),
                RaisingGun = gun && _raisingGun,
                Firing = gun && Time.time - _lastShotAt < 0.5f,
                // (He radioed ahead of his walk-up — ScenarioDef.RadioAhead — or said who he is: known from then on.)
                KnownDeputy = (_introduced || !string.IsNullOrEmpty(_def?.RadioAhead)) && _def != null && _def.Identity == Identity.Officer,
            };
        }

        static ForceKind KindOf(DamageType t) => t switch
        {
            DamageType.Stab => ForceKind.Stab, DamageType.Slash => ForceKind.Slash, DamageType.Blunt => ForceKind.Punch, _ => ForceKind.Shot,
        };

        void EmitForce(bool killed)
        {
            if (!_pending.Any) return;
            // Only the PLAYER's force is reported (and judged). A Marshal night's finder rounds at Ricky carry the hitman's
            // IntruderWeapon as their Source (research/27 §3): his kill is THE TRIAL IS OFF, never the "you shot Ricky" booking.
            // (Today nothing else damages an intruder but the player's weapons, so this changes no other round.)
            if (_pending.Info.Source != null && _pending.Info.Source.GetComponentInParent<IntruderWeapon>() != null) return;
            ForceApplied?.Invoke(this, new ForceEvent
            {
                Time = Time.time, Kind = _forceKind, Before = _forceFacts, Damage = _pending.Total,
                BodyPart = _pending.Part.ToString(), Killed = killed,
            });
        }

        // ───────────────────────────── voice ─────────────────────────────
        int _voice; // his current line: one mouth, so a new line cuts the old one off (quick fade) instead of talking over it
        bool _voiceThroughDoor; // that line is on the door's voice source (muffled), not an Sfx handle

        void StopVoice(float fade)
        {
            Sfx.Stop(_voice, fade);
            _voice = 0;
            if (_voiceThroughDoor && Door != null) Door.StopVoiceThrough();
            _voiceThroughDoor = false;
        }

        void Say(string key)
        {
            if (key == null) return;             // no take in his own voice: he stays quiet rather than borrow someone else's
            StopVoice(0.12f);
            var head = _hasAnim ? _anim.Head : null;
            var door = BehindDoor ? Door : null; // behind the closed door (his): muffled, through the leaf
            // Its own source on the door: the wood cracks of later rounds and his grunts can't cut the line off mid-word.
            if (door != null && !door.IsOpen && door.PlayVoiceThrough(key, transform.position + Vector3.up * 1.6f, 1f, 900f)) _voiceThroughDoor = true;
            else _voice = head != null ? Sfx.PlayFollow(key, head)
                : Sfx.Play(key, _hasAnim && _anim.Hips != null ? _anim.Hips.position + Vector3.up * 0.7f : transform.position + Vector3.up * 1.6f);
            var text = VoiceLines.Text(key);
            if (!string.IsNullOrEmpty(text)) HIS.UI.Subtitles.Show(_speaker, text);
        }

        void _anim_HandsDown() { if (_hasAnim) _anim.SetHandsUp(false); }

        // ───────────────────────────── gestures ─────────────────────────────
        float _actUntil;
        bool Gesturing => Time.time < _actUntil;

        /// <summary>
        /// An in-place gesture over his gait (hiccup, rummaging, a thumb at next door): feet planted for the clip. Never over
        /// a swing, reaction or fall; a hit or a yell cuts it short (<see cref="CancelAct"/>).
        /// </summary>
        bool Gesture(Act kind, float seconds = -1f)
        {
            if (!_hasAnim || !Upright(_gait) || (MovementLocked && _lock != Lock.Gesture)) return false;
            if (MovementLocked) _lockedUntil = Time.time; // a new gesture replaces the old one (no EndAct: the trigger would be lost in its blend-out)
            if (seconds < 0f) seconds = IntruderClips.ActLength[(int)kind] * IntruderClips.OneShotExit;
            StopHere();
            _anim.Act(kind);
            LockMovement(Lock.Gesture, seconds);
            _actUntil = Time.time + seconds;
            return true;
        }

        void CancelAct()
        {
            if (_hasAnim) _anim.EndAct();
            if (MovementLocked && _lock == Lock.Gesture) _lockedUntil = Time.time;
            _actUntil = 0f;
        }

        // ───────────────────────────── entries ─────────────────────────────
        /// <summary>
        /// Unlocked door (the neighbour, family, a sneaking burglar): keys or fumbling, the door swings open normally, he walks
        /// in. Shot through the closed door first (<see cref="ShotThroughDoor"/>), he never comes in: see <see cref="ShotAtTheDoor"/>.
        /// </summary>
        IEnumerator QuietEntry(HIS.Room.VirtualDoor door)
        {
            SetState(State.BreakIn);
            yield return AtTheDoor(0f);          // hit during the wait outside (knockDelay)
            if (IsDrunk && _hasAnim) { yield return DrunkAtTheDoor(door); yield break; }
            if (KeyedVisitor) yield return KnockAndAnnounce(door);   // Ortiz, Sal: a knock and who he is, then the key (IntruderEntries)
            Sfx.Play("door.keys.jingle", _outsidePoint + Vector3.up * 1f, 0.8f);
            yield return AtTheDoor(1.4f);
            if (door != null) { _openedDoorHimself = !door.IsOpen; door.OpenGently(1.6f, 80f); } // (a Marshal visitor pulls it to behind him)
            yield return AtTheDoor(0.5f);        // still hidden (and hittable) as the leaf starts to swing
            SetVisible(true);
            yield return new WaitForSeconds(0.6f);
        }

        /// <summary>
        /// Mike at the wrong door: at the handle in the dark (hidden), he rattles it and mumbles. It's unlocked, so it swings
        /// open under his hand and there he is, still working a handle that's gone — then he staggers in (WalkIn drops the
        /// gesture as his feet start moving).
        /// </summary>
        IEnumerator DrunkAtTheDoor(HIS.Room.VirtualDoor door)
        {
            var inward = Flat(_insidePoint - _outsidePoint).normalized;
            // DrunkTryDoor works a handle ~0.41 m in front of him at 0.97 m: stand half a metre outside the door plane.
            float plane = door != null ? Vector3.Dot(door.transform.position - _outsidePoint, inward) : 0.8f;
            transform.position = _outsidePoint + inward * Mathf.Max(0f, plane - 0.5f);
            _anim.Act(Act.TryDoor);
            var latch = transform.position + inward * 0.45f + Vector3.up * 0.95f;
            yield return AtTheDoor(0.3f);
            for (int i = 0; i < 3; i++)
            {
                Sfx.Play("door.keys.unlock", latch, 0.45f + 0.1f * i);        // the handle clacking
                if (door != null) door.Pound(0.15f + 0.05f * i);              // the leaf rattles in its frame
                if (i == 1) Sfx.Play("drunk.mumble", transform.position + Vector3.up * 1.5f, 0.7f);
                yield return AtTheDoor(0.6f);
            }
            if (door != null) door.OpenGently(1.4f, 80f);
            yield return AtTheDoor(0.15f);
            SetVisible(true);
            yield return new WaitForSeconds(0.9f);                            // still at the handle as the leaf swings away
        }

        /// <summary>
        /// Waits at the door while he's still hidden (closed, or only just swinging open). Shot now, he never comes in:
        /// <see cref="ShotAtTheDoor"/> doesn't return. (A plain wait here would let a hit land on him unseen and unreacted,
        /// then walk him in.)
        /// </summary>
        IEnumerator AtTheDoor(float seconds)
        {
            float until = Time.time + seconds;
            while (true)
            {
                // (In view, the door opened on him — the delivery man: any new hit counts the same.)
                if ((ShotThroughDoor || (!BehindDoor && Health < _doorHealth)) && Current != State.Dead) yield return ShotAtTheDoor();
                if (_doorAnswer == DoorAnswer.Leave) yield return LeaveFromDoor();   // talked off through the door (pick #2)
                if (Time.time >= until) yield break;
                yield return null;
            }
        }

        /// <summary>
        /// Hit through the closed door (Brett 2026-09-23: shooting through doors; 2026-09-24: a real reaction, no "What the
        /// hell?!" lines). The scream is ResolveHit's intruder.pain on the door's voice source (muffled); here his body slams
        /// into the leaf (it judders; a dull, low-passed thud). On his feet: a groan through the door, then he runs off on the
        /// far side, his steps muffled and fading (Update). Legs gone: he slides down the leaf, then silence (he drags himself
        /// off, soundless). Killed: Kill/DieAtTheDoor (thump, the death cry, the floor thud, silence). Never returns, so Run()
        /// never walks him in; he's despawned once Gone, and a hit that kills him stops this with everything else (Kill).
        /// </summary>
        IEnumerator ShotAtTheDoor()
        {
            _givingUp = true;                    // the door opening on him now doesn't restart him (WatchDoor)
            DropCarried();                       // a Marshal night's courier: the pizza box falls where his hands were (IntruderVisitors)
            bool downed = _gait == Gait.Crawl;
            Debug.Log($"[HIS] Intruder #{Id}: shot through the door, {(downed ? "slumps against it" : "runs off")}");
            var door = Door;
            var body = transform.position + Vector3.up * 0.9f;
            // Out on the path (walk-up) he's nowhere near the leaf: no thump on it, just his cry and the run.
            bool atLeaf = door != null && FlatDistance(transform.position, door.Center) < 1.6f;
            // Hit as it swung open (hidden still): he yanks it shut behind him. Not a door the player opened on him.
            if (door != null && door.IsOpen && atLeaf && BehindDoor) door.Close(0.35f);
            else if (door != null && atLeaf && !door.IsOpen)
            {
                door.Thump(downed ? 0.8f : 0.55f, 1100f);
                door.PlayThrough("body.fall", body, downed ? 0.8f : 0.55f, 0.9f, 500f);
            }
            if (downed)
            {
                yield return new WaitForSeconds(0.45f);
                if (door != null && !door.IsOpen && atLeaf) door.PlayThrough("body.fall", transform.position + Vector3.up * 0.3f, 0.45f, 0.75f, 380f); // sliding down the leaf
                yield return new WaitForSeconds(0.25f);
            }
            else
            {
                yield return new WaitForSeconds(0.6f);   // the scream (0.54 s) first, then a second, duller groan
                Cry("intruder.pain", 0.95f, 800f);   // the second, duller groan — louder too (Brett 0.1.17)
                yield return new WaitForSeconds(0.2f);
            }
            yield return FleeAndLeave(run: true);
            while (true) yield return null;
        }

        IEnumerator Brain() => _intent switch
        {
            Intent.Burglar => BurglarBrain(),
            Intent.Surrenderer => SurrenderBrain(),
            Intent.Innocent => InnocentBrain(),
            Intent.Gunman => GunmanBrain(),
            Intent.Police => PoliceBrain(),          // IntruderPolice.cs: an officer answering the call (research/22)
            _ => AttackerBrain(),
        };

        IEnumerator AttackerBrain()
        {
            Say(_weapon != null && _weapon.InHand ? "vo.knife.taunt" : "vo.attacker.taunt");
            yield return MeleeBrain();
        }

        /// <summary>
        /// Fighters rarely listen (research/08: attackers comply ~10–20% when a gun is on them). Returns true when he gave
        /// up and the brain switched to surrendering.
        /// </summary>
        bool TryComply()
        {
            if (!TryHear(out var c)) return false;
            var held = _weapon != null && _weapon.InHand ? _weapon.Kind : WeaponKind.None;
            bool knife = held == WeaponKind.Knife, gun = held == WeaponKind.Gun;
            float p = c switch
            {
                Command.DropWeapon when knife => 0.2f,
                Command.ShowHands or Command.GetDown => knife ? 0.1f : 0.15f,
                Command.GetOut => 0.1f,
                _ => 0f,
            };
            if (AimedAt) p *= 1.5f;
            if (UnityEngine.Random.value >= p) { Debug.Log($"[HIS] Intruder #{Id} ignores '{c}'"); return false; }
            Debug.Log($"[HIS] Intruder #{Id} complies with '{c}'");
            StartCoroutine(c == Command.GetOut ? FleeAndLeave(run: true) : Surrender(c == Command.GetDown));
            return true;
        }

        // ───────────────────────────── burglar ─────────────────────────────
        /// <summary>
        /// Creeps in crouched and works a spot (rummaging, glancing over his shoulder, looking round nervously), then the next.
        /// Challenged, aimed at or a shot: the startle, then he bolts — a jog looking back at you, or a flat-out sprint if he's
        /// holding the knife or someone's shooting. Some give up instead when a gun is on them.
        /// </summary>
        IEnumerator BurglarBrain()
        {
            SetState(State.Idle);
            float enteredAt = Time.time;
            var target = PickLootSpot(out var loot, out bool hasLoot);
            float nextTarget = Time.time + 10f;
            int searched = 0;
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                bool challenged = TryHear(out var c);
                // Yelled at, aimed at or a shot → he runs at once. Walking right up to him only spooks him once he's had a
                // moment inside (the player gets a chance to challenge him first); left alone he gives up after 20 s.
                float inRoom = Time.time - enteredAt;
                bool spooked = challenged || NoticesAim(0.3f) || GunshotRecently(1f)
                               || (inRoom > 3f && FlatDistance(PlayerOnFloor(), transform.position) < 1.6f) || inRoom > 20f;
                if (spooked)
                {
                    // Pick #3 (varied rounds): he freezes and chooses instead of bolting at once (IntruderBurglarChoice).
                    // Left alone 20 s he just goes, as before.
                    if (_styled && (challenged || inRoom <= 20f)) { yield return FreezeAndChoose(challenged, c); yield break; }
                    // (No CancelAct: the startle replaces whatever he was doing on the spot.)
                    // Some give up when a gun is on them and they're told to; most run.
                    if (challenged && c is Command.ShowHands or Command.GetDown or Command.DropWeapon && AimedAt && UnityEngine.Random.value < 0.35f)
                    { yield return Caught(); yield return Surrender(c == Command.GetDown); yield break; }
                    Say("vo.burglar.flee");
                    yield return FleeAndLeave(run: true, caught: true);
                    yield break;
                }
                if (!_agent.enabled || !_agent.isOnNavMesh || MovementLocked) { yield return null; continue; }
                if (FlatDistance(transform.position, target) < 0.6f)
                {
                    // At the spot: face what he's going through, then rummage / glance back / rummage / look round, and move on.
                    StopHere();
                    var toLoot = loot - transform.position; toLoot.y = 0f;
                    if (toLoot.sqrMagnitude > 0.04f && Vector3.Angle(transform.forward, toLoot) > 25f) { FaceTowards(loot, 6f); yield return null; continue; }
                    var kind = searched % 2 == 0 ? (hasLoot ? Act.Rummage : Act.LookAround) : searched % 4 == 1 ? Act.LookOverShoulder : Act.LookAround;
                    Gesture(kind);
                    if (++searched >= 4) { target = PickLootSpot(out loot, out hasLoot); searched = 0; nextTarget = Time.time + 10f; }
                }
                else
                {
                    if (Time.time > nextTarget) { target = PickLootSpot(out loot, out hasLoot); nextTarget = Time.time + 10f; }
                    _agent.speed = GaitSpeed;
                    _agent.SetDestination(target);
                }
                yield return null;
            }
        }

        /// <summary>Caught in the act: the startle (CaughtReact, cut to 1.1 s), turning to whoever caught him.</summary>
        IEnumerator Caught()
        {
            if (!Gesture(Act.CaughtReact)) yield break;
            float until = Time.time + IntruderClips.ActLength[(int)Act.CaughtReact] * 0.8f;
            while (Current != State.Dead && Time.time < until)
            {
                if (Time.deltaTime > 0f) FaceTowards(PlayerOnFloor(), 8f);
                yield return null;
            }
        }

        // ───────────────────────────── surrender ─────────────────────────────
        /// <summary>Came in, but gives up as soon as he's challenged: hands up, drops anything, gets down when told.</summary>
        IEnumerator SurrenderBrain()
        {
            SetState(State.Idle);
            var spot = Vector3.Lerp(_insidePoint, PlayerOnFloor(), 0.35f);
            float since = Time.time;
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                if (TryHear(out var c) || NoticesAim(0.5f) || GunshotRecently(1f) || Time.time - since > 15f)
                {
                    yield return Surrender(c == Command.GetDown);
                    yield break;
                }
                if (_agent.enabled && _agent.isOnNavMesh) { _agent.speed = GaitSpeed * 0.6f; _agent.SetDestination(spot); }
                FaceTowards(PlayerOnFloor(), 3f);
                yield return null;
            }
        }

        /// <summary>
        /// Hands up (drops a weapon). "Get down!" → onto his knees, hands on his head; told again (or, some, on their own after a
        /// few seconds) → face down. "Get out!" → he gets up and walks out. The police take him after a while down.
        /// </summary>
        IEnumerator Surrender(bool straightDown, bool quiet = false)
        {
            _reactions++;                        // a swing still winding up never lands once he's given up
            CancelAct();
            StopHere();
            SetState(State.Complying);
            if (_weapon != null && _weapon.InHand) _weapon.Drop(transform.forward * 0.5f + Vector3.up * 0.3f);
            RaiseHands();
            if (!quiet) Say(VoiceLines.Surrender(_intent, PlayerHolds)); // his own voice; "Don't shoot!" only at a gun
            float handsUpAt = Time.time;
            bool decided = false;
            // "Get on the ground!": he gets his "okay, okay" out with his hands up, then goes down.
            if (straightDown) { yield return new WaitForSeconds(Mathf.Clamp(Sfx.SecondsLeft(_voice), 0.8f, 2.5f)); yield return GetDown(); }
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                if (TryHear(out var c))
                {
                    if (c == Command.GetDown) yield return GetDown();
                    else if (c == Command.GetOut) { yield return FleeAndLeave(run: false); yield break; }
                }
                // A few seconds on his knees: some lie down flat without being told twice.
                if (_gait == Gait.Kneel && !decided && Time.time - _downAt > 3f)
                {
                    decided = true;
                    if (UnityEngine.Random.value < 0.4f) yield return GoProne();
                }
                if (!LyingDown) FaceTowards(PlayerOnFloor(), 2f);
                // Police arrive: 6 s face down, 8 s on his knees, or 12 s with his hands up and nobody telling him to get down.
                if ((_posture == Posture.Prone && Time.time - _proneAt > 6f) || (_gait == Gait.Kneel && Time.time - _downAt > 8f)
                    || (_posture == Posture.HandsUp && Upright(_gait) && Time.time - handsUpAt > 12f))
                {
                    InCustody = true;
                    Debug.Log($"[HIS] Intruder #{Id}: in custody");
                    yield break;
                }
                yield return null;
            }
        }

        float _proneAt, _downAt;

        /// <summary>"Get on the ground!": down onto his knees (FallToKnees), hands on his head. Already kneeling: face down.</summary>
        IEnumerator GetDown()
        {
            if (_gait == Gait.Kneel) { yield return GoProne(); yield break; }
            if (!Upright(_gait)) yield break;    // already on the floor (face down, crawling, sitting)
            CancelAct();
            Say(VoiceLines.SurrenderDown(_intent));
            _anim_HandsDown();
            StopHere();
            _posture = Posture.HandsUp; _downAt = Time.time; // hands on his head: shown and empty
            SetGait(Gait.Kneel);
            if (_hasAnim) _anim.SetHandsOnHead(true);        // fades in once he's settled on his knees
            LockMovement(Lock.Fall, IntruderClips.KneelDownTime);
            yield return UntilPose(Gait.Kneel, IntruderClips.KneelDownTime + 1f);
        }

        /// <summary>From his knees (or his feet: the Animator goes via the knees) down onto his front, and he stays there.</summary>
        IEnumerator GoProne()
        {
            if (_posture == Posture.Prone || !(Upright(_gait) || _gait == Gait.Kneel)) yield break;
            bool fromFeet = _gait != Gait.Kneel;
            CancelAct();
            if (fromFeet) { Say(VoiceLines.SurrenderDown(_intent)); _downAt = Time.time; }
            _anim_HandsDown();
            if (_hasAnim) _anim.SetHandsOnHead(true);   // hands to the floor on the way down, behind his head once he's flat
            StopHere();
            _posture = Posture.Prone; _proneAt = Time.time;
            SetGait(Gait.Prone);
            float t = (fromFeet ? IntruderClips.KneelDownTime : 0f) + IntruderClips.ProneDownTime;
            LockMovement(Lock.Fall, t);
            yield return UntilPose(Gait.Prone, t + 1f);
        }

        /// <summary>
        /// Back on his feet from his knees, his front or his backside (the Animator plays KneelToStand / GetUpFromStomach /
        /// GetUpFromBack, travel through the NavMesh), feet held until he's up. Legs shot out: he can't (he crawls).
        /// </summary>
        IEnumerator StandUp()
        {
            _anim_HandsDown();
            if (_hasAnim) _anim.SetHandsOnHead(false);
            var from = _gait;
            if (from is not (Gait.Kneel or Gait.Prone or Gait.Sit)) yield break;
            if (_legDamage >= downAtLegDamage) { SetGait(Gait.Crawl); yield break; } // legs shot while down: he crawls out
            var to = _legDamage >= limpAtLegDamage ? Gait.Limp : _standGait;
            float t = from == Gait.Kneel ? IntruderClips.KneelUpTime : from == Gait.Prone ? IntruderClips.ProneUpTime : IntruderClips.SitUpTime;
            StopHere();
            SetGait(to);
            LockMovement(Lock.Fall, t);
            yield return UntilPose(to, t + 1.5f);
            _floored = false;
        }

        /// <summary>Until the Animator has settled in this gait's state (the way down/up is over), or the timeout.</summary>
        IEnumerator UntilPose(Gait gait, float timeout)
        {
            float giveUp = Time.time + timeout;
            while (Current != State.Dead && Time.time < giveUp && !(_hasAnim && _anim.IsIn(gait))) yield return null;
        }

        // ───────────────────────────── the drunk neighbour ─────────────────────────────
        /// <summary>
        /// The wrong-house neighbour: drunk, harmless, never attacks. He sways, wanders, hiccups and stumbles on the spot (and,
        /// rarely, trips flat on his face). "Who's there?" → "It's me, Mike!" with a thumb at next door or a shrug; a gun on him
        /// → hands up and maybe a wobbly step back; a shot right beside him may put him on his backside. "Get out" → he
        /// apologises and jogs off home. Left alone long enough, he realises it's not his place and wanders out.
        /// </summary>
        IEnumerator InnocentBrain()
        {
            // A Marshal night's visit (ScenarioDef.StaysFor: Ortiz checking in, Sal): a short beat, not Mike's wander (IntruderVisitors).
            if (Visiting) { yield return VisitBrain(); yield break; }
            SetState(State.Idle);
            // Challenged at the door or aimed at as he comes in: straight to who he is (no cheery hello over it). A Marshal
            // night's visitor says his own (VisitorVoice; Mike's key otherwise), and it names him (_introduced).
            if (_heard.Count == 0 && AimedFor <= 0.3f && !GunshotRecently(1f))
            {
                Say(VoiceLines.Visitor(VisitorVoice, "hello"));
                GreetedAt = Time.time;
                if (KeyedVisitor) _introduced = true;
            }
            float since = Time.time, identifiedAt = -1f, nextTripCheck = 0f;
            float nextFidget = Time.time + UnityEngine.Random.Range(3f, 6f), nextWander = Time.time + UnityEngine.Random.Range(6f, 9f);
            bool steppedBack = false;
            var spot = Vector3.Lerp(_insidePoint, PlayerOnFloor(), 0.4f);
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                // A Marshal night's sober visitor hurt by you (the relief): his hurt line, never the calm leaving one. (Mike: as before.)
                if (HurtVisitor) { yield return HurtAndGone(); yield break; }
                bool heard = TryHear(out var c);
                bool aimed = AimedFor > 0.3f, shot = GunshotRecently(1f);

                // A shot right beside him: he reels back onto his backside (once; only with the floor behind him clear). Mike
                // only: a sober visitor never staggers (research/27 §6 step 8).
                if (shot && IsDrunk && !_sitDone && Upright(_gait) && !Falling && FlatDistance(_gunshotFrom, transform.position) < 3f)
                {
                    _sitDone = true;
                    if ((ForceStumbleBack || UnityEngine.Random.value < 0.5f) && CanStumbleBack())
                    {
                        if (!_identityStated) identifiedAt = Time.time;
                        yield return StumbleBack();
                        continue;
                    }
                }
                if (heard && c == Command.GetOut && _identityStated)
                {
                    Say(VoiceLines.Visitor(VisitorVoice, "leave"));
                    yield return FleeAndLeave(run: IsDrunk);              // Mike jogs off home; a sober visitor walks out
                    yield break;
                }
                if (heard && c == Command.GetDown && _identityStated) { yield return GetDown(); continue; }
                if (heard && c == Command.ShowHands && _identityStated) RaiseHands();
                if (!_identityStated && (heard || aimed || shot))
                {
                    _identityStated = true; identifiedAt = Time.time;
                    yield return Identify(gunOnHim: aimed || shot);
                    continue;
                }
                // He said who he is, and now there's a gun on him: hands up (and maybe a step back from it).
                if (_identityStated && _posture == Posture.Standing && Upright(_gait) && !_floored && (aimed || shot))
                {
                    RaiseHands();
                    if (!steppedBack && UnityEngine.Random.value < 0.5f) { steppedBack = true; yield return StepBack(); }
                    steppedBack = true;
                }
                // Police sort out the misunderstanding once he's been down a while. A Marshal night's visitor (nobody calls the
                // police to a safe house) gets up after the same wait and goes, with his leaving line.
                if ((_posture == Posture.Prone && Time.time - _proneAt > 8f) || (_gait == Gait.Kneel && Time.time - _downAt > 10f))
                {
                    if (!IsDrunk && VisitorVoice != null)
                    {
                        Say(VoiceLines.Visitor(VisitorVoice, "leave"));
                        yield return FleeAndLeave(run: false);
                        yield break;
                    }
                    InCustody = true;
                    Debug.Log($"[HIS] Intruder #{Id}: neighbour held for police");
                    yield break;
                }
                // Left alone long enough, he works out it's not his place and goes home.
                if (_posture != Posture.Prone && _gait != Gait.Kneel && ((identifiedAt > 0f && Time.time - identifiedAt > 12f) || Time.time - since > 25f))
                {
                    Say(VoiceLines.Visitor(VisitorVoice, "leave"));
                    yield return FleeAndLeave(run: false);
                    yield break;
                }
                // Nobody's challenged him yet: he wanders, sways, and now and then stumbles on the spot or hiccups.
                if (!_identityStated && !MovementLocked && _agent.enabled && _agent.isOnNavMesh)
                {
                    bool walking = _agent.velocity.sqrMagnitude > 0.3f * 0.3f;
                    // The rare trip: only mid-stride, with ~3 m of clear floor ahead and the player out of the way.
                    if (_tripPlanned && walking && Time.time >= nextTripCheck)
                    {
                        nextTripCheck = Time.time + 0.25f;
                        if (CanTrip()) { _tripPlanned = false; yield return Trip(); continue; }
                    }
                    bool there = FlatDistance(transform.position, spot) < 0.8f;
                    if (there && !walking && Time.time >= nextFidget && !Sfx.IsPlaying(_voice))
                    {
                        // Mike stumbles on the spot or hiccups; a sober visitor only looks round the place.
                        var kind = !IsDrunk ? Act.LookAround : UnityEngine.Random.value < 0.55f ? Act.Stumble : Act.Hiccup;
                        Gesture(kind);
                        nextFidget = Time.time + IntruderClips.ActLength[(int)kind] + UnityEngine.Random.Range(5f, 10f);
                    }
                    else
                    {
                        if (there && Time.time >= nextWander) { spot = WanderSpot(spot); nextWander = Time.time + UnityEngine.Random.Range(7f, 11f); }
                        _agent.speed = GaitSpeed;
                        _agent.SetDestination(spot);
                    }
                }
                // Facing you once he's talking to you (or just standing about); not while he walks somewhere else.
                if (!LyingDown && (_identityStated || _agent.velocity.sqrMagnitude < 0.04f)) FaceTowards(PlayerOnFloor(), 2f);
                yield return null;
            }
        }

        /// <summary>"It's me, Mike, from next door!" A gun on him → hands up (maybe a step back); just asked → a thumb at next door, or a shrug.</summary>
        IEnumerator Identify(bool gunOnHim)
        {
            CancelAct();
            StopHere();
            SetState(State.Complying);
            Say(VoiceLines.ItsMe(VisitorVoice, gunOnHim ? PlayerWeapon.Gun : PlayerHolds));   // Mike's, or the visitor's own (research/27 §5)
            if (GreetedAt < 0f) GreetedAt = Time.time;
            if (KeyedVisitor) _introduced = true;
            if (!Upright(_gait) || _floored) { if (_gait == Gait.Sit) RaiseHands(); yield break; } // answering from the floor
            if (gunOnHim)
            {
                RaiseHands();
                if (UnityEngine.Random.value < 0.5f) yield return StepBack();
                yield break;
            }
            // The thumb lands on "from next door", about a second into the line.
            float until = Time.time + 0.7f;
            while (Current != State.Dead && Time.time < until && AimedFor <= 0.3f) yield return null;
            if (AimedFor > 0.3f) { RaiseHands(); yield break; }
            if (!IsDrunk && VisitorVoice != null) yield break;   // (the thumb points at next door: Mike's; a visitor just faces you)
            Gesture(UnityEngine.Random.value < 0.6f ? Act.PointThumb : Act.Shrug);
        }

        /// <summary>A wobbly step back from the gun: DrunkWalkBack with his feet matched (1.08 m/s back for 0.9 s), floor permitting.</summary>
        IEnumerator StepBack()
        {
            const float seconds = 0.9f;
            float dist = IntruderClips.DrunkBackNatural * seconds;
            if (!_hasAnim || _gait != Gait.Drunk || !_agent.enabled || !_agent.isOnNavMesh || MovementLocked) yield break;
            var back = -transform.forward;
            if (FreeDistance(back, 0.35f, dist + 0.4f) < dist + 0.4f) yield break;
            if (NavMesh.Raycast(transform.position, transform.position + back * dist, out _, NavMesh.AllAreas)) yield break;
            if (!Gesture(Act.WalkBack, seconds)) yield break;
            float t = 0f;
            while (t < seconds && Current != State.Dead && MovementLocked && _lock == Lock.Gesture)
            {
                t += Time.deltaTime;
                if (_agent.enabled && _agent.isOnNavMesh) _agent.Move(back * (IntruderClips.DrunkBackNatural * Time.deltaTime));
                yield return null;
            }
            CancelAct();
        }

        /// <summary>Room for TripFall: its travel (2.5 m, veering right) plus the body beyond it, on the NavMesh, not through the player.</summary>
        bool CanTrip()
        {
            var travel = transform.TransformDirection(new Vector3(IntruderClips.TripTravel.x, 0f, IntruderClips.TripTravel.y));
            var dir = travel.normalized;
            if (FreeDistance(dir, 0.48f, IntruderClips.TripClear) < IntruderClips.TripClear) return false;
            if (NavMesh.Raycast(transform.position, transform.position + travel, out _, NavMesh.AllAreas)) return false;
            var toPlayer = PlayerOnFloor() - transform.position; toPlayer.y = 0f;
            float ahead = Vector3.Dot(toPlayer, dir);
            return !(ahead > -0.3f && ahead < IntruderClips.TripClear + 0.3f && (toPlayer - dir * ahead).magnitude < 0.8f);
        }

        bool CanStumbleBack()
        {
            var back = -transform.forward;
            return FreeDistance(back, 0.51f, IntruderClips.StumbleBackClear) >= IntruderClips.StumbleBackClear
                   && !NavMesh.Raycast(transform.position, transform.position + back * 1.9f, out _, NavMesh.AllAreas);
        }

        /// <summary>
        /// The rare drunk trip: a stagger and a face-plant (root motion through the NavMesh), a groan on the floor, then he
        /// pushes himself back up. He's Down the whole time he's on the floor. Challenged down there, he answers from the
        /// floor and stays put until he's told to go (or gives up and goes).
        /// </summary>
        IEnumerator Trip()
        {
            Debug.Log($"[HIS] Intruder #{Id}: trips");
            yield return FallOver(IntruderAnimator.FallKind.Trip, Gait.Prone, IntruderClips.TripLength);
            if (Current == State.Dead) yield break;
            Sfx.Play("drunk.mumble", Chest, 0.8f);
            float lie = Time.time + UnityEngine.Random.Range(1.5f, 2.5f);
            while (Current != State.Dead && Time.time < lie && _heard.Count == 0 && AimedFor <= 0.3f && !GunshotRecently(1f)) yield return null;
            if (Current == State.Dead || _heard.Count > 0 || AimedFor > 0.3f || GunshotRecently(1f)) yield break; // the brain answers
            yield return StandUp();
        }

        /// <summary>A shot right beside him: he reels back onto his backside and sits there, hands up, until he's told to go.</summary>
        IEnumerator StumbleBack()
        {
            Debug.Log($"[HIS] Intruder #{Id}: knocked onto his backside by the shot");
            if (!_identityStated) { _identityStated = true; SetState(State.Complying); Say(VoiceLines.NeighborItsMe(PlayerWeapon.Gun)); } // a shot: a gun
            yield return FallOver(IntruderAnimator.FallKind.StumbleBack, Gait.Sit, IntruderClips.StumbleBackLength);
            if (Current != State.Dead) RaiseHands();
        }

        Gait _fallingTo;

        /// <summary>Off his feet by accident (Down from the first frame); the fall's travel goes through the NavMesh.</summary>
        IEnumerator FallOver(IntruderAnimator.FallKind kind, Gait landsIn, float length)
        {
            CancelAct();
            _anim_HandsDown();
            StopHere();
            _floored = true; _tripping = true; _fallingTo = landsIn;
            _anim.Fall(kind);
            StartCoroutine(FloorThud(0.9f));
            LockMovement(Lock.Fall, length);
            yield return null;                   // the Animator has taken the Fall trigger: now the gait (no race with the way down)
            SetGait(landsIn);
            yield return UntilPose(landsIn, length + 0.6f);
            _tripping = false;
        }

        /// <summary>On a doorway apron (any door's NavMesh behind it): never somewhere to wander to or search.</summary>
        static bool OutTheDoor(Vector3 p) => DoorwayNav.AnyBeyond(p);

        /// <summary>Somewhere else to stand about: a NavMesh point a couple of metres off, not on top of the player.</summary>
        Vector3 WanderSpot(Vector3 from)
        {
            for (int i = 0; i < 6; i++)
            {
                var r = UnityEngine.Random.insideUnitCircle * 2.2f;
                if (NavMesh.SamplePosition(from + new Vector3(r.x, 0f, r.y), out var hit, 0.8f, NavMesh.AllAreas) && !OutTheDoor(hit.position)
                    && FlatDistance(hit.position, PlayerOnFloor()) > 1.6f && FlatDistance(hit.position, from) > 0.8f)
                    return hit.position;
            }
            return from;
        }

        // ───────────────────────────── shared ─────────────────────────────
        /// <summary>Hands up, unless he's on the floor: face down (legs gone) is his surrender; kneeling, his hands are on his head.</summary>
        void RaiseHands()
        {
            if (_gait == Gait.Crawl) { if (_posture != Posture.Prone) { _posture = Posture.Prone; _proneAt = Time.time; } return; }
            if (_gait is Gait.Kneel or Gait.Prone) return;
            CancelAct();
            _posture = Posture.HandsUp;
            if (_hasAnim) _anim.SetHandsUp(true);
        }

        /// <summary>Off his feet (face down, crawling, kneeling, sitting): he doesn't spin on the floor to keep facing the player.</summary>
        bool LyingDown => _posture == Posture.Prone || !Upright(_gait) || _floored;

        /// <summary>
        /// Run only on working legs; a leg hit on the way out slows him right there. The burglar jogs off looking back at you
        /// (RunLookBack), or sprints if he's holding the knife, hurt, or someone's shooting; the neighbour does a drunk jog.
        /// </summary>
        float FleeSpeed(bool run)
        {
            if (!run) return GaitSpeed;
            return _gait switch
            {
                Gait.Normal or Gait.Calm => IntruderClips.RunSpeed,
                Gait.Drunk => IntruderClips.DrunkRunSpeed,
                Gait.Gun => _legDamage >= limpAtLegDamage ? IntruderClips.LimpSpeed : IntruderClips.RunSpeed,
                Gait.Sneak => (_weapon != null && _weapon.InHand) || GunshotRecently(4f) || Health < 100f ? IntruderClips.RunSpeed : IntruderClips.LookBackRunSpeed,
                _ => GaitSpeed,
            };
        }

        /// <summary>
        /// Out through the doorway the way he came in (an agent path over the DoorwayNav link to where he waited in the dark
        /// box); hidden and gone. Off his feet, he gets up first; caught in the act, the startle comes first. No way out: he
        /// stops and gives himself up.
        /// </summary>
        IEnumerator FleeAndLeave(bool run, bool caught = false)
        {
            bool outside = Current is State.Outside or State.BreakIn; // never came in (shot through the closed door)
            _reactions++;                        // a swing still winding up never lands once he's leaving
            _fleeingNow = true;
            SetState(State.Fleeing);
            if (caught) yield return Caught(); // this frame: over the rummaging, before anything else
            else CancelAct();
            if (outside) { yield return RunOffOutside(run); yield break; }
            yield return StandUp();
            if (Current == State.Dead) yield break;
            _posture = Posture.Standing;
            _anim_HandsDown();
            if (_gait == Gait.Normal) SetGait(Gait.Calm);

            // Out the way he came: an agent path over the doorway link (DoorwayNav) to where he waited behind the door. The
            // gunman who runs may, once, turn and point it on the way (IntruderGunman). No way out (the path is blocked by
            // real furniture, not just a wait on a reaction): he stops, and gives himself up (Surrender: custody).
            var nav = Nav;
            bool way = _agent.enabled && _agent.isOnNavMesh && nav != null && nav.Ready && nav.OnApron(_outsidePoint, out _);
            if (way)
            {
                nav.OnApron(_outsidePoint, out var exit);
                float fleeStart = Time.time;
                if (WalksOffPorch) StartCoroutine(HideOncePastTheDoor());   // a Marshal visitor: one switch to the outside's layer, on the step
                yield return PathTo(exit, () => FleeSpeed(run),
                    () => WantsTurnAndPoint(Time.time - fleeStart) ? TurnAndPoint() : null);
            }
            if (Current == State.Dead) yield break;
            if (!way || _walkBlocked || !nav.OnApron(transform.position, out _))
            {
                _fleeingNow = false;
                if (VisitorVoice != null)
                {
                    // A Marshal night's visitor (Ortiz, Sal, the courier, the relief) never gives himself up to the police: nobody
                    // calls them to a safe house (Brett 2026-10-04 (a)). Blocked in, he's gone where he stands, hidden for good.
                    Debug.LogWarning($"[HIS] Intruder #{Id}: no way out ({(way ? "path blocked" : "no doorway link")}); a visitor: gone where he stands");
                    if (_hasAnim) _anim.SetSpeedOverride(0f);
                    if (_agent.enabled && _agent.isOnNavMesh) { _agent.ResetPath(); _agent.velocity = Vector3.zero; }
                    SetState(State.Gone);
                    Vanish();
                    yield break;
                }
                Debug.LogWarning($"[HIS] Intruder #{Id}: no way out ({(way ? "path blocked" : "no doorway link")}); gives up where he stands");
                if (_hasAnim) _anim.SetSpeedOverride(null);
                yield return Surrender(straightDown: false);
                yield break;
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            if (_agent.enabled) _agent.enabled = false;
            // A Marshal night's visitor walks on, off the porch past the door camera's edge, before he's gone (IntruderVisitors).
            if (WalksOffPorch) { yield return WalkOffPorch(run); if (Current == State.Dead) yield break; }
            _fleeingNow = false;
            SetState(State.Gone);
            Vanish();                            // gone is gone: no statue on the step, in the doorway or on the camera (Brett 0.1.56)
        }

        /// <summary>
        /// Away from the door on the far side, 4 m past where he stood (hidden, no NavMesh): his steps and voice fade off.
        /// Legs shot out through the door: he drags himself 1.5 m (crawl speed) instead of 15 s of it.
        /// </summary>
        IEnumerator RunOffOutside(bool run)
        {
            if (_agent.enabled) _agent.enabled = false;
            // Walking away out of sight: rounds through the door no longer find him (the doorway window is over). In view (the
            // player opened the door on him) he stays hittable: a shot in the back is judged "running away", not blind.
            if (BehindDoor) SetHittable(false);
            // A Marshal night's visitor on his feet (the courier, a visitor sent off from the door): off along the front of the
            // house past the camera's edge, never back to the street in its view (IntruderVisitors).
            if (WalksOffPorch && _gait != Gait.Crawl)
            {
                yield return WalkOffPorch(run);
                if (Current == State.Dead) yield break;
                _fleeingNow = false;
                SetState(State.Gone);
                Vanish();
                yield break;
            }
            // Off the side of the porch past its camera's edge, or back down the path to the street if he was hit out there
            // (you watch him go on the monitor), or 4 m straight back. Legs out: 1.5 m, dragging himself away from the house.
            var door = Door;
            var from = door != null ? door.Center : _insidePoint;
            var to = _gait == Gait.Crawl ? transform.position + Flat(transform.position - from).normalized * 1.5f
                   : PorchExit() is Vector3 off ? off
                   : _outsidePoint + Flat(_outsidePoint - _insidePoint).normalized * 4f;
            to.y = transform.position.y;
            while (Current != State.Dead && FlatDistance(transform.position, to) > 0.05f)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                float s = MovementLocked ? 0f : FleeSpeed(run);
                if (_hasAnim) _anim.SetSpeedOverride(s);
                FaceTowards(to, 10f);
                transform.position = Vector3.MoveTowards(transform.position, to, s * Time.deltaTime);
                yield return null;
            }
            if (Current == State.Dead) yield break;
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            _fleeingNow = false;
            // Legs shot out, he lies where he dragged himself (as before 0.1.56): drawn for the door camera on the outside's layer,
            // never in the room — a wounded man on the step doesn't blink out on the monitor. Hidden before Gone (SetVisible on a
            // Gone man is Vanish). On his feet: gone is gone, never left standing at the curb or the step (Brett 0.1.56).
            bool lying = _gait == Gait.Crawl;
            if (lying) SetVisible(false);
            SetState(State.Gone);
            if (!lying) Vanish();
        }

        // ───────────────────────────── helpers ─────────────────────────────
        void StopHere()
        {
            if (_agent.enabled && _agent.isOnNavMesh) { _agent.ResetPath(); _agent.velocity = Vector3.zero; }
        }

        void FaceTowards(Vector3 point, float rate)
        {
            if (MovementLocked && _lock >= Lock.React) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(Flat(point - transform.position), Vector3.up), rate * Time.deltaTime);
        }

        /// <summary>
        /// Somewhere a burglar would go: a scanned storage unit/table/screen (<paramref name="loot"/> = the thing itself, to face
        /// while he goes through it), else the middle of the floor (nothing to rummage: he just looks round).
        /// </summary>
        Vector3 PickLootSpot(out Vector3 loot, out bool hasLoot)
        {
            var scanner = HIS.Room.RoomScanner.Instance;
            var room = scanner != null ? scanner.Room : null;
            if (room != null)
            {
                var candidates = new List<(Vector3 spot, Vector3 thing)>();
                foreach (var a in room.Anchors)
                {
                    if (a == null) continue;
                    var l = a.Label;
                    if ((l & (Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.STORAGE | Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.TABLE |
                              Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.SCREEN)) == 0) continue;
                    var p = a.transform.position; p.y = _floorY;
                    if (FlatDistance(p, PlayerOnFloor()) < 2.5f) continue; // not right next to the player
                    if (NavMesh.SamplePosition(p, out var hit, 1.2f, NavMesh.AllAreas) && !OutTheDoor(hit.position)) candidates.Add((hit.position, p));
                }
                if (candidates.Count > 0)
                {
                    var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                    loot = pick.thing; hasLoot = true;
                    return pick.spot;
                }
            }
            var mid = Vector3.Lerp(_insidePoint, PlayerOnFloor(), 0.5f);
            loot = mid + Flat(mid - _insidePoint).normalized; hasLoot = false;
            return mid;
        }
    }
}
