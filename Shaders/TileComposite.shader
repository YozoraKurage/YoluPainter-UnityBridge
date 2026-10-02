Shader "Hidden/YoluPainter/TileComposite"
{
    Properties { _MainTex ("Below", 2D) = "black" {} _LayerTex ("Layer", 2D) = "black" {} _MaskTex ("Mask (hide amount in alpha)", 2D) = "black" {} }
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
            sampler2D _MainTex, _LayerTex, _MaskTex;
            float _Opacity;
            int _BlendMode;
            float4 _Mask; // x: 有効(1)/無効(0), y: 反転, z: 濃度。CpuCompositor の LayerMask.Factor と同じ式
            float4 frag(v2f_img i) : SV_Target
            {
                float4 b = tex2D(_MainTex, i.uv);
                float4 s = tex2D(_LayerTex, i.uv);
                float factor = 1;
                if (_Mask.x > 0.5)
                {
                    float h = tex2D(_MaskTex, i.uv).a;
                    factor = _Mask.y > 0.5 ? 1 - _Mask.z * (1 - h) : 1 - _Mask.z * h;
                }
                float sa = saturate(s.a * _Opacity * factor), a = sa + b.a * (1-sa);
                float3 blend = s.rgb;
                if (_BlendMode == 1) blend = s.rgb * b.rgb;
                else if (_BlendMode == 2) blend = 1 - (1-s.rgb) * (1-b.rgb);
                float3 premult = (1-sa)*b.a*b.rgb + (1-b.a)*sa*s.rgb + b.a*sa*blend;
                return a > 0 ? float4(premult/a, a) : float4(b.rgb, 0);
            }
            ENDCG
        }
        // Pass 1: そのままのコピー。CopyTexture が使えない環境で、完成したタイルを composite の
        // 該当領域へ描き込むのに使う（作業タイルはポイントサンプリングなので画素がそのまま移る）。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragCopy
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 fragCopy(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv); }
            ENDCG
        }
    }
    Fallback Off
}
