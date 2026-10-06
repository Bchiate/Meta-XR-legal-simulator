using UnityEditor;
using UnityEngine;

/// <summary>
/// Headless "play server": Unity -batchmode -projectPath . -executeMethod HISAutomation.PlayServer
/// (no -quit). Opens Main.unity, enters Play Mode with the XR Simulator runtime and keeps the process alive so the
/// Meta XR Operator MCP server (localhost:8720) can drive the game. Kill the process to stop.
/// </summary>
public static class HISAutomation
{
    /// <summary>Headless: regenerate everything built from code — intruder prefab + Animator, sound library, main scene.</summary>
    public static void RebuildAll()
    {
        IntruderBuilder.Build();
        SfxLibraryBuilder.Build();
        MainSceneBuilder.Build();
        Debug.Log("[HIS] RebuildAll done");
    }

    public static void PlayServer()
    {
        Debug.Log("[HIS] PlayServer: opening main scene");
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        PlayerSettings.runInBackground = true;
        EditorApplication.playModeStateChanged += s => Debug.Log($"[HIS] PlayServer: play mode state {s}");
        EditorApplication.EnterPlaymode();
        Debug.Log("[HIS] PlayServer: EnterPlaymode requested; staying alive");
    }
}
