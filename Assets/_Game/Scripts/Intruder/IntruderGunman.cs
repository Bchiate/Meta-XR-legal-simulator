using System.Collections;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using Act = HIS.Intruder.IntruderAnimator.ActKind;
using Gait = HIS.Intruder.IntruderAnimator.Gait;
using GunPose = HIS.Intruder.IntruderAnimator.GunPose;

namespace HIS.Intruder
{
    /// <summary>
    /// The gunman (M3a; research/08 "armed attacker / gun-in-hand burglar", research/13). Hidden intent = an armed robber; the
    /// scenario's starting morale m0 and <see cref="ScenarioDef.RunsWhenChallenged"/> make him one of three men:
    /// - aggressive (m0 ≥ 1): once he's seen you (1 s perception gate) he raises the gun on you — the TELL: 0.7 s of raise
    ///   plus a shout ("Drop it!" / "Don't move!") — and holds you at gunpoint, walking in to ~3 m; he fires when you aim at
    ///   him, shoot at him, close in, or his patience runs out;
    /// - hesitant (m0 low): gun held low, he looks for loot; challenged, the SWAT 4 morale roll decides — comply (toss the
    ///   gun away, then the surrender ladder) or raise it and stand you off (fires if shot at, or after 3–5 s);
    /// - runner: challenged, he bolts for the door with the gun (15%: turns once to point it, 0.8 s tell, one hurried shot).
    /// No round ever leaves before the tell has finished. Pointing is the prop's Muzzle ray (IntruderAnimator aims it), so the
    /// judge sees exactly what the player sees.
    /// </summary>
    public partial class Intruder
    {
        // ---- Tunables (research/08 §2c–3; PLAN.md M3a) ----
        const float PerceptionGate = 1f;       // s after the door opens on him before he can start raising
        const float TellSeconds = 0.7f;        // the raise + shout (0.6–0.8 s): no round before it's done
        const float TurnTellSeconds = 0.8f;    // the runner turning to point it
        const float TurnAndPointChance = 0.15f;
        const float PointConeDegrees = 10f;    // the judge's "pointing at you": muzzle ray within this of your head or chest
        // P(hit) per round: 0.6 at ≤2 m falling to 0.3 at 8 m, +0.15 once he's been steady on you 1.5 s (first run: 0.9 at 2 m
        // put three rounds in a standing player in ~2 s — no time to react after the tell). Hurried shots (floor ×0.6, the runner's
        // turn ×0.5) scale it down. It sizes the spread round his barrel (PlayerShape.SpreadSigma); where a round lands on you —
        // head, torso, arm, leg or past you — is the ray through the player's body shapes (Brett 0.1.17: PlayerState's wound rule).
        const float HitNear = 0.6f, HitFar = 0.3f, HitNearDist = 2f, HitFarDist = 8f, SteadyAimBonus = 0.15f, SteadyAimSeconds = 1.5f;
        const float StandoffDistance = 3f;     // the aggressive man walks in to here, gun on you
        const float LootGiveUp = 20f;          // left alone this long, the hesitant man turns on you / the runner leaves

        /// <summary>Tests: the compliance roll's random draw (0 = he gives in whenever P &gt; 0). Null = random.</summary>
        public static float? TestRoll;
        /// <summary>Tests: true = the runner always turns and points, false = never; null = the 15% roll.</summary>
        public static bool? TestTurnAndPoint;
        /// <summary>Tests: scales his spread (0 = every round flies straight down the barrel). Null = 1.</summary>
        public static float? TestSpread;

        float _morale = 0.5f;                  // m0
        bool _raisingGun, _turnPlanned;
        float _tellDoneAt = -1f, _lastShotAt = -100f;
        int _woundCount, _woundsAtTell, _vestStops;
        // Pressure at the last compliance roll (no re-roll without new pressure: research/08, SWAT 4's bListeningForCompliance).
        float _rollAt = -100f; bool _rollAimed; int _rollWounds, _rollBand, _repeatYells;

