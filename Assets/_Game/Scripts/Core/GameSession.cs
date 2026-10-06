using System;
using System.Collections;
using HIS.Intruder;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Session plumbing shared by every mode. Manual restart is "hold Menu" (handled by <see cref="GamePause"/>); a short
    /// Menu press pauses instead of wiping the run. Sandbox: player death → 4 s → reset. Scenario and Marshal modes: the
    /// ScenarioDirector ends the incident (or the night) on player death and clears the room itself (<see cref="ClearWorld"/>).
    /// </summary>
    public class GameSession : MonoBehaviour
    {
        [SerializeField] float restartDelay = 4f;

        public bool IsRestarting { get; private set; }
        public int Resets { get; private set; }

        /// <summary>After a hold-Menu restart (or a Sandbox death reset) has cleared the room.</summary>
        public static event Action SessionReset;
        /// <summary>Why the last <see cref="SessionReset"/> happened (e.g. <see cref="MenuHoldReason"/>, "player died").</summary>
        public static string LastResetReason { get; private set; }
        /// <summary>The player held Menu to restart (the only reset that may re-ask setup questions, e.g. the door).</summary>
        public const string MenuHoldReason = "menu hold";
        /// <summary>The player's own gun fired at his head (Firearm; Brett 2026-10-03: "shooting yourself in the head should
        /// automatically restart at any point") — the same restart as hold-Menu.</summary>
        public const string SelfShotReason = "shot yourself";
        /// <summary>The pause menu's "Doors: pick again" row (Brett 2026-10-04: "there should also be a way to change the doors
        /// youre using on the scan"): the room starts over with the door picker open (<see cref="HIS.Room.DoorPicker"/>).</summary>
        public const string PickDoorsReason = "pick doors";
        /// <summary>After <see cref="ClearWorld"/>: the room is empty between incidents (the loadout swaps a changed pick here).</summary>
        public static event Action WorldCleared;

        static GameSession _instance;
        IntruderSpawner _spawner;
        PlayerState _player;

        void Awake() => _instance = this;

        void Start()
        {
            _spawner = GetComponent<IntruderSpawner>();
            _player = PlayerState.Instance;
            if (_player != null) _player.Died += OnPlayerDied;
            Debug.Log($"[HIS] Build {BuildInfo.Label}");
        }

        void OnPlayerDied()
        {
            var director = HIS.Scenario.ScenarioDirector.Instance;
            // Scenarios and Marshal: the director ends the round or the night itself (a Marshal death is the hitmen's card,
            // research/27 §2); only Sandbox restarts on its own.
            if (director != null && director.CurrentMode != HIS.Scenario.ScenarioDirector.Mode.Sandbox) return; // the director judges it
            StartCoroutine(RestartAfterDeath());
        }

        IEnumerator RestartAfterDeath()
        {
            IsRestarting = true;
            if (_spawner != null) _spawner.Pause(true);
            yield return new WaitForSeconds(restartDelay);
            ResetSession("player died");
        }

        public void ResetSession(string reason)
        {
            Debug.Log($"[HIS] Session reset ({reason})");
            StopAllCoroutines();
            var show = HIS.Outcome.OutcomeShow.Instance;
            if (show != null) show.Clear();
            Sfx.StopAll(); // the old run's lines/tails (frozen by a pause) must not play on into the new one; ResetDoor's Stop then no-ops
            ClearWorld(resetHealth: true);
            if (_spawner != null) _spawner.ResetSpawner();
            Resets++;
            IsRestarting = false;
            LastResetReason = reason;
            SessionReset?.Invoke();
        }

        /// <summary>Empty the room: intruders, bodies, world blood, the door back to closed and intact; optionally heal.</summary>
        /// <param name="keepProtectee">
        /// A Marshal night's own clears only (its visitor rounds and the 2:00 AM cut, research/27 §3): Ricky stays in his spot.
        /// Never a night-wide flag: hold-Menu, a mode switch and the preview clear with the default, so no second Ricky is left.
        /// </param>
        public static void ClearWorld(bool resetHealth, bool keepProtectee = false)
        {
            CorpseRegistry.ClearAll();
            foreach (var door in HIS.Room.VirtualDoor.All) if (door != null) door.ResetDoor(); // every door, front and second
            // (A Marshal night's clears also keep the handover's Ortiz while he's out on the porch walking off — Brett 0.1.57, A6:
            // he goes by himself past the camera's edge, never popped off the feed mid-walk; Intruder.HandoverWalkingOff.)
            foreach (var i in FindObjectsByType<Intruder.Intruder>(FindObjectsInactive.Exclude))
                if (!(keepProtectee && (i.IsProtectee || i.HandoverWalkingOff))) Destroy(i.gameObject);
            // A knife or gun he dropped or threw is no longer his child: it would lie in the room (a live Rigidbody) forever.
            foreach (var w in FindObjectsByType<Intruder.IntruderWeapon>(FindObjectsInactive.Exclude)) if (!w.InHand) Destroy(w.gameObject);
            BloodDecals.ClearAll();
            if (resetHealth && PlayerState.Instance != null) PlayerState.Instance.ResetHealth();
            if (_instance != null && _instance._spawner != null) _instance._spawner.Pause(false);
            WorldCleared?.Invoke();
        }
    }
}
