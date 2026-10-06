using HIS.Weapons;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// One pane of a police vehicle's glass (a thin box collider on it): a round cracks it and carries on (Firearm); the third
    /// hit on the same pane shatters it and this collider goes with it (0.1.41). <see cref="Pane"/>: which pane of the vehicle
    /// (a pane can have two parts: a cruiser's side glass and its front door's window); <see cref="Part"/>: the part's glass
    /// object, which its cracks stick to.
    /// </summary>
    public class VehicleGlass : MonoBehaviour, IPierceable
    {
        internal PoliceCars Cars;
        internal int Vehicle, Pane;
        internal Transform Part;
        public void Pierce(in DamageInfo info) { if (Cars != null) Cars.OnGlassHit(this, info); }
    }
}
