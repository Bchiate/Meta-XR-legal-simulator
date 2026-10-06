using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Runtime guarantees for a passthrough-only app: passthrough on, camera transparent,
    /// and a log line we can grep in logcat to confirm the rig came up.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class MRBootstrap : MonoBehaviour
    {
        public const string Version = "0.1.0";

        void Awake()
        {
            Application.targetFrameRate = 72;
            // Physics at the display rate: knife/hand velocities are sampled per physics step (50 Hz vs 72 Hz made them jitter ±40%).
            Time.fixedDeltaTime = 1f / 72f;
#if UNITY_EDITOR
            Application.runInBackground = true; // Editor Play Mode is driven from the background (menu bar + Operator MCP)
#endif
    #if UNITY_EDITOR
            // HIS > Editor Room > Brett's Room: skip the simulator's synthetic room and load the captured real room.
            // Runs before MRUK.Awake (execution order -1000), which is where MRUK starts loading.
            if (UnityEditor.EditorPrefs.GetBool("HIS.ForceBrettRoom", false))
            {
                var mruk = FindAnyObjectByType<Meta.XR.MRUtilityKit.MRUK>();
                if (mruk != null && mruk.SceneSettings.SceneJsons is { Length: > 0 })
                {
                    mruk.SceneSettings.DataSource = Meta.XR.MRUtilityKit.MRUK.SceneDataSource.Json;
                    Debug.Log("[HIS] Editor room: Brett's room JSON");
                }
            }
#endif
            var mgr = OVRManager.instance ?? FindAnyObjectByType<OVRManager>();
            if (mgr != null)
            {
                mgr.isInsightPassthroughEnabled = true;
                mgr.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            }
            else Debug.LogError("[HIS] No OVRManager in scene");

            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            Debug.Log($"[HIS] MRBootstrap v{Version} — passthrough requested, tracking origin FloorLevel");
        }

        void Start()
        {
            Debug.Log($"[HIS] Passthrough initialized: {OVRManager.IsInsightPassthroughInitialized()} | supported: {OVRManager.IsInsightPassthroughSupported()}");
        }

        // ── Depth coverage check (once when depth arrives, again after every resume) ──
        // Does the eye's view reach past the environment-depth frame? There, Meta's occlusion read clamped/wrapped depth
        // (HISDepthOcclusion.hlsl now skips it). Logs where the left eye's view edges land in the depth frame's uv: inside
        // 0..1 = covered. The answer to Brett's "translucent objects in the corner of my vision" (0.1.41).
        static readonly int ReprojectionId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
        static readonly Vector2[] s_edges = { new(0f, 0f), new(0.5f, 0f), new(1f, 0f), new(0f, 0.5f), new(1f, 0.5f), new(0f, 1f), new(0.5f, 1f), new(1f, 1f) };
        Meta.XR.EnvironmentDepth.EnvironmentDepthManager _depth;
        bool _depthChecked;
        float _depthCheckAt;

        void OnApplicationPause(bool paused) { if (!paused) { _depthChecked = false; _depthCheckAt = Time.unscaledTime + 3f; } }

        void Update()
        {
            if (_depthChecked || Application.isEditor || Time.unscaledTime < _depthCheckAt) return;
            if (_depth == null && (_depth = FindAnyObjectByType<Meta.XR.EnvironmentDepth.EnvironmentDepthManager>()) == null) { _depthChecked = true; return; }
            if (!_depth.IsDepthAvailable) return;
            var cam = Camera.main;
            if (cam == null || !cam.stereoEnabled) return;
            var m = Shader.GetGlobalMatrixArray(ReprojectionId);
            if (m == null || m.Length == 0) return;
            _depthChecked = true;
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            int behind = 0;
            foreach (var e in s_edges)
            {
                var ray = cam.ViewportPointToRay(new Vector3(e.x, e.y, 0f), Camera.MonoOrStereoscopicEye.Left);
                var p = ray.GetPoint(2f);
                var c = m[0] * new Vector4(p.x, p.y, p.z, 1f);
                if (c.w <= 0f) { behind++; continue; }
                float u = (c.x / c.w + 1f) * 0.5f, v = (c.y / c.w + 1f) * 0.5f;
                uMin = Mathf.Min(uMin, u); uMax = Mathf.Max(uMax, u); vMin = Mathf.Min(vMin, v); vMax = Mathf.Max(vMax, v);
            }
            bool covered = behind == 0 && uMin >= 0f && uMax <= 1f && vMin >= 0f && vMax <= 1f;
            Debug.Log($"[HIS] Depth coverage: left eye's view edges at depth uv u {uMin:F2}..{uMax:F2}, v {vMin:F2}..{vMax:F2}" +
                      $"{(behind > 0 ? $", {behind} behind the depth camera" : "")} — {(covered ? "all inside the depth frame" : "the view reaches PAST the depth frame (edges unoccluded now)")}");
        }
    }
}
