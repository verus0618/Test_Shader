// AutoHideShaderGraphGUI.cs
// Version: 1.000
//
// Material inspector for Shader Graph shaders that hides properties the current keyword / Boolean
// choices leave unconnected (see ShaderGraphUsageAnalyzer). Everything else, including Surface
// Options and Advanced Options, is drawn by the regular URP Shader Graph inspector it wraps.
//
// Enable it per graph: Graph Settings > Custom Editor GUI = VFX.Shaders.Editor.AutoHideShaderGraphGUI,
// or select graphs and use Tools > VFX > Auto Hide Material Properties > Enable On Selected Shader Graphs.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VFX.Shaders.Editor
{
    /// <summary>
    /// Shader Graph material inspector that hides properties unused by the selected branches.
    /// </summary>
    public class AutoHideShaderGraphGUI : ShaderGUI
    {
        // Sub target -> the URP inspector Shader Graph would use without a custom editor.
        private static readonly Dictionary<string, string> DefaultGuiBySubTarget = new Dictionary<string, string>
        {
            { "UniversalLitSubTarget", "ShaderGraphLitGUI" },
            { "UniversalUnlitSubTarget", "ShaderGraphUnlitGUI" },
            { "UniversalSixWaySubTarget", "SixWayGUI" },
            { "UniversalTerrainLitSubTarget", "ShaderGraphTerrainLitGUI" },
            { "UniversalDecalSubTarget", "DecalShaderGraphGUI" },
        };

        private ShaderGUI innerGui;
        private Shader innerGuiShader;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            Material material = materialEditor.target as Material;
            Shader shader = material != null ? material.shader : null;

            ShaderGraphUsage usage = ShaderGraphUsageAnalyzer.Get(shader);
            MaterialProperty[] visible = FilterProperties(materialEditor.targets, properties, usage);

            ShaderGUI inner = GetInnerGui(shader, usage);
            if (inner != null)
                inner.OnGUI(materialEditor, visible);
            else
                base.OnGUI(materialEditor, visible);
        }

        public override void ValidateMaterial(Material material)
        {
            // URP updates surface keywords and the render queue here, so the wrapped inspector must run it.
            Shader shader = material != null ? material.shader : null;
            GetInnerGui(shader, ShaderGraphUsageAnalyzer.Get(shader))?.ValidateMaterial(material);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            ShaderGUI inner = GetInnerGui(newShader, ShaderGraphUsageAnalyzer.Get(newShader));
            if (inner != null)
                inner.AssignNewShaderToMaterial(material, oldShader, newShader);
            else
                base.AssignNewShaderToMaterial(material, oldShader, newShader);
        }

        public override void OnClosed(Material material)
        {
            innerGui?.OnClosed(material);
        }

        private static MaterialProperty[] FilterProperties(Object[] targets, MaterialProperty[] properties, ShaderGraphUsage usage)
        {
            if (usage == null || usage.Rules.Count == 0)
                return properties;

            var visible = new List<MaterialProperty>(properties.Length);
            foreach (MaterialProperty property in properties)
            {
                if (IsUsedByAnyTarget(targets, property.name, usage))
                    visible.Add(property);
            }

            return visible.ToArray();
        }

        private static bool IsUsedByAnyTarget(Object[] targets, string propertyName, ShaderGraphUsage usage)
        {
            // With several materials selected, keep the property while any of them needs it.
            foreach (Object target in targets)
            {
                Material material = target as Material;
                if (material == null || usage.IsUsed(propertyName, material))
                    return true;
            }

            return false;
        }

        private ShaderGUI GetInnerGui(Shader shader, ShaderGraphUsage usage)
        {
            if (shader == null)
                return null;

            if (innerGuiShader == shader)
                return innerGui;

            innerGuiShader = shader;
            innerGui = null;

            if (usage == null || !DefaultGuiBySubTarget.TryGetValue(usage.SubTargetName, out string guiName))
                return null;

            foreach (Type type in TypeCache.GetTypesDerivedFrom<ShaderGUI>())
            {
                if (type.Name != guiName || type.IsAbstract)
                    continue;

                try
                {
                    // URP keeps these inspectors internal, so they are created through reflection.
                    innerGui = (ShaderGUI)Activator.CreateInstance(type, nonPublic: true);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AutoHide] Could not create '{type.FullName}': {e.Message}. Using the default inspector.");
                }

                break;
            }

            return innerGui;
        }
    }

    /// <summary>
    /// Menu commands that switch the auto-hide inspector on or off for selected shader graphs.
    /// </summary>
    internal static class AutoHideShaderGraphMenu
    {
        private const string MenuRoot = "Tools/VFX/Auto Hide Material Properties/";
        private const string EmptyGuiField = "\"m_CustomEditorGUI\": \"\"";

        private static string OurGuiField => $"\"m_CustomEditorGUI\": \"{typeof(AutoHideShaderGraphGUI).FullName}\"";

        [MenuItem(MenuRoot + "Enable On Selected Shader Graphs")]
        private static void Enable()
        {
            foreach (string path in SelectedGraphPaths())
            {
                string text = File.ReadAllText(path);
                if (text.Contains(OurGuiField))
                {
                    Debug.Log($"[AutoHide] Already enabled: {path}");
                    continue;
                }

                if (!text.Contains(EmptyGuiField))
                {
                    Debug.LogWarning($"[AutoHide] Skipped {path}: it already uses another Custom Editor GUI.");
                    continue;
                }

                WriteGraph(path, text.Replace(EmptyGuiField, OurGuiField));
                Debug.Log($"[AutoHide] Enabled: {path}");
            }
        }

        [MenuItem(MenuRoot + "Disable On Selected Shader Graphs")]
        private static void Disable()
        {
            foreach (string path in SelectedGraphPaths())
            {
                string text = File.ReadAllText(path);
                if (!text.Contains(OurGuiField))
                    continue;

                WriteGraph(path, text.Replace(OurGuiField, EmptyGuiField));
                Debug.Log($"[AutoHide] Disabled: {path}");
            }
        }

        [MenuItem(MenuRoot + "Enable On Selected Shader Graphs", true)]
        [MenuItem(MenuRoot + "Disable On Selected Shader Graphs", true)]
        private static bool HasSelectedGraphs()
        {
            return SelectedGraphPaths().Count > 0;
        }

        private static List<string> SelectedGraphPaths()
        {
            var paths = new List<string>();
            foreach (Object selected in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(selected);
                if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }

            return paths;
        }

        private static void WriteGraph(string path, string text)
        {
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path);
        }
    }
}
