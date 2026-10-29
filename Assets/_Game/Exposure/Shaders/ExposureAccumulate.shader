// Накопление выдержки: result = lerp(предыдущее, новый кадр, _Weight).
// При _Weight = 1/(n+1) получается среднее всех кадров — неподвижное резкое, движущееся размазано.
Shader "Hidden/Jam/ExposureAccumulate"
{
    Properties
    {
        _MainTex ("Frame", 2D) = "black" {}
        _PrevTex ("Accumulated", 2D) = "black" {}
        _Weight ("Weight", Range(0, 1)) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _PrevTex;
            float _Weight;

            fixed4 frag (v2f_img i) : SV_Target
            {
                float3 frame = tex2D(_MainTex, i.uv).rgb;
                float3 prev  = tex2D(_PrevTex, i.uv).rgb;
                return float4(lerp(prev, frame, _Weight), 1);
            }
            ENDCG
        }
    }
}
