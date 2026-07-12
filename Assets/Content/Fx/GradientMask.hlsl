// GradientMask.hlsl
// Custom Function for Unity Shader Graph.
// Rebuilt to match the "Left / Right / Up / Down" graph layout.
//
// -----------------------------------------------------------------------------
// GRAPH LOGIC (per branch, identical chain x4):
//   Add( UV_component (or 1 - UV_component), Offset.channel )
//   -> Maximum( x, 0 )
//   -> Power( x, Power.channel )
//   -> Clamp( x, 0, 1 )
//
//   Left  -> uses (1 - UV.x)   [bright at the left edge]
//   Right -> uses UV.x         [bright at the right edge]
//   Up    -> uses UV.y         [bright at the top edge]
//   Down  -> uses (1 - UV.y)   [bright at the bottom edge]
//
//   Horizontal = Left * Right * 4.0 * Multiply
//   Vertical   = Up   * Down  * 4.0 * Multiply
//   General    = Horizontal * Vertical
//
//   NOTE: the "4.0" is a fixed constant taken directly from the graph
//   (the "X 4" value on the Multiply nodes) - it is NOT the exposed
//   Multiply input, it is baked into the function.
//
// -----------------------------------------------------------------------------
// INPUTS
// -----------------------------------------------------------------------------
// UV       (Vector2) - UV coordinates used to build the gradients.
// Offset   (Vector4) - per-direction offset added before the power curve.
//                       .r -> Left   .g -> Right   .b -> Up   .a -> Down
// Power    (Vector4) - per-direction exponent for the power curve.
//                       Same channel mapping as Offset (.r/.g/.b/.a).
// Multiply (Float)   - user-exposed strength multiplier, applied on top of
//                       the fixed x4 gain, AFTER the per-branch clamp
//                       (matches the "Multiply(1)" property node in the graph).
//
// -----------------------------------------------------------------------------
// OUTPUTS
// -----------------------------------------------------------------------------
// Horizontal (Float) - Left * Right band, boosted by 4 * Multiply.
// Vertical   (Float) - Up * Down band, boosted by 4 * Multiply.
// General    (Float) - Horizontal * Vertical (combined gradient).
// -----------------------------------------------------------------------------
// If your Offset/Power channel order (R/G/B/A -> Left/Right/Up/Down) is
// different in your graph, just swap the .r/.g/.b/.a below to match.

void GradientMask_float(
    float2 UV,
    float4 Offset,
    float4 Power,
    float Multiply,
    out float Horizontal,
    out float Vertical,
    out float General)
{
    // --- Left branch: bright at x = 0 ---
    float left = max(0.0, (1.0 - UV.x) + Offset.r);
    left = pow(left, Power.r);
    left = saturate(left);

    // --- Right branch: bright at x = 1 ---
    float right = max(0.0, UV.x + Offset.g);
    right = pow(right, Power.g);
    right = saturate(right);

    // --- Up branch: bright at y = 1 ---
    float up = max(0.0, UV.y + Offset.b);
    up = pow(up, Power.b);
    up = saturate(up);

    // --- Down branch: bright at y = 0 ---
    float down = max(0.0, (1.0 - UV.y) + Offset.a);
    down = pow(down, Power.a);
    down = saturate(down);

    // --- Combine into bands ---
    Horizontal = left * right * 4.0 * Multiply;
    Vertical = up * down * 4.0 * Multiply;

    // --- Combine into general gradient ---
    General = Horizontal * Vertical;
}
