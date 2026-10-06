using UnityEditor;
using UnityEngine;

/// <summary>
/// Brett 2026-09-24: "do not play the audio on my computer". Unity on the Mac (batch test runs and the GUI Editor alike)
/// never makes a sound: the Editor's master mute is forced on at every domain load and again on entering Play Mode, and
/// the listener volume is 0 in Play Mode. Sources still play (isPlaying, clip choice, timing — everything the tests
/// check), just silently. Builds are unaffected (Editor-only assembly).
/// </summary>
[InitializeOnLoad]
static class EditorAudioMute
{
    static EditorAudioMute()
    {
        EditorUtility.audioMasterMute = true;
        EditorApplication.playModeStateChanged += st =>
        {
            EditorUtility.audioMasterMute = true;
            if (st != PlayModeStateChange.EnteredPlayMode) return;
            AudioListener.volume = 0f;
            Debug.Log("[HIS] Editor audio muted (EditorAudioMute: no sound on Brett's Mac)");
        };
        EditorApplication.update += Enforce;
    }

    // Anything that raises the volume mid-run (a test, a fade, a user toggle) is pulled back down on the next tick.
    static void Enforce()
    {
        if (!EditorUtility.audioMasterMute) EditorUtility.audioMasterMute = true;
        if (EditorApplication.isPlaying && AudioListener.volume > 0f) AudioListener.volume = 0f;
    }
}
