// PS1-вид для дома, мебели и пропов (URP).
// — Текстура тайлится по осям мира: ось выбирается жёстко по нормали, UV модели не нужны.
//   Для героев с развёрткой (портрет) включить Use Mesh UV.
//   Object UV — текстура привязана к модели, а не к миру: для всего, что движется (предметы в руках, двери),
//   иначе при повороте текстура «ползёт» по предмету. Включает PS1ObjectUV при загрузке сцены.
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
        [Toggle] _ObjectUV ("Texture follows object (moving props)", Float) = 0
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
                float _ObjectUV;
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
                float3 uvPos : TEXCOORD5; // откуда берётся тайлинг: мир или модель
                float3 uvN : TEXCOORD6;
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
                if (_ObjectUV > 0.5)
                {
                    // координаты модели в метрах (с учётом масштаба), чтобы плотность текстуры была как у мира
                    float4x4 m = GetObjectToWorldMatrix();
                    float3 sc = float3(length(m._m00_m10_m20), length(m._m01_m11_m21), length(m._m02_m12_m22));
                    o.uvPos = input.positionOS.xyz * sc;
                    o.uvN = input.normalOS;
                }
                else
                {
                    o.uvPos = o.positionWS;
                    o.uvN = o.normalWS;
                }
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

            // свой список ламп дома (ставит LightBudget каждый кадр): одинаковый для всех пикселей всех объектов,
            // без выбора «4 лампы на объект» — поэтому соседние куски одной стены не расходятся по яркости
            #define JAM_MAX_LIGHTS 48
            float4 _JamLightPos[JAM_MAX_LIGHTS];   // xyz — где, w — 1/радиус²
            float4 _JamLightCol[JAM_MAX_LIGHTS];   // rgb — цвет × яркость
            float _JamLightCount;
            float _JamMainScale;

            half3 JamLights(float3 posWS, float3 n)
            {
                half3 sum = 0;
                int count = (int)_JamLightCount;
                [loop] for (int k = 0; k < count; k++)
                {
                    float3 v = _JamLightPos[k].xyz - posWS;
                    float d2 = max(dot(v, v), 0.0001);
                    // затухание как у URP: 1/d² с мягким обрезом у границы радиуса
                    float f = saturate(1.0 - d2 * d2 * _JamLightPos[k].w * _JamLightPos[k].w);
                    float att = f * f / d2;
                    half w = saturate(abs(dot(n, v * rsqrt(d2))) * 0.5 + 0.5);
                    sum += _JamLightCol[k].rgb * att * (w * w);
                }
                return sum;
            }

            half3 WrapLight(Light l, float3 n)
            {
                // лампы светят сквозь стены (теней нет), поэтому и стена, стоящая к лампе спиной, должна освещаться
                // так же, как пол рядом с ней: считаем свет с любой стороны грани
                half d = saturate(abs(dot(n, l.direction)) * 0.5h + 0.5h);
                return l.color * (l.distanceAttenuation * l.shadowAttenuation) * (d * d);
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 n = normalize(i.normalWS);
                // стены в доме — одна плоскость на две комнаты: со стороны «изнанки» нормаль смотрела от ламп,
                // и стена была чёрной. Освещаем ту сторону, которую видим
                if (dot(n, GetWorldSpaceNormalizeViewDir(i.positionWS)) < 0) n = -n;
                float2 uv = _UseMeshUV > 0.5 ? i.uv : WorldUV(i.uvPos, normalize(i.uvN));
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

                // мягкий («обёрнутый») свет вместо обычного: грань, стоящая к лампе боком или спиной, всё равно получает
                // свет. Иначе у одной стены соседние грани выходили одна светлой, другая чёрной
                // главный (лунный) свет — как раньше, обычный: «обёрнутый» он заливал весь дом ровным светом
                Light mainLight = GetMainLight(inputData.shadowCoord);
                half3 lit = inputData.bakedGI;
                // в игре луна — ровный слабый общий свет без направления: направленная, она красила стены одной стороны
                // светлыми, а противоположные — тёмными
                if (_JamLightCount > 0.5) lit += mainLight.color * _JamMainScale;
                else lit += mainLight.color * (mainLight.distanceAttenuation * mainLight.shadowAttenuation) * saturate(dot(n, mainLight.direction));
                if (_JamLightCount > 0.5)
                    lit += JamLights(inputData.positionWS, n);
                else
                {
                    // вне игры (окно сцены в редакторе) — обычные лампы URP
                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        lit += WrapLight(GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1)), n);
                    LIGHT_LOOP_END
                }
                half4 c = half4(albedo * lit, 1);
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
                float _ObjectUV;
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
