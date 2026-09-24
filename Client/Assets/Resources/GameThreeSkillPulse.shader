Shader "GameThree/SkillPulse"
{
    Properties
    {
        _Color ("Color", Color) = (0.2, 0.8, 1, 0.8)
        _PulseSpeed ("Pulse Speed", Float) = 3
        _Intensity ("Intensity", Range(0, 2)) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _PulseSpeed;
            float _Intensity;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldViewDir : TEXCOORD1;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldViewDir = WorldSpaceViewDir(input.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = _Color;
                float3 normal = normalize(input.worldNormal);
                float3 viewDirection = normalize(input.worldViewDir);
                float facing = abs(dot(normal, viewDirection));
                // Keep the center of a domain transparent so the camera inside
                // it does not turn the whole screen into a translucent haze.
                float rim = 1.0 - saturate(facing);
                float pulse = 0.55 + 0.35 * sin(_Time.y * _PulseSpeed);
                color.a *= (0.06 + 0.94 * rim) * pulse * _Intensity;
                return color;
            }
            ENDCG
        }
    }
}
