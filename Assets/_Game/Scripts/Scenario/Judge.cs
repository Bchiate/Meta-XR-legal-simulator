using System;
using System.Collections.Generic;

namespace HIS.Scenario
{
    /// <summary>
    /// The lean verdict judge (PLAN.md §2, v1.1): Brett's simple rules and nothing more, because gameplay feel comes first.
    /// Every use of force is judged on what the player could see at that moment (the IntruderFacts snapshotted just before
    /// the hit landed), never on the intruder's hidden Intent. The scenario verdict is then the worst call across all of
    /// them. Pure C# with no Unity types, so JudgeSelfTest can run it headless and it can grow into the research/07 table.
    /// Reasons are plain paraphrases for the debrief card (never statute quotes). Every string is built once, so the
    /// DebugHud can show a live "if you fired now" call through <see cref="JudgeFacts"/> without allocating per frame.
    /// </summary>
    public static class Judge
    {
        // ---- Tunables: house rules, not law (Brett D1). Static fields so a debug menu can tweak them live. ----

        /// <summary>After a stranger kicks the door in and stops advancing with empty hands, force is a GOOD CALL for this long.</summary>
        public static float StandingGoodSeconds = 3f;
        /// <summary>From this many seconds of him standing there unarmed it's a BAD CALL. Between the two it's a CLOSE CALL leaning good.</summary>
        public static float StandingBadSeconds = 8f;
        /// <summary>Reaction grace after a threat ends (he turns to run, drops, raises his hands). Research/07 G1.</summary>
        public static float ThreatGraceSeconds = 1f;
        /// <summary>A hit later than this inside the grace window still counts as good, but gets the CLOSE CALL tag.</summary>
        public static float GraceCloseSeconds = 0.5f;
        /// <summary>An armed man standing this close (metres) can strike before you can react, even if he isn't moving.</summary>
        public static float StrikeDistance = 2.5f;
        /// <summary>
        /// A downed man (legs out) with the weapon still in his hand who crawls your way from within StrikeDistance + this
        /// is still a threat: he's a lunge from your legs (Brett 0.1.10). Further out, crawling toward you is not enough.
        /// </summary>
        public static float DownReachExtra = 0.5f;

        /// <summary>
        /// What the evidence recorder should put in <see cref="IntruderFacts.SecondsSinceThreat"/> while he has never
        /// threatened. A zeroed struct would read as "he was a threat just now" and make every hit a good call.
        /// </summary>
        public const float NoThreatYet = 9999f;

        // ---- Who the player is (research/27 §4; Brett 2026-10-01: Marshal, then Secret Service, then husband) ----

        /// <summary>
        /// The player's role tonight, set by ScenarioDirector.ApplyMode from the mode: <c>Role.Marshal</c> on a Marshal
        /// night, <c>Role.Homeowner</c> otherwise. Homeowner is the default, and with it every rule below is exactly as it
        /// was before the roles (the Scenarios and Sandbox game). A static like the tunables: JudgeSelfTest saves and restores it.
        /// </summary>
        public static Role Role { get; set; } = Role.Homeowner;

        /// <summary>
        /// PC 198.5's presumption (a stranger forcing his way into YOUR home is presumed a deadly threat) needs a home of yours:
        /// the homeowner's or the husband's. A Marshal at a safe house (or a Secret Service agent) gets no presumption: he needs
        /// to see a weapon or an attack (research/27 §4). It gates rules 0, 1b and 7, and picks the banner's disclaimer.
        /// </summary>
        public static bool HomePresumption => Role is Role.Homeowner or Role.Husband;

        public const string HeadlineGood = "GOOD CALL", HeadlineBad = "BAD CALL", HeadlinePerfect = "PERFECT", HeadlineDied = "YOU DIDN'T SURVIVE";
        public const string ChargeKilled = "Voluntary manslaughter", ChargeInjured = "Assault with a deadly weapon", ChargeBattery = "Battery";

