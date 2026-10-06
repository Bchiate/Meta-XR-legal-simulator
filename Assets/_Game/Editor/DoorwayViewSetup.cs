using System.Collections.Generic;
using HIS.Security;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.OpenXR;

/// <summary>
/// The outside through an open door (Brett 0.1.28, research/23): the headset draws the "Outside" layer (11: the porch set,
/// the police cars, men behind the door) ONLY through the doorway — the stencil mark HIS/DoorPortalMask leaves in the
/// opening — the portal trick Drop Dead: Home Invasion / Guardian of Realms / Last Stand use for their virtual windows.
///   • Each pipeline asset's main renderer (index 0, the headset): layer 11 out of the default opaque/transparent passes;
///     two RenderObjects features draw it (opaque after the opaques, glows after the transparents) with stencil == 8.
///   • A second renderer, Feed_Renderer (index 1, every layer, no features) for the security cameras (SecurityCamera
///     picks it), so the feed still draws the porch everywhere.
/// Idempotent; also run by the build (HISBuildProcessor). By hand: scripts/unity-batch.sh DoorwayViewSetup.Configure
/// </summary>
public static class DoorwayViewSetup
{
    const string FeedPath = "Assets/Settings/Feed_Renderer.asset";
    static readonly (string asset, string renderer)[] Pipelines =
    {
        ("Assets/Settings/Mobile_RPAsset.asset", "Assets/Settings/Mobile_Renderer.asset"),
        ("Assets/Settings/PC_RPAsset.asset", "Assets/Settings/PC_Renderer.asset"),
    };

    /// <summary>
    /// Android OpenXR depth submission. None (Meta's recommendation for Quest apps): the compositor then draws its own content
    /// (the Guardian boundary, system panels) over the game as usual. Submitted (24-bit, 0.1.31–0.1.41), it depth-composited
    /// them against the game's depth: near the open door (a near leaf beside the far fake outside) Brett saw see-through
    /// shapes that no screenshot or video caught — captures don't include system overlays (0.1.41). The eye buffer keeps a
    /// stencil either way (Unity asks for 24-bit-or-greater when nothing is submitted; 16-bit submission was the stencil-less
    /// case): DoorwayView logs the format at startup and refuses the doorway view without one. HIS_DEPTH_SUBMISSION=24
    /// (environment, at build time) brings the old setting back for an A/B.
    /// </summary>
    public static OpenXRSettings.DepthSubmissionMode WantedDepthSubmission =>
        System.Environment.GetEnvironmentVariable("HIS_DEPTH_SUBMISSION") == "24" ? OpenXRSettings.DepthSubmissionMode.Depth24Bit : OpenXRSettings.DepthSubmissionMode.None;

    public static void Configure()
    {
        bool changed = false;
        // Never 16-bit: with 16-bit submission the Quest's depth buffer had no stencil, the stencil test silently passed
        // everywhere on the headset and layer 11 showed through walls (Brett 0.1.29: the black box in the room, a man's gun
        // through the closed door). See WantedDepthSubmission.
        var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(UnityEditor.BuildTargetGroup.Android);
        var want = WantedDepthSubmission;
        if (openXr != null && openXr.depthSubmissionMode != want)
        {
            openXr.depthSubmissionMode = want;
            EditorUtility.SetDirty(openXr);
            changed = true;
            Debug.Log($"[HIS] DoorwayViewSetup: Android OpenXR depth submission → {want}");
        }
        // The feed renderer first, copied from the untouched mobile renderer (all layers, no features).
        if (AssetDatabase.LoadAssetAtPath<UniversalRendererData>(FeedPath) == null)
        {
            var src = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(Pipelines[0].renderer);
            if (src == null) { Debug.LogError("[HIS] DoorwayViewSetup: no Mobile_Renderer to copy"); return; }
            AssetDatabase.CopyAsset(Pipelines[0].renderer, FeedPath);
            AssetDatabase.ImportAsset(FeedPath);
            var feedNew = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(FeedPath);
            feedNew.opaqueLayerMask = ~0; feedNew.transparentLayerMask = ~0;
            var fso = new SerializedObject(feedNew);
            fso.FindProperty("m_RendererFeatures").ClearArray();
            fso.FindProperty("m_RendererFeatureMap").ClearArray();
            fso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(feedNew);
            changed = true;
            Debug.Log($"[HIS] DoorwayViewSetup: made {FeedPath}");
        }
        var feed = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(FeedPath);

        foreach (var (assetPath, rendererPath) in Pipelines)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);
            var main = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (asset == null || main == null) { Debug.LogWarning($"[HIS] DoorwayViewSetup: skipping {assetPath}"); continue; }

