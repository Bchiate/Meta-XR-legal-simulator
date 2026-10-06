using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;
using Gait = HIS.Intruder.IntruderAnimator.Gait;
using GunPose = HIS.Intruder.IntruderAnimator.GunPose;

namespace HIS.Intruder
{
    /// <summary>
    /// A US Marshal night's hitman (research/27 §2; Brett 2026-10-01: "its the same wave style game just with hitmen instead of
    /// police"). He runs the officer's machine (IntruderPolice.cs: the ride in, the walk up, the stack, the kick, the spread
    /// round you) with <see cref="Faction"/> = Hitmen and his wave's <see cref="HitmanTier"/>. What differs lives here:
    ///   1. The TELL (Brett's rule: a visible or audible warning before any round that can kill you). From the moment he first
    ///      has a line on you his gun stays at low ready for `react` s while he turns onto you, then visibly comes up over
    ///      `raise` s. No round before react + raise, never under <see cref="HitmanTier.MinWindow"/>, and
    ///      <see cref="HitmanTier.BehindExtra"/> more if he's over <see cref="HitmanTier.BehindAngle"/>° off where you face. His
    ///      first contact is spoken on the one-voice floor, or a slide rack at his gun. No line for
    ///      <see cref="HitmanTier.BlindReset"/>: gun low, the window starts again — so every new angle raises again in view.
    ///   2. The fight inside (<see cref="HitmanFight"/>): the officer's standoff loop without orders, Taser or cuffs. Bursts and
    ///      accuracy by tier, an aim that trails a moving player (AimLag), a magazine and reload ("Reloading." from the pros and
    ///      operators, never two mates at once, a step out of your line for it from wave 2 on: Question 6), a new angle when
    ///      blind, and from the floor once his legs are out (×0.6, as the gunman). The pros and operators hit inside fight on
    ///      (IntruderPolice.OfficerHit; the tier's FightsOnHit).
    ///   3. The stack (<see cref="HitmanStack"/>): no knock or announce. From wave 2 on they answer a round you put through THEIR
    ///      door while they're stacked at it (the tier's BlindVolley: 1 round, 2, 3, 4): a rack, 0.6 s, then rounds back along
    ///      the line to where your muzzle was — never where you are now (if you moved, it misses).
    ///   4. The operators' plate (wave 5, <see cref="PlateStop"/>): the first chest round is a stagger, a clank and sparks, no blood.
    /// Brett 0.1.57's five waves: what KIND of hitman he is (his talk, his report, the finder's pick) reads his tier's
    /// <see cref="HitmanTier.Look"/> (street / pro / operator), never the wave number (which is only his wave: its car, its
    /// one finder).
    ///   5. The finder (<see cref="FinderHunt"/>, research/27 §2 "The finder"): one man a wave goes for Ricky instead of you —
    ///      "Where's Ricky?", a spot 1.3–2.0 m from him with a line to his chest, 1.0 s of raise, then pairs at him. He turns
    ///      on you only if you come within 2 m or hit him (or he can't get at Ricky), and then through a fresh tell.
    /// Hitmen are never judged (the director never subscribes to them, as with officers) and never surrender (no brain for it).
    /// </summary>
    public partial class Intruder
    {
        // ---- Tunables (research/27 §2; the per-wave knobs are HitmanTier's) ----
        const float HitLineSeconds = 0.1f;       // how often the line to the player is re-tested (a raycast or two)
        const float AimTrailMax = 0.8f;          // fires where his lagging aim is only while it's this close to your chest (m)
        const float FloorAccuracy = 0.6f;        // legs out, from the floor: × his accuracy (the gunman's floor fire)
        const float OperatorReportPitch = 0.85f; // the operators' "SMG" (wave 5): the pistol report pitched down, a deeper crack
        const float VolleyWait = 0.6f;           // the blind volley: rack, this long, then the rounds (research/27 §2)
        const int MaxVolleys = 2;                // answers through the door per man per stack (not a duel through the leaf)
        const float CoverReach = 1.5f;           // Question 6: cover is a NavMesh point at most this far
        const float CoverCooldown = 4f;          // at most one step out of line this often (a near miss, then fight again)
        const float NearMissCheck = 0.15f;       // after a shot with the player's gun on him: no wound by then = a near miss
        const float BlindTick = 0.25f;           // the blind clock's step (the operators re-angle after 1.2 s)
        const float WalkInCap = 12f;             // the walk in (EnterRoom): this long from the apron, and he fights from where he is…
        const float WalkInDepth = 0.6f;          // …once this far in past his door's plane (never in the doorway, plugging it)
        static readonly float[] s_coverRadii = { 0.6f, 1.0f, 1.4f };
        bool _walkInCapped;                      // (logged once)

        /// <summary>
        /// The walk in has run long (the fixer, 0.1.58: the integrator's MarshalSmoke had a wave-4 lead and cover firing from
        /// just inside the doorway for 150 s without finishing their walk in — so no cover to reload, never "fighting", and the
        /// wave ended only on its timeout; SmokeTwoDoors' wave-2 side man the same): <see cref="WalkInCap"/> s after he set off
        /// from the apron, a man at least <see cref="WalkInDepth"/> m inside his door's plane stops where he stands and takes the
        /// fight from there (EnterRoom's until: "in the room", then HitmanFight). PathTo's stuck clock no longer resets on his
        /// shots either (Intruder.PathTo); this is the backstop for a man still shuffling. Hitmen only: the police never ask.
        /// </summary>
        bool HitmanWalkInOver(float from)
        {
            if (Time.time - from < WalkInCap || BehindDoor) return false;
            var door = Door;
            if (door == null || Vector3.Dot(transform.position - door.Threshold, door.Inward) < WalkInDepth) return false;
            if (!_walkInCapped)
            {
                _walkInCapped = true;
                Debug.Log($"[HIS] Hitman #{Id} ({Role}, wave {Wave}): {WalkInCap:F0} s walking in — fights from {transform.position:F2}");
            }
            return true;
        }

        // ── the tell
        float _seenSince = -1f, _raiseAt, _hitWindow, _lostLineAt = -1f;
        bool _hitRaised, _hitCalledOut;
        float _hitLineAt; bool _hitLine;
        // ── aim, bursts, magazine
        Vector3 _aimPoint; bool _aimInit;
        float _hitNextShot; int _hitBurst, _hitBurstLength = 2, _magLeft;
        float _reloadUntil = -1f; bool _magInDone, _reloadCovered;
        // ── cover, the floor, the door
        float _nextCoverAt, _nearMissCheckAt = -1f, _nearMissSeen = -100f; int _nearMissWounds;
        bool _wasDowned;
        float _volleyFor = -100f; int _volleys;

        /// <summary>The player has fired since this hitman wave was called (PoliceResponse.FiredThisWave; tests: TestFired).</summary>
        bool HitFiredThisWave => Police != null ? Police.FiredThisWave : PoliceResponse.TestFired;
        /// <summary>Legs shot out: on the floor, fighting from there (research/27 §2).</summary>
        bool HitmanDowned => _gait == Gait.Crawl;
        /// <summary>
        /// His pace on the room NavMesh: the tier's gun walk; out of the fight (wave 1 hit) 0; down, 0 — except on the doorway
        /// link, where he crawls off it (a speed of 0 there would hold him on the threshold for good).
        /// </summary>
        float HitmanWalk => HitmanDowned ? (_agent.enabled && _agent.isOnOffMeshLink ? IntruderClips.CrawlSpeed : 0f)
                          : OfficerHit ? 0f : AimedWalk;
        bool Reloading => _reloadUntil >= 0f;

        /// <summary>Once, from ConfigureOfficer (after DressAsHitman): a full magazine, his first burst, the operators' deeper report.</summary>
        void InitHitman()
        {
            ResetFinder();
            _magLeft = _tier.Magazine;
            _hitBurstLength = RollBurst();
            if (_weapon != null && _weapon.TryGetComponent<IntruderGunFx>(out var fx)) fx.Pitch = _tier.Look >= HitmanTier.LookOperator ? OperatorReportPitch : 1f;
            if (_tier.Plated) PlateSparks.Warm();   // (made now, not on the first plate hit mid-fight)
        }

        int RollBurst() => UnityEngine.Random.Range(_tier.BurstRounds.x, _tier.BurstRounds.y + 1);
        static float Roll(Vector2 r) => UnityEngine.Random.Range(r.x, r.y);

