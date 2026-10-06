using System;
using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using UnityEngine.AI;
using Act = HIS.Intruder.IntruderAnimator.ActKind;
using Gait = HIS.Intruder.IntruderAnimator.Gait;
using GunPose = HIS.Intruder.IntruderAnimator.GunPose;

namespace HIS.Intruder
{
    /// <summary>
    /// An officer answering the call (research/22 §3–§5, Brett 0.1.25). Spawned by <see cref="PoliceResponse"/> through
    /// IntruderSpawner.SpawnScenario + <see cref="ConfigureOfficer"/>; Run() hands him straight to <see cref="PoliceBrain"/>:
    ///   1. From his start by the cars, up the FRONT door's porch path (on the door camera only: Outside layer), jogging, then
    ///      walking the last few metres, gun low. Lead and backup stack on the latch side of the door, cover on the hinge side.
    ///      <see cref="PoliceResponse.OfficerAtDoor"/>; the lead bangs on the door and announces until the entry is cleared.
    ///   2. The lead kicks a closed door in (one kick: VirtualDoor.Breach), steps 1–1.5 m in and off to the latch side on the
    ///      room NavMesh (the doorway link, like an intruder's WalkIn); the backup to the other side; the cover holds the
    ///      hinge-side jamb, half behind the real wall. Guns up on the player's chest (the gunman's aim pass), weapon lights on.
    ///   3. Orders by <see cref="PoliceResponse.StanceNow"/>, one voice at a time, only the lead talking: Armed → drop ×3
    ///      (2.5 s apart) → the cover's Taser; Aiming → "Don't do it!" / "Gun coming up!" at 0.15 s, fire at 0.5 s; Unarmed →
    ///      hands ×3, then the cuffs anyway; Surrendered → "Turn around" → "I'm coming up behind you" → holster, walk round the
    ///      furniture to 0.6 m behind the player, cuffs a hand at a time (sound + rumble at each controller) →
    ///      <see cref="PoliceResponse.Cuffed"/>. Nobody is ever told to kneel, lie down or walk.
    ///   4. FAIR fire (Brett: "if you can beat the cops to shooting you should be able to beat them"): only after the player's
    ///      act — fired on them (they answer after 0.35–0.45 s, the cover 0.3 s after the lead) or a gun held on them 0.5 s
    ///      after a spoken warning — bursts of 2–3 through the gunman's shot path (flash, spread, the player's body shape, real
    ///      walls stop rounds). Once the player's hands are empty they stop.
    ///   5. Hit: a body round kills (lethal rounds; SWAT's vest makes the first one a wound); a wound → "I'm hit!", he backs
    ///      out the way he came and off the step (<see cref="PoliceResponse.OfficerDown"/>, not killed).
    /// </summary>
    public partial class Intruder
    {
        /// <summary>His job in the stack (set by <see cref="ConfigureOfficer"/>).</summary>
        public OfficerRole Role { get; private set; }
        /// <summary>Which wave he came with (1 = the first two).</summary>
        public int Wave { get; private set; }
        public bool IsOfficer => _intent == Intent.Police;
        /// <summary>Police or a Marshal night's hitman (research/27). Intent stays Police for both ("squad member"), so the
        /// generic squad checks keep working; this says which brain and look.</summary>
        public SquadFaction Faction { get; private set; } = SquadFaction.Police;
        public bool IsHitman => Faction == SquadFaction.Hitmen;
        /// <summary>A hitman's wave skill (HitmanTier.For(Wave)); unused for police.</summary>
        HitmanTier _tier;
        /// <summary>Out of the fight: wounded and backing out, or dead.</summary>
        public bool OfficerOut => _officerOut || Current == State.Dead;
        /// <summary>Tests: each report an officer makes ("at the door", "fired hit", "tased", "cuffed", "down wounded", "down killed").</summary>
        public static event Action<Intruder, string> OfficerReported;

        /// <summary>Make this spawned intruder an officer: role, wave, and where he starts (by the cars, out on the street).</summary>
        /// <param name="rideSeat">A seat in his wave's vehicle (0 = the driver): he rides in, shootable through the glass, and
        /// gets out when it stops (Brett 0.1.33). −1: he's already out, at <paramref name="start"/>.</param>
        public void ConfigureOfficer(OfficerRole role, int wave, Vector3 start, int rideSeat = -1, int roundTo = -1, int roundSlot = 0,
                                     SquadFaction faction = SquadFaction.Police)
        {
            Role = role; Wave = wave; _officerStart = start; _rideSeat = rideSeat; _roundTo = roundTo; _roundSlot = roundSlot; _seated = false;
            Faction = faction; _tier = HitmanTier.For(wave);
            // A new standoff: forget the last one's "contact" call and its Taser hit (also reset by PoliceResponse.Run, in case
            // this lead never spawns). Waves restart at 1, so a round-A Taser left s_tasedWave == 1 and round B's wave-1 lead
            // went straight to the cuffs on a player still holding a gun.
            if (wave == 1 && role == OfficerRole.Lead) ResetStandoffStatics();
            _speaker = IsHitman ? "Hitman" : wave >= 3 ? "SWAT" : role == OfficerRole.Lead ? "Officer" : "Officer 2";
            s_squad.RemoveAll(o => o == null);
            if (!s_squad.Contains(this)) s_squad.Add(this);
            // The finder pick's fallback is the wave's last-spawned man (research/27 §2): the last one configured. Hitmen only.
            if (IsHitman) s_lastOfWave[wave] = Id;
            Died -= OnOfficerDied; Died += OnOfficerDied;
            // A Marshal night's hitman (research/27 §6 step 4): his tier's look, no weapon light; the police as before.
            if (IsHitman) { DressAsHitman(wave); InitHitman(); }
            else DressAsOfficer(swat: wave >= 3);
            // The spawner put him at the door; his brain seats him in the vehicle (or sets him at his start) next frame. Not
            // drawn until then: for that one frame a cop stood at the door, in the doorway view and on its camera, before the
            // truck had even pulled up (Brett 0.1.42: "I very briefly see a cop flash at the door and disappear").
            HideUntilPlaced();
            Debug.Log($"[HIS] Intruder #{Id}: {(IsHitman ? $"hitman ({_tier.Name})" : "officer")} {role}, wave {wave}, starts at {start:F1}{(roundTo >= 0 ? $", bound for door {roundTo}" : "")}");
        }

        Renderer[] _placementHidden;

        /// <summary>Spawned but not yet placed (seated, at his start): hidden, and not really where he stands (the door camera's
        /// motion sensor skips a hitman in this state: 0.1.56's stray MOTION beep at a wave's call).</summary>
        public bool PlacementPending => _placementHidden != null;

        /// <summary>Not drawn (forceRenderingOff: independent of SetVisible's enabled/layer switching) until <see cref="ShowPlaced"/>.</summary>
        void HideUntilPlaced()
        {
            _placementHidden = GetComponentsInChildren<Renderer>(true);
            foreach (var r in _placementHidden) if (r != null) r.forceRenderingOff = true;
        }

        void ShowPlaced()
        {
            if (_placementHidden == null) return;
            foreach (var r in _placementHidden) if (r != null) r.forceRenderingOff = false;
            _placementHidden = null;
        }

        // ---- Tunables (research/22 §3.3) ----
        const float OfficerJog = 3f;            // up the path (the sprint cycle, eased), then the aimed walk for the last 3 m
        const float OrderGap = 2.5f;            // escalating orders
        const float WarnAt = 0.15f, FireAt = 0.5f;          // muzzle on an officer: the warning, then deadly force
        const float ReactMin = 0.35f, ReactMax = 0.45f;     // their answer to your shot (Blair et al. ~0.39 s)
        const float CoverLag = 0.3f;            // the cover fires after the lead
        const float OfficerAccuracy = 0.8f;     // × the gunman's curve: ~0.55 a round at 3 m once steady
        const float CuffBehind = 0.6f;

        /// <summary>His jog up the path and round the house: the police's <see cref="OfficerJog"/>, a hitman's tier (research/27 §2).</summary>
        float SquadJog => IsHitman ? _tier.Jog : OfficerJog;
        /// <summary>His aimed walk (the last metres, bounding, in through the door): the gun walk, or a hitman's tier pace.</summary>
        float AimedWalk => IsHitman ? _tier.GunWalk : IntruderClips.GunWalkSpeed;

        Vector3 _officerStart;
        int _rideSeat = -1;
        /// <summary>The door he goes round the house to once out of the vehicle (-1: his own); his place in that team.</summary>
        int _roundTo = -1, _roundSlot;
        bool _seated;
        bool _officerOut, _cuffedDone, _taseOrdered;
        float _fireFrom = -1f;
        static readonly List<Intruder> s_squad = new();
        static float s_talkUntil;       // the floor (one officer's voice at a time, OfficerSay): whose line, what kind, until when
        static Intruder s_talker;
        static CopLine s_talkKind;
        static int s_tasedWave = -1;
        static float s_tasedAt = -100f;
        static int s_contactWave = -1;
        float _nextSightCheck, _blindFor;
        Vector3 _spot = new(float.NaN, 0f, 0f);

        /// <summary>Detour waypoints round the parked vehicles on his last walk up the path (tests; 0.1.38: 8–10 of them in laps).</summary>
        public int RouteDetours { get; private set; }

