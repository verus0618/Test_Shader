# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity **6000.5.5f1** sandbox for VFX and shader work on **URP 17.6** (Shader Graph 17.6, VFX Graph 17.5, Cinemachine, Recorder). Most of the content is authored assets (`.shadergraph`, `.shadersubgraph`, `.mat`, `.prefab`, `.vfx`) rather than code. The main scene is `Assets/Scenes/SampleScene.unity`. Render pipeline assets live in `Assets/Settings` (`PC_*` and `Mobile_*` RP asset/renderer pairs).

There is no build script, test assembly, or linter. You check work by opening the project in the Unity Editor: scripts compile on domain reload, and shaders compile when the editor imports them. The editor generates `.csproj`/`.sln` files, and they are gitignored. `Test_Shader.slnx` is the tracked solution.

## Repo gotchas

- `Packages/manifest.json` and `packages-lock.json` are tracked. Don't re-add the generic NuGet `**/[Pp]ackages/*` ignore rule, because it would hide them again.
- `*.dll` is also ignored globally, so native or managed plugin DLLs won't be committed.
- Every asset needs its `.meta` file committed next to it. When you move or rename an asset, move its `.meta` too, or GUID references break.
- `Assets/Samples`, `Assets/TextMesh Pro`, `Assets/Plugins/AssetUsageDetector` and `Assets/KCCExample/Shared/3rdParty` are imported third-party or sample content. Leave them alone unless a task specifically targets them.
- `Assets/Editor/RippleGlobalFallbackEditor.cs` belongs to a "RippleSystem" whose runtime side (`RippleGlobalFallback`, `RippleSurfaceController`, the shaders that read `_RippleHBuffer`) is **not in the repo**. `Assets/RippleSystem` only contains an empty `Baked` folder. The editor script binds a dummy global `ComputeBuffer` so shaders don't spam "buffer required" errors.

## Project-owned code

### Screen-space outline (`Assets/Rendering`, namespace `TestMisha.Rendering`)
`ScreenSpaceOutlineFeature` is a URP `ScriptableRendererFeature` that uses the **RenderGraph API** (`RecordRenderGraph`). It is already registered on both renderer assets in `Assets/Settings`. It adds two raster passes:
1. It draws opaque objects on the selected layers into an R8 mask using `Hidden/TestMisha/OutlineObjectMask`.
2. It runs a full-screen composite with `Hidden/TestMisha/ScreenSpaceOutline`, which finds edges from URP depth and normals (`ConfigureInput(Depth | Normal)`).

Both shaders are loaded with `Shader.Find`, so they must stay in the build (Always Included, or referenced some other way). `Editor/OutlineSceneViewTools.cs` adds the menu **Tools > TestMisha > Screen Space Outline**, which toggles Unity's Scene View selection overlay. Inspector labels include an "Affects Performance: 1-10" estimate. Keep that convention when you add settings. See `Assets/Rendering/README.md`.

### Wall-pass mask (`Assets/Content/Fx/Slime/Collisuion`, note the folder-name typo)
This is a world-space capsule-SDF mask for objects passing through walls. `FX_Slime.shadergraph` uses it through a Custom Function node.
- `WallPassEmitter` goes on the moving object. Each physics step it sweeps probe capsules from the previous position to the current one.
- `WallPassRuntime` is an `[ExecuteAlways]` singleton that creates itself if missing and runs at execution order 1000. It owns the event ring buffer, evicting the event that expires soonest when full. Once per frame in `LateUpdate` it uploads the buffer plus the `WallPassSettings` ScriptableObject parameters as global uniforms (`_WP_*`). It keeps its own clock, rebased after 3600 s, rather than using `_Time`.
- `WallPassReceiver` goes on the wall renderer. **World** mode reads the global buffer, which keeps the SRP Batcher intact. **Anchored** mode keeps a private buffer in anchor-local space and uploads it through a `MaterialPropertyBlock`. The receiver also pads renderer bounds to cover vertex displacement, by default only in Play Mode because the bounds are serialized data.
- `WallPassMask.hlsl` provides `WallPassMask_float/_half(PositionWS, NormalWS, out Mask)`.
- **Invariant:** `WallPassRuntime.MaxEvents` must equal `WP_MAX_EVENTS` in the HLSL (currently 64). Always upload the full fixed-length arrays, because Unity locks a shader array's length on first upload. Pass the live count separately in `_WP_Count`.

### Shader Graph helpers
- `Assets/Editor/MaterialDrawers.cs` defines `ShowIf`, `HideIf`, `ShowIfEnum` and `HideIfEnum` material property drawers. You apply them through Shader Graph **Custom Attributes**. The value is a property reference without its leading `_`. For enums the syntax is `ENUM__VALUE`, which resolves to the keyword `_ENUM_VALUE`, and conditions can be combined with ` AND ` or ` OR `.
- Shared graph logic lives in subgraphs under `Assets/Content/Fx/Shaders/ShaderSubGraphs`. They follow the pattern `SHD_SG_<Feature>_<Part>`, for example the Main shader's dissolve/NL/RL/SMEO pieces and the Flame and Scroll adjustments. Top-level graphs are named `SHD_*` and materials `MT_*`.
