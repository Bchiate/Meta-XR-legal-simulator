using HIS.Scenario;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// The headset's own per-frame timings (XR_META_performance_metrics: the app's GPU and CPU time), summed up every 10 s
    /// while something is happening (a round, the police standoff or a Marshal night's hitman waves): average and peak GPU time
    /// against the 13.9 ms a frame has at 72 Hz, slow frames, and what was out there (live men, bodies, vehicles, open doors,
    /// skinned meshes drawn, door camera frames, and which squad and wave; on a Marshal night Ricky's state too, apart from the
    /// alive/bodies counts: research/27 §6 step 12). A warning line once the average passes 85% of the
    /// frame (Brett 0.1.43: "stop me before my game gets to be a lot for the headset"). On Quest 3 a passthrough app can't
    /// raise its GPU clock past level 2 whatever it hints, so this is the budget: the only way to more headroom is less GPU
    /// work. Costs two plugin calls a frame.
    /// </summary>
    public class PerfWatch : MonoBehaviour
    {
        const float Window = 10f, FrameMs = 1000f / 72f, WarnShare = 0.85f;

        float _winStart = -1f, _gpuSum, _gpuMax, _cpuSum, _cpuMax;
        int _n, _slow, _feedFramesAt;
        bool _checked, _supported;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isEditor || FindAnyObjectByType<PerfWatch>() != null) return;
            var go = new GameObject("[HIS] PerfWatch");
            DontDestroyOnLoad(go);
            go.AddComponent<PerfWatch>();
        }

        static bool Busy()
        {
            var pr = PoliceResponse.Instance;
            if (pr != null && pr.Active) return true;
            var tg = ThermalGovernor.Instance;
            return tg != null && tg.CurrentPhase == ThermalGovernor.Phase.Live;
        }

        void Update()
        {
            if (!_checked)
            {
                _checked = true;
                try { _supported = OVRPlugin.IsPerfMetricsSupported(OVRPlugin.PerfMetrics.App_GpuTime_Float); } catch { _supported = false; }
                Debug.Log($"[HIS] Perf: per-frame GPU/CPU timings {(_supported ? "on" : "not available on this runtime")}");
            }
            if (!_supported) return;
            float now = Time.unscaledTime;
            if (!Busy() || Time.timeScale <= 0f) { if (_n > 0 && now - _winStart >= 3f) Report(now); _winStart = -1f; return; }
            if (_winStart < 0f) { Reset(now); }
            float gpu = Ms(OVRPlugin.PerfMetrics.App_GpuTime_Float), cpu = Ms(OVRPlugin.PerfMetrics.App_CpuTime_Float);
            if (gpu > 0f) { _gpuSum += gpu; _gpuMax = Mathf.Max(_gpuMax, gpu); _cpuSum += Mathf.Max(0f, cpu); _cpuMax = Mathf.Max(_cpuMax, cpu); _n++; }
            if (Time.unscaledDeltaTime > 1.5f / 72f) _slow++;
            // A long frame, at once, with the GC count (0.1.48: the whole view "glitched" — kernel GPU hangs freezing ~0.5 s, which
            // the 10 s averages hid): match its time against the [HIS] lines around it and the kernel log's "GPU hang detected".
            int gc = System.GC.CollectionCount(0);
            if (Time.unscaledDeltaTime > 0.05f && Time.frameCount > _lastLongFrame + 1)
            {
                _lastLongFrame = Time.frameCount;
                Debug.LogWarning($"[HIS] Perf: long frame {Time.unscaledDeltaTime * 1000f:F0} ms (GPU {gpu:F1} ms, GC +{gc - _gcSeen})");
            }
            _gcSeen = gc;
            if (now - _winStart >= Window) Report(now);
        }

        /// <summary>A timing in ms (the runtime may report seconds: anything under half a millisecond-as-seconds is converted).</summary>
        static float Ms(OVRPlugin.PerfMetrics m)
        {
            float? v = null;
            try { v = OVRPlugin.GetPerfMetricsFloat(m); } catch { }
            if (v is not float f || f <= 0f) return -1f;
            return f < 0.5f ? f * 1000f : f;
        }

        int _gcSeen, _lastLongFrame = -10;

        void Reset(float now)
        {
            _winStart = now; _gpuSum = _gpuMax = _cpuSum = _cpuMax = 0f; _n = _slow = 0;
            _feedFramesAt = FeedFrames();
        }

        static int FeedFrames()
        {
            int n = 0;
            foreach (var c in HIS.Security.SecurityCamera.All) if (c != null) n += c.FramesRendered;
            return n;
        }

        void Report(float now)
        {
            if (_n == 0) { Reset(now); return; }
            float gpu = _gpuSum / _n, cpu = _cpuSum / _n, share = gpu / FrameMs;
            int alive = 0, dead = 0;
            // Ricky, the protectee (research/27 §3): the one extra body a Marshal night adds, labelled on his own (awake costs the
            // Animator's CPU, asleep only the skinning) rather than counted as a live man or a body. None in any other mode.
            var ricky = HIS.Intruder.Intruder.Protectee;
            foreach (var i in FindObjectsByType<HIS.Intruder.Intruder>(FindObjectsSortMode.None))
                if (i != null && i != ricky) { if (i.Current == HIS.Intruder.Intruder.State.Dead) dead++; else alive++; }
            string protectee = ricky == null ? "" : $" | Ricky {(ricky.Current == HIS.Intruder.Intruder.State.Dead ? "dead" : ricky.ProtecteeAsleep ? "asleep" : "awake")}";
            int skinned = 0, verts = 0;
            foreach (var r in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
                if (r != null && r.enabled && r.isVisible && r.sharedMesh != null) { skinned++; verts += r.sharedMesh.vertexCount; }
            int open = 0;
            foreach (var d in HIS.Room.VirtualDoor.All) if (d != null && d.IsOpen) open++;
            var pc = HIS.Security.PoliceCars.Instance;
            int cars = pc != null ? pc.ActiveCars : 0;
            // A Marshal night's wave (research/27 §6 step 12: the Marshal GPU check groups windows by wave), e.g. "hitmen wave 3".
            // Hitmen only: a police standoff's line stays exactly main's (Brett 2026-10-03, the normal game as main).
            var pr = PoliceResponse.Instance;
            string squad = pr != null && pr.Active && pr.Faction == SquadFaction.Hitmen ? $" | hitmen wave {pr.Wave}" : "";
            Debug.Log($"[HIS] Perf: GPU {gpu:F1} ms avg / {_gpuMax:F1} peak of {FrameMs:F1} ({share:P0}), CPU {cpu:F1} / {_cpuMax:F1} ms, " +
                      $"{_slow} slow frame(s) in {now - _winStart:F0} s | {alive} alive, {dead} bodies, {skinned} skinned drawn ({verts / 1000}k verts), " +
                      $"{cars} vehicle(s), {open} door(s) open, {FeedFrames() - _feedFramesAt} camera frame(s){squad}{protectee}");
            if (share >= WarnShare)
                Debug.LogWarning($"[HIS] Perf: NEAR THE HEADSET'S LIMIT — the GPU averaged {share:P0} of each frame (keep it under {WarnShare:P0})");
            Reset(now);
        }
    }
}
