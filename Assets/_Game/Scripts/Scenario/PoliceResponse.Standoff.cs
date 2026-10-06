using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Weapons;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Scenario
{
    /// <summary>How the standoff ended (the final card). Append only (ScenarioDirector reads it). The last three end a Marshal
    /// night's hitman waves (research/27): the last wave (HitmanTier.Waves) beaten, the witness killed, the player killed.</summary>
    public enum StandoffEnding { None, Arrested, KilledByPolice, Released, Cancelled, Delivered, WitnessKilled, KilledByHitmen }

    /// <summary>
    /// The standoff itself (research/22 with Brett's changes, 0.1.25): wave 1 = two officers; you surrender (weapons down, hands
    /// up — never kneel or lie down), you're cuffed and booked, and only then do you see the call and your charges. Point a
    /// gun at them or fire and it's a fair gunfight; beat a wave and a bigger one comes (3 officers, then SWAT), until you
    /// give up or they kill you. The card at the end tallies every crime and estimates the sentence (<see cref="Sentencing"/>).
    /// A US Marshal night (research/27) runs the same machine with <see cref="SquadFaction.Hitmen"/> (<see cref="RunHitmen"/>):
    /// <see cref="HitmanTier.Waves"/> waves (five since Brett 0.1.57), silent, no orders, the broken doors shut between waves,
    /// the night's visitors between some of them (<see cref="BetweenHitmanWaves"/>, after the street tidy:
    /// <see cref="TidyForVisitor"/>), and its own card; every hitman difference here is behind <c>Hitmen</c>, so a police run
    /// goes exactly as before.
    /// </summary>
    public partial class PoliceResponse
    {
        /// <summary>Police respond to shootings at all (tests of the old flow switch it off).</summary>
        public static bool Enabled = true;
        /// <summary>Chance they come after a good call where you fired. 0: bad calls only (Brett 0.1.26: "even when I made a
        /// good call police still got called"). Research/22 option 4 had 0.33 so their arrival didn't give the call away.</summary>
        public static float GoodCallOdds = 0f;

        /// <summary>A wave at one door: 2, then 3. With a second door (Brett 0.1.29): 2 front, then 2 + 1 side, then 2 + 2.</summary>
        const int MaxAlive = 3;
        const float EntryHoldSeconds = 2.5f, WaveLullSeconds = 5f, StandoffTimeout = 150f;
        /// <summary>An officer out there (not at the door, not inside) who hasn't moved or changed what he's doing for this long is stuck: withdrawn.</summary>
        const float StuckSeconds = 30f;

        public StandoffEnding Ending { get; private set; }
        /// <summary>The card to show at the end (the delayed verdict).</summary>
        public Verdict EndCard { get; private set; }

        readonly List<Crime> _crimes = new();
        Verdict _call;
        bool _goodCall, _ended, _tased, _aimedAtPolice, _entryTimerOn;
        int _killed, _wounded, _wavesBeaten, _spawnedThisWave, _downThisWave, _spawnedTotal;
        float _stackedAt, _handsUpFor, _waveStart;
        HIS.Intruder.IntruderSpawner _spawner;

        /// <summary>This run sends a Marshal night's hitmen (research/27), not the police.</summary>
        bool Hitmen => Faction == SquadFaction.Hitmen;
        /// <summary>The log prefix: "Police" (unchanged lines for a police run) or "Hitmen".</summary>
        string Tag => Hitmen ? "Hitmen" : "Police";

        /// <summary>Do the police come after this call? Every bad call; some good calls where you fired; never a perfect one,
        /// never if you're already dead, never while a run is already out (a Marshal night's hitmen: research/27 §6 step 2), and
        /// never for a US Marshal (Brett 2026-10-04 (a): a Marshal's bad call is the arrest, straight to the booking — the police
        /// never come to fight him; the gunfights are the hitmen's).</summary>
        public static bool ShouldRespond(in Verdict v, ScenarioRecord rec)
        {
            if (!Enabled || Instance == null || Instance.Active || rec == null || rec.End == EndReason.PlayerDied) return false;
            if (Judge.Role == Role.Marshal) return false;
            if (v.Tier == Tier.Bad) return true;
            return v.Tier == Tier.Good && rec.Force.Count > 0 && GoodCallOdds > 0f && Random.value < GoodCallOdds;
        }

        /// <summary>
        /// A US Marshal night's hitman waves (research/27 §2; five since Brett 0.1.57): the same machine with <see cref="SquadFaction.Hitmen"/>.
        /// No orders, radio or bullhorn; the last wave beaten → <see cref="StandoffEnding.Delivered"/>; the player killed →
        /// <see cref="StandoffEnding.KilledByHitmen"/>. When it returns, <see cref="EndCard"/> holds the night's card.
        /// </summary>
        public IEnumerator RunHitmen() => Run(default, SquadFaction.Hitmen);

        /// <summary>Run the whole standoff; when it returns, <see cref="EndCard"/> holds the verdict to show.</summary>
        /// <param name="faction">Who comes: the police after <paramref name="call"/> (the default), or the hitmen (<see cref="RunHitmen"/>).</param>
        /// <param name="reported">What the dispatcher is told happened (wave 1's radio line): a shooting (the default), a
        /// stabbing (Stab or Slash) or an assault (Punch) — never "Shots fired" when nothing was fired.</param>
        /// <param name="badShot">The bad call was a shooting (<see cref="Judge.BadShot"/>): the card's charge carries the firearm
        /// years (PC 12022.5(a)). Not the radio's fact: any round fired is "Shots fired", a miss or a justified one included.</param>
        public IEnumerator Run(Verdict call, SquadFaction faction = SquadFaction.Police, ForceKind reported = ForceKind.Shot, bool badShot = true)
        {
            Faction = faction;
            _reported = reported;
            _badShot = badShot;
            _call = call; _goodCall = call.Tier != Tier.Bad;
            _crimes.Clear(); _officers.Clear(); _aimed.Clear(); _atDoor.Clear(); _counted.Clear(); _woundedSet.Clear(); _progress.Clear();
            _ended = _tased = _aimedAtPolice = _entryTimerOn = false; PlayerFiredAtPolice = false; FiredThisWave = false; EntryCleared = false;
            _killed = _wounded = _wavesBeaten = _spawnedTotal = 0; _handsUpFor = 0f; _firstStackAt = -1f; _timedOutWave = 0;
            Ending = StandoffEnding.None; Wave = 0; Active = true;
            HIS.Intruder.Intruder.ResetStandoffStatics(); // the last standoff's Taser/contact state (even if no wave-1 lead spawns)
            _spawner = FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
            Firearm.Fired += OnPlayerFired;
            if (PlayerState.Instance != null) PlayerState.Instance.Died += OnPlayerDied;
            if (Hitmen) Debug.Log($"[HIS] Hitmen: the night's waves begin ({HitmanTier.Waves} waves, {HitmenTotal()} men)");
            else Debug.Log($"[HIS] Police responding ({(_goodCall ? "good" : "bad")} call, verdict withheld)");

            yield return CallWave(1);
            while (!_ended)
            {
                if (Time.deltaTime > 0f) Tick();
                if (!_ended && WaveBeaten())
                {
                    _wavesBeaten++;
                    Debug.Log($"[HIS] {Tag}: wave {Wave} beaten");
                    if (Hitmen)
                    {
                        // Research/27 §2: the last wave beaten (HitmanTier.Waves: five since Brett 0.1.57) = the relief makes it at 2 AM.
                        if (Wave >= HitmanTier.Waves) End(StandoffEnding.Delivered);
                        else
                        {
                            yield return Lull(HitmanTier.Lull);
                            // Brett 0.1.57: a visitor between the waves (the courier after wave 1, Sal after wave 3: the director's).
                            // The squad is idle while it plays (this loop is suspended: no Tick); his bad call, Ricky hit or your
                            // death in it Cancel() the run there (_ended: no next wave, no card of its own).
                            if (!_ended && BetweenHitmanWaves != null)
                            {
                                var between = BetweenHitmanWaves(Wave);
                                if (between != null) yield return between;
                            }
                            if (!_ended) yield return CallWave(Wave + 1);
                        }
                    }
                    else
                    {
                        Radio("vo.cop.radio.officerdown");
                        yield return Lull(WaveLullSeconds);
                        if (!_ended) yield return CallWave(Wave + 1);
                    }
                }
                if (!_ended && Time.time - _waveStart > StandoffTimeout)
                {
                    // Hitmen don't arrest anyone: the wave's stragglers withdraw and it counts as beaten (research/27 §2), once.
                    if (Hitmen) { if (_timedOutWave != Wave) { _timedOutWave = Wave; WithdrawWave("timed out"); } }
                    else { Debug.LogWarning("[HIS] Police: standoff timed out — arrest"); End(StandoffEnding.Arrested); }
                }
                yield return null;
            }

            // Cancelled: Cancel() already did the teardown (and raised Ended). This tail only runs here when the iterator
            // outlived the director's StopCoroutine (a nested IEnumerator can) and saw _ended on its next step — never twice.
            if (!Active) yield break;
            EndCard = Hitmen ? ComposeHitmanCard() : ComposeCard();
            TearDown();
            if (Hitmen) Debug.Log($"[HIS] Hitmen over: {Ending} — {EndCard.Headline} ({HitmenStopped} of {HitmenTotal()} stopped, {_wavesBeaten} wave(s) beaten)");
            else Debug.Log($"[HIS] Police standoff over: {Ending} — {EndCard.Headline} ({_crimes.Count} charge(s), {_wavesBeaten} wave(s) beaten)");
        }

        /// <summary>
        /// Stop now (session reset / mode switch / the bad-call preview): no card. The director stops its round coroutine right
        /// after this, and with it (usually) this Run, so its tail would never run: the teardown happens here instead — the
        /// player's Fired/Died handlers off (else one more pair per interrupted standoff, still counting shots and deaths),
        /// Active false, Ended raised once. The officers and the cars go with the director's own clear-up (ClearWorld destroys
        /// every Intruder; OutcomeShow.Clear raises Cleared, which parks the cars), the same as after a normal end. If the Run
        /// iterator does live on, _ended stops its loops on the next frame (Lull, CallWave, the main loop) and its tail skips.
        /// </summary>
        public void Cancel()
        {
            if (!Active) return;
            _ended = true; Ending = StandoffEnding.Cancelled;            // (over even if it had just ended: Cancelled wins, no card)
            TearDown();
            Debug.Log($"[HIS] {(Hitmen ? "Hitmen" : "Police standoff")} cancelled (wave {Wave}, {_wavesBeaten} wave(s) beaten)");
        }

        /// <summary>The end of a standoff, once (Run's tail or Cancel): handlers off, not Active, Ended raised.</summary>
        void TearDown()
        {
            if (!Active) return;
            Firearm.Fired -= OnPlayerFired;
            if (PlayerState.Instance != null) PlayerState.Instance.Died -= OnPlayerDied;
            Active = false;
            RaiseEnded();
        }

        IEnumerator CallWave(int wave)
        {
            Wave = wave; _atDoor.Clear(); EntryCleared = false; _entryTimerOn = false; _downThisWave = 0; _spawnedThisWave = 0;
            _waveStart = Time.time; FiredThisWave = false; _firstStackAt = -1f;
            if (Hitmen) { CallHitmanWave(wave); yield break; }
            RaiseWaveCalled(wave);                                            // the cars drive in on the door camera
            Radio(wave == 1 ? RespondingLine(_reported) : wave >= 3 ? "vo.cop.radio.swat" : "vo.cop.radio.backup");
            // The bullhorn from the street while they pull up (from outside the front door, muffled through it).
            // The front team rides in (Brett 0.1.33): driver, partner, a third in the back — the truck's in its box. They're in
            // their seats as it pulls up (shootable through the glass; the driver shot, it crashes) and get out when it stops.
            bool twoDoors = _spawner != null && _spawner.DoorCount > 1;
            int front = wave == 1 || twoDoors ? 2 : MaxAlive, side = !twoDoors || wave == 1 ? 0 : wave == 2 ? 1 : 2;
            // The side team rides in too (Brett 0.1.42: "if I shoot out the truck the two coming to the back shouldn't get to
            // the back"): the cruiser's back seat, the truck's box. Parked, they get out with the rest and go round the house to
            // the side door (IntruderPolice.RoundToDoor); shot to a stop (the driver killed), everyone left comes to the front.
            for (int i = 0; i < front + side && !_ended; i++)
                Spawn(i == 0 ? OfficerRole.Lead : i == 1 ? OfficerRole.Cover : OfficerRole.Backup, wave, i, 0, ride: true,
                      roundTo: i >= front ? 1 : -1, roundSlot: i - front);
            yield return Lull(1.8f);
            // Cancelled (Cancel(): the round was dropped): no bullhorn into the fresh round. (A normal end still plays it, as before.)
            if (Ending == StandoffEnding.Cancelled) yield break;
            Bullhorn("vo.cop.bullhorn.weapons");
            // Its vehicle stops (or ~8 s: the SWAT truck is the slowest in; a crash takes a moment) — then "coming".
            float until = Time.time + 8f;
            while (!_ended && Time.time < until && !CarsStopped(wave)) yield return null;
            yield return Lull(2.5f);
            if (Ending == StandoffEnding.Cancelled) yield break;
            Bullhorn("vo.cop.bullhorn.coming");
        }

        void Spawn(OfficerRole role, int wave, int index, int door, bool ride = false, int roundTo = -1, int roundSlot = 0)
        {
            if (_spawner == null) return;
            // A hitman is still Intent.Police ("squad member": the generic squad checks keep working, research/27 §6 step 1);
            // his Faction picks the brain and the look. Nobody judges him: the director never hears his ForceApplied.
            var def = Hitmen
                ? new ScenarioDef
                {
                    Name = "Hitman", Intent = Intent.Police, Entry = EntryType.ForcedKick,
                    Weapon = WeaponKind.Gun, Identity = Identity.Stranger, WhoItWas = "A contract killer",
                }
                : new ScenarioDef
                {
                    Name = wave >= 3 ? "SWAT officer" : "Police officer", Intent = Intent.Police, Entry = EntryType.ForcedKick,
                    Weapon = WeaponKind.Gun, Identity = Identity.Stranger, WhoItWas = "A police officer",
                };
            var o = _spawner.SpawnScenario(def, door);                         // the front door (research/22 §3.4); later waves also the side
            if (o == null) return;
            var start = door == 0 ? OfficerStart(wave, index) : SideStart(door, index);
            // Riding: seat = his index (the truck's driver is its lead; the rest are in the box). Board fails → on foot.
            o.ConfigureOfficer(role, wave, start, ride ? index : -1, roundTo, roundSlot, Faction);
            o.name = Hitmen ? $"Hitman {wave}.{index + 1}" : $"Officer {wave}.{index + 1} ({role}{(door > 0 || roundTo > 0 ? ", side door" : "")})";
            o.Died += who => OfficerDown(who, true);
            _officers.Add(o);
            _spawnedThisWave++;
            _spawnedTotal++;
        }

        /// <summary>By his wave's vehicle if it has stopped, else out on the street in front of the house.</summary>
        static Vector3 OfficerStart(int wave, int index)
        {
            if (HIS.Security.PoliceCars.TryGetOfficerStart(wave, index, out var at)) return at;
            var door = HIS.Room.VirtualDoor.Instance;
            var porch = door != null ? HIS.Security.PorchSet.For(door) : null;
            var path = porch != null ? porch.Approach : null;
            if (path != null && path.Length > 0)
            {
                var side = Vector3.Cross(Vector3.up, (path.Length > 1 ? path[1] - path[0] : Vector3.forward).normalized);
                return path[0] + side * ((index % 2 == 0 ? -1f : 1f) * (1.2f + 0.6f * (index / 2)));
            }
            return door != null ? door.Center + door.Outward * 6f : Vector3.zero;
        }

        /// <summary>The side team comes in at the far end of that door's approach (its yard gate), a step apart.</summary>
        internal static Vector3 SideStart(int door, int index)
        {
            var d = door < HIS.Room.VirtualDoor.All.Count ? HIS.Room.VirtualDoor.All[door] : null;
            var porch = d != null ? HIS.Security.PorchSet.For(d) : null;
            var path = porch != null ? porch.Approach : null;
            if (path != null && path.Length > 1)
            {
                var back = (path[0] - path[1]).normalized;
                return path[0] + back * (0.9f * index);
            }
            return d != null ? d.Center + d.Outward * (5f + index) : Vector3.zero;
        }

        static bool CarsStopped(int wave) => HIS.Security.PoliceCars.TryGetOfficerStart(wave, 0, out _);

        // ───────────── each frame ─────────────

        void Tick()
        {
            // Out of the fight. Dead ones reported themselves (Died); one that's Gone or destroyed without a report (withdrawn,
            // or removed by something else) still counts, or the wave would wait for him forever.
            for (int i = _officers.Count - 1; i >= 0; i--)
            {
                var o = _officers[i];
                if (o != null && o.Current is not (IntruderT.State.Dead or IntruderT.State.Gone)) continue;
                _officers.RemoveAt(i);
                if (o != null && o.Current == IntruderT.State.Dead) OnOfficerDown(o, true); // (once: _counted)
                else OfficerWithdrawn(o);
            }
            WatchForStuck();
            var cam = Camera.main;
            var head = cam != null ? cam.transform.position : Vector3.up * 1.6f;
            PlayerFloor = new Vector3(head.x, 0f, head.z);
            // Hitmen take no surrender and give no orders: no stance to read, and their own entry rule (research/27 §6 step 2).
            if (Hitmen) { HitmanEntry(); return; }

            // Aim: seconds of the last second the muzzle was on each officer (research/22 §3.2: 0.5 s → they fire).
            bool aimingAny = false;
            foreach (var o in _officers)
            {
                bool on = o.BehindDoor == false && o.InViewOfPlayer() && PlayerAim.IsAimedAt(o.transform.position + Vector3.up * 1.3f); // (not through a wall, 0.1.53)
                _aimed.TryGetValue(o, out float t);
                t = on ? Mathf.Min(1f, t + Time.deltaTime) : Mathf.Max(0f, t - Time.deltaTime);
                _aimed[o] = t;
                if (t > 0.15f) aimingAny = true;
            }
            if (aimingAny && !_aimedAtPolice) { _aimedAtPolice = true; Debug.Log("[HIS] Police: the player aimed at an officer"); }

            // Hands up: both controllers at or above chin height (relative to YOUR head, so seated works too), held 0.5 s.
            bool armed = PlayerAim.IsArmed;
            var cs = CommandSystem.Instance;
            bool up = cs != null && cs.Left != null && cs.Right != null &&
                      cs.Left.transform.position.y >= head.y - 0.15f && cs.Right.transform.position.y >= head.y - 0.15f;
            _handsUpFor = up && !armed ? _handsUpFor + Time.deltaTime : 0f;
            var was = Stance;
            Stance = aimingAny ? PlayerStance.Aiming : armed ? PlayerStance.Armed : _handsUpFor >= 0.5f ? PlayerStance.Surrendered : PlayerStance.Unarmed;
            if (Stance != was) Debug.Log($"[HIS] Police: player {Stance}");

            // Entry: once the wave's officers are stacked on the step, a beat for the announce, then in.
            int want = Mathf.Min(2, _spawnedThisWave - _downThisWave);
            // Shot at through the door while they're stacked: no more waiting, they go in (hot — the player fired).
            if (!EntryCleared && PlayerFiredAtPolice && _atDoor.Count > 0) { EntryCleared = true; Debug.Log($"[HIS] Police: wave {Wave} shot at — going in now"); }
            if (!EntryCleared && want > 0 && _atDoor.Count >= want)
            {
                if (!_entryTimerOn) { _entryTimerOn = true; _stackedAt = Time.time; }
                else if (Time.time - _stackedAt >= EntryHoldSeconds) { EntryCleared = true; Debug.Log($"[HIS] Police: wave {Wave} going in"); }
            }
        }

        /// <summary>Everyone this wave sent is out of the fight (by the count, or: none of them is left in it).</summary>
        bool WaveBeaten()
        {
            if (_spawnedThisWave <= 0 || Ending != StandoffEnding.None) return false;
            if (_downThisWave >= _spawnedThisWave) return true;
            foreach (var o in _officers) if (o != null && o.Wave == Wave) return false;
            Debug.LogWarning($"[HIS] {Tag}: wave {Wave} has nobody left in it ({_downThisWave}/{_spawnedThisWave} counted) — beaten");
            return true;
        }

        /// <summary>
        /// Brett 0.1.44: "after third wave of cops nothing happens" — an officer whose brain had died stood hidden behind the house,
        /// still "alive", and the wave waited for him forever. Out there (behind his door: on his way up, in his seat, going round),
        /// an officer who hasn't moved 25 cm or changed state in <see cref="StuckSeconds"/> is withdrawn. At the door (BreakIn)
        /// and inside, standing still is part of the job; a wounded one backing out is already counted.
        /// </summary>
        void WatchForStuck()
        {
            for (int i = _officers.Count - 1; i >= 0; i--)
            {
                var o = _officers[i];
                if (o == null) continue;
                if (!o.BehindDoor || o.OfficerOut || o.Current == IntruderT.State.BreakIn) { _progress.Remove(o); continue; }
                var p = o.transform.position;
                if (!_progress.TryGetValue(o, out var g) || (p - g.pos).sqrMagnitude > 0.25f * 0.25f || g.state != o.Current)
                { _progress[o] = (p, o.Current, Time.time); continue; }
                if (Time.time - g.since < StuckSeconds) continue;
                _progress.Remove(o);
                o.Withdraw($"stuck {StuckSeconds:F0} s at {p:F1} ({o.Current}{(o.Riding ? ", riding" : "")})");
            }
        }
        readonly Dictionary<IntruderT, (Vector3 pos, IntruderT.State state, float since)> _progress = new();

        /// <summary>
        /// An officer left the fight without being shot (IntruderPolice.Withdraw, or found Gone/destroyed): he counts toward the
        /// wave being over, with no charge.
        /// </summary>
        public void OfficerWithdrawn(IntruderT officer)
        {
            if (ReferenceEquals(officer, null)) return;
            _officers.Remove(officer);
            _progress.Remove(officer);
            if (!_counted.Add(officer)) return;
            _downThisWave++;
            Debug.LogWarning($"[HIS] {Tag}: {(officer != null ? officer.name : "an officer (destroyed)")} withdrawn ({_downThisWave}/{_spawnedThisWave} this wave)");
        }

        IEnumerator Lull(float seconds) { float until = Time.time + seconds; while (!_ended && Time.time < until) yield return null; }

        // ───────────── hitmen (research/27 §2, §6 step 2) ─────────────

        /// <summary>A broken door stays open while a body (anyone not gone) lies within this many metres of its threshold (Question 5).</summary>
        const float DoorBodyClear = 0.8f;
        /// <summary>A broken door swings shut over this long between hitman waves (Question 5).</summary>
        const float DoorShutSeconds = 1.2f;

        /// <summary>When the first man of this wave stacked (the <see cref="HitmanTier.EntryHoldCap"/> clock), or −1.</summary>
        float _firstStackAt = -1f;
        /// <summary>The wave whose timeout already withdrew its men (0: none), so it happens once.</summary>
        int _timedOutWave;

        /// <summary>
        /// A hitman wave arrives: no radio, no bullhorn, no knock (research/27 §2). The broken doors swing shut first (Question
        /// 5), the wave's car drives in on the door camera, its men ride in seated, and this returns at once (the loop ticks the
        /// entry rule while they come). What warns you is the car itself (Brett 0.1.56: "the bad guys seemingly come from no
        /// where"): its engine pulling up, the monitor's VEHICLE alert and a car door per man (HIS.Security.PoliceCars.Arrival),
        /// Ricky calling it, and their steps up the path.
        /// </summary>
        void CallHitmanWave(int wave)
        {
            ShutBrokenDoors();                                                // before the spawn: the spawner stands each man at the door for a frame
            RaiseWaveCalled(wave);                                            // the wave's own vehicle, one of five (PoliceCars.VehicleFor reads Faction)
            var tier = HitmanTier.For(wave);
            bool twoDoors = _spawner != null && _spawner.DoorCount > 1;
            tier.Split(twoDoors, out int front, out int side);
            // As the police side team: they ride in, and parked, the side men get out and go round the house to the side door.
            // Shot to a stop (the driver killed), everyone left comes to the front (IntruderPolice.RoundToDoor).
            for (int i = 0; i < front + side && !_ended; i++)
                Spawn(i == 0 ? OfficerRole.Lead : i == 1 ? OfficerRole.Cover : OfficerRole.Backup, wave, i, 0, ride: true,
                      roundTo: i >= front ? 1 : -1, roundSlot: i - front);
            if (_spawnedThisWave == 0 && !_ended)
            {
                // No door to spawn at (SpawnScenario null): no card, the director's night starts over after its breather.
                Debug.LogWarning($"[HIS] Hitmen: nobody could be spawned for wave {wave} — the night is called off");
                Cancel();
                return;
            }
            Debug.Log($"[HIS] Hitmen: wave {wave} ({tier.Name}) called: {front} front + {side} side, in the {tier.Vehicle}; {OutsideLoad()}");
        }

        /// <summary>
        /// What's drawn out front as a wave is called, for the headset's [HIS] Perf lines (the fixer, 0.1.58: up to five parked
        /// vehicles by wave 5 — each ~9 renderers, 6 of them blended glass — on top of the porch, never measured on the headset;
        /// Brett's budget note: ~94% GPU at the SWAT wave): the renderers on active objects under every door's porch set (its
        /// props and the vehicles parked on it — what the feed, or an open doorway, draws: PorchSet.SetLive switches their
        /// components on only for those, so the components' own enabled flag isn't the count), and how many of them are blended
        /// (render queue ≥ 3000). The men aren't counted.
        /// </summary>
        static string OutsideLoad()
        {
            int n = 0, blended = 0;
            foreach (var porch in HIS.Security.PorchSet.All)
            {
                if (porch == null) continue;
                foreach (var r in porch.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null) continue;
                    n++;
                    var m = r.sharedMaterial;
                    if (m != null && m.renderQueue >= 3000) blended++;
                }
            }
            return $"outside: {n} renderers ({blended} blended)";
        }

        /// <summary>
        /// Question 5 (research/27 §7, default yes): at each hitman wave, a kicked-in door swings shut unless the player holds it,
        /// a body lies in it, or the player stands in it (within <see cref="DoorBodyClear"/> of its threshold). The walk-ups
        /// render with the doors shut (GPU: the 0.1.47 log hit 90% with two open doorways and three vehicles), and the later
        /// waves' kicks are real ones. The police never close a door (Intruder.cs: a squad never
        /// does), so this is the hitmen's only. A door a visitor left open counts too: Sal lets himself in with his key and
        /// walks out leaving it wide, and nothing else shuts it before wave 1 (Intruder.Run's "last man left it open" close
        /// skips a squad) — an open door is never kicked, so it stayed open all three waves. Only a door the player opened by
        /// hand stays as he left it.
        /// </summary>
        void ShutBrokenDoors()
        {
            var cam = Camera.main;
            foreach (var d in HIS.Room.VirtualDoor.All)
            {
                if (d == null || (!d.Breached && d.OpenedByPlayer) || d.HeldByPlayer || d.State == HIS.Room.VirtualDoor.DoorState.Closed) continue;
                string which = d.Breached ? "broken " : "";
                if (BodyInDoorway(d)) { Debug.Log($"[HIS] Hitmen: the {which}{d.Label} stays open (a body lies in it)"); continue; }
                // The player standing in it (watching the path, no hand on it): the leaf would sweep through him and latch with
                // him inside it, and the wave's kick burst it through him again. It stays open this wave, as with a body.
                if (cam != null && Near(cam.transform.position, d.Threshold)) { Debug.Log($"[HIS] Hitmen: the {which}{d.Label} stays open (you're in it)"); continue; }
                d.Close(DoorShutSeconds);
                Debug.Log($"[HIS] Hitmen: the {which}{d.Label} swings shut");
            }
        }

        /// <summary>Anyone (dead or alive, not gone) with his feet, hips, head or root within <see cref="DoorBodyClear"/> m of the threshold, on the floor plan.</summary>
        static bool BodyInDoorway(HIS.Room.VirtualDoor d)
        {
            foreach (var o in IntruderT.All)
                if (o != null && o.Current != IntruderT.State.Gone && LiesInDoorway(o, d)) return true;
            return false;
        }

        /// <summary><paramref name="o"/>'s feet, hips, head or root within <see cref="DoorBodyClear"/> m of <paramref name="d"/>'s threshold, on the floor plan.</summary>
        static bool LiesInDoorway(IntruderT o, HIS.Room.VirtualDoor d)
        {
            var at = d.Threshold;
            if (Near(o.transform.position, at)) return true;
            var anim = o.GetComponentInChildren<Animator>();
            if (anim == null || !anim.isHuman) return false;
            foreach (var b in DoorwayBones)
            {
                var t = anim.GetBoneTransform(b);
                if (t != null && Near(t.position, at)) return true;
            }
            return false;
        }

        /// <summary>The longest <see cref="TidyForVisitor"/> waits for the leaves it swung shut to latch (their swing + a beat).</summary>
        const float TidyShutWait = DoorShutSeconds + 0.4f;

        /// <summary>
        /// The street tidy before a Marshal night's visitor walks up between waves (Brett 0.1.57: the courier after wave 1, Sal
        /// after wave 3; called by the director's <see cref="BetweenHitmanWaves"/> step): he walks up a clear path to a shut
        /// door. Every squad man out of the room — dead on the porch, the path or the street, or out of the fight (backed out,
        /// withdrawn, gone) and still about — goes, with his blood (BloodDecals.Release); so does any body lying in a doorway
        /// (<see cref="LiesInDoorway"/>), in the room or not: the door couldn't shut on him, and the visitor would step over him.
        /// Kept: bodies inside the room (the fight happened there: the blood, the holes and one body stay through the visit —
        /// CorpseRegistry keeps one at a time anyway), and a man dead in his seat (he's in the car, not on the path: taking him
        /// out would swing its door open on the feed). The doorway's bodies go first; then the broken doors swing shut
        /// (<see cref="ShutBrokenDoors"/>: not one you hold, you stand in or opened by hand) and this waits for them to latch,
        /// at most <see cref="TidyShutWait"/> s; then the rest — the porch's, the path's, the street's — once none of them is in
        /// your sight (<see cref="InSight"/>), at most <see cref="TidyUnseenWait"/> s more (the fixer, 0.1.58 review: all in one
        /// frame, on a live feed or through the open doorway, they blinked out with no cause — CorpseRegistry's own rule is
        /// "once the player isn't looking"). A shut leaf whose latch was kicked out is a working door again (VirtualDoor's
        /// Close: Breached clears), so the visitor's knock, key and push open it as any shut door; the latch chunk stays off,
        /// the holes stay in. Logged. Hitmen only (a police run never has visitors).
        /// </summary>
        /// <param name="who">The visitor's name, for the log.</param>
        public IEnumerator TidyForVisitor(string who)
        {
            if (!Hitmen) yield break;
            int outside = 0, doorway = 0, kept = 0, seated = 0, leaving = 0;
            var doors = HIS.Room.VirtualDoor.All;
            var later = new List<IntruderT>();                                // out front: once he's out of your sight
            foreach (var o in new List<IntruderT>(IntruderT.All))
            {
                if (o == null || !o.IsHitman) continue;
                bool dead = o.Current == IntruderT.State.Dead;
                if (dead && o.Riding) { seated++; continue; }
                if (!dead && !o.OfficerOut && o.Current != IntruderT.State.Gone) continue;   // (a man still in the fight: none after a beaten wave)
                bool inDoorway = false;
                foreach (var d in doors) if (d != null && LiesInDoorway(o, d)) { inDoorway = true; break; }
                // Out of the room: not drawn in it (on the porch side's layer, or gone), or past a door's plane.
                bool outOfRoom = o.BehindDoor || o.Current == IntruderT.State.Gone || HIS.Intruder.DoorwayNav.AnyBeyond(o.transform.position);
                if (!inDoorway && !outOfRoom)
                {
                    // In the room: a body stays; a wounded man still backing out (rare after the lull) walks on out, in view.
                    if (dead) kept++; else leaving++;
                    continue;
                }
                if (inDoorway) { doorway++; TakeAway(o); continue; }        // now: the leaf can't swing shut on him
                outside++;
                if (o.Current == IntruderT.State.Gone) TakeAway(o); else later.Add(o);   // (gone: drawn nowhere already)
            }
            var shutting = new List<HIS.Room.VirtualDoor>();
            foreach (var d in doors) if (d != null && d.IsOpen) shutting.Add(d);
            yield return null;                                                // (Destroy lands at the frame's end: the doorways are clear)
            ShutBrokenDoors();
            for (float t = 0f; t < TidyShutWait && !_ended; t += Time.deltaTime)
            {
                bool moving = false;
                foreach (var d in shutting) if (d != null && d.IsOpen && !d.OpenedByPlayer && !d.HeldByPlayer) moving = true;
                if (!moving) break;
                yield return null;
            }
            // The porch's, the path's and the street's once you can't see them go (behind a shut door, off the live feed).
            float unseenFor = 0f;
            for (; unseenFor < TidyUnseenWait && !_ended && AnyInSight(later); unseenFor += Time.deltaTime) yield return null;
            foreach (var o in later) if (o != null) TakeAway(o);
            if (later.Count > 0) yield return null;                           // (Destroy lands at the frame's end: before he sets off)
            Debug.Log($"[HIS] Hitmen: the street tidied for {who} — {outside} off the porch, path and street, {doorway} from a doorway, " +
                      $"{kept} kept in the room, {seated} left in their seats{(leaving > 0 ? $", {leaving} still backing out" : "")}" +
                      (later.Count > 0 ? $" (out of sight {(unseenFor < TidyUnseenWait ? $"after {unseenFor:F1} s" : $"or not: {TidyUnseenWait:F0} s up")})" : ""));
            foreach (var d in shutting)
                if (d != null && d.IsOpen) Debug.Log($"[HIS] Hitmen: the {d.Label} is still open for {who} ({(d.HeldByPlayer ? "you hold it" : d.OpenedByPlayer ? "you opened it by hand" : "you or someone is in it")})");
        }

        /// <summary>The longest <see cref="TidyForVisitor"/> waits for the bodies out front to be out of your sight.</summary>
        const float TidyUnseenWait = 2f;

        /// <summary>A squad man out of the run, with his blood (the street tidy).</summary>
        void TakeAway(IntruderT o)
        {
            _officers.Remove(o);
            _progress.Remove(o);
            HIS.Intruder.BloodDecals.Release(o.gameObject);
            Destroy(o.gameObject);
        }

        static readonly Plane[] s_eyePlanes = new Plane[6];

        /// <summary>Any of <paramref name="men"/> (out front) in your sight: see <see cref="InSight"/>.</summary>
        static bool AnyInSight(List<IntruderT> men)
        {
            foreach (var o in men) if (InSight(o)) return true;
            return false;
        }

        /// <summary>
        /// A man out front the player could see vanish right now: on his door's feed while it's rendering (the monitor in view and
        /// the picture live — a held frame keeps him till the next, when the visitor walks into it), or through an open doorway in
        /// the headset's view (his spot, a body's length round it, in the eye's frustum: generous — the walls aren't asked).
        /// </summary>
        static bool InSight(IntruderT o)
        {
            if (o == null) return false;
            var p = o.transform.position + Vector3.up * 0.3f;
            var feed = HIS.Security.SecurityCamera.For(o.Door);
            if (feed != null && feed.TargetFps > 0f && feed.Live && feed.Sees(p)) return true;
            bool open = false;
            foreach (var d in HIS.Room.VirtualDoor.All) if (d != null && d.IsOpen) { open = true; break; }
            var eye = Camera.main;
            if (!open || eye == null) return false;
            GeometryUtility.CalculateFrustumPlanes(eye, s_eyePlanes);
            return GeometryUtility.TestPlanesAABB(s_eyePlanes, new Bounds(p, new Vector3(2f, 1.2f, 2f)));
        }

        static readonly HumanBodyBones[] DoorwayBones = { HumanBodyBones.Hips, HumanBodyBones.Head, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };

        static bool Near(Vector3 p, Vector3 at)
        {
            float dx = p.x - at.x, dz = p.z - at.z;
            return dx * dx + dz * dz < DoorBodyClear * DoorBodyClear;
        }

        /// <summary>
        /// The hitmen's entry (research/27 §2 "Hold at the stack"): they go in together, so the wait is for every live man of
        /// this wave stacked at EITHER door (the pincer: both doors burst within a second), then the tier's hold. A team whose
        /// partner never shows (stuck, shot on the way, the side man still going round) goes anyway
        /// <see cref="HitmanTier.EntryHoldCap"/> s after its first man stacked. No hot entry: shot at through the door, the
        /// pros fire back through it instead (IntruderHitman).
        /// </summary>
        void HitmanEntry()
        {
            if (EntryCleared) return;
            int live = 0, stacked = 0;
            foreach (var o in _officers)
            {
                if (o == null || o.Wave != Wave || o.OfficerOut) continue;
                live++;
                if (_atDoor.Contains(o)) stacked++;
            }
            if (stacked == 0) return;
            if (_firstStackAt < 0f) _firstStackAt = Time.time;
            if (stacked >= live)
            {
                if (!_entryTimerOn) { _entryTimerOn = true; _stackedAt = Time.time; }
                else if (Time.time - _stackedAt >= HitmanTier.For(Wave).EntryHold) { EntryCleared = true; Debug.Log($"[HIS] Hitmen: wave {Wave} going in ({stacked} stacked)"); }
            }
            else if (Time.time - _firstStackAt >= HitmanTier.EntryHoldCap)
            {
                EntryCleared = true;
                Debug.Log($"[HIS] Hitmen: wave {Wave} going in without the rest ({stacked} of {live} stacked after {HitmanTier.EntryHoldCap:F0} s)");
            }
        }

        /// <summary>A hitman wave out past the timeout: its men still in it withdraw (Intruder.Withdraw) and the wave counts as beaten.</summary>
        void WithdrawWave(string why)
        {
            Debug.LogWarning($"[HIS] Hitmen: wave {Wave} {why} after {StandoffTimeout:F0} s — its men withdraw");
            for (int i = _officers.Count - 1; i >= 0; i--)
            {
                if (i >= _officers.Count) continue;                           // (Withdraw takes him off the list)
                var o = _officers[i];
                if (o != null && o.Wave == Wave) o.Withdraw(why);
            }
        }

        /// <summary>Hitmen the player put out of the fight: killed, or hit and backed out.</summary>
        int HitmenStopped => _killed + _wounded;

        /// <summary>The night's hitmen, every wave of the tier table, for this room's doors (Brett 0.1.57's five waves: 2 / 3 / 2 /
        /// 3 / 3 = 13 with one door or two). The card's "Hitmen stopped: N of 13." and the run's opening log line read it.</summary>
        int HitmenTotal()
        {
            bool twoDoors = _spawner != null && _spawner.DoorCount > 1;
            int n = 0;
            for (int w = 1; w <= HitmanTier.Waves; w++) { HitmanTier.For(w).Split(twoDoors, out int f, out int s); n += f + s; }
            return n;
        }

        // ───────────── reports ─────────────

        void OnPlayerFired(Vector3 muzzle)
        {
            if (!Active || _officers.Count == 0) return;
            if (!PlayerFiredAtPolice) Debug.Log($"[HIS] {Tag}: the player opened fire");
            if (!FiredThisWave && Hitmen) Debug.Log($"[HIS] Hitmen: the player fired on wave {Wave}");
            PlayerFiredAtPolice = true;
            FiredThisWave = true;
        }

        void OnPlayerDied() { if (Active && !_ended) End(Hitmen ? StandoffEnding.KilledByHitmen : StandoffEnding.KilledByPolice); }

        /// <summary>The witness died during the waves: the night is lost (research/27 §2 "the end"). The squad stands down (End).</summary>
        partial void OnProtecteeDied()
        {
            if (!Active || _ended || !Hitmen) return;
            Debug.Log("[HIS] Hitmen: the witness is dead");
            End(StandoffEnding.WitnessKilled);
        }

        partial void OnOfficerDown(IntruderT officer, bool killed)
        {
            if (officer == null) return;
            if (!_counted.Add(officer))                                         // once per officer (Died + a report can both come)
            {
                if (killed && _wounded > 0 && _woundedSet.Remove(officer)) { _wounded--; _killed++; } // hit, then shot dead backing out
                return;
            }
            if (killed) _killed++; else { _wounded++; _woundedSet.Add(officer); }
            _downThisWave++;
            _officers.Remove(officer);
            Debug.Log($"[HIS] {Tag}: {officer.name} {(killed ? "killed" : "down, out of the fight")} ({_downThisWave}/{_spawnedThisWave} this wave)");
        }
        readonly HashSet<IntruderT> _counted = new(), _woundedSet = new();

        partial void OnCuffed(IntruderT officer)
        {
            if (_ended || Hitmen) return;                                      // (hitmen carry no cuffs: never an arrest on a Marshal night)
            Sfx.Play2D("cuffs.click", 0.8f);
            End(_goodCall && !PlayerFiredAtPolice && !_aimedAtPolice && _killed == 0 && _wounded == 0 && !_tased
                ? StandoffEnding.Released : StandoffEnding.Arrested);
        }

        partial void OnTased(IntruderT officer)
        {
            if (_ended || Hitmen) return;                                      // (no Taser either)
            _tased = true;
            Debug.Log("[HIS] Police: TASED");
            Sfx.Play2D(Sfx.Has("taser.crackle") ? "taser.crackle" : "player.hit", 1f);
            var cs = CommandSystem.Instance;
            if (cs != null)
                foreach (var h in new[] { cs.Left, cs.Right })
                    if (h != null) { h.ForceRelease(); h.Haptic(1f, 1.5f, 0.3f); }  // the weapon falls; the whole body locks up
        }

        partial void OnOfficerFired(IntruderT officer, bool hit) { }

        void End(StandoffEnding e)
        {
            if (_ended) return;
            _ended = true; Ending = e;
            // Research/27 §2: Cancel() and End() never stopped the squad (the director's later ClearWorld did), and HoldFire only
            // covers a dead player, so a hitman would keep shooting through the card. Every hitman ending but the player's death
            // freezes them first.
            if (Hitmen && e != StandoffEnding.KilledByHitmen) StandDownSquad();
        }

        /// <summary>
        /// Every squad member still in the fight stops now (<see cref="ScenarioDirector.StandDown"/>: brain off, no walking, no
        /// more hits taken) and stays where he is, visible, until the room is cleared. Called by End() for a hitman night that
        /// ends any way but the player's death, and by the night's "you shot Ricky" handler right after <see cref="Cancel"/>.
        /// </summary>
        public void StandDownSquad()
        {
            int n = 0;
            for (int i = _officers.Count - 1; i >= 0; i--)
            {
                var o = _officers[i];
                if (o == null || o.Current is IntruderT.State.Dead or IntruderT.State.Gone) continue;
                ScenarioDirector.StandDown(o);
                n++;
            }
            if (n > 0) Debug.Log($"[HIS] {Tag}: {n} stood down");
        }

        /// <summary>What this run's dispatcher was told happened (Run's <c>reported</c>).</summary>
        ForceKind _reported = ForceKind.Shot;
        /// <summary>The bad call was a shooting (Run's <c>badShot</c>): the firearm years on the card's charge.</summary>
        bool _badShot = true;

        /// <summary>Wave 1's dispatcher line by what happened: "Shots fired", "Stabbing" or "Assault" at the residence. A line
        /// with no clip yet stays silent (Radio): it never falls back to "Shots fired".</summary>
        static string RespondingLine(ForceKind reported) => reported switch
        {
            ForceKind.Stab or ForceKind.Slash => "vo.cop.radio.stabbing",
            ForceKind.Punch => "vo.cop.radio.assault",
            _ => "vo.cop.radio.responding",
        };

        void Radio(string key)
        {
            if (!Sfx.Has(key)) return;
            if (Sfx.Has("radio.squelch")) Sfx.Play2D("radio.squelch", 0.5f);
            Sfx.Play2D(key, 0.55f);
            var text = VoiceLines.Text(key);
            if (!string.IsNullOrEmpty(text)) HIS.UI.Subtitles.Show("Radio", text);
        }

        void Bullhorn(string key)
        {
            if (!Sfx.Has(key)) return;
            var door = HIS.Room.VirtualDoor.Instance;
            var at = door != null ? door.Center + door.Outward * 5f : Vector3.zero;
            if (door != null) door.PlayThrough(key, at, 1f, 1f, 2200f); else Sfx.Play(key, at, 1f);
            var text = VoiceLines.Text(key);
            if (!string.IsNullOrEmpty(text)) HIS.UI.Subtitles.Show("Police", text);
        }

        // ───────────── the card ─────────────

        /// <summary>
        /// A Marshal night's card (research/27 §4 "The cards"): hand-built, nobody judged (the hitmen never are). WITNESS
        /// DELIVERED is the full Perfect show; THE TRIAL IS OFF is the dim YouDied one (no confetti, no police). WhoItWas stays
        /// null: the banner's "Who it was:" prefix is hard-coded. Deadpan, and nothing names a door.
        /// </summary>
        Verdict ComposeHitmanCard()
        {
            string tally = $"Hitmen stopped: {HitmenStopped} of {HitmenTotal()}.";
            if (Ending == StandoffEnding.Delivered)
                return new Verdict
                {
                    Tier = Tier.Perfect, Headline = "WITNESS DELIVERED",
                    Reason = "Relief arrived at 2:00 AM. Ricky testifies Monday.", Sentence = tally,
                };
            return new Verdict
            {
                Tier = Tier.YouDied, Headline = "THE TRIAL IS OFF",
                Reason = Ending == StandoffEnding.WitnessKilled ? "They got to Ricky." : "You went down. Then they found Ricky.",
                Sentence = $"{tally} Wave {Mathf.Max(1, Wave)} of {HitmanTier.Waves}.",
            };
        }

        Verdict ComposeCard()
        {
            _crimes.Clear();
            if (!_goodCall && !string.IsNullOrEmpty(_call.Charge))
                // The firearm enhancement only when the bad call was a shooting (_badShot: a bad-tier round into a living man), not
                // the dispatcher's "a round was fired" (_reported: a miss, a justified earlier shot or a round into the body after a
                // knife killing count there): a knife kill isn't "personal use of a firearm" because a gun was fired that night.
                _crimes.Add(Sentencing.FromJudgeCharge(_call.Charge, _badShot));
            // A Marshal's bad call on a visitor (research/27 §4, Q2): the federal count after the judge's charge (the booking
            // placard still reads the judge's), and the OPR referral on the arrest card. Every other role: as before.
            bool federal = !_goodCall && Judge.Role == Role.Marshal;
            if (federal) _crimes.Add(Sentencing.ColorOfLaw(_call.Charge == Judge.ChargeKilled, _call.Charge == Judge.ChargeInjured));
            if (_killed > 0) _crimes.Add(Sentencing.MurderOfOfficer(_killed));
            if (_wounded > 0) _crimes.Add(Sentencing.AttemptedMurderOfOfficer(_wounded));
            if (PlayerFiredAtPolice && _killed + _wounded == 0) _crimes.Add(Sentencing.AssaultOnOfficer(1));
            else if (_aimedAtPolice && !PlayerFiredAtPolice) _crimes.Add(Sentencing.Brandishing);
            if (_tased) _crimes.Add(Sentencing.Resisting);

            var card = _call;
            switch (Ending)
            {
                case StandoffEnding.Released:
                    card.Tier = Tier.Good;
                    card.Reason = _call.Reason + "\nPolice held you until they sorted it out. Next time, holster it before they arrive.";
                    card.Sentence = null;
                    break;
                case StandoffEnding.KilledByPolice:
                    card.Tier = Tier.YouDied; card.Headline = "SHOT BY POLICE";
                    card.Reason = (PlayerFiredAtPolice ? "You fired at the officers." : "You pointed a gun at the officers.") +
                                  $"\nThe shooting before it: {(_goodCall ? "GOOD CALL" : "BAD CALL")}. {_call.Reason}";
                    card.Sentence = Sentencing.Tally(_crimes, arrested: false, _wavesBeaten);
                    break;
                default: // Arrested (and a timeout / cancel shows the same)
                    card.Tier = Tier.Bad;
                    card.Headline = _goodCall ? "ARRESTED" : "BAD CALL";
                    card.Charge = _crimes.Count > 0 ? _crimes[0].Name : _call.Charge;          // the booking placard + the read-out
                    card.Reason = _goodCall ? "It was a good call, until you turned your gun on the police."
                                : _tased ? _call.Reason + "\nYou wouldn't put the gun down, so they tased you."
                                : PlayerFiredAtPolice || _aimedAtPolice ? _call.Reason
                                : _call.Reason + "\nYou gave up without a fight: the right move.";
                    if (federal) card.Reason += "\n" + Sentencing.OprReferral; // (arrests only: a dead man isn't referred)
                    card.Sentence = Sentencing.Tally(_crimes, arrested: true, _wavesBeaten);
                    break;
            }
            return card;
        }
    }
}
