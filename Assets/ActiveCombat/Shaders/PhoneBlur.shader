Shader "ActiveCombat/PhoneBlur"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _BlurPixels ("Blur Pixels", Float) = 6
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct VertexInput { float4 Position : POSITION; float4 Color : COLOR; float2 Coordinates : TEXCOORD0; };
            struct VertexOutput { float4 Position : SV_POSITION; float4 Color : COLOR; float2 Coordinates : TEXCOORD0; };
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Color;
            float _BlurPixels;
            VertexOutput Vertex(VertexInput Input)
            {
                VertexOutput Output;
                Output.Position=UnityObjectToClipPos(Input.Position);
                Output.Coordinates=Input.Coordinates;
                Output.Color=Input.Color*_Color;
                return Output;
            }
            fixed4 Fragment(VertexOutput Input) : SV_Target
            {
                float2 Step=_MainTex_TexelSize.xy*_BlurPixels*0.5;
                fixed4 Result=0;
                float Total=0;
                [unroll]
                for(int Horizontal=-2;Horizontal<=2;Horizontal++)
                {
                    [unroll]
                    for(int Vertical=-2;Vertical<=2;Vertical++)
                    {
                        float Weight=exp(-0.5*(Horizontal*Horizontal+Vertical*Vertical));
                        Result+=tex2D(_MainTex,Input.Coordinates+float2(Horizontal,Vertical)*Step)*Weight;
                        Total+=Weight;
                    }
                }
                Result/=Total;
                return Result*Input.Color;
            }
            ENDCG
        }
    }
}
