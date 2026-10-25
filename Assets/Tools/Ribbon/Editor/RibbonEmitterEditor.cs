using UnityEditor;
using UnityEngine;

namespace TestMisha.Tools
{
    /// <summary>
    /// Inspector for <see cref="RibbonEmitter"/> that shows the assigned material below the component,
    /// with the same collapsible header MeshRenderer uses, so the material can be edited in place.
    /// </summary>
    [CustomEditor(typeof(RibbonEmitter))]
    [CanEditMultipleObjects]
    public sealed class RibbonEmitterEditor : Editor
    {
        private const string EditableAssetRoot = "Assets/";

        private MaterialEditor _materialEditor;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            // Several selected emitters may use different materials, so only a single selection shows one.
            Material material = targets.Length == 1 ? ((RibbonEmitter)target).material : null;
            DrawMaterialEditor(material);
        }

        private void OnDisable()
        {
            DestroyMaterialEditor();
        }

        private void DrawMaterialEditor(Material material)
        {
            if (_materialEditor != null && _materialEditor.target != material)
                DestroyMaterialEditor();

            if (material == null)
                return;

            if (_materialEditor == null)
                _materialEditor = (MaterialEditor)CreateEditor(material, typeof(MaterialEditor));

            EditorGUILayout.Space();
            // The header includes the foldout arrow and shader dropdown, like the material block of MeshRenderer.
            _materialEditor.DrawHeader();

            // Built-in and package materials are read-only, the same rule MeshRenderer follows.
            bool editable = AssetDatabase.GetAssetPath(material).StartsWith(EditableAssetRoot);
            using (new EditorGUI.DisabledScope(!editable))
                _materialEditor.OnInspectorGUI();
        }

        private void DestroyMaterialEditor()
        {
            if (_materialEditor == null)
                return;
            DestroyImmediate(_materialEditor);
            _materialEditor = null;
        }
    }
}
