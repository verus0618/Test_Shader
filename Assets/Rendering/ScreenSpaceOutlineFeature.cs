using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace TestMisha.Rendering
{
    /// <summary>
    /// Screen-space outlines based on discontinuities in the camera depth and normal textures.
    /// Written for Unity 6 / URP 17 Render Graph.
    /// </summary>
    public sealed class ScreenSpaceOutlineFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class OutlineSettings
        {
            [Tooltip("Full-screen outline shader. Keep the supplied shader assigned so it is included in builds.")]
            [InspectorName("Outline Shader (asset reference)")]
            public Shader outlineShader;

            [Tooltip("Shader used to draw the selected object layers into a visibility mask.")]
            [InspectorName("Mask Shader (asset reference)")]
            public Shader maskShader;

            [Tooltip("Where the outline is composited. Before post-processing lets AA and color grading affect it.")]
            [InspectorName("Injection Point | Affects Performance: 1/10")]
            public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

            [Header("Object Filter")]
            [Tooltip("Only opaque objects on these GameObject layers receive an outline.")]
            [InspectorName("Object Layer Mask | Affects Performance: 3/10")]
            public LayerMask objectLayerMask = ~0;

            [Header("Outline")]
            [InspectorName("Outline Color | Affects Performance: 1/10")]
            public Color outlineColor = Color.black;

            [Range(1f, 8f)]
            [Tooltip("Outline width in screen pixels.")]
            [InspectorName("Thickness | Affects Performance: 1/10")]
            public float thickness = 1f;

            [Range(0f, 1f)]
            [Tooltip("Softens the detected edge. Zero produces a crisp threshold.")]
            [InspectorName("Softness | Affects Performance: 1/10")]
            public float softness = 0.1f;

            [Header("Depth Edges")]
            [Range(0.05f, 10f)]
            [Tooltip("Relative depth discontinuity required for an edge. Raise it to remove small depth details.")]
            [InspectorName("Depth Threshold | Affects Performance: 1/10")]
            public float depthThreshold = 1.5f;

            [Header("Normal Edges")]
            [Range(0.01f, 2f)]
            [Tooltip("World-space normal discontinuity required for an edge.")]
            [InspectorName("Normal Threshold | Affects Performance: 1/10")]
            public float normalThreshold = 0.4f;

            [Header("Grazing-angle correction")]
            [Range(0f, 1f)]
            [Tooltip("Angle at which depth edges start being suppressed on surfaces viewed edge-on.")]
            [InspectorName("Steep Angle Threshold | Affects Performance: 1/10")]
            public float steepAngleThreshold = 0.2f;

            [Range(0f, 50f)]
            [Tooltip("How strongly depth thresholds increase on surfaces viewed edge-on.")]
            [InspectorName("Steep Angle Multiplier | Affects Performance: 1/10")]
            public float steepAngleMultiplier = 25f;

            [Header("Cameras")]
            [InspectorName("Show In Scene View | Editor Cost: 2/10")]
            public bool showInSceneView = true;
        }

        private sealed class OutlinePass : ScriptableRenderPass
        {
            private const string PassName = "Screen Space Outline";
            private const string MaskPassName = "Screen Space Outline Layer Mask";

            private static readonly List<ShaderTagId> ShaderTags = new()
            {
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("LightweightForward")
            };

            private readonly ProfilingSampler maskProfilingSampler = new(MaskPassName);
            private Material outlineMaterial;
            private Material maskMaterial;
            private LayerMask objectLayerMask;

            private sealed class MaskPassData
            {
                public RendererListHandle rendererList;
            }

            private sealed class CompositePassData
            {
                public TextureHandle source;
                public TextureHandle layerMask;
                public Material material;
            }

            public OutlinePass()
            {
                profilingSampler = new ProfilingSampler(PassName);
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            }

            public void Setup(Material screenSpaceMaterial, Material objectMaskMaterial, OutlineSettings settings)
            {
                outlineMaterial = screenSpaceMaterial;
                maskMaterial = objectMaskMaterial;
                objectLayerMask = settings.objectLayerMask;
                renderPassEvent = settings.injectionPoint;

                outlineMaterial.SetColor(ShaderIds.OutlineColor, settings.outlineColor);
                outlineMaterial.SetFloat(ShaderIds.Thickness, settings.thickness);
                outlineMaterial.SetFloat(ShaderIds.Softness, settings.softness);
                outlineMaterial.SetFloat(ShaderIds.DepthThreshold, settings.depthThreshold);
                outlineMaterial.SetFloat(ShaderIds.NormalThreshold, settings.normalThreshold);
                outlineMaterial.SetFloat(ShaderIds.SteepAngleThreshold, settings.steepAngleThreshold);
                outlineMaterial.SetFloat(ShaderIds.SteepAngleMultiplier, settings.steepAngleMultiplier);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (outlineMaterial == null || maskMaterial == null || objectLayerMask.value == 0)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer)
                    return;

                TextureHandle source = resources.activeColorTexture;
                if (!source.IsValid())
                    return;

                RenderTextureDescriptor maskDescriptor = cameraData.cameraTargetDescriptor;
                maskDescriptor.depthStencilFormat = GraphicsFormat.None;
                maskDescriptor.msaaSamples = 1;
                maskDescriptor.graphicsFormat = SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.Render)
                    ? GraphicsFormat.R8_UNorm
                    : GraphicsFormat.B8G8R8A8_UNorm;

                TextureHandle layerMaskTexture = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    maskDescriptor,
                    "_OutlineLayerMaskTexture",
                    true,
                    FilterMode.Point);

                RendererListHandle rendererList = CreateMaskRendererList(renderGraph, frameData);
                if (!rendererList.IsValid())
                    return;

                using (var builder = renderGraph.AddRasterRenderPass<MaskPassData>(MaskPassName, out var passData, maskProfilingSampler))
                {
                    passData.rendererList = rendererList;
                    builder.UseRendererList(rendererList);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(layerMaskTexture, 0, AccessFlags.WriteAll);
                    builder.SetRenderFunc(static (MaskPassData data, RasterGraphContext context) =>
                    {
                        context.cmd.DrawRendererList(data.rendererList);
                    });
                }

                TextureDesc destinationDesc = renderGraph.GetTextureDesc(source);
                destinationDesc.name = "CameraColor-ScreenSpaceOutline";
                destinationDesc.clearBuffer = false;

                TextureHandle destination = renderGraph.CreateTexture(destinationDesc);
                using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>(PassName, out var passData, profilingSampler))
                {
                    passData.source = source;
                    passData.layerMask = layerMaskTexture;
                    passData.material = outlineMaterial;

                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(layerMaskTexture, AccessFlags.Read);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.WriteAll);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalTexture(ShaderIds.LayerMaskTexture, data.layerMask);
                        Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, 0);
                    });
                }

                // Let subsequent URP passes consume the outlined image without an extra copy.
                resources.cameraColor = destination;
            }

            private RendererListHandle CreateMaskRendererList(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();

                var filteringSettings = new FilteringSettings(RenderQueueRange.opaque, objectLayerMask);
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    ShaderTags,
                    renderingData,
                    cameraData,
                    lightData,
                    cameraData.defaultOpaqueSortFlags);

                drawingSettings.overrideMaterial = maskMaterial;
                drawingSettings.overrideMaterialPassIndex = 0;

                var rendererListParams = new RendererListParams(
                    renderingData.cullResults,
                    drawingSettings,
                    filteringSettings);

                return renderGraph.CreateRendererList(rendererListParams);
            }
        }

        private static class ShaderIds
        {
            public static readonly int OutlineColor = Shader.PropertyToID("_OutlineColor");
            public static readonly int Thickness = Shader.PropertyToID("_OutlineThickness");
            public static readonly int Softness = Shader.PropertyToID("_OutlineSoftness");
            public static readonly int DepthThreshold = Shader.PropertyToID("_DepthThreshold");
            public static readonly int NormalThreshold = Shader.PropertyToID("_NormalThreshold");
            public static readonly int SteepAngleThreshold = Shader.PropertyToID("_SteepAngleThreshold");
            public static readonly int SteepAngleMultiplier = Shader.PropertyToID("_SteepAngleMultiplier");
            public static readonly int LayerMaskTexture = Shader.PropertyToID("_OutlineLayerMaskTexture");
        }

        [SerializeField]
        private OutlineSettings settings = new();

        private OutlinePass outlinePass;
        private Material outlineMaterial;
        private Material maskMaterial;

        public override void Create()
        {
            DisposeMaterial();

            Shader shader = settings.outlineShader;
            if (shader == null)
                shader = Shader.Find("Hidden/TestMisha/ScreenSpaceOutline");

            Shader objectMaskShader = settings.maskShader;
            if (objectMaskShader == null)
                objectMaskShader = Shader.Find("Hidden/TestMisha/OutlineObjectMask");

            if (shader != null)
                outlineMaterial = CoreUtils.CreateEngineMaterial(shader);
            if (objectMaskShader != null)
                maskMaterial = CoreUtils.CreateEngineMaterial(objectMaskShader);

            outlinePass = new OutlinePass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraData cameraData = renderingData.cameraData;

            if (outlineMaterial == null || maskMaterial == null || settings.objectLayerMask.value == 0 ||
                cameraData.cameraType == CameraType.Preview ||
                cameraData.cameraType == CameraType.Reflection ||
                cameraData.renderType == CameraRenderType.Overlay ||
                (!settings.showInSceneView && cameraData.isSceneViewCamera))
            {
                return;
            }

            outlinePass.Setup(outlineMaterial, maskMaterial, settings);
            renderer.EnqueuePass(outlinePass);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeMaterial();
        }

        private void DisposeMaterial()
        {
            CoreUtils.Destroy(outlineMaterial);
            CoreUtils.Destroy(maskMaterial);
            outlineMaterial = null;
            maskMaterial = null;
        }
    }
}
