using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CREDITS.md (project root) is the single source of truth for attributions. This copies it into a TextAsset the game
/// ships (<see cref="HIS.UI.CreditsMenu"/> shows it from the pause menu). Runs from MainSceneBuilder.Build and before
/// every player build (HISBuildProcessor), so an edited CREDITS.md always reaches the APK. Menu: HIS > Sync Credits.
/// </summary>
public static class CreditsSync
{
    public const string SourceFile = "CREDITS.md";
    public const string TargetAsset = "Assets/_Game/Data/Credits.txt";

    public static string SourcePath => Path.Combine(Directory.GetCurrentDirectory(), SourceFile);

    [MenuItem("HIS/Sync Credits")]
    public static TextAsset Sync()
    {
        if (!File.Exists(SourcePath))
        {
            Debug.LogError($"[HIS] Credits: {SourcePath} not found — in-game credits keep the last copy");
            return AssetDatabase.LoadAssetAtPath<TextAsset>(TargetAsset);
        }
        string text = File.ReadAllText(SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(TargetAsset));
        if (!File.Exists(TargetAsset) || File.ReadAllText(TargetAsset) != text)
        {
            File.WriteAllText(TargetAsset, text);
            AssetDatabase.ImportAsset(TargetAsset, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[HIS] Credits: {SourceFile} copied to {TargetAsset} ({text.Length} chars)");
        }
        return AssetDatabase.LoadAssetAtPath<TextAsset>(TargetAsset);
    }
}