        /// <summary>A new standoff: the last one's "contact" call and Taser hit are forgotten (PoliceResponse.Run calls it).</summary>
        public static void ResetStandoffStatics() { s_contactWave = -1; s_tasedWave = -1; s_tasedAt = -100f; s_boundEpoch.Clear(); ResetFinderStatics(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetPoliceStatics() { s_squad.Clear(); s_talkUntil = 0f; s_talker = null; s_talkKind = CopLine.Chatter; s_tasedWave = -1; s_tasedAt = -100f; s_contactWave = -1; s_boundEpoch.Clear(); ResetFinderStatics(); OfficerReported = null; }

        static PoliceResponse Police => PoliceResponse.Instance;
        PlayerStance CopStance => Police != null ? Police.StanceNow : PoliceResponse.TestStance ?? PlayerStance.Armed;
        bool CopFired => Police != null ? Police.FiredNow : PoliceResponse.TestFired;
        bool CopEntry => Police != null ? Police.EntryNow : PoliceResponse.TestEntryCleared;
        /// <summary>
        /// Hit and still alive (a limb round, or SWAT's vest): out of the fight from here. A wave 2–3 hitman hit inside the room
        /// fights on instead (research/27 §2: "Hit inside: fights on; legs out: from the floor"); hit outside he still backs out.
        /// </summary>
        bool OfficerHit => _woundCount - _vestStops > 0 && Current != State.Dead && !_officerOut && !(IsHitman && _tier.FightsOnHit && !BehindDoor);

        void Report(string what)
        {
            Debug.Log($"[HIS] {(IsHitman ? "Hitman" : "Police")} #{Id} ({Role}, wave {Wave}): {what}");
            OfficerReported?.Invoke(this, what);
        }

        void OnOfficerDied(Intruder _)
        {
            if (_light != null) _light.On = false;
            _officerOut = true;
            Report("down killed");
            var pr = Police;
            if (pr != null) pr.OfficerDown(this, true); // (PoliceResponse counts each officer once)
            if (IsHitman) return;   // no "Man down!" from hitmen: the death grunt only (research/27 §5)
            var mate = FirstMate(OfficerRole.Cover) ?? FirstMate(OfficerRole.Lead) ?? FirstMate(OfficerRole.Backup);
            if (mate != null) mate.StartCoroutine(mate.SayWhenFree("vo.cop.cover.officerdown", 1.2f));
        }

        /// <summary>His brain threw (<see cref="Guarded"/>): out of the fight, so the wave can still end.</summary>
        void OfficerBrainFailed(Exception e) => Withdraw($"brain failed ({e.GetType().Name})");

        /// <summary>
        /// Out of the fight without a shot: his brain failed, or he stood stuck out there (PoliceResponse's watchdog). Out of
        /// his seat, hidden, unshootable, Gone; the standoff counts him (no charge) so the wave can still end (Brett 0.1.44:
        /// "after third wave of cops nothing happens" — one officer's brain died on the walk round the house).
        /// </summary>
        public void Withdraw(string why)
        {
            if (Current is State.Dead or State.Gone) return;
            StopAllCoroutines();
            if (Riding) { transform.SetParent(null, true); Riding = false; }
            if (_agent.enabled) _agent.enabled = false;
            if (_light != null) _light.On = false;
            HideUntilPlaced();
            SetHittable(false);
            _officerOut = true;
            SetState(State.Gone);
            Debug.LogWarning($"[HIS] Police #{Id} ({Role}, wave {Wave}): withdrawn — {why}");
            Report("withdrawn");
            var pr = Police;
            if (pr != null) pr.OfficerWithdrawn(this);
        }

        // ───────────────────────────── the squad ─────────────────────────────
        /// <summary>
        /// <paramref name="o"/> is a mate still in it: another officer of his wave, not out (wounded or dead), not gone. A
        /// predicate over <see cref="s_squad"/> walked with plain for-loops (squad order), not an iterator: IsTalker,
        /// BreachesHere, MaxAimedAt and WantsToFire run every frame per officer, and each iterator call was a heap allocation.
        /// </summary>
        bool IsMate(Intruder o) => o != null && o != this && o.Wave == Wave && !o.OfficerOut && o.Current != State.Gone;

        Intruder FirstMate(OfficerRole role)
        {
            for (int i = 0; i < s_squad.Count; i++) { var o = s_squad[i]; if (IsMate(o) && o.Role == role) return o; }
            return null;
        }

        /// <summary>The one who talks and cuffs: the lead; with the lead down the cover, then the backup.</summary>
        bool IsTalker { get { for (int i = 0; i < s_squad.Count; i++) { var o = s_squad[i]; if (IsMate(o) && Before(o)) return false; } return true; } }

        /// <summary><paramref name="o"/> ranks ahead of him (role, then who came first).</summary>
        bool Before(Intruder o) => o.Role < Role || (o.Role == Role && o.Id < Id);

        /// <summary>
        /// His place at his own door among this wave's officers there (Brett 0.1.29: a later wave splits over the front and
        /// the side door): 0 = the breacher on the latch side, 1 = the hinge side, 2 = behind 0. Stable (the fallen count).
        /// </summary>
        int Slot { get { int s = 0; foreach (var o in s_squad) if (o != null && o != this && o.Wave == Wave && o.DoorIndex == DoorIndex && Before(o)) s++; return s; } }

        /// <summary>This wave's officers at his door (him included, the fallen too).</summary>
        int AtMyDoor { get { int n = 1; foreach (var o in s_squad) if (o != null && o != this && o.Wave == Wave && o.DoorIndex == DoorIndex) n++; return n; } }

        /// <summary>No one still in it ranks ahead of him at his door: he kicks it.</summary>
        bool BreachesHere { get { for (int i = 0; i < s_squad.Count; i++) { var o = s_squad[i]; if (IsMate(o) && o.DoorIndex == DoorIndex && Before(o)) return false; } return true; } }

        float MaxAimedAt()
        {
            var pr = Police;
            if (pr == null) return 0f;
            float m = pr.AimedAtFor(this);
            for (int i = 0; i < s_squad.Count; i++) { var o = s_squad[i]; if (IsMate(o)) m = Mathf.Max(m, pr.AimedAtFor(o)); }
            return m;
        }

        // ───────────────────────────── voice (one at a time) ─────────────────────────────
        /// <summary>
        /// What a line is when another officer's is on (the floor): the cover's chatter ("Gun!", "Officer down!": a routine
        /// line from someone not talking to the player), the talker's orders (drop it, hands up, turn around: never cut),
        /// urgent (hit, breach, entry, contact, arrest), a warning ("Don't do it!", "Taser! Taser!").
        /// </summary>
        enum CopLine { Chatter, Order, Urgent, Warning }

        const float TalkGap = 0.15f;   // a breath between two officers' lines
        const float LineWait = 3f;     // an urgent line waits at most this for the floor (the longest take ~2.5 s + the gap), then it's stale
        Coroutine _lineWaiting;        // his urgent line waiting for another officer's to end (a newer line of his replaces it)

        /// <summary>A line is still on: the one who has the floor is alive and its take (plus the gap) hasn't run out.</summary>
        static bool FloorBusy => Time.time < s_talkUntil && s_talker != null && s_talker.Current != State.Dead;

        /// <summary>
        /// A line in his voice, one officer at a time (HANDOFF 0.1.28: urgent lines could overlap). His own new line cuts his
        /// old one (one mouth: Say). With another officer's line on:
        ///   - routine (chatter, orders): it waits its turn (false; SayWhenFree waits a little first), as before;
        ///   - <paramref name="urgent"/>: it cuts the cover's chatter; anything else it waits for, then a short gap, and goes if
        ///     that's within <see cref="LineWait"/> (else it's skipped: the moment has passed). So nobody talks over anybody,
        ///     and the talker's orders to the player are never cut;
        ///   - <paramref name="warning"/>: at once, cutting whatever is on. The fire (0.5 s of aim) and the Taser (0.9 s) are
        ///     timed from it; waiting would mean a round or a Taser before the words (research/22 §3.2: never unwarned).
        ///     (Another officer's order can't be on then: the warnings answer a gun coming up, and the Taser follows the
        ///     third "Drop it", 2.5 s after it began, and "Drop it" takes run under 2 s; the longest order take is 2.46 s.)
        /// A line with no clip yet only logs. False = not said now (someone's talking, or it's waiting).
        /// </summary>
        bool OfficerSay(string key, bool urgent = false, bool warning = false)
        {
            if (key == null || Current == State.Dead) return false;
            var kind = warning ? CopLine.Warning : urgent ? CopLine.Urgent : IsTalker ? CopLine.Order : CopLine.Chatter;
            if (FloorBusy)
            {
                if (kind < CopLine.Urgent) return false;
                if (s_talker != this)
                {
                    if (kind < CopLine.Warning && s_talkKind != CopLine.Chatter) { WaitForFloor(key, kind); return false; }
                    Debug.Log($"[HIS] Police #{Id} ({Role}): \"{key}\" cuts #{s_talker.Id}'s line");
                    s_talker.StopVoice(0.12f);
                }
            }
            Speak(key, kind);
            return true;
        }

        /// <summary>Say it now and take the floor for as long as the take runs (+ the gap).</summary>
        void Speak(string key, CopLine kind)
        {
            if (_lineWaiting != null) { StopCoroutine(_lineWaiting); _lineWaiting = null; }   // (said something newer: that one's stale)
            // The last speaker's line is over: a door voice flag he still holds is stale. Cleared so his own later StopVoice
            // (his next line, his death at the door) can't stop the line now on that door's voice source.
            if (s_talker != null && s_talker != this && !FloorBusy) s_talker._voiceThroughDoor = false;
            bool has = Sfx.Has(key);
            if (has) Say(key);
            // The take that's actually playing (a spatial line); through the door or not playing: the key's longest take.
            float left = has && _voice != 0 ? Sfx.SecondsLeft(_voice) : 0f;
            s_talker = this; s_talkKind = kind;
            s_talkUntil = Time.time + Mathf.Max(0.9f, left > 0f ? left : has ? Sfx.Length(key) : 0f) + TalkGap;
            Debug.Log($"[HIS] Police #{Id} ({Role}): \"{key}\"{(has ? "" : " (no clip)")}");
        }

        void WaitForFloor(string key, CopLine kind)
        {
            if (_lineWaiting != null) StopCoroutine(_lineWaiting);
            Debug.Log($"[HIS] Police #{Id} ({Role}): \"{key}\" waits for #{s_talker.Id}'s line");
            _lineWaiting = StartCoroutine(SayWhenFloorFree(key, kind));
        }

        IEnumerator SayWhenFloorFree(string key, CopLine kind)
        {
            float until = Time.time + LineWait;
            while (FloorBusy && s_talker != this && Time.time < until && Current != State.Dead) yield return null;
            _lineWaiting = null;
            if (Current == State.Dead) yield break;
            // The standoff ended while it waited (the player shot dead, cuffed): the moment's gone — no line into the show.
            // The arrest line is spoken after End by design, so it may still go.
            if (Police != null && Police.Ending != StandoffEnding.None && key != "vo.cop.lead.arrest")
            { Debug.Log($"[HIS] Police #{Id} ({Role}): \"{key}\" dropped (the standoff is over)"); yield break; }
            if (FloorBusy && s_talker != this) { Debug.Log($"[HIS] Police #{Id} ({Role}): \"{key}\" skipped (the floor was never free)"); yield break; }
            Speak(key, kind);
        }

        IEnumerator SayWhenFree(string key, float maxWait = 1.5f)
        {
            float until = Time.time + maxWait;
            while (Time.time < s_talkUntil && Time.time < until && Current != State.Dead) yield return null;
            OfficerSay(key);
        }

        // ───────────────────────────── the brain ─────────────────────────────
        IEnumerator PoliceBrain()
        {
            if (_rideSeat >= 0) { yield return RideIn(); if (Current == State.Dead) yield break; }
            if (!_seated) PlaceOnFoot();
            // Bound for the side door (Brett 0.1.42): out of the vehicle with the rest, then round the house — unless it was
            // shot to a stop on the way in (its driver killed): then everyone left comes to the front door.
            if (_roundTo >= 0 && _roundTo != DoorIndex)
            {
                if (HIS.Security.PoliceCars.WaveCarCrashed(Wave, Faction)) Report("the vehicle was shot up: in the front with the rest");
                else
                {
                    yield return RoundToDoor(_roundTo);
                    if (Current == State.Dead) yield break;
                    if (OfficerHit) { yield return OfficerWounded(); yield break; }
                }
            }
            var door = Door;
            yield return OfficerWalkUp(door);
            if (OfficerHit) { yield return OfficerWounded(); yield break; }
            // Hitmen (research/27 §2): no knock or announce (a blind volley back through the door instead), and no orders,
            // Taser or cuffs inside: they fight, after a fair tell (IntruderHitman.cs).
            yield return IsHitman ? HitmanStack(door) : OfficerStack(door);
            if (OfficerHit) { yield return OfficerWounded(); yield break; }
            yield return OfficerEntry(door);
            if (OfficerHit) { yield return OfficerWounded(); yield break; }
            yield return IsHitman ? HitmanFight() : OfficerStandoff();
        }

        /// <summary>
        /// In his seat as the vehicle pulls up (PoliceCars.Board): sitting, moving with it, hittable through the glass, drawn
        /// for the camera and the doorway (behind the door). Shot dead there he slumps and stays (the car crashes if he drove);
        /// otherwise, his turn come once it stops (PoliceCars.GetOut), out of his door onto his feet (IntruderCarExit.cs), and on
        /// to the walk-up from there.
        /// </summary>
        IEnumerator RideIn()
        {
            if (!HIS.Security.PoliceCars.Board(this, Wave, _rideSeat, out var car, out var local)) yield break; // (on foot from _officerStart)
            if (_agent.enabled) _agent.enabled = false;
            float ground = _floorY;
            Riding = true;
            transform.SetParent(car, false);
            transform.localPosition = local; transform.localRotation = Quaternion.identity;
            _prevPos = transform.position;
            _floorY = transform.position.y;          // his "floor" is the seat: shot, he slumps where he sits (Collapse)
            OnPorchPath = true;                      // (out there, not at the door: he dies where he is)
            // Sitting (Brett 0.1.48: "theyre like kneeling instead of sitting"): the driver with his hands on the wheel, the rest in
            // their seats; head and shoulders at the windows, legs in the footwell out of sight. Straight into it, no blend.
            var seatGait = _rideSeat == 0 ? Gait.Driving : Gait.Seated;
            SetGait(seatGait);
            if (_hasAnim) _anim.SnapGait(seatGait);
            if (_hasAnim) _anim.SetGunPose(GunPose.None);
            if (_weapon != null && _weapon.InHand) _weapon.Show(false);
            SetHittable(true);
            _seated = true;
            ShowPlaced();
            Report($"riding in seat {_rideSeat}");
            Vector3 exit;
            while (!HIS.Security.PoliceCars.GetOut(Wave, _rideSeat, out exit, Faction))
            {
                if (Current == State.Dead) yield break;   // killed in his seat: stays in the car
                _floorY = transform.position.y;
                yield return null;
            }
            if (Current == State.Dead) yield break;
            // Out of the car (Brett 2026-10-05: "have the flow of cops exiting look better" — he used to pop out standing at his
            // exit): the exit clip from his seat, out of his door as it swings, onto his feet, his gun, his place by the car
            // (IntruderCarExit.cs). The show over (the cars gone) or an old rig: out at once, as before.
            yield return GetOutOfCar(exit, ground);
            if (Current == State.Dead) yield break;
            Report("out of the car");
        }

        /// <summary>No seat (the vehicle had stopped, or none): on his feet at his start out on the street, drawn from here.</summary>
        void PlaceOnFoot()
        {
            if (_agent.enabled) _agent.enabled = false;
            var at = _officerStart != Vector3.zero ? _officerStart : FallbackStart();
            at.y = _floorY;
            transform.position = at;
            _prevPos = at;
            ShowPlaced();
        }

        /// <summary>
        /// From the vehicle round the house to door <paramref name="door"/> (Brett 0.1.42: the side team rides in with the rest):
        /// a jog off the front porch past the edge of its camera (round the parked vehicles), then out of every view for the
        /// walk round — his steps heard along the real walls, as with the burglar who tries the other door — and on at that
        /// door's yard gate, from where <see cref="OfficerWalkUp"/> brings him up its path on its camera.
        /// </summary>
        IEnumerator RoundToDoor(int door)
        {
            HIS.Room.VirtualDoor other = null;
            foreach (var d in HIS.Room.VirtualDoor.All) if (d != null && d.Index == door) other = d;
            var here = PorchFor(Door);
            var spawner = Spawner;
            if (here == null || other == null || PorchFor(other) == null || spawner == null) yield break;
            Report($"round the house to the {(door == 0 ? "front" : "side")} door");
            SetGait(Gait.Gun);
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            OnPorchPath = true;
            SetHittable(true);
            // The way round is the off-view point on the side of the house the other door is on.
            var off = FlatDistance(here.OffLeft, other.Center) <= FlatDistance(here.OffRight, other.Center) ? here.OffLeft : here.OffRight;
            var prev = transform.position;
            foreach (var p in HIS.Security.PoliceCars.RouteAround(prev, off))
            {
                yield return MoveOutside(p, () => SquadJog);
                if (OfficerHit || Current == State.Dead) yield break;
            }
            yield return MoveOutside(off, () => SquadJog);
            if (OfficerHit || Current == State.Dead) yield break;
            // Round the house: nowhere to be seen or shot (the front doorway may stand open onto where he'd be waiting).
            HideUntilPlaced();
            SetHittable(false);
            var from = Door;
            float walk = Mathf.Clamp(Vector3.Distance(from.Center, other.Center) / SquadJog + UnityEngine.Random.Range(1.5f, 3f), 4f, 9f);
            float t0 = Time.time;
            yield return FootstepsRound(from, other, walk);
            while (Time.time - t0 < walk && Current != State.Dead) yield return null;
            if (Current == State.Dead) yield break;
            if (!spawner.PrepareDoor(door, out var outside, out var inside, out var floorY))
            {
                Report("no way round: back to the front door");
                SetHittable(true); ShowPlaced();
                yield break;
            }
            DoorIndex = door;
            HIS.Room.VirtualDoor.Current = Door;
            _outsidePoint = outside; _insidePoint = inside; _floorY = floorY;
            _officerStart = PoliceResponse.SideStart(door, _roundSlot);   // his yard gate (OfficerWalkUp draws him there)
        }

        /// <summary>Street → step on the porch path (the door camera sees it), to his place in the stack.</summary>
        IEnumerator OfficerWalkUp(HIS.Room.VirtualDoor door)
        {
            SetGait(Gait.Gun);
            if (_hasAnim) _anim.SetGunPose(GunPose.None);
            if (_agent.enabled) _agent.enabled = false;
            var stack = StackSpot(door);
            var start = _officerStart != Vector3.zero ? _officerStart : FallbackStart();
            start.y = _floorY;
            var route = new List<Vector3>();
            var path = PorchFor(door)?.Approach;
            // Not in single file on one line (Brett 0.1.30: "the cops come perfectly inline with each other so if you shoot
            // one the other gets hit"): each walks his own lane, ~0.45 m either side of the path's middle, and the later
            // ones set off a beat behind. Hitmen by their tier (research/27 §2): wave 1 bunched (±0.2 m: one shot can find
            // both), wider every wave, the operators of wave 5 spread wide.
            int slot = Slot;
            float laneOff = IsHitman ? _tier.Lane : 0.45f;
            float lane = slot == 0 ? -laneOff : slot == 1 ? laneOff : 0f;
            if (path != null && path.Length >= 2)
            {
                int k = 0; float best = float.MaxValue;
                for (int i = 0; i < path.Length - 1; i++) { float d = FlatDistance(start, path[i]); if (d < best) { best = d; k = i; } }
                for (int i = k; i < path.Length - 1; i++)                      // not the step itself: his stack spot instead
                {
                    var along = Flat(path[Mathf.Min(i + 1, path.Length - 1)] - path[i]).normalized;
                    route.Add(path[i] + Vector3.Cross(Vector3.up, along) * lane);
                }
            }
            // A path point a parked vehicle stands on is skipped (the path runs from the street, where they pull up).
            route.RemoveAll(p => HIS.Security.PoliceCars.InFootprint(p));
            route.Add(stack);
            // Round the vehicles out on the street, never through them (Brett 0.1.33: wave 2 walked through the first car).
            var legs = new List<Vector3>(); var prev = start;
            foreach (var p in route) { legs.AddRange(HIS.Security.PoliceCars.RouteAround(prev, p)); legs.Add(p); prev = p; }
            RouteDetours = legs.Count - route.Count;
            route = legs;
            transform.SetPositionAndRotation(start, Quaternion.LookRotation(Flat(route[0] - start), Vector3.up));
            _prevPos = start;
            ShowPlaced();
            OnPorchPath = true;
            SetHittable(true);                               // a round through the leaf reaches him on the path too (as a walk-up)
            for (float wait = slot * (IsHitman ? _tier.Stagger : 0.7f); wait > 0f && !OfficerHit && Current != State.Dead; wait -= Time.deltaTime) yield return null;
            Debug.Log($"[HIS] Intruder #{Id}: {(IsHitman ? "hitman" : "officer")} up the path ({route.Count} legs) to the stack at {stack:F2}");
            // Wave 1 hitmen bound in step (both stop, both go: street muscle, not a team); everyone else out of step by slot.
            _contactSince = -1f; _underFire = false; _lineCheckAt = 0f; _lineLostAt = -1f; _boundSlot = IsHitman && _tier.BoundInStep ? 0 : slot;
            // Each leg checked again as he goes (Brett 0.1.54: "the swat especially after the car crashes walk through the swat
            // car"): a vehicle can crash onto his way after the route was planned. A blocked leg gets a way round from where he
            // stands; AdvanceTo stops early (_replan) when a car lands across the leg he's on. Capped, so a car that can't be
            // got round never freezes him (he walks as before).
            int replans = 0;
            for (int i = 0; i < route.Count; i++)
            {
                var p = route[i];
                if (replans < MaxReplans && HIS.Security.PoliceCars.SegmentBlocked(transform.position, p))
                {
                    var round = HIS.Security.PoliceCars.RouteAround(transform.position, p);
                    replans++;
                    if (round.Count > 0) { route.InsertRange(i, round); p = route[i]; Report($"a vehicle in the way: round it ({round.Count} legs)"); }
                }
                _replan = false;
                yield return AdvanceTo(p, stack, door, replans < MaxReplans);
                if (OfficerHit || Current == State.Dead) break;
                if (_replan) i--;                                   // stopped short: this leg again, from here
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            OnPorchPath = false;
        }

        // ── Under fire on the way up (Brett 0.1.47: "if im standing there with the door open cops shouldnt sprint all the way
        //    to the door until stacking up while im shooting them").
        const float BoundSeconds = 2.2f;   // one holds and returns fire while his partner moves, then they swap
        /// <summary>How often a walker looks for a vehicle across his leg, and how many times one walk may go round (0.1.54).</summary>
        const float CarCheckSeconds = 0.3f;
        const int MaxReplans = 12;
        bool _replan;
        /// <summary>
        /// × the gunman's curve for rounds from the path (fire to make you get your head down while the partner moves, at 6–15 m
        /// through the doorway): ~0.15–0.22 a round past 8 m, vs ~0.24–0.36 with <see cref="OfficerAccuracy"/>. Tune here.
        /// </summary>
        const float PathFireAccuracy = 0.5f;
        float _contactSince = -1f, _lineCheckAt, _nextPathShot, _lineLostAt = -1f;
        bool _underFire;
        int _boundSlot, _pathBurst, _pathBurstLength = 2;

        /// <summary>
        /// The player has fired on the police, the door he's walking to stands open (not a crack), the player is in view through
        /// it, and the stack (beside the door, out of the doorway's line) is more than a dash away. Hitmen: not "fired on the
        /// police" (that stays set all night after wave 1) but this wave's own rule (the tier's FireFromPath): the pros and
        /// operators (waves 3–5) on sight, the street men (waves 1–2) only once the player has fired this wave (research/27 §6 step 3).
        /// </summary>
        bool UnderFireOnTheWay(HIS.Room.VirtualDoor door, Vector3 stack) =>
            (IsHitman ? _tier.FireFromPath || HitFiredThisWave : CopFired)
            && door != null && door.OpenAngle > 20f && FlatDistance(transform.position, stack) > 3f && SeesPlayerThrough(door);

        /// <summary>
        /// From out here, the player in view: through a doorway (the walls don't stop rays from outside: research in
        /// VirtualDoor.SegmentThroughOpening), or out in the doorway himself — the shared rule, InViewOfPlayer (review 2: a
        /// player firing from the doorway was never engaged from the path) — and nothing of the room in between.
        /// </summary>
        bool SeesPlayerThrough(HIS.Room.VirtualDoor door) => LineToPlayer(transform.position) && InViewOfPlayer();

        /// <summary>
        /// When each wave's team at each door started bounding: one clock per team, so partners stay out of step (one always
        /// covers the other) however far apart they made contact (0.1.48 review). Cleared with the standoff's statics.
        /// </summary>
        static readonly Dictionary<(int wave, int door), float> s_boundEpoch = new();

        /// <summary>
        /// One leg of the walk up. Nobody shooting, or no line on the player: the jog, then the aimed walk for the last 3 m (as
        /// before). Under fire (<see cref="UnderFireOnTheWay"/>): "Contact!", and they bound — holding and returning fire, then
        /// moving at the aimed walk, every <see cref="BoundSeconds"/>, partners out of step (by slot) so one always covers the
        /// other. Once in contact, the last few metres to the stack are a dash out of the doorway's line.
        /// </summary>
        IEnumerator AdvanceTo(Vector3 to, Vector3 stack, HIS.Room.VirtualDoor door, bool watchForCars = false)
        {
            to.y = _floorY;
            float carCheckAt = Time.time + CarCheckSeconds;
            while (Current != State.Dead && !OfficerHit && FlatDistance(transform.position, to) > 0.04f)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                // A vehicle crashed across this leg since it was planned: stop here and let the walk find a way round.
                if (watchForCars && Time.time >= carCheckAt)
                {
                    carCheckAt = Time.time + CarCheckSeconds;
                    if (HIS.Security.PoliceCars.SegmentBlocked(transform.position, to)) { _replan = true; yield break; }
                }
                if (Time.time >= _lineCheckAt)
                {
                    _lineCheckAt = Time.time + 0.2f;
                    bool now = UnderFireOnTheWay(door, stack);
                    // Lost him for a moment (he leans back behind the jamb to fire): keep bounding for 0.8 s, so a line that
                    // flickers at the frame's edge doesn't send him jogging in bursts with his gun bobbing (0.1.48 review).
                    if (now) _lineLostAt = -1f;
                    else if (_underFire && FlatDistance(transform.position, stack) > 3f)
                    {
                        if (_lineLostAt < 0f) _lineLostAt = Time.time;
                        now = Time.time - _lineLostAt < 0.8f;
                    }
                    _underFire = now;
                }
                float near = FlatDistance(transform.position, stack);
                if (_underFire)
                {
                    if (_contactSince < 0f)
                    {
                        _contactSince = Time.time;
                        Report("under fire on the way up: bounding");
                        // A hitman's contact is his tell's callout ("There he is!" / "Contact.", or a rack at his gun): once each.
                        if (IsHitman) HitmanCallout();
                        else if (s_contactWave != Wave) { s_contactWave = Wave; OfficerSay(Wave >= 3 && Sfx.Has("vo.cop.swat.contact") ? "vo.cop.swat.contact" : "vo.cop.cover.contact", urgent: true); }
                        GunUp();
                    }
                    if (!s_boundEpoch.TryGetValue((Wave, DoorIndex), out float epoch)) s_boundEpoch[(Wave, DoorIndex)] = epoch = Time.time;
                    bool hold = ((int)((Time.time - epoch) / (IsHitman ? _tier.BoundSeconds : BoundSeconds)) + _boundSlot) % 2 == 0;
                    if (hold) HoldAndFire();
                    else
                    {
                        StepOutside(to, AimedWalk, aim: true);
                        // On the move a hitman's tell still runs (his gun comes up as he walks; he fires when he next holds).
                        if (IsHitman) HitmanWantsToFire();
                    }
                }
                // In contact before but no line now: still the careful aimed walk until the last few metres, then the dash.
                else if (_contactSince >= 0f) StepOutside(to, near > 3f ? AimedWalk : SquadJog, aim: near > 3f);
                else StepOutside(to, near > 3f ? SquadJog : AimedWalk, aim: false);
                yield return null;
            }
        }

        /// <summary>Holding his ground on the path under fire: gun on the player through the doorway, short bursts when it's deadly force.</summary>
        void HoldAndFire()
        {
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            if (_weapon == null || !_weapon.InHand) GunUp();
            OfficerAim();
            // A hitman: his tell, his tier's path accuracy and bursts (HitmanShot keeps his burst clock).
            if (IsHitman) { if (HitmanWantsToFire()) HitmanShot(); return; }
            if (Time.time < _nextPathShot || !WantsToFire()) return;
            if (!OfficerShot(PathFireAccuracy)) { _nextPathShot = Time.time + 0.25f; return; }   // the frame or a wall in the way: look again shortly
            if (++_pathBurst >= _pathBurstLength) { _pathBurst = 0; _pathBurstLength = UnityEngine.Random.Range(2, 4); _nextPathShot = Time.time + UnityEngine.Random.Range(1.1f, 1.7f); }
            else _nextPathShot = Time.time + UnityEngine.Random.Range(0.3f, 0.45f);
        }

        /// <summary>Stacked on the step: at the door (hittable through the leaf), the lead banging and announcing until it's go.</summary>
        IEnumerator OfficerStack(HIS.Room.VirtualDoor door)
        {
            SetState(State.BreakIn);                         // at the door: a round through the leaf finds him (SetState)
            _doorHealth = Health;
            if (_hasAnim) _anim.SetGunPose(GunPose.Ready);
            var pr = Police;
            if (pr != null) pr.OfficerAtDoor(this);
            Report("at the door");
            float nextKnock = Time.time + 0.3f;
            int knocks = 0;
            bool contact = false;
            while (!CopEntry && Current != State.Dead)
            {
                if (OfficerHit) yield break;
                // Shot at through the door: the first one to call it yells contact; the wave goes in now (PoliceResponse).
                if (CopFired && !contact) { contact = true; if (s_contactWave != Wave) { s_contactWave = Wave; OfficerSay(Wave >= 3 && Sfx.Has("vo.cop.swat.contact") ? "vo.cop.swat.contact" : "vo.cop.cover.contact", urgent: true); } }
                if (Time.deltaTime > 0f && door != null) FaceTowards(door.Center, 4f);
                if (IsTalker && knocks < 3 && Time.time >= nextKnock) { OfficerKnockAndAnnounce(door); knocks++; nextKnock = Time.time + 5.5f; }
                yield return null;
            }
            // Never in unannounced (research/22 §3.4): cleared before his first knock, he knocks and calls out, then a beat.
            if (IsTalker && knocks == 0 && Current != State.Dead && !OfficerHit)
            {
                OfficerKnockAndAnnounce(door);
                for (float t = 0f; t < 1.5f && !CopFired && !OfficerHit && Current != State.Dead; t += Time.deltaTime) yield return null;
            }
        }

        /// <summary>The lead bangs on the door and announces (research/22 §3.4). (Named apart from the Marshal night's visitor
        /// knock, IntruderEntries.KnockAndAnnounce: Ortiz and Sal's knock-and-key, a different routine.)</summary>
        void OfficerKnockAndAnnounce(HIS.Room.VirtualDoor door)
        {
            if (door != null && !door.IsOpen)
            {
                Sfx.Play("door.pound", door.Center, 0.9f);
                door.Pound(0.8f);
            }
            OfficerSay("vo.cop.lead.announce", urgent: true);
        }

        /// <summary>In: the lead kicks a closed door and goes in, the backup after him, the cover to the jamb.</summary>
        IEnumerator OfficerEntry(HIS.Room.VirtualDoor door)
        {
            SetState(State.Entering);
            // A Marshal night (research/27 §2): one hitman per wave goes for Ricky, picked here, at his door going in — by now
            // the side man has gone round (or the car was shot to a stop). His gun stays low through the entry. Police: never.
            if (IsHitman) TakeFinderRole();
            if (!BreachesHere)
            {
                // The first man at this door opens it; with him down, the next one kicks it himself.
                for (float until = Time.time + 6f; door != null && !door.IsOpen && Time.time < until && !BreachesHere; )
                { if (OfficerHit || Current == State.Dead) yield break; yield return null; }
            }
            if (door != null && !door.IsOpen)
            {
                yield return KickDoorIn(door);
                if (OfficerHit || Current == State.Dead) yield break;
            }
            SetVisible(true);
            GunUp();
            // Brett 0.1.26: "they kinda just crowd by the door ... when they breach they move in". The lead goes first, the
            // backup 0.8 s behind, the cover holds the frame 1.3 s over them and follows; each spreads round the player
            // (SpreadSpot) instead of stopping just inside.
            int slot = Slot;
            float wait = IsHitman ? (slot == 2 ? _tier.ThirdWait : slot == 1 ? _tier.SecondWait : 0f)   // research/27 §2, per tier
                                  : slot == 2 ? 0.8f : slot == 1 ? 1.3f : 0f;
            for (float t = 0f; t < wait && !OfficerHit && Current != State.Dead; t += Time.deltaTime)
            {
                OfficerAim();
                // A hitman waiting his turn at the open doorway: his tell runs (the gun comes up in view), but he holds his
                // fire until he's through it (as designed; FireShot's shared wall rule, 0.1.53, would now refuse a round into
                // the house wall from out here anyway — this wait just isn't a firing position).
                if (IsHitman) HitmanWantsToFire();
                yield return null;
            }
            if (OfficerHit || Current == State.Dead) yield break;
            // Hitmen: "Go." (the pros and operators, waves 3–5: the tier's Look) or nothing (the street men); a missing take is
            // silence (research/27 §5).
            if (IsHitman) { if (_tier.Look >= HitmanTier.LookPro && IsTalker) HitmanSay("vo.hitman.pro.go"); }
            else if (IsTalker) OfficerSay(Wave >= 3 && Sfx.Has("vo.cop.swat.entry") ? "vo.cop.swat.entry" : "vo.cop.lead.entry", urgent: true);
            // The finder steps just inside and looks for the witness from there (FinderHunt); everyone else spreads round you.
            yield return EnterRoom(_finding ? FinderDoorway(door) : SpreadSpot(door));
            if (OfficerHit || Current == State.Dead) yield break;
            SetState(State.Idle);
            _enteredRoomAt = Time.time;
            Report("in the room");
        }

        /// <summary>One kick at the latch from 1.15 m out (the kick's reach): it bursts.</summary>
        IEnumerator KickDoorIn(HIS.Room.VirtualDoor door)
        {
            if (!IsHitman) OfficerSay("vo.cop.lead.breach", urgent: true);   // (hitmen don't announce the kick: research/27 §5)
            yield return MoveOutside(DoorFloor(door) + Flat(door.Outward).normalized * 1.15f, () => AimedWalk);
            for (float t = 0f; t < 0.25f; t += Time.deltaTime) { FaceTowards(door.Center, 12f); yield return null; }
            if (OfficerHit || Current == State.Dead) yield break;
            if (_hasAnim) { _anim.SetGunPose(GunPose.None); _anim.Kick(); }
            _kicking = true;
            yield return new WaitForSeconds(IntruderClips.KickContact);
            _kicking = false;
            if (Current == State.Dead) yield break;
            if (!door.IsOpen) door.Breach();
            Debug.Log($"[HIS] Intruder #{Id}: {(IsHitman ? "hitman" : "officer")} kicks the door in");
            yield return new WaitForSeconds(IntruderClips.KickLength * IntruderClips.OneShotExit - IntruderClips.KickContact);
            if (_hasAnim) _anim.ResetKick();
        }

        /// <summary>Through the doorway on the room NavMesh (the DoorwayNav link, as an intruder walks in) to <paramref name="dest"/>, gun on the player.</summary>
        IEnumerator EnterRoom(Vector3 dest)
        {
            var nav = Nav;
            // A hitman walks in at his tier's pace; legs shot out on the way he stops there and fights from the floor
            // (HitmanWalk / HitmanDowned). The police exactly as before.
            Func<float> walk = IsHitman ? (Func<float>)(() => HitmanWalk) : () => OfficerHit ? 0f : IntruderClips.GunWalkSpeed;
            // (A hitman's walk in also ends once it has run long with him well inside the room: HitmanWalkInOver, 0.1.58.)
            float walkFrom = Time.time;
            Func<bool> until = IsHitman ? (Func<bool>)(() => HitmanDowned || HitmanWalkInOver(walkFrom)) : null;
            if (nav != null && nav.Ready && nav.OnApron(transform.position, out var apron))
            {
                if (FlatDistance(transform.position, apron) > 0.05f)
                    yield return MoveOutside(apron, () => IsHitman && HitmanDowned ? IntruderClips.CrawlSpeed : AimedWalk, aim: true);
                if (OfficerHit || Current == State.Dead) yield break;
                nav.Reconnect();
                _navEntry = nav.InsideEnd;
                if (_hasAnim) _anim.SetSpeedOverride(null);
                _agent.enabled = true;
                _agent.Warp(transform.position);
                bool finderWalk = _finding;
                walkFrom = Time.time;                                   // (the cap's clock: from the apron, not the walk to it)
                yield return PathTo(dest, walk,
                    () => { OfficerAim(); return OfficerHit ? null : WantsToFire() ? FireOnce() : null; }, until: until);
                // The finder's way in blocked (research/27 §2: "his walk is blocked: he drops the role and fights you"), or he
                // turned on you on it (you came within 2 m, or hit him): a spread spot round you, as the rest. Police: never.
                if (finderWalk && _finding && _walkBlocked) DropFinder("the way in is blocked");
                if ((_walkBlocked || (finderWalk && !_finding)) && !OfficerHit && Current != State.Dead)
                {
                    var again = SpreadSpot(Door);   // (from here, round whoever's in the way)
                    if (FlatDistance(again, transform.position) > 0.3f)
                        yield return PathTo(again, walk,
                            () => { OfficerAim(); return OfficerHit ? null : WantsToFire() ? FireOnce() : null; }, until: until);
                }
                if (_walkBlocked) Debug.LogWarning($"[HIS] Intruder #{Id}: officer's way in is blocked; holds at {transform.position:F2}");
            }
            else
            {
                Debug.LogWarning($"[HIS] Intruder #{Id}: officer has no doorway link; straight in if the line's clear");
                if (DoorwayNav.ClearLine(transform.position, dest)) yield return MoveOutside(dest, () => AimedWalk, aim: true);
                HandOverToNavMesh();
            }
            if (_hasAnim) _anim.SetSpeedOverride(null);
            StopHere();
        }

        /// <summary>
        /// Holding the player at gunpoint until it's over: deadly force only when provoked (fair), otherwise the talker's orders,
        /// the Taser, the cuffs. Wounded → backs out.
        /// </summary>
        IEnumerator OfficerStandoff()
        {
            StopHere();
            float nextOrder = Time.time + 0.6f, nextShot = 0f, turnAt = -1f, taseAt = -100f;
            int drop = 0, hands = 0, burst = 0, burstLength = UnityEngine.Random.Range(2, 4);
            bool warned = false, contact = false, gunCalled = false, taseCalled = false;
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }   // paused
                if (OfficerHit) { yield return OfficerWounded(); yield break; }
                if (_cuffedDone) { if (_light != null) _light.On = false; yield return null; continue; }   // arrested: it's over
                var stance = CopStance;
                bool fired = CopFired;
                float aimed = MaxAimedAt();
                bool aimFire = stance == PlayerStance.Aiming && aimed >= FireAt;
                bool threat = aimFire || (fired && stance is PlayerStance.Armed or PlayerStance.Aiming);

                // ── Deadly force: after the player's act, never before (research/22 §3.2).
                if (threat)
                {
                    if (_fireFrom < 0f)
                    {
                        // Aimed at: the 0.5 s after the warning was the time. No warning yet (he was busy cuffing or with
                        // the Taser when the gun came up): it's yelled now and the reaction time applies — never a round unwarned.
                        float react = aimFire && warned ? 0f : UnityEngine.Random.Range(ReactMin, ReactMax);
                        if (aimFire && !warned) { warned = true; if (IsTalker) OfficerSay("vo.cop.lead.dontdoit", warning: true); }
                        _fireFrom = Time.time + react + (IsTalker ? 0f : CoverLag);
                        Report(fired ? "returning fire" : "gun on us 0.5 s: firing");
                        if (fired && !contact && !IsTalker)   // (a gunfight: straight after whatever the lead is saying; OfficerSay)
                        { contact = true; OfficerSay(Wave >= 3 && Sfx.Has("vo.cop.swat.contact") ? "vo.cop.swat.contact" : "vo.cop.cover.contact", urgent: true); }
                    }
                    if (_weapon == null || !_weapon.InHand) GunUp();
                    OfficerAim();
                    // No round can reach him for 1.5 s (FireShot's own test: a wall in the way — out beside the doorway, the
                    // player round a corner or down behind cover): don't stand aiming at the wall, take a new angle, at most
                    // every 2 s. The gunfight stays here: orders, the Taser and the cuffs never run while it's on. 0.1.53 review.
                    bool line = ShotLineNow();
                    _noLineFor = line ? 0f : _noLineFor + Time.deltaTime;
                    if (_noLineFor >= 1.5f && Time.time >= _nextSightCheck && _agent.enabled && _agent.isOnNavMesh)
                    {
                        _nextSightCheck = Time.time + 2f;
                        var spot = LineSpot();
                        if (FlatDistance(spot, transform.position) > 0.3f)
                        {
                            Report("moving for a line on the player");
                            yield return PathTo(spot, () => OfficerHit ? 0f : IntruderClips.GunWalkSpeed,
                                () => { OfficerAim(); return OfficerHit ? null : WantsToFire() ? FireOnce() : null; });
                            StopHere();
                            _noLineFor = 0f;
                            continue;
                        }
                    }
                    if (line && Time.time >= _fireFrom && Time.time >= nextShot && Time.time >= _tellDoneAt && !(MovementLocked && _lock >= Lock.React) && GunOnPlayer())
                    {
                        if (!OfficerShot()) nextShot = Time.time + 0.25f;          // a wall in the way: look again shortly
                        else if (++burst >= burstLength) { burst = 0; burstLength = UnityEngine.Random.Range(2, 4); nextShot = Time.time + UnityEngine.Random.Range(1.1f, 1.7f); }
                        else nextShot = Time.time + UnityEngine.Random.Range(0.3f, 0.45f);
                    }
                    yield return null;
                    continue;
                }
                _fireFrom = -1f; burst = 0; _noLineFor = 0f;

                // ── The warning: a gun coming up on us.
                if (stance == PlayerStance.Aiming)
                {
                    if (!warned && aimed >= WarnAt)
                    {
                        warned = true;
                        if (IsTalker) OfficerSay("vo.cop.lead.dontdoit", warning: true);
                        else StartCoroutine(SayWhenFree("vo.cop.cover.gunup", 1.3f));   // right after the lead's "Don't do it!"
                    }
                }
                else warned = false;
                OfficerAim();

                if (_taseOrdered) { _taseOrdered = false; yield return OfficerTase(); continue; }
                // No line to the player for 2 s (he moved behind something): he takes a new angle on him.
                if (Time.time >= _nextSightCheck)
                {
                    _nextSightCheck = Time.time + 0.5f;
                    _blindFor = HasLineOnPlayer() ? 0f : _blindFor + 0.5f;
                    if (_blindFor >= 2f && _agent.enabled && _agent.isOnNavMesh)
                    {
                        _blindFor = 0f;
                        var spot = LineSpot();
                        if (FlatDistance(spot, transform.position) > 0.3f)
                        {
                            Report("moving for a line on the player");
                            yield return PathTo(spot, () => OfficerHit ? 0f : IntruderClips.GunWalkSpeed,
                                () => { OfficerAim(); return OfficerHit ? null : WantsToFire() ? FireOnce() : null; });
                            StopHere();
                            continue;
                        }
                    }
                }
                if (!IsTalker)
                {
                    if (!gunCalled && stance == PlayerStance.Armed && Role == OfficerRole.Cover) { gunCalled = true; StartCoroutine(SayWhenFree("vo.cop.cover.gun", 1f)); }
                    yield return null;
                    continue;
                }

                // ── The talker's orders (escalating), then the Taser or the cuffs.
                bool tased = s_tasedWave == Wave;
                if (tased && stance != PlayerStance.Aiming && Time.time - s_tasedAt > 1.2f) { yield return OfficerCuff(); nextOrder = Time.time + 1f; continue; }
                switch (stance)
                {
                    case PlayerStance.Armed:
                        if (Time.time < nextOrder) break;
                        // The Taser never landed (called off when the gun came up on us, or the one sent was hit or withdrawn):
                        // one more "Drop it", then the Taser again — else the talker stood silent with the gun still in the
                        // player's hand until the standoff timed out (audit 0.1.46).
                        if (taseCalled && !tased && Time.time - taseAt > 4f) { taseCalled = false; drop = 2; }
                        if (drop < 3) { if (OfficerSay($"vo.cop.lead.drop.{drop + 1}")) { drop++; nextOrder = Time.time + OrderGap; } }
                        else if (!taseCalled)
                        {
                            taseCalled = true; taseAt = Time.time;
                            var taser = FirstMate(OfficerRole.Cover) ?? FirstMate(OfficerRole.Backup);
                            if (taser == null || taser.BehindDoor || !taser.ReachesPlayerFromHere()) taser = this;   // (no darts through a wall)
                            taser._taseOrdered = true;
                            Debug.Log($"[HIS] Police #{Id}: third warning ignored — Taser (#{taser.Id})");
                        }
                        break;
                    case PlayerStance.Unarmed:
                        if (Time.time < nextOrder) break;
                        if (hands < 3)
                        {
                            string key = Wave >= 3 && hands == 0 && Sfx.Has("vo.cop.swat.hands") ? "vo.cop.swat.hands" : $"vo.cop.lead.hands.{hands + 1}";
                            if (OfficerSay(key)) { hands++; nextOrder = Time.time + OrderGap; }
                        }
                        else { yield return OfficerCuff(); nextOrder = Time.time + 1.5f; }  // no answer: he comes and cuffs anyway
                        break;
                    case PlayerStance.Surrendered:
                        if (turnAt < 0f) { if (OfficerSay("vo.cop.lead.turn")) turnAt = Time.time; }
                        else if (Time.time - turnAt > 2.2f && Time.time >= s_talkUntil)
                        { OfficerSay("vo.cop.lead.coming", urgent: true); yield return OfficerCuff(); nextOrder = Time.time + 1.5f; turnAt = -1f; }
                        break;
                }
                yield return null;
            }
        }

