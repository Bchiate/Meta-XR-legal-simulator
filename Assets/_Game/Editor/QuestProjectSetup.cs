using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

/// <summary>
/// One-shot, idempotent configuration of this project for Meta Quest 3 passthrough (MR) on OpenXR.
/// Run from the menu (HIS > Configure Project For Quest) or headless:
///   Unity -batchmode -quit -projectPath . -executeMethod QuestProjectSetup.Configure
/// Anything not set here is handled by Meta's Project Setup Tool (FixAll), which runs at the end.
/// </summary>
public static class QuestProjectSetup
{
    const string PackageId = "com.futureclarity.homeinvasionsimulator";

    // Meta OpenXR feature ids (from com.meta.xr.sdk.core) + Unity interaction profiles.
    static readonly string[] AndroidFeatureIds =
    {
        "com.meta.openxr.feature.metaxr",            // Meta XR Feature (required: passthrough, scene, hands via OVRPlugin)
        "com.meta.openxr.feature.foveation",         // Meta XR Foveation
        "com.meta.openxr.feature.subsampledLayout",  // Meta XR Subsampled Layout
        "com.unity.openxr.feature.input.oculustouch",   // Touch controllers (Quest 2/Pro/3 fallback)
        "com.unity.openxr.feature.input.metaquestplus", // Touch Plus controllers (Quest 3 / 3S)
        "com.unity.openxr.feature.arfoundation-meta-session",   // required dependency of the occlusion feature below
        "com.unity.openxr.feature.arfoundation-meta-occlusion", // Depth API (dynamic occlusion) via com.unity.xr.meta-openxr
    };

    [MenuItem("HIS/Configure Project For Quest")]
    public static void Configure()
    {
        Debug.Log("[HIS] QuestProjectSetup: begin");

        SwitchToAndroid();
        ConfigurePlayerSettings();
        ConfigureXRLoader(BuildTargetGroup.Android);
        ConfigureXRLoader(BuildTargetGroup.Standalone); // for Meta XR Simulator in Play Mode
        ConfigureOpenXRFeatures();
        ThermalSetup.Apply(); // Legacy foveation API + XR Performance Settings (thermal governor)
        XrStabilitySetup.Apply(); // SpaceWarp + subsampled layout off (0.1.51: the GPU hangs)
        ConfigureOVRProjectConfig();
        ApplyGameSettings();
        RunMetaProjectSetupToolFixAll();

        AssetDatabase.SaveAssets();
        Debug.Log("[HIS] QuestProjectSetup: done");
    }

    static void SwitchToAndroid()
    {
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return;
        Debug.Log("[HIS] Switching active build target to Android");
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
    }

