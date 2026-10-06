using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>Common weapon plumbing: knows which hand holds it and receives per-frame input from that hand.</summary>
    [RequireComponent(typeof(Grabbable))]
    public abstract class WeaponBase : MonoBehaviour
    {
        public string DisplayName = "Weapon";
        public HolsterType HolsterType = HolsterType.Hip;
        [Tooltip("Local pose of the weapon inside its holster slot (weapon root relative to the slot).")]
        public Vector3 HolsterOffsetPosition;
        public Vector3 HolsterOffsetEuler;
        public Grabbable Grabbable { get; private set; }
        public HandController Hand => Grabbable.Holder;
        public bool IsHeld => Grabbable.IsHeld;

        protected virtual void Awake()
        {
            Grabbable = GetComponent<Grabbable>();
            Grabbable.Grabbed += OnGrabbed;
            Grabbable.Released += OnReleased;
        }

        protected virtual void OnGrabbed(HandController hand) { }
        protected virtual void OnReleased(HandController hand) { }

        /// <summary>Called every frame by the holding hand after its input was read.</summary>
        public abstract void HandUpdate(HandController hand);

        /// <summary>Short status string for the HUD.</summary>
        public virtual string Status => DisplayName;
    }
}
