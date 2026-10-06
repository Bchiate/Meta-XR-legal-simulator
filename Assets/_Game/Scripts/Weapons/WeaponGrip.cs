using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Where a virtual hand goes on this weapon (research/16 §3.3): the holding hand's pose and frame, the trigger it pulls,
    /// and optionally a support pose for the other hand (the shotgun's fore-end). Poses live in <see cref="GripPoses"/>.
    /// </summary>
    public class WeaponGrip : MonoBehaviour
    {
        public GripKind Kind;
        [Tooltip("Pose space for the holding hand: the Model child, so recoil carries the hand with the gun.")]
        public Transform Frame;
        [Tooltip("Turns about its local X with the pull (pivot on the trigger pin); + = shoe swings back.")]
        public Transform Trigger;
        public float TriggerMaxDegrees = 16f;
        public GripKind SupportKind;
        [Tooltip("Pose space for the support hand (the pump: the hand rides the stroke).")]
        public Transform SupportFrame;
        [Tooltip("The free hand squeezing its grip within this distance of the support pose takes hold of it.")]
        public float SupportRadius = 0.13f;

        Quaternion _triggerRest;
        bool _hasRest;
        Firearm _gun;

        public GripPose Pose => GripPoses.Get(Kind);
        public GripPose SupportPose => GripPoses.Get(SupportKind);

        /// <summary>World position of the support hand's web point for the given hand (mirror for the left).</summary>
        public Vector3 SupportPoint(bool left)
        {
            var p = SupportPose; if (p == null || SupportFrame == null) return transform.position;
            var at = p.WebAt; if (left) at.x = -at.x;
            return SupportFrame.TransformPoint(at);
        }

        /// <summary>0 = at rest, 1 = pulled through. A cocked DA/SA gun's trigger already sits back (Firearm.TriggerRest).</summary>
        public void SetTrigger(float pull)
        {
            if (Trigger == null) return;
            if (!_hasRest) { _triggerRest = Trigger.localRotation; _hasRest = true; _gun = GetComponent<Firearm>(); }
            float at = Mathf.Max(_gun != null ? _gun.TriggerRest : 0f, Mathf.Clamp01(pull));
            Trigger.localRotation = _triggerRest * Quaternion.Euler(TriggerMaxDegrees * at, 0f, 0f);
        }
    }
}
