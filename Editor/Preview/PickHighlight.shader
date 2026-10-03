Shader "Hidden/YoluPainter/PickHighlight"
{
    Properties { _IdMap ("ID", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -2, -2
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _IdMap;
            float4 _Color, _IdRgb;
            float _IdTolerance, _UseId;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float3 bary : TEXCOORD1; };
            struct Vary { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 bary : TEXCOORD1; };
            Vary vert(Input v) { Vary o; o.position = UnityObjectToClipPos(v.vertex); o.uv = v.uv; o.bary = v.bary; return o; }
            fixed4 frag(Vary i) : SV_Target
            {
                if (_UseId > .5)
                {
                    clip(i.uv.x); clip(i.uv.y); clip(1-i.uv.x); clip(1-i.uv.y);
                    float4 id = tex2D(_IdMap, i.uv); clip(id.a-.5);
                    float3 delta = abs(floor(id.rgb*255+.5)-_IdRgb.rgb);
                    clip(_IdTolerance+.1-max(delta.x,max(delta.y,delta.z)));
                }
                float3 edge = smoothstep(0, fwidth(i.bary)*1.15, i.bary);
                float wireAlpha = 1-min(edge.x,min(edge.y,edge.z));
                return float4(_Color.rgb, lerp(_Color.a,.9,wireAlpha));
            }
            ENDCG
        }
    }
}
