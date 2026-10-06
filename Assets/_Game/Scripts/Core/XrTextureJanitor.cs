using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR;

namespace HIS.Core
{
    /// <summary>
    /// Frees the eye textures Unity's OpenXR display leaves behind on every headset off/on (0.1.50: the game grew ~393 MB per
    /// pause/resume and the headset killed it at 5 GB after ten of them). When the headset comes off, the runtime destroys
    /// the eye swapchains; when it goes back on, Unity makes a new set of eye RenderTextures (one per swapchain image, each with
    /// its own 4x MSAA colour and depth) and never destroys the old set — no RequestDestroyTexture, the bug Unity once fixed as
    /// OXRB-306, back in 6000.6.0f1 + OpenXR 1.18.0. (Its render-scale path does destroy them, so the old set is plainly dead.)
    /// XR texture ids only grow. So: at each pause note the highest id; a couple of seconds after the resume, collect the eye
    /// textures the display hands the renderer now (the live set) — and only if a live one is NEWER than that id (the set
    /// really was rebuilt; a short focus loss rebuilds nothing), Release() every colour eye texture at or below it that isn't
    /// live: its GPU memory goes, the object stays (the display's id map may still point at it). Depth-only XR textures (the
    /// environment depth) and anything not shaped like the live eye texture are never touched. Logs "[HIS] XR janitor: ...".
    /// 0.1.51 ships it REPORT-ONLY (<see cref="ReleaseStale"/> false): the stale sets' native colour pointers get reused by newer
    /// sets (0.1.50: id 11's colour = id 5's, 12 = 6, 26 = 21), so a Release could act on a live surface. The report says what
    /// is there (id, size, slices, MSAA, created); Optimize Buffer Discards (XrStabilitySetup) shrinks each leaked set meanwhile.
    /// Switch it to releasing on the headset, no rebuild: `adb shell touch /sdcard/Android/data/com.futureclarity.homeinvasionsimulator/files/janitor_release`
    /// (read at every resume; delete the file to go back). Dev builds also list every RenderTexture named "XR Texture [n]".
    /// </summary>
    public class XrTextureJanitor : MonoBehaviour
    {
        public static XrTextureJanitor Instance { get; private set; }
        /// <summary>Off switch (tests, or if a headset build ever misbehaves after a resume).</summary>
        public static bool Enabled = true;
        /// <summary>false: report the stale eye textures only (0.1.51). true: Release() them — only after a headset test proves it safe.</summary>
        public static bool ReleaseStale = false;
        /// <summary>Eye textures released so far and their estimated GPU memory (MB) — MemWatch prints these (0 while report-only).</summary>
        public static int ReleasedCount { get; private set; }
        public static float ReleasedMB { get; private set; }
        /// <summary>Resumes seen, and resumes after which a new eye set was made (≥ 1 per resume when the headset slept).</summary>
        public static int Resumes { get; private set; }
        public static int EyeSetsRebuilt { get; private set; }

        const float SettleSeconds = 2f;  // the session reaches FOCUSED and the new set exists by then
        const int SampleFrames = 12;     // frames: every image of a 3-image swapchain shows up several times
        const uint MaxIds = 4096;        // ids are small and only grow (0.1.50 reached 43 in 3 hours)
        const int MissRun = 64;          // ids are dense: this many missing in a row = past the last one