        // ───────────────────────────── the tell ─────────────────────────────
        /// <summary>
        /// Every frame he could fire (EnterRoom's walk, HoldAndFire on the path, the fight): runs the tell and says whether a
        /// round may go now. The line (<see cref="HitmanLine"/>) starts it; at <c>_raiseAt</c> the gun comes up (GunUp(raise))
        /// with his callout; nothing before the window, the raise's end, his burst clock, a hit reaction, with his gun off
        /// you, or with no round FireShot would take from his muzzle (ShotLineNow: the shared wall rule, 0.1.53 — so a walk
        /// for a new angle isn't a stop-start of refused shots, as WantsToFire for the police). No line for BlindReset: the
        /// gun drops and it all starts again. An empty magazine starts the reload instead.
        /// </summary>
        bool HitmanWantsToFire()
        {
            // The finder ignores you (research/27 §2): no tell on you, no round, until you come within 2 m or hit him.
            if (_finding && FinderHolds()) return false;
            ReloadTick();
            if (_magLeft <= 0 && !Reloading) TryStartReload();   // (empty, and a mate's reload was on: his turn now?)
            // Something else took his gun down (a kick, the dash to the stack): the raise is gone, and with it the tell.
            if (_tellDoneAt >= 0f && _hasAnim && _anim.CurrentGunPose != GunPose.Aimed) ResetTell("gun lowered");
            if (!HitmanLine())
            {
                if (_seenSince >= 0f)
                {
                    if (_lostLineAt < 0f) _lostLineAt = Time.time;
                    else if (Time.time - _lostLineAt >= HitmanTier.BlindReset) ResetTell("lost the player");
                }
                return false;
            }
            _lostLineAt = -1f;
            if (Reloading) return false;
            if (_seenSince < 0f) StartTell();
            bool reacting = MovementLocked && _lock >= Lock.React;
            if (!_hitRaised)
            {
                if (Time.time < _raiseAt || reacting || _weapon == null) return false;
                _hitRaised = true;
                GunUp(_tier.Raise);
                HitmanCallout();
                Report($"raises the gun ({_tier.Raise:F2} s)");
                return false;
            }
            if (Time.time < _seenSince + _hitWindow || Time.time < _tellDoneAt || Time.time < _hitNextShot || reacting || _magLeft <= 0) return false;
            // (Last: two linecasts and the doorway test, only once a round is otherwise due.)
            return HitmanOnTarget() && ShotLineNow();
        }

        /// <summary>His gun is up on you: the tell's raise is over (GunUp(raise) ran and its time is done).</summary>
        bool HitmanGunUp => _hitRaised && _tellDoneAt >= 0f && Time.time >= _tellDoneAt;

        /// <summary>
        /// First line on the player: the clock starts. The gun stays low for `react` (he turns onto you), then comes up with his
        /// callout; no round before the tier's window (react + raise, never under MinWindow) + the extra from behind. From
        /// behind the raise and the callout still come at `react`: the extra half second is all after the audible warning.
        /// </summary>
        void StartTell()
        {
            _seenSince = Time.time; _lostLineAt = -1f; _hitRaised = false;
            float react = UnityEngine.Random.Range(_tier.React.x, _tier.React.y);
            bool behind = false;
            if (_player != null)
            {
                var face = _player.forward; face.y = 0f;
                var to = transform.position - _player.position; to.y = 0f;
                behind = face.sqrMagnitude > 1e-4f && to.sqrMagnitude > 1e-4f && Vector3.Angle(face, to) > HitmanTier.BehindAngle;
            }
            _hitWindow = _tier.Window(react, behind);
            _raiseAt = _seenSince + react;
            _aimPoint = PlayerChest; _aimInit = true;
            Report($"sees the player: gun low {_raiseAt - _seenSince:F2} s, raise {_tier.Raise:F2} s, no round before {_hitWindow:F2} s{(behind ? " (from behind)" : "")}");
        }

        /// <summary>The tell is off: no line, gun at low ready (if it was up), the next line starts a fresh window.</summary>
        void ResetTell(string why)
        {
            bool was = _seenSince >= 0f || _tellDoneAt >= 0f;
            _seenSince = -1f; _lostLineAt = -1f; _hitRaised = false; _tellDoneAt = -1f;
            if (_hasAnim && _anim.CurrentGunPose == GunPose.Aimed) _anim.SetGunPose(GunPose.Ready);
            if (was) Report($"gun low ({why})");
        }

        /// <summary>His gun out at low ready, the tell reset (GunUp() for a hitman: the gun only comes up through the tell).</summary>
        void HitmanLowReady(string why)
        {
            ResetTell(why);
            if (_hasAnim && _weapon != null && _weapon.InHand) _anim.SetGunPose(GunPose.Ready);
        }

        /// <summary>The tell's visible half: the gun up onto his (lagging) aim point over <paramref name="raise"/> seconds.</summary>
        void GunUp(float raise)
        {
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            if (_gait != Gait.Gun && Upright(_gait)) SetGait(Gait.Gun);
            if (_hasAnim) { _anim.AimAt(_aimPoint); _anim.SetGunPose(GunPose.Aimed, raise); }
            _tellDoneAt = Time.time + raise;
        }

        /// <summary>
        /// The tell's audible half, once per man: his first-contact line (the tier's CalloutKey: "There he is!" from the street men, "Contact." from the pros and operators) on the
        /// one-voice floor; with another man on the floor, or no take yet (a missing clip is silence, research/27 §5), a slide
        /// rack at his gun instead.
        /// </summary>
        void HitmanCallout()
        {
            if (_hitCalledOut || Current == State.Dead) return;
            _hitCalledOut = true;
            var key = _tier.CalloutKey;
            if (key != null && Sfx.Has(key) && !(FloorBusy && s_talker != this)) { OfficerSay(key, urgent: true); return; }
            GunSound("pistol.rack", 0.9f);
            Report("racks his slide (the callout)");
        }

        /// <summary>A hitman's line only if it has a take (no new voice clips in milestone A: silence, never another voice).</summary>
        void HitmanSay(string key)
        {
            if (Sfx.Has(key)) OfficerSay(key, urgent: true);
            else Debug.Log($"[HIS] Hitman #{Id} ({Role}): \"{key}\" (no clip: silent)");
        }

        /// <summary>A sound at his gun (muffled through his shut door when he's behind it).</summary>
        void GunSound(string key, float volume)
        {
            if (!Sfx.Has(key)) return;
            var m = _hasAnim ? _anim.Muzzle : null;
            BodySound(key, m != null ? m.position : transform.position + Vector3.up * 1.2f, volume);
        }

        // ───────────────────────────── line and aim ─────────────────────────────
        /// <summary>His line on the player, re-tested every <see cref="HitLineSeconds"/> (<see cref="SeesPlayerNow"/>).</summary>
        bool HitmanLine()
        {
            if (Time.time < _hitLineAt) return _hitLine;
            _hitLineAt = Time.time + HitLineSeconds;
            return _hitLine = SeesPlayerNow();
        }

        /// <summary>
        /// In the room: nothing of the room between his eyes and your chest. Out of it (the path, the step, beside the
        /// doorway — OutsideTheRoom, the shared wall rule in IntruderGunman, 0.1.53): only through his open doorway
        /// (SeesPlayerThrough → InViewOfPlayer: the scanned walls don't stop rays from that side), and from the path only on
        /// waves that fire from it (2–3), or once you've fired this wave (wave 1). Never with the player unable to answer.
        /// (It went by his door's plane before: a man in view, his feet inside that plane but his chest off the room's floor
        /// beside the doorway, "saw" you through the wall with LineFrom, and his tell ran on a line FireShot never took.)
        /// </summary>
        bool SeesPlayerNow()
        {
            if (HoldFire || Current is State.Dead or State.Gone) return false;
            if (!OutsideTheRoom) return LineFrom(transform.position);
            if (BehindDoor && !_tier.FireFromPath && !HitFiredThisWave) return false;
            var door = Door;
            return door != null && door.OpenAngle > 20f && SeesPlayerThrough(door);
        }

        /// <summary>
        /// A line for the fight's blind clock (<see cref="HitmanFight"/>): he sees you (<see cref="HitmanLine"/>), and once his
        /// gun is up (<see cref="HitmanGunUp"/>) a round FireShot will take from his muzzle (ShotLineNow). Seeing alone left
        /// him frozen: eyes on you over a table, or out beside the doorway with his body in view but his gun behind the jamb,
        /// FireShot refused every round (HitmanShot retried each 0.25 s) while the clock read "line" and never re-angled.
        /// Before the raise it's seeing only: the muzzle at low ready is no test of a shot.
        /// </summary>
        bool HitmanHasShot() => HitmanLine() && (!HitmanGunUp || ShotLineNow());

