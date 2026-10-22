Shader "ActiveCombat/TacticalMap"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END
            struct Attributes { float4 Position : POSITION; float2 TextureCoordinates : TEXCOORD0; float2 Shape : TEXCOORD1; half4 Color : COLOR; };
            struct Varyings { float4 Position : SV_POSITION; float2 TextureCoordinates : TEXCOORD0; float Circular : TEXCOORD1; half4 Color : COLOR; };
            Varyings Vertex(Attributes Input)
            {
                Varyings Output;
                Output.Position = TransformObjectToHClip(Input.Position.xyz);
                Output.TextureCoordinates = Input.TextureCoordinates;
                Output.Circular = Input.Shape.x;
                Output.Color = Input.Color;
                #ifndef UNITY_COLORSPACE_GAMMA
                    Output.Color.rgb = SRGBToLinear(Output.Color.rgb);
                #endif
                return Output;
            }
            half4 Fragment(Varyings Input) : SV_Target
            {
                clip(0.25 - dot(Input.TextureCoordinates - 0.5, Input.TextureCoordinates - 0.5) * Input.Circular);
                return _BaseColor * Input.Color;
            }
            ENDHLSL
        }
    }
}