        // ───────────────────────────── aim and fire ─────────────────────────────
        /// <summary>Pistol out and coming up on the player (the raise is the gunman's; rounds wait for it).</summary>
        void GunUp()
        {
            if (_weapon != null && !_weapon.InHand) _weapon.Show(true);
            if (_gait != Gait.Gun && Upright(_gait)) SetGait(Gait.Gun);
            // A hitman's gun only comes up through his tell (GunUp(raise), research/27 §6 step 3): here it's out, at low ready.
            if (IsHitman) { HitmanLowReady("gun out"); return; }
            if (_hasAnim) _anim.SetGunPose(GunPose.Aimed, 0.4f);
            _tellDoneAt = Time.time + 0.4f;
            if (_light != null) _light.Muzzle = _weapon != null ? _weapon.Muzzle : null;
        }

        /// <summary>Every frame he's holding the player: gun on the chest, body turned to him, weapon light on in view.</summary>
        void OfficerAim()
        {
            if (IsHitman) { HitmanAim(); return; }   // low ready until his tell, a lagging aim, no light (IntruderHitman.cs)
            bool gun = _weapon != null && _weapon.InHand;
            if (_hasAnim && gun)
            {
                if (_anim.CurrentGunPose != GunPose.Aimed) _anim.SetGunPose(GunPose.Aimed, 0.4f);
                _anim.AimAt(PlayerChest);
            }
            if (Time.deltaTime > 0f && !(_agent.enabled && _agent.hasPath && _agent.velocity.sqrMagnitude > 0.04f)) FaceTowards(PlayerOnFloor(), 6f);
            if (_light != null) _light.On = gun && !BehindDoor && !OfficerOut && Current != State.Gone;
        }