        /// <summary>
        /// <see cref="LineFrom"/> for a spot he isn't standing on (the re-peek), under the shared wall rule (0.1.53): a spot off
        /// the room's floor (the apron, beside the doorway) also needs his eyes or chest on your head or chest through a door's
        /// opening — from out there the scanned walls don't stop LineFrom's ray, so it said yes through the wall.
        /// </summary>
        bool LineFromSpot(Vector3 floor)
        {
            if (!LineFrom(floor)) return false;
            var chest = new Vector3(floor.x, _floorY + 1.25f, floor.z);
            if (InRoom(chest)) return true;
            var eye = new Vector3(floor.x, _floorY + 1.45f, floor.z);
            return OpeningBetween(eye, PlayerHead) != null || OpeningBetween(eye, PlayerChest) != null
                || OpeningBetween(chest, PlayerHead) != null || OpeningBetween(chest, PlayerChest) != null;
        }

        /// <summary>From standing (or lying, legs out) at <paramref name="floor"/>: nothing of the room between his eyes and your chest.</summary>
        bool LineFrom(Vector3 floor)
        {
            var eye = new Vector3(floor.x, _floorY + (HitmanDowned ? 0.45f : 1.45f), floor.z);
            var to = PlayerChest - eye;
            if (s_roomMask == 0) s_roomMask = LayerMask.GetMask("Room");
            return !Physics.Raycast(eye, to.normalized, Mathf.Max(0f, to.magnitude - 0.3f), s_roomMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// OfficerAim for a hitman: his aim point eases onto your chest (1 − e^(−dt/τ), τ = the tier's AimLag: a moving player
        /// gets a trailing gun), the gun at low ready until the tell raised it, then on that point; body turned to you (slowly
        /// from the floor). No weapon light.
        /// </summary>
        void HitmanAim()
        {
            if (_finding && FinderHolds() && FinderAim()) return;   // the finder: on Ricky, not you
            ReloadTick();
            float dt = Time.deltaTime;
            var chest = PlayerChest;
            if (!_aimInit) { _aimPoint = chest; _aimInit = true; }
            else if (dt > 0f) _aimPoint = Vector3.Lerp(_aimPoint, chest, 1f - Mathf.Exp(-dt / Mathf.Max(0.02f, _tier.AimLag)));
            if (_hasAnim && _weapon != null && _weapon.InHand)
            {
                _anim.AimAt(_aimPoint);
                if (_tellDoneAt < 0f && _anim.CurrentGunPose != GunPose.Ready) _anim.SetGunPose(GunPose.Ready);
            }
            bool walking = _agent.enabled && _agent.hasPath && _agent.velocity.sqrMagnitude > 0.04f;
            if (dt > 0f && !walking) FaceTowards(PlayerOnFloor(), HitmanDowned ? 2f : 6f);
        }

        /// <summary>
        /// His muzzle is on you (the judge's own test), or on his trailing aim point while that's still near your chest: he
        /// fires where his gun is — at you a moment ago. Further off he's still swinging onto you.
        /// </summary>
        bool HitmanOnTarget()
        {
            var m = HasGun && _hasAnim ? _anim.Muzzle : null;
            if (m == null) return false;
            if (GunOnPlayer()) return true;
            if ((_aimPoint - PlayerChest).sqrMagnitude > AimTrailMax * AimTrailMax) return false;
            var to = _aimPoint - m.position;
            return to.sqrMagnitude > 0.04f && Vector3.Angle(m.forward, to) < PointConeDegrees;
        }

        // ───────────────────────────── fire, magazine, reload ─────────────────────────────
        /// <summary>
        /// One round through the gunman's path (FireShot: his barrel, the spread, the walls, your body), at his tier's accuracy
        /// (from the path through the doorway: the path accuracy; from the floor ×0.6), then his burst clock and magazine.
        /// </summary>
        bool HitmanShot()
        {
            var ps = PlayerState.Instance;
            float before = ps != null ? ps.LastHitTime : 0f;
            float accuracy = (BehindDoor ? _tier.PathAccuracy : _tier.Accuracy) * (HitmanDowned ? FloorAccuracy : 1f);
            if (!FireShot(accuracy)) { _hitNextShot = Time.time + 0.25f; return false; }   // a wall in the way: look again shortly
            _magLeft--;
            bool hit = ps != null && ps.LastHitTime != before;
            Report(hit ? "fired hit" : "fired miss");
            var pr = Police;
            if (pr != null) pr.OfficerFired(this, hit);
            if (++_hitBurst >= _hitBurstLength) { _hitBurst = 0; _hitBurstLength = RollBurst(); _hitNextShot = Time.time + Roll(_tier.BurstPause); }
            else _hitNextShot = Time.time + Roll(_tier.BurstGap);
            if (_magLeft <= 0) { _hitBurst = 0; TryStartReload(); }
            return true;
        }

        /// <summary>
        /// The magazine's empty: gun to low ready, the mag out (and "Reloading." from the pros and operators: the tier's CallsReload), a fresh one in after the tier's
        /// reload. Never two of a wave at once (research/27 §2): a mate's reload is on, he waits for it, empty.
        /// </summary>
        bool TryStartReload()
        {
            if (Reloading) return true;
            for (int i = 0; i < s_squad.Count; i++) { var o = s_squad[i]; if (IsMate(o) && o.Reloading) return false; }
            _reloadUntil = Time.time + _tier.Reload; _magInDone = false; _reloadCovered = false;
            HitmanLowReady("reloading");
            GunSound("pistol.magout", 0.8f);
            if (_tier.CallsReload) HitmanSay("vo.hitman.pro.reload");
            Report($"reloading ({_tier.Reload:F1} s)");
            return true;
        }

        /// <summary>The reload's clock: the new mag in two-thirds of the way, the slide home at the end, a full magazine.</summary>
        void ReloadTick()
        {
            if (!Reloading) return;
            if (!_magInDone && Time.time >= _reloadUntil - _tier.Reload * 0.35f) { _magInDone = true; GunSound("pistol.magin", 0.8f); }
            if (Time.time < _reloadUntil) return;
            _reloadUntil = -1f;
            _magLeft = _tier.Magazine;
            GunSound("pistol.sliderelease", 0.7f);
            Report("reloaded");
        }

        // ───────────────────────────── the walk up: his steps (Brett 0.1.56) ─────────────────────────────
        /// <summary>A step on the path is heard toward him from his door, at most this far out (out at the street his own spot
        /// would leave it inaudible); one step sound for the whole squad at most every <see cref="PathStepGap"/> s (the door's
        /// three through-sources stay free for the kick and the car doors).</summary>
        const float PathStepReach = 3f, PathStepGap = 0.22f;
        static float s_nextPathStep;
        bool _pathStepsReported;

        /// <summary>
        /// One of his steps on the walk up (IntruderBrains.TrackFacts, outside, on his feet; Brett 0.1.56: "the bad guys seemingly
        /// come from no where"): gravel under his feet, louder as he nears his door, muffled through it while it's shut. Hitmen
        /// only: the police's and the visitors' walk-ups are as before.
        /// </summary>
        void PathStep(Vector3 p)
        {
            float now = Time.time;
            if (now < s_nextPathStep && s_nextPathStep - now <= PathStepGap) return;   // (a stale clock from an earlier Play session: ignored)
            s_nextPathStep = now + PathStepGap;
            if (!_pathStepsReported) { _pathStepsReported = true; Report("footsteps up the path"); }
            string key = Sfx.Has("steps.gravel") ? "steps.gravel" : "intruder.step";
            var door = Door;
            if (door == null) { Sfx.Play(key, p, 0.7f); return; }
            float near = Mathf.InverseLerp(14f, 2f, FlatDistance(p, door.Center));
            var at = door.Center + Vector3.ClampMagnitude(p - door.Center, PathStepReach);
            door.PlayThrough(key, at, Mathf.Lerp(0.5f, 1f, near), UnityEngine.Random.Range(0.92f, 1.08f), door.IsOpen ? 0f : 900f);
        }

        // ───────────────────────────── the stack: no knock, the blind volley ─────────────────────────────
        /// <summary>
        /// Stacked on the step (hittable through the leaf), waiting for the go (PoliceResponse's entry hold: both doors manned).
        /// No knock, no announce. From wave 2 on (the tier's BlindVolley): a round you put through his door while he's stacked at it is answered
        /// (<see cref="BlindVolley"/>), by the man who'd kick it.
        /// </summary>
        IEnumerator HitmanStack(HIS.Room.VirtualDoor door)
        {
            SetState(State.BreakIn);                         // at the door: a round through the leaf finds him (SetState)
            _doorHealth = Health;
            if (_hasAnim) _anim.SetGunPose(GunPose.Ready);
            var pr = Police;
            if (pr != null) pr.OfficerAtDoor(this);
            Report("at the door");
            float stackedAt = Time.time;
            while (!CopEntry && Current != State.Dead)
            {
                if (OfficerHit) yield break;
                ReloadTick();
                if (WantsBlindVolley(door, stackedAt)) { yield return BlindVolley(door); continue; }
                if (Time.deltaTime > 0f && door != null) FaceTowards(door.Center, 4f);
                yield return null;
            }
        }

        /// <summary>Only after a round of yours came through HIS shut door since he stacked at it (VirtualDoor.LastInboundAt).</summary>
        bool WantsBlindVolley(HIS.Room.VirtualDoor door, float stackedAt) =>
            _tier.BlindVolley > 0 && door != null && !door.IsOpen && _volleys < MaxVolleys && !HoldFire && !Reloading && _magLeft > 0
            && door.LastInboundAt > stackedAt && door.LastInboundAt > _volleyFor && BreachesHere;

        /// <summary>
        /// Answering a round through the shut door (research/27 §2; waves 2–5 since 0.1.58): to the kick spot in front of the leaf, a rack
        /// (the warning, muffled through the door), the gun up on the line over 0.6 s, then his tier's volley (the street crew 1
        /// round, the pros 2, the heavier pros 3, the operators 4) along the line from his muzzle to where your muzzle was (<see cref="FireBlind"/>). Then back beside
        /// the door.
        /// </summary>
        IEnumerator BlindVolley(HIS.Room.VirtualDoor door)
        {
            _volleyFor = door.LastInboundAt; _volleys++;
            var at = door.LastInboundFrom;
            Report($"answers through the door (your round came from {at:F2})");
            yield return MoveOutside(DoorFloor(door) + Flat(door.Outward).normalized * 1.15f, () => AimedWalk);
            if (OfficerHit || Current == State.Dead) yield break;
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            if (_gait != Gait.Gun && Upright(_gait)) SetGait(Gait.Gun);
            GunSound("pistol.rack", 0.9f);
            if (_hasAnim) { _anim.AimAt(at); _anim.SetGunPose(GunPose.Aimed, VolleyWait); }
            for (float t = 0f; t < VolleyWait; t += Time.deltaTime)
            {
                if (OfficerHit || Current == State.Dead) yield break;
                if (_hasAnim) _anim.AimAt(at);
                if (Time.deltaTime > 0f) FaceTowards(at, 8f);
                yield return null;
            }
            int rounds = Mathf.Min(_tier.BlindVolley, _magLeft), burst = Mathf.Max(1, _tier.BurstRounds.x);
            for (int i = 0; i < rounds; i++)
            {
                if (OfficerHit || Current == State.Dead || door.IsOpen || HoldFire) break;
                if (_hasAnim) _anim.AimAt(at);
                FireBlind(door, at);
                float gap = (i + 1) % burst == 0 ? Roll(_tier.BurstPause) : Roll(_tier.BurstGap);
                for (float t = 0f; t < gap && i + 1 < rounds; t += Time.deltaTime)
                {
                    if (OfficerHit || Current == State.Dead) break;
                    if (_hasAnim) _anim.AimAt(at);
                    yield return null;
                }
            }
            if (Current == State.Dead) yield break;
            HitmanLowReady("volley done");
            if (_magLeft <= 0) TryStartReload();
            if (OfficerHit) yield break;
            yield return MoveOutside(StackSpot(door), () => AimedWalk);   // out of the leaf's line again
        }

        /// <summary>
        /// One blind round through the shut leaf: along the line from his muzzle to <paramref name="at"/> (where your muzzle was
        /// when you fired through it) with his tier's spread at that range (no steady bonus: he can't see you). It never reads
        /// where you are now: the hole and splinters on the leaf (VirtualDoor.BulletHole, the puff on our side), then whatever
        /// that fixed line meets — real furniture, your body as it stands (PlayerShape), or nothing. A miss past the leaf's
        /// edge goes into the house wall.
        /// </summary>
        void FireBlind(HIS.Room.VirtualDoor door, Vector3 at)
        {
            var m = _hasAnim ? _anim.Muzzle : null;
            if (m == null || HoldFire) return;
            var origin = m.position;
            var line = at - origin;
            float range = line.magnitude;
            if (range < 0.3f) return;
            float p = Mathf.Clamp01(Mathf.Lerp(HitNear, HitFar, Mathf.InverseLerp(HitNearDist, HitFarDist, range)) * _tier.Accuracy);
            float sigma = PlayerShape.SpreadSigma(p, range) * (TestSpread ?? 1f);
            var dir = PlayerShape.Spread(line / range, sigma, UnityEngine.Random.value, UnityEngine.Random.value);
            _lastShotAt = Time.time;
            _magLeft--;
            if (_hasAnim) _anim.Recoil();
            if (_weapon != null && _weapon.TryGetComponent<IntruderGunFx>(out var fx)) fx.Fire();
            var zone = HitZone.None;
            bool leafHit = door.RaycastLeaf(origin, dir, 30f, out var leaf);
            if (leafHit)
            {
                door.BulletHole(in leaf, dir, false, true, fromOutside: true);
                var exit = leaf.ExitPoint + dir * 0.002f;
                var ps = PlayerState.Instance;
                zone = PlayerShape.Current(_player, PlayerHead, _floorY).Raycast(exit, dir, 30f, out float d);
                if (_roomOnlyMask < 0) _roomOnlyMask = LayerMask.GetMask("Room");
                bool wall = Physics.Raycast(exit, dir, out var impact, zone != HitZone.None ? d : 30f, _roomOnlyMask, QueryTriggerInteraction.Ignore);
                if (wall || ps == null || ps.IsDead) zone = HitZone.None;   // the furniture in front of you took it
                if (zone != HitZone.None) ps.TakeHit(zone, HitBy.Gun, origin);
                else if (wall) Sfx.Play("impact.wall", impact.point, 0.9f);
            }
            Debug.Log($"[HIS] Intruder #{Id}: FIRES BLIND through {(leafHit ? "the door" : "the house wall (missed the leaf)")} along the line to {at:F2} ({range:F1} m, P {p:F2}) → {(zone != HitZone.None ? "HIT " + PlayerShape.Name(zone) : "miss")}");
            Report(zone != HitZone.None ? "fired blind hit" : "fired blind miss");
            var pr = Police;
            if (pr != null) pr.OfficerFired(this, zone != HitZone.None);
        }

        // ───────────────────────────── the fight ─────────────────────────────
        /// <summary>
        /// In the room until it's over: the officer's standoff loop without the orders, the Taser or the cuffs. He fires on
        /// sight after his tell (<see cref="HitmanWantsToFire"/>), in his tier's bursts; reloads (from wave 2 on out of your line for
        /// it, and after a near miss: Question 6); takes a new angle (<see cref="LineSpot"/>: out beside the doorway, onto its
        /// centre line first) when he's had no line for the tier's BlindReangle — no line being no sight of you, or once his
        /// gun is up no round FireShot would take (<see cref="HitmanHasShot"/>; he walks there at low ready, so he raises
        /// again there through a fresh tell); legs out, he fights from the floor where he lies. Wave 1 hit: backs out.
        /// </summary>
        IEnumerator HitmanFight()
        {
            StopHere();
            // The finder (research/27 §2): Ricky first. He comes on to the fight below only once he's turned on you (within
            // 2 m, or hit), can't get at Ricky, or Ricky is down — and there his gun comes up on you through a fresh tell.
            if (_finding)
            {
                yield return FinderHunt();
                if (Current == State.Dead) yield break;
                StopHere();
            }
            Report("fighting");
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }   // paused
                if (OfficerHit) { yield return OfficerWounded(); yield break; }
                bool downed = HitmanDowned;
                if (downed && !_wasDowned) { _wasDowned = true; HitmanLowReady("legs out: fights from the floor"); }
                OfficerAim();
                // (BehindDoor is "not drawn in the room", not "outside": a man in view out beside the doorway — the way in
                // blocked, on the apron's NavMesh — still moves, and LineSpot takes him onto the doorway's line first.)
                bool canMove = !downed && _agent.enabled && _agent.isOnNavMesh && !BehindDoor;
                if (canMove && Reloading && _tier.CoverOnReload && !_reloadCovered) { _reloadCovered = true; yield return TakeCover("reloading"); continue; }
                if (canMove && NearMiss()) { yield return TakeCover("near miss"); continue; }
                if (HitmanWantsToFire()) HitmanShot();
                if (!downed && Time.time >= _nextSightCheck)
                {
                    _nextSightCheck = Time.time + BlindTick;
                    // Blind = no sight of you, or (gun up) no round FireShot would take: 0.1.53's wall rule, not a frozen man
                    // aiming at the wall (HitmanHasShot). The tier's BlindReangle as before.
                    _blindFor = HitmanHasShot() || Reloading ? 0f : _blindFor + BlindTick;
                    if (_blindFor >= _tier.BlindReangle && canMove)
                    {
                        _blindFor = 0f;
                        var spot = LineSpot();
                        if (FlatDistance(spot, transform.position) > 0.3f)
                        {
                            Report("moving for a line on the player");
                            // Gun down for the walk, as TakeCover: research/27 §2 "a man who moves to a new angle always raises
                            // again in your view". Seeing you with no shot, his tell was still done (the blind reset only follows
                            // lost sight), so the walk's WantsToFire would fire the frame his muzzle cleared, with no new raise.
                            // After lost sight the tell has already reset (every tier's BlindReangle outlasts BlindReset): a no-op.
                            HitmanLowReady("new angle");
                            yield return PathTo(spot, () => HitmanWalk,
                                () => { OfficerAim(); return WantsToFire() ? FireOnce() : null; }, until: () => HitmanDowned);
                            StopHere();
                            continue;
                        }
                    }
                }
                yield return null;
            }
        }

