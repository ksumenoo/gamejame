// Вывод low-res кадра на экран: point-выборка, 15-битный цвет PS1 и упорядоченный дизеринг 4×4.
Shader "Jam/LowResOutput"
{
    Properties
    {
        [PerRendererData] _MainTex ("Frame", 2D) = "black" {}
        _Levels ("Color levels per channel", Float) = 32
        _Dither ("Dither strength", Range(0, 2)) = 1
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Opaque" "IgnoreProjector"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off ZWrite Off ZTest Always
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Levels, _Dither;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            static const float bayer[16] = {
                 0,  8,  2, 10,
                12,  4, 14,  6,
                 3, 11,  1,  9,
                15,  7, 13,  5 };

            fixed4 frag (v2f i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb;
                // дизеринг по пикселям low-res кадра, а не экрана
                int2 px = int2(floor(i.uv * _MainTex_TexelSize.zw)) % 4;
                float t = (bayer[px.y * 4 + px.x] + 0.5) / 16.0 - 0.5;
                float levels = max(_Levels, 2) - 1;
                c = floor(saturate(c + t * _Dither / levels) * levels + 0.5) / levels;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
}
