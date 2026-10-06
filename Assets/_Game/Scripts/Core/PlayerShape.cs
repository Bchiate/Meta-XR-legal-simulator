using HIS.Weapons;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>Where a blow or a round landed on the player. None = it missed him.</summary>
    public enum HitZone { None, Head, Torso, Arm, Leg }

    /// <summary>What hit the player: the wound rule (<see cref="PlayerState.IsFatal"/>) depends on it.</summary>
    public enum HitBy { Gun, Knife, Fists }

    /// <summary>
    /// The player's body as the gunman's rounds see it. The player has no colliders (the passthrough body is his real one), so
    /// each shot builds a few analytic shapes from the head pose, <see cref="PlayerBody"/>'s Chest (neck pivot) and Waist (belt)
    /// and the two controllers: a head sphere, a torso capsule from the shoulder line to the hips, arm capsules shoulder → hand
    /// and leg capsules hip → floor. A round is one ray; the nearest shape it crosses is the zone (an arm held out in front of
    /// the chest takes the round first). Plain math on a struct: no allocations, nothing on the hot path.
    /// </summary>
    public struct PlayerShape
    {
        // Sizes (m): an adult man with the headset on.
        public const float HeadRadius = 0.11f, TorsoRadius = 0.14f, ArmRadius = 0.055f, LegRadius = 0.075f;
        const float ShoulderWidth = 0.18f, ShoulderDrop = 0.15f; // shoulder joints from the neck pivot
        const float TorsoTopDrop = 0.16f;                         // capsule axis top under the neck (its cap reaches the collar)
        const float TorsoBelowWaist = 0.10f;                      // axis bottom under the belt (its cap reaches the crotch)
        const float HipWidth = 0.10f, HipDrop = 0.12f, FootLift = 0.06f;
        const float ArmReach = 0.85f;                             // a controller further than this from its shoulder isn't a hand
        /// <summary>PlayerBody's head-neck model (Oculus default): the eyes 7.5 cm above and 8 cm in front of the neck pivot.</summary>
        static readonly Vector3 NeckToEye = new(0f, 0.075f, 0.0805f);
        /// <summary>Skull centre from the eyes, in the head's frame: 7 cm back, 1 cm up.</summary>
        static readonly Vector3 EyeToHeadCentre = new(0f, 0.01f, -0.07f);
        /// <summary>PlayerBody's belt: this fraction of the eye height below the eyes (its gripBelowEyeRatio).</summary>
        const float EyeToBeltRatio = 0.31f;
        /// <summary>
        /// A standing man seen from the front, as one disc round his chest (m): the radius at which a round aimed at the chest
        /// with a Gaussian spread hits him as often as it lands inside this disc (Monte Carlo on the default body; WoundSelfTest).
        /// </summary>
        public const float EquivalentRadius = 0.27f;

        public Vector3 Head, TorsoTop, TorsoBottom, LShoulder, LHand, RShoulder, RHand, LHip, LFoot, RHip, RFoot;

        /// <summary>
        /// The shapes from a head pose and a body frame. <paramref name="bodyYaw"/> faces the torso (PlayerBody's clamped yaw).
        /// A hand is only used within reach of its shoulder (untracked controllers sit at the rig origin); otherwise it hangs.
        /// </summary>
        public static PlayerShape Build(Vector3 eye, Quaternion headRot, Vector3 neck, Vector3 waist, Quaternion bodyYaw, float floorY,
                                        Vector3? leftHand, Vector3? rightHand)
        {
            Vector3 up = Vector3.up, right = bodyYaw * Vector3.right, fwd = bodyYaw * Vector3.forward;
            var s = new PlayerShape
            {
                Head = eye + headRot * EyeToHeadCentre,
                TorsoTop = neck - up * TorsoTopDrop,
                TorsoBottom = waist - up * TorsoBelowWaist,
                RShoulder = neck + right * ShoulderWidth - up * ShoulderDrop,
                LShoulder = neck - right * ShoulderWidth - up * ShoulderDrop,
                RHip = waist + right * HipWidth - up * HipDrop,
                LHip = waist - right * HipWidth - up * HipDrop,
            };
            if (s.TorsoBottom.y > s.TorsoTop.y - 0.05f) s.TorsoBottom = s.TorsoTop - up * 0.05f; // folded right down: still a torso
            s.RHand = HandOr(rightHand, s.RShoulder, fwd);
            s.LHand = HandOr(leftHand, s.LShoulder, fwd);
            s.RFoot = new Vector3(s.RHip.x, Mathf.Min(floorY + FootLift, s.RHip.y), s.RHip.z);
            s.LFoot = new Vector3(s.LHip.x, Mathf.Min(floorY + FootLift, s.LHip.y), s.LHip.z);
            return s;
        }

        static Vector3 HandOr(Vector3? hand, Vector3 shoulder, Vector3 fwd) =>
            hand.HasValue && (hand.Value - shoulder).sqrMagnitude <= ArmReach * ArmReach ? hand.Value : shoulder + Vector3.down * 0.58f + fwd * 0.08f;

        /// <summary>
        /// The zone of the first shape the ray crosses within <paramref name="maxDistance"/> (None = a miss), and how far along
        /// the ray it is. Ties go head → torso → arms → legs.
        /// </summary>
        public HitZone Raycast(Vector3 origin, Vector3 dir, float maxDistance, out float distance)
        {
            dir.Normalize();
            distance = maxDistance;
            var zone = HitZone.None;
            Nearest(RaySphere(origin, dir, Head, HeadRadius), HitZone.Head, ref distance, ref zone);
            Nearest(RayCapsule(origin, dir, TorsoTop, TorsoBottom, TorsoRadius), HitZone.Torso, ref distance, ref zone);
            Nearest(RayCapsule(origin, dir, LShoulder, LHand, ArmRadius), HitZone.Arm, ref distance, ref zone);
            Nearest(RayCapsule(origin, dir, RShoulder, RHand, ArmRadius), HitZone.Arm, ref distance, ref zone);
            Nearest(RayCapsule(origin, dir, LHip, LFoot, LegRadius), HitZone.Leg, ref distance, ref zone);
            Nearest(RayCapsule(origin, dir, RHip, RFoot, LegRadius), HitZone.Leg, ref distance, ref zone);
            return zone;
        }

        static void Nearest(float t, HitZone z, ref float best, ref HitZone zone) { if (t >= 0f && t < best) { best = t; zone = z; } }

        /// <summary>Distance along a unit ray to a sphere; 0 from inside it, −1 = no hit ahead.</summary>
        public static float RaySphere(Vector3 o, Vector3 d, Vector3 c, float r)
        {
            var oc = o - c;
            float b = Vector3.Dot(oc, d), cc = oc.sqrMagnitude - r * r, h = b * b - cc;
            if (cc <= 0f) return 0f;
            if (h < 0f) return -1f;
            float t = -b - Mathf.Sqrt(h);
            return t >= 0f ? t : -1f;
        }

        /// <summary>
        /// Distance along a unit ray to a capsule (segment a–b, radius r); 0 from inside it, −1 = no hit ahead. The side of the
        /// cylinder first (Íñigo Quílez's capIntersect); off its length, or parallel to the axis, the nearer end sphere.
        /// </summary>
        public static float RayCapsule(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float r)
        {
            Vector3 ba = b - a, oa = o - a;
            float baba = Vector3.Dot(ba, ba);
            if (baba < 1e-8f) return RaySphere(o, d, a, r);
            float s = Mathf.Clamp01(Vector3.Dot(oa, ba) / baba);
            if ((oa - ba * s).sqrMagnitude <= r * r) return 0f; // a contact shot
            float bard = Vector3.Dot(ba, d), baoa = Vector3.Dot(ba, oa);
            float qa = baba - bard * bard;
            if (qa > 1e-6f * baba)
            {
                float qb = baba * Vector3.Dot(d, oa) - baoa * bard, qc = baba * oa.sqrMagnitude - baoa * baoa - r * r * baba;
                float h = qb * qb - qa * qc;
                if (h < 0f) return -1f; // clear of the infinite cylinder: clear of the end spheres too
                float t = (-qb - Mathf.Sqrt(h)) / qa, y = baoa + t * bard;
                if (t >= 0f && y > 0f && y < baba) return t;
            }
            float t1 = RaySphere(o, d, a, r), t2 = RaySphere(o, d, b, r);
            return t1 < 0f ? t2 : t2 < 0f ? t1 : Mathf.Min(t1, t2);
        }

        /// <summary>
        /// The aim turned by a random error from a 2D Gaussian of <paramref name="sigma"/> radians (Box–Muller; u1, u2 uniform
        /// in [0, 1]). σ 0 = straight down the barrel.
        /// </summary>
        public static Vector3 Spread(Vector3 aim, float sigma, float u1, float u2)
        {
            aim.Normalize();
            if (sigma <= 0f) return aim;
            float off = sigma * Mathf.Sqrt(-2f * Mathf.Log(Mathf.Max(u1, 1e-6f))), turn = 2f * Mathf.PI * u2;
            var side = Vector3.Cross(aim, Vector3.up);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(aim, Vector3.forward);
            side.Normalize();
            var upAxis = Vector3.Cross(side, aim);
            return (aim + (side * Mathf.Cos(turn) + upAxis * Mathf.Sin(turn)) * Mathf.Tan(Mathf.Min(off, 1.2f))).normalized;
        }

        /// <summary>
        /// The spread (σ, radians) at which rounds aimed at a standing man's chest from <paramref name="range"/> hit him about
        /// <paramref name="hitChance"/> of the time: inside the <see cref="EquivalentRadius"/> disc with P = 1 − exp(−ρ²/2σ²).
        /// </summary>
        public static float SpreadSigma(float hitChance, float range)
        {
            float p = Mathf.Clamp(hitChance, 0.02f, 0.98f);
            float atTarget = EquivalentRadius / Mathf.Sqrt(-2f * Mathf.Log(1f - p));
            return Mathf.Atan2(atTarget, Mathf.Max(range, 0.3f));
        }

        // ───────────────────────────── the live player ─────────────────────────────
        static PlayerBody s_body;
        static HandController s_left, s_right;
        static float s_lookedAt = -100f;

        /// <summary>
        /// The player's body right now. <paramref name="eye"/> is where the shooter takes the head to be (Intruder.PlayerHead: an
        /// untracked headset lying at the floor counts as a man standing, eyes 1.6 m up). While that is the real tracked head the
        /// torso hangs from PlayerBody's Chest/Waist and the arms reach to the controllers; otherwise it's built from the head
        /// alone, hands hanging.
        /// </summary>
        public static PlayerShape Current(Transform head, Vector3 eye, float floorY)
        {
            var headRot = head != null ? head.rotation : Quaternion.identity;
            bool tracked = head != null && (head.position - eye).sqrMagnitude < 1e-4f;
            if ((s_body == null || s_left == null || s_right == null) && Mathf.Abs(Time.unscaledTime - s_lookedAt) > 2f) FindRig(); // (clock restarts in Play Mode)
            var neck = eye - headRot * NeckToEye;
            Vector3 waist; Quaternion yaw;
            if (tracked && s_body != null && s_body.Chest != null && (s_body.Chest.position - neck).sqrMagnitude < 0.2f * 0.2f)
            {
                neck = s_body.Chest.position; waist = s_body.Waist.position; yaw = s_body.Chest.rotation;
            }
            else
            {
                waist = new Vector3(neck.x, floorY + (eye.y - floorY) * (1f - EyeToBeltRatio), neck.z);
                yaw = Quaternion.Euler(0f, HeadingYaw(headRot), 0f);
            }
            var left = tracked && s_left != null && s_left.isActiveAndEnabled ? s_left.transform.position : (Vector3?)null;
            var right = tracked && s_right != null && s_right.isActiveAndEnabled ? s_right.transform.position : (Vector3?)null;
            return Build(eye, headRot, neck, waist, yaw, floorY, left, right);
        }

        static void FindRig()
        {
            s_lookedAt = Time.unscaledTime;
            s_body = Object.FindAnyObjectByType<PlayerBody>();
            foreach (var h in Object.FindObjectsByType<HandController>(FindObjectsInactive.Exclude))
                if (h.IsLeft) s_left = h; else s_right = h;
        }

        /// <summary>PlayerBody's VRIK heading: stable when looking straight down.</summary>
        static float HeadingYaw(Quaternion r)
        {
            var face = Vector3.Cross(r * Vector3.right, Vector3.up) + r * Vector3.forward;
            face.y = 0f;
            return face.sqrMagnitude < 1e-6f ? r.eulerAngles.y : Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
        }

        /// <summary>"head" / "torso" / "arm" / "leg" / "miss" (logs).</summary>
        public static string Name(HitZone z) => z switch
        {
            HitZone.Head => "head", HitZone.Torso => "torso", HitZone.Arm => "arm", HitZone.Leg => "leg", _ => "miss",
        };
    }
}
