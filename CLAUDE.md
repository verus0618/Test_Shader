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

### Screen-space outline (`Assets/Rendering`, namespace `VFX.Rendering`)
`ScreenSpaceOutlineFeature` is a URP `ScriptableRendererFeature` that uses the **RenderGraph API** (`RecordRenderGraph`). It is already registered on both renderer assets in `Assets/Settings`. It adds two raster passes:
1. It draws opaque objects on the selected layers into an R8 mask using `Hidden/VFX/OutlineObjectMask`.
2. It runs a full-screen composite with `Hidden/VFX/ScreenSpaceOutline`, which finds edges from URP depth and normals (`ConfigureInput(Depth | Normal)`).

Both shaders are loaded with `Shader.Find`, so they must stay in the build (Always Included, or referenced some other way). `Editor/OutlineSceneViewTools.cs` adds the menu **Tools > VFX > Screen Space Outline**, which toggles Unity's Scene View selection overlay. Inspector labels include an "Affects Performance: 1-10" estimate. Keep that convention when you add settings. See `Assets/Rendering/README.md`.

### Wall-pass mask (`Assets/Content/Fx/Slime/Collisuion`, note the folder-name typo)
This is a world-space capsule-SDF mask for objects passing through walls. `FX_Slime.shadergraph` uses it through a Custom Function node.
- `WallPassEmitter` goes on the moving object. Each physics step it sweeps probe capsules from the previous position to the current one.
- `WallPassRuntime` is an `[ExecuteAlways]` singleton that creates itself if missing and runs at execution order 1000. It owns the event ring buffer, evicting the event that expires soonest when full. Once per frame in `LateUpdate` it uploads the buffer plus the `WallPassSettings` ScriptableObject parameters as global uniforms (`_WP_*`). It keeps its own clock, rebased after 3600 s, rather than using `_Time`.
- `WallPassReceiver` goes on the wall renderer. **World** mode reads the global buffer, which keeps the SRP Batcher intact. **Anchored** mode keeps a private buffer in anchor-local space and uploads it through a `MaterialPropertyBlock`. The receiver also pads renderer bounds to cover vertex displacement, by default only in Play Mode because the bounds are serialized data.
- `WallPassMask.hlsl` provides `WallPassMask_float/_half(PositionWS, NormalWS, out Mask)`.
- **Invariant:** `WallPassRuntime.MaxEvents` must equal `WP_MAX_EVENTS` in the HLSL (currently 64). Always upload the full fixed-length arrays, because Unity locks a shader array's length on first upload. Pass the live count separately in `_WP_Count`.

### Play Mode fly camera (`Assets/Tools/FlyCamera`, namespace `VFX.Tools`, own asmdef)
`FlyCamera` gives Scene View controls in Play Mode: RMB look with WASD/QE and Shift, RMB + wheel for speed, wheel dolly, MMB pan, Alt+LMB orbit and Alt+RMB zoom. In the Editor, `FlyCameraAutoAttach` adds it to `Camera.main` when Play Mode starts, so scenes don't need editing. A `FlyCamera` placed by hand takes priority. **Tools > VFX > Fly Camera** toggles auto-attach and "Start From Scene View Camera" (both stored in EditorPrefs). Active Input Handling is set to the new Input System only, so `com.unity.inputsystem` must stay installed, because the legacy `UnityEngine.Input` throws in that mode. The asmdef detects the package through `versionDefines` (`VFX_INPUT_SYSTEM`) and falls back to legacy input when it's missing.

### Ribbon tools (`Assets/Tools/Ribbon`, namespace `VFX.Tools`, asmdefs `VFX.Ribbon` / `VFX.Ribbon.Editor`)
- `RibbonEmitter` is a Niagara-style trail: it spawns points over time and draws them with `Graphics.RenderMesh` from `beginCameraRendering`.
- `SplineRibbon` is a static ribbon along a cubic Bezier spline. Knots (`Auto`/`Mirrored`/`Aligned`/`Free` tangents, per-knot width and roll) are stored in object space. Facing is `Local` (Facing Vector, like LineRenderer Transform Z), `Camera`, or `MinimumTwist` (parallel transport). An `AnimationCurve` profile plus `profileDepth` (relative to width) bends the cross-section. It renders through its own `MeshFilter`/`MeshRenderer`, so picking, the selection outline and the material slot work as usual. The mesh is `HideAndDontSave` and rebuilt in `beginCameraRendering`: once after a change, or per camera in Camera mode. Code that changes knots must call `RequestRebuild()`. `SplineRibbonEditor` provides the Scene View knot and tangent handles and **GameObject > Effects > Spline Ribbon**.
- The namespace `VFX.Tools` hides `UnityEditor.Tools`, so editor code must write `UnityEditor.Tools.hidden` etc.

### Shader Graph helpers
- `Assets/Editor/ShaderGraphAutoHide` (namespace `VFX.Shaders.Editor`) hides material properties automatically, based on graph connections. `ShaderGraphUsageAnalyzer` parses the `.shadergraph` JSON and walks back from the master stack. It records which Enum/Boolean keyword entries, and which Branch nodes with a Boolean property predicate, each property and keyword passes through. A Custom Interpolator vertex block inherits the reach of the `CustomInterpolatorNode`s that read it. Conditions are kept in DNF, capped at 64 terms (above that the property always shows), and terms that cover every state of a switch are merged. `AutoHideShaderGraphGUI` is a `ShaderGUI` that wraps URP's internal Shader Graph inspector, created by reflection from the sub target, and passes it only the used properties. Enable it per graph through Graph Settings > Custom Editor GUI, or with **Tools > VFX > Auto Hide Material Properties**. It replaced the old `ShowIf`/`HideIf` Custom Attribute drawers (`MaterialDrawers.cs`, now deleted).
- Shared graph logic lives in subgraphs under `Assets/Content/Fx/Shaders/ShaderSubGraphs`. They follow the pattern `SHD_SG_<Feature>_<Part>`, for example the Main shader's dissolve/NL/RL/SMEO pieces and the Flame and Scroll adjustments. Top-level graphs are named `SHD_*` and materials `MT_*`.
