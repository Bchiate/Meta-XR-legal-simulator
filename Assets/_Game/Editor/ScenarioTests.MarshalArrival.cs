using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The hitmen's arrival cues (Brett 0.1.56: "the bad guys seemingly come from no where"), watched per wave from the log and
/// the squad's reports: the car's engine at the call, the doorbell camera's VEHICLE alert, Ricky's line — HIS line for that
/// wave (Brett 0.1.57's five waves: HitmanTier.RickyLine, "That's not the relief." … "That's a van. Oh no.") — a car door per
/// rider, their footsteps up the path — all before the first man of that wave is at the door, with at least
/// <see cref="ArrivalWatch.MinLead"/> s from the engine to him. Each wave's engine must be its own vehicle's (HitmanTier.Vehicle:
/// Sedan, RedSedan, SUV, CharcoalSUV, Van). Used by the Marshal smoke run (every wave) and the full night (wave 1 after a
/// Y-skip of the quiet before the first car).
/// </summary>
public static partial class ScenarioTests
{
    sealed class ArrivalWatch : IDisposable
    {
        /// <summary>The least warning: the engine's start to the wave's first man at the door.</summary>
        public const float MinLead = 6f;

        class Cues
        {
            public float Called = -1f, Engine = -1f, Vehicle = -1f, Ricky = -1f, FirstDoor = -1f, OutOfCar = -1f, Steps = -1f, AtDoor = -1f;
            public int Doors;
            /// <summary>What Ricky said for this wave's car (the log's quote), and the vehicle whose engine was heard for it.</summary>
            public string RickySaid, EngineOf;
        }

        readonly Dictionary<int, Cues> _waves = new();
        int _current;
        /// <summary>"Marshal: on now" lines seen (Y cut the quiet before the first car short).</summary>
        public int OnNow { get; private set; }
        static readonly Regex CalledRx = new(@"^\[HIS\] Hitmen: wave (\d) \(.*\) called");
        // (Brett 0.1.57: five vehicles, one per wave — "Sedan", "RedSedan", "SUV", "CharcoalSUV", "Van" — named by the tier table.)
        static readonly Regex EngineRx = new(@"^\[HIS\] PoliceCars: (\S+) engine \(wave (\d)\)");
        static readonly Regex DoorRx = new(@"^\[HIS\] PoliceCars: (\S+) door \d");
        static readonly Regex RickyRx = new(@"^\[HIS\] Ricky: another car \(wave (\d)\)");
        /// <summary>The quoted line of Ricky's call (IntruderProtectee's log: the first car's and each later one's).</summary>
        static readonly Regex QuoteRx = new(@"— ""(.*?)""");

        public ArrivalWatch()
        {
            Application.logMessageReceived += OnLog;
            IntruderT.OfficerReported += OnReport;
        }

        public void Dispose()
        {
            Application.logMessageReceived -= OnLog;
            IntruderT.OfficerReported -= OnReport;
        }

        /// <summary>The monitor's big label read VEHICLE at a <see cref="Poll"/> (a placed monitor seen at all: <see cref="MonitorSeen"/>).</summary>
        public bool LabelVehicle { get; private set; }
        public bool MonitorSeen { get; private set; }

        /// <summary>Called from the test's loop: the placed monitor's big label, for its VEHICLE flash.</summary>
        public void Poll()
        {
            var mon = HIS.Security.SecurityMonitor.Instance;
            if (mon == null || !mon.Placed) return;
            MonitorSeen = true;
            if (!LabelVehicle && (mon.MainLabelText?.Contains("VEHICLE") ?? false)) LabelVehicle = true;
        }

        /// <summary>One check: the monitor's label flashed VEHICLE at some point (no placed monitor in this room: noted, not failed).</summary>
        public void CheckLabel(string what)
        {
            MarshalCheck(LabelVehicle || !MonitorSeen, $"{what}: the monitor's label reads VEHICLE as a car pulls in",
                MonitorSeen ? (LabelVehicle ? "seen" : "never read VEHICLE") : "no placed monitor in this room (not checked)");
        }

