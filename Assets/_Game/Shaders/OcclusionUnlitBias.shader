// HIS/OcclusionUnlitBias — Meta's EnvironmentDepth/URP/OcclusionUnlit (alpha-blended over passthrough, hidden by real
// furniture/hands through the Depth API) plus _EnvironmentDepthBias and ZWrite Off. For light and marks laid ON the real
// room (the bad-call searchlight disc, the mugshot height chart): Meta's shader passes a bias of 0, so a quad 1-2 cm
// off a wall 3 m away gets eaten in patches by depth noise (research/05 §46,72; research/02 §35). ~0.06 is Meta's value
// for things flush with real surfaces.
Shader "HIS/OcclusionUnlitBias"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        _EnvironmentDepthBias("Environment Depth Bias", Float) = 0.06
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        // One OneMinusSrcAlpha on alpha so it blends properly against the transparent (passthrough) background.
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ZWrite Off

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

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
                float _EnvironmentDepthBias;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionCS = TransformObjectToHClip(input.vertex.xyz);
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(output, input.vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 finalColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, finalColor, _EnvironmentDepthBias);
                return finalColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
