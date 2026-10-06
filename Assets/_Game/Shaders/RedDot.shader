// HIS/RedDot — the AR-15's reflex-sight glass (Brett 2026-10-03: a red dot rather than a scope, "whatever will look the best but
// wont make the headset heat up"). A COLLIMATED dot: per fragment, the view ray from THIS eye through the glass is compared with
// the sight's zeroed axis (_ZeroDir, object space); where they agree within _DotRadius (radians) the dot glows. So the dot sits
// at infinity — on the target however the head moves behind the sight, fused by both eyes — like a real reflex sight, with no
// second camera and no render texture (a magnified scope can't magnify passthrough). Single-pass instanced: the per-eye camera
// position after UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX, as HIS/LitPBR's view direction. Elsewhere a faint coated-glass
// tint, a little stronger at grazing angles. No texture, no depth write; one small disc, so next to nothing to fill.
// Premultiplied output (the eye buffer's alpha is what passthrough composites under): the tint darkens a touch, the dot's
// glow adds light.
Shader "HIS/RedDot"
{
    Properties
    {
        _DotColor ("Dot Color", Color) = (1, 0.06, 0.03, 1)
        _DotRadius ("Dot Radius (radians)", Float) = 0.003
        _ZeroDir ("Zeroed Axis (object space)", Vector) = (0, 0, 1, 0)
        _Tint ("Glass Tint (rgb; a = opacity)", Color) = (0.3, 0.5, 0.7, 0.08)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back   // the disc faces the shooter; from the muzzle end it isn't drawn

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 zeroWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _DotColor;
                float _DotRadius;
                float4 _ZeroDir;
                half4 _Tint;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.zeroWS = TransformObjectToWorldDir(_ZeroDir.xyz); // the gun's aim: recoil and the hand carry it
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 view = normalize(input.positionWS - GetCameraPositionWS()); // this eye → this point of the glass
                float3 zero = normalize(input.zeroWS);
                // The angle between the view ray and the zeroed axis (small angles: |v × z|); looking back into the sight, none.
                float off = length(cross(view, zero));
                float ahead = step(0.0, dot(view, zero));
                float aa = max(fwidth(off), 1e-6);
                float core = saturate((_DotRadius - off) / aa + 0.5) * ahead;               // the dot, anti-aliased to the pixel
                float glow = exp(-off * off / (_DotRadius * _DotRadius * 4.0)) * 0.25 * ahead; // a little bloom round it
                float ndv = abs(dot(normalize(input.normalWS), view));
                float a = _Tint.a + (1.0 - _Tint.a) * 0.5 * pow(1.0 - ndv, 4.0);            // coated glass, more at a slant
                float3 rgb = _Tint.rgb * a + _DotColor.rgb * (core + glow);
                return half4(rgb, saturate(max(a, core)));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
