using UnityEngine;

namespace HIS.Scenario
{
    /// <summary>
    /// One hitman wave's skill (research/27 §2, the knob table). Brett 2026-10-01: "characters after the first wave are more
    /// difficult more accurate faster draw etc and third wave is more than second shouldnt be like ridiculous but should go
    /// until its like if a real life navy seal pulled up itd be hard". Brett 0.1.57 ("it should be mostly the mob coming after
    /// him"): FIVE waves, each harder than the last, with short breathers — street muscle (2 men), a street crew (3), the
    /// professionals (2), a heavier pro team (3), the operators (3): 13 men a night. Faster react, a quicker raise and better
    /// accuracy every wave; the operators of wave 5 are the old wave 3, the top of the scale.
    /// Two numbers per tier: <see cref="Wave"/> is the wave (its vehicle, the clock, the finder's one-a-wave, "Wave n of 5")
    /// and <see cref="Look"/> is the kind of men it sends (street / pro / operator): their clothes, their talk, the report of
    /// their guns. Anything that asks "what kind of hitman is this" reads Look, never the wave number.
    /// "Not ridiculous" (§2): never above 0.9 accuracy, never under a <see cref="MinWindow"/> tell, a fresh raise after every
    /// lost line, no fire through walls, no extra hit points beyond one plate (the operators' only).
    /// Ranges are (min, max) for Random.Range.
    /// </summary>
    public readonly struct HitmanTier
    {
        /// <summary>No round before this many seconds after he first has a line to you (the tell), whatever the tier.</summary>
        public const float MinWindow = 0.7f;
        /// <summary>Extra window when he first sees you from more than <see cref="BehindAngle"/>° off where you face.</summary>
        public const float BehindExtra = 0.5f, BehindAngle = 70f;
        /// <summary>No line for this long: the gun drops to low ready and the window starts again.</summary>
        public const float BlindReset = 1.0f;
        /// <summary>The longest a stacked team waits for the other door's men before going in anyway (§6 step 2).</summary>
        public const float EntryHoldCap = 12f;
        /// <summary>The <see cref="Look"/> classes: street (a beanie, "There he is!"), pro (suits, "Contact."), operator (helmet, plate, SMG).</summary>
        public const int LookStreet = 1, LookPro = 2, LookOperator = 3;

        /// <summary>The wave number, 1–<see cref="Waves"/> (its vehicle, the story clock, the card's "Wave n of 5").</summary>
        public readonly int Wave;
        /// <summary>
        /// The kind of men (<see cref="LookStreet"/> waves 1–2, <see cref="LookPro"/> waves 3–4, <see cref="LookOperator"/>
        /// wave 5): what they wear (IntruderPoliceLook.DressAsHitman), what they say ("Go.", "Find the witness."), the finder's
        /// pick and the operators' deeper report (IntruderHitman). Brett 0.1.57: wave 2 dresses as street, wave 4 as pros.
        /// </summary>
        public readonly int Look;
        /// <summary>"street muscle", "street crew", "professionals", "heavy professionals", "operators" (the log's name).</summary>
        public readonly string Name;
        /// <summary>Men at the front and the side door (two doors), and at the front alone (one door).</summary>
        public readonly int FrontTwoDoors, SideTwoDoors, FrontOneDoor;
        /// <summary>Gun low after he first sees you, then the visible raise (seconds).</summary>
        public readonly Vector2 React;
        public readonly float Raise;
        /// <summary>× the gunman's hit curve inside the room; aim lag τ (s) behind a moving player.</summary>
        public readonly float Accuracy, AimLag;
        /// <summary>From the path through an open doorway: on sight (else only once the player fired this wave), and the accuracy.</summary>
        public readonly bool FireFromPath;
        public readonly float PathAccuracy;
        /// <summary>Burst: rounds (min, max inclusive), seconds between rounds, pause after a burst.</summary>
        public readonly Vector2Int BurstRounds;
        public readonly Vector2 BurstGap, BurstPause;
        /// <summary>Magazine, reload seconds, says "Reloading." (the pros and operators), steps out of your line to reload (Question 6).</summary>
        public readonly int Magazine;
        public readonly float Reload;
        public readonly bool CallsReload, CoverOnReload;
        /// <summary>Up the path: jog and gun-walk speeds (m/s), lane offset from the path's middle (m), set-off stagger per slot (s).</summary>
        public readonly float Jog, GunWalk, Lane, Stagger;
        /// <summary>Bounding under fire: the cycle (s), and whether partners move in step (the street: both stop, both go).</summary>
        public readonly float BoundSeconds;
        public readonly bool BoundInStep;
        /// <summary>Stacked: the hold before going in once every live man of the wave is at a door (s).</summary>
        public readonly float EntryHold;
        /// <summary>Inside: how long the 2nd and 3rd man wait after the first goes through (s).</summary>
        public readonly float SecondWait, ThirdWait;
        /// <summary>Blind (no line on you): a new angle after this long (s).</summary>
        public readonly float BlindReangle;
        /// <summary>Rounds fired back through a shut door you just shot through while he was stacked at it (0: none; wave 1).</summary>
        public readonly int BlindVolley;
        /// <summary>Hit inside the room: fights on (the pros and operators; legs out: from the floor) instead of backing out.</summary>
        public readonly bool FightsOnHit;
        /// <summary>A plate: the first chest round is a stagger, a clank and sparks, no blood (wave 5, the operators, only).</summary>
        public readonly bool Plated;
        /// <summary>The first-contact line (the audible half of the tell).</summary>
        public readonly string CalloutKey;
        /// <summary>
        /// The vehicle the wave arrives in: its name in PoliceCars (the GameObject's and the log's: "Sedan", "RedSedan", "SUV",
        /// "CharcoalSUV", "Van"), one per wave (<see cref="HIS.Security.PoliceCars.VehicleFor(int, SquadFaction)"/>).
        /// </summary>
        public readonly string Vehicle;
        /// <summary>
        /// Ricky's line when this wave's car pulls up (IntruderProtectee): "That's not the relief." (wave 1, as he ducks),
        /// "Another car.", "They're back.", "More of them.", "That's a van. Oh no." (VoiceLines).
        /// </summary>
        public readonly string RickyLine;
        /// <summary>Seconds between this wave being beaten and the next one called.</summary>
        public const float Lull = 8f;

        HitmanTier(int wave, int look, string name, int frontTwo, int sideTwo, int frontOne, Vector2 react, float raise, float accuracy, float aimLag,
                   bool fireFromPath, float pathAccuracy, Vector2Int burstRounds, Vector2 burstGap, Vector2 burstPause,
                   int magazine, float reload, bool callsReload, bool coverOnReload, float jog, float gunWalk, float lane, float stagger,
                   float boundSeconds, bool boundInStep, float entryHold, float secondWait, float thirdWait, float blindReangle,
                   int blindVolley, bool fightsOnHit, bool plated, string calloutKey, string vehicle, string rickyLine)
        {
            Wave = wave; Look = look; Name = name; FrontTwoDoors = frontTwo; SideTwoDoors = sideTwo; FrontOneDoor = frontOne;
            React = react; Raise = raise; Accuracy = accuracy; AimLag = aimLag; FireFromPath = fireFromPath; PathAccuracy = pathAccuracy;
            BurstRounds = burstRounds; BurstGap = burstGap; BurstPause = burstPause;
            Magazine = magazine; Reload = reload; CallsReload = callsReload; CoverOnReload = coverOnReload;
            Jog = jog; GunWalk = gunWalk; Lane = lane; Stagger = stagger; BoundSeconds = boundSeconds; BoundInStep = boundInStep;
            EntryHold = entryHold; SecondWait = secondWait; ThirdWait = thirdWait; BlindReangle = blindReangle;
            BlindVolley = blindVolley; FightsOnHit = fightsOnHit; Plated = plated; CalloutKey = calloutKey; Vehicle = vehicle;
            RickyLine = rickyLine;
        }

        /// <summary>A night's hitman waves (Brett 0.1.57: five, was three).</summary>
        public const int Waves = 5;

        /// <summary>The tier for <paramref name="wave"/> (1–5; anything past 5 is wave 5, anything under 1 wave 1).</summary>
        public static HitmanTier For(int wave) =>
            wave <= 1 ? Street : wave == 2 ? StreetCrew : wave == 3 ? Pro : wave == 4 ? ProHeavy : Operator;

        /// <summary>The night's men, every wave, for a room with two doors or one (2 + 3 + 2 + 3 + 3 = 13 either way).</summary>
        public static int Total(bool twoDoors)
        {
            int n = 0;
            for (int w = 1; w <= Waves; w++) { For(w).Split(twoDoors, out int f, out int s); n += f + s; }
            return n;
        }

        /// <summary>Men this wave sends to the front and the side door.</summary>
        public void Split(bool twoDoors, out int front, out int side)
        {
            front = twoDoors ? FrontTwoDoors : FrontOneDoor;
            side = twoDoors ? SideTwoDoors : 0;
        }

        /// <summary>The window before his first round: react + raise, never under <see cref="MinWindow"/>, + <see cref="BehindExtra"/> from behind.</summary>
        public float Window(float react, bool fromBehind) => Mathf.Max(MinWindow, react + Raise) + (fromBehind ? BehindExtra : 0f);

        // Each wave between its neighbours on every knob (Brett 0.1.57: "each harder"): react and raise down, accuracy, the
        // magazine and the pace up, the waits shorter. The two new waves' given numbers (the lead's table) are kept as given;
        // the rest are filled between the waves either side. A street crew still backs out when hit and never says "Reloading."
        // (street men), but steps out of your line to reload and answers one round through its door.

        /// <summary>Wave 1: two street men in the grey sedan (unchanged since research/27).</summary>
        static readonly HitmanTier Street = new(1, LookStreet, "street muscle", 2, 0, 2,
            react: new Vector2(0.7f, 0.9f), raise: 0.7f, accuracy: 0.5f, aimLag: 0.35f,
            fireFromPath: false, pathAccuracy: 0.3f,
            burstRounds: new Vector2Int(1, 2), burstGap: new Vector2(0.45f, 0.65f), burstPause: new Vector2(1.6f, 2.4f),
            magazine: 7, reload: 2.6f, callsReload: false, coverOnReload: false,
            jog: 2.6f, gunWalk: 1.25f, lane: 0.2f, stagger: 1.2f, boundSeconds: 3.0f, boundInStep: true,
            entryHold: 1.5f, secondWait: 1.6f, thirdWait: 1.2f, blindReangle: 3.5f,
            blindVolley: 0, fightsOnHit: false, plated: false, calloutKey: "vo.hitman.street.spot", vehicle: "Sedan", rickyLine: "vo.ricky.car");

        /// <summary>Wave 2 (Brett 0.1.57): a street crew of three in a dark red sedan — two at the front and one round the side.</summary>
        static readonly HitmanTier StreetCrew = new(2, LookStreet, "street crew", 2, 1, 3,
            react: new Vector2(0.6f, 0.8f), raise: 0.65f, accuracy: 0.55f, aimLag: 0.3f,
            fireFromPath: false, pathAccuracy: 0.35f,
            burstRounds: new Vector2Int(1, 2), burstGap: new Vector2(0.4f, 0.55f), burstPause: new Vector2(1.4f, 2.0f),
            magazine: 8, reload: 2.3f, callsReload: false, coverOnReload: true,
            jog: 2.8f, gunWalk: 1.38f, lane: 0.32f, stagger: 1.0f, boundSeconds: 2.6f, boundInStep: true,
            entryHold: 1.15f, secondWait: 1.3f, thirdWait: 0.9f, blindReangle: 2.75f,
            blindVolley: 1, fightsOnHit: false, plated: false, calloutKey: "vo.hitman.street.spot", vehicle: "RedSedan", rickyLine: "vo.ricky.car2");

        /// <summary>Wave 3: the professionals in the black SUV (the old wave 2, unchanged).</summary>
        static readonly HitmanTier Pro = new(3, LookPro, "professionals", 1, 1, 2,
            react: new Vector2(0.45f, 0.55f), raise: 0.5f, accuracy: 0.75f, aimLag: 0.22f,
            fireFromPath: true, pathAccuracy: 0.45f,
            burstRounds: new Vector2Int(2, 3), burstGap: new Vector2(0.3f, 0.4f), burstPause: new Vector2(1.0f, 1.4f),
            magazine: 12, reload: 1.8f, callsReload: true, coverOnReload: true,
            jog: 3.0f, gunWalk: 1.5f, lane: 0.45f, stagger: 0.7f, boundSeconds: 2.2f, boundInStep: false,
            entryHold: 0.8f, secondWait: 1.0f, thirdWait: 0.6f, blindReangle: 2.0f,
            blindVolley: 2, fightsOnHit: true, plated: false, calloutKey: "vo.hitman.pro.contact", vehicle: "SUV", rickyLine: "vo.ricky.car3");

        /// <summary>Wave 4 (Brett 0.1.57): a heavier pro team of three in a charcoal SUV; suits like wave 3, no plate.</summary>
        static readonly HitmanTier ProHeavy = new(4, LookPro, "heavy professionals", 2, 1, 3,
            react: new Vector2(0.4f, 0.5f), raise: 0.45f, accuracy: 0.82f, aimLag: 0.18f,
            fireFromPath: true, pathAccuracy: 0.52f,
            burstRounds: new Vector2Int(2, 3), burstGap: new Vector2(0.22f, 0.3f), burstPause: new Vector2(0.85f, 1.2f),
            magazine: 15, reload: 1.6f, callsReload: true, coverOnReload: true,
            jog: 3.2f, gunWalk: 1.65f, lane: 0.52f, stagger: 0.55f, boundSeconds: 1.85f, boundInStep: false,
            entryHold: 0.6f, secondWait: 0.75f, thirdWait: 0.45f, blindReangle: 1.6f,
            blindVolley: 3, fightsOnHit: true, plated: false, calloutKey: "vo.hitman.pro.contact", vehicle: "CharcoalSUV", rickyLine: "vo.ricky.car4");

        /// <summary>Wave 5: the operators in the white van (the old wave 3, unchanged): plated, the SMG — the top of the scale.</summary>
        static readonly HitmanTier Operator = new(5, LookOperator, "operators", 2, 1, 3,
            react: new Vector2(0.3f, 0.4f), raise: 0.4f, accuracy: 0.9f, aimLag: 0.15f,
            fireFromPath: true, pathAccuracy: 0.6f,
            burstRounds: new Vector2Int(2, 2), burstGap: new Vector2(0.15f, 0.2f), burstPause: new Vector2(0.7f, 1.0f),
            magazine: 20, reload: 1.4f, callsReload: true, coverOnReload: true,
            jog: 3.4f, gunWalk: 1.8f, lane: 0.6f, stagger: 0.4f, boundSeconds: 1.5f, boundInStep: false,
            entryHold: 0.4f, secondWait: 0.5f, thirdWait: 0.3f, blindReangle: 1.2f,
            blindVolley: 4, fightsOnHit: true, plated: true, calloutKey: "vo.hitman.pro.contact", vehicle: "Van", rickyLine: "vo.ricky.car5");
    }
}