        // ---- Fixed reasons (one or two short sentences, readable at arm's length in VR) ----
        const string Attacking = "He was attacking you.";
        const string JustThreat = "He was a threat a moment ago. The law allows a split second to react.";
        const string JustRan = "He had just turned to run. The law allows a split second to react.";
        const string JustGaveUp = "He had only just given up. The law allows a split second to react.";
        const string JustDown = "He had only just gone down. The law allows a split second to react.";
        const string AlreadyDown = "He was already down. He couldn't hurt you anymore.";
        const string HandsUpEmpty = "His hands were up and empty. Once the danger is over, so is your right to use force.";
        const string ProneEmpty = "He was face-down on the floor with empty hands. Once the danger is over, so is your right to use force.";
        const string FleeingEmpty = "He was running away. Hurting someone who is leaving isn't self-defense.";
        const string Identified = "He told you who he was, and his hands were empty.";
        const string WalkedIn = "He walked in through an unlocked door and wasn't threatening you.";
        const string WalkedInComing = "He was coming toward you, but his hands were empty and he hadn't attacked you."; // let in (research/25)
        const string StillOutside = "He was still outside and hadn't tried to force his way in.";
        const string BrokeInAdvancing = "A stranger kicked your door in and was coming at you.";
        const string JustKickedIn = "He had just kicked your door in. The law presumes you feared for your life.";
        const string KickingDoorIn = "He was kicking your door in. The law presumes you feared for your life, but you fired blind.";
        const string FiredBlind = "You fired through a closed door at someone you couldn't see.";
        const string ArmedOnCamera = "The door camera showed him coming to your door with a gun in his hand.";
        const string PerfectCustody = "You held him until the police arrived. You handled it without firing a shot.";
        const string PerfectLeft = "He left and nobody got hurt. You handled it without firing a shot.";
        const string PerfectOther = "Nobody got hurt. You handled it without firing a shot.";
        const string DiedNoForce = "He got to you before you could stop him.";
        const string DiedFought = "You fought back, but he got to you first.";
        // His gun on you (rule 0b, M3a)
        const string ShootingAtYou = "He was shooting at you.";
        const string PointingGun = "He was pointing a gun at you.";
        const string RaisingGunAt = "He was raising his gun at you.";
        const string DownPointing = "He was down, but still pointing the gun at you.";
        const string TurnedAndPointed = "He was running, but turned and pointed the gun at you.";
        const string TurnedRaising = "He was running, but turned and was raising his gun at you.";
        // The Marshal (research/27 §4). Deadpan, and nothing names a door.
        const string ForcedNotHome = "He forced the door, but this isn't your home. You needed to see a weapon or an attack.";
        // (VISITORS, Brett 0.1.56: true on every path now — he radios ahead of each walk-up and gives his name, door open or shut.)
        const string KnownDeputyReason = "Deputy Ortiz. He radioed ahead and gave his name.";

        // ---- Reasons that name his weapon, indexed by (int)WeaponKind; built once in the static constructor ----
        static readonly string[] s_cameAtWith, s_downStillComing, s_armedClose, s_handsUpArmed, s_proneArmed, s_fleeingArmed, s_walkedInArmed, s_brokeInArmed;

        // "N seconds" reasons for the standing timer, filled lazily (the scenario clock is 90 s, so 100 slots cover it).
        static readonly string[] s_standingClose = new string[100], s_standingBad = new string[100];

        static Judge()
        {
            int count = 1;
            foreach (WeaponKind k in Enum.GetValues(typeof(WeaponKind))) count = Math.Max(count, (int)k + 1);
            s_cameAtWith = PerWeapon(count, "He came at you with a {0}.");
            s_downStillComing = PerWeapon(count, "He was down, but still coming at you with the {0}.");
            s_armedClose = PerWeapon(count, "He had a {0} and was close enough to use it.");
            s_handsUpArmed = PerWeapon(count, "His hands were up, but he was still holding the {0}.");
            s_proneArmed = PerWeapon(count, "He was on the ground, but still holding the {0}.");
            s_fleeingArmed = PerWeapon(count, "He was running away, even with the {0} in his hand. He wasn't coming at you anymore.");
            s_walkedInArmed = PerWeapon(count, "He walked in holding a {0}, but he wasn't coming at you.");
            s_brokeInArmed = PerWeapon(count, "He kicked your door in holding a {0}.");
        }