        Cues W(int wave) { if (!_waves.TryGetValue(wave, out var c)) _waves[wave] = c = new Cues(); return c; }
        static void First(ref float at) { if (at < 0f) at = Time.time; }
        /// <summary>The wave whose tier arrives in <paramref name="vehicle"/> (HitmanTier.Vehicle); 0: not a hitman vehicle.</summary>
        static int WaveOf(string vehicle)
        {
            for (int w = 1; w <= HIS.Scenario.HitmanTier.Waves; w++) if (HIS.Scenario.HitmanTier.For(w).Vehicle == vehicle) return w;
            return 0;
        }

        void OnLog(string msg, string trace, LogType type)
        {
            if (!msg.StartsWith("[HIS] ")) return;
            if (msg.StartsWith("[HIS] Marshal: on now")) { OnNow++; return; }
            var m = CalledRx.Match(msg);
            if (m.Success) { _current = int.Parse(m.Groups[1].Value); First(ref W(_current).Called); return; }
            m = EngineRx.Match(msg);
            if (m.Success)
            {
                var e = W(int.Parse(m.Groups[2].Value));
                if (!msg.Contains("not heard")) First(ref e.Engine);
                e.EngineOf ??= m.Groups[1].Value;                                   // (whose engine it was: the wave's own vehicle?)
                return;
            }
            m = DoorRx.Match(msg);
            if (m.Success) { int dw = WaveOf(m.Groups[1].Value); if (dw > 0) { var c = W(dw); c.Doors++; First(ref c.FirstDoor); } return; }
            if (msg.StartsWith("[HIS] SecurityCamera FRONT DOOR: vehicle")) { if (_current > 0) First(ref W(_current).Vehicle); return; }
            if (msg.StartsWith("[HIS] Ricky: the first car")) { var c1 = W(1); First(ref c1.Ricky); c1.RickySaid ??= Quote(msg); return; }
            m = RickyRx.Match(msg);
            if (m.Success) { var c = W(int.Parse(m.Groups[1].Value)); First(ref c.Ricky); c.RickySaid ??= Quote(msg); }
        }

        static string Quote(string msg) { var q = QuoteRx.Match(msg); return q.Success ? q.Groups[1].Value : null; }

        /// <summary>Ricky's line for wave <paramref name="w"/>'s car, as the log quotes it (VoiceLines' text of the tier's RickyLine).</summary>
        static string RickyLineFor(int w) => HIS.Scenario.VoiceLines.Text(HIS.Scenario.HitmanTier.For(w).RickyLine);

        /// <summary>
        /// Wave <paramref name="w"/>'s own cues (Brett 0.1.57: one vehicle and one Ricky line per wave): the engine heard for it
        /// was its tier's vehicle's, and Ricky's line was its tier's. Empty: right; else what was wrong (for the check's info).
        /// </summary>
        static string OwnCues(int w, Cues c)
        {
            string want = HIS.Scenario.HitmanTier.For(w).Vehicle, line = RickyLineFor(w), bad = "";
            if (c.EngineOf != want) bad += $" the engine was {c.EngineOf ?? "nobody"}'s, not the {want}'s;";
            if (c.RickySaid != line) bad += $" Ricky said \"{c.RickySaid ?? "nothing"}\", not \"{line}\";";
            return bad;
        }

        void OnReport(IntruderT o, string r)
        {
            if (o == null || !o.IsHitman) return;
            var c = W(o.Wave);
            if (r == "out of the car") First(ref c.OutOfCar);
            else if (r == "footsteps up the path") First(ref c.Steps);
            else if (r == "at the door") First(ref c.AtDoor);
        }

        /// <summary>The least warning before the first man is out of the car (the full night puts each man down as he gets out).</summary>
        public const float MinLeadToOut = 4f;

