Shader "Hidden/DotTexturePainter/OrderedBrush"
{
    Properties { _MainTex ("Source tile", 2D) = "black" {} }
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
            float4 _BrushColor;
            float4 _CenterRadiusHardness; // tile-local pixels x/y, radius, hardness
            float2 _TileSize;
            float _OpacityFlow, _Erase;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 b = tex2D(_MainTex, i.uv);
                float d = length(i.uv * _TileSize - _CenterRadiusHardness.xy) / max(0.0001, _CenterRadiusHardness.z);
                float coverage = d <= _CenterRadiusHardness.w ? 1 : saturate((1-d) / max(0.0001, 1-_CenterRadiusHardness.w));
                float sa = coverage * _OpacityFlow * _BrushColor.a;
                if (_Erase > 0.5) return float4(b.rgb, b.a*(1-sa));
                float a = sa + b.a*(1-sa);
                return a > 0 ? float4((_BrushColor.rgb*sa+b.rgb*b.a*(1-sa))/a,a) : b;
            }
            ENDCG
        }
    }
    Fallback Off
}
