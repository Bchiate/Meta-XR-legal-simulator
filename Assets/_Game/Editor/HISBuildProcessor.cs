using System;
using System.Diagnostics;
using System.IO;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// Build-time tweaks that must not leak into Editor Play Mode:
///  • stamps the build (versionCode +1, versionName "0.1.N (git sha) dev time") for the HUD, logcat and app info;
///  • forces MRUK to load the room from the device only (never the fake prefab room) in player builds;
///  • strips debug visuals (room wireframe, sanity cube) from non-development builds;
///  • refreshes the in-game credits from CREDITS.md (<see cref="CreditsSync"/>);
///  • adds the boot-config keys Unity's own Meta Quest feature would add (off here: Meta's plugin drives the headset) that
///    the game relies on — xr-hide-memoryless-render-texture, the pair of Optimize Buffer Discards (<see cref="XrStabilitySetup"/>).
/// </summary>
public class HISBuildProcessor : IPreprocessBuildWithReport, IProcessSceneWithReport, IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        bool dev = (report.summary.options & BuildOptions.Development) != 0;
        DoorwayViewSetup.Configure(); // the outside through an open door (idempotent: a no-op once the renderer assets are set)
        XrStabilitySetup.Apply();     // SpaceWarp + subsampled layout off (0.1.51: the GPU hangs); idempotent
        // Dirty = uncommitted code/assets; the version bump itself (ProjectSettings.asset) doesn't count.
        string sha = Git("rev-parse --short HEAD") ?? "nogit";
        string dirty = string.IsNullOrWhiteSpace(Git("status --porcelain --untracked-files=no -- . \":(exclude)ProjectSettings/ProjectSettings.asset\"")) ? "" : "+";

        int code = Math.Max(1, PlayerSettings.Android.bundleVersionCode) + 1;
        PlayerSettings.Android.bundleVersionCode = code;
        // Android versionName is free text: it carries the whole stamp, read at runtime via Application.version.
        PlayerSettings.bundleVersion = $"0.1.{code} ({sha}{dirty}){(dev ? " dev" : "")} {DateTime.Now:yyyy-MM-dd HH:mm}";
        AssetDatabase.SaveAssets(); // persist the bump so versionCode keeps increasing across Editor sessions
        CreditsSync.Sync(); // in-game credits = CREDITS.md as of this build
        Debug.Log($"[HIS] Build stamp: {PlayerSettings.bundleVersion} (versionCode {code})");
    }

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null) return; // Play Mode: keep the Editor's fallback rooms

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var mruk in root.GetComponentsInChildren<MRUK>(true))
            {
                mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
                // The Editor-only fallback rooms (Brett's room JSON, sample prefab rooms) must not ship in the APK.
                mruk.SceneSettings.SceneJsons = Array.Empty<TextAsset>();
                mruk.SceneSettings.RoomPrefabs = Array.Empty<GameObject>();
                Debug.Log($"[HIS] Build: MRUK data source forced to Device in {scene.name}");
            }
            // The red debug cube goes from every build, dev too: it has a collider that stopped rounds (Brett 0.1.30 saw it
            // hanging in front of the doorway, right in the line of fire). So does the scan wireframe (Brett asked for it off):
            // only ThermalGovernor hid it, and only after a thermal tier change, so dev builds still laid its see-through film
            // and red edges over his walls and furniture (found 0.1.41).
            if (root.name == "[HIS] SanityCube" || root.name == "[HIS] RoomWireframe")
            {
                root.SetActive(false);
                Debug.Log($"[HIS] Build: disabled debug object {root.name}");
            }
        }
    }

    static string Git(string args)
    {
        try
        {
            var git = File.Exists("/usr/bin/git") ? "/usr/bin/git" : "git";
            var psi = new ProcessStartInfo(git, args)
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string output = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    /// <summary>Boot-config keys added to the generated Gradle project (key → value), only if not already there.</summary>
    static readonly (string key, string value)[] BootConfigKeys =
    {
        // Unity's MetaQuestFeatureBuildHooks pairs this with Optimize Buffer Discards: memoryless MSAA eye buffers really get no
        // memory (0.1.51: shrinks the ~393 MB Unity leaks at every headset off/on, OXRB-306).
        ("xr-hide-memoryless-render-texture", "1"),
    };

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var bootConfig = Path.Combine(path, "src", "main", "assets", "bin", "Data", "boot.config");
        if (!File.Exists(bootConfig)) { Debug.LogWarning($"[HIS] Build: no boot.config at {bootConfig}"); return; }
        var lines = new System.Collections.Generic.List<string>(File.ReadAllLines(bootConfig));
        var added = new System.Collections.Generic.List<string>();
        foreach (var (key, value) in BootConfigKeys)
        {
            if (lines.Exists(l => l.StartsWith(key + "="))) continue;
            lines.Add($"{key}={value}");
            added.Add(key);
        }
        if (added.Count > 0) File.WriteAllLines(bootConfig, lines);
        Debug.Log($"[HIS] Build: boot.config {(added.Count > 0 ? "+ " + string.Join(", ", added) : "already has the XR keys")}");
        // Unity's SpaceWarp hook writes xr-meta-enabled from the SpaceWarp feature's state (over Meta's "always true"), so a
        // settings change can quietly switch Unity's Meta mode off (0.1.51's first build). Shout if it isn't on.
        if (!lines.Contains("xr-meta-enabled=1"))
            Debug.LogError("[HIS] Build: boot.config has xr-meta-enabled off — keep the SpaceWarp feature enabled (XrStabilitySetup)");
    }
}
