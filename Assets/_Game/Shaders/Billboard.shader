// Спрайт-билборд для призрака и фигур прошлого.
// Квад (Unity Quad, плоскость XY) поворачивается вокруг вертикали лицом к той камере, которая его рендерит —
// поэтому каждая обскура видит фигуру анфас. Масштаб объекта = размер спрайта.
//
// «Точка обзора» (_ViewRadius > 0): фигура целиком проступает только у камеры, стоящей рядом с _ViewPoint
// и смотрящей примерно по _ViewDir. Дальше от точки — рассыпается на пиксели (дизер-растворение).
// Так сцену прошлого надо найти — как анаморфную картинку.
Shader "Jam/Billboard"
{
    Properties
    {
        [MainTexture] _BaseMap ("Sprite", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.4
        _Opacity ("Opacity (dither, flickers)", Range(0, 1)) = 1
        _ViewPoint ("View point (world)", Vector) = (0, 0, 0, 0)
        _ViewDir ("View direction (world)", Vector) = (0, 0, 1, 0)
        _ViewRadius ("View radius, m (0 = always visible)", Float) = 0
        _ViewFade ("Fade distance, m", Float) = 2.5
        _ViewAngle ("Max angle, deg", Float) = 35
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "RenderPipeline"="UniversalPipeline" "DisableBatching"="True" "IgnoreProjector"="True" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _Opacity;
                float4 _ViewPoint;
                float4 _ViewDir;
                float _ViewRadius;
                float _ViewFade;
                float _ViewAngle;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half fog : TEXCOORD1;
                half vis : TEXCOORD2;
            };

            Varyings vert (Attributes input)
            {
                float3 pivot = TransformObjectToWorld(float3(0, 0, 0));
                float3 camPos = GetCameraPositionWS();
                float3 toCam = camPos - pivot;
                toCam.y = 0;
                float len = length(toCam);
                float3 fwd = len > 1e-4 ? toCam / len : float3(0, 0, 1);
                float3 up = float3(0, 1, 0);
                float3 right = normalize(cross(fwd, up));

                float sx = length(float3(UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20));
                float sy = length(float3(UNITY_MATRIX_M._m01, UNITY_MATRIX_M._m11, UNITY_MATRIX_M._m21));
                float3 posWS = pivot + right * (input.positionOS.x * sx) + up * (input.positionOS.y * sy);

                // насколько камера близка к точке обзора: по расстоянию и по направлению взгляда
                half vis = 1;
                if (_ViewRadius > 0)
                {
                    float d = length(camPos.xz - _ViewPoint.xz);
                    float byDist = saturate((_ViewRadius + _ViewFade - d) / max(_ViewFade, 0.01));
                    float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                    float2 cf = normalize(camFwd.xz + 1e-5);
                    float2 vf = normalize(_ViewDir.xz + 1e-5);
                    float cosA = dot(cf, vf);
                    float cosMax = cos(radians(_ViewAngle));
                    float byAngle = saturate((cosA - (cosMax - 0.25)) / 0.25);
                    vis = byDist * byAngle;
                }

                Varyings o;
                o.positionCS = TransformWorldToHClip(posWS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.vis = vis;
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

            half4 frag (Varyings i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;
                clip(c.a - _Cutoff);
                // растворение по пикселям экрана: чем дальше от точки обзора, тем больше пикселей выпадает
                clip(i.vis - Hash(floor(i.positionCS.xy)) * 0.999);
                // полупрозрачность «сеткой» в духе PS1; узор меняется 6 раз в секунду — тень мерцает
                if (_Opacity < 0.999) clip(_Opacity - Hash(floor(i.positionCS.xy) + floor(_Time.y * 6.0) * 7.13 + 31.7) * 0.999);
                c.rgb = MixFog(c.rgb, i.fog);
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }
    }
}
