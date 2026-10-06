// HIS/LitPBR — same PBR inputs as HIS/OcclusionLitPBR but never occluded by the Depth API (for worn/held weapons: the player's own body must not hide them).
Shader "HIS/LitPBR"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0,2)) = 1.0
        _MetallicMap ("Metallic (R)", 2D) = "black" {}
        _RoughnessMap ("Roughness (R)", 2D) = "gray" {}
        _Metallic ("Metallic Scale", Range(0,1)) = 1.0
        _Glossiness ("Smoothness Scale", Range(0,1)) = 1.0
        _OcclusionMap ("AO (R)", 2D) = "white" {}
        _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_instancing
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 posWorld : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicMap); SAMPLER(sampler_MetallicMap);
            TEXTURE2D(_RoughnessMap); SAMPLER(sampler_RoughnessMap);
            TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float4 _MainTex_ST;
            float _BumpScale, _Metallic, _Glossiness, _EnvironmentDepthBias;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs vertexInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = vertexInputs.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.normalWS = normalInputs.normalWS;
                output.tangentWS = float4(normalInputs.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.posWorld = vertexInputs.positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.uv;
                float4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * _Color;

                // Tangent-space normal map
                float3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
                float3 bitangent = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                float3 normalWS = normalize(TransformTangentToWorld(nTS, half3x3(input.tangentWS.xyz, bitangent, input.normalWS)));

                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = input.posWorld;
                lightingInput.normalWS = normalWS;
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.posWorld);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(input.posWorld);
                lightingInput.positionCS = input.positionCS;
                lightingInput.bakedGI = SampleSH(normalWS);

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo.rgb;
                s.alpha = 1;
                s.metallic = SAMPLE_TEXTURE2D(_MetallicMap, sampler_MetallicMap, uv).r * _Metallic;
                s.smoothness = (1.0 - SAMPLE_TEXTURE2D(_RoughnessMap, sampler_RoughnessMap, uv).r) * _Glossiness;
                s.occlusion = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).r;
                s.emission = 0;
                half4 color = UniversalFragmentPBR(lightingInput, s);
                return color;
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            HLSLPROGRAM
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