        /// <summary>
        /// A tier that takes cover (CoverOnReload: waves 2–5): a round of yours went past him (you fired with your gun on him, and no wound came of it). Seen once per
        /// shot, judged <see cref="NearMissCheck"/> later; at most one step out of line per <see cref="CoverCooldown"/>.
        /// </summary>
        bool NearMiss()
        {
            if (!_tier.CoverOnReload) return false;
            if (_gunshotAt > _nearMissSeen)
            {
                _nearMissSeen = _gunshotAt;
                // (In view only: out beside the doorway your round stopped on the wall in front of him — it never went past
                // him. The shared wall rule's "the player aiming at him", as the standoff's aim test: 0.1.53.)
                if (AimedAt && InViewOfPlayer()) { _nearMissCheckAt = Time.time + NearMissCheck; _nearMissWounds = _woundCount; }
            }
            if (_nearMissCheckAt < 0f || Time.time < _nearMissCheckAt) return false;
            _nearMissCheckAt = -1f;
            return _woundCount == _nearMissWounds && Time.time >= _nextCoverAt && !Reloading;
        }

        /// <summary>
        /// Question 6 (research/27 §7, default yes): a step to a NavMesh point within <see cref="CoverReach"/> with no line to
        /// you, gun low; hold there till the reload's done and 0.6–1.0 s more; then back to where he had his line (else a new
        /// angle round you) — and his gun comes up again through a fresh tell. Flanked in cover, the tell still runs there.
        /// </summary>
        IEnumerator TakeCover(string why)
        {
            _nextCoverAt = Time.time + CoverCooldown;
            if (!TryCoverSpot(out var cover)) { Report($"no cover within {CoverReach:F1} m ({why}): stays"); yield break; }
            var peek = transform.position;
            Report($"steps out of your line ({why})");
            HitmanLowReady(why);
            yield return PathTo(cover, () => HitmanWalk, () => { OfficerAim(); return null; }, until: () => HitmanDowned);
            if (Current == State.Dead || OfficerHit || HitmanDowned) yield break;
            StopHere();
            float holdUntil = Mathf.Max(Time.time + UnityEngine.Random.Range(0.6f, 1.0f), Reloading ? _reloadUntil : 0f);
            while (Time.time < holdUntil && Current != State.Dead && !OfficerHit && !HitmanDowned)
            {
                if (Time.deltaTime > 0f) { OfficerAim(); if (HitmanWantsToFire()) HitmanShot(); }
                yield return null;
            }
            if (Current == State.Dead || OfficerHit || HitmanDowned) yield break;
            // Back to where he had his line if it's still one by the wall rule (LineFromSpot: a peek spot out beside the
            // doorway needs you in view through it), else a new angle as the blind clock takes one (LineSpot).
            var back = LineFromSpot(peek) ? peek : LineSpot();
            Report("re-peeks");
            if (FlatDistance(back, transform.position) > 0.3f)
                yield return PathTo(back, () => HitmanWalk, () => { OfficerAim(); return WantsToFire() ? FireOnce() : null; }, until: () => HitmanDowned);
            StopHere();
        }

