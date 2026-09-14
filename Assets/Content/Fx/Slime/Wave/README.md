# Ripple System — Setup

## 1. Mesh preprocessing (once per wall/blob mesh)
`Tools > Ripple System > Bake Mesh For Ripples` -> select the mesh -> Bake.
Produces `<mesh>_RippleBaked.asset` (with UV3) and `<mesh>_RippleAdjacency.asset`.

Assign the baked mesh to the object's MeshFilter — the baked UV3 channel is
required, the original imported mesh asset is left untouched.

## 2. Component setup
Add `RippleSurfaceController` to the wall/blob object:
- `Adjacency` -> the matching `_RippleAdjacency.asset`
- `Target Material` -> the wall's material instance
- `Wave Compute` -> `RippleWave.compute`
- `Occluder Layer Mask` -> layer(s) of the character and any passing objects

The component adds a trigger `BoxCollider` sized to the mesh bounds and a
`RippleWallTriggerZone` to track objects entering/leaving that volume.

Also add `RippleGlobalFallback.cs` and `RippleGlobalFallbackEditor.cs`
anywhere in the project — no setup required, they register themselves.

## 3. Shader Graph wiring
1. Custom Function Node, Source: File -> `RippleSample.hlsl`, function `SampleRippleHeight`.
2. **Node stage must be Vertex**, not Fragment.
3. Input `WeldedIdUV` = `UV(3)` (the mesh's UV3 channel).
4. Carry the `Height` output to Fragment via a Vertex->Fragment interpolator
   (Custom Interpolator or a spare Vertex Color channel), then use it as
   usual on the Fragment stage (emission, blending into the refraction from
   `SHD_SG_Depth Refraction`, Fresnel modulation, etc. — same as the slime
   cube).

## 4. Tuning (defaults are reasonable starting points)
- `waveSpeed2` — do not exceed ~0.49 or the simulation diverges (stability condition).
- `damping` — closer to 1 means longer-lived waves and more visible interference/edge echo.
- `pulsePeriod` / `pulseStrength` — frequency and strength of the "drips" while an object sits in the silhouette.
- `maskSmoothRate` — higher gives a sharper silhouette edge, lower gives a softer entry.

## Known limitation
`IsPointInsideNonConvexOccluder` in `RippleSurfaceController` is a stub
(`return false`). Non-convex `MeshCollider`s (a sofa, a shelf) produce no
ripple yet. That needs mesh voxelization/SDF — the next step if such objects
are needed sooner than marking their collider convex or wrapping them in a
simplified convex collider.
