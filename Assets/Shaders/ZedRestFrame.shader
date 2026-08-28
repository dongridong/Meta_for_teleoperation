// The surround the video panel sits in — drawn on the inside of a large sphere
// around the viewer.
//
// At 1:1 angular size the panel covers only about half the headset's field of
// view, and the decision (UNITY_PORT_PROMPT.md section 4) is to accept that
// rather than magnify. What is left over should not be pure black: peripheral
// vision with nothing stable in it is worse than peripheral vision with a quiet,
// world-locked reference in it. So: a very dark neutral grey with a faint static
// grid — enough to give the eye something fixed to hold on to, low-contrast
// enough that noticing it means it has been drawn too strongly.
//
// World-locked, never head-locked. A surround that follows the head provides no
// reference at all, which defeats the purpose.
Shader "ZED/RestFrame"
{
    Properties
    {
        // Linear ~0.02. Not black, but close enough that it reads as "unlit room".
        _BaseColor ("Base colour (linear)", Color) = (0.02, 0.02, 0.021, 1)
        _GridColor ("Grid colour (linear)", Color) = (0.045, 0.045, 0.048, 1)

        // Grid lines per full turn / per full sweep of the sphere's UV.
        _GridDensity ("Grid density (lines)", Range(4, 128)) = 48

        // Line width as a fraction of one cell. Keep this small.
        _GridWidth ("Grid line width", Range(0.001, 0.2)) = 0.02
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ZedRestFrame"
            Tags { "LightMode" = "UniversalForward" }

            // Seen from the inside, so the outward-facing triangles are the ones
            // to discard.
            Cull Front
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _GridColor;
                float _GridDensity;
                float _GridWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Screen-space derivatives keep the lines a roughly constant
                // apparent thickness instead of aliasing into noise near the poles,
                // where a sphere's UV grid bunches up.
                float2 cell = frac(input.uv * _GridDensity);
                float2 toLine = min(cell, 1.0 - cell);
                float2 fw = fwidth(input.uv * _GridDensity) + 1e-5;
                float2 line2 = 1.0 - smoothstep(float2(0, 0), _GridWidth + fw, toLine);
                float grid = saturate(max(line2.x, line2.y));

                return half4(lerp(_BaseColor.rgb, _GridColor.rgb, grid), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
