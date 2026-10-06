using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// A Marshal night's misses by the player's own hand (research/27 §1 "A miss at any point ends the night", §4, §6 step 11;
/// Brett 2026-10-03: "dont waste too much time on tests beyond making sure it works"), on the mob night since Brett 0.1.57
/// (the courier and Sal come BETWEEN the hitman waves). One run, one door, four nights; every hitman goes down as he reports
/// out of the car:
///   1. Ricky shot dead under the 1:12 card → the booking (Mugshot, no standoff), THE TRIAL IS OFF, the federal count; no
///      hitmen that night.
///   2. Ricky shot in the leg (he lives) → the booking, BAD CALL, assault + the federal count.
///   3. The courier, after wave 1: the door opened by hand on him and two rounds into him face to face → BAD, and (Brett
///      2026-10-04 (a): every bad call on a Marshal night is the arrest) straight to the booking a beat later: no police run,
///      no dispatcher, the mugshot, the card with the judge's charge, "18 U.S.C. 242" and the OPR referral, "Who it was" the
///      courier. The hitman run is called off there (its own Cancel, no card of its own): no wave 2, and the next night opens
///      with the 1:12 card. The police stay switched on (PoliceResponse.Enabled) to prove a Marshal night never calls them.
///   4. Next night: the courier left alone (PERFECT), then Sal, after wave 3, in the room — and Ricky shot in the leg during
///      Sal's visit: Sal's round is dropped unjudged, the run is called off (no wave 4), and Ricky's card follows (BAD CALL,
///      "You shot Ricky", the federal count), the booking's mugshot (ForceBadMode taken by the show), and Ricky and Sal stood
///      down under it (no more hits land).
///   Just this: scripts/unity-batch.sh ScenarioTests.RunMarshalCallsHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunMarshalCallsHeadless() => RunMarshal(MarshalCalls, 1800f);   // (the mob night since Brett 0.1.57: was 1300)

    static async Task MarshalCalls()
    {
        var dir = ScenarioDirector.Instance;
        var spawner = UnityEngine.Object.FindAnyObjectByType<HIS.Intruder.IntruderSpawner>();
        if (!await WaitFor(() => spawner != null && spawner.NavMeshReady && spawner.DoorConfirmed, 30))
        { ExtraFail("marshal calls: room", $"spawner {(spawner == null ? "missing" : spawner.Status)}"); return; }
        var door = VirtualDoor.Instance;
        if (door == null || s_odGrip == null) { ExtraFail("marshal calls: the front door", $"door {door}, GripInput {s_odGrip}"); return; }

        int bookings = 0, hitmenRuns = 0, policeRuns = 0, safeHouses = 0, rickyHits = 0, failures = 0, dispatcher = 0, cancels = 0, dropped = 0;
        float cardAt = -1f;
        var wavesCalled = new List<int>();                               // this night's hitman waves as called
        string firstFailure = null;
        var waveRx = new System.Text.RegularExpressions.Regex(@"^\[HIS\] Hitmen: wave (\d) \(.*\) called");
        Application.LogCallback onLog = (msg, trace, type) =>
        {
            if (msg.StartsWith("[HIS] Bad-call show: Mugshot")) bookings++;
            if (msg.StartsWith("[HIS] Hitmen: the night's waves begin")) hitmenRuns++;
            if (msg.StartsWith("[HIS] Hitmen cancelled")) cancels++;
            if (msg.StartsWith("[HIS] Police responding")) policeRuns++;
            if (msg.StartsWith("[HIS] Subtitle Radio:")) dispatcher++;                  // the police dispatcher (never on a Marshal night)
            if (msg.Contains(": safe house, 1:12 AM")) { safeHouses++; cardAt = Time.time; lock (wavesCalled) wavesCalled.Clear(); }
            if (msg.StartsWith("[HIS] Marshal: you hit Ricky")) rickyHits++;
            if (msg.Contains("dropped (Ricky was hit)")) dropped++;
            var m = waveRx.Match(msg); if (m.Success) lock (wavesCalled) wavesCalled.Add(int.Parse(m.Groups[1].Value));
            if (type == LogType.Exception || msg.Contains("brain failed") || msg.Contains("brain stopped")) { failures++; firstFailure ??= msg; }
        };
        var down = new HashSet<IntruderT>();
        Action<IntruderT, string> onReport = (o, r) =>
        {
            if (o == null || !o.IsHitman) return;
            if (r == "out of the car" || r == "in the room" || r == "fighting" || r.StartsWith("finder: ")) lock (down) down.Add(o);
        };
        void PutDown() { lock (down) foreach (var o in down) if (o != null && o.Current != IntruderT.State.Dead) o.TakeDamage(1000f); }
        // Waits for cond, putting each hitman down as he gets out and cutting the quiet after each 1:12 card short (one Y per
        // card), as the night runs; false on timeout.
        float ySentFor = -1f;
        async Task<bool> RunNightUntil(Func<bool> cond, float timeout)
        {
            for (float t = 0f; t < timeout && EditorApplication.isPlaying; t += 0.05f)
            {
                PutDown();
                if (cond()) return true;
                if (ySentFor != cardAt && cardAt > 0f && Time.time > cardAt + 3.4f) { ySentFor = cardAt; dir.RequestSkip(); }
                await Wait(0.05f);
            }
            return false;
        }
        string Waves() { lock (wavesCalled) return wavesCalled.Count == 0 ? "none" : string.Join(", ", wavesCalled); }
        bool Called(int w) { lock (wavesCalled) return wavesCalled.Contains(w); }

        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        bool visitorsWere = ScenarioDirector.MarshalVisitors;
        ScenarioDirector.MarshalVisitors = true;
        // The police on, as in the game: a Marshal's bad call must still never bring them (Brett 2026-10-04 (a)).
        PoliceResponse.Enabled = true; PoliceResponse.TestStance = null; PoliceResponse.TestFired = false; PoliceResponse.TestEntryCleared = false;
        var police = PoliceResponse.Instance;
        var hand = OpenDoorHand(false);
        try
        {
            // ── 1. Ricky shot dead under the 1:12 card.
            int nights = dir.NightsDone;
            dir.SetMode(ScenarioDirector.Mode.Marshal);
            if (!await WaitFor(() => IntruderT.Protectee != null, 60)) { ExtraFail("marshal calls: Ricky spawns", dir.Status); return; }
            var ricky = IntruderT.Protectee;
            await Wait(1f);
            ShootOfficer(ricky, HumanBodyBones.Chest);
            if (!await WaitFor(() => dir.NightsDone > nights, 30)) { ExtraFail("marshal calls: you shot Ricky dead", $"no card ({dir.Status})"); return; }
            await Wait(0.5f);                                                // (the show has begun: its booking line is in)
            var v = dir.LastVerdict.GetValueOrDefault();
            MarshalCheck(v.Tier == Tier.Bad && v.Headline == Judge.HeadlineTrialOff && v.Charge == Judge.ChargeKilled
                         && Has(v.Reason, "You shot Ricky") && Has(v.Sentence, "18 U.S.C. 242") && Has(v.Sentence, "Don't drop the soap")
                         && bookings == 1 && policeRuns == 0 && hitmenRuns == 0 && rickyHits == 1 && ricky != null && ricky.Current == IntruderT.State.Dead,
                  "marshal calls: you shoot Ricky dead → the booking, THE TRIAL IS OFF, the federal count",
                  $"{v.Tier} '{v.Headline}' charge '{v.Charge}' — {v.Reason} / {OneLine(v.Sentence)}; bookings {bookings}, police {policeRuns}, hitmen {hitmenRuns}, " +
                  $"Ricky {(ricky != null ? ricky.Current.ToString() : "gone")}");

            // ── 2. Next night, Ricky shot in the leg: he lives.
            nights = dir.NightsDone;
            if (!await WaitFor(() => IntruderT.Protectee != null && IntruderT.Protectee != ricky, 60)) { ExtraFail("marshal calls: the next night's Ricky", dir.Status); return; }
            ricky = IntruderT.Protectee;
            await Wait(1f);
            ShootOfficer(ricky, HumanBodyBones.LeftUpperLeg);
            if (!await WaitFor(() => dir.NightsDone > nights, 30)) { ExtraFail("marshal calls: you shot Ricky in the leg", $"no card ({dir.Status})"); return; }
            await Wait(0.5f);
            v = dir.LastVerdict.GetValueOrDefault();
            MarshalCheck(v.Tier == Tier.Bad && v.Headline == Judge.HeadlineBad && v.Charge == Judge.ChargeInjured
                         && Has(v.Reason, "You shot Ricky") && Has(v.Sentence, "18 U.S.C. 242")
                         && bookings == 2 && policeRuns == 0 && hitmenRuns == 0 && ricky != null && ricky.Current != IntruderT.State.Dead,
                  "marshal calls: you shoot Ricky in the leg → the booking, BAD CALL, the federal count",
                  $"{v.Tier} '{v.Headline}' charge '{v.Charge}' — {v.Reason} / {OneLine(v.Sentence)}; bookings {bookings}, police {policeRuns}, hitmen {hitmenRuns}, " +
                  $"Ricky {(ricky != null ? ricky.Current.ToString() : "gone")}");

            // ── 3. Next night: wave 1, then the courier — the door opened by hand on him and two rounds into him.
            if (!await WaitFor(() => IntruderT.Protectee != null && IntruderT.Protectee != ricky, 60)) { ExtraFail("marshal calls: the third night's Ricky", dir.Status); return; }
            ricky = IntruderT.Protectee;
            bool courierUp = await RunNightUntil(() =>
            {
                var c = Live();
                if (dir.Record == null || dir.Record.Def.Name != CourierName || c == null) return false;
                if (!door.IsOpen && door.HandleFree && (c.Current is IntruderT.State.Outside or IntruderT.State.BreakIn) && FlatOf(c.transform.position, door.Threshold) < 2.6f)
                    OpenByHand(hand, door);
                return door.IsOpen && !c.BehindDoor && c.Current is not (IntruderT.State.Dead or IntruderT.State.Gone);
            }, 240);
            if (!courierUp) { ExtraFail("marshal calls: the courier at the open door", $"{dir.Status}; waves {Waves()}; {Live()?.Summary}"); return; }
            bool wave1First = Called(1) && !Called(2);
            await Wait(0.6f);
            nights = dir.NightsDone;
            int hitmenBefore = hitmenRuns, cancelsBefore = cancels;
            var courier = Live();
            float shotAt = Time.time;
            ShootOfficer(courier, HumanBodyBones.Chest);
            await Wait(0.25f);
            ShootOfficer(courier, HumanBodyBones.Chest);
            bool over = await RunNightUntil(() => dir.NightsDone > nights, 30);
            float cardAfter = Time.time - shotAt;
            await Wait(0.5f);                                                // (the show has begun: its booking line is in)
            var card = dir.LastVerdict.GetValueOrDefault();                  // the night's card: the booking
            MarshalCheck(over && cardAfter < 4.5f && wave1First && policeRuns == 0 && dispatcher == 0
                         && (police == null || !police.Active) && dir.LastStandoff == StandoffEnding.None
                         && card.Tier == Tier.Bad && card.Headline == Judge.HeadlineBad && (card.Charge == Judge.ChargeKilled || card.Charge == Judge.ChargeInjured)
                         && Has(card.Reason, Sentencing.OprReferral) && Has(card.Sentence, "18 U.S.C. 242") && Has(card.Sentence, "Don't drop the soap")
                         && card.WhoItWas == Judge.WhoCourier && bookings == 3,
                  "marshal calls: you shoot the courier between the waves → straight to the booking (no police), the federal count",
                  $"night over {over} {cardAfter:F1} s after the shot; waves before him {Waves()} (wave 1 only {wave1First}); police {policeRuns}, " +
                  $"dispatcher {dispatcher}, {dir.LastStandoff}; card {card.Tier} '{card.Headline}' charge '{card.Charge}' who '{card.WhoItWas}' — " +
                  $"{OneLine(card.Reason)} / {OneLine(card.Sentence)}; bookings {bookings}");
            // The night ends there: the hitman run called off (no card of its own), no wave 2, and a new night with the 1:12 card.
            string wavesOnNight3 = Waves();
            int nightsSeen = safeHouses;
            bool next = await RunNightUntil(() => safeHouses > nightsSeen, 60);
            MarshalCheck(next && cancels == cancelsBefore + 1 && hitmenRuns == hitmenBefore && wavesOnNight3 == "1" && rickyHits == 2,
                  "marshal calls: the night ends on the courier's card — the hitman run called off, no wave 2, a new night",
                  $"next night {(next ? "began" : "never began")}, hitman runs cancelled +{cancels - cancelsBefore}, waves that night {wavesOnNight3}, " +
                  $"Ricky hit {rickyHits} time(s) (both by the test)");

            // ── 4. That night: the courier left alone, then Sal after wave 3 — and Ricky shot in the leg during Sal's visit.
            ricky = IntruderT.Protectee;
            int completed = dir.Completed;
            bool salIn = await RunNightUntil(() => dir.Record != null && dir.Record.Def.Name == SalName && Alive()
                                                   && State() == IntruderT.State.Idle && !Live().BehindDoor, 360);
            if (!salIn) { ExtraFail("marshal calls: Sal comes in", $"{dir.Status}; waves {Waves()}; {Live()?.Summary}"); return; }
            int before = dir.Completed - completed;                          // the courier, left alone
            var leftAlone = dir.LastVerdict.GetValueOrDefault();
            string wavesBeforeSal = Waves();
            await Wait(1f);
            var sal = Live();
            ricky = IntruderT.Protectee;
            nights = dir.NightsDone;
            int policeBefore = policeRuns, dispatcherBefore = dispatcher, bookingsBefore = bookings, droppedBefore = dropped;
            cancelsBefore = cancels;
            // The show's own mode, from its log line (OutcomeShow takes ForceBadMode before Started fires: reading it there is
            // always "none").
            string forceModeAtShow = "no show";
            Application.LogCallback onShow = (msg, trace, type) =>
            {
                if (forceModeAtShow != "no show" || !msg.StartsWith("[HIS] Bad-call show: ")) return;
                var rest = msg.Substring("[HIS] Bad-call show: ".Length);
                int comma = rest.IndexOf(',');
                forceModeAtShow = comma > 0 ? rest.Substring(0, comma) : rest;
            };
            Application.logMessageReceived += onShow;
            try
            {
                float hitAt = Time.time;
                ShootOfficer(ricky, HumanBodyBones.LeftUpperLeg);
                bool booked = await RunNightUntil(() => dir.NightsDone > nights, 12);
                float after = Time.time - hitAt;
                await Wait(0.5f);
                var rc = dir.LastVerdict.GetValueOrDefault();
                MarshalCheck(booked && after <= 1.5f + 1.5f && before == 1 && leftAlone.Tier == Tier.Perfect && wavesBeforeSal == "1, 2, 3"
                             && dropped == droppedBefore + 1 && cancels == cancelsBefore + 1 && !Called(4)
                             && policeRuns == policeBefore && dispatcher == dispatcherBefore && bookings == bookingsBefore + 1
                             && rc.Tier == Tier.Bad && rc.Headline == Judge.HeadlineBad && rc.Charge == Judge.ChargeInjured
                             && Has(rc.Reason, "You shot Ricky") && Has(rc.Sentence, "18 U.S.C. 242"),
                      "marshal calls: Ricky hit during Sal's visit → Sal's round dropped, the run called off (no wave 4), Ricky's card",
                      $"card {(booked ? $"{after:F1} s after the hit" : "never")}; the courier {leftAlone.Tier} ({before} visitor(s) before Sal), waves before Sal {wavesBeforeSal}; " +
                      $"Sal's round dropped +{dropped - droppedBefore}, hitman runs cancelled +{cancels - cancelsBefore}, wave 4 {(Called(4) ? "CALLED" : "never called")}; " +
                      $"police {policeRuns - policeBefore}, dispatcher {dispatcher - dispatcherBefore}, bookings +{bookings - bookingsBefore}; " +
                      $"card {rc.Tier} '{rc.Headline}' charge '{rc.Charge}' — {OneLine(rc.Reason)} / {OneLine(rc.Sentence)}");
                // During the card: the booking override was taken by the show (never left armed), Ricky and Sal can't be hit.
                var rickyNow = IntruderT.Protectee;
                bool rickyShut = rickyNow != null && System.Linq.Enumerable.All(rickyNow.GetComponentsInChildren<Collider>(), c => !c.enabled);
                bool salShut = sal != null && System.Linq.Enumerable.All(sal.GetComponentsInChildren<Collider>(), c => !c.enabled);
                MarshalCheck(forceModeAtShow == "Mugshot" && HIS.Outcome.OutcomeShow.ForceBadMode == null && rickyShut && salShut,
                      "marshal calls: the booking is the mugshot, and Ricky and Sal are stood down under the card",
                      $"the show's mode {forceModeAtShow}, ForceBadMode now {(HIS.Outcome.OutcomeShow.ForceBadMode?.ToString() ?? "none")}; " +
                      $"Ricky {(rickyNow == null ? "missing" : rickyShut ? "stood down" : "still hittable")}, Sal {(sal == null ? "gone" : salShut ? "stood down" : "still hittable")}");
            }
            finally { Application.logMessageReceived -= onShow; }
            MarshalCheck(failures == 0, "marshal calls: no brain failure or exception", failures == 0 ? "none" : $"{failures}, first: {firstFailure}");
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            if (hand != null) UnityEngine.Object.Destroy(hand.gameObject);
            ScenarioDirector.MarshalVisitors = visitorsWere;
            PoliceResponse.TestStance = null; PoliceResponse.Enabled = false;
            if (dir != null) dir.SetMode(ScenarioDirector.Mode.Scenarios);
        }
    }

    static bool Has(string text, string what) => text != null && text.Contains(what);

    /// <summary>The player's hit on <paramref name="o"/>'s hitbox at <paramref name="bone"/> (a stab, a round…), from where you stand.</summary>
    static void HitWith(IntruderT o, HumanBodyBones bone, DamageType type, float amount)
    {
        var anim = o != null ? o.GetComponentInChildren<Animator>() : null;
        var b = anim != null ? anim.GetBoneTransform(bone) : null;
        var col = b != null ? b.GetComponent<Collider>() : null;
        if (col == null || !col.TryGetComponent<IDamageable>(out var target)) { Debug.LogWarning($"[HIS] SCENARIO marshal calls: no hitbox at {bone}"); return; }
        var origin = Camera.main != null ? Camera.main.transform.position : Vector3.up * 1.6f;
        var dir = (col.bounds.center - origin).normalized;
        target.ApplyDamage(new DamageInfo { Amount = amount, Type = type, Direction = dir, Point = col.ClosestPoint(origin), Normal = -dir, Penetration = 1f, Source = Camera.main != null ? Camera.main.gameObject : null });
    }

    /// <summary>A card's text on one log line.</summary>
    static string OneLine(string text) => text?.Replace("\n", " | ");
}
