// NearShaderProxyMeshGenerator
// Prototype version: 0.0.9
// Closed-volume camera proximity effect using stencil parity.
// Four fade shells approximate a smooth spatial gradient before the core.

Shader "orz_Shop/NearShaderProxyVolume"
{
    Properties
    {
        _Color ("Color", Color) = (0,0,0,1)
        _CoreStrength ("Core Black Strength", Range(0,1)) = 1
        _FadeDistance ("Outer Fade Distance (m)", Range(0,0.25)) = 0.05
        _FadeStrength ("Fade To Core", Range(0,1)) = 0.2
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
        float _FadeStrength;

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
        };

        struct v2f
        {
            float4 pos : SV_POSITION;
        };

        v2f ExpandVertex(appdata v, float distance)
        {
            v2f o;
            float3 worldPosition = mul(unity_ObjectToWorld, v.vertex).xyz;
            float3 worldNormal = UnityObjectToWorldNormal(v.normal);
            worldPosition += normalize(worldNormal) * distance;
            o.pos = UnityWorldToClipPos(worldPosition);
            return o;
        }

        v2f VertFade1(appdata v) { return ExpandVertex(v, _FadeDistance); }
        v2f VertFade2(appdata v) { return ExpandVertex(v, _FadeDistance * 0.75); }
        v2f VertFade3(appdata v) { return ExpandVertex(v, _FadeDistance * 0.50); }
        v2f VertFade4(appdata v) { return ExpandVertex(v, _FadeDistance * 0.25); }
        v2f VertCore (appdata v) { return ExpandVertex(v, 0.0); }

        // Convert desired cumulative opacity into the alpha needed for only
        // this pass, because nested shells are blended sequentially.
        float IncrementalAlpha(float previousOpacity, float targetOpacity)
        {
            previousOpacity = saturate(previousOpacity);
            targetOpacity = saturate(max(previousOpacity, targetOpacity));

            float remaining = max(1.0 - previousOpacity, 1e-5);
            return saturate((targetOpacity - previousOpacity) / remaining);
        }

        float FadeTargetOpacity()
        {
            return saturate(_CoreStrength * _FadeStrength);
        }

        float SmoothFadeOpacity(float proximity)
        {
            // smoothstep(0, 1, proximity), written explicitly for predictable
            // behavior on older Unity shader targets.
            proximity = saturate(proximity);
            float eased = proximity * proximity * (3.0 - 2.0 * proximity);
            return FadeTargetOpacity() * eased;
        }

        fixed4 FragFade1(v2f i) : SV_Target
        {
            // Representative point for the outermost 25% band.
            float target = SmoothFadeOpacity(0.125);
            return fixed4(_Color.rgb, target);
        }

        fixed4 FragFade2(v2f i) : SV_Target
        {
            float previous = SmoothFadeOpacity(0.125);
            float target = SmoothFadeOpacity(0.375);
            return fixed4(_Color.rgb, IncrementalAlpha(previous, target));
        }

        fixed4 FragFade3(v2f i) : SV_Target
        {
            float previous = SmoothFadeOpacity(0.375);
            float target = SmoothFadeOpacity(0.625);
            return fixed4(_Color.rgb, IncrementalAlpha(previous, target));
        }

        fixed4 FragFade4(v2f i) : SV_Target
        {
            float previous = SmoothFadeOpacity(0.625);
            float target = SmoothFadeOpacity(0.875);
            return fixed4(_Color.rgb, IncrementalAlpha(previous, target));
        }

        fixed4 FragCore(v2f i) : SV_Target
        {
            float previous = SmoothFadeOpacity(0.875);
            float target = saturate(_CoreStrength);

            return fixed4(
                _Color.rgb,
                IncrementalAlpha(previous, target));
        }

        fixed4 FragMask(v2f i) : SV_Target
        {
            return 0;
        }
        ENDCG

        // ---------------------------------------------------------------------
        // Fade band 1: 100% of Fade Distance
        Pass
        {
            Name "FADE1_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Always
                Pass Invert
            }

            CGPROGRAM
            #pragma vertex VertFade1
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "FADE1_COLOR"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Equal
                Pass Zero
            }

            CGPROGRAM
            #pragma vertex VertFade1
            #pragma fragment FragFade1
            ENDCG
        }

        // ---------------------------------------------------------------------
        // Fade band 2: 75% of Fade Distance
        Pass
        {
            Name "FADE2_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Always
                Pass Invert
            }

            CGPROGRAM
            #pragma vertex VertFade2
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "FADE2_COLOR"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Equal
                Pass Zero
            }

            CGPROGRAM
            #pragma vertex VertFade2
            #pragma fragment FragFade2
            ENDCG
        }

        // ---------------------------------------------------------------------
        // Fade band 3: 50% of Fade Distance
        Pass
        {
            Name "FADE3_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Always
                Pass Invert
            }

            CGPROGRAM
            #pragma vertex VertFade3
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "FADE3_COLOR"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Equal
                Pass Zero
            }

            CGPROGRAM
            #pragma vertex VertFade3
            #pragma fragment FragFade3
            ENDCG
        }

        // ---------------------------------------------------------------------
        // Fade band 4: 25% of Fade Distance
        Pass
        {
            Name "FADE4_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Always
                Pass Invert
            }

            CGPROGRAM
            #pragma vertex VertFade4
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "FADE4_COLOR"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Equal
                Pass Zero
            }

            CGPROGRAM
            #pragma vertex VertFade4
            #pragma fragment FragFade4
            ENDCG
        }

        // ---------------------------------------------------------------------
        // Core
        Pass
        {
            Name "CORE_PARITY"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Always
                Pass Invert
            }

            CGPROGRAM
            #pragma vertex VertCore
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "CORE_BLACK"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp Equal
                Pass Zero
            }

            CGPROGRAM
            #pragma vertex VertCore
            #pragma fragment FragCore
            ENDCG
        }
    }

    Fallback Off
}