            // Renderer list: [main, feed].
            var aso = new SerializedObject(asset);
            var list = aso.FindProperty("m_RendererDataList");
            if (list.arraySize < 2 || list.GetArrayElementAtIndex(1).objectReferenceValue != feed)
            {
                if (list.arraySize < 2) list.arraySize = 2;
                list.GetArrayElementAtIndex(1).objectReferenceValue = feed;
                aso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
                changed = true;
            }

            // Main renderer: no layer 11 in the default passes...
            int outside = 1 << PorchSet.OutsideLayer;
            if ((main.opaqueLayerMask & outside) != 0 || (main.transparentLayerMask & outside) != 0)
            {
                main.opaqueLayerMask &= ~outside;
                main.transparentLayerMask &= ~outside;
                EditorUtility.SetDirty(main);
                changed = true;
            }
            // ...but through the doorway.
            changed |= EnsureFeature(main, DoorwayView.OpaqueFeature, RenderPassEvent.AfterRenderingOpaques, RenderQueueType.Opaque);
            changed |= EnsureFeature(main, DoorwayView.GlowFeature, RenderPassEvent.AfterRenderingTransparents, RenderQueueType.Transparent);
        }

        if (changed) AssetDatabase.SaveAssets();
        Debug.Log($"[HIS] DoorwayViewSetup: {(changed ? "configured" : "already set")} (main renderers: layer 11 only through the doorway; {FeedPath} for the feeds)");
    }

    static bool EnsureFeature(UniversalRendererData data, string name, RenderPassEvent evt, RenderQueueType queue)
    {
        foreach (var f in data.rendererFeatures)
            if (f != null && f.name == name) return Apply((RenderObjects)f, evt, queue, data);
        var feature = ScriptableObject.CreateInstance<RenderObjects>();
        feature.name = name;
        Apply(feature, evt, queue, data);
        AssetDatabase.AddObjectToAsset(feature, data);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
        var so = new SerializedObject(data);
        var features = so.FindProperty("m_RendererFeatures");
        var map = so.FindProperty("m_RendererFeatureMap");
        features.arraySize++;
        features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
        map.arraySize++;
        map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(data);
        Debug.Log($"[HIS] DoorwayViewSetup: added {name} to {data.name}");
        return true;
    }

    /// <summary>Layer 11, its own shaders, depth test on, stencil == 8 (the open doorway). True if anything changed.</summary>
    static bool Apply(RenderObjects f, RenderPassEvent evt, RenderQueueType queue, Object owner)
    {
        var s = f.settings;
        bool changed = s.Event != evt || s.filterSettings.RenderQueueType != queue || s.filterSettings.LayerMask != 1 << PorchSet.OutsideLayer
                       || !s.stencilSettings.overrideStencilState || s.stencilSettings.stencilReference != 8
                       || s.stencilSettings.stencilCompareFunction != CompareFunction.Equal
                       || s.overrideMode != RenderObjects.RenderObjectsSettings.OverrideMaterialMode.None || !f.isActive;
        s.passTag = f.name;
        s.Event = evt;
        s.filterSettings.RenderQueueType = queue;
        s.filterSettings.LayerMask = 1 << PorchSet.OutsideLayer;
        s.filterSettings.PassNames = new string[0];
        s.overrideMode = RenderObjects.RenderObjectsSettings.OverrideMaterialMode.None; // their own shaders
        s.overrideMaterial = null;
        s.overrideShader = null;
        s.overrideDepthState = false;
        s.stencilSettings.overrideStencilState = true;
        s.stencilSettings.stencilReference = 8;
        s.stencilSettings.stencilCompareFunction = CompareFunction.Equal;
        s.stencilSettings.passOperation = StencilOp.Keep;
        s.stencilSettings.failOperation = StencilOp.Keep;
        s.stencilSettings.zFailOperation = StencilOp.Keep;
        s.cameraSettings.overrideCamera = false;
        f.SetActive(true);
        if (changed) { EditorUtility.SetDirty(f); EditorUtility.SetDirty(owner); }
        return changed;
    }
}
