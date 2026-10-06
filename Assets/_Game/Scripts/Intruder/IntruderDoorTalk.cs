using System.Collections;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// Talk through the door (Brett's pick #2, 2026-09-24: "you can't talk to him before he's in"). While he's behind the closed
    /// door, a yell tells him someone's home, and he answers through the leaf, muffled, in his own voice:
    ///   Mike — "Dave? It's Mike, lemme in, man." (told to get out: "Alright, alright… jeez." and he goes home)
    ///   burglar — dead silence, then (about 6 times in 10) his steps hurry away: a perfect round with the door never opened
    ///   surrenderer — "Sorry! Sorry, wrong house!" and he leaves
    ///   knife man — "Open the door. Now." and the next kick bursts it
    ///   home invader — "Open the door! I know you're in there!" (told to get out: "Yeah? Make me!"), then the door goes
    ///   gunman — "I know you're in there!" or a lie ("Pizza! …Uh, delivery!"); the hard one comes straight through, the
    ///            soft one may think better of it when told to leave
    /// He answers once; the decision then stands (<see cref="_doorAnswer"/>): the door waits end at once for a kick, or he
    /// walks off. The delivery man of a false alarm already said his line. The judge reads none of this: leaving before
    /// entering is EndReason.Left as ever, and the kick-in still counts as forcing the door.
    /// </summary>
    public partial class Intruder
    {
        enum DoorAnswer { None, Leave, KickNow }
        DoorAnswer _doorAnswer;
        bool _doorTalked;

        /// <summary>Tests: the roll for "does he leave?" (0 = always, 1 = never); null = random.</summary>
        public static float? TestDoorRoll;
        static float DoorRoll => TestDoorRoll ?? Random.value;

        /// <summary>Hear() routes a yell here while he's behind the closed door on a varied-entries round.</summary>
        bool HearsThroughDoor => _styled && BehindDoor && !OnPorchPath && Current is State.Outside or State.BreakIn; // (down the path: out of earshot)

        void HearThroughDoor(Command c)
        {
            if (_doorTalked || c == Command.None || Current is State.Dead or State.Gone) return;
            _doorTalked = true;
            StartCoroutine(AnswerThroughDoor(c));
        }

        IEnumerator AnswerThroughDoor(Command c)
        {
            yield return new WaitForSeconds(Random.Range(0.5f, 1.1f)); // a beat: someone's home
            if (Current is State.Dead or State.Gone || !BehindDoor || ShotThroughDoor) yield break;
            bool getOut = c != Command.WhoIsThere;                      // any order through the door reads as "go away"
            string said = null;
            switch (_intent)
            {
                case Intent.Burglar:
                    if (DoorRoll < 0.6f) _doorAnswer = DoorAnswer.Leave; // no word: just his steps hurrying off
                    break;
                case Intent.Surrenderer:
                    said = "vo.surrender.door"; _doorAnswer = DoorAnswer.Leave;
                    break;
                case Intent.Innocent:
                    if (Style == EntryStyle.FalseAlarm) break;          // the delivery man already spoke; he's leaving anyway
                    // Mike's lines, or a Marshal night's visitor's own (VisitorVoice, research/27 §5). Told to go, he goes only if
                    // he has a line for it (Mike, Sal); Ortiz has none: a deputy with a key comes in anyway, saying who he is.
                    string goKey = getOut ? VoiceLines.Visitor(VisitorVoice, "door.leave") : null;
                    said = goKey ?? VoiceLines.Visitor(VisitorVoice, "door");
                    if (goKey != null) _doorAnswer = DoorAnswer.Leave;
                    break;
                case Intent.KnifeAttacker:
                    said = "vo.knife.door"; _doorAnswer = DoorAnswer.KickNow;
                    break;
                case Intent.MeleeAttacker:
                    said = getOut ? "vo.attacker.makeme" : "vo.attacker.door"; _doorAnswer = DoorAnswer.KickNow;
                    break;
                case Intent.Gunman:
                    said = DoorRoll < 0.35f ? "vo.gunman.door.lie" : "vo.gunman.door";
                    if (Aggressive) _doorAnswer = DoorAnswer.KickNow;
                    else if (getOut && DoorRoll < 0.5f) _doorAnswer = DoorAnswer.Leave;
                    break;
            }
            // (A keyed visitor's answer is captioned before its take lands: who he is must reach the player. Once said, it names
            // him for the judge — IdentityStated, and a deputy's KnownDeputy: research/27 §4.)
            if (said != null && (Sfx.Has(said) || KeyedVisitor)) { Say(said); if (KeyedVisitor) _introduced = true; }
            if (_doorAnswer == DoorAnswer.KickNow) _enraged = true;    // the next kick goes through (IntruderEntries)
            Debug.Log($"[HIS] Intruder #{Id}: heard '{c}' through the door → {(said ?? "silence")}, {_doorAnswer}");
        }

        /// <summary>A door wait that ends early for a kick-now answer, walks him off for a leave, and reacts to a hit.</summary>
        IEnumerator DoorWait(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until && !_enraged)
            {
                if (_doorAnswer == DoorAnswer.Leave) yield return LeaveFromDoor();
                if (ShotThroughDoor && Current != State.Dead) yield return ShotAtTheDoor();
                yield return null;
            }
        }

        /// <summary>Talked off before the door ever opened: his steps fade away on the far side. Never returns.</summary>
        IEnumerator LeaveFromDoor()
        {
            _forcingDoor = _kicking = false;
            _givingUp = true;   // the door opening on him now doesn't restart him (WatchDoor)
            Debug.Log($"[HIS] Intruder #{Id}: leaves from the door without coming in");
            yield return FleeAndLeave(run: _intent == Intent.Burglar);
            while (true) yield return null;
        }
    }
}
