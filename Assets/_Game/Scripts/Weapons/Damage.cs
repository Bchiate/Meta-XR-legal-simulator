using UnityEngine;

namespace HIS.Weapons
{
    public enum DamageType { Bullet, Stab, Slash, Blunt }

    public enum BodyPart { Head, Neck, Torso, Abdomen, LeftArm, RightArm, LeftLeg, RightLeg }

    /// <summary>Everything a hit needs to be resolved into damage + a wound.</summary>
    public struct DamageInfo
    {
        public float Amount;          // base damage before body-part multipliers
        public DamageType Type;
        public Vector3 Point;         // world hit point
        public Vector3 Normal;        // surface normal at the hit
        public Vector3 Direction;     // travel direction of the bullet/blade
        public float Penetration;     // 0..1, how deep a stab went / bullet energy remaining
        public GameObject Source;     // weapon that caused it

        public override string ToString() => $"{Type} {Amount:F0} @ {Point}";
    }

    public interface IDamageable
    {
        /// <summary>Returns the damage actually applied after multipliers.</summary>
        float ApplyDamage(in DamageInfo info);
    }

    /// <summary>
    /// Something a round goes through, marking it (a police car's glass: a crack, a tinkle) and carrying on with a little
    /// less punch into whatever is behind it (the driver). Firearm follows up to two of these per round.
    /// </summary>
    public interface IPierceable
    {
        void Pierce(in DamageInfo info);
    }

    /// <summary>Not flesh (a car's body): no flesh sound, no hit-confirm pulse in the hand.</summary>
    public interface IInanimate { }
}
