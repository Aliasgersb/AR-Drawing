Shader "SpatialDrawing/TubeVertexColor"
{
    Properties
    {
        _AmbientLight ("Ambient Light", Range(0, 1)) = 0.85
        _DirectionalLight ("Directional Light", Vector) = (0.5, 1.0, 0.2, 0)
        _LightStrength ("Light Strength", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float4 color      : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                float  _AmbientLight;
                float4 _DirectionalLight;
                float  _LightStrength;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS   = TransformObjectToWorldNormal(input.normalOS);
                output.color      = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normal = normalize(input.normalWS);
                float3 lightDir = normalize(_DirectionalLight.xyz);

                // Very subtle diffuse — just enough to show 3D shape
                float nDotL = saturate(dot(normal, lightDir));

                // High ambient keeps base color bright
                // Low light strength prevents harsh seams
                float lighting = _AmbientLight + (_LightStrength * nDotL);

                float3 finalColor = input.color.rgb * lighting;
                return half4(finalColor, input.color.a);
            }
            ENDHLSL
        }
    }
}
