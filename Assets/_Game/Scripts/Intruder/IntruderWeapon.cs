using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// A prop an intruder carries: the Ka-Bar or the Glock (M3a gunman). Parented to the right hand bone by IntruderBuilder
    /// and hidden unless the scenario arms him with it. Drop()/Toss() let it fall to the real floor as a plain physics prop —
    /// never a player Grabbable (its auto-return would teleport it into the player's holster).
    /// </summary>
    public class IntruderWeapon : MonoBehaviour
    {
        [SerializeField] HIS.Scenario.WeaponKind kind = HIS.Scenario.WeaponKind.Knife;
        [Tooltip("Gun: the barrel's end, +Z out of the bore (the judge's 'pointing at you' ray and the flash).")]
        [SerializeField] Transform muzzle;
        [Tooltip("Collider for the prop once it's on the floor (m).")]
        [SerializeField] Vector3 floorBox = new(0.04f, 0.03f, 0.31f);

        public HIS.Scenario.WeaponKind Kind => kind;
        public Transform Muzzle => muzzle;
        public bool InHand { get; private set; }

        Transform _socket;
        Vector3 _localPos; Quaternion _localRot;

        void Awake()
        {
            _socket = transform.parent;
            _localPos = transform.localPosition; _localRot = transform.localRotation;
        }

        /// <summary>Editor (IntruderBuilder): what this prop is.</summary>
        public void Setup(HIS.Scenario.WeaponKind k, Transform muzzlePoint, Vector3 box) { kind = k; muzzle = muzzlePoint; floorBox = box; }

        public void Show(bool inHand)
        {
            if (inHand) ReturnToSocket();
            InHand = inHand;
            gameObject.SetActive(inHand);
        }

        /// <summary>Let go of it: it falls where his hand is and stays on the floor (evidence: "he dropped the knife").</summary>
        public void Drop(Vector3 push) => Release(push, Random.insideUnitSphere * 6f, "dropped");

        /// <summary>Thrown away on "Drop it!" (TossAway): a lob off to one side, spinning, then it clatters on the floor.</summary>
        public void Toss(Vector3 velocity) => Release(velocity, new Vector3(Random.Range(-4f, 4f), Random.Range(-10f, 10f), Random.Range(-4f, 4f)), "tossed");

        void Release(Vector3 velocity, Vector3 spin, string how)
        {
            if (!InHand) return;
            InHand = false;
            var owner = GetComponentInParent<Intruder>();
            transform.SetParent(null, true);
            if (!TryGetComponent<BoxCollider>(out var col)) col = gameObject.AddComponent<BoxCollider>();
            col.size = floorBox; col.center = Vector3.zero;
            // Not against his own body: the bone hitboxes (kinematic) shoved the gun about while he collapsed onto it, so it
            // skated and sometimes came to rest wedged into him (DroppedMag does the same with the player's hands).
            if (owner != null)
                foreach (var c in owner.GetComponentsInChildren<Collider>(true))
                    if (c != null && c != col) Physics.IgnoreCollision(col, c);
            if (!TryGetComponent<Rigidbody>(out var rb)) rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = false; rb.useGravity = true; rb.mass = kind == HIS.Scenario.WeaponKind.Gun ? 0.9f : 0.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; // a thrown gun mustn't tunnel through the real floor
            rb.linearVelocity = velocity; rb.angularVelocity = spin;
            if (kind == HIS.Scenario.WeaponKind.Gun) StartCoroutine(Clatter());
            Debug.Log($"[HIS] Intruder {how} his {kind}");
        }

        /// <summary>The gun hitting the floor: one clack when it first lands (the casing clink, low and heavy).</summary>
        System.Collections.IEnumerator Clatter()
        {
            var rb = GetComponent<Rigidbody>();
            bool falling = false;
            for (float giveUp = Time.time + 3f; Time.time < giveUp && rb != null; )
            {
                float vy = rb.linearVelocity.y;
                if (vy < -1f) falling = true;
                else if (falling && vy > -0.2f)
                {
                    int h = HIS.Core.Sfx.Play("casing.brass", transform.position, 1f);
                    if (h != 0) HIS.Core.Sfx.ScalePitch(h, 0.45f);
                    yield break;
                }
                yield return null;
            }
        }

        void ReturnToSocket()
        {
            if (_socket == null || transform.parent == _socket) return;
            if (TryGetComponent<Rigidbody>(out var rb)) Destroy(rb);
            if (TryGetComponent<BoxCollider>(out var col)) Destroy(col);
            transform.SetParent(_socket, false);
            transform.localPosition = _localPos; transform.localRotation = _localRot;
        }
    }
}
