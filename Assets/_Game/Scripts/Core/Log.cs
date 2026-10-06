using System.Diagnostics;

namespace HIS.Core
{
    /// <summary>
    /// Hot-path logging (every hit, shot, world impact). Compiled out unless the HIS_VERBOSE scripting define is set,
    /// so combat doesn't pay for string formatting + logcat on device.
    /// </summary>
    public static class Log
    {
        [Conditional("HIS_VERBOSE")]
        public static void V(string message) => UnityEngine.Debug.Log(message);
    }
}
