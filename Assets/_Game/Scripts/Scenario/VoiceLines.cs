using System.Collections.Generic;

namespace HIS.Scenario
{
    /// <summary>
    /// Every spoken line in the game: Sfx clip key → subtitle text. The single source of truth, so the words on screen
    /// always match the words heard: <see cref="HIS.UI.Subtitles"/> shows this text, and scripts/make_tts.py renders the
    /// placeholder clips from it (macOS <c>say</c>) into Assets/_Game/Audio/Clips/&lt;key&gt;/&lt;key&gt;_01.wav. Brett's own
    /// recordings later drop into the same key folders (more files = random variants), so no code changes when they land.
    ///
    /// Table format — make_tts.py parses it with a regex, so keep it exactly like this:
    ///   one entry per line, <c>("vo.&lt;speaker&gt;.&lt;line&gt;", "Subtitle text"),</c> between the BEGIN/END TABLE markers;
    ///   plain double-quoted strings, no escaped quotes, no string concatenation, no interpolation.
    ///   The key's speaker part (player, police, burglar, …) picks the TTS voice and, for police, the bullhorn effect.
    ///   Keep lines short: ≤ 2.5 s spoken (about 8 words).
    /// </summary>
    public static class VoiceLines
    {
        // BEGIN TABLE (parsed by scripts/make_tts.py)
        static readonly (string key, string text)[] Table =
        {
            // --- the player's yell commands (A/X; PLAN.md §6). Played 2D, in your own head. ---
            ("vo.player.who", "Who the hell is in my house?!"),   // (Brett 2026-09-25: "whos there im armed isnt the best line")
            ("vo.player.hands", "Show me your hands!"),
            ("vo.player.drop", "Drop the weapon!"),
            ("vo.player.ground", "Get on the ground!"),
            ("vo.player.out", "Get out of my house!"),

            // --- the intruder (spatial, from his head) ---
            ("vo.attacker.taunt", "Get over here!"),
            ("vo.knife.taunt", "You're dead!"),
            ("vo.burglar.flee", "Whoa— I'm out, I'm out!"),
            ("vo.burglar.shot", "Agh! What the hell?!"),               // hit through the closed door: he runs off, never comes in
            // Giving up: the words depend on what's in the player's hands (VoiceLines.Surrender picks one).
            ("vo.surrender.ok", "Okay! Okay! Don't shoot!"),           // a gun
            ("vo.surrender.chill", "Whoa, whoa, chill out, dude!"),    // a knife
            ("vo.surrender.stab", "Are you gonna stab me?!"),
            ("vo.surrender.nostab", "Okay! Don't stab me, man!"),
            ("vo.surrender.easy", "Okay, okay! Easy!"),                // empty hands
            ("vo.surrender.giveup", "Alright, alright! I give up!"),
            ("vo.surrender.down", "I'm down, I'm down!"),
            // The same give-up lines in each fighter's OWN voice (Brett 0.1.16: "the attacker's voice should never be a
            // woman if he's a man" — the vo.surrender.* takes are the young burglar's voice). VoiceLines.Surrender routes.
            ("vo.attacker.surrender.ok", "Okay! Okay! Don't shoot!"),
            ("vo.attacker.surrender.nostab", "Okay! Don't stab me, man!"),
            ("vo.attacker.surrender.easy", "Okay, okay! Easy!"),
            ("vo.attacker.surrender.down", "I'm down, I'm down!"),
            ("vo.knife.surrender.ok", "Okay! Okay! Don't shoot!"),
            ("vo.knife.surrender.nostab", "Okay! Don't stab me, man!"),
            ("vo.knife.surrender.easy", "Okay, okay! Easy!"),
            ("vo.knife.surrender.down", "I'm down, I'm down!"),
            ("vo.gunman.surrender.ok", "Okay! Okay! Don't shoot!"),
            ("vo.gunman.surrender.nostab", "Okay! Don't stab me, man!"),
            ("vo.gunman.surrender.easy", "Okay, okay! Easy!"),
            ("vo.gunman.surrender.down", "I'm down, I'm down!"),
            // Pick #1's false alarm: the man at the door is only delivering something (nobody comes in).
            ("vo.delivery.hello", "Delivery! I'll just leave it here."),
            // Pick #2: answers through the closed door when the player yells (IntruderDoorTalk).
            ("vo.attacker.door", "Open the door! I know you're in there!"),
            ("vo.attacker.makeme", "Yeah? Make me!"),
            ("vo.knife.door", "Open the door. Now."),
            ("vo.gunman.door", "I know you're in there!"),
            ("vo.gunman.door.lie", "Pizza! Uh... delivery!"),
            ("vo.surrender.door", "Sorry! Sorry, wrong house!"),
            ("vo.neighbor.door", "Dave? It's Mike, lemme in, man."),
            ("vo.neighbor.door.leave", "Alright, alright... jeez."),
            // Pick #3: the burglar caught in the act freezes, then chooses (IntruderBurglarChoice).
            ("vo.burglar.whoa", "Whoa, whoa, whoa!"),
            ("vo.burglar.leaving", "I'm leaving, I'm leaving!"),
            ("vo.burglar.cornered", "Get out of my way!"),

            // --- the gunman (M3a): the shout that goes with raising the gun (the tell), his entry, giving in, running ---
            ("vo.gunman.money", "Where's the money? Where is it?!"),
            ("vo.gunman.dontmove", "Don't move! Don't you move!"),     // the tell, you're not holding a gun
            ("vo.gunman.dropit", "Drop it! Drop the gun!"),            // the tell, you are
            ("vo.gunman.backoff", "Back off! Back the hell off!"),     // the runner turning to point it
            ("vo.gunman.putdown", "Okay! Okay! I'm putting it down!"), // complying: the toss
            ("vo.gunman.flee", "Shit! I'm gone, I'm gone!"),

            // --- the wrong-house neighbour (Mike, drunk, unlocked door) ---
            ("vo.neighbor.hello", "Dave? Dave, you up? It's Mike…"),
            // "It's me!" by what's in the player's hands (VoiceLines.NeighborItsMe); his thumb at next door lands ~1 s in.
            ("vo.neighbor.itsme", "Whoa, whoa! Don't shoot! It's me, Mike, from next door!"),  // a gun
            ("vo.neighbor.chill", "Chill, dude! It's Mike, from next door!"),     // a knife
            ("vo.neighbor.nostab", "Don't stab me! It's Mike, from next door!"),
            ("vo.neighbor.easy", "Whoa, whoa! It's Mike, from next door!"),             // empty hands
            ("vo.neighbor.shot", "Ow! Dave, what the hell?!"),         // hit through the closed door: he runs home
            ("vo.neighbor.leave", "Okay, okay… wrong house. Sorry, man."),

            // --- police: bullhorn from outside your door on a bad call, escalating and deadpan (PLAN.md M4) ---
            ("vo.police.handsup", "This is the police. Come out with your hands up!"),
            ("vo.police.window", "Sir. We can see you through the window."),
            // The helicopter bit says one of these or the window line (VoiceLines.PoliceHeli: random, never twice running).
            ("vo.police.heli.spotlight", "You're in the spotlight, sir. Literally."),
            ("vo.police.heli.pilot", "Sir, the pilot says hi."),
            ("vo.police.heli.wave", "That's our helicopter. Please don't wave."),
            ("vo.police.heli.thermal", "Sir, the helicopter has thermal. Nice try."),
            ("vo.police.heli.judging", "That's our helicopter. It's also judging you."),
            ("vo.police.heli.bill", "Sir, the helicopter bills by the minute."),
            ("vo.police.surround", "We have the house surrounded. It's a small house."),
            ("vo.police.neighbors", "Sir, your neighbors are filming this."),
            ("vo.police.camera", "Look at the camera. Do not smile."),                              // mugshot
            ("vo.police.turn", "Now turn to your left."),
            // The rare treat (Brett 0.1.17: "funny the first time annoying on repeat"): BadCallShow.PickTurnLine.
            ("vo.police.turn.other", "Now turn to your left. Your other left."),
            // The charge from the verdict (Judge.Charge*), last word of the bullhorn escalation.
            ("vo.police.charge.manslaughter", "You're under arrest for voluntary manslaughter."),
            ("vo.police.charge.adw", "You're under arrest for assault with a deadly weapon."),
            ("vo.police.charge.battery", "Under arrest for battery. Not the double-A kind."),
            // --- police radio on the custody ending (no bullhorn, no sirens) ---
            ("vo.police.custody", "Suspect in custody."),
            // The police standoff (research/22, Brett 0.1.25): Ready-or-Not-style barks, our own words; never kneel/lie down.
            ("vo.cop.bullhorn.weapons", "Police department! Put down any weapons!"),
            ("vo.cop.bullhorn.coming", "Officers coming in! Hands where we can see them!"),
            ("vo.cop.radio.responding", "Units responding. Shots fired at the residence."),
            ("vo.cop.radio.officerdown", "Officer needs help! All units, code three!"),
            ("vo.cop.radio.suspectdown", "Suspect down. Send medical."),
            ("vo.cop.radio.backup", "Backup rolling. More units en route."),
            ("vo.cop.radio.swat", "SWAT is on scene."),
            ("vo.cop.lead.announce", "Police! Anyone inside, sound off!"),
            ("vo.cop.lead.breach", "Breaching!"),
            ("vo.cop.lead.entry", "Police! Let me see your hands!"),
            ("vo.cop.lead.drop.1", "Drop the weapon!"),
            ("vo.cop.lead.drop.2", "Drop it! Drop it now!"),
            ("vo.cop.lead.drop.3", "Last warning! Put it down!"),
            ("vo.cop.lead.hands.1", "Hands! Show me your hands!"),
            ("vo.cop.lead.hands.2", "Get your hands up! Higher!"),
            ("vo.cop.lead.hands.3", "Hands up, now!"),
            ("vo.cop.lead.dontdoit", "Don't do it!"),
            ("vo.cop.lead.turn", "Turn around. Face away from me."),
            ("vo.cop.lead.coming", "I'm coming up behind you. Don't move."),
            ("vo.cop.lead.handsaway", "Hands away from your waist!"),
            ("vo.cop.lead.good", "Good. Keep them there."),
            ("vo.cop.lead.arrest", "You're under arrest. Don't resist."),
            ("vo.cop.lead.rights", "You have the right to remain silent. Please use it."),
            ("vo.cop.lead.released", "Your story checks out. Cuffs are coming off."),
            ("vo.cop.lead.hit", "I'm hit! I'm hit!"),
            ("vo.cop.cover.gun", "Gun! He's got a gun!"),
            ("vo.cop.cover.gunup", "Gun coming up!"),
            ("vo.cop.cover.taser", "Taser! Taser!"),
            ("vo.cop.cover.contact", "Shots fired! Shots fired!"),
            ("vo.cop.cover.hit", "I'm hit! Get me out of here!"),
            ("vo.cop.cover.officerdown", "Officer down! Officer down!"),
            ("vo.cop.cover.suspectdown", "Suspect down. Get medical in here."),
            ("vo.cop.swat.entry", "SWAT! Show me your hands!"),
            ("vo.cop.swat.hands", "Hands! Hands! Hands!"),
            ("vo.cop.swat.contact", "Contact front!"),
            ("vo.cop.book.soap", "Welcome to county. Don't drop the soap."),

            // --- ENDING + ARRIVAL (Brett 0.1.56, 2026-10-04). The dispatcher by what happened (wave 1's radio after a bad
            // call in the plain game: never "Shots fired" when nothing was fired), and Ricky calling each later hitman car. ---
            ("vo.cop.radio.stabbing", "Units responding. Stabbing at the residence."),
            ("vo.cop.radio.assault", "Units responding. Assault at the residence."),
            ("vo.ricky.car2", "Another car."),
            ("vo.ricky.car3", "They're back."),

            // --- a US Marshal night (research/27 §5). Deadpan; no line names a door. ---
            // Your yells in Marshal mode (VoiceLines.KeyFor by Judge.Role). The TTS reads "You Ess Marshals" (make_tts).
            ("vo.player.marshal.who", "U.S. Marshals! Who's out there?"),
            ("vo.player.marshal.hands", "U.S. Marshals! Show me your hands!"),
            ("vo.player.marshal.drop", "U.S. Marshals! Drop the weapon!"),
            ("vo.player.marshal.ground", "U.S. Marshals! Get on the ground!"),
            ("vo.player.marshal.out", "U.S. Marshals! Back away, now!"),
            // Ricky, the witness: only after a bell, a knock or the first car.
            ("vo.ricky.knock", "Who's that? Nobody's supposed to know."),
            ("vo.ricky.car", "That's not the relief."),
            ("vo.ricky.down", "Okay, okay. I'm down."),
            ("vo.ricky.hit", "Agh! I'm hit!"),
            ("vo.ricky.beg", "No, no, please!"),
            // --- THE HANDOVER ON THE RADIO (Scenario/MarshalBriefing, Brett 2026-10-04; vo.deputy.radio.*: his deputy voice
            // through the police radio's band, a squelch first, RadioVoice). Since 0.1.58 he's in the room and says the handover
            // in person (vo.deputy.handover.* below): only step 10 (.camera, .menu) and the sign-off (.done) still go out on the
            // radio; the rest are the fallback when no spot in the room fits him. Ricky answers once, and complains at a
            // practice hit (no harm during the check). ---
            ("vo.deputy.radio.outfront", "Marshal, it's Ortiz. I'm out front."),
            ("vo.deputy.radio.ricky", "That's Ricky. He testifies Monday."),
            ("vo.deputy.radio.breathing", "Keep him breathing till two."),
            ("vo.deputy.radio.check", "Weapons check before I go."),
            ("vo.deputy.radio.stand", "Stand up straight for me."),
            ("vo.deputy.radio.draw", "Sidearm, right hip. Draw it."),
            ("vo.deputy.radio.three", "Put three in the target."),
            ("vo.deputy.radio.dry", "You're dry. Reload."),
            ("vo.deputy.radio.holster", "Holster it."),
            ("vo.deputy.radio.longgun", "Long gun's on your shoulder."),
            ("vo.deputy.radio.oneround", "One round."),
            ("vo.deputy.radio.pump", "Pump it."),
            ("vo.deputy.radio.reloadit", "Reload it."),
            ("vo.deputy.radio.knife", "Knife's on your left hip. Use it."),
            ("vo.deputy.radio.yell", "Let me hear you call it."),
            ("vo.deputy.radio.door", "Check the door. Open, then shut."),
            ("vo.deputy.radio.camera", "That screen's your porch camera."),
            ("vo.deputy.radio.menu", "Menu pauses. Hold it to start over."),
            // (The sign-off, on the radio from the porch once he has left: the 1:20 check-in visit is gone, Brett 0.1.57.)
            ("vo.deputy.radio.done", "Good. See you at two."),
            ("vo.ricky.newguy", "So you're the new guy."),
            ("vo.ricky.watchit", "Hey! Watch it!"),
            // --- THE HANDOVER IN PERSON (Brett 0.1.57: "I thought ortiz was just apart of the story intro and tutorial"): Ortiz
            // stands in the room by the front door for the handover and the weapons check, says each line to your face (his
            // deputy voice, no radio), and leaves when you get the door for him; step 10 and the sign-off stay on the radio
            // (vo.deputy.radio.camera / .menu / .done). The short handover (no check due) ends on vo.deputy.leave. ---
            ("vo.deputy.handover.hello", "Marshal. I'm Ortiz."),
            ("vo.deputy.handover.ricky", "That's Ricky. He testifies Monday."),
            ("vo.deputy.handover.breathing", "Keep him breathing till two."),
            ("vo.deputy.handover.check", "Weapons check before I go."),
            ("vo.deputy.handover.stand", "Stand up straight for me."),
            ("vo.deputy.handover.draw", "Sidearm, right hip. Draw it."),
            ("vo.deputy.handover.three", "Put three in the target."),
            ("vo.deputy.handover.dry", "You're dry. Reload."),
            ("vo.deputy.handover.holster", "Holster it."),
            ("vo.deputy.handover.longgun", "Long gun's on your shoulder."),
            ("vo.deputy.handover.oneround", "One round."),
            ("vo.deputy.handover.pump", "Pump it."),
            ("vo.deputy.handover.reloadit", "Reload it."),
            ("vo.deputy.handover.knife", "Knife's on your left hip. Use it."),
            ("vo.deputy.handover.yell", "Let me hear you call it."),
            ("vo.deputy.handover.door", "Get the door for me."),
            ("vo.deputy.handover.lock", "Lock it behind me."),
            ("vo.deputy.handover.skip", "Alright. Lock it behind me."),
            ("vo.deputy.handover.watchit", "Whoa! Watch it!"),
            // --- FIVE WAVES (Brett 0.1.57, "it should be mostly the mob coming after him"): Ricky calls waves 4 and 5's cars. ---
            ("vo.ricky.car4", "More of them."),
            ("vo.ricky.car5", "That's a van. Oh no."),
            // Deputy Ortiz checking in (Mike's slots through ScenarioDef.VoicePrefix "deputy").
            ("vo.deputy.door", "Marshals. Ortiz. I'm coming in."),
            ("vo.deputy.hello", "Ortiz. Just checking on you two."),
            ("vo.deputy.itsme", "Whoa! Ortiz! U.S. Marshals!"),
            ("vo.deputy.easy", "Easy. It's Ortiz."),
            ("vo.deputy.shot", "Agh! I'm a Marshal!"),
            ("vo.deputy.leave", "Two o'clock. Lock it behind me."),
            // --- VISITORS (Brett 0.1.56, decision (b): Ortiz is known before the door opens). His call before each walk-up, on
            // the radio (vo.deputy.radio.*: RadioVoice, the handover's treatment), and his name at the door — through it when it's
            // shut (KnockAndAnnounce's "knock" slot), to your face when you opened it on him. The hitmen never radio or knock. ---
            ("vo.deputy.radio.walkup", "Marshal, Ortiz. Coming up the walk."),
            ("vo.deputy.radio.relief", "Marshal, Ortiz. It's two. Coming up the walk."),
            ("vo.deputy.knock", "It's Ortiz."),
            ("vo.relief.knock", "It's Ortiz."),          // (the relief's name at the door, as the check-in's: "Relief's here" is said once, inside)
            // Ortiz again at 2 AM: the relief.
            ("vo.relief.door", "Marshals. Ortiz. Relief's here."),
            ("vo.relief.hello", "Relief's here. Let's get him moving."),
            ("vo.relief.itsme", "Whoa! Ortiz! U.S. Marshals!"),
            ("vo.relief.easy", "Easy. It's Ortiz."),
            ("vo.relief.shot", "Agh! I'm a Marshal!"),
            // The pizza courier (the delivery voice).
            ("vo.courier.hello", "Pizza. Large pepperoni. It's paid for."),
            // Sal, Ricky's brother.
            ("vo.brother.door", "Ricky? It's Sal. Open up."),
            ("vo.brother.door.leave", "Alright. I'm going."),
            ("vo.brother.hello", "Ricky? It's Sal. You up?"),
            ("vo.brother.itsme", "Whoa! Don't shoot! I'm his brother!"),
            ("vo.brother.easy", "Easy! Sal. Ricky's brother."),
            ("vo.brother.shot", "Ow! What the hell, man?!"),
            ("vo.brother.leave", "Fine. Tell him Sal came by."),
            // The hitmen: wave 1 street muscle, waves 2–3 the pros. Hit or dying: the intruder grunts only.
            ("vo.hitman.street.spot", "There he is!"),
            ("vo.hitman.street.where", "Where's Ricky? Where is he?"),
            ("vo.hitman.pro.contact", "Contact."),
            ("vo.hitman.pro.go", "Go."),
            ("vo.hitman.pro.reload", "Reloading."),
            ("vo.hitman.pro.where", "Find the witness."),
        };
        // END TABLE