        bool HasGun => _weapon != null && _weapon.InHand && _weapon.Kind == WeaponKind.Gun;
        bool Aggressive => _morale >= 1f;

        /// <summary>
        /// No tell and no round while the player can't answer it: choosing the door (InputGate UI), paused (time is stopped
        /// anyway) or already dead (Sandbox keeps him in the room for the 4 s restart: no bursts into a corpse).
        /// </summary>
        static bool HoldFire => !InputGate.GameplayActive || (PlayerState.Instance != null && PlayerState.Instance.IsDead);
        static int _roomOnlyMask = -1;   // real walls and furniture (not the door leaf: rounds go through it)

        /// <summary>The player's head: the camera. Untracked (tests, headset off) the camera sits at the floor → assume 1.6 m.</summary>
        Vector3 PlayerHead
        {
            get
            {
                var p = _player != null ? _player.position : transform.position + transform.forward * 3f;
                if (p.y - _floorY < 0.2f) p.y = _floorY + 1.6f; // (a player sitting or lying on the floor keeps his real head)
                return p;
            }
        }
        Vector3 PlayerChest => PlayerHead + Vector3.down * 0.35f;

        /// <summary>His muzzle ray is within <see cref="PointConeDegrees"/> of the player's head or chest (IntruderFacts.PointingAtPlayer).</summary>
        bool GunOnPlayer()
        {
            var m = HasGun && _hasAnim ? _anim.Muzzle : null;
            if (m == null) return false;
            return Within(m, PlayerHead) || Within(m, PlayerChest);
            static bool Within(Transform m, Vector3 p)
            {
                var to = p - m.position;
                return to.sqrMagnitude < 30f * 30f && Vector3.Angle(m.forward, to) < PointConeDegrees;
            }
        }

