#ifndef SCENE_DEPTH_UNDER_SURFACE_INCLUDED
#define SCENE_DEPTH_UNDER_SURFACE_INCLUDED

// How far the scene behind a surface sits below it, in world units along Y. Negative when the scene
// is beneath the surface, which is the convention the water's Depth_MinMax range is tuned for.
//
// This reproduces what Transform(Screen -> World).y - Object.Position.y used to compute, without
// reconstructing a world position. World reconstruction needs UNITY_MATRIX_I_VP, which is built for
// the render target being drawn into -- under Tile-Only Mode that is the backbuffer, whose UV origin
// can disagree with _CameraDepthTexture (the mismatch URP documents in
// RenderingUtils.ComputeInverseViewProjectionMatrix). Comparing depths in view space and projecting
// onto Y sidesteps that entirely.
//
// RawDepth must come from a Scene Depth node set to Raw. Do not sample the depth texture here:
// CustomFunctionNode does not implement IMayRequireDepthTexture, so a graph without a Scene Depth
// node never declares that it needs _CameraDepthTexture and the sample silently returns a constant.

void SceneDepthUnderSurface_float(float RawDepth, float3 ViewPosition, out float DepthUnderSurface)
{
	// Invert the projection directly rather than going through _ProjectionParams. For an orthographic
	// camera clip.z = view.z * P[2][2] + P[2][3] with w = 1, and device depth is that clip.z, so the
	// view-space Z comes straight back out. Using the matrix keeps this correct for a reversed-Z
	// buffer and for a negative near plane, neither of which _ProjectionParams reports reliably.
	float projectionZScale = UNITY_MATRIX_P._m22;
	projectionZScale = abs(projectionZScale) < 1e-6 ? 1e-6 : projectionZScale;
	float sceneEyeOrthographic = -(RawDepth - UNITY_MATRIX_P._m23) / projectionZScale;
	float sceneEyePerspective = LinearEyeDepth(RawDepth, _ZBufferParams);

	// unity_OrthoParams.w is 1 for orthographic cameras and 0 for perspective ones.
	float sceneEyeDepth = lerp(sceneEyePerspective, sceneEyeOrthographic, unity_OrthoParams.w);

	// Unity's view space looks down -Z, so eye depth is the negated view-space Z.
	float surfaceEyeDepth = -ViewPosition.z;
	float distanceAlongViewRay = sceneEyeDepth - surfaceEyeDepth;

	// Row 2 of the view matrix is the camera's negated forward, so its Y says how much world height
	// is lost per unit travelled along the ray. Downward-looking cameras make this negative.
	float forwardY = -UNITY_MATRIX_V._m21;

	DepthUnderSurface = distanceAlongViewRay * forwardY;
}

void SceneDepthUnderSurface_half(half RawDepth, half3 ViewPosition, out half DepthUnderSurface)
{
	float depthUnderSurface;
	SceneDepthUnderSurface_float((float)RawDepth, (float3)ViewPosition, depthUnderSurface);
	DepthUnderSurface = (half)depthUnderSurface;
}

#endif
