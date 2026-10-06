// HIS/SecurityPorch — the virtual night exterior outside each door (HIS.Security.PorchSet), seen ONLY by that door's security
// camera (layer "Outside"; its renderers are switched on just for that camera's render). Unlit, with the lighting baked in
// here instead of URP lights (the Quest budget is one directional light):
//   • the camera's own IR illuminator: bright near the lens, soft inverse-square falloff, strongest in the middle of the view
//   • the porch lamp and a far streetlight as two point terms (_Lamp, _Street: world xyz, strength w)
//   • police light bars on a bad call (_FlashA/_FlashB: world xyz, strength w; 0 = off), softer falloff so they reach the porch
//   • surfaces from vertex colour × a procedural pattern chosen by vertex alpha (id = a*8): 0 plain, 1 grass, 2 concrete
//     with joints, 3 lap siding / boards, 4 asphalt, 5 foliage, 6 emissive (lamp glass, lit windows, the sky gradient)
// _Glow = 1 (the additive glow material): a soft round sprite from the uv, for the lamp halos.
// Two looks (research/23): the security camera's IR night vision (global _HIS_FeedView = 1, set around each feed render),
// and colour for the headset, which sees this set through an open doorway (DoorwayView: layer 11 through the doorway's
// stencil): moonlight, warm porch lamp, sodium streetlight, red/blue light bars (_HIS_FlashColA/B), each house its paint,
// fog into the night. The headset look is Depth-API occluded like HIS/DoorVoid (a hand in front of the doorway shows).
// Lives in Resources/Security so the build includes it (its materials are loaded from there).
Shader "HIS/SecurityPorch"
{
    Properties
    {
        _IR ("IR illuminator strength", Float) = 1.8
        _IRRange ("IR falloff distance (m)", Float) = 3
        _Ambient ("Ambient", Float) = 0.01
        _Lamp ("Porch lamp (world xyz, strength w)", Vector) = (0, 0, 0, 0)
        _LampRange ("Porch lamp range (m)", Float) = 1.8
        _Street ("Street light (world xyz, strength w)", Vector) = (0, 0, 0, 0)
        _StreetRange ("Street light range (m)", Float) = 6
        _Emission ("Emissive gain", Float) = 2
        _FlashA ("Police light A (world xyz, strength w)", Vector) = (0, 0, 0, 0)
        _FlashB ("Police light B (world xyz, strength w)", Vector) = (0, 0, 0, 0)
        _FlashRange ("Police light range (m)", Float) = 6
        _Fill ("Fill light: a floodlight on the house (world xyz, strength w; 0 = none)", Vector) = (0, 0, 0, 0)
        _FillRange ("Fill light range (m)", Float) = 4
        _Moon ("Moonlight (the headset look; the backyard has no streetlight)", Float) = 1
        _Glow ("Glow sprite mode", Float) = 0
        _Alpha ("Alpha (a see-through material: car glass; premultiplied, blend One OneMinusSrcAlpha)", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
            #include "Assets/_Game/Shaders/HISDepthOcclusion.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                half4 color : COLOR;
                META_DEPTH_VERTEX_OUTPUT(3)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float _IR;
                float _IRRange;
                float _Ambient;
                float4 _Lamp;
                float _LampRange;
                float4 _Street;
                float _StreetRange;
                float _Emission;
                float4 _FlashA;
                float4 _FlashB;
                float _FlashRange;
                float4 _Fill;
                float _FillRange;
                float _Moon;
                float _Glow;
                float _Alpha;
                float _SrcBlend;
                float _DstBlend;
                float _ZWrite;
            CBUFFER_END

            // Globals (not per material): which look, and the light bars' colours for the headset look.
            float _HIS_FeedView;
            float4 _HIS_FlashColA;
            float4 _HIS_FlashColB;

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionWS = TransformObjectToWorld(input.vertex.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normal);
                output.uv = input.uv;
                output.color = input.color;
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(output, input.vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                return output;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float a = Hash(i), b = Hash(i + float2(1, 0)), c = Hash(i + float2(0, 1)), d = Hash(i + float2(1, 1));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            half Pattern(int id, float2 uv)
            {
                UNITY_BRANCH
                if (id == 1) // grass: fine blades over clumps (grass is bright in IR)
                    return 0.62 + 0.45 * Noise(uv * 34.0) * Noise(uv * 7.0 + 3.1) + 0.3 * (Noise(uv * 1.7) - 0.5);
                else if (id == 2) // concrete, control joints every 1.2 m along u
                {
                    float n = 0.9 + 0.1 * Noise(uv * 28.0) + 0.12 * (Noise(uv * 2.5) - 0.5);
                    float j = frac(uv.x / 1.2);
                    return n * lerp(0.45, 1.0, smoothstep(0.0, 0.015, j) * smoothstep(0.0, 0.015, 1.0 - j));
                }
                else if (id == 3) // lap siding / boards 0.2 m along v: a shadow line under each board
                {
                    float b = frac(uv.y / 0.2);
                    return lerp(0.45, 1.0, smoothstep(0.0, 0.14, b)) * lerp(1.0, 0.88, b) * (0.93 + 0.07 * Noise(uv * float2(3.0, 50.0)));
                }
                else if (id == 4) // asphalt: speckle and patches
                    return 0.85 + 0.25 * (Hash(floor(uv * 80.0)) - 0.5) + 0.2 * (Noise(uv * 0.9) - 0.5);
                else if (id == 5) // foliage: clumpy, deep shadows between the leaves
                {
                    float n = Noise(uv * 8.0) * 0.6 + Noise(uv * 21.0) * 0.4;
                    return 0.3 + 1.1 * n * n;
                }
                return 1.0;
            }

            // ── the headset's colour look ──
            half3 Paint(int id, float3 P)
            {
                UNITY_BRANCH
                if (id == 1) return half3(0.42, 0.62, 0.30);                   // lawn
                else if (id == 2) return half3(1.0, 0.97, 0.92);               // concrete
                else if (id == 3)                                              // siding: each house its own paint
                {
                    float h = Hash(floor(P.xz / 7.0) + 0.37);
                    return h < 0.25 ? half3(0.80, 0.87, 0.97) : h < 0.5 ? half3(0.97, 0.91, 0.78) : h < 0.75 ? half3(0.80, 0.90, 0.80) : half3(0.95, 0.83, 0.78);
                }
                else if (id == 4) return half3(0.78, 0.82, 0.92);              // asphalt
                else if (id == 5) return half3(0.34, 0.58, 0.30);              // leaves
                return half3(1.0, 1.0, 1.0);
            }

            half3 FlashColour(float3 P, float3 N)
            {
                half3 c = 0;
                UNITY_BRANCH
                if (_FlashA.w + _FlashB.w > 0.0)
                {
                    float3 ta = _FlashA.xyz - P; float da = max(length(ta), 1e-3); float ra = da / _FlashRange;
                    float3 tb = _FlashB.xyz - P; float db = max(length(tb), 1e-3); float rb = db / _FlashRange;
                    c = _FlashA.w * _HIS_FlashColA.rgb * saturate(dot(N, ta / da) * 0.7 + 0.3) / (1.0 + ra * ra)
                      + _FlashB.w * _HIS_FlashColB.rgb * saturate(dot(N, tb / db) * 0.7 + 0.3) / (1.0 + rb * rb);
                }
                return c;
            }

            // A floodlight on the house (the backyard's, over the bins: Brett 0.1.43 "the right side ... just looks black").
            float FillLight(float3 P, float3 N)
            {
                UNITY_BRANCH
                if (_Fill.w <= 0.0) return 0.0;
                float3 tf = _Fill.xyz - P; float df = max(length(tf), 1e-3); float rf = df / _FillRange;
                return _Fill.w * saturate(dot(N, tf / df) * 0.8 + 0.2) / (1.0 + rf * rf * rf);
            }

            half3 NightFog(half3 c, float3 P)
            {
                float d = length(GetCameraPositionWS() - P);
                return lerp(half3(0.006, 0.008, 0.016), c, exp2(-d * 0.045));
            }

            half4 Occluded(Varyings input, half4 c)
            {
                HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, c, 0.06);
                return c;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                bool eye = _HIS_FeedView < 0.5;
                if (_Glow > 0.5)
                {
                    float r = length(input.uv * 2.0 - 1.0);
                    float g = saturate(1.0 - r);
                    half3 glow = input.color.rgb * (g * g * (0.6 + 0.4 * g));
                    if (eye)
                    {
                        // A halo by a light bar takes that bar's colour; every other halo is a warm lamp.
                        float3 P = input.positionWS;
                        half3 tint = half3(1.0, 0.8, 0.55);
                        if (_FlashA.w > 0.0 && distance(P, _FlashA.xyz) < 1.2) tint = normalize(_HIS_FlashColA.rgb + 1e-4) * 1.4;
                        else if (_FlashB.w > 0.0 && distance(P, _FlashB.xyz) < 1.2) tint = normalize(_HIS_FlashColB.rgb + 1e-4) * 1.4;
                        return Occluded(input, half4(glow * tint, 0.0));
                    }
                    return half4(glow, 0.0);
                }
                int id = (int)round(input.color.a * 8.0);
                if (id == 6)
                {
                    if (!eye) return half4(input.color.rgb * _Emission, 1.0);
                    // Lit windows and lamp glass warm; the dim sky gradient a deep night blue.
                    half lum = dot(input.color.rgb, half3(0.333, 0.333, 0.334));
                    half3 tint = lerp(half3(0.35, 0.45, 0.85), half3(1.0, 0.82, 0.55), smoothstep(0.06, 0.3, lum));
                    return Occluded(input, half4(NightFog(input.color.rgb * _Emission * tint, input.positionWS), 1.0));
                }
                if (eye)
                {
                    float3 P = input.positionWS;
                    float3 N = normalize(input.normalWS);
                    float3 tl = _Lamp.xyz - P; float dl = max(length(tl), 1e-3); float rl = dl / _LampRange;
                    float lamp = _Lamp.w * saturate(dot(N, tl / dl) * 0.75 + 0.25) / (1.0 + rl * rl * rl);
                    float3 ts = _Street.xyz - P; float ds = max(length(ts), 1e-3); float rs = ds / _StreetRange;
                    float street = _Street.w * saturate(dot(N, ts / ds) * 0.8 + 0.2) / (1.0 + rs * rs * rs);
                    half3 moon = half3(0.55, 0.66, 1.0) * (0.03 + 0.03 * saturate(N.y)) * _Moon;
                    half3 light = moon + lamp * half3(1.0, 0.78, 0.5) + street * half3(1.0, 0.7, 0.4) + FlashColour(P, N)
                                + FillLight(P, N) * half3(1.0, 0.92, 0.8);
                    half3 albedo = input.color.rgb * Pattern(id, input.uv) * Paint(id, P);
                    // Premultiplied by _Alpha (car glass: One OneMinusSrcAlpha): one blend state serves colour AND alpha, so
                    // glass over the car keeps the eye buffer's alpha at 1. With SrcAlpha it wrote 0.4² + 0.6 = 0.76 there,
                    // and the compositor showed the real room through every window (Brett 0.1.41: light "peeking through").
                    return Occluded(input, half4(NightFog(albedo * light, P) * _Alpha, _Alpha));
                }

                float3 P = input.positionWS;
                float3 N = normalize(input.normalWS);
                float3 toCam = GetCameraPositionWS() - P;
                float dc = max(length(toCam), 1e-3);
                float3 vc = toCam / dc;
                float cone = saturate((dot(-vc, GetViewForwardDir()) - 0.25) / 0.55);
                // Steeper than inverse-square (the camera's auto-exposure is set for the porch): bright underfoot, dark by the street.
                float dr = dc / _IRRange;
                float ir = _IR * (0.3 + 0.7 * saturate(dot(N, vc))) * lerp(0.4, 1.0, cone) / (1.0 + dr * dr * dr);
                float3 tl = _Lamp.xyz - P; float dl = max(length(tl), 1e-3); float rl = dl / _LampRange;
                float lamp = _Lamp.w * saturate(dot(N, tl / dl) * 0.75 + 0.25) / (1.0 + rl * rl * rl);
                float3 ts = _Street.xyz - P; float ds = max(length(ts), 1e-3); float rs = ds / _StreetRange;
                float street = _Street.w * saturate(dot(N, ts / ds) * 0.8 + 0.2) / (1.0 + rs * rs * rs); // a pool, not a wash
                // Police light bars (a strobe, driven from the CPU): inverse-square, so the far porch catches a little of it.
                float flash = 0.0;
                UNITY_BRANCH
                if (_FlashA.w + _FlashB.w > 0.0)
                {
                    float3 ta = _FlashA.xyz - P; float da = max(length(ta), 1e-3); float ra = da / _FlashRange;
                    float3 tb = _FlashB.xyz - P; float db = max(length(tb), 1e-3); float rb = db / _FlashRange;
                    flash = _FlashA.w * saturate(dot(N, ta / da) * 0.7 + 0.3) / (1.0 + ra * ra)
                          + _FlashB.w * saturate(dot(N, tb / db) * 0.7 + 0.3) / (1.0 + rb * rb);
                }
                half3 albedo = input.color.rgb * Pattern(id, input.uv);
                return half4(albedo * (_Ambient + ir + lamp + street + flash + FillLight(P, N)) * _Alpha, _Alpha); // (premultiplied, as above)
            }
            ENDHLSL
        }
    }

    FallBack Off
}
