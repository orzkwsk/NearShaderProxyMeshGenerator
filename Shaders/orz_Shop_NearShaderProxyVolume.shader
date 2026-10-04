// NearShaderProxyMeshGenerator
// Prototype version: 0.0.8
// Closed-volume camera proximity effect using stencil parity.

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

        v2f VertOuter(appdata v) { return ExpandVertex(v, _FadeDistance); }
        v2f VertMid(appdata v)   { return ExpandVertex(v, _FadeDistance * 0.5); }
        v2f VertCore(appdata v)  { return ExpandVertex(v, 0.0); }

        // Each shell is blended on top of the previous shell. Therefore the
        // per-pass alpha must be converted from a desired cumulative opacity.
        // This keeps CoreStrength equal to the actual final black opacity.
        float IncrementalAlpha(float previousOpacity, float targetOpacity)
        {
            previousOpacity = saturate(previousOpacity);
            targetOpacity = saturate(max(previousOpacity, targetOpacity));

            float remaining = max(1.0 - previousOpacity, 1e-5);
            return saturate((targetOpacity - previousOpacity) / remaining);
        }

        float FadeTargetOpacity()
        {
            // 0   : no pre-core fade
            // 0.5 : fade reaches half of CoreStrength before the core
            // 1   : fade reaches CoreStrength by the mid shell
            return saturate(_CoreStrength * _FadeStrength);
        }

        fixed4 FragOuter(v2f i) : SV_Target
        {
            float fadeTarget = FadeTargetOpacity();

            // Keep the first contact subtle even when Fade To Core is 100%.
            float outerOpacity = fadeTarget * 0.20;

            return fixed4(
                _Color.rgb,
                saturate(outerOpacity));
        }

        fixed4 FragMid(v2f i) : SV_Target
        {
            float fadeTarget = FadeTargetOpacity();
            float outerOpacity = fadeTarget * 0.20;

            // Outer has already been blended. Add only the alpha required to
            // make the cumulative opacity exactly fadeTarget.
            float alpha = IncrementalAlpha(
                outerOpacity,
                fadeTarget);

            return fixed4(
                _Color.rgb,
                alpha);
        }

        fixed4 FragCore(v2f i) : SV_Target
        {
            float fadeTarget = FadeTargetOpacity();

            // Outer + Mid already equal fadeTarget. Add only enough black so
            // the final cumulative opacity equals CoreStrength exactly.
            float alpha = IncrementalAlpha(
                fadeTarget,
                saturate(_CoreStrength));

            return fixed4(
                _Color.rgb,
                alpha);
        }

        fixed4 FragMask(v2f i) : SV_Target { return 0; }
        ENDCG

        Pass
        {
            Name "OUTER_PARITY"
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
            #pragma vertex VertOuter
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "OUTER_FADE"
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
            #pragma vertex VertOuter
            #pragma fragment FragOuter
            ENDCG
        }

        Pass
        {
            Name "MID_PARITY"
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
            #pragma vertex VertMid
            #pragma fragment FragMask
            ENDCG
        }

        Pass
        {
            Name "MID_FADE"
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
            #pragma vertex VertMid
            #pragma fragment FragMid
            ENDCG
        }

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
