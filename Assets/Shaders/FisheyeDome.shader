// Draws a stereo fisheye stream (the stereo viewer server's /stereo_raw and
// /stereo_h264 paths) as a dome around the viewer, so the picture stays fixed in
// space while the head turns.
//
// This is a port of the fragment shader in that server's own web page, which is the
// reference: where the two disagree, the page is right. Lens model: EQUIDISTANT
// fisheye — the angle from the optical axis grows linearly with the distance from
// the image circle's centre.
//
// The dome is a sphere drawn from the inside and centred on the head
// (FisheyeDome.cs keeps it there). Its object-space position IS the view
// direction, so the per-pixel work is: direction -> (theta, phi) -> point in the
// image circle -> texel. Object space already has the recentre yaw in it, because
// the dome's own rotation is that yaw.
//
// Separate from ZedSbsStereo on purpose: the ZED path draws a flat 1:1 panel and
// must not change when this one does.
Shader "ZED/FisheyeDome"
{
    Properties
    {
        _MainTex ("Video (side-by-side or single lens)", 2D) = "black" {}
        _HalfFov ("Half the lens field of view (radians)", Float) = 1.6580628
        _Radius ("Image circle radius / eye image width", Float) = 0.5
        _CentreX ("Image circle centre x (0..1 of the eye image)", Float) = 0.5
        _CentreY ("Image circle centre y (0..1 from the TOP)", Float) = 0.5
        _Sep ("Eye images turned against each other (radians)", Float) = 0
        [Toggle] _Mono ("Single-lens frame", Float) = 0
        [Toggle] _Swap ("Swap eyes", Float) = 0
        [Toggle] _FlipV ("Flip vertically", Float) = 0
    }

    SubShader
    {
        // Right after the background: everything else in the scene (hands, status
        // dots, the control panel) draws over it. No depth is written, so nothing
        // has to fight a sphere 20 m away for the depth buffer.
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Background+1"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "FisheyeDome"
            Tags { "LightMode" = "UniversalForward" }

            Cull Front      // seen from inside
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;   // (1/w, 1/h, w, h), set by Unity with the texture
                float _HalfFov;
                float _Radius;
                float _CentreX;
                float _CentreY;
                float _Sep;
                float _Mono;
                float _Swap;
                float _FlipV;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dirOS      : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // Normalised per pixel, not here: the direction to a point on a flat
                // triangle is exact, the interpolated vertex normal is not.
                output.dirOS = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Sets unity_StereoEyeIndex. The eye choice below depends on it.
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                bool rightEye = unity_StereoEyeIndex == 1;
                float3 d = normalize(input.dirOS);

                // Each eye's image turned by half the separation, in opposite
                // directions — moves where the two eyes converge.
                float eyeYaw = (rightEye ? -_Sep : _Sep) * 0.5;
                float c = cos(eyeYaw), s = sin(eyeYaw);
                d = float3(c * d.x + s * d.z, d.y, -s * d.x + c * d.z);

                // Angle from straight ahead (+Z in Unity; the page's -Z in WebXR).
                float theta = acos(clamp(d.z, -1.0, 1.0));
                if (theta > _HalfFov)
                    return half4(0, 0, 0, 1);
                float phi = atan2(d.y, d.x);

                bool mono = _Mono > 0.5;
                float eyeW = mono ? 1.0 : 0.5;   // one eye's share of the frame width
                // Eye image width / height: the circle radius is in eye WIDTHS, so
                // its vertical reach is scaled by this.
                float aspect = _MainTex_TexelSize.w > 0.0
                    ? (_MainTex_TexelSize.z * eyeW) / _MainTex_TexelSize.w
                    : 1.0;

                // Point in the eye image, y UP (0 = bottom), 0..1 each way.
                float rr = (theta / _HalfFov) * _Radius;
                float2 eyeUv = float2(_CentreX + rr * cos(phi),
                                      (1.0 - _CentreY) + rr * sin(phi) * aspect);
                if (eyeUv.x < 0.0 || eyeUv.x > 1.0 || eyeUv.y < 0.0 || eyeUv.y > 1.0)
                    return half4(0, 0, 0, 1);

                // Soften the circle's rim (the page's 0.03 rad).
                float edge = saturate((_HalfFov - theta) / 0.03);

                bool useRight = !mono && (rightEye != (_Swap > 0.5));
                float u = (useRight ? 0.5 : 0.0) + eyeUv.x * eyeW;
                float v = _FlipV > 0.5 ? 1.0 - eyeUv.y : eyeUv.y;

                // LOD 0: the uv jumps at the circle edge and the eye seam, and an
                // implicit-derivative fetch would pick a garbage mip there.
                half3 rgb = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, float2(u, v), 0).rgb;
                return half4(rgb * edge, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