        /// <summary>Deadly force right now (walking in: the same rules as the standoff, the reaction delay included).</summary>
        bool WantsToFire()
        {
            if (IsHitman) return HitmanWantsToFire();   // on sight, after the tell (research/27 §2)
            var stance = CopStance;
            bool threat = (stance == PlayerStance.Aiming && MaxAimedAt() >= FireAt) || (CopFired && stance is PlayerStance.Armed or PlayerStance.Aiming);
            if (!threat) { _fireFrom = -1f; return false; }
            if (_fireFrom < 0f) _fireFrom = Time.time + UnityEngine.Random.Range(ReactMin, ReactMax);
            // (A line, not just the gun on him: from outside a wall can be in the way — 0.1.53. With none he keeps walking in.)
            return Time.time >= _fireFrom && Time.time >= _tellDoneAt && !(MovementLocked && _lock >= Lock.React) && GunOnPlayer() && ShotLineNow();
        }

        IEnumerator FireOnce()
        {
            StopHere();
            OfficerShot();
            if (!IsHitman)
            {
                for (float t = 0f; t < UnityEngine.Random.Range(0.3f, 0.45f); t += Time.deltaTime) { OfficerAim(); yield return null; }
                yield break;
            }
            // A hitman holds for his tier's gap (mid-burst), or a beat before walking on (his burst pause runs on the move).
            float hold = Mathf.Clamp(_hitNextShot - Time.time, 0.1f, 0.45f);
            for (float t = 0f; t < hold; t += Time.deltaTime) { OfficerAim(); yield return null; }
        }

