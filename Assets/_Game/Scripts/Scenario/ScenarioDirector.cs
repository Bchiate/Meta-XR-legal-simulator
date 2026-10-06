using System.Collections;
using HIS.Core;
using HIS.Intruder;
using HIS.Outcome;
using HIS.UI;
using HIS.Weapons;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Scenario
{
    /// <summary>
    /// Runs the game in Scenario mode (PLAN.md M2b): one incident at a time — an intruder with a hidden intent comes
    /// through the door, the player handles it (yell, aim, let him go, shoot or stab), the incident ends (dead, left,
    /// in custody, player dead, timeout), the lean <see cref="Judge"/> rules on what the player could see, and the
    /// <see cref="OutcomeShow"/> plays confetti or police lights. Then the next one. Sandbox mode hands the room back to
    /// the endless attacker spawner. Toggle: hold BOTH thumbsticks clicked in together for 0.6 s.
    /// Once the verdict banner is up, Y skips the rest of the show and goes straight to the next scenario (and after the
    /// banner, Y cuts the breather before the next one short: Brett 0.1.37)
    /// (Brett 0.1.8; 0.1.19: "no way to skip" — he pressed B on the gun hand, which only reloaded). A bad-call show whose banner waits for the
    /// mugshot can be skipped a second in (<see cref="OutcomeShow.CanSkip"/>).
    /// The pause menu's "Preview bad call" (<see cref="PreviewBadCall"/>) drops the current incident and plays the bad-call
    /// show on the spot with a sample verdict, then carries on.
    /// Marshal mode (research/27, ScenarioDirector.Marshal.cs) runs a US Marshal night instead of the deck: the handover when
    /// due, Ricky, five hitman waves on the police machine with two judged visitors between them (Brett 0.1.57), the 2 AM
    /// relief, then the night's card.
    /// It's reached from the pause menu's Mode row (saved on the headset); the
    /// chord still toggles Scenarios and Sandbox as on main, and from Marshal goes back to Scenarios (Brett 2026-10-03:
    /// "lets work on safely adding the story" — the normal game's controls don't change).
    /// </summary>
    public partial class ScenarioDirector : MonoBehaviour
    {
        /// <summary>Append only (saved settings, the chord). Marshal: a US Marshal night at a safe house (research/27).</summary>
        public enum Mode { Scenarios, Sandbox, Marshal }

        public static ScenarioDirector Instance { get; private set; }

        [SerializeField] Mode mode = Mode.Scenarios;
        [SerializeField] IntruderSpawner spawner;
        [SerializeField] float firstDelay = 4f, betweenDelay = 3f, timeout = 120f;
        [Tooltip("After a B/Y skip: the room is already cleared, so only a beat before the next intruder.")]
        [SerializeField] float skipDelay = 0.5f;

        public Mode CurrentMode => mode;
        public ScenarioRecord Record { get; private set; }
        public IntruderT Intruder { get; private set; }
        public Verdict? LastVerdict { get; private set; }
        /// <summary>How the last police standoff ended (None: they didn't come).</summary>
        public StandoffEnding LastStandoff { get; private set; }
        public string Status { get; private set; } = "starting";

        /// <summary>The aggressive gunman: kicks in, raises the gun on you, fires if you don't stop him. Sandbox deals him too.</summary>
        static readonly ScenarioDef ArmedRobber = new()
        {
            Name = "Armed robber", Intent = Intent.Gunman, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun,
            Identity = Identity.Stranger, Morale = 1.25f, WhoItWas = "An armed robber who kicked your door in and pulled a gun on you",
        };
        /// <summary>Pick #1's false alarm: a knock, "Delivery!", his steps leave — nobody comes in. Never in the Deck.</summary>
        static readonly ScenarioDef Delivery = new()
        {
            Name = "False alarm (delivery)", Intent = Intent.Innocent, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.None,
            Identity = Identity.Stranger, WhoItWas = "A delivery driver who knocked and left — nobody was breaking in",
        };
        // (declared before the Deck: static initializers run in order)

        // The v1 deck (PLAN.md §1): armed attacker, burglar who runs (sometimes still holding the knife), surrenders,
        // wrong house. Weighted by repetition; never the same one twice in a row.
        static readonly ScenarioDef[] Deck =
        {
            new() { Name = "Home invader", Intent = Intent.MeleeAttacker, Entry = EntryType.ForcedKick, Weapon = WeaponKind.None,
                    Identity = Identity.Stranger, WhoItWas = "A stranger who kicked your door in and came at you" },
            new() { Name = "Knife attacker", Intent = Intent.KnifeAttacker, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Knife,
                    Identity = Identity.Stranger, WhoItWas = "An intruder with a knife, coming for you" },
            new() { Name = "Burglar who runs", Intent = Intent.Burglar, Entry = EntryType.ForcedKick, Weapon = WeaponKind.None,
                    Identity = Identity.Stranger, WhoItWas = "A burglar who only wanted your stuff — he ran when challenged" },
            new() { Name = "Burglar with a knife", Intent = Intent.Burglar, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Knife,
                    Identity = Identity.Stranger, WhoItWas = "A burglar holding a knife — he ran, he never came at you" },
            new() { Name = "Surrenders", Intent = Intent.Surrenderer, Entry = EntryType.ForcedKick, Weapon = WeaponKind.None,
                    Identity = Identity.Stranger, WhoItWas = "A burglar who gave up the moment he was challenged" },
            new() { Name = "Surrenders (knife)", Intent = Intent.Surrenderer, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Knife,
                    Identity = Identity.Stranger, WhoItWas = "An intruder with a knife who dropped it and gave up" },
            new() { Name = "Wrong house", Intent = Intent.Innocent, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.None,
                    Identity = Identity.Neighbor, WhoItWas = "Mike, your drunk neighbour — wrong house" },
            // M3a: the gunman (IntruderGunman). m0 is his hidden starting morale (research/08 §2c): over 1 he never gives in.
            ArmedRobber,
            new() { Name = "Robber drops the gun", Intent = Intent.Gunman, Entry = EntryType.OpenedUnlocked, Weapon = WeaponKind.Gun,
                    Identity = Identity.Stranger, Morale = 0.15f, WhoItWas = "An armed robber who picked your lock — he'd have given up the gun if told" },
            new() { Name = "Robber runs with the gun", Intent = Intent.Gunman, Entry = EntryType.ForcedKick, Weapon = WeaponKind.Gun,
                    Identity = Identity.Stranger, Morale = 0.9f, RunsWhenChallenged = true,
                    WhoItWas = "A robber with a gun who only wanted out — he ran the moment he was challenged" },
        };


        int _lastIndex = -1, _round, _sinceFight, _lastFalseAlarm = -99;
        bool _lastLeft;
        EntryStyle _lastStyle;
        readonly Intent?[] _intents = new Intent?[2]; // the last two dealt, newest first

        ScenarioDef Remember(ScenarioDef d) { _intents[1] = _intents[0]; _intents[0] = d.Intent; return d; }

        /// <summary>Tests / dev: the next scenario to play, by name (consumed once).</summary>
        public static string ForcedNext;
        /// <summary>
        /// Brett's pick #1 (2026-09-24): a rolled entry style per round (<see cref="EntryStyle"/>), a random wait at the door
        /// and between rounds, pacing (a fight at least every 3rd round, never two rounds in a row that end with him
        /// leaving) and the rare false alarm. Off = the 0.1.17 door (tests run with it off unless they test it).
        /// </summary>
        public static bool VariedEntries = true;
        /// <summary>Tests: this round's entry style (consumed once).</summary>
        public static EntryStyle? ForcedStyle;
        /// <summary>Tests: every round's entry style until cleared (the door-shot sweep pins Classic).</summary>
        public static EntryStyle? PinnedStyle;
        /// <summary>Pause menu: the rare false alarm (a knock and nobody comes in; at most 1 round in 6).</summary>
        public static bool FalseAlarms = true;
        /// <summary>Tests: don't start the next scenario until <see cref="ForcedNext"/> names it.</summary>
        public static bool WaitForForced;
        /// <summary>Tests: this round's door (0 = front, 1 = second; consumed once). Otherwise rolled when there are two.</summary>
        public static int? ForcedDoor;
        int _lastDoor, _sameDoorRun;
        public static System.Collections.Generic.IEnumerable<ScenarioDef> AllScenarios => Deck;
        public int Round => _round;
        /// <summary>Scenarios judged so far (tests wait on this).</summary>
        public int Completed { get; private set; }

        /// <summary>Tests: as if the player yelled it.</summary>
        public void Inject(Command c) { Debug.Log($"[HIS] Command (injected): {c}"); OnCommand(c, Vector3.zero); }
        bool _playerDied;
        bool _skipped, _skipArmed, _skipRequested;
        Coroutine _loop, _preview;

        /// <summary>A preview of the bad-call show is running (pause menu).</summary>
        public bool Previewing { get; private set; }
        /// <summary>Tests: previews finished, and whether the last one was skipped.</summary>
        public int PreviewsDone { get; private set; }
        public bool LastPreviewSkipped { get; private set; }

        /// <summary>Tests / tools: as if B/Y was pressed (taken the next frame the show can be skipped).</summary>
        public void RequestSkip() => _skipRequested = true;

        void Awake()
        {
            Instance = this;
            if (spawner == null) spawner = GetComponent<IntruderSpawner>();
            // Sandbox: about one attacker in three comes in with a gun (the rest are the fists-and-fury home invader).
            if (spawner != null) spawner.SandboxRole = () => Random.value < 0.35f ? ArmedRobber : null;
            // The pause menu's pick, kept on the headset across launches. Never in the Editor: the tests play the scene's own
            // mode, whatever a Play Mode session last picked.
            if (!Application.isEditor && HasSavedMode) mode = SavedMode;
            ApplyMode();
        }

        void OnEnable()
        {
            CommandSystem.Issued += OnCommand;
            GameSession.SessionReset += OnSessionReset;
            PoliceResponse.WaveCalled += OnMarshalWave;                  // a Marshal night's clock (no-op otherwise)
        }

        void OnDisable()
        {
            CommandSystem.Issued -= OnCommand;
            GameSession.SessionReset -= OnSessionReset;
            PoliceResponse.WaveCalled -= OnMarshalWave;
        }

        void Start()
        {
            if (PoliceResponse.Instance == null) gameObject.AddComponent<PoliceResponse>(); // the standoff after a shooting (research/22)
            CommandSystem.ContextResolver = ContextCommand;
            if (PlayerState.Instance != null) PlayerState.Instance.Died += () => _playerDied = true;
            if (mode != Mode.Sandbox) _loop = StartLoop();
        }

        /// <summary>The mode's round coroutine: the scenario deck, or a Marshal night (research/27). Not for Sandbox.</summary>
        Coroutine StartLoop() => StartCoroutine(mode == Mode.Marshal ? MarshalNight() : Loop());

        void ApplyMode()
        {
            if (spawner != null) spawner.AutoSpawn = mode == Mode.Sandbox;
            // One body at a time outside Sandbox (a Marshal night too: three waves of bodies were the GPU risk, research/27 §6).
            CorpseRegistry.MaxCorpses = mode == Mode.Sandbox ? 2 : 1;
            // Who the judge weighs the player as (research/27 §4): a Marshal has no home presumption and yells "U.S. Marshals!";
            // Scenarios and Sandbox stay the homeowner, every rule as before.
            Judge.Role = mode == Mode.Marshal ? Role.Marshal : Role.Homeowner;
        }

        /// <summary>Switch modes (dev chord / later the main menu). Clears the room and starts over.</summary>
        public void SetMode(Mode m)
        {
            if (m == mode) return;
            bool leftMarshal = mode == Mode.Marshal;
            mode = m;
            StopLoop();                                                  // (a standoff in progress is cancelled first)
            StopPreview();
            if (OutcomeShow.Instance != null) OutcomeShow.Instance.Clear();
            Sfx.StopAll(); // the old intruder's lines and the siren don't follow you into the other mode
            GameSession.ClearWorld(resetHealth: true);
            // Out of Marshal: the hitmen's three vehicles go (the next police call builds main's three, as a session's first
            // call does), so police play after a Marshal night carries none of them (research/27 §6 step 6; Brett 2026-10-03).
            if (leftMarshal && HIS.Security.PoliceCars.Instance != null) HIS.Security.PoliceCars.Instance.DropHitmanCars();
            // A "you shot Ricky" booking whose show never played (no door found for it) must not turn the next bad call's show
            // into a mugshot.
            if (leftMarshal) OutcomeShow.ForceBadMode = null;
            ApplyMode();
            // (Not saved here: only the pause menu's Mode row keeps a mode across launches, GamePause.ApplyModePick. The chord
            // is main's dev toggle, and on main every launch starts in the scene's mode; the chord out of Marshal only puts the
            // save back to Scenarios, Update.)
            Notice.Show("mode", mode == Mode.Scenarios ? "<b>SCENARIOS</b>\n<size=70%>Something's coming. Handle it.</size>"
                              : mode == Mode.Marshal ? "<b>U.S. MARSHAL</b>\n<size=70%>Safe house. Relief's at two.</size>"
                              : "<b>SANDBOX</b>\n<size=70%>Endless attackers. No verdicts.</size>", 10);
            CancelInvoke(nameof(HideModeNotice)); Invoke(nameof(HideModeNotice), 2.5f);
            if (mode == Mode.Marshal) s_marshalCardAt = Time.time;       // (the weapons check won't repeat it: MarshalCardJustShown)
            // Brett 0.1.57 (the lead's A1): into Marshal, the next night opens with Ortiz's handover (the story's intro), the drill
            // only when it's due (MarshalBriefing.EnteredMarshal: on the headset; the Editor only under its test hook).
            if (mode == Mode.Marshal) MarshalBriefing.EnteredMarshal();
            if (mode != Mode.Sandbox) _loop = StartLoop();
            else if (spawner != null) spawner.ResetSpawner();
            Debug.Log($"[HIS] Mode: {mode}");
        }

        const string ModePrefKey = "his.mode";
        static int _savedMode = -1;
        /// <summary>
        /// The mode the pause menu's Mode row applied (GamePause.ApplyModePick, on resume), saved in PlayerPrefs on the headset
        /// like the bad-call show (BadCallShow.Mode) and played from the next launch. Read and written on the device only (see
        /// Awake). The chord writes it only on its way out of Marshal (back to Scenarios): until the Mode row is used, every launch
        /// starts in the scene's mode, as on main.
        /// </summary>
        public static Mode SavedMode
        {
            get { if (_savedMode < 0) _savedMode = Mathf.Clamp(PlayerPrefs.GetInt(ModePrefKey, 0), 0, ModeCount - 1); return (Mode)_savedMode; }
            set { _savedMode = Mathf.Clamp((int)value, 0, ModeCount - 1); PlayerPrefs.SetInt(ModePrefKey, _savedMode); PlayerPrefs.Save(); }
        }
        /// <summary>A mode was saved on this device (else the scene's own mode plays).</summary>
        public static bool HasSavedMode => PlayerPrefs.HasKey(ModePrefKey);
        /// <summary>How many modes there are (the pause menu's Mode row cycles through them).</summary>
        public const int ModeCount = 3;
        /// <summary>The pause menu's Mode row labels, by <see cref="Mode"/>.</summary>
        public static readonly string[] ModeNames = { "Scenarios", "Sandbox", "Marshal" };

        void HideModeNotice() => Notice.Hide("mode");

        const float ChordHold = 0.6f;
        float _chordHeld;
        bool _chordFired;
        void Update()
        {
            // Mode toggle: BOTH sticks held in together for 0.6 s. A stick click racks/pumps, so a quick click (or a double
            // click of one stick) must never count. Not while paused or in a menu; release both to arm it again.
            bool both = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)
                        && OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (!both || !InputGate.GameplayActive || Time.timeScale <= 0f) { _chordHeld = 0f; _chordFired = false; return; }
            if (_chordFired) return;
            _chordHeld += Time.unscaledDeltaTime;
            if (_chordHeld < ChordHold) return;
            _chordFired = true;
            // As main: Scenarios ↔ Sandbox, and Marshal → Scenarios. Marshal is the pause menu's Mode row's only (a chord out of
            // Sandbox starting a hitman night would break the toggle Brett uses). The chord out of Marshal also takes back the
            // row's saved Marshal (the one save the chord makes), so the next launch starts in Scenarios as main's does, not
            // in a night he chorded out of. Scenarios ↔ Sandbox still never saves.
            if (mode == Mode.Marshal && !Application.isEditor) SavedMode = Mode.Scenarios;
            SetMode(mode == Mode.Scenarios ? Mode.Sandbox : Mode.Scenarios);
        }

        IEnumerator Loop()
        {
            Status = "waiting for room";
            while (spawner == null || !spawner.NavMeshReady) yield return null;
            Status = "waiting for the door confirm";
            while (!spawner.DoorConfirmed) yield return null;
            yield return new WaitForSeconds(firstDelay);
            while (true)
            {
                while (WaitForForced && string.IsNullOrEmpty(ForcedNext)) yield return null;
                if (!spawner.DoorConfirmed)
                {
                    // The door picker is open again (hold-Menu restart): no intruder until the player has chosen.
                    Status = "waiting for the door confirm";
                    while (!spawner.DoorConfirmed) yield return null;
                    yield return new WaitForSeconds(firstDelay);
                }
                yield return RunScenario(NextDef());
                // Pick #1: a varied breather between rounds (tests that force scenarios keep the fixed one). Y cuts it short
                // (Brett 0.1.37: "after a good call if i hit y it doesnt restart": the banner had gone, and the next man was
                // still up to 10 s away). Scaled time, like the WaitForSeconds it replaces: a pause freezes it.
                float breather = _skipped ? skipDelay : VariedEntries && !WaitForForced ? Random.Range(3f, 10f) : betweenDelay;
                _skipArmed = false;
                for (float t = 0f; t < breather; t += Time.deltaTime)
                {
                    if (!_skipped && SkipPressed(null)) { Debug.Log($"[HIS] Next round now (Y, {breather - t:F1} s early)"); break; }
                    yield return null;
                }
                _skipped = false;
            }
        }

        ScenarioDef NextDef()
        {
            if (!string.IsNullOrEmpty(ForcedNext))
            {
                if (ForcedNext == Delivery.Name) { ForcedNext = null; return Remember(Delivery); }
                for (int k = 0; k < Deck.Length; k++)
                    if (Deck[k].Name == ForcedNext) { ForcedNext = null; _lastIndex = k; return Remember(Deck[k]); }
                Debug.LogWarning($"[HIS] No scenario named '{ForcedNext}'"); ForcedNext = null;
            }
            // Never the same entry twice running, never the same kind of man (intent) three times running (research/08 §2a).
            if (VariedEntries && FalseAlarms && _round - _lastFalseAlarm >= 6 && Random.value < 0.18f)
            { _lastFalseAlarm = _round + 1; return Remember(Delivery); }
            // Pacing (pick #1): after two rounds without a fighter the next one fights; after he left, the next one won't.
            bool Allowed(ScenarioDef d) => !VariedEntries || ((_sinceFight < 2 || IsFighter(d)) && !(_lastLeft && IsLeaver(d)));
            int i, tries = 0;
            do { i = Random.Range(0, Deck.Length); }
            while (++tries < 80 && ((Deck.Length > 1 && i == _lastIndex) || (_intents[0] == Deck[i].Intent && _intents[1] == Deck[i].Intent)
                                    || !Allowed(Deck[i])));
            _sinceFight = IsFighter(Deck[i]) ? 0 : _sinceFight + 1;
            _lastIndex = i;
            return Remember(Deck[i]);
        }

        static bool IsFighter(ScenarioDef d) =>
            d.Intent is Intent.MeleeAttacker or Intent.KnifeAttacker || (d.Intent == Intent.Gunman && d.Morale > 1f && !d.RunsWhenChallenged);
        static bool IsLeaver(ScenarioDef d) => d.Intent is Intent.Burglar or Intent.Surrenderer || d.RunsWhenChallenged;

        /// <summary>This round's way through the door: never the same style twice running; burglars may pry it quietly.</summary>
        EntryStyle RollStyle(ScenarioDef def)
        {
            if (def.Style.HasValue) return def.Style.Value;              // a Marshal night's visitor (the courier: research/27 §6 step 8)
            if (def == Delivery) return EntryStyle.FalseAlarm;
            if (ForcedStyle.HasValue) { var f = ForcedStyle.Value; ForcedStyle = null; return _lastStyle = f; }
            if (PinnedStyle.HasValue) return _lastStyle = PinnedStyle.Value;
            if (def.Entry != EntryType.ForcedKick) return EntryStyle.Classic;   // keys / a picked lock / drunk Mike: their own entry
            bool sneak = def.Intent is Intent.Burglar or Intent.Surrenderer;
            EntryStyle s; int tries = 0;
            do
            {
                float r = Random.value;
                s = sneak ? (r < 0.35f ? EntryStyle.QuietPry : r < 0.55f ? EntryStyle.KnockThenKick : r < 0.75f ? EntryStyle.Classic
                              : r < 0.9f ? EntryStyle.WeakGuy : EntryStyle.Rush)
                          : (r < 0.3f ? EntryStyle.Rush : r < 0.55f ? EntryStyle.KnockThenKick : r < 0.8f ? EntryStyle.Classic : EntryStyle.WeakGuy);
            } while (s == _lastStyle && ++tries < 10);
            return _lastStyle = s;
        }

        /// <summary>
        /// Which door this round (Brett 0.1.20: "a second door"): with two, a coin flip leaning a little to the front door, and
        /// never the same door three rounds running, so neither door can be written off.
        /// </summary>
        int RollDoor()
        {
            int doors = spawner != null ? spawner.DoorCount : 1;
            int d;
            if (ForcedDoor.HasValue) { d = ForcedDoor.Value; ForcedDoor = null; }
            else if (doors < 2) d = 0;
            else if (_sameDoorRun >= 2) d = 1 - _lastDoor;
            else d = Random.value < 0.55f ? 0 : 1;
            _sameDoorRun = d == _lastDoor ? _sameDoorRun + 1 : 1;
            _lastDoor = d;
            if (doors > 1) Debug.Log($"[HIS] Round door: {(d == 0 ? "front" : "second")}");
            return d;
        }

        /// <summary>
        /// One judged round: a clean room, <paramref name="def"/>'s man at a door, the wait for an ending, the verdict (and the
        /// police after a bad call), the show.
        /// </summary>
        /// <param name="keepProtectee">
        /// A Marshal night's visitor (research/27 §3, §6 step 8): Ricky survives this round's clears; the round is dropped,
        /// unjudged, the moment the player hurts Ricky (the night's card takes over); a call left alone gets no show at all —
        /// no banner, nothing (Brett 0.1.58, replaying the intro: "when he leaves it shouldnt give the good call banner the mob
        /// people should come"; it was research/27 Q3's quiet banner until then): the night just goes on, and the 2 AM relief's
        /// good call has WITNESS DELIVERED for its show. A bad call on the visitor stops
        /// the round <see cref="MarshalEndBeat"/> s later and returns with no police and no show: the night's booking follows
        /// (Brett 2026-10-04 (a): every bad call on a Marshal night is the arrest). False: exactly the scenario round.
        /// </param>
        /// <param name="door">This round's door (null: rolled, <see cref="RollDoor"/>). A Marshal night's visitors come to the front.</param>
        /// <param name="clearWorld">
        /// False: a Marshal night's visitor between hitman waves (Brett 0.1.57: the courier after wave 1, Sal after wave 3). The
        /// round starts in the room as the last wave left it — no room clear, so the hitmen's bodies in the room, the blood, the
        /// holes and the doors stay, and your wounds carry on (no heal); left alone he gets no show (keepProtectee), so nothing
        /// raises OutcomeShow.Cleared and the hitmen's cars stay parked, the run out. True (the default): every round as before.
        /// </param>
        IEnumerator RunScenario(ScenarioDef def, bool keepProtectee = false, int? door = null, bool clearWorld = true)
        {
            if (clearWorld) GameSession.ClearWorld(resetHealth: true, keepProtectee);
            _round++;
            _playerDied = false;
            _skipped = false;
            var rec = new ScenarioRecord { Def = def, StartTime = Time.time };
            Record = rec;
            int shotsAtStart = Firearm.TotalShotsFired;                  // the dispatcher's line: was a round fired this round?
            Intruder = spawner.SpawnScenario(def, door ?? RollDoor());
            if (Intruder == null) { Status = "waiting for a door"; yield return new WaitForSeconds(2f); yield break; }
            // A Marshal night's deputy (research/27 §6 step 4): a light windbreaker and the gold badge on his belt, so he reads as
            // neither hitman tier on the camera. Dressed as an officer is, right after the spawn. (No deck scenario is an Officer.)
            if (def.Identity == Identity.Officer) Intruder.DressAsDeputy();
            // (A def's own style plays even with varied entries off: the courier is a false alarm in the tests too.)
            if (VariedEntries || def.Style.HasValue) { Intruder.Style = RollStyle(def); Debug.Log($"[HIS] Entry style: {Intruder.Style}"); }
            Status = $"#{_round} {def.Name}";
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.ShowForScenario(); // M1a safety: "Stand in the ring"
            bool judged = false;
            float badAt = -1f;                                           // a Marshal night's visitor: when the first bad force landed
            Intruder.ForceApplied += (who, e) =>
            {
                if (judged) { Debug.Log($"[HIS] Force {e.Kind} after the verdict: not judged"); return; }
                rec.Force.Add(e);
                var (tier, close, reason) = Judge.JudgeForce(e);
                if (keepProtectee && tier == Tier.Bad && badAt < 0f && !e.Before.Dead) badAt = Time.time;
                Debug.Log($"[HIS] Force {e.Kind} {e.Damage:F0} on {e.BodyPart}{(e.Killed ? " (killed)" : "")} → {tier}{(close ? " close" : "")}: {reason}");
            };
            Debug.Log($"[HIS] Scenario #{_round}: {def.Name}");

            while (true)
            {
                if (keepProtectee && _rickyHit) break;                    // you hurt Ricky: the night's card, not this round's
                if (_playerDied) { rec.End = EndReason.PlayerDied; break; }
                if (Intruder == null) { rec.End = EndReason.TimedOut; break; }
                // A Marshal night's visitor hurt by you: a beat for the moment to land, then everything stops (no leave line,
                // no walk-out) and the night's booking follows (Brett 2026-10-04 (a)). Killed outright: the Dead line below.
                if (keepProtectee && badAt >= 0f && Intruder.Current != IntruderT.State.Dead && Time.time - badAt >= MarshalEndBeat)
                { rec.End = EndReason.Stopped; break; }
                if (Intruder.Current == IntruderT.State.Dead) { yield return new WaitForSeconds(keepProtectee ? MarshalEndBeat : 2.5f); rec.End = EndReason.Killed; break; }
                if (Intruder.HasLeft) { rec.End = EndReason.Left; break; }
                if (Intruder.InCustody) { rec.End = EndReason.Custody; break; }
                // The 2 AM relief (research/27 §1): "Relief's here. Let's get him moving." — and a few seconds on, the night is done.
                if (def.RelievedAfter > 0f && Intruder.GreetedAt >= 0f && Time.time - Intruder.GreetedAt >= def.RelievedAfter)
                { rec.End = EndReason.Relieved; break; }
                if (Time.time - rec.StartTime > timeout) { rec.End = EndReason.TimedOut; break; }
                yield return null;
            }
            if (keepProtectee && _rickyHit)
            {
                // Dropped unjudged: the night's "you shot Ricky" card plays instead (ScenarioDirector.Marshal). He stops where he is.
                // A bad call on the visitor this round (the beat before its stop) still goes on that card (RickyHitCard: his
                // charge beside Ricky's; Brett 2026-10-04 (a): the booking with the right charge).
                judged = true;
                _rickyHitVisitorCall = null;
                if (badAt >= 0f)
                {
                    rec.EndTime = Time.time;
                    rec.End = Intruder != null && Intruder.Current == IntruderT.State.Dead ? EndReason.Killed : EndReason.Stopped;
                    if (Intruder != null) rec.LastFacts = Intruder.Facts;
                    var call = Judge.Evaluate(rec);
                    if (call.Tier == Tier.Bad)
                    {
                        _rickyHitVisitorCall = call;
                        _rickyHitVisitorGun = false;
                        foreach (var e in rec.Force) if (e.Kind == ForceKind.Shot && !e.Before.Dead) { _rickyHitVisitorGun = true; break; }
                        Debug.Log($"[HIS] Scenario #{_round} {def.Name}: a bad call on him before Ricky was hit ({call.Charge}) — on Ricky's card too");
                    }
                }
                if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.Hide();
                StandDown(Intruder);
                Debug.Log($"[HIS] Scenario #{_round} {def.Name}: dropped (Ricky was hit)");
                yield break;
            }
            rec.EndTime = Time.time;
            _lastLeft = rec.End == EndReason.Left;
            judged = true;
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.Hide(); // the show gets the floor
            if (Intruder != null) rec.LastFacts = Intruder.Facts;
            StandDown(Intruder);
            rec.PlayerHurt = PlayerState.Instance != null && PlayerState.Instance.Hurt; // (a limb wound too; the judge doesn't read it)
            var verdict = Judge.Evaluate(rec);
            LastVerdict = verdict;
            Completed++;
            Status = $"#{_round} {def.Name}: {verdict.Headline}";
            Debug.Log($"[HIS] VERDICT #{_round} {def.Name}: {verdict.Tier}{(verdict.CloseCall ? " (close call)" : "")} — {verdict.Reason} | end {rec.End}, force events {rec.Force.Count}");

            // (Never on a Marshal night: the police don't come to a safe house — Brett 2026-10-04 (a). Its visitors never end in
            // custody; this is the guard if one ever did.)
            if (rec.End == EndReason.Custody && !keepProtectee) yield return PoliceTakeHim();

            // A Marshal night's bad call on a visitor (Brett 2026-10-04 (a)): never the police standoff — no radio, no waves. The
            // round ends here with no show; the night's booking (ScenarioDirector.Marshal VisitorBookingCard) is the card.
            if (keepProtectee && verdict.Tier == Tier.Bad)
            {
                LastStandoff = StandoffEnding.None;
                Status = $"#{_round} {def.Name}: the booking";
                yield break;
            }

            // Brett 0.1.25: no verdict up front after a shooting — the police come first (every bad call, some good ones), and
            // the call is revealed at the END: booked (the mugshot, the charges, the sentence), shot by police, or released.
            var shown = verdict;
            LastStandoff = StandoffEnding.None;
            if (PoliceResponse.ShouldRespond(verdict, rec))
            {
                Status = $"#{_round} police responding";
                var police = PoliceResponse.Instance;
                // The dispatcher says what happened: "Shots fired" only if a round was fired (a stabbing or a beating is
                // called in as one: no "Shots fired" when nothing was fired).
                // The card's firearm years only for a bad shooting (Judge.BadShot), not for any round fired.
                yield return police.Run(verdict, SquadFaction.Police, ReportedForce(rec, Firearm.TotalShotsFired > shotsAtStart), Judge.BadShot(rec));
                LastStandoff = police.Ending;
                if (police.Ending == StandoffEnding.Cancelled) yield break;
                shown = police.EndCard;
                if (police.Ending == StandoffEnding.Arrested) HIS.Outcome.OutcomeShow.ForceBadMode = HIS.Outcome.BadShowMode.Mugshot; // the booking
                Status = $"#{_round} {def.Name}: {shown.Headline} ({police.Ending})";
            }

            int showDoor = LastStandoff != StandoffEnding.None || Intruder == null ? 0 : Intruder.DoorIndex; // (police: the front door)
            // A Marshal night's visitor left alone: no show at all, mid-shift (Brett 0.1.58, replaying the intro: "when he leaves it
            // shouldnt give the good call banner the mob people should come" — research/27 Q3's quiet banner is gone). The verdict
            // is still recorded (LastVerdict, Completed: AfterVisitor reads them, the night's tally counts him) and the night goes
            // on — the gap, the next car; the relief's good call is followed by WITNESS DELIVERED. Nothing here clears the room or
            // raises OutcomeShow.Cleared, so between the waves (clearWorld false) the bodies, the blood and the hitmen's parked
            // cars stay as they are. Anything else (you died: YouDied; a Scenarios round): the full show, as every round.
            if (keepProtectee && LastStandoff == StandoffEnding.None && (shown.Tier is Tier.Perfect or Tier.Good))
            {
                Debug.Log($"[HIS] Scenario #{_round} {def.Name}: no show (left alone) — the night goes on");
                yield break;
            }
            yield return PlayShow(shown, showDoor, $"#{_round} {def.Name}", keepProtectee: keepProtectee);
        }

        /// <summary>
        /// What the police dispatcher is told happened: a shooting if any round was fired this round (<paramref name="fired"/>,
        /// a miss included), else a stabbing (a stab or slash on record), else an assault (a blow); nothing on record: a shooting
        /// (the line every caller had before).
        /// </summary>
        static ForceKind ReportedForce(ScenarioRecord rec, bool fired)
        {
            if (fired || rec == null) return ForceKind.Shot;
            bool blade = false, blow = false;
            foreach (var e in rec.Force)
            {
                if (e.Kind == ForceKind.Shot) return ForceKind.Shot;
                if (e.Kind is ForceKind.Stab or ForceKind.Slash) blade = true;
                else if (e.Kind == ForceKind.Punch) blow = true;
            }
            return blade ? ForceKind.Stab : blow ? ForceKind.Punch : ForceKind.Shot;
        }

        /// <summary>
        /// The outcome show for <paramref name="shown"/> at door <paramref name="showDoor"/>, then the banner lingers 1.5 s, longer
        /// after a mugshot so it's up ≥ 4.5 s (scaled time: pause freezes it). A B/Y skip ends it early and clears the room now
        /// (_skipped). Shared by the scenario round and the Marshal night's card (research/27 §6 step 8).
        /// </summary>
        /// <param name="what">The round's label for the skip log line ("#3 Knife attacker", "Marshal night 2").</param>
        /// <param name="quiet">The banner only (OutcomeShow.Play's quiet). Nothing passes it since Brett 0.1.58: the Marshal
        /// visitor left alone that had it (research/27 Q3) gets no show at all now (RunScenario).</param>
        /// <param name="keepProtectee">A skip's clear keeps Ricky (a Marshal visitor's show, mid-night: you died in his round).</param>
        IEnumerator PlayShow(Verdict shown, int showDoor, string what, bool quiet = false, bool keepProtectee = false)
        {
            float length = 6f;
            var show = OutcomeShow.Instance;
            bool played = false;
            if (show != null && spawner.TryGetDoor(showDoor, out var door, out var outward)) { length = show.Play(shown, door, outward, quiet); played = true; }

            // The show, then the banner lingers 1.5 s, longer after a mugshot so it's up ≥ 4.5 s (scaled time: pause freezes
            // it). A skip ends it here.
            _skipArmed = false; _skipRequested = false;
            for (float wait = length + (played ? show.Linger : 1.5f); wait > 0f; wait -= Time.deltaTime)
            {
                if (SkipPressed(played ? show : null)) { _skipped = true; break; }
                if (keepProtectee && _rickyHit) break;                    // a visitor's banner: you hurt Ricky, his card now
                yield return null;
            }
            if (OutcomeShow.Instance != null) OutcomeShow.Instance.Clear();
            if (_skipped)
            {
                // Same cleanup as the natural end, just now: the cheer/radio tails and captions go, the room empties.
                Sfx.StopAll();
                Subtitles.Clear();
                GameSession.ClearWorld(resetHealth: true, keepProtectee);
                Debug.Log($"[HIS] Outcome show skipped ({what})");
            }
        }

        // ───────────────────────────── preview (pause menu) ─────────────────────────────

        /// <summary>
        /// Play the bad-call show right now with <see cref="OutcomeShow.SampleBadVerdict"/>: the incident in progress (if
        /// any) is dropped and the room cleared, nothing spawns during it, B/Y skips it, and afterwards the scenario loop
        /// starts over (Sandbox: the spawner resumes).
        /// </summary>
        public void PreviewBadCall()
        {
            var show = OutcomeShow.Instance;
            if (show == null) { Debug.LogWarning("[HIS] Preview: no OutcomeShow"); return; }
            StopLoop();                                                  // (a standoff in progress is cancelled first)
            StopPreview();
            show.Clear();
            Sfx.StopAll();
            Subtitles.Clear();
            if (HIS.Room.SafeSpot.Instance != null) HIS.Room.SafeSpot.Instance.Hide();
            GameSession.ClearWorld(resetHealth: true);
            Intruder = null; Record = null;
            if (spawner != null) spawner.Pause(true);
            _preview = StartCoroutine(Preview(show));
        }

        IEnumerator Preview(OutcomeShow show)
        {
            Previewing = true;
            Status = $"preview: {HIS.Outcome.BadCallShow.Mode} bad call";
            if (spawner == null || !spawner.TryGetDoor(out var door, out var outward))
            {
                var cam = Camera.main;
                var eye = cam != null ? cam.transform.position : new Vector3(0f, 1.6f, 0f);
                var fwd = cam != null ? cam.transform.forward : Vector3.forward;
                fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                door = eye - fwd * 3f; door.y = eye.y - 1f;
                outward = -fwd;
            }
            float length = show.Play(OutcomeShow.SampleBadVerdict, door, outward);
            Debug.Log($"[HIS] Preview bad call: {HIS.Outcome.BadCallShow.Mode}, {length:F1} s");
            _skipArmed = false; _skipRequested = false;
            bool skipped = false;
            for (float wait = length + show.Linger; wait > 0f; wait -= Time.deltaTime)
            {
                if (SkipPressed(show)) { skipped = true; break; }
                yield return null;
            }
            show.Clear();
            if (skipped) { Sfx.StopAll(); Subtitles.Clear(); }
            GameSession.ClearWorld(resetHealth: true); // also lets the spawner go again
            Previewing = false;
            LastPreviewSkipped = skipped;
            PreviewsDone++;
            _preview = null;
            Debug.Log($"[HIS] Preview bad call {(skipped ? "skipped" : "finished")}");
            if (mode != Mode.Sandbox && _loop == null) _loop = StartLoop();
        }

        /// <summary>
        /// Drop the round in progress (mode switch, preview, hold-Menu reset). A police standoff it's waiting on is cancelled
        /// FIRST: stopping the loop alone never runs the standoff's tail (its Fired/Died handlers stayed on, Active stayed
        /// true, Ended never came), and a Run that outlived the stop could call its next wave into the fresh round.
        /// </summary>
        void StopLoop()
        {
            var police = PoliceResponse.Instance;
            if (police != null && police.Active) police.Cancel();
            if (_loop != null) { StopCoroutine(_loop); _loop = null; }
            HideMarshalCard();                                           // a Marshal night stopped mid-intro: its card goes too
            EndMarshalNight();                                           // Ricky goes, the monitor back to the real clock (no-op otherwise)
            MarshalBriefing.Cancel();                                    // a weapons check stopped mid-step: dropped now, still owed (no-op otherwise)
        }

        void StopPreview()
        {
            if (_preview == null) return;
            StopCoroutine(_preview);
            _preview = null;
            Previewing = false;
            if (spawner != null) spawner.Pause(false);
        }

        // ───────────────────────────── skip (B / Y) ─────────────────────────────

        /// <summary>
        /// A fresh B/Y press on a skip hand while the banner is up (<paramref name="show"/> null = no show ran: any time).
        /// The edge comes from the held level, armed only after the buttons are seen up inside the window, so a B/Y already
        /// held when the banner appears, or frozen by a pause (HandController stops refreshing while the gate is shut),
        /// never counts.
        /// </summary>
        bool SkipPressed(OutcomeShow show)
        {
            if (_skipRequested && Time.deltaTime > 0f && (show == null || show.CanSkip)) { _skipRequested = false; return true; }
            if (!InputGate.GameplayActive || Time.deltaTime <= 0f || (show != null && !show.CanSkip)) { _skipArmed = false; return false; }
            SkipHands(out var l, out var r);
            bool lh = l != null && l.SecondaryButton, rh = r != null && r.SecondaryButton;
            if (!_skipArmed) { _skipArmed = !lh && !rh && (l != null || r != null); return false; }
            if (!lh && !rh) return false;
            (lh ? l : r).HapticLight();
            return true;
        }

        /// <summary>
        /// The hand whose button skips: the LEFT only, Y (Brett 0.1.26: "it can only be on the left hand not the right hand the
        /// right hand is reserved for reload"). The right stays null so B never skips.
        /// </summary>
        static void SkipHands(out HandController left, out HandController right)
        {
            var cs = CommandSystem.Instance;
            left = cs != null ? cs.Left : null;
            right = null;
        }

        /// <summary>The banner's skip hint: "B / Y", "Y" or "B"; null when no controller can skip (no hands found).</summary>
        public static string SkipButtons()
        {
            SkipHands(out var l, out var r);
            return l != null && r != null ? "B / Y" : l != null ? "Y" : r != null ? "B" : null;
        }

        /// <summary>
        /// The incident is over and about to be judged: a live intruder stops acting (no more swings at a dead player, no
        /// wandering through the show) and can't be hit any more, so no force lands after the verdict. He stays visible.
        /// Internal: a Marshal night's hitmen stand down the same way before its card (PoliceResponse.StandDownSquad, research/27 §2).
        /// </summary>
        internal static void StandDown(IntruderT i)
        {
            if (i == null || i.Current is IntruderT.State.Dead or IntruderT.State.Gone) return; // a corpse ignores hits already
            i.StopAllCoroutines(); // brain, swing in progress, walk-in
            // The scripted walks (off the porch, up the path, out the door) drive the walk blend every frame through the speed
            // override; stopped mid-walk he'd keep walking (or running) on the spot. Stood down, he stands.
            var anim = i.GetComponentInChildren<HIS.Intruder.IntruderAnimator>();
            if (anim != null) anim.SetSpeedOverride(0f);
            if (i.TryGetComponent(out UnityEngine.AI.NavMeshAgent agent) && agent.enabled && agent.isOnNavMesh)
            {
                agent.ResetPath();
                agent.velocity = Vector3.zero;
            }
            foreach (var c in i.GetComponentsInChildren<Collider>()) c.enabled = false; // hitboxes (SetVisible(false) hides the rest later)
        }

        /// <summary>Custody ending: a car door and a radio out front, "Suspect in custody", he's led away (no lights, no wail).</summary>
        IEnumerator PoliceTakeHim()
        {
            if (spawner.TryGetDoor(out var door, out var outward))
            {
                Sfx.Play("outcome.cardoor", door + outward * 6f, 0.8f);
                yield return new WaitForSeconds(0.8f);
                Sfx.Play("outcome.radio", door + outward * 2f, 0.9f);
                Sfx.Play("vo.police.custody", door + outward * 2f, 0.9f);
                Subtitles.Show("Police", VoiceLines.Text("vo.police.custody"));
                yield return new WaitForSeconds(2f);
            }
            if (Intruder != null) Intruder.SetVisible(false);
        }

        void OnCommand(Command c, Vector3 from)
        {
            if (Record != null) Record.Commands.Add((Time.time, c));
            if (Intruder != null) Intruder.Hear(c);
            else if (spawner != null && spawner.Active != null) spawner.Active.Hear(c); // Sandbox: whoever is in the room
        }

        /// <summary>A tap of A/X yells the command that fits what the player is looking at right now.</summary>
        Command ContextCommand()
        {
            var i = Intruder != null ? Intruder : spawner != null ? spawner.Active : null;
            if (i == null || i.Current is IntruderT.State.Outside or IntruderT.State.BreakIn or IntruderT.State.Dead or IntruderT.State.Gone)
                return Command.WhoIsThere;
            var f = i.Facts;
            if (f.WeaponInHand != WeaponKind.None) return Command.DropWeapon;
            if (f.Fleeing || f.IdentityStated) return Command.GetOut;
            if (f.Posture == Posture.HandsUp) return Command.GetDown;
            if (f.Posture == Posture.Prone) return Command.GetDown;
            return Command.ShowHands;
        }

        void OnSessionReset()
        {
            // Hold-Menu restart: drop the current incident (or preview) and start a fresh one.
            StopPreview();
            if (mode == Mode.Sandbox) return;
            StopLoop();                                                  // (a standoff in progress is cancelled first)
            if (OutcomeShow.Instance != null) OutcomeShow.Instance.Clear();
            Intruder = null; Record = null;
            _loop = StartLoop();
        }
    }
}
