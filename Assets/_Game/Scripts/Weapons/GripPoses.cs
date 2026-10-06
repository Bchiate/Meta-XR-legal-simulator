using UnityEngine;

namespace HIS.Weapons
{
    public enum GripKind { None, Pistol, ShotgunRear, ShotgunPump, Knife, PistolM9, RifleGrip, RifleHandguard } // (serialized as ints: add at the end)

    /// <summary>
    /// A fixed hand pose on a weapon, authored for the RIGHT hand in the weapon's pose frame (<see cref="WeaponGrip"/>:
    /// the Model child, or the shotgun's Pump); the left hand gets the mirror image. The web of the hand (between thumb and
    /// index, <see cref="HandRig.WebLocalR"/>) lands on <see cref="WebAt"/>; the hand's thumb side (+Z) points along
    /// <see cref="ThumbDir"/> and the back of the hand (+Y) along <see cref="BackDir"/>, then <see cref="Tweak"/> turns it in
    /// hand space. <see cref="Grip"/> is the held shape with the index resting on the trigger (trigger guns) or wrapped.
    /// </summary>
    public sealed class GripPose
    {
        /// <summary>Support poses only: the web point in the support frame. A main pose puts the web on the weapon's GripPoint.</summary>
        public Vector3 WebAt;
        public Vector3 ThumbDir, BackDir, Tweak;
        /// <summary>Hand-space nudge off the web point (right hand): −Y lifts the palm off a wide grip (the web stays put).</summary>
        public Vector3 Shift;
        public HandShape Grip;
        public bool TriggerFinger;
        /// <summary>Index straight along the frame (off the trigger) / at the end of the pull.</summary>
        public FingerPose IndexOff, IndexPull;
        /// <summary>Thumb when it's off the controller's face (not touching the rest, stick or buttons).</summary>
        public FingerPose ThumbLift;
        public Quaternion Rotation => Quaternion.LookRotation(ThumbDir, BackDir) * Quaternion.Euler(Tweak);
    }

    /// <summary>The hand poses per weapon (research/16 §3.3). Checked in renders: HandSnapshots → Logs/poses/hands_*.png.</summary>
    public static class GripPoses
    {
        // Glock 19 (Model_G19 frame: muzzle +Z, slide on top). Web high in the tang under the slide, three fingers round the
        // grip, thumb along the left of the frame, index on the trigger shoe (or straight along the frame above the guard).
        public static readonly GripPose Pistol = new()
        {
            // Solved by HandSnapshots (SOLVE=1) against Model_G19: palm ≤ 4 mm into the grip (flesh on it), fingers ≤ 2 mm.
            ThumbDir = new Vector3(0f, 0.951f, 0.309f), BackDir = new Vector3(1f, 0f, 0f), Tweak = new Vector3(-10f, -27f, 30f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(20f, 10f, 40f, -10f), Index = new FingerPose(28f, 40f, 20f, -8f),
                Middle = new FingerPose(60f, 50f, 30f), Ring = new FingerPose(70f, 100f, 60f), Pinky = new FingerPose(35f, 100f, 60f),
            },
            TriggerFinger = true,
            IndexOff = new FingerPose(34f, 0f, 0f, 14f), IndexPull = new FingerPose(20f, 55f, 28f, -12f),
            ThumbLift = new FingerPose(20f, 0f, 5f, -10f), // straightened along the frame (lifting it swung it over the slide)
        };

        // M9 (Model_M9 frame, web seat on the G19's GripPoint): the same hold on a grip 6 mm thicker and ~3.5° less raked, the
        // trigger further forward (72 mm DA reach), index pad on the middle of the curved shoe. Solved (SOLVE=1, 2026-09-23):
        // palm ≤ 2 mm, fingers ≤ 1 mm; the thumb along the frame under the slide catch reads 7 mm into the frame's left edge
        // (hidden by the gun; the renders show it outside the frame and clear of the slide). The palm sits 12 mm off the web
        // point toward the back of the hand (the wider grip), like the shotgun's. ThumbLift hand-set: a small lift along the
        // frame (the solver's pick swung it up over the slide).
        public static readonly GripPose PistolM9 = new()
        {
            ThumbDir = new Vector3(0f, 0.968f, 0.25f), BackDir = new Vector3(1f, 0f, 0f), Tweak = new Vector3(5f, -7f, 15f), Shift = new Vector3(0f, -0.012f, 0f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(20f, 0f, 0f, -10f), Index = new FingerPose(24f, 25f, 13f, 8f),
                Middle = new FingerPose(25f, 90f, 54f), Ring = new FingerPose(30f, 85f, 51f), Pinky = new FingerPose(5f, 65f, 39f),
            },
            TriggerFinger = true,
            IndexOff = new FingerPose(22f, 0f, 0f, 22f), IndexPull = new FingerPose(16f, 45f, 23f, 8f),
            ThumbLift = new FingerPose(28f, 0f, 0f, -10f),
        };

        // Remington 870 (Model_R870 frame): rear hand on the stock's wrist behind the receiver, thumb over the top to the left,
        // index on the trigger. The wrist is 4 cm wide, so the hand sits 12 mm off the web point toward its back (palm clear).
        // Solved (SOLVE=1): palm ≤ 2 mm, thumb base ≤ 3 mm, fingers ≤ 2 mm.
        public static readonly GripPose ShotgunRear = new()
        {
            ThumbDir = new Vector3(0f, 0.82f, 0.57f), BackDir = new Vector3(1f, 0f, 0f), Tweak = new Vector3(15f, -7f, 20f), Shift = new Vector3(0f, -0.012f, 0f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(-10f, 30f, 30f, -20f), Index = new FingerPose(28f, 40f, 20f, 8f),
                Middle = new FingerPose(30f, 105f, 63f), Ring = new FingerPose(45f, 105f, 63f), Pinky = new FingerPose(55f, 105f, 63f),
            },
            TriggerFinger = true,
            IndexOff = new FingerPose(6f, 35f, 11f, 26f), IndexPull = new FingerPose(30f, 48f, 24f, 8f),
            ThumbLift = new FingerPose(-20f, 15f, 15f, -40f),
        };

