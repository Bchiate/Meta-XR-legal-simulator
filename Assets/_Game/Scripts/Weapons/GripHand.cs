using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// The virtual hand shown ONLY while this controller holds a weapon (or steadies the shotgun's fore-end): Meta's
    /// OculusHand rig as shipped, snapped to the weapon's grip pose (<see cref="GripPoses"/>), parented to the weapon's pose
    /// frame so recoil and the pump stroke carry it. Empty hands show nothing: passthrough shows the real ones (research/16 §3.2).
    /// The index finger follows the Touch sensors: off the trigger → straight along the frame; touching → resting on the
    /// shoe (+8 % curl, not Alyx's full curl); pulling → curls with the trigger value, and the gun's trigger turns with it.
    /// The thumb lifts a little when it leaves the controller's face.
    /// </summary>
    [RequireComponent(typeof(HandController))]
    public class GripHand : MonoBehaviour
    {
        public static readonly Color HandColor = new(0.52f, 0.54f, 0.58f);

        [Tooltip("Meta's OculusHand_R / OculusHand_L model (com.meta.xr.sdk.core Meshes/HandTracking), as shipped.")]
        [SerializeField] GameObject handModel;
        [SerializeField] Material material;

        HandController _hand;
        HandRig _rig;
        WeaponGrip _grip;
        GripPose _pose;
        bool _support, _analog, _logged;
        float _onto = 1f, _curl, _thumb;
        HandShape _shape;

        public bool Showing => _grip != null;
        public WeaponGrip Grip => _grip;
        public bool Supporting => _grip != null && _support;

        void Awake() { _hand = GetComponent<HandController>(); }

        bool EnsureRig()
        {
            if (_rig != null && _rig.Root != null) return true;
            if (handModel == null) { if (!_logged) { _logged = true; Debug.LogWarning("[HIS] GripHand: no hand model"); } return false; }
            var go = Instantiate(handModel, transform, false);
            go.name = _hand.IsLeft ? "GripHand_L" : "GripHand_R";
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
            {
                if (material != null) smr.sharedMaterial = material;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; smr.receiveShadows = false;
                smr.updateWhenOffscreen = true; // bounds from the bind pose would cull a hand bent round a grip
            }
            _rig = new HandRig(go, _hand.IsLeft);
            go.SetActive(false);
            return true;
        }

        /// <summary>Holding hand (support = false: pose at the weapon's GripPoint) or the free hand on the support pose.</summary>
        public void Attach(WeaponGrip grip, bool support)
        {
            if (grip == null || !EnsureRig()) return;
            var pose = support ? grip.SupportPose : grip.Pose;
            var frame = support ? grip.SupportFrame : grip.Frame;
            if (pose == null || frame == null) return;
            Detach();
            _grip = grip; _pose = pose; _support = support;
            // Main hand: the web lands on the weapon's GripPoint (where the controller's web point holds it). Poses are authored
            // for the right hand; Place mirrors them for the left.
            var web = support ? pose.WebAt : frame.InverseTransformPoint(grip.GetComponent<Grabbable>().GripPoint.position);
            if (!support && _hand.IsLeft) web.x = -web.x; // GripPoint is where THIS hand goes; Place flips it back
            _shape = pose.Grip;
            _rig.Apply(_shape);
            _rig.Place(frame, web, pose.Rotation, false, pose.Shift);
            _analog = OVRInput.GetCurrentInteractionProfile(_hand.IsLeft ? OVRInput.Hand.HandLeft : OVRInput.Hand.HandRight) != OVRInput.InteractionProfile.Touch;
            _onto = 1f; _curl = 0f; _thumb = 0f;
            _rig.Root.SetActive(true);
        }

        public void Detach()
        {
            if (_grip != null && !_support) _grip.SetTrigger(0f);
            _grip = null; _pose = null;
            if (_rig == null || _rig.Root == null) return;
            _rig.Root.SetActive(false);
            _rig.Root.transform.SetParent(transform, false);
        }

        void LateUpdate()
        {
            if (_grip == null) return;
            if (_rig.Root == null) { _grip = null; _rig = null; return; } // went down with its weapon
            var c = _hand.Controller;
            float dt = Time.unscaledDeltaTime;
            if (!_support && _pose.TriggerFinger)
            {
                float pull = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, c);
                bool touch = OVRInput.Get(OVRInput.Touch.PrimaryIndexTrigger, c);
                // Touch Plus/Pro report an analog curl (0 pointing … 1 on the trigger); plain Touch only near-touch.
                float curl = _analog ? OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTriggerCurl, c)
                                     : OVRInput.Get(OVRInput.NearTouch.PrimaryIndexTrigger, c) || pull > 0f ? 1f : 0f;
                float onto = Mathf.Max(curl, touch || pull > 0.02f ? 1f : 0f);
                _onto = Mathf.Lerp(_onto, onto, 1f - Mathf.Exp(-20f * dt));                         // AnimatedHandOVR gains
                _curl = Mathf.Lerp(_curl, (touch ? 0.08f : 0f) + 0.92f * pull, 1f - Mathf.Exp(-35f * dt));
                var idx = FingerPose.Lerp(_pose.IndexOff, _pose.Grip.Index, _onto);
                _shape.Index = FingerPose.Lerp(idx, _pose.IndexPull, _curl);
                _grip.SetTrigger(pull);
            }
            if (!_support && _pose.TriggerFinger)
            {
                bool thumbOn = OVRInput.Get(OVRInput.Touch.PrimaryThumbRest, c) || OVRInput.Get(OVRInput.Touch.One, c) ||
                               OVRInput.Get(OVRInput.Touch.Two, c) || OVRInput.Get(OVRInput.Touch.PrimaryThumbstick, c);
                _thumb = Mathf.Lerp(_thumb, thumbOn ? 0f : 1f, 1f - Mathf.Exp(-12f * dt));
                _shape.Thumb = FingerPose.Lerp(_pose.Grip.Thumb, _pose.ThumbLift, _thumb);
            }
            _rig.Apply(_shape);
        }
    }
}
