// NearShaderProxyMeshGenerator
// Prototype version: 0.0.10
// Continuous camera-to-surface fade + stencil-parity core blackout.

Shader "orz_Shop/NearShaderProxyVolume"
{
    Properties
    {
        _Color ("Color", Color) = (0,0,0,1)
        _CoreStrength ("Core Black Strength", Range(0,1)) = 1
        _FadeDistance ("Fade Distance (m)", Range(0,0.25)) = 0.05
        _FadeStrength ("Fade To Core", Range(0,1)) = 1
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
        };

        struct v2fSurface
        {
            float4 pos : SV_POSITION;
            float3 worldPos : TEXCOORD0;
        };

        struct v2fCore
        {
            float4 pos : SV_POSITION;
        };

        v2fSurface VertSurface(appdata v)
        {
            v2fSurface o;
            float4 world = mul(unity_ObjectToWorld, v.vertex);
            o.worldPos = world.xyz;
            o.pos = UnityWorldToClipPos(world.xyz);
            return o;
        }

        v2fCore VertCore(appdata v)
        {
            v2fCore o;
            o.pos = UnityObjectToClipPos(v.vertex);
            return o;
        }

        fixed4 FragSurface(v2fSurface i) : SV_Target
        {
            float fadeDistance = max(_FadeDistance, 1e-5);
            float surfaceDistance = distance(_WorldSpaceCameraPos, i.worldPos);

            if (surfaceDistance >= fadeDistance)
                discard;

            // 0 at the outer edge, 1 at the proxy surface.
            float proximity = saturate(1.0 - surfaceDistance / fadeDistance);

            // Continuous ease-in/ease-out. Unlike the old nested-shell approach,
            // this value is evaluated per fragment and therefore has no spatial bands.
            float eased = proximity * proximity * (3.0 - 2.0 * proximity);

            float targetOpacity =
                saturate(_CoreStrength * _FadeStrength);

            return fixed4(
                _Color.rgb,
                saturate(targetOpacity * eased));
        }

        fixed4 FragCore(v2fCore i) : SV_Target
        {
            return fixed4(
                _Color.rgb,
                saturate(_CoreStrength));
        }

        fixed4 FragMask(v2fCore i) : SV_Target
        {
            return 0;
        }
        ENDCG

        // ---------------------------------------------------------------------
        // 1) Determine whether each camera ray starts inside the closed proxy.
        // Outside rays cross the closed surface an even number of times (stencil 0).
        // Inside rays cross it an odd number of times (stencil 128).
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

        // ---------------------------------------------------------------------
        // 2) Outside only: draw the actual proxy surface once and calculate
        // continuous camera-to-fragment distance. Cull Back keeps the inside
        // surface from stacking an additional fade under the core blackout.
        Pass
        {
            Name "SURFACE_FADE"
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Back
            ZWrite Off
            ZTest Always

            Stencil
            {
                Ref 128
                ReadMask 128
                WriteMask 128
                Comp NotEqual
                Pass Keep
            }

            CGPROGRAM
            #pragma vertex VertSurface
            #pragma fragment FragSurface
            ENDCG
        }

        // ---------------------------------------------------------------------
        // 3) Inside only: draw the closed-volume blackout and clear our stencil bit.
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
