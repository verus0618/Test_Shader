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

        // Ring-shaped radial falloff: 0 right at the point (normDist = 0),
        // rises smoothly to 1 around the middle of the radius, and fades
        // back to 0 at the edge (MaxDistance) — a pure spatial shape, with
        // no dependency on time at all. Built from two smoothsteps around a
        // peak, so it is always continuous — unlike pow()-based approaches,
        // it never "snaps" to full brightness near the center for small
        // Smooth values.
        float normDist = saturate(dist / MaxDistance);
        float peakPos = 0.5; // ring peaks at the middle of the radius
        float halfWidth = lerp(0.04, 0.5, saturate(Smooth)); // 0 = thin/sharp ring, 1 = wide/soft ring
        float rise = smoothstep(peakPos - halfWidth, peakPos, normDist);
        float fall = smoothstep(peakPos, peakPos + halfWidth, normDist);
        float distFade = saturate(rise - fall);

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