        /// <summary>Debrief noun for a weapon; unknown kinds read "weapon".</summary>
        static string WeaponNoun(WeaponKind k) => k switch
        {
            WeaponKind.Knife => "knife",
            WeaponKind.Gun => "gun",
            _ => "weapon",
        };

        static string[] PerWeapon(int count, string format)
        {
            var a = new string[count];
            for (int i = 0; i < count; i++) a[i] = string.Format(format, WeaponNoun((WeaponKind)i));
            return a;
        }

        static string Pick(string[] table, WeaponKind k)
        {
            int i = (int)k;
            return (uint)i < (uint)table.Length ? table[i] : table[0];
        }

        static string StandingReason(bool bad, float seconds)
        {
            int n = seconds <= 0f ? 0 : (int)seconds;
            var cache = bad ? s_standingBad : s_standingClose;
            if (n < cache.Length && cache[n] != null) return cache[n];
            string unit = n == 1 ? "second" : "seconds";
            string s = bad
                ? $"He'd been standing there unarmed for {n} {unit}. The break-in alone didn't justify it anymore."
                : $"He kicked your door in, but he'd been standing there unarmed for {n} {unit}.";
            if (n < cache.Length) cache[n] = s;
            return s;
        }

        static (Tier tier, bool close, string reason) Good(string reason) => (Tier.Good, false, reason);
        static (Tier tier, bool close, string reason) GoodClose(string reason) => (Tier.Good, true, reason);
        static (Tier tier, bool close, string reason) Bad(string reason) => (Tier.Bad, false, reason);

        /// <summary>Judges one use of force on what the player could see just before it landed (<see cref="ForceEvent.Before"/>).</summary>
        public static (Tier tier, bool close, string reason) JudgeForce(in ForceEvent e)
        {
            ref readonly var f = ref e.Before;
            return JudgeFacts(in f);
        }

