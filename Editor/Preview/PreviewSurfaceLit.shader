Shader "Hidden/YoluPainter/PreviewSurfaceLit"
{
    // 中立の表示（PreviewSurface）に、環境（SH の拡散と決まった粗さの映り込み）と自前の影のマップを足したもの。環境か影を使うあいだだけ
    // IsolatedModelPreview が中立の材料のシェーダーをこれに替える（使わなければ前の版の PreviewSurface のまま。値の意味と既定は同じ）。
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _PreviewLit ("Neutral lighting", Range(0, 1)) = 1
        _NormalMap ("Normal map (tangent space, OpenGL Y+)", 2D) = "bump" {}
        [ToggleUI] _UseNormalMap ("Use normal map", Float) = 0
        // 擬似的なシーンの照明（PreviewSceneSettings）。既定は前の版の固定の値と同じ: 0.35 + 0.65 × saturate(dot(n, L))。
        // 色も Vector で持つ（Color はリニアのカラースペースで変換されて、前の版の明るさと変わってしまう）
        _PreviewLightDir ("Direction toward the light (world)", Vector) = (-0.3, 0.65, -0.7, 0)
        _PreviewLight ("Light (rgb multiplier)", Vector) = (0.65, 0.65, 0.65, 1)
        _PreviewAmbient ("Ambient (rgb multiplier)", Vector) = (0.35, 0.35, 0.35, 1)
        // 環境（PreviewEnvironment）: 使うあいだは一様な環境光の代わりに環境の拡散（SH、_YPSH）と、決まった粗さの誘電体の映り込み
        [ToggleUI] _YPEnvOn ("Environment", Float) = 0
        _YPEnvCube ("Environment (convolved cube)", Cube) = "black" {}
        _YPEnvParams ("Environment (x: intensity, y: reflection mip, z: reflection strength)", Vector) = (1, 4, 1, 0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Back ZWrite On ZTest LEqual
        Pass
        {
            Name "DOT_PREVIEW"
            Tags { "LightMode"="Always" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "PreviewDisplay.cginc"
            sampler2D _MainTex;
            float4 _Color;
            float _PreviewLit;
            sampler2D _NormalMap;
            float _UseNormalMap;
            float4 _PreviewLightDir, _PreviewLight, _PreviewAmbient;
            float _YPEnvOn;
            samplerCUBE _YPEnvCube;
            float4 _YPEnvParams;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1; float3 tangent : TEXCOORD2; float3 bitangent : TEXCOORD3; float3 world : TEXCOORD4; };
            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.normal = UnityObjectToWorldNormal(input.normal);
                output.tangent = UnityObjectToWorldDir(input.tangent.xyz);
                output.bitangent = cross(output.normal, output.tangent) * input.tangent.w * unity_WorldTransformParams.w;
                output.world = mul(unity_ObjectToWorld, input.vertex).xyz;
                return output;
            }
            float4 frag(v2f input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv) * _Color;
                float checker = fmod(floor(input.uv.x * 24) + floor(input.uv.y * 24), 2);
                float3 background = lerp(float3(0.24, 0.24, 0.24), float3(0.34, 0.34, 0.34), checker);
                float3 geometric = normalize(input.normal);
                float3 n = geometric;
                if (_UseNormalMap > 0.5)
                {
                    // YoluPainter の Normal の出力: リニアの RGB に詰めた接空間の法線（OpenGL の Y+）
                    float3 t = tex2D(_NormalMap, input.uv).xyz * 2 - 1;
                    n = normalize(input.tangent * t.x + input.bitangent * t.y + n * t.z);
                }
                float3 toLight = normalize(_PreviewLightDir.xyz);
                // 影は面の形の法線で調べる（ノーマルマップの細かい凹凸は影のマップに無い）
                float shadow = YPShadow(input.world, geometric, input.position.xy);
                float3 ambient = _PreviewAmbient.rgb;
                float3 reflection = 0;
                if (_YPEnvOn > 0.5)
                {
                    ambient = YPEvaluateSH(n) * _YPEnvParams.x;
                    // 決まった粗さ（mip）の誘電体（F0 = 0.04）の映り込み。塗った値は変えず、形を読む手がかりとして足す
                    float3 v = normalize(_WorldSpaceCameraPos - input.world);
                    float nv = saturate(dot(n, v));
                    float fresnel = 0.04 + (0.5 - 0.04) * pow(1 - nv, 5);
                    reflection = texCUBElod(_YPEnvCube, float4(reflect(-v, n), _YPEnvParams.y)).rgb * fresnel * _YPEnvParams.x * _YPEnvParams.z;
                }
                float3 light = ambient + _PreviewLight.rgb * saturate(dot(n, toLight)) * (1 - shadow);
                color.rgb = lerp(background, color.rgb, color.a);
                return float4(color.rgb * lerp(float3(1, 1, 1), light, _PreviewLit) + reflection * _PreviewLit, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
