#ifndef SLIME_VISCOSITY_INCLUDED
#define SLIME_VISCOSITY_INCLUDED

// Must match SlimeViscosityDriver.MaxSlots.
#define SV_MAX_SLOTS 4

// Set per renderer by SlimeViscosityDriver through a MaterialPropertyBlock.
// With no driver these stay zero and the function returns no offset.
float4 _SV_Anchor[SV_MAX_SLOTS]; // xyz: contact point on the slime surface (WS), w: deformation radius (0 = slot off)
float4 _SV_Offset[SV_MAX_SLOTS]; // xyz: viscous drag vector (WS)
float4 _SV_Params;               // x: drag length at which Mask = 1, y: enabled (0/1)

// Shader Graph Custom Function (File mode), name "SlimeViscosity". Use Float precision on the node.
// Inputs:  PositionWS - Position node, World space; NormalWS - Normal Vector node, World space.
// Outputs: OffsetOS   - add to the object-space vertex position before Vertex Position.
//          Mask       - 0..1 debug: how strongly this vertex is influenced (smooth spot, no rings).
void SlimeViscosity_float(float3 PositionWS, float3 NormalWS, out float3 OffsetOS, out float Mask)
{
    float3 offsetWS = 0;
    float mask = 0;
    float fullDrag = max(_SV_Params.x, 1e-4);

    [unroll]
    for (int i = 0; i < SV_MAX_SLOTS; i++)
    {
        float4 anchor = _SV_Anchor[i];
        float3 drag = _SV_Offset[i].xyz;

        // Gaussian falloff around the contact point, cut to exactly zero beyond two radii
        // so nothing far away is touched. Zero for inactive slots.
        float radius = max(anchor.w, 1e-4);
        float3 toVertex = PositionWS - anchor.xyz;
        float d2 = dot(toVertex, toVertex) / (radius * radius);
        float window = saturate(1.0 - d2 * 0.25);
        float falloff = anchor.w > 0 ? exp(-2.0 * d2) * window * window : 0;

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
