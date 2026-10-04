Shader "Hidden/YoluPainter/NormalResourceReadback"
{
    Properties { _MainTex ("Normal", 2D) = "bump" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 frag(v2f_img i) : SV_Target
            {
                return float4(normalize(UnpackNormal(tex2Dlod(_MainTex, float4(i.uv, 0, 0)))) * .5 + .5, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
