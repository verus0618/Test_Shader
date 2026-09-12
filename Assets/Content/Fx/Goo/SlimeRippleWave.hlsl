#ifndef SLIME_RIPPLE_WAVE_INCLUDED
#define SLIME_RIPPLE_WAVE_INCLUDED

#define MAX_SLIME_IMPULSES 1024

float4 _ImpulsePositions[MAX_SLIME_IMPULSES]; // xyz = local-space position of a silhouette sample point
float4 _ImpulseData[MAX_SLIME_IMPULSES];      // x = startTime, y = exitStartTime (-1 = not exiting), z = weight, w = intensity (manual override, usually 1)
int _ImpulseCount;

// NOTE: parameter order here matches the Custom Function Node's Inputs list
// order exactly (Position, CurrentTime, Frequency, Speed, DistanceFalloff,
// MaxDistance, Smooth, Fade_In_Time, Fade_Out_Time) — Shader Graph binds by
// position, not by name, so this order must always match the node's Inputs
// list. If you ever add/reorder inputs on the node, update this signature
// to match, in the same order, before anything else.
//
// - Smooth: shapes the ring itself (0 at center, peak at mid-radius, 0 at
//   the edge). Higher Smooth = thinner/sharper ring, lower = wider/softer.
//   This is a static shape control, independent of time.
// - Fade_In_Time: how many seconds it takes a newly appeared point to ramp
//   from 0 to full strength.
// - Fade_Out_Time: how many seconds it takes a point to ramp from full
//   strength down to 0 after the object starts exiting the cube.
void CalculateSlimeRipple_float(
    float3 Position,
    float CurrentTime,
    float Frequency,
    float Speed,
    float DistanceFalloff,
    float MaxDistance,
    float Smooth,
    float FadeInTime,
    float FadeOutTime,
    out float Ripple)
{
    float total = 0.0;

    for (int i = 0; i < MAX_SLIME_IMPULSES; i++)
    {
        if (i >= _ImpulseCount) break;

        float3 impulsePos = _ImpulsePositions[i].xyz;
        float startTime = _ImpulseData[i].x;
        float exitStartTime = _ImpulseData[i].y; // -1 = point is not exiting yet
        float weight = _ImpulseData[i].z;
        float intensity = _ImpulseData[i].w;

        float dist = distance(Position, impulsePos);
        if (dist > MaxDistance) continue;

        float age = CurrentTime - startTime;

        float wave = sin(dist * Frequency - age * Speed);

        // Ring-shaped radial falloff: 0 at the center, 1 at the middle of the
        // radius, 0 again at MaxDistance (a ring, not a filled disc). Smooth
        // controls how sharp/narrow that ring is.
        float normDist = saturate(dist / MaxDistance);
        float ring = sin(normDist * 3.14159265); // 0 -> 1 -> 0 across the radius
        float distFade = pow(saturate(ring), max(Smooth, 0.0001));

        // DistanceFalloff still tapers overall intensity toward MaxDistance,
        // on top of the ring shape.
        distFade *= pow(saturate(1.0 - normDist), max(DistanceFalloff, 0.0) * 0.25);

        // Fade-in: point ramps 0 -> 1 over FadeInTime seconds after it appears.
        float fadeIn = (FadeInTime > 0.0001) ? saturate(age / FadeInTime) : 1.0;

        // Fade-out: once the object starts exiting (exitStartTime >= 0), the
        // point ramps 1 -> 0 over FadeOutTime seconds. Until then it's fully 1.
        float fadeOut = 1.0;
        if (exitStartTime >= 0.0)
        {
            float ageSinceExit = CurrentTime - exitStartTime;
            fadeOut = (FadeOutTime > 0.0001) ? saturate(1.0 - ageSinceExit / FadeOutTime) : 0.0;
        }

        total += wave * distFade * weight * intensity * fadeIn * fadeOut;
    }

    Ripple = total;
}

#endif
