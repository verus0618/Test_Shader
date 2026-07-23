#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace TestMisha.Rendering.Editor
{
    /// <summary>
    /// Prevents Unity's own red selection wire/outline from being confused with
    /// the Screen Space Outline renderer feature while testing in Scene View.
    /// This code is Editor-only and is never included in a player build.
    /// </summary>
    [InitializeOnLoad]
    internal static class OutlineSceneViewTools
    {
        private const string MenuRoot = "Tools/TestMisha/Screen Space Outline/";
        private static string HidePreferenceKey =>
            "TestMisha.ScreenSpaceOutline.HideSelectionOverlay." + Application.dataPath;

        private static readonly Type AnnotationUtilityType =
            typeof(SceneView).Assembly.GetType("UnityEditor.AnnotationUtility");

        static OutlineSceneViewTools()
        {
            // Default to hidden for this project so a selected object cannot look
            // like it received an outline from the wrong GameObject layer.
            if (!EditorPrefs.HasKey(HidePreferenceKey))
                EditorPrefs.SetBool(HidePreferenceKey, true);

            EditorApplication.delayCall += ApplySavedPreference;
        }

        [MenuItem(MenuRoot + "Hide Unity Selection Overlay", false, 2000)]
        private static void HideSelectionOverlay()
        {
            EditorPrefs.SetBool(HidePreferenceKey, true);
            SetSelectionOverlayVisible(false);
        }

        [MenuItem(MenuRoot + "Show Unity Selection Overlay", false, 2001)]
        private static void ShowSelectionOverlay()
        {
            EditorPrefs.SetBool(HidePreferenceKey, false);
            SetSelectionOverlayVisible(true);
        }

        private static void ApplySavedPreference()
        {
            SetSelectionOverlayVisible(!EditorPrefs.GetBool(HidePreferenceKey, true));
        }

        private static void SetSelectionOverlayVisible(bool visible)
        {
            if (AnnotationUtilityType == null)
            {
                Debug.LogWarning("Screen Space Outline: Unity selection overlay API was not found.");
                return;
            }

            SetStaticBoolProperty("showSelectionOutline", visible);
            SetStaticBoolProperty("showSelectionWire", visible);
            SceneView.RepaintAll();
        }

        private static void SetStaticBoolProperty(string propertyName, bool value)
        {
            PropertyInfo property = AnnotationUtilityType.GetProperty(
                propertyName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            property?.SetValue(null, value);
        }
    }
}
#endif
