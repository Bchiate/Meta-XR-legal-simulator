using System.Collections.Generic;

namespace HIS.Scenario
{
    // Shared vocabulary of the scenario simulator (PLAN.md M2a/M2b; research/07, 08, 13). Pure data — no Unity
    // references — so the Judge stays testable headless and weapons/intruder code never depends on Scenario code.

    /// <summary>What the player yells (controller A/X; research/08).</summary>
    public enum Command { None, WhoIsThere, ShowHands, DropWeapon, GetDown, GetOut }

    /// <summary>The intruder's hidden intent for this scenario. The judge never reads it — only what was observable.</summary>
    public enum Intent { MeleeAttacker, KnifeAttacker, Burglar, Surrenderer, Innocent, Gunman, Police }

    public enum EntryType { ForcedKick, OpenedUnlocked }

    /// <summary>How the door goes this round (Brett's pick #1: "the door never plays the same way twice"). Append only.</summary>
    public enum EntryStyle { Classic, Rush, KnockThenKick, WeakGuy, QuietPry, FalseAlarm }

    public enum WeaponKind { None, Knife, Gun }   // append only (Judge reason tables are indexed by it)

    /// <summary>Who he really is (revealed in the debrief; drives the "family / identified" rules). Append only. Officer: a
    /// deputy marshal on a Marshal night (Ortiz, research/27 §1), who says so at the door and has a key.</summary>
    public enum Identity { Stranger, Neighbor, Family, Officer }

    /// <summary>What a visitor carries up the path (ScenarioDef.Carries). ByRole: as before (the burglar's crowbar, the delivery
    /// man's parcel); None: hands empty; PizzaBox: a Marshal night's pizza courier.</summary>
    public enum VisitorProp { ByRole, None, PizzaBox }

    /// <summary>
    /// Who the player is tonight (research/27 §4; Brett 2026-10-01: "1. US Marshal, 2. Secret Service, 3. husband and wife").
    /// Judge.Role holds it (set from the mode by ScenarioDirector.ApplyMode). Homeowner is the default: every rule, line and
    /// card as before the roles. Append only.
    /// </summary>
    public enum Role { Homeowner, Marshal, SecretService, Husband }

    public enum Posture { Standing, HandsUp, Prone }

    /// <summary>
    /// Everything the player could see or hear about one intruder at one moment. Snapshotted just before a hit is
    /// applied (so the hit that kills him is judged on what he was doing, not on him being dead).
    /// </summary>
    public struct IntruderFacts
    {
        public int IntruderId;
        public float Time;                    // Time.time of the snapshot
        public WeaponKind WeaponInHand;       // a visible weapon in his hand
        public bool Advancing;                // moving toward the player (> 0.3 m/s closing speed)
        public bool Attacking;                // swinging / stabbing, or charging within striking distance
        public bool Fleeing;                  // moving away from the player (running for the door)
        public Posture Posture;               // hands up / prone = surrendering
        public bool Down;                     // legs shot out, crawling, incapacitated
        public bool IdentityStated;           // he said who he is ("It's me, Mike from next door!")
        public bool ForcedEntry;              // he kicked the door in (vs walked in through an unlocked door)
        public bool Inside;                   // past the door plane, in the room
        public float Distance;                // to the player's feet, metres
        public float SecondsSinceThreat;      // since he last attacked / charged / advanced armed (large if never)
        public float UnarmedStandingSeconds;  // since he stopped advancing with empty hands inside (0 while moving/armed)
        public bool Dead;
        public bool BehindDoor;               // out of sight behind the closed door: the player fired blind, through it
        public bool ForcingDoor;              // kicking the door in right now (PC 198.5 "is unlawfully and forcibly entering")
        public bool SeenArmedOnCamera;        // behind the door, but the door camera showed him with a gun in hand (the last 3 s)
        // The gun (M3a). Pointing comes from the prop's Muzzle ray (within ~10° of the player's head or chest), never from
        // which clip plays: what the player saw is what the judge weighs.
        public bool PointingAtPlayer;         // his gun's muzzle is on you
        public bool RaisingGun;               // the visible + audible tell: bringing the gun up at you ("Don't move!")
        public bool Firing;                   // he fired at you in the last half second
        // A Marshal night (research/27 §4): a deputy marshal (Ortiz) who radioed ahead (ScenarioDef.RadioAhead) or said who he
        // was, at the door or to your face. The judge names him on the card ("Deputy Ortiz. He radioed ahead and gave his name.").
        public bool KnownDeputy;

        public bool Surrendering => Posture != Posture.Standing;
    }

    public enum ForceKind { Shot, Stab, Slash, Punch }

