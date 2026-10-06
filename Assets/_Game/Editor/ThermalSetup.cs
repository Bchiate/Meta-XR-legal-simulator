using System.Linq;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings;

/// <summary>
/// Project settings the thermal governor (<see cref="HIS.Core.ThermalGovernor"/>) needs on Quest (Android). Idempotent;
/// also run by <see cref="QuestProjectSetup.Configure"/>. Headless: scripts/unity-batch.sh ThermalSetup.Apply
/// <list type="bullet">
/// <item>Foveated rendering API = Legacy: the enabled Meta XR Foveation feature (what OVRManager.foveatedRenderingLevel
/// drives) only works on that path; the SRP path needs Unity's own Foveated Rendering feature, which is off. With SRP selected
/// nothing foveated at all (VrApi Fov=0 in 0.1.15).</item>
/// <item>Unity "XR Performance Settings" (XR_EXT_performance_settings) on: CPU/GPU level hints reach the runtime and
/// its thermal Warning/Impaired notifications reach the game.</item>
/// </list>
/// </summary>
public static class ThermalSetup
{
    [MenuItem("HIS/Setup/Apply Thermal Settings")]
    public static void Apply()
    {
        var group = BuildTargetGroup.Android;
        FeatureHelpers.RefreshFeatures(group);
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
        if (settings == null) { Debug.LogError("[HIS] ThermalSetup: no OpenXR settings for Android"); return; }

        if (settings.foveatedRenderingApi != OpenXRSettings.BackendFovationApi.Legacy)
        {
            settings.foveatedRenderingApi = OpenXRSettings.BackendFovationApi.Legacy;
            EditorUtility.SetDirty(settings);
        }
        var perf = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(group, new[] { XrPerformanceSettingsFeature.featureId }).FirstOrDefault();
        if (perf == null) Debug.LogError("[HIS] ThermalSetup: XR Performance Settings feature not found");
        else if (!perf.enabled) { perf.enabled = true; EditorUtility.SetDirty(perf); }

        AssetDatabase.SaveAssets();
        Debug.Log($"[HIS] ThermalSetup: foveation API {settings.foveatedRenderingApi}, XR Performance Settings {(perf != null && perf.enabled ? "on" : "OFF")}, " +
                  $"Meta foveation {(settings.GetFeature<Meta.XR.MetaXRFoveationFeature>()?.enabled == true ? "on" : "OFF")}");
    }
}
