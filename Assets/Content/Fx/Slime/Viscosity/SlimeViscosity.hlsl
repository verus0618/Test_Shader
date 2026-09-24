#ifndef SLIME_VISCOSITY_INCLUDED
#define SLIME_VISCOSITY_INCLUDED

// Must match SlimeViscosityDriver.MaxSlots.
#define SV_MAX_SLOTS 16

// Set per renderer by SlimeViscosityDriver through a MaterialPropertyBlock.
// With no driver these stay zero and the functions return no offset.
// Array names carry their size: Unity locks an array's length for the editor session on its first upload,
// so after changing SV_MAX_SLOTS the names must change too (here and in the driver).
float4 _SV_Anchor16[SV_MAX_SLOTS]; // xyz: contact point on the slime surface (WS), w: 1 = slot on, 0 = off
float4 _SV_AxisU16[SV_MAX_SLOTS];  // xyz: patch axis U in the face plane / its half-length (Contact Size applied)
float4 _SV_AxisV16[SV_MAX_SLOTS];  // xyz: patch axis V in the face plane / its half-length
float4 _SV_AxisN16[SV_MAX_SLOTS];  // xyz: face normal / depth half-length
float4 _SV_Offset16[SV_MAX_SLOTS]; // xyz: viscous drag vector (WS), w: mask strength 0..1 (drag length / full-mask drag)
float4 _SV_Params;                 // x: unused, y: enabled (0/1), z: active slot count (packed first),
                                   // w: where the fade starts, in patch units (1 - Softness)

// Drag of every active contact summed at a world position, and the strongest contact's mask there.
void SV_Accumulate(float3 positionWS, out float3 offsetWS, out float mask)
{
    offsetWS = 0;
    mask = 0;
    // Only the active contacts, packed to the front by the driver: idle slots cost nothing.
    int count = min((int)_SV_Params.z, SV_MAX_SLOTS);
    // Patch profile in patch units (1 = the patch edge): fully dragged up to fadeStart, easing to exactly
    // zero at the edge. Lower fadeStart (higher Softness) gives a softer mound.
    float fadeStart = min(_SV_Params.w, 0.98);

    [loop]
    for (int i = 0; i < count; i++)
    {
        // Position in patch units: an ellipse matching the intruder's shape on the face, scaled by Contact Size.
        float3 toVertex = positionWS - _SV_Anchor16[i].xyz;
        float3 local = float3(dot(toVertex, _SV_AxisU16[i].xyz),
                              dot(toVertex, _SV_AxisV16[i].xyz),
                              dot(toVertex, _SV_AxisN16[i].xyz));
        float distanceSq = dot(local, local);

        // The falloff is exactly zero outside the patch, so most of the surface skips the rest.
        [branch]
        if (distanceSq < 1.0)
        {
            float falloff = (1.0 - smoothstep(fadeStart, 1.0, sqrt(distanceSq))) * _SV_Anchor16[i].w;
            offsetWS += _SV_Offset16[i].xyz * falloff;
            mask = max(mask, falloff * _SV_Offset16[i].w);
        }
    }

    offsetWS *= _SV_Params.y;
    mask *= _SV_Params.y;
}

// Shader Graph Custom Function (File mode), name "SlimeViscosity". Use Float precision on the node.
// Inputs:  PositionWS - Position node, World space; NormalWS - Normal Vector node, World space.
// Outputs: OffsetOS   - add to the object-space vertex position before Vertex Position.
//          Mask       - 0..1: how strongly this point is dragged, following the contact footprint.
// For the fragment stage prefer SlimeViscosityMask plus a Custom Interpolator: calling this per pixel runs the
// whole contact loop for every pixel.
void SlimeViscosity_float(float3 PositionWS, float3 NormalWS, out float3 OffsetOS, out float Mask)
{
    float3 offsetWS;
    SV_Accumulate(PositionWS, offsetWS, Mask);
    OffsetOS = mul((float3x3)GetWorldToObjectMatrix(), offsetWS);
}

void SlimeViscosity_half(half3 PositionWS, half3 NormalWS, out half3 OffsetOS, out half Mask)
{
    float3 offset;
    float mask;
    SlimeViscosity_float(PositionWS, NormalWS, offset, mask);
    OffsetOS = offset;
    Mask = mask;
}

// Shader Graph Custom Function (File mode), name "SlimeViscosityMask", vertex stage.
// Input:  PositionOS - the final object-space vertex position, the value going into Vertex Position.
// Output: Mask       - the same value a per-pixel SlimeViscosity Mask gives on the displaced surface.
// Send it to the fragment stage through a Custom Interpolator, so the contact loop runs once per vertex
// instead of once per pixel.
void SlimeViscosityMask_float(float3 PositionOS, out float Mask)
{
    float3 offsetWS;
    SV_Accumulate(TransformObjectToWorld(PositionOS), offsetWS, Mask);
}

void SlimeViscosityMask_half(half3 PositionOS, out half Mask)
{
    float mask;
    SlimeViscosityMask_float(PositionOS, mask);
    Mask = mask;
}

#endif
