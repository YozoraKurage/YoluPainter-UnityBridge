Shader "Hidden/YoluPainter/PreviewShadow"
{
    // 3D ビューの自己の影（PreviewShadowMap）: パス 0 は光から見た深さ（0〜1、0 が光の側）を描く。パス 1 はマテリアル表示の上に掛け算で
    // 重ねる影で、面の向きが光に向いている所を「環境光 ÷（環境光 + 直接光 × N·L）」まで暗くする（ランバートの面なら影の中の明るさと同じ。
    // 映り込み・発光・トゥーンの影の色は区別しない近似）。プレビューの中だけの描画。
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "PreviewDisplay.cginc"
    float4x4 _YPShadowViewProj; // 光のカメラ（GPU の向きに直した射影 × 光の向きの空間）
    float4 _YPOverlay;          // x: 直接光の明るさ（輝度）、y: 一様な環境光の明るさ（輝度）、z: 環境の SH を使う（1）、w: 環境の明るさ
    ENDCG
    SubShader
    {
        Pass
        {
            Name "CASTER"
            Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            struct v2f { float4 position : SV_POSITION; float depth : TEXCOORD0; };
            v2f vert(float4 vertex : POSITION)
            {
                float4 world = mul(unity_ObjectToWorld, vertex);
                v2f o; o.position = mul(_YPShadowViewProj, world); o.depth = mul(_YPShadowMatrix, world).z; return o;
            }
            float4 frag(v2f i) : SV_Target { return i.depth; }
            ENDCG
        }
        Pass
        {
            Name "OVERLAY"
            Tags { "LightMode"="Always" }
            Cull Back ZWrite Off ZTest LEqual
            Offset -1, -1
            Blend DstColor Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 position : SV_POSITION; float3 world : TEXCOORD0; float3 normal : TEXCOORD1; };
            v2f vert(appdata input)
            {
                v2f o; o.position = UnityObjectToClipPos(input.vertex);
                o.world = mul(unity_ObjectToWorld, input.vertex).xyz; o.normal = UnityObjectToWorldNormal(input.normal); return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float nl = dot(n, _YPShadowLight.xyz);
                if (nl <= 0) return 1;
                float shadow = YPShadow(i.world, n, i.position.xy);
                float ambient = _YPOverlay.z > 0.5 ? dot(YPEvaluateSH(n), float3(0.2126, 0.7152, 0.0722)) * _YPOverlay.w : _YPOverlay.y;
                float direct = _YPOverlay.x * nl;
                float ratio = (ambient + 1e-3) / (ambient + direct + 1e-3);
                return lerp(1, ratio, shadow);
            }
            ENDCG
        }
    }
    Fallback Off
}
