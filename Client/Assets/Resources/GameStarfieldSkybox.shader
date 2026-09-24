Shader "MyDemo/StarfieldSkybox"
{
    Properties
    {
        _MainTex ("Starfield", 2D) = "black" {}
        _Tint ("Tint", Color) = (0.72, 0.82, 1, 1)
        _Exposure ("Exposure", Range(0, 4)) = 1
        _Rotation ("Rotation", Range(0, 360)) = 0
        _Mapping ("Panorama Mapping", Range(0, 1)) = 0
        _VerticalOffset ("Vertical Offset", Range(-0.5, 0.5)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            half4 _Tint;
            half _Exposure;
            float _Rotation;
            float _Mapping;
            float _VerticalOffset;

            struct Attributes
            {
                float4 vertex : POSITION;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.direction = input.vertex.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float angle = radians(_Rotation);
                float sine = sin(angle);
                float cosine = cos(angle);
                direction.xz = float2(
                    direction.x * cosine - direction.z * sine,
                    direction.x * sine + direction.z * cosine);

                half3 color;
                if (_Mapping > 0.5)
                {
                    // Equirectangular panorama (2:1 source). U wraps around the
                    // horizon, V runs from the zenith (0) down to the nadir (1).
                    float u = atan2(direction.z, direction.x) * (1.0 / 6.28318530718) + 0.5;
                    float v = acos(clamp(direction.y, -1.0, 1.0)) * (1.0 / 3.14159265359);
                    v = saturate(v + _VerticalOffset);
                    color = tex2D(_MainTex, float2(u, v)).rgb;
                }
                else
                {
                    // Triplanar projection turns a square texture into a
                    // seamless direction-based sky without tying it to world space.
                    float3 weights = pow(abs(direction), 4.0);
                    weights /= max(weights.x + weights.y + weights.z, 0.0001);

                    float2 uvX = direction.zy * 0.5 + 0.5;
                    float2 uvY = direction.xz * 0.5 + 0.5;
                    float2 uvZ = direction.xy * 0.5 + 0.5;
                    color = tex2D(_MainTex, uvX).rgb * weights.x
                        + tex2D(_MainTex, uvY).rgb * weights.y
                        + tex2D(_MainTex, uvZ).rgb * weights.z;
                }
                return half4(color * _Tint.rgb * _Exposure, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
