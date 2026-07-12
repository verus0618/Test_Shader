// MaterialDrawers.cs
// Version: 1.003

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

// Shows a property in the inspector only when Keyword enum conditions are met
// Use double underscore __ as separator between enum Reference and value Reference Suffix
// Use " AND " for AND logic (all conditions must match)
// Use " OR " for OR logic (at least one condition must match)
// Usage in Shader Graph Custom Attributes:
// Name: ShowIfEnum | Value: YOUR_ENUM__YOUR_VALUE
// Name: ShowIfEnum | Value: YOUR_ENUM__YOUR_VALUE AND ANOTHER_ENUM__ANOTHER_VALUE
// Name: ShowIfEnum | Value: YOUR_ENUM__YOUR_VALUE OR ANOTHER_ENUM__ANOTHER_VALUE
public class ShowIfEnumDrawer : MaterialPropertyDrawer
{
    private readonly string[] keywordNames;
    private readonly bool useAndLogic;

    public ShowIfEnumDrawer(string param)
    {
        if (param.Contains(" AND "))
        {
            useAndLogic = true;
            keywordNames = BuildKeywords(param.Split(new string[] { " AND " }, System.StringSplitOptions.None));
        }
        else if (param.Contains(" OR "))
        {
            useAndLogic = false;
            keywordNames = BuildKeywords(param.Split(new string[] { " OR " }, System.StringSplitOptions.None));
        }
        else
        {
            useAndLogic = true;
            keywordNames = BuildKeywords(new string[] { param });
        }
    }

    private string[] BuildKeywords(string[] parts)
    {
        string[] keywords = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int sep = parts[i].IndexOf("__");
            if (sep >= 0)
            {
                string enumRef = parts[i].Substring(0, sep);
                string valueSuffix = parts[i].Substring(sep + 2);
                keywords[i] = "_" + enumRef + "_" + valueSuffix;
            }
            else
            {
                keywords[i] = "_" + parts[i];
            }
        }
        return keywords;
    }

    private bool IsVisible(MaterialEditor editor)
    {
        Material material = editor.target as Material;
        if (material == null) return true;

        if (useAndLogic)
        {
            foreach (string keyword in keywordNames)
                if (!material.IsKeywordEnabled(keyword)) return false;
            return true;
        }
        else
        {
            foreach (string keyword in keywordNames)
                if (material.IsKeywordEnabled(keyword)) return true;
            return false;
        }
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

// Hides a property in the inspector when Keyword enum conditions are met
// Use double underscore __ as separator between enum Reference and value Reference Suffix
// Use " AND " for AND logic (all conditions must match to hide)
// Use " OR " for OR logic (at least one condition must match to hide)
// Usage in Shader Graph Custom Attributes:
// Name: HideIfEnum | Value: YOUR_ENUM__YOUR_VALUE
// Name: HideIfEnum | Value: YOUR_ENUM__YOUR_VALUE AND ANOTHER_ENUM__ANOTHER_VALUE
// Name: HideIfEnum | Value: YOUR_ENUM__YOUR_VALUE OR ANOTHER_ENUM__ANOTHER_VALUE
public class HideIfEnumDrawer : MaterialPropertyDrawer
{
    private readonly string[] keywordNames;
    private readonly bool useAndLogic;

    public HideIfEnumDrawer(string param)
    {
        if (param.Contains(" AND "))
        {
            useAndLogic = true;
            keywordNames = BuildKeywords(param.Split(new string[] { " AND " }, System.StringSplitOptions.None));
        }
        else if (param.Contains(" OR "))
        {
            useAndLogic = false;
            keywordNames = BuildKeywords(param.Split(new string[] { " OR " }, System.StringSplitOptions.None));
        }
        else
        {
            useAndLogic = true;
            keywordNames = BuildKeywords(new string[] { param });
        }
    }

    private string[] BuildKeywords(string[] parts)
    {
        string[] keywords = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int sep = parts[i].IndexOf("__");
            if (sep >= 0)
            {
                string enumRef = parts[i].Substring(0, sep);
                string valueSuffix = parts[i].Substring(sep + 2);
                keywords[i] = "_" + enumRef + "_" + valueSuffix;
            }
            else
            {
                keywords[i] = "_" + parts[i];
            }
        }
        return keywords;
    }

    private bool IsVisible(MaterialEditor editor)
    {
        Material material = editor.target as Material;
        if (material == null) return true;

        if (useAndLogic)
        {
            foreach (string keyword in keywordNames)
                if (!material.IsKeywordEnabled(keyword)) return true;
            return false;
        }
        else
        {
            foreach (string keyword in keywordNames)
                if (material.IsKeywordEnabled(keyword)) return false;
            return true;
        }
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