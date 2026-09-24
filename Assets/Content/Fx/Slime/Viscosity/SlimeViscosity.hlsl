#ifndef SLIME_VISCOSITY_INCLUDED
#define SLIME_VISCOSITY_INCLUDED

// Must match SlimeViscosityDriver.MaxSlots.
#define SV_MAX_SLOTS 4
#define SV_TWO_PI 6.28318530718

// Set per renderer by SlimeViscosityDriver through a MaterialPropertyBlock.
// With no driver these stay zero and the function returns no offset.
float4 _SV_Anchor[SV_MAX_SLOTS]; // xyz: contact point on the slime surface (WS), w: deformation radius (0 = slot off)
float4 _SV_Offset[SV_MAX_SLOTS]; // xyz: viscous drag vector (WS)
float4 _SV_Ripple[SV_MAX_SLOTS]; // x: seconds since contact, y: mask envelope 0..1
float4 _SV_Params;               // x: rim bulge, y: enabled (0/1), z: ripple displacement, w: ripple mask strength
float4 _SV_RippleShape;          // x: wavelength, y: speed, z: reach

// Shader Graph Custom Function (File mode), name "SlimeViscosity". Use Float precision on the node.
// Inputs:  PositionWS - Position node, World space; NormalWS - Normal Vector node, World space.
// Outputs: OffsetOS   - add to the object-space vertex position before Vertex Position.
//          Mask       - 0..1, fades in on contact and out after release, with rings running outward.
void SlimeViscosity_float(float3 PositionWS, float3 NormalWS, out float3 OffsetOS, out float Mask)
{
    float3 normalWS = normalize(NormalWS);
    float3 offsetWS = 0;
    float mask = 0;

    float wavelength = max(_SV_RippleShape.x, 1e-3);
    float rippleSpeed = _SV_RippleShape.y;
    float rippleReach = max(_SV_RippleShape.z, 1e-3);

    [unroll]
    for (int i = 0; i < SV_MAX_SLOTS; i++)
    {
        float4 anchor = _SV_Anchor[i];
        float3 drag = _SV_Offset[i].xyz;
        float age = _SV_Ripple[i].x;
        float envelope = _SV_Ripple[i].y;
        float slotOn = anchor.w > 0 ? 1.0 : 0.0;

        float radius = max(anchor.w, 1e-4);
        float3 toVertex = PositionWS - anchor.xyz;
        float distance = length(toVertex);
        float d2 = (distance * distance) / (radius * radius);
        float falloff = exp(-2.0 * d2) * slotOn;

        // Ring around the contact: rises when the surface is pushed in, pinches into a neck when pulled out.
        float ring = 4.0 * d2 * falloff;
        float push = -dot(drag, normalWS);

        // Rings travel outward from the contact; nothing ahead of the wave front, dying out with distance.
        float front = saturate((rippleSpeed * age - distance) / wavelength + 1.0);
        float wave = sin(SV_TWO_PI * (distance - rippleSpeed * age) / wavelength);
        float rippleWeight = front * exp(-distance / rippleReach) * envelope * slotOn;

        offsetWS += drag * falloff
                  + normalWS * (push * _SV_Params.x * ring)
                  + normalWS * (wave * rippleWeight * _SV_Params.z);

        float rippleMask = (0.5 + 0.5 * wave) * rippleWeight * _SV_Params.w;
        mask = max(mask, saturate(falloff * envelope + rippleMask * (1.0 - falloff)));
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