    /// <summary>One use of force by the player on an intruder (a shotgun blast = one event, however many pellets hit).</summary>
    public struct ForceEvent
    {
        public float Time;
        public ForceKind Kind;
        public int ShotId;            // groups pellets of one trigger pull
        public IntruderFacts Before;  // facts just before this hit landed
        public float Damage;          // after body-part multipliers
        public string BodyPart;
        public bool Killed;           // this event killed him
    }

    /// <summary>How a round ended. Append only. Relieved: a Marshal night's 2 AM relief, a few seconds after his hello (research/27 §1).
    /// Stopped: a Marshal night's visitor hurt by the player — the round stops a beat after the bad call (Brett 2026-10-04 (a):
    /// every bad call on a Marshal night is the arrest).</summary>
    public enum EndReason { Killed, Left, Custody, PlayerDied, TimedOut, Relieved, Stopped }

    public enum Tier { Perfect, Good, Bad, YouDied }

    /// <summary>The judge's call on one scenario, ready for the show and the debrief card.</summary>
    public struct Verdict
    {
        public Tier Tier;
        public bool CloseCall;
        public string Headline;   // "GOOD CALL" / "BAD CALL" / "PERFECT" / "YOU DIDN'T SURVIVE"
        public string Reason;     // one or two plain sentences: why
        public string Charge;     // bad call only: "Voluntary manslaughter" etc.
        public string WhoItWas;   // "A burglar with a knife", "Mike, your drunk neighbor"
        public string Sentence;   // the police ending's tally: charges, estimated sentence (Sentencing.Tally); replaces the charge line
    }

    /// <summary>A scenario to play (a deck entry).</summary>
    public class ScenarioDef
    {
        public string Name;          // "Burglar who runs"
        public Intent Intent;
        public EntryType Entry;
        public WeaponKind Weapon;
        public Identity Identity;
        public string WhoItWas;      // debrief line
        public string VoicePrefix;   // clip keys vo.<prefix>.* for an Innocent visitor's lines in Mike's slots ("deputy", "brother", "courier"; null = Mike's "neighbor")
        // ── A Marshal night's visitors (research/27 §1, §5). Every one is optional: unset, a def plays exactly as before. ──
        /// <summary>His way through the door, ahead of the director's roll (the courier: <see cref="EntryStyle.FalseAlarm"/>).</summary>
        public EntryStyle? Style;
        /// <summary>The caption's name for him ("Ortiz", "Sal", "Courier"); null = by <see cref="Identity"/> as before.</summary>
        public string Speaker;
        /// <summary>
        /// An Intent.Innocent visitor who isn't drunk Mike: no drunk sway, trip, stumble, hiccup or rattle at the handle (Sal and
        /// Ortiz never stagger: research/27 §6 step 8). Inverted from the spec's "Drunk" flag so every existing Innocent def,
        /// Mike and the tests' included, stays drunk without a change.
        /// </summary>
        public bool Sober;
        /// <summary>Seconds after his hello the round ends (<see cref="EndReason.Relieved"/>): the 2 AM relief. ≤ 0 = never.</summary>
        public float RelievedAfter = -1f;
        // ── VISITORS (Brett 0.1.56, 2026-10-04: "he also just stands there for like a minute ... the ortiz dude could be anybody
        //    walking up ... pizza delivery guy carrying a mail package"). Marshal visitor defs only; unset = as before. ──
        /// <summary>Leaving, he walks out the door and off along the front of the house past the porch camera's edge before he's
        /// gone (not a stop on the step).</summary>
        public bool WalksOff;
        /// <summary>A visit: seconds he stays after his hello (or after saying who he is when challenged first), standing and
        /// facing you, before his leaving line — instead of Mike's wander. ≤ 0 = Mike's rules.</summary>
        public float StaysFor = -1f;
        /// <summary>What he carries up the path (ByRole = as before: the burglar's crowbar, the delivery man's parcel).</summary>
        public VisitorProp Carries;
        /// <summary>A radio key he calls in on before he walks up (Ortiz: "Marshal, Ortiz. Coming up the walk."); null = none.</summary>
        public string RadioAhead;
        /// <summary>Starting morale m0 for the compliance roll (research/08 §2c: P = fit × (1 − m)); above 1 he never gives in. &lt;0 = the intent's default.</summary>
        public float Morale = -1f;
        /// <summary>Gunman: challenged, he runs for the door with the gun instead of raising it (research/08 "gun in hand" burglar).</summary>
        public bool RunsWhenChallenged;
    }

    /// <summary>What happened in one scenario: the evidence the judge reads.</summary>
    public class ScenarioRecord
    {
        public ScenarioDef Def;
        public float StartTime, EndTime;
        public EndReason End;
        public readonly List<ForceEvent> Force = new();
        public readonly List<(float time, Command command)> Commands = new();
        public IntruderFacts LastFacts;      // facts at the end (or at death)
        public bool PlayerHurt;
    }
}
