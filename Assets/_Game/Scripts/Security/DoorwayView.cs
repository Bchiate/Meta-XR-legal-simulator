using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HIS.Security
{
    /// <summary>
    /// The outside through an open door (Brett 0.1.28: "make it not black ... when the doors open ... continuity with the
    /// feed"; research/23). The door camera's world (PorchSet, the police cars, men behind the door: layer 11) is drawn by the
    /// headset only through the doorway: the main renderer leaves layer 11 out of its normal passes and two RenderObjects
    /// passes draw it where HIS/DoorPortalMask marked the opening (stencil 8) — set up by the editor's DoorwayViewSetup. The
    /// security cameras use the second renderer (<see cref="FeedRenderer"/>), which draws everything as before.
    /// <see cref="Available"/> only when the renderer really is set up that way: otherwise the headset keeps culling layer 11
    /// (a man behind the closed door must never show through the wall) and the doorway stays the dark box.
    /// </summary>
    public static class DoorwayView
    {
        public const string OpaqueFeature = "HIS Outside Through Doorway";
        public const string GlowFeature = "HIS Outside Through Doorway (glow)";
        /// <summary>The security cameras' renderer (every layer, no doorway passes).</summary>
        public const int FeedRenderer = 1;

        static int s_checkedFrame = -1;
        static bool s_available;
        static Camera s_masked;
        static bool s_noStencilLogged, s_formatLogged;

        /// <summary>The headset draws layer 11 only through open doorways (the renderer is set up for it).</summary>
        public static bool Available
        {
            get
            {
                if (Time.frameCount == s_checkedFrame) return s_available;
                s_checkedFrame = Time.frameCount;
                s_available = Check();
                return s_available;
            }
        }

        static bool Check()
        {
            // No stencil in the headset's depth buffer (16-bit): the doorway test can't work — never draw layer 11 then.
            if (UnityEngine.XR.XRSettings.enabled && !s_noStencilLogged)
            {
                var fmt = UnityEngine.XR.XRSettings.eyeTextureDesc.depthStencilFormat;
                if (!s_formatLogged && fmt != UnityEngine.Experimental.Rendering.GraphicsFormat.None)
                { s_formatLogged = true; Debug.Log($"[HIS] DoorwayView: eye buffer depth {fmt}"); }
                if (fmt != UnityEngine.Experimental.Rendering.GraphicsFormat.None && !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsStencilFormat(fmt))
                {
                    s_noStencilLogged = true;
                    Debug.LogWarning($"[HIS] DoorwayView: the eye buffer's depth ({fmt}) has no stencil — the outside stays feed-only");
                }
            }
            if (s_noStencilLogged) return false;
            return RendererSetUp();
        }

        // The renderer side, per URP asset (PorchSet reads Available every frame while a door is open, and the feature
        // names are string allocations each read). The asset doesn't change at runtime; a quality switch to another asset
        // is a new key. Only the stencil check above runs every frame.
        static UniversalRenderPipelineAsset s_rendererAsset;
        static bool s_rendererSetUp, s_hasFeed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_rendererAsset = null; s_rendererSetUp = s_hasFeed = false; } // (an edit between plays)

        /// <summary>The main renderer leaves layer 11 to the doorway passes and a feed renderer exists (cached per asset).</summary>
        static bool RendererSetUp() => ReadRenderers() && s_rendererSetUp;

        /// <summary>Fills the per-asset cache; false with no URP asset.</summary>
        static bool ReadRenderers()
        {
            var asset = UniversalRenderPipeline.asset;
            if (asset == null) return false;
            if (asset == s_rendererAsset) return true;
            s_rendererAsset = asset;
            var list = asset.rendererDataList;
            s_hasFeed = list.Length > FeedRenderer && list[FeedRenderer] != null;
            s_rendererSetUp = false;
            if (!s_hasFeed || list[0] is not UniversalRendererData main) return true;
            int outside = 1 << PorchSet.OutsideLayer;
            if ((main.opaqueLayerMask & outside) != 0 || (main.transparentLayerMask & outside) != 0) return true;
            foreach (var f in main.rendererFeatures) if (f != null && f.isActive && f.name == OpaqueFeature) s_rendererSetUp = true;
            return true;
        }

        /// <summary>
        /// A security camera: the feed renderer (draws the porch everywhere, not just through a doorway) — whenever it exists,
        /// even with <see cref="Available"/> false (no stencil in the eye buffer): the main renderer then still leaves layer 11
        /// to the doorway passes, which need stencil 8, so a feed on it would show an empty porch.
        /// </summary>
        public static void UseFeedRenderer(Camera cam)
        {
            if (cam == null || !ReadRenderers() || !s_hasFeed) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null) data.SetRenderer(FeedRenderer);
        }

        /// <summary>
        /// Every frame (SecurityCamera's update): the headset camera includes layer 11 in its culling (the doorway passes
        /// need it in the culled set; the normal passes leave it out) — only while <see cref="Available"/>.
        /// </summary>
        public static void KeepHeadsetMask()
        {
            var cam = Camera.main;
            if (cam == null || cam == s_masked && (cam.cullingMask & (1 << PorchSet.OutsideLayer)) != 0) return;
            if (!Available) { if (cam != null) cam.cullingMask &= ~(1 << PorchSet.OutsideLayer); return; }
            cam.cullingMask |= 1 << PorchSet.OutsideLayer;
            s_masked = cam;
            Debug.Log($"[HIS] DoorwayView: {cam.name} draws the outside through open doorways");
        }
    }
}