        /// <summary>"You're under arrest for …" for a verdict's charge (null = no line for it).</summary>
        public static string PoliceCharge(string charge) => charge switch
        {
            Judge.ChargeKilled => "vo.police.charge.manslaughter",
            Judge.ChargeInjured => "vo.police.charge.adw",
            Judge.ChargeBattery => "vo.police.charge.battery",
            _ => null,
        };

        // Lines that depend on what the player holds (Brett 2026-09-23: "don't shoot" only at a gun; a knife gets "chill out
        // dude" / "are you gonna stab me"). Each set = keys to pick from at random; more takes per key add variety too.
        static readonly string[] SurrenderGun = { "vo.surrender.ok" };
        static readonly string[] SurrenderKnife = { "vo.surrender.chill", "vo.surrender.stab", "vo.surrender.nostab" };
        static readonly string[] SurrenderEmpty = { "vo.surrender.easy", "vo.surrender.giveup" };
        static readonly string[] ItsMeGun = { "vo.neighbor.itsme" };
        static readonly string[] ItsMeKnife = { "vo.neighbor.chill", "vo.neighbor.nostab" };
        static readonly string[] ItsMeEmpty = { "vo.neighbor.easy" };

        /// <summary>His first words giving up, by what the player is holding.</summary>
        public static string Surrender(PlayerWeapon held) =>
            Pick(held == PlayerWeapon.Gun ? SurrenderGun : held == PlayerWeapon.Knife ? SurrenderKnife : SurrenderEmpty);

