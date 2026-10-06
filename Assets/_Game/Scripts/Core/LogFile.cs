using System.IO;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// On the headset, every [HIS] line, warning and error also goes to a file (the Quest's logcat buffer only holds about a
    /// minute, so a playtest's rounds were gone by the time we looked; Brett 0.1.26). One file per launch in the app's files
    /// folder: his.log (this run), his.1.log, his.2.log (the runs before). Pull with scripts/pull-log.sh.
    /// </summary>
    public static class LogFile
    {
        const long MaxBytes = 16L * 1024 * 1024; // stop writing past this (a run is normally well under 1 MB)
        static StreamWriter s_writer;
        static long s_bytes;
        static readonly object s_lock = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Open()
        {
            if (Application.isEditor) return;
            try
            {
                string dir = Application.persistentDataPath;
                string P(int i) => Path.Combine(dir, i == 0 ? "his.log" : $"his.{i}.log");
                if (File.Exists(P(2))) File.Delete(P(2));
                if (File.Exists(P(1))) File.Move(P(1), P(2));
                if (File.Exists(P(0))) File.Move(P(0), P(1));
                s_writer = new StreamWriter(new FileStream(P(0), FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                s_writer.WriteLine($"# {BuildInfo.Label} started {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Application.logMessageReceivedThreaded += OnLog;
            }
            catch (System.Exception e) { Debug.LogWarning($"[HIS] LogFile: {e.Message}"); s_writer = null; }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            bool his = message.StartsWith("[HIS]");
            if (!his && type == LogType.Log) return;
            lock (s_lock)
            {
                if (s_writer == null || s_bytes > MaxBytes) return;
                string line = $"{System.DateTime.Now:HH:mm:ss.fff} {(type == LogType.Log ? "" : type + " ")}{message}";
                if (type == LogType.Exception || type == LogType.Error) line += "\n" + stack;
                try { s_writer.WriteLine(line); s_bytes += line.Length + 1; }
                catch { s_writer = null; }
            }
        }
    }
}
