Shader "ActiveCombat/EntityEffect"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1,1,1,1)
        _CombatEffectParameters ("Effect parameters", Vector) = (1,0,1,0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _CombatEffectParameters;
            CBUFFER_END
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
                UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
                    UNITY_DOTS_INSTANCED_PROP(float4, _CombatEffectParameters)
                UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
                #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
                #define _CombatEffectParameters UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _CombatEffectParameters)
            #endif
            struct Attributes
            {
                float3 Position : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 Position : SV_POSITION;
                float3 WorldPosition : TEXCOORD0;
                float4 Parameters : TEXCOORD1;
                half4 Color : COLOR;
                half Fog : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vertex(Attributes Input)
            {
                UNITY_SETUP_INSTANCE_ID(Input);
                Varyings Output = (Varyings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(Output);
                float3 Position = Input.Position;
                float4 Parameters = _CombatEffectParameters;
                if (Parameters.y > 0.5 && Parameters.y < 1.5 && dot(Position.xz, Position.xz) < 0.95)
                {
                    Position.xz *= (1 - 0.09 / max(0.00001, Parameters.x)) / 0.91;
                }
                if (Parameters.y > 2.5 && Position.z > 0)
                {
                    Position.x *= Parameters.z;
                }
                Output.WorldPosition = TransformObjectToWorld(Position);
                Output.Position = TransformWorldToHClip(Output.WorldPosition);
                Output.Color = _BaseColor;
                Output.Parameters = Parameters;
                Output.Fog = ComputeFogFactor(Output.Position.z);
                return Output;
            }
            half4 Fragment(Varyings Input) : SV_Target
            {
                if (Input.Parameters.y > 1.5 && Input.Parameters.y < 2.5)
                {
                    float2 Cell = abs(frac(Input.WorldPosition.xz / Input.Parameters.z + 0.5) - 0.5);
                    clip(0.46 - max(Cell.x, Cell.y));
                    clip((Input.Parameters.x + 0.5) * Input.Parameters.z - max(abs(Input.WorldPosition.x), abs(Input.WorldPosition.z)));
                }
                Input.Color.rgb = MixFog(Input.Color.rgb, Input.Fog);
                return Input.Color;
            }
            ENDHLSL
        }
    }
}