        /// <summary>
        /// Giving up in HIS OWN voice: the attacker, knife man and gunman have their own takes of the lines; the burglar and the
        /// surrenderer use the vo.surrender.* set (their voice). Never another character's voice — a fighter whose own take is
        /// missing says nothing, and Mike (who has his own "it's me" lines) says nothing here. Null = stay quiet.
        /// </summary>
        public static string Surrender(Intent who, PlayerWeapon held)
        {
            string own = OwnPrefix(who);
            if (own == null) return who == Intent.Innocent ? null : Surrender(held);
            return Own(own + (held == PlayerWeapon.Gun ? "ok" : held == PlayerWeapon.Knife ? "nostab" : "easy"));
        }

        /// <summary>"I'm down, I'm down!" in his own voice (null = quiet, see <see cref="Surrender(Intent, PlayerWeapon)"/>).</summary>
        public static string SurrenderDown(Intent who)
        {
            string own = OwnPrefix(who);
            if (own == null) return who == Intent.Innocent ? null : "vo.surrender.down";
            return Own(own + "down");
        }

        static string OwnPrefix(Intent who) => who switch
        {
            Intent.MeleeAttacker => "vo.attacker.surrender.",
            Intent.KnifeAttacker => "vo.knife.surrender.",
            Intent.Gunman => "vo.gunman.surrender.",
            _ => null,
        };

