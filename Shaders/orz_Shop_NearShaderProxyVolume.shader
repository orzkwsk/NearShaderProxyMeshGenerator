// NearShaderProxyMeshGenerator
// Prototype version: 0.0.2
// Closed-volume camera proximity effect using stencil parity.

Shader "orz_Shop/NearShaderProxyVolume"
{
    Properties
    {
        _Color ("Color", Color) = (0,0,0,1)
        _CoreStrength ("Core Black Strength", Range(0,1)) = 1
        _FadeDistance ("Outer Fade Distance (m)", Range(0,0.25)) = 0.05
        _FadeStrength ("Outer Fade Strength", Range(0,1)) = 0.2
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

        v2f VertOuter(appdata v)
        {
            return ExpandVertex(v, _FadeDistance);
        }

        v2f VertMid(appdata v)
        {
            return ExpandVertex(v, _FadeDistance * 0.5);
        }

        v2f VertCore(appdata v)
        {
            return ExpandVertex(v, 0.0);
        }

        fixed4 FragOuter(v2f i) : SV_Target
        {
            return fixed4(_Color.rgb, saturate(_FadeStrength * 0.35));
        }

        fixed4 FragMid(v2f i) : SV_Target
        {
            return fixed4(_Color.rgb, saturate(_FadeStrength * 0.65));
        }

        fixed4 FragCore(v2f i) : SV_Target
        {
            return fixed4(_Color.rgb, saturate(_CoreStrength));
        }

        fixed4 FragMask(v2f i) : SV_Target
        {
            return 0;
        }
        ENDCG

        // OUTER SHELL ---------------------------------------------------------
        // Toggle stencil bit 7 once for every surface crossing. A closed mesh
        // gives an even number of crossings from outside and an odd number
        // when the camera is inside the shell.
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
            Cull Front
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

        // MID SHELL -----------------------------------------------------------
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
            Cull Front
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

        // CORE ----------------------------------------------------------------
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
            Cull Front
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