        /// <summary>
        /// The nearest NavMesh point (on rings of 0.6 / 1.0 / 1.4 m round him, 30° apart) within <see cref="CoverReach"/> that has
        /// no line to your chest, isn't through a doorway or on a mate's spot, is clear of the witness (<see cref="OffWitness"/>,
        /// as SpreadSpot and HoldSpot: your miss at him there would carry on into Ricky), and is a short walk (not round a wall).
        /// </summary>
        bool TryCoverSpot(out Vector3 spot)
        {
            spot = default;
            var nav = Nav;
            var filter = nav != null ? nav.Filter : new NavMeshQueryFilter { agentTypeID = _agent.agentTypeID, areaMask = NavMesh.AllAreas };
            var here = transform.position;
            var player = PlayerOnFloor();
            float best = float.MaxValue;
            s_route ??= new NavMeshPath();
            foreach (float r in s_coverRadii)
                for (int k = 0; k < 12; k++)
                {
                    var p = here + Quaternion.AngleAxis(k * 30f, Vector3.up) * Vector3.forward * r;
                    if (!NavMesh.SamplePosition(p, out var hit, 0.25f, filter)) continue;
                    var q = hit.position;
                    float d = FlatDistance(q, here);
                    if (d < 0.3f || d > CoverReach || d >= best) continue;
                    if (DoorwayNav.AnyBeyond(q) || LineFrom(q) || !OffWitness(q, player)) continue;
                    bool taken = false;
                    for (int i = 0; i < s_squad.Count && !taken; i++) { var o = s_squad[i]; taken = IsMate(o) && FlatDistance(o.transform.position, q) < 0.6f; }
                    if (taken) continue;
                    if (!NavMesh.CalculatePath(here, q, filter, s_route) || s_route.status != NavMeshPathStatus.PathComplete) continue;
                    var c = s_route.corners; float len = 0f;
                    for (int i = 0; i + 1 < c.Length; i++) len += Vector3.Distance(c[i], c[i + 1]);
                    if (len > CoverReach * 1.5f) continue;
                    best = d; spot = q;
                }
            return best < float.MaxValue;
        }

        // ───────────────────────────── the finder (going for Ricky) ─────────────────────────────
        // Research/27 §2 "The finder", §6 step 5. One man a wave is sent for the witness, not for you. Ricky gets a slower tell
        // than you do: the words ("Where's Ricky?" / "Find the witness."), the walk to him, then 1.0 s of raise — about 3–4 s
        // from the doorway, and your body between them shields him. Only Room colliders, your body and Ricky's own hitboxes
        // stop his rounds, all from inside the room (the shared wall rule's idea: a Room-mask line to Ricky's chest).
        const float FinderRingMin = 1.3f, FinderRingMax = 2.0f;   // his spot: on this ring round Ricky (m)
        const float FinderEngageRange = 2f;     // you within this (flat): he drops the role and fights you
        const float FinderRaiseSeconds = 1.0f;  // the raise on Ricky
        /// <summary>
        /// No round at Ricky sooner than this after "in the room" (research/27 §6 step 11's check, ≥ 2.5 s): the walk normally
        /// gives it, this keeps it when Ricky's spot is near the doorway.
        /// </summary>
        const float FinderMinWindow = 2.5f;
        const float FinderHitChance = 0.85f;    // the spread, sized as P 0.85 on a standing man at that range (PlayerShape)
        const int FinderPair = 2;               // rounds, then his tier's burst pause
        const float FinderPleaRange = 3f;       // Ricky: "No, no, please!"
        const string RickyBegLine = "vo.ricky.beg";   // (said in Ricky's voice by FinderPlea; his other keys: IntruderProtectee.cs)
        const float FinderNoLine = 1.5f;        // no line on Ricky this long at his spot: one new spot, then he drops the role
        const float FinderStepIn = 1.0f;        // just inside his doorway, where he looks for the witness
        const float FinderEyeHeight = 1.4f;     // the line to Ricky is tested from about where his raised muzzle will be
        const float FinderRoundDamage = 34f;    // the M9's (Firearm.damage); only the graded-HP tests read it (Hitbox.LethalRounds)
        const float FinderRange = 30f;
        static readonly float[] s_finderAngles = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 105f, -105f, 135f, -135f, 165f, -165f };
        static readonly float[] s_finderRadii = { 1.6f, 1.3f, 2.0f };
        /// <summary>Per wave of this run: its last-spawned man (the pick's fallback), and the man who took the role (one a wave).</summary>
        static readonly Dictionary<int, int> s_lastOfWave = new(), s_finderOfWave = new();