        /// <summary>
        /// Brett's rules in order, first match wins. Returns only Tier.Good or Tier.Bad. Also usable live ("what would a
        /// hit right now be?") for the DebugHud's provisional verdict; it allocates nothing after the first call per string.
        /// </summary>
        public static (Tier tier, bool close, string reason) JudgeFacts(in IntruderFacts f)
        {
            var weapon = f.WeaponInHand;
            bool armed = weapon != WeaponKind.None;
            bool surrendering = f.Posture != Posture.Standing;   // same as f.Surrendering, without the defensive copy on an `in` struct

            // 0. Fired through the closed door: the player saw only the door, so nothing else he did counts. A man kicking it
            //    in is covered (PC 198.5, "is unlawfully and forcibly entering"), and firing blind keeps it a close call.
            //    But one the door camera showed coming up with a gun in his hand was seen (Brett 0.1.29: "if i can see a dude
            //    with a gun approaching my door on camera I think its a good call to shoot"). Knocking, pounding, trying the
            //    handle or just standing out there, unseen or empty-handed: bad.
            //    Research/27 §4: a deputy who said who he was at the door (and has a key) is bad by name, and the kicking-in
            //    cover is the home presumption's, so it needs one (a Marshal firing blind at a kick is just firing blind).
            if (f.BehindDoor) return f.KnownDeputy ? Bad(KnownDeputyReason) : f.SeenArmedOnCamera ? Good(ArmedOnCamera)
                                   : f.ForcingDoor && HomePresumption ? GoodClose(KickingDoorIn) : Bad(FiredBlind);

            // 0b. His gun on you (M3a, Brett: "did they have a gun, were they pointing it"): firing, pointed at you (the
            //     prop's muzzle ray), or being raised at you — the raise counts too (research/08: SWAT 4 didn't count it and
            //     suspects shot first). Good whatever else he is doing: running, down, hands half up.
            if (weapon == WeaponKind.Gun && (f.Firing || f.PointingAtPlayer || f.RaisingGun))
                return Good(f.Firing ? ShootingAtYou : f.Down ? DownPointing
                          : f.Fleeing ? (f.PointingAtPlayer ? TurnedAndPointed : TurnedRaising)
                          : f.PointingAtPlayer ? PointingGun : RaisingGunAt);

            // 1. An active threat. Standing still with a knife within reach counts too, but not while he is running,
            //    giving up or down: Brett's summary makes those bad or close even with the weapon still in his hand.
            // Down (legs out) but the weapon still in his fist and still coming: clawing at you from the floor, or crawling
            //    your way from close enough to reach you. Down, empty-handed, or lying still: rules 2–3 (bad after the grace).
            if (f.Down && armed && (f.Attacking || f.Advancing && f.Distance <= StrikeDistance + DownReachExtra))
                return Good(Pick(s_downStillComing, weapon));
            if (f.Attacking) return Good(armed ? Pick(s_cameAtWith, weapon) : Attacking);
            // Warn first (research/27 §4) goes here: specified, not built until the Secret Service (armed, in view ≥ 2 s, no yell since → CLOSE CALL).
            if (armed && !f.Down)
            {
                if (f.Advancing) return Good(Pick(s_cameAtWith, weapon));
                if (f.Distance < StrikeDistance && !f.Fleeing && !surrendering) return Good(Pick(s_armedClose, weapon));
            }

            // 1b. He kicked the door in and is still a threat by the break-in alone (rule 8's plain goods): say so, not "a
            //     moment ago" — the recorder refreshes the grace clock every frame he's like this, so rule 2 would always win.
            //     Only in a home of yours (research/27 §4): without the presumption the break-in alone is no threat.
            bool brokeIn = HomePresumption && f.ForcedEntry && !f.Down && !surrendering && !f.Fleeing && !(f.IdentityStated && !armed);
            if (brokeIn)
            {
                if (f.Advancing) return Good(BrokeInAdvancing);
                if (armed) return Good(Pick(s_brokeInArmed, weapon));
                if (f.UnarmedStandingSeconds <= StandingGoodSeconds) return Good(JustKickedIn);
            }

            // 2. Reaction grace: the threat ended a split second ago.
            if (f.SecondsSinceThreat < ThreatGraceSeconds)
            {
                string why = f.Fleeing ? JustRan : f.Down ? JustDown : surrendering ? JustGaveUp : JustThreat;
                return (Tier.Good, f.SecondsSinceThreat > GraceCloseSeconds, why);
            }

            // 3. Already down (incapacitated): finishing shots are always bad.
            if (f.Down) return Bad(AlreadyDown);

            // 4. Surrendering. Hands up (or face-down) and empty is bad; still holding the weapon is a close call leaning good.
            if (surrendering)
            {
                bool prone = f.Posture == Posture.Prone;
                if (armed) return GoodClose(Pick(prone ? s_proneArmed : s_handsUpArmed, weapon));
                return Bad(prone ? ProneEmpty : HandsUpEmpty);
            }

            // 5. Running away, even with the weapon still in his hand (he'd have to turn on you again: rule 1).
            if (f.Fleeing) return Bad(armed ? Pick(s_fleeingArmed, weapon) : FleeingEmpty);

            // 6. He said who he is and his hands are empty (wrong house, family): the home presumption doesn't cover it. A
            //    deputy who said so at the door counts as having said it (research/27 §4), and the card names him.
            if ((f.IdentityStated || f.KnownDeputy) && !armed) return Bad(f.KnownDeputy ? KnownDeputyReason : Identified);

            // 7. No break-in. Unarmed and not threatening is bad; holding a weapon without coming at you is a close call. An
            //    unarmed man you let in who walks at you is still bad until he swings (research/25), said as it was.
            //    Without the home presumption (research/27 §4) a forced door lands here too, and unarmed it's bad, said so.
            if (!f.ForcedEntry || !HomePresumption)
            {
                if (armed) return GoodClose(Pick(s_walkedInArmed, weapon));
                if (f.ForcedEntry) return Bad(ForcedNotHome);
                if (f.Advancing && f.Inside) return Bad(WalkedInComing);
                return Bad(f.Inside ? WalkedIn : StillOutside);
            }

            // 8. He kicked the door in (PC 198.5 home presumption), weakened by Brett's unarmed-standing house rule (its plain
            //    goods are rule 1b; only the standing timer's close and bad calls are left, so the grace can still rescue them).
            float t = f.UnarmedStandingSeconds;
            if (t < StandingBadSeconds) return GoodClose(StandingReason(false, t));
            return Bad(StandingReason(true, t));
        }

