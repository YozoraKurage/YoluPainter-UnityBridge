Shader "Hidden/YoluPainter/ShapeGradientOverlay"
{
    // 形のグラデーションの値を 3D ビューの面に薄く重ねる（IsolatedModelPreview.ShownShapeGradient）。値の式は Core の ShapeVolume と
    // 同じ形・同じ順（float）: ボックスはいちばん近い面までの距離、球は表面までの距離をやわらかさの帯で 0〜1 に、平面は 0.5 + y / 幅。
    // その後に Generator のレベル（下限・上限・やわらかさ）と反転。崩しと合成は入れない。プレビューの中だけで、合成・保存には関わらない。
    Properties
    {
        _Tint ("Tint (rgb, max alpha)", Vector) = (1, 0.55, 0.12, 0.55)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Back ZWrite Off ZTest LEqual
        Offset -1, -1
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            Name "SHAPE_GRADIENT_OVERLAY"
            Tags { "LightMode"="Always" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4x4 _ShapeFromWorld;
            float4 _ShapeHalf;   // xyz: 半分の幅、w: 形（0 ボックス・1 球・2 平面）
            float4 _ShapeParams; // x: 帯の幅、y: 1 / 平面の幅
            float4 _ShapeLevels; // 下限・上限・やわらかさ・反転
            float4 _Tint;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 position : SV_POSITION; float3 world : TEXCOORD0; };
            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.world = mul(unity_ObjectToWorld, input.vertex).xyz;
                return output;
            }
            float Ramp(float d)
            {
                if (d <= 0) return 0;
                return _ShapeParams.x <= 0 ? 1 : saturate(d / _ShapeParams.x);
            }
            fixed4 frag(v2f input) : SV_Target
            {
                float3 l = mul(_ShapeFromWorld, float4(input.world, 1)).xyz;
                float b;
                if (_ShapeHalf.w < 0.5)
                {
                    float3 inset = _ShapeHalf.xyz - abs(l);
                    b = Ramp(min(inset.x, min(inset.y, inset.z)));
                }
                else if (_ShapeHalf.w < 1.5) b = Ramp(_ShapeHalf.x - length(l));
                else b = saturate(0.5 + l.y * _ShapeParams.y);
                float t = saturate((b - _ShapeLevels.x) / max(_ShapeLevels.y - _ShapeLevels.x, 1e-6));
                t += _ShapeLevels.z * (t * t * (3 - 2 * t) - t);
                if (_ShapeLevels.w > 0.5) t = 1 - t;
                return fixed4(_Tint.rgb, _Tint.a * t);
            }
            ENDCG
        }
    }
    Fallback Off
}