        bool _finding, _finderAsked, _hunting, _finderRaised, _finderPleaded;
        float _finderUpAt, _enteredRoomAt = -1f;
        int _finderWounds, _finderBurst;
        Collider[] _victimBoxes;
        Collider _victimTorso;

        /// <summary>A new standoff (ResetStandoffStatics): no wave has a finder or a last-spawned man yet.</summary>
        static void ResetFinderStatics() { s_lastOfWave.Clear(); s_finderOfWave.Clear(); }

        /// <summary>A fresh man (InitHitman): not asked, not finding.</summary>
        void ResetFinder()
        {
            _finding = _finderAsked = _hunting = _finderRaised = _finderPleaded = false;
            _enteredRoomAt = -1f; _finderBurst = 0; _victimBoxes = null; _victimTorso = null;
        }

        /// <summary>The witness, if he's in the room and alive (Ricky, research/27 §3; null in every other mode).</summary>
        static Intruder FinderVictim
        {
            get { var r = Protectee; return r != null && r.Current is not (State.Dead or State.Gone) ? r : null; }
        }

        /// <summary>He came in through the side door he was sent round to (not shot to a stop, not "no way round").</summary>
        bool WentRound => _roundTo > 0 && DoorIndex == _roundTo;

        /// <summary>
        /// Research/27 §2 "Who": one man a wave. The street men (waves 1–2, by the tier's Look): the second man (the Cover). The
        /// pros and operators (waves 3–5): the man who actually went round to the side door; with one door, the car shot to a
        /// stop or no way round (nobody went round), the wave's last-spawned man. Asked once, at his door going in
        /// (OfficerEntry): by then the side man has come round. A hitman with a live witness only — never the police, so never
        /// in a police standoff, even on a Marshal night.
        /// </summary>
        void TakeFinderRole()
        {
            if (_finderAsked) return;
            _finderAsked = true;
            if (!IsHitman || FinderVictim == null || s_finderOfWave.ContainsKey(Wave)) return;
            bool pick;
            if (_tier.Look <= HitmanTier.LookStreet) pick = Role == OfficerRole.Cover;
            else if (WentRound) pick = true;
            else
            {
                pick = s_lastOfWave.TryGetValue(Wave, out int last) && last == Id;
                // (A mate who did come round is the one. The side man spawns last in every tier split today, so this is only
                // the general case.)
                for (int i = 0; i < s_squad.Count && pick; i++)
                {
                    var o = s_squad[i];
                    if (o != null && o != this && o.IsHitman && o.Wave == Wave && o.WentRound) pick = false;
                }
            }
            if (!pick) return;
            s_finderOfWave[Wave] = Id;
            _finding = true; _hunting = false; _finderRaised = false; _finderPleaded = false; _finderBurst = 0;
            _finderWounds = _woundCount;   // (a plate round on the path doesn't count: "you hit him" is from here on)
            CacheVictim(FinderVictim);
            HitmanLowReady("finder: the witness, not you");
            Report($"finder: takes the role (wave {Wave}{(WentRound ? ", round at the side door" : "")})");
        }

        /// <summary>
        /// He still holds the role: the witness alive, you not within <see cref="FinderEngageRange"/>, not hit since he took it.
        /// Otherwise he drops it here (and fights you). Every frame he could aim or fire.
        /// </summary>
        bool FinderHolds()
        {
            if (!_finding) return false;
            if (FinderVictim == null) { DropFinder("the witness is down"); return false; }
            if (_woundCount > _finderWounds) { DropFinder("hit"); return false; }
            if (FlatDistance(PlayerOnFloor(), transform.position) < FinderEngageRange) { DropFinder("you're within 2 m"); return false; }
            return true;
        }

        /// <summary>
        /// The role's over: he's a plain hitman again — gun at low ready, his tell on you starts fresh (so his gun comes up on you
        /// in view, research/27 §2), his spot off Ricky freed for his mates. The wave keeps no other finder.
        /// </summary>
        void DropFinder(string why)
        {
            if (!_finding) return;
            _finding = _hunting = _finderRaised = false;
            _spot = new Vector3(float.NaN, 0f, 0f);
            HitmanLowReady($"finder: {why}");
            Report($"finder: drops the role ({why}): fights you");
        }

        /// <summary>Ricky's hitboxes (the colliders his Hitbox parts own) for the rounds, his torso for the aim. Once a role.</summary>
        void CacheVictim(Intruder v)
        {
            _victimBoxes = null; _victimTorso = null;
            if (v == null) return;
            var boxes = new List<Collider>();
            foreach (var c in v.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                var hb = c.GetComponentInParent<Hitbox>();
                if (hb == null || c.GetComponentInParent<Intruder>() != v) continue;
                boxes.Add(c);
                if (_victimTorso == null && hb.Part == BodyPart.Torso) _victimTorso = c;
            }
            _victimBoxes = boxes.ToArray();
        }

        /// <summary>Ricky's chest: his torso hitbox (crouched or cowering, it's where he is), else the hips + 0.3 m.</summary>
        Vector3 VictimChest(Intruder v) => _victimTorso != null && _victimTorso.enabled ? _victimTorso.bounds.center : v.BodyChest;

