# Screen-space outline (Unity 6 / URP 17)

`ScreenSpaceOutlineFeature` detects edges from URP's camera depth and world-space normal textures. It is already registered in both renderer assets under `Assets/Settings`.

## Controls

- **Outline Color**: color and opacity of the line.
- **Object Layer Mask**: only opaque GameObjects on the selected layers receive outlines.
- **Thickness**: width in screen pixels.
- **Softness**: anti-aliased transition around the edge threshold.
- **Depth Threshold**: sensitivity to silhouette and depth discontinuities.
- **Normal Threshold**: sensitivity to creases and changes in surface direction.
- **Steep Angle** controls: suppress false depth edges on surfaces viewed at grazing angles.

The effect applies to opaque objects that participate in the URP depth-normal prepass. Transparent objects are composited before the outline pass but do not contribute normals by default.

The layer filter adds one inexpensive R8 mask and redraws the selected opaque geometry with a minimal unlit override shader. Prefer one feature with a combined layer mask whenever the selected layers share the same outline style.

In Scene View, Unity's own selection outline/wire can look like a second outline on an excluded layer. It is hidden automatically for this project. Use **Tools > TestMisha > Screen Space Outline > Show/Hide Unity Selection Overlay** to toggle it; this editor-only option does not affect Game View or builds.

Inspector labels include an estimated **Affects Performance: 1-10** value. These values describe how much changing that individual setting affects cost; the fixed cost of enabling the complete feature is higher on mobile and WebGL because it requires a depth-normal prepass, one layer-mask draw and one full-screen composite.