        // ───────────────────────────── the brain ─────────────────────────────
        IEnumerator GunmanBrain()
        {
            SetState(State.Idle);
            bool runner = _def != null && _def.RunsWhenChallenged;
            if (_hasAnim) _anim.SetGunPose(GunPose.Ready);
            float enteredAt = Time.time, patienceUntil = float.MaxValue, nextShot = 0f;
            int burst = 0, burstLength = UnityEngine.Random.Range(2, 4);
            var target = PickLootSpot(out var loot, out _);
            float nextTarget = Time.time + 10f;
            int searched = 0;
            if (Aggressive && _heard.Count == 0) Say("vo.gunman.money");
            Debug.Log($"[HIS] Intruder #{Id}: gunman in ({(runner ? "runner" : Aggressive ? "aggressive" : "hesitant")}, m0 {_morale:F2})");
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                if (!HasGun) { yield return Surrender(false); yield break; } // lost it (shouldn't happen): he gives up
                bool heard = TryHear(out var c);
                float dist = FlatDistance(PlayerOnFloor(), transform.position);
                bool challenged = heard || (runner ? NoticesAim(0.3f) : AimedFor > 0.3f) || GunshotRecently(1f) || _woundCount > 0
                                  || (Time.time - enteredAt > 3f && dist < 1.6f) || Time.time - enteredAt > LootGiveUp;

                // The runner: challenged (or bored), he's gone — gun down, back out the way he came.
                if (runner && _tellDoneAt < 0f && challenged && Upright(_gait))
                {
                    _turnPlanned = TestTurnAndPoint ?? UnityEngine.Random.value < TurnAndPointChance;
                    if (_hasAnim) _anim.SetGunPose(GunPose.None);
                    Say("vo.gunman.flee");
                    Debug.Log($"[HIS] Intruder #{Id}: runs with the gun{(_turnPlanned ? " (will turn and point)" : "")}");
                    // No caught-in-the-act startle: its hands fly up, and with a gun in one of them that sweeps the muzzle across you.
                    yield return FleeAndLeave(run: true);
                    yield break;
                }
                // A yell: the morale roll (not "Who's there?" — that's no order).
                if (heard && c != Command.WhoIsThere && MoraleRoll(c)) { yield return GunmanComply(c); yield break; }

                bool gateOpen = Time.time - _visibleSince >= PerceptionGate;
                if (_tellDoneAt < 0f)
                {
                    // Gun still low. The aggressive man raises it as soon as he's seen you; the others once challenged.
                    if (gateOpen && (Aggressive || challenged) && !Falling && !(MovementLocked && _lock >= Lock.React) && !HoldFire)
                    {
                        yield return RaiseTell(turning: false);
                        patienceUntil = Time.time + (Aggressive ? UnityEngine.Random.Range(2.5f, 4f) : UnityEngine.Random.Range(3f, 5f));
                        continue;
                    }
                    if (!Aggressive && _gait == Gait.Gun) Loot(ref target, ref loot, ref nextTarget, ref searched);
                    else if (_gait == Gait.Gun && _agent.enabled && _agent.isOnNavMesh && !MovementLocked)
                    { _agent.speed = GaitSpeed; if (dist > 4f) _agent.SetDestination(PlayerOnFloor()); else StopHere(); }
                    if (Aggressive || challenged) FaceTowards(PlayerOnFloor(), 4f);
                    yield return null;
                    continue;
                }

                // ── Gun up: a standoff. He keeps it on you and fires when provoked.
                bool floor = _gait == Gait.Crawl;
                if (_hasAnim) _anim.AimAt(PlayerChest);
                FaceTowards(PlayerOnFloor(), floor ? 2f : 8f);
                // Still out of the room with no shot on the player for 1.5 s (beside the doorway, the player out of its line):
                // he comes on in rather than stand aiming at the wall (0.1.53 review 2).
                _noShotLineFor = !floor && OutsideTheRoom && !ShotLineNow() ? _noShotLineFor + Time.deltaTime : 0f;
                if (!floor && ((Aggressive && dist > StandoffDistance) || _noShotLineFor > 1.5f) && _agent.enabled && _agent.isOnNavMesh && !MovementLocked)
                { _agent.speed = GaitSpeed; _agent.SetDestination(PlayerOnFloor()); }
                else StopHere();
                bool hold = HoldFire;
                if (hold) patienceUntil += Time.deltaTime; // the player can't answer: his patience waits too
                bool provoked = GunshotRecently(1.5f) || _woundCount > _woundsAtTell || Time.time > patienceUntil
                                || (Aggressive && (AimedFor > 0.6f || dist < 1.5f));
                if (provoked && !hold && Time.time >= nextShot && !(MovementLocked && _lock >= Lock.React) && GunOnPlayer())
                {
                    if (!FireShot(floor ? 0.6f : 1f)) nextShot = Time.time + 0.25f; // wall/furniture in the way: look again shortly
                    else if (++burst >= burstLength) { burst = 0; burstLength = UnityEngine.Random.Range(2, 4); nextShot = Time.time + UnityEngine.Random.Range(1.2f, 2f); }
                    else nextShot = Time.time + UnityEngine.Random.Range(0.35f, 0.6f);
                }
                yield return null;
            }
        }

        /// <summary>The hesitant man looking for loot with the gun held low (the burglar's rounds, looking about).</summary>
        void Loot(ref Vector3 target, ref Vector3 loot, ref float nextTarget, ref int searched)
        {
            if (!_agent.enabled || !_agent.isOnNavMesh || MovementLocked) return;
            if (FlatDistance(transform.position, target) < 0.6f)
            {
                StopHere();
                var toLoot = loot - transform.position; toLoot.y = 0f;
                if (toLoot.sqrMagnitude > 0.04f && Vector3.Angle(transform.forward, toLoot) > 25f) { FaceTowards(loot, 6f); return; }
                Gesture(searched % 2 == 0 ? Act.LookAround : Act.LookOverShoulder);
                if (++searched >= 3) { target = PickLootSpot(out loot, out _); searched = 0; nextTarget = Time.time + 10f; }
            }
            else
            {
                if (Time.time > nextTarget) { target = PickLootSpot(out loot, out _); nextTarget = Time.time + 10f; }
                _agent.speed = GaitSpeed;
                _agent.SetDestination(target);
            }
        }