        /// <summary>
        /// The scenario verdict. Hits on an already-dead body are ignored. Any bad hit makes it a BAD CALL (first bad
        /// reason, charge by outcome); otherwise any hit is a GOOD CALL (CLOSE CALL if any hit was close); no force at all
        /// is PERFECT. Dying always shows YOU DIDN'T SURVIVE: no confetti and no sirens (PLAN.md §2).
        /// </summary>
        public static Verdict Evaluate(ScenarioRecord r)
        {
            var v = new Verdict { WhoItWas = r?.Def?.WhoItWas };
            if (r == null) return Perfect(v, PerfectOther);

            int counted = 0;
            bool anyBad = false, anyClose = false, anyKilled = false, badDeadly = false, badKilled = false;
            string firstReason = null, closeReason = null, badReason = null;
            var force = r.Force;
            for (int i = 0; i < force.Count; i++)
            {
                var e = force[i];
                if (e.Before.Dead) continue;   // shots into a corpse don't change the call
                counted++;
                if (e.Killed) anyKilled = true;
                var (tier, close, reason) = JudgeForce(in e);
                firstReason ??= reason;
                if (tier == Tier.Bad)
                {
                    anyBad = true;
                    badReason ??= reason;
                    if (e.Kind != ForceKind.Punch) badDeadly = true;
                    if (e.Killed) badKilled = true;
                }
                else if (close)
                {
                    anyClose = true;
                    closeReason ??= reason;
                }
            }

            if (r.End == EndReason.PlayerDied)
            {
                v.Tier = Tier.YouDied;
                v.Headline = HeadlineDied;
                v.Reason = counted == 0 ? DiedNoForce : DiedFought;
                return v;
            }

            if (anyBad)
            {
                // He died with no single killing hit on record (bled out, Downed timeout): the bad hit takes the blame (research/07 §4).
                if (!anyKilled && badDeadly && (r.End == EndReason.Killed || r.LastFacts.Dead)) badKilled = true;
                v.Tier = Tier.Bad;
                v.Headline = HeadlineBad;
                v.Reason = badReason;
                v.Charge = badKilled ? ChargeKilled : badDeadly ? ChargeInjured : ChargeBattery;
                return v;
            }

            if (counted > 0)
            {
                v.Tier = Tier.Good;
                v.CloseCall = anyClose;
                v.Headline = HeadlineGood;
                v.Reason = anyClose ? closeReason : firstReason;
                return v;
            }

            return Perfect(v, r.End == EndReason.Custody ? PerfectCustody : r.End == EndReason.Left ? PerfectLeft : PerfectOther);
        }

        /// <summary>
        /// The bad call was a shooting: a round into a living man that was itself a bad call. The arrest card's firearm years
        /// (PC 12022.5(a), personal use of a firearm in the felony) go with it — not with a miss, a justified earlier shot or a
        /// round into the body after a knife or a fist killed him (0.1.57 review: those once added 4 years to a knife killing).
        /// </summary>
        public static bool BadShot(ScenarioRecord r)
        {
            if (r == null) return false;
            var force = r.Force;
            for (int i = 0; i < force.Count; i++)
            {
                var e = force[i];
                if (e.Kind == ForceKind.Shot && !e.Before.Dead && JudgeForce(in e).tier == Tier.Bad) return true;
            }
            return false;
        }

        static Verdict Perfect(Verdict v, string reason)
        {
            v.Tier = Tier.Perfect;
            v.Headline = HeadlinePerfect;
            v.Reason = reason;
            return v;
        }

        // ---- The Marshal night's cards (research/27 §4 "The cards"). Hand-built: the hitmen are never judged, so the card
        //      is the night's verdict. Deadpan, and nothing names a door. Built once per night, so plain strings are fine. ----

