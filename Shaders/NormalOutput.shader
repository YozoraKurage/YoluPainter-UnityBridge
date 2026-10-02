Shader "Hidden/YoluPainter/NormalOutput"
{
    // Normal の出力（NormalMaps.Output / OutputFromComposites と同じ式・同じ順）。_MainTex = Normal チャンネルのレイヤーの合成、
    // _HeightTex = Height チャンネルの合成（どちらも straight RGBA8、左下原点）。塗った法線をアルファで平らな法線の上に置き、
    // Height → Normal が有効なら、高さ（R × A）を Sobel 3×3（÷8、テクセル単位、Y 上向き）で微分した法線を下地にして RNM で重ねる。
    // 出力は不透明（A = 1）、OpenGL（Y+）。入力は画素の中心だけを読む。
    Properties { _MainTex ("Normal composite", 2D) = "black" {} _HeightTex ("Height composite", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex, _HeightTex;
            float4 _Size;     // 幅, 高さ, 1 / 幅, 1 / 高さ
            float4 _Settings; // x: Height → Normal (1) / なし (0), y: 強さ, z: 端を回り込む (1) / 端の画素を使う (0)

            // TileComposite.shader と同じ式（CPU の NormalMaps と同じ）
            float4 ToBytes(float4 c) { return floor(saturate(c) * 255 + 0.5) / 255; }
            float3 SafeNormalize(float3 v) { float l2 = dot(v, v); return l2 < 1e-12 ? float3(0, 0, 1) : v / sqrt(l2); }
            float3 DecodeNormal(float3 c) { return SafeNormalize(c * 2 - 1); }
            float3 EncodeNormal(float3 v) { return SafeNormalize(v) * 0.5 + 0.5; }
            float3 Rnm(float3 b, float3 d)
            {
                float3 t = b + float3(0, 0, 1), u = d * float3(-1, -1, 1);
                if (t.z <= 1e-6) return b;
                return t * (dot(t, u) / t.z) - u;
            }
            float4 Texel(sampler2D tex, float x, float y) { return tex2Dlod(tex, float4((x + 0.5) * _Size.z, (y + 0.5) * _Size.w, 0, 0)); }
            float HeightAt(float x, float y) { float4 c = Texel(_HeightTex, x, y); return c.r * c.a; }

            float4 frag(v2f_img i) : SV_Target
            {
                float w = _Size.x, h = _Size.y;
                float x = min(floor(i.uv.x * w), w - 1), y = min(floor(i.uv.y * h), h - 1);
                float4 c = Texel(_MainTex, x, y);
                float3 n = c.a <= 0 ? float3(0, 0, 1) : SafeNormalize(DecodeNormal(c.rgb) * c.a + float3(0, 0, 1 - c.a));
                if (_Settings.x > 0.5)
                {
                    bool wrap = _Settings.z > 0.5;
                    float xl = x > 0 ? x - 1 : (wrap ? w - 1 : 0), xr = x < w - 1 ? x + 1 : (wrap ? 0 : w - 1);
                    float yb = y > 0 ? y - 1 : (wrap ? h - 1 : 0), yt = y < h - 1 ? y + 1 : (wrap ? 0 : h - 1);
                    float gx = (HeightAt(xr, yt) + 2 * HeightAt(xr, y) + HeightAt(xr, yb) - HeightAt(xl, yt) - 2 * HeightAt(xl, y) - HeightAt(xl, yb)) / 8;
                    float gy = (HeightAt(xl, yt) + 2 * HeightAt(x, yt) + HeightAt(xr, yt) - HeightAt(xl, yb) - 2 * HeightAt(x, yb) - HeightAt(xr, yb)) / 8;
                    float3 base = SafeNormalize(float3(-_Settings.y * gx, -_Settings.y * gy, 1));
                    n = Rnm(base, n);
                }
                return ToBytes(float4(EncodeNormal(n), 1));
            }
            ENDCG
        }
    }
    Fallback Off
}
