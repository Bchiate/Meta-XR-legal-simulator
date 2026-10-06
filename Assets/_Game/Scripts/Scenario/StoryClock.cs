using System;
using UnityEngine;

namespace HIS.Scenario
{
    /// <summary>
    /// The story's own time (research/27 §1: "Clock on the monitor"). A Marshal night runs from 1:12 AM to the 2:00 AM relief in
    /// a few real minutes: the night sets the clock at each beat (Brett 0.1.57's mob night: the card 1:12, the waves 1:15 /
    /// 1:24 / 1:33 / 1:42 / 1:51 as each is called, the courier 1:20 and Sal 1:38 as they walk up between them, the cut 2:00),
    /// and between beats it ticks in game time (paused with the game, like the rest); a beat never sets it back.
    /// The security monitor shows <c>StoryClock.Now ?? DateTime.Now</c>: null (no story running, Scenarios and Sandbox) is the
    /// real clock, as before. Allocation-free to read every frame (DateTime is a struct).
    /// </summary>
    public static class StoryClock
    {
        static DateTime? _set;
        static float _setAt;

        /// <summary>
        /// The story's time now, or null when no story is running (the monitor then shows the real time). Setting it pins the
        /// clock there and lets it tick on from this moment; null stops it (ScenarioDirector.StopLoop).
        /// </summary>
        public static DateTime? Now
        {
            get => _set.HasValue ? _set.Value.AddSeconds(Mathf.Max(0f, Time.time - _setAt)) : null;
            set { _set = value; _setAt = Time.time; }
        }

        /// <summary>
        /// The clock to <paramref name="hour"/>:<paramref name="minute"/> tonight (today's date, so the monitor's date line stays
        /// believable), never backwards: a long wave that ran past the next beat's time keeps its own.
        /// </summary>
        public static void AdvanceTo(int hour, int minute)
        {
            var at = DateTime.Today.AddHours(hour).AddMinutes(minute);
            var now = Now;
            if (now.HasValue && now.Value >= at) return;
            Now = at;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _set = null; _setAt = 0f; } // domain reload off: no story time left over from the last play
    }
}