        readonly HashSet<RenderTexture> _live = new();
        readonly HashSet<RenderTexture> _released = new();
        uint _maxBeforePause;            // highest XR texture id when the headset came off (0: none known)
        bool _pending;
        float _due, _nextBaseline = 5f;
        int _frames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Application.isEditor || Instance != null) return; // the leak is the headset's OpenXR display
            var go = new GameObject("[HIS] XrTextureJanitor");
            DontDestroyOnLoad(go);
            go.AddComponent<XrTextureJanitor>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; Enabled = true; ReleaseStale = false; ReleasedCount = 0; ReleasedMB = 0f; Resumes = EyeSetsRebuilt = 0; } // domain reload off

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _pending = false;
                var d = Display();
                if (d != null) _maxBeforePause = System.Math.Max(_maxBeforePause, HighestId(d));
                return;
            }
            try { ReleaseStale = System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "janitor_release")); } catch { }
            Resumes++;
            if (_maxBeforePause == 0) return; // nothing known from before: leave everything alone
            _pending = true; _due = Time.realtimeSinceStartup + SettleSeconds; _frames = 0; _live.Clear();
        }

        void LateUpdate()
        {
            // While playing (no resume pending) keep the baseline current, in case the display has already stopped by the
            // time the next pause arrives: no new eye set is made mid-play (the eye buffers are never resized at runtime).
            if (!_pending && Time.realtimeSinceStartup >= _nextBaseline)
            {
                _nextBaseline = Time.realtimeSinceStartup + 20f;
                var d = Display();
                if (d != null) _maxBeforePause = System.Math.Max(_maxBeforePause, HighestId(d));
            }
            if (!_pending || !Enabled || Time.realtimeSinceStartup < _due) return;
            var display = Display();
            if (display == null) return;
            if (display.GetRenderPassCount() > 0)
            {
                var rt = display.GetRenderTextureForRenderPass(0);
                if (rt != null) _live.Add(rt);
            }
            if (++_frames < SampleFrames) return;
            _pending = false;
            Sweep(display);
        }

        static readonly List<XRDisplaySubsystem> s_displays = new(1);

        static XRDisplaySubsystem Display()
        {
            s_displays.Clear();
            SubsystemManager.GetSubsystems(s_displays);
            foreach (var d in s_displays) if (d != null && d.running) return d;
            return null;
        }

        static RenderTexture Get(XRDisplaySubsystem d, uint id)
        {
            try { return d.GetRenderTexture(id); } catch { return null; }
        }

        static uint HighestId(XRDisplaySubsystem d)
        {
            uint highest = 0; int misses = 0;
            for (uint id = 1; id < MaxIds && misses < MissRun; id++)
            {
                if (Get(d, id) == null) { misses++; continue; }
                misses = 0; highest = id;
            }
            return highest;
        }

        void Sweep(XRDisplaySubsystem display)
        {
            uint before = _maxBeforePause;
            RenderTexture model = null;
            foreach (var l in _live) { model = l; break; }
            if (model == null) { Debug.Log("[HIS] XR janitor: no live eye texture after the resume — nothing released"); return; }

            // Pass 1: the ids of the live set, and the highest id now.
            uint highest = 0, newestLive = 0; int misses = 0;
            for (uint id = 1; id < MaxIds && misses < MissRun; id++)
            {
                var rt = Get(display, id);
                if (rt == null) { misses++; continue; }
                misses = 0; highest = id;
                if (_live.Contains(rt)) newestLive = System.Math.Max(newestLive, id);
            }
            _maxBeforePause = highest;
            if (Debug.isDebugBuild) LogNamedXrTextures();
            if (newestLive <= before)
            {
                Debug.Log($"[HIS] XR janitor: eye textures not rebuilt by this resume (live id {newestLive} ≤ {before}) — nothing to release");
                return;
            }

            EyeSetsRebuilt++;
            // Pass 2: release the dead colour eye textures from before the pause.
            int freed = 0; float mb = 0f;
            var ids = new System.Text.StringBuilder();
            for (uint id = 1; id <= before && id < MaxIds; id++)
            {
                var rt = Get(display, id);
                if (rt == null || _live.Contains(rt) || _released.Contains(rt)) continue;
                if (rt.graphicsFormat == GraphicsFormat.None) continue;                       // depth-only (environment depth): in use
                if (rt.dimension != model.dimension || rt.volumeDepth != model.volumeDepth
                    || rt.width != model.width || rt.height != model.height) continue;       // not an old eye texture
                if (!rt.IsCreated()) continue;
                mb += EstimateMB(rt);
                if (ReleaseStale) { rt.Release(); _released.Add(rt); }
                freed++;
                ids.Append(ids.Length > 0 ? "," : "").Append(id);
            }
            if (ReleaseStale) { ReleasedCount += freed; ReleasedMB += mb; }
            Debug.Log($"[HIS] XR janitor: {_live.Count} live eye texture(s) {model.width}x{model.height}x{model.volumeDepth} aa{model.antiAliasing} " +
                      $"(newest id {newestLive}), {(ReleaseStale ? "released" : "would release (report only)")} {freed} stale from before the pause " +
                      $"(~{mb:0} MB{(freed > 0 ? $", ids {ids}" : "")}); {ReleasedCount} released so far (~{ReleasedMB:0} MB)");
        }

        /// <summary>
        /// Dev builds: every RenderTexture named "XR Texture …" (Unity's eye textures), as FindObjectsOfTypeAll sees them — the
        /// second view on the stale sets in case the display's id map has dropped them (then the id walk finds nothing).
        /// </summary>
        static void LogNamedXrTextures()
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
            {
                if (rt == null || !rt.name.StartsWith("XR Texture")) continue;
                n++;
                sb.Append(n > 1 ? "; " : "").Append(rt.name).Append(' ').Append(rt.width).Append('x').Append(rt.height).Append('x').Append(rt.volumeDepth)
                  .Append(" aa").Append(rt.antiAliasing).Append(rt.graphicsFormat == GraphicsFormat.None ? " depth-only" : "").Append(rt.IsCreated() ? "" : " (released)");
            }
            Debug.Log($"[HIS] XR janitor: {n} RenderTexture(s) named \"XR Texture\"{(n > 0 ? ": " + sb : "")}");
        }

        /// <summary>Unity-owned MSAA colour + depth per array slice (the swapchain colour image is the runtime's: not counted).</summary>
        static float EstimateMB(RenderTexture rt)
        {
            int samples = Mathf.Max(1, rt.antiAliasing), slices = Mathf.Max(1, rt.volumeDepth);
            float px = (float)rt.width * rt.height * slices;
            float colour = samples > 1 ? px * 4f * samples : 0f;
            float depth = rt.depthStencilFormat != GraphicsFormat.None ? px * 4f * samples : 0f;
            return (colour + depth) / (1024f * 1024f);
        }
    }
}
