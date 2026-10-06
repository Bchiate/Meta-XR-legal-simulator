using System.Collections;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// The door opened on him (research/25; Brett 2026-10-01: "adding a way to open the door yourself before someone breaks
    /// in"). The player unlatches the door by hand (VirtualDoor.OpenedByPlayer) while the man is at it, hidden behind it:
    /// he sees you and does what he already does elsewhere —
    ///   home invader / knife man / armed robber: shoves the door wide out of your hand and comes in (his brain's own line)
    ///   Mike / the robber who picked the lock: eases it wide and walks in · burglar: the startle, then mostly runs off
    ///   surrenderer: "Sorry! Sorry, wrong house!" and goes · the gun runner: "Shit! I'm gone" · the delivery man: not
    ///   restarted, only shown — he delivers face to face · police: nothing (they walk through an open door anyway).
    /// A man still out on the path keeps coming: this fires once he's within 1.6 m of the threshold. One who is already
    /// leaving (shot at the door, talked off) leaves as he was. The judge: a man let in through a door you opened didn't
    /// force his way in (<see cref="_letIn"/>, BuildFacts' ForcedEntry), unless he was already forcing it (Brett: "the whole
    /// kick-in counts").
    /// </summary>
    public partial class Intruder
    {
        const float DoorOpenedReach = 1.6f;  // from the threshold: at the leaf (0.5 m), the step (0.6 m), where he waits (1.25 m)
        const float DoorwayRadius = 0.7f;    // InDoorwayOf: the leaf can't swing shut on anyone this close to the threshold...
        const float DoorPlaneMargin = 0.2f;  // ...in the room or straddling its plane (the leaf only swings into the room)

        bool _doorOpenedOnHim;  // the watcher fired (once)
        bool _letIn;            // the door was opened on him before he forced it: not a forced entry, for good
        bool _givingUp;         // shot at the door / talked off: already leaving, the door opening doesn't restart him
        bool _madeWay;          // police: eased a leaf left ajar out of the way once

        /// <summary>
        /// Where he is, for his door: his feet, or once he's dead his hips. A death clip carries the body up to a metre from
        /// where he stood and leaves the root there (the agent is off): shot dead mid-kick, he falls in through the burst
        /// doorway with his feet still out on the step — his pool, the shut-again hide and the leaf go by the body (review
        /// 2026-10-01: the root put that pool on the Outside layer, unseen on the room floor).
        /// </summary>
        Vector3 BodyPoint => Current == State.Dead && _hasAnim && _anim.Hips != null ? _anim.Hips.position : transform.position;

        /// <summary>Out past his door's plane (the step, the porch): no room floor under him (<see cref="BodyPoint"/>).</summary>
        bool OutsideDoorPlane
        {
            get { var d = Door; return d != null && Vector3.Dot(BodyPoint - d.Threshold, d.Inward) < -DoorPlaneMargin; }
        }

        /// <summary>
        /// Killed out past the door plane (not police: their own code places them): no room floor for a dropped knife (it
        /// would fall through the void box) and his pool goes on the porch (Outside layer), which the open doorway shows.
        /// A body lying in over the threshold (he fell in) bleeds on the room floor (<see cref="BodyPoint"/>).
        /// </summary>
        bool OnTheStepInView => _intent != Intent.Police && OutsideDoorPlane;

        /// <summary>
        /// Every frame (Update): the door opened on him; police reaching an ajar leaf; and the door shut again on a man (or a
        /// body) it showed out there — back behind it on the Outside layer, so a round through the shut leaf is blind again.
        /// </summary>
        void WatchDoor()
        {
            if (Current == State.Gone) return;
            var door = Door;
            if (door == null) return;
            if (_intent == Intent.Police)
            {
                // Officers already walk through an open door instead of kicking it (IntruderPolice): a leaf the player left
                // ajar swings wide as each one reaches it, once, so it can't block the doorway link.
                if (!_madeWay && Current == State.Entering && door.IsOpen && FlatDistance(transform.position, door.Threshold) < 1.5f)
                { _madeWay = true; door.MakeWay(); }
                return;
            }
            if (_visible && !door.IsOpen && OutsideDoorPlane) { SetVisible(false); return; }
            if (_doorOpenedOnHim || _givingUp || _doorAnswer == DoorAnswer.Leave || _visible) return;
            if (Current is not (State.Outside or State.BreakIn)) return;
            if (!door.OpenedByPlayer || !door.IsOpen || FlatDistance(transform.position, door.Threshold) > DoorOpenedReach) return;
            _doorOpenedOnHim = true;
            if (Style == EntryStyle.FalseAlarm)
            {
                // Not restarted, only shown: FalseAlarmVisit carries on face to face ("Delivery! I'll just leave it here.", the
                // parcel on the step, then he walks off) — he never forced anything, and a restart would end his walk-up loop.
                _letIn = true;
                Debug.Log($"[HIS] Intruder #{Id}: the door opened on him (the delivery man, face to face)");
                SetVisible(true);
                return;
            }
            StopAllCoroutines(); // like Kill: whatever he was doing at the door (a wait, a knock, a kick wind-up) is over
            StartCoroutine(DoorOpenedOnHim());
        }

        IEnumerator DoorOpenedOnHim()
        {
            _letIn = !_forcingDoor && _kicksLanded == 0;
            _forcingDoor = _kicking = _enraged = false;
            OnPorchPath = false;
            CancelAct();
            if (_hasAnim) { _anim.ResetKick(); _anim.SetSpeedOverride(0f); }
            Debug.Log($"[HIS] Intruder #{Id}: the door opened on him ({(_letIn ? "let in" : "still forced")})");
            SetState(State.BreakIn);   // your yells queue for his brain from here
            SetVisible(true);          // in plain sight: the judge reads BehindDoor = false; the gunman's perception gate starts
            for (float t = 0f; t < 0.4f && Current != State.Dead; t += Time.deltaTime) { FaceTowards(PlayerOnFloor(), 8f); yield return null; }
            if (Current == State.Dead) yield break;

            // A Marshal night's keyed visitor with a knock line (Ortiz checking in; Brett 0.1.56: "could be anybody walking up" —
            // the door opened on him, he walked in without a word): who he is, to your face, before he comes in.
            if (KeyedVisitor && !_introduced && VoiceLines.Visitor(VisitorVoice, "knock") is string knock)
            {
                Say(knock);
                _introduced = true;
                Debug.Log($"[HIS] Intruder #{Id}: says who he is to your face ({_def?.Name})");
                yield return new WaitForSeconds(Mathf.Clamp(Sfx.Has(knock) ? Sfx.Length(knock) + 0.2f : 1f, 0.6f, 2.6f));
                if (Current == State.Dead) yield break;
            }

            switch (_intent)
            {
                case Intent.Surrenderer:
                    if (Sfx.Has("vo.surrender.door")) Say("vo.surrender.door");
                    yield return FleeAndLeave(run: false);
                    yield break;
                case Intent.Gunman when _def?.RunsWhenChallenged == true:
                    Say("vo.gunman.flee");
                    yield return FleeAndLeave(run: true);
                    yield break;
                case Intent.Burglar:
                    // Expected an empty house: the startle, then mostly gone (as when he hears you through the door).
                    yield return Caught();
                    if (Current == State.Dead) yield break;
                    if (DoorRoll < 0.6f) { Say("vo.burglar.flee"); yield return FleeAndLeave(run: true); yield break; }
                    break;                 // walks in; challenged, his brain freezes and chooses (IntruderBurglarChoice)
            }

            // In. A fighter shoves the door wide out of your hand; Mike and the robber who picked the lock ease it open.
            bool shove = Fighter;
            yield return ToTheApron();
            if (Current == State.Dead) yield break;
            var door = Door;
            if (door != null && door.MakeWay(shove ? 0.3f : 0.8f)) yield return new WaitForSeconds(shove ? 0.35f : 0.8f);
            yield return WalkIn();
            if (Current is State.Gone or State.Dead) yield break;
            yield return Brain();
        }

        /// <summary>
        /// Onto the doorway apron before the walk in, if he's off it (coming round from the side of the porch): WalkIn starts
        /// on the apron, and he's in plain view now — no pop sideways.
        /// </summary>
        IEnumerator ToTheApron()
        {
            var nav = Nav;
            if (nav == null || !nav.Ready || !nav.OnApron(transform.position, out var a) || FlatDistance(transform.position, a) <= 0.15f) yield break;
            for (float giveUp = Time.time + 3f; Current != State.Dead && FlatDistance(transform.position, a) > 0.03f && Time.time < giveUp; )
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; } // paused
                float s = MovementLocked ? 0f : GaitSpeed;
                if (_hasAnim) _anim.SetSpeedOverride(s);
                FaceTowards(a, 8f);
                var next = Vector3.MoveTowards(transform.position, a, s * Time.deltaTime);
                next.y = a.y;
                transform.position = next;
                yield return null;
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
        }

        /// <summary>Anyone in <paramref name="door"/>'s doorway, alive or a body: its leaf can't swing shut on him (VirtualDoor).</summary>
        public static bool AnyInDoorway(HIS.Room.VirtualDoor door)
        {
            if (door == null) return false;
            foreach (var i in s_all) if (i != null && i.InDoorwayOf(door)) return true;
            return false;
        }

        /// <summary>On the doorway link, walking in within 2 m, reacting to the door opened on him, or within 0.7 m of the threshold on the room side of (or across) its plane.</summary>
        bool InDoorwayOf(HIS.Room.VirtualDoor door)
        {
            if (Current == State.Gone || Door != door) return false;
            if (OnDoorwayLink) return true;
            var p = BodyPoint; // (a body: where it lies)
            float flat = FlatDistance(p, door.Threshold);
            if (Current == State.Entering && flat < 2f) return true;
            if (_doorOpenedOnHim && _visible && Current == State.BreakIn) return true; // facing you in the doorway (the delivery man too)
            // A man (or a body) outside the plane can't be hit by a leaf that only swings into the room: kill him through the
            // door, open it to look, and it still shuts.
            return flat < DoorwayRadius && Vector3.Dot(p - door.Threshold, door.Inward) > -DoorPlaneMargin;
        }
    }
}
