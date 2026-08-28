// Draws the ZED's side-by-side stereo pair on one world-locked quad, giving each
// eye a different horizontal slice of the SAME texture. The stream is never split
// into two textures — there is only ever one decoded picture per frame.
//
// Three things happen here, and all three are load-bearing (UNITY_PORT_PROMPT.md):
//
//  * Eye split with a convergence-plane shift (HIT). Without the shift, parallax
//    at infinity is zero, the whole scene floats in front of the display, and the
//    eyes tire fighting to fuse it. _HitUv is PER-EYE UV; halving it converts to
//    full-SBS UV because each eye owns half the width.
//        left  u in [0 + hitFull, 0.5 + hitFull]
//        right u in [0.5 - hitFull, 1 - hitFull]
//
//  * Edge feathering. The panel covers roughly half the headset's field of view at
//    1:1 angular size, and what makes the surround annoying is the hard rectangular
//    boundary, not the area. Fading alpha to zero at the edges makes the eye stop
//    registering a border at all.
//
//  * Single Pass Instanced support. This is not optional — with SPI on and a
//    shader that ignores unity_StereoEyeIndex, BOTH eyes get the same half and
//    there is no stereo at all, which reads as "the depth looks off" rather than
//    as an obvious failure.
Shader "ZED/SbsStereo"
{
    Properties
    {
        _MainTex ("SBS Video", 2D) = "black" {}

        // Per-eye UV shift that places the convergence plane. Computed on the CPU
        // from the camera calibration; see ZedCalibration.HitUv.
        _HitUv ("HIT (per-eye UV)", Float) = 0.011069

        // Edge fade widths as a fraction of the panel. Separate per axis on
        // purpose: the vertical letterbox is far taller than the side margins.
        _FeatherX ("Feather X (fraction)", Range(0, 0.5)) = 0.04
        _FeatherY ("Feather Y (fraction)", Range(0, 0.5)) = 0.04

        // Escape hatches for the two things that are impossible to predict from
        // here, because they depend on how the decoder hands the texture over.
        // Both default to off; flip them if the picture is upside down, or if the
        // colour comes out washed out / too dark.
        [Toggle] _FlipV ("Flip vertically", Float) = 0
        [Toggle] _SrgbToLinear ("Convert sRGB -> linear", Float) = 0
    }

    SubShader
    {
        // Transparent because the feathered edges must blend with the rest frame
        // behind them. No depth write: this is a backdrop, not geometry.
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ZedSbsStereo"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Core.hlsl does not pull in Color.hlsl, and SRGBToLinear lives there.
            // Without this the shader compiles in the editor but fails on gles3 and
            // vulkan — i.e. it only breaks on the device, as a magenta panel.
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _HitUv;
                float _FeatherX;
                float _FeatherY;
                float _FlipV;
                float _SrgbToLinear;
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
                // Sets unity_StereoEyeIndex. Everything below depends on it.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 panelUv = input.uv;
                if (_FlipV > 0.5)
                    panelUv.y = 1.0 - panelUv.y;

                // Per-eye UV -> full-SBS UV: each eye owns half the width.
                float hitFull = _HitUv * 0.5;
                float u0 = (unity_StereoEyeIndex == 0) ? (0.0 + hitFull) : (0.5 - hitFull);
                float2 uv = float2(u0 + panelUv.x * 0.5, panelUv.y);

                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);

                if (_SrgbToLinear > 0.5)
                    c.rgb = SRGBToLinear(c.rgb);

                // Distance to the nearest edge along each axis, in panel fractions.
                // Fading both independently keeps the wide side margins and the
                // tall vertical ones from needing the same width.
                float2 toEdge = min(input.uv, 1.0 - input.uv);
                float ax = _FeatherX > 0.0001 ? smoothstep(0.0, _FeatherX, toEdge.x) : 1.0;
                float ay = _FeatherY > 0.0001 ? smoothstep(0.0, _FeatherY, toEdge.y) : 1.0;

                c.a = ax * ay;
                return c;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
