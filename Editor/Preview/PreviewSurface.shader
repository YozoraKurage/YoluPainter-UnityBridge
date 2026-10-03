Shader "Hidden/YoluPainter/PreviewSurface"
{
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
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color;
            float _PreviewLit;
            sampler2D _NormalMap;
            float _UseNormalMap;
            float4 _PreviewLightDir, _PreviewLight, _PreviewAmbient;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1; float3 tangent : TEXCOORD2; float3 bitangent : TEXCOORD3; };
            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.normal = UnityObjectToWorldNormal(input.normal);
                output.tangent = UnityObjectToWorldDir(input.tangent.xyz);
                output.bitangent = cross(output.normal, output.tangent) * input.tangent.w * unity_WorldTransformParams.w;
                return output;
            }
            fixed4 frag(v2f input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv) * _Color;
                float checker = fmod(floor(input.uv.x * 24) + floor(input.uv.y * 24), 2);
                float3 background = lerp(float3(0.24, 0.24, 0.24), float3(0.34, 0.34, 0.34), checker);
                float3 n = normalize(input.normal);
                if (_UseNormalMap > 0.5)
                {
                    // YoluPainter の Normal の出力: リニアの RGB に詰めた接空間の法線（OpenGL の Y+）
                    float3 t = tex2D(_NormalMap, input.uv).xyz * 2 - 1;
                    n = normalize(input.tangent * t.x + input.bitangent * t.y + n * t.z);
                }
                float3 light = _PreviewAmbient.rgb + _PreviewLight.rgb * saturate(dot(n, normalize(_PreviewLightDir.xyz)));
                color.rgb = lerp(background, color.rgb, color.a);
                return fixed4(color.rgb * lerp(float3(1, 1, 1), light, _PreviewLit), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