        /// <summary>
        /// The TELL: the gun comes up onto you over <see cref="TellSeconds"/> (the runner's turn: <see cref="TurnTellSeconds"/>)
        /// with a shout. The judge counts the raise itself (IntruderFacts.RaisingGun); nothing fires until it's over.
        /// </summary>
        IEnumerator RaiseTell(bool turning)
        {
            float seconds = turning ? TurnTellSeconds : TellSeconds;
            CancelAct();
            StopHere();
            _raisingGun = true;
            Say(turning ? "vo.gunman.backoff" : PlayerHolds == PlayerWeapon.Gun ? "vo.gunman.dropit" : "vo.gunman.dontmove");
            if (_hasAnim) { _anim.AimAt(PlayerChest); _anim.SetGunPose(GunPose.Aimed, seconds); }
            Debug.Log($"[HIS] Intruder #{Id}: TELL — raising the gun ({seconds:F1} s, seen for {Time.time - _visibleSince:F2} s)");
            float until = Time.time + seconds;
            while (Current != State.Dead && Time.time < until)
            {
                if (_hasAnim) _anim.AimAt(PlayerChest);
                FaceTowards(PlayerOnFloor(), _gait == Gait.Crawl ? 2f : 10f);
                yield return null;
            }
            _raisingGun = false;
            if (Current == State.Dead) yield break;
            _tellDoneAt = Time.time; _woundsAtTell = _woundCount;
            Debug.Log($"[HIS] Intruder #{Id}: TELL done — gun on the player");
        }

