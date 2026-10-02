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
            float4 _Mask; // x: 有効(1)/無効(0), y: 反転, z: 濃度。CpuCompositor の RasterMask.Factor と同じ式
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
        // Pass 2: 調整レイヤー。下の合成結果（_MainTex）に AdjustmentSettings と同じ式の調整をかけ、合成モードで
        // 組み合わせてから 不透明度×マスク で混ぜ戻す。アルファは下のまま、完全に透明な画素は変えない。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragAdjust
            #include "UnityCG.cginc"
            sampler2D _MainTex, _MaskTex;
            float _Opacity;
            int _BlendMode;
            float4 _Mask;
            float _AdjType;   // 0 反転 / 1 レベル補正 / 2 色相・彩度・明度
            float4 _AdjLevels; // 入力の黒, 入力の白, ガンマ, -
            float4 _AdjOutput; // 出力の黒, 出力の白, -, -
            float4 _AdjHsl;    // 色相（度）, 彩度, 明度, -
            float HueToRgb(float p, float q, float t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1.0/6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0/3) return p + (q - p) * (2.0/3 - t) * 6;
                return p;
            }
            float3 Adjust(float3 c)
            {
                if (_AdjType < 0.5) return 1 - c;
                if (_AdjType < 1.5)
                {
                    float3 v = saturate((c - _AdjLevels.x) / (_AdjLevels.y - _AdjLevels.x));
                    v = pow(v, 1 / _AdjLevels.z);
                    return _AdjOutput.x + v * (_AdjOutput.y - _AdjOutput.x);
                }
                float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b)), l = (mx + mn) / 2, h = 0, s = 0, d = mx - mn;
                if (d > 1e-6)
                {
                    s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
                    if (mx == c.r) h = (c.g - c.b) / d + (c.g < c.b ? 6 : 0);
                    else if (mx == c.g) h = (c.b - c.r) / d + 2;
                    else h = (c.r - c.g) / d + 4;
                    h /= 6;
                }
                h = h + _AdjHsl.x / 360; h -= floor(h);
                s = saturate(s * (1 + _AdjHsl.y));
                l = _AdjHsl.z >= 0 ? l + (1 - l) * _AdjHsl.z : l * (1 + _AdjHsl.z);
                if (s <= 0) return float3(l, l, l);
                float q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                return float3(HueToRgb(p, q, h + 1.0/3), HueToRgb(p, q, h), HueToRgb(p, q, h - 1.0/3));
            }
            float4 fragAdjust(v2f_img i) : SV_Target
            {
                float4 b = tex2D(_MainTex, i.uv);
                float factor = 1;
                if (_Mask.x > 0.5)
                {
                    float hide = tex2D(_MaskTex, i.uv).a;
                    factor = _Mask.y > 0.5 ? 1 - _Mask.z * (1 - hide) : 1 - _Mask.z * hide;
                }
                float amount = _Opacity * factor;
                if (amount <= 0 || b.a <= 0) return b;
                // CPU と同じく、調整結果をいったん 8 bit に丸めてから合成モードと混ぜ戻しにかける。
                float3 a = round(saturate(Adjust(b.rgb)) * 255) / 255;
                float3 m = a;
                if (_BlendMode == 1) m = b.rgb * a;
                else if (_BlendMode == 2) m = 1 - (1 - b.rgb) * (1 - a);
                return float4(b.rgb + (m - b.rgb) * amount, b.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
