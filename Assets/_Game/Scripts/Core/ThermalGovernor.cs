using HIS.Intruder;
using HIS.Outcome;
using HIS.Scenario;
using HIS.UI;
using Meta.XR.EnvironmentDepth;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings;
using Perf = OVRManager.ProcessorPerformanceLevel;

namespace HIS.Core
{
    /// <summary>
    /// Keeps a long session cool (Brett: "never too much for a long session"). Two jobs:
    /// <list type="bullet">
    /// <item>CPU/GPU level hints by phase: SustainedLow everywhere (0.1.15 sent no hint and the OS ran fights at levels 2/2,
    /// inside Low's CPU 2–4 / GPU 1–4 range, so the OS can still ramp up) except SustainedHigh while the room first
    /// loads; never Boost.</item>
    /// <item>Heat tiers (<see cref="ThermalTiers"/>) from the OS thermal status/headroom, battery temperature, XR thermal
    /// notifications and a sustained frame-rate drop, polled every <see cref="PollSeconds"/>: Warm/Hot turn down blood,
    /// sprays, confetti, bodies, foveation and occlusion quality (<see cref="PerfBudget"/>), and Hot asks for a short break
    /// between rounds. Everything goes back when it cools.</item>
    /// </list>
    /// Never the eye-buffer scale or MSAA (0.1.51): changing either rebuilds the eye swapchains, and 0.1.50's Warm switch freed
    /// an eye image the GPU was still drawing into (a kernel GPU page fault). They stay as the render asset ships them.
    /// Visible changes (occlusion, the room wireframe) wait for a gap between rounds, never mid-fight.
    /// Self-starting (no scene object needed). Logs "[HIS] Thermal: ..." on every change and every <see cref="LogSeconds"/>.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class ThermalGovernor : MonoBehaviour
    {
        public enum Phase { Loading, Idle, Live }

        public static ThermalGovernor Instance { get; private set; }
        /// <summary>Tests / tools: read heat from this instead of the device (set before the governor starts, or call <see cref="Rebind"/>).</summary>
        public static IThermalSource OverrideSource;
        public const string NoticeKey = "thermal";
        public const string BreakMessage = "<b>Headset is getting warm</b>\n<size=70%>Take a short break (Menu pauses)</size>";

        public float PollSeconds = 5f, LogSeconds = 30f, LoadingHighSeconds = 60f;

        public ThermalTier Tier => _tiers.Tier;
        public Phase CurrentPhase { get; private set; } = Phase.Loading;
        public ThermalSample Last { get; private set; } = ThermalSample.Unknown;
        public ThermalTiers Tiers => _tiers;
        public static string HudLine => Instance != null ? Instance._hud : "";

        readonly ThermalTiers _tiers = new();
        IThermalSource _source;
        float _boot, _phaseSince, _nextPhase, _nextPoll, _nextLog, _fpsTime, _nextFind;
        int _fpsFrames;
        volatile int _xrCpu, _xrGpu, _xrDirty;
        Perf? _hint;
        bool _visualsDirty, _foveationSet, _paused;
        bool? _fovDynamic; // what we last sent: MetaXRFoveationFeature's dynamic getter really returns level != 0
        ScenarioRecord _showSeenFor; // the round whose verdict show has started (no break notice before it)
        float _gpuUtil = float.NaN;
        OcclusionShadersMode? _baseOcclusion;
        IntruderSpawner _spawner;
        EnvironmentDepthManager _depth;
        EffectMesh _wireframe;
        string _hud = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("[HIS] ThermalGovernor");
            DontDestroyOnLoad(go);
            go.AddComponent<ThermalGovernor>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _boot = _phaseSince = Time.unscaledTime;
            PerfBudget.Apply(ThermalTier.Normal);
            Rebind();
        }