        /// <summary>
        /// One check per wave in <paramref name="waves"/> for a run that puts every man down as he gets out (the full night):
        /// the engine at the call, VEHICLE, Ricky's line and a car door, all before the wave's first man is out of the car, with
        /// at least <see cref="MinLeadToOut"/> s from the engine to him (no steps or door: nobody gets that far).
        /// </summary>
        public void CheckBeforeOut(string what, params int[] waves)
        {
            foreach (int w in waves)
            {
                _waves.TryGetValue(w, out var c);
                c ??= new Cues();
                bool before(float t) => t >= 0f && t <= c.OutOfCar;
                float lead = c.Engine >= 0f && c.OutOfCar >= 0f ? c.OutOfCar - c.Engine : -1f;
                string own = OwnCues(w, c);
                bool ok = c.Called >= 0f && c.OutOfCar >= 0f
                          && c.Engine >= 0f && Mathf.Abs(c.Engine - c.Called) <= 0.2f
                          && before(c.Vehicle) && c.Vehicle >= c.Called
                          && before(c.Ricky)
                          && c.Doors >= 1 && before(c.FirstDoor)
                          && lead >= MinLeadToOut
                          && own.Length == 0;
                string R(float t) => t < 0f ? "never" : c.Called >= 0f ? $"+{t - c.Called:F1} s" : $"{t:F1}";
                MarshalCheck(ok, $"{what}: wave {w} is heard and seen coming before the men are out",
                    $"called {(c.Called >= 0f ? "yes" : "no")}; engine {R(c.Engine)} ({c.EngineOf ?? "none"}), VEHICLE {R(c.Vehicle)}, " +
                    $"Ricky {R(c.Ricky)} (\"{c.RickySaid}\"), car doors {c.Doors} (first {R(c.FirstDoor)}), out of the car {R(c.OutOfCar)}; " +
                    $"warning {(lead >= 0f ? $"{lead:F1} s" : "none")} (want ≥ {MinLeadToOut:F0} s){own}");
            }
        }

        /// <summary>One check per wave in <paramref name="waves"/> (each must have reached the door), named "<paramref name="what"/>: wave N …".</summary>
        public void Check(string what, params int[] waves)
        {
            foreach (int w in waves)
            {
                _waves.TryGetValue(w, out var c);
                c ??= new Cues();
                bool before(float t) => t >= 0f && (c.AtDoor < 0f || t <= c.AtDoor);
                float lead = c.Engine >= 0f && c.AtDoor >= 0f ? c.AtDoor - c.Engine : -1f;
                string own = OwnCues(w, c);
                bool ok = c.Called >= 0f && c.AtDoor >= 0f
                          && c.Engine >= 0f && Mathf.Abs(c.Engine - c.Called) <= 0.2f
                          && before(c.Vehicle) && c.Vehicle >= c.Called && (c.OutOfCar < 0f || c.Vehicle <= c.OutOfCar)
                          && before(c.Ricky)
                          && c.Doors >= 1 && before(c.FirstDoor)
                          && before(c.Steps)
                          && lead >= MinLead
                          && own.Length == 0;
                string R(float t) => t < 0f ? "never" : c.Called >= 0f ? $"+{t - c.Called:F1} s" : $"{t:F1}";
                MarshalCheck(ok, $"{what}: wave {w} is heard and seen coming before the door",
                    $"called {(c.Called >= 0f ? "yes" : "no")}; engine {R(c.Engine)} ({c.EngineOf ?? "none"}), VEHICLE {R(c.Vehicle)}, " +
                    $"Ricky {R(c.Ricky)} (\"{c.RickySaid}\"), car doors {c.Doors} (first {R(c.FirstDoor)}), out of the car {R(c.OutOfCar)}, " +
                    $"steps {R(c.Steps)}, at the door {R(c.AtDoor)}; warning {(lead >= 0f ? $"{lead:F1} s" : "none")} (want ≥ {MinLead:F0} s){own}");
            }
        }
    }
}