        /// <summary>
        /// One round at the player: flash + report + kick. It leaves along his barrel (the aim pass put it on your chest) turned
        /// by his spread — wider with range, tighter once he's steady on you — and lands on the first thing that ray crosses:
        /// a wall or furniture (an impact there), your head / torso / an arm / a leg (<see cref="PlayerShape"/>, then
        /// PlayerState's wound rule), or nothing. False = not fired: before the tell, <see cref="HoldFire"/>, or a real wall /
        /// furniture between his muzzle and both your head and chest (the player's rounds stop on the same Room colliders: no
        /// shooting through the wall either way).
        /// </summary>
        bool FireShot(float accuracy)
        {
            var m = _hasAnim ? _anim.Muzzle : null;
            if (m == null || _tellDoneAt < 0f || HoldFire) return false; // never before the tell
            if (_roomOnlyMask < 0) _roomOnlyMask = LayerMask.GetMask("Room");
            if (!ShotLine(m.position, out var through)) return false;   // a wall / furniture in the way (see ShotLine)
            _lastShotAt = Time.time;
            Vector3 from = m.position, barrel = m.forward; // this frame's aimed pose, before this round's kick
            if (_hasAnim) _anim.Recoil();
            if (_weapon.TryGetComponent<IntruderGunFx>(out var fx)) fx.Fire();
            float range = Vector3.Distance(from, PlayerHead);
            float p = Mathf.Lerp(HitNear, HitFar, Mathf.InverseLerp(HitNearDist, HitFarDist, range))
                      + SteadyAimBonus * Mathf.Clamp01((Time.time - _tellDoneAt) / SteadyAimSeconds);
            p = Mathf.Clamp01(p * accuracy);
            float sigma = PlayerShape.SpreadSigma(p, range) * (TestSpread ?? 1f);
            var dir = PlayerShape.Spread(barrel, sigma, UnityEngine.Random.value, UnityEngine.Random.value);
            var zone = PlayerShape.Current(_player, PlayerHead, _floorY).Raycast(from, dir, 30f, out float at);
            bool wall = Physics.Raycast(from, dir, out var impact, zone != HitZone.None ? at : 30f, _roomOnlyMask, QueryTriggerInteraction.Ignore);
            var ps = PlayerState.Instance;
            if (wall || ps == null || ps.IsDead) zone = HitZone.None; // the wall / furniture in front of you took it
            if (zone != HitZone.None && through != null && !through.SegmentThroughOpening(from, from + dir * at, 0f)) zone = HitZone.None; // the spread put it into the wall beside the doorway
            // From outside, a miss that would land in the room through a wall really hit the wall's outside, beside the doorway:
            // heard there, muffled, not on the wall behind the player.
            bool outsideWall = false;
            if (wall && through != null && !through.SegmentThroughOpening(from, impact.point, 0f)) { wall = false; outsideWall = true; }
            else if (zone == HitZone.None && !wall && through != null && !through.SegmentThroughOpening(from, from + dir * 30f, 0f)) outsideWall = true;
            // A vehicle's body before the player, the wall or the house: the round stops in it — its hole, sparks and ping, as
            // the player's do (Brett 0.1.54: the SWAT shot him through their crashed truck; the cars are cover both ways).
            float reach = zone != HitZone.None ? at : wall ? impact.distance : 30f;
            bool car = VehicleBetween(from, from + dir * reach, out var carHit, out var carBody);
            if (car)
            {
                zone = HitZone.None; wall = false; outsideWall = false;
                carBody.ApplyDamage(new HIS.Weapons.DamageInfo { Amount = 0f, Type = HIS.Weapons.DamageType.Bullet, Point = carHit.point, Normal = carHit.normal, Direction = dir,
                                                      Source = _weapon != null ? _weapon.gameObject : gameObject });
            }
            Debug.Log($"[HIS] Intruder #{Id}: FIRES at the player ({range:F1} m, P hit {p:F2}, spread {sigma * Mathf.Rad2Deg:F1}°, " +
                      $"barrel {Vector3.Angle(barrel, PlayerChest - from):F1}° off your chest) → {(zone != HitZone.None ? "HIT " + PlayerShape.Name(zone) : car ? "into a vehicle" : "miss")}");
            if (zone != HitZone.None) ps.TakeHit(zone, HitBy.Gun, from);
            else if (wall) Sfx.Play("impact.wall", impact.point, 0.9f); // a miss goes somewhere: the wall behind you, or one in the way
            else if (outsideWall)
            {
                float toward = Vector3.Dot(dir, through.Outward);           // < 0: heading in
                float t = toward < -0.05f ? Vector3.Dot(through.Center - from, through.Outward) / toward : -1f;
                if (t > 0f) through.PlayThrough("impact.wall", from + dir * t, 0.8f, 1f, 1200f);
            }
            return true;
        }

        // ───────────────────────────── walls between him and the player (0.1.53) ─────────────────────────────
        // Brett 0.1.53: "bros shooting me through the wall". Wave 2's lead, already in view at the open front door and starting
        // his entry from the stack beside it, chest behind the wall, fired 5 rounds from 4.3 m and killed him while every M9
        // round of Brett's stopped on that wall. MRUK's walls only stop rays from the room side, and FireShot only made HIDDEN
        // men (BehindDoor = not visible) go through the opening. Now everything that reaches the player from outside — rounds,
        // the Taser, a blow, the player aiming at him — needs his body in view through a doorway, whether he's hidden or not.

        /// <summary>His head (the bone when animated) and his chest.</summary>
        Vector3 BodyHead => _hasAnim && _anim.Head != null ? _anim.Head.position : new Vector3(transform.position.x, _floorY + 1.55f, transform.position.z);
        Vector3 BodyChest => _hasAnim && _anim.Hips != null ? _anim.Hips.position + Vector3.up * 0.3f : new Vector3(transform.position.x, _floorY + 1.25f, transform.position.z);

        float _noShotLineFor;

        static readonly RaycastHit[] s_carHits = new RaycastHit[8];

