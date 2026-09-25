using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

namespace TestMisha.Slime
{
    /// <summary>
    /// Runtime side of SlimeRefractionObject. For every Game and Scene View camera that sees a Refractive object it
    /// gathers the visible transparent renderers (Refractive objects, components marked Visible In Refraction and
    /// every other transparent renderer in the loaded scenes), sorts them back to front by distance, binds each
    /// Refractive object's screen copy through its MaterialPropertyBlock and records one pass right after URP's
    /// own opaque copy (BeforeRenderingTransparents):
    /// copy the screen and depth, then far to near: grab the copy for a Refractive object, draw the object into
    /// the copy. Objects in front of the nearest Refractive one are left out: no copy looks at them.
    /// The copies are persistent textures, one per camera and Refractive object, because the material property
    /// block must name a texture before the camera culls, long before the Render Graph allocates its own.
    /// The pass is enqueued from RenderPipelineManager.beginCameraRendering, so no renderer asset needs a feature.
    /// Transparent renderers are searched with FindObjectsByType once per camera render, and only while a
    /// Refractive object is on screen.
    /// </summary>
    static class SlimeRefractionSystem
    {
        static readonly int CameraOpaqueTextureId = Shader.PropertyToID("_CameraOpaqueTexture");

        /// <summary>What the chain needs to draw one renderer the way the transparent pass does.</summary>
        internal sealed class ChainRenderer
        {
            public readonly Renderer renderer;
            // Name for the Frame Debugger and the Render Graph Viewer, built once instead of every frame.
            public readonly string drawPassName;
            readonly List<Material> _materials = new List<Material>();
            readonly List<int> _passes = new List<int>();
            int _subMeshCount;

            // Drawing the mesh directly, see Prepare.
            MeshFilter _filter;
            Mesh _mesh;
            Matrix4x4 _matrix;
            MaterialPropertyBlock _block;
            bool _hasBlock;

            public ChainRenderer(Renderer renderer)
            {
                this.renderer = renderer;
                drawPassName = "Slime Refraction Draw: " + renderer.name;
            }

            /// <summary>Lowest render queue among the materials that are drawn.</summary>
            public int Queue { get; private set; }

            /// <summary>True if any drawn material uses a Lit forward pass, so the draw needs lighting globals.</summary>
            public bool HasLitPass { get; private set; }

            /// <summary>
            /// Refreshes the materials and their forward passes. transparentOnly skips materials in the opaque
            /// queues, which the opaque copy already holds. drawMesh draws a MeshRenderer's mesh with its matrix
            /// and property block instead of the renderer itself: the GPU Resident Drawer takes over mesh
            /// renderers without a property block, and CommandBuffer.DrawRenderer may not draw those.
            /// Returns false when nothing would be drawn.
            /// </summary>
            public bool Prepare(bool transparentOnly, bool drawMesh)
            {
                _materials.Clear();
                _passes.Clear();
                renderer.GetSharedMaterials(_materials);

                _mesh = null;
                // A statically batched renderer shares a combined mesh, only the renderer knows its part.
                if (drawMesh && renderer is MeshRenderer && !renderer.isPartOfStaticBatch)
                {
                    if (_filter == null)
                        renderer.TryGetComponent(out _filter);
                    _mesh = _filter != null ? _filter.sharedMesh : null;
                    _matrix = renderer.localToWorldMatrix;
                    _hasBlock = renderer.HasPropertyBlock();
                    if (_hasBlock)
                    {
                        _block ??= new MaterialPropertyBlock();
                        renderer.GetPropertyBlock(_block);
                    }
                }
                _subMeshCount = SubMeshCount();

                int queue = int.MaxValue;
                bool hasLit = false;
                for (int i = 0; i < _materials.Count; i++)
                {
                    Material material = _materials[i];
                    int pass = -1;
                    bool isLit = false;
                    if (material != null && material.shader != null && (!transparentOnly || material.renderQueue > (int)RenderQueue.GeometryLast))
                        GetPassInfo(material, out pass, out isLit);
                    _passes.Add(pass);
                    if (pass >= 0)
                    {
                        queue = Mathf.Min(queue, material.renderQueue);
                        hasLit |= isLit;
                    }
                }

                Queue = queue;
                HasLitPass = hasLit;
                return queue != int.MaxValue;
            }

