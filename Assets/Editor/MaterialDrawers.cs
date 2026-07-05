// MaterialDrawers.cs
// Version: 1.001

using UnityEngine;
using UnityEditor;

// Shows a property in the inspector only when a boolean property is enabled (value == 1)
// Usage in Shader Graph Custom Attributes:
// Name: ShowIf | Value: YourBooleanPropertyReference
public class ShowIfDrawer : MaterialPropertyDrawer
{
    private readonly string controllerName;

    public ShowIfDrawer(string controllerName)
    {
        this.controllerName = "_" + controllerName;
    }

    private bool IsVisible(MaterialEditor editor)
    {
        Material material = editor.target as Material;
        if (material == null) return true;
        try { return material.GetFloat(controllerName) == 1.0f; }
        catch { return true; }
    }

    public override void OnGUI(Rect position, MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (IsVisible(editor))
            editor.DefaultShaderProperty(position, prop, label);
    }

    public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (!IsVisible(editor))
            return -EditorGUIUtility.standardVerticalSpacing;
        return MaterialEditor.GetDefaultPropertyHeight(prop);
    }
}

// Hides a property in the inspector when a boolean property is enabled (value == 1)
// Usage in Shader Graph Custom Attributes:
// Name: HideIf | Value: YourBooleanPropertyReference
public class HideIfDrawer : MaterialPropertyDrawer
{
    private readonly string controllerName;

    public HideIfDrawer(string controllerName)
    {
        this.controllerName = "_" + controllerName;
    }

    private bool IsVisible(MaterialEditor editor)
    {
        Material material = editor.target as Material;
        if (material == null) return true;
        try { return material.GetFloat(controllerName) == 0.0f; }
        catch { return true; }
    }

    public override void OnGUI(Rect position, MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (IsVisible(editor))
            editor.DefaultShaderProperty(position, prop, label);
    }

    public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (!IsVisible(editor))
            return -EditorGUIUtility.standardVerticalSpacing;
        return MaterialEditor.GetDefaultPropertyHeight(prop);
    }
}

// Shows a property in the inspector only when a Keyword enum matches the specified value
// Use double underscore __ as separator between enum Reference and value Reference Suffix
// Usage in Shader Graph Custom Attributes:
// Name: ShowIfEnum | Value: YOUR_ENUM_REFERENCE__YOUR_VALUE_SUFFIX
public class ShowIfEnumDrawer : MaterialPropertyDrawer
{
    private readonly string keywordName;

    public ShowIfEnumDrawer(string param)
    {
        int sep = param.IndexOf("__");
        if (sep >= 0)
        {
            string enumRef = param.Substring(0, sep);
            string valueSuffix = param.Substring(sep + 2);
            keywordName = "_" + enumRef + "_" + valueSuffix;
        }
        else
        {
            keywordName = "_" + param;
        }
    }

    private bool IsVisible(MaterialEditor editor)
    {
        Material material = editor.target as Material;
        if (material == null) return true;
        return material.IsKeywordEnabled(keywordName);
    }

    public override void OnGUI(Rect position, MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (IsVisible(editor))
            editor.DefaultShaderProperty(position, prop, label);
    }

    public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (!IsVisible(editor))
            return -EditorGUIUtility.standardVerticalSpacing;
        return MaterialEditor.GetDefaultPropertyHeight(prop);
    }
}

// Hides a property in the inspector when a Keyword enum matches the specified value
// Use double underscore __ as separator between enum Reference and value Reference Suffix
// Usage in Shader Graph Custom Attributes:
// Name: HideIfEnum | Value: YOUR_ENUM_REFERENCE__YOUR_VALUE_SUFFIX
public class HideIfEnumDrawer : MaterialPropertyDrawer
{
    private readonly string keywordName;

    public HideIfEnumDrawer(string param)
    {
        int sep = param.IndexOf("__");
        if (sep >= 0)
        {
            string enumRef = param.Substring(0, sep);
            string valueSuffix = param.Substring(sep + 2);
            keywordName = "_" + enumRef + "_" + valueSuffix;
        }
        else
        {
            keywordName = "_" + param;
        }
    }

    private bool IsVisible(MaterialEditor editor)
    {
        Material material = editor.target as Material;
        if (material == null) return true;
        return !material.IsKeywordEnabled(keywordName);
    }

    public override void OnGUI(Rect position, MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (IsVisible(editor))
            editor.DefaultShaderProperty(position, prop, label);
    }

    public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
    {
        if (!IsVisible(editor))
            return -EditorGUIUtility.standardVerticalSpacing;
        return MaterialEditor.GetDefaultPropertyHeight(prop);
    }
}