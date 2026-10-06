using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// Keeps the scene's one directional light at the same angle to the virtual door in every session (Brett 0.1.17: the door
    /// "appearing gray sometimes with like a light gray outline in the door"). The light is fixed in Unity's world, but the
    /// world (the floor-level tracking space) turns with the headset's heading at launch or a recenter, while the door stays
    /// put in the real room — so the same door was lit differently every session: in his 0.1.15/0.1.16 play session the leaf's
    /// room face got N·L 0.01 (ambient only: 0.39 linear, mid gray) while its thin side faces got 0.64 (a white outline around
    /// gray panels and trim); in the 0.1.16 soak it got 0.51 (0.83, near white).
    /// Now the light comes from the room side, <see cref="SideAngle"/>° round toward the latch, at the scene light's own pitch:
    /// the closed leaf and the open leaf's face toward the doorway both get N·L ≈ 0.45, and the intruder coming in is lit from
    /// the front, whatever the heading. Re-aimed every <see cref="CheckSeconds"/> s (door picked, hinge side chosen, recenter).
    /// Self-starting (no scene object needed), like ThermalGovernor. No shadows, so turning the light costs nothing.
    /// </summary>
    public class DoorKeyLight : MonoBehaviour
    {
        /// <summary>How far the light's direction is turned from straight in through the door toward the latch side.</summary>
        public const float SideAngle = 45f;
        public const float CheckSeconds = 0.5f;
        const float MinPitch = 25f, MaxPitch = 75f;

        public static DoorKeyLight Instance { get; private set; }
        /// <summary>The directional light being aimed (null until the scene has one).</summary>
        public Light Light => _light;
        /// <summary>Times the light was turned (tests).</summary>
        public int Aims { get; private set; }

        Light _light;
        float _next, _nextFind;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("[HIS] DoorKeyLight");
            DontDestroyOnLoad(go);
            go.AddComponent<DoorKeyLight>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + CheckSeconds;
            var door = VirtualDoor.Current; // the door the latest intruder came to (front or second)
            if (door == null) return;
            if (_light == null && now >= _nextFind) { _nextFind = now + 2f; _light = FindKeyLight(); }
            if (_light != null) { AimAt(door); Fill(door); }
        }

        /// <summary>
        /// Every other door gets its own key light (VirtualDoor.SetFill) from where this light would come for it, at the light's
        /// colour and strength; the aimed-at door none. Two doors on facing walls can't both be lit by one directional light
        /// (Brett 0.1.38: "the second door turned grey" when the police came to the front door).
        /// </summary>
        void Fill(VirtualDoor aimed)
        {
            var t = _light.transform;
            float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(-t.forward.y, -1f, 1f)) * Mathf.Rad2Deg, MinPitch, MaxPitch);
            var c = (QualitySettings.activeColorSpace == ColorSpace.Linear ? _light.color.linear : _light.color) * _light.intensity;
            foreach (var d in VirtualDoor.All)
            {
                if (d == null) continue;
                var toward = -(Aim(d.Inward, d.HingeToLatch, pitch) * Vector3.forward);
                d.SetFill(d == aimed ? Color.black : c, toward);
            }
        }

        /// <summary>Turn the light for this door now (Update does it within <see cref="CheckSeconds"/>). True if it moved.</summary>
        public bool AimAt(VirtualDoor door)
        {
            if (door == null || _light == null) return false;
            var t = _light.transform;
            float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(-t.forward.y, -1f, 1f)) * Mathf.Rad2Deg, MinPitch, MaxPitch);
            var want = Aim(door.Inward, door.HingeToLatch, pitch);
            if (Quaternion.Angle(t.rotation, want) < 0.5f) return false;
            t.rotation = want;
            Aims++;
            return true;
        }

        /// <summary>
        /// The light's rotation for a door (pure; the tests call it): it travels toward the door from the room side, turned
        /// <see cref="SideAngle"/>° toward the latch, <paramref name="pitchDeg"/> below the horizon.
        /// </summary>
        public static Quaternion Aim(Vector3 inward, Vector3 hingeToLatch, float pitchDeg)
        {
            inward.y = 0f; hingeToLatch.y = 0f;
            inward = inward.sqrMagnitude > 1e-6f ? inward.normalized : Vector3.forward;
            hingeToLatch = hingeToLatch.sqrMagnitude > 1e-6f ? hingeToLatch.normalized : Vector3.Cross(Vector3.up, inward);
            float side = SideAngle * Mathf.Deg2Rad, pitch = pitchDeg * Mathf.Deg2Rad;
            var towardLight = (inward * Mathf.Cos(side) + hingeToLatch * Mathf.Sin(side)).normalized; // horizontal
            var travel = -towardLight * Mathf.Cos(pitch) + Vector3.down * Mathf.Sin(pitch);
            return Quaternion.LookRotation(travel, Vector3.up);
        }

        static Light FindKeyLight()
        {
            var sun = RenderSettings.sun;
            if (sun != null && sun.type == LightType.Directional) return sun;
            Light best = null;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) // (only until one is found; not per frame)
                if (l.type == LightType.Directional && l.isActiveAndEnabled && (best == null || l.intensity > best.intensity)) best = l;
            return best;
        }
    }
}
