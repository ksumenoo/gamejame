// PS1-вид для дома, мебели и пропов (URP).
// — Текстура тайлится по осям мира: ось выбирается жёстко по нормали, UV модели не нужны.
//   Для героев с развёрткой (портрет) включить Use Mesh UV.
// — Цвета вершин (Vertex Paint в Blender) умножаются на текстуру.
// — Вершины прилипают к сетке экрана (_PS1SnapRes — глобально, ставит LevelSettings) — «дрожание» PS1.
// — Освещение URP (главный + точечные, Forward и Forward+), туман, point-фильтр берётся из импорта текстуры.
Shader "Jam/PS1Lit"
{
    Properties
    {
        [MainTexture] _BaseMap ("Tile", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _TileSize ("Tile size, m", Float) = 1
        [Toggle] _UseMeshUV ("Use mesh UV (heroes)", Float) = 0
        _VertexColorAmount ("Vertex color amount", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _TileSize;
                float _UseMeshUV;
                half _VertexColorAmount;
            CBUFFER_END

            float4 _PS1SnapRes; // xy — сетка снапа в пикселях; 0 — без снапа
            float _PS1Unlit;    // 1 — «дневной» снимок для альбома: цвет текстуры без света дома (ставит FamilyAlbum)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                half4 color : TEXCOORD3;
                half fog : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert (Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 cs = TransformWorldToHClip(o.positionWS);

                if (_PS1SnapRes.x > 0 && cs.w > 0)
                {
                    float2 grid = _PS1SnapRes.xy * 0.5;
                    cs.xy = round(cs.xy / cs.w * grid) / grid * cs.w;
                }
                o.positionCS = cs;
                o.uv = input.uv;
                o.color = input.color;
                o.fog = ComputeFogFactor(cs.z);
                return o;
            }

            float2 WorldUV(float3 p, float3 n)
            {
                float3 a = abs(n);
                float2 uv = a.y >= a.x && a.y >= a.z ? p.xz
                          : a.x >= a.z ? p.zy
                          : p.xy;
                return uv / max(_TileSize, 0.01);
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS);
                float2 uv = _UseMeshUV > 0.5 ? i.uv : WorldUV(i.positionWS, n);
                uv = uv * _BaseMap_ST.xy + _BaseMap_ST.zw;

                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb * _BaseColor.rgb;
                albedo *= lerp(half3(1, 1, 1), i.color.rgb, _VertexColorAmount);

                InputData inputData = (InputData)0;
                inputData.positionWS = i.positionWS;
                inputData.positionCS = i.positionCS;
                inputData.normalWS = n;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                inputData.fogCoord = i.fog;
                inputData.bakedGI = SampleSH(n);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData s = (SurfaceData)0;
                s.albedo = albedo;
                s.alpha = 1;
                s.occlusion = 1;
                s.normalTS = half3(0, 0, 1);

                half4 c = UniversalFragmentBlinnPhong(inputData, s);
                if (_PS1Unlit > 0.5)
                {
                    // ровный свет, чуть темнее на гранях, повёрнутых от объектива — чтобы читалась форма
                    half shade = 0.55h + 0.45h * saturate(dot(n, inputData.viewDirectionWS));
                    c.rgb = albedo * shade * 1.2h;
                }
                c.rgb = MixFog(c.rgb, inputData.fogCoord);
                return half4(c.rgb, 1);
            }
            ENDHLSL
        }

        // глубина для URP (depth prepass / depth texture)
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _TileSize;
                float _UseMeshUV;
                half _VertexColorAmount;
            CBUFFER_END

            struct A { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; };

            V vert (A input)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return o;
            }
            half frag (V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
