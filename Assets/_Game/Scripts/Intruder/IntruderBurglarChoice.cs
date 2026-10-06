using System.Collections;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using UnityEngine.AI;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// Burglars stop instantly running (Brett's pick #3: "the second the door opens half the time they just run out"). On a
    /// varied-entries round he NOTICES you first — your aim has to be on him for 1–2.5 s (a yell or a shot still startles him
    /// at once) — then FREEZES (the startle, "Whoa, whoa, whoa!", a still beat: not fleeing, not attacking) and CHOOSES:
    ///   backs off to the door, "I'm leaving, I'm leaving!" ~30% · bolts ~25% · gives up ~20% (more with a gun on him and an
    ///   order) · ducks behind your real couch/table/bed, then runs or gives up ~15% · pulls his knife ~10%, only if he has one
    ///   and you're standing between him and the door (then he's a knife attacker, in his own voice).
    /// The gun runner and the surrenderer use the same notice delay. The judge reads what he does, as ever: backing off =
    /// fleeing (shooting him is a bad call), frozen = neither, the knife = the knife attacker's facts.
    /// </summary>
    public partial class Intruder
    {
        public enum BurglarChoice { BackAway, Bolt, GiveUp, Hide, PullKnife }

        /// <summary>Tests: force the choice (null = the weighted roll).</summary>
        public static BurglarChoice? TestBurglarChoice;
        /// <summary>Tests: seconds of aim before he notices (null = rolled 1–2.5 s per man).</summary>
        public static float? TestNoticeDelay;

        float _noticeDelay = -1f;

        /// <summary>Aimed at long enough for him to notice: the rolled delay on varied rounds, <paramref name="legacy"/> s otherwise.</summary>
        bool NoticesAim(float legacy)
        {
            if (!_styled) return AimedFor > legacy;
            if (_noticeDelay < 0f) _noticeDelay = TestNoticeDelay ?? Random.Range(1f, 2.5f);
            return AimedFor > _noticeDelay;
        }

        /// <summary>Caught in the act: the startle, a word, a frozen beat, then his choice. Ends the brain (never loops back).</summary>
        IEnumerator FreezeAndChoose(bool challenged, Command c)
        {
            yield return Caught();
            if (Current == State.Dead) yield break;
            StopHere();
            if (Sfx.Has("vo.burglar.whoa")) Say("vo.burglar.whoa");
            float until = Time.time + Random.Range(0.8f, 1.6f);
            bool shotAt = false;
            while (Current != State.Dead && Time.time < until)
            {
                if (Time.deltaTime > 0f) FaceTowards(PlayerOnFloor(), 6f);
                if (GunshotRecently(0.3f)) { shotAt = true; break; }     // fired on while frozen: that decides it
                yield return null;
            }
            if (Current == State.Dead) yield break;
            var choice = shotAt ? BurglarChoice.Bolt : PickChoice(challenged, c);
            Debug.Log($"[HIS] Intruder #{Id}: burglar caught, froze — {choice}");
            switch (choice)
            {
                case BurglarChoice.BackAway:
                    if (Sfx.Has("vo.burglar.leaving")) Say("vo.burglar.leaving");
                    yield return FleeAndLeave(run: false);
                    break;
                case BurglarChoice.Bolt:
                    Say("vo.burglar.flee");
                    yield return FleeAndLeave(run: true);
                    break;
                case BurglarChoice.GiveUp:
                    yield return Surrender(c == Command.GetDown);
                    break;
                case BurglarChoice.Hide:
                    yield return HideThenDecide();
                    break;
                case BurglarChoice.PullKnife:
                    yield return TurnOnYou();
                    break;
            }
        }

        BurglarChoice PickChoice(bool challenged, Command c)
        {
            if (TestBurglarChoice.HasValue) return TestBurglarChoice.Value;
            bool armed = _weapon != null && _weapon.InHand && _weapon.Kind == WeaponKind.Knife;
            float giveUp = challenged && c is Command.ShowHands or Command.GetDown or Command.DropWeapon && AimedAt ? 0.45f : 0.2f;
            float knife = armed && PlayerBlocksTheDoor() ? 0.1f : 0f;
            float hide = CoverSpot(out _) ? 0.15f : 0f;
            // What's left of 1 goes to backing off / bolting in the pitch's 30:25 ratio.
            float rest = Mathf.Max(0f, 1f - giveUp - knife - hide), back = rest * 30f / 55f;
            float r = Random.value;
            if ((r -= giveUp) < 0f) return BurglarChoice.GiveUp;
            if ((r -= knife) < 0f) return BurglarChoice.PullKnife;
            if ((r -= hide) < 0f) return BurglarChoice.Hide;
            if ((r -= back) < 0f) return BurglarChoice.BackAway;
            return BurglarChoice.Bolt;
        }

        /// <summary>You're standing between him and the way out (within 1.2 m of his line to the doorway, and nearer than it).</summary>
        bool PlayerBlocksTheDoor()
        {
            Vector3 me = Flat(transform.position), door = Flat(_insidePoint), you = Flat(PlayerOnFloor());
            var seg = door - me; float len = seg.magnitude;
            if (len < 0.5f) return false;
            float along = Vector3.Dot(you - me, seg / len);
            return along > 0.3f && along < len && Vector3.Distance(me + seg / len * along, you) < 1.2f;
        }

        /// <summary>Behind real furniture from you: the far side of a couch/table/bed/cabinet within 4 m, on the NavMesh.</summary>
        bool CoverSpot(out Vector3 spot)
        {
            spot = default;
            var geo = HIS.Room.RoomScanner.Instance != null ? HIS.Room.RoomScanner.Instance.Geometry : null;
            if (geo == null) return false;
            Vector2 me = new(transform.position.x, transform.position.z), you = new(PlayerOnFloor().x, PlayerOnFloor().z);
            float best = float.MaxValue;
            foreach (var b in geo.Boxes)
            {
                if (b.Name is not ("Couch" or "Table" or "Bed" or "Cabinet") || b.Top - b.Bottom < 0.4f) continue;
                if (Vector2.Distance(me, b.Center) > 4f) continue;
                var away = b.Center - you; if (away.sqrMagnitude < 0.01f) continue; away.Normalize();
                float reach = Mathf.Abs(Vector2.Dot(away, b.AxisX)) * b.Half.x + Mathf.Abs(Vector2.Dot(away, b.AxisZ)) * b.Half.y;
                var p2 = b.Center + away * (reach + 0.45f);
                var p = new Vector3(p2.x, transform.position.y, p2.y);
                if (!NavMesh.SamplePosition(p, out var hit, 0.5f, NavMesh.AllAreas)) continue;
                float d = Vector2.Distance(me, p2);
                if (d < best) { best = d; spot = hit.position; }
            }
            return best < float.MaxValue;
        }

        /// <summary>Ducks behind the furniture (kneeling: the real couch hides him), waits, then runs or gives up.</summary>
        IEnumerator HideThenDecide()
        {
            if (!CoverSpot(out var spot)) { yield return FleeAndLeave(run: true); yield break; }
            _fleeingNow = true;                               // heading away from you: not coming at you
            yield return PathTo(spot, () => IntruderClips.RunSpeed, () => null);
            _fleeingNow = false;
            if (Current == State.Dead) yield break;
            StopHere();
            SetGait(Gait.Kneel);
            float until = Time.time + Random.Range(2f, 4f);
            Command heard = Command.None;
            while (Current != State.Dead && Time.time < until)
            {
                if (TryHear(out var c)) { heard = c; break; }
                if (GunshotRecently(0.3f)) break;
                yield return null;
            }
            if (Current == State.Dead) yield break;
            // Yelled at from cover, he mostly gives up; otherwise it's a coin toss.
            bool giveUp = heard != Command.None ? Random.value < 0.7f : Random.value < 0.5f;
            if (giveUp) { yield return Surrender(heard == Command.GetDown); yield break; }
            yield return StandUp();
            if (Current == State.Dead) yield break;
            Say("vo.burglar.flee");
            yield return FleeAndLeave(run: true);
        }

        /// <summary>Cornered with a knife in his hand: he comes at you (the knife attacker's brain) — in his own voice.</summary>
        IEnumerator TurnOnYou()
        {
            if (Sfx.Has("vo.burglar.cornered")) Say("vo.burglar.cornered");
            _intent = Intent.KnifeAttacker;
            yield return MeleeBrain();
        }
    }
}