        /// <summary>From standing at <paramref name="floor"/>: nothing of the room (walls, furniture) between his gun and <paramref name="chest"/>.</summary>
        bool FinderLineFrom(Vector3 floor, Vector3 chest)
        {
            var from = new Vector3(floor.x, _floorY + FinderEyeHeight, floor.z);
            if (s_roomMask == 0) s_roomMask = LayerMask.GetMask("Room");
            return !Physics.Linecast(from, chest, s_roomMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>A line on Ricky from where he stands, in the room (never from outside it: the shared wall rule's idea).</summary>
        bool FinderLineNow(Intruder v) => !OutsideTheRoom && FinderLineFrom(transform.position, VictimChest(v));

        /// <summary>
        /// Just inside his doorway (<see cref="FinderStepIn"/>), on the room NavMesh, a step aside if a mate holds there (one who
        /// found no spread spot "holds just inside"): he stops there to look for Ricky. Nothing: the doorway link's room end.
        /// </summary>
        Vector3 FinderDoorway(HIS.Room.VirtualDoor door)
        {
            var nav = Nav;
            var filter = nav != null ? nav.Filter : new NavMeshQueryFilter { agentTypeID = _agent.agentTypeID, areaMask = NavMesh.AllAreas };
            var straight = DoorFloor(door) - OutwardFlat(door) * FinderStepIn;
            foreach (float l in s_finderDoorAside)
            {
                if (!NavMesh.SamplePosition(straight + LatchSide(door) * l, out var hit, 0.4f, filter) || DoorwayNav.AnyBeyond(hit.position)) continue;
                var q = hit.position;
                bool taken = false;
                for (int i = 0; i < s_squad.Count && !taken; i++)
                {
                    var o = s_squad[i];
                    taken = IsMate(o) && (FlatDistance(o.transform.position, q) < 0.6f || (!float.IsNaN(o._spot.x) && FlatDistance(o._spot, q) < 0.6f));
                }
                if (!taken) return q;
            }
            return nav != null && nav.Ready ? nav.InsideEnd : straight;
        }
        static readonly float[] s_finderDoorAside = { 0f, 0.6f, -0.6f };

        /// <summary>
        /// His spot, picked the way <see cref="SpreadSpot"/> picks but on a ring round Ricky (<see cref="FinderRingMin"/>–
        /// <see cref="FinderRingMax"/>), starting on his own side of him: on the room NavMesh, not through a doorway, not on a
        /// mate's spot, with a Room-mask line to Ricky's chest. Pass 0 also keeps out of your reach (he'd turn on you there) and
        /// off his mates' routes; pass 1 takes any spot he can walk to. None: false (he drops the role and fights you).
        /// </summary>
        bool FinderSpot(Intruder v, out Vector3 spot)
        {
            spot = default;
            var nav = Nav;
            var filter = nav != null ? nav.Filter : new NavMeshQueryFilter { agentTypeID = _agent.agentTypeID, areaMask = NavMesh.AllAreas };
            var ricky = v.transform.position; ricky.y = _floorY;
            var chest = VictimChest(v);
            var from = _agent.enabled && _agent.isOnNavMesh ? transform.position : nav != null && nav.Ready ? nav.InsideEnd : transform.position;
            var toMe = Flat(from - ricky).normalized;
            var player = PlayerOnFloor();
            s_route ??= new NavMeshPath();
            for (int pass = 0; pass <= 1; pass++)
                foreach (float da in s_finderAngles)
                    foreach (float r in s_finderRadii)
                    {
                        var p = ricky + Quaternion.AngleAxis(da, Vector3.up) * toMe * r;
                        if (!NavMesh.SamplePosition(p, out var hit, 0.25f, filter)) continue;
                        var q = hit.position;
                        float dr = FlatDistance(q, ricky);
                        if (dr < FinderRingMin - 0.05f || dr > FinderRingMax + 0.05f || DoorwayNav.AnyBeyond(q)) continue;
                        bool taken = false;
                        for (int i = 0; i < s_squad.Count && !taken; i++)
                        {
                            var o = s_squad[i];
                            taken = IsMate(o) && (FlatDistance(o.transform.position, q) < 0.7f || (!float.IsNaN(o._spot.x) && FlatDistance(o._spot, q) < 0.8f));
                        }
                        if (taken || !FinderLineFrom(q, chest)) continue;
                        if (pass == 0)
                        {
                            if (FlatDistance(q, player) < FinderEngageRange + 0.3f || !RouteClearOfMates(from, q, filter)) continue;
                        }
                        else if (!NavMesh.CalculatePath(from, q, filter, s_route) || s_route.status != NavMeshPathStatus.PathComplete) continue;
                        spot = q;
                        return true;
                    }
            return false;
        }

        /// <summary>
        /// The finder's job, from "in the room" (HitmanFight runs it first): his line ("Where's Ricky?" from the street men, "Find
        /// the witness." from the pros and operators), the walk to his spot, gun low on Ricky; there, 1.0 s of raise, then pairs at Ricky's chest
        /// (<see cref="FireAtProtectee"/>) in his tier's gaps and pauses, a reload with a fresh raise after it. Never sooner than
        /// <see cref="FinderMinWindow"/> after "in the room". Ends when he drops the role (you within 2 m, hit, no spot, his
        /// walk blocked, no line for 1.5 s twice, Ricky down) — or the night stands the squad down (Ricky killed: End).
        /// </summary>
        IEnumerator FinderHunt()
        {
            var v = FinderVictim;
            if (!_finding) yield break;
            if (v == null) { DropFinder("the witness is down"); yield break; }
            _hunting = true;
            StopHere();
            HitmanSay(_tier.Look <= HitmanTier.LookStreet ? "vo.hitman.street.where" : "vo.hitman.pro.where");
            if (!FinderSpot(v, out var spot)) { DropFinder("no spot with a line on the witness"); yield break; }
            _spot = spot;
            Report($"finder: goes for the witness (spot {spot:F2}, {FlatDistance(spot, v.transform.position):F2} m from him, {FlatDistance(spot, transform.position):F2} m to walk)");
            yield return FinderWalk(spot);
            int respots = 0;
            float noLine = 0f, nextRound = 0f;
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }   // paused
                if (!FinderHolds() || OfficerHit) yield break;
                v = FinderVictim;
                OfficerAim();                                                   // (FinderAim: on Ricky, low until the raise)
                if (HoldFire) { yield return null; continue; }
                if (!FinderLineNow(v))
                {
                    noLine += Time.deltaTime;
                    if (noLine < FinderNoLine) { yield return null; continue; }
                    noLine = 0f;
                    if (respots++ < 1 && FinderSpot(v, out spot) && FlatDistance(spot, transform.position) > 0.3f)
                    {
                        _spot = spot; _finderRaised = false; _finderBurst = 0;
                        if (_hasAnim && _anim.CurrentGunPose == GunPose.Aimed) _anim.SetGunPose(GunPose.Ready);
                        Report($"finder: no line on the witness: a new spot {spot:F2}");
                        yield return FinderWalk(spot);
                        continue;
                    }
                    DropFinder("no line on the witness");
                    yield break;
                }
                noLine = 0f;
                // The magazine: a reload lowers the gun (TryStartReload), so Ricky gets a fresh 1.0 s raise after it.
                if (Reloading) { _finderRaised = false; yield return null; continue; }
                if (_magLeft <= 0) { TryStartReload(); _finderRaised = false; yield return null; continue; }
                if (!_finderRaised) { FinderRaise(v); yield return null; continue; }
                if (Time.time < _finderUpAt || Time.time < _enteredRoomAt + FinderMinWindow || Time.time < nextRound || Reacting)
                { yield return null; continue; }
                FireAtProtectee(v);
                if (++_finderBurst >= FinderPair) { _finderBurst = 0; nextRound = Time.time + Roll(_tier.BurstPause); }
                else nextRound = Time.time + Roll(_tier.BurstGap);
                if (_magLeft <= 0) TryStartReload();
                yield return null;
            }
        }

        /// <summary>To <paramref name="spot"/> at his tier's gun walk, gun low on Ricky; he stops the moment he drops the role.
        /// Blocked (or off the NavMesh): he drops it (research/27 §2: "his walk is blocked: he drops the role and fights you").</summary>
        IEnumerator FinderWalk(Vector3 spot)
        {
            if (FlatDistance(spot, transform.position) > 0.15f)
            {
                if (!_agent.enabled || !_agent.isOnNavMesh) { DropFinder("off the room NavMesh"); yield break; }
                yield return PathTo(spot, () => HitmanWalk, () => { OfficerAim(); return null; }, until: () => HitmanDowned || !_finding);
                if (_finding && _walkBlocked && Current != State.Dead && !OfficerHit) DropFinder("his walk to the witness is blocked");
            }
            StopHere();
        }

        /// <summary>Ricky's tell, the visible half: the gun up onto his chest over <see cref="FinderRaiseSeconds"/>.</summary>
        void FinderRaise(Intruder v)
        {
            _finderRaised = true;
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            if (_gait != Gait.Gun && Upright(_gait)) SetGait(Gait.Gun);
            if (_hasAnim) { _anim.AimAt(VictimChest(v)); _anim.SetGunPose(GunPose.Aimed, FinderRaiseSeconds); }
            _finderUpAt = Time.time + FinderRaiseSeconds;
            Report($"finder: raises on the witness ({FinderRaiseSeconds:F2} s)");
        }

        /// <summary>
        /// OfficerAim for the finder (HitmanAim while he holds the role). Going in: gun at low ready, facing into the room (he
        /// doesn't know where Ricky is through the wall). Hunting: the gun on Ricky's chest, low until his raise, turned to him;
        /// Ricky's plea once he's within 3 m. False: no witness (the plain aim then).
        /// </summary>
        bool FinderAim()
        {
            var v = FinderVictim;
            if (v == null) return false;
            ReloadTick();
            bool gun = _hasAnim && _weapon != null && _weapon.InHand;
            bool walking = _agent.enabled && _agent.hasPath && _agent.velocity.sqrMagnitude > 0.04f;
            float dt = Time.deltaTime;
            if (_hunting)
            {
                if (gun)
                {
                    _anim.AimAt(VictimChest(v));
                    if (!_finderRaised && _anim.CurrentGunPose != GunPose.Ready) _anim.SetGunPose(GunPose.Ready);
                }
                if (dt > 0f && !walking) FaceTowards(v.transform.position, HitmanDowned ? 2f : 6f);
            }
            else
            {
                if (gun && _anim.CurrentGunPose != GunPose.Ready) _anim.SetGunPose(GunPose.Ready);
                var door = Door;
                if (dt > 0f && !walking && door != null) FaceTowards(transform.position - OutwardFlat(door), 4f);
            }
            FinderPlea(v);
            return true;
        }

        /// <summary>
        /// Research/27 §3: a finder within <see cref="FinderPleaRange"/> of Ricky, in the room — "No, no, please!" (he wakes for
        /// it). Once per finder. Ricky's own voice and caption (his Say); a missing take is silence.
        /// </summary>
        void FinderPlea(Intruder v)
        {
            if (_finderPleaded || FlatDistance(transform.position, v.transform.position) >= FinderPleaRange || OutsideTheRoom) return;
            _finderPleaded = true;
            v.WakeProtectee();
            v.Say(RickyBegLine);
            Report("finder: within 3 m of the witness (he begs)");
        }

        /// <summary>
        /// One round at Ricky (research/27 §6 step 5): from his muzzle along the line to Ricky's chest, turned by the spread at
        /// P <see cref="FinderHitChance"/> for that range (round the line, as <see cref="FireBlind"/>: the aim pass needn't reach
        /// a crouched man's chest). It lands on the nearest of the room (a wall, furniture: Impact.WorldHit), your body (you
        /// shield him: PlayerShape, your wound rule) and Ricky's hitboxes (Hitbox.ApplyDamage, Source = his gun: the night's
        /// EmitForce filter keeps it off the "you shot Ricky" card). Other bodies don't stop it.
        /// </summary>
        void FireAtProtectee(Intruder v)
        {
            var m = _hasAnim ? _anim.Muzzle : null;
            if (m == null || _weapon == null || HoldFire || v == null) return;
            var origin = m.position;
            var line = VictimChest(v) - origin;
            float range = line.magnitude;
            if (range < 0.2f) return;
            float sigma = PlayerShape.SpreadSigma(FinderHitChance, range) * (TestSpread ?? 1f);
            var dir = PlayerShape.Spread(line / range, sigma, UnityEngine.Random.value, UnityEngine.Random.value);
            _lastShotAt = Time.time;
            _magLeft--;
            if (_hasAnim) _anim.Recoil();
            if (_weapon.TryGetComponent<IntruderGunFx>(out var fx)) fx.Fire();
            if (_roomOnlyMask < 0) _roomOnlyMask = LayerMask.GetMask("Room");
            // Nearest first: the room, then your body short of that, then Ricky short of both.
            float best = FinderRange;
            bool wall = Physics.Raycast(origin, dir, out var wallHit, best, _roomOnlyMask, QueryTriggerInteraction.Ignore);
            if (wall) best = wallHit.distance;
            var ps = PlayerState.Instance;
            var zone = HitZone.None;
            if (ps != null && !ps.IsDead)
            {
                zone = PlayerShape.Current(_player, PlayerHead, _floorY).Raycast(origin, dir, best, out float pd);
                if (zone != HitZone.None) best = pd;
            }
            Collider box = null; RaycastHit boxHit = default;
            if (_victimBoxes != null)
            {
                var ray = new Ray(origin, dir);
                for (int i = 0; i < _victimBoxes.Length; i++)
                {
                    var c = _victimBoxes[i];
                    if (c != null && c.enabled && c.Raycast(ray, out var h, best)) { best = h.distance; box = c; boxHit = h; }
                }
            }
            var info = new DamageInfo { Amount = FinderRoundDamage, Type = DamageType.Bullet, Direction = dir, Penetration = 1f, Source = _weapon.gameObject };
            string what;
            if (box != null)
            {
                info.Point = boxHit.point; info.Normal = boxHit.normal;
                var hb = box.GetComponentInParent<Hitbox>();
                what = $"HIT the witness ({(hb != null ? hb.Part.ToString() : box.name)})";
                if (hb != null) hb.ApplyDamage(info);
            }
            else if (zone != HitZone.None) { what = $"HIT you, in the way ({PlayerShape.Name(zone)})"; ps.TakeHit(zone, HitBy.Gun, origin); }
            else if (wall) { info.Point = wallHit.point; info.Normal = wallHit.normal; what = $"miss ({wallHit.collider.name})"; Impact.WorldHit(info, wallHit); }
            else what = "miss";
            Debug.Log($"[HIS] Intruder #{Id}: FIRES at the witness ({range:F1} m, P {FinderHitChance:F2}, spread {sigma * Mathf.Rad2Deg:F1}°) → {what}");
            Report($"finder: fires at the witness → {what}");
            var pr = Police;
            if (pr != null && box == null && zone != HitZone.None) pr.OfficerFired(this, true);   // (your body took it)
        }

        /// <summary>
        /// A spot is clear of the witness (research/27 §2): not within 1.2 m of Ricky, nor within 0.6 m of the line from you to
        /// him — your round stops at the first body (Firearm), so a man standing there turns your miss into a hit on Ricky.
        /// SpreadSpot and HoldSpot reject the rest (the finder has his own spot). No witness (every mode but a Marshal night): true.
        /// </summary>
        static bool OffWitness(Vector3 q, Vector3 player)
        {
            var r = FinderVictim;
            if (r == null) return true;
            var at = r.transform.position;
            return FlatDistance(q, at) >= WitnessKeepOff && SegmentGap(player, at, q) >= WitnessLineKeepOff;
        }
        const float WitnessKeepOff = 1.2f, WitnessLineKeepOff = 0.6f;

        // ───────────────────────────── the plate ─────────────────────────────
        /// <summary>
        /// An operator's plate (wave 5) took a chest round (Hitbox: the first one; the next kills): a clank and a few sparks off it, no wound
        /// and no blood. The stagger is the squad's reel (ResolveHit: a round of 20+ reels him), and he stays in the fight.
        /// </summary>
        public void PlateStop(in DamageInfo info)
        {
            BodySound("metal.hit", info.Point, 0.9f);
            if (!BehindDoor) PlateSparks.Emit(info.Point, info.Normal, info.Direction);
            Report("the plate stopped a round");
        }
    }

