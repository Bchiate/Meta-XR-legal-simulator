using HIS.Weapons;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// A police vehicle's body (a box collider): a round stops here — a hole, sparks and a metal ping, no flesh.
    /// <see cref="Part"/>: <see cref="PoliceCars.BodyPart"/> (the shell) or <see cref="PoliceCars.LightBarPart"/> (a hit breaks
    /// the bar: it stops flashing, 0.1.41).
    /// </summary>
    public class VehicleBody : MonoBehaviour, IDamageable, IInanimate
    {
        internal PoliceCars Cars;
        internal int Vehicle, Part;
        public float ApplyDamage(in DamageInfo info) { if (Cars != null) Cars.OnBodyHit(Vehicle, Part, info); return 0f; }
    }
}
