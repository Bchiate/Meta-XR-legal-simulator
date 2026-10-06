using System.Collections.Generic;
using HIS.Room;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// Self-starting (no scene object, like DoorKeyLight): builds a porch set + security camera for every door rig as it is
    /// built (<see cref="VirtualDoor.Built"/>, and any already in <see cref="VirtualDoor.All"/>), drops the ones whose door is
    /// gone or was rebuilt, keeps the one wall monitor by the front door, and keeps the "Outside" layer (11) off the headset
    /// camera(s) — only security cameras draw it.
    /// </summary>
    public class SecuritySystem : MonoBehaviour
    {
        public static SecuritySystem Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            StripOutsideLayer();
            if (Instance != null) return;
            var go = new GameObject("[HIS] Security");
            DontDestroyOnLoad(go);
            go.AddComponent<SecuritySystem>();
        }

        float _nextStrip;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            VirtualDoor.Built += OnBuilt;
            gameObject.AddComponent<PoliceCars>(); // bad call → cruisers pull up on the front camera
        }

        void Start()
        {
            foreach (var d in new List<VirtualDoor>(VirtualDoor.All)) if (d != null && SecurityCamera.For(d) == null) OnBuilt(d);
            EnsureMonitor();
        }

        void OnDestroy()
        {
            VirtualDoor.Built -= OnBuilt;
            if (Instance == this) Instance = null;
        }

        void OnBuilt(VirtualDoor d)
        {
            // Raised from inside VirtualDoor.Build (RoomScanner's door rig): never let a failure here break the door.
            try
            {
                if (d == null) return;
                foreach (var p in new List<PorchSet>(PorchSet.All))
                {
                    if (p == null) continue;
                    if (p.Door == null || p.Door == d || p.Door.Index == d.Index)
                    {
                        var cam = p.Door != null ? SecurityCamera.For(p.Door) : null;
                        if (cam != null) Destroy(cam.gameObject);
                        Destroy(p.gameObject);
                    }
                }
                foreach (var c in new List<SecurityCamera>(SecurityCamera.All))
                    if (c != null && (c.Door == null || c.Door == d)) Destroy(c.gameObject);
                var porch = PorchSet.Build(d, transform);
                SecurityCamera.Build(d, porch, transform);
                try { ExteriorDoor.Build(d, porch); } // the door's outside for the feed: never let it cost the camera
                catch (System.Exception e) { Debug.LogError($"[HIS] SecuritySystem: door {d.Label} exterior failed: {e}"); }
                EnsureMonitor();
                StripOutsideLayer();
            }
            catch (System.Exception e) { Debug.LogError($"[HIS] SecuritySystem: door {d?.Label} rig failed: {e}"); }
        }

        void EnsureMonitor()
        {
            if (SecurityMonitor.Instance != null) return;
            var go = new GameObject("[HIS] SecurityMonitor");
            go.transform.SetParent(transform, false);
            go.AddComponent<SecurityMonitor>();
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (now < _nextStrip) return;
            _nextStrip = now + 1f;
            StripOutsideLayer();
        }

        static Camera[] s_cams = new Camera[8];

        /// <summary>
        /// Only security cameras render layer 11 everywhere: take it off the headset camera and every other on-screen camera —
        /// unless the renderer draws it only through open doorways (<see cref="DoorwayView"/>): then the headset keeps it.
        /// </summary>
        public static void StripOutsideLayer()
        {
            const int bit = 1 << PorchSet.OutsideLayer;
            if (DoorwayView.Available) { DoorwayView.KeepHeadsetMask(); return; }
            var main = Camera.main;
            if (main != null && (main.cullingMask & bit) != 0) main.cullingMask &= ~bit;
            if (s_cams.Length < Camera.allCamerasCount) s_cams = new Camera[Camera.allCamerasCount + 4];
            int n = Camera.GetAllCameras(s_cams);
            for (int i = 0; i < n; i++)
            {
                var c = s_cams[i];
                if (c == null || c.targetTexture != null || (c.cullingMask & bit) == 0 || c.GetComponent<SecurityCamera>() != null) continue;
                c.cullingMask &= ~bit;
            }
            System.Array.Clear(s_cams, 0, n);
        }
    }
}
