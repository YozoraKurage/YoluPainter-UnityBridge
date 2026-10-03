Shader "Hidden/YoluPainter/PreviewChannelPack"
{
    // マテリアル表示（3D ビューを元のマテリアルのシェーダーで描く）のために、塗ったチャンネルの合成（straight RGBA8、左下原点）を
    // マテリアルのテクスチャの読み方に詰め直す。データのチャンネルは塗っていない所（アルファ 0）を 0 とみなして値 × アルファで平らにする
    // （LilToonAssignment.Convert と同じ整数の式: v = (r × a + 127) / 255 の切り捨て）。入力と出力が同じ大きさのときは画素の中心だけを読む。
    Properties { _MainTex ("Composite", 2D) = "black" {} _SecondTex ("Second composite", 2D) = "black" {} _OrigTex ("Original map", 2D) = "white" {} }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex, _SecondTex, _OrigTex;
        float4 _Params; // x: 反転（1 − v）
        float4 _Flags;  // x: Metallic を塗った, y: Roughness を塗った, z: 元のマップがある
        float4 _Consts; // x: _Metallic, y: _Glossiness（塗っていない側に元のマップが無いときの値）
        float Bytes(float x) { return floor(saturate(x) * 255 + 0.5); }
        // 値 × アルファ（塗っていない所は 0）。0〜1
        float Flatten(float4 c) { return floor((Bytes(c.r) * Bytes(c.a) + 127) / 255) / 255; }
        ENDCG
        Pass // 0: 値（r × a）、_Params.x が 1 なら 1 − 値。灰色・不透明
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float v = Flatten(tex2D(_MainTex, i.uv));
                if (_Params.x > 0.5) v = (255 - floor(v * 255 + 0.5)) / 255;
                return float4(v, v, v, 1);
            }
            ENDCG
        }
        Pass // 1: Standard の _MetallicGlossMap（R = Metallic、A = 1 − Roughness）
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 original = tex2D(_OrigTex, i.uv);
                float m = _Flags.x > 0.5 ? Flatten(tex2D(_MainTex, i.uv)) : (_Flags.z > 0.5 ? original.r : _Consts.x);
                float s = _Flags.y > 0.5 ? (255 - Bytes(Flatten(tex2D(_SecondTex, i.uv)))) / 255 : (_Flags.z > 0.5 ? original.a : _Consts.y);
                return float4(m, m, m, s);
            }
            ENDCG
        }
        Pass // 2: 色を sRGB の描き先へ（リニアのカラースペースで、書くときの sRGB への変換の後にバイトが元と同じになるよう、先にリニアへ戻す）
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                return float4(GammaToLinearSpaceExact(c.r), GammaToLinearSpaceExact(c.g), GammaToLinearSpaceExact(c.b), c.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