        /// <summary>One round through the gunman's path (<see cref="FireShot"/>), reported to the police response.</summary>
        bool OfficerShot(float accuracy = OfficerAccuracy)
        {
            if (IsHitman) return HitmanShot();   // his tier's accuracy, burst and magazine
            var ps = PlayerState.Instance;
            float before = ps != null ? ps.LastHitTime : 0f;
            if (!FireShot(accuracy)) return false;
            bool hit = ps != null && ps.LastHitTime != before;
            Report(hit ? "fired hit" : "fired miss");
            var pr = Police;
            if (pr != null) pr.OfficerFired(this, hit);
            return true;
        }

        // ───────────────────────────── Taser ─────────────────────────────
        /// <summary>The cover swaps to the yellow Taser: "Taser! Taser!", a beat on target, the hit (PoliceResponse.Tased).</summary>
        IEnumerator OfficerTase()
        {
            if (!ReachesPlayerFromHere()) { Report("Taser: no line through the doorway"); yield break; }   // (0.1.53: not even announced)
            OfficerSay("vo.cop.cover.taser", warning: true);
            ShowTaser(true);
            for (float t = 0f; t < 0.9f; t += Time.deltaTime)
            {
                if (Current == State.Dead || OfficerHit || CopFired || (CopStance == PlayerStance.Aiming && MaxAimedAt() >= FireAt)) { ShowTaser(false); yield break; }
                OfficerAim();
                yield return null;
            }
            if (!ReachesPlayerFromHere()) { ShowTaser(false); Report("Taser: no line through the doorway"); yield break; }   // (0.1.53)
            s_tasedWave = Wave; s_tasedAt = Time.time;
            Report("tased");
            var pr = Police;
            if (pr != null) pr.Tased(this);
            for (float t = 0f; t < 1.4f && Current != State.Dead; t += Time.deltaTime) { OfficerAim(); yield return null; }
            ShowTaser(false);
        }

