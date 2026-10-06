using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Headless APK build for Quest. Invoked by scripts/build-and-deploy.sh:
///   Unity -batchmode -quit -projectPath . -executeMethod BuildScript.BuildQuest [-outputPath Builds/his.apk] [-dev]
/// </summary>
public static class BuildScript
{
    static bool s_forceDev;

    [MenuItem("HIS/Build Quest APK")]
    public static void BuildQuestMenu() { s_forceDev = false; BuildQuest(); }

    [MenuItem("HIS/Build Quest APK (Development)")]
    public static void BuildQuestDevMenu() { s_forceDev = true; BuildQuest(); }

    public static void BuildQuest()
    {
        string output = GetArg("-outputPath") ?? "Builds/HomeInvasionSimulator.apk";
        bool dev = s_forceDev || Environment.GetCommandLineArgs().Contains("-dev");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0)
            throw new Exception("[HIS] No scenes enabled in Build Settings");

        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = dev ? (BuildOptions.Development | BuildOptions.ConnectWithProfiler) : BuildOptions.None,
        };

        Debug.Log($"[HIS] Building {scenes.Length} scene(s) -> {output} (dev={dev})");
        BuildReport report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        Debug.Log($"[HIS] Build {s.result}: {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalWarnings} warnings, {s.totalTime.TotalSeconds:F0}s");
        if (s.result != BuildResult.Succeeded)
        {
            foreach (var step in report.steps)
                foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                    Debug.LogError($"[HIS] {step.name}: {m.content}");
            // Only headless builds exit with an error code; quitting the GUI Editor would kill the whole tooling loop.
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    static string GetArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
