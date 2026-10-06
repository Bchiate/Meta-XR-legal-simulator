using HIS.Weapons;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>A body region on the intruder. Routes hits to the Intruder with the right multiplier and wound placement.</summary>
    public class Hitbox : MonoBehaviour, IDamageable
    {
        public BodyPart Part = BodyPart.Torso;
        [Tooltip("Damage multiplier for this region (head 3x, limbs 0.6x...)")]
        public float Multiplier = 1f;

        Intruder _intruder;
        WoundSystem _wounds;

        void Awake()
        {
            _intruder = GetComponentInParent<Intruder>();
            _wounds = GetComponentInParent<WoundSystem>();
        }

        /// <summary>
        /// Rounds kill (Brett 0.1.22: "most often if they got shot they should just die" — the same rule as the player's own
        /// body): a bullet or pellet in the head, neck, chest or belly kills him, through the door too; his first round in an
        /// arm or leg is a real wound (a leg takes his legs out), his second limb round kills. Off: the graded hit points of
        /// 0.1.21 and before (the older scenario/door-shot/pose tests are built on those and switch this off).
        /// </summary>
        public static bool LethalRounds = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => LethalRounds = true; // domain reload off

        public float ApplyDamage(in DamageInfo info)
        {
            // Ricky during the Marshal weapons check, and Ortiz in the room for the handover (Brett 0.1.57): no wound, no damage,
            // nothing reported (Intruder.PracticeHit). Anyone else, or Ricky any other time: false, as before.
            if (_intruder != null && _intruder.PracticeHit(info)) return 0f;
            float applied = info.Amount * Multiplier;
            if (Part == BodyPart.Head && info.Type == DamageType.Bullet) applied = Mathf.Max(applied, 1000f); // headshot = kill
            // Police hook (Scripts/Police/IntruderPoliceLook.cs): a SWAT officer's heavy vest turns a torso/belly round into a
            // wound, counted like a limb round (the first wounds, the next kills). A wave 3 hitman's plate covers the chest only:
            // his belly is as usual (research/27 §2).
            bool vest = _intruder != null && _intruder.Armored && (Part == BodyPart.Torso || (Part == BodyPart.Abdomen && !_intruder.IsHitman));
            if (LethalRounds && info.Type == DamageType.Bullet && _intruder != null)
                applied = !vest && Part is BodyPart.Head or BodyPart.Neck or BodyPart.Torso or BodyPart.Abdomen
                    ? Mathf.Max(applied, 1000f)
                    : _intruder.LimbRound(Part, applied);
            // The plate stopped it (the first chest round; not the one that kills): a stagger, a clank and sparks — no wound
            // and no blood (Intruder.PlateStop). Hitmen only: SWAT vests bleed as before.
            bool plate = vest && _intruder.IsHitman && Part == BodyPart.Torso && LethalRounds && info.Type == DamageType.Bullet
                         && applied < _intruder.Health;
            // Explicit null checks: `?.` skips Unity's destroyed-object check.
            // Shot through the closed door he's out of sight: the wound goes on his body (hidden with him, there when the door
            // bursts and he comes in) but no spray or splatter shows on our side of it. A hit that kills him mid-kick bursts the
            // door and shows him (Intruder.Kill), so that one sprays after all.
            bool hidden = _intruder != null && _intruder.BehindDoor;
            if (plate) _intruder.PlateStop(info);
            else if (_wounds != null && !hidden) _wounds.AddWound(this, info, applied);
            if (_intruder != null) _intruder.TakeDamage(applied, Part, info);
            if (_wounds != null && hidden && !plate) _wounds.AddWound(this, info, applied, worldFx: !_intruder.BehindDoor);
            HIS.Core.Log.V($"[HIS] hit {Part} {info.Type} {applied:F0} dmg");
            return applied;
        }
    }
}
