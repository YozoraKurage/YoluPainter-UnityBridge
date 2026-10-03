Shader "Hidden/YoluPainter/PreviewEnvironment"
{
    // 3D ビューの環境（PreviewEnvironment）: 元（内蔵の空・緯度経度の 2D・キューブ）を回してキューブの写しに焼き（パス 0）、粗さの段ごとに
    // GGX で畳み込み（パス 1）、環境光の SH のために小さな緯度経度に写し（パス 2）、背景に描く（パス 3）。元のテクスチャは GPU で読むだけで、
    // 取り込みの設定は変えない。プレビューの中だけの描画。
    Properties
    {
        _YPSourceLatLong ("Source (latitude-longitude)", 2D) = "black" {}
        _YPSourceCube ("Source (cube)", Cube) = "black" {}
        _YPSrcCube ("Baked cube (box mips)", Cube) = "black" {}
        _YPEnvCube ("Convolved cube", Cube) = "black" {}
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "PreviewDisplay.cginc"
    sampler2D _YPSourceLatLong;
    samplerCUBE _YPSourceCube;
    samplerCUBE _YPSrcCube;
    samplerCUBE _YPEnvCube;
    float4 _YPSource;        // x: 元の種類（0 空・1 緯度経度・2 キューブ・3 向きの確かめの模様）、y: 元を読む mip、z: 明るさ
    float4 _YPSkyZenith, _YPSkyHorizon, _YPSkyGround;
    float4 _YPFaceZ, _YPFaceX, _YPFaceY; // キューブの面の向き: d = Z + (2u − 1) X + (2v − 1) Y
    float4 _YPConvolve;      // x: GGX の α（粗さ²）、y: 写しの 1 面の画素数、z: 標本の数、w: 写しの最後の mip
    float4 _YPSamples[64];   // Hammersley の (ξ1, ξ2)
    float4x4 _YPInvViewProj; // 背景: 画面 → 世界
    float4 _YPBackground;    // x: 環境の mip（< 0 なら元を直に読む）、y: 明るさ、z: 元を直に読むときの mip

    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
    struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata input) { v2f o; o.position = UnityObjectToClipPos(input.vertex); o.uv = input.uv; return o; }

    float3 Sky(float3 d)
    {
        float y = d.y;
        float3 up = lerp(_YPSkyHorizon.rgb, _YPSkyZenith.rgb, sqrt(saturate(y)));
        float3 down = lerp(_YPSkyHorizon.rgb, _YPSkyGround.rgb, saturate(-y * 6));
        return y >= 0 ? up : down;
    }

    // 世界の向き d に見える元の値（回転は _YPRotation、mip は lod）
    float3 SampleSource(float3 d, float lod)
    {
        float3 s = YPToSource(normalize(d));
        if (_YPSource.x < 0.5) return Sky(s);
        if (_YPSource.x < 1.5) return tex2Dlod(_YPSourceLatLong, float4(YPLatLong(s), 0, lod)).rgb;
        if (_YPSource.x < 2.5) return texCUBElod(_YPSourceCube, float4(s, lod)).rgb;
        return s * 0.5 + 0.5; // 向きの確かめ（回さない元で使う）
    }

    float3 FaceDirection(float2 uv) { return normalize(_YPFaceZ.xyz + (uv.x * 2 - 1) * _YPFaceX.xyz + (uv.y * 2 - 1) * _YPFaceY.xyz); }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "BAKE_FACE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target { return float4(SampleSource(FaceDirection(i.uv), _YPSource.y), 1); }
            ENDCG
        }
        Pass
        {
            Name "CONVOLVE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float3 n = FaceDirection(i.uv);
                float alpha = _YPConvolve.x;
                if (alpha <= 0) return float4(texCUBElod(_YPSrcCube, float4(n, 0)).rgb, 1);
                float3 up = abs(n.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 tx = normalize(cross(up, n)), ty = cross(n, tx);
                float a2 = alpha * alpha, texelSolidAngle = 4 * UNITY_PI / (6 * _YPConvolve.y * _YPConvolve.y);
                int count = (int)_YPConvolve.z;
                float3 sum = 0; float weight = 0;
                for (int k = 0; k < 64; k++)
                {
                    if (k >= count) break;
                    float2 xi = _YPSamples[k].xy;
                    float phi = 2 * UNITY_PI * xi.x;
                    float cosTheta = sqrt((1 - xi.y) / (1 + (a2 - 1) * xi.y)), sinTheta = sqrt(saturate(1 - cosTheta * cosTheta));
                    float3 h = tx * (sinTheta * cos(phi)) + ty * (sinTheta * sin(phi)) + n * cosTheta;
                    float3 l = 2 * dot(n, h) * h - n;
                    float nl = dot(n, l);
                    if (nl <= 0) continue;
                    // 見る向き = 法線と見なす（Unity の映り込みのキューブと同じ近似）。標本の立体角から読む mip を決める（ちらつきを抑える）
                    float nh = saturate(cosTheta), d = (nh * nh * (a2 - 1) + 1);
                    float pdf = a2 / (UNITY_PI * d * d) * 0.25;
                    float sampleSolidAngle = 1 / (count * pdf + 1e-6);
                    float lod = clamp(0.5 * log2(sampleSolidAngle / texelSolidAngle) + 1, 0, _YPConvolve.w);
                    sum += texCUBElod(_YPSrcCube, float4(l, lod)).rgb * nl;
                    weight += nl;
                }
                return float4(sum / max(weight, 1e-4), 1);
            }
            ENDCG
        }
        Pass
        {
            Name "TO_LATLONG"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target { return float4(texCUBElod(_YPSrcCube, float4(YPFromLatLong(i.uv), _YPSource.y)).rgb, 1); }
            ENDCG
        }
        Pass
        {
            Name "BACKGROUND"
            Tags { "LightMode"="Always" }
            CGPROGRAM
            #pragma vertex vertBackground
            #pragma fragment frag
            #pragma target 3.0
            struct v2fb { float4 position : SV_POSITION; float2 ndc : TEXCOORD0; };
            v2fb vertBackground(appdata input)
            {
                // 画面いっぱいの四角（頂点は −1〜1 のクリップ空間に置く）。奥行きは見ない
                v2fb o; o.position = float4(input.vertex.xy, 0.5, 1); o.ndc = input.vertex.xy; return o;
            }
            float4 frag(v2fb i) : SV_Target
            {
                float4 p = mul(_YPInvViewProj, float4(i.ndc, 0.5, 1));
                float3 d = normalize(p.xyz / p.w - _WorldSpaceCameraPos);
                float3 c = _YPBackground.x < 0 ? SampleSource(d, _YPBackground.z) : texCUBElod(_YPEnvCube, float4(d, _YPBackground.x)).rgb;
                return float4(c * _YPBackground.y, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
