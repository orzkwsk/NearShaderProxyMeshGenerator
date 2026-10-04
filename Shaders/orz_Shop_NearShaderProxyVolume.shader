// NearShaderProxyMeshGenerator
// Prototype version: 0.0.12
// Eight nested closed-volume fade shells + inward core.
// Proxy geometry is stencil-only. A tagged helper triangle resolves each
// shell/core to fullscreen exactly once per pixel.

Shader "orz_Shop/NearShaderProxyVolume"
{
    Properties
    {
        _Color ("Color", Color) = (0,0,0,1)
        _CoreStrength ("Core Black Strength", Range(0,1)) = 1
        _FadeDistance ("Outer Fade Distance (m)", Range(0,0.25)) = 0.05
        _CoreInset ("Core Inset (m)", Range(0,0.05)) = 0.01
        _FadeStrength ("Fade To Core", Range(0,1)) = 1
        _DitherStrength ("Shell Dither Strength", Range(0,1)) = 0.15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Overlay+100"
            "RenderType"="Transparent"
            "VRCFallback"="Hidden"
        }

        CGINCLUDE
        #include "UnityCG.cginc"

        fixed4 _Color;
        float _CoreStrength;
        float _FadeDistance;
        float _CoreInset;
        float _FadeStrength;
        float _DitherStrength;

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 renderData : TEXCOORD1;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        float Smooth01(float t)
        {
            t = saturate(t);
            return t * t * (3.0 - 2.0 * t);
        }

        // t=0 is the outer edge, t=1 is the inset core boundary.
        float ShellOffset(float t)
        {
            float positionT = Smooth01(t);
            return lerp(_FadeDistance, -_CoreInset, positionT);
        }

        v2f VolumeVertex(appdata v, float distance)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            float marker = step(0.5, v.renderData.z);

            float3 worldPosition =
                mul(unity_ObjectToWorld, v.vertex).xyz;

            float3 worldNormal =
                UnityObjectToWorldNormal(v.normal);

            worldPosition +=
                normalize(worldNormal) * distance;

            float4 volumeClip =
                UnityWorldToClipPos(worldPosition);

            // Tagged helper vertices never participate in parity.
            float4 offscreen =
                float4(2.0, 2.0, 2.0, 1.0);

            o.pos =
                lerp(volumeClip, offscreen, marker);

            return o;
        }

        v2f FullscreenVertex(appdata v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            float marker =
                step(0.5, v.renderData.z);

            float2 clipXY =
                v.renderData.xy * 2.0 - 1.0;

            float4 fullscreenClip =
                float4(clipXY, 0.0, 1.0);

            // Normal proxy vertices never participate in fullscreen resolve.
            float4 offscreen =
                float4(2.0, 2.0, 2.0, 1.0);

            o.pos =
                lerp(offscreen, fullscreenClip, marker);

            return o;
        }

        v2f VertShell1(appdata v) { return VolumeVertex(v, ShellOffset(0.0625)); }
        v2f VertShell2(appdata v) { return VolumeVertex(v, ShellOffset(0.1875)); }
        v2f VertShell3(appdata v) { return VolumeVertex(v, ShellOffset(0.3125)); }
        v2f VertShell4(appdata v) { return VolumeVertex(v, ShellOffset(0.4375)); }
        v2f VertShell5(appdata v) { return VolumeVertex(v, ShellOffset(0.5625)); }
        v2f VertShell6(appdata v) { return VolumeVertex(v, ShellOffset(0.6875)); }
        v2f VertShell7(appdata v) { return VolumeVertex(v, ShellOffset(0.8125)); }
        v2f VertShell8(appdata v) { return VolumeVertex(v, ShellOffset(0.9375)); }
        v2f VertCore  (appdata v) { return VolumeVertex(v, -_CoreInset); }

        float FadeTargetOpacity()
        {
            return saturate(_CoreStrength * _FadeStrength);
        }

        float IncrementalAlpha(float previousOpacity, float targetOpacity)
        {
            previousOpacity =
                saturate(previousOpacity);

            targetOpacity =
                saturate(max(previousOpacity, targetOpacity));

            float remaining =
                max(1.0 - previousOpacity, 1e-5);

            return saturate(
                (targetOpacity - previousOpacity) /
                remaining);
        }

        float InterleavedGradientNoise(float2 pixel)
        {
            pixel = floor(pixel);

            return frac(
                52.9829189 *
                frac(
                    dot(
                        pixel,
                        float2(
                            0.06711056,
                            0.00583715))));
        }

        float ApplyShellDither(
            float alpha,
            float2 pixel)
        {
            float noise =
                InterleavedGradientNoise(pixel) - 0.5;

            float room =
                min(alpha, 1.0 - alpha);

            float amplitude =
                room * _DitherStrength * 0.75;

            return saturate(
                alpha +
                noise * 2.0 * amplitude);
        }

        fixed4 ShellColor(
            v2f i,
            float previousLevel,
            float currentLevel)
        {
            float fadeTarget =
                FadeTargetOpacity();

            float previousOpacity =
                fadeTarget *
                saturate(previousLevel);

            float currentOpacity =
                fadeTarget *
                saturate(currentLevel);

            float alpha =
                IncrementalAlpha(
                    previousOpacity,
                    currentOpacity);

            alpha =
                ApplyShellDither(
                    alpha,
                    i.pos.xy);

            return fixed4(
                _Color.rgb,
                alpha);
        }

        fixed4 FragShell1(v2f i) : SV_Target { return ShellColor(i, 0.000, 0.125); }
        fixed4 FragShell2(v2f i) : SV_Target { return ShellColor(i, 0.125, 0.250); }
        fixed4 FragShell3(v2f i) : SV_Target { return ShellColor(i, 0.250, 0.375); }
        fixed4 FragShell4(v2f i) : SV_Target { return ShellColor(i, 0.375, 0.500); }
        fixed4 FragShell5(v2f i) : SV_Target { return ShellColor(i, 0.500, 0.625); }
        fixed4 FragShell6(v2f i) : SV_Target { return ShellColor(i, 0.625, 0.750); }
        fixed4 FragShell7(v2f i) : SV_Target { return ShellColor(i, 0.750, 0.875); }
        fixed4 FragShell8(v2f i) : SV_Target { return ShellColor(i, 0.875, 1.000); }

        fixed4 FragCore(v2f i) : SV_Target
        {
            float alpha =
                IncrementalAlpha(
                    FadeTargetOpacity(),
                    saturate(_CoreStrength));

            return fixed4(
                _Color.rgb,
                alpha);
        }

        fixed4 FragMask(v2f i) : SV_Target
        {
            return 0;
        }
        ENDCG

        // Shell 1 -------------------------------------------------------------
        Pass
        {
            Name "SHELL1_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell1
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL1_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell1
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 2 -------------------------------------------------------------
        Pass
        {
            Name "SHELL2_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell2
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL2_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell2
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 3 -------------------------------------------------------------
        Pass
        {
            Name "SHELL3_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell3
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL3_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell3
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 4 -------------------------------------------------------------
        Pass
        {
            Name "SHELL4_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell4
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL4_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell4
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 5 -------------------------------------------------------------
        Pass
        {
            Name "SHELL5_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell5
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL5_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell5
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 6 -------------------------------------------------------------
        Pass
        {
            Name "SHELL6_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell6
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL6_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell6
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 7 -------------------------------------------------------------
        Pass
        {
            Name "SHELL7_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell7
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL7_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell7
            #pragma multi_compile_instancing
            ENDCG
        }

        // Shell 8 -------------------------------------------------------------
        Pass
        {
            Name "SHELL8_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertShell8
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "SHELL8_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragShell8
            #pragma multi_compile_instancing
            ENDCG
        }

        // Inset core ----------------------------------------------------------
        Pass
        {
            Name "CORE_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Invert }
            CGPROGRAM
            #pragma vertex VertCore
            #pragma fragment FragMask
            #pragma multi_compile_instancing
            ENDCG
        }

        Pass
        {
            Name "CORE_BLACK"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Equal Pass Zero }
            CGPROGRAM
            #pragma vertex FullscreenVertex
            #pragma fragment FragCore
            #pragma multi_compile_instancing
            ENDCG
        }
    }

    Fallback Off
}
