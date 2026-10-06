using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HIS.Scenario
{
    /// <summary>An officer's job in the stack (research/22 §4): Lead gives the orders and cuffs; Cover holds the doorway.</summary>
    public enum OfficerRole { Lead, Cover, Backup }

    /// <summary>
    /// Who the wave machine sends (research/27): the police after a bad call, or the hitmen of a US Marshal night — the same
    /// rides, walk-ups, stacks, kicks and spreads, with no orders, Taser or cuffs, and fire on sight after a fair tell (HitmanTier).
    /// </summary>
    public enum SquadFaction { Police, Hitmen }

    /// <summary>What the officers see the player doing, re-read every frame (research/22 §3).</summary>
    public enum PlayerStance
    {
        /// <summary>A weapon in a hand, not pointed at an officer (warnings → Taser).</summary>
        Armed,
        /// <summary>A gun's muzzle on an officer (the tell: "Don't do it!"; 0.5 s of it and they fire).</summary>
        Aiming,
        /// <summary>Empty hands, not raised (they order "Hands up!", then come and cuff).</summary>
        Unarmed,
        /// <summary>Empty hands at face height or higher (no kneeling or lying down, ever — Brett 0.1.25).</summary>
        Surrendered,
    }

    /// <summary>
    /// The police answering a shooting (Brett 0.1.25, research/22): on a bad call (and some good ones) there's no verdict up
    /// front. The cars pull up on the door camera, two officers come up the path, announce, breach the FRONT door and hold you
    /// at gunpoint. Weapons down and hands up → cuffs → booking → the verdict at the end, with your crimes tallied and a
    /// sentence estimate. Point a gun at them or fire → a short, fair gunfight: beat them and more come, in waves, until you
    /// surrender or die. The officers themselves are Intruder instances with Intent.Police (IntruderPolice.cs).
    /// This class is the contract between the round (ScenarioDirector), the officers and the door camera; see members.
    /// </summary>
    public partial class PoliceResponse : MonoBehaviour
    {
        public static PoliceResponse Instance { get; private set; }

        /// <summary>A standoff is running (the round's verdict waits for it).</summary>
        public bool Active { get; private set; }
        /// <summary>1 = the first two officers; each wave the player beats brings a bigger one.</summary>
        public int Wave { get; private set; }
        /// <summary>Who this run sends: the police (a bad call) or a Marshal night's hitmen (research/27). Set by Run.</summary>
        public SquadFaction Faction { get; private set; } = SquadFaction.Police;
        /// <summary>The officers of this wave that are still in the fight.</summary>
        public IReadOnlyList<HIS.Intruder.Intruder> Officers => _officers;
        readonly List<HIS.Intruder.Intruder> _officers = new();

        /// <summary>A wave is called (wave number): the door camera's cars drive in (PoliceCars), sirens close in.</summary>
        public static event Action<int> WaveCalled;
        /// <summary>The standoff is over (arrested, killed, cancelled): anything outside tied to it can go.</summary>
        public static event Action Ended;

        // ───────────── what the officers read ─────────────
        /// <summary>What the player is doing right now.</summary>
        public PlayerStance Stance { get; private set; } = PlayerStance.Armed;
        /// <summary>The player has fired since this wave reached the door (deadly force: they fire back at once).</summary>
        public bool PlayerFiredAtPolice { get; private set; }
        /// <summary>
        /// The player has fired since THIS wave was called (reset in CallWave, set on every shot while a squad is out). The
        /// hitmen read this, not <see cref="PlayerFiredAtPolice"/>: that one is only reset when a run starts, so after wave 1
        /// it stays true for the whole night (research/27 §6 step 2), and wave 1's path fire waits for the player's first shot.
        /// </summary>
        public bool FiredThisWave { get; private set; }
        /// <summary>Seconds (of the last second) the player's muzzle has been on <paramref name="officer"/> (the 0.5 s rule).</summary>
        public float AimedAtFor(HIS.Intruder.Intruder officer) => officer != null && _aimed.TryGetValue(officer, out var t) ? t : 0f;
        readonly Dictionary<HIS.Intruder.Intruder, float> _aimed = new();
        /// <summary>The lead may go in: both officers of the wave are stacked on the step and the entry was announced twice.</summary>
        public bool EntryCleared { get; private set; }
        /// <summary>Where the player stands, on the floor.</summary>
        public Vector3 PlayerFloor { get; private set; }

        // ───────────── what the officers report ─────────────
        /// <summary>This officer is stacked on the front step, ready to go in.</summary>
        public void OfficerAtDoor(HIS.Intruder.Intruder officer) { if (officer != null) _atDoor.Add(officer); }
        readonly HashSet<HIS.Intruder.Intruder> _atDoor = new();
        /// <summary>This officer is out of the fight (killed, or hit and backed out).</summary>
        public void OfficerDown(HIS.Intruder.Intruder officer, bool killed) => OnOfficerDown(officer, killed);
        /// <summary>The cuffs are on (the lead, behind the player): arrest.</summary>
        public void Cuffed(HIS.Intruder.Intruder officer) => OnCuffed(officer);
        /// <summary>The Taser hit the player (passive refusal): they drop what they hold; the arrest follows.</summary>
        public void Tased(HIS.Intruder.Intruder officer) => OnTased(officer);
        /// <summary>An officer fired at the player (hit or not).</summary>
        public void OfficerFired(HIS.Intruder.Intruder officer, bool hit) => OnOfficerFired(officer, hit);
        /// <summary>The witness (Ricky, research/27 §3) was killed during a hitman run: the night is lost (THE TRIAL IS OFF).</summary>
        public void ProtecteeDied() => OnProtecteeDied();

        // ───────────── a Marshal night's visitors between the waves (Brett 0.1.57) ─────────────
        /// <summary>
        /// Brett 0.1.57 (pick 3: the courier and Sal come BETWEEN the waves, "don't shoot him" while you're on edge): what a
        /// hitman run plays after a beaten wave's lull and before it calls the next one, given the beaten wave's number — the
        /// director's visitor for that slot, or nothing (null, or an IEnumerator that ends at once). The squad is idle meanwhile:
        /// the run's loop is suspended in it (no Tick, no entry rule, no timeout clock). Read only by a hitman run (Faction ==
        /// Hitmen); a police run never reads it, so the standoff is as before. The director sets it before RunHitmen and clears it
        /// after (and at every night's end); a visitor's bad call, Ricky hit or the player's death in it Cancel() the run there.
        /// </summary>
        public Func<int, IEnumerator> BetweenHitmanWaves;

        // ───────────── the tally (a Marshal night's card: "Hitmen stopped: N of 13") ─────────────
        /// <summary>Squad members killed this run.</summary>
        public int Killed => _killed;
        /// <summary>Squad members hit and out of the fight alive (backed out) this run.</summary>
        public int Wounded => _wounded;
        /// <summary>Squad members spawned this run, every wave.</summary>
        public int Spawned => _spawnedTotal;
        /// <summary>Waves beaten this run (a hitman wave that timed out and withdrew counts too).</summary>
        public int WavesBeaten => _wavesBeaten;

        partial void OnOfficerDown(HIS.Intruder.Intruder officer, bool killed);
        partial void OnCuffed(HIS.Intruder.Intruder officer);
        partial void OnTased(HIS.Intruder.Intruder officer);
        partial void OnOfficerFired(HIS.Intruder.Intruder officer, bool hit);
        partial void OnProtecteeDied();

        void Awake() { if (Instance != null && Instance != this) { Destroy(this); return; } Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }
        void Start() => StartCoroutine(PrewarmWhenQuiet());

        /// <summary>
        /// The officers' navy uniform made ahead, once (IntruderPoliceLook.PrewarmUniform; HANDOFF 0.1.26: the first officer's
        /// spawn hitched 10–20 ms on the recolour, right as the standoff began). Not before the room is ready (the scan and
        /// the NavMesh bake have heavy frames of their own), then at the first quiet check: no standoff, and nobody in the
        /// room or at a door (outside on the path, dead or gone only). Checked twice a second. A round that never goes quiet
        /// leaves it to the first officer, as before.
        /// </summary>
        IEnumerator PrewarmWhenQuiet()
        {
            var wait = new WaitForSeconds(0.5f);
            while (!HIS.Intruder.Intruder.UniformReady)
            {
                yield return wait;
                if (_spawner == null) _spawner = FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
                if (Active || _spawner == null || !_spawner.NavMeshReady || !NobodyIn()) continue;
                if (HIS.Intruder.Intruder.PrewarmUniform()) yield break;
            }
        }

        /// <summary>No intruder in the room or at a door: each is still out on the path, dead or gone (or there's none). A Marshal
        /// night's Ricky doesn't count: he's in the room all night, crouched still (research/27 §3), so he'd keep it from ever going quiet.</summary>
        static bool NobodyIn()
        {
            foreach (var o in HIS.Intruder.Intruder.All)
            {
                if (o == null || o.IsProtectee) continue;
                var s = o.Current;
                if (s != HIS.Intruder.Intruder.State.Outside && s != HIS.Intruder.Intruder.State.Dead && s != HIS.Intruder.Intruder.State.Gone) return false;
            }
            return true;
        }

        // ───────────── test hooks (officers can be exercised without a whole round) ─────────────
        /// <summary>Tests: what the officers read as the player's stance (null = the real reading).</summary>
        public static PlayerStance? TestStance;
        /// <summary>Tests: treat the player as having fired at the police.</summary>
        public static bool TestFired;
        /// <summary>Tests: the lead may go in without the stack/announce bookkeeping.</summary>
        public static bool TestEntryCleared;

        /// <summary>The stance the officers act on (test override first).</summary>
        public PlayerStance StanceNow => TestStance ?? Stance;
        /// <summary>Fired at the police (test override first).</summary>
        public bool FiredNow => TestFired || PlayerFiredAtPolice;
        /// <summary>Entry cleared (test override first).</summary>
        public bool EntryNow => TestEntryCleared || EntryCleared;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; WaveCalled = null; Ended = null; TestStance = null; TestFired = false; TestEntryCleared = false; }

        static void RaiseWaveCalled(int wave) => WaveCalled?.Invoke(wave);
        static void RaiseEnded() => Ended?.Invoke();
    }
}
