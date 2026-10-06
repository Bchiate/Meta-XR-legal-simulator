using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Build stamp. HISBuildProcessor writes it into the Android version name before every player build
    /// ("0.1.12 (6e784f8) dev 2026-09-22 18:40"), so it shows on the dev HUD, in logcat and in the Quest's app info.
    /// </summary>
    public static class BuildInfo
    {
        public static string Label => Application.isEditor ? "editor" : Application.version;
    }
}
