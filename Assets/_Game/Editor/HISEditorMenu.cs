using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Menu-bar hooks so the Editor can be driven without dialogs (used by the AI tooling via the macOS menu bar).</summary>
[InitializeOnLoad]
public static class HISEditorMenu
{
    static HISEditorMenu()
    {
        // Script edits arrive from outside while Play Mode may be running; recompiling mid-play under OpenXR crashes the Editor.
        // 1 = "Recompile After Finished Playing".
        if (EditorPrefs.GetInt("ScriptCompilationDuringPlay", 0) != 1) EditorPrefs.SetInt("ScriptCompilationDuringPlay", 1);
    }

    const string MainScene = "Assets/_Game/Scenes/Main.unity";

    [MenuItem("HIS/Open Main Scene", false, 100)]
    public static void OpenMain()
    {
        if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        if (EditorSceneManager.GetActiveScene().path != MainScene)
        {
            EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
        }
        Debug.Log("[HIS] Main scene open");
    }

    [MenuItem("HIS/Play Main Scene", false, 101)]
    public static void PlayMain()
    {
        if (EditorApplication.isPlaying) return;
        OpenMain();
        // The Editor is driven from the background (menu bar + Operator MCP); without this Play Mode pauses on focus loss.
        PlayerSettings.runInBackground = true;
        EditorApplication.isPaused = false;
        EditorApplication.EnterPlaymode();
        Debug.Log("[HIS] Entering Play Mode");
    }

    [MenuItem("HIS/Stop Play Mode", false, 102)]
    public static void Stop()
    {
        if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        Debug.Log("[HIS] Exiting Play Mode");
    }

    const string ForceBrettRoomPref = "HIS.ForceBrettRoom";

    [MenuItem("HIS/Editor Room/Brett's Room (JSON)", false, 110)]
    static void UseBrettRoom() { EditorPrefs.SetBool(ForceBrettRoomPref, true); Debug.Log("[HIS] Editor room: Brett's room (JSON)"); }
    [MenuItem("HIS/Editor Room/Brett's Room (JSON)", true)]
    static bool UseBrettRoomValidate() { Menu.SetChecked("HIS/Editor Room/Brett's Room (JSON)", EditorPrefs.GetBool(ForceBrettRoomPref, false)); return true; }

    [MenuItem("HIS/Editor Room/Simulator Room", false, 111)]
    static void UseSimRoom() { EditorPrefs.SetBool(ForceBrettRoomPref, false); Debug.Log("[HIS] Editor room: simulator room (JSON / prefab fallback)"); }
    [MenuItem("HIS/Editor Room/Simulator Room", true)]
    static bool UseSimRoomValidate() { Menu.SetChecked("HIS/Editor Room/Simulator Room", !EditorPrefs.GetBool(ForceBrettRoomPref, false)); return true; }

    [MenuItem("HIS/Close Popup Windows", false, 103)]
    public static void ClosePopups()
    {
        foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            var n = w.GetType().FullName ?? "";
            if (n.Contains("Meta.XR") && (n.Contains("Guide") || n.Contains("Welcome") || n.Contains("Onboarding")))
            {
                Debug.Log($"[HIS] Closing {n}");
                w.Close();
            }
        }
    }
}
