Shader "Hidden/DotTexturePainter/TileComposite"
{
    Properties { _MainTex ("Below", 2D) = "black" {} _LayerTex ("Layer", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _LayerTex;
            float _Opacity;
            int _BlendMode;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 b = tex2D(_MainTex, i.uv);
                float4 s = tex2D(_LayerTex, i.uv);
                float sa = saturate(s.a * _Opacity), a = sa + b.a * (1-sa);
                float3 blend = s.rgb;
                if (_BlendMode == 1) blend = s.rgb * b.rgb;
                else if (_BlendMode == 2) blend = 1 - (1-s.rgb) * (1-b.rgb);
                float3 premult = (1-sa)*b.a*b.rgb + (1-b.a)*sa*s.rgb + b.a*sa*blend;
                return a > 0 ? float4(premult/a, a) : float4(b.rgb, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
