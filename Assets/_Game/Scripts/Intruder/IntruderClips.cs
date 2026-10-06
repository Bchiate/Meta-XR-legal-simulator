using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// Measured facts about the Mixamo clips on Alex's rig (HIS > Debug > Measure Intruder Clips → Logs/clip-metrics.txt,
    /// 2026-09-23). Gameplay speeds, locks and blow timings are derived from these so feet stay planted and blows land on
    /// the contact frame. Re-measure after swapping a clip or the character. Root frame: x = his right, z = his forward.
    /// </summary>
    public static class IntruderClips
    {
        // Natural ground speed of the in-place cycles (backward slip of the planted foot/hand), m/s.
        public const float WalkNatural = 1.63f, RunNatural = 5.22f, LimpNatural = 1.60f, CrawlNatural = 0.42f;

        // Gameplay speed per gait. The Animator plays each cycle at speed / natural so the planted foot doesn't slide.
        public const float WalkSpeed = 1.25f, LimpSpeed = 0.8f, CrawlSpeed = 0.35f;
        /// <summary>Fleeing / charging: the sprint cycle played at 0.69× reads as a hard run at 3.6 m/s.</summary>
        public const float RunSpeed = 3.6f;

        // 2026-09-23 scenario clips (Logs/clip-metrics.txt slip medians).
        /// <summary>Drunk neighbour: DrunkWalk slips 0.77 m/s, played at 0.75 (0.97×); DrunkRun (leaving when told) at its natural 2.31.</summary>
        public const float DrunkWalkNatural = 0.77f, DrunkRunNatural = 2.31f, DrunkWalkSpeed = 0.75f, DrunkRunSpeed = 2.31f;
        /// <summary>DrunkWalkBack: a backward shuffle at 1.08 m/s (hands up, a step away from the gun).</summary>
        public const float DrunkBackNatural = 1.08f;
        /// <summary>Burglar: CrouchedWalk slips 1.24 m/s, played at 0.9 (0.73×). SneakForward (2.01) reads too fast for a man creeping.</summary>
        public const float SneakNatural = 1.24f, SneakSpeed = 0.9f;
        /// <summary>RunLookBack: a jog looking over his shoulder at 1.69 m/s — an unarmed burglar's getaway when nobody's shooting.</summary>
        public const float LookBackRunSpeed = 1.69f;

        /// <summary>
        /// In-place gestures (<see cref="IntruderAnimator.ActKind"/> order): clip lengths after the import trims (CaughtReact is cut
        /// to its 1.1 s startle). The brain holds his feet for length × OneShotExit.
        /// </summary>
        public static readonly float[] ActLength = { 5.23f, 5.40f, 3.97f, 3.60f, 2.00f, 1.57f, 1.10f, 4.83f, 4.47f, 3.77f,
                                                     2.20f, 2.00f, 0.67f, 2.47f };   // + gunman: TossAway, PistolDraw, PistolLower (cut), PistolGrabFloor

        // ── Gunman (M3a; Logs/clip-metrics.txt, Logs/poses/Pistol*.png, TossAway.png, 2026-09-24).
        /// <summary>
        /// PistolWalkAimed slips 2.37 m/s over a 0.8 s cycle (long strides). His walk with the gun is 1.5 m/s, the cycle at 0.63×
        /// (a 1.27 s stride pair — normal cadence; at 1.25 m/s the legs read slow-motion). The run is the plain sprint (Running,
        /// RunSpeed): Mixamo's two pistol runs both hold the gun up at the face (research/09 correction), so fleeing uses the
        /// sprint's own arm swing with the gun in his fist.
        /// </summary>
        public const float GunWalkNatural = 2.37f, GunWalkSpeed = 1.5f;
        /// <summary>
        /// TossAway (2.2 s overhand throw): wind-up back by 0.55 s, arm over his head at 0.83 s, whipping forward-down by 1.10 s —
        /// the gun leaves his hand at <see cref="TossRelease"/>. His arms are back at his sides by 1.65 s.
        /// </summary>
        public const float TossRelease = 0.9f, TossDone = 1.65f;

        /// <summary>
        /// Posture changes (import-trimmed). FallToKnees 1.1 s (knees down at 0.8 s of the cut), CrouchToProne 1.8 s (flat by
        /// 1.1 s; the state leaves at 1.1 s and lowers into the flat hold over 0.6 s), KneelToStand 2.2 s, GetUpFromStomach 4.2 s, GetUpFromBack (from sitting) 1.9 s, + the blends in/out.
        /// </summary>
        public const float KneelDownTime = 1.1f + 0.3f, ProneDownTime = 1.8f + 0.3f, KneelUpTime = 2.2f + 0.3f,
                           ProneUpTime = 4.2f + 0.4f, SitUpTime = 1.9f + 0.4f;

        /// <summary>
        /// TripFall (cut from 0.6 s, where the stumble starts): a lurch into a face-plant, root motion (0.36, 1.69) m and the head
        /// 0.48 m past the root at the end; on the floor 0.7 s in, still by 1.4 s. Needs ~2.3 m of clear floor ahead.
        /// </summary>
        public const float TripLength = 1.50f, TripClear = 2.3f;
        public static readonly Vector2 TripTravel = new(0.36f, 1.69f);
        /// <summary>StumbleBackFall: staggers 1.89 m backwards onto his backside, sitting up by 2.4 s. Needs ~2.3 m clear behind.</summary>
        public const float StumbleBackLength = 3.33f, StumbleBackClear = 2.3f;

        // ── Getting out of a car (Brett 2026-10-05: "have the flow of cops exiting look better"): Mixamo "Exiting Car" (Ch18, out of
        //    the driver's side, 5.8 s, root motion), imported as ExitCar and its mirror ExitCarMirror (the passenger's side:
        //    MixamoImportSettings.Sided). Clip seconds at 1× (IntruderCarExit plays it at ~1.5×); the take's frame: x his right
        //    (−x = out of the driver's door), z his forward, from where he sits; the mirror negates x and the turns.
        //    Measured on the Alex rig with its root motion applied (ExitClipProbe.Run → Logs/exit-clip-probe.txt, 2026-10-05; the
        //    pre-measured raw-FBX timings were within 0.07 s, the turn 4°; ExitShutsDoor is now his hand back at the door's line,
        //    4.13 s — the old 3.4 was the start of the push): still until 0.3 s; his left hand on the door 0.45 s, pushed widest
        //    1.43 s; his left foot out over the sill from 1.43 s, down on the road 2.20 s; up 1.9–2.63 s; the right foot off the
        //    floor 2.45 s, down 2.80 s; his hips past the door's line by a body's width 2.90 s; then he turns back (3.0–3.7 s),
        //    pushes the door shut with his left hand (back at the door's line 4.13 s) and walks off to his left (4.5–5.8 s) — the
        //    tail the game skips (its doors stay open: the police use them as cover). Planted feet slide 1.1 cm (left, 2.20–2.80 s)
        //    and 3.1 cm (right, 2.80–2.90 s, still settling); ExitCarMirror is the exact mirror (travel +0.92, turn +69°).
        /// <summary>The take's length (s).</summary>
        public const float ExitLength = 5.80f;
        /// <summary>His hand on the door (its swing starts) and the door pushed wide (its swing ends).</summary>
        public const float ExitDoorTouch = 0.45f, ExitDoorOpen = 1.43f;
        /// <summary>His first foot starts out over the sill (the seated part is over) and is down on the road outside.</summary>
        public const float ExitFootFrom = 1.43f, ExitFootOut = 2.20f;
        /// <summary>
        /// He starts to come up off the seat (his hips leaving it, his head through the door: the probe's "up 1.9–2.63 s"). Shot from
        /// here he dies a death on the street from where he is, not the seat's slump (Intruder.Kill).
        /// </summary>
        public const float ExitRiseFrom = 1.90f;
        /// <summary>On his feet (his head at full height) — no longer a rider from here; his gun shows a beat later, outside the body.</summary>
        public const float ExitStand = 2.63f, ExitGunAt = 2.73f;
        /// <summary>His hips past the door's line by a body's width, both feet down: the hand-off to his gun gait (IntruderBuilder's exit time).</summary>
        public const float ExitClear = 2.90f;
        /// <summary>The take shuts the door behind him from here (skipped: past <see cref="ExitClear"/>): his hand back at the door's line.</summary>
        public const float ExitShutsDoor = 4.13f;
        /// <summary>
        /// His hips over the root sitting at the take's start (its seat is high: the car floor 0.28 m up), and sitting in SittingIdle
        /// (Gait.Seated) and Driving: the exit starts this much lower so his hips stay where they were.
        /// </summary>
        public const float ExitSeatHips = 0.80f, SitHips = 0.53f, DriveHips = 0.59f;
        /// <summary>
        /// The exit clip's root starts under his centre of mass, ~0.10 m ahead of his hips sitting (x right, z forward): it starts that
        /// far ahead of the seated clips' root (theirs is under the hips) so the hips don't slide across the cross-fade.
        /// </summary>
        public static readonly Vector2 ExitRootAhead = new(-0.01f, 0.10f);
        /// <summary>The root's travel (x, z) and turn (°, + = to his right) from the start to <see cref="ExitClear"/>: out to his left, facing out.</summary>
        public static readonly Vector2 ExitTravel = new(-0.92f, -0.09f);
        public const float ExitTurn = -69f;

        /// <summary>Normalized exit time of every one-shot state (IntruderBuilder.OneShot).</summary>
        public const float OneShotExit = 0.92f;

        /// <summary>Hit reaction clip lengths, <see cref="IntruderAnimator.HitKind"/> order (Head, Gut, Body, Legs).</summary>
        public static readonly float[] HitLength = { 1.43f, 1.60f, 1.33f, 1.33f, 1.50f };   // + Pistol: PistolHit cut to 0.5–2.0 s (flinch, back on aim)

        /// <summary>Attack clip lengths and the moment the fist/blade reaches furthest forward, <see cref="IntruderAnimator.AttackKind"/> order.</summary>
        public static readonly float[] AttackLength = { 2.00f, 1.30f, 1.97f, 2.63f, 2.50f, 3.27f };
        public static readonly float[] AttackContact = { 1.13f, 0.43f, 0.66f, 1.03f, 0.78f, 1.26f };

        /// <summary>MmaKick: the foot reaches 1.23 m forward at 0.67 s (the door plane is 1.25 m away).</summary>
        public const float KickLength = 1.60f, KickContact = 0.68f;

        /// <summary>
        /// Legs shot out (state Fall_LegsOut): TripFall's lunge — the leg gives way mid-stride and he pitches forward — handed
        /// over to the face-down crawl at <see cref="FallToCrawl"/> (normalized, 0.45 s in, hips still 0.6 m up, hands reaching
        /// for the floor) over <see cref="FallBlend"/>: he lands in the crawl on his hands, hips down 0.7 s after the shot, settled
        /// by <see cref="FallOnFloor"/>. Root travel (0.23, 1.60) m, veering 8° right; the crawl's head/hands end 0.46/0.6 m past
        /// the root (<see cref="FallBodyAhead"/>). Logs/poses/Ctl_LegsOut*.png, 2026-09-24.
        /// Not TripFall to its end: it ends rolled onto his side, and blending that into the crawl swept his legs 27–43 cm through
        /// the floor (Cand_Trip170/190). Not HitOnLegs (≤ 0.1.13): it ends on his BACK, and the blend into the face-down crawl
        /// swung him upright with his legs 76 cm through the floor and the body jumping 0.8 m (Ctl_LegsOutBlend).
        /// </summary>
        public const float FallToCrawl = 0.30f, FallBlend = 0.45f, FallOnFloor = 0.9f, FallBodyAhead = 0.65f;
        public static readonly Vector2 FallTravel = new(0.23f, 1.60f);

        /// <summary>
        /// Deaths, <see cref="IntruderAnimator.DeathKind"/> order. Where the head ends up (x right, z forward; +z = face down
        /// toward where he was facing, −z = on his back), how far the body reaches along that fall, its half-width across it,
        /// when the pose settles and the clip length.
        /// </summary>
        /// 5 = Kneeling: KneelDeath cut from 1.9 s (its knees pose; it starts standing), pitches forward face down, 1.8 s.
        /// 6 = Prone: ProneDeath from face down, settles in 1.2 s. 7 = OnBack: sitting → slumps flat (a held StumbleBackFall frame);
        /// its length covers the 0.8 s blend in (IntruderBuilder): at 0.60 the rig went to sleep mid-slump, head still up.
        public static readonly Vector2[] DeathHead = { new(-0.04f, 1.49f), new(0.44f, -0.82f), new(0.08f, -1.80f), new(-0.40f, 1.59f), new(-0.08f, 1.71f),
                                                       new(-0.02f, 1.60f), new(-0.18f, 0.64f), new(0.04f, -0.30f) };
        public static readonly float[] DeathReach = { 1.49f, 0.94f, 1.92f, 1.71f, 1.72f, 1.83f, 1.08f, 0.58f };
        public static readonly float[] DeathHalfWidth = { 0.48f, 0.53f, 0.92f, 0.58f, 0.92f, 0.56f, 0.44f, 0.51f };
        public static readonly float[] DeathSettle = { 2.97f, 2.77f, 2.40f, 2.87f, 3.17f, 1.60f, 1.20f, 0.50f };
        public static readonly float[] DeathLength = { 3.43f, 2.83f, 2.57f, 2.97f, 3.17f, 1.80f, 1.87f, 0.90f };
    }
}
