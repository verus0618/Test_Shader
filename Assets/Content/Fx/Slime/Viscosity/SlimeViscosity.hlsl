#ifndef SLIME_VISCOSITY_INCLUDED
#define SLIME_VISCOSITY_INCLUDED

// Must match SlimeViscosityDriver.MaxSlots.
#define SV_MAX_SLOTS 4

// Footprint profile in footprint units (1 = the intruder's edge): fully dragged inside the core,
// easing to exactly zero a little past the intruder's edge so the surface bends around it.
#define SV_CORE 0.35
#define SV_EDGE 1.35

// Set per renderer by SlimeViscosityDriver through a MaterialPropertyBlock.
// With no driver these stay zero and the function returns no offset.
float4 _SV_Anchor[SV_MAX_SLOTS]; // xyz: contact point on the slime surface (WS), w: 1 = slot on, 0 = off
float4 _SV_AxisU[SV_MAX_SLOTS];  // xyz: footprint axis U in the face plane / its half-length
float4 _SV_AxisV[SV_MAX_SLOTS];  // xyz: footprint axis V in the face plane / its half-length
float4 _SV_AxisN[SV_MAX_SLOTS];  // xyz: face normal / depth half-length
float4 _SV_Offset[SV_MAX_SLOTS]; // xyz: viscous drag vector (WS)
float4 _SV_Params;               // x: drag length at which Mask = 1, y: enabled (0/1)

// Shader Graph Custom Function (File mode), name "SlimeViscosity". Use Float precision on the node.
// Inputs:  PositionWS - Position node, World space; NormalWS - Normal Vector node, World space.
// Outputs: OffsetOS   - add to the object-space vertex position before Vertex Position.
//          Mask       - 0..1 debug: how strongly this vertex is influenced, following the contact footprint.
void SlimeViscosity_float(float3 PositionWS, float3 NormalWS, out float3 OffsetOS, out float Mask)
{
    float3 offsetWS = 0;
    float mask = 0;
    float fullDrag = max(_SV_Params.x, 1e-4);

    [unroll]
    for (int i = 0; i < SV_MAX_SLOTS; i++)
    {
        float3 drag = _SV_Offset[i].xyz;

        // Position in footprint units: an ellipse matching the intruder's shape on the face.
        float3 toVertex = PositionWS - _SV_Anchor[i].xyz;
        float3 local = float3(dot(toVertex, _SV_AxisU[i].xyz),
                              dot(toVertex, _SV_AxisV[i].xyz),
                              dot(toVertex, _SV_AxisN[i].xyz));
        float falloff = (1.0 - smoothstep(SV_CORE, SV_EDGE, length(local))) * _SV_Anchor[i].w;

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
