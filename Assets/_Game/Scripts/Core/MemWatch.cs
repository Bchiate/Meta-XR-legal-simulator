using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace HIS.Core
{
    /// <summary>
    /// The game's memory, logged every 60 s and around every headset-off pause (0.1.50: the app grew ~393 MB at every headset
    /// off/on — Unity's OpenXR display leaking its eye textures, see <see cref="XrTextureJanitor"/> — until the headset killed it
    /// at 5 GB). One line:
    /// the process's resident and swapped memory (/proc/self/status), Unity's own allocator and the graphics driver's share,
    /// then live Mesh / Material / Texture2D / RenderTexture / GameObject counts with the names that grew most since the last
    /// line — so a playtest log says what leaks, not just that something does. Dev builds count objects (a few ms each
    /// time); release builds log the memory figures only.
    /// </summary>
    public class MemWatch : MonoBehaviour
    {
        /// <summary>Seconds between lines (headless test runs: 15 s, so a standoff test shows what each wave leaves behind).</summary>
        static float Period => Application.isBatchMode ? 15f : 60f;
        float _next = 10f;
        long _rssStart = -1, _rssLast = -1;
        readonly Dictionary<string, int> _lastByName = new();
        readonly Dictionary<string, int> _lastTotals = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            // Headset builds, and headless test runs in the Editor (object counts only there: no /proc on the Mac).
            if ((Application.isEditor && !Application.isBatchMode) || FindAnyObjectByType<MemWatch>() != null) return;
            var go = new GameObject("[HIS] MemWatch");
            DontDestroyOnLoad(go);
            go.AddComponent<MemWatch>();
        }

        void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + Period;
            Report(_resumeLine ? "after resume" : "");
            _resumeLine = false;
        }

        // Before the headset-off pause, and 5 s after it ends: OnApplicationPause(false) comes before the XR session restarts
        // and makes its new eye textures, and XrTextureJanitor sweeps at ~2.5 s — the pair says what one off/on costs.
        void OnApplicationPause(bool paused)
        {
            if (paused) { Report("pausing"); _resumeLine = false; return; }
            _next = Time.realtimeSinceStartup + 5f;
            _resumeLine = true;
        }
        bool _resumeLine;

        void Report(string why)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ReadProcStatus(out long rss, out long swap);
            if (_rssStart < 0 && rss > 0) _rssStart = rss;
            var sb = new StringBuilder(256);
            sb.Append("[HIS] Mem").Append(why.Length > 0 ? $" ({why})" : "").Append(": ");
            if (rss > 0)
            {
                sb.Append($"rss {rss / 1024} MB");
                if (_rssLast > 0) sb.Append($" ({Signed((rss - _rssLast) / 1024)} since last, {Signed((rss - _rssStart) / 1024)} since start)");
                sb.Append($", swap {swap / 1024} MB");
                _rssLast = rss;
            }
            else sb.Append("rss ?");
            sb.Append($" | unity {Profiler.GetTotalAllocatedMemoryLong() >> 20}/{Profiler.GetTotalReservedMemoryLong() >> 20} MB, " +
                      $"mono {Profiler.GetMonoUsedSizeLong() >> 20} MB");
            long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver(); // dev builds only (0 otherwise)
            if (gfx > 0) sb.Append($", gfx {gfx >> 20} MB");
            if (XrTextureJanitor.Resumes > 0)
                sb.Append($", resumes {XrTextureJanitor.Resumes} (eye sets rebuilt {XrTextureJanitor.EyeSetsRebuilt})");
            if (XrTextureJanitor.ReleasedCount > 0) sb.Append($", XR janitor freed {XrTextureJanitor.ReleasedCount} (~{XrTextureJanitor.ReleasedMB:0} MB)");
            if (Debug.isDebugBuild || Application.isEditor)
            {
                var grew = new List<(string name, int d)>();
                sb.Append(" | ");
                Count<Mesh>("meshes", sb, grew); Count<Material>("mats", sb, grew); Count<Texture2D>("tex", sb, grew);
                Count<RenderTexture>("rt", sb, grew); Count<GameObject>("go", sb, grew); Count<AudioClip>("clips", sb, grew);
                grew.Sort((a, b) => b.d.CompareTo(a.d));
                if (grew.Count > 0)
                {
                    sb.Append(" | grew:");
                    for (int i = 0; i < grew.Count && i < 5; i++) sb.Append(' ').Append(grew[i].name).Append(" +").Append(grew[i].d);
                }
            }
            sb.Append($" ({clock.ElapsedMilliseconds} ms)");
            Debug.Log(sb.ToString());
        }

        static readonly List<string> s_gone = new();

        static string Signed(long v) => v >= 0 ? $"+{v}" : v.ToString();

        /// <summary>Counts live objects of <typeparamref name="T"/> (assets included) and notes the names that grew.</summary>
        void Count<T>(string label, StringBuilder sb, List<(string, int)> grew) where T : Object
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            int last = _lastTotals.TryGetValue(label, out var l) ? l : -1;
            sb.Append(label).Append(' ').Append(all.Length);
            if (last >= 0 && all.Length != last) sb.Append('(').Append(Signed(all.Length - last)).Append(')');
            sb.Append(' ');
            _lastTotals[label] = all.Length;
            if (typeof(T) == typeof(GameObject)) return; // names of GameObjects churn every round: totals only
            var now = new Dictionary<string, int>();
            foreach (var o in all)
            {
                string key = $"{label}:{(string.IsNullOrEmpty(o.name) ? "(unnamed)" : o.name)}";
                now[key] = now.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            bool seen = _lastTotals.ContainsKey(label + "#seen");
            foreach (var kv in now)
            {
                int before = _lastByName.TryGetValue(kv.Key, out var b) ? b : 0;
                if (seen && kv.Value - before >= 3) grew.Add((kv.Key, kv.Value - before));
                _lastByName[kv.Key] = kv.Value;
            }
            // Names gone since the last line count as zero (so one that comes back is measured from nothing).
            s_gone.Clear();
            foreach (var key in _lastByName.Keys) if (key.StartsWith(label + ":") && !now.ContainsKey(key)) s_gone.Add(key);
            foreach (var key in s_gone) _lastByName[key] = 0;
            _lastTotals[label + "#seen"] = 1;
        }

        static void ReadProcStatus(out long rssKb, out long swapKb)
        {
            rssKb = swapKb = -1;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/self/status"))
                {
                    if (line.StartsWith("VmRSS:")) rssKb = ParseKb(line);
                    else if (line.StartsWith("VmSwap:")) swapKb = ParseKb(line);
                }
            }
            catch { }
        }

        static long ParseKb(string line)
        {
            var parts = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var v) ? v : -1;
        }
    }
}