    /// <summary>
    /// The sparks off a hitman's plate (research/27 §6 step 3): one shared world-space particle system in the room, fed with
    /// Emit (6 short streaks a hit, nothing made per shot). The police cars' sparks live on the porch's Outside layer, which
    /// the headset never draws, so this is its own small copy: additive, 0.1–0.2 s, no light, no GPU instancing (ConfettiBurst:
    /// the instanced variant ran without its instance buffer on the Quest).
    /// </summary>
    static class PlateSparks
    {
        const int PerHit = 6;
        static readonly Color Hot = new(1f, 0.95f, 0.8f), Orange = new(1f, 0.6f, 0.26f);
        static ParticleSystem s_ps;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_ps = null;

        /// <summary>Build it now (the intro, not mid-fight).</summary>
        public static void Warm() { if (s_ps == null) s_ps = Build(); }

        public static void Emit(Vector3 at, Vector3 normal, Vector3 dir)
        {
            Warm();
            if (s_ps == null) return;
            if (normal.sqrMagnitude < 1e-4f) normal = -dir;
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.up;
            normal.Normalize();
            var d = dir.sqrMagnitude > 1e-4f ? dir.normalized : -normal;
            var main = (Vector3.Reflect(d, normal) * 0.6f + normal * 0.4f).normalized;   // glancing off the plate
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < PerHit; i++)
            {
                ep.position = at + normal * 0.01f;
                ep.velocity = (main + Random.insideUnitSphere * 0.75f).normalized * (4f * Random.Range(0.5f, 1.3f));
                ep.startSize = Random.Range(0.012f, 0.02f);
                ep.startLifetime = Random.Range(0.1f, 0.2f);
                ep.startColor = Color.Lerp(Hot, Orange, Random.value);
                s_ps.Emit(ep, 1);
            }
        }

        static ParticleSystem Build()
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            var go = new GameObject("PlateSparks");
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false; main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.7f; main.maxParticles = 36;
            main.startLifetime = 0.2f; main.startSpeed = 0f; main.startSize = 0.015f;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var fade = new Gradient();   // additive: fading to black is fading out
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.45f, 0.45f, 0.45f), 0.5f), new GradientColorKey(Color.black, 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            var col = ps.colorOverLifetime; col.enabled = true; col.color = fade;
            // Additive, as the muzzle flash's material (GunEffects.MakeFlashQuad): _Surface alone doesn't set the blend state.
            var mat = new Material(shader) { name = "PlateSparks", color = Color.white, renderQueue = 3100 };
            mat.SetFloat("_Surface", 1); mat.SetFloat("_Blend", 1); mat.SetFloat("_ZWrite", 0); mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One); mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = 1.5f; r.velocityScale = 0.03f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.enableGPUInstancing = false;
            return ps;
        }
    }
}