        /// <summary>The headlines of a Marshal night's end cards.</summary>
        public const string HeadlineDelivered = "WITNESS DELIVERED", HeadlineTrialOff = "THE TRIAL IS OFF";

        /// <summary>
        /// The visitors' "Who it was:" lines (research/27 §4) for their ScenarioDefs: Ortiz's check-in (no longer on the night
        /// since Brett 0.1.57: kept for the judge's self-test and any later use), the courier, Sal and the 2 AM relief. The cards
        /// hint how the address got out (Ricky ordered a pizza, gave his brother a key); nobody says it.
        /// </summary>
        public const string WhoOrtizCheckIn = "Deputy Ortiz, checking on the house.", WhoCourier = "A pizza courier. Ricky ordered it.",
                            WhoSal = "Sal, Ricky's brother. Ricky gave him a key.", WhoOrtizRelief = "Deputy Ortiz. Your relief.";

        const string ShotRicky = "You shot Ricky. He was the one you were guarding.";
        const string StabbedRicky = "You stabbed Ricky. He was the one you were guarding.";
        const string CutRicky = "You cut Ricky. He was the one you were guarding.";
        const string HitRicky = "You hit Ricky. He was the one you were guarding.";

        /// <summary>
        /// WITNESS DELIVERED, the full Perfect show (research/27 §4): "Relief arrived at 2:00 AM. Ricky testifies Monday." and
        /// "Hitmen stopped: 13 of 13. Visitors: 3, no bad calls." (Brett 0.1.57's five waves; the courier, Sal and the relief).
        /// A negative <paramref name="visitors"/> leaves the visitor count off (a night without visitors, as milestone A's bare night).
        /// </summary>
        public static Verdict WitnessDelivered(int hitmenStopped, int hitmenTotal, int visitors = -1) => new()
        {
            Tier = Tier.Perfect, Headline = HeadlineDelivered,
            Reason = "Relief arrived at 2:00 AM. Ricky testifies Monday.",
            Sentence = visitors < 0 ? HitmenTally(hitmenStopped, hitmenTotal)
                                    : HitmenTally(hitmenStopped, hitmenTotal) + " " + VisitorTally(visitors),
        };

        /// <summary>
        /// THE TRIAL IS OFF, the dim YouDied show: no confetti, no police (research/27 §4). The hitmen got Ricky ("They got to
        /// Ricky.") or the player went down ("You went down. Then they found Ricky."), with "Hitmen stopped: 3 of 13. Wave 2 of 5."
        /// The caller stands the squad down first (PoliceResponse.End does).
        /// </summary>
        public static Verdict TrialIsOff(bool witnessKilled, int hitmenStopped, int hitmenTotal, int wave, int waves) => new()
        {
            Tier = Tier.YouDied, Headline = HeadlineTrialOff,
            Reason = witnessKilled ? "They got to Ricky." : "You went down. Then they found Ricky.",
            Sentence = $"{HitmenTally(hitmenStopped, hitmenTotal)} Wave {Math.Max(1, wave)} of {waves}.",
        };

        /// <summary>"Hitmen stopped: 13 of 13." (killed or put out of the fight, of all the night's men).</summary>
        public static string HitmenTally(int stopped, int total) => $"Hitmen stopped: {stopped} of {total}.";

        /// <summary>
        /// "Visitors: 3, no bad calls." A bad call on a visitor ends the night (research/27 Q2), so a delivered night always
        /// reads "no bad calls"; the count is still honest if that ever changes.
        /// </summary>
        public static string VisitorTally(int visitors, int badCalls = 0) =>
            $"Visitors: {visitors}, {(badCalls <= 0 ? "no bad calls" : badCalls == 1 ? "1 bad call" : $"{badCalls} bad calls")}.";

