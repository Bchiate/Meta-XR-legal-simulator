// HIS depth occlusion: Meta's META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY with a bounds check. Include AFTER Meta's
// EnvironmentOcclusionURP.hlsl; use HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, color, bias) where Meta's macro was.
//
// Meta's CalculateEnvironmentDepthOcclusion samples the environment depth at whatever uv the reprojection gives, with no range
// check. A fragment the depth camera doesn't see (the edge of the eye's view past the depth frame, or behind the depth camera:
// w <= 0) then reads clamped or wrapped depth from somewhere else in the frame, and a big virtual surface close to the eye in
// the corner of the view (the open door leaf) gets see-through patches that move with the head (Brett 0.1.40/0.1.41: "translucent
// objects in the corner of my vision" near the open door). Out there no real depth is known: draw the fragment unoccluded.
#ifndef HIS_DEPTH_OCCLUSION_INCLUDED
#define HIS_DEPTH_OCCLUSION_INCLUDED

#if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)

float HIS_DepthOcclusion(float3 posWorld, float bias)
{
    const float4 depthSpace = mul(_EnvironmentDepthReprojectionMatrices[unity_StereoEyeIndex], float4(posWorld, 1.0));
    if (depthSpace.w <= 0.0) return 1.0;
    const float2 uv = (depthSpace.xy / depthSpace.w + 1.0) * 0.5;
    if (any(uv < 0.0) || any(uv > 1.0)) return 1.0;
    return CalculateEnvironmentDepthOcclusion(posWorld, bias);
}

#define HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, output, zBias) output *= HIS_DepthOcclusion(input.posWorld, zBias);

#else

#define HIS_DEPTH_OCCLUDE_PREMULTIPLY(input, output, zBias)

#endif

#endif
