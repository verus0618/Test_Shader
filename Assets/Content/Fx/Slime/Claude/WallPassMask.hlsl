#ifndef WALL_PASS_MASK_INCLUDED
#define WALL_PASS_MASK_INCLUDED

// =============================================================
//  WallPassMask.hlsl
//
//  Gradient mask that follows the silhouette of an object passing
//  through a wall. Evaluated as a capsule SDF in world space, so it
//  is independent of UVs, seams and wall topology.
//
//  Works for MeshRenderer and SkinnedMeshRenderer alike: skinning is
//  resolved before the vertex shader, so the incoming position is
//  already deformed.
//
//  Single Custom Function node, single output: a 0..1 grayscale mask.
// =============================================================

#define WP_MAX_EVENTS 64   // must match WallPassRuntime.MaxEvents

// --- Event buffer ---------------------------------------------------
// Fed globally via Shader.SetGlobal* for static walls, or overridden
// per renderer through a MaterialPropertyBlock in Anchored mode.
//   A.xyz = swept capsule start, A.w = event birth time
//   B.xyz = swept capsule end,   B.w = capsule radius
float4 _WP_A[WP_MAX_EVENTS];
float4 _WP_B[WP_MAX_EVENTS];
float  _WP_Count;
float  _WP_Clock;

// World -> event storage space. Identity for static walls, the anchor's
// worldToLocal matrix for walls that move or are driven by a rig.
float4x4 _WP_WorldToSpace;

// --- Parameters supplied by WallPassSettings (ScriptableObject) ------
float _WP_Delay;        // seconds between the contact event and mask onset
float _WP_Attack;       // seconds to reach full strength
float _WP_Lifetime;     // total imprint lifetime in seconds
float _WP_Falloff;      // gradient length outward from the silhouette, in meters
float _WP_DepthWeight;  // 1 = true 3D distance, 0 = full projection along the surface normal
float _WP_Intensity;    // global multiplier applied before the final clamp

// An unset (all-zero) matrix is treated as identity so Shader Graph
// previews keep working without a runtime present.
float3 WP_ToSpace(float3 p)
{
    if (abs(_WP_WorldToSpace[3][3]) < 1e-6) return p;
    return mul(_WP_WorldToSpace, float4(p, 1.0)).xyz;
}

// Direction variant of the above: rotation only, no translation.
// Assumes uniform anchor scale.
float3 WP_DirToSpace(float3 v)
{
    if (abs(_WP_WorldToSpace[3][3]) < 1e-6) return v;
    return mul((float3x3)_WP_WorldToSpace, v);
}

// Compresses the component along 'axis' by 'weight'. Applied to every point
// before the distance test, it turns the isotropic SDF into an anisotropic one:
// at weight 1 distance is physical, at weight 0 the silhouette is projected
// along the axis with no depth falloff at all.
float3 WP_Squash(float3 v, float3 axis, float weight)
{
    return v - axis * (dot(v, axis) * (1.0 - weight));
}

// Point-to-segment distance: the core of the capsule SDF.
float WP_SegDist(float3 p, float3 a, float3 b)
{
    float3 ab = b - a;
    float3 ap = p - a;
    float  t  = saturate(dot(ap, ab) / max(dot(ab, ab), 1e-6));
    return length(ap - ab * t);
}

// Grayscale mask: 1 on the silhouette, fading to 0 over _WP_Falloff meters.
float WP_EvaluateMask(float3 worldPos, float3 worldNormal)
{
    float3 p = WP_ToSpace(worldPos);

    // Surface normal in event space, used as the projection axis.
    float3 axis = WP_DirToSpace(worldNormal);
    float  axisLen = length(axis);
    float  weight = saturate(_WP_DepthWeight);
    if (axisLen < 1e-5) weight = 1.0; else axis /= axisLen;

    p = WP_Squash(p, axis, weight);

    float acc = 0.0;

    int   count   = min((int)_WP_Count, WP_MAX_EVENTS);
    float invLife = 1.0 / max(_WP_Lifetime, 1e-4);
    float invFall = 1.0 / max(_WP_Falloff,  1e-4);

    [loop]
    for (int i = 0; i < count; i++)
    {
        float4 A = _WP_A[i];
        float4 B = _WP_B[i];

        // Event age, delay already subtracted.
        float age = _WP_Clock - A.w - _WP_Delay;
        if (age < 0.0) continue;

        float life = age * invLife;
        if (life >= 1.0) continue;

        // Envelope: smooth onset followed by quadratic decay.
        float attack = saturate(age / max(_WP_Attack, 1e-4));
        float decay  = 1.0 - life;
        float env    = attack * decay * decay;

        float3 a = WP_Squash(A.xyz, axis, weight);
        float3 b = WP_Squash(B.xyz, axis, weight);

        // Distance to the capsule: 0 inside the silhouette, growing outward.
        float d = max(WP_SegDist(p, a, b) - B.w, 0.0);

        acc += exp(-d * invFall) * env;
    }

    // Soft saturation instead of a hard clamp, so overlapping capsules
    // blend without visible steps.
    return 1.0 - exp(-acc);
}

// -------------------------------------------------------------
//  Custom Function node "WallPassMask"
//    In : PositionWS (Vector3), NormalWS (Vector3)
//    Out: Mask (Float), 0..1
// -------------------------------------------------------------
void WallPassMask_float(float3 PositionWS, float3 NormalWS, out float Mask)
{
    Mask = saturate(WP_EvaluateMask(PositionWS, NormalWS) * _WP_Intensity);
}

void WallPassMask_half(half3 PositionWS, half3 NormalWS, out half Mask)
{
    float m;
    WallPassMask_float((float3)PositionWS, (float3)NormalWS, m);
    Mask = (half)m;
}

#endif // WALL_PASS_MASK_INCLUDED