        /// <summary>Pick the heat source again (after setting <see cref="OverrideSource"/>) and start from Normal.</summary>
        public void Rebind()
        {
            (_source as System.IDisposable)?.Dispose(); // (the Android source's headroom thread)
            _source = OverrideSource ?? new AndroidThermalSource();
            _tiers.Reset();
            PerfBudget.Apply(ThermalTier.Normal);
            _nextPoll = Time.unscaledTime + 1f;
            Debug.Log($"[HIS] Thermal: governor up, source {_source.Describe}, poll {PollSeconds:0} s");
        }

        void OnEnable() => XrPerformanceSettingsFeature.OnXrPerformanceChangeNotification += OnXrPerformance;
        void OnDisable() => XrPerformanceSettingsFeature.OnXrPerformanceChangeNotification -= OnXrPerformance;

        // Headset off / app backgrounded: the frames around it aren't a fair frame-rate sample.
        void OnApplicationPause(bool paused)
        {
            _fpsTime = 0f; _fpsFrames = 0; _phaseSince = Time.unscaledTime;
            AndroidThermalSource.AppPaused = paused; // (its headroom thread skips the binder call while the headset is off)
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            (_source as System.IDisposable)?.Dispose();
            Instance = null;
            PerfBudget.Apply(ThermalTier.Normal);
        }

        // May arrive off the main thread: just record it; Update acts on it.
        void OnXrPerformance(PerformanceChangeNotification n)
        {
            if (n.subDomain != PerformanceSubDomain.Thermal) return;
            if (n.domain == PerformanceDomain.Cpu) _xrCpu = (int)n.toLevel; else _xrGpu = (int)n.toLevel;
            _xrDirty = 1;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            _fpsTime += Time.unscaledDeltaTime; _fpsFrames++;
            if (_xrDirty != 0)
            {
                _xrDirty = 0;
                Debug.Log($"[HIS] Thermal: XR notification cpu={_xrCpu} gpu={_xrGpu} (0 normal, 25 warning, 75 impaired)");
                _nextPoll = now; // act on it now, not in up to 5 s
            }
            if (now >= _nextPhase) { _nextPhase = now + 0.5f; UpdatePhase(now); }
            if (now >= _nextPoll) { _nextPoll = now + PollSeconds; Poll(now); }
        }

        // ───────────────────────────── phase → CPU/GPU hints ─────────────────────────────

        void UpdatePhase(float now)
        {
            var p = ComputePhase(now);
            bool paused = GamePause.Instance != null && GamePause.Instance.IsPaused;
            if (p != CurrentPhase || paused != _paused)
            {
                CurrentPhase = p; _phaseSince = now; _paused = paused;
                _fpsTime = 0f; _fpsFrames = 0; // a poll window spanning a phase change isn't a fair frame-rate sample
            }
            ApplyHints();
            if (_visualsDirty && p != Phase.Live) ApplyVisuals();
            UpdateNotice();
        }

        Phase ComputePhase(float now)
        {
            if (_spawner == null && now >= _nextFind) { _nextFind = now + 2f; _spawner = FindAnyObjectByType<IntruderSpawner>(); }
            bool paused = GamePause.Instance != null && GamePause.Instance.IsPaused;
            var show = OutcomeShow.Instance;
            bool showing = show != null && show.Playing;
            var dir = ScenarioDirector.Instance;
            // A Marshal night's hitman waves are the fight itself (research/27 §6 step 8): Live while they're out, lulls included,
            // so no break notice or visual change lands mid-fight. (A police standoff keeps today's phases.)
            var pr = PoliceResponse.Instance;
            bool hitmen = pr != null && pr.Active && pr.Faction == SquadFaction.Hitmen;
            bool live = !paused && !showing && dir != null && (dir.CurrentMode == ScenarioDirector.Mode.Sandbox
                ? _spawner != null && _spawner.Active != null
                : hitmen || (!dir.Previewing && dir.Intruder != null && dir.Record != null && dir.Record.EndTime <= 0f));
            if (live) return Phase.Live;
            bool loading = _spawner == null || !_spawner.NavMeshReady;
            return loading && !paused && now - _boot < LoadingHighSeconds ? Phase.Loading : Phase.Idle;
        }

