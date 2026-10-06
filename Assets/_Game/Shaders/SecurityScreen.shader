// HIS/SecurityScreen — the wall monitor's picture: a doorbell/eave camera at night (HIS.Security.SecurityMonitor).
// The feed (a small mipmapped RenderTexture from HIS.Security.SecurityCamera, ≤ 12 fps) is shown as an IR night-vision
// picture: grayscale, a per-camera barrel/fisheye squeeze (_Barrels), the smart-zoom crop (_MainCrop/_InsetCrop: the
// camera's zoom onto a man in view), grain, vignette, faint rolling scanlines, a bloom lift on bright spots from mip 2. A second feed can sit in a corner (picture-in-picture, _InsetRect) in the same draw, so nothing z-fights on the
// 16-bit eye depth. The REC dot and the motion border flash are drawn here too (no extra quads).
// Occluded by real things in front of it (the player's hand) through the Depth API like HIS/OcclusionUnlitBias, with
// Meta's ~0.06 bias: the screen sits 2 cm off a real wall, so depth noise must not eat it.
Shader "HIS/SecurityScreen"
{
    Properties
    {
        [MainTexture] _MainTex ("Feed", 2D) = "black" {}
        _InsetTex ("Inset feed", 2D) = "black" {}
        _InsetRect ("Inset rect in screen uv (min xy, max zw; off when zw <= xy)", Vector) = (0, 0, 0, 0)
        _Aspect ("Screen aspect (w/h)", Float) = 1.7778
        _Exposure ("Exposure", Float) = 1.4
        _Grain ("Grain", Range(0, 1)) = 0.08
        _Barrels ("Barrel squeeze (x main, y inset)", Vector) = (0.2, 0.2, 0, 0)
        _MainCrop ("Main feed crop (xy corner, zw size)", Vector) = (0, 0, 1, 1)
        _InsetCrop ("Inset feed crop (xy corner, zw size)", Vector) = (0, 0, 1, 1)
        _Vignette ("Vignette", Range(0, 2)) = 0.9
        _Scan ("Scanlines", Range(0, 1)) = 0.05
        _Bloom ("Bloom lift", Range(0, 3)) = 0.9
        _Tint ("Tint", Color) = (0.94, 1.0, 0.97, 1)
        _Flash ("Motion flash (x main, y inset)", Vector) = (0, 0, 0, 0)
        _Rec ("REC dot (uv xy, radius z, on w)", Vector) = (0.955, 0.915, 0.012, 1)
        _Power ("Screen on", Range(0, 1)) = 1
        _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.06
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        // Premultiplied like HIS/OcclusionLitPBR: where the Depth API occludes it, rgb and alpha go to 0 (passthrough shows).
        Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
        ZWrite On

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
            #include "Assets/_Game/Shaders/HISDepthOcclusion.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                META_DEPTH_VERTEX_OUTPUT(1)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_InsetTex);
            SAMPLER(sampler_InsetTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float4 _InsetTex_TexelSize;
                float4 _InsetRect;
                float _Aspect;
                float _Exposure;
                float _Grain;
                float4 _Barrels;
                float4 _MainCrop;
                float4 _InsetCrop;
                float _Vignette;
                float _Scan;
                float _Bloom;
                half4 _Tint;
                float4 _Flash;
                float4 _Rec;
                float _Power;
                float _EnvironmentDepthBias;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = input.uv;
                output.positionCS = TransformObjectToHClip(input.vertex.xyz);
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(output, input.vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                return output;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(443.897, 441.423));
                p += dot(p, p.yx + 19.19);
                return frac((p.x + p.y) * p.x);
            }

            half Luma(half3 c) { return dot(c, half3(0.2126, 0.7152, 0.0722)); }

            // One feed at local uv (0..1 over its own rect): barrel squeeze, luma, bloom lift, exposure. Returns a display
            // value in gamma space (0..1), so grain and scanlines are even to the eye (added in linear, a dark picture turned
            // to salt-and-pepper); r2 = squared radius (0 centre, 1 corner) for the vignette.
            half Feed(TEXTURE2D_PARAM(tex, samp), float4 crop, float barrel, float2 local, float aspect, out float r2)
            {
                // Barrel: the centre is magnified, the edges squeezed; corners stay on the corners. Then the zoom crop.
                float2 d = (local - 0.5) * float2(aspect, 1.0);
                r2 = dot(d, d) / (0.25 * (aspect * aspect + 1.0));
                float2 src = 0.5 + (local - 0.5) * (1.0 - barrel + barrel * r2);
                src = crop.xy + saturate(src) * crop.zw;
                half lum = Luma(SAMPLE_TEXTURE2D(tex, samp, src).rgb);    // trilinear: no sparkle from across the room
                // Bloom-ish lift from mip 2 (a 4x blur for free): only its bright part is added (lamps, IR-lit faces).
                half blur = Luma(SAMPLE_TEXTURE2D_LOD(tex, samp, src, 2.0).rgb);
                lum += max(0.0, blur - 0.35) * _Bloom;
                lum = 1.0 - exp(-lum * _Exposure);                 // soft shoulder: bright spots bloom out, never clip hard
                return pow(max(lum, 0.0), 1.0 / 2.2);
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.uv;

                bool hasInset = _InsetRect.z > _InsetRect.x && _InsetRect.w > _InsetRect.y;
                bool inInset = hasInset && all(uv >= _InsetRect.xy) && all(uv <= _InsetRect.zw);
                float2 size = inInset ? (_InsetRect.zw - _InsetRect.xy) : float2(1.0, 1.0);
                float2 local = inInset ? (uv - _InsetRect.xy) / size : uv;
                float aspect = _Aspect * size.x / size.y;

                // Both feeds sampled, one kept (no gradient ops in divergent flow; 4 taps is nothing on a 512-px picture).
                float r2Inset, r2Main;
                half gInset = Feed(TEXTURE2D_ARGS(_InsetTex, sampler_InsetTex), _InsetCrop, _Barrels.y, local, aspect, r2Inset);
                half gMain = Feed(TEXTURE2D_ARGS(_MainTex, sampler_MainTex), _MainCrop, _Barrels.x, local, aspect, r2Main);
                half g = inInset ? gInset : gMain;
                float r2 = inInset ? r2Inset : r2Main;
                g *= 1.0 - _Vignette * 0.45 * r2 * r2;              // lens falloff toward the corners

                // Sensor grain, a touch stronger in the dark. Faded out where a grain cell gets smaller than a
                // display pixel (the screen seen from across the room would sparkle).
                float t = floor(_Time.y * 12.0); // the feed's own rate (not 24 Hz shimmer against 72 Hz)
                float2 cells = local * (inInset ? float2(128.0, 72.0) : float2(256.0, 144.0));
                float cellPx = max(fwidth(cells.x), fwidth(cells.y));
                float n = Hash(floor(cells) + t * 1.618);
                g += (n - 0.5) * _Grain * (1.1 - g * 0.5) * saturate(1.6 - cellPx);
                // Faint scanlines (same fade) and a slow bright roll bar (CCTV).
                float lines = local.y * 120.0;
                float scan = (0.5 + 0.5 * sin(lines * 6.28318)) * saturate(1.5 - fwidth(lines) * 2.0);
                float roll = exp(-pow(frac(local.y * 0.7 - _Time.y * 0.11) - 0.5, 2.0) * 90.0);
                g *= 1.0 - _Scan * scan;
                g += roll * 0.025;
                half lum = g;

                half3 col = saturate(lum) * _Tint.rgb;

                // Motion: the feed's border pulses (red-white).
                float edge = min(min(local.x, 1.0 - local.x) * aspect, min(local.y, 1.0 - local.y));
                float flash = inInset ? _Flash.y : _Flash.x;
                col = lerp(col, half3(1.0, 0.25, 0.2), saturate(flash) * (1.0 - smoothstep(0.012, 0.026, edge)));
                // The inset's frame.
                if (inInset) col = lerp(col, half3(0.78, 0.8, 0.8), 1.0 - smoothstep(0.004, 0.009, edge));

                // REC dot (main picture only), a round dot in screen space.
                float2 rd = (uv - _Rec.xy) * float2(_Aspect, 1.0);
                float dot01 = (1.0 - smoothstep(_Rec.z * 0.8, _Rec.z, length(rd))) * step(0.5, _Rec.w) * (inInset ? 0.0 : 1.0);
                col = lerp(col, half3(0.95, 0.06, 0.04), dot01);

                col = lerp(half3(0.1, 0.105, 0.11), col, _Power);
                half4 finalColor = half4(pow(saturate(col), 2.2), 1.0); // gamma → linear (the eye buffer encodes it back)
                HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, finalColor, _EnvironmentDepthBias);
                return finalColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
