// HIS/DoorPortalMask — an invisible quad exactly over the doorway opening that marks its pixels in the stencil buffer (bit
// 8). The dark box behind the door (HIS/DoorVoid) draws only where this mark is: the classic portal trick, so the box is
// seen THROUGH the opening and never beside it. Without it, the box's side walls (1.8 m deep) showed as black bars over the
// real wall beside the frame whenever the door was looked at from an angle (Brett 0.1.17: "the black and stuff at angles"),
// because the doorway's depth mask switches real-wall occlusion off in a strip around the frame.
// Draws before the void (queue 2005 < 2010), after the leaf/trim (2000): a leaf in front of the opening keeps its pixels
// unmarked by the depth test, so the box never draws over the leaf either.
Shader "HIS/DoorPortalMask"
{
    Properties
    {
        [MainColor] _BaseColor("Unused (never drawn)", Color) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+5" "RenderPipeline" = "UniversalPipeline" }
        ColorMask 0
        ZWrite Off
        ZTest LEqual
        Cull Off
        Stencil
        {
            Ref 8
            WriteMask 8
            Comp Always
            Pass Replace
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.vertex.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack Off
}
