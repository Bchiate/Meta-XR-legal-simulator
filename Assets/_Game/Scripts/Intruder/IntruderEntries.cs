using System.Collections;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// The door never plays the same way twice (Brett's pick #1, 2026-09-24: "pointing at the door waiting… not the most
    /// fun"). The director rolls an <see cref="EntryStyle"/> per round (<see cref="ScenarioDirector.VariedEntries"/>);
    /// this half plays it: a random wait at the door, then Classic (pound ×3, kick ×3), Rush (silence, the first kick bursts
    /// it), KnockThenKick (a polite knock, silence, then kicks), WeakGuy (kicks that fail, a quiet spell, then it goes),
    /// QuietPry (a scrape at the latch and the door creeps open — burglars), or FalseAlarm (a knock, "Delivery!", steps
    /// leave: nobody comes in). Unlocked-door entries (keys, drunk Mike) keep <see cref="QuietEntry"/>.
    ///
    /// Shot through the door (Brett 0.1.17: "he'll kick it in anyway and won't react"): he is hittable the whole time he's
    /// at the door, including the wait before the first sound, and every hit gets a reaction. A burglar/runner, a man with
    /// his legs gone, or a fighter really hit (<see cref="DoorGrazeDamage"/> or more) gives up (<see cref="ShotAtTheDoor"/>);
    /// a fighter only grazed reels into the leaf, a beat, then comes through angrier on the next kick.
    /// </summary>
    public partial class Intruder
    {
        /// <summary>
        /// This round's way through the door, set by the director right after spawning when entries vary. Unset (tests and
        /// tools that spawn him directly), he keeps the fixed 0.1.17 door.
        /// </summary>
        public EntryStyle Style { get => _style; set { _style = value; _styled = true; } }
        EntryStyle _style = EntryStyle.Classic;
        bool _styled;

        /// <summary>
        /// A hit through the door that takes at least this much makes even a fighter give up. Below it he's only grazed and
        /// comes on angrier. (A pistol round through the leaf lands 23–34; the door-shot repro had fighters coming in after
        /// 23–55, Brett 0.1.17's "he'll kick it in anyway".)
        /// </summary>
        const float DoorGrazeDamage = 15f;

        float _doorHealth = 100f;     // his health when he last reacted at the door: any drop since is a new hit to react to
        bool _enraged;                // grazed through the door: the next kick goes through

        bool NewDoorHit => ShotThroughDoor && Current != State.Dead && Health < _doorHealth;

        bool Fighter => _intent is Intent.MeleeAttacker or Intent.KnifeAttacker || (_intent == Intent.Gunman && Aggressive);

        /// <summary>Run()'s door phase when entries vary: the wait (hidden, hittable), then the rolled style.</summary>
        IEnumerator EntryByStyle(HIS.Room.VirtualDoor door)
        {
            if (!_doorOpenedOnHim) _doorHealth = Health; // (in view on the path, the delivery man's hits there still count: AtTheDoor)
            if (Style == EntryStyle.FalseAlarm) { yield return FalseAlarmVisit(door); yield break; }
            // The wait before the first sound: short for a rush, anything from a breath to a long, quiet stretch otherwise.
            // Up the path on camera first: he's been in sight a while already, so only a breath at the door.
            yield return DoorWait(_cameUpStreet ? Random.Range(0.5f, 2.5f) : Style == EntryStyle.Rush ? Random.Range(2f, 4f) : Random.Range(2.5f, 10f));
            yield return WarnIfUnseen(door);                   // a door you can't see never opens without 3 s of warning
            if (Current is State.Gone or State.Dead) yield break;
            if (_entry == EntryType.OpenedUnlocked) { yield return QuietEntry(door); yield break; }
            StepUpToDoor(door);
            SetState(State.BreakIn);
            Debug.Log($"[HIS] Intruder #{Id}: entry {Style}");
            switch (Style)
            {
                case EntryStyle.Rush:
                    _forcingDoor = true;
                    yield return KickDoor(door, 1);
                    break;
                case EntryStyle.KnockThenKick:
                    // A knock is not forcing the door (shooting a knocker is still a bad call); the kicks are.
                    for (int i = 0; i < 2 && !_enraged; i++)          // told "open up" already: no polite knock
                    {
                        Sfx.Play("door.pound", _outsidePoint + Vector3.up * 1.2f, 0.35f);
                        if (HIS.Room.RoomScanner.Instance != null) HIS.Room.RoomScanner.Instance.ShakeDoor(0.2f);
                        yield return DoorWait(0.45f);
                    }
                    yield return DoorWait(Random.Range(4f, 8f));
                    _forcingDoor = true;
                    yield return KickDoor(door, 2);
                    break;
                case EntryStyle.WeakGuy:
                    // The whole attempt counts as forcing the door, the quiet spell in the middle too (he isn't done).
                    _forcingDoor = true;
                    yield return Pound(2);
                    yield return KickDoor(door, 2, breaks: false);
                    _kicking = false;                              // catching his breath: killed now, he doesn't burst it
                    yield return AtDoorReacting(Random.Range(5f, 7f));
                    yield return KickDoor(door, 2);
                    break;
                case EntryStyle.QuietPry:
                    yield return PryDoor(door);
                    break;
                default:
                    yield return ClassicKickIn(door);
                    break;
            }
            if (_hasAnim) _anim.ResetKick();
        }

        /// <summary>The original break-in (pound ×3, the latch holds twice, the third kick bursts it), with the hit reactions.</summary>
        IEnumerator ClassicKickIn(HIS.Room.VirtualDoor door)
        {
            _forcingDoor = true;
            yield return Pound(3);
            yield return KickDoor(door, 3);
        }

        /// <summary>Fist blows on the leaf, building up; a hit reacts.</summary>
        IEnumerator Pound(int blows)
        {
            for (int i = 0; i < blows; i++)
            {
                if (ShotThroughDoor) yield return ReactToDoorShot();
                Sfx.Play("door.pound", _outsidePoint + Vector3.up * 1.1f, 0.7f + 0.1f * i);
                if (HIS.Room.RoomScanner.Instance != null) HIS.Room.RoomScanner.Instance.ShakeDoor(0.6f + 0.25f * i);
                yield return AtDoorReacting(0.55f);
            }
            yield return AtDoorReacting(0.4f);
        }

        /// <summary>
        /// Kicks timed to the clip's contact frame, each after the last clip ends. The last one bursts the door (unless
        /// <paramref name="breaks"/> is false: the latch holds, a weak guy's kicks); an enraged man's next kick bursts it.
        /// </summary>
        IEnumerator KickDoor(HIS.Room.VirtualDoor door, int kicks, bool breaks = true)
        {
            const float kickRecover = IntruderClips.KickLength * IntruderClips.OneShotExit - IntruderClips.KickContact;
            for (int k = 0; k < kicks; k++)
            {
                if (ShotThroughDoor) yield return ReactToDoorShot();   // gives up = never returns
                bool final = (breaks && k == kicks - 1) || _enraged;
                _kicking = true;
                if (_hasAnim) _anim.Kick();
                yield return new WaitForSeconds(IntruderClips.KickContact);
                // Hit mid-kick: it never lands. He reacts (or gives up: never returns); an enraged man redoes it, and it bursts.
                if (NewDoorHit) { _kicking = false; yield return ReactToDoorShot(); k--; continue; }
                if (door == null) Sfx.Play(final ? "door.burst" : "door.kick", _outsidePoint + Vector3.up * 1.0f, 1f);
                _kicksLanded++;
                if (final) { _forcingDoor = _kicking = false; if (door != null) door.Breach(); SetVisible(true); }
                else if (door != null) door.KickImpact(0.7f + 0.3f * k);
                yield return new WaitForSeconds(final ? kickRecover : IntruderClips.KickLength - IntruderClips.KickContact + 0.25f);
                if (final) yield break;
            }
        }

        /// <summary>
        /// A burglar working the latch: two scrapes (prying is forcing the door), then the leaf creeps open and he eases in.
        /// Fighters never pry; a fighter dealt this style kicks instead.
        /// </summary>
        IEnumerator PryDoor(HIS.Room.VirtualDoor door)
        {
            if (Fighter) { yield return ClassicKickIn(door); yield break; }
            _forcingDoor = true;
            var latch = _outsidePoint + Vector3.up * 1.0f;
            for (int i = 0; i < 2; i++)
            {
                Sfx.Play(Sfx.Has("door.pry") ? "door.pry" : "door.keys.unlock", latch, 0.55f + 0.15f * i);
                if (door != null) door.Pound(0.12f);
                yield return AtDoorReacting(1.1f);
            }
            _forcingDoor = false;
            if (door != null) door.OpenGently(3.2f, 80f);   // a slow creep: a gap first, then the whole leaf
            yield return AtTheDoor(0.9f);                   // still hidden (and hittable) behind the first few degrees
            SetVisible(true);
            yield return new WaitForSeconds(0.8f);
        }

        /// <summary>
        /// Nobody's coming in: two polite knocks, "Delivery!" through the door, a beat, and his steps fade away. Hold your
        /// fire: a perfect round. Shoot through the door: you shot a man who only knocked (the judge's BehindDoor rule).
        /// </summary>
        IEnumerator FalseAlarmVisit(HIS.Room.VirtualDoor door)
        {
            yield return AtTheDoor(_cameUpStreet ? Random.Range(0.5f, 1.5f) : Random.Range(2f, 5f));
            SetState(State.BreakIn);
            Debug.Log($"[HIS] Intruder #{Id}: entry FalseAlarm (nobody comes in)");
            // He rings / knocks only at a shut door: the player may have opened it on him already (research/25).
            if (_cameUpStreet && door != null && !door.IsOpen) { door.RingDoorbell(); ProtecteeHearsTheDoor(); yield return AtTheDoor(1.6f); } // the delivery man rings
            else for (int i = 0; i < 2 && (door == null || !door.IsOpen); i++)
            {
                Sfx.Play("door.pound", _outsidePoint + Vector3.up * 1.2f, 0.3f);
                if (i == 0) ProtecteeHearsTheDoor();        // a Marshal night's Ricky: "Who's that?" about half the time (no Ricky: nothing)
                yield return AtTheDoor(0.45f);
            }
            yield return AtTheDoor(0.6f);
            // A Marshal night's pizza courier (Brett 0.1.56: "pizza delivery guy carrying a mail package"): the box held out if
            // you opened the door, his line, set down on the step, and he walks off past the camera's edge (IntruderVisitors).
            if (_carry != null)
            {
                yield return HandOverPizza(door);
                yield return FleeAndLeave(run: false);
                while (true) yield return null;
            }
            // A Marshal night's courier says his own line (ScenarioDef.VoicePrefix "courier", the delivery voice: research/27 §5),
            // captioned even before its take lands; the plain delivery man as before (only with his clip).
            var prefix = VisitorVoice;
            Say(prefix != null ? VoiceLines.Visitor(prefix, "hello") : Sfx.Has("vo.delivery.hello") ? "vo.delivery.hello" : null);
            DropProp(onStep: true);                   // "I'll just leave it here": the parcel sits on the step on camera
            yield return AtTheDoor(2.5f);
            yield return FleeAndLeave(run: false);   // outside: RunOffOutside — his steps fade, then Gone
            while (true) yield return null;
        }

        /// <summary>
        /// A Marshal night's keyed visitor at the shut door (research/27 §1: Ortiz "walks up and knocks: 'Marshals. Ortiz. I'm
        /// coming in.'", Sal "Ricky? It's Sal. Open up."): two knocks (not forcing the door), a beat, and who he is through the
        /// leaf, muffled. He has a key, so <see cref="QuietEntry"/> then lets him in. A hit at any point is answered as at any
        /// door (<see cref="AtTheDoor"/>: he never comes in). Already answered through the door (a yell got there first): no
        /// knock and no repeat, just the beat before the key.
        /// </summary>
        IEnumerator KnockAndAnnounce(HIS.Room.VirtualDoor door)
        {
            if (Current is State.Dead or State.Gone) yield break;
            if (_introduced) { yield return AtTheDoor(0.6f); yield break; }
            for (int i = 0; i < 2 && (door == null || !door.IsOpen); i++)
            {
                Sfx.Play("door.pound", _outsidePoint + Vector3.up * 1.2f, 0.35f);
                if (HIS.Room.RoomScanner.Instance != null) HIS.Room.RoomScanner.Instance.ShakeDoor(0.2f);
                // Ricky (research/27 §3: he speaks only after a bell, a knock or the first car) — not "Who's that? Nobody's
                // supposed to know." for the deputy who just radioed that he's coming up (ScenarioDef.RadioAhead).
                if (i == 0 && string.IsNullOrEmpty(_def?.RadioAhead)) ProtecteeHearsTheDoor();
                yield return AtTheDoor(0.45f);
            }
            yield return AtTheDoor(0.5f);
            float words = 0f;
            if (!_introduced)                                 // (a yell during the knocks got its answer first)
            {
                // His knock line if he has one (Ortiz checking in: "It's Ortiz."), else his door line.
                var key = VoiceLines.Visitor(VisitorVoice, "knock") ?? VoiceLines.Visitor(VisitorVoice, "door");
                Say(key);
                _introduced = key != null;
                words = key != null ? Sfx.Length(key) : 0f;
                Debug.Log($"[HIS] Intruder #{Id}: knocks and says who he is ({_def.Name})");
            }
            yield return AtTheDoor(Mathf.Clamp(words + 0.3f, 1.2f, 2.8f));   // his words, then the key
        }

        /// <summary>
        /// Up to the leaf to pound, kick or pry it: half a metre behind its plane, not ~1.25 m back where he waited. A round
        /// through the leaf then reaches him low (his legs: Brett shoots legs first) and toward the edges, as it would a real
        /// man at a real door (the door-shot repro: rounds at the low/edge spots never reached him).
        /// </summary>
        void StepUpToDoor(HIS.Room.VirtualDoor door)
        {
            var inward = Flat(_insidePoint - _outsidePoint).normalized;
            float plane = door != null ? Vector3.Dot(door.transform.position - _outsidePoint, inward) : 0.8f;
            transform.position = _outsidePoint + inward * Mathf.Max(0f, plane - 0.5f);
        }

        /// <summary>A wait at the door that answers a hit (see <see cref="ReactToDoorShot"/>) instead of ignoring it.</summary>
        IEnumerator AtDoorReacting(float seconds)
        {
            float until = Time.time + seconds;
            while (Time.time < until && !_enraged)             // told "open up"/grazed: done waiting, the door goes
            {
                if (_doorAnswer == DoorAnswer.Leave) yield return LeaveFromDoor();
                if (NewDoorHit) yield return ReactToDoorShot();
                yield return null;
            }
        }

        /// <summary>
        /// Hit through the door while at it. Burglars and runners, a man whose legs are gone, and a fighter hit hard give up
        /// (<see cref="ShotAtTheDoor"/> never returns). A grazed fighter reels: his body hits the leaf (it judders, a dull
        /// thud; the scream already came from ResolveHit), a beat, and he comes through angrier. Each new hit re-decides.
        /// </summary>
        IEnumerator ReactToDoorShot()
        {
            if (!NewDoorHit) yield break;
            float took = _doorHealth - Health;
            _doorHealth = Health;
            if (!Fighter || _gait == Gait.Crawl || took >= DoorGrazeDamage) { StopForcing(); yield return ShotAtTheDoor(); }
            _enraged = true;
            var door = Door;
            if (door != null && !door.IsOpen) door.Thump(0.6f, 1100f);
            Debug.Log($"[HIS] Intruder #{Id}: grazed through the door ({Health:F0} hp) — reels, then comes on angrier");
            yield return new WaitForSeconds(0.9f);
        }
    }
}
