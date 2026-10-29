// Отображение пластины на RawImage (UI) или на квад в мире.
// Переворачивает картинку как обскура (на 180°), тонирует под дагеротип, добавляет зерно, виньетку,
// слой царапин от художников и анимацию проявки (_Develop 0 → 1).
Shader "Jam/PlateDisplay"
{
    Properties
    {
        [PerRendererData] _MainTex ("Plate", 2D) = "black" {}
        _Develop ("Develop", Range(0, 1)) = 1
        [Toggle] _Flip ("Flip like obscura", Float) = 1
        _DarkTone ("Dark tone", Color) = (0.07, 0.06, 0.05, 1)
        _LightTone ("Light tone", Color) = (0.86, 0.80, 0.68, 1)
        _Contrast ("Contrast", Range(0.5, 3)) = 1.4
        _Grain ("Grain", Range(0, 0.5)) = 0.08
        _Vignette ("Vignette", Range(0, 2)) = 0.9
        _Overlay ("Scratches / patina (multiply)", 2D) = "white" {}
        _OverlayStrength ("Overlay strength", Range(0, 1)) = 0.6

        // для UI-масок
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex;
            sampler2D _Overlay;
            float _Develop, _Flip, _Contrast, _Grain, _Vignette, _OverlayStrength;
            fixed4 _DarkTone, _LightTone;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = _Flip > 0.5 ? 1.0 - i.uv : i.uv;

                float3 c = tex2D(_MainTex, uv).rgb;
                float lum = dot(c, float3(0.299, 0.587, 0.114));
                lum = saturate((lum - 0.5) * _Contrast + 0.5);

                // проявка: светлое проступает раньше тёмного
                float reveal = saturate(_Develop * 1.6 - (1.0 - lum) * 0.6);
                lum *= reveal;

                // зерно (статичное на пластине, чуть живое во время проявки)
                float g = hash(floor(i.uv * float2(320, 240)) + floor(_Develop * 8.0)) - 0.5;
                lum = saturate(lum + g * _Grain);

                // виньетка
                float2 d = i.uv - 0.5;
                lum *= saturate(1.0 - dot(d, d) * _Vignette * 2.0);

                float3 col = lerp(_DarkTone.rgb, _LightTone.rgb, lum);

                // царапины и патина от художников
                float3 ov = tex2D(_Overlay, i.uv).rgb;
                col *= lerp(1.0, ov, _OverlayStrength);

                return fixed4(col, i.color.a);
            }
            ENDCG
        }
    }
}