        static string Own(string key) => HIS.Core.Sfx.Has(key) ? key : null;

        /// <summary>The neighbour's "It's me, Mike, from next door!", by what the player is holding.</summary>
        public static string NeighborItsMe(PlayerWeapon held) =>
            Pick(held == PlayerWeapon.Gun ? ItsMeGun : held == PlayerWeapon.Knife ? ItsMeKnife : ItsMeEmpty);

        /// <summary>
        /// A visitor's line in his own voice: <c>vo.&lt;prefix&gt;.&lt;slot&gt;</c> when the table has it (a Marshal night's
        /// Ortiz, Sal and the courier use Mike's slots: research/27 §5), else null: he stays quiet, never another man's voice.
        /// A null prefix is Mike ("neighbor"), so the plain game's keys are unchanged.
        /// </summary>
        public static string Visitor(string prefix, string slot)
        {
            string key = "vo." + (string.IsNullOrEmpty(prefix) ? "neighbor" : prefix) + "." + slot;
            return Map.ContainsKey(key) ? key : null;
        }

        /// <summary>
        /// "It's me!" in a visitor's voice, by what the player holds: a gun → itsme, empty hands → easy, a knife → his chill /
        /// nostab takes, which fall back to itsme / easy when he has none (research/27 §5). Mike (no prefix): as
        /// <see cref="NeighborItsMe"/>.
        /// </summary>
        public static string ItsMe(string prefix, PlayerWeapon held)
        {
            if (string.IsNullOrEmpty(prefix) || prefix == "neighbor") return NeighborItsMe(held);
            if (held == PlayerWeapon.Gun) return Visitor(prefix, "itsme");
            if (held != PlayerWeapon.Knife) return Visitor(prefix, "easy");
            bool chill = UnityEngine.Random.value < 0.5f;
            return Visitor(prefix, chill ? "chill" : "nostab") ?? Visitor(prefix, chill ? "itsme" : "easy");
        }