        // ───────────────────────────── cuffs ─────────────────────────────
        /// <summary>
        /// Stop the arrest and hold again: a gun up on us, or a weapon back in hand (untased). Not "fired" on its own: that
        /// stays set for the wave, and a player who fired and then gave up still gets cuffed.
        /// </summary>
        bool CuffAbort() => CopStance == PlayerStance.Aiming || (CopStance == PlayerStance.Armed && s_tasedWave != Wave);

        /// <summary>
        /// Holster (PistolLower), walk round the furniture to 0.6 m behind the player (or as close as he can get), then the
        /// cuffs a hand at a time: a click and a ratchet at each controller with a short rumble in it → PoliceResponse.Cuffed.
        /// </summary>
        IEnumerator OfficerCuff()
        {
            Report("coming to cuff");
            if (_hasAnim) _anim.SetGunPose(GunPose.None);
            if (_hasAnim && Upright(_gait) && Gesture(Act.PistolLower))
                for (float until = Time.time + 0.7f; Time.time < until && MovementLocked && _lock == Lock.Gesture; ) yield return null;
            CancelAct();
            if (_weapon != null && _weapon.InHand) _weapon.Show(false);
            if (_light != null) _light.On = false;
            if (Upright(_gait)) SetGait(Gait.Calm);
            if (!_agent.enabled || !_agent.isOnNavMesh) yield return EnterRoom(HoldSpot(Door, 1f)); // (the cover took over outside)
            float lastProgress = Time.time, nextPath = 0f, giveUp = Time.time + 15f;
            var lastPos = transform.position;
            while (Current != State.Dead && Time.time < giveUp)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                if (OfficerHit) yield break;
                if (CuffAbort()) { Report("cuffing aborted"); GunUp(); yield break; }
                var target = BehindPlayer();
                if (FlatDistance(transform.position, target) < 0.15f) break;
                if (_agent.enabled && _agent.isOnNavMesh && !_agent.isOnOffMeshLink && Time.time >= nextPath)
                { _agent.speed = IntruderClips.WalkSpeed; _agent.SetDestination(target); nextPath = Time.time + 0.3f; }
                if (FlatDistance(transform.position, lastPos) > 0.08f) { lastPos = transform.position; lastProgress = Time.time; }
                else if (Time.time - lastProgress > 2.5f) { Debug.Log($"[HIS] Intruder #{Id}: officer can't get behind the player; cuffs from {FlatDistance(transform.position, PlayerOnFloor()):F2} m"); break; }
                yield return null;
            }
            if (Current == State.Dead) yield break;
            StopHere();
            for (float t = 0f; t < 0.35f; t += Time.deltaTime) { FaceTowards(PlayerOnFloor(), 8f); yield return null; }
            yield return CuffHand(left: true);
            if (OfficerHit || CuffAbort()) { GunUp(); yield break; }
            yield return CuffHand(left: false);
            _cuffedDone = true;
            Report("cuffed");
            var pr = Police;
            if (pr != null) pr.Cuffed(this);
            OfficerSay("vo.cop.lead.arrest", urgent: true);
        }