        /// <summary>
        /// The nearest vehicle body between <paramref name="a"/> and <paramref name="b"/> (not its glass: a round goes on
        /// through a pane, as the player's does). Only while vehicles are out (PoliceCars active); no allocations.
        /// </summary>
        static bool VehicleBetween(Vector3 a, Vector3 b, out RaycastHit hit, out HIS.Security.VehicleBody body)
        {
            hit = default; body = null;
            var pc = HIS.Security.PoliceCars.Instance;
            if (pc == null || !pc.Active) return false;
            var d = b - a; float len = d.magnitude;
            if (len < 1e-3f) return false;
            int n = Physics.RaycastNonAlloc(a, d / len, s_carHits, len, 1 << HIS.Security.PorchSet.OutsideLayer, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (s_carHits[i].distance >= best) continue;
                var vb = s_carHits[i].collider.GetComponent<HIS.Security.VehicleBody>();
                if (vb == null) continue;
                best = s_carHits[i].distance; hit = s_carHits[i]; body = vb;
            }
            return body != null;
        }

        /// <summary>On the scanned room's floor (no room yet: inside).</summary>
        static bool InRoom(Vector3 p)
        {
            var room = HIS.Room.RoomScanner.Instance != null ? HIS.Room.RoomScanner.Instance.Room : null;
            return room == null || room.IsPositionInRoom(p, false);
        }

        /// <summary>Out of the room: hidden behind his door, or his chest off the room's floor (on the path, the step, beside the doorway).</summary>
        public bool OutsideTheRoom => BehindDoor || !InRoom(BodyChest);

        /// <summary>The door whose opening joins <paramref name="a"/> and <paramref name="b"/> (one each side of its plane), or null.</summary>
        static HIS.Room.VirtualDoor OpeningBetween(Vector3 a, Vector3 b, float margin = 0.1f)
        {
            foreach (var d in HIS.Room.VirtualDoor.All) if (d != null && d.SegmentThroughOpening(a, b, margin)) return d;
            return null;
        }

        /// <summary>
        /// The player can see him: in the room, or — out of it — his head or chest in line with the player's head or chest through
        /// a door's opening (<paramref name="via"/>), or the player out there with him (leaning out of the doorway). Walls only.
        /// </summary>
        public bool InViewOfPlayer(out HIS.Room.VirtualDoor via)
        {
            via = null;
            if (!OutsideTheRoom) return true;
            Vector3 ph = PlayerHead, pc = PlayerChest, bh = BodyHead, bc = BodyChest;
            // The player out here too (leaning out of the doorway), and the line between them stays out of the room — not a man
            // round the side of the house seeing him across it (review 2).
            if (!InRoom(ph) && !InRoom(pc) && !InRoom(Vector3.Lerp(bc, pc, 0.5f))) return true;
            via = OpeningBetween(bh, ph) ?? OpeningBetween(bh, pc) ?? OpeningBetween(bc, ph) ?? OpeningBetween(bc, pc);
            return via != null;
        }
        public bool InViewOfPlayer() => InViewOfPlayer(out _);

        /// <summary>
        /// A round from the muzzle at <paramref name="muzzle"/> can reach the player: no Room collider across both lines to his
        /// head and chest, and from out of the room his body is in view through a doorway and the gun points through it — the
        /// muzzle still outside: the line to the player crosses that opening (<paramref name="roundGate"/>: every round must);
        /// the muzzle already reaching in (a raised pistol at the doorway): the arm came in through the opening, not the wall.
        /// No side effects (WantsToFire, the standoff, the Taser use it).
        /// </summary>
        bool ShotLine(Vector3 muzzle, out HIS.Room.VirtualDoor roundGate)
        {
            roundGate = null;
            if (_roomOnlyMask < 0) _roomOnlyMask = LayerMask.GetMask("Room");
            // A wall, the furniture or a vehicle's body across both lines (the cars out front are cover: 0.1.54).
            bool chestBlocked = Physics.Linecast(muzzle, PlayerChest, _roomOnlyMask, QueryTriggerInteraction.Ignore) || VehicleBetween(muzzle, PlayerChest, out _, out _);
            if (chestBlocked && (Physics.Linecast(muzzle, PlayerHead, _roomOnlyMask, QueryTriggerInteraction.Ignore) || VehicleBetween(muzzle, PlayerHead, out _, out _))) return false;
            if (!InViewOfPlayer(out var via)) return false;
            if (via == null) return true;                                        // in the room, or the player out here with him
            if (Vector3.Dot(muzzle - via.Center, via.Outward) <= 0f)             // the gun already reaches in
                return via.SegmentThroughOpening(BodyChest, muzzle, 0f) || via.SegmentThroughOpening(BodyHead, muzzle, 0f);
            roundGate = OpeningBetween(muzzle, PlayerChest) ?? OpeningBetween(muzzle, PlayerHead);
            return roundGate != null;
        }

