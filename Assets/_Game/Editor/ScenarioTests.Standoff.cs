using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// The police standoff end to end (research/22, Brett 0.1.25): a bad call brings the police instead of the verdict; you
/// surrender → cuffed → the card shows the charges, an estimated sentence and the soap joke. Beat a wave → the next one
/// comes, and the card tallies the officers. (Officer behaviour itself: ScenarioTests.Police.cs.)
///   Just these: scripts/unity-batch.sh ScenarioTests.RunStandoffHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_standoffOnly;

    public static void RunStandoffHeadless() { s_standoffOnly = s_doorwayOnly = true; RunHeadless(); }

    /// <summary>The same with a second door (Brett 0.1.29): wave 2 = two officers at the front door + one at the side door.</summary>
    public static void RunStandoffTwoDoorsHeadless() { s_standoffTwoDoors = true; s_beforePlay = LoadTwoDoorRoom; RunStandoffHeadless(); }
    static bool s_standoffTwoDoors;

    static IEnumerable<Case> WithStandoffCases(IEnumerable<Case> cases)
    {
        if (!s_standoffOnly) foreach (var c in cases) yield return c;
        if (s_standoffOnly || !s_doorwayOnly) foreach (var c in StandoffCases()) yield return c; // (not tacked onto the other focused suites)
    }

    static System.Action Police(PlayerStance? stance) => () =>
    {
        PoliceResponse.Enabled = true; PoliceResponse.TestStance = stance; PoliceResponse.TestFired = false; PoliceResponse.TestEntryCleared = false;
    };

    static async Task<bool> StandoffOver(float timeout) =>
        await WaitFor(() => ScenarioDirector.Instance.LastStandoff != StandoffEnding.None && PoliceResponse.Instance != null && !PoliceResponse.Instance.Active, timeout);

    static IEnumerable<Case> StandoffCases() => s_standoffTwoDoors ? BaseStandoffCases().Append(SwatParksCase()) : BaseStandoffCases().Append(UnderFireCase());

    /// <summary>
    /// Brett 0.1.47: "if im standing there with the door open cops shouldnt sprint all the way to the door until stacking up
    /// while im shooting them". The front door open, the player armed and firing on them: wave 1 bounds up the path — holds
    /// and returns fire from out there, moves at the aimed walk — and still gets to the door.
    /// </summary>
    static Case UnderFireCase() => new Case { Name = "Surrenders", Expect = Tier.Bad, Doorway = true, Setup = Police(null), Act = async () =>
    {
        const string what = "police: under fire with the door open → they bound up the path, firing back";
        await WaitFor(() => Alive() && State() == IntruderT.State.Complying, 60); await Wait(1.5f);
        Shoot(3, 40f, HumanBodyBones.Chest);
        var police = PoliceResponse.Instance;
        if (!await WaitFor(() => police != null && police.Active && police.Officers.Count >= 2, 20)) { ExtraFail(what, "no first wave"); return; }
        var door = HIS.Room.VirtualDoor.Instance;
        if (door != null && !door.IsOpen) door.OpenGently(0.5f, 80f);
        // The player stands in the doorway's line, 2.2 m in (from the path, only the doorway is open: VirtualDoor.SegmentThroughOpening).
        if (door != null)
        {
            // 0.1.48 review: the scanned walls only stop rays from the room side; from the path, only the doorway may see in.
            var street = door.Threshold + door.Outward * 8f + Vector3.up * 1.45f;
            var inLine = door.Threshold + door.Inward * 2.2f + Vector3.up * 1.3f;
            var behindWall = door.Threshold + door.Inward * 0.6f + door.HingeToLatch * (door.Opening.width * 0.5f + 1.2f) + Vector3.up * 1.3f;
            bool sees = door.SegmentThroughOpening(street, inLine), throughWall = door.SegmentThroughOpening(street, behindWall);
            string w = $"in line {sees}, behind the wall beside the door {throughWall}";
            if (sees && !throughWall) ExtraPass("police: from the path only the doorway sees in", w); else ExtraFail("police: from the path only the doorway sees in", w);
        }
        var cam = Camera.main; var camWas = cam != null ? cam.transform.position : Vector3.zero;
        if (cam != null && door != null) { var at = door.Threshold + door.Inward * 2.2f; cam.transform.position = new Vector3(at.x, camWas.y, at.z); }
        int bounding = 0, pathShots = 0, atDoor = 0;
        System.Action<IntruderT, string> onReport = (o, r) =>
        {
            if (r.StartsWith("under fire on the way up")) bounding++;
            if (r.StartsWith("fired") && o.BehindDoor && o.Current != IntruderT.State.BreakIn) pathShots++;
            if (r == "at the door") atDoor++;
        };
        IntruderT.OfficerReported += onReport;
        HIS.Core.PlayerState.GodMode = true;                                  // their rounds land, but the run goes on
        PoliceResponse.TestStance = PlayerStance.Armed; PoliceResponse.TestFired = true;
        try
        {
            bool reached = await WaitFor(() => atDoor >= 1 || police.EntryCleared, 70);
            string info = $"{bounding} officer(s) bounding, {pathShots} round(s) fired from the path, reached the door {reached}";
            if (bounding >= 1 && pathShots >= 1 && reached) ExtraPass(what, info); else ExtraFail(what, info);
        }
        finally
        {
            IntruderT.OfficerReported -= onReport;
            PoliceResponse.TestFired = false;
            PoliceResponse.TestStance = PlayerStance.Surrendered;          // (before god mode goes: no round lands on the way out)
        }
        bool over = await StandoffOver(150);
        HIS.Core.PlayerState.GodMode = false;
        if (cam != null) cam.transform.position = camWas;
        if (!over) ExtraFail("police: arrested after (under fire)", "still running");
    } };

    /// <summary>
    /// Brett 0.1.44: "after third wave of cops nothing happens" (every wave 3 on the headset). The SWAT truck parks and both
    /// side officers go round the house together: RouteAround's one shared list was refilled under the first one's feet, his
    /// brain died ("Collection was modified"), he stood hidden behind the house and the wave never ended. Now both must reach
    /// the side door with no brain failure; then the wave's last officers are withdrawn (the stuck-officer path) and wave 4
    /// must still come.
    /// </summary>
    static Case SwatParksCase() => new Case { Name = "Surrenders", Expect = Tier.Bad, Doorway = true, Setup = Police(null), Act = async () =>
    {
        const string what = "waves: SWAT truck parks → both side officers go round and reach the side door";
        await WaitFor(() => Alive() && State() == IntruderT.State.Complying, 60); await Wait(1.5f);
        Shoot(3, 40f, HumanBodyBones.Chest);
        var police = PoliceResponse.Instance;
        if (!await WaitFor(() => police != null && police.Active && police.Officers.Count >= 2, 20)) { ExtraFail(what, "no first wave"); return; }
        int failures = 0, withdrawn = 0;
        Application.LogCallback onLog = (msg, trace, type) => { if (msg.Contains("brain failed") || (type == LogType.Exception && msg.Contains("Collection was modified"))) failures++; };
        var sideAtDoor = new HashSet<IntruderT>();
        System.Action<IntruderT, string> onReport = (o, r) =>
        {
            if (r == "withdrawn") withdrawn++;
            if (r == "at the door" && o.Wave == 3 && o.DoorIndex == 1) sideAtDoor.Add(o);
        };
        Application.logMessageReceived += onLog;
        IntruderT.OfficerReported += onReport;
        try
        {
            for (int w = 1; w <= 2; w++)                                      // waves 1 and 2: down as soon as they're out
            {
                await WaitFor(() => police.Wave == w && police.Officers.Count > 0 && NoneRiding(police), 40);
                PoliceResponse.TestFired = true; await Wait(0.5f);
                foreach (var o in new List<IntruderT>(police.Officers)) o.TakeDamage(1000f);
                PoliceResponse.TestFired = false;
            }
            if (!await WaitFor(() => police.Wave >= 3 && police.Officers.Count >= 4 && NoneRiding(police), 40))
            { ExtraFail(what, $"wave {police.Wave}, {police.Officers.Count} officer(s), riding {!NoneRiding(police)}"); return; }
            // The truck parked (nobody shot its driver). The front two go down; the side two go round on their own.
            foreach (var o in new List<IntruderT>(police.Officers)) if (!o.name.Contains("side door")) o.TakeDamage(1000f);
            bool both = await WaitFor(() => sideAtDoor.Count >= 2, 60);
            string info = $"{sideAtDoor.Count}/2 at the side door, {failures} brain failure(s), {withdrawn} withdrawn";
            if (both && failures == 0 && withdrawn == 0) ExtraPass(what, info); else ExtraFail(what, info);

            // Whoever's left of wave 3 is withdrawn (what the watchdog does to a stuck one): the wave still ends, wave 4 comes.
            const string what2 = "waves: withdrawn officers count — the wave ends, the next one comes";
            foreach (var o in new List<IntruderT>(police.Officers)) if (o.Wave == 3) o.Withdraw("test");
            bool w4 = await WaitFor(() => police.Wave >= 4 && police.Officers.Count > 0, 15);
            string info2 = $"wave {police.Wave}, {police.Officers.Count} officer(s), {withdrawn} withdrawn";
            if (w4) ExtraPass(what2, info2); else ExtraFail(what2, info2);
        }
        finally
        {
            Application.logMessageReceived -= onLog;
            IntruderT.OfficerReported -= onReport;
            PoliceResponse.TestFired = false;
        }
        PoliceResponse.TestStance = PlayerStance.Surrendered;
        if (!await StandoffOver(150)) ExtraFail("waves: arrested after (SWAT parks)", "still running");
    } };

    static IEnumerable<Case> BaseStandoffCases() => new[]
    {
        // A bad call (his hands were up and empty), then you surrender: cuffed, booked, the charges and the sentence at the end.
        new Case { Name = "Surrenders", Expect = Tier.Bad, Doorway = true, Setup = Police(PlayerStance.Surrendered), Act = async () =>
            { await WaitFor(() => Alive() && State() == IntruderT.State.Complying, 60); await Wait(1.5f);
              Shoot(3, 40f, HumanBodyBones.Chest);
              var police = PoliceResponse.Instance;
              if (!await WaitFor(() => police != null && police.Active, 15)) { ExtraFail("standoff: police come", "no standoff after a bad call"); return; }
              ExtraPass("standoff: police come", "verdict withheld, police responding");
              if (!await StandoffOver(150)) { ExtraFail("standoff: arrested", $"still running (wave {police.Wave}, {police.Officers.Count} officer(s))"); return; }
              var card = police.EndCard; var e = ScenarioDirector.Instance.LastStandoff;
              bool ok = e == StandoffEnding.Arrested && card.Tier == Tier.Bad && card.Sentence != null && card.Sentence.Contains("Voluntary manslaughter")
                        && card.Sentence.Contains("Estimated sentence") && card.Sentence.Contains("soap");
              if (ok) ExtraPass("standoff: arrested, crimes + sentence", card.Sentence.Replace("\n", " | "));
              else ExtraFail("standoff: arrested, crimes + sentence", $"{e}: {card.Headline} / {card.Sentence}"); } },
        // Fire on them and win: they return fire; both officers down → the next wave; then surrender: the tally has the officers.
        new Case { Name = "Surrenders", Expect = Tier.Bad, Doorway = true, Setup = Police(null), Act = async () =>
            { await WaitFor(() => Alive() && State() == IntruderT.State.Complying, 60); await Wait(1.5f);
              Shoot(3, 40f, HumanBodyBones.Chest);
              var police = PoliceResponse.Instance;
              if (!await WaitFor(() => police != null && police.Active && police.Officers.Count >= 2, 20)) { ExtraFail("waves: first wave", "no officers"); return; }
              await WaitFor(() => police.EntryCleared, 60);
              RouteCheck(police, "wave 1");
              PoliceResponse.TestFired = true;                                  // opened fire on them
              await Wait(2f);
              foreach (var o in new List<IntruderT>(police.Officers)) o.TakeDamage(1000f);
              PoliceResponse.TestFired = false;
              bool next = await WaitFor(() => police.Wave >= 2 && police.Officers.Count > 0, 30);
              if (next) ExtraPass("waves: beat wave 1", $"wave {police.Wave}, {police.Officers.Count} officer(s)");
              else { ExtraFail("waves: beat wave 1", $"wave {police.Wave}"); return; }
              if (await WaitFor(() => police.Officers.Count > 0 && NoneRiding(police), 20)) { await Wait(1.5f); RouteCheck(police, "wave 2"); }
              if (HIS.Room.VirtualDoor.All.Count > 1)
              {
                  await WaitFor(() => police.Officers.Count >= 3, 10);
                  // 0.1.42: the side officer rides in the cruiser's back seat and goes round the house once it's parked.
                  await WaitFor(() => { foreach (var o in police.Officers) if (o.DoorIndex == 1) return true; return false; }, 40);
                  int front = 0, side = 0; foreach (var o in police.Officers) if (o.DoorIndex == 0) front++; else side++;
                  bool sideIn = await WaitFor(() => { foreach (var o in police.Officers) if (o.DoorIndex == 1 && !o.BehindDoor) return true; return false; }, 60);
                  if (front == 2 && side == 1 && sideIn) ExtraPass("waves: wave 2 splits over both doors", $"{front} front, {side} side (rode in, went round); the side officer came in");
                  else ExtraFail("waves: wave 2 splits over both doors", $"{front} front, {side} side, side officer in {sideIn}");

                  // Brett 0.1.42: "if I shoot the driver the 3 should get out and have to come from front". Wave 3: all four
                  // ride in the SWAT truck; its driver shot dead as it pulls up → it crashes → the three left all come to the
                  // front door, nobody round the back.
                  PoliceResponse.TestFired = true;
                  await Wait(0.5f);
                  foreach (var o in new List<IntruderT>(police.Officers)) o.TakeDamage(1000f);
                  PoliceResponse.TestFired = false;
                  bool w3 = await WaitFor(() => police.Wave >= 3 && police.Officers.Count >= 4, 30);
                  IntruderT driver = null;
                  await WaitFor(() => { foreach (var o in police.Officers) if (o.Role == OfficerRole.Lead && o.Riding) { driver = o; return true; } return false; }, 10);
                  int riding = 0; foreach (var o in police.Officers) if (o.Riding) riding++;
                  if (driver != null) driver.TakeDamage(1000f);
                  bool crashed = await WaitFor(() => HIS.Security.PoliceCars.WaveCarCrashed(3), 5);
                  await WaitFor(() => NoneRiding(police), 25);
                  await Wait(9f);                                             // long past when a side man would have gone round
                  int f3 = 0, s3 = 0; foreach (var o in police.Officers) if (o.DoorIndex == 0) f3++; else s3++;
                  string info3 = $"wave {police.Wave}: {riding} rode in, driver {(driver != null ? "shot" : "never seen riding")}, crashed {crashed}; after: {f3} front, {s3} side";
                  if (w3 && riding == 4 && driver != null && crashed && s3 == 0 && f3 == 3) ExtraPass("waves: SWAT truck shot to a stop → all to the front", info3);
                  else ExtraFail("waves: SWAT truck shot to a stop → all to the front", info3);
              }
              // Brett 0.1.44: "after third wave of cops nothing happens". Beat every wave through the third: the fourth must
              // come and reach the door.
              while (police.Active && police.Wave < 4)
              {
                  int w = police.Wave;
                  PoliceResponse.TestFired = true;
                  await Wait(0.5f);
                  foreach (var o in new List<IntruderT>(police.Officers)) o.TakeDamage(1000f);
                  PoliceResponse.TestFired = false;
                  if (!await WaitFor(() => police.Wave > w && police.Officers.Count > 0, 40)) break;
              }
              var atDoor4 = new HashSet<IntruderT>();
              System.Action<IntruderT, string> onReport = (o, what) => { if (what == "at the door" && o.Wave >= 4) atDoor4.Add(o); };
              IntruderT.OfficerReported += onReport;
              bool reached4 = police.Wave >= 4 && await WaitFor(() => atDoor4.Count > 0, 60);
              IntruderT.OfficerReported -= onReport;
              var who4 = new List<string>();
              foreach (var o in police.Officers)
                  if (o != null) who4.Add($"{o.name}: {o.Current}{(o.Riding ? " riding" : "")}{(o.BehindDoor ? " behind" : "")} at {o.transform.position:F1}");
              string info4 = $"wave {police.Wave}, {police.Officers.Count} officer(s), {atDoor4.Count} at the door; {string.Join("; ", who4)}";
              if (reached4) ExtraPass("waves: wave 4 comes after the SWAT wave", info4); else ExtraFail("waves: wave 4 comes after the SWAT wave", info4);
              PoliceResponse.TestStance = PlayerStance.Surrendered;
              if (!await StandoffOver(150)) { ExtraFail("waves: arrested after", "still running"); return; }
              var card = police.EndCard;
              bool ok = card.Sentence != null && card.Sentence.Contains("Murder of a peace officer") && card.Sentence.Contains("without the possibility of parole");
              if (ok) ExtraPass("waves: the tally", card.Sentence.Replace("\n", " | ")); else ExtraFail("waves: the tally", card.Sentence); } },
    };

    /// <summary>
    /// Brett 0.1.38: "the cops ran in zig zags outside for a few laps before coming down the path" — riders were routed round
    /// their own car on every leg (13–15 legs, 7–9 of them detours). Each officer's walk up may add at most 4 detour points.
    /// </summary>
    static bool NoneRiding(PoliceResponse police) { foreach (var o in police.Officers) if (o != null && o.Riding) return false; return true; }

    static void RouteCheck(PoliceResponse police, string wave)
    {
        int worst = 0; string who = "";
        foreach (var o in police.Officers) if (o != null && o.RouteDetours >= worst) { worst = o.RouteDetours; who = o.name; }
        if (worst <= 4) ExtraPass($"standoff: {wave} walks up round the cars, no laps", $"most detour points {worst} ({who})");
        else ExtraFail($"standoff: {wave} walks up round the cars, no laps", $"{worst} detour points ({who})");
    }
}