        /// <summary>0.6 m behind the player on the room NavMesh; a wall there → beside him on this officer's side.</summary>
        Vector3 BehindPlayer()
        {
            var p = PlayerOnFloor();
            var nav = Nav;
            var filter = nav != null ? nav.Filter : new NavMeshQueryFilter { agentTypeID = _agent.agentTypeID, areaMask = NavMesh.AllAreas };
            var back = _player != null ? Flat(_player.forward).normalized : Flat(p - transform.position).normalized;
            if (NavMesh.SamplePosition(p - back * CuffBehind, out var hit, 0.35f, filter)) return hit.position;
            var side = p + Flat(transform.position - p).normalized * (CuffBehind + 0.1f);
            if (NavMesh.SamplePosition(side, out hit, 0.6f, filter)) return hit.position;
            return transform.position;
        }

        IEnumerator CuffHand(bool left)
        {
            var cs = CommandSystem.Instance;
            var hand = cs != null ? (left ? cs.Left : cs.Right) : null;
            var at = hand != null ? hand.transform.position
                   : PlayerHead + Vector3.down * 0.7f + (_player != null ? _player.right : Vector3.right) * (left ? -0.18f : 0.18f);
            if (Sfx.Has("cuffs.click")) Sfx.Play("cuffs.click", at, 0.9f);
            if (hand != null) hand.Haptic(0.9f, 0.05f, 0.9f);
            yield return new WaitForSeconds(0.2f);
            if (Sfx.Has("cuffs.ratchet")) Sfx.Play("cuffs.ratchet", at, 0.8f);
            for (int i = 0; i < 5; i++) { if (hand != null) hand.Haptic(0.5f, 0.02f, 1f); yield return new WaitForSeconds(0.06f); }
            yield return new WaitForSeconds(0.3f);
        }

