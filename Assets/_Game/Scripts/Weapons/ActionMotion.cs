using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// How a gun's moving parts move (<see cref="Firearm"/>): the slide/bolt stroke per shot, the animated magazine reload, the
    /// charging handle and the dust cover. Seconds and metres, all in one place. A new instance is the M9's motion exactly as
    /// tuned in research/20 (the pistol reload Brett asked for: "do the ... m9 reload"); <see cref="Rifle"/> is the AR-15's.
    /// </summary>
    [System.Serializable]
    public class ActionMotion
    {
        [Tooltip("0 = the slide rides the recoil curve (pistols). Above 0 = the bolt snaps fully back on the shot and stays there " +
                 "this long, then the buffer spring drives it home over BoltHomeSeconds, on its own clock (rifle).")]
        public float BoltBackSeconds, BoltHomeSeconds;

        [Tooltip("Reload: how far the magazine slides out of the well (m) and how long that takes, before it falls free.")]
        public float MagOutTravel = 0.13f, MagOutSeconds = 0.12f;
        [Tooltip("The dropped magazine's speed out of the well (m/s, along the magazine axis).")]
        public float DropSpeed = 0.7f;
        [Tooltip("Reload: the fresh magazine appears this far under its seat (m) this many seconds in, and rises home over MagInSeconds.")]
        public float MagInTravel = 0.13f, MagInAt = 0.6f, MagInSeconds = 0.25f;
        [Tooltip("The seat: the hand pushes it this far past home (m), then it clicks back down onto the catch over SeatSeconds. 0 = straight home.")]
        public float SeatOvershoot, SeatSeconds;
        [Tooltip("Reload of an empty gun: this many seconds in, the slide / bolt release slams it home over SlideSlamSeconds.")]
        public float SlideReleaseAt = 1.1f, SlideSlamSeconds = 0.04f;

        [Tooltip("Charging handle (rifle): pulled back over this, held a beat, then let go (the bolt rides it home in SlideSlamSeconds).")]
        public float ChargeBackSeconds = 0.14f, ChargeHoldSeconds = 0.06f;
        [Tooltip("Dust cover (rifle): springs open over this the first time the bolt moves, with a small slap past open.")]
        public float CoverOpenSeconds = 0.07f;

        [Tooltip("One [HIS] line per reload phase (mag out, fresh mag in, seated, bolt release / charging handle) so a test can follow it.")]
        public bool LogPhases;

        /// <summary>
        /// The AR-15 (Brett 2026-10-03: "do good animations"). Light 5.56: the action is quicker than the M9's slide and the
        /// magazine drops straight out of a vertical well instead of sliding down a raked grip.
        /// </summary>
        public static ActionMotion Rifle => new()
        {
            // The carrier is back for one frame at 72 Hz and home two frames later (~45 ms a shot): a real AR cycles in ~75 ms at
            // its ~800 rpm cyclic rate, and anything slower than this smears into a blur instead of a snap.
            BoltBackSeconds = 0.015f, BoltHomeSeconds = 0.03f,
            // The magazine's top sits 31 mm inside the well (prep parts.md §4): out 45 mm in 80 ms on the release, then it falls free.
            MagOutTravel = 0.045f, MagOutSeconds = 0.08f, DropSpeed = 1.0f,
            // The off hand brings a fresh one up from the pouch: in view 11 cm under the well at 0.7 s, home 0.3 s later (eased:
            // fast, then slowing into the well), pushed 3 mm past the catch and clicked back down in 60 ms.
            MagInTravel = 0.11f, MagInAt = 0.7f, MagInSeconds = 0.3f, SeatOvershoot = 0.003f, SeatSeconds = 0.06f,
            // Then the bolt catch is slapped: the carrier slams home in 35 ms (one B press = the whole reload, 1.8 s like the
            // M9's 1.6 plus the longer magazine).
            SlideReleaseAt = 1.25f, SlideSlamSeconds = 0.035f,
            // Racking the charging handle (stick click on an empty chamber, or an empty gun whose bolt isn't locked): back in
            // 130 ms, a 50 ms beat, released.
            ChargeBackSeconds = 0.13f, ChargeHoldSeconds = 0.05f,
            // The dust cover is spring-loaded: it flips open with the first shot in ~60 ms and slaps the receiver.
            CoverOpenSeconds = 0.06f,
            LogPhases = true,
        };
    }
}
