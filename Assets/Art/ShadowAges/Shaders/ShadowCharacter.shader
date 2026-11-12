Shader "VaticanDefendor/Skinned Character"
{
    Properties
    {
        _BaseMap ("Base color", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)
        _NormalMap ("Normal", 2D) = "bump" {}
        _PackedMap ("Occlusion Roughness Metallic", 2D) = "white" {}
        _EmissionMap ("Emission", 2D) = "black" {}
        _EmissionColor ("Emission color", Color) = (0,0,0,1)
        _Smoothness ("Smoothness", Range(0,1)) = 0.6
        _Metallic ("Metallic", Range(0,1)) = 0
        _UsePacked ("Use packed map", Float) = 0
        [HideInInspector] _SkinMatrixIndex ("Skin matrix index", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile_instancing
        #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _EmissionColor;
            float _Smoothness;
            float _Metallic;
            float _UsePacked;
            float _SkinMatrixIndex;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
        TEXTURE2D(_PackedMap); SAMPLER(sampler_PackedMap);
        TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
        #if defined(UNITY_DOTS_INSTANCING_ENABLED)
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
                UNITY_DOTS_INSTANCED_PROP(float, _SkinMatrixIndex)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
            #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
            ByteAddressBuffer _SkinMatrices;
            float3x4 LoadBone(uint Index)
            {
                uint Offset = Index * 48;
                float4 First = asfloat(_SkinMatrices.Load4(Offset));
                float4 Second = asfloat(_SkinMatrices.Load4(Offset + 16));
                float4 Third = asfloat(_SkinMatrices.Load4(Offset + 32));
                return float3x4(First.x,First.w,Second.z,Third.y,First.y,Second.x,Second.w,Third.z,First.z,Second.y,Third.x,Third.w);
            }
        #endif
        struct Attributes
        {
            float3 Position : POSITION;
            float3 Normal : NORMAL;
            float4 Tangent : TANGENT;
            float2 Coordinates : TEXCOORD0;
            float4 Weights : BLENDWEIGHTS;
            uint4 Bones : BLENDINDICES;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 Position : SV_POSITION;
            float3 WorldPosition : TEXCOORD0;
            float3 Normal : TEXCOORD1;
            float4 Tangent : TEXCOORD2;
            float2 Coordinates : TEXCOORD3;
            half Fog : TEXCOORD4;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };
        void Skin(inout Attributes Input)
        {
            #if defined(UNITY_DOTS_INSTANCING_ENABLED)
                uint Start = asuint(UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _SkinMatrixIndex));
                float3 Position = 0;
                float3 Normal = 0;
                float3 Tangent = 0;
                for (int Influence = 0; Influence < 4; Influence++)
                {
                    float3x4 Matrix = LoadBone(Start + Input.Bones[Influence]);
                    Position += mul(Matrix, float4(Input.Position, 1)) * Input.Weights[Influence];
                    Normal += mul(Matrix, float4(Input.Normal, 0)) * Input.Weights[Influence];
                    Tangent += mul(Matrix, float4(Input.Tangent.xyz, 0)) * Input.Weights[Influence];
                }
                Input.Position = Position;
                Input.Normal = Normal;
                Input.Tangent.xyz = Tangent;
            #endif
        }
        Varyings Vertex(Attributes Input)
        {
            UNITY_SETUP_INSTANCE_ID(Input);
            Skin(Input);
            Varyings Output = (Varyings)0;
            UNITY_TRANSFER_INSTANCE_ID(Input, Output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(Output);
            VertexPositionInputs Position = GetVertexPositionInputs(Input.Position);
            VertexNormalInputs Normal = GetVertexNormalInputs(Input.Normal, Input.Tangent);
            Output.Position = Position.positionCS;
            Output.WorldPosition = Position.positionWS;
            Output.Normal = Normal.normalWS;
            Output.Tangent = float4(Normal.tangentWS, Input.Tangent.w * GetOddNegativeScale());
            Output.Coordinates = TRANSFORM_TEX(Input.Coordinates, _BaseMap);
            Output.Fog = ComputeFogFactor(Position.positionCS.z);
            return Output;
        }
        half4 Fragment(Varyings Input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(Input);
            half4 Base = SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,Input.Coordinates) * _BaseColor;
            half3 Packed = SAMPLE_TEXTURE2D(_PackedMap,sampler_PackedMap,Input.Coordinates).rgb;
            half3 Normal = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,Input.Coordinates));
            half3 Bitangent = cross(Input.Normal,Input.Tangent.xyz) * Input.Tangent.w;
            InputData Lighting = (InputData)0;
            Lighting.positionWS = Input.WorldPosition;
            Lighting.normalWS = normalize(mul(Normal,half3x3(normalize(Input.Tangent.xyz),normalize(Bitangent),normalize(Input.Normal))));
            Lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(Input.WorldPosition);
            Lighting.shadowCoord = TransformWorldToShadowCoord(Input.WorldPosition);
            Lighting.fogCoord = Input.Fog;
            Lighting.bakedGI = SampleSH(Lighting.normalWS);
            Lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(Input.Position);
            Lighting.shadowMask = half4(1,1,1,1);
            SurfaceData Surface = (SurfaceData)0;
            Surface.albedo = Base.rgb;
            Surface.alpha = 1;
            Surface.normalTS = Normal;
            Surface.metallic = lerp(_Metallic,Packed.b,_UsePacked);
            Surface.smoothness = lerp(_Smoothness,1-Packed.g,_UsePacked);
            Surface.occlusion = lerp(1,Packed.r,_UsePacked);
            Surface.emission = SAMPLE_TEXTURE2D(_EmissionMap,sampler_EmissionMap,Input.Coordinates).rgb * _EmissionColor.rgb;
            half4 Color = UniversalFragmentPBR(Lighting,Surface);
            Color.rgb = MixFog(Color.rgb,Input.Fog);
            return Color;
        }
        half4 DepthFragment(Varyings Input) : SV_Target
        {
            return 0;
        }
        ENDHLSL
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment DepthFragment
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode"="DepthOnly" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment DepthFragment
            ENDHLSL
        }
    }
}
