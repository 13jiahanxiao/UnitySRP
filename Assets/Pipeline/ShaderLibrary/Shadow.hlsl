#ifndef MYRP_SHADOW_INCLUDED
#define MYRP_SHADOW_INCLUDED
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

CBUFFER_START(UnityPerFrame) 
	float4x4 unity_MatrixVP;
CBUFFER_END

CBUFFER_START(UnityPerDraw) 
	float4x4 unity_ObjectToWorld;
CBUFFER_END

CBUFFER_START(_ShadowBuffer)
float _ShadowBias;
CBUFFER_END

#define UNITY_MATRIX_M unity_ObjectToWorld
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"


UNITY_INSTANCING_BUFFER_START(PerInstance)
UNITY_DEFINE_INSTANCED_PROP(float4,_Color)
UNITY_INSTANCING_BUFFER_END(PerInstance)

struct VertexInput {
	float4 pos :POSITION;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VertexOutput {
	float4 clipPos:SV_POSITION;
};

VertexOutput ShadowPassVertex(VertexInput input) {
	VertexOutput output;
	
	UNITY_SETUP_INSTANCE_ID(input);
	float4 worldPos = mul(UNITY_MATRIX_M, float4(input.pos.xyz,1.0));
	output.clipPos = mul(unity_MatrixVP,worldPos);
#if UNITY_REVERSED_Z
	output.clipPos.z -= _ShadowBias;
	output.clipPos.z = min(output.clipPos.z, output.clipPos.w * UNITY_NEAR_CLIP_VALUE);
#else
	output.clips.z += _ShadowBias;
	output.clipPos.z = max(output.clipPos.z, output.clipPos.w * UNITY_NEAR_CLIP_VALUE);
#endif
	return output;
}

float4 ShadowPassFragment(VertexOutput input) :SV_TARGET
{
	return 0;
}



#endif //MYRP_Shadow_INCLUDED