        /// <summary>
        /// The player hurt Ricky, the protectee (research/27 §4): BAD whatever he was doing, he was the one you were guarding.
        /// THE TRIAL IS OFF if he died, BAD CALL if he lived. It's a booking with no standoff: the caller sets
        /// OutcomeShow.ForceBadMode = Mugshot before the show. The charges go by outcome as the judge's own do (killed:
        /// voluntary manslaughter; hurt: assault with a deadly weapon; a punch that didn't kill: battery; the gun enhancement
        /// only for a shot), plus the federal count (<see cref="Sentencing.ColorOfLaw(bool, bool)"/>), tallied with the estimate
        /// and the soap line (it's an arrest). WhoItWas stays null: the reason already says who.
        /// </summary>
        /// <param name="killed">Ricky died of it.</param>
        /// <param name="kind">What hurt him (the reason's verb, and whether the gun enhancement applies).</param>
        /// <param name="visitor">A bad call on a visitor in the same moment (the round Ricky's hit dropped): that charge is added
        /// after Ricky's, and his federal count beside Ricky's (one line ×2 when they're the same). Null: Ricky's alone.</param>
        /// <param name="visitorGun">A round of the player's hit the visitor (his charge's gun enhancement).</param>
        public static Verdict ProtecteeShot(bool killed, ForceKind kind = ForceKind.Shot, Verdict? visitor = null, bool visitorGun = false)
        {
            bool deadly = kind != ForceKind.Punch;
            string charge = killed ? ChargeKilled : deadly ? ChargeInjured : ChargeBattery;
            var crimes = new List<Crime>(4) { Sentencing.FromJudgeCharge(charge, gun: kind == ForceKind.Shot) };
            var federal = Sentencing.ColorOfLaw(killed, injured: !killed && deadly);
            if (visitor.HasValue && visitor.Value.Tier == Tier.Bad)
            {
                string vc = visitor.Value.Charge;
                var vCrime = Sentencing.FromJudgeCharge(vc, visitorGun);
                if (vCrime.Name == crimes[0].Name && vCrime.Years == crimes[0].Years && vCrime.JailMonths == crimes[0].JailMonths)
                { var c = crimes[0]; c.Count++; crimes[0] = c; }                 // the same charge twice: one line ×2
                else crimes.Add(vCrime);
                var vFederal = Sentencing.ColorOfLaw(vc);
                if (vFederal.Years == federal.Years && vFederal.JailMonths == federal.JailMonths) federal.Count++;
                else crimes.Add(vFederal);
            }
            crimes.Insert(crimes.Count > 1 && crimes[^1].Federal ? crimes.Count - 1 : crimes.Count, federal);
            return new Verdict
            {
                Tier = Tier.Bad, Headline = killed ? HeadlineTrialOff : HeadlineBad,
                Charge = charge,                                             // the booking placard and the read-out
                Reason = kind switch { ForceKind.Stab => StabbedRicky, ForceKind.Slash => CutRicky, ForceKind.Punch => HitRicky, _ => ShotRicky },
                Sentence = Sentencing.Tally(crimes, arrested: true, wavesBeaten: 0),
            };
        }

        /// <summary>
        /// A Marshal's bad call on a visitor (Ortiz, the courier, Sal, the relief; Brett 2026-10-04 (a): every bad call on a
        /// Marshal night is the arrest, never a police standoff): the booking card built from the judge's own
        /// <paramref name="call"/>. BAD CALL; the judge's reason and the OPR referral; the judge's charge (the gun enhancement
        /// only when <paramref name="gun"/>: a knife is no firearm), then the federal count, tallied with the estimate and the
        /// soap line (it's an arrest). Charge and WhoItWas are kept: the booking placard and "Who it was:" read the judge's.
        /// </summary>
        /// <param name="call">The round's verdict (Tier.Bad, with its Charge and Reason).</param>
        /// <param name="gun">A round of the player's hit him (any shot on record).</param>
        public static Verdict VisitorBooked(in Verdict call, bool gun)
        {
            var crimes = new List<Crime>(2) { Sentencing.FromJudgeCharge(call.Charge, gun), Sentencing.ColorOfLaw(call.Charge) };
            var v = call;
            v.Tier = Tier.Bad; v.Headline = HeadlineBad; v.CloseCall = false;
            v.Reason = string.IsNullOrEmpty(call.Reason) ? Sentencing.OprReferral : call.Reason + "\n" + Sentencing.OprReferral;
            v.Sentence = Sentencing.Tally(crimes, arrested: true, wavesBeaten: 0);
            return v;
        }
    }
}
