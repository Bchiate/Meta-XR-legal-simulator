using System;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Minimal controller-grab target. Held objects become kinematic and follow the hand's anchor;
    /// on release they inherit the hand's velocity. Designed so Meta's Interaction SDK can replace it later
    /// (weapons only listen to OnGrabbed/OnReleased and the hand's input events).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Grabbable : MonoBehaviour
    {
        [Tooltip("Where the palm sits when held. Local pose of this transform is applied to the hand anchor.")]
        [SerializeField] Transform gripPoint;
        [SerializeField] float grabRadius = 0.12f;
        [Tooltip("Optional: the grab zone runs from the grip point to this point (e.g. along a shotgun stock) so the weapon can be grabbed anywhere on it.")]
        [SerializeField] Transform grabZoneEnd;
        [SerializeField] bool allowTwoHands;

        public HandController Holder { get; private set; }
        public bool IsHeld => Holder != null;
        public float GrabRadius => grabRadius;
        public Transform GripPoint => gripPoint != null ? gripPoint : transform;

        /// <summary>
        /// Distance from a hand to the nearest grabbable point (grip, or along the grab zone). Slung on the back, only the zone's
        /// upper half counts — the stock end over the right shoulder, where the player reaches and the headset still sees the hand:
        /// its grip end hangs behind the right hip, where it tied with the holstered pistol (0.1.16: reaching for the pistol with
        /// the body at the clamp, or for an empty hip, took the shotgun).
        /// </summary>
        public float GrabDistance(Vector3 hand)
        {
            var a = GripPoint.position;
            if (grabZoneEnd == null) return Vector3.Distance(hand, a);
            var b = grabZoneEnd.position; var ab = b - a;
            float from = CurrentHolster != null && CurrentHolster.Type == HolsterType.Back ? 0.5f : 0f;
            float t = Mathf.Clamp(Vector3.Dot(hand - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f), from, 1f);
            return Vector3.Distance(hand, a + ab * t);
        }

        public event Action<HandController> Grabbed;
        public event Action<HandController> Released;

        Rigidbody _rb;
        Transform _originalParent;
        Vector3 _homePosition; Quaternion _homeRotation;
        float _lostTimer, _restTimer;
        [SerializeField] float returnToHolsterAfter = 6f;

        public Holster CurrentHolster { get; private set; }
        /// <summary>The slot the loadout gave it: auto-return and ties go here first (pistol and knife never swap hips).</summary>
        public Holster HomeHolster { get; set; }

        Vector3 _dockPos; Quaternion _dockRot;

        public void DockTo(Transform slot, Vector3 localPos, Quaternion localRot)
        {
            CurrentHolster = slot.GetComponent<Holster>();
            _rb.isKinematic = true;
            // No physics interpolation while riding the body: an interpolated kinematic child keeps getting its transform
            // overwritten with a stale physics pose — docked guns ended up 0.5 m below their holsters (M0 device test).
            _rb.interpolation = RigidbodyInterpolation.None;
            transform.SetParent(slot, false);
            _dockPos = localPos; _dockRot = localRot;
            transform.localPosition = localPos; transform.localRotation = localRot;
        }

        // Belt and braces: while docked, the holster slot alone decides where the weapon is.
        void LateUpdate()
        {
            if (CurrentHolster == null || IsHeld) return;
            if (transform.parent != CurrentHolster.transform) transform.SetParent(CurrentHolster.transform, false);
            transform.localPosition = _dockPos; transform.localRotation = _dockRot;
        }

        Holster _pulledFrom;
        public void BeginPull()
        {
            _pulledFrom = CurrentHolster;
            if (CurrentHolster != null) { CurrentHolster.Undock(this); CurrentHolster = null; HIS.Core.Sfx.Play("holster.draw", transform.position); }
            transform.SetParent(null, true);
            _rb.isKinematic = true;
        }
        public void EndPull()
        {
            if (_pulledFrom != null && _pulledFrom.IsFree) { _pulledFrom.Dock(this); _pulledFrom = null; return; }
            _pulledFrom = null; _rb.isKinematic = false; _rb.interpolation = RigidbodyInterpolation.Interpolate; _rb.WakeUp();
        }

        void Start() { _homePosition = transform.position; _homeRotation = transform.rotation; }

        void FixedUpdate()
        {
            if (IsHeld || _rb.isKinematic || CurrentHolster != null) return;
            // Floor safety: never sink below the tracked floor (y = 0 at FloorLevel origin); settle on it instead.
            if (transform.position.y < 0.02f)
            {
                var p = transform.position; p.y = 0.02f; transform.position = p;
                if (_rb.linearVelocity.y < 0) { var v = _rb.linearVelocity; v.y = 0; _rb.linearVelocity = v * 0.5f; }
            }
            // Left lying around (or lost) → back into a free matching holster.
            bool lost = transform.position.y < -0.5f || transform.position.y > 4f || (transform.position - _homePosition).magnitude > 12f;
            _lostTimer = lost ? _lostTimer + Time.fixedDeltaTime : 0f;
            _restTimer = _rb.linearVelocity.sqrMagnitude < 0.01f ? _restTimer + Time.fixedDeltaTime : 0f;
            if (_lostTimer > 3f || _restTimer > returnToHolsterAfter)
            {
                _lostTimer = _restTimer = 0f; _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero;
                var holster = Holster.FindFree(this);
                if (holster != null) { holster.Dock(this); Debug.Log($"[HIS] {name} returned to {holster.name}"); }
                else { transform.SetPositionAndRotation(_homePosition, _homeRotation); _rb.isKinematic = true; }
            }
        }

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        public void OnGrab(HandController hand)
        {
            if (IsHeld) Holder.ForceRelease();
            if (CurrentHolster != null) { CurrentHolster.Undock(this); CurrentHolster = null; HIS.Core.Sfx.Play("holster.draw", transform.position); }
            _pulledFrom = null;
            Holder = hand;
            _originalParent = null; // never re-parent to a holster implicitly
            _rb.interpolation = RigidbodyInterpolation.None; // interpolation lags a hand-parented kinematic body
            _rb.isKinematic = true;
            _rb.detectCollisions = true;

            // Snap so that gripPoint coincides with the hand anchor.
            transform.SetParent(hand.Anchor, true);
            var gripLocalRot = Quaternion.Inverse(transform.rotation) * GripPoint.rotation;
            transform.localRotation = Quaternion.Inverse(gripLocalRot);
            var gripLocalPos = transform.InverseTransformPoint(GripPoint.position);
            transform.localPosition = -(transform.localRotation * Vector3.Scale(gripLocalPos, transform.localScale));
            Grabbed?.Invoke(hand);
        }

        public void OnRelease(HandController hand, Vector3 velocity, Vector3 angularVelocity)
        {
            if (Holder != hand) return;
            Holder = null;
            transform.SetParent(_originalParent, true);
            // Let go near a matching holster → it snaps in instead of dropping.
            var holster = Holster.FindNearest(GripPoint.position, this);
            if (holster != null)
            {
                holster.Dock(this);
                hand.Haptic(0.6f, 0.06f);
                HIS.Core.Sfx.Play("holster.in", transform.position, Mathf.Lerp(0.35f, 1f, velocity.magnitude / 1.5f));
                Debug.Log($"[HIS] {name} docked in {holster.name}");
                Released?.Invoke(hand); return;
            }
            Debug.Log($"[HIS] {name} dropped at {transform.position}");
            _rb.isKinematic = false;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.WakeUp();
            _rb.linearVelocity = velocity;
            _rb.angularVelocity = angularVelocity;
            Released?.Invoke(hand);
        }
    }
}
