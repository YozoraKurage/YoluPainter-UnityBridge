Shader "Hidden/YoluPainter/TileComposite"
{
    Properties { _MainTex ("Below", 2D) = "black" {} _LayerTex ("Layer", 2D) = "black" {} _MaskTex ("Mask (hide amount in alpha)", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        // 合成モードの色の式。CpuCompositor.BlendRgb と同じ式・同じ番号（LayerBlendMode の値）。
        float Dodge(float d, float s) { return d <= 0 ? 0 : (s >= 1 ? 1 : min(1, d / (1 - s))); }
        float Burn(float d, float s) { return d >= 1 ? 1 : (s <= 0 ? 0 : 1 - min(1, (1 - d) / s)); }
        float Separable(int mode, float d, float s)
        {
            float v = s;
            if (mode == 1) v = d * s;
            else if (mode == 2) v = d + s - d * s;
            else if (mode == 3) v = d <= 0.5 ? 2 * d * s : 1 - 2 * (1 - d) * (1 - s);
            else if (mode == 4) v = min(d, s);
            else if (mode == 5) v = max(d, s);
            else if (mode == 6) v = Dodge(d, s);
            else if (mode == 7) v = Burn(d, s);
            else if (mode == 8) v = d + s;
            else if (mode == 9) v = d + s - 1;
            else if (mode == 10) v = s <= 0.5 ? 2 * d * s : 1 - 2 * (1 - d) * (1 - s);
            else if (mode == 11) v = s <= 0.5 ? d - (1 - 2 * s) * d * (1 - d) : d + (2 * s - 1) * (sqrt(d) - d);
            else if (mode == 12) v = s <= 0.5 ? Burn(d, 2 * s) : Dodge(d, 2 * s - 1);
            else if (mode == 13) v = d + 2 * s - 1;
            else if (mode == 14) v = s <= 0.5 ? min(d, 2 * s) : max(d, 2 * s - 1);
            else if (mode == 15) v = d + s >= 1 - 0.5 / 255 ? 1 : 0;
            else if (mode == 16) v = abs(d - s);
            else if (mode == 17) v = d + s - 2 * d * s;
            else if (mode == 18) v = d - s;
            else if (mode == 19) v = s <= 0 ? (d <= 0 ? 0 : 1) : d / s;
            return saturate(v);
        }
        float Lum(float3 c) { return dot(c, float3(0.3, 0.59, 0.11)); }
        float Sat(float3 c) { return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); }
        float3 SetLum(float3 c, float l)
        {
            c += l - Lum(c);
            float lum = Lum(c), n = min(c.r, min(c.g, c.b)), x = max(c.r, max(c.g, c.b));
            if (n < 0 && lum - n > 1e-6) c = lum + (c - lum) * lum / (lum - n);
            if (x > 1 && x - lum > 1e-6) c = lum + (c - lum) * (1 - lum) / (x - lum);
            return saturate(c);
        }
        float3 SetSat(float3 c, float s)
        {
            float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
            if (mx - mn <= 1e-6) return float3(0, 0, 0);
            float3 r = (c - mn) * s / (mx - mn);
            return float3(c.r == mx ? s : (c.r == mn ? 0 : r.r), c.g == mx ? s : (c.g == mn ? 0 : r.g), c.b == mx ? s : (c.b == mn ? 0 : r.b));
        }
        // CPU の MathUtil.ToByte と同じ丸め（floor(v × 255 + 0.5)、半分は切り上げ）で 8 bit の値にしてから書く。RenderTexture への
        // 書き込みの変換は最近接への丸めだが、ちょうど半分のときの向きが実装次第（D3D 系は偶数へ）で、グループのように合成を
        // 重ねると、そのずれが次の段に入って 1 を超える差になる。k/255 を書けば変換は k に落ちる。
        float4 ToBytes(float4 c) { return floor(saturate(c) * 255 + 0.5) / 255; }
        float3 BlendRgb(int mode, float3 d, float3 s)
        {
            if (mode == 0) return s;
            if (mode == 20) return SetLum(SetSat(s, Sat(d)), Lum(d));
            if (mode == 21) return SetLum(SetSat(d, Sat(s)), Lum(d));
            if (mode == 22) return SetLum(s, Lum(d));
            if (mode == 23) return SetLum(d, Lum(s));
            if (mode == 24) return s.r + s.g + s.b < d.r + d.g + d.b - 0.5 / 255 ? s : d;
            if (mode == 25) return s.r + s.g + s.b > d.r + d.g + d.b + 0.5 / 255 ? s : d;
            return float3(Separable(mode, d.r, s.r), Separable(mode, d.g, s.g), Separable(mode, d.b, s.b));
        }
        ENDCG
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
                float3 blend = BlendRgb(_BlendMode, b.rgb, s.rgb);
                float3 premult = (1-sa)*b.a*b.rgb + (1-b.a)*sa*s.rgb + b.a*sa*blend;
                return ToBytes(a > 0 ? float4(premult/a, a) : float4(b.rgb, 0));
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
                float3 m = BlendRgb(_BlendMode, b.rgb, a);
                return ToBytes(float4(b.rgb + (m - b.rgb) * amount, b.a));
            }
            ENDCG
        }
        // Pass 3: クリッピング。_MainTex（クリッピングのまとまり＝下地にここまで重ねた結果）に、_LayerTex（クリッピングされた
        // レイヤー）を合成モードで組み合わせ、レイヤーのアルファ×不透明度×マスクで混ぜる。まとまりのアルファ（下地のアルファ）は
        // 変えない。CpuCompositor.ClipOnto と同じ式。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragClip
            #include "UnityCG.cginc"
            sampler2D _MainTex, _LayerTex, _MaskTex;
            float _Opacity;
            int _BlendMode;
            float4 _Mask;
            float4 fragClip(v2f_img i) : SV_Target
            {
                float4 g = tex2D(_MainTex, i.uv);
                float4 s = tex2D(_LayerTex, i.uv);
                float factor = 1;
                if (_Mask.x > 0.5)
                {
                    float hide = tex2D(_MaskTex, i.uv).a;
                    factor = _Mask.y > 0.5 ? 1 - _Mask.z * (1 - hide) : 1 - _Mask.z * hide;
                }
                float t = s.a * _Opacity * factor;
                if (t <= 0 || g.a <= 0) return g;
                float3 m = BlendRgb(_BlendMode, g.rgb, s.rgb);
                return ToBytes(float4(g.rgb + (m - g.rgb) * t, g.a));
            }
            ENDCG
        }
        // Pass 4: 通過グループのフェード。_MainTex（グループの下の結果）と _LayerTex（その上にグループの中身を重ねた結果）を、
        // グループの 不透明度×マスク で乗算済みアルファの線形補間で混ぜる。CpuCompositor.Fade と同じ式（amount ≥ 1 は中身そのまま、
        // ≤ 0 は下のまま。片方が透明でももう片方を暗くしない）。
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragFade
            #include "UnityCG.cginc"
            sampler2D _MainTex, _LayerTex, _MaskTex;
            float _Opacity;
            float4 _Mask;
            float4 fragFade(v2f_img i) : SV_Target
            {
                float4 b = tex2D(_MainTex, i.uv);
                float4 s = tex2D(_LayerTex, i.uv);
                float factor = 1;
                if (_Mask.x > 0.5)
                {
                    float hide = tex2D(_MaskTex, i.uv).a;
                    factor = _Mask.y > 0.5 ? 1 - _Mask.z * (1 - hide) : 1 - _Mask.z * hide;
                }
                float amount = _Opacity * factor;
                if (amount >= 1) return s;
                if (amount <= 0) return b;
                float ba = b.a * (1 - amount), ia = s.a * amount, a = ba + ia;
                if (a <= 0) return float4(0, 0, 0, 0);
                return ToBytes(float4((b.rgb * ba + s.rgb * ia) / a, a));
            }
            ENDCG
        }
    }
    Fallback Off
}