    static void ConfigurePlayerSettings()
    {
        var android = NamedBuildTarget.Android;

        PlayerSettings.companyName = "Future Clarity Technologies";
        PlayerSettings.productName = "Home Invasion Simulator";
        PlayerSettings.SetApplicationIdentifier(android, PackageId);
        // Version name/code are stamped per build by HISBuildProcessor; never move the code backwards (store uploads need it increasing).
        if (PlayerSettings.Android.bundleVersionCode < 1) PlayerSettings.Android.bundleVersionCode = 1;

        // Quest hard requirements
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        PlayerSettings.colorSpace = ColorSpace.Linear;

        // Performance defaults recommended by Meta
        PlayerSettings.SetMobileMTRendering(android, true);
        PlayerSettings.Android.optimizedFramePacing = false;
        PlayerSettings.Android.androidTVCompatibility = false;
        PlayerSettings.Android.textureCompressionFormats = new[] { TextureCompressionFormat.ASTC };
        PlayerSettings.SetIl2CppCompilerConfiguration(android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.startInFullscreen = true;
        PlayerSettings.Android.renderOutsideSafeArea = true;

        // MR apps: no Unity splash (PST flags it; Personal license keeps the logo but we disable the VR splash)
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.virtualRealitySplashScreen = null;

        Debug.Log("[HIS] Player settings configured");
    }

    static void ConfigureXRLoader(BuildTargetGroup group)
    {
        // XRGeneralSettingsPerBuildTarget.GetOrCreate() is internal; reflect to it so we don't need the UI.
        var getOrCreate = typeof(XRGeneralSettingsPerBuildTarget).GetMethod("GetOrCreate",
            BindingFlags.NonPublic | BindingFlags.Static);
        var perBuildTarget = (XRGeneralSettingsPerBuildTarget)getOrCreate.Invoke(null, null);

        var settings = perBuildTarget.SettingsForBuildTarget(group);
        if (settings == null || settings.Manager == null)
        {
            perBuildTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            settings = perBuildTarget.SettingsForBuildTarget(group);
        }

        settings.InitManagerOnStart = true;
        var manager = settings.Manager;
        const string loader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        bool already = manager.activeLoaders.Any(l => l != null && l.GetType().FullName == loader);
        if (!already)
        {
            if (!XRPackageMetadataStore.AssignLoader(manager, loader, group))
                Debug.LogError($"[HIS] Failed to assign OpenXR loader for {group}");
        }
        EditorUtility.SetDirty(settings);
        EditorUtility.SetDirty(manager);
        EditorUtility.SetDirty(perBuildTarget);
        Debug.Log($"[HIS] OpenXR loader active for {group}: {manager.activeLoaders.Count} loader(s)");
    }

    static void ConfigureOpenXRFeatures()
    {
        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var found = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(BuildTargetGroup.Android, AndroidFeatureIds);
        foreach (var f in found)
        {
            f.enabled = true;
            EditorUtility.SetDirty(f);
        }
        var foundIds = found.Select(f => f.GetType().GetCustomAttribute<OpenXRFeatureAttribute>()?.FeatureId).ToArray();
        var missing = AndroidFeatureIds.Except(foundIds).ToArray();
        if (missing.Length > 0) Debug.LogWarning("[HIS] OpenXR features not found: " + string.Join(", ", missing));

        var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (openXr != null)
        {
            openXr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced; // Multiview on Quest
            // Never 16-bit (no stencil on the Quest: the doorway portal breaks). None by default: DoorwayViewSetup.WantedDepthSubmission.
            openXr.depthSubmissionMode = DoorwayViewSetup.WantedDepthSubmission;
            EditorUtility.SetDirty(openXr);
        }
        Debug.Log($"[HIS] OpenXR features enabled: {found.Length}/{AndroidFeatureIds.Length}");
    }

    static void ConfigureOVRProjectConfig()
    {
        var cfg = OVRProjectConfig.CachedProjectConfig;
        cfg.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>
        {
            OVRProjectConfig.DeviceType.Quest3,
            OVRProjectConfig.DeviceType.Quest3S,
        };
        cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required;   // passthrough-only game
        cfg.insightPassthroughEnabled = true;
        cfg.sceneSupport = OVRProjectConfig.FeatureSupport.Required;                // MRUK room (doors/walls)
        cfg.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
        cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
        cfg.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
        cfg.focusAware = true;
        OVRProjectConfig.CommitProjectConfig(cfg);
        Debug.Log("[HIS] OVRProjectConfig: passthrough=Required scene=Required anchors=Enabled devices=Quest3/3S");
    }

    // Physics layers the game relies on. Fixed indices so prefabs/scenes built by code agree with the TagManager.
    public const int DoorLayer = 8, WeaponLayer = 9, RoomLayer = 10;
    const string TmpEssentials = "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage";

    /// <summary>Layers and TextMesh Pro resources. Safe to run any time (idempotent).</summary>
    [MenuItem("HIS/Setup/Apply Layers and TMP")]
    public static void ApplyGameSettings()
    {
        EnsureLayer(DoorLayer, "Door");
        EnsureLayer(WeaponLayer, "Weapon");
        EnsureLayer(RoomLayer, "Room");
        EnsureLayer(11, "Outside");

        // (Fixed timestep 1/72 is set at runtime by MRBootstrap; the TimeManager asset didn't persist a SerializedObject edit on 6000.6.)

        // TMP ships its shaders + default font (LiberationSans SDF) as a package; without them every text renders blank.
        if (!AssetDatabase.IsValidFolder("Assets/TextMesh Pro"))
        {
            Debug.Log("[HIS] Importing TMP Essential Resources");
            AssetDatabase.ImportPackage(TmpEssentials, false);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[HIS] Game settings applied (layers Door/Weapon/Room, TMP essentials)");
    }

    static void EnsureLayer(int index, string name)
    {
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        var slot = layers.GetArrayElementAtIndex(index);
        if (slot.stringValue == name) return;
        if (!string.IsNullOrEmpty(slot.stringValue))
        {
            Debug.LogError($"[HIS] Layer {index} is '{slot.stringValue}', expected '{name}' — not overwriting");
            return;
        }
        slot.stringValue = name;
        tagManager.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[HIS] Layer {index} = {name}");
    }

    static void RunMetaProjectSetupToolFixAll()
    {
        // internal static void FixTasks(BuildTargetGroup, Func<...> filter, LogMessages, bool blocking, Action<...> onCompleted)
        var t = typeof(OVRProjectSetup);
        var fixTasks = t.GetMethod("FixTasks", BindingFlags.NonPublic | BindingFlags.Static);
        var logMessages = t.GetNestedType("LogMessages", BindingFlags.NonPublic);
        if (fixTasks == null || logMessages == null)
        {
            Debug.LogWarning("[HIS] OVRProjectSetup.FixTasks not found; run Meta > Tools > Project Setup Tool manually");
            return;
        }
        var all = Enum.ToObject(logMessages, 3); // LogMessages.All
        fixTasks.Invoke(null, new object[] { BuildTargetGroup.Android, null, all, true, null });
        Debug.Log("[HIS] Meta Project Setup Tool: FixAll(Android) executed");

    }
}
