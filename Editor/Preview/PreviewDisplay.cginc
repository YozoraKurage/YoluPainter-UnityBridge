// 3D ビューの表示（環境・影）で、中立のシェーダー（PreviewSurface）・マテリアル表示の影の重ね描き（PreviewShadow）・環境の焼き
// （PreviewEnvironment）が共に使う式。プレビューの中だけの描画で、合成・保存には関わらない。
#ifndef YOLUPAINTER_PREVIEW_DISPLAY_INCLUDED
#define YOLUPAINTER_PREVIEW_DISPLAY_INCLUDED

// ───── 環境の向きと緯度経度 ─────

// 環境を上の軸のまわりに θ 回したとき、世界の向き d に見える元の環境の向き（R(−θ) d）。_YPRotation = (cos θ, sin θ)。
float4 _YPRotation;
float3 YPToSource(float3 d)
{
    float c = _YPRotation.x, s = _YPRotation.y;
    return float3(d.x * c - d.z * s, d.y, d.x * s + d.z * c);
}

// Unity の Skybox/Panoramic と同じ緯度経度の読み方（u = 0.5 − atan2(z, x) / 2π、v = 1 − acos(y) / π）。
float2 YPLatLong(float3 d)
{
    d = normalize(d);
    float longitude = atan2(d.z, d.x), latitude = acos(clamp(d.y, -1, 1));
    return float2(0.5 - longitude * (0.5 / UNITY_PI), 1 - latitude * (1 / UNITY_PI));
}
float3 YPFromLatLong(float2 uv)
{
    float longitude = (0.5 - uv.x) * (2 * UNITY_PI), latitude = (1 - uv.y) * UNITY_PI;
    float s = sin(latitude);
    return float3(s * cos(longitude), cos(latitude), s * sin(longitude));
}

// ───── 環境光（Unity の SphericalHarmonicsL2 と同じ並びと式。c0 + c1 y + c2 z + c3 x + c4 xy + c5 yz + c6 (3z²−1) + c7 xz + c8 (x²−y²)） ─────

float4 _YPSH[9];
float3 YPEvaluateSH(float3 n)
{
    float3 r = _YPSH[0].rgb + _YPSH[1].rgb * n.y + _YPSH[2].rgb * n.z + _YPSH[3].rgb * n.x;
    r += _YPSH[4].rgb * (n.x * n.y) + _YPSH[5].rgb * (n.y * n.z) + _YPSH[6].rgb * (3 * n.z * n.z - 1);
    r += _YPSH[7].rgb * (n.x * n.z) + _YPSH[8].rgb * (n.x * n.x - n.y * n.y);
    return max(r, 0);
}

// ───── 影（自前の影のマップ: 光から見た深さを 0〜1 に。PreviewShadowMap） ─────

sampler2D _YPShadowMap;
float4x4 _YPShadowMatrix;   // 世界 → (u, v, 深さ 0〜1。0 が光の側)
float4 _YPShadowParams;     // x: 使う（1）・使わない（0）、y: ぼかしの半径（u v）、z: 深さの偏り、w: 法線の向きにずらす量（世界）
float4 _YPShadowLight;      // xyz: 光の来る向き（世界）

static const float2 YPPoisson[16] =
{
    float2(-0.94201624, -0.39906216), float2(0.94558609, -0.76890725), float2(-0.09418410, -0.92938870), float2(0.34495938, 0.29387760),
    float2(-0.91588581, 0.45771432), float2(-0.81544232, -0.87912464), float2(-0.38277543, 0.27676845), float2(0.97484398, 0.75648379),
    float2(0.44323325, -0.97511554), float2(0.53742981, -0.47373420), float2(-0.26496911, -0.41893023), float2(0.79197514, 0.19090188),
    float2(-0.24188840, 0.99706507), float2(-0.81409955, 0.91437590), float2(0.19984126, 0.78641367), float2(0.14383161, -0.14100790)
};

// 影の量（0 = 光が当たる、1 = 影）。normal は面の法線（世界）、pixel は画面の画素の位置（ぼかしの向きを画素ごとに回す）。
float YPShadow(float3 world, float3 normal, float2 pixel)
{
    if (_YPShadowParams.x < 0.5) return 0;
    float nl = dot(normal, _YPShadowLight.xyz);
    // 光に対して寝た面ほど法線の向きに大きくずらす（影のにきびを避ける）
    float3 p = world + normal * _YPShadowParams.w * saturate(1.5 - abs(nl));
    float3 s = mul(_YPShadowMatrix, float4(p, 1)).xyz;
    if (s.x < 0 || s.y < 0 || s.x > 1 || s.y > 1 || s.z > 1) return 0;
    float depth = s.z - _YPShadowParams.z;
    float angle = 6.2831853 * frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    float ca = cos(angle), sa = sin(angle);
    float sum = 0;
    [unroll] for (int k = 0; k < 16; k++)
    {
        float2 o = YPPoisson[k];
        o = float2(o.x * ca - o.y * sa, o.x * sa + o.y * ca) * _YPShadowParams.y;
        sum += depth > tex2Dlod(_YPShadowMap, float4(s.xy + o, 0, 0)).r ? 1 : 0;
    }
    return sum / 16;
}

#endif
