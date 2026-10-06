using System.Collections;
using HIS.Security;
using UnityEngine;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// Out of the car, the rider's half (Brett 2026-10-05: "have the flow of cops exiting look better" — they teleported from the
    /// seat to standing outside). The car's half is PoliceCars.Exit.cs. Police and a Marshal night's hitmen alike:
    ///  • His turn comes (PoliceCars.GetOut: the driver first, the passenger a beat later, the back seat once the door is free).
    ///  • From his seat he plays Mixamo's Exiting Car (ExitCar out to his left, ExitCarMirror to his right) at ~1.5× (in a hurry;
    ///    later hitman waves a bit faster), carried out of the door by the clip's own travel and turn — his feet planted as
    ///    authored — plus, only while he's still sitting (his legs in the footwell, out of sight): a back-seat man's slide up to
    ///    the door's opening by the side of the front seat's back (the front seats sit in their opening: no slide), the drop
    ///    that matches the take's high seat to ours, then up (a low car) or down (the cab) to the road as his first foot goes out.
    ///  • His hand on the door: it swings (PoliceCars.SwingDoor), its clunk on that swing.
    ///  • On his feet outside: no longer a rider (a round now gets a standing reaction, or a standing death), his gun in his hand
    ///    as he straightens up outside the body. Clear of the door's line, the take's slow tail (he'd turn back and shut the
    ///    door) is skipped: the animator blends him into his gun gait, he steps to his place by the car if he isn't there,
    ///    turns to the path, and the walk-up goes on as before. The doors stay open.
    ///  • The box team (SWAT truck, van): out of the back — up on their feet in its dark doorway as the first man pushes the
    ///    back doors open (behind the opening leaves), then each at his turn out to its lip at a walk, a step down onto the road,
    ///    on to his exit behind it.
    ///  • Shot dead getting out: still in the car's body (in his seat, his legs going out) he slumps where he is (the seat death;
    ///    the clip holds its pose); once his legs are out and he's past its side (coming up), or up, a death on the street from
    ///    where he is, clear of the car and its open door (Intruder.Kill / FreeDistance) — no horn, his door free for the back
    ///    seat. The others go on.
    /// No NavMesh out there, so the clip's root motion comes here (IntruderAnimator.RootMotionSink: one cached callback per man,
    /// no allocation per frame). An old controller without the exit states, or no rig: out at once, as before (PopOutOfCar).
    /// </summary>
    public partial class Intruder
    {
        /// <summary>The cross-fade from the seated pose into the exit clip (s).</summary>
        const float ExitBlendIn = 0.3f;
        /// <summary>The hand-off from the exit clip to his gun gait (s): IntruderBuilder's exit-time blend.</summary>
        const float ExitHandOff = 0.3f;
        /// <summary>His slide up to the door's opening starts this far into the clip (just after the cross-fade) and ends as his first foot goes out.</summary>
        const float ExitGlideFrom = 0.2f;
        /// <summary>His door starts to swing this long (clip s) before his hand reaches it (IntruderClips.ExitDoorTouch).</summary>
        const float DoorLead = 0.12f;
        /// <summary>Out and standing within this of his place by the car, he stays where he is; farther, a few steps there.</summary>
        const float ExitStandSlack = 0.35f;
        /// <summary>
        /// The box team out of the back, at 1× (s, m): the back doors' swing; the beat before he moves; the step down off the
        /// doorway's lip and its lift (it clears the rear step's edge, 15 cm out and 5 cm under the lip, by ~10 cm).
        /// </summary>
        const float BackDoorSwing = 0.8f, BackWait = 0.45f, HopTime = 0.38f, HopLift = 0.12f;

        PoliceCars.ExitPlan _exitPlan;
        System.Action<Vector3, Quaternion> _exitSink;
        /// <summary>The exit clip's root motion is his (the sink is live); the frame his exit's brain last ran (it lets go when that stops).</summary>
        bool _exiting;
        int _exitBeat;
        float _exitStart, _exitDrop, _exitClipNow, _exitTurned, _landY, _exitGround;
        Vector3 _exitMoved;

        /// <summary>
        /// How fast he gets out: men in a hurry — the police 1.5× the take (clear of the door ~1.9 s after his turn), SWAT 1.6×; a
        /// hitman by his wave, 1.45× the first to 1.7× by the fifth.
        /// </summary>
        float ExitRate => IsHitman ? Mathf.Clamp(1.45f + 0.06f * (Wave - 1), 1.45f, 1.7f) : Wave >= 3 ? 1.6f : 1.5f;

        /// <summary>His rig has the exit clips (IntruderBuilder 2026-10-05): else he pops out as before.</summary>
        bool CanAnimateExit => _hasAnim && _anim.HasExitClips;

        /// <summary>Still in the car's space (its child): the call cleared under him unparents him (PoliceCars.ResetCombat).</summary>
        bool InCar(in PoliceCars.ExitPlan p) => p.Car != null && transform.parent == p.Car;

        /// <summary>
        /// His turn has come (RideIn, PoliceCars.GetOut): out of his seat to <paramref name="exit"/> (world; zero: the show is over,
        /// out where he is), the street's floor at <paramref name="ground"/>. Leaves him standing there facing the path, his
        /// <c>_officerStart</c> set, not riding, the gun gait on and his weapon in his hand — or dead.
        /// </summary>
        IEnumerator GetOutOfCar(Vector3 exit, float ground)
        {
            if (exit == Vector3.zero || !CanAnimateExit || !PoliceCars.TryGetExit(Wave, _rideSeat, out var plan, Faction) || plan.Car == null)
            { yield return PopOutOfCar(exit, ground); yield break; }
            if (plan.Way == PoliceCars.ExitWay.Back) { yield return HopOutTheBack(plan, exit, ground); yield break; }

            float rate = ExitRate;
            _exitPlan = plan;
            _exitGround = ground;
            _exitMoved = Vector3.zero; _exitTurned = 0f; _exitClipNow = 0f;
            _exitDrop = Mathf.Max(0f, IntruderClips.ExitSeatHips - (_gait == Gait.Driving ? IntruderClips.DriveHips : IntruderClips.SitHips));
            _exitStart = Time.time;
            _anim.SetSpeedOverride(0f);
            _anim.ExitCar(plan.Right, rate, ExitBlendIn);
            _exitSink ??= OnExitRootMotion;
            _anim.RootMotionSink = _exitSink;
            _exiting = true; _exitBeat = Time.frameCount;
            Report($"getting out ({(plan.Right ? "right" : "left")} side{(plan.Way == PoliceCars.ExitWay.Side ? ", no door" : "")}, {rate:F2}×)");
            bool onClip = false, swung = false, up = false, armed = false, cleared = false;
            float giveUp = Time.time + ExitBlendIn + (IntruderClips.ExitClear + 1f) / rate + ExitHandOff + 1f;
            while (Current != State.Dead && Current != State.Gone && InCar(plan))
            {
                _exitBeat = Time.frameCount;
                float ct = _anim.ExitClipTime;
                if (ct >= 0f) { onClip = true; _exitClipNow = ct; }
                else if (onClip) break;   // off the clip: the hand-off to the gun gait is over — or a hit reaction / his legs shot out took him off it
                if (!swung && _exitClipNow >= IntruderClips.ExitDoorTouch - DoorLead)
                {
                    // His hand on the door: it swings open with his push, its clunk on its own swing (the hitmen's). It starts a beat
                    // before his hand gets there (the latch: he pulls the handle), so the hand never pokes through the shut skin.
                    swung = true;
                    PoliceCars.SwingDoor(Wave, _rideSeat, (IntruderClips.ExitDoorOpen - IntruderClips.ExitDoorTouch + DoorLead) / rate, Faction);
                }
                if (!up && _exitClipNow >= IntruderClips.ExitStand)
                {
                    // On his feet outside: no longer a rider (a round now gets a standing reaction or a standing death); the gun
                    // gait waits for the clip's hand-off at ExitClear.
                    up = true; Riding = false; _floorY = ground; SetGait(Gait.Gun);
                }
                if (!armed && _exitClipNow >= IntruderClips.ExitGunAt) { armed = true; if (_weapon != null && !_weapon.InHand) _weapon.Show(true); }
                if (!cleared && _exitClipNow >= IntruderClips.ExitClear) { cleared = true; PoliceCars.ClearedDoor(Wave, _rideSeat, Faction); }
                if (cleared && _exitClipNow >= IntruderClips.ExitClear + 0.2f * rate) _anim.EndExit(ExitHandOff);   // (the exit-time transition missed: hand him over now)
                if (!up) _floorY = transform.position.y;   // shot before he's up, he slumps where he is (Collapse's seat death)
                if (Time.time > giveUp) { Debug.LogWarning($"[HIS] Intruder #{Id}: getting out timed out at {_exitClipNow:F2} s of the clip"); break; }
                yield return null;
            }
            _exiting = false;
            if (_anim.RootMotionSink == _exitSink) _anim.RootMotionSink = null;
            if (Current == State.Dead || Current == State.Gone) yield break;
            if (!up) { Riding = false; SetGait(Gait.Gun); }
            _anim.EndExit(ExitHandOff);
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            if (!cleared) PoliceCars.ClearedDoor(Wave, _rideSeat, Faction);
            yield return OntoTheStreet(exit, ground);
        }

        /// <summary>
        /// The exit clip's root motion (IntruderAnimator.OnAnimatorMove): his place in the car's space this frame — the clip's own
        /// travel and turn (it can be shoved meanwhile: he goes with it), the slide up to the door and the height path.
        /// </summary>
        void OnExitRootMotion(Vector3 moved, Quaternion turned)
        {
            var p = _exitPlan;
            if (!_exiting || !InCar(p) || Current == State.Dead || Current == State.Gone || Time.frameCount - _exitBeat > 2)
            {
                // Done, dead, withdrawn, off the car (the call cleared under him), or the brain running it stopped: let go of the
                // root (shot dead, the pose holds where it is).
                _exiting = false;
                if (_hasAnim) { _anim.RootMotionSink = null; if (Current == State.Dead) _anim.FreezeExit(); }
                return;
            }
            var d = p.Car.InverseTransformDirection(moved); d.y = 0f;
            _exitMoved += d;
            _exitTurned += Mathf.DeltaAngle(0f, turned.eulerAngles.y);
            float ct = _anim.ExitClipTime;
            if (ct >= 0f) _exitClipNow = ct;
            // The cross-fade's weight: the root's start offsets come in with the clip's pose, so his hips stay where they sat.
            float w = Mathf.Clamp01((Time.time - _exitStart) / ExitBlendIn);
            var ahead = IntruderClips.ExitRootAhead;                                  // (measured on the unmirrored take)
            if (p.Right == (IntruderClips.ExitTravel.x <= 0f)) ahead.x = -ahead.x;   // the mirrored one is playing
            // The slide up to the door's opening, seated (legs in the footwell); a back-seat man by the side of the front seat's back.
            float g = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ExitGlideFrom, IntruderClips.ExitFootFrom, _exitClipNow));
            Vector3 via = p.Via - p.Seat, sill = p.Sill - p.Seat;
            var glide = !p.HasVia ? sill * g : g < 0.5f ? via * (g * 2f) : Vector3.Lerp(via, sill, g * 2f - 1f);
            // Down onto the take's higher seat, held, then to the road as his first foot goes out (up from a low car, down from a cab).
            float rise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(IntruderClips.ExitFootFrom, IntruderClips.ExitFootOut, _exitClipNow));
            float y = Mathf.Lerp(p.Seat.y - _exitDrop * w, 0f, rise);
            transform.localPosition = new Vector3(p.Seat.x + glide.x + ahead.x * w + _exitMoved.x, y, p.Seat.z + glide.z + ahead.y * w + _exitMoved.z);
            transform.localRotation = Quaternion.Euler(0f, _exitTurned, 0f);
        }

        /// <summary>
        /// The box team (SWAT truck, van): out of the back. The first one pushes the back doors open, and he and the rest stand up
        /// ready in the doorway's dark recess behind the opening leaves (StandUpInTheBox), facing out, guns in hand; each at his
        /// turn out through the doorway at the aimed walk (his legs walk it — 2026-10-05, the cars fixer: a frozen crouch slid out of a flat black face and hopped down
        /// through the rear step); a step down off its lip, over the step, onto the road; on to his exit behind it.
        /// </summary>
        IEnumerator HopOutTheBack(PoliceCars.ExitPlan p, Vector3 exit, float ground)
        {
            float rate = ExitRate;
            Report($"getting out (the back, {rate:F2}×)");
            PoliceCars.SwingDoor(Wave, _rideSeat, BackDoorSwing / rate, Faction);   // (open already: nothing; the rest stand up)
            if (!_boxStanding) StandInTheBox(p);
            for (float t = 0f; t < BackWait / rate; t += Time.deltaTime) { if (Current == State.Dead || !InCar(p)) yield break; yield return null; }
            // Out to the lip at the walk, his speed coming up as the animator's does (its 10/s smoothing): no foot slide.
            float speed = IntruderClips.GunWalkSpeed, dist = Vector3.Distance(p.Via, p.Sill), gone = 0f;
            _anim.SetSpeedOverride(speed);
            for (float t = 0f; gone < dist; )
            {
                if (Current == State.Dead || !InCar(p)) yield break;   // (the call cleared under him: ResetCombat set him down)
                t += Time.deltaTime;
                gone = Mathf.Min(dist, speed * (t - (1f - Mathf.Exp(-10f * t)) / 10f));
                transform.localPosition = Vector3.Lerp(p.Via, p.Sill, gone / dist);
                _floorY = transform.position.y;
                yield return null;
            }
            transform.localPosition = p.Sill;
            // The step down: in plain view from here, no longer a rider. Shot in the air he lands first (LandOnDeath), then dies there.
            Riding = false; _boxStanding = false;
            _landY = ground;
            Died -= LandOnDeath; Died += LandOnDeath;
            float hop = HopTime / rate;
            for (float t = 0f; t < hop && Current != State.Dead && InCar(p); t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / hop);
                var q = Vector3.Lerp(p.Sill, p.Land, s);
                q.y = Mathf.Lerp(p.Sill.y, p.Land.y, s * s) + HopLift * Mathf.Sin(Mathf.PI * s);
                transform.localPosition = q;
                yield return null;
            }
            Died -= LandOnDeath;
            if (Current == State.Dead || !InCar(p)) yield break;
            transform.localPosition = p.Land;
            _floorY = ground;
            yield return OntoTheStreet(exit, ground);
        }

        bool _boxStanding;

        /// <summary>
        /// The box team (SWAT truck, van), a man still in his seat as the back doors start to swing open (PoliceCars.SwingDoor, the
        /// first man's push): up on his feet now, ready in the doorway behind the opening leaves, his gun in his hand — his own turn
        /// walks him out from there (<see cref="HopOutTheBack"/>). Nothing for a man who isn't riding in a box seat (or is dead).
        /// </summary>
        public void StandUpInTheBox()
        {
            if (!Riding || _boxStanding || Current == State.Dead || Current == State.Gone || !CanAnimateExit) return;
            if (!PoliceCars.TryGetExit(Wave, _rideSeat, out var p, Faction) || p.Way != PoliceCars.ExitWay.Back || !InCar(p)) return;
            StandInTheBox(p);
        }

        /// <summary>On his feet at his place in the back doorway (<see cref="PoliceCars.ExitPlan.Via"/>), facing out, gun in hand.</summary>
        void StandInTheBox(in PoliceCars.ExitPlan p)
        {
            SetGait(Gait.Gun);
            _anim.SnapGait(Gait.Gun);
            _anim.SetSpeedOverride(0f);
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            transform.localPosition = p.Via;
            _floorY = transform.position.y;
            _boxStanding = true;
        }

        /// <summary>Shot in mid-hop out of the back: down onto the road first, so he dies there (not in the air).</summary>
        void LandOnDeath(Intruder _)
        {
            Died -= LandOnDeath;
            var at = transform.position; at.y = _landY;
            transform.position = at;
            _floorY = _landY;
        }

        /// <summary>
        /// Out and on his feet by the car: off it (his own transform on the street), a few steps to his place if he isn't there,
        /// facing the path (the walk-up turns him to its first leg at once), the walk-up's start.
        /// </summary>
        IEnumerator OntoTheStreet(Vector3 exit, float ground)
        {
            transform.SetParent(null, true);
            Riding = false;
            var at = transform.position; at.y = ground;
            transform.SetPositionAndRotation(at, Quaternion.LookRotation(Flat(transform.forward), Vector3.up));
            _floorY = ground; _prevPos = at;
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            var stand = exit; stand.y = ground;
            // (Also when he's still inside a vehicle's padded footprint: the walk-up then sets off from outside it, as from the old
            // exits — no extra step-out leg round his own car.)
            if (exit != Vector3.zero && (FlatDistance(at, stand) > ExitStandSlack || PoliceCars.InFootprint(at))) yield return MoveOutside(stand, () => AimedWalk);
            if (Current == State.Dead) yield break;
            var toPath = PorchFor(Door)?.Step ?? (Door != null ? Door.Center : transform.position + transform.forward);
            for (float t = 0f; t < 0.45f && Current != State.Dead && !OfficerHit; t += Time.deltaTime) { FaceTowards(toPath, 7f); yield return null; }
            _officerStart = transform.position;
        }

        /// <summary>
        /// No exit clip (an old controller, no rig) or the show over: as before — his door swings open, and he's out at his exit
        /// (or where he is), on his feet, gun in hand, a beat later.
        /// </summary>
        IEnumerator PopOutOfCar(Vector3 exit, float ground)
        {
            PoliceCars.SwingDoor(Wave, _rideSeat, 0.7f, Faction);
            for (float t = 0f; t < 0.5f && exit != Vector3.zero && Current != State.Dead; t += Time.deltaTime) yield return null;
            if (Current == State.Dead) yield break;
            transform.SetParent(null, true);
            Riding = false;
            _floorY = ground;
            var at = exit == Vector3.zero ? transform.position : exit;
            at.y = _floorY;
            var toPath = PorchFor(Door)?.Step ?? (Door != null ? Door.Center : at + transform.forward);
            transform.SetPositionAndRotation(at, Quaternion.LookRotation(Flat(toPath - at), Vector3.up));
            _prevPos = at;
            _officerStart = at;
            SetGait(Gait.Gun);                       // up out of the seat
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            PoliceCars.ClearedDoor(Wave, _rideSeat, Faction);
            for (float t = 0f; t < 1.1f && Current != State.Dead; t += Time.deltaTime) yield return null;
        }
    }
}