        /// <summary><see cref="ShotLine"/> from his gun's muzzle now (false with no gun out).</summary>
        public bool ShotLineNow()
        {
            var m = _hasAnim ? _anim.Muzzle : null;
            return m != null && ShotLine(m.position, out _);
        }

        /// <summary>A blow or a Taser dart can reach the player: in the room, or his body seen through a doorway (no reaching round the jamb).</summary>
        public bool ReachesPlayerFromHere() => !OutsideTheRoom || InViewOfPlayer();

        // ───────────────────────────── compliance (research/08 §2c) ─────────────────────────────
        /// <summary>Distance band for "pressure changed": under 2 m, 2–5 m, over 5 m.</summary>
        int DistanceBand() { float d = FlatDistance(PlayerOnFloor(), transform.position); return d < 2f ? 0 : d <= 5f ? 1 : 2; }

        /// <summary>m_eff = m0 + modifiers: what's pointed at him, how close, his wounds, being shot at, having fired, repeat yells.</summary>
        float MoraleNow()
        {
            float m = _morale;
            var held = PlayerHolds;
            if (AimedAt) m -= 0.25f;
            else m += held == PlayerWeapon.Gun ? -0.10f : held == PlayerWeapon.Knife ? -0.05f : 0.20f;
            int band = DistanceBand();
            if (band == 0) m -= 0.10f; else if (band == 2) m += 0.10f;
            if (_woundCount > 0) m -= Mathf.Min(0.6f, 0.30f + 0.15f * (_woundCount - 1));
            if (GunshotRecently(3f)) m -= 0.15f;              // shot at: rounds going past him
            if (Time.time - _lastShotAt < 3f) m += 0.20f;     // he's just fired: blood up
            m -= 0.05f * Mathf.Min(_repeatYells, 3);
            return m;
        }

        /// <summary>fit[intent, command]: the aggressive man barely listens; the others do what they're told (research/08 §2c).</summary>
        float Fit(Command c) => c switch
        {
            Command.DropWeapon => Aggressive ? 0.3f : 1f,
            Command.ShowHands or Command.GetDown => Aggressive ? 0.2f : 0.8f,
            Command.GetOut => Aggressive ? 0.2f : 0.6f,
            _ => 0f,
        };

        /// <summary>
        /// P(comply) = fit × (1 − m_eff). Rolled once per pressure: yelling the same thing again rolls again only after his aim
        /// state, wounds or distance band changed, or 3 s passed (SWAT 4 — no mashing the yell button into a surrender).
        /// </summary>
        bool MoraleRoll(Command c)
        {
            bool aimed = AimedAt; int band = DistanceBand();
            bool fresh = _rollAt < 0f || aimed != _rollAimed || _woundCount != _rollWounds || band != _rollBand || Time.time - _rollAt > 3f;
            if (!fresh) { Debug.Log($"[HIS] Intruder #{Id} ignores '{c}' (no new pressure: no re-roll)"); return false; }
            if (_rollAt >= 0f) _repeatYells++;
            _rollAt = Time.time; _rollAimed = aimed; _rollWounds = _woundCount; _rollBand = band;
            float m = MoraleNow();
            float p = Mathf.Clamp01(Fit(c) * (1f - m));
            float r = TestRoll ?? UnityEngine.Random.value;
            bool yes = r < p;
            Debug.Log($"[HIS] Intruder #{Id} morale roll '{c}': m {m:F2}, P {p:F2} → {(yes ? "complies" : "refuses")}");
            return yes;
        }

