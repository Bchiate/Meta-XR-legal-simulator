using System;
using System.Collections.Generic;
using HIS.Room;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Security
{
    /// <summary>
    /// One door's security camera (front: a doorbell cam beside the door; side: an eave cam over it — <see cref="PorchSet"/>).
    /// A plain non-XR URP camera rendering ONLY layer 11 "Outside" (its porch set, and the intruder while he's outside:
    /// Intruder keeps his renderers on layer 11 behind his door) into a small RenderTexture (<see cref="FeedWidth"/>×
    /// <see cref="FeedHeight"/>, mipmapped + trilinear so it doesn't sparkle from across the room, no MSAA/shadows/post).
    /// When it renders (every extra URP camera costs a whole culling + submit on the CPU — research/21 §1):
    ///  • only while the monitor can be seen (<see cref="TargetFps"/> &gt; 0, set by the monitor), and
    ///  • only while the picture can change: someone in view, 1 s after he leaves, 3 s after one dies in view, once on
    ///    start, and while <see cref="KeepLive"/> says so (ExteriorDoor: the door moving, splinters in the air; PoliceCars)
    ///    — otherwise the last frame is held (the screen's grain keeps it looking live);
    ///  • at most one security camera per frame (two doors stagger), never without a real GPU (batch -nographics: a
    ///    Null-device render crashed the Editor in ParticleSystemRenderer's billboard draw).
    /// During its own render only (begin/endCameraRendering), restored exactly afterwards:
    ///  • the Depth API occlusion keywords (HARD_/SOFT_OCCLUSION, global, set by EnvironmentDepthManager) are off — the
    ///    eye-space environment depth means nothing for this camera;
    ///  • the IR illuminator: the scene's one directional light is turned to shine from the lens at whoever is in view, white,
    ///    at an intensity falling off with his distance (no URP additional light: nothing depends on per-pixel extra lights);
    ///  • its porch set's renderers are on, lit by the porch shader's own IR term;
    ///  • a man still on the porch side of the door plane is drawn even if his renderers are back on the room's layers (the
    ///    breach frame), so he never pops off the feed mid-kick.
    /// Smart IR (research/21 §3.2): the LED power follows the nearest man's distance with a ~0.45 s lag, so a face stepping up
    /// to the lens blooms white, then exposes. Smart zoom (§2.2): <see cref="ZoomRect"/> eases in on a man in view so he fills
    /// ~60% of the picture's height, and back out when he's gone (the monitor's shader crops; the camera never moves).
    /// Motion: every 0.25 s, any live intruder (not Gone/Dead) outside the door and inside the view frustum; one newly in view
    /// raises <see cref="Motion"/> (debounced <see cref="MotionDebounce"/> s).
    /// </summary>
    [DisallowMultipleComponent]
    public class SecurityCamera : MonoBehaviour
    {
        public const int FeedWidth = 512, FeedHeight = 288;
        public const float Fps = 12f;
        public const float MotionDebounce = 5f;
        /// <summary>How far out the motion sensor reaches (the far sidewalk is ~10 m).</summary>
        public const float MotionRange = 14f;
        /// <summary>IR on the man: intensity of the directional "LED" light at the lens, and the distance it halves over.</summary>
        public const float IrGain = 2.2f, IrReach = 1.8f;
        const string HardKeyword = EnvironmentDepthManager.HardOcclusionKeyword, SoftKeyword = EnvironmentDepthManager.SoftOcclusionKeyword;

        static readonly List<SecurityCamera> s_all = new();
        public static IReadOnlyList<SecurityCamera> All => s_all;
        /// <summary>Raised for every camera's motion (the monitor listens here).</summary>
        public static event Action<SecurityCamera> AnyMotion;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_all.Clear(); AnyMotion = null; s_claimedFrame = -1; s_intruders = Array.Empty<IntruderT>(); s_nextScan = 0f;
            s_sun = null; s_nextSunFind = 0f;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera; // Play Mode without a domain reload
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            s_hooked = false;
        }

        public static SecurityCamera For(VirtualDoor d)
        {
            if (d == null) return null;
            foreach (var c in s_all) if (c != null && c.Door == d) return c;
            return null;
        }

        public VirtualDoor Door { get; private set; }
        public PorchSet Porch { get; private set; }
        public Camera Camera => _cam;
        public RenderTexture Feed => _rt;
        public string Label => Door != null ? Door.Label : "CAMERA";
        /// <summary>This camera's motion (an intruder came into view), at most once per <see cref="MotionDebounce"/> s.</summary>
        public event Action<SecurityCamera> Motion;
        public bool IntruderInView { get; private set; }
        public float LastMotionTime { get; private set; } = -100f;
        /// <summary>Who walked into view for the last <see cref="Motion"/> (null for a simulated one).</summary>
        public IntruderT LastMotionBy { get; private set; }
        public int FramesRendered { get; private set; }
        /// <summary>Frames per second wanted while the monitor can be seen (0 = it can't: no renders). Set by the monitor.</summary>
        public float TargetFps { get; set; }
        /// <summary>The picture can change (someone in view or just gone, or the first frame): renders are allowed.</summary>
        public bool Live => _refresh || Time.unscaledTime < _renderUntil;
        /// <summary>Crop of the feed the monitor shows: xy = lower-left corner, zw = size (0..1; (0,0,1,1) = the whole picture).</summary>
        public Vector4 ZoomRect => _zoom;
        /// <summary>Smart-IR LED power, 1 = full (dims for a face close to the lens, with a lag).</summary>
        public float IrPower => _irPower;
        /// <summary>False under -nographics (GraphicsDeviceType.Null): nothing is ever rendered then.</summary>
        public static bool CanRender => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        // Test hooks: what the last render saw of the occlusion keywords.
        public bool LastRenderOcclusionOff { get; private set; }

        Camera _cam;
        RenderTexture _rt;
        float _nextRender, _renderUntil;
        bool _refresh = true;
        bool _inRender, _hard, _soft;
        readonly HashSet<IntruderT> _inView = new();
        readonly HashSet<IntruderT> _nowIn = new();
        readonly Plane[] _planes = new Plane[6];
        float _nextMotionCheck;
        Transform _target;          // the nearest live man in view (IR aim, zoom)
        float _targetDist = 99f;
        float _irPower = 1f;
        Vector4 _zoom = new(0f, 0f, 1f, 1f);
        // The directional light while it's the IR illuminator.
        Light _sunUsed;
        Quaternion _sunRot;
        float _sunIntensity;
        Color _sunColor;

        static int s_claimedFrame = -1;
        static IntruderT[] s_intruders = Array.Empty<IntruderT>();
        static float s_nextScan;
        static bool s_hooked;
        static Light s_sun;
        static float s_nextSunFind;

        /// <summary>Creates the camera for <paramref name="door"/>, mounted where <paramref name="porch"/> says.</summary>
        public static SecurityCamera Build(VirtualDoor door, PorchSet porch, Transform parent)
        {
            var go = new GameObject($"[HIS] SecurityCam ({door.Label})");
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<SecurityCamera>();
            c.Init(door, porch);
            return c;
        }

        void Init(VirtualDoor door, PorchSet porch)
        {
            Door = door; Porch = porch;
            s_all.Add(this);
            Hook();
            HIS.Core.GameSession.WorldCleared += Refresh;     // a cleared room: the empty porch the next time the screen is seen

            // Mipmapped + trilinear: at 3 m the 512-px picture is squeezed ~4:1 on the eye buffer (research/21 §2.4).
            _rt = new RenderTexture(FeedWidth, FeedHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = $"SecurityFeed {door.Label}", antiAliasing = 1, useMipMap = true, autoGenerateMips = true,
                filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp,
            };
            if (CanRender)
            {
                _rt.Create();
                var prev = RenderTexture.active; RenderTexture.active = _rt; GL.Clear(true, true, Color.black); RenderTexture.active = prev;
            }

            _cam = gameObject.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.008f, 0.008f, 0.01f, 1f);
            _cam.cullingMask = 1 << PorchSet.OutsideLayer; // the porch + whoever is outside; never room particles/decals
            DoorwayView.UseFeedRenderer(_cam);              // (the headset's renderer draws layer 11 only through open doorways)
            _cam.fieldOfView = porch.VerticalFov;
            _cam.nearClipPlane = PorchSet.NearClip;
            _cam.farClipPlane = PorchSet.FarClip;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cam.depth = -50f; // before the headset camera
            var data = _cam.GetUniversalAdditionalCameraData();
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.allowXRRendering = false;
            data.stopNaN = false;
            data.dithering = false;

            Place();
            _nextRender = Time.unscaledTime + 0.05f * s_all.Count;
            Debug.Log($"[HIS] SecurityCamera {door.Label}: {(porch.DoorbellCam ? "doorbell" : "eave")} cam {FeedWidth}x{FeedHeight} @ ≤{Fps} fps, " +
                      $"vFOV {_cam.fieldOfView:F0}° (h {porch.HorizontalFov}°), at {transform.position:F2}");
        }

        void Place()
        {
            if (Porch == null) return;
            transform.SetPositionAndRotation(Porch.CameraPosition, Porch.CameraRotation);
        }

        void OnDestroy()
        {
            s_all.Remove(this);
            HIS.Core.GameSession.WorldCleared -= Refresh;
            if (_inRender) EndRender();
            ReturnBorrowed();
            if (_cam != null) _cam.targetTexture = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
        }

        static readonly int FeedViewId = Shader.PropertyToID("_HIS_FeedView");

        static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            foreach (var c in s_all) if (c != null && c._cam == cam) { c.BeginRender(); return; }
        }

        static void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            foreach (var c in s_all) if (c != null && c._cam == cam) { c.EndRender(); return; }
        }

        /// <summary>The scene's one directional light (DoorKeyLight's), borrowed as the IR illuminator during our renders.</summary>
        static Light Sun()
        {
            if (s_sun != null && s_sun.isActiveAndEnabled) return s_sun;
            if (Time.unscaledTime < s_nextSunFind) return null;
            s_nextSunFind = Time.unscaledTime + 2f;
            s_sun = RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional && RenderSettings.sun.isActiveAndEnabled ? RenderSettings.sun : null;
            if (s_sun == null)
                foreach (var l in FindObjectsByType<Light>())
                    if (l.type == LightType.Directional && l.isActiveAndEnabled && (s_sun == null || l.intensity > s_sun.intensity)) s_sun = l;
            return s_sun;
        }

        void BeginRender()
        {
            if (_inRender) return;
            _inRender = true;
            _hard = Shader.IsKeywordEnabled(HardKeyword);
            _soft = Shader.IsKeywordEnabled(SoftKeyword);
            if (_hard) Shader.DisableKeyword(HardKeyword);
            if (_soft) Shader.DisableKeyword(SoftKeyword);
            LastRenderOcclusionOff = !Shader.IsKeywordEnabled(HardKeyword) && !Shader.IsKeywordEnabled(SoftKeyword);
            var sun = Sun();
            if (sun != null)
            {
                _sunUsed = sun;
                _sunRot = sun.transform.rotation; _sunIntensity = sun.intensity; _sunColor = sun.color;
                var dir = _target != null ? _target.position + Vector3.up * 1.1f - transform.position : transform.forward;
                if (dir.sqrMagnitude < 1e-4f || Mathf.Abs(dir.normalized.y) > 0.999f) dir = transform.forward;
                sun.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                float d = _target != null ? _targetDist : 3f;
                sun.intensity = Mathf.Max(0.06f, IrGain * _irPower / (1f + d * d / (IrReach * IrReach)));
                sun.color = Color.white;
            }
            if (Porch != null) { Porch.PushLighting(_irPower); Porch.SetLive(true); }
            Shader.SetGlobalFloat(FeedViewId, 1f); // HIS/SecurityPorch: the IR night-vision look (the headset's doorway view: colour)
            BorrowMenOutside();
        }

        // A man still on the porch side of the door plane whose renderers went back to the room's layers (the breach: he's
        // visible to the headset from that frame on) would vanish from the feed mid-kick. Put them on layer 11 for this render
        // only, and back exactly afterwards (the headset camera renders at another time).
        readonly List<(Renderer r, int layer)> _borrowed = new();
        static readonly List<Renderer> s_rbuf = new();

        void BorrowMenOutside()
        {
            _borrowed.Clear();
            if (Door == null) return;
            var door = Door.Center; var outward = Door.Outward;
            foreach (var i in s_intruders)
            {
                if (i == null || !i.isActiveAndEnabled || i.Current == IntruderT.State.Gone) continue;
                if (Vector3.Dot(i.transform.position - door, outward) < 0.02f) continue;
                i.GetComponentsInChildren(false, s_rbuf);
                foreach (var r in s_rbuf)
                {
                    if (r == null || !r.enabled || r.gameObject.layer == PorchSet.OutsideLayer) continue;
                    _borrowed.Add((r, r.gameObject.layer));
                    r.gameObject.layer = PorchSet.OutsideLayer;
                }
                s_rbuf.Clear();
            }
        }

        void ReturnBorrowed()
        {
            foreach (var (r, layer) in _borrowed) if (r != null) r.gameObject.layer = layer;
            _borrowed.Clear();
        }

        void EndRender()
        {
            if (!_inRender) return;
            _inRender = false;
            if (_hard) Shader.EnableKeyword(HardKeyword); else Shader.DisableKeyword(HardKeyword);
            if (_soft) Shader.EnableKeyword(SoftKeyword); else Shader.DisableKeyword(SoftKeyword);
            if (_sunUsed != null)
            {
                _sunUsed.transform.rotation = _sunRot; _sunUsed.intensity = _sunIntensity; _sunUsed.color = _sunColor;
                _sunUsed = null;
            }
            if (Porch != null) Porch.SetLive(false);
            Shader.SetGlobalFloat(FeedViewId, 0f);
            ReturnBorrowed();
            FramesRendered++;
            _refresh = false;
        }

        void Update()
        {
            if (Door == null || Porch == null) { Destroy(gameObject); return; }
            Place();
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            if (now >= _nextMotionCheck)
            {
                _nextMotionCheck = now + 0.25f;
                CheckMotion(now);
            }
            // Smart IR: the LEDs dim for a man close to the lens, with a lag (he blooms white stepping up, then exposes).
            float want = _target != null ? Mathf.Clamp(_targetDist * _targetDist / (1.5f * 1.5f), 0.2f, 1f) : 1f;
            _irPower = Mathf.Lerp(_irPower, want, 1f - Mathf.Exp(-dt / 0.45f));
            UpdateZoom(dt);
        }

        void LateUpdate()
        {
            if (_cam == null) return;
            if (_inRender) EndRender(); // a render that never finished (no SRP callback): never leave the keywords off
            float now = Time.unscaledTime;
            if (TargetFps <= 0f || !CanRender || !Live) { _nextRender = now; _cam.enabled = false; return; } // hold the last frame
            float interval = 1f / Mathf.Max(0.5f, TargetFps);
            bool render = now >= _nextRender && s_claimedFrame != Time.frameCount;
            if (render)
            {
                s_claimedFrame = Time.frameCount;
                _nextRender += interval;
                if (now - _nextRender > interval) _nextRender = now + interval;
            }
            _cam.enabled = render;
        }

        /// <summary>Renders one frame now (tests; batch mode draws no game view). Same callbacks as a normal frame.</summary>
        public void RenderNow()
        {
            if (_cam == null || !CanRender) return;
            bool was = _cam.enabled;
            _cam.enabled = false;
            _cam.Render();
            _cam.enabled = was;
        }

        // ───────────────────────────── smart zoom ─────────────────────────────

        void UpdateZoom(float dt)
        {
            var want = new Vector4(0f, 0f, 1f, 1f);
            if (_target != null && _cam != null)
            {
                var feet = _cam.WorldToViewportPoint(_target.position);
                var head = _cam.WorldToViewportPoint(_target.position + Vector3.up * 1.8f);
                if (feet.z > 0f && head.z > 0f)
                {
                    float h = Mathf.Abs(head.y - feet.y);
                    float size = Mathf.Clamp(h / 0.6f, 0.4f, 1f);             // up to 2.5×, him ~60% of the height
                    var centre = new Vector2((feet.x + head.x) * 0.5f, (feet.y + head.y) * 0.5f);
                    float x = Mathf.Clamp(centre.x - size * 0.5f, 0f, 1f - size), y = Mathf.Clamp(centre.y - size * 0.5f, 0f, 1f - size);
                    want = new Vector4(x, y, size, size);
                }
            }
            _zoom = Vector4.Lerp(_zoom, want, 1f - Mathf.Exp(-dt / 0.35f));
        }

        // ───────────────────────────── motion ─────────────────────────────

        void CheckMotion(float now)
        {
            if (now >= s_nextScan)
            {
                s_nextScan = now + 0.25f;
                s_intruders = FindObjectsByType<IntruderT>();
            }
            GeometryUtility.CalculateFrustumPlanes(_cam, _planes);
            var camPos = transform.position;
            var door = Door.Center; var outward = Door.Outward;
            _nowIn.Clear();
            bool fresh = false, justDied = false;
            IntruderT freshMan = null;
            Transform nearest = null; float nearestD = 99f;
            foreach (var i in s_intruders)
            {
                if (i == null || !i.isActiveAndEnabled || i.Current == IntruderT.State.Gone) continue;
                // His spawn frame at the step, hidden (not there yet): a hitman, or a Marshal night's visitor about to be put at the
                // street end of the path. (The police as before.)
                if (i.PlacementPending && (i.IsHitman || i.Intent != HIS.Scenario.Intent.Police)) continue;
                var p = i.transform.position;
                if (Vector3.Dot(p - door, outward) < 0.05f) continue;            // in the room (or in the doorway)
                var flat = p - camPos; flat.y = 0f;
                if (flat.magnitude > MotionRange) continue;
                if (!PorchSet.InFrustum(_planes, p + Vector3.up * 0.15f, 0.1f) && !PorchSet.InFrustum(_planes, p + Vector3.up * 0.95f, 0.15f)
                    && !PorchSet.InFrustum(_planes, p + Vector3.up * 1.6f, 0.1f)) continue;
                if (i.Current == IntruderT.State.Dead) { if (_inView.Contains(i)) justDied = true; continue; }
                _nowIn.Add(i);
                if (!_inView.Contains(i)) { fresh = true; freshMan ??= i; }
                float d = Vector3.Distance(p + Vector3.up * 1.1f, camPos);
                if (d < nearestD) { nearestD = d; nearest = i.transform; }
            }
            // Someone the picture showed is out of it now (off its edge, gone for good, removed): one fresh frame the next time
            // the screen is seen, so the held picture never keeps him standing there (0.1.56: "he just stays glitching there").
            foreach (var i in _inView) if (i == null || !_nowIn.Contains(i)) { _refresh = true; break; }
            _inView.Clear();
            foreach (var i in _nowIn) _inView.Add(i);
            IntruderInView = _nowIn.Count > 0;
            _target = nearest; _targetDist = nearestD;
            if (IntruderInView) _renderUntil = Mathf.Max(_renderUntil, now + 1f);   // live while he's there, and 1 s after
            if (justDied) _renderUntil = Mathf.Max(_renderUntil, now + 3f);         // his fall
            if (fresh && now - LastMotionTime >= MotionDebounce) RaiseMotion(now, freshMan);
        }

        void RaiseMotion(float now, IntruderT who)
        {
            LastMotionTime = now;
            LastMotionBy = who;
            LastAlert = Alert.Motion;
            Debug.Log($"[HIS] SecurityCamera {Label}: motion");
            Motion?.Invoke(this);
            AnyMotion?.Invoke(this);
        }

        /// <summary>What the last alert was: a man walking into view, or a vehicle pulling into the picture.</summary>
        public enum Alert { Motion, Vehicle }
        /// <summary>The kind of this camera's last <see cref="Motion"/> (the monitor's label: MOTION or VEHICLE).</summary>
        public Alert LastAlert { get; private set; } = Alert.Motion;

        /// <summary>
        /// A vehicle has just pulled into this camera's picture (a Marshal night's hitman car, PoliceCars.Arrival; Brett 0.1.56:
        /// "the bad guys seemingly come from no where"): the same alert as motion — the monitor's chime and flashing label, which
        /// reads VEHICLE — without the men's debounce (<see cref="LastMotionTime"/> is theirs: the men getting out of it a few
        /// seconds later still raise their own MOTION).
        /// </summary>
        public void VehicleInView()
        {
            LastAlert = Alert.Vehicle;
            _renderUntil = Mathf.Max(_renderUntil, Time.unscaledTime + 1f);
            Debug.Log($"[HIS] SecurityCamera {Label}: vehicle");
            Motion?.Invoke(this);
            AnyMotion?.Invoke(this);
        }

        /// <summary>Test hook: motion on this camera now, as if someone just walked into view.</summary>
        public void SimulateMotion() => RaiseMotion(Time.unscaledTime, null);

        /// <summary>Render the next chance we get even if nobody's in view (the picture changed some other way).</summary>
        public void Refresh() => _refresh = true;

        /// <summary>
        /// Something else outside is moving for the next <paramref name="seconds"/> (the door swinging, splinters in the air,
        /// police cars pulling up): renders stay allowed that long (still only while the monitor can be seen).
        /// </summary>
        public void KeepLive(float seconds) => _renderUntil = Mathf.Max(_renderUntil, Time.unscaledTime + seconds);

        /// <summary>Test hook: is this world point in the camera's view (same test the motion sensor uses)?</summary>
        public bool Sees(Vector3 p)
        {
            GeometryUtility.CalculateFrustumPlanes(_cam, _planes);
            return PorchSet.InFrustum(_planes, p, 0f);
        }
    }
}
