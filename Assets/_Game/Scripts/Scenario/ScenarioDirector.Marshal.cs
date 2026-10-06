using System.Collections;
using HIS.Core;
using HIS.Outcome;
using HIS.UI;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Scenario
{
    /// <summary>
    /// Marshal mode (research/27; Brett 2026-10-01: "its the same wave style game just with hitmen instead of police"): a
    /// night shift at a safe house with Ricky, the witness. The whole night (§1, §6 step 8; Brett 2026-10-03: "finish the ricky
    /// and stuff for the full marshall story"):
    /// Ortiz's handover in person when it's owed (<see cref="MarshalBriefing"/>: the first night after the app starts or a
    /// switch into Marshal, or asked for from the pause menu; the weapons drill inside it once per headset or when asked) →
    /// SAFE HOUSE · 1:12 AM, Ricky crouched in his spot → (Brett 0.1.57: "it should be mostly the mob coming after him") a
    /// short quiet, then five hitman waves on the police machine (<see cref="PoliceResponse.RunHitmen"/>), each harder, with
    /// two quiet visitors between them, each judged as any round but with no show at all when left alone (Brett 0.1.58: "when
    /// he leaves it shouldnt give the good call banner the mob people should come"; research/27 Q3's quiet banner until then): the pizza
    /// courier after wave 1, Sal after wave 3 ("don't shoot him" while you're on edge: <see cref="BetweenWaves"/>) → the
    /// 2:00 AM cut (cars and bodies gone, Ricky kept) → the relief (Ortiz again; the round ends a few seconds after his
    /// hello) → WITNESS DELIVERED, the full show. (Ortiz's 1:20 check-in is gone: he's in the handover and at two.)
    /// Any miss ends the night on its own card, one look per meaning (Brett 2026-10-04 (a)): any harm you do — Ricky or a
    /// visitor, by gun or knife — stops the night a beat later and is the arrest (red/blue lights, the cruisers on the camera,
    /// the mugshot, the red card with the charge, the federal count and the OPR referral; never a police standoff: the police
    /// never come to fight a Marshal); the hitmen getting Ricky or you is the grey THE TRIAL IS OFF; the night survived is
    /// WITNESS DELIVERED. Every difference from the scenario game is behind Mode.Marshal: the
    /// shared round code only changes with its keepProtectee argument, which only this night passes. Nothing on screen names
    /// a door, and there are no prompts or hints.
    /// </summary>
    public partial class ScenarioDirector
    {
        const string MarshalCardKey = "marshal";
        /// <summary>The intro card (research/27 §1, 0:05): the time on the monitor's clock and the one line of set-up.</summary>
        const string MarshalIntro = "<b>SAFE HOUSE · 1:12 AM</b>\n<size=70%>Relief's at two. Nobody knows this address.</size>";
        /// <summary>The cut after the last wave (research/27 §1, 9:30): the street is empty again when it goes.</summary>
        const string MarshalTwoAm = "<b>2:00 AM</b>";
        /// <summary>Seconds the intro and 2:00 AM cards stay up ("about 3 s"), the quiet after the 2:00 AM card before the relief
        /// (and after the 1:12 card on a test's bare night), and the breather after the night's card before the next night (Y
        /// cuts it short, as between rounds).</summary>
        const float MarshalIntroSeconds = 3f, MarshalQuietSeconds = 4f, MarshalBreather = 6f;
        /// <summary>
        /// The quiet after the 1:12 card before the first car (Brett 0.1.57: "mostly the mob coming after him" — the lead's pick
        /// 2: the first car about 30 s after the card, where the 0.1.56 night had two minutes of visitors first). Y cuts it
        /// short, never under <see cref="FirstCarFloor"/>.
        /// </summary>
        const float FirstCarQuiet = 25f;
        /// <summary>The quiet after a visitor leaves before the next car (random, so the night isn't a metronome; Y cuts it short).</summary>
        const float VisitorGapMin = 6f, VisitorGapMax = 10f;
        /// <summary>A beat between the night going wrong (Ricky down, you down) and its card, so the moment lands first.</summary>
        const float MarshalEndBeat = 1.5f;
        /// <summary>The least quiet after a card or a visitor before a car is called, even after a Y-skip (Brett 0.1.56:
        /// "the bad guys seemingly come from no where"): the car's build-up (its engine, the monitor's VEHICLE, Ricky's call)
        /// all hangs off the wave call, so it is never cut, and no car pulls in on a banner's last frame.</summary>
        const float FirstCarFloor = 1.5f;

        // ───────────── the visitors (research/27 §1, §4, §5). Unarmed, none forces the door: PERFECT left alone, BAD shot. ─────────────
        // Sober (no drunk sway or stagger), each in his own voice through Mike's slots (VoicePrefix), captioned by name.
        // (Brett 0.1.57: Ortiz's 1:20 check-in is gone — he's in the room for the handover (MarshalBriefing) and comes back at
        // two. Its voice rows and Judge.WhoOrtizCheckIn stay; nothing on the night plays them.)

        /// <summary>Between waves 1 and 2, 1:20: the pizza courier. The bell, his line through the door, the box on the step; he walks off.</summary>
        static readonly ScenarioDef Courier = new()
        {
            Name = "Pizza courier", Intent = Intent.Innocent, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.None,
            Identity = Identity.Stranger, WhoItWas = Judge.WhoCourier,
            VoicePrefix = "courier", Speaker = "Courier", Sober = true, Style = EntryStyle.FalseAlarm,
            Carries = VisitorProp.PizzaBox, WalksOff = true,      // VISITORS: the box in his hands, set on the step; off the porch
        };
        /// <summary>Between waves 3 and 4, 1:38: Sal, Ricky's brother. Knocks, lets himself in with the key Ricky gave him, hands empty.</summary>
        static readonly ScenarioDef Sal = new()
        {
            Name = "Sal, Ricky's brother", Intent = Intent.Innocent, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.None,
            Identity = Identity.Family, WhoItWas = Judge.WhoSal,
            VoicePrefix = "brother", Speaker = "Sal", Sober = true,
            Carries = VisitorProp.None, StaysFor = 8f, WalksOff = true, // VISITORS: hands empty, a short visit, off the porch
        };
        /// <summary>2:00 AM: the relief, Ortiz again (the test: the last man up the path after five waves). The round ends
        /// <see cref="ScenarioDef.RelievedAfter"/> s after his hello.</summary>
        static readonly ScenarioDef OrtizRelief = new()
        {
            Name = "Deputy Ortiz, the relief", Intent = Intent.Innocent, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.None,
            Identity = Identity.Officer, WhoItWas = Judge.WhoOrtizRelief,
            VoicePrefix = "relief", Speaker = "Ortiz", Sober = true, RelievedAfter = 4f,
            RadioAhead = "vo.deputy.radio.relief",                  // VISITORS: he radios before he walks up (the hitmen never do)
        };
        /// <summary>
        /// The visitors between the hitman waves (Brett 0.1.57 pick 3), in a fixed order (research/27 §1: testers get the same
        /// night; the cards hint how the address got out): who comes after which beaten wave, and the monitor's clock as he
        /// walks up. Declared after the defs (static initializers run in order).
        /// </summary>
        static readonly (int AfterWave, ScenarioDef Def, int Hour, int Minute)[] BetweenWaveVisitors =
        {
            (1, Courier, 1, 20),
            (3, Sal, 1, 38),
        };
        /// <summary>The monitor's clock as each hitman wave is called (Brett 0.1.57's five: 1:15, 1:24, 1:33, 1:42, 1:51).</summary>
        static readonly Vector2Int[] WaveClock = { new(1, 15), new(1, 24), new(1, 33), new(1, 42), new(1, 51) };

        /// <summary>Marshal nights that reached their card (tests wait on this, as <see cref="Completed"/> for rounds).</summary>
        public int NightsDone { get; private set; }
        int _night;

        /// <summary>Tests: false = milestone A's bare night (Ricky, then the waves straight to the card: no visitors, no cut, no relief).</summary>
        public static bool MarshalVisitors = true;

        /// <summary>Tonight's Ricky (null before he spawns, after the night, or if no hiding spot could be found).</summary>
        IntruderT _ricky;
        /// <summary>The player's force landed on Ricky tonight (his ForceApplied; the finder's rounds never report: research/27 §3).</summary>
        bool _rickyHit;
        ForceKind _rickyHitKind;
        /// <summary>A bad call on the visitor in the round Ricky's hit dropped (RunScenario): his charge goes on Ricky's card too
        /// (Brett 2026-10-04 (a): the booking with the right charge). Null: none. Gun: a round of yours hit the visitor.</summary>
        Verdict? _rickyHitVisitorCall;
        bool _rickyHitVisitorGun;
        /// <summary>Visitors whose round was played out tonight (WITNESS DELIVERED's "Visitors: 3, no bad calls.": the courier,
        /// Sal and the relief since Brett 0.1.57).</summary>
        int _visitorsTonight;

        /// <summary>How a night's step left it. Booked: a bad call on the visitor (his booking card next).</summary>
        enum NightStep { On, RickyHit, Booked, Ended, CalledOff }

        /// <summary>How the visitor between the waves left the night (<see cref="BetweenWaves"/>): On, or the step that Cancel()ed
        /// the hitman run there, read once RunHitmen returns (before its Cancelled ending, which is then the visitor's, not "no door").</summary>
        NightStep _betweenStep;
        /// <summary>
        /// Bumped by every night's end (<see cref="EndMarshalNight"/>: StopLoop, and each new night's start). A visitor's step
        /// between the waves runs inside the hitman run, inside the night — nested coroutines, which can outlive the director's
        /// StopCoroutine (PoliceResponse.Run's tail says as much) — so it steps its round itself and stops the moment the night
        /// that started it is gone (<see cref="WhileTonight"/>): no ghost visitor, banner or room clear in the fresh night.
        /// </summary>
        int _nightToken;

        /// <summary>When SetMode last put up the U.S. MARSHAL mode card (Time.time; far in the past: not yet).</summary>
        static float s_marshalCardAt = -100f;
        /// <summary>How long after that card the weapons check leaves its own out (the card is up 2.5 s, the night starts firstDelay = 4 s on).</summary>
        const float MarshalCardFresh = 6f;
        /// <summary>
        /// The switch into Marshal put the U.S. MARSHAL card up moments ago: the weapons check opening the night doesn't put the
        /// same card up again 1.5 s after it went (MarshalBriefing's card step).
        /// </summary>
        internal static bool MarshalCardJustShown => Time.time - s_marshalCardAt < MarshalCardFresh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetMarshalStatics() { _savedMode = -1; MarshalVisitors = true; s_marshalCardAt = -100f; } // domain reload off

        /// <summary>Marshal mode's round loop: the room and door waits (as <see cref="Loop"/>), then one night after another.</summary>
        IEnumerator MarshalNight()
        {
            Status = "waiting for room";
            while (spawner == null || !spawner.NavMeshReady) yield return null;
            Status = "waiting for the door confirm";
            while (!spawner.DoorConfirmed) yield return null;
            yield return new WaitForSeconds(firstDelay);
            while (true)
            {
                if (!spawner.DoorConfirmed)
                {
                    // The door picker is open again (hold-Menu restart): no night until the player has chosen.
                    Status = "waiting for the door confirm";
                    while (!spawner.DoorConfirmed) yield return null;
                    yield return new WaitForSeconds(firstDelay);
                }
                yield return OneMarshalNight();
                // The breather before the next night; Y cuts it short (as between rounds, Brett 0.1.37). Scaled time: a pause freezes it.
                float breather = _skipped ? skipDelay : MarshalBreather;
                _skipArmed = false;
                for (float t = 0f; t < breather; t += Time.deltaTime)
                {
                    if (!_skipped && SkipPressed(null)) { Debug.Log($"[HIS] Next night now (Y, {breather - t:F1} s early)"); break; }
                    yield return null;
                }
                _skipped = false;
            }
        }

        /// <summary>One night, research/27 §1 from the top (see the class summary).</summary>
        IEnumerator OneMarshalNight()
        {
            GameSession.ClearWorld(resetHealth: true);                   // a fresh night: last night's Ricky goes with the rest
            EndMarshalNight();
            yield return null;                                           // (Destroy lands at the frame's end: no second Ricky in the scene)
            _night++;
            _skipped = false;
            _rickyHit = false;
            _rickyHitVisitorCall = null;
            _visitorsTonight = 0;
            // Nothing the round's checks read (the thermal break notice waits on a pending verdict through Record) may be left
            // over from a Scenarios round before the switch.
            Intruder = null; Record = null; LastStandoff = StandoffEnding.None;
            string label = $"Marshal night {_night}";

            // THE HANDOVER, in person (Brett 0.1.57: "I thought ortiz was just apart of the story intro and tutorial which seemed
            // to be missing"; MarshalBriefing): the app's first Marshal night, the first after a switch into Marshal, or the pause
            // row's "Weapons check" — with the weapons drill when it's due, else the short one. Ricky is in his spot from the start
            // of the night — spawned first, under the U.S. MARSHAL card, so Ortiz (in the room by the front door: the briefing
            // places him) can name him — and practice-safe while it runs (Intruder.PracticeHit: no harm, nothing judged). Then the
            // room is cleared again (its holes, the door it swung), Ricky kept — and Ortiz too if he's out on the porch walking off
            // (A6: Intruder.HandoverWalkingOff; he goes by himself past the camera's edge, never popped off the feed mid-walk).
            // (The Editor and the batch tests run it only when asked: MarshalBriefing.Due.)
            if (MarshalBriefing.Due)
            {
                Status = $"{label}: the handover";
                Debug.Log($"[HIS] {label}: the handover{(MarshalBriefing.DrillDue ? " (the weapons check)" : " (short)")}");
                IntruderT.PrewarmMarshalLooks();                         // Ricky's sweats, Ortiz's windbreaker: the one-off hitch under the card
                // The story's clock from the handover on: the monitor (step 10 sends you to it) reads a few minutes past one, not
                // the real time; it ticks on to the 1:12 card (AdvanceTo never goes back: a long check only runs it a little late).
                StoryClock.AdvanceTo(1, 4);
                SpawnRicky(label);
                yield return MarshalBriefing.Run();
                var handoverDeputy = IntruderT.HandoverDeputy;
                if (handoverDeputy != null)
                    Debug.Log($"[HIS] {label}: Ortiz {(handoverDeputy.HandoverWalkingOff ? "on his way off the porch — kept through the room's clear" : "still in the room — he goes with the room's clear")}");
                GameSession.ClearWorld(resetHealth: true, keepProtectee: true);
                yield return null;
            }

            // 1:12 AM: the card, and Ricky in his spot if the check didn't place him (spawned under the card: his grey dress's
            // one-off hitch hides behind it).
            Debug.Log($"[HIS] {label}: safe house, 1:12 AM");
            StoryClock.AdvanceTo(1, 12);
            Status = $"{label}: 1:12 AM";
            Notice.Show(MarshalCardKey, MarshalIntro, 10);                // the mode card's path (SetMode), same priority
            if (_ricky == null)
            {
                IntruderT.PrewarmMarshalLooks();                         // Ricky's sweats, Ortiz's windbreaker: the one-off hitch under the card
                SpawnRicky(label);
            }
            yield return NightWait(MarshalIntroSeconds, skippable: false);
            HideMarshalCard();
            if (_rickyHit) { yield return RickyHitCard(label); yield break; }

            // The quiet before the first car (Brett 0.1.57: "it should be mostly the mob coming after him"; the lead's pick 2: the
            // first car about 30 s after the card). Y ("Marshal: on now") cuts it short but never the first car's build-up (its
            // engine, the monitor's VEHICLE, Ricky's call: all hang off the wave call): FirstCarFloor after the card at the least.
            // (A test's bare night, MarshalVisitors off: the old short quiet, not skippable.)
            float quietFrom = Time.time;
            yield return NightWait(MarshalVisitors ? FirstCarQuiet : MarshalQuietSeconds, skippable: MarshalVisitors);
            float quietFor = Time.time - quietFrom;
            if (quietFor < FirstCarFloor && !_rickyHit) yield return NightWait(FirstCarFloor - quietFor, skippable: false);
            if (_rickyHit) { yield return RickyHitCard(label); yield break; }

            // The waves (research/27 §2; five since Brett 0.1.57, HitmanTier), the visitors between them (BetweenWaves). Ricky
            // ducks by himself at the first car.
            var police = PoliceResponse.Instance;
            if (police == null) { Debug.LogWarning($"[HIS] {label}: no PoliceResponse, no hitmen"); yield return new WaitForSeconds(2f); yield break; }
            if (police.Active) { Debug.LogWarning($"[HIS] {label}: a squad was still out — cancelled first"); police.Cancel(); }
            // Only a man already gone (hidden for good) is taken out here: the handover's Ortiz may still be walking off the
            // porch past the camera's edge as the first car comes (never popped off the feed mid-walk; MarshalBriefing).
            ClearLeftoverVisitors(label, all: false);
            Intruder = null; Record = null;
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.ShowForScenario(); // (the ring is off by default)
            Status = $"{label}: hitmen";
            _betweenStep = NightStep.On;
            int tonight = _nightToken;
            police.BetweenHitmanWaves = beaten => WhileTonight(BetweenWaves(beaten, label), tonight, label);
            Debug.Log($"[HIS] {label}: the first car, {Time.time - quietFrom:F1} s after the 1:12 card went");
            yield return police.RunHitmen();
            police.BetweenHitmanWaves = null;
            var between = _betweenStep;
            _betweenStep = NightStep.On;
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.Hide(); // the show gets the floor
            if (_rickyHit) { yield return RickyHitCard(label); yield break; } // (Cancel()ed by the hit: no card of its own)
            // A visitor between the waves ended the night (BetweenWaves Cancel()ed the run: its Cancelled ending is his, not "no
            // door"): a bad call on him is the booking (Brett 2026-10-04 (a)); his round's own card (you died) already played.
            if (between == NightStep.Booked) { yield return VisitorBookingCard(label); yield break; }
            if (between != NightStep.On) yield break;
            LastStandoff = police.Ending;
            if (police.Ending == StandoffEnding.Cancelled)
            {
                // Called off with nobody spawned (no door to spawn at): no card; a beat, then the loop starts a new night.
                Status = $"{label}: called off";
                yield return new WaitForSeconds(2f);
                yield break;
            }
            var card = police.EndCard;
            if (police.Ending != StandoffEnding.Delivered)
            {
                // THE TRIAL IS OFF: they got Ricky, or you (the squad already stood down: PoliceResponse.End).
                yield return new WaitForSeconds(MarshalEndBeat);
                yield return NightCard(card, police.Ending.ToString(), label);
                yield break;
            }

            if (MarshalVisitors)
            {
                // The 2:00 AM cut (research/27 §1, §6 step 8): the cars and bodies go under the card (GPU, and no shot-up cars
                // round the relief); Ricky stays where he is.
                StoryClock.AdvanceTo(2, 0);
                Status = $"{label}: 2:00 AM";
                Debug.Log($"[HIS] {label}: 2:00 AM — the street is cleared");
                Notice.Show(MarshalCardKey, MarshalTwoAm, 10);
                if (HIS.Security.PoliceCars.Instance != null) HIS.Security.PoliceCars.Instance.End();
                GameSession.ClearWorld(resetHealth: true, keepProtectee: true);
                yield return NightWait(MarshalIntroSeconds, skippable: false);
                HideMarshalCard();
                if (_rickyHit) { yield return RickyHitCard(label); yield break; }
                yield return NightWait(MarshalQuietSeconds, skippable: false);
                if (_rickyHit) { yield return RickyHitCard(label); yield break; }

                // The relief: the last man up the path is the one you must not shoot.
                int before = Completed;
                Status = $"{label}: {OrtizRelief.Name}";
                yield return CallAhead(OrtizRelief, label);               // VISITORS: the relief radios before he walks up
                if (_rickyHit) { yield return RickyHitCard(label); yield break; }
                yield return RunScenario(OrtizRelief, keepProtectee: true, door: 0);
                var step = AfterVisitor(before, label);
                if (step == NightStep.RickyHit) { yield return RickyHitCard(label); yield break; }
                if (step == NightStep.Booked) { yield return VisitorBookingCard(label); yield break; } // a bad call on the relief: the booking
                if (step == NightStep.Ended) yield break;                  // (the round's own card ended it)
                // (CalledOff: he never came, no door to spawn at. The waves are beaten: the night is still delivered.)
                card.Sentence = $"{card.Sentence} {Judge.VisitorTally(_visitorsTonight)}";
                LastStandoff = police.Ending;                              // (the relief's round reset it: the night's ending again)
            }
            yield return NightCard(card, police.Ending.ToString(), label);
        }

        /// <summary>
        /// The night's card, recorded first (LastVerdict, NightsDone: tests read them), then the show at the front door. Ricky
        /// stands down first (as RickyHitCard does): no round during the show or the breather can hit him under a card that's
        /// already up (WITNESS DELIVERED with him dead on screen).
        /// </summary>
        IEnumerator NightCard(Verdict card, string how, string label)
        {
            if (_ricky != null) StandDown(_ricky);
            LastVerdict = card;
            NightsDone++;
            Status = $"{label}: {card.Headline} ({how})";
            Debug.Log($"[HIS] {label}: {card.Headline} ({how}) — {card.Reason} | {card.Sentence}");
            yield return PlayShow(card, 0, label);                       // the cars are out front: the front door's show
        }

        /// <summary>
        /// After a visitor's round: carry on (left alone), or the night is over — Ricky was hit (his card next), a bad call on
        /// the visitor (the booking next: Brett 2026-10-04 (a), never the police standoff), the round's own card ended it (you
        /// died), or it never started (no door: called off).
        /// </summary>
        NightStep AfterVisitor(int completedBefore, string label)
        {
            if (_rickyHit) return NightStep.RickyHit;
            if (Completed == completedBefore) { Debug.LogWarning($"[HIS] {label}: the visitor never came (no door)"); return NightStep.CalledOff; }
            _visitorsTonight++;
            var v = LastVerdict.GetValueOrDefault();
            if (LastStandoff == StandoffEnding.None && v.Tier is Tier.Perfect or Tier.Good) return NightStep.On;
            if (v.Tier == Tier.Bad) return NightStep.Booked;              // (NightCard counts the night)
            NightsDone++;                                                 // its card was the night's (shown by the round)
            Debug.Log($"[HIS] {label}: over on a visitor ({v.Tier}{(LastStandoff != StandoffEnding.None ? $", {LastStandoff}" : "")})");
            return NightStep.Ended;
        }

        /// <summary>
        /// The quiet between the night's beats, in game time (a pause freezes it). Ends at once if the player hurts Ricky; a
        /// <paramref name="skippable"/> wait is cut short by Y (as between rounds), and after a skipped banner it's only a beat.
        /// </summary>
        IEnumerator NightWait(float seconds, bool skippable)
        {
            if (skippable && _skipped) seconds = skipDelay;
            _skipped = false;
            _skipArmed = false;
            for (float t = 0f; t < seconds && !_rickyHit; t += Time.deltaTime)
            {
                if (skippable && SkipPressed(null)) { Debug.Log($"[HIS] Marshal: on now (Y, {seconds - t:F1} s early)"); break; }
                yield return null;
            }
        }

        static float VisitorGap() => Random.Range(VisitorGapMin, VisitorGapMax);

        // ───────────── VISITORS (Brett 0.1.56: "the ortiz dude could be anybody walking up ... pizza delivery guy ... just got
        //               stuck there even into a new round") ─────────────

        /// <summary>
        /// A visitor who radios ahead (ScenarioDef.RadioAhead: the relief at 2 AM — the hitmen never radio or knock): his call
        /// over the Marshal's radio (RadioVoice: squelch, his voice through the radio band, the caption), then he sets off up the
        /// path once it's said (about 10 s of warning in all). Not skippable: a Y on the gap before it still gets the call.
        /// </summary>
        IEnumerator CallAhead(ScenarioDef d, string label)
        {
            if (d == null || string.IsNullOrEmpty(d.RadioAhead) || _rickyHit) yield break;
            Debug.Log($"[HIS] Marshal: {d.Speaker} radios ahead ({d.Name})");
            float len = RadioVoice.Transmit(d.RadioAhead, d.Speaker ?? "Ortiz");
            yield return NightWait(Mathf.Max(2f, len + 0.6f), skippable: false);
        }

        /// <summary>
        /// Brett 0.1.57 (pick 3: the pizza courier and Sal come BETWEEN the waves — "don't shoot him" while you're on edge): the
        /// hitman run's step after beaten wave <paramref name="beaten"/>'s lull (PoliceResponse.BetweenHitmanWaves), the squad idle
        /// meanwhile. Nothing unless <see cref="BetweenWaveVisitors"/> has a visitor for that slot (the courier after wave 1, Sal
        /// after wave 3). Then: the street tidy (PoliceResponse.TidyForVisitor: the bodies off the porch, path and street and out
        /// of the doorways, the broken doors shut — those in the room stay), the clock (1:20 / 1:38), his round as any visitor's
        /// but with no room clear (RunScenario's clearWorld false: the night's blood, holes, bodies in the room and your wounds
        /// stay; left alone he gets no show, so nothing sends the hitmen's parked cars away), the safety net (nobody from the visit left in the scene), the
        /// director's Intruder and Record cleared again (nothing reads him as one of the squad's), the ring back up, and a gap
        /// (6–10 s; Y cuts it, never under <see cref="FirstCarFloor"/>) before the next car is called. His bad call, Ricky hit or
        /// your death in his round end the night: the run is Cancel()ed here (no next wave, no card of its own) and
        /// <see cref="_betweenStep"/> tells OneMarshalNight which card follows (the booking, Ricky's, or none: the round's own).
        /// A test's bare night (MarshalVisitors off) has none.
        /// </summary>
        IEnumerator BetweenWaves(int beaten, string label)
        {
            if (!MarshalVisitors || _rickyHit) yield break;
            int k = -1;
            for (int i = 0; i < BetweenWaveVisitors.Length; i++) if (BetweenWaveVisitors[i].AfterWave == beaten) { k = i; break; }
            if (k < 0) yield break;
            var (_, def, hour, minute) = BetweenWaveVisitors[k];
            var police = PoliceResponse.Instance;
            if (police == null || !police.Active) yield break;              // (the run was called off under us: StopLoop)
            Debug.Log($"[HIS] {label}: {def.Name} between waves {beaten} and {beaten + 1}");
            yield return police.TidyForVisitor(def.Name);
            if (_rickyHit || !police.Active) yield break;
            StoryClock.AdvanceTo(hour, minute);
            int before = Completed;
            Status = $"{label}: {def.Name}";
            yield return CallAhead(def, label);                            // (neither radios: only Ortiz does, at two)
            if (_rickyHit || !police.Active) yield break;
            yield return RunScenario(def, keepProtectee: true, door: 0, clearWorld: false);
            ClearLeftoverVisitors(label, all: false);                     // VISITORS: a visitor who left is out of the scene
            var step = AfterVisitor(before, label);
            if (step == NightStep.CalledOff) step = NightStep.On;         // (he never came — no door: the next wave's call finds that out)
            if (step != NightStep.On)
            {
                _betweenStep = step;
                Debug.Log($"[HIS] {label}: the night ends on {def.Name} ({step}) — no more waves");
                police.Cancel();                                          // (no card of its own; Ricky's hit already did it)
                // His round's own card (you died) was a full show, whose end sent the hitmen's cars away (Cleared); the backstop,
                // should it ever have been a quiet one.
                if (step == NightStep.Ended && HIS.Security.PoliceCars.Instance != null) HIS.Security.PoliceCars.Instance.End();
                yield break;
            }
            ClearLeftoverVisitors(label, all: true);                      // VISITORS: nobody from the visit stands into the next wave
            Intruder = null; Record = null;
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.ShowForScenario(); // (his round's end took the ring down)
            Status = $"{label}: hitmen";
            float gapFrom = Time.time;
            yield return NightWait(VisitorGap(), skippable: true);
            float quietFor = Time.time - gapFrom;
            if (quietFor < FirstCarFloor && !_rickyHit) yield return NightWait(FirstCarFloor - quietFor, skippable: false);
            if (_rickyHit) { _betweenStep = NightStep.RickyHit; police.Cancel(); }   // (OnRickyForce cancelled it already: a no-op)
        }

        /// <summary>
        /// Steps <paramref name="inner"/> here, its nested steps flattened on one stack (as Intruder.Guarded does: no child
        /// coroutines of Unity's, which a StopCoroutine up the chain would leave running), while the night that started it is
        /// still on (<see cref="_nightToken"/>); a stop or a new night ends it at its next step. Yield instructions pass through.
        /// </summary>
        IEnumerator WhileTonight(IEnumerator inner, int token, string label)
        {
            var stack = new System.Collections.Generic.Stack<IEnumerator>();
            stack.Push(inner);
            while (stack.Count > 0)
            {
                if (token != _nightToken) { Debug.Log($"[HIS] {label}: its visitor step ends with the night"); yield break; }
                var step = stack.Peek();
                if (!step.MoveNext()) { stack.Pop(); continue; }
                if (step.Current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return step.Current;
            }
        }

        /// <summary>
        /// The safety net: a visitor who left (Gone: already hidden) is destroyed once his round is over, so nothing of him is in
        /// the scene for the gap, the camera or the waves; <paramref name="all"/>: every visitor still about (after a visitor
        /// between the waves, before the next car). Ricky and any squad man (alive or a body) stay. Not a room clear: the doors
        /// (one the player opened by hand), holes and blood are kept.
        /// </summary>
        void ClearLeftoverVisitors(string label, bool all)
        {
            int n = 0;
            foreach (var i in FindObjectsByType<IntruderT>(FindObjectsInactive.Exclude))
            {
                if (i == null || i.IsProtectee || i.Intent == Intent.Police) continue;
                if (!all && !i.HasLeft) continue;
                Destroy(i.gameObject);
                n++;
            }
            if (n > 0) Debug.Log($"[HIS] {label}: {n} visitor(s) cleared from the scene (left)");
        }

        // ───────────── Ricky (research/27 §3) ─────────────

        /// <summary>Tonight's Ricky in his hiding spot (IntruderSpawner.SpawnProtectee), watched for the player's force and his death.</summary>
        void SpawnRicky(string label)
        {
            _ricky = spawner != null ? spawner.SpawnProtectee() : null;
            if (_ricky == null) { Debug.LogWarning($"[HIS] {label}: no Ricky (no hiding spot) — the night goes on without him"); return; }
            _ricky.ForceApplied += OnRickyForce;
            _ricky.Died += OnRickyDied;
        }

        /// <summary>
        /// The player hurt Ricky (research/27 §4 "You shot Ricky"): the night stops here. A squad out (the hitmen, or the police
        /// after a bad call) is cancelled first, then frozen where it stands (Cancel and End never stop it on their own), and a
        /// visitor in the room stops too. The card follows a beat later (<see cref="RickyHitCard"/>). The first hit only: more
        /// rounds before the card can still kill him, and the card reads how he ended up.
        /// </summary>
        void OnRickyForce(IntruderT who, ForceEvent e)
        {
            if (who != _ricky || _rickyHit) return;
            _rickyHit = true;
            _rickyHitKind = e.Kind;
            Debug.Log($"[HIS] Marshal: you hit Ricky ({e.Kind} on {e.BodyPart}{(e.Killed ? ", killed" : "")})");
            var police = PoliceResponse.Instance;
            if (police != null && police.Active) { police.Cancel(); police.StandDownSquad(); }
            if (Intruder != null) StandDown(Intruder);
        }

        /// <summary>
        /// Ricky died. By the player's hand, <see cref="OnRickyForce"/> came first (EmitForce precedes Died) and has it. Otherwise
        /// during a hitman wave it was the finder: the night is lost (PoliceResponse.ProtecteeDied → WitnessKilled).
        /// </summary>
        void OnRickyDied(IntruderT who)
        {
            if (who != _ricky || _rickyHit) return;
            var police = PoliceResponse.Instance;
            if (police != null && police.Active && police.Faction == SquadFaction.Hitmen) police.ProtecteeDied();
            else Debug.LogWarning("[HIS] Marshal: Ricky died with no hitman wave out, and not by your hand");
        }

        /// <summary>
        /// "You shot Ricky" (research/27 §4): a booking with no standoff (OutcomeShow.ForceBadMode = Mugshot). THE TRIAL IS OFF
        /// if he died, BAD CALL if he lived; the charges by what you did, plus the federal count (Judge.ProtecteeShot). A beat
        /// first (the moment lands; another round of yours may still land), then he can't be hit any more.
        /// </summary>
        IEnumerator RickyHitCard(string label)
        {
            Status = $"{label}: you hit Ricky";
            yield return new WaitForSeconds(MarshalEndBeat);
            bool killed = _ricky != null && _ricky.Current == IntruderT.State.Dead;
            // A bad call on the visitor in the same round (dropped when Ricky was hit) is on the card too: his charge, his count.
            var card = Judge.ProtecteeShot(killed, _rickyHitKind, _rickyHitVisitorCall, _rickyHitVisitorGun);
            yield return BookingCard(card, (killed ? "Ricky killed" : "Ricky hurt") + (_rickyHitVisitorCall.HasValue ? ", and a bad call on the visitor" : ""), label);
        }

        /// <summary>
        /// A bad call on a visitor (Brett 2026-10-04 (a): "BAD CALL = ALWAYS ARREST in Marshal mode"): the round already stopped a
        /// beat after it (RunScenario's keepProtectee stop: he stood down where he was, no leave line, no police). The booking
        /// card from the judge's call (<see cref="Judge.VisitorBooked"/>: his charge — the gun enhancement only for a shot —
        /// the federal count, the OPR referral, the soap line).
        /// </summary>
        IEnumerator VisitorBookingCard(string label)
        {
            var rec = Record;
            var call = LastVerdict.GetValueOrDefault();
            bool gun = false;
            if (rec != null) foreach (var e in rec.Force) if (e.Kind == ForceKind.Shot && !e.Before.Dead) { gun = true; break; }
            string who = rec != null && rec.Def != null ? rec.Def.Name : "a visitor";
            Status = $"{label}: bad call on {who}";
            yield return BookingCard(Judge.VisitorBooked(call, gun), $"bad call on {who}", label);
        }

        /// <summary>
        /// Every Marshal arrest's one look (Brett 2026-10-04 (a)): no police standoff, straight to the booking
        /// (OutcomeShow.ForceBadMode = Mugshot: red/blue lights, the cruisers on the camera — none roll in while a hitman car is
        /// out — the mugshot, the red card). NightCard stands Ricky down first, so no stray round makes a second card.
        /// </summary>
        IEnumerator BookingCard(Verdict card, string how, string label)
        {
            StandDown(Intruder);                                         // (a visitor in the room stops where he is; null-safe)
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.Hide();
            LastStandoff = StandoffEnding.None;
            OutcomeShow.ForceBadMode = BadShowMode.Mugshot;              // the booking
            Debug.Log($"[HIS] Marshal: the arrest ({how}) — the booking, no police standoff");
            yield return NightCard(card, how, label);
            OutcomeShow.ForceBadMode = null;                             // (the show takes it; with no door for the show, it mustn't wait for the next bad call)
        }

        /// <summary>
        /// The night's own things go: Ricky (his handlers first, then his body — the clears that run beside this destroy him too)
        /// and the story clock (else the monitor keeps 1 AM in Scenarios mode). StopLoop and each new night; a no-op outside Marshal.
        /// </summary>
        void EndMarshalNight()
        {
            if (_ricky != null)
            {
                _ricky.ForceApplied -= OnRickyForce;
                _ricky.Died -= OnRickyDied;
                Destroy(_ricky.gameObject);
            }
            _ricky = null;
            var stray = IntruderT.Protectee;                             // (one this night didn't spawn, e.g. a test's)
            if (stray != null) Destroy(stray.gameObject);
            StoryClock.Now = null;
            // (A night stopped mid-run: its visitor step goes with it — the hook, and one already running: WhileTonight. Only a
            // hitman run reads the hook; the next night sets its own.)
            var police = PoliceResponse.Instance;
            if (police != null) police.BetweenHitmanWaves = null;
            _betweenStep = NightStep.On;
            _nightToken++;
        }

        /// <summary>The monitor's clock as each hitman wave is called (<see cref="WaveClock"/>, Brett 0.1.57: 1:15, 1:24, 1:33, 1:42,
        /// 1:51; never back: StoryClock.AdvanceTo). Only on a Marshal night.</summary>
        void OnMarshalWave(int wave)
        {
            if (mode != Mode.Marshal || StoryClock.Now == null) return;
            var police = PoliceResponse.Instance;
            if (police == null || police.Faction != SquadFaction.Hitmen) return;
            var at = WaveClock[Mathf.Clamp(wave, 1, WaveClock.Length) - 1];
            StoryClock.AdvanceTo(at.x, at.y);
        }

        /// <summary>
        /// Marshal mode: drop the night in progress and start a fresh one now (the pause menu's "Weapons check": call
        /// MarshalBriefing.Request() first, and the new night opens with the check). The room and the door confirm are kept.
        /// Nothing outside Marshal mode.
        /// </summary>
        public void RestartMarshalNight()
        {
            if (mode != Mode.Marshal) return;
            StopLoop();                                                  // (a squad out is cancelled first; Ricky goes)
            StopPreview();
            if (OutcomeShow.Instance != null) OutcomeShow.Instance.Clear();
            Sfx.StopAll();
            Subtitles.Clear();
            GameSession.ClearWorld(resetHealth: true);
            Intruder = null; Record = null;
            Debug.Log("[HIS] Marshal: the night starts over");
            _loop = StartLoop();
        }

        /// <summary>Take the night's card down (its time is up, or the night was stopped mid-card: StopLoop).</summary>
        static void HideMarshalCard() => Notice.Hide(MarshalCardKey);
    }
}