        /// <summary>
        /// He gives in: "Get out!" → he backs off out of the door, gun down. Anything else → "Okay, I'm putting it down!", the
        /// gun lowered and lobbed away to his side (TossAway; on the floor or his knees he just lets go), then the surrender
        /// ladder (hands up → kneel → prone) — dropping the weapon BEFORE the hands go up, so it reads (research/08, SEF).
        /// </summary>
        IEnumerator GunmanComply(Command c)
        {
            _raisingGun = false; _tellDoneAt = -1f;
            _reactions++;
            CancelAct();
            StopHere();
            if (c == Command.GetOut)
            {
                if (_hasAnim) _anim.SetGunPose(GunPose.None);
                Say("vo.gunman.flee");
                yield return FleeAndLeave(run: true);
                yield break;
            }
            SetState(State.Complying);
            Say("vo.gunman.putdown");
            if (_hasAnim) _anim.SetGunPose(GunPose.Ready);
            if (_hasAnim && _gait == Gait.Gun && !Falling && Gesture(Act.TossAway))
            {
                if (_hasAnim) _anim.SetGunPose(GunPose.None);
                float release = Time.time + IntruderClips.TossRelease;
                while (Current != State.Dead && Time.time < release && MovementLocked && _lock == Lock.Gesture) yield return null;
                if (Current == State.Dead) yield break;
                if (HasGun) _weapon.Toss(transform.right * 1.4f + transform.forward * 1.4f + Vector3.up * 1.4f); // the overhand swing, off to his gun side
                float done = release - IntruderClips.TossRelease + IntruderClips.TossDone;
                while (Current != State.Dead && Time.time < done && MovementLocked && _lock == Lock.Gesture) yield return null;
                CancelAct();
            }
            else if (HasGun) _weapon.Drop(transform.right * 0.6f + Vector3.up * 0.3f);
            if (Current == State.Dead) yield break;
            if (_hasAnim) _anim.SetGunPose(GunPose.None);
            if (_gait == Gait.Gun) SetGait(Gait.Calm);   // empty-handed now: the plain stance under his raised hands
            yield return Surrender(c == Command.GetDown, quiet: true);
        }

        /// <summary>
        /// The runner, once, mid-flight: he stops, turns and brings the gun up at you (the 0.8 s tell, "Back off!"), fires one
        /// hurried round (half accuracy), then runs on. Fleeing or not, a gun on you is a good call.
        /// </summary>
        IEnumerator TurnAndPoint()
        {
            Debug.Log($"[HIS] Intruder #{Id}: turns and points the gun as he runs");
            StopHere();
            yield return RaiseTell(turning: true);
            float giveUp = Time.time + 0.6f;
            while (Current != State.Dead && Time.time < giveUp && !GunOnPlayer()) { if (_hasAnim) _anim.AimAt(PlayerChest); FaceTowards(PlayerOnFloor(), 10f); yield return null; }
            if (Current == State.Dead) yield break;
            if (GunOnPlayer()) FireShot(0.5f);
            for (float hold = Time.time + 0.35f; Current != State.Dead && Time.time < hold; ) { if (_hasAnim) _anim.AimAt(PlayerChest); yield return null; }
            _tellDoneAt = -1f;
            if (_hasAnim) _anim.SetGunPose(GunPose.None);
        }

        /// <summary>FleeAndLeave's hook: the planned turn, once, 2–7 m from you, after the first 0.8 s of running.</summary>
        bool WantsTurnAndPoint(float fleeingFor)
        {
            if (!_turnPlanned || !HasGun || MovementLocked || fleeingFor < 0.8f || !Upright(_gait)) return false;
            float d = FlatDistance(PlayerOnFloor(), transform.position);
            if (d < 2f || d > 7f) return false;
            _turnPlanned = false;
            return true;
        }
    }
}
