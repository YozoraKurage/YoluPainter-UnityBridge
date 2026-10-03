Shader "Hidden/YoluPainter/PreviewToneMap"
{
    // 3D ビューの絵だけに当てる露出とトーンマッピング（IsolatedModelPreview）。テクスチャの値・書き出し・2D キャンバスには当てない。
    // 入力は HDR の描き先。値の意味は前の版の 8 bit の描き先と同じ「画面にそのまま出す値」なので、ガンマの値としてリニアに直してから当て、戻す
    // （_YPToneMap.z。どちらのカラースペースでも 1）。
    // Neutral は Unity の Post Processing の NeutralTonemap、ACES は Stephen Hill の近似（sRGB の原色）。None は露出を掛けて 0〜1 で切るだけ。
    Properties { _MainTex ("HDR", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "TONE_MAP"
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _YPToneMap; // x: 曲線（0 None・1 Neutral・2 ACES）、y: 露出の倍率、z: ガンマの値として扱う（1）

            float3 NeutralCurve(float3 x, float a, float b, float c, float d, float e, float f) { return ((x * (a * x + c * b) + d * e) / (x * (a * x + b) + d * f)) - e / f; }
            float3 Neutral(float3 x)
            {
                const float a = 0.2, b = 0.29, c = 0.24, d = 0.272, e = 0.02, f = 0.3, whiteLevel = 5.3;
                float3 whiteScale = 1.0 / NeutralCurve(whiteLevel, a, b, c, d, e, f);
                return NeutralCurve(x * whiteScale, a, b, c, d, e, f) * whiteScale;
            }
            float3 Aces(float3 color)
            {
                const float3x3 acesIn = { 0.59719, 0.35458, 0.04823, 0.07600, 0.90834, 0.01566, 0.02840, 0.13383, 0.83777 };
                const float3x3 acesOut = { 1.60475, -0.53108, -0.07367, -0.10208, 1.10813, -0.00605, -0.00327, -0.07276, 1.07602 };
                float3 v = mul(acesIn, color);
                float3 a = v * (v + 0.0245786) - 0.000090537, b = v * (0.983729 * v + 0.4329510) + 0.238081;
                return mul(acesOut, a / b);
            }
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float3 x = max(c.rgb, 0);
                if (_YPToneMap.z > 0.5) x = GammaToLinearSpace(x);
                x *= _YPToneMap.y;
                if (_YPToneMap.x > 1.5) x = Aces(x); else if (_YPToneMap.x > 0.5) x = Neutral(x);
                x = saturate(x);
                if (_YPToneMap.z > 0.5) x = LinearToGammaSpace(x);
                return float4(x, c.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