            public void Draw(RasterCommandBuffer cmd)
            {
                for (int i = 0; i < _materials.Count; i++)
                {
                    if (_passes[i] < 0)
                        continue;

                    // Like MeshRenderer: extra materials draw the last submesh again.
                    int subMesh = Mathf.Clamp(i, 0, Mathf.Max(_subMeshCount - 1, 0));
                    if (_mesh != null)
                        cmd.DrawMesh(_mesh, _matrix, _materials[i], subMesh, _passes[i], _hasBlock ? _block : null);
                    else
                        cmd.DrawRenderer(renderer, _materials[i], subMesh, _passes[i]);
                }
            }

            int SubMeshCount()
            {
                Mesh mesh = _mesh;
                if (renderer is SkinnedMeshRenderer skinned)
                    mesh = skinned.sharedMesh;
                else if (mesh == null && renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter))
                    mesh = filter.sharedMesh;
                // Other renderers draw one submesh per material (a particle system's trails are submesh 1).
                return mesh != null ? mesh.subMeshCount : _materials.Count;
            }
        }

        struct ChainItem
        {
            public ChainRenderer chain;
            // Set for Refractive objects only, like the screen copy.
            public SlimeRefractionObject refractive;
            public RTHandle grab;
            public int divisor;
            public Bounds bounds;
            public float distance;
            public int queue;
        }

        sealed class CameraState
        {
            public readonly ChainPass pass = new ChainPass();
            public readonly Dictionary<SlimeRefractionObject, RTHandle> grabs = new Dictionary<SlimeRefractionObject, RTHandle>();

            public RTHandle EnsureGrab(SlimeRefractionObject obj, int width, int height, GraphicsFormat format)
            {
                if (grabs.TryGetValue(obj, out RTHandle grab) && grab != null && grab.rt != null &&
                    grab.rt.width == width && grab.rt.height == height && grab.rt.graphicsFormat == format)
                {
                    return grab;
                }

                grab?.Release();
                grab = RTHandles.Alloc(width, height, format, filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp, name: "_SlimeRefractionGrab_" + obj.name);
                grabs[obj] = grab;
                return grab;
            }

            public void ReleaseGrab(SlimeRefractionObject obj)
            {
                if (grabs.TryGetValue(obj, out RTHandle grab))
                {
                    grab?.Release();
                    grabs.Remove(obj);
                }
            }

            public void Release()
            {
                foreach (RTHandle grab in grabs.Values)
                    grab?.Release();
                grabs.Clear();
                pass.items.Clear();
            }
        }

        static readonly List<SlimeRefractionObject> s_objects = new List<SlimeRefractionObject>();
        // Renderers with a component: the automatic search leaves them to their role.
        static readonly HashSet<Renderer> s_componentRenderers = new HashSet<Renderer>();
        // Draw data of the renderers found automatically, kept between frames.
        static readonly Dictionary<Renderer, ChainRenderer> s_foundRenderers = new Dictionary<Renderer, ChainRenderer>();
        static readonly List<Renderer> s_lostRenderers = new List<Renderer>();
        // Renderers the last full scan found (before the per-frame frustum/size check); refreshed every
        // Search Interval frames instead of every frame, see RescanCandidates.
        static readonly List<Renderer> s_scanCandidates = new List<Renderer>();
        static readonly List<string> s_excludeTags = new List<string>();
        static int s_lastScanFrame = -1;
        static readonly Dictionary<Camera, CameraState> s_cameras = new Dictionary<Camera, CameraState>();
        static readonly List<Camera> s_deadCameras = new List<Camera>();
        static readonly Plane[] s_frustumPlanes = new Plane[6];
        static MaterialPropertyBlock s_block;
        static CopyDepthPass s_copyDepthPass;
        static bool s_subscribed;
        // Which pass a material draws with, keyed by the material itself; see GetPassInfo.
        static readonly Dictionary<Material, MaterialPassInfo> s_passCache = new Dictionary<Material, MaterialPassInfo>();