        static string _lastPick;

        /// <summary>A random key from the set, never the same line twice running.</summary>
        static string Pick(string[] keys)
        {
            int i = keys.Length == 1 ? 0 : UnityEngine.Random.Range(0, keys.Length);
            if (keys.Length > 1 && keys[i] == _lastPick) i = (i + 1) % keys.Length;
            return _lastPick = keys[i];
        }

        // The helicopter bit (Brett 0.1.16: "add a few other lines so its not just we can see you through the window each
        // time"). The window line stays in the pool and is also the fallback while the new lines have no audio yet.
        public const string PoliceHeliFallback = "vo.police.window";
        static readonly string[] HeliPool =
        {
            PoliceHeliFallback, "vo.police.heli.spotlight", "vo.police.heli.pilot", "vo.police.heli.wave",
            "vo.police.heli.thermal", "vo.police.heli.judging", "vo.police.heli.bill",
        };
        static readonly string[] _heliScratch = new string[HeliPool.Length];
        static string _lastHeli;

        /// <summary>Every line the helicopter bit can say (tests).</summary>
        public static IReadOnlyList<string> PoliceHeliPool => HeliPool;

        /// <summary>
        /// The helicopter bit's line: a random one of <see cref="PoliceHeliPool"/> that <paramref name="hasAudio"/> says has
        /// clips (null = all do), never the one picked last time while another is available; none with audio → the window line.
        /// </summary>
        public static string PoliceHeli(System.Func<string, bool> hasAudio)
        {
            int n = 0;
            foreach (var k in HeliPool)
                if (hasAudio == null || hasAudio(k)) _heliScratch[n++] = k;
            if (n == 0) return _lastHeli = PoliceHeliFallback;
            int i = UnityEngine.Random.Range(0, n);
            if (n > 1 && _heliScratch[i] == _lastHeli) i = (i + 1 + UnityEngine.Random.Range(0, n - 1)) % n;
            return _lastHeli = _heliScratch[i];
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetPicks() { _lastPick = null; _lastHeli = null; }

        static readonly Dictionary<string, string> Map = BuildMap();

        static Dictionary<string, string> BuildMap()
        {
            var m = new Dictionary<string, string>(Table.Length, System.StringComparer.Ordinal);
            foreach (var (key, text) in Table) m[key] = text;
            return m;
        }

        /// <summary>Subtitle text for a clip key, or null if the key has no line (callers skip the subtitle).</summary>
        public static string Text(string key) => key != null && Map.TryGetValue(key, out var t) ? t : null;

        /// <summary>Every (key, text) pair, in table order.</summary>
        public static IEnumerable<(string key, string text)> All => Table;

        /// <summary>
        /// The player's clip key for a yell command (null for <see cref="Command.None"/>). A Marshal (Judge.Role) yells the
        /// "U.S. Marshals!" lines (research/27 §5). Their fallback: a missing take of hands / drop / ground plays the plain line
        /// (the same man, the same words); a missing who / out stays silent with the Marshal's caption, because the plain ones
        /// say "my house". Every other role: the plain keys, as before.
        /// </summary>
        public static string KeyFor(Command c)
        {
            string plain = c switch
            {
                Command.WhoIsThere => "vo.player.who",
                Command.ShowHands => "vo.player.hands",
                Command.DropWeapon => "vo.player.drop",
                Command.GetDown => "vo.player.ground",
                Command.GetOut => "vo.player.out",
                _ => null,
            };
            if (plain == null || Judge.Role != Role.Marshal) return plain;
            string marshal = c switch
            {
                Command.WhoIsThere => "vo.player.marshal.who",
                Command.ShowHands => "vo.player.marshal.hands",
                Command.DropWeapon => "vo.player.marshal.drop",
                Command.GetDown => "vo.player.marshal.ground",
                _ => "vo.player.marshal.out",
            };
            if (c is Command.WhoIsThere or Command.GetOut) return marshal;
            return HIS.Core.Sfx.Has(marshal) ? marshal : plain;
        }
    }
}