        /// <summary>The hint for a phase at a tier (pure; the tests check it). Never Boost.</summary>
        public static Perf HintFor(Phase p, ThermalTier t) => p switch
        {
            Phase.Live => Perf.SustainedLow,   // 0.1.15 (no hint sent) ran fights at 2/2; Low (CPU 2–4, GPU 1–4) still lets the OS ramp
            Phase.Loading => t == ThermalTier.Hot ? Perf.SustainedLow : Perf.SustainedHigh, // room scan + NavMesh bake load faster
            _ => Perf.SustainedLow,
        };

        void ApplyHints(bool resend = false)
        {
            var want = HintFor(CurrentPhase, Tier);
            if (_hint == want && !resend) return;
            bool changed = _hint != want;
            _hint = want;
            // Resent every log period: a hint sent before the XR session was up is silently dropped by the runtime.
            if (!Application.isEditor) { OVRManager.suggestedCpuPerfLevel = want; OVRManager.suggestedGpuPerfLevel = want; }
            if (changed) Debug.Log($"[HIS] Thermal: CPU/GPU hint {want} ({CurrentPhase}, {Tier})");
        }

        // ───────────────────────────── polling → tier ─────────────────────────────

        void Poll(float now)
        {
            var s = ThermalSample.Unknown;
            _source?.Read(ref s, now);
            s.XrLevel = Mathf.Max(_xrCpu, _xrGpu);
            // Frame rate counts only on the device, in a settled phase: loading hitches, pause menus and the Meta menu
            // (focus loss pauses the game) aren't heat.
            bool fair = !Application.isEditor && CurrentPhase != Phase.Loading && !_paused && now - _phaseSince > 3f && _fpsTime > 1f;
            s.Fps = fair ? _fpsFrames / _fpsTime : float.NaN;
            _fpsTime = 0f; _fpsFrames = 0;
            Last = s;
            ReadGpuLoad();

            var before = Tier;
            if (_tiers.Update(s, now))
            {
                Debug.Log($"[HIS] Thermal: {before} -> {Tier} ({_tiers.Reason}) | {s} | {CurrentPhase}");
                ApplyTier();
            }
            if (!_foveationSet || now >= _nextLog) ApplyFoveation(); // late session start, or anything else resetting it
            if (now >= _nextLog)
            {
                if (_nextLog == 0f && !Application.isEditor)
                    Debug.Log($"[HIS] Thermal: XR_EXT_performance_settings {(UnityEngine.XR.OpenXR.OpenXRRuntime.IsExtensionEnabled(XrPerformanceSettingsFeature.extensionString) ? "on" : "OFF (hints and thermal notifications won't reach the runtime)")}");
                _nextLog = now + LogSeconds;
                ApplyHints(resend: true);
                Debug.Log($"[HIS] Thermal: {Tier} | {s} | gpu={(float.IsNaN(_gpuUtil) ? "?" : _gpuUtil.ToString("0.00"))} hint={_hint} " +
                          $"fov={(Application.isEditor ? "editor" : OVRManager.foveatedRenderingLevel.ToString())} scale={CurrentScale()} | {CurrentPhase}");
            }
            _hud = $"{Tier.ToString().ToUpperInvariant()} {(float.IsNaN(s.BatteryC) ? "" : $"{s.BatteryC:0}C ")}" +
                   $"{(float.IsNaN(s.Headroom) ? "" : $"hr{s.Headroom:0.00} ")}{(s.Plugged ? "USB " : "")}{(_hint == Perf.SustainedHigh ? "hi" : "lo")}";
        }

        void ReadGpuLoad()
        {
            if (Application.isEditor) return;
            float? g = null;
            try { g = OVRPlugin.GetPerfMetricsFloat(OVRPlugin.PerfMetrics.System_GpuUtilPercentage_Float); } catch { }
            _gpuUtil = g is float v && v >= 0f ? (v > 1.5f ? v / 100f : v) : float.NaN; // (logged with the 30 s line)
        }

        void ApplyTier()
        {
            PerfBudget.Apply(Tier);
            ApplyHints();
            ApplyFoveation();
            _visualsDirty = true;
            if (CurrentPhase != Phase.Live) ApplyVisuals();
            UpdateNotice();
        }

        // ───────────────────────────── actuators ─────────────────────────────

        void ApplyFoveation()
        {
            if (Application.isEditor) return;
            try
            {
                if (!OVRManager.fixedFoveatedRenderingSupported) return; // session not up yet: retried next poll
                var lv = PerfBudget.Current;
                // Not compared through OVRManager.useDynamicFoveatedRendering: in v205 its OpenXR getter reads the *level*
                // (true whenever foveation is on), so "dynamic" was never sent. Dynamic first: each setter sends both values.
                if (OVRManager.foveatedRenderingLevel != lv.Foveation || _fovDynamic != lv.DynamicFoveation)
                {
                    OVRManager.useDynamicFoveatedRendering = lv.DynamicFoveation;
                    OVRManager.foveatedRenderingLevel = lv.Foveation;
                    _fovDynamic = lv.DynamicFoveation;
                }
                if (!_foveationSet) Debug.Log($"[HIS] Thermal: foveation {lv.Foveation}{(lv.DynamicFoveation ? " (dynamic)" : "")}");
                _foveationSet = true;
            }
            catch (System.Exception e) { _foveationSet = true; Debug.LogWarning($"[HIS] Thermal: foveation unavailable: {e.Message}"); }
        }

        string CurrentScale() => UniversalRenderPipeline.asset is { } a ? $"{a.renderScale:0.00}/msaa{a.msaaSampleCount}" : "?";

        /// <summary>Occlusion mode and room wireframe: only between rounds. (Never the eye-buffer scale or MSAA: see the class notes.)</summary>
        void ApplyVisuals()
        {
            _visualsDirty = false;
            var lv = PerfBudget.Current;
            if (_depth == null) _depth = FindAnyObjectByType<EnvironmentDepthManager>();
            if (_depth != null)
            {
                _baseOcclusion ??= _depth.OcclusionShadersMode;
                var want = lv.HardOcclusion && _baseOcclusion == OcclusionShadersMode.SoftOcclusion ? OcclusionShadersMode.HardOcclusion : _baseOcclusion.Value;
                if (_depth.OcclusionShadersMode != want) { _depth.OcclusionShadersMode = want; Debug.Log($"[HIS] Thermal: occlusion {want}"); }
            }
            // Dev builds only. HideMesh, not SetActive: EffectMesh parents its meshes to the room anchors, not to itself.
            if (_wireframe == null && GameObject.Find("[HIS] RoomWireframe") is { } wf) _wireframe = wf.GetComponent<EffectMesh>();
            if (_wireframe != null && _wireframe.HideMesh == lv.Wireframe) _wireframe.HideMesh = !lv.Wireframe;
        }

        /// <summary>Hot: a gentle break notice, only while nothing is live and the verdict show is over.</summary>
        public static bool WantsBreakNotice(ThermalTier t, Phase p, bool showPlaying) => PerfBudget.For(t).BreakNotice && p != Phase.Live && !showPlaying;

        void UpdateNotice()
        {
            bool showing = OutcomeShow.Instance != null && OutcomeShow.Instance.Playing;
            var dir = ScenarioDirector.Instance;
            if (showing && dir != null) _showSeenFor = dir.Record;
            // A round that has ended but whose show hasn't started yet (custody: police lines first) isn't a break.
            bool verdictPending = dir != null && dir.CurrentMode != ScenarioDirector.Mode.Sandbox && dir.Record != null
                                  && dir.Record.EndTime > 0f && dir.Record != _showSeenFor;
            bool want = WantsBreakNotice(Tier, CurrentPhase, showing) && !verdictPending && Time.unscaledTime - _phaseSince >= 1f;
            bool shown = Notice.Posted(NoticeKey) != null;
            if (want && !shown) { Notice.Show(NoticeKey, BreakMessage, 50); Debug.Log("[HIS] Thermal: break notice shown"); }
            else if (!want && shown) Notice.Hide(NoticeKey);
        }
    }
}
