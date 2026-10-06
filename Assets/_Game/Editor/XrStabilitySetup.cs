using System.Linq;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Android OpenXR render settings that keep long sessions stable (0.1.51, after 0.1.50's kernel GPU hangs and 5 GB leak).
/// (SpaceWarp off was tried and reverted the same day — see its item.)
/// Idempotent; run by every player build (<see cref="HISBuildProcessor"/>) and by <see cref="QuestProjectSetup.Configure"/>,
/// so nothing (Meta's project-setup fixes included) can quietly turn them back. Headless: scripts/unity-batch.sh XrStabilitySetup.Apply
/// <list type="bullet">
/// <item>Optimize Buffer Discards (Vulkan) ON: the eye buffers' 4x MSAA colour and depth become memoryless (lazily allocated,
/// tile memory only). Each eye texture drops from ~131 MB to ~15 MB, so the set Unity leaks at every headset off/on (OXRB-306,
/// see XrTextureJanitor) costs ~45 MB instead of ~393 MB, and the live eye buffers shrink by ~350 MB. Unity's own Meta Quest
/// feature turns this on by default; it is off here (Meta's plugin drives the headset), so nothing set it. On Vulkan a pass that
/// does reload the buffers still works (the driver commits memory for it) — check the doorway portal on the headset anyway.</item>
/// <item>Application SpaceWarp stays ON although the game never uses it: Unity's SpaceWarp build hook writes the boot-config
/// key xr-meta-enabled from the feature's state, overriding Meta's own "always true" — turning SpaceWarp off silently turned
/// Unity's Meta mode off too (seen in 0.1.51's first build). HISBuildProcessor checks xr-meta-enabled=1 in every build.</item>
/// <item>Meta XR Subsampled Layout OFF: a test, not a proven fix — the 0.1.48/0.1.50 kernel GPU hangs had no trigger in the
/// game's own code, and this is the remaining Quest 3 Vulkan render-layout option. Foveation still works without it (slightly
/// more bandwidth). Keep it off only if the hang count per standoff drops.</item>
/// </list>
/// </summary>
public static class XrStabilitySetup
{
    const string SpaceWarpId = "com.unity.openxr.feature.spacewarp";       // must stay ON (see above)
    const string SubsampledLayoutId = "com.meta.openxr.feature.subsampledLayout";

    [MenuItem("HIS/Setup/Apply XR Stability Settings")]
    public static void Apply()
    {
        var group = BuildTargetGroup.Android;
        FeatureHelpers.RefreshFeatures(group);
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        if (settings == null) { Debug.LogError("[HIS] XrStabilitySetup: no OpenXR settings for Android"); return; }

        bool changed = false;
        // (Not shown in the OpenXR settings UI in 1.18 — it lives on Unity's Meta Quest feature, which only copies it in when enabled.)
        if (!settings.optimizeBufferDiscards) { settings.optimizeBufferDiscards = true; EditorUtility.SetDirty(settings); changed = true; }
        foreach (var (id, want) in new[] { (SpaceWarpId, true), (SubsampledLayoutId, false) })
        {
            var f = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(group, new[] { id }).FirstOrDefault();
            if (f == null) { Debug.LogWarning($"[HIS] XrStabilitySetup: feature {id} not found"); continue; }
            if (f.enabled != want) { f.enabled = want; EditorUtility.SetDirty(f); changed = true; }
        }
        if (changed) AssetDatabase.SaveAssets();
        Debug.Log($"[HIS] XrStabilitySetup: buffer discards {(settings.optimizeBufferDiscards ? "on" : "OFF")}, SpaceWarp {State(SpaceWarpId)}, " +
                  $"subsampled layout {State(SubsampledLayoutId)}{(changed ? " (changed)" : "")}");
    }

    static string State(string id) =>
        FeatureHelpers.GetFeaturesWithIdsForBuildTarget(BuildTargetGroup.Android, new[] { id }).FirstOrDefault() is { } f ? (f.enabled ? "ON" : "off") : "absent";
}
