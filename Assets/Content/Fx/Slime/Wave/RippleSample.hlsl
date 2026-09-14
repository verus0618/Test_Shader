// RippleSample.hlsl
// Plug in as "Source: File" in a Custom Function Node (Shader Graph).
// IMPORTANT: this node must sit on the Vertex stage of the graph, not
// Fragment. A welded ID cannot be interpolated like a normal attribute — it
// is an index, not a value — so the buffer read has to happen before
// triangle interpolation. The resulting Height is a plain float and
// interpolates safely afterwards; carry it to the Fragment stage through a
// Vertex->Fragment interpolator (e.g. a Custom Interpolator or a spare
// Vertex Color channel).

StructuredBuffer<float> _RippleHBuffer;
int _RippleHBufferLength;

void SampleRippleHeight_float(float2 WeldedIdUV, out float Height)
{
    uint id = (uint)round(WeldedIdUV.x);
    id = min(id, (uint)max(_RippleHBufferLength - 1, 0)); // clamp so a smaller fallback buffer (e.g. the 1-element dummy) never reads out of bounds

    Height = _RippleHBuffer[id];
}
