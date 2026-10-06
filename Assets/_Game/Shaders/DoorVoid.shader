// HIS/DoorVoid — the dark box behind the virtual door, drawn ONLY through the doorway opening (stencil bit 8, written by
// HIS/DoorPortalMask). Unlit near-black, occluded by real things in front of the doorway (the player's hand reaching through)
// via the Depth API like HIS/OcclusionUnlitBias, with Meta's ~0.06 bias so depth noise at the frame can't pepper it.
// Queue 2010: after the leaf/trim and the portal mask.
Shader "HIS/DoorVoid"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (0.012, 0.012, 0.016, 1)
        _EnvironmentDepthBias("Environment Depth Bias", Float) = 0.06
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+10" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        // Off (0.1.29): with the outside drawn through the doorway (DoorwayView, after the opaques) the box is only its dark
        // backdrop, behind the porch set — it must not hide it in depth. (Someone in the doorway still draws over it: he
        // comes first, queue 2000.)
        ZWrite Off
        Stencil
        {
            Ref 8
            ReadMask 8
            Comp Equal
        }

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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                META_DEPTH_VERTEX_OUTPUT(0)
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _EnvironmentDepthBias;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.vertex.xyz);
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(output, input.vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 finalColor = _BaseColor;
                HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, finalColor, _EnvironmentDepthBias);
                return finalColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