        // ───────────────────────────── hit: out of the fight ─────────────────────────────
        /// <summary>Wounded: "I'm hit!", out of the fight (PoliceResponse.OfficerDown, not killed), back out the door and off the step.</summary>
        IEnumerator OfficerWounded()
        {
            _officerOut = true;
            Report("down wounded");
            // Hitmen: no "I'm hit!" and no "Man down!" — the pain grunts only (research/27 §5).
            if (!IsHitman) OfficerSay(Role == OfficerRole.Lead ? "vo.cop.lead.hit" : "vo.cop.cover.hit", urgent: true);
            var pr = Police;
            if (pr != null) pr.OfficerDown(this, false);
            if (_light != null) _light.On = false;
            ShowTaser(false);
            var mate = IsHitman ? null : FirstMate(OfficerRole.Cover) ?? FirstMate(OfficerRole.Lead) ?? FirstMate(OfficerRole.Backup);
            if (mate != null) mate.StartCoroutine(mate.SayWhenFree("vo.cop.cover.officerdown", 1.5f));
            if (_hasAnim) _anim.SetGunPose(GunPose.None);
            for (float until = Time.time + 2f; Time.time < until && MovementLocked && Current != State.Dead; ) yield return null; // the flinch / the fall first
            if (Current == State.Dead) yield break;
            // Out hurt, not strolling (Brett 0.1.26): a limp (legs shot out: he crawls), a groan on the way.
            if (Upright(_gait)) SetGait(Gait.Limp);
            StartCoroutine(GroanLater(UnityEngine.Random.Range(1.2f, 2f)));
            var door = Door;
            var nav = Nav;
            if (_agent.enabled && _agent.isOnNavMesh && nav != null && nav.Ready && nav.OnApron(_outsidePoint, out var exit))
            {
                if (_hasAnim) _anim.SetSpeedOverride(null);
                yield return PathTo(exit, () => GaitSpeed, null);
                if (Current == State.Dead) yield break;
            }
            if (_agent.enabled) _agent.enabled = false;
            var porch = PorchFor(door);
            var off = porch != null ? Nearer(porch.OffLeft, porch.OffRight)
                    : transform.position + (door != null ? Flat(door.Outward).normalized : -transform.forward) * 3f;
            off.y = _floorY;
            while (Current != State.Dead && FlatDistance(transform.position, off) > 0.05f)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                float s = MovementLocked ? 0f : GaitSpeed;
                if (_hasAnim) _anim.SetSpeedOverride(s);
                FaceTowards(off, 8f);
                var next = Vector3.MoveTowards(transform.position, off, s * Time.deltaTime); next.y = _floorY;
                transform.position = next;
                if (_visible && door != null && Vector3.Dot(transform.position - door.Center, door.Outward) > 0.5f) SetVisible(false);
                yield return null;
            }
            if (Current == State.Dead) yield break;
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            SetVisible(false);
            SetState(State.Gone);
            Report("backed out, gone");
        }

        IEnumerator GroanLater(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
            if (Current != State.Dead && Current != State.Gone) Cry("intruder.pain", BehindDoor ? 1.1f : 0.8f, DoorCryLowpass);
        }

        // ───────────────────────────── places ─────────────────────────────
        /// <summary>A straight move outside (no NavMesh out there) at <paramref name="speed"/>; stops if he's hit.</summary>
        IEnumerator MoveOutside(Vector3 to, Func<float> speed, bool aim = false)
        {
            to.y = _floorY;
            while (Current != State.Dead && FlatDistance(transform.position, to) > 0.04f && !OfficerHit)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }
                StepOutside(to, speed(), aim);
                yield return null;
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
        }

        /// <summary>One frame of a straight move outside toward <paramref name="to"/> at <paramref name="sp"/> (aimed: gun on the player).</summary>
        void StepOutside(Vector3 to, float sp, bool aim)
        {
            if (!aim && _hasAnim) _anim.SetGunPose(sp > AimedWalk + 0.1f ? GunPose.None : GunPose.Ready);
            float s = MovementLocked ? 0f : sp;
            if (_hasAnim) _anim.SetSpeedOverride(s);
            if (aim) OfficerAim();
            FaceTowards(to, aim ? 3f : 8f);
            var next = Vector3.MoveTowards(transform.position, to, s * Time.deltaTime);
            next.y = _floorY;
            transform.position = next;
        }

        Vector3 DoorFloor(HIS.Room.VirtualDoor door)
        {
            var t = door != null ? door.Threshold : _insidePoint;
            return new Vector3(t.x, _floorY, t.z);
        }

        Vector3 LatchSide(HIS.Room.VirtualDoor door) => door != null ? Flat(door.HingeToLatch).normalized : Vector3.Cross(Vector3.up, Flat(_insidePoint - _outsidePoint).normalized);
        Vector3 OutwardFlat(HIS.Room.VirtualDoor door) => door != null ? Flat(door.Outward).normalized : Flat(_outsidePoint - _insidePoint).normalized;

        /// <summary>Beside the door on the step, against the house wall: lead/backup on the latch side, cover on the hinge side.</summary>
        Vector3 StackSpot(HIS.Room.VirtualDoor door)
        {
            // Brett 0.1.26: "shooting through the door at the cops didnt work perfect" — stacked 0.35 m past the edges they
            // stood behind the real wall, which stops rounds, while the camera showed them at the door. Now each one's body
            // is half across an edge of the leaf (the backup a step behind the lead): shots through the door find them.
            float half = door != null ? door.Opening.width * 0.5f : 0.45f;
            int slot = Slot;
            float side = slot == 1 ? -1f : 1f;
            float lateral = slot >= 2 ? half + 0.25f : half - 0.05f;
            float outward = slot >= 2 ? 1.0f : 0.45f;
            var spot = DoorFloor(door) + OutwardFlat(door) * outward + LatchSide(door) * side * lateral;
            // A fallen officer (an earlier wave) lies there: a step further out, not standing in him on the door camera.
            foreach (var o in s_squad)
                if (o != null && o != this && o.Current == State.Dead && FlatDistance(o.transform.position, spot) < 0.6f)
                { spot += OutwardFlat(door) * 0.7f; break; }
            return spot;
        }

        /// <summary>1–1.5 m in, off to one side (<paramref name="side"/>: +1 latch, −1 hinge), on the room NavMesh, not on top of the player.</summary>
        Vector3 HoldSpot(HIS.Room.VirtualDoor door, float side)
        {
            var nav = Nav;
            var filter = nav != null ? nav.Filter : new NavMeshQueryFilter { agentTypeID = _agent.agentTypeID, areaMask = NavMesh.AllAreas };
            var th = DoorFloor(door); var inward = -OutwardFlat(door); var lat = LatchSide(door) * side;
            var player = PlayerOnFloor();
            foreach (float d in new[] { 1.25f, 1.0f, 1.5f, 0.8f })
                foreach (float l in new[] { 0.5f, 0.35f, 0.7f, 0.2f, 0f })
                {
                    var p = th + inward * d + lat * l;
                    if (!NavMesh.SamplePosition(p, out var hit, 0.2f, filter)) continue;
                    if (DoorwayNav.AnyBeyond(hit.position) || FlatDistance(hit.position, player) < 1.1f) continue;
                    if (!OffWitness(hit.position, player)) continue;   // (a Marshal night: not in front of Ricky, research/27 §2)
                    bool taken = false;
                    for (int i = 0; i < s_squad.Count; i++) { var o = s_squad[i]; if (IsMate(o) && FlatDistance(o.transform.position, hit.position) < 0.6f) taken = true; }
                    if (!taken) return hit.position;
                }
            return nav != null && nav.Ready ? nav.InsideEnd : th + inward * 0.8f;
        }

        /// <summary>
        /// Where he holds the player from once in (Brett 0.1.26: move in, don't crowd the door): round the player, the lead on
        /// the latch side, the backup (or with no backup, the cover) on the hinge side, a third between them; 1.6–2.6 m from him, on
        /// the room NavMesh, with a clear line to his chest and not on a mate's spot. None fits: the old spot just inside.
        /// </summary>
        Vector3 SpreadSpot(HIS.Room.VirtualDoor door)
        {
            var nav = Nav;
            var filter = nav != null ? nav.Filter : new NavMeshQueryFilter { agentTypeID = _agent.agentTypeID, areaMask = NavMesh.AllAreas };
            var player = PlayerOnFloor();
            var toDoor = Flat(DoorFloor(door) - player);
            float doorDist = toDoor.magnitude;
            toDoor.Normalize();
            float latch = Mathf.Sign(Vector3.Dot(Vector3.Cross(Vector3.up, toDoor), LatchSide(door))); // (+angle turns toward Cross(up, v))
            // Two of them: the cover takes the hinge side (a middle spot sat in the doorway in a small room). Three: the middle.
            int slot = Slot;
            bool middle = slot == 1 && AtMyDoor >= 3;
            float baseAngle = slot == 0 ? 40f * latch : middle ? 0f : -40f * latch;
            float baseR = Mathf.Clamp(Mathf.Min(middle ? 2.4f : 2.1f, doorDist - 0.6f), 1.6f, 2.6f);
            int tried = 0;
            // (Walking in: from the doorway; already in: from where he stands.)
            var from = _agent.enabled && _agent.isOnNavMesh ? transform.position : nav != null && nav.Ready ? nav.InsideEnd : DoorFloor(door);
            // The lane in from the doorway to the player: whoever isn't last in keeps off it, or the next man can't get past
            // (a small room's gap between furniture fits one). Slot 1 at each door comes in last.
            Vector3[] lane = null;
            if (slot != 1 && NavMesh.SamplePosition(player, out var ph, 1f, filter))
            {
                s_route ??= new NavMeshPath();
                var inside = nav != null && nav.Ready ? nav.InsideEnd : DoorFloor(door);
                if (NavMesh.CalculatePath(inside, ph.position, filter, s_route) && s_route.status == NavMeshPathStatus.PathComplete) lane = s_route.corners;
            }
            // Pass 0: off the lane, a clear route; 1: a clear route; 2: just a line on him (they squeeze past each other).
            for (int pass = lane != null ? 0 : 1; pass <= 2; pass++)
                foreach (float da in new[] { 0f, 15f, -15f, 30f, -30f, 50f, -50f, 75f, -75f })
                    foreach (float r in new[] { baseR, baseR + 0.4f, baseR - 0.3f, 1.6f })
                    {
                        var p = player + Quaternion.AngleAxis(baseAngle + da, Vector3.up) * toDoor * r;
                        if (!NavMesh.SamplePosition(p, out var hit, 0.25f, filter)) continue;
                        var q = hit.position;
                        if (DoorwayNav.AnyBeyond(q) || FlatDistance(q, player) < 1.5f) continue;
                        // A Marshal night (research/27 §2): off Ricky and off your line to him — your round stops at the first
                        // body (Firearm), so a man in front of Ricky would turn your miss into a hit on him. No witness: no-op.
                        if (!OffWitness(q, player)) { tried++; continue; }
                        bool taken = false;
                        for (int i = 0; i < s_squad.Count; i++)
                        {
                            var o = s_squad[i];
                            if (IsMate(o) && (FlatDistance(o.transform.position, q) < 0.7f || (!float.IsNaN(o._spot.x) && FlatDistance(o._spot, q) < 0.8f))) { taken = true; break; }
                        }
                        if (taken || !LineToPlayer(q)) { tried++; continue; }
                        if (pass == 0 && OnLane(lane, q)) { tried++; continue; }
                        if (pass <= 1 && !RouteClearOfMates(from, q, filter)) { tried++; continue; }
                        _spot = q;
                        Debug.Log($"[HIS] {(IsHitman ? "Hitman" : "Police")} #{Id} ({Role}): spot {q:F2}, {FlatDistance(q, player):F2} m from the player at {baseAngle + da:F0}° (pass {pass})");
                        return q;
                    }
            _spot = HoldSpot(door, slot >= 1 ? -1f : 1f);
            Debug.Log($"[HIS] {(IsHitman ? "Hitman" : "Police")} #{Id} ({Role}): no spread spot round the player at {player:F2} (door {doorDist:F2} m, {tried} blocked/taken) — holds just inside");
            return _spot;
        }

        static bool OnLane(Vector3[] lane, Vector3 q)
        {
            for (int i = 0; lane != null && i + 1 < lane.Length; i++) if (SegmentGap(lane[i], lane[i + 1], q) < 0.6f) return true;
            return false;
        }

        static NavMeshPath s_route; // (made on first use: Unity won't build one while loading the type)

        /// <summary>The NavMesh route <paramref name="from"/> → <paramref name="to"/> exists and doesn't squeeze past a mate
        /// (standing, or where he's going): a room's gaps between furniture fit one man, and the second one stalled behind him.</summary>
        bool RouteClearOfMates(Vector3 from, Vector3 to, NavMeshQueryFilter filter)
        {
            s_route ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, filter, s_route) || s_route.status != NavMeshPathStatus.PathComplete) return false;
            var c = s_route.corners;
            for (int m = 0; m < s_squad.Count; m++)
            {
                var o = s_squad[m];
                if (!IsMate(o)) continue;
                var a = o.transform.position; var b = float.IsNaN(o._spot.x) ? a : o._spot;
                for (int i = 0; i + 1 < c.Length; i++)
                    if (SegmentGap(c[i], c[i + 1], a) < 0.55f || SegmentGap(c[i], c[i + 1], b) < 0.55f) return false;
            }
            return true;
        }

        static float SegmentGap(Vector3 p, Vector3 q, Vector3 x)
        {
            p.y = q.y = x.y = 0f;
            var d = q - p; float t = d.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(x - p, d) / d.sqrMagnitude) : 0f;
            return Vector3.Distance(p + d * t, x);
        }

        float _noLineFor;

        /// <summary>He can see the player from where he stands: nothing of the room between them, and from outside, through a doorway.</summary>
        bool HasLineOnPlayer() => LineToPlayer(transform.position) && InViewOfPlayer();

        /// <summary>
        /// Where to go for a line on the player: out beside the doorway, first onto its centre line on the apron (the way in may
        /// be blocked — a body across the threshold); from there, or inside, a spread spot as usual.
        /// </summary>
        Vector3 LineSpot()
        {
            var nav = Nav;
            return OutsideTheRoom && nav != null && nav.OutsideEnd.sqrMagnitude > 0f && FlatDistance(nav.OutsideEnd, transform.position) > 0.3f
                ? nav.OutsideEnd : SpreadSpot(Door);
        }

        /// <summary>From standing at <paramref name="floor"/>, nothing of the room (walls, furniture) between his eyes and the player's chest.</summary>
        bool LineToPlayer(Vector3 floor)
        {
            var eye = new Vector3(floor.x, _floorY + 1.45f, floor.z);
            var to = PlayerChest - eye;
            if (s_roomMask == 0) s_roomMask = LayerMask.GetMask("Room");
            return !Physics.Raycast(eye, to.normalized, Mathf.Max(0f, to.magnitude - 0.3f), s_roomMask, QueryTriggerInteraction.Ignore);
        }
        static int s_roomMask;

        /// <summary>No start from the lead: by the police cars, else the street end of the path, 1.2 m aside per officer.</summary>
        Vector3 FallbackStart()
        {
            int index = 0;
            foreach (var o in s_squad) { if (o == this) break; if (o != null && o.Wave == Wave) index++; }
            if (HIS.Security.PoliceCars.TryGetOfficerStart(Wave, index, out var at, Faction)) return at;
            var path = PorchFor(Door)?.Approach;
            if (path != null && path.Length > 1)
            {
                var aside = Vector3.Cross(Vector3.up, Flat(path[1] - path[0]).normalized);
                return path[0] + aside * ((index % 2 == 0 ? -1f : 1f) * 1.2f * (1 + index / 2));
            }
            return _outsidePoint + OutwardFlat(Door) * 6f;
        }
    }
}