        // Fore-end (Pump frame), authored as a right hand (a left-hander's support hand); Brett's left hand is its mirror:
        // the pump lies in the palm, thumb along the near side, fingers round the far side. Solved: every finger ≤ 1 mm.
        public static readonly GripPose ShotgunPump = new()
        {
            WebAt = new Vector3(0.016f, -0.026f, -0.02f), ThumbDir = new Vector3(0f, 0f, 1f), BackDir = new Vector3(0f, -1f, 0f),
            Tweak = new Vector3(-5f, 5f, -10f), Shift = new Vector3(0f, -0.004f, 0f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(-30f, 20f, 10f, -40f), Index = new FingerPose(70f, 70f, 42f), Middle = new FingerPose(65f, 80f, 48f),
                Ring = new FingerPose(60f, 80f, 48f), Pinky = new FingerPose(40f, 80f, 48f),
            },
        };

        // Ka-Bar (Model_KaBar frame: blade +Z, edge down): hammer grip, the web on the spine right behind the guard, all four
        // fingers round the handle, thumb over the index. Solved (SOLVE=1): nothing sinks in more than 1 mm. The prefab's
        // GripPoint is derived from this and the pistol pose (MainSceneBuilder), so the hand sits on the controller the same way.
        public static readonly GripPose Knife = new()
        {
            ThumbDir = new Vector3(0f, 0f, 1f), BackDir = new Vector3(1f, 0f, 0f), Tweak = new Vector3(15f, -2f, 35f), Shift = new Vector3(0f, -0.012f, 0f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(-10f, 50f, 30f, -40f), Index = new FingerPose(25f, 105f, 63f), Middle = new FingerPose(30f, 105f, 63f),
                Ring = new FingerPose(35f, 105f, 63f), Pinky = new FingerPose(95f, 105f, 63f),
            },
        };

        // AR-15 (Model_AR15 frame: muzzle +Z, web seat at the origin, bore 46 mm above it): a pistol-grip hold on a grip only 23 mm
        // wide and deep that rakes ~27° (its axis measured from the mesh: back strap 35°, front strap 18°), so the hand starts
        // from the Glock's family with the grip's own axis; web high in the tang, three fingers round the front strap, index on
        // the trigger (70 mm reach). Solved (HandSnapshots SOLVE=1 -hands ar15, 2026-10-04): palm and thumb base ≤ 1 mm, the
        // wrapping fingers ≤ 1 mm; the index resting on the shoe reads 11 mm into the lower receiver's side and the thumb round
        // the left of the tang 10 mm into the receiver (both hidden by the gun; the solver's best). The hand sits 18 mm off the
        // controller's web point toward the back of the hand (the shotgun's 12).
        public static readonly GripPose RifleGrip = new()
        {
            ThumbDir = new Vector3(0f, 0.894f, 0.447f), BackDir = new Vector3(1f, 0f, 0f), Tweak = new Vector3(-5f, -22f, 20f), Shift = new Vector3(0f, -0.018f, 0f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(-10f, 60f, 0f, -30f), Index = new FingerPose(36f, 35f, 18f, 8f),
                Middle = new FingerPose(15f, 105f, 63f), Ring = new FingerPose(5f, 105f, 63f), Pinky = new FingerPose(80f, 50f, 30f),
            },
            TriggerFinger = true,
            IndexOff = new FingerPose(34f, 0f, 0f, 18f), IndexPull = new FingerPose(28f, 45f, 23f, 8f),
            ThumbLift = new FingerPose(-10f, 30f, 0f, 5f),
        };

        // AR-15 handguard (the Model's "Handguard" point: on the bore axis, mid-length of the round 57 × 53 mm M4 handguard),
        // authored as a right hand like ShotgunPump; Brett's left hand is its mirror. Static (no pump): the handguard lies in the
        // palm, thumb along the near side, fingers round the far side — a bigger tube than the 870's fore-end, so the web sits
        // further out and lower. Solved (SOLVE=1, 2026-10-04): every finger ≤ 1 mm, the thumb 2 mm.
        public static readonly GripPose RifleHandguard = new()
        {
            WebAt = new Vector3(0.021f, -0.03f, 0f), ThumbDir = new Vector3(0f, 0f, 1f), BackDir = new Vector3(0f, -1f, 0f),
            Tweak = new Vector3(-5f, 10f, -10f), Shift = new Vector3(0f, -0.004f, 0f),
            Grip = new HandShape
            {
                Thumb = new FingerPose(-20f, 20f, 40f, -50f), Index = new FingerPose(0f, 90f, 54f), Middle = new FingerPose(0f, 85f, 51f),
                Ring = new FingerPose(0f, 70f, 42f), Pinky = new FingerPose(0f, 35f, 21f),
            },
        };

        public static GripPose Get(GripKind k) => k switch
        {
            GripKind.Pistol => Pistol, GripKind.ShotgunRear => ShotgunRear, GripKind.ShotgunPump => ShotgunPump, GripKind.Knife => Knife, GripKind.PistolM9 => PistolM9,
            GripKind.RifleGrip => RifleGrip, GripKind.RifleHandguard => RifleHandguard, _ => null,
        };
    }
}
