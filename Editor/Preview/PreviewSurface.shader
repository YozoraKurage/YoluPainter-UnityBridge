Shader "Hidden/DotTexturePainter/PreviewSurface"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _PreviewLit ("Neutral lighting", Float) = 1
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
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1; };
            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.normal = UnityObjectToWorldNormal(input.normal);
                return output;
            }
            fixed4 frag(v2f input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv) * _Color;
                float checker = fmod(floor(input.uv.x * 24) + floor(input.uv.y * 24), 2);
                float3 background = lerp(float3(0.24, 0.24, 0.24), float3(0.34, 0.34, 0.34), checker);
                float light = 0.35 + 0.65 * saturate(dot(normalize(input.normal), normalize(float3(-0.3, 0.65, -0.7))));
                color.rgb = lerp(background, color.rgb, color.a);
                return fixed4(color.rgb * lerp(1, light, _PreviewLit), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