        // The camera being prepared.
        static Vector3 s_cameraPosition;
        static Vector3 s_cameraForward;
        static bool s_orthographic;

        internal static void Register(SlimeRefractionObject obj)
        {
            if (!s_objects.Contains(obj))
                s_objects.Add(obj);
            if (obj.TargetRenderer != null)
                s_componentRenderers.Add(obj.TargetRenderer);

            if (!s_subscribed)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                s_subscribed = true;
            }
        }

        internal static void Unregister(SlimeRefractionObject obj)
        {
            s_objects.Remove(obj);
            if (obj.TargetRenderer != null)
                s_componentRenderers.Remove(obj.TargetRenderer);
            ReleaseGrabs(obj);

            if (s_objects.Count == 0)
                Shutdown();
        }

        static void ReleaseGrabs(SlimeRefractionObject obj)
        {
            foreach (CameraState state in s_cameras.Values)
                state.ReleaseGrab(obj);

            // The block carries the screen copy, which is released now. This also clears values other scripts
            // put into the renderer's block; they are expected to set them again.
            if (obj.HasBoundGrab && obj.TargetRenderer != null && obj.TargetRenderer.HasPropertyBlock())
                obj.TargetRenderer.SetPropertyBlock(null);
            obj.HasBoundGrab = false;
        }

        // An object switched away from Refractive would keep reading its last, frozen copy.
        static void ReleaseStaleGrabs()
        {
            foreach (SlimeRefractionObject obj in s_objects)
            {
                if (obj.HasBoundGrab && obj.role != SlimeRefractionObject.Role.Refractive)
                    ReleaseGrabs(obj);
            }
        }

        static void Shutdown()
        {
            if (s_subscribed)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                s_subscribed = false;
            }

            foreach (CameraState state in s_cameras.Values)
                state.Release();
            s_cameras.Clear();
            s_foundRenderers.Clear();
            s_componentRenderers.Clear();

            s_scanCandidates.Clear();
            s_lastScanFrame = -1;

            s_copyDepthPass?.Dispose();
            s_copyDepthPass = null;
        }

        static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return;
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset))
                return;

            ScriptableRenderer renderer;
            if (camera.TryGetComponent(out UniversalAdditionalCameraData cameraData))
            {
                // Overlay cameras draw on top of a finished base camera, they have no transparent pass of their own.
                if (cameraData.renderType == CameraRenderType.Overlay)
                    return;
                renderer = cameraData.scriptableRenderer;
            }
            else
            {
                renderer = asset.scriptableRenderer;
            }

            if (renderer == null)
                return;

            RemoveDestroyedCameras();
            ReleaseStaleGrabs();
            if (!s_cameras.TryGetValue(camera, out CameraState state))
            {
                state = new CameraState();
                s_cameras.Add(camera, state);
            }

            List<ChainItem> items = state.pass.items;
            items.Clear();
            GeometryUtility.CalculateFrustumPlanes(camera, s_frustumPlanes);
            Transform cameraTransform = camera.transform;
            s_cameraPosition = cameraTransform.position;
            s_cameraForward = cameraTransform.forward;
            s_orthographic = camera.orthographic;

            // Without a Refractive object on screen there is nothing to fill and no reason to search the scene.
            if (!CollectComponents(camera, items))
            {
                items.Clear();
                return;
            }

            int searchInterval = ReconcileSearchInterval(items);
            float minScreenSize = ReconcileMinScreenSize(items);
            CollectExcludeTags(items);

            CollectFoundRenderers(camera, items, searchInterval, minScreenSize);
            PlaceInsideRefractive(items);
            SortBackToFront(items);

            // Objects in front of the nearest Refractive one end up in no copy.
            int lastRefractive = items.Count - 1;
            while (lastRefractive >= 0 && items[lastRefractive].refractive == null)
                lastRefractive--;
            items.RemoveRange(lastRefractive + 1, items.Count - lastRefractive - 1);

            GraphicsFormat format = GrabFormat(camera, asset);
            // URP renders Game cameras at the render scale, so the copies follow it like the opaque texture does.
            float scale = camera.cameraType == CameraType.Game ? asset.renderScale : 1f;
            int width = Mathf.Max(1, Mathf.RoundToInt(camera.pixelWidth * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(camera.pixelHeight * scale));

            for (int i = 0; i < items.Count; i++)
            {
                ChainItem item = items[i];
                if (item.refractive == null)
                    continue;

                item.divisor = Divisor(item.refractive.grabResolution, asset.opaqueDownsampling);
                item.grab = state.EnsureGrab(item.refractive, Mathf.Max(1, width / item.divisor), Mathf.Max(1, height / item.divisor), format);
                items[i] = item;
                BindGrab(item.refractive, item.grab);
            }

            s_copyDepthPass ??= CreateCopyDepthPass();
            state.pass.copyDepthPass = s_copyDepthPass;
            renderer.EnqueuePass(state.pass);
        }

        /// <summary>Smallest Search Interval among the visible Refractive objects; the most demanding wins.</summary>
        static int ReconcileSearchInterval(List<ChainItem> items)
        {
            int interval = int.MaxValue;
            foreach (ChainItem item in items)
            {
                if (item.refractive != null)
                    interval = Mathf.Min(interval, Mathf.Max(1, item.refractive.searchInterval));
            }
            return interval == int.MaxValue ? 1 : interval;
        }

        /// <summary>Smallest Min Screen Size among the visible Refractive objects; the most demanding wins.</summary>
        static float ReconcileMinScreenSize(List<ChainItem> items)
        {
            float minSize = float.MaxValue;
            foreach (ChainItem item in items)
            {
                if (item.refractive != null)
                    minSize = Mathf.Min(minSize, Mathf.Max(0f, item.refractive.minScreenSize));
            }
            return minSize == float.MaxValue ? 0f : minSize;
        }

        /// <summary>Union of every visible Refractive object's Exclude Tag, applied to the whole shared chain.</summary>
        static void CollectExcludeTags(List<ChainItem> items)
        {
            s_excludeTags.Clear();
            foreach (ChainItem item in items)
            {
                if (item.refractive == null)
                    continue;
                string tag = item.refractive.excludeTag;
                if (!string.IsNullOrEmpty(tag) && tag != "Untagged" && !s_excludeTags.Contains(tag))
                    s_excludeTags.Add(tag);
            }
        }

        /// <summary>Adds the visible objects with a component. Returns true when one of them is Refractive.</summary>
        static bool CollectComponents(Camera camera, List<ChainItem> items)
        {
            bool anyRefractive = false;
            foreach (SlimeRefractionObject obj in s_objects)
            {
                if (obj.role == SlimeRefractionObject.Role.HiddenFromRefraction)
                    continue;

                // A Refractive object carries a property block, which keeps it away from the GPU Resident Drawer,
                // so it is drawn as a renderer: that way it keeps its per-renderer data and the block with its copy.
                bool refractive = obj.role == SlimeRefractionObject.Role.Refractive;
                if (!IsDrawable(camera, obj.TargetRenderer, out Bounds bounds) || !obj.Chain.Prepare(false, !refractive))
                    continue;

                if (refractive && obj.Chain.Queue <= (int)RenderQueue.GeometryLast && !obj.WarnedAboutQueue)
                {
                    // Shader Graph materials with Allow Material Override take queue 2000 from the shader until URP
                    // writes their own; the opaque pass runs before any copy exists.
                    Debug.LogWarning($"Slime Refraction: '{obj.name}' renders in queue {obj.Chain.Queue}, the opaque pass, where Scene Color reads a copy that is not made yet. Give its material the Transparent render queue (3000).", obj);
                    obj.WarnedAboutQueue = true;
                }

                items.Add(CreateItem(obj.Chain, refractive ? obj : null, bounds));
                anyRefractive |= refractive;
            }
            return anyRefractive;
        }

        /// <summary>
        /// Adds every other visible transparent renderer in the loaded scenes. The expensive part, finding which
        /// renderers even qualify, only reruns every searchInterval frames (RescanCandidates); the frustum, size
        /// and material checks that depend on this camera and this frame still run every time.
        /// </summary>
        static void CollectFoundRenderers(Camera camera, List<ChainItem> items, int searchInterval, float minScreenSize)
        {
            if (s_lastScanFrame < 0 || Time.frameCount - s_lastScanFrame >= searchInterval)
            {
                RescanCandidates();
                s_lastScanFrame = Time.frameCount;
            }

            foreach (Renderer renderer in s_scanCandidates)
            {
                // A component can be added or removed between scans; re-check rather than wait for the next one.
                if (renderer == null || s_componentRenderers.Contains(renderer))
                    continue;
                if (!IsDrawable(camera, renderer, out Bounds bounds))
                    continue;
                if (minScreenSize > 0f && ScreenSizeFraction(camera, bounds) < minScreenSize)
                    continue;

                if (!s_foundRenderers.TryGetValue(renderer, out ChainRenderer chain))
                {
                    chain = new ChainRenderer(renderer);
                    s_foundRenderers.Add(renderer, chain);
                }

                if (chain.Prepare(true, true))
                    items.Add(CreateItem(chain, null, bounds));
            }
        }

        /// <summary>The expensive full-scene scan behind CollectFoundRenderers, throttled by Search Interval.</summary>
        static void RescanCandidates()
        {
            s_scanCandidates.Clear();
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
            foreach (Renderer renderer in renderers)
            {
                if (s_componentRenderers.Contains(renderer) || !IsSupported(renderer) || IsExcludedByTag(renderer))
                    continue;
                s_scanCandidates.Add(renderer);
            }

            // Forget destroyed renderers; safe to do only here since a stale entry just fails IsDrawable meanwhile.
            foreach (Renderer renderer in s_foundRenderers.Keys)
            {
                if (renderer == null)
                    s_lostRenderers.Add(renderer);
            }
            foreach (Renderer renderer in s_lostRenderers)
                s_foundRenderers.Remove(renderer);
            s_lostRenderers.Clear();
        }

        static bool IsExcludedByTag(Renderer renderer)
        {
            for (int i = 0; i < s_excludeTags.Count; i++)
            {
                try
                {
                    if (renderer.CompareTag(s_excludeTags[i]))
                        return true;
                }
                catch (UnityException)
                {
                    // The tag was removed from Tag Manager after being set on a Refractive object; stop trying it.
                    s_excludeTags.RemoveAt(i);
                    i--;
                }
            }
            return false;
        }

        // Roughly the fraction of the screen's half-height the renderer's bounding sphere occupies: cheap, and
        // only meant as a coarse threshold, not an exact on-screen measurement.
        static float ScreenSizeFraction(Camera camera, Bounds bounds)
        {
            float radius = bounds.extents.magnitude;
            if (camera.orthographic)
                return radius / Mathf.Max(camera.orthographicSize, 0.0001f);

            float distance = Vector3.Distance(bounds.center, camera.transform.position);
            float tanHalfFov = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return radius / Mathf.Max(distance * tanHalfFov, 0.0001f);
        }

        // Renderers the chain can draw: mesh renderers as meshes, the rest through CommandBuffer.DrawRenderer.
        // VFX Graph draws its particles its own way and is left out.
        static bool IsSupported(Renderer renderer)
        {
            return renderer is MeshRenderer || renderer is SkinnedMeshRenderer || renderer is ParticleSystemRenderer ||
                   renderer is TrailRenderer || renderer is LineRenderer || renderer is SpriteRenderer;
        }

        static bool IsDrawable(Camera camera, Renderer renderer, out Bounds bounds)
        {
            bounds = default;
            if (renderer == null || !renderer.enabled || renderer.forceRenderingOff ||
                renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
            {
                return false;
            }

            GameObject go = renderer.gameObject;
            if ((camera.cullingMask & (1 << go.layer)) == 0)
                return false;
            // Prefab Mode and other preview scenes: that Scene View camera only draws its own scene.
            if (camera.scene.IsValid() && go.scene != camera.scene)
                return false;

            bounds = renderer.bounds;
            return GeometryUtility.TestPlanesAABB(s_frustumPlanes, bounds);
        }

        static ChainItem CreateItem(ChainRenderer chain, SlimeRefractionObject refractive, Bounds bounds)
        {
            Vector3 toCenter = bounds.center - s_cameraPosition;
            return new ChainItem
            {
                chain = chain,
                refractive = refractive,
                bounds = bounds,
                queue = chain.Queue,
                distance = s_orthographic ? Vector3.Dot(toCenter, s_cameraForward) : toCenter.sqrMagnitude
            };
        }

        // An object whose centre is inside a Refractive object (a flame burning in the slime) goes into that
        // object's copy even when the centre is nearer than the slime's. In the transparent pass the depth test
        // hides the part behind the slime's surface, so the copy is the only place where that part shows up.
        static void PlaceInsideRefractive(List<ChainItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                ChainItem item = items[i];
                if (item.refractive != null)
                    continue;

                for (int j = 0; j < items.Count; j++)
                {
                    ChainItem slime = items[j];
                    if (slime.refractive == null || item.distance > slime.distance || !slime.bounds.Contains(item.bounds.center))
                        continue;
                    item.distance = slime.distance + Mathf.Max(Mathf.Abs(slime.distance) * 1e-4f, 1e-4f);
                }
                items[i] = item;
            }
        }

        // Back to front by distance to the bounds centre, like URP sorts transparent objects. Unlike the transparent
        // pass, the render queue only breaks ties: an object behind a Refractive one must reach its copy even when its
        // queue is higher. In the transparent pass such an object is drawn after the Refractive one and hidden by its depth.
        static int CompareBackToFront(in ChainItem a, in ChainItem b)
        {
            int order = b.distance.CompareTo(a.distance);
            if (order == 0)
                order = a.queue.CompareTo(b.queue);
            return order;
        }

        // Insertion sort: stable, and no garbage (List.Sort with a Comparison allocates).
        static void SortBackToFront(List<ChainItem> items)
        {
            for (int i = 1; i < items.Count; i++)
            {
                ChainItem item = items[i];
                int j = i - 1;
                while (j >= 0 && CompareBackToFront(items[j], item) > 0)
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

        static void BindGrab(SlimeRefractionObject obj, RTHandle grab)
        {
            s_block ??= new MaterialPropertyBlock();
            Renderer renderer = obj.TargetRenderer;
            renderer.GetPropertyBlock(s_block);
            s_block.SetTexture(CameraOpaqueTextureId, grab.rt);
            renderer.SetPropertyBlock(s_block);
            obj.HasBoundGrab = true;
        }

        static void RemoveDestroyedCameras()
        {
            foreach (Camera camera in s_cameras.Keys)
            {
                if (camera == null)
                    s_deadCameras.Add(camera);
            }

            foreach (Camera camera in s_deadCameras)
            {
                s_cameras[camera].Release();
                s_cameras.Remove(camera);
            }
            s_deadCameras.Clear();
        }

        static int Divisor(SlimeRefractionObject.GrabResolution resolution, Downsampling pipelineDownsampling)
        {
            switch (resolution)
            {
                case SlimeRefractionObject.GrabResolution.Full:
                    return 1;
                case SlimeRefractionObject.GrabResolution.Half:
                    return 2;
                case SlimeRefractionObject.GrabResolution.Quarter:
                    return 4;
                default:
                    switch (pipelineDownsampling)
                    {
                        case Downsampling.None:
                            return 1;
                        case Downsampling._2xBilinear:
                            return 2;
                        default:
                            return 4;
                    }
            }
        }

        static GraphicsFormat GrabFormat(Camera camera, UniversalRenderPipelineAsset asset)
        {
            if (camera.allowHDR && asset.supportsHDR)
            {
                const GraphicsFormat packed = GraphicsFormat.B10G11R11_UFloatPack32;
                bool usePacked = asset.hdrColorBufferPrecision == HDRColorBufferPrecision._32Bits &&
                                 SystemInfo.IsFormatSupported(packed, GraphicsFormatUsage.Render);
                return usePacked ? packed : GraphicsFormat.R16G16B16A16_SFloat;
            }

            return QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
        }

        // Created on first use while rendering: Unity forbids ShaderTagId (Shader.TagToID) in static
        // initializers that can run while a MonoBehaviour is constructed.
        static class PassTags
        {
            public static readonly ShaderTagId LightMode = new ShaderTagId("LightMode");
            public static readonly ShaderTagId UniversalForward = new ShaderTagId("UniversalForward");
            public static readonly ShaderTagId UniversalForwardOnly = new ShaderTagId("UniversalForwardOnly");
            public static readonly ShaderTagId SrpDefaultUnlit = new ShaderTagId("SRPDefaultUnlit");
        }

        /// <summary>Which pass index a material draws with, and whether that pass is Lit. Immutable per shader,
        /// so the pass-tag scan (native FindPassTagValue calls) is cached; only the cheap enabled-check reruns.</summary>
        struct MaterialPassInfo
        {
            public Shader shader;
            // Index of the pass tagged UniversalForward/UniversalForwardOnly, or -1 if there is none.
            public int litPass;
            public string litPassName;
            // Index of the first SRPDefaultUnlit or untagged pass, used only when litPass is -1.
            public int unlitPass;
        }

        /// <summary>Index of the pass the transparent pass would draw for this material, or -1, and whether it is Lit.</summary>
        static void GetPassInfo(Material material, out int pass, out bool isLit)
        {
            if (!s_passCache.TryGetValue(material, out MaterialPassInfo info) || info.shader != material.shader)
            {
                info = ScanPasses(material);
                s_passCache[material] = info;
            }

            if (info.litPass >= 0)
            {
                // A Lit pass that is disabled (e.g. by a material's disabledShaderPasses) draws nothing at all,
                // it never falls back to an Unlit pass; that matches how the transparent pass treats it.
                pass = material.GetShaderPassEnabled(info.litPassName) ? info.litPass : -1;
                isLit = pass >= 0;
                return;
            }

            pass = info.unlitPass;
            isLit = false;
        }

        static MaterialPassInfo ScanPasses(Material material)
        {
            var info = new MaterialPassInfo { shader = material.shader, litPass = -1, unlitPass = -1 };
            Shader shader = material.shader;
            for (int pass = 0; pass < material.passCount; pass++)
            {
                ShaderTagId lightMode = shader.FindPassTagValue(pass, PassTags.LightMode);
                if (info.litPass < 0 && (lightMode == PassTags.UniversalForward || lightMode == PassTags.UniversalForwardOnly))
                {
                    info.litPass = pass;
                    info.litPassName = lightMode.name;
                }
                // A pass without a LightMode tag counts as SRPDefaultUnlit.
                else if (info.unlitPass < 0 && (lightMode == PassTags.SrpDefaultUnlit || lightMode == ShaderTagId.none))
                {
                    info.unlitPass = pass;
                }
            }
            return info;
        }

        static CopyDepthPass CreateCopyDepthPass()
        {
            if (!GraphicsSettings.TryGetRenderPipelineSettings(out UniversalRendererResources resources) || resources.copyDepthPS == null)
                return null;
            return new CopyDepthPass(RenderPassEvent.BeforeRenderingTransparents, resources.copyDepthPS, customPassName: "Slime Refraction Copy Depth");
        }

        sealed class ChainPass : ScriptableRenderPass
        {
            const string CopySceneName = "Slime Refraction Copy Scene";
            const string CopyDepthName = "Slime Refraction Copy Depth";
            const string HalveName = "Slime Refraction Halve";

            public readonly List<ChainItem> items = new List<ChainItem>();
            public CopyDepthPass copyDepthPass;

            readonly ProfilingSampler _drawSampler = new ProfilingSampler("Slime Refraction Draw");

            sealed class DrawPassData
            {
                public ChainRenderer chain;
                // What the object's Scene Color reads while it is drawn into the copy.
                public TextureHandle sceneColor;
            }

            public ChainPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                profilingSampler = new ProfilingSampler("Slime Refraction");
                // The chain reads the camera color, which is impossible when the camera renders straight to the screen.
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (items.Count == 0)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer)
                    return;

                TextureHandle cameraColor = resources.activeColorTexture;
                TextureHandle cameraDepth = resources.activeDepthTexture;
                if (!cameraColor.IsValid())
                    return;

                // URP's opaque copy: what Scene Color reads outside the chain.
                TextureHandle opaque = resources.cameraOpaqueTexture;

                // With one object nothing is drawn: its copy is the opaque screen. Otherwise the objects behind the
                // nearest one are drawn into a scratch copy, depth tested against the opaque depth and each other.
                bool drawChain = items.Count > 1 && copyDepthPass != null && cameraDepth.IsValid();
                TextureHandle scene = cameraColor;
                TextureHandle depth = TextureHandle.nullHandle;
                if (drawChain)
                {
                    scene = renderGraph.CreateTexture(ScratchDesc(renderGraph, cameraColor, "_SlimeRefractionScene"));
                    renderGraph.AddBlitPass(cameraColor, scene, Vector2.one, Vector2.zero,
                        filterMode: RenderGraphUtils.BlitFilterMode.ClampNearest, passName: CopySceneName);

                    depth = renderGraph.CreateTexture(ScratchDesc(renderGraph, cameraDepth, "_SlimeRefractionDepth"));
                    copyDepthPass.Render(renderGraph, frameData, depth, cameraDepth, false, CopyDepthName);
                }

                int lastDrawn = items.Count - 2;
                for (int i = 0; i < items.Count; i++)
                {
                    ChainItem item = items[i];
                    TextureHandle sceneColor = opaque;
                    if (item.refractive != null)
                    {
                        sceneColor = renderGraph.ImportTexture(item.grab);
                        AddGrab(renderGraph, scene, sceneColor, item.divisor, item.refractive.GrabPassName);
                    }

                    if (drawChain && i <= lastDrawn)
                        AddDraw(renderGraph, scene, depth, sceneColor, item.chain, i == lastDrawn ? opaque : TextureHandle.nullHandle);
                }
            }

            static TextureDesc ScratchDesc(RenderGraph renderGraph, TextureHandle source, string name, int divisor = 1)
            {
                TextureDesc desc = renderGraph.GetTextureDesc(source);
                desc.name = name;
                desc.msaaSamples = MSAASamples.None;
                desc.bindTextureMS = false;
                desc.clearBuffer = false;
                if (divisor > 1)
                {
                    desc.sizeMode = TextureSizeMode.Explicit;
                    desc.width = Mathf.Max(1, desc.width / divisor);
                    desc.height = Mathf.Max(1, desc.height / divisor);
                }
                return desc;
            }

            static void AddGrab(RenderGraph renderGraph, TextureHandle source, TextureHandle grab, int divisor, string passName)
            {
                // Two bilinear halvings average 4x4 texels like URP's 4x box downsampling; one blit would skip most of them.
                if (divisor >= 4)
                {
                    TextureHandle half = renderGraph.CreateTexture(ScratchDesc(renderGraph, source, "_SlimeRefractionHalf", 2));
                    renderGraph.AddBlitPass(source, half, Vector2.one, Vector2.zero, passName: HalveName);
                    source = half;
                }

                renderGraph.AddBlitPass(source, grab, Vector2.one, Vector2.zero, passName: passName);
            }

            void AddDraw(RenderGraph renderGraph, TextureHandle scene, TextureHandle depth, TextureHandle sceneColor,
                ChainRenderer chain, TextureHandle restoreOpaque)
            {
                using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>(chain.drawPassName, out var passData, _drawSampler))
                {
                    passData.chain = chain;
                    passData.sceneColor = sceneColor;

                    builder.SetRenderAttachment(scene, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                    if (sceneColor.IsValid())
                        builder.UseTexture(sceneColor, AccessFlags.Read);
                    // Shadow maps and the rest of the lighting inputs, only when a Lit material actually reads
                    // them; an Unlit-only draw (most VFX) skips this and stays easier for the graph to schedule.
                    if (chain.HasLitPass)
                        builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    // After the last draw, Scene Color of everything else reads URP's opaque copy again.
                    if (restoreOpaque.IsValid())
                        builder.SetGlobalTextureAfterPass(restoreOpaque, CameraOpaqueTextureId);

                    builder.SetRenderFunc(static (DrawPassData data, RasterGraphContext context) =>
                    {
                        if (data.sceneColor.IsValid())
                            context.cmd.SetGlobalTexture(CameraOpaqueTextureId, data.sceneColor);
                        data.chain.Draw(context.cmd);
                    });
                }
            }
        }
    }
}
