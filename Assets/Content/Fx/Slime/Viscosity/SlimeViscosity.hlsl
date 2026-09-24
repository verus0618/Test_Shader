#ifndef SLIME_VISCOSITY_INCLUDED
#define SLIME_VISCOSITY_INCLUDED

// Must match SlimeViscosityDriver.MaxSlots.
#define SV_MAX_SLOTS 16

// Set per renderer by SlimeViscosityDriver through a MaterialPropertyBlock.
// With no driver these stay zero and the function returns no offset.
// Array names carry their size: Unity locks an array's length for the editor session on its first upload,
// so after changing SV_MAX_SLOTS the names must change too (here and in the driver).
float4 _SV_Anchor16[SV_MAX_SLOTS]; // xyz: contact point on the slime surface (WS), w: 1 = slot on, 0 = off
float4 _SV_AxisU16[SV_MAX_SLOTS];  // xyz: patch axis U in the face plane / its half-length (Contact Size applied)
float4 _SV_AxisV16[SV_MAX_SLOTS];  // xyz: patch axis V in the face plane / its half-length
float4 _SV_AxisN16[SV_MAX_SLOTS];  // xyz: face normal / depth half-length
float4 _SV_Offset16[SV_MAX_SLOTS]; // xyz: viscous drag vector (WS)
float4 _SV_Params;                 // x: drag length at which Mask = 1, y: enabled (0/1), z: active slot count (packed first),
                                   // w: where the fade starts, in patch units (1 - Softness)

// Shader Graph Custom Function (File mode), name "SlimeViscosity". Use Float precision on the node.
// Inputs:  PositionWS - Position node, World space; NormalWS - Normal Vector node, World space.
// Outputs: OffsetOS   - add to the object-space vertex position before Vertex Position.
//          Mask       - 0..1 debug: how strongly this vertex is influenced, following the contact footprint.
void SlimeViscosity_float(float3 PositionWS, float3 NormalWS, out float3 OffsetOS, out float Mask)
{
    float3 offsetWS = 0;
    float mask = 0;
    float fullDrag = max(_SV_Params.x, 1e-4);
    // Only the active contacts, packed to the front by the driver: idle slots cost nothing.
    int count = min((int)_SV_Params.z, SV_MAX_SLOTS);
    // Patch profile in patch units (1 = the patch edge): fully dragged up to fadeStart, easing to exactly
    // zero at the edge. Lower fadeStart (higher Softness) gives a softer mound.
    float fadeStart = min(_SV_Params.w, 0.98);

    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 drag = _SV_Offset16[i].xyz;

        // Position in patch units: an ellipse matching the intruder's shape on the face, scaled by Contact Size.
        float3 toVertex = PositionWS - _SV_Anchor16[i].xyz;
        float3 local = float3(dot(toVertex, _SV_AxisU16[i].xyz),
                              dot(toVertex, _SV_AxisV16[i].xyz),
                              dot(toVertex, _SV_AxisN16[i].xyz));
        float falloff = (1.0 - smoothstep(fadeStart, 1.0, length(local))) * _SV_Anchor16[i].w;

        offsetWS += drag * falloff;
        mask = max(mask, falloff * saturate(length(drag) / fullDrag));
    }

    offsetWS *= _SV_Params.y;
    OffsetOS = mul((float3x3)GetWorldToObjectMatrix(), offsetWS);
    Mask = mask * _SV_Params.y;
}

void SlimeViscosity_half(half3 PositionWS, half3 NormalWS, out half3 OffsetOS, out half Mask)
{
    float3 offset;
    float mask;
    SlimeViscosity_float(PositionWS, NormalWS, offset, mask);
    OffsetOS = offset;
    Mask = mask;
}

#endif
